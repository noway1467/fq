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
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
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
            // 测试回调异常交给测试计失败，不能弹出系统错误框阻塞用户桌面。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools-test-data")); Directory.CreateDirectory(root);
            if (args.Length > 0 && args[0] == "serve") { Serve(root); return 0; }
            if (args.Length > 0 && args[0] == "render") { Render(root); return 0; }
            if (args.Length > 0 && args[0] == "shadow") { RenderShadow(root); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "desktop") { Desktop(root); return Environment.ExitCode; }
            if (args.Length > 0 && args[0] == "wheel-desktop") { WheelDesktop(root); return Environment.ExitCode; }
            if (args.Length > 2 && args[0] == "benchmark") { Benchmark(root, Path.GetFullPath(args[2])); return Environment.ExitCode; }
            if (args.Length > 2 && args[0] == "upgrade") { try { Upgrade(root, Path.GetFullPath(args[2]), args.Length > 3 ? Path.GetFullPath(args[3]) : Path.GetFullPath(args[2])); return 0; } catch(Exception error) { Console.WriteLine("UPGRADE FAIL: " + error.GetType().Name + " " + error.Message); return 1; } }
            int passed = 0, failed = 0;
            Action<string, Action> run = (name, test) => { try { test(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); failed++; } };
            string folder = Path.Combine(root, "shared"); Directory.CreateDirectory(folder); Directory.CreateDirectory(Path.Combine(folder, "子目录"));
            File.WriteAllBytes(Path.Combine(folder, "视频 sample.mp4"), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()); File.WriteAllBytes(Path.Combine(folder, "empty.bin"), new byte[0]);
            run("FQF1：首帧、静止、128像素脏块、整帧回退及基线恢复", () => {
                using (RemoteDesktop desktop = new RemoteDesktop())
                using (Bitmap bitmap = new Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
                    byte[] first = desktop.EncodeFrame(bitmap, 0, 0); uint seq = FrameSequence(first);
                    File.WriteAllBytes(Path.Combine(root,"frame-full.fqf"),first);
                    Assert(Encoding.ASCII.GetString(first, 0, 4) == "FQF1" && first[4] == 1 && seq > 0 && first[17] == 1, "首帧头部");
                    byte[] idle = desktop.EncodeFrame(bitmap, 0, seq); Assert(idle.Length == 18 && idle[4] == 0 && FrameSequence(idle) == seq, "静止帧重复传像素");
                    object pixels = typeof(RemoteDesktop).GetField("currentPixels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(desktop);
                    desktop.EncodeFrame(bitmap, 0, seq);
                    Assert(Object.ReferenceEquals(pixels, typeof(RemoteDesktop).GetField("currentPixels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(desktop)), "静止帧重建像素数组");
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.FillRectangle(Brushes.Blue, 12, 12, 10, 10);
                    byte[] tile = desktop.EncodeFrame(bitmap, 0, seq); Assert(tile[4] == 2 && tile[17] == 1 && tile[23] == 128 && tile[25] == 128 && FrameSequence(tile) == seq + 1, "局部变化未返回128块");
                    File.WriteAllBytes(Path.Combine(root,"frame-tile.fqf"),tile);
                    using (MemoryStream bytes = new MemoryStream(tile, 30, tile.Length - 30)) using (Bitmap image = new Bitmap(bytes)) Assert(image.Width == 128 && image.GetPixel(16, 16).B > 150, "脏块JPEG内容错误");
                    seq = FrameSequence(tile);
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Green);
                    byte[] large = desktop.EncodeFrame(bitmap, 0, seq); Assert(large[4] == 1, "大面积变化未回退整帧");seq=FrameSequence(large);
                    File.WriteAllBytes(Path.Combine(root,"frame-large.fqf"),large);
                    byte[] lost = desktop.EncodeFrame(bitmap, 0, seq - 1); Assert(lost[4] == 1 && FrameSequence(lost)>seq, "丢帧未恢复基线");
                    byte[] switched = desktop.EncodeFrame(bitmap, 1, FrameSequence(lost));Assert(switched[4]==1, "切屏未强制整帧");
                    using(Bitmap resized=new Bitmap(320,240))Assert(desktop.EncodeFrame(resized,1,FrameSequence(switched))[4]==1,"尺寸变化未强制整帧");
                }
            });
            run("FQF1：静止请求短等待、旧JPEG兼容、等待中停止即时撤销", () => {
                using(ToolsService server=new ToolsService(0,true))
                using(RemoteDesktop desktop=new RemoteDesktop())
                using(Bitmap bitmap=new Bitmap(400,300))
                using(ManualResetEvent sampled=new ManualResetEvent(false))
                {
                    server.ConfirmRemote=address=>true;server.AllowRemote();string cookie=Login(server,"remote");int samples=0;
                    server.Capture=display=>new byte[]{255,216,255,217};
                    server.CaptureDelta=(display,since)=>{Interlocked.Increment(ref samples);sampled.Set();return desktop.EncodeFrame(bitmap,display,since);};
                    Reply first=Request(server.Port,"/api/screen?delta=1&since=0",cookie);Assert(first.Code==200&&first.Headers.Contains("application/octet-stream"),"二进制内容类型");
                    uint seq=FrameSequence(first.Body);Stopwatch watch=Stopwatch.StartNew();samples=0;
                    Reply idle=Request(server.Port,"/api/screen?delta=1&since="+seq,cookie);
                    Assert(idle.Code==200&&idle.Body.Length==18&&watch.ElapsedMilliseconds>=275&&samples<=5,"静止请求未节流或多传像素");
                    Assert(Request(server.Port,"/api/screen?display=0",cookie).Headers.Contains("image/jpeg"),"旧JPEG不兼容");
                    Assert(Request(server.Port,"/api/screen?delta=1&since=-1",cookie).Code==400,"无效帧序号未拒绝");
                    sampled.Reset();Task<Reply> pending=Task.Run(()=>Request(server.Port,"/api/screen?delta=1&since="+seq,cookie));
                    Assert(sampled.WaitOne(2000),"未开始长轮询");watch.Restart();server.StopRemote();
                    Assert(pending.Wait(1000)&&pending.Result.Code==401&&watch.ElapsedMilliseconds<250,"撤销后仍交付或等待旧帧");
                }
            });
            run("组合键：白名单、去重顺序、扩展键与安全热键拒绝", () => {
                Func<string,ushort[]> parse=json=>RemoteDesktop.ParseChord(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json));
                Assert(parse("{\"key\":\"escape\",\"modifiers\":[\"shift\",\"ctrl\",\"ctrl\"]}").SequenceEqual(new ushort[]{0x11,0x10,0x1b}),"修饰键顺序或去重");
                Assert(parse("{\"key\":\"win\",\"modifiers\":[]}").Single()==0x5b,"Win键");
                foreach(string key in new[]{"a","z","0","9","f1","f12","delete","insert","space","home","end","pageup","pagedown"})Assert(parse("{\"key\":\""+key+"\",\"modifiers\":[]}").Length==1,"主键 "+key);
                foreach(string json in new[]{"{\"key\":\"delete\",\"modifiers\":[\"ctrl\",\"alt\"]}","{\"key\":\"f12\",\"modifiers\":[\"alt\",\"ctrl\",\"shift\"]}","{\"key\":\"f13\",\"modifiers\":[]}","{\"key\":\"a\",\"modifiers\":[\"command\"]}","{\"key\":\"a\",\"modifiers\":\"ctrl\"}"})
                {bool rejected=false;try{parse(json);}catch(ToolsHttp.HttpError e){rejected=e.Code==400;}Assert(rejected,"未拒绝非法组合 "+json);}
                var keyMethod=typeof(RemoteDesktop).GetMethod("Key",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
                foreach(ushort key in new ushort[]{0x5b,0x2e,0x2d,0x25,0x21})
                {
                    object native=keyMethod.Invoke(null,new object[]{key,true,false});object union=native.GetType().GetField("Value",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(native);
                    object keyboard=union.GetType().GetField("Key",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(union);
                    Assert((uint)keyboard.GetType().GetField("Flags",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(keyboard)==3,"释放丢失扩展键标志");
                }
            });
            run("剪贴板：默认拒读、CSRF、文本边界、开关持久化不被共享覆盖", () => {
                string profile=Path.Combine(root,"clipboard-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile,"tools-settings.json"),"{\"Folders\":[],\"RemoteEnabled\":false}",new UTF8Encoding(false));
                using(ToolsService server=new ToolsService(0,true,profile))
                using(RemoteDesktop desktop=new RemoteDesktop())
                {
                    string stored="远控测试 😀\n下一行";int reads=0,writes=0;
                    server.Displays=desktop.Displays;server.ClipboardText=(text,authorized)=>{Assert(authorized(),"未复核会话");if(text==null){reads++;return stored;}stored=text;writes++;return "";};
                    server.ConfirmRemote=address=>true;server.AllowRemote();string cookie=Login(server,"remote");
                    Assert(!server.RemoteClipboardRead&&Request(server.Port,"/api/clipboard",cookie).Code==403&&reads==0,"旧配置默认开放读取");
                    Assert((bool)Request(server.Port,"/api/displays",cookie).Json()["clipboardWrite"],"缺少写入能力");
                    Assert(Request(server.Port,"/api/clipboard",cookie,"{\"text\":\"测试\"}").Code==403&&writes==0,"写入绕过CSRF");
                    foreach(string text in new[]{"",new string('x',4001),"a\0b","\ud800"})
                    {
                        bool rejected=false;try{ToolsService.ValidateClipboardText(text);}catch(ToolsHttp.HttpError e){rejected=e.Code==400;}Assert(rejected,"非法剪贴板内容未拒绝");
                    }
                    Assert(Request(server.Port,"/api/clipboard",cookie,new JavaScriptSerializer().Serialize(new{text=stored}),TokenHeader(cookie)).Code==200&&writes==1,"授权推送失败");
                    server.SetRemoteClipboardRead(true);server.Share(folder);server.StopSharing();
                    Assert((bool)Request(server.Port,"/api/status",cookie).Json()["clipboardRead"],"状态接口未同步开启读取");
                    Assert(server.RemoteClipboardRead&&new ToolsPreferences(profile).Value.RemoteClipboardRead,"保存共享覆盖剪贴板设置");
                    Assert((string)Request(server.Port,"/api/clipboard",cookie).Json()["text"]==stored&&reads==1,"单次读取失败");
                    server.SetRemoteClipboardRead(false);Assert(Request(server.Port,"/api/clipboard",cookie).Code==403&&reads==1,"关闭开关仍允许读取");
                    Assert(!(bool)Request(server.Port,"/api/status",cookie).Json()["clipboardRead"],"状态接口未同步关闭读取");
                    server.SetRemoteClipboardRead(true);server.StopRemote();Assert(Request(server.Port,"/api/clipboard",cookie).Code==401,"停止后仍可读取");
                }
                using(ToolsService restarted=new ToolsService(0,true,profile))Assert(restarted.RemoteClipboardRead,"重启丢失读取开关");
            });
            run("剪贴板：真实WinForms STA读写、换行、长文本拒绝与本机提示", () => {
                IDataObject previous=Clipboard.GetDataObject();
                try
                {
                    using(ToolsWindow window=new ToolsWindow(-1,true))
                    {
                        ToolsService server=(ToolsService)typeof(ToolsWindow).GetField("service",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(window);
                        server.ConfirmRemoteDevice=address=>1;server.AllowRemote();server.SetRemoteClipboardRead(true);string cookie=Login(server,"remote");
                        Func<string,Reply> uiRequest=data=>{
                            Task<Reply> request=Task.Run(()=>Request(server.Port,"/api/clipboard",cookie,data,data==null?"":TokenHeader(cookie)));
                            Stopwatch watch=Stopwatch.StartNew();while(!request.IsCompleted&&watch.ElapsedMilliseconds<6000){Application.DoEvents();Thread.Sleep(5);}
                            Assert(request.IsCompleted,"STA请求未完成");return request.Result;
                        };
                        Reply written=uiRequest("{\"text\":\"中文 😀\\n第二行\"}");Assert(written.Code==200,"STA写入失败 "+Encoding.UTF8.GetString(written.Body));
                        Assert(Clipboard.GetText()=="中文 😀\r\n第二行","未保留Unicode或规整CRLF");
                        Assert((string)uiRequest(null).Json()["text"]=="中文 😀\r\n第二行","STA读取失败");
                        Form banner=(Form)typeof(ToolsWindow).GetField("banner",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(window);
                        Assert(banner!=null&&Descendants(banner).OfType<Label>().Any(label=>label.Text=="控制端已读取本机剪贴板"),"缺少本机可见提示");
                        Clipboard.SetText(new string('x',4001));Assert(uiRequest(null).Code==409,"长剪贴板应明确拒绝");
                        server.StopRemote();
                    }
                }
                finally{if(previous==null)Clipboard.Clear();else Clipboard.SetDataObject(previous,true);}
            });
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
            run("多目录：追加、重复路径去重、逐目录读取及移除后的授权撤销", () => {
                string second = Path.Combine(root, "另一文件夹"); Directory.CreateDirectory(second); File.WriteAllText(Path.Combine(second, "second.txt"), "second folder", new UTF8Encoding(false));
                using (ToolsService server = new ToolsService(0, true))
                {
                    server.Share(folder); Reply first = Request(server.Port, "/api/login", "", "{\"mode\":\"share\",\"code\":\"" + server.ShareCode + "\"}");
                    string oldCookie = "fq_share=" + first.Json()["token"], oldPair = PairCookie(first);
                    server.AddFolders(new[] { second, folder + "\\" }); Assert(server.Folders.Length == 2 && server.SavedFolders.Length == 2, "追加覆盖旧目录或未去重");
                    Assert(Request(server.Port, "/api/list", oldCookie).Code == 401, "旧会话仍然有效"); Assert(Reconnect(server, "share", oldPair).Code == 401, "旧配对获得新目录权限");
                    string cookie = Login(server, "share"); var roots = Items(Request(server.Port, "/api/list", cookie)); Assert(roots.Length == 2 && roots.All(i => (bool)i["dir"]), "没有虚拟目录列表");
                    var secondRoot = roots.Single(i => (string)i["name"] == "另一文件夹"); var children = Items(Request(server.Port, "/api/list?id=" + secondRoot["id"], cookie)); string file = "/file?id=" + children.Single()["id"];
                    Assert(Encoding.UTF8.GetString(Request(server.Port, file, cookie).Body) == "second folder", "读取第二个目录失败");
                    Assert(Request(server.Port, "/file?id=" + secondRoot["id"], cookie).Code == 404, "把目录当文件读取");
                    server.RemoveFolder(second); Assert(server.Folders.SequenceEqual(new[] { folder }), "移除影响其他目录"); Assert(Request(server.Port, file, cookie).Code == 401, "移除未撤销下载");
                    string fresh = Login(server, "share"); Assert(Request(server.Port, file, fresh).Code == 404, "旧索引仍可读取移除目录"); Assert(File.Exists(Path.Combine(second, "second.txt")), "移除删除了用户文件");
                    Assert(Items(Request(server.Port, "/api/list", fresh)).Any(i => (string)i["name"] == "视频 sample.mp4"), "单目录旧浏览方式不兼容");
                    server.RemoveFolder(folder); Assert(server.Folder == null && server.SavedFolders.Length == 0, "移除最后一项未关闭");
                }
            });
            run("多目录设置：重启恢复、停止后移除不重开、无效目录追加不影响原共享", () => {
                string profile = Path.Combine(root, "multi-" + Guid.NewGuid().ToString("N")); string second = Path.Combine(folder, "子目录");
                using (ToolsService server = new ToolsService(0, true, profile))
                {
                    server.AddFolders(new[] { folder, second }); string cookie = Login(server, "share");
                    bool rejected = false; try { server.AddFolders(new[] { Path.Combine(root, "missing-" + Guid.NewGuid().ToString("N")) }); } catch (IOException) { rejected = true; }
                    Assert(rejected && Request(server.Port, "/api/list", cookie).Code == 200 && server.SavedFolders.Length == 2, "追加失败破坏原共享"); server.AllowRemote();
                }
                using (ToolsService server = new ToolsService(0, true, profile))
                {
                    Assert(server.RestoreSettings() == null && server.Folders.Length == 2 && server.RemoteEnabled, "多目录恢复失败或远控操作丢失目录");
                    server.StopSharing(); server.RemoveFolder(second); Assert(server.Folder == null && server.SavedFolders.SequenceEqual(new[] { folder }), "停止后移除重新开放共享");
                }
                using (ToolsService server = new ToolsService(0, true, profile)) { server.RestoreSettings(); Assert(server.Folder == null, "重启忽略停止意愿"); server.StartSavedSharing(); Assert(server.Folder == folder, "保留目录无法再次开启"); }
            });
            run("多目录迁移：旧 Folder 配置只读恢复、目录失效不悄悄共享其余目录", () => {
                string profile = Path.Combine(root, "legacy-multi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile); string path = Path.Combine(profile, "tools-settings.json");
                string original = new JavaScriptSerializer().Serialize(new { Folder = folder, ShareEnabled = true, RemoteEnabled = false }); File.WriteAllText(path, original, new UTF8Encoding(false));
                using (ToolsService server = new ToolsService(0, true, profile)) { Assert(server.RestoreSettings() == null && server.Folders.SequenceEqual(new[] { folder }), "旧配置迁移失败"); Assert(File.ReadAllText(path) == original, "恢复重写了旧配置"); }
                new ToolsPreferences(profile).SaveFolders(new[] { folder, Path.Combine(root, "missing-" + Guid.NewGuid().ToString("N")) }, true, false);
                using (ToolsService server = new ToolsService(0, true, profile)) { Assert(server.RestoreSettings() != null && server.Folder == null && server.SavedFolders.Length == 2, "失效目录被忽略或清除了原设置"); }
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
                    Assert(banner.FormBorderStyle == FormBorderStyle.None && banner.ShowInTaskbar, "浮条标题栏或任务栏入口错误");
                    var flags = (CreateParams)typeof(RemoteBanner).GetProperty("CreateParams", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(banner, null);
                    Assert((flags.ExStyle & 0x08000000) != 0, "浮条可能抢焦点");
                    Descendants(banner).OfType<Button>().Single(button => button.AccessibleName == "断开远控").PerformClick(); Assert(disconnected, "断开未执行"); banner.Close();
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
            run("关闭远控设置窗口和浮条只隐藏，手动停止才断开", () => {
                string profile=Path.Combine(root,"hide-tools-"+Guid.NewGuid().ToString("N"));
                using(ToolsWindow window=new ToolsWindow(-1,true,profile)) {
                    var members=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                    ToolsService service=(ToolsService)typeof(ToolsWindow).GetField("service",members).GetValue(window);
                    service.Share(folder);service.AllowRemote();service.ConfirmRemoteDevice=address=>2;
                    string shareCode=service.ShareCode,remoteCode=service.RemoteCode;window.Show();Application.DoEvents();window.Close();
                    Assert(!window.IsDisposed&&!window.Visible&&service.RemoteEnabled,"关闭窗口停止了服务");
                    Assert(service.ShareCode==shareCode&&service.RemoteCode==remoteCode,"关闭窗口更换了连接码");
                    string cookie=Login(service,"remote");Assert(service.RemoteActive,"隐藏面板后无法连接");
                    typeof(ToolsWindow).GetMethod("UpdateState",members).Invoke(window,null);Application.DoEvents();
                    Form banner=(Form)typeof(ToolsWindow).GetField("banner",members).GetValue(window);Assert(banner!=null,"没有远控浮条");banner.Close();
                    Assert(!banner.IsDisposed&&banner.WindowState==FormWindowState.Minimized&&service.RemoteActive,"关闭浮条断开了控制");
                    window.Show();Descendants(window.Surface).OfType<Button>().Single(b=>b.Text=="立即停止远控").PerformClick();
                    Assert(!service.RemoteEnabled&&!service.RemoteActive,"手动停止未生效");Assert(Request(service.Port,"/api/screen",cookie).Code==401,"手动停止后旧会话仍能操作");
                }
            });
            run("锁屏或注销不清除远控恢复意愿，重启继续连接上次", () => {
                string profile=Path.Combine(root,"session-restore-"+Guid.NewGuid().ToString("N")),pair;int port;
                using(ToolsService first=new ToolsService(0,true,profile)) {
                    first.AllowRemote();first.ConfirmRemoteDevice=address=>2;port=first.Port;
                    Reply login=Request(port,"/api/login","","{\"mode\":\"remote\",\"code\":\""+first.RemoteCode+"\"}");Assert(login.Code==200,"首次配对失败");pair=PairCookie(login);
                    first.SuspendRemote();Assert(!first.RemoteActive&&!first.RemoteEnabled,"系统暂停没有断开会话");Assert(new ToolsPreferences(profile).Value.RemoteEnabled,"注销清除了下次启动的开启状态");
                    first.ResumeRemote();Assert(first.RemoteEnabled,"解锁后没有恢复等待连接");first.SuspendRemote();
                }
                using(ToolsService restarted=new ToolsService(port,true,profile)) {
                    restarted.ConfirmRemoteDevice=address=>0;Assert(restarted.RestoreSettings()==null&&restarted.RemoteEnabled,"重启没有恢复远控");
                    Assert(Reconnect(restarted,"remote",pair).Code==200,"重启后无法连接上次设备");restarted.StopRemote();restarted.ResumeRemote();Assert(!restarted.RemoteEnabled,"系统恢复绕过了用户手动停止");
                }
            });
            run("本机确认勾选始终允许，真实控件返回设备信任选项", () => {
                using(ToolsWindow window=new ToolsWindow(-1,true))
                using(System.Windows.Forms.Timer click=new System.Windows.Forms.Timer { Interval=100 })
                {
                    window.Show();Application.DoEvents();bool checkedBox=false;
                    click.Tick+=delegate {
                        ConfirmDialog prompt=Application.OpenForms.OfType<ConfirmDialog>().FirstOrDefault(f=>f.Text=="是否允许远程控制？");if(prompt==null)return;
                        click.Stop();
                        var controls=Descendants(prompt.Surface).ToArray();var always=controls.OfType<ToggleSwitch>().Single(c=>c.AccessibleName=="始终允许此设备");always.Checked=true;checkedBox=true;
                        controls.OfType<Button>().Single(b=>b.Text=="允许本次控制").PerformClick();
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
            run("status 接口如实反映配对状态且原生跳转端点返回 204", () => {
                string profile=Path.Combine(root,"status-tools-"+Guid.NewGuid().ToString("N"));
                using(ToolsService server=new ToolsService(0,true,profile))
                {
                    server.Share(folder);server.ConfirmRemoteDevice=address=>2;server.AllowRemote();
                    Reply anonymous=Request(server.Port,"/api/status");
                    Assert(anonymous.Code==200&&!(bool)anonymous.Json()["paired"]&&!(bool)anonymous.Json()["remotePaired"]&&!(bool)anonymous.Json()["trusted"],"匿名请求不应报告已配对");
                    Reply shareLogin=Request(server.Port,"/api/login","","{\"mode\":\"share\",\"code\":\""+server.ShareCode+"\"}");
                    string sharePair=PairCookie(shareLogin);
                    Reply shareOnly=Request(server.Port,"/api/status",sharePair);
                    Assert(shareOnly.Code==200&&(bool)shareOnly.Json()["paired"]&&!(bool)shareOnly.Json()["remotePaired"],"共享配对后 paired 应为 true");
                    Reply remoteLogin=Request(server.Port,"/api/login","","{\"mode\":\"remote\",\"code\":\""+server.RemoteCode+"\"}");
                    string remotePair=PairCookie(remoteLogin);
                    Reply both=Request(server.Port,"/api/status",sharePair+"; "+remotePair);
                    Assert(both.Code==200&&(bool)both.Json()["paired"]&&(bool)both.Json()["remotePaired"]&&(bool)both.Json()["trusted"],"远控配对后 remotePaired 与 trusted 应为 true");
                    Assert(Request(server.Port,"/native/paired").Code==204&&Request(server.Port,"/native/unavailable").Code==204,"原生状态通知端点应返回 204");
                }
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
                service.Displays = read => new { items = new[] { new { id = 0, name = "主屏幕 · 1200 × 720" }, new { id = 1, name = "测试副屏 · 1200 × 720" } } };
                service.Input = data => File.AppendAllText(Path.Combine(root, "input-events.jsonl"), new JavaScriptSerializer().Serialize(data) + "\n", new UTF8Encoding(false)); service.AllowRemote();
                File.WriteAllText(Path.Combine(root, "server.json"), new JavaScriptSerializer().Serialize(new { port = service.Port, share = service.ShareCode, remote = service.RemoteCode }), new UTF8Encoding(false));
                Stopwatch clock = Stopwatch.StartNew(); while (clock.Elapsed.TotalMinutes < 15 && !File.Exists(Path.Combine(root, "stop"))) Thread.Sleep(100);
            }
        }
        private static Dictionary<string, object>[] Items(Reply reply)
        {
            Assert(reply.Code == 200, "目录请求失败"); return ((System.Collections.IEnumerable)reply.Json()["items"]).Cast<Dictionary<string, object>>().ToArray();
        }
        private static uint FrameSequence(byte[] frame) { return ((uint)frame[8]<<24)|((uint)frame[9]<<16)|((uint)frame[10]<<8)|frame[11]; }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
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
            new LocalStore(profile).SaveSettings(new AppSettings {Port=port-1,Nickname="隔离升级验证",CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(root,"received")});
            string pair,remotePair;
            // 由旧成品的实际实现写出配置，不能用新源码生成数据冒充跨版本升级。
            Type oldService=System.Reflection.Assembly.LoadFile(previousExecutable).GetType("FeiqLight.ToolsService",true);
            const System.Reflection.BindingFlags members=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            object[] constructor=oldService.GetConstructor(members,null,new[]{typeof(int),typeof(bool),typeof(string)},null)!=null?new object[]{port,true,profile}:new object[]{port,true,profile,null};
            using(IDisposable setup=(IDisposable)Activator.CreateInstance(oldService,members,null,constructor,null))
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
            using (RemoteDesktop remote = new RemoteDesktop())
            {
                form.Controls.Add(editor); Exception failure = null;
                form.Shown += async delegate {
                    try {
                        editor.Focus(); await Task.Delay(500);
                        Assert(form.ContainsFocus && GetForegroundWindow() == form.Handle, "测试窗口不是前台窗口，已拒绝向其他软件输入");
                        Type native = typeof(RemoteDesktop).GetNestedType("NativeInput", System.Reflection.BindingFlags.NonPublic);
                        File.WriteAllText(Path.Combine(root, "desktop-layout.txt"), "size=" + System.Runtime.InteropServices.Marshal.SizeOf(native) + " union=" + System.Runtime.InteropServices.Marshal.OffsetOf(native, "Value"), new UTF8Encoding(false));
                        remote.Apply(new Dictionary<string, object> { { "type", "text" }, { "text", "飞Q中文 Codex 测试\n第二行 😀" } }); await Task.Delay(400);
                        File.WriteAllText(Path.Combine(root, "desktop-input-codepoints.txt"), String.Join(" ", editor.Text.Select(c => ((int)c).ToString("x4"))), new UTF8Encoding(false));
                        Assert(editor.Text.Replace("\r", "").Replace("\v", "\n") == "飞Q中文 Codex 测试\n第二行 😀", "真实 SendInput 中文/换行不一致");
                        Assert(form.ContainsFocus && GetForegroundWindow() == form.Handle, "测试窗口失去焦点，已停止输入");
                        remote.Apply(new Dictionary<string, object> { { "type", "key" }, { "key", "selectall" } }); await Task.Delay(150);
                        Assert(editor.SelectionLength == editor.TextLength, "Ctrl+A 未到测试窗口");
                        editor.Select(editor.TextLength,0);
                        remote.Apply(new Dictionary<string,object>{{"type","chord"},{"key","a"},{"modifiers",new object[]{"ctrl"}}});await Task.Delay(150);
                        Assert(editor.SelectionLength==editor.TextLength,"新chord Ctrl+A未到测试窗口");
                        byte[] frame = remote.Capture(); using (MemoryStream buffer = new MemoryStream(frame)) using (Image image = Image.FromStream(buffer)) Assert(image.Width > 0 && image.Width <= 1600, "真实桌面捕获无效");
                        using (Bitmap snapshot = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(snapshot, form.ClientRectangle); snapshot.Save(Path.Combine(root, "desktop-input.png")); }
                        Console.WriteLine("PASS actual SendInput Chinese / multiline / emoji / legacy and new chord Ctrl+A / desktop JPEG capture");
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
            using (RemoteDesktop remote = new RemoteDesktop())
            {
                editor.Lines = Enumerable.Range(1, 200).Select(i => "远程滚轮测试 / Scroll line " + i).ToArray();
                form.Controls.Add(editor); Exception failure = null; Point originalCursor = Cursor.Position;
                service.Input = remote.Apply; service.ReleaseInput = remote.Release;
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
                    using (Bitmap image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); CheckWindowBorder(image); image.Save(Path.Combine(root, "windows-tools-" + (dark ? "dark" : "light") + ".png")); }
                    window.MinimumSize = Size.Empty; window.ClientSize = new Size(540, 510); Application.DoEvents();
                    Panel body = window.Surface.Controls.OfType<Panel>().Single();
                    File.WriteAllText(Path.Combine(root, "windows-tools-layout.txt"), "client=" + body.ClientSize + " padding=" + body.Padding + " display=" + body.DisplayRectangle + "\n" + String.Join("\n", body.Controls.Cast<Control>().Select(c => c.GetType().Name + " " + c.Bounds + " margin=" + c.Margin)), new UTF8Encoding(false));
                    Assert(!body.HorizontalScroll.Visible, "共享窗口最小尺寸出现横向滚动条");
                    using (Bitmap image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(root, "windows-tools-minimum-" + (dark ? "dark" : "light") + ".png")); }
                    ToolsService service=(ToolsService)typeof(ToolsWindow).GetField("service",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(window);
                    service.Share(root);typeof(ToolsWindow).GetMethod("UpdateState",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(window,null);Application.DoEvents();
                    foreach(string field in new[]{"codeCaption","shareCode"})
                    {
                        Label label=(Label)typeof(ToolsWindow).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(window);
                        using(Graphics graphics=label.CreateGraphics())
                        {
                            Size required=TextRenderer.MeasureText(graphics,label.Text,label.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
                            Assert(label.ClientSize.Height>=required.Height+label.Padding.Vertical&&label.ClientSize.Width>=required.Width,"连接码文字被裁切 "+field+" actual="+label.ClientSize+" required="+required);
                        }
                    }
                    using(Bitmap image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(root,"windows-tools-active-minimum-"+(dark?"dark":"light")+".png"));}
                    window.Close();
                }
                using (RemoteBanner banner = new RemoteBanner("192.168.100.123", () => { }))
                {
                    banner.Show(); Application.DoEvents();
                    using (Bitmap image = new Bitmap(banner.Width, banner.Height)) { banner.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); CheckWindowBorder(image); image.Save(Path.Combine(root, "windows-banner-" + (dark ? "dark" : "light") + ".png")); }
                    banner.Close();
                }
            }
            Console.WriteLine("PASS Windows tools light/dark and minimum-size rendering");
        }
        private static void CheckWindowBorder(Bitmap image)
        {
            for(int x=0;x<image.Width;x++)Assert(image.GetPixel(x,0).ToArgb()==Color.Black.ToArgb()&&image.GetPixel(x,image.Height-1).ToArgb()==Color.Black.ToArgb(),"上下边框不是完整黑色实线");
            for(int y=0;y<image.Height;y++)Assert(image.GetPixel(0,y).ToArgb()==Color.Black.ToArgb()&&image.GetPixel(image.Width-1,y).ToArgb()==Color.Black.ToArgb(),"左右边框不是完整黑色实线");
            Assert(image.GetPixel(1,image.Height/2).ToArgb()!=Color.Black.ToArgb()&&image.GetPixel(image.Width-2,image.Height/2).ToArgb()!=Color.Black.ToArgb(),"边框超过1px");
        }
        private static void RenderShadow(string root)
        {
            using(Form backdrop=new Form{Text="飞Q窗口阴影验证",FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.CenterScreen,ClientSize=new Size(900,620),BackColor=Color.FromArgb(235,235,235),TopMost=true})
            {
                Exception failure=null;
                backdrop.Shown+=async delegate{
                    try{
                        foreach(bool dark in new[]{false,true}){
                            Theme.Configure(dark,"blue");
                            using(ModernWindow window=new ModernWindow{Text="窗口边框与阴影",StartPosition=FormStartPosition.Manual,ClientSize=new Size(450,320),TopMost=true})
                            using(RemoteBanner banner=new RemoteBanner("隔离测试设备",()=>{})){
                                window.Location=backdrop.PointToScreen(new Point(60,60));window.Show(backdrop);
                                banner.Location=backdrop.PointToScreen(new Point(470,440));banner.Show(backdrop);await Task.Delay(400);
                                foreach(Point point in new[]{new Point(10,10),new Point(890,10),new Point(10,610),new Point(890,610),new Point(300,250),new Point(600,480)}){
                                    uint process;GetWindowThreadProcessId(WindowFromPoint(backdrop.PointToScreen(point)),out process);
                                    Assert(process==(uint)Process.GetCurrentProcess().Id,"隔离测试画面被其他程序遮挡，未采集桌面");
                                }
                                using(Bitmap capture=new Bitmap(backdrop.ClientSize.Width,backdrop.ClientSize.Height)){
                                    using(Graphics graphics=Graphics.FromImage(capture))graphics.CopyFromScreen(backdrop.PointToScreen(Point.Empty),Point.Empty,backdrop.ClientSize);
                                    capture.Save(Path.Combine(root,"window-shadow-"+(dark?"dark":"light")+".png"));
                                }
                            }
                        }
                        Console.WriteLine("PASS 系统合成阴影实际桌面截图");
                    }catch(Exception error){failure=error;}finally{backdrop.Close();}
                };
                Application.Run(backdrop);if(failure!=null){Console.WriteLine("FAIL shadow: "+failure);Environment.ExitCode=1;}
            }
        }
        private static void Benchmark(string root, string previousExecutable)
        {
            AppDomain.MonitoringIsEnabled=true;
            var oldType=System.Reflection.Assembly.LoadFile(previousExecutable).GetType("FeiqLight.RemoteDesktop",true);
            object oldDesktop=Activator.CreateInstance(oldType,true);
            var oldCapture=oldType.GetMethod("Capture",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            List<object> results=new List<object>();int phase=0,tick=0;
            using(Form surface=new Form{Text="飞Q远控性能验证（隔离测试画面）",FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Bounds=Screen.PrimaryScreen.Bounds,TopMost=true})
            using(System.Windows.Forms.Timer animation=new System.Windows.Forms.Timer{Interval=100})
            {
                surface.Paint+=delegate(object sender,PaintEventArgs e){
                    e.Graphics.Clear(phase==2&&tick%2==1?Color.DarkSlateBlue:Color.FromArgb(22,36,50));
                    e.Graphics.DrawString("飞Q远控性能验证 / 仅捕获本测试画面",SystemFonts.CaptionFont,Brushes.White,40,40);
                    e.Graphics.DrawString(phase==0?"静止画面":phase==1?"局部变化":"整屏变化",SystemFonts.DefaultFont,Brushes.White,40,72);
                    if(phase==1)e.Graphics.FillRectangle(Brushes.CornflowerBlue,120+(tick%20)*5,140,40,40);
                };
                animation.Tick+=delegate{if(phase!=0){tick++;surface.Invalidate();}};
                Exception failure=null;
                surface.Shown+=async delegate{
                    try
                    {
                        animation.Start();await Task.Delay(300);
                        for(phase=0;phase<3;phase++)
                        {
                            tick=0;surface.Invalidate();await Task.Delay(150);
                            foreach(bool delta in new[]{false,true})
                            {
                                using(RemoteDesktop desktop=new RemoteDesktop())
                                using(ToolsService server=new ToolsService(0,true))
                                {
                                    int captures=0;long captureTicks=0,captureAllocated=0;
                                    server.Capture=display=>{
                                        long memory=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;Stopwatch captureClock=Stopwatch.StartNew();
                                        byte[] bytes=(byte[])oldCapture.Invoke(oldDesktop,new object[]{display});
                                        captureTicks+=captureClock.ElapsedTicks;captureAllocated+=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-memory;captures++;return bytes;
                                    };
                                    server.CaptureDelta=(display,since)=>{
                                        long memory=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;Stopwatch captureClock=Stopwatch.StartNew();byte[] bytes=desktop.CaptureDelta(display,since);
                                        captureTicks+=captureClock.ElapsedTicks;captureAllocated+=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-memory;captures++;return bytes;
                                    };
                                    server.ConfirmRemote=address=>true;server.AllowRemote();string cookie=await Task.Run(()=>Login(server,"remote"));
                                    // 首次分配两份像素数组是常驻缓存，热路径测量单独预热后计数。
                                    Reply warmup=await Task.Run(()=>Request(server.Port,delta?"/api/screen?display=0&delta=1&since=0":"/api/screen?display=0",cookie));
                                    Assert(warmup.Code==200,"性能预热失败");captures=0;captureTicks=0;captureAllocated=0;
                                    Stopwatch duration=Stopwatch.StartNew();uint seq=delta?FrameSequence(warmup.Body):0;int requests=0,updates=0,full=0,tiles=0;long bytesTotal=0;
                                    while(duration.ElapsedMilliseconds<10000)
                                    {
                                        Reply reply=await Task.Run(()=>Request(server.Port,delta?"/api/screen?display=0&delta=1&since="+seq:"/api/screen?display=0",cookie));
                                        Assert(reply.Code==200,"性能回环请求失败");requests++;bytesTotal+=reply.Body.Length+Encoding.ASCII.GetByteCount(reply.Headers)+4;
                                        if(delta){seq=FrameSequence(reply.Body);if(reply.Body[4]!=0)updates++;if(reply.Body[4]==1)full++;if(reply.Body[4]==2)tiles++;}
                                        else{updates++;full++;}
                                        await Task.Delay(delta?16:250);
                                    }
                                    var sample=new{scenario=phase==0?"static":phase==1?"local":"full",mode=delta?"FQF1":"old-JPEG",seconds=duration.Elapsed.TotalSeconds,requests,bytesTotal,updates,fps=updates/duration.Elapsed.TotalSeconds,full,tiles,captures,averageCaptureMs=captureTicks*1000.0/Stopwatch.Frequency/captures,managedAllocatedBytes=captureAllocated};
                                    results.Add(sample);Console.WriteLine(new JavaScriptSerializer().Serialize(sample));
                                }
                            }
                        }
                    }
                    catch(Exception error){failure=error;}
                    finally{animation.Stop();surface.Close();}
                };
                Application.Run(surface);
                File.WriteAllText(Path.Combine(root,"performance.json"),new JavaScriptSerializer().Serialize(results),new UTF8Encoding(false));
                if(failure!=null){Console.WriteLine("FAIL benchmark: "+failure);Environment.ExitCode=1;}
            }
        }
    }
}
