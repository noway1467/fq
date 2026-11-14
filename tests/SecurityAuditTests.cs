using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FeiqLight.Tests
{
    internal static class SecurityAuditTests
    {
        private const string Identity = "0123456789abcdef0123456789abcdef";
        private static readonly IPAddress Receiver = IPAddress.Parse("127.0.0.2"), Attacker = IPAddress.Parse("127.0.0.3");
        private static bool baseline;
        private static string root;
        private static int passed, failed;
        private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        private static void Await(Func<bool> condition, string reason)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < 2500) Thread.Sleep(10);
            Check(condition(), reason);
        }
        private static int Port()
        {
            TcpListener tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            try { int port = ((IPEndPoint)tcp.LocalEndpoint).Port; using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port))) return port; }
            finally { tcp.Stop(); }
        }
        private static LanService Node()
        {
            LanService node = new LanService(new AppSettings { Nickname = "漏洞验证", Notifications = false, AutoReceiveFiles = false, ReceiveFolder = root }, Port(), true, "audit-local");
            node.Start(); return node;
        }
        private static void Send(UdpClient socket, int port, uint mode, string body, string login)
        {
            byte[] bytes = Protocol.Encode(900, login, "audit-host", mode | Protocol.Utf8, body, null, Identity);
            socket.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        }
        private static Packet Message(UdpClient socket, long number, int timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeout)
            {
                socket.Client.ReceiveTimeout = Math.Max(1, timeout - (int)clock.ElapsedMilliseconds);
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try { Packet packet; if (Protocol.TryParse(socket.Receive(ref from), out packet) && packet.Mode == Protocol.SendMessage && packet.Number == number) return packet; }
                catch (SocketException e) { if (e.SocketErrorCode != SocketError.TimedOut) throw; return null; }
            }
            return null;
        }
        private static byte[] Download(int port, long packet, IPAddress source)
        {
            try
            {
                using (TcpClient socket = new TcpClient(new IPEndPoint(source, 0)))
                {
                    socket.Connect(IPAddress.Loopback, port); socket.ReceiveTimeout = 1500; socket.SendTimeout = 1500;
                    byte[] request = Protocol.Encode(1, "peer", "audit-host", Protocol.GetFile | Protocol.Utf8, packet.ToString("x") + ":0:0:", null);
                    NetworkStream stream = socket.GetStream(); stream.Write(request, 0, request.Length);
                    using (MemoryStream result = new MemoryStream()) { stream.CopyTo(result); return result.ToArray(); }
                }
            }
            catch (IOException) { return new byte[0]; }
            catch (SocketException) { return new byte[0]; }
        }
        private static bool HttpAvailable(int port)
        {
            try
            {
                using (TcpClient socket = new TcpClient())
                {
                    socket.Connect(IPAddress.Loopback, port); socket.ReceiveTimeout = 1500; socket.SendTimeout = 1500;
                    byte[] request = Encoding.ASCII.GetBytes("GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nConnection: close\r\n\r\n");
                    NetworkStream stream = socket.GetStream(); stream.Write(request, 0, request.Length);
                    using (StreamReader reader = new StreamReader(stream, Encoding.ASCII)) return (reader.ReadLine() ?? "").StartsWith("HTTP/1.1 200 ");
                }
            }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
        }
        private sealed class SlowClients : IDisposable
        {
            private readonly List<TcpClient> clients = new List<TcpClient>();
            private readonly Thread drip;
            private volatile bool stopped;
            internal SlowClients(int port, int count, bool http)
            {
                for (int i = 0; i < count; i++)
                {
                    TcpClient socket = new TcpClient(new IPEndPoint(Attacker, 0)); clients.Add(socket);
                    socket.Connect(IPAddress.Loopback, port); socket.SendTimeout = 500;
                    string prefix = !http ? "1:" : i % 2 == 0 ? "GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nX-Audit: " : "POST /api/login HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json\r\nContent-Length: 16384\r\n\r\n";
                    byte[] bytes = Encoding.ASCII.GetBytes(prefix); socket.GetStream().Write(bytes, 0, bytes.Length);
                }
                // 不补建连接，验证同一批回环连接能否长期占位。
                drip = new Thread(() => {
                    while (!stopped)
                    {
                        foreach (TcpClient socket in clients) try { socket.GetStream().WriteByte((byte)'x'); } catch (IOException) { } catch (InvalidOperationException) { }
                        Thread.Sleep(200);
                    }
                });
                drip.IsBackground = true; drip.Start();
            }
            public void Dispose() { stopped = true; foreach (TcpClient socket in clients) socket.Close(); Check(drip.Join(2000), "慢连接线程未退出"); }
        }
        private static void SpoofRetry()
        {
            byte[] content = Encoding.UTF8.GetBytes("仅原接收者可下载的测试文件");
            string file = Path.Combine(root, "authorized.bin"); File.WriteAllBytes(file, content);
            using (LanService node = Node())
            using (UdpClient receiver = new UdpClient(new IPEndPoint(Receiver, 0)))
            using (UdpClient attacker = new UdpClient(new IPEndPoint(Attacker, 0)))
            {
                Send(receiver, node.Port, Protocol.AnswerEntry, "原接收者", "peer"); Await(() => node.Peers.Count == 1, "未收到真实发现包");
                Peer peer = node.Peers.Single(); ChatRecord record = node.SendMessage(peer, "AUDIT-PRIVATE-MESSAGE", new[] { file });
                Check(Message(receiver, record.Packet, 1500) != null, "原接收者没有收到首发消息");
                Send(attacker, node.Port, Protocol.AnswerEntry, "冒用身份", "impostor");
                Await(() => peer.Endpoint.Equals(attacker.Client.LocalEndPoint) && peer.Login == "impostor", "没有走到实际身份更新路径");
                Packet leaked = Message(attacker, record.Packet, 3400);
                Send(attacker, node.Port, Protocol.ReceiveMessage, record.Packet.ToString(), "impostor"); Thread.Sleep(250);
                bool forgedAck = record.State == "已送达";
                Console.WriteLine("EVIDENCE retry_leaked=" + (leaked != null) + " forged_ack_accepted=" + forgedAck);
                Check((leaked != null) == baseline && forgedAck == baseline, "重发泄露或伪造 ACK 结果与预期不符");
                if (!baseline)
                {
                    Check(Message(receiver, record.Packet, 500) != null, "固定接收地址后没有正常重发");
                    Send(receiver, node.Port, Protocol.ReceiveMessage, record.Packet.ToString(), "peer"); Await(() => record.State == "已送达", "原接收者的有效 ACK 被拒绝");
                }
                Check(Download(node.Port, record.Packet, Attacker).Length == 0, "冒用设备身份获得了附件下载权限");
                Check(Download(node.Port, record.Packet, Receiver).SequenceEqual(content), "原接收者失去附件权限");
            }
        }
        private static void SlowFile()
        {
            byte[] content = Encoding.UTF8.GetBytes("slow-file-authorized"); string file = Path.Combine(root, "slow.bin"); File.WriteAllBytes(file, content);
            using (LanService node = Node())
            using (UdpClient receiver = new UdpClient(new IPEndPoint(Receiver, 0)))
            {
                Send(receiver, node.Port, Protocol.AnswerEntry, "原接收者", "peer"); Await(() => node.Peers.Count == 1, "未收到发现包");
                ChatRecord record = node.SendMessage(node.Peers.Single(), "文件占位检查", new[] { file });
                Check(Download(node.Port, record.Packet, Receiver).SequenceEqual(content), "攻击前合法下载失败");
                using (SlowClients slow = new SlowClients(node.Port, 8, false))
                {
                    Thread.Sleep(700); Check(Download(node.Port, record.Packet, Receiver).Length == 0, "没有成功填满附件服务名额");
                    Thread.Sleep(5600); bool recovered = Download(node.Port, record.Packet, Receiver).SequenceEqual(content);
                    Console.WriteLine("EVIDENCE file_recovered_while_dripping=" + recovered);
                    Check(recovered != baseline, "慢附件请求总时限未生效或基线未复现");
                }
                Await(() => Download(node.Port, record.Packet, Receiver).SequenceEqual(content), "攻击连接关闭后文件服务未恢复");
            }
        }
        private static void SlowHttp()
        {
            using (ToolsService server = new ToolsService(0, true, Path.Combine(root, "http")))
            {
                Check(HttpAvailable(server.Port), "攻击前 HTTP 服务不可用");
                using (SlowClients slow = new SlowClients(server.Port, 12, true))
                {
                    Thread.Sleep(700); Check(!HttpAvailable(server.Port), "没有成功填满 HTTP 服务名额");
                    Thread.Sleep(5600); bool recovered = HttpAvailable(server.Port);
                    Console.WriteLine("EVIDENCE http_recovered_while_dripping=" + recovered);
                    Check(recovered != baseline, "HTTP 请求头和正文总时限未生效或基线未复现");
                }
                Await(() => HttpAvailable(server.Port), "攻击连接关闭后 HTTP 服务未恢复");
            }
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        public static int Main(string[] args)
        {
            root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); baseline = args.Length > 1 && args[1] == "baseline";
            ThreadPool.SetMinThreads(32, 32);
            Run("冒用设备 ID 的重发截获、ACK 与附件权限", SpoofRetry);
            Run("8 个未认证慢附件请求与合法下载", SlowFile);
            Run("12 个慢 HTTP 请求头/正文与合法请求", SlowHttp);
            Console.WriteLine("RESULT Windows mode=" + (baseline ? "baseline" : "fixed") + " passed=" + passed + " failed=" + failed);
            return failed == 0 ? 0 : 1;
        }
    }
}
