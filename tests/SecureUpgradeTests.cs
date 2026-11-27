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
        private static string only;
        private static void Check(bool value,string message){if(!value)throw new Exception(message);}
        private static void Run(string name,Action test){if(!String.IsNullOrEmpty(only)&&!name.Contains(only))return;try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetBaseException().Message);Console.WriteLine(e.StackTrace);}}
        private static int Port(){using(TcpListenerSocket socket=new TcpListenerSocket())return socket.Port;}
        private static int ToolPorts(){for(int port=18000;port<45000;port+=3){var listeners=new List<TcpListener>();try{for(int i=0;i<3;i++){var listener=new TcpListener(IPAddress.Loopback,port+i);listeners.Add(listener);listener.Start();}return port;}catch(SocketException){}finally{foreach(var listener in listeners)listener.Stop();}}throw new IOException("没有可用测试端口");}
        private sealed class TcpListenerSocket:IDisposable {private readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);public TcpListenerSocket(){listener.Start();}public int Port{get{return ((IPEndPoint)listener.LocalEndpoint).Port;}}public void Dispose(){listener.Stop();}}
        private static void Wait(Func<bool> condition,string message){for(int i=0;i<500;i++){if(condition())return;Thread.Sleep(10);}throw new Exception(message);}
        private static LanService Node(string root,int port,string id){var node=new LanService(new AppSettings{DeviceId=id,Nickname=id},port,true,id);node.ConfigureSecurity(root,(name,code)=>true);node.Start();return node;}
        private static Peer Find(LanService a,LanService b){a.Probe(new IPEndPoint(IPAddress.Loopback,b.Port));Wait(()=>a.Peers.Count>0,"发现超时");return a.Peers.First(p=>p.DeviceId==b.Settings.DeviceId);}
        private static void Raw(LanService sender,LanService receiver,byte[] bytes){typeof(LanService).GetMethod("Send",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(sender,new object[]{bytes,new IPEndPoint(IPAddress.Loopback,receiver.Port)});}
        private static int Entries(LanService node,string field){return ((System.Collections.IDictionary)typeof(LanService).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(node)).Count;}
        private static string Http(int port,string path,string cookie,string origin=null,string method="GET"){using(TcpClient client=new TcpClient()){client.Connect(IPAddress.Loopback,port);client.ReceiveTimeout=10000;byte[] request=Encoding.ASCII.GetBytes(method+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\n"+(origin==null?"":"Origin: "+origin+"\r\n")+"Connection: close\r\n\r\n");client.GetStream().Write(request,0,request.Length);using(StreamReader reader=new StreamReader(client.GetStream()))return reader.ReadToEnd();}}
        public static int Main(string[] args)
        {
            SetErrorMode(3);Console.OutputEncoding=Encoding.UTF8;only=args.Length>1?args[1]:null;string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            Run("断开：迟到的配对确认不能保存信任",()=>{
                foreach(bool outgoing in new[]{false,true})
                {
                    string profile=Path.Combine(root,"disconnect-pair-"+outgoing);
                    using(LanService a=Node(profile,Port(),"15151515151515151515151515151515"))
                    using(LanService b=Node(Path.Combine(root,"disconnect-remote-"+outgoing),Port(),"16161616161616161616161616161616"))
                    using(ManualResetEventSlim entered=new ManualResetEventSlim(false))
                    using(ManualResetEventSlim release=new ManualResetEventSlim(false))
                    {
                        Peer peerB=Find(a,b),peerA=Find(b,a);string fingerprint=b.Security.Fingerprint;
                        a.Security.ConfirmPeer=(name,code)=>{entered.Set();Check(release.Wait(5000),"配对确认未放行");return true;};
                        Task pairing=Task.Run(()=>{try{if(outgoing)a.VerifyPeer(peerB);else b.VerifyPeer(peerA);}catch(IOException){}catch(ObjectDisposedException){}catch(System.Security.Authentication.AuthenticationException){}});
                        try
                        {
                            Check(entered.Wait(5000),"未进入真实 TLS 配对确认");
                            a.Dispose();release.Set();Check(pairing.Wait(5000),"断开后配对任务未退出");
                            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                            object sync=typeof(LanService).GetField("gate",flags).GetValue(a);
                            var sockets=(HashSet<TcpClient>)typeof(LanService).GetField("connections",flags).GetValue(a);
                            Wait(()=>{lock(sync)return sockets.Count==0;},"旧 TLS 处理未结束");
                            Check(!a.Security.IsTrusted(b.Settings.DeviceId,fingerprint),"断开后的旧确认仍建立了信任");
                            using(SecureIdentity restored=new SecureIdentity(profile))
                                Check(!restored.IsTrusted(b.Settings.DeviceId,fingerprint),"重启后恢复了断开时失效的配对");
                        }
                        finally {release.Set();Check(pairing.Wait(5000),"配对测试线程未退出");}
                    }
                }
            });
            Run("物理网络筛选、热点支持、最长子网匹配",()=>{
                Check(LanNetwork.Eligible(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,"Microsoft Wi-Fi Direct Virtual Adapter"),"热点被排除");
                Check(!LanNetwork.Eligible(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,"WireGuard Tunnel"),"VPN 被选用");
                var link=new LanNetwork.Link{Address=IPAddress.Parse("192.168.43.1"),Mask=IPAddress.Parse("255.255.255.0"),Index=1};Check(link.Broadcast.Equals(IPAddress.Parse("192.168.43.255")),"广播错误");Check(!link.Contains(IPAddress.Parse("10.0.0.1")),"放行网段外地址");
            });
            Run("文件额度：完成释放、可调上限、千项邀请重启恢复",()=>{
                string profile=Path.Combine(root,"quota-sender"),receiver=Path.Combine(root,"quota-receiver"),source=Path.Combine(root,"quota.txt");File.WriteAllText(source,"quota");
                var settings=new AppSettings{BatchFileLimit=50000,PendingFileLimit=100};var store=new LocalStore(Path.Combine(root,"quota-settings"));store.SaveSettings(settings);
                Check(store.LoadSettings().BatchFileLimit==50000&&store.LoadSettings().PendingFileLimit==100,"额度没有持久化");
                var legacy=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AppSettings>("{}");Check(legacy.BatchFileLimit==5000&&legacy.PendingFileLimit==10000,"旧设置默认额度错误");
                using(LanService a=Node(profile,Port(),"11111111111111111111111111111112"))using(LanService b=Node(receiver,Port(),"22222222222222222222222222222223")){
                    a.Settings.PendingFileLimit=100;Peer peer=Find(a,b),sender=Find(b,a);var incoming=new System.Collections.Concurrent.ConcurrentQueue<IncomingMessage>();b.MessageReceived+=incoming.Enqueue;int completed=0;
                    a.TransferChanged+=(record,id,percent,state,rate)=>{if(state=="对方已校验保存")Interlocked.Increment(ref completed);};
                    for(int i=0;i<5;i++){ChatRecord sent=a.SendMessage(peer,"",Enumerable.Repeat(source,20).ToArray());Wait(()=>sent.State=="已送达","邀请未送达");}
                    Wait(()=>incoming.Count==5,"邀请未入队");bool full=false;try{a.SendMessage(peer,"",new[]{source});}catch(InvalidOperationException){full=true;}Check(full,"待处理上限未生效");
                    IncomingMessage first;Check(incoming.TryPeek(out first),"无首条邀请");b.ReceiveFileAsync(sender,first.Record.Packet,first.Files[0],Path.Combine(root,"quota-received.txt"),null,CancellationToken.None).GetAwaiter().GetResult();
                    Wait(()=>completed==1,"未收到保存确认");
                    ChatRecord next=a.SendMessage(peer,"",new[]{source});Wait(()=>next.State=="已送达","已收完文件仍占额度");
                    a.Settings.PendingFileLimit=2000;
                    for(int i=0;i<51;i++){ChatRecord sent=a.SendMessage(peer,"",Enumerable.Repeat(source,20).ToArray());Wait(()=>sent.State=="已送达","扩大额度后未送达");}
                    Check(Entries(a,"offers")==1121,"千项邀请数量错误");
                }
                using(LanService restored=Node(profile,Port(),"11111111111111111111111111111112"))Check(Entries(restored,"offers")==1121,"超过千项的邀请重启丢失");
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
            Run("默认兼容旧版明文，手动关闭后拒绝发送",()=>{using(LanService secure=Node(Path.Combine(root,"secure"),Port(),"cccccccccccccccccccccccccccccccc"))using(LanService legacy=new LanService(new AppSettings(),Port(),true,"legacy")){
                legacy.Start();Peer peer=Find(secure,legacy);Check(secure.Settings.AllowLegacyProtocol,"新配置未默认开启兼容");IncomingMessage got=null;legacy.MessageReceived+=m=>got=m;secure.SendMessage(peer,"compat",null);Wait(()=>got!=null,"兼容消息未到达");
                secure.Settings.AllowLegacyProtocol=false;bool rejected=false;try{secure.SendMessage(peer,"disabled",null);}catch(IOException){rejected=true;}Check(rejected,"手动关闭后仍发送明文");
            }});
            Run("已配对会话拒绝省略身份的明文消息和退出报文",()=>{
                using(LanService a=Node(Path.Combine(root,"strip-a"),Port(),"12121212121212121212121212121212"))using(LanService b=Node(Path.Combine(root,"strip-b"),Port(),"34343434343434343434343434343434")){
                    Peer peer=Find(a,b);a.VerifyPeer(peer);var received=new System.Collections.Concurrent.ConcurrentQueue<string>();a.MessageReceived+=m=>received.Enqueue(m.Record.Text);
                    Raw(b,a,Protocol.Encode(93001,b.Login,b.Host,Protocol.SendMessage|Protocol.Utf8,"stripped-message",null));
                    Raw(b,a,Protocol.Encode(93002,b.Login,b.Host,Protocol.Exit|Protocol.Utf8,"stripped-exit",null));
                    Raw(b,a,Protocol.Encode(93003,b.Login,b.Host,Protocol.SendMessage|Protocol.Utf8,"legacy-barrier",null,"56565656565656565656565656565656"));
                    Wait(()=>received.Contains("legacy-barrier"),"旧设备兼容消息未处理");
                    Check(!received.Contains("stripped-message"),"省略设备 ID 的明文混入已信任会话");Check(peer.Online,"省略身份的退出报文把已信任联系人下线");
                }
            });
            Run("文件任务写盘失败不留下重试消息或发送授权",()=>{
                string senderRoot=Path.Combine(root,"write-failure-a"),file=Path.Combine(root,"write-failure.txt");File.WriteAllText(file,"未发送的测试文件");
                using(LanService a=Node(senderRoot,Port(),"78787878787878787878787878787878"))using(LanService b=Node(Path.Combine(root,"write-failure-b"),Port(),"90909090909090909090909090909090")){
                    Peer peer=Find(a,b);a.VerifyPeer(peer);Directory.CreateDirectory(Path.Combine(senderRoot,"transfer-offers.json.new"));bool rejected=false;
                    try{a.SendMessage(peer,"不能后台补发",new[]{file});}catch(IOException){rejected=true;}catch(UnauthorizedAccessException){rejected=true;}
                    Check(rejected,"未复现写盘失败");Check(Entries(a,"pending")==0&&Entries(a,"offers")==0,"报告发送失败后仍保留自动重试或附件授权");
                }
            });
            Run("文件夹与其中的文件重复选择时仅发送一次",()=>{
                string folder=Path.Combine(root,"batch"),child=Path.Combine(folder,"子目录");
                var items=SendBatch.Expand(new[]{folder,Path.Combine(child,"文件0.txt"),child});
                Check(items.Count==25&&items.Select(item=>item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()==25,"重叠选择产生重复文件");
            });
            Run("长正文与目录一起发送保留 TXT 原文及全部相对路径",()=>{
                string text=new string('文',22000),folder=Path.Combine(root,"batch");
                using(LanService a=Node(Path.Combine(root,"long-batch-a"),Port(),"abababababababababababababababab"))using(LanService b=Node(Path.Combine(root,"long-batch-b"),Port(),"cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd")){
                    Peer peer=Find(a,b);var incoming=new System.Collections.Concurrent.ConcurrentQueue<IncomingMessage>();b.MessageReceived+=incoming.Enqueue;
                    var sent=a.SendBatchContent(peer,text,new[]{folder},root,(record,done,total)=>Check(total==25,"批次总文件数错误"));Wait(()=>incoming.Count==2&&sent.All(record=>record.State=="已送达"),"长正文批次未全部送达");
                    Check(incoming.Sum(entry=>entry.Files.Count)==26,"目录或 TXT 附件丢失");
                    IncomingMessage message=incoming.Single(m=>m.Files.Any(f=>f.Name.StartsWith("消息-")));Attachment file=message.Files.Single(f=>f.Name.StartsWith("消息-"));
                    string destination=Path.Combine(root,"batch-long-text.txt");b.ReceiveFileAsync(Find(b,a),message.Record.Packet,file,destination,null,CancellationToken.None).GetAwaiter().GetResult();
                    Check(File.ReadAllText(destination,Encoding.UTF8)==text,"批量长正文被截断或改写");Check(incoming.SelectMany(m=>m.Files).Count(f=>f.RelativePath!=null)==25,"批量长正文丢失目录结构");
                }
            });
            Run("HTTPS 网关令牌、跨站限制与固定证书",()=>{
                using(SecureIdentity server=new SecureIdentity(Path.Combine(root,"https-server")))using(SecureIdentity client=new SecureIdentity(Path.Combine(root,"https-client"))){
                server.ConfirmPeer=(name,code)=>true;client.ConfirmPeer=(name,code)=>true;server.EnsureTrusted("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",client.Fingerprint,"client");client.EnsureTrusted("dddddddddddddddddddddddddddddddd",server.Fingerprint,"server");
                string payload=new string('x',131073);
                using(ToolsHttp http=new ToolsHttp(0,true,(request,response)=>response.Bytes(Encoding.UTF8.GetBytes(request.Path=="/large"?payload:"protected"),"text/plain"),server))
                using(ToolsGateway gateway=new ToolsGateway(client,new Peer{DeviceId="dddddddddddddddddddddddddddddddd",Fingerprint=server.Fingerprint,Endpoint=new IPEndPoint(IPAddress.Loopback,2425)},http.Port,true)){
                    using(TcpClient direct=new TcpClient()) {direct.Connect(IPAddress.Loopback,http.Port);using(var transport=client.Client(direct,server.Fingerprint)) {byte[] request=Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1:"+http.Port+"\r\nConnection: close\r\n\r\n");transport.Write(request,0,request.Length);using(StreamReader reader=new StreamReader(transport)) {string directResponse=reader.ReadToEnd();Check(directResponse.Contains("protected"),"直接 HTTPS 响应没有正文，长度="+directResponse.Length);}}}
                    Uri uri=new Uri(gateway.Url);Check(Http(uri.Port,"/","")=="","无令牌进入网关");string first=Http(uri.Port,uri.PathAndQuery,"");Check(first.Contains("303")&&first.Contains("Location: /?gateway=")&&first.Contains("SameSite=Lax"),"网关入口未携带跳转参数或 SameSite=Lax");
                    string location=first.Split(new[]{"\r\n"},StringSplitOptions.None).First(s=>s.StartsWith("Location: ")).Substring(10).Trim();
                    string withoutCookie=Http(uri.Port,location,"");Check(withoutCookie.EndsWith("protected")&&withoutCookie.Contains("Set-Cookie: "),"外部浏览器丢弃重定向 Cookie 后无法打开共享页");
                    Check(Http(uri.Port,location+"suffix","")=="","网关接受非完整令牌");
                    string cookie=first.Split(new[]{"\r\n"},StringSplitOptions.None).First(s=>s.StartsWith("Set-Cookie: ")).Substring(12).Split(';')[0];
                    string forwarded=Http(uri.Port,"/",cookie);Check(forwarded.EndsWith("protected"),"HTTPS 网关正文丢失，响应长度="+forwarded.Length);Check(Http(uri.Port,"/",cookie,"http://evil.invalid")=="","跨站请求被放行");
                    Check(Http(uri.Port,"/large",cookie).EndsWith(payload,StringComparison.Ordinal),"HTTPS 网关截断了多段正文");
                    string head=Http(uri.Port,"/",cookie,null,"HEAD");Check(head.Contains("Content-Length: 9")&&head.EndsWith("\r\n\r\n"),"HEAD 请求带上了正文或长度错误");
                    server.Forget("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");Check(!server.IsTrustedFingerprint(client.Fingerprint),"撤销未移除指纹");Check(!Http(uri.Port,"/",cookie).Contains("protected"),"撤销后仍可访问共享");
                }
                }
            });
            Run("VPN 单向连接受阻时通过反向同步送达消息与推送文件",()=>{
                const string aid="dededededededededededededededede",bid="efefefefefefefefefefefefefefefef";
                string source=Path.Combine(root,"vpn-push-source.bin"),destination=Path.Combine(root,"vpn-pushed.bin");
                byte[] payload=Enumerable.Range(0,256*1024+19).Select(i=>(byte)(i*31+7)).ToArray();File.WriteAllBytes(source,payload);
                using(LanService a=Node(Path.Combine(root,"vpn-a"),Port(),aid))using(LanService b=Node(Path.Combine(root,"vpn-b"),Port(),bid)){
                    Peer peerB=Find(a,b),peerA=Find(b,a);a.VerifyPeer(peerB);
                    IncomingMessage bGot=null,aGot=null;b.MessageReceived+=m=>bGot=m;a.MessageReceived+=m=>aGot=m;
                    // 停止 B 的 TCP 监听模拟 VPN 阻断入站 TCP（A 无法直连 B，仅 B 可主动连 A）。
                    ((TcpListener)typeof(LanService).GetField("tcp",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(b)).Stop();
                    ChatRecord sentText=a.SendMessage(peerB,"VPN反向同步文本",null);
                    Wait(()=>bGot!=null&&bGot.Record.Text=="VPN反向同步文本"&&sentText.State=="已送达","单向受阻时反向同步未送达文本消息");
                    ChatRecord sentFile=b.SendMessage(peerA,"VPN文件邀请",new[]{source});
                    Wait(()=>aGot!=null&&aGot.Files!=null&&aGot.Files.Count==1&&sentFile.State=="已送达","单向受阻时文件邀请未送达");
                    a.ReceiveFileAsync(peerB,aGot.Record.Packet,aGot.Files[0],destination,null,CancellationToken.None).GetAwaiter().GetResult();
                    Check(File.ReadAllBytes(destination).SequenceEqual(payload),"单向受阻时反向推送文件内容不一致");
                }
            });
            Run("VPN 回连身份核验、共享远控端口、流式正文与附件拒绝",()=>{
                const string aid="01010101010101010101010101010101",bid="02020202020202020202020202020202";
                using(LanService a=Node(Path.Combine(root,"reverse-a"),Port(),aid))using(LanService b=Node(Path.Combine(root,"reverse-b"),ToolPorts(),bid)){
                    Peer peerB=Find(a,b),peerA=Find(b,a);a.VerifyPeer(peerB);
                    a.PeerConnector=(socket,target)=>{throw new SocketException((int)SocketError.ConnectionRefused);};
                    a.VerifyPeer(peerB);Check(peerB.Online,"反向身份核验未完成");
                    string payload=new string('z',256*1024+13);
                    foreach(int servicePort in new[]{b.Port==65535?65534:b.Port+1,b.Port>=65534?b.Port-2:b.Port+2})
                    using(ToolsHttp http=new ToolsHttp(servicePort,true,(request,response)=>{
                        Check(request.Fingerprint==a.Security.Fingerprint,"回连丢失客户端身份");
                        if(request.Path=="/api/input")response.Bytes(request.Body,"application/json");
                        else response.Bytes(Encoding.UTF8.GetBytes(payload),"text/plain");
                    },b.Security))using(ToolsGateway gateway=new ToolsGateway(a.Security,peerB,servicePort,true,a)){
                        Uri uri=new Uri(gateway.Url);string redirect=Http(uri.Port,uri.PathAndQuery,"");string cookie=redirect.Split(new[]{"\r\n"},StringSplitOptions.None).First(s=>s.StartsWith("Set-Cookie: ")).Substring(12).Split(';')[0];Console.WriteLine("CHECK 回连 GET");
                        Check(Http(uri.Port,"/",cookie).EndsWith(payload,StringComparison.Ordinal),"共享/远控回连正文不完整");
                        Check(Http(uri.Port,"/",cookie,null,"HEAD").EndsWith("\r\n\r\n"),"回连 HEAD 响应错误");
                    }
                    string source=Path.Combine(root,"reverse-reject.txt");File.WriteAllText(source,"拒绝文件");IncomingMessage offer=null;a.MessageReceived+=m=>offer=m;
                    ChatRecord sent=b.SendMessage(peerA,"拒绝",new[]{source});Wait(()=>offer!=null,"文件邀请丢失");a.RejectFiles(peerB,sent.Packet);Wait(()=>b.OutgoingRecord(peerA.Id,sent.Packet)==null,"回连拒绝未撤销文件授权");
                    a.ForgetSecurity(peerB);Check(Entries(a,"reverseRequests")==0,"撤销后残留回连请求");
                }
            });
            Run("重复关闭旧共享服务不能移除重建后的回连入口",()=>{
                using(LanService a=Node(Path.Combine(root,"lifecycle-a"),Port(),"03030303030303030303030303030303"))using(LanService b=Node(Path.Combine(root,"lifecycle-b"),ToolPorts(),"04040404040404040404040404040404")){
                    Peer peer=Find(a,b);a.VerifyPeer(peer);a.PeerConnector=(socket,target)=>{throw new SocketException((int)SocketError.ConnectionRefused);};
                    int port=b.Port+1;ToolsHttp old=new ToolsHttp(port,true,(request,response)=>response.Json(new{}),b.Security);old.Dispose();
                    using(ToolsHttp current=new ToolsHttp(port,true,(request,response)=>response.Bytes(Encoding.UTF8.GetBytes("rebuilt"),"text/plain"),b.Security))using(ToolsGateway gateway=new ToolsGateway(a.Security,peer,port,true,a)){
                        old.Dispose();Uri uri=new Uri(gateway.Url);string redirect=Http(uri.Port,uri.PathAndQuery,"");string cookie=redirect.Split(new[]{"\r\n"},StringSplitOptions.None).First(s=>s.StartsWith("Set-Cookie: ")).Substring(12).Split(';')[0];
                        Check(Http(uri.Port,"/",cookie).EndsWith("rebuilt"),"新服务无法通过回连接收真实 HTTP 请求");
                    }
                }
            });
            Console.WriteLine("WINDOWS SECURE: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
