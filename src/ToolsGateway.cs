using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FeiqLight
{
    // 浏览器仅连接回环入口；跨设备链路使用固定证书的 TLS，不安装系统根证书。
    internal sealed class ToolsGateway : IDisposable
    {
        private sealed class Connection
        {
            internal TcpClient Client;
            internal SslStream Tls;
            internal int Requests;
            internal long IdleSince;
            internal bool Pooled, Closed;
        }
        private sealed class Slot { internal Connection Connection; internal Lease Owner; }
        private sealed class Lease { internal Slot Slot; internal string Cookie; internal long Generation; }
        private readonly TcpListener listener;
        private readonly SecureIdentity security;
        private readonly string fingerprint, deviceId, remoteHost, access=ToolsService.RandomToken(32), cookiePrefix;
        private readonly IPEndPoint target;
        private readonly IPAddress local;
        private readonly int routeIndex;
        private readonly LanService node;
        private readonly Peer peer;
        private readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();
        private readonly HashSet<Connection> upstreams = new HashSet<Connection>();
        private readonly Dictionary<CancellationTokenSource,bool> connecting = new Dictionary<CancellationTokenSource,bool>();
        private readonly object gate = new object();
        private readonly Slot screen = new Slot(), input = new Slot();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Timer idleTimer;
        private readonly int networkEpoch;
        // 实例固定设备、指纹、端口和网络代次；两个物理槽再绑定远控 Cookie 与会话代次。
        private string sessionCookie;
        private long sessionGeneration;
        private bool closed;
        private long lastActivity;
        internal const long ScreenResponseLimit=16*1024*1024, InputResponseLimit=64*1024;
        internal bool IsClosed { get { lock(gate)return closed; } }
        internal bool Matches(Peer value,int port)
        {
            if(!Available())return false;
            lock(gate){if(value.DeviceId!=deviceId||value.Fingerprint!=fingerprint||port!=target.Port)return false;lastActivity=clock.ElapsedMilliseconds;return true;}
        }
        internal string Url { get { return "http://"+listener.LocalEndpoint+"/?access="+access; } }
        internal ToolsGateway(SecureIdentity security,Peer peer,int port,bool loopback,LanService node=null)
        {
            this.node=node;networkEpoch=node==null?0:node.TransportEpoch;this.peer=new Peer{DeviceId=peer.DeviceId,Fingerprint=peer.Fingerprint,Endpoint=new IPEndPoint(peer.Endpoint.Address,peer.Endpoint.Port),Login=peer.Login};
            this.security=security;deviceId=peer.DeviceId;fingerprint=peer.Fingerprint;target=new IPEndPoint(peer.Endpoint.Address,port);remoteHost=target.ToString();
            if(!security.IsTrusted(deviceId,fingerprint))throw new IOException("请先核对设备身份。");
            if(!loopback){LanNetwork.Link route=LanNetwork.Route(LanNetwork.Scan(),target.Address);if(route==null)throw new IOException("目标不在当前物理局域网内。");local=route.Address;routeIndex=route.Index;}
            else if(!IPAddress.IsLoopback(target.Address))throw new IOException("回环模式禁止局域网连接。");
            cookiePrefix="g"+SecureIdentity.Hash(Encoding.UTF8.GetBytes(peer.DeviceId+":"+port+":"+fingerprint)).Substring(0,16)+"_";
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start(12);
            idleTimer = new Timer(_ => ExpireIdle(), null, 1000, 1000);
            security.TrustRevoked+=OnRevoked;
            if(node!=null)node.NetworkReset+=Dispose;
            Task.Run((Action)Accept);
        }
        private void OnRevoked(string value){if(value==fingerprint)Dispose();}
        private void Accept()
        {
            try {while(true){TcpClient client=listener.AcceptTcpClient();lock(gate){if(closed||clients.Count>=12){client.Close();if(closed)return;continue;}clients.Add(client);lastActivity=clock.ElapsedMilliseconds;}Task.Run(()=>Serve(client));}}
            catch(SocketException){Dispose();}catch(ObjectDisposedException){}
        }
        private void Serve(TcpClient client)
        {
            Connection remote=null;
            Lease lease=null;
            bool reusable=false,downstreamStarted=false,authenticationRejected=false;
            int rejection=0;
            Timer readDeadline=null,downstreamMonitor=null;
            bool timedOut=false;
            try {
                if(!Available())return;
                client.ReceiveTimeout=5000;client.SendTimeout=15000;RequestInput deadline=new RequestInput(client);deadline.Begin(5000);
                string header=ReadHeader(deadline);string[] lines=header.Split(new[]{"\r\n"},StringSplitOptions.None);string[] first=lines[0].Split(' ');
                if(first.Length!=3||first[2]!="HTTP/1.1"||!new[]{"GET","HEAD","POST"}.Contains(first[0]))return;
                Dictionary<string,string> fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(string line in lines.Skip(1).Where(s=>s.Length>0)){int colon=line.IndexOf(':');if(colon<1||fields.ContainsKey(line.Substring(0,colon)))return;fields.Add(line.Substring(0,colon),line.Substring(colon+1).Trim());}
                string host,origin;if(!fields.TryGetValue("Host",out host)||host!=listener.LocalEndpoint.ToString())return;
                if(fields.TryGetValue("Origin",out origin)&&origin!="http://"+host)return;
                Stream output=client.GetStream();string guard="fq_gateway_"+((IPEndPoint)listener.LocalEndpoint).Port;
                if((first[0]=="GET"||first[0]=="HEAD")&&first[1]=="/?access="+access){Write(output,"HTTP/1.1 303 See Other\r\nLocation: /?gateway="+access+"\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nSet-Cookie: "+guard+"="+access+"; Path=/; HttpOnly; SameSite=Lax\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");return;}
                string cookies;fields.TryGetValue("Cookie",out cookies);string[] parts=(cookies??"").Split(';').Select(s=>s.Trim()).ToArray();
                bool cookieAllowed=parts.Contains(guard+"="+access);
                string gwHeader;fields.TryGetValue("X-FQ-Gateway",out gwHeader);
                bool tokenAllowed=HasAccess(first[1])||gwHeader==access;
                bool staticShell=(first[0]=="GET"||first[0]=="HEAD")&&(first[1]=="/app.css"||first[1]=="/app.js"||first[1]=="/favicon.ico");
                if(!cookieAllowed&&!tokenAllowed&&!staticShell)return;
                int length=0;string bodySize;if(fields.ContainsKey("Transfer-Encoding")||(fields.TryGetValue("Content-Length",out bodySize)&&(!Int32.TryParse(bodySize,out length)||length<0||length>16384)))return;
                if(!first[1].StartsWith("/",StringComparison.Ordinal)||first[1].StartsWith("//",StringComparison.Ordinal))return;
                byte[] body=SecureIdentity.ReadBytes(deadline,length);
                string[] forwardedCookies=parts.Where(s=>s.StartsWith(cookiePrefix,StringComparison.Ordinal)).Select(s=>s.Substring(cookiePrefix.Length)).ToArray();
                string remoteCookie=RemoteCookie(forwardedCookies);
                string targetPath=StripAccess(first[1]),path=targetPath.Split('?')[0];
                bool archive=path=="/file"&&targetPath.Split('?').Skip(1).SelectMany(value=>value.Split('&')).Contains("archive=store");
                bool remoteResponse=path=="/api/screen"||path=="/api/input";
                long generation;lock(gate)generation=sessionGeneration;
                string csrf;fields.TryGetValue("X-FQ-Token",out csrf);
                bool logout=first[0]=="POST"&&path=="/api/logout"&&remoteCookie.Length>0&&csrf==remoteCookie.Substring(remoteCookie.IndexOf('=')+1);
                if(logout)
                {
                    lock(gate){if(sessionCookie!=null&&sessionCookie!=remoteCookie)throw new IOException("远控会话已变更。");generation=sessionGeneration;}
                    if(!ChangeSession("",generation))throw new IOException("远控会话已变更。");
                    generation++;
                }
                if(remoteCookie.Length>0&&((first[0]=="GET"&&path=="/api/screen")||(first[0]=="POST"&&path=="/api/input")))
                    lease=Acquire(path=="/api/screen"?screen:input,remoteCookie);
                fields["Host"]=remoteHost;fields["Connection"]=lease==null?"close":"keep-alive";if(origin!=null)fields["Origin"]="https://"+remoteHost;
                fields.Remove("X-FQ-Gateway");fields.Remove("Referer");
                fields.Remove("X-FQ-Archive-Progress");if(archive)fields["X-FQ-Archive-Progress"]="1";
                string requestId=Guid.NewGuid().ToString("N");fields["X-FQ-Request-ID"]=requestId;
                fields["Cookie"]=String.Join("; ",forwardedCookies);
                Socket downstreamSocket=client.Client;
                if(archive)downstreamMonitor=new Timer(_=>{
                    try{if(downstreamSocket.Poll(0,SelectMode.SelectRead)&&downstreamSocket.Available==0){Connection active=remote;if(active!=null)CloseConnection(active);}}
                    catch(SocketException){}catch(ObjectDisposedException){}
                },null,100,100);
                for(int attempt=0;attempt<2;attempt++)
                {
                    remote=TakeConnection(lease);
                    if(remoteResponse&&readDeadline==null)readDeadline=new Timer(_=>{timedOut=true;Connection active=remote;if(active!=null)CloseConnection(active);},null,10000,Timeout.Infinite);
                    if(timedOut)throw new IOException("远控响应超时。");
                    bool reused=remote.Requests>0;
                    try
                    {
                    SslStream tls=remote.Tls;
                    Write(tls,first[0]+" "+targetPath+" "+first[2]+"\r\n"+String.Join("\r\n",fields.Select(p=>p.Key+": "+p.Value))+"\r\n\r\n");
                    // Framework SslStream 的空写入仍会发 TLS 记录；GET 后多出的未读记录会令对端关闭时复位连接。
                    if(body.Length>0)tls.Write(body,0,body.Length);tls.Flush();
                    string response;
                    do {response=ReadHeader(tls);if(response.StartsWith("HTTP/1.1 102 ",StringComparison.Ordinal)&&(!archive||!response.Contains("\r\nX-FQ-Archive-Progress: 1\r\n")))throw new IOException("归档进展响应无效。");}
                    while(response.StartsWith("HTTP/1.1 102 ",StringComparison.Ordinal));
                    List<string> responseLines=response.Split(new[]{"\r\n"},StringSplitOptions.RemoveEmptyEntries).ToList();
                    string[] status=responseLines[0].Split(new[]{' '},3);int code;
                    if(status.Length!=3||status[0]!="HTTP/1.1"||!Int32.TryParse(status[1],NumberStyles.None,CultureInfo.InvariantCulture,out code)||code<200||code>599)throw new IOException("共享响应状态无效。");
                    authenticationRejected=code==401||code==403;
                    if(authenticationRejected)rejection=code;
                    bool keepAlive=ConnectionToken(responseLines,"keep-alive")&&!ConnectionToken(responseLines,"close");
                    if(responseLines.Any(line=>line.StartsWith("Transfer-Encoding:",StringComparison.OrdinalIgnoreCase)))throw new IOException("共享响应帧边界无效。");
                    string nextCookie=RemoteCookie(responseLines.Where(line=>line.StartsWith("Set-Cookie:",StringComparison.OrdinalIgnoreCase)).Select(line=>line.Substring(11).Trim().Split(';')[0]));
                    for(int i=1;i<responseLines.Count;i++)if(responseLines[i].StartsWith("Set-Cookie:",StringComparison.OrdinalIgnoreCase))responseLines[i]="Set-Cookie: "+cookiePrefix+responseLines[i].Substring(11).Trim().Replace("; Secure","").Replace("SameSite=Strict","SameSite=Lax");
                    if(!cookieAllowed&&tokenAllowed&&responseLines.Count>1)responseLines.Insert(1,"Set-Cookie: "+guard+"="+access+"; Path=/; HttpOnly; SameSite=Lax");
                    string[] lengths=responseLines.Where(line=>line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).ToArray();long remaining;
                    if(lengths.Length!=1||!Int64.TryParse(lengths[0].Substring(15).Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out remaining)||remaining<0)throw new IOException("共享响应长度无效。");
                    if(remoteResponse&&remaining>(path=="/api/screen"?ScreenResponseLimit:InputResponseLimit))throw new IOException("远控响应超过长度上限。");
                    string[] responseIds=responseLines.Where(line=>line.StartsWith("X-FQ-Request-ID:",StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(responseIds.Length>1||(reused&&responseIds.Length==0)||(responseIds.Length==1&&responseIds[0].Substring(16).Trim()!=requestId))throw new IOException("上游响应不属于当前请求。");
                    if(!Available()||(lease!=null&&!Current(lease)))throw new IOException("远控会话已变更。");
                    if(first[0]=="POST"&&(path=="/api/login"||path=="/api/reconnect")&&code==200&&nextCookie.Length>0)
                    {
                        // 浏览器收到 Cookie 后可立即发下一请求，必须先发布新会话并关闭旧槽。
                        if(!ChangeSession(nextCookie,generation))throw new IOException("远控会话已变更。");
                    }
                    responseLines.RemoveAll(line=>line.StartsWith("Connection:",StringComparison.OrdinalIgnoreCase)||line.StartsWith("Keep-Alive:",StringComparison.OrdinalIgnoreCase));
                    responseLines.Add("Connection: close");
                    // 上游复用不改变浏览器入口的单请求契约；写出任何头或字节后不能重试。
                    downstreamStarted=true;
                    Write(output,String.Join("\r\n",responseLines)+"\r\n\r\n");
                    // 响应头逐字读取后仍可能留有已解密正文，按声明长度读取，不依赖通用流复制或连接关闭。
                    byte[] buffer=new byte[65536];
                    while(first[0]!="HEAD"&&remaining>0){int count=tls.Read(buffer,0,(int)Math.Min(buffer.Length,remaining));if(count==0)throw new EndOfStreamException("共享响应正文被截断。");output.Write(buffer,0,count);remaining-=count;}
                    remote.Requests++;
                    if(lease!=null&&authenticationRejected)ChangeSession(code==401?"":lease.Cookie,lease.Generation);
                    else if(logout&&code==200)ChangeSession("",generation);
                    else if(logout)ChangeSession(remoteCookie,generation);
                    reusable=lease!=null&&code==200&&keepAlive&&nextCookie.Length==0&&remote.Requests<16&&responseIds.Length==1&&!timedOut;
                    break;
                    }
                    catch(IOException error)
                    {
                        SocketException socket=error.GetBaseException() as SocketException;
                        // Schannel 也可能把陈旧连接 EOF 报为 reset/abort；超时、鉴权和协议错误不属此分支。
                        bool eof=error is EndOfStreamException||(socket!=null&&(socket.SocketErrorCode==SocketError.ConnectionReset||socket.SocketErrorCode==SocketError.ConnectionAborted||socket.SocketErrorCode==SocketError.Shutdown));
                        if(!eof||timedOut||attempt!=0||lease==null||first[0]!="GET"||!reused||downstreamStarted||authenticationRejected||!Current(lease))throw;
                        CloseConnection(remote);remote=null;
                    }
                }
            } catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}catch(System.Security.Authentication.AuthenticationException){}catch(AggregateException){}catch(OperationCanceledException){}
            finally
            {
                if(readDeadline!=null)readDeadline.Dispose();if(downstreamMonitor!=null)downstreamMonitor.Dispose();
                if(lease!=null&&authenticationRejected)ChangeSession(rejection==401?"":lease.Cookie,lease.Generation);
                bool returned=false;
                if(lease!=null)
                {
                    reusable=reusable&&Current(lease);
                    lock(gate)
                    {
                        if(lease.Slot.Owner==lease)
                        {
                            lease.Slot.Owner=null;
                            if(reusable&&remote!=null&&!remote.Closed&&lease.Generation==sessionGeneration){remote.IdleSince=clock.ElapsedMilliseconds;lease.Slot.Connection=remote;returned=true;}
                            Monitor.PulseAll(gate);
                        }
                    }
                }
                if(!returned&&remote!=null)CloseConnection(remote);
                client.Close();lock(gate){clients.Remove(client);lastActivity=clock.ElapsedMilliseconds;}
            }
        }
        private bool Available()
        {
            if(!security.IsTrusted(deviceId,fingerprint)||(node!=null&&node.TransportEpoch!=networkEpoch)){Dispose();return false;}
            lock(gate)return !closed;
        }
        private bool Current(Lease lease)
        {
            if(!Available())return false;
            lock(gate)return lease.Generation==sessionGeneration&&lease.Cookie==sessionCookie&&lease.Slot.Owner==lease;
        }
        private Lease Acquire(Slot slot,string cookie)
        {
            long until=clock.ElapsedMilliseconds+5000;
            long waitingGeneration=-1;
            while(Available())
            {
                lock(gate)
                {
                    if(closed)break;
                    if(sessionCookie==null){sessionCookie=cookie;sessionGeneration++;}
                    if(sessionCookie!=cookie)throw new IOException("远控会话已变更。");
                    if(waitingGeneration<0)waitingGeneration=sessionGeneration;
                    else if(waitingGeneration!=sessionGeneration)throw new IOException("远控会话已变更。");
                    if(slot.Owner==null){Lease lease=new Lease{Slot=slot,Cookie=cookie,Generation=sessionGeneration};slot.Owner=lease;return lease;}
                    int left=(int)(until-clock.ElapsedMilliseconds);
                    if(left<=0)throw new IOException("远控请求等待超时。");
                    Monitor.Wait(gate,left);
                }
            }
            throw new IOException("连接已关闭。");
        }
        private Connection TakeConnection(Lease lease)
        {
            Connection connection=null;
            if(lease!=null)
            {
                if(!Current(lease))throw new IOException("远控会话已变更。");
                lock(gate){if(lease.Generation!=sessionGeneration||lease.Slot.Owner!=lease)throw new IOException("远控会话已变更。");connection=lease.Slot.Connection;lease.Slot.Connection=null;}
                if(connection!=null&&clock.ElapsedMilliseconds-connection.IdleSince>=2000){CloseConnection(connection);connection=null;}
            }
            if(connection!=null)return connection;
            CancellationTokenSource cancellation=new CancellationTokenSource();
            lock(gate){if(closed||(lease!=null&&lease.Generation!=sessionGeneration)){cancellation.Dispose();throw new IOException("连接已关闭。");}connecting.Add(cancellation,lease!=null);}
            connection=new Connection{Pooled=lease!=null};
            try
            {
                connection.Client=node==null?new TcpClient(AddressFamily.InterNetwork):node.ConnectPeer(peer,target.Port,cancellation.Token);
                lock(gate){if(closed||upstreams.Count>=12||(lease!=null&&lease.Generation!=sessionGeneration))throw new IOException("连接已关闭或请求过多。");upstreams.Add(connection);}
                TcpClient raw=connection.Client;raw.ReceiveTimeout=45000;raw.SendTimeout=15000;raw.NoDelay=true;
                if(node==null)
                {
                    if(local!=null){raw.Client.Bind(new IPEndPoint(local,0));try{raw.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(routeIndex));}catch(SocketException){}}
                    if(!raw.ConnectAsync(target.Address,target.Port).Wait(8000))throw new IOException("共享连接超时。");
                }
                connection.Tls=security.Client(raw,fingerprint);
                if(!Available()||(lease!=null&&!Current(lease)))throw new IOException("远控会话已变更。");
                return connection;
            }
            catch{CloseConnection(connection);throw;}
            finally {lock(gate)connecting.Remove(cancellation);cancellation.Dispose();}
        }
        private void CloseConnection(Connection connection)
        {
            bool release;
            lock(gate){release=!connection.Closed;connection.Closed=true;upstreams.Remove(connection);}
            // 先关 socket，立即中断在用读写；节点额度不能等空闲超时才释放。
            if(connection.Client==null)return;
            connection.Client.Close();
            if(release&&node!=null)node.ReleaseConnection(connection.Client);
            if(connection.Tls!=null)try{connection.Tls.Dispose();}catch(IOException){}catch(ObjectDisposedException){}
        }
        private bool ChangeSession(string cookie,long expectedGeneration)
        {
            Connection[] stale;
            CancellationTokenSource[] pending;
            lock(gate)
            {
                if(closed||sessionGeneration!=expectedGeneration)return false;
                sessionCookie=cookie;sessionGeneration++;
                stale=upstreams.Where(connection=>connection.Pooled).ToArray();
                pending=connecting.Where(item=>item.Value).Select(item=>item.Key).ToArray();
                screen.Connection=input.Connection=null;screen.Owner=input.Owner=null;
                Monitor.PulseAll(gate);
            }
            foreach(Connection connection in stale)CloseConnection(connection);
            foreach(CancellationTokenSource cancellation in pending)try{cancellation.Cancel();}catch(ObjectDisposedException){}
            return true;
        }
        private void ExpireIdle()
        {
            if(!Available())return;
            bool expiredGateway;lock(gate)expiredGateway=clients.Count==0&&clock.ElapsedMilliseconds-lastActivity>=600000;
            if(expiredGateway){Dispose();return;}
            List<Connection> expired=new List<Connection>();
            lock(gate)foreach(Slot slot in new[]{screen,input})
                if(slot.Owner==null&&slot.Connection!=null&&clock.ElapsedMilliseconds-slot.Connection.IdleSince>=2000){expired.Add(slot.Connection);slot.Connection=null;}
            foreach(Connection connection in expired)CloseConnection(connection);
        }
        private static string RemoteCookie(IEnumerable<string> cookies)
        {
            string[] sessions=cookies.Where(cookie=>(cookie.StartsWith("fq_remote=",StringComparison.Ordinal)||cookie.StartsWith("fq_phone=",StringComparison.Ordinal))&&cookie.Length>cookie.IndexOf('=')+1).ToArray();
            return sessions.Length==1?sessions[0]:"";
        }
        private static bool ConnectionToken(IEnumerable<string> lines,string token)
        {
            return lines.Where(line=>line.StartsWith("Connection:",StringComparison.OrdinalIgnoreCase)).SelectMany(line=>line.Substring(11).Split(',')).Any(value=>value.Trim().Equals(token,StringComparison.OrdinalIgnoreCase));
        }
        private bool HasAccess(string path){int query=path.IndexOf('?');if(query<0)return false;return path.Substring(query+1).Split('&').Any(p=>p=="gateway="+access||p=="access="+access);}
        private static string StripAccess(string path){int start=path.IndexOf('?');if(start<0)return path;string query=String.Join("&",path.Substring(start+1).Split('&').Where(p=>!p.StartsWith("gateway=",StringComparison.Ordinal)&&!p.StartsWith("access=",StringComparison.Ordinal)));return path.Substring(0,start)+(query.Length==0?"":"?"+query);}
        private static string ReadHeader(Stream stream){List<byte> bytes=new List<byte>();int last=0;while(bytes.Count<16384){int b=stream.ReadByte();if(b<0)throw new EndOfStreamException();bytes.Add((byte)b);last=(last<<8)|b;if(last==0x0d0a0d0a)return Encoding.ASCII.GetString(bytes.ToArray());}throw new IOException("请求头过长。");}
        private static void Write(Stream stream,string text){byte[] bytes=Encoding.ASCII.GetBytes(text);stream.Write(bytes,0,bytes.Length);}
        public void Dispose()
        {
            Connection[] active;
            CancellationTokenSource[] pending;
            lock(gate)
            {
                if(closed)return;closed=true;sessionGeneration++;
                listener.Stop();idleTimer.Dispose();
                foreach(TcpClient client in clients)client.Close();
                active=upstreams.ToArray();screen.Connection=input.Connection=null;screen.Owner=input.Owner=null;
                pending=connecting.Keys.ToArray();
                Monitor.PulseAll(gate);
            }
            security.TrustRevoked-=OnRevoked;if(node!=null)node.NetworkReset-=Dispose;
            foreach(Connection connection in active)CloseConnection(connection);
            foreach(CancellationTokenSource cancellation in pending)try{cancellation.Cancel();}catch(ObjectDisposedException){}
        }
    }
}
