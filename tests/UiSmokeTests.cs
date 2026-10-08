using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class UiSmokeTests
    {
        private static IEnumerable<Control> All(Control root)
        {
            foreach (Control child in root.Controls) { yield return child; foreach (Control descendant in All(child)) yield return descendant; }
        }
        private static T Find<T>(Control root, string accessible) where T : Control { return All(root).OfType<T>().Single(c => c.AccessibleName == accessible); }
        private static Button Button(Control root, string text) { return All(root).OfType<Button>().Single(c => c.Text.StartsWith(text)); }
        private static void Check(bool value, string error) { if (!value) throw new Exception(error); }
        private static void Pump(Func<bool> done, string message)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
            Check(done(), message + " | " + String.Join(" | ", Application.OpenForms.Cast<Form>().SelectMany(f => All(f).OfType<Label>()).Select(c => c.Text)));
        }
        private static int Port()
        {
            return SelfTests.FreePort();
        }
        private static void Render(Form form, string name)
        {
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + "-layout.txt"), new[] { "FORM " + form.Size + " CLIENT " + form.ClientSize }.Concat(All(form).Select(c => c.GetType().Name + " " + c.AccessibleName + " " + c.Text.Replace("\n", " ").Substring(0, Math.Min(c.Text.Length, 28)) + " bounds=" + c.Bounds + " parent=" + c.Parent.GetType().Name + " client=" + c.Parent.ClientSize + (c is TableLayoutPanel ? " rows=" + String.Join(",", ((TableLayoutPanel)c).GetRowHeights()) : ""))));
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                // RichTextBox 不支持 DrawToBitmap，使用其原生打印接口输出同一份 RTF。
                // 这是控件渲染预览，不伪装成桌面截图。
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    foreach (RichTextBox box in All(form).OfType<RichTextBox>().Where(c => c.Visible))
                    {
                        Point screen = box.PointToScreen(Point.Empty);
                        Rectangle bounds = new Rectangle(screen.X - form.Left, screen.Y - form.Top, box.Width, box.Height);
                        using (SolidBrush background = new SolidBrush(box.BackColor)) graphics.FillRectangle(background, bounds);
                        float x = 1440F / graphics.DpiX, y = 1440F / graphics.DpiY;
                        IntPtr dc = graphics.GetHdc(), memory = IntPtr.Zero;
                        try
                        {
                            FormatRange range = new FormatRange { Dc = dc, Target = dc, Area = new Rect { Left = (int)(bounds.Left * x), Top = (int)(bounds.Top * y), Right = (int)(bounds.Right * x), Bottom = (int)(bounds.Bottom * y) }, Page = new Rect { Right = (int)(bitmap.Width * x), Bottom = (int)(bitmap.Height * y) }, Min = 0, Max = -1 };
                            memory = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(FormatRange))); Marshal.StructureToPtr(range, memory, false);
                            SendMessage(box.Handle, 0x0439, new IntPtr(1), memory);
                        }
                        finally { SendMessage(box.Handle, 0x0439, IntPtr.Zero, IntPtr.Zero); if (memory != IntPtr.Zero) Marshal.FreeCoTaskMem(memory); graphics.ReleaseHdc(dc); }
                    }
                }
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name + ".png"), ImageFormat.Png);
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct FormatRange { public IntPtr Dc, Target; public Rect Area, Page; public int Min, Max; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        private delegate bool WindowVisitor(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowVisitor visitor, IntPtr parameter);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private static ToolStripDropDown OpenMenu()
        {
            ToolStripDropDown result = null;
            EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr window, IntPtr parameter)
            {
                ToolStripDropDown menu = Control.FromHandle(window) as ToolStripDropDown;
                if (menu != null && menu.Visible) result = menu;
                return true;
            }, IntPtr.Zero);
            Check(result != null, "弹出菜单没有打开"); return result;
        }
        private static void ClickMenu(ToolStripDropDown menu, string text)
        {
            ToolStripItem item = menu.Items.Cast<ToolStripItem>().Single(i => i.Text == text);
            Point point = new Point(item.Bounds.Left + item.Width / 2, item.Bounds.Top + item.Height / 2);
            // 走菜单真实鼠标事件分发，不能只调用条目的 Click 回调跳过自动关闭流程。
            foreach (string method in new[] { "OnMouseDown", "OnMouseUp" })
                typeof(ToolStrip).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
        }
        private static void MenuKey(ToolStripDropDown menu, Keys key)
        {
            typeof(ToolStripDropDown).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(menu, new object[] { key });
        }
        private static void MenuDialog<T>(Action openMenu, string item, Action<T> inspect, bool keyboard = false) where T : Form
        {
            Exception asynchronous = null; bool shown = false;
            ThreadExceptionEventHandler handler = delegate(object sender, ThreadExceptionEventArgs args) { if (asynchronous == null) asynchronous = args.Exception; };
            Application.ThreadException += handler;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 30 })
            {
                timer.Tick += delegate
                {
                    T dialog = Application.OpenForms.OfType<T>().FirstOrDefault(); if (dialog == null) return;
                    timer.Stop(); shown = true;
                    try { inspect(dialog); } catch (Exception e) { asynchronous = e; }
                    finally { if (!dialog.IsDisposed) dialog.Close(); }
                };
                try
                {
                    openMenu(); ToolStripDropDown menu = OpenMenu(); timer.Start();
                    if (keyboard) { menu.Items.Cast<ToolStripItem>().Single(i => i.Text == item).Select(); MenuKey(menu, Keys.Enter); }
                    else ClickMenu(menu, item);
                    Application.DoEvents(); if (asynchronous != null) throw asynchronous; Check(shown, "菜单没有打开目标对话框：" + item);
                }
                // .NET Framework 的 add_ThreadException 替换而非叠加处理器；移除局部处理后恢复全局失败捕获。
                finally { timer.Stop(); Application.ThreadException -= handler; SelfTests.InstallUiExceptionHandler(); }
            }
        }
        private static void ReceiveWorkflow(string directory, Action<string, Action> test)
        {
            int senderPort = Port(), receiverPort = Port(); while (senderPort == receiverPort) receiverPort = Port();
            string folder = Path.Combine(directory, "自动接收"), source = Path.Combine(directory, "后台附件.txt"), empty = Path.Combine(directory, "空文件.txt");
            File.WriteAllText(source, "后台接收验证"); File.WriteAllText(empty, ""); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "后台附件.txt"), "不能覆盖");
            LocalStore store = new LocalStore(Path.Combine(directory, "receive-ui"));
            using (LanService sender = new LanService(new AppSettings { Nickname = "文件测试发送方", Notifications = false }, senderPort, true, "receive-sender"))
            using (LanService receiver = new LanService(new AppSettings { Nickname = "文件测试接收方", Notifications = false, ReceiveFolder = folder }, receiverPort, true, "receive-receiver"))
            using (MainForm main = new MainForm(receiver, store))
            {
                sender.Start(); receiver.Start(); main.Show(); main.NetworkStarted(); sender.Probe(new IPEndPoint(IPAddress.Loopback, receiverPort));
                Pump(() => sender.Peers.Count == 1 && receiver.Peers.Count == 1, "文件测试发现失败"); Peer peer = receiver.Peers.Single(); ChatForm chat = null;
                try
                {
                    test("自动接收：首次未打开会话、批量/空文件、同名保护与历史路径", () =>
                    {
                        sender.SendMessage(sender.Peers.Single(), "自动接收测试", new[] { source, empty, source });
                        try { Pump(() => Directory.GetFiles(folder).Length == 4 && store.History(peer.Id, 20).Count(r => r.State == "已保存") == 1, "默认自动接收未完成"); }
                        catch (Exception e) { throw new Exception(e.Message + "；文件=" + String.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName)) + "；历史=" + String.Join(",", store.History(peer.Id, 20).Select(r => r.State)) + "；自动=" + receiver.Settings.AutoReceiveFiles); }
                        Check(main.ActiveChat == null && !Application.OpenForms.OfType<NoticeDialog>().Any(), "自动接收打开了会话或授权弹窗");
                        Check(File.ReadAllText(Path.Combine(folder, "后台附件.txt")) == "不能覆盖", "自动接收覆盖旧文件");
                        Check(File.ReadAllText(Path.Combine(folder, "后台附件 (1).txt")) == File.ReadAllText(source) && File.ReadAllText(Path.Combine(folder, "后台附件 (2).txt")) == File.ReadAllText(source), "同名自动接收内容不一致");
                        Check(new FileInfo(Path.Combine(folder, "空文件.txt")).Length == 0 && !Directory.GetFiles(folder, "*.part").Any(), "空文件或临时清理失败");
                        ChatRecord invite = new LocalStore(store.Root).History(peer.Id, 20).First(r => r.Sender == "文件测试发送方"); Check(invite.GetLocalFiles().Length == 3 && invite.GetLocalFiles().All(File.Exists), "原邀请消息未关联全部本地路径");
                        chat = All(main).OfType<ChatForm>().Single(); Check(chat.PendingFileCount == 0, "完成后仍留在待接收队列");
                        Check(store.History(peer.Id,20).Count==1,"批量普通文件生成了重复完成消息");
                    });
                    test("图片接收：单条邀请原位完成、预览唯一且重新读取历史不重复",()=>{
                        string image=Path.Combine(directory,"单张图片.png");using(Bitmap bitmap=new Bitmap(120,80))using(Graphics g=Graphics.FromImage(bitmap)){g.Clear(Color.Orange);bitmap.Save(image,ImageFormat.Png);}
                        ChatRecord sent=sender.SendMessage(sender.Peers.Single(),"",new[]{image});
                        Pump(()=>store.History(peer.Id,20).Any(r=>r.Packet==sent.Packet&&r.State=="已保存"),"图片未接收完成");
                        List<ChatRecord> rows=new LocalStore(store.Root).History(peer.Id,20);Check(rows.Count==2&&rows.Count(r=>r.Packet==sent.Packet)==1,"单张图片新增了第二条完成消息");
                        ChatRecord saved=rows.Single(r=>r.Packet==sent.Packet);Check(saved.GetLocalFiles().Length==1&&File.ReadAllBytes(image).SequenceEqual(File.ReadAllBytes(saved.GetLocalFiles()[0])),"图片附件重复或字节改变");
                        main.OpenChat(peer);BubbleHistory history=All(chat).OfType<BubbleHistory>().Single();
                        var visible=(List<ChatRecord>)typeof(BubbleHistory).GetField("records",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history);Check(visible.Count(r=>r.Packet==sent.Packet)==1&&visible.Count==2,"渲染消息重复");
                        history.Refresh();var previews=(Dictionary<string,Bitmap>)typeof(BubbleHistory).GetField("previews",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history);
                        Pump(()=>previews.ContainsKey(saved.GetLocalFiles()[0])&&previews[saved.GetLocalFiles()[0]]!=null,"接收图片未生成预览");
                        Render(main,"preview-single-image-received");
                    });
                    test("附件队列绘制：完成移除后的旧索引回调不越界",()=>{
                        Check(chat.PendingFileCount==0,"前置接收尚未完成");ListBox list=Find<ListBox>(chat,"收到的文件列表");
                        using(Bitmap bitmap=new Bitmap(220,60))using(Graphics g=Graphics.FromImage(bitmap)){
                            MethodInfo draw=typeof(ChatForm).GetMethod("DrawFile",BindingFlags.Instance|BindingFlags.NonPublic);
                            foreach(int index in new[]{-1,0,3})draw.Invoke(chat,new object[]{list,new DrawItemEventArgs(g,Theme.Body,new Rectangle(0,0,220,60),index,DrawItemState.None)});
                        }
                    });
                    test("旧重复完成行：只隐藏已有邀请关联的副本，不删除独立记录",()=>{
                        LocalStore legacy=new LocalStore(Path.Combine(directory,"legacy-completion"));string path=Path.Combine(directory,"old.png");
                        legacy.Append(new ChatRecord{PeerId="legacy",Packet=1,Text="[文件] old.png",Sender="phone",State="已收到",LocalFiles=new[]{path},Time=DateTime.Now});
                        legacy.Append(new ChatRecord{PeerId="legacy",Packet=2,Text="已保存："+path,Sender="文件",State="已保存",LocalFiles=new[]{path},Time=DateTime.Now});
                        legacy.Append(new ChatRecord{PeerId="legacy",Packet=3,Text="已保存："+path+".other",Sender="文件",State="已保存",Time=DateTime.Now});
                        legacy.Append(new ChatRecord{PeerId="legacy",Packet=4,Text="普通正文：已保存",Sender="phone",State="已收到",Time=DateTime.Now});
                        var rows=legacy.History("legacy",20);Check(rows.Count==3&&rows.All(r=>r.Packet!=2)&&rows.Any(r=>r.Packet==3),"旧历史合并误删独立消息");
                    });
                    test("自动接收失败：无抢焦点弹窗、不无限重试、修正目录后手动重试", () =>
                    {
                        receiver.Settings.ReceiveFolder = source; sender.SendMessage(sender.Peers.Single(), "失败后重试", new[] { source });
                        Pump(() => chat.PendingFileCount == 1 && !chat.HasAutomaticFile && Button(chat, "接收").Enabled, "错误目录未进入可重试状态");
                        Check(!Application.OpenForms.OfType<NoticeDialog>().Any(), "自动失败弹窗阻塞界面");
                        receiver.Settings.ReceiveFolder = Path.Combine(directory, "重试成功"); main.OpenChat(peer); Button(chat, "接收").PerformClick();
                        Pump(() => chat.PendingFileCount == 0, "手动重试未完成"); Check(File.ReadAllText(Path.Combine(receiver.Settings.ReceiveFolder, "后台附件.txt")) == File.ReadAllText(source), "重试文件不正确");
                    });
                    test("自动接收开关：关闭时等待、设置恢复后处理排队文件、目录持久化", () =>
                    {
                        receiver.Settings.AutoReceiveFiles = false; string nextFolder = Path.Combine(directory, "设置指定目录"); receiver.Settings.ReceiveFolder = nextFolder;
                        sender.SendMessage(sender.Peers.Single(), "需要手动确认", new[] { source }); Pump(() => chat.PendingFileCount == 1, "未保留等待任务");
                        for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(10); } Check(!Directory.Exists(nextFolder), "关闭自动接收后仍下载了文件");
                        MenuDialog<SettingsDialog>(() => Find<Button>(main, "菜单").PerformClick(), "设置", dialog =>
                        {
                            Check(!Find<ToggleSwitch>(dialog, "自动接收文件").Checked && Find<TextBox>(dialog, "文件保存文件夹").Text == nextFolder, "设置未回显");
                            Find<ToggleSwitch>(dialog, "自动接收文件").Checked = true; Button(dialog, "保存").PerformClick();
                        });
                        Pump(() => chat.PendingFileCount == 0, "开启自动接收没有处理排队文件"); Check(File.Exists(Path.Combine(nextFolder, "后台附件.txt")) && store.LoadSettings().ReceiveFolder == nextFolder && store.LoadSettings().AutoReceiveFiles, "新目录或接收开关未持久化");
                    });
                    test("文件气泡菜单：接收完成、重新读取历史、复制路径和多个文件入口", () =>
                    {
                        main.OpenChat(peer); BubbleHistory history = Find<BubbleHistory>(chat, "聊天记录");
                        typeof(BubbleHistory).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(history, 0);
                        history.ContextMenuStrip.Show(history, new Point(20, 20)); ToolStripMenuItem folderItem = history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "打开文件所在目录");
                        Check(folderItem.Visible && folderItem.DropDownItems.Count == 3, "多文件邀请没有目录子菜单"); history.ContextMenuStrip.Close();
                        using (HistoryDialog dialog = new HistoryDialog(new LocalStore(store.Root), peer))
                        {
                            dialog.Show(main); BubbleHistory stored = Find<BubbleHistory>(dialog, "聊天记录");
                            typeof(BubbleHistory).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(stored, 1);
                            IDataObject clipboard = Clipboard.GetDataObject();
                            try { stored.ContextMenuStrip.Show(stored, new Point(20, 20)); ClickMenu(stored.ContextMenuStrip, "复制文件路径"); Check(File.Exists(Clipboard.GetText()), "历史右键未复制真实路径"); }
                            finally { if (clipboard != null) Clipboard.SetDataObject(clipboard, true); else Clipboard.Clear(); }
                            dialog.Close();
                        }
                        Render(main, "preview-file-context");
                    });
                    test("自动接收取消：真实停滞 TCP、无半成品、队列可拒绝", () =>
                    {
                        string cancelledFolder = Path.Combine(directory, "取消自动接收"); receiver.Settings.ReceiveFolder = cancelledFolder;
                        TcpListener server = new TcpListener(IPAddress.Loopback, 0); server.Start();
                        using (UdpClient announce = new UdpClient((IPEndPoint)server.LocalEndpoint))
                        using (ManualResetEventSlim release = new ManualResetEventSlim(false))
                        {
                            Task hold = Task.Run(() => { using (TcpClient connection = server.AcceptTcpClient()) { connection.GetStream().Write(new byte[32], 0, 32); release.Wait(30000); } });
                            try
                            {
                                byte[] bytes = Protocol.Encode(42, "cancel-ui", "local", Protocol.SendMessage | Protocol.FileAttach | Protocol.Utf8, "取消测试", Protocol.FileList(new[] { new Attachment { Id = 0, Name = "取消.bin", Size = 1048576 } }));
                                announce.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, receiverPort));
                                Pump(() => Directory.Exists(cancelledFolder) && Directory.GetFiles(cancelledFolder, "*.part").Length == 1, "自动接收未连接停滞测试服务器");
                                main.OpenChat(receiver.Peers.Single(p => p.Login == "cancel-ui")); ChatForm cancelChat = main.ActiveChat; Button(cancelChat, "取消").PerformClick();
                                Pump(() => !Directory.GetFiles(cancelledFolder).Any() && All(cancelChat).OfType<Button>().Any(button => button.Text == "接收" && button.Enabled) && Button(cancelChat,"拒绝").Enabled, "取消没有中止传输或清理半成品");
                                Check(cancelChat.PendingFileCount == 1 && !cancelChat.HasAutomaticFile, "取消后立即重试或丢失任务");
                                Button(cancelChat,"拒绝").PerformClick(); Check(cancelChat.PendingFileCount == 0, "取消后不能拒绝邀请");
                            }
                            finally { release.Set(); server.Stop(); try { hold.Wait(7000); } catch (AggregateException) { } }
                        }
                    });
                }
                finally { main.ExitApplication(); Application.DoEvents(); }
            }
        }
        public static void Run(string directory, Action<string, Action> test)
        {
            test("文件进度卡片：逐文件渲染、方向隔离与更新不抢滚动", () => {
                using(Form window=new Form {ClientSize=new Size(620,660)})
                using(BubbleHistory history=new BubbleHistory {Dock=DockStyle.Fill}) {
                    window.Controls.Add(history);window.Show();
                    ChatRecord sent=new ChatRecord {Packet=42,PeerId="progress",Text="[文件] 大文件.zip、报告.pdf",Sender="本机",Outgoing=true,State="已确认送达",Time=DateTime.Now,
                        Transfers=new[]{new FileTransferState{Id=0,Name="大文件.zip",Size=1073741824,Percent=47,Status="发送中"},new FileTransferState{Id=1,Name="报告.pdf",Size=2048,Percent=0,Status="等待对方接收"}}};
                    history.Append(sent);
                    ChatRecord received=new ChatRecord {Packet=42,PeerId="progress",Text="",Sender="对端",State="已收到",Time=DateTime.Now,
                        Transfers=new[]{new FileTransferState{Id=0,Name="接收文件.mp4",Size=1073741824,Percent=26,Status="接收中 · 点击取消"}}};
                    history.Append(received);Application.DoEvents();Render(window,"preview-file-progress-light");
                    var rectangles=(List<Rectangle>)typeof(BubbleHistory).GetField("bounds",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history);
                    // 与自绘控件相同，使用实际 Graphics DPI；系统缩放下 DeviceDpi 不一定等于绘图 DPI。
                    using(Graphics graphics=history.CreateGraphics())Check(rectangles[0].Height<=200*graphics.DpiY/96F&&rectangles[1].Height<=135*graphics.DpiY/96F,"文件气泡残留过大固定空白");
                    var hits=(System.Collections.ICollection)typeof(BubbleHistory).GetField("fileHits",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history);Check(hits.Count==3,"缺少每个文件的独立卡片");
                    for(int i=0;i<12;i++)history.Append(new ChatRecord{Packet=100+i,Text="旧消息阅读位置",Sender="测试",Time=DateTime.Now});
                    history.AutoScrollPosition=new Point(0,40);int y=history.AutoScrollPosition.Y;sent.Transfers[0].Percent=83;history.Delivery(sent);Application.DoEvents();
                    Check(history.AutoScrollPosition.Y==y,"发送进度抢走旧消息位置");Check(received.Transfers[0].Percent==26,"同号入站进度被出站覆盖");
                    history.Clear();Check(sent.DisplayText()=="","重复显示附件清单");window.Close();
                }
            });
            test("会话菜单：备注、置顶、移除恢复和清空隔离真实控件交互",()=>{
                string folder=Path.Combine(directory,"conversation-ui");LocalStore store=new LocalStore(folder);AppSettings settings=new AppSettings {CloseToTray=false,ReceiveFolder=Path.Combine(folder,"received")};
                settings.Conversations=new[]{new SavedConversation {Endpoint="127.0.0.1:32425",Login="a",Nickname="项目讨论"},new SavedConversation {Endpoint="127.0.0.1:32426",Login="b",Nickname="设计同事"}};
                using(LanService network=new LanService(settings,Port(),true,"contact-ui"))using(MainForm main=new MainForm(network,store)) {
                    Peer a=network.Peers.Single(p=>p.Login=="a"),b=network.Peers.Single(p=>p.Login=="b");
                    store.Append(new ChatRecord {PeerId=a.Id,Packet=1,Text="请看 https://example.com/design?q=1，明天讨论。",Time=DateTime.Now.AddMinutes(-1),Sender="项目讨论",State="已收到"});store.Append(new ChatRecord {PeerId=b.Id,Packet=2,Text="另一会话",Time=DateTime.Now.AddMinutes(-1),Sender="设计同事",State="已收到"});
                    main.Show();main.OpenChat(a);Application.DoEvents();
                    bool deleteWithHistory=false;Action<string,bool> menuAction=delegate(string label,bool editNote){
                        typeof(MainForm).GetMethod("ShowConversationMenu",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(main,new object[]{a});ContextMenuStrip menu=(ContextMenuStrip)typeof(MainForm).GetField("conversationMenu",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);
                        using(System.Windows.Forms.Timer answer=new System.Windows.Forms.Timer {Interval=30}){
                            answer.Tick+=delegate {ConversationDialog dialog=Application.OpenForms.OfType<ConversationDialog>().FirstOrDefault();if(dialog==null)return;answer.Stop();if(editNote)Find<TextBox>(dialog,"联系人备注").Text="产品负责人";else if(deleteWithHistory)Find<ToggleSwitch>(dialog,"同时删除聊天记录").Checked=true;Button(dialog,editNote?"保存":"确认删除").PerformClick();};
                            if(label.Contains("备注")||label.Contains("删除")||label.Contains("清空"))answer.Start();menu.Items.Cast<ToolStripItem>().Single(i=>i.Text.StartsWith(label)).PerformClick();Application.DoEvents();
                        }
                    };
                    menuAction("设置备注",true);Check(a.DisplayName=="产品负责人"&&a.Nickname=="项目讨论","备注改动了对方身份或未生效");menuAction("置顶",false);
                    ConversationList contacts=All(main).OfType<ConversationList>().Single();Check(contacts.Items[0].Peer.Id==a.Id&&a.Pinned,"置顶排序未生效");Check(store.LoadSettings().Conversations.Single(c=>c.Login=="a").Note=="产品负责人","备注没有持久化");Render(main,"preview-conversation-links");
                    menuAction("删除联系人",false);Check(a.Hidden&&contacts.Items.All(c=>c.Peer.Id!=a.Id),"移除后仍显示");Check(store.History(a.Id,10).Count==1,"仅移除列表误删记录");main.OpenChat(a);Application.DoEvents();Check(!a.Hidden&&main.ActiveChat!=null,"恢复入口失败");
                    menuAction("清空聊天记录",false);Check(store.History(a.Id,10).Count==0&&store.History(b.Id,10).Count==1,"清空影响其他会话");Check(All(main.ActiveChat).OfType<BubbleHistory>().Single().TextLength==0,"清空没有刷新聊天画面");
                    store.Append(new ChatRecord {PeerId=a.Id,Packet=3,Text="删除连同记录",Time=DateTime.Now.AddSeconds(1),Sender="测试",State="已收到"});deleteWithHistory=true;menuAction("删除联系人",false);Check(a.Hidden&&store.History(a.Id,10).Count==0,"删除联系人并清空记录失败");main.ExitApplication();
                }
            });
            test("背景图片：多会话共享、源文件无锁、损坏图片安全回退",()=>{
                string image=Path.Combine(directory,"wallpaper.png");using(Bitmap bitmap=new Bitmap(80,60)){using(Graphics g=Graphics.FromImage(bitmap))g.Clear(Color.SeaGreen);bitmap.Save(image,ImageFormat.Png);}
                using(BubbleHistory first=new BubbleHistory())using(BubbleHistory second=new BubbleHistory()){
                    first.Size=second.Size=new Size(320,240);first.SetWallpaper("image",image);second.SetWallpaper("image",image);
                    FieldInfo asset=typeof(BubbleHistory).GetField("wallpaperImage",BindingFlags.Instance|BindingFlags.NonPublic);Check(asset.GetValue(first)!=null&&Object.ReferenceEquals(asset.GetValue(first),asset.GetValue(second)),"同一背景被重复分配");
                    first.Dispose();using(Bitmap rendered=new Bitmap(320,240))second.DrawToBitmap(rendered,new Rectangle(0,0,320,240));
                    File.WriteAllText(image,"invalid image");second.SetWallpaper("image",image);Check(asset.GetValue(second)==null,"坏图未回退");
                }
                File.Delete(image);
            });
            test("外观设置：预设、字号、背景保存与预览排版",()=>{
                string folder=Path.Combine(directory,"appearance-ui");LocalStore store=new LocalStore(folder);AppSettings settings=new AppSettings {ReceiveFolder=Path.Combine(folder,"received")};
                using(SettingsDialog dialog=new SettingsDialog(settings,folder,2425)){
                    dialog.Show();Application.DoEvents();Panel scroll=(Panel)typeof(SettingsDialog).GetField("body",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(dialog);
                    Control appearance=All(dialog).Single(c=>c.AccessibleName=="聊天外观");scroll.ScrollControlIntoView(appearance);Application.DoEvents();Button(dialog,"薄荷").PerformClick();Find<TrackBar>(dialog,"聊天字号").Value=16;All(dialog).OfType<Button>().Single(b=>b.Text.EndsWith("流线")).PerformClick();foreach(TableLayoutPanel grid in All(appearance).OfType<TableLayoutPanel>()){Button[] buttons=grid.Controls.OfType<Button>().ToArray();for(int i=0;i<buttons.Length;i++)for(int j=i+1;j<buttons.Length;j++){if(buttons[i].Top==buttons[j].Top)Check(Math.Max(buttons[i].Left,buttons[j].Left)-Math.Min(buttons[i].Right,buttons[j].Right)>=10,"外观按钮横向间距不足");if(buttons[i].Left==buttons[j].Left)Check(Math.Max(buttons[i].Top,buttons[j].Top)-Math.Min(buttons[i].Bottom,buttons[j].Bottom)>=10,"外观按钮纵向间距不足");}}Render(dialog,"preview-appearance-settings");
                    Button(dialog,"保存").PerformClick();Check(dialog.Value!=null&&dialog.Value.ChatFontSize==16&&dialog.Value.ChatBackground=="lines"&&dialog.Value.Accent=="green"&&!dialog.Value.DarkMode,"外观设置未保存");store.SaveSettings(dialog.Value);Check(store.LoadSettings().ChatFontSize==16,"字号重启不保留");dialog.Close();
                }
            });
            test("诊断：默认关闭、持久化、轮转限额及清除不影响历史",()=>{
                string folder=Path.Combine(directory,"diagnostic-test");LocalStore store=new LocalStore(folder);AppSettings settings=store.LoadSettings();
                Diagnostics log=new Diagnostics(folder,()=>settings.DiagnosticsEnabled);log.Record(DiagnosticEvent.Started);Check(!Directory.Exists(Path.Combine(folder,"diagnostics")),"默认产生诊断文件");
                settings.DiagnosticsEnabled=true;store.SaveSettings(settings);Check(store.LoadSettings().DiagnosticsEnabled,"诊断开关未持久化");
                for(int i=0;i<4000;i++)log.Record(DiagnosticEvent.FileStatus);
                string[] files=Directory.GetFiles(Path.Combine(folder,"diagnostics"));Check(files.Length==2&&files.All(f=>new FileInfo(f).Length<=Diagnostics.Limit),"诊断轮转超限");
                string text=log.Read();settings.DiagnosticsEnabled=false;log.Record(DiagnosticEvent.NetworkError);Check(text==log.Read(),"关闭后仍在写入");
                Check(!text.Contains(folder)&&!text.Contains(Environment.UserName),"诊断泄露身份或路径");
                log.Clear();Check(Directory.GetFiles(Path.Combine(folder,"diagnostics")).Length==0&&File.Exists(Path.Combine(folder,"settings.json")),"清除误删设置");
                settings.ReceiveFolder=Path.Combine(folder,"received");
                using(SettingsDialog dialog=new SettingsDialog(settings,folder,2425)) {
                    dialog.Show();Application.DoEvents();ToggleSwitch toggle=Find<ToggleSwitch>(dialog,"记录故障诊断");Check(!toggle.Checked,"诊断开关默认不符");toggle.Checked=true;
                    Panel scroll=(Panel)typeof(SettingsDialog).GetField("body",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(dialog);scroll.ScrollControlIntoView(Button(dialog,"清除诊断记录"));Application.DoEvents();Render(dialog,"preview-diagnostics-settings");
                    Button(dialog,"保存").PerformClick();Check(dialog.Value!=null&&dialog.Value.DiagnosticsEnabled,"设置页面未保存诊断开关");dialog.Close();
                }
            });
            test("气泡轮廓：左右尾角镜像且圆角留白", () => {
                Rectangle body = new Rectangle(20, 10, 180, 90);
                MethodInfo shape=typeof(BubbleHistory).GetMethod("BubblePath",BindingFlags.Static|BindingFlags.NonPublic);
                using(var outgoing=(System.Drawing.Drawing2D.GraphicsPath)shape.Invoke(null,new object[]{body,14,7,true}))
                using(var incoming=(System.Drawing.Drawing2D.GraphicsPath)shape.Invoke(null,new object[]{body,14,7,false})) {
                    Check(outgoing.GetBounds().Right>body.Right && incoming.GetBounds().Left<body.Left,"缺少气泡尾角");
                    Check(!outgoing.IsVisible(body.Left,body.Top),"圆角退化成直角");
                    PointF[] right=outgoing.PathPoints,left=incoming.PathPoints;
                    for(int i=0;i<right.Length;i++)Check(Math.Abs(right[i].X+left[i].X-220)<.01F&&Math.Abs(right[i].Y-left[i].Y)<.01F,"尾角未镜像");
                }
            });
            test("离线会话：重启仍显示联系人、预览和未读，历史与草稿可打开", () =>
            {
                LocalStore store=new LocalStore(Path.Combine(directory,"offline-roster"));int aPort=Port(),bPort=Port();string peerId;
                using(LanService remote=new LanService(new AppSettings { Nickname="离线测试联系人" },bPort,true,"offline-peer"))
                using(LanService local=new LanService(new AppSettings { Nickname="本机",Notifications=false },aPort,true,"offline-local"))
                using(MainForm window=new MainForm(local,store))
                {
                    typeof(MainForm).GetMethod("PrepareNetworkCallbacks",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
                    remote.Start();local.Start();window.NetworkStarted();remote.Probe(new IPEndPoint(IPAddress.Loopback,aPort));
                    Pump(()=>local.Peers.Count==1&&remote.Peers.Count==1,"联系人发现失败");peerId=local.Peers[0].Id;
                    remote.SendMessage(remote.Peers[0],"离线后仍可找到的历史",null);
                    Pump(()=>Find<ConversationList>(window,"会话列表").Items.Any(i=>i.Preview.Contains("仍可找到")),"新消息未显示");
                    Check(!window.Visible,"初始化网络回调不应抢先显示窗口");window.Show();
                    store.SaveDraft(peerId,"尚未发送的草稿");window.ExitApplication();
                }
                using(LanService restarted=new LanService(store.LoadSettings(),aPort,true,"offline-local"))
                using(MainForm window=new MainForm(restarted,store))
                {
                    restarted.Start();window.Show();window.NetworkStarted();Application.DoEvents();
                    ConversationList list=Find<ConversationList>(window,"会话列表");
                    Check(list.Items.Count==1,"重启后离线联系人丢失");Check(!list.Items[0].Peer.Online,"历史联系人被当成在线");
                    Check(list.Items[0].Preview.Contains("仍可找到")&&list.Items[0].Unread==1,"预览/未读没有恢复");
                    window.OpenChat(list.Items[0].Peer);Check(Find<BubbleHistory>(window.ActiveChat,"聊天记录").Text.Contains("仍可找到"),"离线历史无法读取");
                    Check(Find<RichTextBox>(window.ActiveChat,"消息输入框").Text=="尚未发送的草稿","离线草稿无法恢复");
                    Check(store.LoadSettings().Conversations.Single(c=>c.Id==peerId).Unread==0,"打开会话后的已读状态未保存");
                    Peer original=list.Items[0].Peer;
                    using(LanService remote=new LanService(new AppSettings { Nickname="重新上线并改名" },bPort,true,"offline-peer"))
                    {
                        remote.Start();restarted.Refresh();Pump(()=>original.Online&&original.Nickname=="重新上线并改名","历史联系人重连后没有原位更新");
                        Pump(()=>Find<Label>(window.ActiveChat,"聊天联系人昵称").Text=="重新上线并改名"&&Find<Label>(window.ActiveChat,"聊天联系人状态").Text=="在线","重连后的聊天抬头未刷新");
                        Check(Find<ConversationList>(window,"会话列表").Items.Count==1,"重连产生重复联系人");
                    }
                    Pump(()=>!original.Online&&Find<Label>(window.ActiveChat,"聊天联系人状态").Text=="离线","退出后状态未刷新");
                    Render(window,"preview-offline-history");window.ExitApplication();
                }
            });
            test("开机自启动：设置开关、取消不生效、托盘启动与恢复", () =>
            {
                LocalStore store=new LocalStore(Path.Combine(directory,"startup-ui"));
                AppSettings settings=new AppSettings { Nickname="自启动测试",Notifications=false,ReceiveFolder=Path.Combine(directory,"startup-received") };store.SaveSettings(settings);
                int port=Port();WindowsStartup startup=new WindowsStartup(Path.Combine(directory,"startup-links"),typeof(MainForm).Assembly.Location,store.Root,port,true);
                using(LanService node=new LanService(settings,port,true,"startup-ui"))
                using(MainForm window=new MainForm(node,store,startup,true))
                {
                    node.Start();window.NetworkStarted();window.Show();Application.DoEvents();
                    Check(!window.Visible&&window.IsHandleCreated,"自启动应隐藏主窗但保留消息句柄");
                    NotifyIcon tray=(NotifyIcon)typeof(MainForm).GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
                    typeof(NotifyIcon).GetMethod("OnDoubleClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(tray,new object[]{EventArgs.Empty});Application.DoEvents();Check(window.Visible,"托盘双击无法恢复窗口");
                    foreach(bool enable in new[]{true,false})
                    {
                        MenuDialog<SettingsDialog>(()=>Find<Button>(window,"菜单").PerformClick(),"设置",dialog=>{
                            ToggleSwitch toggle=Find<ToggleSwitch>(dialog,"开机自启动");Check(toggle.Enabled,"真实启动项适配器下开关不可用");toggle.Checked=enable;
                            if(enable)Render(dialog,"preview-startup-settings");Button(dialog,"保存").PerformClick();
                        });
                        Check(startup.IsEnabled==enable&&store.LoadSettings().StartWithWindows==enable,"设置开关未实际写入/移除启动项");
                        if(enable) {
                            MenuDialog<SettingsDialog>(()=>Find<Button>(window,"菜单").PerformClick(),"设置",dialog=>{Find<ToggleSwitch>(dialog,"开机自启动").Checked=false;Button(dialog,"取消").PerformClick();});
                            Check(startup.IsEnabled&&store.LoadSettings().StartWithWindows,"取消设置仍改变了启动项");
                        }
                    }
                    window.ExitApplication();
                }
            });
            Application.EnableVisualStyles();
            test("窗口持久化：自动保存尺寸位置、重启最大化、托盘与屏幕越界恢复", () =>
            {
                LocalStore placementStore = new LocalStore(Path.Combine(directory, "window-placement"));
                AppSettings settings = new AppSettings { Nickname = "窗口测试", Notifications = false };
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Rectangle wanted = new Rectangle(area.Left + 12, area.Top + 12, Math.Min(960, area.Width - 24), Math.Min(640, area.Height - 24));
                using (LanService node = new LanService(settings, Port(), true, "window-test"))
                using (MainForm window = new MainForm(node, placementStore))
                {
                    window.Show(); Application.DoEvents(); window.Bounds = wanted;
                    Pump(() => placementStore.LoadSettings().Window != null && placementStore.LoadSettings().Window.Width == wanted.Width, "移动缩放没有自动落盘");
                    WindowPlacement saved = placementStore.LoadSettings().Window;
                    Check(saved.X == wanted.X && saved.Y == wanted.Y && saved.Height == wanted.Height, "窗口位置或高度未保存");
                    window.WindowState = FormWindowState.Maximized; Application.DoEvents();
                    window.WindowState = FormWindowState.Minimized; Application.DoEvents(); window.Close();
                    Check(placementStore.LoadSettings().Window.Maximized, "最小化/托盘覆盖了最大化状态");
                    window.ExitApplication();
                }
                using (LanService node = new LanService(placementStore.LoadSettings(), Port(), true, "window-restored"))
                using (MainForm window = new MainForm(node, placementStore))
                {
                    window.Show(); Application.DoEvents(); Check(window.WindowState == FormWindowState.Maximized, "重启未恢复最大化");
                    window.WindowState = FormWindowState.Normal; Application.DoEvents(); Check(window.Bounds == wanted, "重启后普通窗口尺寸丢失"); window.ExitApplication();
                }
                settings.Window = new WindowPlacement { X = 90000, Y = 90000, Width = 1200, Height = 800 };
                using (LanService node = new LanService(settings, Port(), true, "window-offscreen"))
                using (MainForm window = new MainForm(node, placementStore))
                { window.Show(); Application.DoEvents(); Check(Screen.FromControl(window).WorkingArea.Contains(window.Bounds), "窗口仍在显示器外"); window.ExitApplication(); }
            });
            ReceiveWorkflow(directory, test);
            int portA = Port(), portB = Port(); while (portA == portB) portB = Port();
            LocalStore storeA = new LocalStore(Path.Combine(directory, "ui-a")), storeB = new LocalStore(Path.Combine(directory, "ui-b"));
            using (LanService a = new LanService(new AppSettings { Nickname = "测试节点 A", Group = "界面验证", Signature = "仅回环测试，不连接真实局域网", Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(directory, "received-a") }, portA, true, "ui-a"))
            using (LanService b = new LanService(new AppSettings { Nickname = "测试节点 B", Group = "界面验证", Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(directory, "received-b") }, portB, true, "ui-b"))
            using (MainForm mainA = new MainForm(a, storeA))
            using (MainForm mainB = new MainForm(b, storeB))
            {
                mainA.Text += " · 自动化验证"; mainB.Text += " · 自动化验证";
                a.Start(); b.Start(); mainA.Show(); mainB.Show(); mainA.NetworkStarted(); mainB.NetworkStarted();
                ChatForm chatA = null, chatB = null;
                List<ToolStripDropDown> ownedMenus = new List<ToolStripDropDown>();
                try
                {
                    test("自绘 UI：会话列表、空状态与统一控件样式", () =>
                    {
                        Render(mainA, "preview-empty"); a.Probe(new IPEndPoint(IPAddress.Loopback, portB));
                        Pump(() => Find<ConversationList>(mainA, "会话列表").Items.Count == 1, "联系人未呈现");
                        ConversationList list = Find<ConversationList>(mainA, "会话列表"); Check(list.Items[0].Peer.Nickname == "测试节点 B", "联系人显示不正确");
                        Check(!All(mainA).Any(c => c is TreeView || c is TabControl), "统一界面残留原生树或标签页");
                        Check(All(mainA).OfType<Button>().All(c => c is RoundedButton), "按钮未统一自绘");
                        Render(mainA, "preview-main");
                    });
                    test("原生 UI：搜索过滤与恢复", () =>
                    {
                        TextBox search = Find<TextBox>(mainA, "查找联系人"); search.Text = "不存在的联系人";
                        Check(Find<ConversationList>(mainA, "会话列表").Items.Count == 0, "筛选未生效");
                        search.Clear(); Check(Find<ConversationList>(mainA, "会话列表").Items.Count == 1, "清空搜索未恢复");
                    });
                    test("自绘 UI：会话列表点击进入统一主窗口", () =>
                    {
                        ConversationList list = Find<ConversationList>(mainA, "会话列表");
                        // 控件级测试触发真实事件处理器，不替代系统鼠标命中测试。
                        typeof(ConversationList).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(list, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 40, 40, 0) });
                        chatA = mainA.ActiveChat;
                        Check(chatA.Visible && Find<RichTextBox>(chatA, "消息输入框").CanFocus, "聊天窗口或输入框不可用");
                        Check(!chatA.TopLevel && chatA.Parent != null, "聊天没有嵌入主窗口");
                        mainB.OpenChat(b.Peers.Single(p => p.Login == "ui-a")); chatB = mainB.ActiveChat;
                    });
                    if (chatA != null && chatB != null)
                    {
                        test("在线头像：光边渲染、离线还原与聊天标题即时更新", () =>
                        {
                            foreach(int size in new[]{46,52,78,104}) using(Bitmap offline=Theme.ContactAvatar(size,"头像",false)) using(Bitmap online=Theme.ContactAvatar(size,"头像",true))
                            {
                                Check(offline.GetPixel(size/2,size/2)==online.GetPixel(size/2,size/2),"光边改变了头像中心");
                                int halo=Theme.AvatarHalo(size),center=online.Width/2;
                                Check(offline.GetPixel(center,halo/2).A==0&&online.GetPixel(center,halo/2).A>0,"头像外没有光晕");
                                Check(online.GetPixel(center,halo-1).A>online.GetPixel(center,1).A,"光晕没有向外渐隐");
                                for(int y=0;y<offline.Height;y++)for(int x=0;x<offline.Width;x++)if(offline.GetPixel(x,y).A==255)Check(offline.GetPixel(x,y)==online.GetPixel(x,y),"外发光染色了头像内部");
                                Check(online.GetPixel(0,0).A==0,"光边填满了透明角落");
                            }
                            Peer contact=a.Peers.Single(p=>p.Login=="ui-b"); PictureBox portrait=Find<PictureBox>(chatA,"聊天联系人头像");
                            try {
                                contact.Online=false;chatA.RefreshPeer();Image offline=portrait.Image;
                                Check(portrait.AccessibleDescription=="离线","离线标记未更新");
                                contact.Online=true;chatA.RefreshPeer();Check(!Object.ReferenceEquals(offline,portrait.Image)&&portrait.AccessibleDescription=="在线","上线未刷新头像");
                                Image online=portrait.Image;chatA.RefreshPeer();Check(Object.ReferenceEquals(online,portrait.Image),"未变化的头像仍反复创建位图");
                                foreach(bool dark in new[]{false,true}) {
                                    Theme.Change(dark,"purple");
                                    using(Bitmap preview=new Bitmap(380,140)) using(Graphics g=Graphics.FromImage(preview)) {
                                        g.Clear(Theme.Surface);
                                        for(int i=0;i<2;i++) {using(Bitmap image=Theme.ContactAvatar(64,"联系人",i==0))g.DrawImageUnscaled(image,50+i*180-Theme.AvatarHalo(64),20-Theme.AvatarHalo(64));TextRenderer.DrawText(g,i==0?"在线":"离线",Theme.Body,new Point(64+i*180,100),Theme.Ink);}
                                        preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-presence-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
                                    }
                                }
                            } finally {contact.Online=true;chatA.RefreshPeer();Theme.Change(false,"blue");}
                        });
                        test("聊天菜单：语义图标、字号单选、鼠标键盘和明暗渲染", () =>
                        {
                            int original=a.Settings.ChatFontSize;
                            try {
                                foreach(bool dark in new[]{false,true}) {
                                    Theme.Change(dark,"blue");Find<Button>(chatA,"会话菜单").PerformClick();ContextMenuStrip menu=(ContextMenuStrip)OpenMenu();
                                    var actions=menu.Items.OfType<ToolStripMenuItem>().ToArray();var glyphs=actions.Select(i=>Theme.MenuGlyph(i.Text)).ToArray();
                                    Check(glyphs.Length==6&&glyphs.All(s=>s.Length>0&&s!="more")&&glyphs.Distinct().Count()==6,"聊天菜单图标重复或缺失");
                                    Check(menu.Items.OfType<ToolStripSeparator>().Count()==2&&menu.Width>=220,"菜单分组或宽度未生效");
                                    ToolStripMenuItem font=actions.Single(i=>i.Text=="字号");font.ShowDropDown();Application.DoEvents();
                                    ToolStripDropDown sizes=font.DropDown;var options=sizes.Items.OfType<ToolStripMenuItem>().ToArray();
                                    Check(options.All(i=>Theme.MenuGlyph(i.Text)=="")&&options.Count(i=>i.Checked)==1,"字号菜单有装饰图标或单选标记错误");
                                    Check(options.Single(i=>i.Checked).Text==a.Settings.ChatFontSize.ToString(),"当前字号标错");
                                    Rectangle bounds=Rectangle.Union(menu.Bounds,sizes.Bounds);
                                    using(Bitmap preview=new Bitmap(bounds.Width,bounds.Height)) {
                                        using(Graphics g=Graphics.FromImage(preview)) g.Clear(Theme.Ice);
                                        menu.DrawToBitmap(preview,new Rectangle(menu.Left-bounds.Left,menu.Top-bounds.Top,menu.Width,menu.Height));
                                        sizes.DrawToBitmap(preview,new Rectangle(sizes.Left-bounds.Left,sizes.Top-bounds.Top,sizes.Width,sizes.Height));
                                        preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-chat-menu-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
                                    }
                                    if(!dark) ClickMenu(sizes,"11");else {options.Single(i=>i.Text=="15").Select();MenuKey(sizes,Keys.Enter);}
                                    Check(a.Settings.ChatFontSize==(dark?15:11),"字号鼠标或键盘选择没有生效");
                                    Check(storeA.LoadSettings().ChatFontSize==a.Settings.ChatFontSize,"字号未持久化");
                                    menu.Close();
                                }
                            } finally {typeof(MainForm).GetMethod("SetChatFontSize",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(mainA,new object[]{original});Theme.Change(false,"blue");}
                        });
                        test("输入菜单：复制、剪切、纯文本粘贴、全选、删除、撤销与菜单复用", () =>
                        {
                            RichTextBox editor = Find<RichTextBox>(chatA, "消息输入框"); ContextMenuStrip menu = editor.ContextMenuStrip; Check(menu != null, "输入框未配置菜单"); ownedMenus.Add(menu);
                            IDataObject clipboard = Clipboard.GetDataObject();
                            try
                            {
                                editor.Text = "剪切文本"; editor.Select(0, 2); menu.Show(editor, new Point(2, 2)); ClickMenu(menu, "复制"); Check(Clipboard.GetText() == "剪切", "复制选区失败");
                                menu.Show(editor, new Point(2, 2)); ClickMenu(menu, "剪切"); Check(editor.Text == "文本", "剪切失败");
                                menu.Show(editor, new Point(2, 2)); ClickMenu(menu, "粘贴"); Check(editor.Text == "剪切文本", "粘贴失败");
                                menu.Show(editor, new Point(2, 2)); ClickMenu(menu, "全选"); Check(editor.SelectionLength == editor.TextLength, "全选失败");
                                menu.Show(editor, new Point(2, 2)); ClickMenu(menu, "删除"); Check(editor.TextLength == 0, "删除失败");
                                menu.Show(editor, new Point(2, 2)); Check(menu.Items[0].Enabled, "撤销状态未启用"); ClickMenu(menu, "撤销"); Check(editor.Text == "剪切文本", "撤销失败");
                                Clipboard.SetText("纯文本😊"); editor.SelectAll(); typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { new KeyEventArgs(Keys.Control | Keys.V) }); Check(editor.Text == "纯文本😊", "Ctrl+V 未按纯文本粘贴");
                                editor.ReadOnly = true; editor.Select(0, 0); menu.Show(editor, new Point(2, 2)); Check(!menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "粘贴").Enabled && !menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "复制").Enabled, "只读/空选区菜单状态错误"); menu.Close();
                                Check(!menu.IsDisposed && Object.ReferenceEquals(menu, editor.ContextMenuStrip), "输入菜单提前释放或未复用");
                                TextBox search = Find<TextBox>(mainA, "查找联系人"); Check(search.ContextMenuStrip != null, "搜索输入未统一菜单");
                            }
                            finally { editor.ReadOnly = false; editor.Clear(); chatA.SaveDraft(); if (clipboard != null) Clipboard.SetDataObject(clipboard, true); else Clipboard.Clear(); }
                        });
                        test("原生 UI：发送按钮到远端记录、ACK、历史持久化", () =>
                        {
                            const string text = "你好，飞秋！这是一条真实的回环测试消息。\n中文、换行与表情 😊 都能正常收发。";
                            Find<RichTextBox>(chatA, "消息输入框").Text = text; Find<Button>(chatA, "发送消息").PerformClick();
                            Pump(() => Find<BubbleHistory>(chatB, "聊天记录").Text.Contains(text), "远端界面未出现消息");
                            Pump(() => Find<BubbleHistory>(chatA, "聊天记录").HasDelivery("已送达"), "发送方未显示已送达");
                            Check(Find<RichTextBox>(chatA, "消息输入框").TextLength == 0, "发送后未清空输入");
                            Check(storeB.History(b.Peers.Single(p => p.Login == "ui-a").Id, 10).Single().Text == text, "收到的消息未保存");
                            Render(mainA, "preview-chat"); Render(mainB, "preview-received");
                        });
                        test("转发：目标选择、真实文本送达和目标草稿保护", () =>
                        {
                            RichTextBox editor = Find<RichTextBox>(chatA, "消息输入框"); editor.Text = "转发不能清空这份草稿";
                            Exception failure = null;
                            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 })
                            {
                                timer.Tick += delegate { ForwardDialog dialog = Application.OpenForms.OfType<ForwardDialog>().FirstOrDefault(); if (dialog == null) return; timer.Stop();
                                    try { All(dialog).OfType<ListBox>().Single().SelectedIndex = 0; Render(dialog, "preview-forward"); Button(dialog, "确认转发").PerformClick(); }
                                    catch (Exception e) { failure = e; dialog.Close(); } };
                                timer.Start(); typeof(MainForm).GetMethod("Forward", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mainA, new object[] { new ChatRecord { Text = "转发回环验证 😊\n保留换行", Sender = "来源", State = "已收到" } });
                            }
                            if (failure != null) throw failure;
                            Pump(() => Find<BubbleHistory>(chatB, "聊天记录").Text.Contains("转发回环验证"), "转发未送到远端");
                            Check(editor.Text == "转发不能清空这份草稿", "转发清空了草稿"); editor.Clear(); chatA.SaveDraft();
                        });
                        test("转发：部分附件拒绝、同名避让后正文正确及普通文本不误判", () =>
                        {
                            string local = Path.Combine(directory,"转发(1).txt"); File.WriteAllText(local,"内容");
                            ChatRecord record = new ChatRecord { Text="正文\n[文件] 原名.txt、另一个.txt",HasAttachments=true,AttachmentNames=new[]{"原名.txt","另一个.txt"},LocalFiles=new[]{local} };
                            bool rejected=false;try{record.ForwardText();}catch(InvalidOperationException){rejected=true;}Check(rejected,"部分接收被当作完整转发");
                            record.AttachmentNames=new[]{"原名.txt"};record.Text="正文\n[文件] 原名.txt";Check(record.ForwardText()=="正文","同名改名后泄漏附件展示标签");
                            Check(new ChatRecord{Text="普通正文 [文件] 不是真附件"}.ForwardText()=="普通正文 [文件] 不是真附件","普通正文被修改");
                        });
                        test("原生 UI：Enter 事件发送与空消息防护", () =>
                        {
                            RichTextBox editor = Find<RichTextBox>(chatB, "消息输入框"); editor.Text = "收到！发送确认与本地记录也都正常。";
                            typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { new KeyEventArgs(Keys.Enter) });
                            Pump(() => Find<BubbleHistory>(chatA, "聊天记录").Text.Contains("收到！"), "Enter 未触发发送");
                            int length = Find<BubbleHistory>(chatB, "聊天记录").TextLength; Find<Button>(chatB, "发送消息").PerformClick();
                            Check(Find<BubbleHistory>(chatB, "聊天记录").TextLength == length, "空消息被发送");
                            Render(mainA, "preview-chat");
                        });
                        test("滚动：浏览旧消息后发送跳到最新，收到消息不抢位置", () => {
                            BubbleHistory history=Find<BubbleHistory>(chatA,"聊天记录");
                            for(int i=0;i<40;i++) history.Append(new ChatRecord {Packet=100000+i,Text="滚动测试记录 "+i,Sender="测试",State="已收到",Time=DateTime.Now});
                            history.AutoScrollPosition=Point.Empty;history.Append(new ChatRecord {Packet=200000,Text="收到消息不抢位置",Sender="测试",State="已收到"});Check(history.AutoScrollPosition.Y==0,"收到消息强制滚动");
                            Find<RichTextBox>(chatA,"消息输入框").Text="发送后跳到最新";Find<Button>(chatA,"发送消息").PerformClick();Application.DoEvents();
                            Check(-history.AutoScrollPosition.Y+history.ClientSize.Height>=history.AutoScrollMinSize.Height-4,"发送后没有滚到最新");
                        });
                        test("原生 UI：最小尺寸布局与关闭后恢复会话", () =>
                        {
                            mainA.Size = mainA.MinimumSize; Application.DoEvents(); Render(mainA, "preview-chat-minimum");
                            Check(Find<Button>(chatA, "发送消息").Bounds.Width >= 38, "最小尺寸下发送按钮丢失");
                            foreach (Button button in All(mainA).OfType<Button>().Where(c => c.Visible)) Check(button.Parent.ClientRectangle.Contains(button.Bounds), "按钮超出容器：" + button.Text);
                            chatA.Close(); Check(!chatA.Visible && !chatA.IsDisposed, "关闭没有收起会话");
                            mainA.OpenChat(a.Peers.Single(p => p.Login == "ui-b")); Check(chatA.Visible, "无法重新打开会话");
                        });
                        test("窗口缩放：连续 80 次、独立双缓冲与最大化恢复", () =>
                        {
                            int regions = 0; EventHandler changed = delegate { regions++; }; mainA.RegionChanged += changed;
                            try
                            {
                                Check((GetWindowLong(mainA.Handle, -20) & 0x02000000) == 0, "主窗口仍强制整树合成");
                                for (int i = 0; i < 80; i++) { mainA.Size = new Size(880 + i % 16 * 18, 580 + i % 10 * 17); Application.DoEvents(); mainA.Update(); }
                                Check(regions == 0 && mainA.Region == null, "缩放仍然重建裁剪区域");
                                mainA.WindowState = FormWindowState.Maximized; Application.DoEvents(); mainA.WindowState = FormWindowState.Normal; mainA.Size = new Size(1120, 760); Application.DoEvents();
                                Check(Find<RichTextBox>(chatA, "消息输入框").CanFocus && Find<BubbleHistory>(chatA, "聊天记录").TextLength > 0, "缩放后输入或记录异常");
                                foreach (Button button in All(mainA).OfType<Button>().Where(c => c.Visible)) Check(button.Parent.ClientRectangle.Contains(button.Bounds), "缩放后按钮越界：" + button.Text);
                                Render(mainA, "preview-resized");
                            }
                            finally { mainA.RegionChanged -= changed; }
                        });
                        test("遮挡恢复：对端断开/重连、输入、菜单、最小化与真实重绘", () =>
                        {
                            RichTextBox input=Find<RichTextBox>(chatA,"消息输入框"); string draft=input.Text;
                            int paints=0; PaintEventHandler painted=delegate { paints++; };
                            mainA.Paint+=painted;
                            try
                            {
                                mainA.Activate(); Application.DoEvents();
                                for(int i=0;i<4;i++)
                                {
                                    int transientPort=Port();
                                    using(LanService transient=new LanService(new AppSettings { Notifications=false },transientPort,true,"occlusion-peer"))
                                    using(Form cover=new Form { StartPosition=FormStartPosition.Manual,Bounds=mainA.Bounds,BackColor=Color.DarkSlateGray,ShowInTaskbar=false })
                                    {
                                        transient.Start();transient.Probe(new IPEndPoint(IPAddress.Loopback,portA));
                                        Pump(()=>a.Peers.Any(p=>p.Login=="occlusion-peer"&&p.Online),"遮挡前临时联系人未上线");
                                        cover.Show(); cover.BringToFront(); Application.DoEvents();
                                        transient.Dispose();
                                        Pump(()=>a.Peers.Where(p=>p.Login=="occlusion-peer").All(p=>!p.Online),"遮挡期间未处理对端退出");
                                        using(LanService restored=new LanService(new AppSettings { Notifications=false },transientPort,true,"occlusion-peer"))
                                        {
                                            restored.Start();a.Refresh();
                                            Pump(()=>a.Peers.Any(p=>p.Login=="occlusion-peer"&&p.Online),"遮挡期间未自动找回重启节点");
                                        }
                                        input.Text="遮挡期间编辑 "+i; Application.DoEvents(); cover.Close();
                                    }
                                    mainA.Activate(); mainA.Invalidate(true); mainA.Update(); Application.DoEvents();
                                    Check(input.Text=="遮挡期间编辑 "+i,"遮挡后输入丢失");
                                    input.ContextMenuStrip.Show(input,new Point(8,8)); Application.DoEvents(); Check(OpenMenu().Visible,"遮挡后菜单失效"); MenuKey(OpenMenu(),Keys.Escape);
                                    mainA.WindowState=FormWindowState.Minimized; Application.DoEvents(); mainA.WindowState=FormWindowState.Normal; Application.DoEvents();
                                }
                                Check(paints>=4,"恢复后没有发生绘制"); a.Refresh();
                                Pump(()=>a.Peers.Any(p=>p.Login=="ui-b"&&p.Online),"遮挡后网络失效");
                                Render(mainA,"preview-occlusion-restored");
                                Check(storeA.LoadSettings().KnownPeers.Contains("127.0.0.1:"+portB),"真实发现的地址未持久化");
                            }
                            finally { input.Text=draft; mainA.Paint-=painted; }
                        });
                        test("菜单视觉：明暗主题、图标留白、快捷键、危险操作与键盘关闭", () =>
                        {
                            try
                            {
                                foreach(bool dark in new[]{false,true})
                                {
                                    Theme.Change(dark,"purple");
                                    using(ContextMenuStrip menu=Theme.Menu())
                                    {
                                        menu.Items.Add(new ToolStripMenuItem("复制消息") { ShortcutKeyDisplayString="Ctrl+C" });menu.Items.Add("转发…");menu.Items.Add("打开文件所在目录");menu.Items.Add(new ToolStripSeparator());menu.Items.Add("删除");
                                        menu.Show(mainA,new Point(70,90));menu.Items[1].Select();Application.DoEvents();
                                        Check(menu.ShowImageMargin&&menu.Items[0].Padding.Vertical>=14,"菜单缺少图标留白或垂直间距");
                                        using(Bitmap bitmap=new Bitmap(menu.Width,menu.Height)) {menu.DrawToBitmap(bitmap,new Rectangle(Point.Empty,menu.Size));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-menu-"+(dark?"dark":"light")+".png"),ImageFormat.Png);}
                                        MenuKey(menu,Keys.Escape);Check(!menu.Visible,"菜单无法键盘关闭");
                                    }
                                }
                            }
                            finally { Theme.Change(false,"blue"); }
                        });
                        test("自绘 UI：未读筛选、会话草稿保存与恢复", () =>
                        {
                            RichTextBox input = Find<RichTextBox>(chatA, "消息输入框"); input.Text = "尚未发送的草稿"; chatA.Close();
                            Check(storeA.LoadDraft(a.Peers.Single(p => p.Login == "ui-b").Id) == input.Text, "草稿没有持久化");
                            b.SendMessage(b.Peers.Single(p => p.Login == "ui-a"), "未读筛选验证", null);
                            Pump(() => Find<ConversationList>(mainA, "会话列表").Items.Any(i => i.Unread > 0), "未读数没有更新");
                            Button(mainA, "未读").PerformClick(); Check(Find<ConversationList>(mainA, "会话列表").Items.Count == 1, "未读筛选丢失会话");
                            Find<ConversationList>(mainA, "会话列表").ActivatePeer(0); Check(mainA.ActiveChat == chatA && input.Text == "尚未发送的草稿", "重新打开会话丢失草稿");
                            Check(Find<ConversationList>(mainA, "会话列表").Items.Count == 0, "打开会话后未清除未读");
                            Button(mainA, "全部").PerformClick(); input.Clear(); chatA.SaveDraft(); Check(storeA.LoadDraft(a.Peers.Single(p => p.Login == "ui-b").Id) == "", "空草稿未清理");
                        });
                        test("自绘 UI：附件抽屉、拒绝与最小窗口布局", () =>
                        {
                            string file = Path.Combine(directory, "界面附件.txt"); File.WriteAllText(file, "仅用于界面附件验证");
                            b.SendMessage(b.Peers.Single(p => p.Login == "ui-a"), "附件", new[] { file });
                            ListBox attachments = Find<ListBox>(chatA, "收到的文件列表"); Pump(() => attachments.Items.Count == 1 && attachments.Visible, "附件抽屉未展示");
                            Check(attachments.DrawMode == DrawMode.OwnerDrawFixed && attachments.BorderStyle == BorderStyle.None, "文件列表未自绘");
                            foreach (Button button in All(mainA).OfType<Button>().Where(c => c.Visible)) Check(button.Parent.ClientRectangle.Contains(button.Bounds), "附件展开时按钮越界：" + button.Text);
                            Render(mainA, "preview-files"); Button(chatA, "拒绝").PerformClick(); Check(attachments.Items.Count == 0 && !attachments.Visible, "拒绝后抽屉未收起");
                        });
                    }
                    test("菜单回归：主菜单鼠标选择设置并保存", () =>
                    {
                        MenuDialog<SettingsDialog>(() => Find<Button>(mainA, "菜单").PerformClick(), "设置", dialog =>
                        {
                            Find<TextBox>(dialog, "昵称").Text = "菜单设置测试"; Button(dialog, "保存").PerformClick();
                        });
                        Check(storeA.LoadSettings().Nickname == "菜单设置测试", "菜单设置未保存");
                    });
                    test("菜单回归：设置反复打开、取消、键盘选择与菜单复用", () =>
                    {
                        ToolStripDropDown first = null;
                        for (int i = 0; i < 8; i++)
                        {
                            MenuDialog<SettingsDialog>(() =>
                            {
                                Find<Button>(mainA, "菜单").PerformClick(); ToolStripDropDown current = OpenMenu();
                                if (first == null) { first = current; ownedMenus.Add(first); }
                                else Check(Object.ReferenceEquals(first, current), "每次打开菜单仍在创建新实例");
                            }, "设置", dialog =>
                            {
                                Check(!first.IsDisposed, "模态窗口的消息循环中菜单被提前释放");
                                Find<TextBox>(dialog, "昵称").Text = "不应保存的修改"; Button(dialog, "取消").PerformClick();
                            }, i % 2 == 1);
                            Check(!first.IsDisposed, "取消设置后菜单被释放");
                        }
                        Check(storeA.LoadSettings().Nickname == "菜单设置测试", "取消设置仍写入了配置");
                        Find<Button>(mainA, "菜单").PerformClick(); MenuKey(OpenMenu(), Keys.Escape);
                        Check(!first.Visible && !first.IsDisposed, "Esc 没有安全关闭菜单");
                    });
                    test("菜单回归：托盘菜单设置、添加联系人和关于对话框", () =>
                    {
                        NotifyIcon tray = (NotifyIcon)typeof(MainForm).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mainA);
                        ownedMenus.Add(tray.ContextMenuStrip);
                        MenuDialog<SettingsDialog>(() => tray.ContextMenuStrip.Show(mainA, new Point(20, 60)), "设置", dialog => Button(dialog, "取消").PerformClick());
                        MenuDialog<AddressDialog>(() => Find<Button>(mainA, "菜单").PerformClick(), "添加联系人", dialog => { Check(dialog.Visible, "添加联系人未显示"); });
                        MenuDialog<NoticeDialog>(() => Find<Button>(mainA, "菜单").PerformClick(), "关于", dialog => Button(dialog, "确定").PerformClick());
                    });
                    if (chatA != null)
                    {
                        test("菜单回归：聊天记录模态窗口、关闭和恢复会话", () =>
                        {
                            mainA.OpenChat(a.Peers.Single(p => p.Login == "ui-b"));
                            ToolStripDropDown first = null;
                            for (int i = 0; i < 3; i++)
                                MenuDialog<HistoryDialog>(() =>
                                {
                                    Find<Button>(chatA, "会话菜单").PerformClick(); ToolStripDropDown current = OpenMenu();
                                    if (first == null) { first = current; ownedMenus.Add(first); } else Check(Object.ReferenceEquals(first, current), "会话菜单未复用");
                                }, "聊天记录", dialog => Check(!first.IsDisposed, "打开聊天记录期间菜单被释放"), i % 2 == 1);
                            Find<Button>(chatA, "会话菜单").PerformClick(); ClickMenu(OpenMenu(), "关闭会话");
                            Check(!chatA.Visible && !first.IsDisposed, "收起会话错误释放了菜单");
                            mainA.OpenChat(a.Peers.Single(p => p.Login == "ui-b"));
                            Find<Button>(chatA, "会话菜单").PerformClick(); Check(Object.ReferenceEquals(first, OpenMenu()), "恢复会话没有复用菜单"); first.Close();
                        });
                        test("菜单回归：表情插入、嵌套字号菜单及重复打开", () =>
                        {
                            RichTextBox editor = Find<RichTextBox>(chatA, "消息输入框"); editor.Clear();
                            ToolStripDropDown first = null;
                            for (int i = 0; i < 6; i++)
                            {
                                Find<Button>(chatA, "表情").PerformClick(); ToolStripDropDown menu = OpenMenu();
                                if (first == null) { first = menu; ownedMenus.Add(first); } else Check(Object.ReferenceEquals(first, menu), "表情菜单未复用");
                                Control panel=((ToolStripControlHost)menu.Items[0]).Control;
                                Find<Button>(panel,"😊").PerformClick(); Check(!menu.Visible&&!menu.IsDisposed, "选择表情后未关闭或错误释放面板");
                            }
                            Check(editor.Text == String.Concat(Enumerable.Repeat("😊", 6)), "表情漏插入或重复绑定事件");
                            Find<Button>(chatA, "会话菜单").PerformClick(); ToolStripDropDown parent = OpenMenu();
                            ToolStripMenuItem size = (ToolStripMenuItem)parent.Items.Cast<ToolStripItem>().Single(i => i.Text == "字号");
                            size.ShowDropDown(); ownedMenus.Add(size.DropDown); ClickMenu(size.DropDown, "14");
                            Check(editor.Font.Size == 14F && !parent.IsDisposed, "字号选择失败或父菜单被释放");
                            Find<Button>(chatA, "会话菜单").PerformClick(); size.ShowDropDown(); ClickMenu(size.DropDown, "10");
                            Check(editor.Font.Size == 10F, "字号子菜单无法再次使用"); editor.Clear(); chatA.SaveDraft();
                        });
                    }
                    test("原生 UI：个人设置保存与网络同步", () =>
                    {
                        Exception callbackError = null;
                        using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 })
                        {
                            timer.Tick += delegate
                            {
                                SettingsDialog dialog = Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault(); if (dialog == null) return;
                                timer.Stop();
                                try { Find<TextBox>(dialog, "昵称").Text = "研发测试 A"; Render(dialog, "preview-settings"); Button(dialog, "保存").PerformClick(); }
                                catch (Exception e) { callbackError = e; dialog.Close(); }
                            };
                            timer.Start(); Button(mainA, "设置").PerformClick();
                        }
                        if (callbackError != null) throw callbackError;
                        Check(storeA.LoadSettings().Nickname == "研发测试 A", "设置没有持久化");
                        Pump(() => b.Peers.Any(p => p.Nickname == "研发测试 A"), "资料更改未同步");
                    });
                    test("失败文本重发：真实收发与重复点击保护",()=>{
                        Peer remote=a.Peers.Single(p=>p.Login=="ui-b");
                        ChatRecord failed=new ChatRecord {PeerId=remote.Id,Packet=800000,Text="手动重发 [文件] 只是文字 👩🏽‍💻 (๑•̀ㅂ•́)و✧\n末尾换行\n",Outgoing=true,State="未确认送达，请检查网络后重发",Time=DateTime.Now};chatA.Append(failed);
                        MethodInfo retry=typeof(ChatForm).GetMethod("RetryMessage",BindingFlags.Instance|BindingFlags.NonPublic);retry.Invoke(chatA,new object[]{failed});
                        Pump(()=>Find<BubbleHistory>(chatB,"聊天记录").Text.Contains(failed.Text),"重发没有到达对端");Check(failed.State=="已重新发送（新消息）","原记录未标记");
                        int count=storeA.History(remote.Id,150).Count(r=>r.Text==failed.Text);retry.Invoke(chatA,new object[]{failed});Check(storeA.History(remote.Id,150).Count(r=>r.Text==failed.Text)==count,"同一记录被重复发送");
                    });
                    test("表情分类与组合 emoji 不截断",()=>{
                        RichTextBox input=Find<RichTextBox>(chatA,"消息输入框");input.Text="前后";input.Select(1,0);
                        // 同时支持源码同程序集测试和引用交付 EXE 的外部包测试，不扩大产品 API 可见性。
                        MethodInfo insert=typeof(ChatForm).GetMethod("InsertEmoji",BindingFlags.Instance|BindingFlags.NonPublic);
                        Check((bool)insert.Invoke(chatA,new object[]{"👨‍👩‍👧‍👦"}),"插入失败");Check(input.Text=="前👨‍👩‍👧‍👦后","光标插入错误");
                        input.Text=new string('x',17999);input.Select(input.TextLength,0);Check(!(bool)insert.Invoke(chatA,new object[]{"😀"}),"边界插入截断了 emoji");Check(input.TextLength==17999,"拒绝后内容被修改");input.Clear();
                        Find<Button>(chatA,"表情").PerformClick();Application.DoEvents();
                        ToolStripDropDown menu=OpenMenu();Control panel=((ToolStripControlHost)menu.Items[0]).Control;
                        ComboBox category=Find<ComboBox>(panel,"表情分类");FlowLayoutPanel grid=Find<FlowLayoutPanel>(panel,"表情网格");
                        Check(category.Items.Count==7&&grid.Controls.Count==24,"表情面板不完整");
                        Check(grid.Controls.Cast<Control>().Select(c=>c.Left).Distinct().Count()>=6,"表情仍然排成长竖条");
                        foreach(Control cell in grid.Controls)Check(grid.ClientRectangle.Contains(cell.Bounds),"默认网格超出容器");
                        int count=0;for(int i=0;i<category.Items.Count;i++){category.SelectedIndex=i;count+=grid.Controls.Count;}Check(count==152,"表情数量不符");category.SelectedIndex=0;
                        input.Text="👩🏽‍💻 👨‍👩‍👧‍👦 🇨🇳 1️⃣ (๑•̀ㅂ•́)و✧";Render(mainA,"preview-emoji-chat");
                        using(Bitmap preview=new Bitmap(menu.Width,menu.Height)) {menu.DrawToBitmap(preview,new Rectangle(Point.Empty,preview.Size));preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-emoji-grid.png"),ImageFormat.Png);}
                        menu.Close();input.Clear();
                    });
                    test("表情网格：缩放尺寸约束、屏幕边缘与颜文字",()=>{
                        Type pickerType=typeof(ChatForm).Assembly.GetType("FeiqLight.EmojiPicker",true);
                        using(ToolStripDropDown picker=(ToolStripDropDown)Activator.CreateInstance(pickerType,new object[]{(Action<string>)delegate(string value){}})) {
                            Control panel=((ToolStripControlHost)picker.Items[0]).Control;ComboBox category=Find<ComboBox>(panel,"表情分类");FlowLayoutPanel grid=Find<FlowLayoutPanel>(panel,"表情网格");
                            foreach(int dpi in new[]{96,144,192})foreach(int index in new[]{0,6}) {
                                category.SelectedIndex=index;pickerType.GetMethod("Prepare",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(picker,new object[]{new Size(320,220),dpi});picker.PerformLayout();grid.PerformLayout();
                                Check(picker.Width<=320&&picker.Height<=220,"小工作区弹层越界");Check(grid.Controls.Cast<Control>().All(c=>c.Right<=grid.ClientSize.Width),"网格产生横向溢出");
                                Rectangle work=new Rectangle(-320,20,320,220);Point location=(Point)pickerType.GetMethod("Fit",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{new Rectangle(-8,230,8,8),picker.Size,work});Check(work.Contains(new Rectangle(location,picker.Size)),"负坐标屏幕边缘弹层出界");
                            }
                        }
                    });
                    test("彩色表情：面板与气泡使用彩色像素且不改变原始文字",()=>{
                        string text="😀 ❤️ 👩‍💻 👍🏽 🇨🇳";
                        foreach(bool dark in new[]{false,true}) {
                            Theme.Change(dark,"blue");using(Form host=new Form{ClientSize=new Size(460,220)})using(BubbleHistory history=new BubbleHistory{Dock=DockStyle.Fill}) {
                                host.Controls.Add(history);host.Show();history.Append(new ChatRecord{Text=text,Sender="彩色表情",State="已送达",Outgoing=true,Time=DateTime.Now});Application.DoEvents();
                                using(Bitmap bitmap=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));int warm=0;for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++){Color c=bitmap.GetPixel(x,y);if(c.R>180&&c.G>90&&c.G<220&&c.B<100)warm++;}Check(warm>20,"消息表情仍是单色字形");bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"preview-color-emoji-"+(dark?"dark":"light")+".png"),ImageFormat.Png);}
                                Check(history.Text.Contains(text),"彩色绘制改变了消息原文");host.Close();
                            }
                        }Theme.Change(false,"blue");
                    });
                    test("输入字体生命周期：相同字号反复应用、输入和缩放不使用已释放字体",()=>{
                        RichTextBox input=Find<RichTextBox>(chatA,"消息输入框");int original=a.Settings.ChatFontSize;
                        MethodInfo appearance=typeof(ChatForm).GetMethod("ApplyAppearance",BindingFlags.Instance|BindingFlags.NonPublic),setSize=typeof(MainForm).GetMethod("SetChatFontSize",BindingFlags.Instance|BindingFlags.NonPublic);
                        try {foreach(int size in new[]{9,10,14,18,10}) {setSize.Invoke(mainA,new object[]{size});Font font=input.Font;for(int i=0;i<6;i++){appearance.Invoke(chatA,null);Check(Object.ReferenceEquals(font,input.Font),"相同字号仍更换字体");Check(input.Font.Height>0,"当前字体已失效");input.Text="重复应用 "+i+"\n继续输入 😀";Application.DoEvents();input.Select(input.TextLength,0);input.ScrollToCaret();using(Bitmap bitmap=new Bitmap(chatA.Width,chatA.Height))chatA.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));}}}
                        finally {input.Clear();setSize.Invoke(mainA,new object[]{original});}
                    });
                    test("输入区：短文本无滚动箭头、长文本可滚到末尾",()=>{
                        RichTextBox input=Find<RichTextBox>(chatA,"消息输入框");input.Text="短消息";Application.DoEvents();int first=input.Height;Check(input.ScrollBars==RichTextBoxScrollBars.None,"短输入仍有原生滚动箭头");
                        input.Text=String.Join("\n",Enumerable.Range(0,40).Select(i=>"第 "+i+" 行输入"));Application.DoEvents();Check(input.Height>first,"多行输入区未增高");input.SelectionStart=input.TextLength;input.ScrollToCaret();Point end=input.GetPositionFromCharIndex(input.TextLength-1);Check(end.Y>=0&&end.Y<input.ClientSize.Height,"长输入不能到达末尾");input.Text="😀 发送消息";Render(mainA,"preview-compose-clean");input.Clear();
                    });
                    test("长提示可滚动、只读且确认按钮不被挤走",()=>{
                        string text=String.Concat(Enumerable.Repeat("文件路径很长，需要完整展示错误原因 👩🏽‍💻\r\n",80));
                        using(NoticeDialog dialog=new NoticeDialog("长错误信息",text)) {
                            dialog.Show(mainA);Application.DoEvents();RichTextBox details=Find<RichTextBox>(dialog,"提示详情");Check(details.ReadOnly&&details.Text.Replace("\r","")==text.Replace("\r",""),"提示被截断或可编辑");
                            details.SelectionStart=details.TextLength;details.ScrollToCaret();Point end=details.GetPositionFromCharIndex(details.TextLength-1);Check(end.Y>=0&&end.Y<details.ClientSize.Height,"不能滚到提示末尾");
                            Check(Button(dialog,"确定").Parent.ClientRectangle.Contains(Button(dialog,"确定").Bounds),"确认按钮越界");Render(dialog,"preview-long-notice");dialog.Close();
                        }
                    });
                    test("窄聊天气泡：长单词、路径、昵称与状态不横向越界",()=>{
                        using(Form host=new Form{ClientSize=new Size(280,420)})using(BubbleHistory history=new BubbleHistory{Dock=DockStyle.Fill}) {
                            host.Controls.Add(history);host.Show();history.Append(new ChatRecord{Text=new string('W',3000)+"\n"+String.Concat(Enumerable.Repeat("👩🏽‍💻中文",100)),Sender=new string('名',80),State=new string('状',80),Time=DateTime.Now,Outgoing=true});Application.DoEvents();
                            var bounds=(List<Rectangle>)typeof(BubbleHistory).GetField("bounds",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history);
                            Check(bounds.All(r=>r.Left>=0&&r.Right<=history.ClientSize.Width),"气泡横向越界");Check(history.AutoScrollMinSize.Height>1000,"长单词未换行");Render(host,"preview-narrow-long-bubble");host.Close();
                        }
                    });
                    test("多文件位置菜单：长文件名省略显示，完整路径不丢失",()=>{
                        using(Form host=new Form{ClientSize=new Size(500,400)})using(BubbleHistory history=new BubbleHistory{Dock=DockStyle.Fill}) {
                            host.Controls.Add(history);host.Show();string path=Path.Combine(directory,new string('w',180)+".txt");
                            history.Append(new ChatRecord{Text="两个文件",LocalFiles=new[]{path,Path.Combine(directory,"普通.txt")},Sender="测试",State="已送达",Time=DateTime.Now});
                            typeof(BubbleHistory).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(history,0);
                            history.ContextMenuStrip.Show(history,new Point(8,8));Application.DoEvents();ToolStripMenuItem folder=history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item=>item.Text=="打开文件所在目录");
                            Check(folder.DropDownItems[0].Text.Length<=49&&folder.DropDownItems[0].Text.EndsWith("…"),"长文件名菜单未限制宽度");Check(folder.DropDownItems[0].ToolTipText==path,"完整路径丢失");history.ContextMenuStrip.Close();host.Close();
                        }
                    });
                    test("转发附件：真实新邀请与文件字节一致", () => {
                        string source=Path.Combine(directory,"转发附件.txt"),destination=Path.Combine(directory,"转发验证保存.txt");File.WriteAllText(source,"真实转发文件 😊");
                        IncomingMessage incoming=null;Action<IncomingMessage> handler=message=>{if(message.Files.Any(f=>f.Name=="转发附件.txt"))incoming=message;};b.MessageReceived+=handler;
                        try {
                            using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=100}) {
                                timer.Tick+=delegate{ForwardDialog dialog=Application.OpenForms.OfType<ForwardDialog>().FirstOrDefault();if(dialog==null)return;timer.Stop();All(dialog).OfType<ListBox>().Single().SelectedIndex=0;Button(dialog,"确认转发").PerformClick();};timer.Start();
                                typeof(MainForm).GetMethod("Forward",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(mainA,new object[]{new ChatRecord{Text="附件正文\n[文件] 转发附件.txt",Outgoing=true,LocalFiles=new[]{source}}});
                            }
                            Pump(()=>incoming!=null,"未收到转发附件邀请");Check(incoming.Record.Text.StartsWith("附件正文"),"转发丢失正文");
                            Task<string> task=b.ReceiveFileAsync(incoming.Peer,incoming.Record.Packet,incoming.Files.Single(),destination,null,CancellationToken.None);Pump(()=>task.IsCompleted,"转发附件下载超时");task.GetAwaiter().GetResult();
                            Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(destination)),"转发附件内容不同");
                        }finally{b.MessageReceived-=handler;}
                    });
                    test("夜间主题：保存立即生效、聊天与设置渲染、恢复浅色", () => {
                        MenuDialog<SettingsDialog>(()=>Find<Button>(mainA,"菜单").PerformClick(),"设置",dialog=>{
                            Control accent=Find<TableLayoutPanel>(dialog,"主题颜色");((ScrollableControl)accent.Parent.Parent).ScrollControlIntoView(accent);Application.DoEvents();Render(dialog,"preview-settings-appearance");
                            Check(!All(dialog).OfType<ComboBox>().Any(),"设置仍使用默认下拉框");
                            Find<ToggleSwitch>(dialog,"夜间模式").Checked=true;Find<Button>(dialog,"紫罗兰").PerformClick();Button(dialog,"保存").PerformClick();
                        });Check(storeA.LoadSettings().DarkMode&&storeA.LoadSettings().Accent=="purple","主题保存失败");Check(mainA.BackColor==Theme.Surface&&Theme.Surface.GetBrightness()<.3,"夜间背景未生效");Render(mainA,"preview-night");
                        MenuDialog<SettingsDialog>(()=>Find<Button>(mainA,"菜单").PerformClick(),"设置",dialog=>{Render(dialog,"preview-night-settings");Find<ToggleSwitch>(dialog,"夜间模式").Checked=false;Find<Button>(dialog,"经典蓝").PerformClick();Button(dialog,"保存").PerformClick();});
                        Check(!storeA.LoadSettings().DarkMode&&Theme.Ink.GetBrightness()<.3,"浅色恢复失败");
                    });
                    test("统一视觉：三色明暗选中态、按钮与设置小窗口", () => {
                        try {
                            foreach(bool dark in new[]{false,true}) foreach(string accent in new[]{"blue","green","purple"}) {
                                Theme.Change(dark,accent);
                                Check(Theme.Sky == Theme.Blend(Theme.Blue,Theme.Surface,dark?.30F:.17F),"选中态未随强调色变化");
                                using(Button primary=Theme.Button("主操作",null,true))using(Button secondary=Theme.Button("次操作",null,false))using(Button icon=Theme.IconButton("more","菜单",null,false)) {
                                    Check(primary.BackColor==Theme.Blue&&secondary.BackColor==Theme.Ice&&icon.BackColor==Theme.Surface,"按钮视觉角色混乱");
                                    Check(((RoundedButton)secondary).Radius==Theme.ControlRadius&&((RoundedButton)icon).Radius==Theme.ControlRadius,"普通按钮圆角不一致");
                                }
                            }
                            Theme.Change(false,"blue");
                            using(SettingsDialog dialog=new SettingsDialog(new AppSettings { AutoReceiveFiles = false },storeA.Root,a.Port)) {
                                dialog.Show(mainA);dialog.Height=500;Application.DoEvents();Control accent=Find<TableLayoutPanel>(dialog,"主题颜色");((ScrollableControl)accent.Parent.Parent).ScrollControlIntoView(accent);Application.DoEvents();
                                Check(Button(dialog,"保存").Parent.ClientRectangle.Contains(Button(dialog,"保存").Bounds),"小窗口保存按钮不可见");Render(dialog,"preview-unified-small-settings");dialog.Close();
                            }
                        }finally {Theme.Change(false,"blue");}
                    });
                    test("联系人资料：改名、下线和重新打开会话同步抬头", () =>
                    {
                        Peer peer = a.Peers.Single(p => p.Login == "ui-b"); mainA.OpenChat(peer);
                        b.Settings.Nickname = "新昵称 B"; b.Refresh();
                        Pump(() => Find<Label>(chatA, "聊天联系人昵称").Text == "新昵称 B", "改名未刷新聊天抬头");
                        b.Dispose(); Pump(() => !peer.Online && Find<Label>(chatA, "聊天联系人状态").Text == "离线", "离线未刷新聊天抬头");
                        chatA.Close(); mainA.OpenChat(peer);
                        Check(Find<Label>(chatA, "聊天联系人状态").Text == "离线" && Find<Label>(chatA, "聊天联系人昵称").Text == peer.Nickname, "恢复会话显示旧资料");
                    });
                    test("菜单回归：退出释放菜单并保存未确认消息终态", () =>
                    {
                        Peer offline = a.Peers.Single(p => p.Login == "ui-b");
                        Find<RichTextBox>(chatA, "消息输入框").Text = "退出前未收到 ACK"; Find<Button>(chatA, "发送消息").PerformClick();
                        Check(storeA.History(offline.Id, 20).Last().State == "等待确认", "退出前状态不正确");
                        Find<Button>(mainA, "菜单").PerformClick(); ToolStripDropDown menu = OpenMenu(); ClickMenu(menu, "退出");
                        Check(!menu.IsDisposed, "退出操作在菜单事件派发中直接销毁了菜单");
                        Pump(() => mainA.IsDisposed, "菜单退出未结束主窗口");
                        Check(ownedMenus.Count >= 5 && ownedMenus.All(m => m.IsDisposed), "窗口退出后仍有菜单未释放");
                        Check(new LocalStore(storeA.Root).History(offline.Id, 20).Last().State == "已退出，未确认送达", "窗口正常退出未保存待确认终态");
                    });
                }
                finally { if (!mainA.IsDisposed) mainA.ExitApplication(); if (!mainB.IsDisposed) mainB.ExitApplication(); Application.DoEvents(); }
            }
        }
    }
}
