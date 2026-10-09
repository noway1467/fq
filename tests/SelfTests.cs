using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FeiqLight.Tests
{
    internal static class SelfTests
    {
        private static int passed;
        private static int failed;
        private static string root;
        private static readonly List<string> results = new List<string>();
        private static string currentTest="测试初始化/收尾";
        private static int uiExceptions;
        internal static void InstallUiExceptionHandler() { System.Windows.Forms.Application.ThreadException -= CaptureUiException; System.Windows.Forms.Application.ThreadException += CaptureUiException; }
        private static void CaptureUiException(object sender,ThreadExceptionEventArgs args) {
            uiExceptions++;failed++;string details="FAIL UI CALLBACK ["+currentTest+"]: "+args.Exception;Log(details);
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-exceptions.log"),DateTime.Now.ToString("o")+"\r\n"+details+"\r\n",new UTF8Encoding(false));
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Run(string name, Action test)
        {
            Stopwatch clock = Stopwatch.StartNew();currentTest=name;int before=uiExceptions;InstallUiExceptionHandler();
            try { test(); if(uiExceptions==before){passed++;Log("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)");} }
            catch (Exception error) { failed++; Log("FAIL " + name + ": " + error); }
            finally {currentTest="测试初始化/收尾";InstallUiExceptionHandler();}
        }
        private static void Log(string message) { Console.WriteLine(message); results.Add(message); }
        private static void Wait(Func<bool> condition, string message, int timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < timeout) Thread.Sleep(10);
            Assert(condition(), message);
        }
        internal static int FreePort()
        {
            SocketException last=null;
            // Windows 的 TCP/UDP 保留区间可能不同；只在测试选端口阶段换候选，不吞应用实际连接错误。
            for(int attempt=0;attempt<32;attempt++) {
                TcpListener probe=new TcpListener(IPAddress.Loopback,0);
                try {probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;using(UdpClient socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,port)))return port;}
                catch(SocketException e){if(e.SocketErrorCode!=SocketError.AddressAlreadyInUse&&e.SocketErrorCode!=SocketError.AccessDenied)throw;last=e;}
                finally {probe.Stop();}
            }
            throw new IOException("未找到同时可绑定 TCP/UDP 的回环测试端口",last);
        }
        private static IPEndPoint Endpoint(int port) { return new IPEndPoint(IPAddress.Loopback, port); }
        private static void ExpectFailure(Action action, string message)
        {
            bool failed = false; try { action(); } catch { failed = true; } Assert(failed, message);
        }
        private static bool EqualFiles(string first, string second)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream a = File.OpenRead(first))
            using (FileStream b = File.OpenRead(second)) return sha.ComputeHash(a).SequenceEqual(sha.ComputeHash(b));
        }
        private static byte[] Fetch(int port, long packet, int file, string local)
        {
            using (TcpClient tcp = new TcpClient(new IPEndPoint(IPAddress.Parse(local), 0)))
            {
                tcp.Connect(IPAddress.Loopback, port); tcp.ReceiveTimeout = 3000;
                NetworkStream stream = tcp.GetStream();
                byte[] request = Protocol.Encode(10, "requester", "local", Protocol.GetFile, packet.ToString("x") + ":" + file.ToString("x") + ":0:", null);
                stream.Write(request, 0, request.Length); byte[] buffer = new byte[256]; int read = stream.Read(buffer, 0, buffer.Length);
                return buffer.Take(read).ToArray();
            }
        }
        [STAThread]
        public static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            // UI 消息循环异常不会沿普通 test() 调用栈抛出，必须单独计失败，不能弹窗后仍宣称通过。
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            InstallUiExceptionHandler();
            // 包测试的解压路径较深，测试资料仍放工作区 build/test-data，避免测试自身叠出 MAX_PATH。
            string dataRoot=Environment.GetEnvironmentVariable("FEIQ_TEST_DATA_ROOT");
            if(String.IsNullOrWhiteSpace(dataRoot)) dataRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data");
            root = Path.Combine(Path.GetFullPath(dataRoot), "FeiqLight-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                Run("同名会话：固定编号、旧配置迁移、隐藏备注及新增身份不重排", () => {
                    LocalStore store=new LocalStore(Path.Combine(root,"conversation-numbers"));
                    Peer[] peers=Enumerable.Range(1,5).Select(i=>new Peer {Endpoint=Endpoint(32425),Login="phone-"+i,Nickname="phone"}).ToArray();
                    Peer.UpdateConversationNames(peers);Assert(peers.Select(p=>p.DisplayName).Distinct().Count()==5&&peers.All(p=>p.ConversationNumber>0),"同地址不同登录身份仍重名");
                    var names=peers.ToDictionary(p=>p.Id,p=>p.DisplayName);peers[0].Online=true;peers[1].Hidden=true;
                    Peer.UpdateConversationNames(peers.Reverse());Assert(peers.All(p=>p.DisplayName==names[p.Id]),"上下线、隐藏或重排改变编号");
                    AppSettings settings=new AppSettings {Conversations=peers.Select(p=>new SavedConversation {Endpoint=p.Endpoint.ToString(),Login=p.Login,Nickname=p.Nickname,ConversationNumber=p.ConversationNumber,Hidden=p.Hidden}).ToArray()};
                    store.SaveSettings(settings);Peer[] restored=store.LoadSettings().Conversations.Select(c=>c.OfflinePeer()).ToArray();Peer.UpdateConversationNames(restored);
                    Assert(restored.All(p=>p.DisplayName==names[p.Id]),"重启后编号变化");
                    Peer added=new Peer {Endpoint=Endpoint(32424),Login="new",Nickname="phone"};Peer.UpdateConversationNames(restored.Concat(new[]{added}));
                    Assert(added.ConversationNumber==6&&restored.All(p=>p.DisplayName==names[p.Id]),"新增身份抢占旧编号");
                    restored[0].Note="我的手机";Peer.UpdateConversationNames(restored);Assert(restored[0].DisplayName=="我的手机","独有备注仍带多余编号");
                    restored[1].Note="我的手机";Peer.UpdateConversationNames(restored);Assert(restored[0].DisplayName!=restored[1].DisplayName&&restored[0].DuplicateName,"同名备注未区分");
                    restored[0].ConversationNumber=restored[1].ConversationNumber;restored[2].ConversationNumber=-1;Peer.UpdateConversationNames(restored);
                    Assert(restored.Select(p=>p.ConversationNumber).Distinct().Count()==5&&restored.All(p=>p.ConversationNumber>0),"损坏或重复编号未修复");
                    restored[0].ConversationNumber=1000000;restored[1].ConversationNumber=0;Peer.UpdateConversationNames(restored);var repaired=restored.Select(p=>p.ConversationNumber).ToArray();Peer.UpdateConversationNames(restored.Reverse());
                    Assert(restored.Select(p=>p.ConversationNumber).SequenceEqual(repaired)&&repaired.All(n=>n>0&&n<=1000000),"编号边界导致每次刷新重新编号");
                });
                Run("会话管理：备注与置顶持久化、隐藏不丢历史、清空隔离及迟到 ACK", () => {
                    LocalStore store=new LocalStore(Path.Combine(root,"conversation-management"));AppSettings settings=new AppSettings();
                    SavedConversation saved=new SavedConversation {Endpoint="127.0.0.1:32425",Login="alice",Nickname="对方昵称",Note="我的备注",Pinned=true,Hidden=true};
                    settings.Conversations=new[]{saved};settings.ChatFontSize=16;settings.ChatBackground="lines";store.SaveSettings(settings);
                    AppSettings loaded=store.LoadSettings();Peer peer=loaded.Conversations[0].OfflinePeer();Assert(peer.DisplayName=="我的备注"&&peer.Nickname=="对方昵称"&&peer.Pinned&&peer.Hidden,"本地会话属性未恢复");Assert(loaded.ChatFontSize==16&&loaded.ChatBackground=="lines","外观未持久化");
                    ChatRecord first=new ChatRecord {PeerId=peer.Id,Packet=1,Text="保留后再清空",Time=DateTime.Now.AddSeconds(-1),Outgoing=true,State="等待确认"};
                    ChatRecord other=new ChatRecord {PeerId="other",Packet=2,Text="不受影响",Time=DateTime.Now,State="已收到"};store.Append(first);store.Append(other);store.SaveDraft(peer.Id,"保留草稿");
                    Assert(store.History(peer.Id,20).Count==1,"隐藏误删历史");store.ClearHistory(peer.Id);first.State="已送达";store.Append(first);store.CompletePending();
                    Assert(store.History(peer.Id,20).Count==0&&store.History("other",20).Count==1,"清空复活旧消息或影响另一会话");Assert(store.LoadDraft(peer.Id)=="保留草稿","清空误删草稿");
                    first.Packet=3;first.Time=DateTime.Now.AddSeconds(1);store.Append(first);Assert(store.History(peer.Id,20).Count==1,"清空后无法写新消息");
                });
                Run("默认打开：任意附件使用 Shell 关联，不经过命令解释器", () => {
                    string file=Path.Combine(root,"带 空格的附件.apk");File.WriteAllText(file,"test");var info=MediaPreview.DefaultOpenInfo(file);Assert(info.UseShellExecute&&info.Verb=="open"&&info.FileName==file&&String.IsNullOrEmpty(info.Arguments),"没有使用系统文件关联");ExpectFailure(()=>MediaPreview.DefaultOpenInfo("missing.apk"),"相对/不存在文件未拒绝");
                });
                Run("主题色板：薄荷、暮紫与经典的面板和背景均有区别", () => {
                    Theme.Configure(false,"blue");var original=Theme.Surface;var wall=Theme.Wallpaper;Theme.Configure(false,"green");Assert(Theme.Surface!=original&&Theme.Wallpaper!=wall,"薄荷仍只换强调色");Theme.Configure(true,"blue");original=Theme.Surface;wall=Theme.Wallpaper;Theme.Configure(true,"purple");Assert(Theme.Surface!=original&&Theme.Wallpaper!=wall,"暮紫仍只换强调色");Theme.Configure(false,"blue");
                });
                Run("网页链接：多链接、中文标点、www、查询串与危险协议隔离", () => {
                    var links=MessageLinks.Find("看 https://example.com/a?q=1&x=2。以及 www.example.org/path，(https://example.net/a(b))");
                    Assert(links.Count==3,"链接数量不正确");Assert(links[0].Url=="https://example.com/a?q=1&x=2"&&links[1].Url.StartsWith("https://www.example.org/"),"链接或查询参数丢失");
                    Assert(links[2].Url.EndsWith("a(b)"),"括号截断错误");Assert(MessageLinks.Find("javascript:alert(1) file:///C:/abc ftp://host/path https://user:pass@example.org").Count==0,"危险协议或凭证地址被识别");
                });
                Run("开机启动：旧配置默认关闭、真实快捷方式创建/更新/删除与幂等", () =>
                {
                    string folder=Path.Combine(root,"startup"), profile=Path.Combine(root,"自动启动 资料");
                    LocalStore store=new LocalStore(profile);
                    File.WriteAllText(Path.Combine(profile,"settings.json"),"{\"Nickname\":\"旧配置\",\"CloseToTray\":true}");
                    Assert(!store.LoadSettings().StartWithWindows,"旧配置擅自开启自启动");
                    string executable=typeof(MainForm).Assembly.Location;
                    WindowsStartup startup=new WindowsStartup(folder,executable,profile,32425,true);
                    Assert(startup.ShortcutPath==new WindowsStartup(folder,executable,profile+Path.DirectorySeparatorChar,32425,true).ShortcutPath,"资料路径尾部分隔符导致重复启动项");
                    Assert(!startup.IsEnabled,"未创建启动项却显示启用");
                    AppSettings previous=store.LoadSettings(), next=store.LoadSettings();next.StartWithWindows=true;
                    startup.SaveSettings(store,previous,next);
                    Assert(startup.IsEnabled&&store.LoadSettings().StartWithWindows,"开启未持久化或快捷方式参数不匹配");
                    byte[] bytes=File.ReadAllBytes(startup.ShortcutPath);startup.SetEnabled(true);
                    Assert(bytes.SequenceEqual(File.ReadAllBytes(startup.ShortcutPath)),"重复开启重建了启动项");
                    WindowsStartup other=new WindowsStartup(folder,executable,Path.Combine(root,"other-profile"),32426,true);other.SetEnabled(true);
                    string moved=Path.Combine(root,"移动后的 飞Q.exe");File.Copy(executable,moved);
                    WindowsStartup relocated=new WindowsStartup(folder,moved,profile,32425,true);relocated.SetEnabled(true);
                    Assert(relocated.IsEnabled&&!startup.IsEnabled,"程序移动后启动路径没有更新");
                    previous=store.LoadSettings();next=store.LoadSettings();next.StartWithWindows=false;relocated.SaveSettings(store,previous,next);
                    Assert(!File.Exists(relocated.ShortcutPath)&&!store.LoadSettings().StartWithWindows,"关闭没有删除自身启动项");
                    Assert(other.IsEnabled,"关闭误删了其他资料目录启动项");other.SetEnabled(false);
                });
                Run("开机启动：失败不虚报成功、不覆盖其他启动项、配置失败不更改启动项", () =>
                {
                    LocalStore store=new LocalStore(Path.Combine(root,"startup-failure"));AppSettings original=new AppSettings();store.SaveSettings(original);
                    string folder=Path.Combine(root,"startup-protected");Directory.CreateDirectory(folder);
                    WindowsStartup startup=new WindowsStartup(folder,typeof(MainForm).Assembly.Location,store.Root,32425,true);
                    byte[] foreign=Encoding.UTF8.GetBytes("不是飞Q创建的快捷方式");File.WriteAllBytes(startup.ShortcutPath,foreign);
                    AppSettings desired=store.LoadSettings();desired.StartWithWindows=true;
                    ExpectFailure(()=>startup.SaveSettings(store,original,desired),"覆盖了不属于飞Q的启动项");
                    Assert(File.ReadAllBytes(startup.ShortcutPath).SequenceEqual(foreign)&&!store.LoadSettings().StartWithWindows,"失败后破坏启动项或设置");
                    File.Delete(startup.ShortcutPath);startup.SaveSettings(store,original,desired);
                    Directory.CreateDirectory(Path.Combine(store.Root,"settings.json.tmp"));
                    ExpectFailure(()=>startup.SaveSettings(store,desired,original),"无法保存配置时仍更改启动项");
                    Assert(startup.IsEnabled&&store.LoadSettings().StartWithWindows,"保存失败却取消了自启动");startup.SetEnabled(false);
                });
                Run("启动自动单播：旧配置兼容、地址持久化与离线后恢复", () =>
                {
                    LocalStore saved = new LocalStore(Path.Combine(root, "reconnect"));
                    Assert(saved.LoadSettings().KnownPeers.Length == 0, "新配置应无历史地址");
                    int peerPort = FreePort();
                    AppSettings settings = new AppSettings { KnownPeers = new[] { "无效", "127.0.0.1:1", "127.0.0.1:" + peerPort, "127.0.0.1:" + peerPort } };
                    saved.SaveSettings(settings); settings = saved.LoadSettings();
                    Assert(settings.KnownPeers.Length == 1, "历史地址未校验或去重");
                    using (LanService first = new LanService(settings, FreePort(), true, "remembered-a"))
                    {
                        first.Start(); Assert(first.Peers.Count == 0, "历史联系人被假装成在线");
                        using (LanService peer = new LanService(new AppSettings(), peerPort, true, "remembered-b"))
                        {
                            peer.Start(); first.Refresh();
                            Wait(() => first.Peers.Any(p => p.Login == "remembered-b" && p.Online), "没有自动找回历史地址", 4000);
                        }
                    }
                    using (LanService peer = new LanService(new AppSettings(), peerPort, true, "remembered-b"))
                    using (LanService restarted = new LanService(saved.LoadSettings(), FreePort(), true, "remembered-a"))
                    { peer.Start(); restarted.Start(); Wait(() => restarted.Peers.Any(p => p.Login == "remembered-b" && p.Online), "重启仍需手动 Probe", 4000); }
                });
                Run("离线会话索引：旧配置兼容、无在线伪装、上限与大配置往返", () =>
                {
                    LocalStore store=new LocalStore(Path.Combine(root,"conversation-index"));Assert(store.LoadSettings().Conversations.Length==0,"新配置不是空索引");
                    string value=new string('\\',128);
                    SavedConversation[] entries=Enumerable.Range(0,2000).Select(i=>new SavedConversation { Endpoint="127.0.0.1:"+(1024+i),Login=value,Host=value,Nickname=value,Group=value,Preview=new string('\\',160),MessageUtcTicks=DateTime.UtcNow.Ticks,Unread=3 }).ToArray();
                    AppSettings settings=new AppSettings { Conversations=entries };store.SaveSettings(settings);AppSettings loaded=store.LoadSettings();
                    Assert(loaded.Conversations.Length==2000&&loaded.Conversations[0].Unread==3,"较大合法索引导致配置丢失");
                    SavedConversation[] clean=SavedConversation.Normalize(new[]{null,new SavedConversation {Endpoint="bad",Login="a"},entries[0],entries[0]});
                    Assert(clean.Length==1&&!clean[0].OfflinePeer().Online&&!clean[0].OfflinePeer().Utf8,"无效数据、重复数据或历史在线状态被信任");
                    using(LanService node=new LanService(loaded,FreePort(),true,"restore-bound"))Assert(node.Peers.Count==2000&&node.Peers.All(p=>!p.Online),"没有恢复离线索引");
                });
                Run("历史时间：UTC 存储读回本地显示，多次保存不叠加时差", () =>
                {
                    LocalStore store=new LocalStore(Path.Combine(root,"history-timezone"));
                    DateTime time=new DateTime(2026,10,7,21,15,0,DateTimeKind.Local);
                    store.Append(new ChatRecord { PeerId="127.0.0.1:32425/time",Packet=1,Text="时间一致",Time=time,State="已送达",Outgoing=true });
                    ChatRecord loaded=store.History("127.0.0.1:32425/time",1).Single();
                    Assert(loaded.Time.Kind==DateTimeKind.Local&&loaded.Time==time,"读取历史后显示成 UTC 时间");
                    store.Append(loaded);ChatRecord again=store.History(loaded.PeerId,1).Single();
                    Assert(again.Time==time&&again.Time.ToUniversalTime()==time.ToUniversalTime(),"再次保存叠加了时区偏移");
                });
                Run("UTF-8 / 中文 / 表情 / 冒号 / 换行报文往返", () =>
                {
                    Packet packet; string message = "中文消息：你好 😊\nsecond:line";
                    Assert(Protocol.TryParse(Protocol.Encode(123, "测试", "主机", Protocol.SendMessage | Protocol.Utf8, message, "group"), out packet), "解析失败");
                    Assert(packet.Body == message && packet.Login == "测试" && packet.Number == 123 && packet.Extra == "group", "报文内容丢失");
                });
                Run("GBK 兼容编码往返", () =>
                {
                    Packet packet; Assert(Protocol.TryParse(Protocol.Encode(9, "旧客户端", "机器", Protocol.SendMessage, "简体中文聊天", null), out packet), "解析失败");
                    Assert(packet.Body == "简体中文聊天" && packet.Login == "旧客户端", "旧编码乱码");
                });
                Run("畸形 / 超长报文拒绝", () =>
                {
                    Packet packet; foreach (string wire in new[] { "", "1:a:b:c:32:bad", "1:1:b:c:NaN:bad", "2:1:b:c:32:bad", "1:-1:b:c:32:bad" }) Assert(!Protocol.TryParse(Encoding.UTF8.GetBytes(wire), out packet), "接受了畸形报文");
                    Assert(!Protocol.TryParse(new byte[60001], out packet), "未限制报文大小");
                    ExpectFailure(() => Protocol.Encode(1, "u", "h", Protocol.SendMessage, new string('a', 60000), null), "允许超长发送");
                });
                Run("危险文件名 / 设备名 / 路径穿越拒绝", () =>
                {
                    foreach (string name in new[] { "../a.exe", "..\\x.txt", "C:\\x", "a:b", "NUL", "con.txt", "LPT1.txt", "x.", "x ", "a\0b", "a\nb" }) Assert(!Protocol.SafeFileName(name), "放行危险文件名 " + name);
                    Assert(Protocol.SafeFileName("项目资料（最终）.zip"), "误拒绝普通中文文件名");
                });
                Run("附件协议与目录过滤", () =>
                {
                    var parsed = Protocol.ParseFiles(Protocol.FileList(new[] { new Attachment { Id = 0, Name = "文档.txt", Size = 12345, Modified = 42 } }));
                    Assert(parsed.Count == 1 && parsed[0].Size == 12345 && parsed[0].Name == "文档.txt", "附件往返失败");
                    Assert(Protocol.ParseFiles("0:../bad:10:1:1:\a1:folder:0:1:2:\a2:bad:ffffffffffffffff:1:1:\a").Count == 0, "接受了非法附件");
                });
                Run("媒体预览：图片真实解码、损坏文件回退与扩展名边界", () => {
                    string file=Path.Combine(root,"preview.png");
                    using(var image=new System.Drawing.Bitmap(80,60)) {using(var g=System.Drawing.Graphics.FromImage(image))g.Clear(System.Drawing.Color.CornflowerBlue);image.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
                    using(var thumbnail=MediaPreview.Thumbnail(file)) Assert(thumbnail!=null&&thumbnail.Width>0,"系统未生成图片缩略图");
                    string bad=Path.Combine(root,"broken.png"); File.WriteAllText(bad,"not an image");using(var thumbnail=MediaPreview.Thumbnail(bad)) Assert(thumbnail==null,"损坏图片未回退");
                    Assert(!MediaPreview.Supported("bad.jp")&&!MediaPreview.Supported("bad.exe")&&MediaPreview.Video("movie.MP4"),"媒体扩展名识别错误");
                });
                Run("夜间主题与颜色设置持久化",()=>{
                    LocalStore store=new LocalStore(Path.Combine(root,"theme"));store.SaveSettings(new AppSettings{DarkMode=true,Accent="purple"});Assert(store.LoadSettings().DarkMode&&store.LoadSettings().Accent=="purple","主题未保存");
                    Theme.Configure(true,"purple");Assert(Theme.Surface.GetBrightness()<.3&&Theme.Ink.GetBrightness()>.7,"夜间对比度错误");Theme.Configure(false,"blue");
                });
                Run("配置原子保存、恢复、损坏降级与原文件备份", () =>
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "settings")); AppSettings settings = store.LoadSettings(); settings.Nickname = "测试昵称"; store.SaveSettings(settings); settings.Group = "研发组"; store.SaveSettings(settings);
                    Assert(store.LoadSettings().Nickname == "测试昵称" && store.LoadSettings().Group == "研发组", "配置未持久化");
                    File.WriteAllText(Path.Combine(store.Root, "settings.json"), "{bad json");
                    Assert(store.LoadSettings() != null && store.Warning != null, "损坏配置未降级");
                    byte[] original=File.ReadAllBytes(Path.Combine(store.Root,"settings.json"));store.SaveSettings(settings);
                    string backup=Directory.GetFiles(store.Root,"settings-recovery-*.json").Single();
                    Assert(File.ReadAllBytes(backup).SequenceEqual(original),"自动保存覆盖了损坏配置且未保留原字节");
                    store.SaveSettings(settings);Assert(Directory.GetFiles(store.Root,"settings-recovery-*.json").Length==1,"正常重复保存不应堆积恢复备份");
                });
                Run("接收配置：旧版迁移、默认无需确认、自定义目录与关闭开关持久化", () =>
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "receive-settings"));
                    Assert(store.LoadSettings().AutoReceiveFiles && store.LoadSettings().ReceiveFolder == AppSettings.DefaultReceiveFolder, "首次默认值不正确");
                    File.WriteAllText(Path.Combine(store.Root, "settings.json"), "{\"Nickname\":\"旧版昵称\",\"CloseToTray\":false}");
                    AppSettings settings = store.LoadSettings(); Assert(settings.AutoReceiveFiles && !settings.CloseToTray && settings.ReceiveFolder == AppSettings.DefaultReceiveFolder, "旧配置迁移丢失默认值或原设置");
                    settings.AutoReceiveFiles = false; settings.ReceiveFolder = Path.Combine(root, "自定义接收"); store.SaveSettings(settings);
                    Assert(!store.LoadSettings().AutoReceiveFiles && store.LoadSettings().ReceiveFolder == settings.ReceiveFolder, "新设置未持久化");
                    ReceiveStorage.ValidateWritable(settings.ReceiveFolder); Assert(Directory.GetFiles(settings.ReceiveFolder).Length == 0, "目录探测遗留了文件");
                    ExpectFailure(() => ReceiveStorage.Folder("relative"), "允许相对目录"); ExpectFailure(() => ReceiveStorage.Folder(@"C:relative"), "允许驱动器相对目录"); ExpectFailure(() => ReceiveStorage.Folder(@"\relative"), "允许省略驱动器");
                    ExpectFailure(() => ReceiveStorage.Destination(settings.ReceiveFolder, "../escape.txt"), "允许接收路径穿越");
                });
                Run("文件历史：本地路径持久化、旧完成记录兼容、普通文本不能伪装附件", () =>
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "file-history")); string file = Path.Combine(root, "文件记录.txt");
                    ChatRecord record = new ChatRecord { PeerId = "files", Packet = 1, Text = "[文件] 文件记录.txt", State = "已送达", LocalFiles = new[] { file }, Outgoing = true };
                    store.Append(record); Assert(store.History("files", 10).Single().GetLocalFiles().Single() == file, "重启历史丢失文件路径");
                    ChatRecord legacy = new ChatRecord { Sender = "文件", State = "已保存", Text = "已保存：" + file };
                    Assert(legacy.GetLocalFiles().Single() == file, "旧完成记录未兼容"); legacy.State = "已收到"; Assert(legacy.GetLocalFiles().Length == 0, "把网络文本当作本地附件");
                });
                Run("聊天历史写入、送达更新去重、尾部恢复", () =>
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "history"));
                    ChatRecord record = new ChatRecord { PeerId = "127.0.0.1/test", Packet = 1, Text = "你好", Sender = "测试", Time = DateTime.Now, Outgoing = true, State = "等待确认" };
                    store.Append(record); record.State = "已送达"; store.Append(record);
                    File.AppendAllText(Directory.GetFiles(store.Root, "history-*.jsonl")[0], "{broken\n");
                    var history = store.History(record.PeerId, 50); Assert(history.Count == 1 && history[0].State == "已送达", "确认状态重复显示或未更新");
                });
                Run("历史中断后再追加：残缺 JSON 和 UTF-8 尾部不吞新记录", () =>
                {
                    foreach (byte[] broken in new[] { Encoding.UTF8.GetBytes("{\"PeerId\":\"断电中断"), new byte[] { (byte)'{', 0xE4, 0xBD } })
                    {
                        string path = Path.Combine(root, "tail-" + Guid.NewGuid().ToString("N")); LocalStore store = new LocalStore(path);
                        string peer = "127.0.0.1:2425/tail";
                        store.Append(new ChatRecord { PeerId = peer, Packet = 1, Text = "旧记录", State = "已收到" });
                        string file = Directory.GetFiles(path, "history-*.jsonl").Single();
                        using (FileStream output = new FileStream(file, FileMode.Append)) output.Write(broken, 0, broken.Length);
                        byte[] before = File.ReadAllBytes(file); store = new LocalStore(path);
                        store.Append(new ChatRecord { PeerId = peer, Packet = 2, Text = "重启后正常消息", State = "已收到" });
                        var records = store.History(peer, 20);
                        Assert(records.Count == 2 && records[1].Packet == 2, "残缺尾行导致下一条消息丢失");
                        Assert(File.ReadAllBytes(file).Take(before.Length).SequenceEqual(before), "修复尾行时修改了旧数据");
                    }
                });
                Run("完整但无换行的历史末条正常保留", () =>
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "tail-valid")); string peer = "tail-valid";
                    store.Append(new ChatRecord { PeerId = peer, Packet = 1, Text = "完整末条", State = "已收到" });
                    string file = Directory.GetFiles(store.Root, "history-*.jsonl").Single();
                    using (FileStream output = new FileStream(file, FileMode.Open, FileAccess.Write)) output.SetLength(output.Length - 1);
                    store.Append(new ChatRecord { PeerId = peer, Packet = 2, Text = "新消息", State = "已收到" });
                    Assert(store.History(peer, 20).Count == 2, "误删完整但未换行的记录");
                });
                Run("未确认记录：当前重试不误改，异常退出后恢复并持久化", () =>
                {
                    string path = Path.Combine(root, "pending-recovery"); LocalStore store = new LocalStore(path); string peer = "pending-peer";
                    ChatRecord waiting = new ChatRecord { PeerId = peer, Packet = 1, Outgoing = true, Text = "尚未确认", State = "等待确认" };
                    store.Append(waiting);
                    store.Append(new ChatRecord { PeerId = peer, Packet = 1, Outgoing = false, Text = "远端恰好使用相同消息号", State = "已收到" });
                    store.Append(new ChatRecord { PeerId = peer, Packet = 2, Outgoing = true, Text = "确认过的", State = "已送达" });
                    Assert(store.History(peer, 20).Single(r => r.Packet == 1 && r.Outgoing).State == "等待确认", "误终止当前进程待确认消息");
                    var restored = new LocalStore(path).History(peer, 20);
                    Assert(restored.Single(r => r.Packet == 1 && r.Outgoing).State == "上次退出，未确认送达", "旧等待记录未恢复");
                    Assert(restored.Single(r => r.Packet == 1 && !r.Outgoing).State == "已收到", "恢复发送状态破坏了同号接收记录");
                    Assert(restored.Single(r => r.Packet == 2).State == "已送达", "错误改变已送达消息");
                    long size = new FileInfo(Directory.GetFiles(path, "history-*.jsonl").Single()).Length;
                    Assert(new LocalStore(path).History(peer, 20).Count == 3 && new FileInfo(Directory.GetFiles(path, "history-*.jsonl").Single()).Length == size, "重复恢复生成无穷状态记录");
                });
                Run("正常结束记录会话：保存明确未确认状态，不改变已收到的 ACK", () =>
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "pending-complete")); string peer = "pending-complete";
                    ChatRecord pending = new ChatRecord { PeerId = peer, Packet = 1, Outgoing = true, Text = "等待", State = "等待确认" };
                    ChatRecord ack = new ChatRecord { PeerId = peer, Packet = 2, Outgoing = true, Text = "网络已确认", State = "等待确认" };
                    store.Append(pending); store.Append(ack); ack.State = "已送达"; store.CompletePending(); store.CompletePending();
                    var records = new LocalStore(store.Root).History(peer, 20);
                    Assert(records.Single(r => r.Packet == 1).State == "已退出，未确认送达", "退出未保存未确认状态");
                    Assert(records.Single(r => r.Packet == 2).State == "已送达", "覆盖了已收到的 ACK 状态");
                });
                Run("IPv4 与端口输入验证", () =>
                {
                    Assert(AddressDialog.Parse("192.168.1.12:2425").Port == 2425, "合法地址失败");
                    foreach (string invalid in new[] { "bad", "::1", "0.0.0.0", "255.255.255.255", "224.0.0.1", "127.0.0.1:80", "127.0.0.1:65536" }) ExpectFailure(() => AddressDialog.Parse(invalid), "非法地址被接受");
                });
                NetworkTests();
                UiSmokeTests.Run(root, Run);
            }
            finally
            {
                // 测试只删除本次创建、位于项目构建目录中的 GUID 沙盒。
                string resolved = Path.GetFullPath(root), temp = Path.GetFullPath(dataRoot);
                if (resolved.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("FeiqLight-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
            }
            Log("RESULT " + passed + " passed, " + failed + " failed");
            string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results.txt"); File.WriteAllLines(report, results, new UTF8Encoding(false));
            return failed == 0 ? 0 : 1;
        }
        private sealed class DirectProgress : IProgress<int>
        {
            public readonly List<int> Values = new List<int>();
            public void Report(int value) { Values.Add(value); }
        }
        private static long ReadOffset(TcpClient connection)
        {
            connection.ReceiveTimeout = 5000; List<byte> bytes = new List<byte>(); int b;
            while ((b = connection.GetStream().ReadByte()) > 0) bytes.Add((byte)b);
            Packet request; Assert(Protocol.TryParse(bytes.ToArray(), out request), "续传请求无法解析");
            return Int64.Parse(request.Body.Split(':')[2], System.Globalization.NumberStyles.HexNumber);
        }
        private static void NetworkTests()
        {
            int portA = FreePort(), portB = FreePort(); while (portA == portB) portB = FreePort();
            using (LanService a = new LanService(new AppSettings { Nickname = "测试甲", Group = "测试组" }, portA, true, "alice"))
            using (LanService b = new LanService(new AppSettings { Nickname = "测试乙", Group = "测试组" }, portB, true, "bob"))
            {
                a.Start(); b.Start(); Peer bob = null; Peer alice = null;
                List<IncomingMessage> inbox = new List<IncomingMessage>(); object inboxGate = new object();
                b.MessageReceived += message => { lock (inboxGate) inbox.Add(message); };
                Run("双节点真实 UDP 发现与昵称/分组同步", () =>
                {
                    a.Probe(Endpoint(portB)); Wait(() => a.Peers.Count == 1 && b.Peers.Count == 1, "发现超时", 5000);
                    bob = a.Peers[0]; alice = b.Peers[0]; Assert(bob.Nickname == "测试乙" && alice.Nickname == "测试甲" && bob.Group == "测试组" && bob.Utf8, "发现资料错误");
                });
                if (bob == null || alice == null) return;
                Run("双节点真实中文消息 + ACK 送达确认", () =>
                {
                    ChatRecord record = a.SendMessage(bob, "中文 / emoji 😊 / 冒号: /\n换行", null);
                    Wait(() => record.State == "已送达", "未收到确认", 5000);
                    Wait(() => { lock (inboxGate) return inbox.Any(m => m.Record.Packet == record.Packet); }, "未收到消息", 5000);
                    lock (inboxGate) Assert(inbox.Single(m => m.Record.Packet == record.Packet).Record.Text.Contains("😊"), "消息乱码");
                });
                Run("网络层 GBK 旧协议消息", () =>
                {
                    bob.Utf8 = false; ChatRecord record = a.SendMessage(bob, "旧版中文消息", null);
                    Wait(() => { lock (inboxGate) return inbox.Any(m => m.Record.Packet == record.Packet); }, "GBK 消息未到达", 5000);
                    lock (inboxGate) Assert(inbox.Single(m => m.Record.Packet == record.Packet).Record.Text == "旧版中文消息", "GBK 乱码"); bob.Utf8 = true;
                });
                Run("Unicode 语料、字节长度边界与无损失败", () =>
                {
                    string[] corpus={"ASCII: [] [文件] 内容\t\r\n", "中文𠮷𠀀 (๑•̀ㅂ•́)و✧", "👨‍👩‍👧‍👦 👩🏽‍💻 👍🏿 🇨🇳 1️⃣ ❤️", "e\u0301 العربية עברית \u200f"};
                    foreach(string text in corpus) { Packet parsed; Assert(Protocol.TryParse(Protocol.Encode(1,"用户😀","主机",Protocol.SendMessage|Protocol.Utf8,text,null),out parsed)&&parsed.Body==text&&parsed.Login=="用户😀","Unicode 被修改"); }
                    int overhead=Protocol.Encode(1,"u","h",Protocol.SendMessage|Protocol.Utf8,"",null).Length;
                    string boundary=new string('x',60000-overhead-4)+"😀"; Packet packet;
                    Assert(Protocol.TryParse(Protocol.Encode(1,"u","h",Protocol.SendMessage|Protocol.Utf8,boundary,null),out packet)&&packet.Body==boundary,"60000 字节边界错误");
                    ExpectFailure(()=>Protocol.Encode(1,"u","h",Protocol.SendMessage|Protocol.Utf8,boundary+"x",null),"超限未拒绝");
                    foreach(string invalid in new[]{"\ud800","\udc00","a\0b"}) ExpectFailure(()=>Protocol.Encode(1,"u","h",Protocol.Utf8,invalid,null),"无效 Unicode 被静默替换");
                    ExpectFailure(()=>Protocol.Encode(1,"u","h",Protocol.SendMessage,"😀",null),"GBK 静默替换 emoji");
                    byte[] malformed=Encoding.ASCII.GetBytes("1:1:u:h:8388640:").Concat(new byte[]{0xf0,0x9f,0,0}).ToArray(); Assert(!Protocol.TryParse(malformed,out packet),"接受了截断 UTF-8");
                    Assert(Protocol.Prefix(new string('a',127)+"😀",128)==new string('a',127),"昵称截断代理对");
                    foreach(string bad in new[]{"COM¹.txt","LPT².zip","CON .txt",new string('中',81)+".txt","bad\ud800.txt"}) Assert(!Protocol.SafeFileName(bad),"接受不兼容文件名");
                    Assert(Protocol.SafeFileName("👩🏽‍💻最终稿.tar.gz"),"错误拒绝 emoji 文件名");
                });
                Run("UDP 重复消息去重，同时返回 ACK", () =>
                {
                    using (UdpClient sender = new UdpClient(Endpoint(0)))
                    {
                        byte[] packet = Protocol.Encode(9944, "dedup", "host", Protocol.SendMessage | Protocol.SendCheck | Protocol.Utf8, "去重测试", null);
                        sender.Send(packet, packet.Length, Endpoint(portB)); sender.Send(packet, packet.Length, Endpoint(portB));
                        sender.Client.ReceiveTimeout = 3000; IPEndPoint from = Endpoint(0); Packet ack;
                        Assert(Protocol.TryParse(sender.Receive(ref from), out ack) && ack.Mode == Protocol.ReceiveMessage, "缺少第一次确认");
                        Assert(Protocol.TryParse(sender.Receive(ref from), out ack) && ack.Mode == Protocol.ReceiveMessage, "重复包未确认");
                        Wait(() => { lock (inboxGate) return inbox.Any(m => m.Record.Packet == 9944); }, "未处理去重消息", 3000);
                        lock (inboxGate) Assert(inbox.Count(m => m.Record.Packet == 9944) == 1, "出现重复消息");
                    }
                });
                Run("真实 TCP 大文件传输与 SHA-256 一致性", () =>
                {
                    string source = Path.Combine(root, "中文测试文件.bin"), destination = Path.Combine(root, "received.bin");
                    byte[] data = new byte[3 * 1024 * 1024 + 7]; new Random(7).NextBytes(data); File.WriteAllBytes(source, data);
                    ChatRecord record = a.SendMessage(bob, "请接收文件", new[] { source });
                    IncomingMessage message = null; Wait(() => { lock (inboxGate) { message = inbox.FirstOrDefault(m => m.Record.Packet == record.Packet); return message != null; } }, "附件报价未到达", 5000);
                    Assert(message.Files.Count == 1, "缺少附件元数据");
                    b.ReceiveFileAsync(alice, record.Packet, message.Files[0], destination, null, CancellationToken.None).GetAwaiter().GetResult();
                    Assert(EqualFiles(source, destination), "文件 SHA-256 不一致");
                    ExpectFailure(() => b.ReceiveFileAsync(alice, record.Packet, message.Files[0], destination, null, CancellationToken.None).GetAwaiter().GetResult(), "覆盖了已有文件");
                    Assert(EqualFiles(source, destination), "已有文件被损坏");
                });
                Run("发送进度：真实多文件 TCP、中间百分比、空文件与 ACK 语义", () =>
                {
                    string first=Path.Combine(root,"progress-large.bin"),empty=Path.Combine(root,"progress-empty.bin");
                    File.WriteAllBytes(first,new byte[8*1024*1024+17]);File.WriteAllBytes(empty,new byte[0]);
                    List<Tuple<long,int,int,string>> progress=new List<Tuple<long,int,int,string>>();
                    Action<ChatRecord,int,int,string> handler=(r,id,p,s)=>{lock(progress)progress.Add(Tuple.Create(r.Packet,id,p,s));};
                    a.TransferChanged+=handler;
                    try {
                        ChatRecord sent=a.SendMessage(bob,"",new[]{first,empty});
                        Wait(()=>sent.State!="等待确认","邀请未确认",4000);
                        lock(progress)Assert(progress.Count==0,"ACK 被误当作文件进度");
                        Assert(sent.Transfers.Length==2&&sent.Transfers.All(f=>f.Percent==0),"逐文件初始状态错误");
                        for(int i=0;i<2;i++)b.ReceiveFileAsync(alice,sent.Packet,new Attachment{Id=i,Name=i==0?"progress-large.bin":"progress-empty.bin",Size=i==0?new FileInfo(first).Length:0},Path.Combine(root,"progress-copy-"+i),null,CancellationToken.None).GetAwaiter().GetResult();
                        Wait(()=>{lock(progress)return progress.Count(p=>p.Item4=="已发送（非保存确认）")==2;},"缺少逐文件完成事件",4000);
                        lock(progress) {
                            Assert(progress.All(p=>p.Item1==sent.Packet&&p.Item3>=0&&p.Item3<=100),"进度关联错消息或越界");
                            Assert(progress.Any(p=>p.Item2==0&&p.Item3>0&&p.Item3<100),"大文件没有中间进度");
                            foreach(int id in new[]{0,1}) {int previous=-1;foreach(var p in progress.Where(p=>p.Item2==id)){Assert(p.Item3>=previous,"进度倒退");previous=p.Item3;}Assert(previous==100,"完成未到100%");}
                        }
                    } finally {a.TransferChanged-=handler;}
                });
                Run("同名接收：并发提交自动改名，不覆盖文件或同名目录", () =>
                {
                    string folder = Path.Combine(root, "collision"); Directory.CreateDirectory(folder);
                    string source = Path.Combine(root, "同名.txt"), destination = Path.Combine(folder, "同名.txt"); File.WriteAllText(source, "本次传输"); File.WriteAllText(destination, "必须保留"); Directory.CreateDirectory(Path.Combine(folder, "同名 (1).txt"));
                    ChatRecord record = a.SendMessage(bob, "文件", new[] { source }); Attachment file = new Attachment { Id = 0, Name = "同名.txt", Size = new FileInfo(source).Length };
                    Task<string> first = b.ReceiveFileAsync(alice, record.Packet, file, destination, null, CancellationToken.None, true), second = b.ReceiveFileAsync(alice, record.Packet, file, destination, null, CancellationToken.None, true);
                    Task.WaitAll(first, second); Assert(first.Result != second.Result && EqualFiles(source, first.Result) && EqualFiles(source, second.Result), "并发同名文件未分别保存");
                    Assert(File.ReadAllText(destination) == "必须保留" && Directory.Exists(Path.Combine(folder, "同名 (1).txt")), "覆盖了旧文件或目录"); Assert(!Directory.GetFiles(folder, "*.part").Any(), "残留半成品");
                    Assert(record.GetLocalFiles().Single() == source, "发送记录没有本地文件路径");
                });
                Run("四任务并发、不同扩展名二进制内容不被改写", () =>
                {
                    string folder=Path.Combine(root,"formats");Directory.CreateDirectory(folder);
                    string[] names={"👩🏽‍💻资料.GIF","压缩.tar.gz","无扩展名","颜文字(｡･ω･｡).webp"};
                    List<string> sources=new List<string>(); Random random=new Random(91);
                    foreach(string name in names) {string source=Path.Combine(folder,name);byte[] bytes=new byte[262144+sources.Count];random.NextBytes(bytes);File.WriteAllBytes(source,bytes);sources.Add(source);}
                    ChatRecord record=a.SendMessage(bob,"",sources);List<Task<string>> downloads=new List<Task<string>>();
                    for(int i=0;i<sources.Count;i++) downloads.Add(b.ReceiveFileAsync(alice,record.Packet,new Attachment{Id=i,Name=names[i],Size=new FileInfo(sources[i]).Length},Path.Combine(folder,"received-"+i),null,CancellationToken.None));
                    Task.WaitAll(downloads.ToArray());for(int i=0;i<sources.Count;i++)Assert(EqualFiles(sources[i],downloads[i].Result),"并发内容串写");
                });
                Run("未知文件、错误来源地址的 TCP 请求拒绝", () =>
                {
                    string path = Path.Combine(root, "private-test.txt"); File.WriteAllText(path, "test only");
                    ChatRecord record = a.SendMessage(bob, "文件", new[] { path });
                    Assert(Fetch(portA, record.Packet, 999, "127.0.0.1").Length == 0, "未知文件获准读取");
                    Assert(Fetch(portA, record.Packet, 0, "127.0.0.2").Length == 0, "非接收方获准读取");
                    Assert(Fetch(portA, record.Packet, 0, "127.0.0.1").Length > 0, "正常请求被拒绝");
                });
                Run("文件改变后拒绝发送、清理半成品", () =>
                {
                    string path = Path.Combine(root, "changed.txt"), target = Path.Combine(root, "changed-received.txt"); File.WriteAllText(path, "before");
                    ChatRecord record = a.SendMessage(bob, "文件", new[] { path }); File.WriteAllText(path, "after-changed");
                    ExpectFailure(() => b.ReceiveFileAsync(alice, record.Packet, new Attachment { Id = 0, Name = "changed.txt", Size = 6 }, target, null, CancellationToken.None).GetAwaiter().GetResult(), "改变的文件被静默接受");
                    Assert(!File.Exists(target) && !Directory.GetFiles(root, "*.part").Any(), "留下不完整文件");
                });
                Run("真实断网后保留断点、非零偏移续传与完整内容校验", () =>
                {
                    byte[] payload = new byte[1024 * 1024 + 7]; new Random(123).NextBytes(payload);
                    TcpListener server = new TcpListener(IPAddress.Loopback, 0); server.Start();
                    string target = Path.Combine(root, "resume.bin"); Peer remote = new Peer { Endpoint = (IPEndPoint)server.LocalEndpoint, Login = "resume", Host = "server" };
                    Attachment file = new Attachment { Id = 2, Name = "resume.bin", Size = payload.Length, Modified = 42 };
                    Task sender = Task.Run(() =>
                    {
                        using (TcpClient first = server.AcceptTcpClient()) { Assert(ReadOffset(first) == 0, "首次偏移非零"); first.GetStream().Write(payload, 0, 80000); }
                        using (TcpClient second = server.AcceptTcpClient()) { long offset = ReadOffset(second); Assert(offset == 80000, "重试没有从断点开始"); second.GetStream().Write(payload, (int)offset, payload.Length - (int)offset); }
                    });
                    try
                    {
                        ExpectFailure(() => b.ReceiveFileAsync(remote, 99, file, target, null, CancellationToken.None).GetAwaiter().GetResult(), "中断应报错");
                        string part = LanService.PartialPath(remote, 99, file, target);
                        Assert(!File.Exists(target) && new FileInfo(part).Length == 80000, "断点未保留或提前发布了半成品");
                        Assert(part != LanService.PartialPath(remote, 100, file, target), "不同邀请混用了断点");
                        DirectProgress progress = new DirectProgress();
                        b.ReceiveFileAsync(remote, 99, file, target, progress, CancellationToken.None).GetAwaiter().GetResult();
                        Assert(sender.Wait(8000), "发送测试未完成"); Assert(progress.Values[0] > 0, "进度没有恢复");
                        Assert(File.ReadAllBytes(target).SequenceEqual(payload) && !File.Exists(part), "续传结果损坏或未清理断点");
                    }
                    finally { server.Stop(); }
                });
                Run("零字节文件传输", () =>
                {
                    string source = Path.Combine(root, "empty.txt"), target = Path.Combine(root, "empty-received.txt"); File.WriteAllBytes(source, new byte[0]);
                    ChatRecord record = a.SendMessage(bob, "空文件", new[] { source });
                    b.ReceiveFileAsync(alice, record.Packet, new Attachment { Id = 0, Name = "empty.txt", Size = 0 }, target, null, CancellationToken.None).GetAwaiter().GetResult();
                    Assert(File.Exists(target) && new FileInfo(target).Length == 0, "空文件传输失败");
                });
                Run("取消文件传输与临时文件清理", () =>
                {
                    TcpListener server = new TcpListener(IPAddress.Loopback, 0); server.Start();
                    using (ManualResetEventSlim release = new ManualResetEventSlim(false))
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        Task holder = Task.Run(() => { using (TcpClient connection = server.AcceptTcpClient()) { connection.GetStream().Write(new byte[32], 0, 32); release.Wait(5000); } });
                        string path = Path.Combine(root, "cancelled.bin"); Peer fake = new Peer { Endpoint = (IPEndPoint)server.LocalEndpoint };
                        cancel.CancelAfter(150);
                        try { ExpectFailure(() => b.ReceiveFileAsync(fake, 1, new Attachment { Id = 0, Name = "cancelled.bin", Size = 1048576 }, path, null, cancel.Token).GetAwaiter().GetResult(), "取消没有中止接收"); }
                        finally { release.Set(); holder.Wait(6000); server.Stop(); }
                        Assert(!File.Exists(path) && !Directory.GetFiles(root, "*.part").Any(), "取消残留文件");
                    }
                });
                Run("排队接收取消：不等待前一个同目标任务结束", () =>
                {
                    TcpListener server=new TcpListener(IPAddress.Loopback,0);server.Start();
                    using(ManualResetEventSlim entered=new ManualResetEventSlim(false))
                    using(ManualResetEventSlim release=new ManualResetEventSlim(false))
                    using(CancellationTokenSource firstCancel=new CancellationTokenSource())
                    using(CancellationTokenSource queuedCancel=new CancellationTokenSource()) {
                        Task first=null,queued=null;
                        Task sender=Task.Run(()=>{using(TcpClient client=server.AcceptTcpClient()){client.GetStream().Write(new byte[32],0,32);entered.Set();release.Wait(8000);}});
                        try {
                            Peer remote=new Peer{Endpoint=(IPEndPoint)server.LocalEndpoint,Login="queued",Host="test"};string target=Path.Combine(root,"queued-cancel.bin");Attachment file=new Attachment{Id=0,Name="queued-cancel.bin",Size=1048576};
                            first=b.ReceiveFileAsync(remote,101,file,target,null,firstCancel.Token);Assert(entered.Wait(3000),"首个下载没有进入传输");
                            queued=b.ReceiveFileAsync(remote,102,file,target,null,queuedCancel.Token);
                            Wait(()=>queued.Status==TaskStatus.Running,"排队任务没有开始等待目标锁",3000);queuedCancel.Cancel();
                            Wait(()=>queued.IsCompleted,"取消仍在等待前一个下载释放目标锁",1500);Assert(queued.IsCanceled,"排队取消没有返回取消状态");Assert(!first.IsCompleted,"取消排队任务中断了前一个任务");
                        } finally {
                            firstCancel.Cancel();queuedCancel.Cancel();release.Set();server.Stop();
                            foreach(Task job in new[]{first,queued,sender}.Where(t=>t!=null)) try{Assert(job.Wait(5000),"测试任务未退出");}catch(AggregateException){ }
                        }
                    }
                });
                Run("未确认消息自动重发且最终明确失败", () =>
                {
                    using (UdpClient blackhole = new UdpClient(Endpoint(0)))
                    {
                        int count = 0; Peer noAck = new Peer { Endpoint = (IPEndPoint)blackhole.Client.LocalEndPoint, Login = "noack", Nickname = "未确认节点", Utf8 = true };
                        ChatRecord record = a.SendMessage(noAck, "重试测试", null);
                        blackhole.Client.ReceiveTimeout = 12000;
                        for (int i = 0; i < 4; i++) { IPEndPoint from = Endpoint(0); blackhole.Receive(ref from); count++; }
                        Wait(() => record.State.StartsWith("未确认"), "重试后没有失败状态", 5000); Assert(count == 4, "重试次数不正确");
                    }
                });
                Run("回环隔离不能发送至局域网", () => { ExpectFailure(() => a.Probe(new IPEndPoint(IPAddress.Parse("192.168.1.1"), 2425)), "测试节点连接了外部网络"); });
                Run("端口冲突明确拒绝且不复用同一套接字", () =>
                {
                    using (LanService duplicate = new LanService(new AppSettings(), portA, true, "duplicate")) ExpectFailure(duplicate.Start, "两个服务共享了端口");
                });
                Run("退出广播更新离线状态", () =>
                {
                    b.Dispose(); Wait(() => !bob.Online, "未收到退出通知", 4000);
                });
            }
        }
    }
}
