using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FeiqLight.Tests
{
    internal static class RemoteReuseTests
    {
        private const string ServerId = "dddddddddddddddddddddddddddddddd";
        private const string ClientId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        private sealed class Reply
        {
            internal int Code;
            internal string Header = "";
            internal byte[] Body = new byte[0];
            internal string Cookies { get { return String.Join("; ", Header.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(line => line.StartsWith("Set-Cookie: ")).Select(line => line.Substring(12).Split(';')[0])); } }
        }
        private sealed class Incoming
        {
            internal string Method, Path;
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal string Header(string name) { string value; return Headers.TryGetValue(name, out value) ? value : ""; }
        }
        private sealed class TlsPeer : IDisposable
        {
            internal readonly SecureIdentity Server, Client;
            internal readonly ManualResetEvent ScreenEntered = new ManualResetEvent(false), ReleaseScreen = new ManualResetEvent(true);
            internal readonly ManualResetEvent ReleaseLogin = new ManualResetEvent(true);
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Dictionary<TcpClient, SslStream> sockets = new Dictionary<TcpClient, SslStream>();
            private readonly object gate = new object();
            private bool closed;
            internal bool Legacy;
            internal int Handshakes, Screens, Inputs, DropInput, DropScreen, Reject, HoldLogin;
            internal int ScreenDelay;
            internal string Fault = "", Token = "";
            private int session;
            internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
            internal TlsPeer(string root)
            {
                Server = new SecureIdentity(Path.Combine(root, "server")); Client = new SecureIdentity(Path.Combine(root, "client"));
                Server.ConfirmPeer = Client.ConfirmPeer = (name, code) => true;
                Check(Server.EnsureTrusted(ClientId, Client.Fingerprint, "client"), "服务器信任失败");
                Check(Client.EnsureTrusted(ServerId, Server.Fingerprint, "server"), "客户端信任失败");
                listener.Start(12); Task.Run((Action)Accept);
            }
            internal ToolsGateway Gateway(LanService node = null)
            {
                return new ToolsGateway(node == null ? Client : node.Security, new Peer { DeviceId = ServerId, Fingerprint = Server.Fingerprint, Endpoint = new IPEndPoint(IPAddress.Loopback, Port) }, Port, true, node);
            }
            private void Accept()
            {
                try
                {
                    while (true)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (gate) { if (closed) { client.Close(); return; } sockets.Add(client, null); }
                        Task.Run(() => Serve(client));
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
            private void Serve(TcpClient client)
            {
                SslStream tls = null;
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000; client.NoDelay = true;
                    tls = Server.Server(client, true);
                    Check(SecureIdentity.RemoteFingerprint(tls) == Client.Fingerprint, "回环证书不符");
                    Interlocked.Increment(ref Handshakes);
                    lock (gate) sockets[client] = tls;
                    for (int index = 0; index < 16; index++)
                    {
                        Incoming request = ReadRequest(tls);
                        if (request.Path == "/api/login" || request.Path == "/api/reconnect")
                        {
                            Token = "remote-session-" + Interlocked.Increment(ref session);
                            string body = "{\"token\":\"" + Token + "\",\"mode\":\"remote\"}";
                            string extra = "Set-Cookie: fq_remote=" + Token + "; Path=/; HttpOnly; SameSite=Strict\r\n";
                            if (Interlocked.Exchange(ref HoldLogin, 0) != 0)
                            {
                                Write(tls, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" + extra + "\r\n");
                                ReleaseLogin.WaitOne(5000); Write(tls, body);
                            }
                            else Send(tls, 200, body, false, extra);
                            return;
                        }
                        if (!request.Header("Cookie").Split(';').Select(value => value.Trim()).Contains("fq_remote=" + Token)) { Send(tls, 401, "{}", false); return; }
                        bool screen = request.Method == "GET" && request.Path == "/api/screen";
                        bool input = request.Method == "POST" && request.Path == "/api/input";
                        if (screen)
                        {
                            Interlocked.Increment(ref Screens); ScreenEntered.Set(); ReleaseScreen.WaitOne(5000);
                            if (ScreenDelay > 0) Thread.Sleep(ScreenDelay);
                            if (Interlocked.Exchange(ref DropScreen, 0) != 0) return;
                        }
                        if (input)
                        {
                            if (request.Header("X-FQ-Token") != Token) { Send(tls, 403, "{}", false); return; }
                            Interlocked.Increment(ref Inputs);
                            if (Interlocked.Exchange(ref DropInput, 0) != 0) return;
                        }
                        if (Reject != 0) { Send(tls, Reject, "{}", false); return; }
                        string fault = Interlocked.Exchange(ref Fault, "");
                        if (fault == "duplicate") { Write(tls, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\n{}"); return; }
                        if (fault == "truncated") { Write(tls, "HTTP/1.1 200 OK\r\nContent-Length: 20\r\nConnection: keep-alive\r\n\r\n{}"); return; }
                        bool reuse = !Legacy && (screen || input) && index < 15 && request.Header("Connection") == "keep-alive";
                        Send(tls, 200, "{}", reuse);
                        if (!reuse) return;
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (System.Security.Authentication.AuthenticationException) { }
                finally { if (tls != null) try { tls.Dispose(); } catch (IOException) { } client.Close(); lock (gate) sockets.Remove(client); }
            }
            internal void CloseIdle()
            {
                KeyValuePair<TcpClient, SslStream>[] active; lock (gate) active = sockets.ToArray();
                foreach (var socket in active) { if (socket.Value != null) socket.Value.Dispose(); socket.Key.Close(); }
            }
            public void Dispose()
            {
                lock (gate) { closed = true; listener.Stop(); foreach (TcpClient client in sockets.Keys) client.Close(); }
                ReleaseScreen.Set();
                ReleaseLogin.Set();
                Wait(() => { lock (gate) return sockets.Count == 0; }, 6000, "TLS 回环线程未退出");
                ScreenEntered.Dispose(); ReleaseScreen.Dispose(); ReleaseLogin.Dispose(); Server.Dispose(); Client.Dispose();
            }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Wait(Func<bool> condition, int timeout, string message)
        {
            Stopwatch watch = Stopwatch.StartNew(); while (!condition() && watch.ElapsedMilliseconds < timeout) Thread.Sleep(5);
            Check(condition(), message);
        }
        private static string ReadHeader(Stream stream)
        {
            List<byte> bytes = new List<byte>(); int last = 0;
            while (bytes.Count < 16384) { int value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); bytes.Add((byte)value); last = (last << 8) | value; if (last == 0x0d0a0d0a) return Encoding.ASCII.GetString(bytes.ToArray()); }
            throw new IOException("回环响应头过长");
        }
        private static Incoming ReadRequest(Stream stream)
        {
            string[] lines = ReadHeader(stream).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string[] first = lines[0].Split(' '); Incoming request = new Incoming { Method = first[0], Path = first[1].Split('?')[0] };
            foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); request.Headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim()); }
            int length; if (Int32.TryParse(request.Header("Content-Length"), out length)) SecureIdentity.ReadBytes(stream, length);
            return request;
        }
        private static void Write(Stream stream, string text) { byte[] bytes = Encoding.ASCII.GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
        private static void Send(Stream stream, int code, string body, bool reuse, string extra = "")
        {
            Write(stream, "HTTP/1.1 " + code + " Result\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: " + (reuse ? "keep-alive" : "close") + "\r\n" + extra + "\r\n" + body);
        }
        private static Reply Call(ToolsGateway gateway, string path, string cookie = "", string body = null, string token = "")
        {
            Uri uri = new Uri(gateway.Url); string access = uri.Query.Substring("?access=".Length);
            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, uri.Port); client.ReceiveTimeout = 5000;
                string request = (body == null ? "GET" : "POST") + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + uri.Port + "\r\nX-FQ-Gateway: " + access + "\r\nCookie: " + cookie + "\r\nConnection: close\r\n";
                if (body != null) request += "Content-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nX-FQ-Token: " + token + "\r\n";
                Write(client.GetStream(), request + "\r\n" + (body ?? ""));
                using (MemoryStream bytes = new MemoryStream())
                {
                    try { client.GetStream().CopyTo(bytes); } catch (IOException) { }
                    byte[] result = bytes.ToArray(); string all = Encoding.ASCII.GetString(result); int split = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split < 0) return new Reply();
                    return new Reply { Code = Int32.Parse(all.Split(' ')[1]), Header = all.Substring(0, split), Body = result.Skip(split + 4).ToArray() };
                }
            }
        }
        private static string Login(ToolsGateway gateway)
        {
            Reply reply = Call(gateway, "/api/login", "", "{\"mode\":\"remote\"}"); Check(reply.Code == 200, "网关 TLS 登录失败"); return reply.Cookies;
        }
        private static string Profile(string root) { return Path.Combine(root, "remote-tls-" + Guid.NewGuid().ToString("N")); }
        internal static void Run(Action<string, Action> run, string root)
        {
            run("WP-4 TLS：24 次画面/输入请求握手由 24 次降为 2 次，浏览器头仍为 close", () => {
                int legacy, pooled;
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    peer.Legacy = true; string cookie = Login(gateway); int before = peer.Handshakes;
                    for (int i = 0; i < 12; i++) { Check(Call(gateway, "/api/screen", cookie).Code == 200, "旧端画面失败"); Check(Call(gateway, "/api/input", cookie, "{}", peer.Token).Code == 200, "旧端输入失败"); }
                    legacy = peer.Handshakes - before; Check(legacy == 24, "旧端未逐次握手");
                }
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); int before = peer.Handshakes;
                    for (int i = 0; i < 12; i++)
                    {
                        Reply screen = Call(gateway, "/api/screen", cookie); Check(screen.Code == 200 && screen.Header.Contains("Connection: close") && !screen.Header.Contains("keep-alive"), "浏览器继承了上游复用头");
                        Check(Call(gateway, "/api/input", cookie, "{}", peer.Token).Code == 200, "输入失败");
                    }
                    pooled = peer.Handshakes - before; Check(pooled == 2 && peer.Inputs == 12, "连接未分槽复用");
                }
                Console.WriteLine("METRIC Windows requests=24 legacy_tls=" + legacy + " pooled_tls=" + pooled + " reduction=91.67%");
            });
            run("WP-4 TLS：300ms 静止画面与输入物理分连", () => {
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); Call(gateway, "/api/screen", cookie); Call(gateway, "/api/input", cookie, "{}", peer.Token);
                    peer.ScreenEntered.Reset(); peer.ScreenDelay = 300;
                    Task<Reply> screen = Task.Run(() => Call(gateway, "/api/screen", cookie)); Check(peer.ScreenEntered.WaitOne(2000), "画面请求未进入");
                    Stopwatch watch = Stopwatch.StartNew(); Reply input = Call(gateway, "/api/input", cookie, "{}", peer.Token); long elapsed = watch.ElapsedMilliseconds;
                    Check(input.Code == 200 && elapsed < 200 && !screen.IsCompleted, "输入排在静止画面后面：" + elapsed + "ms");
                    Check(screen.Result.Code == 200, "静止画面失败"); Console.WriteLine("METRIC Windows stationary_screen_ms=300 input_ms=" + elapsed);
                }
            });
            run("WP-4 双端成对：生产网关与远控服务的 24 次 TLS 请求只建连 2 次", () => {
                string profile = Profile(root);
                using (TlsPeer peer = new TlsPeer(profile))
                using (LanService node = new LanService(new AppSettings { DeviceId = ClientId, ReceiveFolder = Path.Combine(profile, "received") }, LoopbackPorts.Chat(), true, "paired-remote"))
                using (ToolsService server = new ToolsService(0, true, Path.Combine(profile, "tools"), peer.Server))
                using (System.Drawing.Bitmap image = new System.Drawing.Bitmap(64, 64))
                using (MemoryStream encoded = new MemoryStream())
                {
                    image.Save(encoded, System.Drawing.Imaging.ImageFormat.Jpeg); byte[] jpeg = encoded.ToArray(); int inputs = 0, connections = 0;
                    server.ConfirmRemote = address => true; server.Capture = display => jpeg; server.Input = data => Interlocked.Increment(ref inputs); server.AllowRemote();
                    node.ConfigureSecurity(Path.Combine(profile, "client"), (name, code) => true); node.PeerConnector = (raw, endpoint) => { Interlocked.Increment(ref connections); raw.Connect(endpoint); }; node.Start();
                    Peer target = new Peer { DeviceId = ServerId, Fingerprint = peer.Server.Fingerprint, Endpoint = new IPEndPoint(IPAddress.Loopback, server.Port) };
                    using (ToolsGateway gateway = new ToolsGateway(node.Security, target, server.Port, true, node))
                    {
                        Reply login = Call(gateway, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + server.RemoteCode + "\"}"); Check(login.Code == 200, "生产服务登录失败");
                        string cookie = login.Cookies, token = (string)new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(login.Body))["token"]; int before = connections;
                        for (int i = 0; i < 12; i++) { Check(Call(gateway, "/api/screen", cookie).Body.SequenceEqual(jpeg), "生产画面帧被截断"); Check(Call(gateway, "/api/input", cookie, "{}", token).Code == 200, "生产输入失败"); }
                        Check(connections - before == 2 && inputs == 12, "生产双端未复用或输入次数错误");
                        Console.WriteLine("METRIC Windows production_pair_requests=24 tls_connections=" + (connections - before));
                    }
                }
            });
            run("WP-4 TLS：陈旧空闲 GET 换新连接仅重试一次", () => {
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); Check(Call(gateway, "/api/screen", cookie).Code == 200, "预热失败"); int before = peer.Handshakes;
                    peer.CloseIdle(); Check(Call(gateway, "/api/screen", cookie).Code == 200, "陈旧空闲连接未恢复"); Check(peer.Handshakes == before + 1, "陈旧空闲连接重试次数错误");
                    peer.DropScreen = 1; Check(Call(gateway, "/api/screen", cookie).Code == 200, "复用 EOF 未重试");
                    int screens = peer.Screens; peer.CloseIdle(); peer.DropScreen = 1;
                    Check(Call(gateway, "/api/screen", cookie).Code != 200 && peer.Screens == screens + 1, "新连接 EOF 被再次重试");
                }
            });
            run("WP-4 TLS：输入已执行后断流仅执行一次，GET 头后截断不重试", () => {
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); Check(Call(gateway, "/api/input", cookie, "{}", peer.Token).Code == 200, "输入预热失败");
                    int inputs = peer.Inputs; peer.DropInput = 1; Check(Call(gateway, "/api/input", cookie, "{}", peer.Token).Code != 200 && peer.Inputs == inputs + 1, "已执行的输入被重放");
                    Check(Call(gateway, "/api/screen", cookie).Code == 200, "画面预热失败"); int screens = peer.Screens; peer.Fault = "truncated";
                    Reply truncated = Call(gateway, "/api/screen", cookie); Check(truncated.Code == 200 && truncated.Body.Length == 2 && peer.Screens == screens + 1, "头后截断被重试或掩盖");
                    Check(Call(gateway, "/api/screen", cookie).Code == 200, "截断连接被回池");
                }
            });
            run("WP-4 TLS：重复长度拒绝、401/403 不重试，403 后正常换新连接", () => {
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); Call(gateway, "/api/screen", cookie); peer.Fault = "duplicate";
                    Check(Call(gateway, "/api/screen", cookie).Code != 200, "重复 Content-Length 被接受"); Check(Call(gateway, "/api/screen", cookie).Code == 200, "坏连接被回池");
                    foreach (int code in new[] { 403, 401 }) { int screens = peer.Screens; peer.Reject = code; Check(Call(gateway, "/api/screen", cookie).Code == code && peer.Screens == screens + 1, "鉴权拒绝被重试"); peer.Reject = 0; if (code == 403) Check(Call(gateway, "/api/screen", cookie).Code == 200, "业务 403 误撤会话"); }
                }
            });
            run("WP-4 TLS：Cookie 切换与网关实例隔离，旧会话不能借新槽位", () => {
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string first = Login(gateway), oldToken = peer.Token; Call(gateway, "/api/screen", first); string next = Login(gateway); int screens = peer.Screens;
                    Check(Call(gateway, "/api/screen", first).Code != 200 && peer.Screens == screens, "旧 Cookie 借用了新会话槽位");
                    Check(Call(gateway, "/api/logout", first, "{}", oldToken).Code != 200, "旧 Cookie 登出被接受");
                    Check(Call(gateway, "/api/screen", next).Code == 200, "新会话失败"); int before = peer.Handshakes;
                    using (ToolsGateway second = peer.Gateway()) Check(Call(second, "/api/screen", next).Code == 200 && peer.Handshakes == before + 1, "跨网关实例共享了连接");
                }
            });
            run("WP-4 TLS：新 Cookie 先于下游响应头发布，登录正文等待不阻塞新画面", () => {
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); Call(gateway, "/api/screen", cookie); peer.ReleaseLogin.Reset(); peer.HoldLogin = 1;
                    Uri uri = new Uri(gateway.Url);
                    using (TcpClient browser = Connect(uri.Port))
                    {
                        Write(browser.GetStream(), "POST /api/reconnect HTTP/1.1\r\nHost: 127.0.0.1:" + uri.Port + "\r\nX-FQ-Gateway: " + uri.Query.Substring("?access=".Length) + "\r\nCookie: " + cookie + "\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                        try
                        {
                            Reply headers = new Reply { Header = ReadHeader(browser.GetStream()) };
                            Check(Call(gateway, "/api/screen", headers.Cookies).Code == 200, "新 Cookie 已交给浏览器，但槽位仍绑定旧会话");
                        }
                        finally { peer.ReleaseLogin.Set(); }
                        using (MemoryStream remaining = new MemoryStream()) browser.GetStream().CopyTo(remaining);
                    }
                }
            });
            run("WP-4 TLS：停止与撤信中断在用连接并唤醒等待者", () => {
                foreach (bool revoke in new[] { false, true })
                using (TlsPeer peer = new TlsPeer(Profile(root))) using (ToolsGateway gateway = peer.Gateway())
                {
                    string cookie = Login(gateway); Call(gateway, "/api/input", cookie, "{}", peer.Token);
                    peer.ReleaseScreen.Reset(); Task<Reply> screen = Task.Run(() => Call(gateway, "/api/screen", cookie)); Check(peer.ScreenEntered.WaitOne(2000), "在用画面未进入");
                    Task<Reply> waiter = Task.Run(() => Call(gateway, "/api/screen", cookie)); Thread.Sleep(30); Stopwatch watch = Stopwatch.StartNew();
                    if (revoke) peer.Client.Forget(ServerId); else gateway.Dispose();
                    Check(Task.WaitAll(new Task[] { screen, waiter }, 500) && screen.Result.Code != 200 && waiter.Result.Code != 200 && watch.ElapsedMilliseconds < 500, "撤销未及时唤醒请求");
                    peer.ReleaseScreen.Set(); Console.WriteLine("METRIC Windows gateway_" + (revoke ? "revoke" : "stop") + "_ms=" + watch.ElapsedMilliseconds);
                }
            });
            run("WP-4 TLS：空闲双槽计入节点上限 8，网络代次更新清空旧槽", () => {
                string profile = Profile(root);
                using (TlsPeer peer = new TlsPeer(profile))
                using (LanService node = new LanService(new AppSettings { DeviceId = ClientId, ReceiveFolder = Path.Combine(profile, "received") }, LoopbackPorts.Chat(), true, "remote-pool"))
                {
                    node.ConfigureSecurity(Path.Combine(profile, "client"), (name, code) => true); node.Start();
                    using (ToolsGateway gateway = peer.Gateway(node))
                    {
                        string cookie = Login(gateway); Call(gateway, "/api/screen", cookie); Call(gateway, "/api/input", cookie, "{}", peer.Token);
                        var connections = (HashSet<TcpClient>)typeof(LanService).GetField("connections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(node);
                        Check(connections.Count == 2, "空闲复用槽未登记到节点");
                        List<TcpClient> extra = new List<TcpClient>();
                        try
                        {
                            Peer target = new Peer { DeviceId = ServerId, Fingerprint = peer.Server.Fingerprint, Endpoint = new IPEndPoint(IPAddress.Loopback, peer.Port) };
                            for (int i = 0; i < 6; i++) extra.Add(node.ConnectPeer(target, peer.Port));
                            bool rejected = false; try { using (TcpClient ignored = node.ConnectPeer(target, peer.Port)) { } } catch (IOException) { rejected = true; }
                            Check(rejected && connections.Count == 8, "复用绕过了节点并发上限 8");
                        }
                        finally { foreach (TcpClient client in extra) { client.Close(); node.ReleaseConnection(client); } }
                        typeof(LanService).GetMethod("ResetNetwork", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(node, null);
                        Check(connections.Count == 0, "切网未即时释放旧槽节点额度");
                        using (ToolsGateway fresh = peer.Gateway(node))
                        using (MainForm form = new MainForm(node, new LocalStore(Path.Combine(profile, "ui"))))
                        {
                            form.PrepareNetworkCallbacks();
                            var gateways = (List<ToolsGateway>)typeof(MainForm).GetField("gateways", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(form);
                            gateways.Add(gateway); gateways.Add(fresh);
                            typeof(MainForm).GetMethod("OnNetworkReset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(form, null);
                            System.Windows.Forms.Application.DoEvents();
                            Check(!gateways.Contains(gateway) && gateways.Contains(fresh) && Call(fresh, "/api/screen", cookie).Code == 200, "延后的旧 UI 回调误关新网关");
                        }
                    }
                }
            });
            ServerTests(run, root);
        }
        private static TcpClient Connect(int port)
        {
            TcpClient client = new TcpClient(); client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 4000; client.SendTimeout = 4000; client.NoDelay = true; return client;
        }
        private static Reply Direct(Stream tls, int port, string path, string cookie, string body = null, string extra = "")
        {
            string method = body == null ? "GET" : "POST";
            string header = method + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nCookie: " + cookie + "\r\nConnection: keep-alive\r\n" + extra;
            if (body != null) header += "Content-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nX-FQ-Token: " + cookie.Substring(cookie.IndexOf('=') + 1) + "\r\n";
            Write(tls, header + "\r\n" + (body ?? "")); string response = ReadHeader(tls);
            string size = response.Split(new[] { "\r\n" }, StringSplitOptions.None).Single(line => line.StartsWith("Content-Length:")).Substring(15).Trim();
            return new Reply { Code = Int32.Parse(response.Split(' ')[1]), Header = response, Body = SecureIdentity.ReadBytes(tls, Int32.Parse(size)) };
        }
        private static Reply Direct(SecureIdentity identity, int port, string fingerprint, string path, string cookie, string body = null)
        {
            using (TcpClient client = Connect(port)) using (SslStream tls = identity.Client(client, fingerprint)) return Direct(tls, port, path, cookie, body);
        }
        private static string Login(TlsPeer peer, ToolsService server)
        {
            Reply reply = Direct(peer.Client, server.Port, peer.Server.Fingerprint, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + server.RemoteCode + "\"}");
            Check(reply.Code == 200 && reply.Header.Contains("Connection: close"), "业务登录不能复用");
            return "fq_remote=" + new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(reply.Body))["token"];
        }
        private static bool Closed(Stream stream)
        {
            try { return stream.ReadByte() < 0; } catch (IOException) { return true; } catch (ObjectDisposedException) { return true; }
        }
        private static void ServerTests(Action<string, Action> run, string root)
        {
            run("WP-4 服务端 TLS：16 请求上限、每次 Origin/会话/CSRF 复核，仅画面与输入可复用", () => {
                string profile = Profile(root);
                using (TlsPeer peer = new TlsPeer(profile)) using (ToolsService server = new ToolsService(0, true, Path.Combine(profile, "tools"), peer.Server))
                {
                    int inputs = 0; server.ConfirmRemote = address => true; server.Capture = display => new byte[] { 1, 2, 3 }; server.Input = data => inputs++; server.Displays = read => new { items = new object[0] }; server.AllowRemote();
                    string cookie = Login(peer, server);
                    using (TcpClient raw = Connect(server.Port)) using (SslStream tls = peer.Client.Client(raw, peer.Server.Fingerprint))
                    {
                        for (int i = 0; i < 16; i++) { Reply reply = Direct(tls, server.Port, "/api/screen", cookie); Check(reply.Code == 200 && reply.Header.Contains(i < 15 ? "Connection: keep-alive" : "Connection: close"), "请求次数上限无效"); }
                        Check(Closed(tls), "16 请求后仍保留连接");
                    }
                    using (TcpClient raw = Connect(server.Port)) using (SslStream tls = peer.Client.Client(raw, peer.Server.Fingerprint))
                    {
                        Check(Direct(tls, server.Port, "/api/screen", cookie).Code == 200, "Origin 预热失败");
                        Reply denied = Direct(tls, server.Port, "/api/screen", cookie, null, "Origin: https://bad.example\r\n"); Check(denied.Code == 403 && denied.Header.Contains("Connection: close"), "复用连接绕过 Origin");
                    }
                    using (TcpClient raw = Connect(server.Port)) using (SslStream tls = peer.Client.Client(raw, peer.Server.Fingerprint))
                    {
                        Check(Direct(tls, server.Port, "/api/input", cookie, "{}").Code == 200, "输入预热失败");
                        Reply denied = Direct(tls, server.Port, "/api/input", "fq_remote=wrong", "{}"); Check(denied.Code == 401 && inputs == 1 && denied.Header.Contains("Connection: close"), "复用连接绕过当前会话");
                    }
                    Reply displays = Direct(peer.Client, server.Port, peer.Server.Fingerprint, "/api/displays", cookie); Check(displays.Code == 200 && displays.Header.Contains("Connection: close"), "其他 API 获得了复用资格");
                }
            });
            run("WP-4 服务端 TLS：停止/登出/切网/切换控制端/撤信立即关闭活动与空闲连接", () => {
                foreach (string action in new[] { "stop", "logout", "network", "switch", "revoke" })
                {
                    string profile = Profile(root);
                    using (TlsPeer peer = new TlsPeer(profile)) using (ToolsService server = new ToolsService(0, true, Path.Combine(profile, "tools"), peer.Server))
                    using (ManualResetEvent entered = new ManualResetEvent(false)) using (ManualResetEvent release = new ManualResetEvent(false)) using (ManualResetEvent finished = new ManualResetEvent(false))
                    {
                        bool block = false; server.ConfirmRemote = address => true; server.Input = data => { }; server.Capture = display => { if (block) { entered.Set(); release.WaitOne(5000); finished.Set(); } return new byte[] { 1, 2, 3 }; }; server.AllowRemote();
                        string cookie = Login(peer, server);
                        using (TcpClient screenRaw = Connect(server.Port)) using (SslStream screenTls = peer.Client.Client(screenRaw, peer.Server.Fingerprint))
                        using (TcpClient inputRaw = Connect(server.Port)) using (SslStream inputTls = peer.Client.Client(inputRaw, peer.Server.Fingerprint))
                        {
                            Check(Direct(screenTls, server.Port, "/api/screen", cookie).Code == 200 && Direct(inputTls, server.Port, "/api/input", cookie, "{}").Code == 200, "双连接预热失败");
                            block = true;
                            Task<bool> active = Task.Run(() => { try { return Direct(screenTls, server.Port, "/api/screen", cookie).Code != 200; } catch (IOException) { return true; } });
                            Check(entered.WaitOne(2000), "活动截图未进入"); Stopwatch watch = Stopwatch.StartNew();
                            try
                            {
                                if (action == "stop") server.StopRemote();
                                else if (action == "logout") Check(Direct(peer.Client, server.Port, peer.Server.Fingerprint, "/api/logout", cookie, "{\"mode\":\"remote\"}").Code == 200, "登出响应失败");
                                else if (action == "network") server.NetworkReset();
                                else if (action == "switch") server.AllowRemote();
                                else peer.Server.Forget(ClientId);
                                Check(active.Wait(500) && active.Result && Closed(inputTls) && watch.ElapsedMilliseconds < 500, "活动/空闲连接未立即关闭：" + action);
                                Console.WriteLine("METRIC Windows server_" + action + "_ms=" + watch.ElapsedMilliseconds);
                            }
                            finally { release.Set(); Check(finished.WaitOne(2000), "旧截图线程未退出"); }
                            try { Check(Direct(peer.Client, server.Port, peer.Server.Fingerprint, "/api/screen", cookie).Code != 200, "旧会话仍可请求：" + action); } catch (IOException) { }
                        }
                    }
                }
            });
        }
    }
}
