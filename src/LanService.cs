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
    public sealed class LanService : IDisposable
    {
        private sealed class Pending
        {
            public readonly IPEndPoint Endpoint;
            public readonly string Login, DeviceId, Fingerprint;
            public int Sending;
            public byte[] Bytes; public ChatRecord Record;
            public DateTime Last; public int Attempts;
            public Pending(Peer peer)
            {
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
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        private readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        private readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();
        private readonly Dictionary<string, Offer> offers = new Dictionary<string, Offer>();
        private readonly HashSet<TcpClient> connections = new HashSet<TcpClient>();
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
        }
        public event Action NetworkReset;
        public void ConfigureSecurity(string root, Func<string,string,bool> confirmation)
        {
            if(udp!=null)throw new InvalidOperationException("安全身份必须在启动前配置。");
            dataRoot=root;Security=new SecureIdentity(root);Security.ConfirmPeer=confirmation;RestoreOffers();
        }
        private void SaveOffers(Dictionary<string,Offer> snapshot = null)
        {
            if(dataRoot==null)return;
            lock(gate) {
                var saved=(snapshot??offers).Where(o=>o.Value.Fingerprint!=null).Select(o=>new StoredOffer{Key=o.Key,Path=o.Value.Path,Address=o.Value.Address,DeviceId=o.Value.DeviceId,Fingerprint=o.Value.Fingerprint,Sha256=o.Value.Sha256,Size=o.Value.Size,ModifiedTicks=o.Value.Modified.Ticks,ExpiresTicks=o.Value.Expires.Ticks,FileId=o.Value.FileId,Record=o.Value.Record}).ToArray();
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
                offers[item.Key]=new Offer{Path=file.FullName,Size=item.Size,Modified=file.LastWriteTimeUtc,Address=item.Address,Expires=new DateTime(item.ExpiresTicks,DateTimeKind.Utc),DeviceId=item.DeviceId,Fingerprint=item.Fingerprint,Sha256=item.Sha256,Record=item.Record,FileId=item.FileId};
                sequence=Math.Max(sequence,item.Record.Packet);
            }
        }
        public event Action PeersChanged;
        public event Action<IncomingMessage> MessageReceived;
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
        public ChatRecord OutgoingRecord(string peer, long packet) { lock(gate) { Offer offer=offers.Values.FirstOrDefault(o=>o.Record.PeerId==peer&&o.Record.Packet==packet&&o.Expires>DateTime.UtcNow);return offer==null?null:offer.Record; } }
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
        public void Probe(IPEndPoint endpoint)
        {
            if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("目前仅支持 IPv4。");
            Send(Encode(Protocol.Entry | Protocol.CapUtf8 | Protocol.FileAttach, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group)), endpoint);
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
                    try { Probe(new IPEndPoint(address, Port)); } catch (SocketException e) { Report("广播失败：" + e.Message); }
            }
            // 单播补充广播；只有收到真实报文才置为在线，不把历史地址伪装成在线联系人。
            List<Peer> known = Peers; TimeSpan now = clock.Elapsed;
            // 每次单播都计为一次待回应探测，对方任何报文都会清零。
            lock (gate) foreach (Peer peer in known) { peer.Probed = now; peer.Misses++; }
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
            }
            foreach(Pending item in interrupted)Complete(item.Record,"网络已切换，未确认送达");
            Action reset=NetworkReset;if(reset!=null)reset();Changed();
        }
        // 网络切换或用户刷新时立即复核：仍能回应的联系人保持在线，失联者约 6 秒内离线。
        public void Revalidate()
        {
            lock (gate) verifyFrom = clock.Elapsed;
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
                    peer.Group = String.IsNullOrWhiteSpace(packet.Extra) ? "我的局域网" : Protocol.Prefix(packet.Extra, 128);
                }
                peer.Utf8 = peer.Utf8 || (packet.Command & (Protocol.Utf8 | Protocol.CapUtf8)) != 0;
                peer.Online = packet.Mode != Protocol.Exit; peer.LastSeen = DateTime.UtcNow; peer.Seen = clock.Elapsed; peer.Misses = 0;
            }
            Changed(); return peer;
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
                    if ((Settings.DeviceId!=null&&packet.DeviceId==Settings.DeviceId)||((packet.ReplyPort>0?packet.ReplyPort:remote.Port) == Port && localAddresses.Contains(remote.Address) && packet.Login == Login && packet.Host == Host)) continue;
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
                    if (pending.TryGetValue(number, out candidate) && (secure || candidate.Fingerprint==null) && candidate.Endpoint.Equals(remote) && candidate.Login == packet.Login && (packet.DeviceId == null || candidate.DeviceId == null || candidate.DeviceId == packet.DeviceId))
                    { item = candidate; pending.Remove(number); }
                }
                if (item != null) Complete(item.Record, "已送达");
                return;
            }
            if (packet.Mode == Protocol.ReleaseFiles)
            {
                long messageId;
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
            record.State = state; Action<ChatRecord> handler = DeliveryChanged; if (!disposed && handler != null) handler(record);
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
                List<Pending> retry = new List<Pending>(), failed = new List<Pending>(); List<IPEndPoint> probes = new List<IPEndPoint>(); bool changed = false;
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow; TimeSpan elapsed = clock.Elapsed;
                    foreach (Pending item in pending.Values.ToList())
                    {
                        if (item.Sending!=0 || (now - item.Last).TotalSeconds < 2) continue;
                        if (item.Attempts >= 4) { pending.Remove(item.Record.Packet); failed.Add(item); }
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
                        bool verify = peer.Seen < verifyFrom;
                        if ((!verify && elapsed - peer.Seen < HeartbeatIdle) || elapsed - peer.Probed < HeartbeatRetry) continue;
                        if (peer.Misses >= (verify ? VerifyMisses : IdleMisses)) { peer.Online = false; changed = true; }
                        else { peer.Probed = elapsed; peer.Misses++; probes.Add(peer.Endpoint); }
                    }
                }
                foreach (Pending item in retry) try { if(item.Fingerprint!=null&&Security!=null)Task.Run(()=>SendSecurePending(item));else Send(item.Bytes, item.Endpoint); } catch (SocketException) { }
                foreach (IPEndPoint target in probes) try { Probe(target); } catch (SocketException) { } catch(IOException) { }
                foreach (Pending item in failed) Complete(item.Record, "未确认送达，请检查网络后重发");
                if (changed) Changed();
                if (Interlocked.Exchange(ref networkDirty, 0) != 0) Revalidate();
                else if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 30) Refresh();
            }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Report("网络维护失败：" + e.Message); }
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
            Offer active = null; int percent = 0; long version = 0;
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
                    lock (gate) if (!offers.TryGetValue(messageId + ":" + fileId, out offer)) return;
                    if (offer.Fingerprint!=null || offer.Expires < DateTime.UtcNow || offer.Address != ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() || offset < 0 || offset > offer.Size) return;
                    // 保留重复下载能力，但只让最新连接更新这一文件的显示进度。
                    lock (gate) { version = ++offer.ProgressVersion; active = offer; }
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
                    Transfer(offer, version, 100, "已发送（非保存确认）"); active = null;
                    Action<string> notice = TransferNotice; if (!disposed && notice != null) notice("文件已发送：" + current.Name);
                }
            }
            catch (IOException) { /* 对方取消或连接超时，不弹出无关用户对话框。 */ }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (UnauthorizedAccessException e) { Report("无法读取发送文件：" + e.Message); }
            finally { if (active != null) Transfer(active, version, percent, "发送中断，等待对方重试"); lock (gate) connections.Remove(client); client.Close(); }
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
            try {
                if(!LoopbackOnly&&LanNetwork.Route(links,((IPEndPoint)client.Client.RemoteEndPoint).Address)==null)return;
                client.ReceiveTimeout=5000;byte[] first=new byte[1];
                if(client.Client.Receive(first,0,1,SocketFlags.Peek)!=1)return;
                if(first[0]=='F'&&Security!=null) {
                    RequestInput prefix=new RequestInput(client);prefix.Begin(5000);
                    if(prefix.ReadByte()!='F'||prefix.ReadByte()!='Q'||prefix.ReadByte()!='2'||prefix.ReadByte()!='\n')return;
                    ServeSecure(client);
                }
                else if(Security==null||Settings.AllowLegacyProtocol)ServeFile(client);
            } catch(IOException e) {Report("安全连接未完成："+e.Message);} catch(SocketException) {} catch(System.Security.Authentication.AuthenticationException e) {Report("TLS 握手未通过："+e.GetBaseException().Message);} catch(ObjectDisposedException) {} catch(System.ComponentModel.Win32Exception e){Report("TLS 系统凭据失败："+e.NativeErrorCode+" "+e.Message);} catch(CryptographicException e){Report("TLS 证书失败："+e.Message);}
            finally {client.Close();lock(gate)connections.Remove(client);}
        }
        private TcpClient SecureConnect(IPEndPoint endpoint)
        {
            if(LoopbackOnly&&!IPAddress.IsLoopback(endpoint.Address))throw new IOException("回环模式禁止连接局域网。");
            TcpClient client=new TcpClient(AddressFamily.InterNetwork);
            lock(gate){if(disposed||connections.Count>=8){client.Close();throw new IOException("服务已关闭或连接任务过多。");}connections.Add(client);}
            try {BindClient(client,endpoint.Address);Task connecting=client.ConnectAsync(endpoint.Address,endpoint.Port);if(!connecting.Wait(8000))throw new IOException("加密连接超时。");client.ReceiveTimeout=5000;client.SendTimeout=15000;client.GetStream().Write(new byte[]{70,81,50,10},0,4);return client;}
            catch{client.Close();lock(gate)connections.Remove(client);throw;}
        }
        private void BindClient(TcpClient client,IPAddress destination)
        {
            if(!LoopbackOnly){LanNetwork.Link route=LanNetwork.Route(links,destination);if(route==null)throw new IOException("目标不在当前物理局域网内。");client.Client.Bind(new IPEndPoint(route.Address,0));client.Client.SetSocketOption(SocketOptionLevel.IP,(SocketOptionName)31,IPAddress.HostToNetworkOrder(route.Index));}
        }
        private void SendSecurePending(Pending item)
        {
            if(Interlocked.CompareExchange(ref item.Sending,1,0)!=0)return;
            TcpClient connection=null;
            try {
                using(TcpClient client=connection=SecureConnect(item.Endpoint))using(SslStream stream=Security.Client(client,item.Fingerprint)) {
                    client.ReceiveTimeout=45000;stream.ReadTimeout=45000;
                    AuthenticateSecureClient(stream,item.DeviceId,item.Fingerprint,item.Login);
                    SaveOffers();
                    SecureIdentity.WriteText(stream,"M\n"+Port);SecureIdentity.WriteFrame(stream,item.Bytes);
                    if(SecureIdentity.ReadText(stream,1024)!="OK")throw new IOException("对方未接受消息或未完成配对。");
                    lock(gate){Pending active;if(!pending.TryGetValue(item.Record.Packet,out active)||active!=item)return;pending.Remove(item.Record.Packet);}
                    Complete(item.Record,"已送达");
                }
            } catch(Exception error) { if(!(error is IOException||error is SocketException||error is System.Security.Authentication.AuthenticationException||error is AggregateException||error is ObjectDisposedException))throw;
                if(error is SecureIdentity.TrustException || error is System.Security.Authentication.AuthenticationException){lock(gate)pending.Remove(item.Record.Packet);Complete(item.Record,"身份核对未通过，未确认送达");}
                Report("加密发送未完成："+error.GetBaseException().Message); }
            finally {if(connection!=null)lock(gate)connections.Remove(connection);item.Last=DateTime.UtcNow;Interlocked.Exchange(ref item.Sending,0);}
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
                if(header[0]=="V"&&packet.Mode==Protocol.Entry){SecureIdentity.WriteText(stream,"OK");return;}
                if(header[0]!="F"||packet.Mode!=Protocol.GetFile)throw new IOException("未知安全操作。");
                string[] fields=packet.Body.Split(':');long number,offset;int fileId;
                if(fields.Length<3||!Int64.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out number)||!Int32.TryParse(fields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out fileId)||!Int64.TryParse(fields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out offset))throw new IOException("文件请求无效。");
                Offer offer;lock(gate){if(!offers.TryGetValue(number+":"+fileId,out offer))throw new IOException("文件邀请不存在或已过期。");}
                if(offer.DeviceId!=packet.DeviceId||offer.Fingerprint!=fingerprint||offer.Expires<DateTime.UtcNow||offset<0||offset>offer.Size)throw new IOException("文件授权无效。");
                FileInfo current=new FileInfo(offer.Path);if(!current.Exists||current.Length!=offer.Size||current.LastWriteTimeUtc!=offer.Modified)throw new IOException("源文件已改变。");
                long version;lock(gate)version=++offer.ProgressVersion;
                SecureIdentity.WriteText(stream,"OK\n"+offer.Size+"\n"+offer.Sha256);
                long sent=offset;TransferRate rate=new TransferRate(offset);var timing=System.Diagnostics.Stopwatch.StartNew();
                try {
                using(FileStream file=File.OpenRead(offer.Path)) {file.Position=offset;byte[] buffer=new byte[65536];while(sent<offer.Size){int count=file.Read(buffer,0,(int)Math.Min(buffer.Length,offer.Size-sent));if(count==0)throw new EndOfStreamException();stream.Write(buffer,0,count);sent+=count;lock(gate)offer.Expires=DateTime.UtcNow.AddDays(7);if(rate.Update(sent,timing.Elapsed.TotalMilliseconds))Transfer(offer,version,(int)(sent*100.0/Math.Max(1,offer.Size)),"发送中",rate.BytesPerSecond);}}
                stream.Flush();Transfer(offer,version,100,"已发送，等待校验保存");
                if(SecureIdentity.ReadText(stream,128)=="STORED\n"+offer.Sha256){Transfer(offer,version,100,"对方已校验保存");SaveOffers();}
                }catch(IOException){Transfer(offer,version,offer.Percent,"传输中断或保存确认未送达");throw;}
            }
        }
        private byte[] SecureHello() { return Protocol.Encode(0,Login,Host,Protocol.Entry|Protocol.Utf8,Protocol.Field(Settings.Nickname),null,Settings.DeviceId,null,Security.Fingerprint); }
        public void VerifyPeer(Peer peer){if(Security==null||peer.Fingerprint==null)throw new IOException("请先升级对方飞Q并刷新联系人。");using(TcpClient client=SecureConnect(peer.Endpoint))try{using(SslStream stream=Security.Client(client,peer.Fingerprint)){AuthenticateSecureClient(stream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);SecureIdentity.WriteText(stream,"V\n"+Port);SecureIdentity.WriteFrame(stream,SecureHello());if(SecureIdentity.ReadText(stream,16)!="OK")throw new IOException("身份验证未完成。");}}finally{lock(gate)connections.Remove(client);}}
        public void ForgetSecurity(Peer peer){try{Security.Forget(peer.DeviceId);}finally{lock(gate){foreach(Pending item in pending.Values.Where(p=>p.DeviceId==peer.DeviceId).ToList()){pending.Remove(item.Record.Packet);Complete(item.Record,"设备信任已撤销，未确认送达");}foreach(string key in offers.Where(o=>o.Value.DeviceId==peer.DeviceId).Select(o=>o.Key).ToList())offers.Remove(key);foreach(TcpClient client in connections)client.Close();SaveOffers();}}}
        private void AuthenticateSecureClient(SslStream stream,string id,string fingerprint,string name)
        {
            stream.ReadTimeout=45000;stream.WriteTimeout=15000;
            SecureIdentity.WriteFrame(stream,SecureHello());Packet remote;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream,5000),4096),out remote)||remote.DeviceId!=id||remote.Fingerprint!=fingerprint)throw new IOException("对方设备身份与所选联系人不一致。");
            bool accepted=Security.EnsureTrusted(id,fingerprint,name);SecureIdentity.WriteText(stream,accepted?"YES":"NO");
            if(!accepted||SecureIdentity.ReadText(new SecureIdentity.Deadline(stream,45000),16)!="YES")throw new SecureIdentity.TrustException("设备配对未通过，请在两端核对后重试。");
        }
        private Packet AuthenticateSecureServer(SslStream stream)
        {
            Packet remote;
            if(!Protocol.TryParse(SecureIdentity.ReadFrame(new SecureIdentity.Deadline(stream,5000),4096),out remote)||remote.DeviceId==null||remote.Fingerprint!=SecureIdentity.RemoteFingerprint(stream))throw new IOException("加密连接身份无效。");
            SecureIdentity.WriteFrame(stream,SecureHello());
            bool accepted=Security.EnsureTrusted(remote.DeviceId,remote.Fingerprint,remote.Body);
            bool other=SecureIdentity.ReadText(new SecureIdentity.Deadline(stream,45000),16)=="YES";
            SecureIdentity.WriteText(stream,accepted&&other?"YES":"NO");return accepted&&other?remote:null;
        }
        public void RejectFiles(Peer peer, long packet)
        {
            if(Security!=null&&peer.Fingerprint!=null){Pending item=new Pending(peer);item.Record=new ChatRecord{Packet=packet};item.Bytes=Encode(Protocol.ReleaseFiles,packet.ToString(CultureInfo.InvariantCulture),null);Task.Run(()=>{TcpClient connection=null;try{using(TcpClient client=connection=SecureConnect(item.Endpoint))using(SslStream stream=Security.Client(client,item.Fingerprint)){AuthenticateSecureClient(stream,item.DeviceId,item.Fingerprint,item.Login);SecureIdentity.WriteText(stream,"M\n"+Port);SecureIdentity.WriteFrame(stream,item.Bytes);SecureIdentity.ReadText(stream,1024);}}catch(Exception e){Report("拒绝文件通知未送达："+e.Message);}finally{if(connection!=null)lock(gate)connections.Remove(connection);}});return;}
            if(Security!=null&&(!Settings.AllowLegacyProtocol||Security.TrustedFingerprint(peer.DeviceId)!=null))throw new IOException("未允许旧版明文协议。");Send(Encode(Protocol.ReleaseFiles, packet.ToString(CultureInfo.InvariantCulture), null), peer.Endpoint);
        }
        public Task<string> ReceiveFileAsync(Peer peer, long packet, Attachment file, string destination, IProgress<int> progress, CancellationToken cancel, bool renameOnCollision = false, IProgress<TransferProgress> details = null)
        {
            if(Security!=null&&peer.Fingerprint!=null)return Task.Run(()=>ReceiveSecureFile(peer,packet,file,destination,progress,cancel,renameOnCollision,details),cancel);
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
                        if (owned && File.Exists(temporary) && (cancel.IsCancellationRequested || new FileInfo(temporary).Length == 0)) File.Delete(temporary);
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
        private string ReceiveSecureFile(Peer peer,long packet,Attachment file,string destination,IProgress<int> progress,CancellationToken cancel,bool rename,IProgress<TransferProgress> details)
        {
            if(!Protocol.SafeFileName(file.Name)||file.Size<0||!SecureIdentity.ValidFingerprint(file.Sha256))throw new IOException("文件元数据或校验值无效。");
            string path=Path.GetFullPath(destination),part=PartialPath(peer,packet,file,path);bool owned=false;TcpClient connection=null;
            SemaphoreSlim targetLock=receiveLocks[(path.ToUpperInvariant().GetHashCode()&Int32.MaxValue)%receiveLocks.Length];targetLock.Wait(cancel);
            try {
                if(!rename&&(File.Exists(path)||Directory.Exists(path)))throw new IOException("目标已存在，不会覆盖。");
                cancel.ThrowIfCancellationRequested();using(TcpClient client=connection=SecureConnect(peer.Endpoint))using(cancel.Register(()=>client.Close()))using(SslStream stream=Security.Client(client,peer.Fingerprint)) {
                    AuthenticateSecureClient(stream,peer.DeviceId,peer.Fingerprint,peer.DisplayName);
                    lock(gate)connections.Add(client);
                    try {
                        using(FileStream output=new FileStream(part,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                            owned=true;if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("断点文件不能是链接。");
                            if(output.Length>file.Size)output.SetLength(0);long offset=output.Length;output.Position=offset;
                            byte[] request=Protocol.Encode(Next(),Login,Host,Protocol.GetFile|Protocol.Utf8,packet.ToString("x")+":"+file.Id.ToString("x")+":"+offset.ToString("x")+":",null,Settings.DeviceId,null,Security.Fingerprint);
                            SecureIdentity.WriteText(stream,"F\n"+Port);SecureIdentity.WriteFrame(stream,request);
                            string[] response=SecureIdentity.ReadText(stream,256).Split('\n');
                            if(response.Length!=3||response[0]!="OK"||response[1]!=file.Size.ToString(CultureInfo.InvariantCulture)||response[2]!=file.Sha256)throw new IOException("发送方文件信息已变化。");
                            byte[] buffer=new byte[65536];long received=offset;TransferRate rate=new TransferRate(offset);var timer=System.Diagnostics.Stopwatch.StartNew();
                            while(received<file.Size){cancel.ThrowIfCancellationRequested();int count=stream.Read(buffer,0,(int)Math.Min(buffer.Length,file.Size-received));if(count==0)throw new IOException("传输中断，已保留断点。");output.Write(buffer,0,count);received+=count;bool sampled=rate.Update(received,timer.Elapsed.TotalMilliseconds);if(sampled){int percent=(int)(received*100.0/Math.Max(1,file.Size));if(progress!=null)progress.Report(percent);if(details!=null)details.Report(new TransferProgress(percent,rate.BytesPerSecond));}}
                            output.Flush(true);output.Position=0;string hash;using(SHA256 sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(output)).Replace("-","").ToLowerInvariant();
                            if(hash!=file.Sha256){output.SetLength(0);throw new IOException("文件校验失败，已清除错误断点，请重新接收。");}
                        }
                        cancel.ThrowIfCancellationRequested();string original=path;int suffix=0;
                        while(true){try{File.Move(part,path);break;}catch(IOException){if(!rename||(!File.Exists(path)&&!Directory.Exists(path))||++suffix>10000)throw;path=Path.Combine(Path.GetDirectoryName(original),Path.GetFileNameWithoutExtension(original)+" ("+suffix+")"+Path.GetExtension(original));}}
                        try{SecureIdentity.WriteText(stream,"STORED\n"+file.Sha256);}catch(IOException){Report("文件已校验保存，但保存确认未送达。");}
                        if(progress!=null)progress.Report(100);return path;
                    } finally {lock(gate)connections.Remove(client);}
                }
            } catch(Exception){if(cancel.IsCancellationRequested)throw new OperationCanceledException(cancel);throw;}
            finally {if(connection!=null)lock(gate)connections.Remove(connection);try{if(owned&&File.Exists(part)&&(cancel.IsCancellationRequested||new FileInfo(part).Length==0))File.Delete(part);}finally{targetLock.Release();}}
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
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged; NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
            if (timer != null) timer.Dispose();
            if (udp != null) udp.Close();
            if (tcp != null) tcp.Stop();
            lock (gate)
            {
                foreach (TcpClient client in connections) client.Close(); offers.Clear();
                // 即使 UI 已销毁，持久层仍持有原记录，可在退出时保存明确终态。
                foreach (Pending item in pending.Values) item.Record.State = "已退出，未确认送达";
                pending.Clear();
            }
            if(Security!=null)Security.Dispose();
        }
    }
}
