using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FeiqLight
{
    public sealed class OfferRevokedException : IOException
    {
        public OfferRevokedException() : base("对方已取消发送") { }
    }
    public sealed class LanService : IDisposable
    {
        private sealed class Pending
        {
            public readonly Peer Peer;
            public readonly IPEndPoint Endpoint;
            public readonly string Login, DeviceId, Fingerprint;
            public int Sending, Transmitting;
            public volatile bool Delivered, DirectFailed, Canceled;
            public TimeSpan AttemptStarted = clock.Elapsed;
            public volatile TcpClient ConnectingClient;
            public byte[] Bytes; public ChatRecord Record;
            public DateTime Last; public int Attempts;
            public Pending(Peer peer)
            {
                Peer = peer;
                Endpoint = new IPEndPoint(peer.Endpoint.Address, peer.Endpoint.Port);
                Login = peer.Login; DeviceId = peer.DeviceId; Fingerprint=peer.Fingerprint;
            }
        }
        private sealed class Offer
        {
            public string Path; public long Size; public DateTime Modified;
            public string Address; public DateTime Expires;
            public ChatRecord Record; public int FileId, Percent; public long ProgressVersion; public string TransferState;
            public string DeviceId, Fingerprint, Sha256;
            public int Pushing;
            public bool Canceled;
            public readonly HashSet<TcpClient> ActiveClients = new HashSet<TcpClient>();
        }
        private sealed class PendingDownload
        {
            public readonly string DeviceId, Fingerprint;
            public readonly long Packet, Offset;
            public readonly int FileId;
            public readonly TimeSpan CreatedAt = clock.Elapsed;
            public volatile bool DirectFailed;
            public volatile TcpClient DirectClient, PushedClient;
            public volatile SslStream PushedStream;
            public int Claimed;
            public readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false), Done = new ManualResetEventSlim(false);
            public PendingDownload(string deviceId, string fingerprint, long packet, int fileId, long offset)
            {
                DeviceId = deviceId; Fingerprint = fingerprint; Packet = packet; FileId = fileId; Offset = offset;
            }
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        private readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        private readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();
        private readonly Dictionary<string, Offer> offers = new Dictionary<string, Offer>();
        private readonly List<PendingDownload> pendingDownloads = new List<PendingDownload>();
        private readonly HashSet<string> activeSyncPeers = new HashSet<string>();
        private sealed class ReverseRequest
        {
            internal string DeviceId, Fingerprint;
            internal int Port;
            internal TcpClient Client, Direct;
            internal readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        }
        private readonly Dictionary<string,ReverseRequest> reverseRequests = new Dictionary<string,ReverseRequest>();
        private readonly HashSet<string> activeReverseRequests = new HashSet<string>();
        private int transportEpoch;
        internal int TransportEpoch {get{lock(gate)return transportEpoch;}}
        internal Action<TcpClient,IPEndPoint> PeerConnector = (client,target)=>{if(!client.ConnectAsync(target.Address,target.Port).Wait(8000))throw new IOException("连接超时。");};
        private readonly HashSet<TcpClient> connections = new HashSet<TcpClient>();
        private readonly HashSet<TcpClient> probeSockets = new HashSet<TcpClient>();
        private readonly HashSet<IPEndPoint> pendingTcpTargets = new HashSet<IPEndPoint>();
        private readonly HashSet<IPEndPoint> activeTcpProbes = new HashSet<IPEndPoint>();
        private readonly SemaphoreSlim probeConcurrency = new SemaphoreSlim(4, 4);
        private int tcpFallbackScheduled;
        private static readonly SemaphoreSlim[] receiveLocks = Enumerable.Range(0, 32).Select(i => new SemaphoreSlim(1, 1)).ToArray();
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        // 静默 6 秒后逐个单播 ENTRY，连续 3 次无回应判离线；网络切换或手动刷新后 2 次无回应即离线。
        internal TimeSpan HeartbeatIdle = TimeSpan.FromSeconds(6), HeartbeatRetry = TimeSpan.FromSeconds(3);
        internal int IdleMisses = 3, VerifyMisses = 2;
        private TimeSpan verifyFrom;
        private int networkDirty;
        private volatile HashSet<IPAddress> localAddresses;
        private readonly object sendGate = new object();
        internal Func<List<LanNetwork.Link>> NetworkLinks = LanNetwork.Scan;
        private List<LanNetwork.Link> links = new List<LanNetwork.Link>();
        private string networkKey;
        private TimeSpan lastMaintenance;
        public string NetworkStatus { get { return LoopbackOnly?"回环测试":links.Count==0?"网络已断开，等待 Wi-Fi、热点或以太网恢复":"物理局域网 · "+String.Join("、",links.Select(l=>l.Address.ToString()))+" · UDP/TCP "+Port; } }
        private UdpClient udp;
        private TcpListener tcp;
        private Timer timer;
        private volatile bool disposed;
        private DateTime lastRefresh;
        private long sequence;
        public readonly int Port;
        public readonly bool LoopbackOnly;
        public readonly string Login;
        public readonly string Host;
        public AppSettings Settings { get; set; }
        public SecureIdentity Security { get; private set; }
        private string dataRoot;
        public sealed class StoredOffer
        {
            public string Key, Path, Address, DeviceId, Fingerprint, Sha256;
            public long Size, ModifiedTicks, ExpiresTicks;
            public int FileId;
            public ChatRecord Record;
            public string TransferState;
            public int Percent;
        }
        public event Action NetworkReset;
        public void ConfigureSecurity(string root, Func<string,string,bool> confirmation)
        {
            if(udp!=null)throw new InvalidOperationException("安全身份必须在启动前配置。");
            dataRoot=root;Security=new SecureIdentity(root);Security.ConfirmPeer=confirmation;Security.TrustRevoked+=OnTransportRevoked;RestoreOffers();
        }
        private void OnTransportRevoked(string fingerprint)
        {
            lock(gate){foreach(TcpClient client in connections)client.Close();foreach(TcpClient client in probeSockets)client.Close();CancelReverseRequests();}
        }
        private void CancelReverseRequests(){transportEpoch++;foreach(ReverseRequest request in reverseRequests.Values)request.Ready.Set();reverseRequests.Clear();}
        private void SaveOffers(Dictionary<string,Offer> snapshot = null)
        {
            if(dataRoot==null)return;
            lock(gate) {
                if(disposed)return;
                var saved=(snapshot??offers).Where(o=>o.Value.Fingerprint!=null).Select(o=>new StoredOffer{Key=o.Key,Path=o.Value.Path,Address=o.Value.Address,DeviceId=o.Value.DeviceId,Fingerprint=o.Value.Fingerprint,Sha256=o.Value.Sha256,Size=o.Value.Size,ModifiedTicks=o.Value.Modified.Ticks,ExpiresTicks=o.Value.Expires.Ticks,FileId=o.Value.FileId,Record=o.Value.Record,TransferState=o.Value.TransferState,Percent=o.Value.Percent}).ToArray();
                SecureIdentity.AtomicBytes(Path.Combine(dataRoot,"transfer-offers.json"),System.Text.Encoding.UTF8.GetBytes(new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=8*1024*1024}.Serialize(saved)));
            }
        }
        private void RestoreOffers()
        {
            string path=Path.Combine(dataRoot,"transfer-offers.json");if(!File.Exists(path))return;
            if(new FileInfo(path).Length>8*1024*1024)throw new IOException("文件任务记录过大。");
            var saved=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=8*1024*1024}.Deserialize<StoredOffer[]>(File.ReadAllText(path,System.Text.Encoding.UTF8));
            if(saved==null||saved.Length>1000)throw new IOException("文件任务记录损坏。");
            foreach(StoredOffer item in saved) {
                if(item==null||item.Record==null||item.ExpiresTicks<=DateTime.UtcNow.Ticks||!Security.IsTrusted(item.DeviceId,item.Fingerprint)||!SecureIdentity.ValidFingerprint(item.Sha256))continue;
                FileInfo file=new FileInfo(item.Path);if(!file.Exists||file.Length!=item.Size||file.LastWriteTimeUtc.Ticks!=item.ModifiedTicks)continue;
                if(item.Key!=item.Record.Packet+":"+item.FileId)throw new IOException("文件任务编号无效。");
                FileTransferState previous=(item.Record.Transfers??new FileTransferState[0]).FirstOrDefault(transfer=>transfer.Id==item.FileId);
                string transferState=item.TransferState??(previous==null?null:previous.Status);
                int percent=item.TransferState==null?(previous==null?0:previous.Percent):item.Percent;
                offers[item.Key]=new Offer{Path=file.FullName,Size=item.Size,Modified=file.LastWriteTimeUtc,Address=item.Address,Expires=new DateTime(item.ExpiresTicks,DateTimeKind.Utc),DeviceId=item.DeviceId,Fingerprint=item.Fingerprint,Sha256=item.Sha256,Record=item.Record,FileId=item.FileId,TransferState=transferState,Percent=Math.Max(0,Math.Min(100,percent))};
                sequence=Math.Max(sequence,item.Record.Packet);
            }
        }
        public event Action PeersChanged;
        public event Action<IncomingMessage> MessageReceived;
        public event Action<Peer, long> OfferCanceled;
        public event Action<ChatRecord> DeliveryChanged;
        public event Action<string> Error;
        public event Action<string> TransferNotice;
        public event Action<ChatRecord, int, int, string, double> TransferChanged;

        public LanService(AppSettings settings, int port, bool loopbackOnly, string login)
        {
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port", "端口必须在 1024–65535 之间。");
            Settings = settings; Port = port; LoopbackOnly = loopbackOnly;
            Login = Protocol.Field(login ?? Environment.UserName); Host = Protocol.Field(Environment.MachineName);
            localAddresses = loopbackOnly?new HashSet<IPAddress>{IPAddress.Loopback}:CurrentAddresses();
            foreach(SavedConversation conversation in SavedConversation.Normalize(settings.Conversations))
            {
                Peer peer=conversation.OfflinePeer();
                if(!loopbackOnly||IPAddress.IsLoopback(peer.Endpoint.Address))peers[peer.Id]=peer;
            }
            byte[] seed = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(seed);
            sequence = BitConverter.ToUInt32(seed, 0) & 0x3fffffff;
        }
        public static List<IPAddress> LocalIPv4()
        {
            return LanNetwork.Scan().Select(l=>l.Address).Distinct().ToList();
        }
        private static HashSet<IPAddress> CurrentAddresses() { HashSet<IPAddress> result = new HashSet<IPAddress>(LocalIPv4()); result.Add(IPAddress.Loopback); return result; }
        public List<Peer> Peers { get { lock (gate) return peers.Values.OrderByDescending(p => p.Online).ThenBy(p => p.Nickname).ToList(); } }
        public ChatRecord OutgoingRecord(string peer, long packet) { lock(gate) { Offer offer=offers.Values.FirstOrDefault(o=>!o.Canceled&&o.Record.PeerId==peer&&o.Record.Packet==packet&&o.Expires>DateTime.UtcNow);return offer==null?null:offer.Record; } }

        private static bool IsOfferCompleted(Offer offer) { return offer!=null&&(offer.TransferState=="已发送（非保存确认）"||offer.TransferState=="对方已校验保存"); }
        private static bool IsUnconfirmedState(string state) { string s=state??"";return s=="等待确认"||s.Contains("未确认")||s.Contains("失败"); }
        private void NotifyDelivery(ChatRecord record) { Action<ChatRecord> handler=DeliveryChanged;if(!disposed&&handler!=null)handler(record); }
        private bool TryBeginOfferStream(string key,Func<Offer,bool> authorize,TcpClient client,out Offer offer,out long version)
        {
            lock(gate) {
                if(!disposed&&offers.TryGetValue(key,out offer)&&!offer.Canceled&&offer.Expires>=DateTime.UtcNow&&authorize(offer)) {
                    version=++offer.ProgressVersion;offer.ActiveClients.Add(client);return true;
                }
                offer=null;version=0;return false;
            }
        }
        private void EndOfferStream(Offer offer,TcpClient client) { if(offer!=null&&client!=null)lock(gate)offer.ActiveClients.Remove(client); }

        public bool HasActiveOutgoingTransfer(string peerId,long packet) { lock(gate)return offers.Any(item=>item.Value.Record.PeerId==peerId&&item.Value.Record.Packet==packet&&!item.Value.Canceled&&item.Value.ActiveClients.Count>0); }
        public bool HasActiveOutgoingTransfer(string peerId) { lock(gate)return offers.Any(item=>item.Value.Record.PeerId==peerId&&!item.Value.Canceled&&item.Value.ActiveClients.Count>0); }
        public bool CanCancelOutgoing(string peerId,long packet)
        {
            lock(gate) return pending.Any(p=>p.Key==packet&&!p.Value.Canceled&&!p.Value.Delivered&&p.Value.Record.PeerId==peerId)||offers.Any(o=>o.Value.Record.PeerId==peerId&&o.Value.Record.Packet==packet&&!o.Value.Canceled&&o.Value.Expires>=DateTime.UtcNow&&!IsOfferCompleted(o.Value));
        }
        public bool HasUndeliveredOutgoing(string peerId,long packet,string recordState)
        {
            lock(gate) return pending.Any(p=>p.Key==packet&&!p.Value.Canceled&&!p.Value.Delivered&&p.Value.Record.PeerId==peerId)||(IsUnconfirmedState(recordState)&&offers.Any(o=>o.Value.Record.PeerId==peerId&&o.Value.Record.Packet==packet&&!o.Value.Canceled&&!IsOfferCompleted(o.Value)));
        }
        public void Start()
        {
            if (udp != null) throw new InvalidOperationException("通信服务已启动。");
            IPAddress bind = LoopbackOnly ? IPAddress.Loopback : IPAddress.Any;
            try
            {
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.ExclusiveAddressUse = true; udp.EnableBroadcast = !LoopbackOnly;
                udp.Client.Bind(new IPEndPoint(bind, Port));
                // Windows 的 UDP ICMP 提示不应让后续接收循环永久退出。
                try { udp.Client.IOControl((IOControlCode)(-1744830452), new byte[4], null); } catch (SocketException) { }
                tcp = new TcpListener(bind, Port); tcp.ExclusiveAddressUse = true; tcp.Start(8);
                Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
                Task.Factory.StartNew(AcceptLoop, TaskCreationOptions.LongRunning);
                // 回环测试不受宿主网络变化影响；真实网络切换由维护线程合并后复核。
                if (!LoopbackOnly) { NetworkChange.NetworkAddressChanged += OnNetworkChanged; NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged; }
                lastMaintenance=clock.Elapsed;
                timer = new Timer(Tick, null, 1000, 1000);
                Refresh();
                string receiveRoot = Settings.ReceiveFolder;
                Task.Run(() => { try { ReceiveStorage.CleanExpiredPartials(receiveRoot, DateTime.UtcNow); } catch (Exception e) { Report("过期断点清理未完成：" + e.Message); } });
            }
            catch { Dispose(); throw; }
        }
        private long Next() { return Interlocked.Increment(ref sequence); }
        // TCP 文件请求以第一个 NUL 结束；尾随 UDP 身份扩展会让旧服务因未读字节关闭时发 RST。
        private byte[] Encode(uint command, string body, string extra) {
            uint mode = command & 255;
            return Protocol.Encode(Next(), Login, Host, command | Protocol.Utf8, body, extra, mode == Protocol.GetFile ? null : Settings.DeviceId,
                mode == Protocol.Entry || mode == Protocol.AnswerEntry || mode == Protocol.Absence ? Settings.Avatar ?? "default" : null,
                mode == Protocol.GetFile || Security==null ? null : Security.Fingerprint);
        }
        private void Send(byte[] bytes, IPEndPoint endpoint)
        {
            if (disposed) throw new ObjectDisposedException("LanService");
            if (LoopbackOnly && !IPAddress.IsLoopback(endpoint.Address)) throw new InvalidOperationException("回环测试模式不能连接局域网。");
            lock(sendGate) {
                if(!LoopbackOnly) {
                    LanNetwork.Link route=LanNetwork.Route(links,endpoint.Address);
                    if(route==null)throw new IOException("目标不在当前物理局域网内，请重新发现设备。");
                    // 只约束本应用的发送接口，VPN 开启时仍优先使用物理局域网。
                    udp.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(route.Index));
                }
                udp.Send(bytes, bytes.Length, endpoint);
            }
        }
        private void Report(string message) { Action<string> handler = Error; if (!disposed && handler != null) handler(message); }
        private void Changed() { Action handler = PeersChanged; if (!disposed && handler != null) handler(); }
        private byte[] WithPort(byte[] bytes)
        {
            byte[] suffix = System.Text.Encoding.ASCII.GetBytes("FQPORT=" + Port + "\0");
            byte[] wire = new byte[bytes.Length + suffix.Length];
            Buffer.BlockCopy(bytes, 0, wire, 0, bytes.Length);
            Buffer.BlockCopy(suffix, 0, wire, bytes.Length, suffix.Length);
            return wire;
        }
        public void Probe(IPEndPoint endpoint) { Probe(endpoint, false); }
        private void Probe(IPEndPoint endpoint, bool broadcast)
        {
            if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("目前仅支持 IPv4。");
            bool allowed = LoopbackOnly ? IPAddress.IsLoopback(endpoint.Address) : LanNetwork.Route(links, endpoint.Address) != null;
            bool schedule = !broadcast && tcp != null && !disposed && HeartbeatRetry >= TimeSpan.FromSeconds(1) && allowed;
            try
            {
                Send(Encode(Protocol.Entry | Protocol.CapUtf8 | Protocol.FileAttach, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group)), endpoint);
            }
            finally
            {
                if (schedule)
                {
                    lock (gate) { if (pendingTcpTargets.Count < 256) pendingTcpTargets.Add(endpoint); }
                    ScheduleTcpFallback();
                }
            }
        }
        public void Refresh()
        {
            // 切换网络后本机地址会变，先更新自身过滤再广播，避免把自己的新地址当成联系人。
            lastRefresh = DateTime.UtcNow;
            if (!LoopbackOnly)
            {
                List<LanNetwork.Link> next=NetworkLinks();string key=LanNetwork.Key(next);
                bool reset=networkKey!=null&&networkKey!=key;lock(sendGate){links=next;networkKey=key;}
                localAddresses=new HashSet<IPAddress>(next.Select(l=>l.Address));localAddresses.Add(IPAddress.Loopback);
                if(reset)ResetNetwork();
                foreach (IPAddress address in next.Select(l=>l.Broadcast).Distinct())
                    try { Probe(new IPEndPoint(address, Port), true); } catch (SocketException e) { Report("广播失败：" + e.Message); }
            }
            // 单播补充广播；只有收到真实报文才置为在线，不把历史地址伪装成在线联系人。
            List<Peer> known = Peers;
            IEnumerable<IPEndPoint> targets = known.Select(p => p.Endpoint).Concat(AppSettings.NormalizeKnownPeers(Settings.KnownPeers).Select(AppSettings.ParseKnownPeer)).Distinct();
            foreach (IPEndPoint target in targets)
            {
                if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) continue;
                if(!LoopbackOnly&&LanNetwork.Route(links,target.Address)==null)continue;
                try { Probe(target); } catch (SocketException e) { Report("刷新联系人失败：" + e.Message); }
            }
        }
        private void ResetNetwork()
        {
            if(Security!=null)Security.CancelConfirmations();
            List<Pending> interrupted;
            lock(gate) {
                foreach(Peer peer in peers.Values){peer.Online=false;peer.Misses=0;}
                interrupted=pending.Values.ToList();pending.Clear();
                foreach(string key in offers.Where(o=>o.Value.Fingerprint==null).Select(o=>o.Key).ToList()){EndOffer(offers[key],"网络已切换，请重新发送邀请");offers.Remove(key);}
                foreach(TcpClient client in connections)client.Close();
                foreach(TcpClient client in probeSockets)client.Close();
                foreach(PendingDownload pd in pendingDownloads){pd.Ready.Set();pd.Done.Set();}pendingDownloads.Clear();
                CancelReverseRequests();
                pendingTcpTargets.Clear();
            }
            foreach(Pending item in interrupted)Complete(item.Record,"网络已切换，未确认送达");
            Action reset=NetworkReset;if(reset!=null)reset();Changed();
        }
        // 网络切换或用户刷新时立即复核：仍能回应的联系人保持在线，失联者约 6 秒内离线。
        public void Revalidate()
        {
            lock (gate)
            {
                verifyFrom = clock.Elapsed;
                foreach (Peer peer in peers.Values)
                    if (peer.Misses == 0) peer.Probed = verifyFrom - HeartbeatRetry;
            }
            Refresh();
        }
        // 系统地址通知常成批到达，只记标记，由每秒维护合并处理。
        private void OnNetworkChanged(object sender, EventArgs e) { Interlocked.Exchange(ref networkDirty, 1); }
        private Peer Upsert(Packet packet, IPEndPoint endpoint)
        {
            string legacyId = endpoint + "/" + packet.Login; Peer peer;
            bool entry = packet.Mode == Protocol.Entry || packet.Mode == Protocol.AnswerEntry || packet.Mode == Protocol.Absence;
            lock (gate)
            {
                if (disposed) return null;
                peer = packet.DeviceId == null ? null : peers.Values.FirstOrDefault(p => p.DeviceId == packet.DeviceId);
                if(Security!=null&&packet.DeviceId!=null) {
                    string pin=Security.TrustedFingerprint(packet.DeviceId);
                    if(pin!=null&&packet.Fingerprint!=pin)return null;
                }
                // 仅升级端点和登录名均匹配、且尚无稳定身份的旧会话，不按昵称猜测设备。
                if (peer == null) peer = peers.Values.FirstOrDefault(p => p.Endpoint.Equals(endpoint) && p.Login == packet.Login && (packet.DeviceId == null || p.DeviceId == null || p.DeviceId == packet.DeviceId));
                // 旧格式可按地址匹配联系人，但省略身份不能绕过该会话已经保存的证书。
                if (Security != null && peer != null && Security.TrustedFingerprint(peer.DeviceId) != null &&
                    (packet.DeviceId != peer.DeviceId || packet.Fingerprint != Security.TrustedFingerprint(peer.DeviceId))) return null;
                if (peer == null)
                {
                    if (peers.Count >= 2000) return null;
                    peer = new Peer { ConversationId = packet.DeviceId == null ? legacyId : "device:" + packet.DeviceId, DeviceId = packet.DeviceId, Endpoint = endpoint, Login = packet.Login, Host = packet.Host, Nickname = packet.Login, Group = "我的局域网" };
                    peers[peer.Id] = peer;
                }
                peer.ConversationId = peer.Id; peer.DeviceId = packet.DeviceId ?? peer.DeviceId;
                if(packet.Fingerprint!=null)peer.Fingerprint=packet.Fingerprint;
                peer.Endpoint = endpoint; peer.Login = packet.Login; peer.Host = packet.Host;
                if (entry)
                {
                    if(packet.Avatar != null) peer.Avatar = packet.Avatar;
                    peer.Nickname = String.IsNullOrWhiteSpace(packet.Body) ? packet.Login : Protocol.Prefix(packet.Body, 128);
                    if (!String.IsNullOrWhiteSpace(packet.Extra) || String.IsNullOrEmpty(peer.Group))
                        peer.Group = String.IsNullOrWhiteSpace(packet.Extra) ? "我的局域网" : Protocol.Prefix(packet.Extra, 128);
                }
                peer.Utf8 = peer.Utf8 || (packet.Command & (Protocol.Utf8 | Protocol.CapUtf8)) != 0;
                peer.Online = packet.Mode != Protocol.Exit; peer.LastSeen = DateTime.UtcNow; peer.Seen = clock.Elapsed; peer.Misses = 0;
            }
            Changed(); return peer;
        }
        private bool IsSelf(Packet packet, IPEndPoint remote)
        {
            return (Settings.DeviceId != null && packet.DeviceId == Settings.DeviceId) || ((packet.ReplyPort > 0 ? packet.ReplyPort : remote.Port) == Port && localAddresses.Contains(remote.Address) && packet.Login == Login && packet.Host == Host);
        }
        private void ReceiveLoop()
        {
            while (!disposed)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = udp.Receive(ref remote); Packet packet;
                    if(!LoopbackOnly&&LanNetwork.Route(links,remote.Address)==null)continue;
                    if (!Protocol.TryParse(bytes, out packet)) continue;
                    if (IsSelf(packet, remote)) continue;
                    Handle(packet, packet.ReplyPort>0?new IPEndPoint(remote.Address,packet.ReplyPort):remote);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e) { if (!disposed) { Report("网络接收错误：" + e.Message); Thread.Sleep(100); } }
                catch (Exception e) { if (!disposed) Report("消息处理错误：" + e.Message); }
            }
        }
        private void Handle(Packet packet, IPEndPoint remote, bool secure=false)
        {
            if (packet.Mode == Protocol.ReceiveMessage)
            {
                long number; Pending item = null;
                if (!Int64.TryParse(packet.Body, out number)) return;
                lock (gate)
                {
                    Pending candidate;
                    if (pending.TryGetValue(number, out candidate) && !candidate.Canceled && (secure || candidate.Fingerprint==null) && candidate.Endpoint.Equals(remote) && candidate.Login == packet.Login && (packet.DeviceId == null || candidate.DeviceId == null || candidate.DeviceId == packet.DeviceId))
                    { item = candidate; candidate.Delivered=true;candidate.Record.State="已送达"; pending.Remove(number); }
                }
                if (item != null) { Complete(item.Record, "已送达"); if(item.Record.HasAttachments)try{SaveOffers();}catch(IOException){} }
                return;
            }
            if (packet.Mode == Protocol.ReleaseFiles)
            {
                long messageId;
                if ((packet.Body ?? "").StartsWith("cancel:", StringComparison.Ordinal))
                {
                    if (!Int64.TryParse(packet.Body.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out messageId) || messageId <= 0) return;
                    Peer sender;
                    lock (gate)
                    {
                        if (secure)
                        {
                            if (Security == null || !Security.IsTrusted(packet.DeviceId, packet.Fingerprint)) return;
                            sender = peers.Values.FirstOrDefault(p => p.DeviceId == packet.DeviceId && p.Fingerprint == packet.Fingerprint);
                        }
                        else
                        {
                            if (packet.Fingerprint != null || Security != null && (!Settings.AllowLegacyProtocol || Security.TrustedFingerprint(packet.DeviceId) != null)) return;
                            sender = peers.Values.FirstOrDefault(p => p.Endpoint.Equals(remote) && p.Login == packet.Login && p.Host == packet.Host &&
                                (packet.DeviceId == null || p.DeviceId == packet.DeviceId) && p.Fingerprint == null && (Security == null || Security.TrustedFingerprint(p.DeviceId) == null));
                        }
                    }
                    Action<Peer, long> canceled = OfferCanceled;
                    if (sender != null && canceled != null) canceled(sender, messageId);
                    return;
                }
                if (!Int64.TryParse(packet.Body, out messageId)) return;
                lock (gate)
                    foreach (string key in offers.Where(p => p.Key.StartsWith(messageId + ":", StringComparison.Ordinal) && (secure?p.Value.DeviceId==packet.DeviceId&&p.Value.Fingerprint==packet.Fingerprint:p.Value.Fingerprint==null&&p.Value.Address==remote.Address.ToString())).Select(p => p.Key).ToList()) { EndOffer(offers[key], "对方已拒绝"); offers.Remove(key); }
                SaveOffers();
                return;
            }
            if (packet.Mode != Protocol.Entry && packet.Mode != Protocol.AnswerEntry && packet.Mode != Protocol.Absence && packet.Mode != Protocol.Exit && packet.Mode != Protocol.SendMessage) return;
            if(!secure&&packet.Mode==Protocol.SendMessage&&Security!=null&&(!Settings.AllowLegacyProtocol||packet.Fingerprint!=null||Security.TrustedFingerprint(packet.DeviceId)!=null))return;
            Peer peer = Upsert(packet, remote); if (peer == null) return;
            if (packet.Mode == Protocol.Entry)
            {
                uint flags = Protocol.AnswerEntry | Protocol.CapUtf8 | Protocol.FileAttach | (peer.Utf8 ? Protocol.Utf8 : 0);
                try { Send(Encode(flags, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group)), remote); } catch (SocketException) { }
                if (Security != null && peer.DeviceId != null && peer.Fingerprint != null && Security.IsTrusted(peer.DeviceId, peer.Fingerprint)) ScheduleSync(peer);
            }
            if (packet.Mode != Protocol.SendMessage) return;
            if (!secure && (packet.Command & Protocol.SendCheck) != 0)
                try { Send(Encode(Protocol.ReceiveMessage, packet.Number.ToString(CultureInfo.InvariantCulture), null), remote); } catch (SocketException) { }
            lock (gate)
            {
                string key = peer.Id + ":" + packet.Number;
                if (seen.ContainsKey(key)) return;
                if (seen.Count >= 8192) seen.Remove(seen.OrderBy(p => p.Value).First().Key);
                seen[key] = DateTime.UtcNow;
            }
            IncomingMessage message = new IncomingMessage { Peer = peer, Record = new ChatRecord { Packet = packet.Number, PeerId = peer.Id, Sender = peer.Nickname, Text = packet.Body, Time = DateTime.Now, Outgoing = false, State = "已收到" } };
            if ((packet.Command & Protocol.FileAttach) != 0) message.Files = Protocol.ParseFiles(packet.Extra);
            Action<IncomingMessage> handler = MessageReceived; if (handler != null) handler(message);
        }
        private void Complete(ChatRecord record, string state)
        {
            lock (gate)
            {
                // 取消是本机的终态，晚到的切网或发送失败回调不能重新开启重发入口。
                if (record.Outgoing && record.State == "已取消发送" && state != "已取消发送") return;
                record.State = state;
            }
            NotifyDelivery(record);
        }
        private bool CancelOutgoingCore(string peerId,long packet,string knownState,bool notify,bool onlyUndelivered)
        {
            Pending item=null;List<KeyValuePair<string,Offer>> selected;List<TcpClient> socketsToClose=new List<TcpClient>();List<Tuple<Offer,long,int>> notifiedTransfers=new List<Tuple<Offer,long,int>>();TcpClient connectingToClose=null;ChatRecord stateRecord=null;bool wasPending=false,updateRecordState=false;
            Peer cancelPeer = null;
            lock(gate) {
                Pending candidate;
                if(pending.TryGetValue(packet,out candidate)&&candidate.Record.PeerId==peerId&&!candidate.Canceled&&!candidate.Delivered)item=candidate;
                selected=offers.Where(o=>o.Value.Record.PeerId==peerId&&o.Value.Record.Packet==packet&&!o.Value.Canceled&&!IsOfferCompleted(o.Value)).ToList();
                bool active=selected.Any(o=>o.Value.ActiveClients.Count>0);
                string offerState=selected.Select(o=>o.Value.Record.State).FirstOrDefault();
                string effectiveState=item!=null?item.Record.State:offerState=="已送达"?offerState:(!String.IsNullOrEmpty(knownState)?knownState:offerState);
                if(onlyUndelivered&&knownState=="已送达")return false;
                if(onlyUndelivered&&item==null&&(!IsUnconfirmedState(effectiveState)||active))return false;
                if(item==null&&selected.Count==0)return false;
                if (notify && !onlyUndelivered && selected.Count > 0 && (active || effectiveState == "已送达" || selected.Any(o => o.Value.Percent > 0) || item != null && item.Transmitting != 0))
                {
                    Peer target = item != null ? item.Peer : peers.Values.FirstOrDefault(p => p.Id == peerId);
                    if (target != null) cancelPeer = new Peer { Endpoint = new IPEndPoint(target.Endpoint.Address, target.Endpoint.Port), DeviceId = target.DeviceId, Fingerprint = target.Fingerprint, Login = target.Login, Nickname = target.DisplayName };
                }
                Dictionary<string,Offer> nextOffers=new Dictionary<string,Offer>(offers);
                foreach(KeyValuePair<string,Offer> entry in selected)if(entry.Value.Fingerprint!=null)nextOffers.Remove(entry.Key);
                if(selected.Any(o=>o.Value.Fingerprint!=null))SaveOffers(nextOffers);
                if(item!=null) {
                    wasPending=true;item.Canceled=true;pending.Remove(packet);connectingToClose=item.ConnectingClient;item.ConnectingClient=null;stateRecord=item.Record;
                }
                foreach(KeyValuePair<string,Offer> entry in selected) {
                    Offer offer=entry.Value;offer.Canceled=true;offers.Remove(entry.Key);socketsToClose.AddRange(offer.ActiveClients);offer.ActiveClients.Clear();
                    long version=++offer.ProgressVersion;offer.TransferState="已取消发送";
                    if(notify)notifiedTransfers.Add(Tuple.Create(offer,version,offer.Percent));
                    if(stateRecord==null)stateRecord=offer.Record;
                }
                updateRecordState=wasPending||(IsUnconfirmedState(effectiveState)&&effectiveState!="已送达");
                if(updateRecordState&&stateRecord!=null)stateRecord.State="已取消发送";
            }
            if(connectingToClose!=null)try{connectingToClose.Close();}catch{}
            foreach(TcpClient client in socketsToClose)try{client.Close();}catch{}
            foreach(Tuple<Offer,long,int> entry in notifiedTransfers)Transfer(entry.Item1,entry.Item2,entry.Item3,"已取消发送");
            if(notify&&updateRecordState&&stateRecord!=null)NotifyDelivery(stateRecord);
            if (cancelPeer != null)
            {
                Peer target = cancelPeer;
                Task.Run(() => { try { SendReleaseFiles(target, "cancel:" + packet.ToString(CultureInfo.InvariantCulture)); } catch (Exception) { /* 拉取的 REVOKED 终态兜底通知丢失。 */ } });
            }
            return true;
        }
        public bool CancelOutgoing(string peerId,long packet,string knownState=null,bool notify=true) { return CancelOutgoingCore(peerId,packet,knownState,notify,false); }
        public bool CancelUndeliveredOutgoing(string peerId,long packet,string knownState,bool notify=false) { return CancelOutgoingCore(peerId,packet,knownState,notify,true); }
        public void CancelUndeliveredOutgoingForPeer(string peerId)
        {
            List<KeyValuePair<long,string>> messages;
            lock(gate) messages=pending.Values.Where(p=>p.Record.PeerId==peerId&&!p.Canceled&&!p.Delivered).Select(p=>new KeyValuePair<long,string>(p.Record.Packet,p.Record.State)).Concat(offers.Values.Where(o=>o.Record.PeerId==peerId&&!o.Canceled&&IsUnconfirmedState(o.Record.State)).Select(o=>new KeyValuePair<long,string>(o.Record.Packet,o.Record.State))).GroupBy(p=>p.Key).Select(g=>g.First()).ToList();
            foreach(KeyValuePair<long,string> message in messages)CancelUndeliveredOutgoing(peerId,message.Key,message.Value,false);
        }
        public ChatRecord SendContent(Peer peer, string text, IList<string> paths, string dataRoot)
        {
            return SendContent(peer, text, paths, dataRoot, null);
        }
        private ChatRecord SendContent(Peer peer, string text, IList<string> paths, string dataRoot, Dictionary<string,string> relatives)
        {
            if (!MessageText.AsFile(text)) return SendMessage(peer, text, paths, null, relatives);
            if (text.Length > MessageText.InputLimit || text.IndexOf('\0') >= 0) throw new ArgumentException("正文超过 1,048,576 个字符或含有空字符，请直接选择文件发送。");
            if (paths != null && paths.Count >= 20) throw new ArgumentException("超长正文需要一个 TXT 附件位置，请最多选择 19 个其他文件。");
            string folder = Path.Combine(dataRoot, "text-offers"); Directory.CreateDirectory(folder);
            string generated = Path.Combine(folder, "消息-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            // 只清理本功能生成、超过一天且没有有效邀请引用的副本，不触及用户附件。
            lock (gate)
            {
                foreach (string old in Directory.GetFiles(folder, "消息-*.txt"))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1) && !offers.Values.Any(o => o.Path == old && o.Expires > DateTime.UtcNow)) File.Delete(old);
                byte[] bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text);
                if (Directory.GetFiles(folder).Sum(p => new FileInfo(p).Length) + bytes.Length > 64L * 1024 * 1024) throw new IOException("长文本缓存已达 64 MiB，请稍后重试或直接选择 TXT 文件发送。");
                try
                {
                    using (FileStream output = new FileStream(generated, FileMode.CreateNew, FileAccess.Write)) output.Write(bytes, 0, bytes.Length);
                    List<string> files = paths == null ? new List<string>() : new List<string>(paths); files.Add(generated);
                    if (relatives != null) { relatives = new Dictionary<string,string>(relatives, StringComparer.OrdinalIgnoreCase); relatives[generated] = null; }
                    return SendMessage(peer, "长文本已转为 TXT，打开附件查看完整内容。", files, null, relatives);
                }
                catch { if (File.Exists(generated)) File.Delete(generated); throw; }
            }
        }
        public ChatRecord SendMessage(Peer peer, string text, IList<string> paths)
        {
            return SendMessage(peer, text, paths, null);
        }
        public List<ChatRecord> SendBatchContent(Peer peer,string text,IEnumerable<string> paths,string root,Action<ChatRecord,int,int> sent)
        {
            List<SendBatch.Item> expanded=SendBatch.Expand(paths);if(expanded.Any(f=>f.Relative!=null)&&peer.Fingerprint==null)throw new IOException("文件夹发送需要新版加密协议。");
            bool longText=MessageText.AsFile(text);
            if(longText&&expanded.Count>=1000)throw new IOException("超长正文需要一个 TXT 附件位置，请最多选择 999 个文件。");
            if(Security!=null&&peer.Fingerprint!=null)VerifyPeer(peer);
            List<ChatRecord> records=new List<ChatRecord>();
            for(int offset=0;offset<expanded.Count;){
                var batch=expanded.Skip(offset).Take(offset==0&&longText?19:20).ToList();
                ChatRecord record=SendContent(peer,offset==0?text:"",batch.Select(f=>f.Path).ToArray(),root,batch.ToDictionary(f=>f.Path,f=>f.Relative,StringComparer.OrdinalIgnoreCase));
                records.Add(record);offset+=batch.Count;sent(record,offset,expanded.Count);
            }
            return records;
        }
        public ChatRecord RetryMessage(Peer peer, ChatRecord original)
        {
            lock (gate)
            {
                if (!original.Outgoing || original.PeerId != peer.Id || !(original.State ?? "").Contains("未确认") || pending.ContainsKey(original.Packet))
                    throw new InvalidOperationException("此消息正在发送或已送达。");
            }
            Dictionary<string,string> relatives=(original.Transfers??new FileTransferState[0]).Where(f=>f.LocalPath!=null).ToDictionary(f=>f.LocalPath,f=>f.RelativePath,StringComparer.OrdinalIgnoreCase);
            return SendMessage(peer, original.ForwardText(), original.GetLocalFiles(), original,relatives.Count==0?null:relatives);
        }
        private ChatRecord SendMessage(Peer peer, string text, IList<string> paths, ChatRecord original,Dictionary<string,string> relatives=null)
        {
            if (String.IsNullOrWhiteSpace(text) && (paths == null || paths.Count == 0)) throw new ArgumentException("请先输入消息或选择文件。");
            if ((text ?? "").IndexOf('\0') >= 0) throw new ArgumentException("消息不能包含空字符。");
            if (paths != null && paths.Count > 20) throw new ArgumentException("一次最多发送 20 个文件。");
            Pending delivery; bool utf8;
            // 发现报文可以更新联系人地址，但不能改变已发消息的接收者、ACK 或附件授权。
            lock (gate) { delivery = new Pending(peer); utf8 = peer.Utf8; }
            if(Security!=null) {
                if(delivery.Fingerprint==null&&(!Settings.AllowLegacyProtocol||Security.TrustedFingerprint(delivery.DeviceId)!=null))throw new IOException("对方使用旧版明文协议，请升级对方飞Q，或在设置中明确开启兼容模式。");
            }
            long number = original == null ? Next() : original.Packet; List<Attachment> files = new List<Attachment>(); Dictionary<string, Offer> additions = new Dictionary<string, Offer>();
            if (paths != null)
            {
                int id = 0;
                foreach (string path in paths)
                {
                    FileInfo info = new FileInfo(Path.GetFullPath(path));
                    if (!info.Exists || !Protocol.SafeFileName(info.Name)) throw new IOException("文件不存在、文件名不安全或过长：" + info.Name);
                    Attachment file = new Attachment { Id = id++, Name = info.Name, Size = info.Length, Modified = (long)(info.LastWriteTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds, Sha256=delivery.Fingerprint==null?null:SecureIdentity.HashFile(info.FullName) };
                    files.Add(file);
                    if(relatives!=null)file.RelativePath=relatives[info.FullName];
                    additions[number + ":" + file.Id] = new Offer { Path = info.FullName, Size = info.Length, Modified = info.LastWriteTimeUtc, Address = delivery.Endpoint.Address.ToString(), Expires = DateTime.UtcNow.AddMinutes(30), DeviceId=delivery.DeviceId,Fingerprint=delivery.Fingerprint,Sha256=file.Sha256 };
                    if(file.Sha256!=null)additions[number+":"+file.Id].Expires=DateTime.UtcNow.AddDays(7);
                }
            }
            uint command = Protocol.SendMessage | Protocol.SendCheck | (utf8 ? Protocol.Utf8 : 0) | (files.Count > 0 ? Protocol.FileAttach : 0);
            byte[] bytes = Protocol.Encode(number, Login, Host, command, text, files.Count > 0 ? Protocol.FileList(files) : null, Settings.DeviceId,null,Security==null?null:Security.Fingerprint);
            string display = text ?? "";
            if (files.Count > 0) display += (display.Length > 0 ? "\n" : "") + "[文件] " + String.Join("、", files.Select(f => f.Name));
            ChatRecord record = new ChatRecord { Packet = number, PeerId = peer.Id, Sender = Settings.Nickname, Text = display, Time = DateTime.Now, Outgoing = true, State = "等待确认", LocalFiles = paths == null ? null : paths.Select(Path.GetFullPath).ToArray() };
            record.HasAttachments = files.Count > 0; record.AttachmentNames = files.Select(f => f.Name).ToArray();
            record.Transfers = files.Select(f => new FileTransferState { Id = f.Id, Name = f.Name, Size = f.Size, Modified=f.Modified,Sha256=f.Sha256,RelativePath=f.RelativePath,Status = "等待对方接收", LocalPath = record.LocalFiles[f.Id] }).ToArray();
            if (original != null)
            {
                if (original.Transfers != null && original.Transfers.Any(old => !files.Any(f => f.Id == old.Id && f.Name == old.Name && f.Size == old.Size)))
                    throw new IOException("原附件已改变，请作为新附件发送。");
                original.Transfers = record.Transfers; record = original;
            }
            foreach (Attachment file in files) { Offer offer = additions[number + ":" + file.Id]; offer.Record = record; offer.FileId = file.Id; }
            lock (gate)
            {
                if (pending.Count >= 256 || offers.Count + additions.Count > 1000) throw new InvalidOperationException("待处理消息或文件过多，请稍后再试。");
                // 写盘成功后才加入重试和文件授权；否则调用方收到失败后仍可能被后台补发。
                var nextOffers = new Dictionary<string,Offer>(offers);
                foreach (var item in additions) nextOffers[item.Key] = item.Value;
                SaveOffers(nextOffers);
                record.State = "等待确认";
                foreach (var item in additions) offers[item.Key] = item.Value;
                delivery.Bytes = bytes; delivery.Record = record; delivery.Last = DateTime.UtcNow; delivery.Attempts = 1;
                pending[number] = delivery;
            }
            try { if(delivery.Fingerprint!=null&&Security!=null)Task.Run(()=>SendSecurePending(delivery));else Send(bytes, delivery.Endpoint); }
            catch
            {
                lock (gate)
                {
                    pending.Remove(number);
                    foreach (string key in additions.Keys) offers.Remove(key);
                    // 首发失败后不能留下重启仍可读取的孤立文件授权。
                    SaveOffers();
                }
                if (original != null) Complete(original, "发送失败，未确认送达");
                throw;
            }
            return record;
        }
        private void Tick(object ignored)
        {
            if (disposed) return;
            try
            {
                TimeSpan maintenance=clock.Elapsed;if(!LoopbackOnly&&maintenance-lastMaintenance>TimeSpan.FromSeconds(30)){ResetNetwork();Interlocked.Exchange(ref networkDirty,1);}lastMaintenance=maintenance;
                List<Pending> retry = new List<Pending>(), failed = new List<Pending>(); List<IPEndPoint> probes = new List<IPEndPoint>(); List<Peer> syncTargets = new List<Peer>(); bool changed = false;
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow; TimeSpan elapsed = clock.Elapsed;
                    foreach (Pending item in pending.Values.ToList())
                    {
                        if (item.Canceled || item.Sending!=0 || item.Transmitting!=0 || item.Delivered || (now - item.Last).TotalSeconds < 2) continue;
                        if (item.Attempts >= 4) { pending.Remove(item.Record.Packet); item.Record.State="未确认送达，请检查网络后重发"; failed.Add(item); }
                        else { item.Attempts++; item.Last = now; retry.Add(item); }
                    }
                    foreach (string key in seen.Where(p => (now - p.Value).TotalMinutes > 10).Select(p => p.Key).ToList()) seen.Remove(key);
                    foreach (string key in offers.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList()) { EndOffer(offers[key], "邀请已过期，请重新发送"); offers.Remove(key); }
                    // 切网不会发送退出通知；只按实际发出且未获回应的探测判离线，计时器延后不会误判。
                    foreach (Peer peer in peers.Values)
                    {
                        if (!peer.Online)
                        {
                            if (elapsed - peer.Probed >= HeartbeatRetry) { peer.Probed = elapsed; probes.Add(peer.Endpoint); }
                            continue;
                        }
                        if (Security != null && HeartbeatRetry >= TimeSpan.FromSeconds(1) && peer.DeviceId != null && peer.Fingerprint != null && Security.IsTrusted(peer.DeviceId, peer.Fingerprint)) syncTargets.Add(peer);
                        bool verify = peer.Seen < verifyFrom;
                        if ((!verify && elapsed - peer.Seen < HeartbeatIdle) || elapsed - peer.Probed < HeartbeatRetry) continue;
                        if (peer.Misses >= (verify ? VerifyMisses : IdleMisses)) { peer.Online = false; peer.Misses = 0; changed = true; }
                        else { peer.Probed = elapsed; peer.Misses++; probes.Add(peer.Endpoint); }
                    }
                }
                foreach (Pending item in retry) if(!item.Canceled&&!item.Delivered)try { if(item.Fingerprint!=null&&Security!=null)Task.Run(()=>SendSecurePending(item));else Send(item.Bytes, item.Endpoint); } catch (SocketException) { }
                foreach (IPEndPoint target in probes) try { Probe(target); } catch (SocketException) { } catch(IOException) { }
                foreach (Peer target in syncTargets) ScheduleSync(target);
                foreach (Pending item in failed) NotifyDelivery(item.Record);
                if (changed) Changed();
                if (Interlocked.Exchange(ref networkDirty, 0) != 0) Revalidate();
                else if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 30) Refresh();
            }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Report("网络维护失败：" + e.Message); }
        }
        private void ScheduleTcpFallback()
        {
            if (disposed || tcp == null || Interlocked.CompareExchange(ref tcpFallbackScheduled, 1, 0) != 0) return;
            Task.Delay(150).ContinueWith(_ => RunTcpFallback(), TaskScheduler.Default);
        }
        private void RunTcpFallback()
        {
            Interlocked.Exchange(ref tcpFallbackScheduled, 0);
            if (disposed || tcp == null) return;
            List<IPEndPoint> batch = new List<IPEndPoint>();
            lock (gate)
            {
                if (pendingTcpTargets.Count == 0) return;
                foreach (IPEndPoint target in pendingTcpTargets)
                    if (activeTcpProbes.Add(target)) batch.Add(target);
                pendingTcpTargets.Clear();
            }
            foreach (IPEndPoint target in batch)
            {
                Task.Run(() =>
                {
                    probeConcurrency.Wait();
                    try { ProbeTcp(target); }
                    finally { probeConcurrency.Release(); lock (gate) activeTcpProbes.Remove(target); }
                });
            }
        }
        private void ProbeTcp(IPEndPoint target)
        {
            if (disposed || tcp == null) return;
            if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) return;
            if (!LoopbackOnly && LanNetwork.Route(links, target.Address) == null) return;
            Peer known = null;
            lock (gate)
            {
                if (disposed) return;
                known = peers.Values.FirstOrDefault(p => p.Endpoint.Equals(target));
                TimeSpan elapsed = clock.Elapsed;
                if (known != null && known.Online && known.Misses == 0 && known.Seen >= verifyFrom && elapsed - known.Seen < TimeSpan.FromSeconds(1)) return;
            }
            bool connected = false, discovered = false;
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            lock (gate) { if (disposed) { client.Close(); return; } probeSockets.Add(client); }
            try
            {
                using (client)
                {
                    BindClient(client, target.Address);
                    Task connecting = client.ConnectAsync(target.Address, target.Port);
                    connecting.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (!connecting.Wait(1200)) return;
                    connected = true;
                    client.ReceiveTimeout = 1500; client.SendTimeout = 1500;
                    NetworkStream stream = client.GetStream();
                    stream.Write(new byte[] { 70, 81, 68, 10 }, 0, 4);
                    SecureIdentity.WriteFrame(stream, WithPort(Encode(Protocol.Entry | Protocol.CapUtf8 | Protocol.FileAttach, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group))));
                    Packet packet;
                    if (Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream, 1500), 60000), out packet)
                        && (packet.Mode == Protocol.AnswerEntry || packet.Mode == Protocol.Entry || packet.Mode == Protocol.Absence))
                    {
                        IPEndPoint from = new IPEndPoint(target.Address, packet.ReplyPort > 0 ? packet.ReplyPort : target.Port);
                        if (!IsSelf(packet, from)) { Peer latest = Upsert(packet, from); if (latest != null) { known = latest; discovered = true; } }
                    }
                }
            }
            catch (Exception) { }
            finally { lock (gate) probeSockets.Remove(client); }
            if (connected && known != null && Security != null && known.Fingerprint != null && Security.IsTrusted(known.DeviceId, known.Fingerprint))
            {
                if (!discovered) try { VerifyPeer(known); } catch (Exception) { }
                ScheduleSync(known);
            }
        }
        private bool HasPendingFor(string targetDeviceId, string targetFingerprint)
        {
            if (targetDeviceId == null || targetFingerprint == null) return false;
            lock (gate)
            {
                return pending.Values.Any(item => !item.Delivered && !item.Canceled && item.DeviceId == targetDeviceId && item.Fingerprint == targetFingerprint)
                    || pendingDownloads.Any(pd => pd.Claimed == 0 && pd.DeviceId == targetDeviceId && pd.Fingerprint == targetFingerprint);
            }
        }
        private void ScheduleSync(Peer peer)
        {
            if (disposed || tcp == null || Security == null || peer == null || peer.DeviceId == null || peer.Fingerprint == null) return;
            string key = peer.DeviceId;
            lock (gate) { if (!activeSyncPeers.Add(key)) return; }
            Task.Run(() =>
            {
                probeConcurrency.Wait();
                try { SyncPeer(peer); }
                finally { probeConcurrency.Release(); lock (gate) activeSyncPeers.Remove(key); }
            });
        }
        private void SyncPeer(Peer peer)
        {
            if (disposed || tcp == null || Security == null || peer.DeviceId == null || peer.Fingerprint == null) return;
            IPEndPoint target = peer.Endpoint;
            if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) return;
            if (!LoopbackOnly && LanNetwork.Route(links, target.Address) == null) return;
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            lock (gate) { if (disposed) { client.Close(); return; } probeSockets.Add(client); }
            try
            {
                using (client)
                {
                    BindClient(client, target.Address);
                    Task connecting = client.ConnectAsync(target.Address, target.Port);
                    connecting.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (!connecting.Wait(2500)) return;
                    client.ReceiveTimeout = 15000; client.SendTimeout = 15000;
                    client.GetStream().Write(new byte[] { 70, 81, 50, 10 }, 0, 4);
                    using (SslStream stream = Security.Client(client, peer.Fingerprint))
                    {
                        stream.ReadTimeout = 15000; stream.WriteTimeout = 15000;
                        Packet remote = AuthenticateSecureClient(stream, peer.DeviceId, peer.Fingerprint, peer.DisplayName, true);
                        SecureIdentity.WriteText(stream, "S\n" + Port);
                        SecureIdentity.WriteFrame(stream, SecureSyncHello());
                        Upsert(remote, target);
                        while (!disposed)
                        {
                            string cmd = SecureIdentity.ReadText(stream, 1024);
                            if (cmd == "OK") break;
                            if (cmd == "M")
                            {
                                Packet msg;
                                if (!Protocol.TryParse(SecureIdentity.ReadFrame(stream, 60000), out msg) || msg.DeviceId != peer.DeviceId || msg.Fingerprint != peer.Fingerprint || (msg.Mode != Protocol.SendMessage && msg.Mode != Protocol.ReleaseFiles))
                                    throw new IOException("同步消息无效。");
                                Handle(msg, target, true);
                                SecureIdentity.WriteText(stream, "OK");
                            }
                            else if (cmd.StartsWith("D\n", StringComparison.Ordinal))
                            {
                                string[] f = cmd.Substring(2).Split(':');
                                long number, offset; int id;
                                if (f.Length >= 3 && Int64.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) && Int32.TryParse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && Int64.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
                                    Task.Run(() => PushSecureFile(peer, number, id, offset));
                            }
                            else if(cmd.StartsWith("R\n",StringComparison.Ordinal))
                            {
                                string[] fields=cmd.Substring(2).Split(':');int port;
                                if(fields.Length!=2||Protocol.NormalizeDeviceId(fields[0])==null||!Int32.TryParse(fields[1],out port))throw new IOException("回连请求无效。");
                                string ticket=fields[0];string key=peer.DeviceId+":"+ticket;int epoch;
                                lock(gate){if(activeReverseRequests.Count>=8||!activeReverseRequests.Add(key))continue;epoch=transportEpoch;}
                                Task.Run(()=>{try{ServeReverse(target,port,ticket,peer.DeviceId,peer.Fingerprint,epoch);}finally{lock(gate)activeReverseRequests.Remove(key);}});
                            }
                            else throw new IOException("未知同步响应。");
                        }
                    }
                }
            }
            catch (Exception) { }
            finally { lock (gate) probeSockets.Remove(client); }
        }
        private void PushSecureFile(Peer peer, long number, int fileId, long offset)
        {
            Offer offer;
            lock (gate) { if (!offers.TryGetValue(number + ":" + fileId, out offer) || offer.Canceled) return; }
            if (offer.DeviceId != peer.DeviceId || offer.Fingerprint != peer.Fingerprint || offer.Expires < DateTime.UtcNow || offset < 0 || offset > offer.Size) return;
            if (Interlocked.CompareExchange(ref offer.Pushing, 1, 0) != 0) return;
            TcpClient connection = null;
            try
            {
                using (TcpClient client = connection = SecureConnect(peer.Endpoint))
                using (SslStream stream = Security.Client(client, peer.Fingerprint))
                {
                    client.ReceiveTimeout = 45000; stream.ReadTimeout = 45000;
                    Packet remote = AuthenticateSecureClient(stream, peer.DeviceId, peer.Fingerprint, peer.DisplayName);
                    Upsert(remote, peer.Endpoint);
                    SecureIdentity.WriteText(stream, "P\n" + Port);
                    SecureIdentity.WriteFrame(stream, Protocol.Encode(Next(), Login, Host, Protocol.GetFile | Protocol.Utf8, number.ToString("x") + ":" + fileId.ToString("x") + ":" + offset.ToString("x") + ":", null, Settings.DeviceId, null, Security.Fingerprint));
                    StreamOffer(client,stream,number+":"+fileId,o=>o.DeviceId==peer.DeviceId&&o.Fingerprint==peer.Fingerprint&&offset>=0&&offset<=o.Size,offset);
                }
            }
            catch (Exception) { }
            finally
            {
                Interlocked.Exchange(ref offer.Pushing, 0);
                if (connection != null) lock (gate) connections.Remove(connection);
            }
        }
        private void AcceptLoop()
        {
            while (!disposed)
            {
                try
                {
                    TcpClient client = tcp.AcceptTcpClient();
                    lock (gate) { if (connections.Count >= 8 || disposed) { client.Close(); continue; } connections.Add(client); }
                    Task.Run(() => DispatchConnection(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e) { if (!disposed) Report("文件服务错误：" + e.Message); }
            }
        }
        private void ServeFile(TcpClient client)
        {
            Offer active = null; int percent = 0; long version = 0;bool completed=false;
            try
            {
                client.ReceiveTimeout = 5000; client.SendTimeout = 15000;
                using (NetworkStream stream = client.GetStream())
                {
                    RequestInput deadline = new RequestInput(client); deadline.Begin(5000);
                    BufferedStream input = new BufferedStream(deadline, 4096);
                    List<byte> request = new List<byte>(); int b = -1;
                    while (request.Count < 4096 && (b = input.ReadByte()) > 0) request.Add((byte)b);
                    Packet packet;
                    if (b != 0 || request.Count >= 4096 || !Protocol.TryParse(request.ToArray(), out packet) || packet.Mode != Protocol.GetFile) return;
                    string[] fields = packet.Body.Split(':'); long messageId, offset; int fileId;
                    if (fields.Length < 3 || !Int64.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out messageId) || !Int32.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fileId) || !Int64.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset)) return;
                    Offer offer;
                    if(!TryBeginOfferStream(messageId+":"+fileId,o=>o.Fingerprint==null&&o.Address==((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString()&&offset>=0&&offset<=o.Size,client,out offer,out version))return;
                    active=offer;
                    FileInfo current = new FileInfo(offer.Path);
                    if (!current.Exists || current.Length != offer.Size || current.LastWriteTimeUtc != offer.Modified) throw new IOException("原文件已改变");
                    percent = offer.Size == 0 ? 0 : (int)(offset * 100.0 / offer.Size); Transfer(offer, version, percent, "发送中");
                    using (FileStream file = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                    {
                        file.Position = offset; byte[] buffer = new byte[65536]; int count; long sent = offset;
                        TransferRate rate = new TransferRate(offset); var timing = System.Diagnostics.Stopwatch.StartNew();
                        while (!disposed && sent < offer.Size && (count = file.Read(buffer, 0, (int)Math.Min(buffer.Length, offer.Size - sent))) > 0) {
                            stream.Write(buffer, 0, count); sent += count; lock (gate) offer.Expires = DateTime.UtcNow.AddMinutes(30);
                            bool sampled = rate.Update(sent, timing.Elapsed.TotalMilliseconds);
                            int next = (int)(sent * 100.0 / offer.Size); if (next != percent || sampled) { percent = next; Transfer(offer, version, percent, "发送中", rate.BytesPerSecond); }
                        }
                        if (disposed || sent != offer.Size) throw new IOException("发送中断");
                    }
                    Transfer(offer, version, 100, "已发送（非保存确认）");completed=true;
                    Action<string> notice = TransferNotice; if (!disposed && notice != null) notice("文件已发送：" + current.Name);
                }
            }
            catch (IOException) { /* 对方取消或连接超时，不弹出无关用户对话框。 */ }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (UnauthorizedAccessException e) { Report("无法读取发送文件：" + e.Message); }
            finally { if (active != null&&!completed) Transfer(active, version, percent, "发送中断，等待对方重试"); EndOfferStream(active,client); lock (gate) connections.Remove(client); client.Close(); }
        }
        private void Transfer(Offer offer, long version, int percent, string status, double bytesPerSecond = 0)
        {
            lock(gate) {
                if(version != offer.ProgressVersion) return; offer.Percent=percent; offer.TransferState=status;
                var handler = TransferChanged; if (!disposed && handler != null) handler(offer.Record, offer.FileId, percent, status, bytesPerSecond);
            }
        }
        private void EndOffer(Offer offer, string state) { if(offer.TransferState != "已发送（非保存确认）"&&offer.TransferState!="对方已校验保存") Transfer(offer, ++offer.ProgressVersion, offer.Percent, state); }
        private void DispatchConnection(TcpClient client)
        {
            bool handedOff=false;
            try {
                if(!LoopbackOnly&&LanNetwork.Route(links,((IPEndPoint)client.Client.RemoteEndPoint).Address)==null)return;
                client.ReceiveTimeout=5000;byte[] first=new byte[1];
                if(client.Client.Receive(first,0,1,SocketFlags.Peek)!=1)return;
                if(first[0]=='F') {
                    RequestInput prefix=new RequestInput(client);prefix.Begin(5000);
                    int b0=prefix.ReadByte(),b1=prefix.ReadByte(),b2=prefix.ReadByte(),b3=prefix.ReadByte();
                    if(b0=='F'&&b1=='Q'&&b2=='2'&&b3=='\n'&&Security!=null)ServeSecure(client);
                    else if(b0=='F'&&b1=='Q'&&b2=='D'&&b3=='\n')ServeDiscovery(client,prefix);
                    else if(b0=='F'&&b1=='Q'&&b2=='R'&&b3=='\n')handedOff=AcceptReverse(client,prefix);
                    return;
                }
                if(Security==null||Settings.AllowLegacyProtocol)ServeFile(client);
            } catch(IOException e) {Report("安全连接未完成："+e.Message);} catch(SocketException) {} catch(System.Security.Authentication.AuthenticationException e) {Report("TLS 握手未通过："+e.GetBaseException().Message);} catch(ObjectDisposedException) {} catch(System.ComponentModel.Win32Exception e){Report("TLS 系统凭据失败："+e.NativeErrorCode+" "+e.Message);} catch(CryptographicException e){Report("TLS 证书失败："+e.Message);}
            finally {if(!handedOff){client.Close();lock(gate)connections.Remove(client);}}
        }
        private bool AcceptReverse(TcpClient client,Stream input)
        {
            string ticket=System.Text.Encoding.ASCII.GetString(SecureIdentity.ReadBytes(input,32));
            lock(gate){ReverseRequest request;if(!reverseRequests.TryGetValue(ticket,out request)||!Security.IsTrusted(request.DeviceId,request.Fingerprint))return false;
                reverseRequests.Remove(ticket);request.Client=client;request.Direct.Close();request.Ready.Set();return true;}
        }
        // 回连只改变 TCP 建连方向，调用方仍执行原有 TLS、设备配对和业务权限检查。
        internal TcpClient ConnectPeer(Peer peer,int port)
        {
            TcpClient direct=new TcpClient(AddressFamily.InterNetwork);ReverseRequest request=null;string ticket=Guid.NewGuid().ToString("N");
            lock(gate){if(disposed||connections.Count>=8){direct.Close();throw new IOException("服务已关闭或连接任务过多。");}connections.Add(direct);
                if(Security.IsTrusted(peer.DeviceId,peer.Fingerprint)){request=new ReverseRequest{DeviceId=peer.DeviceId,Fingerprint=peer.Fingerprint,Port=port,Direct=direct};reverseRequests.Add(ticket,request);}}
            try {
                if(LoopbackOnly&&!IPAddress.IsLoopback(peer.Endpoint.Address))throw new IOException("回环模式禁止连接局域网。");
                BindClient(direct,peer.Endpoint.Address);
                if(request!=null)Task.Delay(150).ContinueWith(_=>{lock(gate)if(!reverseRequests.ContainsKey(ticket))return;try{Probe(peer.Endpoint);}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}},TaskScheduler.Default);
                Exception failure=null;
                try{PeerConnector(direct,new IPEndPoint(peer.Endpoint.Address,port));}
                catch(Exception e){if(!(e is SocketException||e is IOException||e is AggregateException||e is ObjectDisposedException))throw;failure=e;}
                if(failure==null){lock(gate){reverseRequests.Remove(ticket);if(request==null||request.Client==null){direct.ReceiveTimeout=45000;direct.SendTimeout=15000;return direct;}}}
                direct.Close();ReleaseConnection(direct);
                if(request==null)throw new IOException("无法连接对方。",failure);
                if(!request.Ready.Wait(8000))throw new IOException("无法连接对方，请确认双方已升级并允许局域网连接。",failure);
                lock(gate){if(request.Client==null||disposed||!Security.IsTrusted(request.DeviceId,request.Fingerprint))throw new IOException("连接已取消或设备信任已撤销。");}
                request.Client.ReceiveTimeout=45000;request.Client.SendTimeout=15000;return request.Client;
            }catch{direct.Close();ReleaseConnection(direct);lock(gate)if(request!=null&&request.Client!=null){request.Client.Close();connections.Remove(request.Client);}throw;}
            finally{lock(gate)reverseRequests.Remove(ticket);}
        }
        internal void ReleaseConnection(TcpClient client){lock(gate)connections.Remove(client);}
        private void ServeReverse(IPEndPoint endpoint,int servicePort,string ticket,string id,string fingerprint,int epoch)
        {
            int sharePort=Port==65535?65534:Port+1,phonePort=Port>=65534?Port-2:Port+2;
            if(servicePort!=Port&&servicePort!=sharePort&&servicePort!=phonePort)return;
            TcpClient client=new TcpClient(AddressFamily.InterNetwork);
            lock(gate){if(disposed||epoch!=transportEpoch||connections.Count>=8||!Security.IsTrusted(id,fingerprint)){client.Close();return;}connections.Add(client);}
            try {
                BindClient(client,endpoint.Address);if(!client.ConnectAsync(endpoint.Address,endpoint.Port).Wait(8000))throw new IOException("回连超时。");
                client.ReceiveTimeout=5000;client.SendTimeout=15000;
                byte[] prefix=System.Text.Encoding.ASCII.GetBytes("FQR\n"+ticket);client.GetStream().Write(prefix,0,prefix.Length);
                if(servicePort==Port)DispatchConnection(client);else Security.AcceptReverse(servicePort,client);
            }catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}catch(AggregateException){}
            finally{client.Close();ReleaseConnection(client);}
        }
        private void ServeDiscovery(TcpClient client, Stream input)
        {
            Packet packet;
            if (!Protocol.TryParse(SecureIdentity.ReadFrame(input, 60000), out packet)) return;
            if (packet.Mode != Protocol.Entry && packet.Mode != Protocol.AnswerEntry && packet.Mode != Protocol.Absence) return;
            IPEndPoint from = new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint).Address, packet.ReplyPort > 0 ? packet.ReplyPort : Port);
            if (IsSelf(packet, from)) return;
            Peer peer = Upsert(packet, from);
            if (peer == null) return;
            if (packet.Mode == Protocol.Entry)
            {
                uint flags = Protocol.AnswerEntry | Protocol.CapUtf8 | Protocol.FileAttach | (peer.Utf8 ? Protocol.Utf8 : 0);
                SecureIdentity.WriteFrame(client.GetStream(), WithPort(Encode(flags, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group))));
            }
        }
        private TcpClient SecureConnect(IPEndPoint endpoint, Action<TcpClient> beforeConnect = null)
        {
            if(LoopbackOnly&&!IPAddress.IsLoopback(endpoint.Address))throw new IOException("回环模式禁止连接局域网。");
            TcpClient client=new TcpClient(AddressFamily.InterNetwork);
            lock(gate){if(disposed||connections.Count>=8){client.Close();throw new IOException("服务已关闭或连接任务过多。");}connections.Add(client);}
            try {
                if(beforeConnect!=null)beforeConnect(client);
                BindClient(client,endpoint.Address);
                Task connecting=client.ConnectAsync(endpoint.Address,endpoint.Port);
                connecting.ContinueWith(t => { var unused = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if(!connecting.Wait(8000))throw new IOException("加密连接超时。");
                client.ReceiveTimeout=5000;client.SendTimeout=15000;client.GetStream().Write(new byte[]{70,81,50,10},0,4);return client;
            }
            catch{client.Close();lock(gate)connections.Remove(client);throw;}
        }
        private void BindClient(TcpClient client,IPAddress destination)
        {
            if(!LoopbackOnly){LanNetwork.Link route=LanNetwork.Route(links,destination);if(route==null)throw new IOException("目标不在当前物理局域网内。");client.Client.Bind(new IPEndPoint(route.Address,0));try{client.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(route.Index));}catch(SocketException){}}
        }
        private void SendSecurePending(Pending item)
        {
            if(item.Delivered||item.Canceled||Interlocked.CompareExchange(ref item.Sending,1,0)!=0)return;
            item.AttemptStarted=clock.Elapsed;
            item.DirectFailed=false;
            TcpClient connection=null;
            Task.Delay(150).ContinueWith(_ =>
            {
                if(!item.Delivered&&!item.Canceled&&item.ConnectingClient!=null&&!disposed)
                    try{Send(Encode(Protocol.Entry|Protocol.CapUtf8|Protocol.FileAttach,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group)),item.Endpoint);}catch{}
            }, TaskScheduler.Default);
            try {
                using(TcpClient client=connection=SecureConnect(item.Endpoint,c=>{lock(gate){if(item.Canceled||item.Delivered){c.Close();throw new IOException("已取消发送。");}item.ConnectingClient=c;}}))using(SslStream stream=Security.Client(client,item.Fingerprint)) {
                    if(item.Delivered||item.Canceled)return;
                    client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                    Packet remote=AuthenticateSecureClient(stream,item.DeviceId,item.Fingerprint,item.Login);
                    if(item.Delivered||item.Canceled||Interlocked.CompareExchange(ref item.Transmitting,1,0)!=0)return;
                    try {
                        if(item.Delivered||item.Canceled)return;
                        SaveOffers();
                        lock(gate){Pending active;if(item.Canceled||item.Delivered||!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;}
                        SecureIdentity.WriteText(stream,"M\n"+Port);SecureIdentity.WriteFrame(stream,item.Bytes);
                        if(SecureIdentity.ReadText(stream,1024)!="OK")throw new IOException("对方未接受消息或未完成配对。");
                        lock(gate){Pending active;if(item.Canceled||!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;item.Delivered=true;item.Record.State="已送达";pending.Remove(item.Record.Packet);}
                        Upsert(remote,item.Endpoint);
                        Complete(item.Record,"已送达");
                        if(item.Record.HasAttachments)try{SaveOffers();}catch(IOException){}
                        if(item.Record.HasAttachments&&!disposed&&item.Peer!=null) {
                            Task.Delay(300).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                            Task.Delay(1200).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                        }
                    } finally {
                        Interlocked.Exchange(ref item.Transmitting,0);
                    }
                }
            } catch(Exception error) {
                item.DirectFailed=true;
                if(item.Delivered||item.Canceled)return;
                if(!(error is IOException||error is SocketException||error is System.Security.Authentication.AuthenticationException||error is AggregateException||error is ObjectDisposedException))throw;
                Exception baseErr=error.GetBaseException();
                if(baseErr is SecureIdentity.TrustException || baseErr is System.Security.Authentication.AuthenticationException)
                {
                    lock(gate)
                    {
                        Pending active;
                        if(item.Canceled||item.Delivered||!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;
                        pending.Remove(item.Record.Packet);item.Record.State="身份核对未通过，未确认送达";
                    }
                    NotifyDelivery(item.Record);
                }
                else if(HeartbeatRetry>=TimeSpan.FromSeconds(1)&&!disposed) {
                    TimeSpan waitUntil=clock.Elapsed+TimeSpan.FromMilliseconds(2200);
                    while(!item.Delivered&&!item.Canceled&&!disposed&&clock.Elapsed<waitUntil)Thread.Sleep(50);
                    if(item.Delivered||item.Canceled)return;
                }
                if(!disposed&&!item.Delivered&&!item.Canceled)Report("加密发送未完成："+baseErr.Message);
            }
            finally {lock(gate)item.ConnectingClient=null;if(connection!=null)lock(gate)connections.Remove(connection);item.Last=DateTime.UtcNow;Interlocked.Exchange(ref item.Sending,0);}
        }
        private void StreamOffer(TcpClient client,SslStream stream,string key,Func<Offer,bool> authorize,long offset)
        {
            Offer offer;long version;
            if (!TryBeginOfferStream(key, authorize, client, out offer, out version))
            {
                bool revoked;
                lock (gate)
                {
                    Offer current;
                    revoked = !disposed && (!offers.TryGetValue(key, out current) || (current.Canceled || current.Expires < DateTime.UtcNow) && authorize(current));
                }
                if (revoked) SecureIdentity.WriteText(stream, "REVOKED");
                throw new IOException("文件授权无效或已取消。");
            }
            try {
                FileInfo current=new FileInfo(offer.Path);if(!current.Exists||current.Length!=offer.Size||current.LastWriteTimeUtc!=offer.Modified)throw new IOException("源文件已改变。");
                SecureIdentity.WriteText(stream,"OK\n"+offer.Size+"\n"+offer.Sha256);
                long sent=offset;TransferRate rate=new TransferRate(offset);var timing=System.Diagnostics.Stopwatch.StartNew();
                using(FileStream file=File.OpenRead(offer.Path)) {file.Position=offset;byte[] buffer=new byte[65536];while(sent<offer.Size){int count=file.Read(buffer,0,(int)Math.Min(buffer.Length,offer.Size-sent));if(count==0)throw new EndOfStreamException();stream.Write(buffer,0,count);sent+=count;lock(gate)offer.Expires=DateTime.UtcNow.AddDays(7);if(rate.Update(sent,timing.Elapsed.TotalMilliseconds))Transfer(offer,version,(int)(sent*100.0/Math.Max(1,offer.Size)),"发送中",rate.BytesPerSecond);}}
                stream.Flush();Transfer(offer,version,100,"已发送，等待校验保存");
                if(SecureIdentity.ReadText(stream,128)=="STORED\n"+offer.Sha256){Transfer(offer,version,100,"对方已校验保存");try{SaveOffers();}catch(IOException error){Report("文件完成状态保存失败："+error.Message);}}
            }catch(IOException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            catch(ObjectDisposedException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            finally {EndOfferStream(offer,client);}
        }
        private void ServeSecure(TcpClient client)
        {
            using(SslStream stream=Security.Server(client,true)) {
                client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                Packet identity=AuthenticateSecureServer(stream);
                if(identity==null)return;
                RequestInput input=new RequestInput(client,stream);input.Begin(5000);
                string[] header=SecureIdentity.ReadText(input,128).Split('\n');int senderPort;
                if(header.Length!=2||!Int32.TryParse(header[1],out senderPort)||senderPort<1024||senderPort>65535)throw new IOException("安全报文端口无效。");
                Packet packet;if(!Protocol.TryParse(SecureIdentity.ReadFrame(input,60000),out packet))throw new IOException("安全报文无效。");
                string fingerprint=SecureIdentity.RemoteFingerprint(stream);
                if(packet.DeviceId!=identity.DeviceId||packet.Fingerprint!=fingerprint||packet.Login!=identity.Login)throw new IOException("报文身份与证书不一致。");
                client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                IPEndPoint endpoint=new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint).Address,senderPort);
                if(header[0]=="M"&&(packet.Mode==Protocol.SendMessage||packet.Mode==Protocol.ReleaseFiles)) {Handle(packet,endpoint,true);SecureIdentity.WriteText(stream,"OK");return;}
                if(header[0]=="V"&&packet.Mode==Protocol.Entry){Upsert(packet,endpoint);SecureIdentity.WriteText(stream,"OK");return;}
                if(header[0]=="S"&&(packet.Mode==Protocol.Entry||packet.Mode==Protocol.Absence))
                {
                        Upsert(packet,endpoint);
                        double waitMs=0;
                        lock(gate) {
                            TimeSpan now=clock.Elapsed;
                            foreach(Pending item in pending.Values)if(!item.Delivered&&!item.Canceled&&item.DeviceId==packet.DeviceId&&item.Fingerprint==fingerprint&&!item.DirectFailed){double rem=120-(now-item.AttemptStarted).TotalMilliseconds;if(rem>waitMs)waitMs=rem;}
                            foreach(PendingDownload pd in pendingDownloads)if(pd.Claimed==0&&pd.DeviceId==packet.DeviceId&&pd.Fingerprint==fingerprint&&!pd.DirectFailed){double rem=120-(now-pd.CreatedAt).TotalMilliseconds;if(rem>waitMs)waitMs=rem;}
                        }
                        if(waitMs>0)Thread.Sleep((int)waitMs+5);
                        List<Pending> due=new List<Pending>();
                        lock(gate) {
                            foreach(Pending item in pending.Values.OrderBy(p=>p.Record.Packet))
                                if(!item.Delivered&&!item.Canceled&&item.DeviceId==packet.DeviceId&&item.Fingerprint==fingerprint)due.Add(item);
                        }
                        foreach(Pending item in due) {
                            // 逐条占用发送权；前一条失败不能把后续消息永久留在“发送中”。
                            if(Interlocked.CompareExchange(ref item.Transmitting,1,0)!=0)continue;
                            try {
                                lock(gate){Pending current;if(item.Delivered||item.Canceled||!pending.TryGetValue(item.Record.Packet,out current)||current!=item||!Security.IsTrusted(item.DeviceId,item.Fingerprint))continue;}
                                SaveOffers();
                                lock(gate){Pending current;if(item.Delivered||item.Canceled||!pending.TryGetValue(item.Record.Packet,out current)||current!=item)continue;}
                                SecureIdentity.WriteText(stream,"M");
                                SecureIdentity.WriteFrame(stream,item.Bytes);
                                if(SecureIdentity.ReadText(stream,1024)=="OK") {
                                    TcpClient connecting;bool delivered=false;
                                    lock(gate){Pending active;if(!item.Canceled&&pending.TryGetValue(item.Record.Packet,out active)&&active==item){item.Delivered=true;item.Record.State="已送达";connecting=item.ConnectingClient;item.ConnectingClient=null;pending.Remove(item.Record.Packet);delivered=true;}else connecting=null;}
                                    if(connecting!=null)try{connecting.Close();}catch{}
                                    if(!delivered)continue;
                                    Complete(item.Record,"已送达");
                                    if(item.Record.HasAttachments)try{SaveOffers();}catch(IOException){}
                                    if(item.Record.HasAttachments&&!disposed&&item.Peer!=null) {
                                        Task.Delay(300).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                                        Task.Delay(1200).ContinueWith(_=>ScheduleSync(item.Peer),TaskScheduler.Default);
                                    }
                                }
                            } finally {
                                item.Last=DateTime.UtcNow;
                                Interlocked.Exchange(ref item.Transmitting,0);
                            }
                        }
                        List<PendingDownload> pulls=new List<PendingDownload>();
                        lock(gate) {
                            foreach(PendingDownload pd in pendingDownloads)if(pd.Claimed==0&&pd.DeviceId==packet.DeviceId&&pd.Fingerprint==fingerprint)pulls.Add(pd);
                        }
                        foreach(PendingDownload pd in pulls) {
                            SecureIdentity.WriteText(stream,"D\n"+pd.Packet.ToString("x")+":"+pd.FileId.ToString("x")+":"+pd.Offset.ToString("x"));
                        }
                        if(packet.ReverseConnections){List<KeyValuePair<string,ReverseRequest>> requests;lock(gate)requests=reverseRequests.Where(p=>p.Value.DeviceId==packet.DeviceId&&p.Value.Fingerprint==fingerprint).ToList();
                            foreach(var request in requests)SecureIdentity.WriteText(stream,"R\n"+request.Key+":"+request.Value.Port);}
                        SecureIdentity.WriteText(stream,"OK");
                        return;
                }
                if(header[0]=="P"&&packet.Mode==Protocol.GetFile)
                {
                    Upsert(identity,endpoint);
                    string[] pFields=packet.Body.Split(':');long pNum,pOff;int pFileId;
                    if(pFields.Length<3||!Int64.TryParse(pFields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pNum)||!Int32.TryParse(pFields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pFileId)||!Int64.TryParse(pFields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out pOff))throw new IOException("文件请求无效。");
                    PendingDownload match=null;TcpClient direct=null;
                    lock(gate) {
                        foreach(PendingDownload pd in pendingDownloads) {
                            if(pd.DeviceId==packet.DeviceId&&pd.Fingerprint==fingerprint&&pd.Packet==pNum&&pd.FileId==pFileId&&pd.Offset==pOff&&Interlocked.CompareExchange(ref pd.Claimed,1,0)==0) {
                                match=pd;pendingDownloads.Remove(pd);pd.PushedClient=client;pd.PushedStream=stream;direct=pd.DirectClient;pd.Ready.Set();break;
                            }
                        }
                    }
                    if(match==null)return;
                    if(direct!=null)try{direct.Close();}catch{}
                    while(!match.Done.Wait(1000)&&!disposed&&client.Connected){}
                    return;
                }
                if(header[0]!="F"||packet.Mode!=Protocol.GetFile)throw new IOException("未知安全操作。");
                Upsert(identity,endpoint);
                string[] fields=packet.Body.Split(':');long number,offset;int fileId;
                if(fields.Length<3||!Int64.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out number)||!Int32.TryParse(fields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out fileId)||!Int64.TryParse(fields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out offset))throw new IOException("文件请求无效。");
                StreamOffer(client,stream,number+":"+fileId,o=>o.DeviceId==packet.DeviceId&&o.Fingerprint==fingerprint&&offset>=0&&offset<=o.Size,offset);
            }
        }
        private byte[] SecureHello() { return Protocol.Encode(0,Login,Host,Protocol.Entry|Protocol.Utf8,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group),Settings.DeviceId,null,Security.Fingerprint); }
        private byte[] SecureSyncHello() { return Protocol.Encode(0,Login,Host,Protocol.Absence|Protocol.Utf8,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group),Settings.DeviceId,null,Security.Fingerprint).Concat(System.Text.Encoding.ASCII.GetBytes("FQREV=1\0")).ToArray(); }
        private TcpClient SecurePeerConnect(Peer peer){TcpClient client=ConnectPeer(peer,peer.Endpoint.Port);try{client.GetStream().Write(new byte[]{70,81,50,10},0,4);return client;}catch{client.Close();ReleaseConnection(client);throw;}}
        public void VerifyPeer(Peer peer){if(Security==null||peer.Fingerprint==null)throw new IOException("请先升级对方飞Q并刷新联系人。");using(TcpClient client=SecurePeerConnect(peer))try{using(SslStream stream=Security.Client(client,peer.Fingerprint)){Packet remote=AuthenticateSecureClient(stream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);SecureIdentity.WriteText(stream,"V\n"+Port);SecureIdentity.WriteFrame(stream,SecureHello());if(SecureIdentity.ReadText(stream,16)!="OK")throw new IOException("身份验证未完成。");Upsert(remote,peer.Endpoint);}}finally{lock(gate)connections.Remove(client);}}
        public void ForgetSecurity(Peer peer){try{Security.Forget(peer.DeviceId);}finally{lock(gate){foreach(Pending item in pending.Values.Where(p=>p.DeviceId==peer.DeviceId).ToList()){pending.Remove(item.Record.Packet);Complete(item.Record,"设备信任已撤销，未确认送达");}foreach(string key in offers.Where(o=>o.Value.DeviceId==peer.DeviceId).Select(o=>o.Key).ToList())offers.Remove(key);foreach(TcpClient client in connections)client.Close();SaveOffers();}}}
        private Packet AuthenticateSecureClient(SslStream stream,string id,string fingerprint,string name,bool sync=false)
        {
            stream.ReadTimeout=45000;stream.WriteTimeout=15000;
            SecureIdentity.WriteFrame(stream,sync?SecureSyncHello():SecureHello());Packet remote;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream,5000),4096),out remote)||remote.DeviceId!=id||remote.Fingerprint!=fingerprint)throw new IOException("对方设备身份与所选联系人不一致。");
            bool accepted=Security.EnsureTrusted(id,fingerprint,name);SecureIdentity.WriteText(stream,accepted?"YES":"NO");
            if(!accepted||SecureIdentity.ReadText(new SecureIdentity.Deadline(stream,45000),16)!="YES")throw new SecureIdentity.TrustException("设备配对未通过，请在两端核对后重试。");
            return remote;
        }
        private Packet AuthenticateSecureServer(SslStream stream)
        {
            Packet remote;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream,5000),4096),out remote)||remote.DeviceId==null||remote.Fingerprint!=SecureIdentity.RemoteFingerprint(stream))throw new IOException("加密连接身份无效。");
            SecureIdentity.WriteFrame(stream,SecureHello());
            bool accepted=remote.Mode==Protocol.Absence
                ?(Security.IsTrusted(remote.DeviceId,remote.Fingerprint)||(HasPendingFor(remote.DeviceId,remote.Fingerprint)&&Security.EnsureTrusted(remote.DeviceId,remote.Fingerprint,remote.Body)))
                :Security.EnsureTrusted(remote.DeviceId,remote.Fingerprint,remote.Body);
            bool other=SecureIdentity.ReadText(new SecureIdentity.Deadline(stream,45000),16)=="YES";
            SecureIdentity.WriteText(stream,accepted&&other?"YES":"NO");return accepted&&other?remote:null;
        }
        private void SendReleaseFiles(Peer peer, string body)
        {
            if (Security != null && peer.Fingerprint != null)
            {
                TcpClient connection = null;
                try
                {
                    using (TcpClient client = connection = SecurePeerConnect(peer))
                    using (SslStream stream = Security.Client(client, peer.Fingerprint))
                    {
                        Packet remote = AuthenticateSecureClient(stream, peer.DeviceId, peer.Fingerprint, peer.DisplayName);
                        SecureIdentity.WriteText(stream, "M\n" + Port);
                        SecureIdentity.WriteFrame(stream, Encode(Protocol.ReleaseFiles, body, null));
                        if (SecureIdentity.ReadText(stream, 1024) != "OK") throw new IOException("文件通知未送达。");
                        Upsert(remote, peer.Endpoint);
                    }
                }
                finally { if (connection != null) lock (gate) connections.Remove(connection); }
                return;
            }
            if (Security != null && (!Settings.AllowLegacyProtocol || Security.TrustedFingerprint(peer.DeviceId) != null)) throw new IOException("未允许旧版明文协议。");
            Send(Protocol.Encode(Next(), Login, Host, Protocol.ReleaseFiles | Protocol.Utf8, body, null, Settings.DeviceId), peer.Endpoint);
        }
        public void RejectFiles(Peer peer, long packet)
        {
            string body = packet.ToString(CultureInfo.InvariantCulture);
            if (Security != null && peer.Fingerprint != null)
            {
                Peer snapshot = new Peer { Endpoint = new IPEndPoint(peer.Endpoint.Address, peer.Endpoint.Port), DeviceId = peer.DeviceId, Fingerprint = peer.Fingerprint, Login = peer.Login, Nickname = peer.DisplayName };
                Task.Run(() => { try { SendReleaseFiles(snapshot, body); } catch (Exception e) { Report("已本地拒收并清理断点；拒绝通知未送达：" + e.Message); } });
            }
            else SendReleaseFiles(peer, body);
        }
        public Task<string> ReceiveFileAsync(Peer peer, long packet, Attachment file, string destination, IProgress<int> progress, CancellationToken cancel, bool renameOnCollision = false, IProgress<TransferProgress> details = null, Func<bool> keepPartial = null)
        {
            if(Security!=null&&peer.Fingerprint!=null)return Task.Run(()=>ReceiveSecureFile(peer,packet,file,destination,progress,cancel,renameOnCollision,details,keepPartial),cancel);
            if(Security!=null&&(!Settings.AllowLegacyProtocol||Security.TrustedFingerprint(peer.DeviceId)!=null))throw new IOException("未允许旧版明文文件传输。");
            if (!Protocol.SafeFileName(file.Name) || file.Size < 0) throw new IOException("不安全的文件名或长度。");
            if (LoopbackOnly && !IPAddress.IsLoopback(peer.Endpoint.Address)) throw new InvalidOperationException("回环测试模式不能连接局域网。");
            return Task.Run(() =>
            {
                string path = Path.GetFullPath(destination);
                // 同一目标串行提交，但排队等待本身也必须响应取消，不能等待另一个大文件下载完。
                SemaphoreSlim receiveLock = receiveLocks[(path.ToUpperInvariant().GetHashCode() & Int32.MaxValue) % receiveLocks.Length];
                receiveLock.Wait(cancel);
                try
                {
                    string temporary = PartialPath(peer, packet, file, path);
                    bool owned = false;
                    TcpClient client = new TcpClient(AddressFamily.InterNetwork);
                    lock (gate) { if (disposed) { client.Close(); throw new ObjectDisposedException("LanService"); } if (connections.Count >= 8) { client.Close(); throw new IOException("同时传输的文件过多。"); } connections.Add(client); }
                    try
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (!renameOnCollision && (File.Exists(path) || Directory.Exists(path))) throw new IOException("目标文件已存在；请选择新文件名，不会自动覆盖。");
                        using (FileStream output = new FileStream(temporary, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
                        {
                            owned = true;
                            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) throw new IOException("断点文件不能是链接。");
                            if (output.Length > file.Size) output.SetLength(0);
                            long offset = output.Length; output.Position = offset;
                            using (cancel.Register(() => client.Close()))
                            {
                                BindClient(client,peer.Endpoint.Address);Task connecting = client.ConnectAsync(peer.Endpoint.Address, peer.Endpoint.Port);
                                if (!connecting.Wait(8000)) throw new IOException("连接超时，请确认对方在线且允许 TCP 端口。");
                                client.ReceiveTimeout = 15000; client.SendTimeout = 5000;
                                using (NetworkStream stream = client.GetStream())
                                {
                                    byte[] request = Encode(Protocol.GetFile, packet.ToString("x") + ":" + file.Id.ToString("x") + ":" + offset.ToString("x") + ":", null);
                                    stream.Write(request, 0, request.Length);
                                    byte[] buffer = new byte[65536]; long received = offset; int last = -1;
                                    TransferRate rate = new TransferRate(offset); var timing = System.Diagnostics.Stopwatch.StartNew();
                                    if (progress != null) progress.Report(file.Size == 0 ? 0 : (int)(received * 100.0 / file.Size));
                                    if (details != null) details.Report(new TransferProgress(file.Size == 0 ? 0 : (int)(received * 100.0 / file.Size), 0));
                                    while (received < file.Size)
                                    {
                                        cancel.ThrowIfCancellationRequested();
                                        int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Size - received));
                                        if (count == 0) throw new IOException("传输中断或文件邀请失效；已保留断点，请在发送方在线且邀请有效时重试。");
                                        output.Write(buffer, 0, count); received += count;
                                        int percent = (int)(received * 100.0 / file.Size);
                                        bool sampled = rate.Update(received, timing.Elapsed.TotalMilliseconds);
                                        if (details != null && (percent != last || sampled)) details.Report(new TransferProgress(percent, rate.BytesPerSecond));
                                        if (percent != last) { last = percent; if (progress != null) progress.Report(percent); }
                                    }
                                    output.Flush(true);
                                }
                            }
                        }
                        cancel.ThrowIfCancellationRequested();
                        if (renameOnCollision)
                        {
                            string original = path; int suffix = 0;
                            while (true)
                            {
                                cancel.ThrowIfCancellationRequested();
                                try { File.Move(temporary, path); break; }
                                catch (IOException)
                                {
                                    if ((!File.Exists(path) && !Directory.Exists(path)) || ++suffix > 10000) throw;
                                    path = Path.Combine(Path.GetDirectoryName(original), Path.GetFileNameWithoutExtension(original) + " (" + suffix + ")" + Path.GetExtension(original));
                                }
                            }
                        }
                        else File.Move(temporary, path);
                        if (progress != null) progress.Report(100);
                        return path;
                    }
                    catch (Exception)
                    {
                        if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                        throw;
                    }
                    finally
                    {
                        client.Close(); lock (gate) connections.Remove(client);
                        // 网络失败保留已写入数据；只有明确取消才丢弃断点。
                        if (owned && File.Exists(temporary) && (cancel.IsCancellationRequested && !(keepPartial != null && keepPartial()) || new FileInfo(temporary).Length == 0)) File.Delete(temporary);
                    }
                }
                finally { receiveLock.Release(); }
            }, cancel);
        }
        public static string PartialPath(Peer peer, long packet, Attachment file, string destination)
        {
            string path = Path.GetFullPath(destination);
            string identity = peer.Id + "\n" + peer.Host + "\n" + packet + "\n" + file.Id + "\n" + file.Name + "\n" + file.Size + "\n" + file.Modified + "\n" + path.ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
                return Path.Combine(Path.GetDirectoryName(path), ".feiq-" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "") + ".part");
        }
        private string ReceiveSecureFile(Peer peer,long packet,Attachment file,string destination,IProgress<int> progress,CancellationToken cancel,bool rename,IProgress<TransferProgress> details,Func<bool> keepPartial)
        {
            if(!Protocol.SafeFileName(file.Name)||file.Size<0||!SecureIdentity.ValidFingerprint(file.Sha256))throw new IOException("文件元数据或校验值无效。");
            string path=Path.GetFullPath(destination),part=PartialPath(peer,packet,file,path);bool owned=false,revoked=false;TcpClient connection=null;SslStream directStream=null;PendingDownload pd=null;
            SemaphoreSlim targetLock=receiveLocks[(path.ToUpperInvariant().GetHashCode()&Int32.MaxValue)%receiveLocks.Length];targetLock.Wait(cancel);
            try {
                if(!rename&&(File.Exists(path)||Directory.Exists(path)))throw new IOException("目标已存在，不会覆盖。");
                cancel.ThrowIfCancellationRequested();
                using(FileStream output=new FileStream(part,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                    owned=true;if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("断点文件不能是链接。");
                    if(output.Length>file.Size)output.SetLength(0);long offset=output.Length;output.Position=offset;
                    pd=new PendingDownload(peer.DeviceId,peer.Fingerprint,packet,file.Id,offset);
                    lock(gate)pendingDownloads.Add(pd);
                    PendingDownload activePd=pd;
                    Task.Delay(150).ContinueWith(_=>{
                        if(activePd.Claimed==0&&!disposed)
                            try{Send(Encode(Protocol.Entry|Protocol.CapUtf8|Protocol.FileAttach,Protocol.Field(Settings.Nickname),Protocol.Field(Settings.Group)),peer.Endpoint);}catch{}
                    },TaskScheduler.Default);
                    TimeSpan waitUntil=clock.Elapsed+TimeSpan.FromSeconds(8);
                    Exception connectError=null;
                    try {
                        connection=SecureConnect(peer.Endpoint,c=>activePd.DirectClient=c);
                        directStream=Security.Client(connection,peer.Fingerprint);
                        Packet remote=AuthenticateSecureClient(directStream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);
                        Upsert(remote,peer.Endpoint);
                        if(Interlocked.CompareExchange(ref pd.Claimed,1,0)==0) {
                            lock(gate)pendingDownloads.Remove(pd);
                            byte[] request=Protocol.Encode(Next(),Login,Host,Protocol.GetFile|Protocol.Utf8,packet.ToString("x")+":"+file.Id.ToString("x")+":"+offset.ToString("x")+":",null,Settings.DeviceId,null,Security.Fingerprint);
                            SecureIdentity.WriteText(directStream,"F\n"+Port);SecureIdentity.WriteFrame(directStream,request);
                        } else {
                            try{directStream.Dispose();}catch{}directStream=null;
                            try{connection.Close();}catch{}lock(gate)connections.Remove(connection);connection=null;
                        }
                    } catch(Exception e) {
                        connectError=e is AggregateException?e.GetBaseException():e;
                        if(directStream!=null){try{directStream.Dispose();}catch{}directStream=null;}
                        if(connection!=null){try{connection.Close();}catch{}lock(gate)connections.Remove(connection);connection=null;}
                    }
                    if(directStream==null) {
                        pd.DirectFailed=true;
                        cancel.ThrowIfCancellationRequested();
                        string msg=connectError==null?null:connectError.Message;
                        if(connectError is SecureIdentity.TrustException||connectError is System.Security.Authentication.AuthenticationException||msg=="服务已关闭或连接任务过多。"||msg=="回环模式禁止连接局域网。"||msg=="目标不在当前物理局域网内。")throw connectError;
                        while(pd.PushedStream==null&&HeartbeatRetry>=TimeSpan.FromSeconds(1)&&clock.Elapsed<waitUntil) {
                            cancel.ThrowIfCancellationRequested();
                            if(disposed)throw new ObjectDisposedException("LanService");
                            if(pd.Ready.Wait(100,cancel))break;
                        }
                        if(pd.PushedStream==null)throw connectError??new IOException("加密连接超时。");
                    }
                    TcpClient activeClient=connection??pd.PushedClient;
                    SslStream stream=directStream??pd.PushedStream;
                    using(cancel.Register(()=>{try{activeClient.Close();}catch{}})) {
                        string[] response=SecureIdentity.ReadText(stream,256).Split('\n');
                        if (response.Length == 1 && response[0] == "REVOKED") { revoked = true; throw new OfferRevokedException(); }
                        if(response.Length!=3||response[0]!="OK"||response[1]!=file.Size.ToString(CultureInfo.InvariantCulture)||response[2]!=file.Sha256)throw new IOException("发送方文件信息已变化。");
                        byte[] buffer=new byte[65536];long received=offset;TransferRate rate=new TransferRate(offset);var timer=System.Diagnostics.Stopwatch.StartNew();
                        while(received<file.Size){cancel.ThrowIfCancellationRequested();int count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,file.Size-received));if(count==0)throw new IOException("传输中断，已保留断点。");output.Write(buffer,0,count);received+=count;bool sampled=rate.Update(received,timer.Elapsed.TotalMilliseconds);if(sampled){int percent=(int)(received*100.0/Math.Max(1,file.Size));if(progress!=null)progress.Report(percent);if(details!=null)details.Report(new TransferProgress(percent,rate.BytesPerSecond));}}
                        output.Flush(true);output.Position=0;if(details!=null)details.Report(new TransferProgress(100,0,"正在校验"));
                        string hash;using(SHA256 sha=SHA256.Create()) {
                            int count;while((count=output.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();sha.TransformBlock(buffer,0,count,buffer,0);}
                            sha.TransformFinalBlock(buffer,0,0);hash=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                        }
                        if(hash!=file.Sha256){output.SetLength(0);throw new IOException("文件校验失败，已清除错误断点，请重新接收。");}
                    }
                }
                cancel.ThrowIfCancellationRequested();string original=path;int suffix=0;
                while(true){try{File.Move(part,path);break;}catch(IOException){if(!rename||(!File.Exists(path)&&!Directory.Exists(path))||++suffix>10000)throw;path=Path.Combine(Path.GetDirectoryName(original),Path.GetFileNameWithoutExtension(original)+" ("+suffix+")"+Path.GetExtension(original));}}
                SslStream finalStream=directStream??pd.PushedStream;
                try{SecureIdentity.WriteText(finalStream,"STORED\n"+file.Sha256);}catch(IOException){Report("文件已校验保存，但保存确认未送达。");}
                if(progress!=null)progress.Report(100);return path;
            } catch(Exception){if(cancel.IsCancellationRequested)throw new OperationCanceledException(cancel);throw;}
            finally {
                if(pd!=null){lock(gate)pendingDownloads.Remove(pd);pd.Done.Set();}
                if(directStream!=null)try{directStream.Dispose();}catch{}
                if(connection!=null){try{connection.Close();}catch{}lock(gate)connections.Remove(connection);}
                try{if(owned&&File.Exists(part)&&(revoked||cancel.IsCancellationRequested&&!(keepPartial!=null&&keepPartial())||new FileInfo(part).Length==0))File.Delete(part);}finally{targetLock.Release();}
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            if (udp != null)
            {
                foreach (Peer peer in Peers)
                    try { Send(Encode(Protocol.Exit, Settings.Nickname, null), peer.Endpoint); } catch (Exception) { }
            }
            disposed = true;
            if (Security != null) Security.CancelConfirmations();
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged; NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            if (timer != null) timer.Dispose();
            if (udp != null) udp.Close();
            if (tcp != null) tcp.Stop();
            lock (gate)
            {
                foreach (TcpClient client in connections) client.Close();
                foreach (TcpClient client in probeSockets) client.Close();
                foreach (PendingDownload pd in pendingDownloads) { pd.Ready.Set(); pd.Done.Set(); }
                pendingDownloads.Clear();
                CancelReverseRequests();
                pendingTcpTargets.Clear();
                offers.Clear();
                // 即使 UI 已销毁，持久层仍持有原记录，可在退出时保存明确终态。
                foreach (Pending item in pending.Values) item.Record.State = "已退出，未确认送达";
                pending.Clear();
            }
            if(Security!=null){Security.TrustRevoked-=OnTransportRevoked;Security.Dispose();}
        }
    }
}
