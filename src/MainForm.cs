using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
public sealed class MainForm : ModernWindow
    {
        private readonly LanService network;
        private readonly LocalStore store;
        private readonly Diagnostics diagnostics;
        private readonly Dictionary<string, ChatForm> chats = new Dictionary<string, ChatForm>();
        private readonly Dictionary<string, int> unread = new Dictionary<string, int>();
        private readonly Dictionary<string, string> previews = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> times = new Dictionary<string, DateTime>();
        private readonly List<string> recent = new List<string>();
        private readonly ConversationList contacts = new ConversationList();
        private readonly TableLayoutPanel layout;
        private readonly SidebarDivider sidebarDivider = new SidebarDivider { Dock = DockStyle.Fill, Margin = Padding.Empty };
        private const int MinimumWindowWidth = 720, MinimumWindowHeight = 520;
        private readonly TextBox search;
        private readonly Label status = Theme.Label("连接中", Theme.Small, Theme.Muted);
        private readonly ChatHost chatHost = new ChatHost { Dock = DockStyle.Fill, BackColor = Theme.Wallpaper };
        private readonly NotifyIcon tray;
        private readonly TabStrip tabs = new TabStrip { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 8, 0), Margin = Padding.Empty };
        private ContextMenuStrip mainMenu, conversationMenu;
        private ToolStripMenuItem nightItem;
        private ProfileMenuItem profileItem;
        private ToolsWindow toolsWindow;
        private TransferWindow transferWindow;
        private GuestWindow guestWindow;
        private long guestPacket=DateTime.UtcNow.Ticks;
        internal void GuestReceived(string path,string name)
        {
            var file=new FileTransferState { Id=0,Name=name,Size=new FileInfo(path).Length,Percent=100,Status="已保存",LocalPath=path };
            TrySave(new ChatRecord { Packet=++guestPacket,PeerId="browser-guests",Sender="浏览器访客",Time=DateTime.Now,Text="[文件] "+name,State="已保存",HasAttachments=true,AttachmentNames=new[]{name},LocalFiles=new[]{path},Transfers=new[]{file} });
        }
        private void OpenGuest()
        {
            try
            {
                if(guestWindow==null||guestWindow.IsDisposed)guestWindow=new GuestWindow(this,network);
                else guestWindow.ReloadAddresses();
                guestWindow.Show();
                if(guestWindow.WindowState==FormWindowState.Minimized)guestWindow.WindowState=guestWindow.RestoreMaximized?FormWindowState.Maximized:FormWindowState.Normal;
                guestWindow.Activate();
            }
            catch(Exception e){OnError("临时传输未开启："+e.Message);}
        }
        private readonly Dictionary<string, ChatRecord> transferRecords = new Dictionary<string, ChatRecord>();
        private int transferArchiveRevision;
        private static string TransferKey(ChatRecord record) { return record.PeerId + "/" + record.Packet + "/" + record.Outgoing; }
        internal void TrackTransfer(ChatRecord record) { if(record.Transfers!=null&&record.Transfers.Length>0)transferRecords[TransferKey(record)]=record; }
        internal ChatRecord[] TransferRecords { get { return transferRecords.Values.ToArray(); } }
        internal Peer TransferPeer(string id) { return network.Peers.FirstOrDefault(p=>p.Id==id); }
        internal async System.Threading.Tasks.Task LoadTransfers()
        {
            int revision=transferArchiveRevision;
            string[] peers = network.Peers.Select(p=>p.Id).Concat(new[]{"browser-guests"}).ToArray();
            var records = await System.Threading.Tasks.Task.Run(()=>peers.SelectMany(p=>store.AttachmentHistory(p)).ToArray());
            if(IsDisposed)return;
            if(revision!=transferArchiveRevision){await LoadTransfers();return;}
            foreach(ChatRecord record in records)if(!transferRecords.ContainsKey(TransferKey(record)))TrackTransfer(record.Outgoing?network.OutgoingRecord(record.PeerId,record.Packet)??record:record);
        }
        internal bool ReceivingTask(ChatRecord record,int fileId) { ChatForm chat;return chats.TryGetValue(record.PeerId,out chat)&&!chat.IsDisposed&&chat.TaskReceiving(record.Packet,fileId); }
        internal void TransferAction(ChatRecord record,int fileId,bool pause)
        {
            ChatForm chat;
            if(!chats.TryGetValue(record.PeerId,out chat)||chat.IsDisposed){Peer peer=TransferPeer(record.PeerId);if(peer==null)throw new IOException("联系人已移除，请重新添加后继续。");chat=GetOrCreateChat(peer);}
            if(pause)chat.PauseTask(record,fileId);else chat.QueueTask(record,fileId);
        }
        internal System.Threading.Tasks.Task CancelReceiveTask(ChatRecord record,int fileId)
        {
            if(record.Outgoing)return System.Threading.Tasks.Task.FromResult(false);
            ChatForm chat;
            if(!chats.TryGetValue(record.PeerId,out chat)||chat.IsDisposed){Peer peer=TransferPeer(record.PeerId);if(peer==null)throw new IOException("联系人已移除，请重新添加后处理断点。");chat=GetOrCreateChat(peer);}
            return chat.CancelReceiveTask(record,fileId);
        }
        internal System.Threading.Tasks.Task RejectTransfer(ChatRecord record)
        {
            if(record.Outgoing)return System.Threading.Tasks.Task.FromResult(false);
            ChatForm chat;
            if(!chats.TryGetValue(record.PeerId,out chat)||chat.IsDisposed){Peer peer=TransferPeer(record.PeerId);if(peer==null)throw new IOException("联系人已移除，请重新添加后处理文件邀请。");chat=GetOrCreateChat(peer);}
            return chat.RejectTransfer(record);
        }
        internal bool CanCancelTransfer(ChatRecord record){return record.Outgoing&&network.CanCancelOutgoing(record.PeerId,record.Packet);}
        internal void CancelTransfer(ChatRecord record)
        {
            using(var confirm=new ConfirmDialog("取消发送？","将撤销这批尚未接收的文件邀请；本机原文件和对方已保存的文件保留。","取消发送",true))
                if(confirm.ShowDialog(this)==DialogResult.OK)network.CancelOutgoing(record.PeerId,record.Packet,record.State,true);
        }
        internal void OpenTransfers(string sourceId = null)
        {
            if(transferWindow==null||transferWindow.IsDisposed)transferWindow=new TransferWindow(this);
            if(sourceId!=null)transferWindow.SelectSource(sourceId);
            if(transferWindow.WindowState==FormWindowState.Minimized)
                transferWindow.WindowState=transferWindow.RestoreMaximized?FormWindowState.Maximized:FormWindowState.Normal;
            transferWindow.Show();transferWindow.Activate();
        }
        internal WindowPlacement TransferWindowPlacement { get { return network.Settings.TransferWindow; } }
        internal void SaveTransferWindowPlacement(WindowPlacement placement)
        {
            network.Settings.TransferWindow = placement;
            try { store.SaveSettings(network.Settings); }
            catch (Exception error) { status.Text = "传输窗口状态保存失败：" + error.Message; }
        }
        internal WindowPlacement GuestWindowPlacement { get { return network.Settings.GuestWindow; } }
        internal void SaveGuestWindowPlacement(WindowPlacement placement)
        {
            network.Settings.GuestWindow = placement;
            try { store.SaveSettings(network.Settings); }
            catch (Exception error) { status.Text = "临时传输窗口状态保存失败：" + error.Message; }
        }
        private readonly List<ToolsGateway> gateways=new List<ToolsGateway>();
        private bool toolsRestored;
        private readonly List<RoundedButton> filters = new List<RoundedButton>();
        private bool exiting, started, automaticReceiving;
        private bool windowLoaded;
        private readonly WindowsStartup startup;
        private bool startInTray;
        private int peersRefreshQueued;
        private readonly Timer windowSaveTimer = new Timer { Interval = 500 };
        private readonly Timer shareTimer=new Timer{Interval=1000};
        private int filter;
        private string fingerprint;
        private Peer latestSender;
        public ChatForm ActiveChat { get; private set; }
        public MainForm(LanService network, LocalStore store, WindowsStartup startup = null, bool startInTray = false)
        {
            this.startup = startup; this.startInTray = startInTray;
            diagnostics=new Diagnostics(store.Root,()=>network.Settings.DiagnosticsEnabled);
            this.network = network; this.store = store; Text = "飞Q"; ClientSize = new Size(1120, 760); MinimumSize = new Size(MinimumWindowWidth, MinimumWindowHeight);
            HashSet<string> restored=new HashSet<string>(network.Peers.Select(p=>p.Id));
            foreach(SavedConversation item in SavedConversation.Normalize(network.Settings.Conversations).OrderByDescending(c=>c.MessageUtcTicks))
            {
                if(!restored.Contains(item.Id))continue;
                if(item.MessageUtcTicks>0){times[item.Id]=new DateTime(item.MessageUtcTicks,DateTimeKind.Utc).ToLocalTime();recent.Add(item.Id);}
                if(!String.IsNullOrEmpty(item.Preview))previews[item.Id]=item.Preview;
                if(item.Unread>0)unread[item.Id]=item.Unread;
            }
            windowSaveTimer.Tick += delegate { windowSaveTimer.Stop(); SaveWindowPlacement(); };
            LocationChanged += TrackWindowPlacement; SizeChanged += TrackWindowPlacement;
            layout = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 336)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Surface.Controls.Add(layout);
            TableLayoutPanel sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.Controls.Add(sidebar, 0, 0); layout.Controls.Add(sidebarDivider, 1, 0); chatHost.Margin = Padding.Empty; layout.Controls.Add(chatHost, 2, 0);
            sidebarDivider.WidthRequested += SetSidebarWidth;
            sidebarDivider.ResetRequested += delegate { SetSidebarWidth((int)Math.Round(336 * SidebarScale)); };
            layout.SizeChanged += delegate { ApplySidebarWidth(); };
            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(8, 10, 10, 8), Margin = Padding.Empty, BackColor = Theme.Surface };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Button menu = Theme.IconButton("menu", "菜单", ShowMenu, false); menu.Dock = DockStyle.Fill; menu.Margin = Padding.Empty; top.Controls.Add(menu, 0, 0);
            SearchBox query = new SearchBox { Dock = DockStyle.Fill, Margin = new Padding(4, 2, 6, 2) }; search = query.Input; search.AccessibleName = "查找联系人"; search.MaxLength = 100; query.Placeholder = "搜索联系人或消息"; search.TextChanged += delegate { RefreshPeers(); };
            top.Controls.Add(query, 1, 0);
            sidebar.SizeChanged += delegate {
                bool compact = sidebar.Width < 280 * SidebarScale;
                top.Padding = compact ? new Padding(4, 10, 4, 8) : new Padding(8, 10, 10, 8);
                top.ColumnStyles[0].Width = compact ? 36 : 44; top.ColumnStyles[2].Width = compact ? 36 : 42;
                query.Placeholder = compact ? "搜索" : "搜索联系人或消息"; query.Invalidate();
                tabs.Padding = compact ? new Padding(4, 0, 4, 0) : new Padding(14, 0, 8, 0);
            };
            Button add = Theme.IconButton("plus", "添加联系人", delegate { AddPeer(); }, false); add.Dock = DockStyle.Fill; add.Margin = Padding.Empty; top.Controls.Add(add, 2, 0); sidebar.Controls.Add(top, 0, 0);
            string[] names = { "全部", "未读", "在线" };
            for (int i = 0; i < names.Length; i++) { int value = i; RoundedButton button = (RoundedButton)Theme.Button(names[i], delegate { filter = value; RefreshFilters(); RefreshPeers(); }, false); button.Size = new Size(88, 44); button.Margin = Padding.Empty; filters.Add(button); tabs.Add(button); }
            sidebar.Controls.Add(tabs, 0, 1); contacts.Dock = DockStyle.Fill; contacts.Margin = Padding.Empty; contacts.PeerActivated += OpenChat; contacts.PeerMenuRequested += ShowConversationMenu; sidebar.Controls.Add(contacts, 0, 2);
            Panel footer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 1, 10, 0), Margin = Padding.Empty, BackColor = Theme.Surface };
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 0, 0, footer.Width, 0);
            };
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Padding = Padding.Empty; footer.Controls.Add(status);
            Panel connectionDot = new Panel { Dock = DockStyle.Left, Width = 16, AccessibleName = "连接状态指示" };
            connectionDot.Paint += delegate(object sender, PaintEventArgs e) {
                float scale = e.Graphics.DpiX / 96F; float diameter = 6 * scale;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using(SolidBrush dot = new SolidBrush(started ? Theme.Online : Theme.Muted)) e.Graphics.FillEllipse(dot, 0, (connectionDot.Height - diameter) / 2, diameter, diameter);
            }; footer.Controls.Add(connectionDot);
            status.TextChanged += delegate { connectionDot.Invalidate(); };
            RoundedButton refresh = (RoundedButton)Theme.IconButton("refresh", "刷新", delegate { RefreshNetwork(); }, false); refresh.PlainHover = true; refresh.Dock = DockStyle.Right; refresh.Size = new Size(40, 40); refresh.Margin = Padding.Empty; footer.Controls.Add(refresh);
            RoundedButton settings = (RoundedButton)Theme.IconButton("settings", "设置", delegate { OpenSettings(); }, false); settings.PlainHover = true; settings.Dock = DockStyle.Right; settings.Size = new Size(40, 40); settings.Margin = new Padding(0, 0, 4, 0); footer.Controls.Add(settings);
            sidebar.Controls.Add(footer, 0, 3);
            chatHost.Hint = "选择联系人开始聊天";
            tray = new NotifyIcon { Icon = Theme.TrayIcon, Text = "飞Q", Visible = true };
            ContextMenuStrip trayMenu = Theme.Menu(120); trayMenu.Items.Add("打开", null, delegate { Restore(); }); trayMenu.Items.Add("设置", null, delegate { OpenSettings(); }); trayMenu.Items.Add("退出", null, delegate { Ui(ExitApplication); }); tray.ContextMenuStrip = trayMenu;
            // NotifyIcon 在 Opening 之前按菜单当前尺寸计算弹出位置，先排好版，首次右键也从鼠标处向上展开。
            Theme.StyleMenu(trayMenu, 120);
            tray.DoubleClick += delegate { Restore(); }; tray.BalloonTipClicked += delegate { Restore(); if (latestSender != null) OpenChat(latestSender); };
            network.PeersChanged += OnPeersChanged; network.MessageReceived += OnMessage; network.OfferCanceled += OnOfferCanceled; network.DeliveryChanged += OnDelivery; network.Error += OnError; network.TransferChanged += OnTransfer;
            network.ConnectionInterrupted += OnConnectionInterrupted;
            RefreshFilters();
            ApplyWallpaper();
            shareTimer.Tick+=delegate {if(!started||!network.Peers.Any(p=>p.Online))return;try{string[] files=WindowsShare.Take(store.Root);if(files==null)return;Show();WindowState=FormWindowState.Normal;Activate();using(ForwardDialog dialog=new ForwardDialog(network.Peers.Where(p=>p.Online).ToArray(),files.Length))if(dialog.ShowDialog(this)==DialogResult.OK){OpenChat(dialog.Target);ActiveChat.QueueSharedFiles(files);}}catch(Exception e){OnError("系统分享未完成："+e.Message);}};shareTimer.Start();
            Theme.Changed += ApplyWallpaper;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        }
        private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) { Ui(ApplySystemTheme); }
        private void ApplySystemTheme() {
            if(!network.Settings.FollowSystemTheme || Theme.Dark == Theme.SystemDark) return;
            Theme.Change(Theme.SystemDark, network.Settings.Accent); foreach(ChatForm chat in chats.Values) chat.ApplyAppearance(); RefreshFilters();
        }
        // 主界面未打开会话时的背景与聊天页保持一致，切换主题或背景设置后立即刷新。
        private void ApplyWallpaper()
        {
            string pattern = network.Settings.ChatBackground ?? "orbits";
            chatHost.Pattern = pattern == "image" || pattern == "none" ? "orbits" : pattern;
            chatHost.Invalidate();
        }
        internal void Ui(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) action(); }); } catch (InvalidOperationException) { }
        }
        public void NetworkStarted()
        {
            if(!started)diagnostics.Record(DiagnosticEvent.Started); started = true; System.Threading.Interlocked.Exchange(ref peersRefreshQueued,0); RefreshPeers(); if (store.Warning != null) status.Text = store.Warning;
            if (!toolsRestored)
            {
                toolsRestored = true;
                try
                {
                    ToolsPreferences.State saved = new ToolsPreferences(store.Root).Value;
                    if (saved.ShareEnabled || saved.RemoteEnabled) { toolsWindow = new ToolsWindow(network.Port, network.LoopbackOnly, store.Root,network.Security); toolsWindow.PrepareRestoredWindow(); }
                }
                catch (Exception e) { status.Text = "共享与远控未恢复：" + e.Message; }
            }
        }
        internal void PrepareNetworkCallbacks() { if(!IsHandleCreated)CreateHandle();network.NetworkReset+=OnNetworkReset; }
        private void OnNetworkReset(){Ui(()=>{foreach(ToolsGateway gateway in gateways)gateway.Dispose();gateways.Clear();if(guestWindow!=null&&!guestWindow.IsDisposed)guestWindow.Stop();if(toolsWindow!=null&&!toolsWindow.IsDisposed)toolsWindow.NetworkReset();});}
        private bool RememberPeerAddresses()
        {
            List<Peer> peers=network.Peers;Peer.UpdateConversationNames(peers);
            string[] previous = network.Settings.KnownPeers ?? new string[0];
            string[] next = AppSettings.NormalizeKnownPeers(peers.Select(p => p.Endpoint.ToString()).Concat(previous));
            SavedConversation[] old=SavedConversation.Normalize(network.Settings.Conversations);
            SavedConversation[] current=SavedConversation.Normalize(peers.Select(p=>new SavedConversation {
                Endpoint=p.Endpoint.ToString(),Login=p.Login,Host=p.Host,Nickname=p.Nickname,Group=p.Group,Note=p.Note,Pinned=p.Pinned,Hidden=p.Hidden,ConversationNumber=p.ConversationNumber,
                DeviceId=p.DeviceId,Avatar=p.Avatar,Fingerprint=p.Fingerprint,ConversationId=p.Id,
                Preview=previews.ContainsKey(p.Id)?previews[p.Id]:"",Unread=unread.ContainsKey(p.Id)?unread[p.Id]:0,
                MessageUtcTicks=times.ContainsKey(p.Id)&&times[p.Id]!=default(DateTime)?times[p.Id].ToUniversalTime().Ticks:0 }));
            if (new HashSet<string>(previous).SetEquals(next)&&old.Length==current.Length&&old.Zip(current,(a,b)=>a.SameAs(b)).All(equal=>equal)) return true;
            network.Settings.KnownPeers = next;network.Settings.Conversations=current;
            try { store.SaveSettings(network.Settings);return true; }
            catch (Exception e) { network.Settings.KnownPeers = previous;network.Settings.Conversations=old; status.Text = "会话资料保存失败：" + e.Message;return false; }
        }
        private void OnPeersChanged()
        {
            if(!IsHandleCreated||IsDisposed||Disposing||System.Threading.Interlocked.Exchange(ref peersRefreshQueued,1)!=0)return;
            // 广播应答可能成批到达，只排一个刷新，避免联系人快照与配置写入占满 UI 队列。
            Ui(() => { System.Threading.Interlocked.Exchange(ref peersRefreshQueued,0); RefreshPeers(); foreach (ChatForm chat in chats.Values) if (!chat.IsDisposed) chat.RefreshPeer(); });
        }
        private void OnError(string text) { diagnostics.Record(DiagnosticEvent.NetworkError); Ui(() => status.Text = text); }
        private void OnConnectionInterrupted() { diagnostics.Record(DiagnosticEvent.ConnectionInterrupted); }
        internal bool ConfirmSecurity(string name, string code)
        {
            if(IsDisposed||Disposing||!IsHandleCreated)return false;
            Func<bool> confirm=()=>{
                if(IsDisposed||Disposing)return false;
                using(ConfirmDialog dialog=new ConfirmDialog("核对设备安全身份", "正在与「"+name+"」建立加密连接。\n请核对两台设备的配对码：\n\n"+code+"\n\n只有两端显示一致才点击信任。此设备的信任会保存，可在联系人菜单撤销。", "信任并继续", false))
                using(Timer timeout=new Timer{Interval=30000}) {
                    timeout.Tick+=delegate {dialog.DialogResult=DialogResult.Cancel;};timeout.Start();return dialog.ShowDialog(this)==DialogResult.OK;
                }
            };
            try {return InvokeRequired?(bool)Invoke(confirm):confirm();}catch(InvalidOperationException){return false;}
        }
        private void Remember(Peer peer, ChatRecord record) { recent.Remove(peer.Id); recent.Insert(0, peer.Id); if (recent.Count > 1000) recent.RemoveAt(1000); previews[peer.Id] = (record.Outgoing ? "你：" : "") + record.Text; times[peer.Id] = record.Time; }
        private void OnMessage(IncomingMessage message)
        {
            Ui(() =>
            {
                message.Record.Transfers = message.Files.Select(f => new FileTransferState { Id = f.Id, Name = f.Name, Size = f.Size, Modified=f.Modified,Sha256=f.Sha256,RelativePath=f.RelativePath,Status = "待接收 · 点击接收" }).ToArray();
                if (message.Files.Count > 0) { message.Record.HasAttachments = true; message.Record.AttachmentNames = message.Files.Select(f => f.Name).ToArray(); message.Record.Text += (String.IsNullOrEmpty(message.Record.Text) ? "" : "\n") + "[文件] " + String.Join("、", message.Files.Select(f => f.Name)); }
                // ACK 已由网络层返回；重复邀请不能回退本机状态、增加未读或再次触发自动接收。
                try { if (!store.AppendIncoming(message.Record)) return; }
                catch (Exception e) { status.Text = "记录保存失败：" + e.Message; }
                TrackTransfer(message.Record);message.Peer.Hidden=false; Remember(message.Peer, message.Record);
                ChatForm chat; chats.TryGetValue(message.Peer.Id, out chat); bool visible = chat != null && chat == ActiveChat && ContainsFocus;
                if (!visible) { if (!unread.ContainsKey(message.Peer.Id)) unread[message.Peer.Id] = 0; unread[message.Peer.Id]++; }
                bool loaded = false;
                if ((chat == null || chat.IsDisposed) && message.Files.Count > 0) { chat = GetOrCreateChat(message.Peer); loaded = true; }
                if (chat != null && !chat.IsDisposed)
                {
                    if (!loaded) chat.Append(message.Record);
                    if (chats.Values.Sum(c => c.PendingFileCount) + (loaded?0:message.Files.Count) <= 1000) chat.AddAttachments(message);
                    else status.Text = "待接收文件已达 1000 个，请处理后从历史记录继续。";
                }
                latestSender = message.Peer;
                RefreshPeers();
                if (!visible && network.Settings.Notifications) { tray.BalloonTipTitle = message.Peer.DisplayName; tray.BalloonTipText = message.Files.Count > 0 ? "收到文件" : "收到新消息"; tray.ShowBalloonTip(3000); }
            });
        }
        private void OnDelivery(ChatRecord record) { diagnostics.Record(DiagnosticEvent.DeliveryChanged); Ui(() => { TrySave(record); ChatForm chat; if (chats.TryGetValue(record.PeerId, out chat) && !chat.IsDisposed) chat.Delivery(record); }); }
        private void OnOfferCanceled(Peer peer, long packet)
        {
            Ui(() => {
                try
                {
                    ChatForm chat;
                    if (chats.TryGetValue(peer.Id, out chat) && !chat.IsDisposed) chat.OfferCanceled(packet);
                    else foreach (ChatRecord record in store.IncomingAttachments(peer.Id, packet)) CancelIncomingOffer(peer, record, true);
                }
                catch (Exception e) { FileReceiveStatus("对方已取消发送；本地处理未完成：" + e.Message); }
            });
        }
        internal void DeleteIncomingPartials(Peer peer, ChatRecord record)
        {
            if (record.Outgoing || record.PeerId != peer.Id) return;
            foreach (FileTransferState state in record.Transfers ?? new FileTransferState[0])
            {
                if (!String.IsNullOrEmpty(state.LocalPath)||state.Status=="已保存"||state.Status=="本机文件已删除") continue;
                Attachment file = new Attachment { Id = state.Id, Name = state.Name, Size = state.Size, Modified = state.Modified, Sha256 = state.Sha256, RelativePath = state.RelativePath };
                string destination = SendBatch.DestinationPath(network.Settings.ReceiveFolder, file);
                ReceiveStorage.DeletePartialAndEmptyParents(network.Settings.ReceiveFolder, LanService.PartialPath(peer, record.Packet, file, destination), file.RelativePath);
            }
        }
        internal void CancelIncomingOffer(Peer peer, ChatRecord record, bool cleanup)
        {
            FileTransferState[] transfers = record.Transfers ?? new FileTransferState[0];
            // 重新接收「本机文件已删除」的附件时邀请失效，只恢复删除态，不把已接收历史改写成对方取消。
            bool expiredOnly = transfers.Any(f => String.IsNullOrEmpty(f.LocalPath) && f.SavedBefore) && !transfers.Any(f => String.IsNullOrEmpty(f.LocalPath) && !f.SavedBefore && f.Status != "本机文件已删除" && f.Status != "已拒绝");
            FileReceiveStatus(expiredOnly ? "原邀请已失效，请让对方重新发送。" : "对方已取消发送；未完成附件不再接收。");
            if (cleanup)
                try { DeleteIncomingPartials(peer, record); }
                catch (Exception e) { FileReceiveStatus("对方已取消发送；断点清理未完成：" + e.Message); }
            bool changed = false, restored = false;
            foreach (FileTransferState file in transfers)
            {
                if (!String.IsNullOrEmpty(file.LocalPath) || file.Status == "本机文件已删除" || file.Status == "已拒绝") continue;
                if (file.SavedBefore) { file.Percent = 0; file.Status = "本机文件已删除"; file.BytesPerSecond = 0; file.Error = null; file.MarkCurrent(); restored = true; continue; }
                file.Percent = 0; file.Status = "对方已取消发送"; file.BytesPerSecond = 0; changed = true;
            }
            if (changed) { record.State = "对方已取消发送"; TrySave(record); }
            else if (restored) TrySave(record);
        }
        private void OnTransfer(ChatRecord record, int id, int percent, string status, double bytesPerSecond)
        {
            Ui(() => {
                FileTransferState file = record.Transfers == null ? null : record.Transfers.FirstOrDefault(f => f.Id == id);
                if (file == null) return; file.Percent = percent; file.Status = status; file.BytesPerSecond = bytesPerSecond;
                file.MarkCurrent();
                TrackTransfer(record);
                if (status != "发送中"||percent%10==0) TrySave(record);
                ChatForm chat; if (chats.TryGetValue(record.PeerId, out chat) && !chat.IsDisposed) chat.Delivery(record);
            });
        }
        internal void TrySave(ChatRecord record) { TrackTransfer(record);try { store.Append(record); } catch (Exception e) { status.Text = "记录保存失败：" + e.Message; } }
        internal void SaveDraft(string peerId, string text) { try { store.SaveDraft(peerId, text); } catch (Exception e) { status.Text = "草稿保存失败：" + e.Message; } }
        internal void Sent(Peer peer, ChatRecord record) { TrySave(record); Remember(peer, record); RefreshPeers(); }
        internal bool DeleteMessages(Peer peer, ChatRecord[] records)
        {
            ChatForm chat; chats.TryGetValue(peer.Id, out chat);
            if ((chat != null && chat.HasPendingMessages(records)) || (chat==null&&records.Any(r=>(r.Outgoing&&network.HasActiveOutgoingTransfer(peer.Id,r.Packet))||(r.Transfers??new FileTransferState[0]).Any(f=>((f.DisplayStatus()??"").Contains("中")&&!((f.DisplayStatus()??"").Contains("中断"))&&f.Percent<100))))) {
                using(NoticeDialog notice = new NoticeDialog("请先处理文件", "选中的消息含有待接收或传输中的文件，请完成、拒绝或取消发送后再删除。")) notice.ShowDialog(this);
                return false;
            }
            string title = records.Length == 1 ? "删除这条消息？" : "删除选中的 " + records.Length + " 条消息？";
            string detail=records.Any(r=>r.Outgoing&&network.HasUndeliveredOutgoing(peer.Id,r.Packet,r.State))?"将停止发送尚未送达的消息并撤销对应文件邀请；仅删除这台电脑上的消息记录，对方已收到的记录和本机附件文件会保留。":"仅删除这台电脑上的消息记录，对方的记录和已保存的附件文件会保留。";
            using(ConfirmDialog confirm = new ConfirmDialog(title, detail, "删除", true))
                if(confirm.ShowDialog(this) != DialogResult.OK) return false;
            try {
                if((chat!=null&&chat.HasPendingMessages(records))||(chat==null&&records.Any(r=>(r.Outgoing&&network.HasActiveOutgoingTransfer(peer.Id,r.Packet))||(r.Transfers??new FileTransferState[0]).Any(f=>((f.DisplayStatus()??"").Contains("中")&&!((f.DisplayStatus()??"").Contains("中断"))&&f.Percent<100))))) {
                    using(NoticeDialog notice=new NoticeDialog("删除未完成","传输已开始，请等待、拒绝或取消发送后再删除。"))notice.ShowDialog(this);return false;
                }
                foreach(ChatRecord record in records)if(record.Outgoing)network.CancelUndeliveredOutgoing(peer.Id,record.Packet,record.State,false);
                HashSet<long> incomingPackets = new HashSet<long>(records.Where(record => !record.Outgoing).Select(record => record.Packet));
                if (incomingPackets.Count > 0)
                    foreach (ChatRecord record in store.IncomingAttachments(peer.Id).Where(record => incomingPackets.Contains(record.Packet))) DeleteIncomingPartials(peer, record);
                // 持久化成功后再更新界面，写入失败时仍保留原选区和消息。
                store.DeleteRecords(peer.Id, records);
                foreach(ChatRecord record in records)transferRecords.Remove(TransferKey(record));
                transferArchiveRevision++;
                if(chat != null) chat.RemoveMessages(records);
                ChatRecord last = store.History(peer.Id, 1).LastOrDefault();
                if(last == null) { previews.Remove(peer.Id); times.Remove(peer.Id); recent.Remove(peer.Id); }
                else { previews[peer.Id] = (last.Outgoing ? "你：" : "") + last.Text; times[peer.Id] = last.Time; }
                fingerprint = null; RefreshPeers(); return true;
            }
            catch(Exception error) { using(NoticeDialog notice = new NoticeDialog("删除未完成", error.Message)) notice.ShowDialog(this); return false; }
        }
        internal void DeleteReceivedFile(Peer peer,ChatRecord record,string path)
        {
            if(record.PeerId!=peer.Id||record.Outgoing)return;
            DeleteTransferFile(record,path);
        }
        internal void DeleteTransferFile(ChatRecord record,string path)
        {
            if(record.Outgoing)return;
            ChatRecord current;if(transferRecords.TryGetValue(TransferKey(record),out current))record=current;
            ChatForm chat;chats.TryGetValue(record.PeerId,out chat);
            if((record.Transfers??new FileTransferState[0]).Any(f=>String.Equals(f.LocalPath,path,StringComparison.OrdinalIgnoreCase)&&ReceivingAttachment(record,f))){using(NoticeDialog notice=new NoticeDialog("请先处理文件","此文件仍在接收，请等待完成或取消后再删除。"))notice.ShowDialog(this);return;}
            using(ConfirmDialog confirm=new ConfirmDialog("删除这个文件？",path+"\n\n仅删除这个本机文件，聊天记录与对方文件保留。此操作无法撤销。","删除文件",true))if(confirm.ShowDialog(this)!=DialogResult.OK)return;
            if((record.Transfers??new FileTransferState[0]).Any(f=>String.Equals(f.LocalPath,path,StringComparison.OrdinalIgnoreCase)&&ReceivingAttachment(record,f))){using(var notice=new NoticeDialog("请先处理文件","此文件仍在接收，请等待完成或取消后再删除。"))notice.ShowDialog(this);return;}
            try{store.DeleteReceivedFile(record,path);TrackTransfer(record);if(chat!=null&&!chat.IsDisposed)chat.Delivery(record);FileReceiveStatus("已删除本机文件："+Path.GetFileName(path));}
            catch(Exception error){using(NoticeDialog notice=new NoticeDialog("文件删除未完成",error.Message))notice.ShowDialog(this);}
        }
        private bool ReceivingAttachment(ChatRecord record,FileTransferState file)
        {
            string state=file.DisplayStatus()??"";
            return ReceivingTask(record,file.Id)||(state.Contains("中")&&!state.Contains("中断"));
        }
        internal void DeleteReceivedFiles(Peer peer,ChatRecord[] records)
        {
            ChatRecord[] incoming=records.Where(record=>record.PeerId==peer.Id&&record.HasReceivedAttachments).Select(record=>{ChatRecord current;return transferRecords.TryGetValue(TransferKey(record),out current)?current:record;}).ToArray();
            if(incoming.Length==0)return;
            try
            {
                ReceivedFilesDeletionResult planned=store.DeleteReceivedFiles(incoming,false,ReceivingAttachment);
                if(planned.DeletedCount==0&&planned.SkippedDeletedCount==0&&planned.FailedCount==0)
                {
                    string detail=planned.SkippedBusyCount>0?planned.Text:"所选消息中的附件尚未接收保存；如需处理待接收文件，请前往「传输任务」接收或拒绝。";
                    using(var notice=new NoticeDialog("没有可删除的本机文件",detail))notice.ShowDialog(this);
                    return;
                }
                using(var confirm=new ConfirmDialog("删除选中的本机文件？","将删除所选消息中已保存的本机接收文件；聊天记录保留，正在接收或处理的文件及已删除文件自动跳过。此操作无法撤销。","删除文件",true))
                    if(confirm.ShowDialog(this)!=DialogResult.OK)return;
                ReceivedFilesDeletionResult result;
                ChatForm chat;chats.TryGetValue(peer.Id,out chat);
                // 确认弹窗期间可能启动新接收，实际删除时按最新状态逐个跳过。
                try{result=store.DeleteReceivedFiles(incoming,true,ReceivingAttachment);}
                finally
                {
                    // 中途失败时已删除的文件也已落盘，聊天气泡与传输任务必须同步刷新。
                    if(chat!=null&&!chat.IsDisposed)foreach(ChatRecord record in incoming)chat.Delivery(record);
                    foreach(ChatRecord record in incoming)TrackTransfer(record);
                }
                // 有失败项时保留多选，便于解除占用后直接重试。
                if(result.FailedCount==0&&result.SkippedBusyCount==0&&chat!=null&&!chat.IsDisposed)chat.EndSelection();
                FileReceiveStatus(result.Text);
                using(var notice=new NoticeDialog(result.FailedCount==0?"文件删除完成":"部分文件未删除",result.Text))notice.ShowDialog(this);
            }
            catch(Exception error){using(var notice=new NoticeDialog("文件删除未完成",error.Message))notice.ShowDialog(this);}
        }
        internal async System.Threading.Tasks.Task CleanReceivedFiles(IWin32Window owner)
        {
            try
            {
                ChatRecord[] records = await System.Threading.Tasks.Task.Run(() => store.ReceivedFileRecords());
                if (IsDisposed || owner is Control && ((Control)owner).IsDisposed) return;
                for (int i = 0; i < records.Length; i++) {
                    ChatRecord current;
                    if (transferRecords.TryGetValue(TransferKey(records[i]), out current)) records[i] = current;
                }
                ReceivedFilesDeletionResult planned = store.DeleteReceivedFiles(records, false, ReceivingAttachment, true);
                if (planned.DeletedCount == 0 && planned.FailedCount == 0) {
                    using (var notice = new NoticeDialog("没有可清理的已接收文件", planned.SkippedBusyCount > 0 ? planned.Text : "已登记的接收文件均已移走或删除；其他文件未处理。")) notice.ShowDialog(owner);
                    return;
                }
                string detail = "将清理 " + planned.DeletedCount + " 个已登记的接收文件，包含当前及以前保存目录中的文件。\n聊天与传输记录保留，正在接收的文件自动跳过；修改过的文件与其他文件保留。此操作无法撤销。";
                if (records.Any(r => r.PeerId == "browser-guests" && r.GetLocalFiles().Length > 0)) detail += "\n来自临时浏览器传输的文件清理后无法重新接收。";
                using (var confirm = new ConfirmDialog("清理已接收文件？", detail, "清理文件", true))
                    if (confirm.ShowDialog(owner) != DialogResult.OK) return;
                ReceivedFilesDeletionResult result;
                try { result = store.DeleteReceivedFiles(records, true, ReceivingAttachment, true); }
                finally {
                    foreach (ChatRecord record in records.Where(r => !r.Deleted)) {
                        TrackTransfer(record); ChatForm chat;
                        if (chats.TryGetValue(record.PeerId, out chat) && !chat.IsDisposed) chat.Delivery(record);
                    }
                    transferArchiveRevision++;
                }
                FileReceiveStatus(result.Text);
                using (var notice = new NoticeDialog(result.FailedCount == 0 ? "接收文件清理完成" : "部分文件已保留", result.Text)) notice.ShowDialog(owner);
            }
            catch (Exception error) {
                if (!IsDisposed) using (var notice = new NoticeDialog("接收文件清理未完成", error.Message)) notice.ShowDialog(owner);
            }
        }
        internal void Forward(ChatRecord original)
        {
            try
            {
                string[] paths = original.GetLocalFiles();
                string text = original.ForwardText();
                using (ForwardDialog dialog = new ForwardDialog(network.Peers.Where(p => p.Online).ToArray(), paths.Length))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    ChatForm target = GetOrCreateChat(dialog.Target);
                    target.ForwardContent(text, paths); OpenChat(dialog.Target);
                }
            }
            catch (Exception e) { using (NoticeDialog dialog = new NoticeDialog("转发失败", e.Message)) dialog.ShowDialog(this); }
        }
        private ChatForm GetOrCreateChat(Peer peer)
        {
            ChatForm chat;
            if (!chats.TryGetValue(peer.Id, out chat) || chat.IsDisposed)
            {
                chat = new ChatForm(peer, network, store, this) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };
                chats[peer.Id] = chat; chatHost.Controls.Add(chat);
                chat.Enter += delegate { unread.Remove(peer.Id); RefreshPeers(); };
            }
            return chat;
        }
        public void OpenChat(Peer peer)
        {
            peer.Hidden=false; ChatForm chat = GetOrCreateChat(peer);
            if (ActiveChat != null && ActiveChat != chat) { ActiveChat.SaveDraft(); ActiveChat.Hide(); } ActiveChat = chat;
            chat.RefreshPeer(); chat.Show(); chat.BringToFront(); chat.FocusEditor(); unread.Remove(peer.Id); contacts.SelectedId = peer.Id; chatHost.Invalidate(); RefreshPeers(); contacts.Invalidate();
        }
        internal void FileReceiveStatus(string text) { diagnostics.Record(DiagnosticEvent.FileStatus); if (!IsDisposed) status.Text = text; }
        internal async void ReceiveAutomatically()
        {
            if (automaticReceiving || exiting || !network.Settings.AutoReceiveFiles && !chats.Values.Any(c=>c.HasQueuedFile)) return;
            automaticReceiving = true;
            try
            {
                // 全局按序接收，未打开的会话也参与；不为每个邀请启动一个并发下载。
                while (!exiting)
                {
                    bool automatic=network.Settings.AutoReceiveFiles;
                    ChatForm next = chats.Values.FirstOrDefault(c => !c.IsDisposed && (automatic?c.HasAutomaticFile:c.HasQueuedFile));
                    if (next == null) break;
                    if(automatic)await next.ReceiveNextAutomaticAsync();else await next.ReceiveNextQueuedAsync();
                }
            }
            catch (Exception e) { FileReceiveStatus("自动接收失败：" + e.GetBaseException().Message); }
            finally { automaticReceiving = false; }
        }
        internal void CloseChat(ChatForm chat) { chat.SaveDraft(); chat.Hide(); if (ActiveChat == chat) { ActiveChat = null; contacts.SelectedId = null; chatHost.Invalidate(); contacts.Invalidate(); } }
        private void RefreshFilters() { tabs.Active = filter; for (int i = 0; i < filters.Count; i++) { filters[i].Active = i == filter; filters[i].Invalidate(); } }
        private void RefreshPeers()
        {
            bool persisted=RememberPeerAddresses();
            List<Peer> all = network.Peers.Where(p=>!p.Hidden).ToList(); string query = search.Text.Trim();
            string next = filter + "|" + query + "|" + started + "|" + String.Join("|", all.Select(p => p.Id + p.DisplayName + p.Pinned + p.Online + p.Group + p.Endpoint + p.Host + p.Avatar)) + "|" + String.Join("|", recent) + "|" + String.Join("|", unread.Select(p => p.Key + p.Value)) + "|" + String.Join("|", previews.Values);
            if (next == fingerprint) return; fingerprint = next;
            var rows = all.Where(p => filter != 2 || p.Online).Where(p => filter != 1 || (unread.ContainsKey(p.Id) && unread[p.Id] > 0))
                .Where(p => (p.DisplayName + " " + p.Nickname + " " + p.Group + " " + p.Host + " " + p.Endpoint + " " + (previews.ContainsKey(p.Id) ? previews[p.Id] : "")).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                // 手动置顶优先，其余在线优先；同组保留最近消息顺序，不把上线当成新消息。
                .OrderBy(p=>p.Pinned?0:p.Online?1:2).ThenBy(p => recent.Contains(p.Id) ? recent.IndexOf(p.Id) : Int32.MaxValue).ThenBy(p => p.DisplayName,StringComparer.OrdinalIgnoreCase).ThenBy(p=>p.Id,StringComparer.Ordinal)
                .Select(p => new ConversationList.Item { Peer = p, Preview = previews.ContainsKey(p.Id) ? previews[p.Id] : (p.Online ? "在线" : "离线"), Unread = unread.ContainsKey(p.Id) ? unread[p.Id] : 0, Time = times.ContainsKey(p.Id) ? times[p.Id] : default(DateTime) });
            bool searching = query.Length > 0;
            contacts.EmptyText = searching ? "没有找到联系人" : filter == 1 ? "没有未读消息" : filter == 2 ? "当前没有在线联系人" : "暂无会话";
            contacts.EmptyHint = searching ? "换个昵称、备注或消息关键词试试" : "同一局域网内启动飞Q的设备会自动出现";
            contacts.SetItems(rows); foreach(ChatForm chat in chats.Values)if(!chat.IsDisposed)chat.RefreshPeer();
            if (started&&persisted) status.Text = all.Count(p => p.Online) + " 人在线 · " + rows.Count() + " 个会话";
        }
        internal void ShowConversationMenu(Peer peer)
        {
            ShowConversationMenu(peer, false);
        }
        internal void ShowConversationMenu(Peer peer, bool managementOnly)
        {
            if(conversationMenu==null)conversationMenu=Theme.Menu();
            foreach(ToolStripItem item in conversationMenu.Items.Cast<ToolStripItem>().ToArray())item.Dispose();
            conversationMenu.Items.Clear();
            if (!managementOnly) {
                conversationMenu.Items.Add("浏览共享文件…",null,delegate { OpenShared(peer); });
                conversationMenu.Items.Add("控制 Android 手机…",null,delegate { OpenPhone(peer); });
                if(network.Security!=null&&peer.Fingerprint!=null)conversationMenu.Items.Add("撤销设备信任…",null,delegate {using(ConfirmDialog confirm=new ConfirmDialog("撤销设备信任？","下次连接将重新核对配对码，并撤销该设备未完成的文件邀请。","撤销",true))if(confirm.ShowDialog(this)==DialogResult.OK){try{network.ForgetSecurity(peer);if(toolsWindow==null||toolsWindow.IsDisposed)new ToolsPairings(store.Root).ForgetFingerprint(peer.Fingerprint);OnNetworkReset();}catch(Exception error){OnError(error.Message);}}});
                conversationMenu.Items.Add(new ToolStripSeparator());
            }
            conversationMenu.Items.Add("设置备注…",null,delegate { EditConversation(peer); });
            conversationMenu.Items.Add(peer.Pinned?"取消置顶":"置顶会话",null,delegate {
                bool old=peer.Pinned;peer.Pinned=!old;if(!RememberPeerAddresses())peer.Pinned=old;fingerprint=null;RefreshPeers();
            });
            conversationMenu.Items.Add("清空聊天记录…",null,delegate { DeleteConversation(peer,true); });
            conversationMenu.Items.Add("删除联系人…",null,delegate { DeleteConversation(peer,false); });
            conversationMenu.Show(Cursor.Position);
        }
        private void EditConversation(Peer peer)
        {
            using(ConversationDialog dialog=new ConversationDialog(peer,false))if(dialog.ShowDialog(this)==DialogResult.OK) SaveConversationNote(peer, dialog.Note);
        }
        internal void ShowContactProfile(Peer peer) { using(ContactProfileDialog dialog = new ContactProfileDialog(peer, note => SaveConversationNote(peer, note))) dialog.ShowDialog(this); }
        private bool SaveConversationNote(Peer peer, string note)
        {
            string old = peer.Note; peer.Note = note; bool saved = RememberPeerAddresses(); if (!saved) peer.Note = old;
            foreach(ChatForm chat in chats.Values)chat.RefreshPeer();fingerprint=null;RefreshPeers();
            return saved;
        }
        private void DeleteConversation(Peer peer,bool clearOnly)
        {
            using(ConversationDialog dialog=new ConversationDialog(peer,true,clearOnly))if(dialog.ShowDialog(this)==DialogResult.OK){
                ChatForm chat;chats.TryGetValue(peer.Id,out chat);
                if(dialog.DeleteHistory&&((chat!=null&&chat.PendingFileCount>0)||network.HasActiveOutgoingTransfer(peer.Id))){using(NoticeDialog notice=new NoticeDialog("请先处理文件","此会话还有待接收或传输中的文件。请完成、拒绝或取消发送后再清空记录。"))notice.ShowDialog(this);return;}
                try {
                    if(dialog.DeleteHistory){network.CancelUndeliveredOutgoingForPeer(peer.Id);foreach(ChatRecord record in store.IncomingAttachments(peer.Id))DeleteIncomingPartials(peer,record);store.ClearHistory(peer.Id);foreach(string key in transferRecords.Where(item=>item.Value.PeerId==peer.Id).Select(item=>item.Key).ToArray())transferRecords.Remove(key);transferArchiveRevision++;if(chat!=null)chat.ClearHistory();previews.Remove(peer.Id);times.Remove(peer.Id);recent.Remove(peer.Id);}
                    bool hidden=peer.Hidden;peer.Hidden=!clearOnly;unread.Remove(peer.Id);
                    if(!RememberPeerAddresses()){peer.Hidden=hidden;fingerprint=null;RefreshPeers();return;}
                    if(!clearOnly&&chat!=null)CloseChat(chat);
                    fingerprint=null;RefreshPeers();
                }catch(Exception e){using(NoticeDialog notice=new NoticeDialog("操作未完成",e.Message))notice.ShowDialog(this);}
            }
        }
        private void RefreshNetwork() { if (started) try { network.Revalidate(); } catch (Exception e) { status.Text = e.Message; } }
        private void RefreshNetworkAsync()
        {
            if (!started) return;
            System.Threading.Tasks.Task.Run(delegate { network.Revalidate(); }).ContinueWith(task => {
                if (!task.IsFaulted || IsDisposed || !IsHandleCreated) return;
                Exception error = task.Exception != null && task.Exception.InnerException != null ? task.Exception.InnerException : task.Exception;
                try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && error != null) status.Text = error.Message; }); }
                catch (InvalidOperationException) { }
            });
        }
        private void AddPeer()
        {
            if (!started) return; using (AddressDialog dialog = new AddressDialog(network.Port)) if (dialog.ShowDialog(this) == DialogResult.OK) try { network.Probe(dialog.Endpoint); status.Text = "正在查找"; } catch (Exception e) { status.Text = e.Message; }
        }
        internal void SetChatFontSize(int size)
        {
            int old=network.Settings.ChatFontSize;network.Settings.ChatFontSize=Math.Max(9,Math.Min(18,size));
            try {store.SaveSettings(network.Settings);foreach(ChatForm chat in chats.Values)chat.ApplyAppearance();}
            catch(Exception e){network.Settings.ChatFontSize=old;status.Text="字号保存失败："+e.Message;}
        }
        internal void OpenSettings(SettingsPage page = SettingsPage.General)
        {
            bool enabled = false, startupAvailable = startup != null;
            try { enabled = startup != null && startup.IsEnabled; }
            catch (Exception e) { startupAvailable = false; status.Text = "启动项读取失败，其他设置仍可保存：" + e.Message; }
            bool restoreFront = Visible && WindowState != FormWindowState.Minimized;
            AppSettings previous = network.Settings;
            AppSettings next = null;
            using (SettingsDialog dialog = new SettingsDialog(previous, store.Root, network.Port, startupAvailable, enabled, page, CleanReceivedFiles))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    if (restoreFront && !IsDisposed) Activate();
                    return;
                }
                next = dialog.Value;
            }
            if (restoreFront && !IsDisposed) Activate();
            next.Window = previous.Window;
            next.TransferWindow = previous.TransferWindow;
            next.GuestWindow = previous.GuestWindow;
            next.SidebarWidth = previous.SidebarWidth;
            next.KnownPeers = previous.KnownPeers;
            next.Conversations = previous.Conversations;
            next.DeviceId = previous.DeviceId;
            try
            {
                if (startupAvailable) startup.SaveSettings(store, previous, next);
                else store.SaveSettings(next);
                network.Settings = next;
                bool nextDark = next.FollowSystemTheme ? Theme.SystemDark : next.DarkMode;
                string nextAccent = Theme.NormalizeAccent(next.Accent);
                bool themeChanged = nextDark != Theme.Dark || nextAccent != Theme.AccentKey;
                bool appearanceChanged = themeChanged || previous.ChatFontSize != next.ChatFontSize ||
                    !string.Equals(previous.ChatBackground ?? "orbits", next.ChatBackground ?? "orbits", StringComparison.Ordinal) ||
                    !string.Equals(previous.BackgroundImage ?? "", next.BackgroundImage ?? "", StringComparison.OrdinalIgnoreCase);
                bool avatarChanged = !string.Equals(previous.Avatar ?? "", next.Avatar ?? "", StringComparison.Ordinal);
                if (themeChanged) Theme.Change(nextDark, nextAccent);
                if (appearanceChanged) ApplyWallpaper();
                if (appearanceChanged || avatarChanged) foreach (ChatForm chat in chats.Values) chat.ApplyAppearance();
                RefreshFilters();
                RefreshNetworkAsync();
                ReceiveAutomatically();
            }
            catch (Exception e) { status.Text = "设置保存失败：" + e.Message; }
        }
        internal void OpenShared(Peer peer)
        {
            OpenSecureTool(peer,ToolsHttp.PortFor(peer.Endpoint.Port),"share");
        }
        internal void OpenPhone(Peer peer)
        {
            int port = peer.Endpoint.Port >= 65534 ? peer.Endpoint.Port - 2 : peer.Endpoint.Port + 2;
            OpenSecureTool(peer,port,"remote");
        }
        private async void OpenSecureTool(Peer peer,int port,string mode)
        {
            int epoch=network.TransportEpoch;
            try
            {
                await System.Threading.Tasks.Task.Run(()=>network.VerifyPeer(peer));if(IsDisposed||epoch!=network.TransportEpoch)return;
                ToolsGateway gateway=new ToolsGateway(network.Security,peer,port,network.LoopbackOnly,network);gateways.Add(gateway);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(gateway.Url+"#"+mode){UseShellExecute=true});
            }
            catch (Exception e) { if (!IsDisposed) using (NoticeDialog notice = new NoticeDialog("连接未完成", e.GetBaseException().Message)) notice.ShowDialog(this); }
        }
        private void OpenTools()
        {
            try { if (toolsWindow == null || toolsWindow.IsDisposed) toolsWindow = new ToolsWindow(network.Port, network.LoopbackOnly, store.Root,network.Security); toolsWindow.Show(); if (toolsWindow.WindowState == FormWindowState.Minimized) toolsWindow.WindowState = FormWindowState.Normal; toolsWindow.Activate(); }
            catch (Exception e) { using (NoticeDialog notice = new NoticeDialog("共享与远控未开启", "请检查额外 TCP 端口是否被占用。\n" + e.Message)) notice.ShowDialog(this); }
        }
        private void ShowMenu(object sender, EventArgs args)
        {
            // Closed 发生时菜单点击派发可能仍在执行；模态窗口还会开启嵌套消息循环。
            // 菜单跟随主窗口复用和释放，不能在 Closed（包括延迟回调）里销毁它。
            if (mainMenu == null)
            {
                mainMenu = Theme.Menu(); mainMenu.MinimumSize = new Size((int)(236 * DeviceDpi / 96F), 0);
                profileItem = new ProfileMenuItem { Text = network.Settings.Nickname, Detail = "本机 · 端口 " + network.Port, Identity = "device:" + network.Settings.DeviceId, Avatar = network.Settings.Avatar };
                profileItem.Click += delegate { OpenSettings(); };
                mainMenu.Items.Add(profileItem);
                mainMenu.Items.Add(new ToolStripSeparator());
                mainMenu.Items.Add("添加联系人", null, delegate { AddPeer(); }); mainMenu.Items.Add("刷新", null, delegate { RefreshNetwork(); });
                nightItem = new ToolStripMenuItem("夜间模式", null, delegate { ToggleNight(); }) { Tag = "switch" };
                mainMenu.Items.Add(nightItem);
                mainMenu.Items.Add("设置", null, delegate { OpenSettings(); });
                mainMenu.Items.Add("共享与远控…", null, delegate { OpenTools(); });
                mainMenu.Items.Add("传输任务…", null, delegate { OpenTransfers(); });
                mainMenu.Items.Add("临时浏览器传输…",null,delegate {OpenGuest();});
                mainMenu.Items.Add("添加到 Windows 发送到菜单…",null,delegate {try{WindowsShare.Install(Environment.GetFolderPath(Environment.SpecialFolder.SendTo),Application.ExecutablePath,store.Root,network.Port);using(NoticeDialog notice=new NoticeDialog("已添加","资源管理器右键文件 → 发送到 → 飞Q。选择联系人后再确认发送。"))notice.ShowDialog(this);}catch(Exception e){OnError(e.Message);}});
                mainMenu.Items.Add("连接诊断…",null,delegate {using(NoticeDialog notice=new NoticeDialog("连接诊断",network.NetworkStatus+"\n\n聊天 UDP/TCP 监听已启动；共享与远控需主动开启。\n未发现设备时：核对同一网段、防火墙、路由器客户端隔离和 VPN 允许局域网设置。可使用添加联系人输入 IPv4:端口。"))notice.ShowDialog(this);});
                mainMenu.Items.Add("已移除的联系人…", null,delegate {using(ForwardDialog dialog=new ForwardDialog(network.Peers.Where(p=>p.Hidden).ToArray(),-1))if(dialog.ShowDialog(this)==DialogResult.OK)OpenChat(dialog.Target);});
                mainMenu.Items.Add("关于", null, delegate { using (AboutDialog dialog = new AboutDialog()) dialog.ShowDialog(this); });
                mainMenu.Items.Add(new ToolStripSeparator());
                // 退出会销毁窗口与菜单，先让当前菜单事件返回再执行。
                mainMenu.Items.Add("退出", null, delegate { Ui(ExitApplication); });
                mainMenu.Opening += delegate
                {
                    profileItem.Text = network.Settings.Nickname; profileItem.Detail = "本机 · 端口 " + network.Port; profileItem.Avatar = network.Settings.Avatar;
                    nightItem.Checked = Theme.Dark;
                };
            }
            mainMenu.Show((Control)sender, new Point(0, ((Control)sender).Height));
        }
        // 菜单里的夜间开关与设置页共用同一持久化字段，切换后立即重绘并保留当前强调色。
        private void ToggleNight()
        {
            bool previous = network.Settings.DarkMode, follow = network.Settings.FollowSystemTheme, dark = !Theme.Dark;
            network.Settings.DarkMode = dark; network.Settings.FollowSystemTheme = false;
            try { store.SaveSettings(network.Settings); }
            catch (Exception e) { network.Settings.DarkMode = previous; network.Settings.FollowSystemTheme = follow; status.Text = "主题保存失败：" + e.Message; return; }
            Theme.Change(dark, network.Settings.Accent);
            foreach (ChatForm chat in chats.Values) chat.ApplyAppearance();
            RefreshFilters(); ApplyWallpaper();
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            WindowPlacement saved = network.Settings.Window;
            if (saved != null && saved.Width > 0 && saved.Height > 0)
            {
                // 拔掉显示器或改变分辨率后，至少把整个窗口约束回可见工作区。
                Rectangle requested = new Rectangle(Math.Max(-100000, Math.Min(100000, saved.X)), Math.Max(-100000, Math.Min(100000, saved.Y)), Math.Min(100000, saved.Width), Math.Min(100000, saved.Height));
                Rectangle area = Screen.FromRectangle(requested).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumWindowWidth, area.Width), Math.Min(MinimumWindowHeight, area.Height));
                int width = Math.Min(area.Width, Math.Max(MinimumSize.Width, requested.Width));
                int height = Math.Min(area.Height, Math.Max(MinimumSize.Height, requested.Height));
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(Math.Max(area.Left, Math.Min(requested.X, area.Right - width)), Math.Max(area.Top, Math.Min(requested.Y, area.Bottom - height)), width, height);
                if (saved.Maximized) WindowState = FormWindowState.Maximized;
            }
            ApplySidebarWidth();
            windowLoaded = true;
            TrackWindowPlacement(this, EventArgs.Empty);
        }
        protected override void SetVisibleCore(bool value)
        {
            if (value && startInTray)
            {
                startInTray = false;
                // Application.Run 仍需有效窗口句柄派发网络回调；首次不显示，托盘恢复时正常加载布局。
                if (!IsHandleCreated) CreateHandle(); value = false;
            }
            base.SetVisibleCore(value);
        }
        private void TrackWindowPlacement(object sender, EventArgs e)
        {
            if (!windowLoaded || !Visible || WindowState == FormWindowState.Minimized) return;
            Rectangle normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (normal.Width <= 0 || normal.Height <= 0) return;
            network.Settings.Window = new WindowPlacement { X = normal.X, Y = normal.Y, Width = normal.Width, Height = normal.Height, Maximized = WindowState == FormWindowState.Maximized };
            windowSaveTimer.Stop(); windowSaveTimer.Start();
        }
        private float SidebarScale { get { return DeviceDpi / 96F; } }
        private void ApplySidebarWidth()
        {
            float scale = SidebarScale;
            int requested = network.Settings.SidebarWidth > 0 ? network.Settings.SidebarWidth : 336;
            int minimum = (int)Math.Round(200 * scale);
            int maximum = Math.Max(minimum, Math.Min((int)Math.Round(480 * scale), layout.ClientSize.Width - (int)Math.Round(428 * scale)));
            layout.ColumnStyles[1].Width = (int)Math.Round(8 * scale);
            layout.ColumnStyles[0].Width = Math.Max(minimum, Math.Min(maximum, (int)Math.Round(Math.Max(200, Math.Min(480, requested)) * scale)));
        }
        private void SetSidebarWidth(int width)
        {
            int maximum = Math.Max(200, Math.Min(480, (int)(layout.ClientSize.Width / SidebarScale) - 428));
            network.Settings.SidebarWidth = Math.Max(200, Math.Min(maximum, (int)Math.Round(width / SidebarScale)));
            ApplySidebarWidth();
            ModernWindow.RedrawLayout(layout);
            windowSaveTimer.Stop(); windowSaveTimer.Start();
        }
        private void SaveWindowPlacement()
        {
            if (!windowLoaded) return;
            try { store.SaveSettings(network.Settings); }
            catch (Exception e) { status.Text = "窗口状态保存失败：" + e.Message; }
        }
        internal void Restore() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = network.Settings.Window != null && network.Settings.Window.Maximized ? FormWindowState.Maximized : FormWindowState.Normal; Activate(); }
        public void ExitApplication() { exiting = true; Close(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            TrackWindowPlacement(this, EventArgs.Empty); windowSaveTimer.Stop(); SaveWindowPlacement();
            if (!exiting && e.CloseReason == CloseReason.UserClosing && network.Settings.CloseToTray) { e.Cancel = true; Hide(); return; }
            exiting = true; base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                network.NetworkReset-=OnNetworkReset;foreach(ToolsGateway gateway in gateways)gateway.Dispose();gateways.Clear();
                network.ConnectionInterrupted -= OnConnectionInterrupted;
                shareTimer.Stop();shareTimer.Dispose();
                if (toolsWindow != null) toolsWindow.Dispose();
                if (transferWindow != null) transferWindow.Dispose();
                if (guestWindow != null) guestWindow.Dispose();
                windowSaveTimer.Stop(); SaveWindowPlacement(); windowSaveTimer.Dispose();
                diagnostics.Record(DiagnosticEvent.Stopped);network.Dispose();
                try { store.CompletePending(); } catch (IOException e) { status.Text = "记录保存失败：" + e.Message; }
                catch (UnauthorizedAccessException e) { status.Text = "记录保存失败：" + e.Message; }
            }
            if (disposing) { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged; Theme.Changed -= ApplyWallpaper; network.PeersChanged -= OnPeersChanged; network.MessageReceived -= OnMessage; network.OfferCanceled -= OnOfferCanceled; network.DeliveryChanged -= OnDelivery; network.Error -= OnError; network.TransferChanged -= OnTransfer; if (mainMenu != null) mainMenu.Dispose(); if(conversationMenu!=null)conversationMenu.Dispose(); if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); } foreach (ChatForm chat in chats.Values) chat.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
