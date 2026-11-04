using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class ToolsTests
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        private sealed class Reply
        {
            internal int Code; internal byte[] Body; internal string Headers;
            internal Dictionary<string, object> Json() { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Body)); }
        }
        private static Reply Request(int port, string path, string cookie = "", string data = null, string headers = "", string method = null)
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, port); client.ReceiveTimeout = 45000; client.SendTimeout = 5000;
                byte[] body = data == null ? new byte[0] : Encoding.UTF8.GetBytes(data);
                string verb = method ?? (data == null ? "GET" : "POST");
                string request = verb + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nCookie: " + cookie + "\r\n" + headers;
                if (data != null) request += "Content-Type: application/json\r\nContent-Length: " + body.Length + "\r\n";
                byte[] head = Encoding.ASCII.GetBytes(request + "Connection: close\r\n\r\n"); NetworkStream stream = client.GetStream(); stream.Write(head, 0, head.Length); stream.Write(body, 0, body.Length);
                using (MemoryStream result = new MemoryStream())
                {
                    stream.CopyTo(result); byte[] bytes = result.ToArray(); int split = -1;
                    for (int i = 0; i + 3 < bytes.Length; i++) if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) { split = i; break; }
                    if (split < 0) throw new Exception("没有收到 HTTP 响应");
                    string text = Encoding.ASCII.GetString(bytes, 0, split); return new Reply { Code = Int32.Parse(text.Split(' ')[1]), Headers = text, Body = bytes.Skip(split + 4).ToArray() };
                }
            }
        }
        private static string Login(ToolsService server, string mode)
        {
            Reply response = Request(server.Port, "/api/login", "", new JavaScriptSerializer().Serialize(new { mode = mode, code = mode == "share" ? server.ShareCode : server.RemoteCode }));
            Assert(response.Code == 200, "登录失败 " + Encoding.UTF8.GetString(response.Body));
            return "fq_" + mode + "=" + response.Json()["token"];
        }
        private static string TokenHeader(string cookie) { return "X-FQ-Token: " + cookie.Substring(cookie.IndexOf('=') + 1) + "\r\n"; }
        private static string PairCookie(Reply reply) { return reply.Headers.Split(new[] { "\r\n" }, StringSplitOptions.None).Single(s => s.StartsWith("Set-Cookie: fq_pair_")).Substring(12).Split(';')[0]; }
        private static Reply Reconnect(ToolsService server, string mode, string cookie) { return Request(server.Port, "/api/reconnect", cookie, "{\"mode\":\"" + mode + "\"}", "X-FQ-Reconnect: 1\r\n"); }
        private static void Assert(bool value, string text) { if (!value) throw new Exception(text); }
        private static Reply Persistent(TcpClient client, int port, string path, string cookie, string method = "GET")
        {
            Stream stream = client.GetStream(); byte[] header = Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nCookie: " + cookie + "\r\nRange: bytes=40-59\r\nConnection: keep-alive\r\n\r\n"); stream.Write(header, 0, header.Length);
            var bytes = new List<byte>(); int last = 0; while (last != 0x0d0a0d0a) { int b = stream.ReadByte(); if (b < 0) throw new Exception("复用连接提前关闭"); bytes.Add((byte)b); last = (last << 8) | b; }
            string text = Encoding.ASCII.GetString(bytes.ToArray()); int length = method == "HEAD" ? 0 : Int32.Parse(text.Split(new[] { "\r\n" }, StringSplitOptions.None).Single(s => s.StartsWith("Content-Length:")).Substring(15));
            byte[] body = new byte[length]; for (int i = 0; i < length;) { int count = stream.Read(body, i, length - i); if (count == 0) throw new Exception("响应被截断"); i += count; }
            return new Reply { Code = Int32.Parse(text.Split(' ')[1]), Body = body, Headers = text };
        }
        [STAThread] private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools-test-data")); Directory.CreateDirectory(root);
            if (args.Length > 0 && args[0] == "serve") { Serve(root); return 0; }
            if (args.Length > 0 && args[0] == "render") { Render(root); return 0; }
            if (args.Length > 0 && args[0] == "desktop") { Desktop(root); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "wheel-desktop") { WheelDesktop(root); return Environment.ExitCode; }
            if (args.Length > 2 && args[0] == "upgrade") { try { Upgrade(root, Path.GetFullPath(args[2]), args.Length > 3 ? Path.GetFullPath(args[3]) : Path.GetFullPath(args[2])); return 0; } catch(Exception error) { Console.WriteLine("UPGRADE FAIL: " + error.GetType().Name + " " + error.Message); return 1; } }
            int passed = 0, failed = 0;
            Action<string, Action> run = (name, test) => { try { test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } };
            string folder = Path.Combine(root, "shared"); Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(folder, "子目录"));
            File.WriteAllBytes(Path.Combine(folder, "视频 sample.mp4"), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()); File.WriteAllBytes(Path.Combine(folder, "empty.bin"), new byte[0]);
            run("范围解析：首段、尾段、超界、空文件、超过 2 GiB", () => {
                Assert(ToolsHttp.Range("bytes=10-19", 100).SequenceEqual(new long[] { 10, 19 }), "首段");
                Assert(ToolsHttp.Range("bytes=-10", 100).SequenceEqual(new long[] { 90, 99 }), "尾段");
                Assert(ToolsHttp.Range("bytes=2147483650-", 3000000000).SequenceEqual(new long[] { 2147483650, 2999999999 }), "64位偏移");
                foreach (string value in new[] { "bytes=100-", "bytes=20-10", "bytes=0-1,4-5", "bytes=-0", "items=0-1", "bytes=+1-2" }) Assert(ToolsHttp.Range(value, 100) == null, "未拒绝 " + value);
                Assert(ToolsHttp.Range("bytes=0-", 0) == null && ToolsHttp.Range("", 0)[1] == -1, "空文件");
            });
            run("HTTP：未授权拒绝，中文目录、读取、HEAD 和 Range 实际回环", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    server.Share(folder); Assert(Request(server.Port, "/api/list").Code == 401, "匿名读取"); string cookie = Login(server, "share");
                    Reply list = Request(server.Port, "/api/list", cookie); Assert(list.Code == 200, "列表");
                    var entries = ((System.Collections.IEnumerable)list.Json()["items"]).Cast<Dictionary<string, object>>().ToArray(); var video = entries.Single(i => (string)i["name"] == "视频 sample.mp4");
                    string path = "/file?id=" + video["id"];
                    Reply range = Request(server.Port, path, cookie, null, "Range: bytes=40-59\r\n"); Assert(range.Code == 206 && range.Body.SequenceEqual(Enumerable.Range(40, 20).Select(i => (byte)i)), "分段字节不一致");
                    Reply suffix = Request(server.Port, path, cookie, null, "Range: bytes=-4\r\n"); Assert(suffix.Body.SequenceEqual(new byte[] { 252, 253, 254, 255 }), "尾段不一致");
                    Assert(Request(server.Port, path, cookie, null, "Range: bytes=256-\r\n").Code == 416, "非法范围");
                    Reply head = Request(server.Port, path, cookie, null, "", "HEAD"); Assert(head.Code == 200 && head.Body.Length == 0 && head.Headers.Contains("Content-Length: 256"), "HEAD");
                    var empty = entries.Cast<Dictionary<string, object>>().Single(i => (string)i["name"] == "empty.bin"); Assert(Request(server.Port, "/file?id=" + empty["id"], cookie).Body.Length == 0, "空文件");
                    var dir = entries.Cast<Dictionary<string, object>>().Single(i => (bool)i["dir"]); Assert(Request(server.Port, "/api/list?id=" + dir["id"], cookie).Code == 200, "子目录");
                    Reply download = Request(server.Port, path + "&download=1", cookie); Assert(download.Headers.Contains("Content-Disposition: attachment;") && download.Body.Length == 256, "下载");
                }
            });
            run("隔离：文件 ID 不接受路径、不提供写入、禁止跨站", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    server.Share(folder); string cookie = Login(server, "share");
                    Assert(Request(server.Port, "/file?id=..%2fsecret", cookie).Code == 404, "目录逃逸");
                    Assert(Request(server.Port, "/api/list", cookie, null, "", "DELETE").Code == 405, "写入口");
                    Assert(Request(server.Port, "/api/list", cookie, null, "Origin: https://evil.invalid\r\n").Code == 403, "跨站读取");
                    Assert(Request(server.Port, "/api/screen", cookie).Code == 401, "共享令牌获得了远控权限");
                    Assert(Request(server.Port, "/api/logout", cookie, "{\"mode\":\"share\"}").Code == 403, "缺少 CSRF 校验");
                }
            });
            run("撤销：停止共享、更换目录、注销使旧连接失效", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    server.Share(folder); string first = Login(server, "share"); server.StopSharing(); Assert(Request(server.Port, "/api/list", first).Code == 401, "停止未撤销");
                    server.Share(folder); string second = Login(server, "share"); server.Share(folder); Assert(Request(server.Port, "/api/list", second).Code == 401, "更换未撤销");
                    string third = Login(server, "share"); Assert(Request(server.Port, "/api/logout", third, "{\"mode\":\"share\"}", TokenHeader(third)).Code == 200, "注销失败"); Assert(Request(server.Port, "/api/list", third).Code == 401, "注销后仍可访问");
                }
            });
            run("远控：本机拒绝、单次授权、输入校验和停止", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    int inputs = 0; server.Input = data => inputs++; server.Capture = display => new byte[] { 1, 2, 3 }; server.ConfirmRemote = address => false; server.AllowRemote();
                    Assert(Request(server.Port, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + server.RemoteCode + "\"}").Code == 403, "拒绝失效");
                    server.ConfirmRemote = address => true; server.AllowRemote(); string code = server.RemoteCode, cookie = Login(server, "remote");
                    Assert(Request(server.Port, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + code + "\"}").Code == 403, "远控码被复用");
                    Assert(Request(server.Port, "/api/screen", cookie).Body.Length == 3, "画面路由");
                    Assert(Request(server.Port, "/api/input", cookie, "{\"type\":\"click\"}").Code == 403 && inputs == 0, "无校验输入");
                    Assert(Request(server.Port, "/api/input", cookie, "{\"type\":\"click\"}", TokenHeader(cookie)).Code == 200 && inputs == 1, "授权输入");
                    server.StopRemote(); Assert(Request(server.Port, "/api/screen", cookie).Code == 401, "停止后仍有画面");
                }
            });
            run("远控：本机等待确认期间停止，旧确认不能复活会话", () => {
                using (ToolsService server = new ToolsService(0, true))
                using (ManualResetEvent entered = new ManualResetEvent(false))
                using (ManualResetEvent release = new ManualResetEvent(false))
                {
                    server.ConfirmRemote = address => { entered.Set(); release.WaitOne(5000); return true; }; server.AllowRemote(); string code = server.RemoteCode;
                    Task<Reply> login = Task.Run(() => Request(server.Port, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + code + "\"}"));
                    Assert(entered.WaitOne(5000), "没有进入确认"); server.StopRemote(); release.Set(); Assert(login.Result.Code == 403 && !server.RemoteActive, "旧确认复活");
                }
            });
            run("连接码：限制暴力尝试，默认不开共享或远控", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    for (int i = 0; i < 8; i++) Assert(Request(server.Port, "/api/login", "", "{\"mode\":\"share\",\"code\":\"wrong\"}").Code == 403, "默认开放");
                    Assert(Request(server.Port, "/api/login", "", "{\"mode\":\"share\",\"code\":\"wrong\"}").Code == 429, "无限尝试");
                }
            });
            run("页面资源：已内嵌、无外网脚本、所有静态资源可读取", () => {
                using (ToolsService server = new ToolsService(0, true)) foreach (string path in new[] { "/", "/app.js", "/app.css" }) { Reply result = Request(server.Port, path); Assert(result.Code == 200 && result.Body.Length > 100, path); Assert(result.Headers.Contains("frame-ancestors 'none'"), "缺少防嵌入策略"); }
            });
            run("6 位数字连接码，配对跨服务重启，限制目录及权限，清除立即失效", () => {
                string settings = Path.Combine(root, "pairings-" + Guid.NewGuid().ToString("N")), pair; int port;
                using (ToolsService server = new ToolsService(0, true, settings))
                {
                    port = server.Port; server.Share(folder); server.AllowRemote();
                    Assert(System.Text.RegularExpressions.Regex.IsMatch(server.ShareCode, "^[0-9]{6}$") && System.Text.RegularExpressions.Regex.IsMatch(server.RemoteCode, "^[0-9]{6}$"), "不是六位数字");
                    Reply login = Request(port, "/api/login", "", "{\"mode\":\"share\",\"code\":\"" + server.ShareCode + "\"}"); Assert(login.Code == 200, "配对失败"); pair = PairCookie(login);
                    Assert(login.Headers.Contains("HttpOnly; SameSite=Strict; Max-Age=7776000"), "配对没有持久 cookie");
                    Assert(!File.ReadAllText(Path.Combine(settings, "tools-pairings.json")).Contains(pair.Split('=')[1]), "保存了原始凭据");
                    string session = "fq_share=" + login.Json()["token"]; Request(port, "/api/logout", session, "{\"mode\":\"share\"}", TokenHeader(session));
                    Assert(Reconnect(server, "share", pair).Code == 200, "断开后无法重连");
                    Assert(Request(port, "/api/reconnect", pair, "{\"mode\":\"share\"}").Code == 403, "无请求校验也能重连");
                    Assert(Reconnect(server, "remote", pair).Code == 401, "共享配对获得远控权限");
                }
                using (ToolsService server = new ToolsService(port, true, settings))
                {
                    Assert(Reconnect(server, "share", pair).Code == 403, "未开启也可重连"); server.Share(folder);
                    Assert(Reconnect(server, "share", pair).Code == 200, "重启后丢失配对");
                    server.Share(Path.Combine(folder, "子目录")); Assert(Reconnect(server, "share", pair).Code == 401, "配对跨目录复用");
                    server.Share(folder); server.ForgetPairings(); Assert(Reconnect(server, "share", pair).Code == 401, "清除后仍可重连");
                }
            });
            run("远控重连仍需本机确认，单控制端，停止及清除不能被重连绕过", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    int confirms = 0; server.ConfirmRemote = address => { confirms++; return true; }; server.AllowRemote();
                    Reply login = Request(server.Port, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + server.RemoteCode + "\"}"); Assert(login.Code == 200, "首次远控");
                    string pair = PairCookie(login), session = "fq_remote=" + login.Json()["token"];
                    Assert(Reconnect(server, "remote", pair).Code == 409, "同时两个控制端");
                    Request(server.Port, "/api/logout", session, "{\"mode\":\"remote\"}", TokenHeader(session));
                    Assert(Reconnect(server, "remote", pair).Code == 200 && confirms == 2, "重连没有确认");
                    server.StopRemote(); Assert(Reconnect(server, "remote", pair).Code == 403, "重连绕过停止");
                    server.AllowRemote(); server.ConfirmRemote = address => false; Assert(Reconnect(server, "remote", pair).Code == 403, "重连绕过拒绝");
                    server.ForgetPairings(); server.AllowRemote(); Assert(Reconnect(server, "remote", pair).Code == 401, "清除后配对仍有效");
                }
            });
            run("Windows 浮条紧凑布局、无抢焦点标志、断开按钮可用", () => {
                bool disconnected = false;
                using (RemoteBanner banner = new RemoteBanner("192.168.100.123", () => disconnected = true))
                {
                    banner.Show(); Application.DoEvents();
                    Assert(banner.FormBorderStyle == FormBorderStyle.None && !banner.ShowInTaskbar, "仍有多余标题栏");
                    var flags = (CreateParams)typeof(RemoteBanner).GetProperty("CreateParams", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(banner, null);
                    Assert((flags.ExStyle & 0x08000000) != 0, "浮条可能抢焦点");
                    var row = (TableLayoutPanel)banner.Controls[0]; ((Button)row.GetControlFromPosition(2, 0)).PerformClick(); Assert(disconnected, "断开未执行"); banner.Close();
                }
            });
            run("始终允许只信任已配对设备，本机停止和清除仍生效", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    int confirms = 0; server.ConfirmRemoteDevice = address => { confirms++; return 2; }; server.AllowRemote();
                    Reply first = Request(server.Port, "/api/login", "", "{\"mode\":\"remote\",\"code\":\"" + server.RemoteCode + "\"}"); Assert(first.Code == 200, "首次配对失败");
                    string cookie = "fq_remote=" + first.Json()["token"], pair = PairCookie(first);
                    Request(server.Port, "/api/logout", cookie, "{\"mode\":\"remote\"}", TokenHeader(cookie));
                    Assert(Reconnect(server, "remote", pair).Code == 200 && confirms == 1, "已信任仍需确认");
                    server.StopRemote(); Assert(Reconnect(server, "remote", pair).Code == 403, "绕过停止");
                    server.AllowRemote(); server.ForgetPairings(); server.AllowRemote(); Assert(Reconnect(server, "remote", pair).Code == 401, "绕过清除");
                }
            });
            run("本机确认勾选始终允许，真实控件返回设备信任选项", () => {
                using(ToolsWindow window=new ToolsWindow(-1,true))
                using(System.Windows.Forms.Timer click=new System.Windows.Forms.Timer { Interval=100 })
                {
                    window.Show();Application.DoEvents();bool checkedBox=false;
                    click.Tick+=delegate {
                        Form prompt=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Text=="是否允许远程控制？");if(prompt==null)return;
                        var surface=((ModernWindow)prompt).Surface;var always=surface.Controls.OfType<CheckBox>().Single();always.Checked=true;checkedBox=true;click.Stop();
                        surface.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b=>b.Text=="允许本次控制").PerformClick();
                    };
                    click.Start();int result=(int)typeof(ToolsWindow).GetMethod("Confirm",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(window,new object[]{"127.0.0.1"});
                    Assert(checkedBox&&result==2,"勾选未持久授权");window.Close();
                }
            });
            run("截图编码不阻塞共享视频 Range，停止后不交付旧画面", () => {
                using (ToolsService server = new ToolsService(0, true))
                using (ManualResetEvent entered = new ManualResetEvent(false))
                using (ManualResetEvent release = new ManualResetEvent(false))
                {
                    server.Share(folder); string share = Login(server, "share");
                    var files = ((System.Collections.IEnumerable)Request(server.Port, "/api/list", share).Json()["items"]).Cast<Dictionary<string, object>>();
                    string id = (string)files.Single(i => (string)i["name"] == "视频 sample.mp4")["id"];
                    server.ConfirmRemote = address => true; server.AllowRemote(); string remote = Login(server, "remote");
                    server.Capture = display => { entered.Set(); release.WaitOne(5000); return new byte[10]; };
                    Task<Reply> capture = Task.Run(() => Request(server.Port, "/api/screen", remote)); Assert(entered.WaitOne(5000), "没有进入捕获");
                    try
                    {
                        Task<Reply> range = Task.Run(() => Request(server.Port, "/file?id=" + id, share, null, "Range: bytes=20-39\r\n"));
                        Assert(range.Wait(1000) && range.Result.Code == 206 && range.Result.Body.Length == 20, "视频等待截图完成");
                        server.StopRemote();
                    }
                    finally { release.Set(); }
                    Assert(capture.Result.Code == 401, "停止后仍交付旧帧");
                }
            });
            run("视频复用连接：连续 Range、HEAD 边界和每次请求重新校验授权", () => {
                using (ToolsService server = new ToolsService(0, true))
                {
                    server.Share(folder);string cookie=Login(server,"share");
                    var files=((System.Collections.IEnumerable)Request(server.Port,"/api/list",cookie).Json()["items"]).Cast<Dictionary<string,object>>();string id=(string)files.Single(i=>(string)i["name"]=="视频 sample.mp4")["id"];
                    using(TcpClient socket=new TcpClient())
                    {
                        socket.Connect(IPAddress.Loopback,server.Port);socket.ReceiveTimeout=4000;
                        for(int i=0;i<4;i++){Reply result=Persistent(socket,server.Port,"/file?id="+id,cookie,i==1?"HEAD":"GET");Assert(result.Code==206&&result.Headers.Contains("Connection: keep-alive"),"未复用");if(i!=1)Assert(result.Body.SequenceEqual(Enumerable.Range(40,20).Select(v=>(byte)v)),"Range 边界错乱");}
                        server.StopSharing();Assert(Persistent(socket,server.Port,"/file?id="+id,cookie).Code==401,"复用连接绕过停止共享");
                    }
                }
            });
            run("覆盖更新：目录、远控开关和始终允许跨实例恢复，旧会话失效", () => {
                string profile=Path.Combine(root,"upgrade-"+Guid.NewGuid().ToString("N")),pair,session;int port;
                using(ToolsService first=new ToolsService(0,true,profile))
                {
                    first.Share(folder);first.AllowRemote();first.ConfirmRemoteDevice=address=>2;port=first.Port;
                    Reply login=Request(port,"/api/login","","{\"mode\":\"remote\",\"code\":\""+first.RemoteCode+"\"}");Assert(login.Code==200,"首次登录");pair=PairCookie(login);session="fq_remote="+login.Json()["token"];
                }
                using(ToolsService updated=new ToolsService(port,true,profile))
                {
                    int confirmations=0;updated.ConfirmRemoteDevice=address=>{confirmations++;return 0;};
                    Assert(updated.RestoreSettings()==null&&updated.Folder==Path.GetFullPath(folder)&&updated.RemoteEnabled,"重启丢失设置");
                    Assert(Request(port,"/api/screen",session).Code==401,"恢复了旧会话");
                    Assert(Reconnect(updated,"remote",pair).Code==200&&confirmations==0,"升级丢失已信任配对");
                    updated.StopSharing();updated.StopRemote();
                }
                using(ToolsService stopped=new ToolsService(port,true,profile))
                {
                    stopped.RestoreSettings();Assert(stopped.Folder==null&&!stopped.RemoteEnabled&&stopped.SavedFolder==Path.GetFullPath(folder),"主动停止被重启绕过或目录丢失");
                    stopped.Share(stopped.SavedFolder);Assert(stopped.Folder==Path.GetFullPath(folder),"无法复用已选目录");
                }
            });
            run("目录失效不清配置，旧版无设置不擅自开启", () => {
                string profile=Path.Combine(root,"missing-"+Guid.NewGuid().ToString("N"));
                using(ToolsService fresh=new ToolsService(0,true,profile)){Assert(fresh.RestoreSettings()==null&&fresh.Folder==null&&!fresh.RemoteEnabled,"旧版默认开放");}
                string missing=Path.Combine(profile,"missing-share");new ToolsPreferences(profile).Save(missing,true,false);
                using(ToolsService unavailable=new ToolsService(0,true,profile)){Assert(unavailable.RestoreSettings()!=null&&unavailable.Folder==null,"失效目录被共享");}
                Assert(new ToolsPreferences(profile).Value.Folder==missing&&new ToolsPreferences(profile).Value.ShareEnabled,"删除了原设置");
                Directory.CreateDirectory(missing);
                using(ToolsService restored=new ToolsService(0,true,profile)){Assert(restored.RestoreSettings()==null&&restored.Folder==missing,"目录恢复后不能重开");}
            });
            run("恢复只读取旧配置，写入暂时受阻也能恢复共享和远控", () => {
                string profile=Path.Combine(root,"read-only-"+Guid.NewGuid().ToString("N"));
                new ToolsPreferences(profile).Save(folder,true,true);
                string path=Path.Combine(profile,"tools-settings.json");byte[] original=File.ReadAllBytes(path);
                using(FileStream locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(ToolsService restored=new ToolsService(0,true,profile))
                {
                    Assert(restored.RestoreSettings()==null,"恢复服务仍依赖重写配置");
                    Assert(restored.Folder==Path.GetFullPath(folder)&&restored.RemoteEnabled,"未恢复开启状态");
                    Assert(File.ReadAllBytes(path).SequenceEqual(original),"恢复覆盖了旧配置");
                    bool rejected=false;try{restored.StopSharing();}catch(IOException){rejected=true;}
                    Assert(rejected&&restored.Folder==null,"显式停止必须先停服务并报告保存失败");
                }
            });
            run("主程序网络启动后恢复服务，退出不把配置改成停止", () => {
                string profile=Path.Combine(root,"startup-tools-"+Guid.NewGuid().ToString("N"));int port;
                TcpListener probe=new TcpListener(IPAddress.Loopback,0);probe.Start();port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
                new ToolsPreferences(profile).Save(folder,true,true);
                using(LanService network=new LanService(new AppSettings {CloseToTray=false},port-1,true,"restore-test"))
                using(MainForm form=new MainForm(network,new LocalStore(profile)))
                {
                    form.PrepareNetworkCallbacks();form.NetworkStarted();
                    Reply state=Request(port,"/api/status");Assert(state.Code==200&&(bool)state.Json()["share"]&&(bool)state.Json()["remote"],"主程序没有恢复服务");
                    form.NetworkStarted();Assert(Request(port,"/api/status").Code==200,"重复启动回调冲突");
                }
                ToolsPreferences.State saved=new ToolsPreferences(profile).Value;Assert(saved.ShareEnabled&&saved.RemoteEnabled,"退出时清空开关");
            });
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
        private static void Serve(string root)
        {
            File.WriteAllText(Path.Combine(root, "input-events.jsonl"), "", new UTF8Encoding(false));
            using (ToolsService service = new ToolsService(0, true))
            {
                service.Share(Path.Combine(root, "shared")); service.ConfirmRemote = address => true;
                service.Capture = display => { using (Bitmap bitmap = new Bitmap(1200, 720)) using (Graphics g = Graphics.FromImage(bitmap)) using (MemoryStream output = new MemoryStream()) { g.Clear(Color.FromArgb(23, 33, 43)); g.DrawString("Remote input test fixture / not a live desktop", SystemFonts.DefaultFont, Brushes.White, 40, 60); bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg); return output.ToArray(); } };
                service.Displays = () => new { items = new[] { new { id = 0, name = "主屏幕 · 1200 × 720" }, new { id = 1, name = "测试副屏 · 1200 × 720" } } };
                service.Input = data => File.AppendAllText(Path.Combine(root, "input-events.jsonl"), new JavaScriptSerializer().Serialize(data) + "\n", new UTF8Encoding(false)); service.AllowRemote();
                File.WriteAllText(Path.Combine(root, "server.json"), new JavaScriptSerializer().Serialize(new { port = service.Port, share = service.ShareCode, remote = service.RemoteCode }), new UTF8Encoding(false));
                Stopwatch clock = Stopwatch.StartNew(); while (clock.Elapsed.TotalMinutes < 15 && !File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(100);
            }
        }
        private delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr parameter);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
        private static void ClosePackage(Process process)
        {
            if(process.HasExited)return;
            EnumWindows((window,unused)=>{uint id;GetWindowThreadProcessId(window,out id);if(id==process.Id){StringBuilder title=new StringBuilder(64);GetWindowText(window,title,title.Capacity);if(title.ToString()=="飞Q")PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);
            Assert(process.WaitForExit(5000),"测试程序未退出，请保留资料检查 PID="+process.Id);
        }
        private static void Upgrade(string root,string executable,string previousExecutable)
        {
            string profile=Path.Combine(root,"profile"),folder=Path.Combine(root,"shared");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"测试文件.txt"),"shared after replacement",new UTF8Encoding(false));
            int port=UpgradePort();
            new LocalStore(profile).SaveSettings(new AppSettings {Nickname="隔离升级验证",CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(root,"received")});
            string pair,remotePair;
            // 由旧成品的实际实现写出配置，不能用新源码生成数据冒充跨版本升级。
            Type oldService=System.Reflection.Assembly.LoadFile(previousExecutable).GetType("FeiqLight.ToolsService",true);
            const System.Reflection.BindingFlags members=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            using(IDisposable setup=(IDisposable)Activator.CreateInstance(oldService,members,null,new object[]{port,true,profile},null))
            {
                oldService.GetMethod("Share",members).Invoke(setup,new object[]{folder});
                string shareCode=(string)oldService.GetProperty("ShareCode",members).GetValue(setup,null);
                Reply login=Request(port,"/api/login","","{\"mode\":\"share\",\"code\":\""+shareCode+"\"}");Assert(login.Code==200,"旧版初始化共享配对失败");pair=PairCookie(login);
                oldService.GetMethod("AllowRemote",members).Invoke(setup,null);
                oldService.GetField("ConfirmRemoteDevice",members).SetValue(setup,new Func<string,int>(address=>2));
                string remoteCode=(string)oldService.GetProperty("RemoteCode",members).GetValue(setup,null);
                login=Request(port,"/api/login","","{\"mode\":\"remote\",\"code\":\""+remoteCode+"\"}");Assert(login.Code==200,"旧版初始化远控配对失败");remotePair=PairCookie(login);
            }
            byte[] pairings=File.ReadAllBytes(Path.Combine(profile,"tools-pairings.json"));
            for(int run=0;run<2;run++)
            {
                string package=Path.Combine(root,run==0?"first":"replacement");Directory.CreateDirectory(package);string copy=Path.Combine(package,"飞Q.exe");File.Copy(run==0?previousExecutable:executable,copy,true);
                using(Process process=Process.Start(new ProcessStartInfo(copy,"--autostart --loopback --port "+(port-1)+" --profile \""+profile+"\""){UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden}))
                {
                    try
                    {
                        bool ready=false;Stopwatch clock=Stopwatch.StartNew();
                        while(clock.ElapsedMilliseconds<12000&&!ready){Assert(!process.HasExited,"交付程序提前退出");try{Reply status=Request(port,"/api/status");ready=status.Code==200&&(bool)status.Json()["share"]&&(bool)status.Json()["remote"];}catch(SocketException){}if(!ready)Thread.Sleep(100);}
                        Assert(ready,"正式程序未恢复设置");Reply login=Request(port,"/api/reconnect",pair,"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n");Assert(login.Code==200,"换包后共享需重新配对");
                        Reply list=Request(port,"/api/list","fq_share="+login.Json()["token"]);Assert(list.Code==200&&Encoding.UTF8.GetString(list.Body).Contains("测试文件.txt"),"换包后目录丢失");
                        Reply control=Request(port,"/api/reconnect",remotePair,"{\"mode\":\"remote\"}","X-FQ-Reconnect: 1\r\n");Assert(control.Code==200,"换包后丢失始终允许");
                        string session="fq_remote="+control.Json()["token"];Request(port,"/api/logout",session,"{\"mode\":\"remote\"}",TokenHeader(session));
                    }
                    catch(Exception error){Console.WriteLine("升级验证失败："+error.Message+"；聊天端口="+(port-1)+"，共享端口="+port+"，PID="+process.Id);throw;}
                    finally{ClosePackage(process);}
                }
                Assert(File.ReadAllBytes(Path.Combine(profile,"tools-pairings.json")).SequenceEqual(pairings),"启动或升级重写了原配对记录");
            }
            Console.WriteLine("PASS packaged EXE upgrade "+FileVersionInfo.GetVersionInfo(previousExecutable).ProductVersion+" -> "+FileVersionInfo.GetVersionInfo(executable).ProductVersion+": shared directory, enabled switches and trusted pairings preserved; no desktop input injected");
        }
        private static int UpgradePort()
        {
            // 成品同时绑定聊天 UDP/TCP 和相邻 HTTP 端口，不能只检查 HTTP 是否空闲。
            for(int attempt=0;attempt<20;attempt++)
            {
                TcpListener http=new TcpListener(IPAddress.Loopback,0),chat=null;UdpClient udp=null;
                try
                {
                    http.Start();int port=((IPEndPoint)http.LocalEndpoint).Port;
                    chat=new TcpListener(IPAddress.Loopback,port-1);chat.Start();
                    udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,port-1));return port;
                }
                catch(SocketException){if(attempt==19)throw;}
                finally{if(udp!=null)udp.Close();if(chat!=null)chat.Stop();http.Stop();}
            }
            throw new IOException("无法分配升级验证端口");
        }
        private static void Desktop(string root)
        {
            using (Form form = new Form { Text = "飞Q隔离远控测试（不会操作其他软件）", StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(640, 400), TopMost = true })
            using (RichTextBox editor = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 16), AccessibleName = "远控测试输入框" })
            {
                form.Controls.Add(editor); Exception failure = null;
                form.Shown += async delegate {
                    try {
                        editor.Focus(); await Task.Delay(500); RemoteDesktop remote = new RemoteDesktop();
                        Assert(form.ContainsFocus && GetForegroundWindow() == form.Handle, "测试窗口不是前台窗口，已拒绝向其他软件输入");
                        Type native = typeof(RemoteDesktop).GetNestedType("NativeInput", System.Reflection.BindingFlags.NonPublic);
                        File.WriteAllText(Path.Combine(root, "desktop-layout.txt"), "size=" + System.Runtime.InteropServices.Marshal.SizeOf(native) + " union=" + System.Runtime.InteropServices.Marshal.OffsetOf(native, "Value"), new UTF8Encoding(false));
                        remote.Apply(new Dictionary<string, object> { { "type", "text" }, { "text", "飞Q中文 Codex 测试\n第二行 😀" } }); await Task.Delay(400);
                        File.WriteAllText(Path.Combine(root, "desktop-input-codepoints.txt"), String.Join(" ", editor.Text.Select(c => ((int)c).ToString("x4"))), new UTF8Encoding(false));
                        Assert(editor.Text.Replace("\r", "").Replace("\v", "\n") == "飞Q中文 Codex 测试\n第二行 😀", "真实 SendInput 中文/换行不一致");
                        Assert(form.ContainsFocus && GetForegroundWindow() == form.Handle, "测试窗口失去焦点，已停止输入");
                        remote.Apply(new Dictionary<string, object> { { "type", "key" }, { "key", "selectall" } }); await Task.Delay(150);
                        Assert(editor.SelectionLength == editor.TextLength, "Ctrl+A 未到测试窗口");
                        byte[] frame = remote.Capture(); using (MemoryStream buffer = new MemoryStream(frame)) using (Image image = Image.FromStream(buffer)) Assert(image.Width > 0 && image.Width <= 1600, "真实桌面捕获无效");
                        using (Bitmap snapshot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(snapshot, form.ClientRectangle); snapshot.Save(Path.Combine(root, "desktop-input.png")); }
                        Console.WriteLine("PASS actual SendInput Chinese / multiline / emoji / Ctrl+A and desktop JPEG capture");
                    } catch (Exception e) { failure = e; }
                    finally { form.Close(); }
                };
                Application.Run(form); if (failure != null) { File.WriteAllText(Path.Combine(root, "desktop-error.txt"), failure.Message, new UTF8Encoding(false)); Console.WriteLine("FAIL desktop test; see desktop-error.txt"); Environment.ExitCode = 1; }
            }
        }
        private static void WheelDesktop(string root)
        {
            using (Form form = new Form { Text = "飞Q隔离滚轮测试", StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(640, 400), TopMost = true })
            using (RichTextBox editor = new RichTextBox { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 16), ReadOnly = true })
            using (ToolsService service = new ToolsService(0, true))
            {
                editor.Lines = Enumerable.Range(1, 200).Select(i => "远程滚轮测试 / Scroll line " + i).ToArray();
                form.Controls.Add(editor); Exception failure = null; Point originalCursor = Cursor.Position;
                RemoteDesktop remote = new RemoteDesktop(); service.Input = remote.Apply; service.ReleaseInput = remote.Release;
                service.ConfirmRemote = address => true; service.AllowRemote();
                form.Shown += async delegate {
                    try {
                        editor.Focus(); editor.Select(0, 0); editor.ScrollToCaret(); await Task.Delay(300);
                        Assert(form.ContainsFocus && GetForegroundWindow() == form.Handle, "测试窗口不是前台，拒绝注入滚轮");
                        Cursor.Position = editor.PointToScreen(new Point(editor.Width / 2, editor.Height / 2));
                        string cookie = await Task.Run(() => Login(service, "remote"));
                        int initial = editor.GetCharIndexFromPosition(new Point(4, 4));
                        foreach (int delta in new[] { -360, 360 })
                        {
                            Assert(form.ContainsFocus && GetForegroundWindow() == form.Handle, "测试窗口失去焦点，停止滚轮输入");
                            Reply reply = await Task.Run(() => Request(service.Port, "/api/input", cookie, "{\"type\":\"wheel\",\"y\":" + delta + ",\"display\":0}", TokenHeader(cookie)));
                            Assert(reply.Code == 200, "滚轮 HTTP 请求失败"); await Task.Delay(300);
                            int current = editor.GetCharIndexFromPosition(new Point(4, 4));
                            Assert(delta < 0 ? current > initial : current < initial, "滚轮没有实际滚动或方向相反"); initial = current;
                        }
                        using (Bitmap snapshot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(snapshot, new Rectangle(Point.Empty, form.Size)); snapshot.Save(Path.Combine(root, "wheel-desktop.png")); }
                        service.StopRemote();
                        Assert((await Task.Run(() => Request(service.Port, "/api/input", cookie, "{\"type\":\"wheel\",\"y\":120}", TokenHeader(cookie)))).Code == 401, "停止后仍接受滚轮");
                        Console.WriteLine("PASS loopback HTTP -> SendInput -> actual RichTextBox scroll down/up; stopped session rejected");
                    } catch (Exception error) { failure = error; }
                    finally { Cursor.Position = originalCursor; form.Close(); }
                };
                Application.Run(form);
                if (failure != null) { Console.WriteLine("FAIL wheel desktop: " + failure); Environment.ExitCode = 1; }
            }
        }
        private static void Render(string root)
        {
            foreach (bool dark in new[] { false, true })
            {
                Theme.Configure(dark, "blue");
                using (ToolsWindow window = new ToolsWindow(-1, true))
                {
                    window.ClientSize = new Size(640, 560); window.Show(); Application.DoEvents();
                    using (Bitmap image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(root, "windows-tools-" + (dark ? "dark" : "light") + ".png")); }
                    window.ClientSize = new Size(540, 510); Application.DoEvents();
                    Panel body = window.Surface.Controls.OfType<Panel>().Single();
                    File.WriteAllText(Path.Combine(root, "windows-tools-layout.txt"), "client=" + body.ClientSize + " padding=" + body.Padding + " display=" + body.DisplayRectangle + "\n" + String.Join("\n", body.Controls.Cast<Control>().Select(c => c.GetType().Name + " " + c.Bounds + " margin=" + c.Margin)), new UTF8Encoding(false));
                    Assert(!body.HorizontalScroll.Visible, "共享窗口最小尺寸出现横向滚动条");
                    using (Bitmap image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(root, "windows-tools-minimum-" + (dark ? "dark" : "light") + ".png")); }
                    window.Close();
                }
                using (RemoteBanner banner = new RemoteBanner("192.168.100.123", () => { }))
                {
                    banner.Show(); Application.DoEvents();
                    using (Bitmap image = new Bitmap(banner.Width, banner.Height)) { banner.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(root, "windows-banner-" + (dark ? "dark" : "light") + ".png")); }
                    banner.Close();
                }
            }
            Console.WriteLine("PASS Windows tools light/dark and minimum-size rendering");
        }
    }
}
