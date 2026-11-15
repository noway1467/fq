using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FeiqLight;

internal static class ReverseInterop
{
    private static void Wait(Func<bool> done){for(int i=0;i<600;i++){if(done())return;Thread.Sleep(100);}throw new Exception("互通等待超时");}
    private static string Call(int port,string path,string cookie,string body=null)
    {
        using(TcpClient socket=new TcpClient("127.0.0.1",port)){
            socket.ReceiveTimeout=20000;byte[] content=body==null?new byte[0]:Encoding.UTF8.GetBytes(body);
            byte[] header=Encoding.ASCII.GetBytes((body==null?"GET":"POST")+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nContent-Type: application/json\r\nContent-Length: "+content.Length+"\r\nConnection: close\r\n\r\n");
            socket.GetStream().Write(header,0,header.Length);if(content.Length>0)socket.GetStream().Write(content,0,content.Length);
            using(StreamReader reader=new StreamReader(socket.GetStream(),Encoding.UTF8))return reader.ReadToEnd();
        }
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding=Encoding.UTF8;
        try{
            string root=args[0];int port=Int32.Parse(args[1]);Directory.CreateDirectory(root);
            using(LanService node=new LanService(new AppSettings{DeviceId="11111111111111111111111111111111",Nickname="Windows"},port,true,"windows")){
                node.ConfigureSecurity(Path.Combine(root,"win-security"),(name,code)=>true);node.Start();
                Action<ToolsHttp.Request,ToolsHttp.Response> handler=(request,response)=>{if(!node.Security.IsTrustedFingerprint(request.Fingerprint))throw new Exception("未认证请求");response.Bytes(request.Method=="POST"?request.Body:Encoding.UTF8.GetBytes(new string('w',131077)),"text/plain");};
                using(ToolsHttp share=new ToolsHttp(port+1,true,handler,node.Security))using(ToolsHttp remote=new ToolsHttp(port+2,true,handler,node.Security)){
                    File.WriteAllText(Path.Combine(root,"windows-ready"),port.ToString());Console.WriteLine("READY");
                    Wait(()=>File.Exists(Path.Combine(root,"java-ready")));int javaPort=Int32.Parse(File.ReadAllText(Path.Combine(root,"java-ready")));node.Probe(new IPEndPoint(IPAddress.Loopback,javaPort));
                    Wait(()=>node.Peers.Any(p=>p.DeviceId=="22222222222222222222222222222222"));Peer peer=node.Peers.First(p=>p.DeviceId=="22222222222222222222222222222222");node.VerifyPeer(peer);
                    Action<TcpClient,IPEndPoint> original=node.PeerConnector;node.PeerConnector=(client,target)=>{throw new SocketException((int)SocketError.ConnectionRefused);};
                    node.VerifyPeer(peer);
                    foreach(int targetPort in new[]{javaPort+1,javaPort+2})using(ToolsGateway gateway=new ToolsGateway(node.Security,peer,targetPort,true,node)){
                        Uri uri=new Uri(gateway.Url);string redirect=Call(uri.Port,uri.PathAndQuery,"");string cookie=redirect.Split(new[]{"\r\n"},StringSplitOptions.None).First(s=>s.StartsWith("Set-Cookie: ")).Substring(12).Split(';')[0];
                        if(!Call(uri.Port,"/",cookie).EndsWith(new string('j',131077),StringComparison.Ordinal))throw new Exception("Windows -> Java 共享/画面不完整");
                        if(!Call(uri.Port,"/api/input",cookie,"{\"text\":\"中文😀\"}").EndsWith("{\"text\":\"中文😀\"}",StringComparison.Ordinal))throw new Exception("Windows -> Java 输入丢失");
                    }
                    Console.WriteLine("PASS Windows -> Java: 回连身份核验、共享/远控双端口、多段正文、UTF-8 POST");node.PeerConnector=original;
                    File.WriteAllText(Path.Combine(root,"windows-done"),"done");Wait(()=>File.Exists(Path.Combine(root,"java-done")));
                }
            }
            return 0;
        }catch(Exception e){Console.WriteLine(e);return 1;}
    }
}
