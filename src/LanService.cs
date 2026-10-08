using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FeiqLight
{
    public sealed class LanService : IDisposable
    {
        private sealed class Pending
        {
            public Peer Peer; public byte[] Bytes; public ChatRecord Record;
            public DateTime Last; public int Attempts;
        }
        private sealed class Offer
        {
            public string Path; public long Size; public DateTime Modified;
            public string Address; public DateTime Expires;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        private readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        private readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();
        private readonly Dictionary<string, Offer> offers = new Dictionary<string, Offer>();
        private readonly HashSet<TcpClient> connections = new HashSet<TcpClient>();
        private static readonly SemaphoreSlim[] receiveLocks = Enumerable.Range(0, 32).Select(i => new SemaphoreSlim(1, 1)).ToArray();
        private readonly HashSet<IPAddress> localAddresses;
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
        public event Action PeersChanged;
        public event Action<IncomingMessage> MessageReceived;
        public event Action<ChatRecord> DeliveryChanged;
        public event Action<string> Error;
        public event Action<string> TransferNotice;

        public LanService(AppSettings settings, int port, bool loopbackOnly, string login)
        {
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port", "端口必须在 1024–65535 之间。");
            Settings = settings; Port = port; LoopbackOnly = loopbackOnly;
            Login = Protocol.Field(login ?? Environment.UserName); Host = Protocol.Field(Environment.MachineName);
            localAddresses = new HashSet<IPAddress>(LocalIPv4()); localAddresses.Add(IPAddress.Loopback);
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
            List<IPAddress> result = new List<IPAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                if (adapter.OperationalStatus == OperationalStatus.Up)
                    foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address)) result.Add(address.Address);
            return result;
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
                timer = new Timer(Tick, null, 1000, 1000);
                Refresh();
            }
            catch { Dispose(); throw; }
        }
        private long Next() { return Interlocked.Increment(ref sequence); }
        private byte[] Encode(uint command, string body, string extra) { return Protocol.Encode(Next(), Login, Host, command | Protocol.Utf8, body, extra); }
        private void Send(byte[] bytes, IPEndPoint endpoint)
        {
            if (disposed) throw new ObjectDisposedException("LanService");
            if (LoopbackOnly && !IPAddress.IsLoopback(endpoint.Address)) throw new InvalidOperationException("回环测试模式不能连接局域网。");
            udp.Send(bytes, bytes.Length, endpoint);
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
            lastRefresh = DateTime.UtcNow;
            if (!LoopbackOnly)
            {
                HashSet<IPAddress> broadcast = new HashSet<IPAddress>(); broadcast.Add(IPAddress.Broadcast);
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation item in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (item.Address.AddressFamily != AddressFamily.InterNetwork || item.IPv4Mask == null) continue;
                        byte[] ip = item.Address.GetAddressBytes(), mask = item.IPv4Mask.GetAddressBytes();
                        for (int i = 0; i < 4; i++) ip[i] |= (byte)~mask[i];
                        broadcast.Add(new IPAddress(ip));
                    }
                }
                foreach (IPAddress address in broadcast)
                    try { Probe(new IPEndPoint(address, Port)); } catch (SocketException e) { Report("广播失败：" + e.Message); }
            }
            // 单播补充广播；只有收到真实报文才置为在线，不把历史地址伪装成在线联系人。
            IEnumerable<IPEndPoint> targets = Peers.Select(p => p.Endpoint).Concat(AppSettings.NormalizeKnownPeers(Settings.KnownPeers).Select(AppSettings.ParseKnownPeer)).Distinct();
            foreach (IPEndPoint target in targets)
            {
                if (LoopbackOnly && !IPAddress.IsLoopback(target.Address)) continue;
                try { Probe(target); } catch (SocketException e) { Report("刷新联系人失败：" + e.Message); }
            }
        }
        private Peer Upsert(Packet packet, IPEndPoint endpoint)
        {
            string id = endpoint + "/" + packet.Login; Peer peer;
            bool entry = packet.Mode == Protocol.Entry || packet.Mode == Protocol.AnswerEntry || packet.Mode == Protocol.Absence;
            lock (gate)
            {
                if (!peers.TryGetValue(id, out peer))
                {
                    if (peers.Count >= 2000) return null;
                    peer = new Peer { Endpoint = endpoint, Login = packet.Login, Host = packet.Host, Nickname = packet.Login, Group = "我的局域网" };
                    peers[id] = peer;
                }
                if (entry)
                {
                    peer.Nickname = String.IsNullOrWhiteSpace(packet.Body) ? packet.Login : Protocol.Prefix(packet.Body, 128);
                    peer.Group = String.IsNullOrWhiteSpace(packet.Extra) ? "我的局域网" : Protocol.Prefix(packet.Extra, 128);
                }
                peer.Utf8 = peer.Utf8 || (packet.Command & (Protocol.Utf8 | Protocol.CapUtf8)) != 0;
                peer.Online = packet.Mode != Protocol.Exit; peer.LastSeen = DateTime.UtcNow;
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
                    if (!Protocol.TryParse(bytes, out packet)) continue;
                    if (remote.Port == Port && localAddresses.Contains(remote.Address) && packet.Login == Login && packet.Host == Host) continue;
                    Handle(packet, remote);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e) { if (!disposed) { Report("网络接收错误：" + e.Message); Thread.Sleep(100); } }
                catch (Exception e) { if (!disposed) Report("消息处理错误：" + e.Message); }
            }
        }
        private void Handle(Packet packet, IPEndPoint remote)
        {
            if (packet.Mode == Protocol.ReceiveMessage)
            {
                long number; Pending item = null;
                if (!Int64.TryParse(packet.Body, out number)) return;
                lock (gate)
                {
                    Pending candidate;
                    if (pending.TryGetValue(number, out candidate) && candidate.Peer.Endpoint.Equals(remote) && candidate.Peer.Login == packet.Login)
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
                    foreach (string key in offers.Where(p => p.Key.StartsWith(messageId + ":", StringComparison.Ordinal) && p.Value.Address == remote.Address.ToString()).Select(p => p.Key).ToList()) offers.Remove(key);
                return;
            }
            if (packet.Mode != Protocol.Entry && packet.Mode != Protocol.AnswerEntry && packet.Mode != Protocol.Absence && packet.Mode != Protocol.Exit && packet.Mode != Protocol.SendMessage) return;
            Peer peer = Upsert(packet, remote); if (peer == null) return;
            if (packet.Mode == Protocol.Entry)
            {
                uint flags = Protocol.AnswerEntry | Protocol.CapUtf8 | Protocol.FileAttach | (peer.Utf8 ? Protocol.Utf8 : 0);
                Send(Encode(flags, Protocol.Field(Settings.Nickname), Protocol.Field(Settings.Group)), remote);
            }
            if (packet.Mode != Protocol.SendMessage) return;
            if ((packet.Command & Protocol.SendCheck) != 0)
                Send(Encode(Protocol.ReceiveMessage, packet.Number.ToString(CultureInfo.InvariantCulture), null), remote);
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
        public ChatRecord SendMessage(Peer peer, string text, IList<string> paths)
        {
            if (String.IsNullOrWhiteSpace(text) && (paths == null || paths.Count == 0)) throw new ArgumentException("请先输入消息或选择文件。");
            if ((text ?? "").IndexOf('\0') >= 0) throw new ArgumentException("消息不能包含空字符。");
            if (paths != null && paths.Count > 20) throw new ArgumentException("一次最多发送 20 个文件。");
            long number = Next(); List<Attachment> files = new List<Attachment>(); Dictionary<string, Offer> additions = new Dictionary<string, Offer>();
            if (paths != null)
            {
                int id = 0;
                foreach (string path in paths)
                {
                    FileInfo info = new FileInfo(Path.GetFullPath(path));
                    if (!info.Exists || !Protocol.SafeFileName(info.Name)) throw new IOException("文件不存在、文件名不安全或过长：" + info.Name);
                    Attachment file = new Attachment { Id = id++, Name = info.Name, Size = info.Length, Modified = (long)(info.LastWriteTimeUtc - new DateTime(1970, 1, 1)).TotalSeconds };
                    files.Add(file);
                    additions[number + ":" + file.Id] = new Offer { Path = info.FullName, Size = info.Length, Modified = info.LastWriteTimeUtc, Address = peer.Endpoint.Address.ToString(), Expires = DateTime.UtcNow.AddMinutes(30) };
                }
            }
            uint command = Protocol.SendMessage | Protocol.SendCheck | (peer.Utf8 ? Protocol.Utf8 : 0) | (files.Count > 0 ? Protocol.FileAttach : 0);
            byte[] bytes = Protocol.Encode(number, Login, Host, command, text, files.Count > 0 ? Protocol.FileList(files) : null);
            string display = text ?? "";
            if (files.Count > 0) display += (display.Length > 0 ? "\n" : "") + "[文件] " + String.Join("、", files.Select(f => f.Name));
            ChatRecord record = new ChatRecord { Packet = number, PeerId = peer.Id, Sender = Settings.Nickname, Text = display, Time = DateTime.Now, Outgoing = true, State = "等待确认", LocalFiles = paths == null ? null : paths.Select(Path.GetFullPath).ToArray() };
            lock (gate)
            {
                if (pending.Count >= 256 || offers.Count + additions.Count > 1000) throw new InvalidOperationException("待处理消息或文件过多，请稍后再试。");
                foreach (var item in additions) offers[item.Key] = item.Value;
                pending[number] = new Pending { Peer = peer, Bytes = bytes, Record = record, Last = DateTime.UtcNow, Attempts = 1 };
            }
            try { Send(bytes, peer.Endpoint); }
            catch
            {
                lock (gate) { pending.Remove(number); foreach (string key in additions.Keys) offers.Remove(key); }
                throw;
            }
            return record;
        }
        private void Tick(object ignored)
        {
            if (disposed) return;
            try
            {
                List<Pending> retry = new List<Pending>(), failed = new List<Pending>(); bool changed = false;
                lock (gate)
                {
                    DateTime now = DateTime.UtcNow;
                    foreach (Pending item in pending.Values.ToList())
                    {
                        if ((now - item.Last).TotalSeconds < 2) continue;
                        if (item.Attempts >= 4) { pending.Remove(item.Record.Packet); failed.Add(item); }
                        else { item.Attempts++; item.Last = now; retry.Add(item); }
                    }
                    foreach (string key in seen.Where(p => (now - p.Value).TotalMinutes > 10).Select(p => p.Key).ToList()) seen.Remove(key);
                    foreach (string key in offers.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList()) offers.Remove(key);
                    foreach (Peer peer in peers.Values) if (peer.Online && (now - peer.LastSeen).TotalSeconds > 120) { peer.Online = false; changed = true; }
                }
                foreach (Pending item in retry) try { Send(item.Bytes, item.Peer.Endpoint); } catch (SocketException) { }
                foreach (Pending item in failed) Complete(item.Record, "未确认送达，请检查网络后重发");
                if (changed) Changed();
                if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 30) Refresh();
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
                    Task.Run(() => ServeFile(client));
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException e) { if (!disposed) Report("文件服务错误：" + e.Message); }
            }
        }
        private void ServeFile(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 5000; client.SendTimeout = 15000;
                using (NetworkStream stream = client.GetStream())
                {
                    List<byte> request = new List<byte>(); int b;
                    while (request.Count < 4096 && (b = stream.ReadByte()) > 0) request.Add((byte)b);
                    Packet packet;
                    if (request.Count >= 4096 || !Protocol.TryParse(request.ToArray(), out packet) || packet.Mode != Protocol.GetFile) return;
                    string[] fields = packet.Body.Split(':'); long messageId, offset; int fileId;
                    if (fields.Length < 3 || !Int64.TryParse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out messageId) || !Int32.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out fileId) || !Int64.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset)) return;
                    Offer offer;
                    lock (gate) if (!offers.TryGetValue(messageId + ":" + fileId, out offer)) return;
                    if (offer.Expires < DateTime.UtcNow || offer.Address != ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString() || offset < 0 || offset > offer.Size) return;
                    FileInfo current = new FileInfo(offer.Path);
                    if (!current.Exists || current.Length != offer.Size || current.LastWriteTimeUtc != offer.Modified) return;
                    using (FileStream file = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                    {
                        file.Position = offset; byte[] buffer = new byte[65536]; int count;
                        while (!disposed && (count = file.Read(buffer, 0, buffer.Length)) > 0) { stream.Write(buffer, 0, count); lock (gate) offer.Expires = DateTime.UtcNow.AddMinutes(30); }
                    }
                    Action<string> notice = TransferNotice; if (!disposed && notice != null) notice("文件已发送：" + current.Name);
                }
            }
            catch (IOException) { /* 对方取消或连接超时，不弹出无关用户对话框。 */ }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (UnauthorizedAccessException e) { Report("无法读取发送文件：" + e.Message); }
            finally { lock (gate) connections.Remove(client); client.Close(); }
        }
        public void RejectFiles(Peer peer, long packet)
        {
            Send(Encode(Protocol.ReleaseFiles, packet.ToString(CultureInfo.InvariantCulture), null), peer.Endpoint);
        }
        public Task<string> ReceiveFileAsync(Peer peer, long packet, Attachment file, string destination, IProgress<int> progress, CancellationToken cancel, bool renameOnCollision = false)
        {
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
                                Task connecting = client.ConnectAsync(peer.Endpoint.Address, peer.Endpoint.Port);
                                if (!connecting.Wait(8000)) throw new IOException("连接超时，请确认对方在线且允许 TCP 端口。");
                                client.ReceiveTimeout = 15000; client.SendTimeout = 5000;
                                using (NetworkStream stream = client.GetStream())
                                {
                                    byte[] request = Encode(Protocol.GetFile, packet.ToString("x") + ":" + file.Id.ToString("x") + ":" + offset.ToString("x") + ":", null);
                                    stream.Write(request, 0, request.Length);
                                    byte[] buffer = new byte[65536]; long received = offset; int last = -1;
                                    if (progress != null) progress.Report(file.Size == 0 ? 0 : (int)(received * 100.0 / file.Size));
                                    while (received < file.Size)
                                    {
                                        cancel.ThrowIfCancellationRequested();
                                        int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, file.Size - received));
                                        if (count == 0) throw new IOException("传输中断或文件邀请失效；已保留断点，请在发送方在线且邀请有效时重试。");
                                        output.Write(buffer, 0, count); received += count;
                                        int percent = (int)(received * 100.0 / file.Size);
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
        public void Dispose()
        {
            if (disposed) return;
            if (udp != null)
            {
                foreach (Peer peer in Peers)
                    try { Send(Encode(Protocol.Exit, Settings.Nickname, null), peer.Endpoint); } catch (Exception) { }
            }
            disposed = true;
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
        }
    }
}
