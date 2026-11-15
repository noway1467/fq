using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FeiqLight
{
    // 浏览器仅连接回环入口；跨设备链路使用固定证书的 TLS，不安装系统根证书。
    internal sealed class ToolsGateway : IDisposable
    {
        private readonly TcpListener listener;
        private readonly SecureIdentity security;
        private readonly string fingerprint, deviceId, remoteHost, access=ToolsService.RandomToken(32), cookiePrefix;
        private readonly IPEndPoint target;
        private readonly IPAddress local;
        private readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();
        private bool closed;
        internal string Url { get { return "http://"+listener.LocalEndpoint+"/?access="+access; } }
        internal ToolsGateway(SecureIdentity security,Peer peer,int port,bool loopback)
        {
            this.security=security;deviceId=peer.DeviceId;fingerprint=peer.Fingerprint;target=new IPEndPoint(peer.Endpoint.Address,port);remoteHost=target.ToString();
            if(!security.IsTrusted(deviceId,fingerprint))throw new IOException("请先核对设备身份。");
            if(!loopback){LanNetwork.Link route=LanNetwork.Route(LanNetwork.Scan(),target.Address);if(route==null)throw new IOException("目标不在当前物理局域网内。");local=route.Address;}
            else if(!IPAddress.IsLoopback(target.Address))throw new IOException("回环模式禁止局域网连接。");
            cookiePrefix="g"+SecureIdentity.Hash(Encoding.UTF8.GetBytes(peer.DeviceId+":"+port)).Substring(0,16)+"_";
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start(12);Task.Run((Action)Accept);
            security.TrustRevoked+=OnRevoked;
        }
        private void OnRevoked(string value){if(value==fingerprint)Dispose();}
        private void Accept()
        {
            try {while(true){TcpClient client=listener.AcceptTcpClient();lock(clients){if(closed||clients.Count>=12){client.Close();if(closed)return;continue;}clients.Add(client);}Task.Run(()=>Serve(client));}}
            catch(SocketException){Dispose();}catch(ObjectDisposedException){}
        }
        private void Serve(TcpClient client)
        {
            TcpClient remote=null;
            try {
                if(!security.IsTrusted(deviceId,fingerprint))return;
                client.ReceiveTimeout=5000;client.SendTimeout=15000;RequestInput deadline=new RequestInput(client);deadline.Begin(5000);
                string header=ReadHeader(deadline);string[] lines=header.Split(new[]{"\r\n"},StringSplitOptions.None);string[] first=lines[0].Split(' ');
                if(first.Length!=3||first[2]!="HTTP/1.1"||!new[]{"GET","HEAD","POST"}.Contains(first[0]))return;
                Dictionary<string,string> fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(string line in lines.Skip(1).Where(s=>s.Length>0)){int colon=line.IndexOf(':');if(colon<1||fields.ContainsKey(line.Substring(0,colon)))return;fields.Add(line.Substring(0,colon),line.Substring(colon+1).Trim());}
                string host,origin;if(!fields.TryGetValue("Host",out host)||host!=listener.LocalEndpoint.ToString())return;
                if(fields.TryGetValue("Origin",out origin)&&origin!="http://"+host)return;
                Stream output=client.GetStream();string guard="fq_gateway_"+((IPEndPoint)listener.LocalEndpoint).Port;
                if(first[0]=="GET"&&first[1]=="/?access="+access){Write(output,"HTTP/1.1 303 See Other\r\nLocation: /\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nSet-Cookie: "+guard+"="+access+"; Path=/; HttpOnly; SameSite=Strict\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");return;}
                string cookies;fields.TryGetValue("Cookie",out cookies);string[] parts=(cookies??"").Split(';').Select(s=>s.Trim()).ToArray();if(!parts.Contains(guard+"="+access))return;
                int length=0;string bodySize;if(fields.ContainsKey("Transfer-Encoding")||(fields.TryGetValue("Content-Length",out bodySize)&&(!Int32.TryParse(bodySize,out length)||length<0||length>16384)))return;
                if(!first[1].StartsWith("/",StringComparison.Ordinal)||first[1].StartsWith("//",StringComparison.Ordinal))return;
                byte[] body=SecureIdentity.ReadBytes(deadline,length);
                remote=new TcpClient(AddressFamily.InterNetwork);lock(clients){if(closed)return;clients.Add(remote);}
                if(local!=null)remote.Client.Bind(new IPEndPoint(local,0));
                if(!remote.ConnectAsync(target.Address,target.Port).Wait(8000))throw new IOException("共享连接超时。");remote.ReceiveTimeout=45000;remote.SendTimeout=15000;
                using(var tls=security.Client(remote,fingerprint)) {
                    fields["Host"]=remoteHost;fields["Connection"]="close";if(origin!=null)fields["Origin"]="https://"+remoteHost;
                    fields["Cookie"]=String.Join("; ",parts.Where(s=>s.StartsWith(cookiePrefix,StringComparison.Ordinal)).Select(s=>s.Substring(cookiePrefix.Length)));
                    Write(tls,lines[0]+"\r\n"+String.Join("\r\n",fields.Select(p=>p.Key+": "+p.Value))+"\r\n\r\n");tls.Write(body,0,body.Length);tls.Flush();
                    string response=ReadHeader(tls);string[] responseLines=response.Split(new[]{"\r\n"},StringSplitOptions.None);
                    for(int i=1;i<responseLines.Length;i++)if(responseLines[i].StartsWith("Set-Cookie: ",StringComparison.OrdinalIgnoreCase))responseLines[i]="Set-Cookie: "+cookiePrefix+responseLines[i].Substring(12).Replace("; Secure","");
                    Write(output,String.Join("\r\n",responseLines));tls.CopyTo(output,65536);
                }
            } catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}catch(System.Security.Authentication.AuthenticationException){}catch(AggregateException){}
            finally {client.Close();if(remote!=null)remote.Close();lock(clients){clients.Remove(client);if(remote!=null)clients.Remove(remote);}}
        }
        private static string ReadHeader(Stream stream){List<byte> bytes=new List<byte>();int last=0;while(bytes.Count<16384){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();bytes.Add((byte)b);last=(last<<8)|b;if(last==0x0d0a0d0a)return Encoding.ASCII.GetString(bytes.ToArray());}throw new IOException("请求头过长。");}
        private static void Write(Stream stream,string text){byte[] bytes=Encoding.ASCII.GetBytes(text);stream.Write(bytes,0,bytes.Length);}
        public void Dispose(){security.TrustRevoked-=OnRevoked;lock(clients){if(closed)return;closed=true;listener.Stop();foreach(TcpClient client in clients)client.Close();}}
    }
}
