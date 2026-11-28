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
    public sealed partial class LanService : IDisposable
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
            public DateTime Expires;
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
        public string NetworkStatus {
            get {
                return LoopbackOnly?"回环测试":links.Count==0?"网络已断开，等待 Wi-Fi、热点或以太网恢复":"物理局域网 · "+String.Join("、",links.Select(l=>l.Address.ToString()))+" · UDP/TCP "+Port;
            }
        }
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
        private const int CompletedOfferCache = 1000, MaxStoredOffers = AppSettings.MaxFileLimit + CompletedOfferCache + 8, OfferJsonLimit = 64 * 1024 * 1024;
        public sealed class StoredOffer
        {
            public string Key, Path, DeviceId, Fingerprint, Sha256;
            public long Size, ModifiedTicks, ExpiresTicks;
            public int FileId;
            public ChatRecord Record;
            public string TransferState;
            public int Percent;
        }
        public event Action NetworkReset;

        public event Action PeersChanged;
        public event Action<IncomingMessage> MessageReceived;
        public event Action<Peer, long> OfferCanceled;
        public event Action<ChatRecord> DeliveryChanged;
        public event Action<string> Error;
        public event Action ConnectionInterrupted;
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

        public List<Peer> Peers { get { lock (gate) return peers.Values.OrderByDescending(p => p.Online).ThenBy(p => p.Nickname).ToList(); } }

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

        private void Report(string message) { Action<string> handler = Error; if (!disposed && handler != null) handler(message); }
        private void ReportConnectionInterrupted() { Action handler = ConnectionInterrupted; if (!disposed && handler != null) handler(); }
        private void Changed() { Action handler = PeersChanged; if (!disposed && handler != null) handler(); }

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
