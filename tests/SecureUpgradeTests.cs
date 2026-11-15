using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using FeiqLight;

namespace FeiqLight.Tests
{
    internal static class SecureUpgradeTests
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        private static int passed,failed;
        private static void Check(bool value,string message){if(!value)throw new Exception(message);}
        private static void Run(string name,Action test){try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);Console.WriteLine(e.StackTrace);}}
        private static int Port(){using(TcpListenerSocket socket=new TcpListenerSocket())return socket.Port;}
        private sealed class TcpListenerSocket:IDisposable {private readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);public TcpListenerSocket(){listener.Start();}public int Port{get{return ((IPEndPoint)listener.LocalEndpoint).Port;}}public void Dispose(){listener.Stop();}}
        private static void Wait(Func<bool> condition,string message){for(int i=0;i<500;i++){if(condition())return;Thread.Sleep(10);}throw new Exception(message);}
        private static LanService Node(string root,int port,string id){var node=new LanService(new AppSettings{DeviceId=id,Nickname=id},port,true,id);node.ConfigureSecurity(root,(name,code)=>true);node.Start();return node;}
        private static Peer Find(LanService a,LanService b){a.Probe(new IPEndPoint(IPAddress.Loopback,b.Port));Wait(()=>a.Peers.Count>0,"发现超时");return a.Peers.First(p=>p.DeviceId==b.Settings.DeviceId);}
        private static string Http(int port,string path,string cookie,string origin=null){using(TcpClient client=new TcpClient()){client.Connect(IPAddress.Loopback,port);client.ReceiveTimeout=10000;byte[] request=Encoding.ASCII.GetBytes("GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\n"+(origin==null?"":"Origin: "+origin+"\r\n")+"Connection: close\r\n\r\n");client.GetStream().Write(request,0,request.Length);using(StreamReader reader=new StreamReader(client.GetStream()))return reader.ReadToEnd();}}
        public static int Main(string[] args)
        {
            SetErrorMode(3);Console.OutputEncoding=Encoding.UTF8;string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            Run("物理网络筛选、热点支持、最长子网匹配",()=>{
                Check(LanNetwork.Eligible(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,"Microsoft Wi-Fi Direct Virtual Adapter"),"热点被排除");
                Check(!LanNetwork.Eligible(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,"WireGuard Tunnel"),"VPN 被选用");
                var link=new LanNetwork.Link{Address=IPAddress.Parse("192.168.43.1"),Mask=IPAddress.Parse("255.255.255.0"),Index=1};Check(link.Broadcast.Equals(IPAddress.Parse("192.168.43.255")),"广播错误");Check(!link.Contains(IPAddress.Parse("10.0.0.1")),"放行网段外地址");
            });
            Run("目录协议与路径越界拒绝",()=>{var file=new Attachment{Id=0,Name="中文😀.txt",RelativePath="目录/子目录/中文😀.txt",Size=6,Sha256=new string('a',64)};var parsed=Protocol.ParseFiles(Protocol.FileList(new[]{file}));Check(parsed.Count==1&&parsed[0].RelativePath==file.RelativePath,"目录协议丢失");foreach(string path in new[]{"../secret","/root/a","a//b","a/CON","a/../b","a\\b"})Check(!Protocol.SafeRelativePath(path),"接受路径："+path);});
            Run("文件夹展开、相对目录与系统分享隔离队列",()=>{
                string folder=Path.Combine(root,"batch"),child=Path.Combine(folder,"子目录");Directory.CreateDirectory(child);for(int i=0;i<25;i++)File.WriteAllText(Path.Combine(child,"文件"+i+".txt"),"数据 "+i);
                var items=SendBatch.Expand(new[]{folder});Check(items.Count==25&&items.All(f=>f.Relative.StartsWith("batch/子目录/")),"文件夹结构丢失");
                string destination=SendBatch.Destination(Path.Combine(root,"received-folder"),new Attachment{Name="文件0.txt",RelativePath="batch/子目录/文件0.txt"});Check(Path.GetDirectoryName(destination)==Path.Combine(root,"received-folder","batch","子目录"),"接收目录错误");
                string profile=Path.Combine(root,"share-queue");WindowsShare.Enqueue(profile,new[]{folder});Check(WindowsShare.Take(profile).SequenceEqual(new[]{folder}),"系统分享队列丢失路径");Check(WindowsShare.Take(profile)==null,"系统分享重复消费");
                using(LanService a=Node(Path.Combine(root,"batch-a"),Port(),"11111111111111111111111111111111"))using(LanService b=Node(Path.Combine(root,"batch-b"),Port(),"22222222222222222222222222222222")){
                    Peer peer=Find(a,b);var incoming=new System.Collections.Concurrent.ConcurrentQueue<IncomingMessage>();b.MessageReceived+=incoming.Enqueue;
                    var records=a.SendBatchContent(peer,"目录批量",new[]{folder},root,(record,done,total)=>Check(total==25,"总数量错误"));Wait(()=>incoming.Count==2&&records.All(record=>record.State=="已送达"),"跨批邀请未全部收到");Check(incoming.Sum(message=>message.Files.Count)==25&&incoming.SelectMany(message=>message.Files).All(file=>file.RelativePath.StartsWith("batch/子目录/")),"批量丢失目录");
                    a.ForgetSecurity(peer);Check(records.All(record=>a.OutgoingRecord(peer.Id,record.Packet)==null),"撤销后批量授权仍存在");
                }
            });
            Run("TLS 配对、拒绝错误证书、双端重启续传与损坏检测",()=>{
                string aRoot=Path.Combine(root,"a"),bRoot=Path.Combine(root,"b");int aPort=Port(),bPort=Port();const string aid="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",bid="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";IncomingMessage incoming=null;ChatRecord outgoing=null;Peer pa=null;
                string source=Path.Combine(root,"source.bin");byte[] content=Enumerable.Range(0,1024*1024+3).Select(i=>(byte)(i*17)).ToArray();File.WriteAllBytes(source,content);string pin;
                using(LanService a=Node(aRoot,aPort,aid))using(LanService b=Node(bRoot,bPort,bid)){
                    pa=Find(a,b);b.MessageReceived+=m=>incoming=m;a.VerifyPeer(pa);pin=a.Security.Fingerprint;
                    Check(a.Security.IsTrusted(bid,b.Security.Fingerprint)&&b.Security.IsTrusted(aid,pin),"信任未保存");
                    Peer wrong=new Peer{DeviceId=bid,Endpoint=pa.Endpoint,Fingerprint=new string('f',64),Login=pa.Login};bool rejected=false;try{a.VerifyPeer(wrong);}catch(Exception){rejected=true;}Check(rejected,"接受错误证书");
                    outgoing=a.SendMessage(pa,"TLS 文件",new[]{source});Wait(()=>incoming!=null&&outgoing.State=="已送达","安全消息未送达");Check(incoming.Files[0].Sha256==SecureIdentity.Hash(content),"哈希缺失");
                }
                using(LanService a=Node(aRoot,aPort,aid))using(LanService b=Node(bRoot,bPort,bid)){
                    Check(a.Security.Fingerprint==pin,"重启改变身份");Peer peer=Find(b,a);a.Security.ConfirmPeer=(name,code)=>{throw new Exception("已信任设备重复配对");};b.Security.ConfirmPeer=(name,code)=>{throw new Exception("已信任设备重复配对");};
                    string destination=Path.Combine(root,"resumed.bin");Attachment file=incoming.Files[0];File.WriteAllBytes(LanService.PartialPath(peer,incoming.Record.Packet,file,destination),content.Take(123456).ToArray());
                    b.ReceiveFileAsync(peer,incoming.Record.Packet,file,destination,null,CancellationToken.None).GetAwaiter().GetResult();Check(File.ReadAllBytes(destination).SequenceEqual(content),"重启续传内容不一致");
                    string bad=Path.Combine(root,"corrupt.bin");File.WriteAllBytes(LanService.PartialPath(peer,incoming.Record.Packet,file,bad),new byte[123456]);bool rejected=false;try{b.ReceiveFileAsync(peer,incoming.Record.Packet,file,bad,null,CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){rejected=true;}Check(rejected&&!File.Exists(bad),"哈希错误仍提交文件");
                    b.RejectFiles(peer,incoming.Record.Packet);Wait(()=>a.OutgoingRecord(pa.Id,incoming.Record.Packet)==null,"拒绝未撤销邀请");
                }
                using(LanService a=Node(aRoot,aPort,aid))Check(a.OutgoingRecord(pa.Id,incoming.Record.Packet)==null,"重启复活拒绝邀请");
            });
            Run("默认拒绝旧版明文与显式兼容",()=>{using(LanService secure=Node(Path.Combine(root,"secure"),Port(),"cccccccccccccccccccccccccccccccc"))using(LanService legacy=new LanService(new AppSettings(),Port(),true,"legacy")){legacy.Start();Peer peer=Find(secure,legacy);bool rejected=false;try{secure.SendMessage(peer,"secret",null);}catch(IOException){rejected=true;}Check(rejected,"默认发送明文");secure.Settings.AllowLegacyProtocol=true;IncomingMessage got=null;legacy.MessageReceived+=m=>got=m;secure.SendMessage(peer,"compat",null);Wait(()=>got!=null,"兼容消息未到达");}});
            Run("HTTPS 网关令牌、跨站限制与固定证书",()=>{
                using(SecureIdentity server=new SecureIdentity(Path.Combine(root,"https-server")))using(SecureIdentity client=new SecureIdentity(Path.Combine(root,"https-client"))){
                server.ConfirmPeer=(name,code)=>true;client.ConfirmPeer=(name,code)=>true;server.EnsureTrusted("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",client.Fingerprint,"client");client.EnsureTrusted("dddddddddddddddddddddddddddddddd",server.Fingerprint,"server");
                using(ToolsHttp http=new ToolsHttp(0,true,(request,response)=>response.Bytes(Encoding.UTF8.GetBytes("protected"),"text/plain"),server))
                using(ToolsGateway gateway=new ToolsGateway(client,new Peer{DeviceId="dddddddddddddddddddddddddddddddd",Fingerprint=server.Fingerprint,Endpoint=new IPEndPoint(IPAddress.Loopback,2425)},http.Port,true)){
                    Uri uri=new Uri(gateway.Url);Check(Http(uri.Port,"/","")=="","无令牌进入网关");string first=Http(uri.Port,uri.PathAndQuery,"");Check(first.Contains("303"),"网关入口未跳转");string cookie=first.Split(new[]{"\r\n"},StringSplitOptions.None).First(s=>s.StartsWith("Set-Cookie: ")).Substring(12).Split(';')[0];
                    Check(Http(uri.Port,"/",cookie).Contains("protected"),"HTTPS 网关转发失败");Check(Http(uri.Port,"/",cookie,"http://evil.invalid")=="","跨站请求被放行");
                    server.Forget("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");Check(!server.IsTrustedFingerprint(client.Fingerprint),"撤销未移除指纹");Check(!Http(uri.Port,"/",cookie).Contains("protected"),"撤销后仍可访问共享");
                }
                }
            });
            Console.WriteLine("WINDOWS SECURE: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
