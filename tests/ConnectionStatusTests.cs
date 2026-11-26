using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class ConnectionStatusTests
    {
        private static string root, previews;
        private static int passed, failed;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static object Field(object owner, string name) { return owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
        private static void Pump(Func<bool> done, string message)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < 8000) { Application.DoEvents(); Thread.Sleep(10); }
            Application.DoEvents(); Check(done(), message);
        }
        private static int FreePort()
        {
            TcpListener tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start();
            try { int port = ((IPEndPoint)tcp.LocalEndpoint).Port; using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port))) return port; }
            finally { tcp.Stop(); }
        }
        private static LanService Node(string folder, string name)
        {
            var settings = new AppSettings { Nickname = name, Notifications = false, DiagnosticsEnabled = true, ReceiveFolder = Path.Combine(folder, "received") };
            var node = new LanService(settings, FreePort(), true, name);
            node.HeartbeatRetry = TimeSpan.FromMilliseconds(200);
            node.HeartbeatIdle = TimeSpan.FromHours(1);
            node.ConfigureSecurity(folder, (peer, code) => true);
            node.Start(); return node;
        }
        private sealed class Pair : IDisposable
        {
            internal readonly LanService Receiver, Sender;
            internal readonly MainForm Main;
            internal readonly LocalStore Store;
            internal readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
            internal readonly Peer Target, Back;
            internal readonly Label Status;
            internal int Received;
            internal Pair(string name)
            {
                string folder = Path.Combine(root, name);
                Store = new LocalStore(Path.Combine(folder, "receiver"));
                Receiver = Node(Store.Root, "Windows 状态检查");
                Sender = Node(Path.Combine(folder, "sender"), "Android VPN 长名称 / 测试设备");
                Main = new MainForm(Receiver, Store);
                Main.Show(); Main.NetworkStarted();
                Status = (Label)Field(Main, "status");
                Receiver.Error += Errors.Enqueue;
                Receiver.MessageReceived += message => Interlocked.Increment(ref Received);
                Sender.Probe(new IPEndPoint(IPAddress.Loopback, Receiver.Port));
                Pump(() => Receiver.Peers.Count == 1 && Sender.Peers.Count == 1, "发现未完成");
                Target = Sender.Peers.Single(); Back = Receiver.Peers.Single();
                Sender.VerifyPeer(Target);
                Pump(() => Idle() && Status.Text == "1 人在线 · 1 个会话", "在线状态未就绪：" + Status.Text);
            }
            internal bool Idle() { lock (Field(Receiver, "gate")) return ((HashSet<TcpClient>)Field(Receiver, "connections")).Count == 0; }
            public void Dispose() { Sender.Dispose(); Main.ExitApplication(); Main.Dispose(); Receiver.Dispose(); Application.DoEvents(); }
        }
        private static byte[] Hello(LanService node, string fingerprint)
        {
            return Protocol.Encode(0, node.Login, node.Host, Protocol.Entry | Protocol.Utf8, node.Settings.Nickname, node.Settings.Group, node.Settings.DeviceId, null, fingerprint);
        }
        private static SslStream Authenticate(Pair pair, TcpClient raw)
        {
            raw.Connect(IPAddress.Loopback, pair.Receiver.Port);
            raw.GetStream().Write(new byte[] { 70, 81, 50, 10 }, 0, 4);
            SslStream tls = pair.Sender.Security.Client(raw, pair.Receiver.Security.Fingerprint);
            tls.ReadTimeout = 5000; tls.WriteTimeout = 5000;
            SecureIdentity.WriteFrame(tls, Hello(pair.Sender, pair.Sender.Security.Fingerprint));
            SecureIdentity.ReadFrame(tls, 4096);
            SecureIdentity.WriteText(tls, "YES");
            Check(SecureIdentity.ReadText(tls, 16) == "YES", "真实 TLS 身份核对失败");
            return tls;
        }
        private static void Interrupt(Pair pair, bool reset)
        {
            using (TcpClient raw = new TcpClient())
            using (SslStream tls = Authenticate(pair, raw))
            {
                // 模拟切网关闭旧连接；正常同步和直连抢占后也会在业务头之前结束连接。
                if (reset) { raw.Client.LingerState = new LingerOption(true, 0); raw.Close(); }
            }
            Pump(pair.Idle, "中断的连接没有释放");
        }
        private static void Render(Pair pair, string name)
        {
            pair.Main.Refresh(); Application.DoEvents();
            using (Bitmap image = new Bitmap(pair.Main.Width, pair.Main.Height))
            {
                pair.Main.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(previews, name + ".png"), ImageFormat.Png);
            }
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            root = Path.GetFullPath(args[0]); previews = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(root); Directory.CreateDirectory(previews);
            Run("TLS 握手中断流不残留安全连接错误", () => {
                using (Pair pair = new Pair("handshake-eof"))
                {
                    using (TcpClient raw = new TcpClient())
                    {
                        raw.Connect(IPAddress.Loopback, pair.Receiver.Port);
                        raw.GetStream().Write(new byte[] { 70, 81, 50, 10 }, 0, 4);
                        Pump(() => !pair.Idle(), "握手请求没有被接受");
                        raw.Client.Shutdown(SocketShutdown.Send);
                        Pump(pair.Idle, "握手中断的连接没有释放");
                    }
                    Check(pair.Errors.IsEmpty && pair.Status.Text == "1 人在线 · 1 个会话", "握手 EOF 仍覆盖状态：" + pair.Status.Text);
                    Check(new Diagnostics(pair.Store.Root, () => true).Read().Contains("ConnectionInterrupted"), "握手中断未记录诊断");
                    Render(pair, "handshake-eof");
                }
            });
            Run("慢刷新不阻塞界面，连续点击和菜单入口共用一次请求", () => {
                using (Pair pair = new Pair("refresh-responsive"))
                using (var held = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    // 用回环发送锁模拟系统网络阻塞，不扫描宿主网卡或等待真实设备。
                    Task blocker = Task.Run(() => { lock (Field(pair.Receiver, "sendGate")) { held.Set(); release.Wait(4000); } });
                    Check(held.Wait(2000), "测试发送锁没有就绪");
                    var button = (Button)Field(pair.Main, "refresh");
                    MethodInfo refresh = typeof(MainForm).GetMethod("RefreshNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
                    TimeSpan before = (TimeSpan)Field(pair.Receiver, "verifyFrom");
                    try
                    {
                        pair.Status.Text = "安全连接未完成：旧提示";
                        Stopwatch elapsed = Stopwatch.StartNew(); button.PerformClick(); elapsed.Stop();
                        Check(elapsed.ElapsedMilliseconds < 500, "刷新阻塞界面 " + elapsed.ElapsedMilliseconds + "ms");
                        Pump(() => (TimeSpan)Field(pair.Receiver, "verifyFrom") > before, "后台刷新未启动");
                        TimeSpan first = (TimeSpan)Field(pair.Receiver, "verifyFrom");
                        for (int i = 0; i < 30; i++) { button.PerformClick(); refresh.Invoke(pair.Main, null); }
                        bool responded = false; pair.Main.BeginInvoke((MethodInvoker)(() => responded = true));
                        Pump(() => responded, "刷新期间 UI 消息无法处理");
                        Check(!button.Enabled && (TimeSpan)Field(pair.Receiver, "verifyFrom") == first, "重复点击排入了新的刷新");
                        Console.WriteLine("METRIC 刷新点击返回 " + elapsed.ElapsedMilliseconds + "ms；30 次连续调用未新增刷新；UI 回调已执行");
                        Render(pair, "refresh-pending");
                    }
                    finally { release.Set(); Check(blocker.Wait(2000), "测试发送锁未释放"); }
                    Pump(() => button.Enabled, "刷新完成后按钮未恢复");
                    Check(pair.Status.Text == "1 人在线 · 1 个会话", "刷新结束仍残留错误：" + pair.Status.Text);
                    Render(pair, "refresh-complete");
                    // 完成后允许下一次刷新，避免节流把入口永久锁死。
                    before = (TimeSpan)Field(pair.Receiver, "verifyFrom"); button.PerformClick();
                    Pump(() => button.Enabled && (TimeSpan)Field(pair.Receiver, "verifyFrom") > before, "完成后不能再次刷新");
                }
            });
            foreach (bool reset in new[] { false, true })
            {
                string name = reset ? "reset-dark" : "eof-light";
                Run("已认证连接" + (reset ? "被重置" : "正常关闭") + "不覆盖在线状态", () => {
                    Theme.Change(reset, "blue");
                    using (Pair pair = new Pair(name))
                    {
                        Interrupt(pair, reset);
                        Render(pair, name);
                        Check(pair.Back.Online, "单条连接关闭误判联系人离线");
                        Check(pair.Status.Text == "1 人在线 · 1 个会话", "在线时残留连接错误：" + pair.Status.Text);
                        Check(pair.Errors.IsEmpty, "临时中断进入全局错误通道");
                        string diagnostics = new Diagnostics(pair.Store.Root, () => true).Read();
                        Check(diagnostics.Contains("ConnectionInterrupted"), "诊断没有记录连接中断");
                        pair.Receiver.Probe(new IPEndPoint(IPAddress.Loopback, pair.Sender.Port));
                        Pump(() => pair.Idle() && pair.Status.Text == "1 人在线 · 1 个会话", "探测后在线状态未保持");
                        Check(pair.Received == 0, "测试意外依赖业务消息恢复状态");
                        ChatRecord sent = pair.Sender.SendMessage(pair.Target, "中断后正常发送 中文 / English", null);
                        Pump(() => sent.State == "已送达" && pair.Received == 1, "中断后正常消息无法送达");
                    }
                });
            }
            Run("发现连接读取中被重置不覆盖在线状态", () => {
                using (Pair pair = new Pair("discovery-reset"))
                {
                    using (TcpClient raw = new TcpClient())
                    {
                        raw.Connect(IPAddress.Loopback, pair.Receiver.Port);
                        // 请求只送到帧长度，接收方阻塞读取正文时复现底层 SocketException。
                        byte[] prefix = { 70, 81, 68, 10, 0, 0, 4, 0 };
                        raw.GetStream().Write(prefix, 0, prefix.Length);
                        Pump(() => !pair.Idle(), "发现请求没有被接受");
                        raw.Client.LingerState = new LingerOption(true, 0);
                        raw.Close();
                    }
                    Pump(pair.Idle, "被重置的发现连接没有释放");
                    Check(pair.Errors.IsEmpty && pair.Status.Text == "1 人在线 · 1 个会话", "底层重置仍覆盖在线状态：" + pair.Status.Text);
                    Check(new Diagnostics(pair.Store.Root, () => true).Read().Contains("ConnectionInterrupted"), "重置未记入诊断");
                }
            });
            Run("非法安全报文仍显示真实错误", () => {
                using (Pair pair = new Pair("invalid-identity"))
                {
                    using (TcpClient raw = new TcpClient())
                    using (SslStream tls = Authenticate(pair, raw))
                    {
                        SecureIdentity.WriteText(tls, "V\n" + pair.Sender.Port);
                        SecureIdentity.WriteFrame(tls, Hello(pair.Sender, new string('f', 64)));
                        Pump(() => !pair.Errors.IsEmpty, "非法报文错误被隐藏");
                    }
                    Check(pair.Status.Text.Contains("报文身份与证书不一致"), "身份错误提示丢失：" + pair.Status.Text);
                    Check(!new Diagnostics(pair.Store.Root, () => true).Read().Contains("ConnectionInterrupted"), "身份错误被错误归类为临时断线");
                }
            });
            Run("真实离线与重启恢复更新状态且保留信任", () => {
                Theme.Change(false, "blue");
                using (Pair pair = new Pair("restart"))
                {
                    string senderRoot = Path.Combine(root, "restart", "sender"), fingerprint = pair.Sender.Security.Fingerprint;
                    string id = pair.Sender.Settings.DeviceId;
                    pair.Sender.Dispose();
                    Pump(() => !pair.Back.Online && pair.Status.Text == "0 人在线 · 1 个会话", "真实离线后仍显示在线");
                    Render(pair, "offline-light");
                    using (LanService restarted = Node(senderRoot, "Android VPN 长名称 / 测试设备"))
                    {
                        restarted.Settings.DeviceId = id;
                        restarted.Probe(new IPEndPoint(IPAddress.Loopback, pair.Receiver.Port));
                        Pump(() => pair.Back.Online && pair.Status.Text == "1 人在线 · 1 个会话", "无需发送消息的重启恢复失败");
                        Check(restarted.Security.Fingerprint == fingerprint && pair.Receiver.Security.IsTrusted(id, fingerprint), "恢复改变证书或丢失信任");
                    }
                }
            });
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
