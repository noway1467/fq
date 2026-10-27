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
        private readonly TextBox search;
        private readonly Label status = Theme.Label("连接中", Theme.Small, Theme.Muted);
        private readonly ChatHost chatHost = new ChatHost { Dock = DockStyle.Fill, BackColor = Theme.Wallpaper };
        private readonly NotifyIcon tray;
        private readonly TabStrip tabs = new TabStrip { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 8, 0), Margin = Padding.Empty };
        private ContextMenuStrip mainMenu, conversationMenu;
        private ToolStripMenuItem nightItem;
        private ProfileMenuItem profileItem;
        private readonly List<RoundedButton> filters = new List<RoundedButton>();
        private bool exiting, started, automaticReceiving;
        private bool windowLoaded;
        private readonly WindowsStartup startup;
        private bool startInTray;
        private int peersRefreshQueued;
        private readonly Timer windowSaveTimer = new Timer { Interval = 500 };
        private int filter;
        private string fingerprint;
        private Peer latestSender;
        public ChatForm ActiveChat { get; private set; }
        public MainForm(LanService network, LocalStore store, WindowsStartup startup = null, bool startInTray = false)
        {
            this.startup = startup; this.startInTray = startInTray;
            diagnostics=new Diagnostics(store.Root,()=>network.Settings.DiagnosticsEnabled);
            this.network = network; this.store = store; Text = "飞Q"; ClientSize = new Size(1120, 760); MinimumSize = new Size(880, 580);
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
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = Theme.Surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 336)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Surface.Controls.Add(layout);
            TableLayoutPanel sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.Controls.Add(sidebar, 0, 0); layout.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Line, Margin = Padding.Empty }, 1, 0); chatHost.Margin = Padding.Empty; layout.Controls.Add(chatHost, 2, 0);
            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(8, 10, 10, 8), Margin = Padding.Empty, BackColor = Theme.Surface };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Button menu = Theme.IconButton("menu", "菜单", ShowMenu, false); menu.Dock = DockStyle.Fill; menu.Margin = Padding.Empty; top.Controls.Add(menu, 0, 0);
            SearchBox query = new SearchBox { Dock = DockStyle.Fill, Margin = new Padding(4, 2, 6, 2) }; search = query.Input; search.AccessibleName = "查找联系人"; search.MaxLength = 100; query.Placeholder = "搜索联系人或消息"; search.TextChanged += delegate { RefreshPeers(); };
            top.Controls.Add(query, 1, 0);
            Button add = Theme.IconButton("plus", "添加联系人", delegate { AddPeer(); }, false); add.Dock = DockStyle.Fill; add.Margin = Padding.Empty; top.Controls.Add(add, 2, 0); sidebar.Controls.Add(top, 0, 0);
            string[] names = { "全部", "未读", "在线" };
            for (int i = 0; i < names.Length; i++) { int value = i; RoundedButton button = (RoundedButton)Theme.Button(names[i], delegate { filter = value; RefreshFilters(); RefreshPeers(); }, false); button.Size = new Size(88, 44); button.Margin = Padding.Empty; filters.Add(button); tabs.Add(button); }
            sidebar.Controls.Add(tabs, 0, 1); contacts.Dock = DockStyle.Fill; contacts.Margin = Padding.Empty; contacts.PeerActivated += OpenChat; contacts.PeerMenuRequested += ShowConversationMenu; sidebar.Controls.Add(contacts, 0, 2);
            Panel footer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 10, 0), Margin = Padding.Empty, BackColor = Theme.Surface };
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 0, 0, footer.Width, 0);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush dot = new SolidBrush(started ? Theme.Online : Theme.Muted)) e.Graphics.FillEllipse(dot, 2, footer.Height / 2 - 3, 6, 6);
            };
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Padding = new Padding(12, 0, 0, 0); footer.Controls.Add(status);
            Button refresh = Theme.IconButton("refresh", "刷新", delegate { RefreshNetwork(); }, false); refresh.Dock = DockStyle.Right; refresh.Size = new Size(40, 40); refresh.Margin = Padding.Empty; footer.Controls.Add(refresh);
            Button settings = Theme.IconButton("settings", "设置", delegate { OpenSettings(); }, false); settings.Dock = DockStyle.Right; settings.Size = new Size(40, 40); settings.Margin = new Padding(0, 0, 4, 0); footer.Controls.Add(settings);
            sidebar.Controls.Add(footer, 0, 3);
            chatHost.Hint = "选择联系人开始聊天";
            tray = new NotifyIcon { Icon = Theme.AppIcon, Text = "飞Q", Visible = true };
            ContextMenuStrip trayMenu = Theme.Menu(); trayMenu.Items.Add("打开", null, delegate { Restore(); }); trayMenu.Items.Add("设置", null, delegate { OpenSettings(); }); trayMenu.Items.Add("退出", null, delegate { Ui(ExitApplication); }); tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { Restore(); }; tray.BalloonTipClicked += delegate { Restore(); if (latestSender != null) OpenChat(latestSender); };
            network.PeersChanged += OnPeersChanged; network.MessageReceived += OnMessage; network.DeliveryChanged += OnDelivery; network.Error += OnError; network.TransferChanged += OnTransfer;
            RefreshFilters();
            ApplyWallpaper();
            Theme.Changed += ApplyWallpaper;
        }
        // 主界面未打开会话时的背景与聊天页保持一致，切换主题或背景设置后立即刷新。
        private void ApplyWallpaper()
        {
            string pattern = network.Settings.ChatBackground ?? "orbits";
            chatHost.Pattern = pattern == "image" || pattern == "none" ? "orbits" : pattern;
            chatHost.Invalidate();
        }
        private void Ui(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)delegate { if (!IsDisposed && !Disposing) action(); }); } catch (InvalidOperationException) { }
        }
        public void NetworkStarted() { if(!started)diagnostics.Record(DiagnosticEvent.Started); started = true; System.Threading.Interlocked.Exchange(ref peersRefreshQueued,0); RefreshPeers(); if (store.Warning != null) status.Text = store.Warning; }
        internal void PrepareNetworkCallbacks() { if(!IsHandleCreated)CreateHandle(); }
        private bool RememberPeerAddresses()
        {
            List<Peer> peers=network.Peers;Peer.UpdateConversationNames(peers);
            string[] previous = network.Settings.KnownPeers ?? new string[0];
            string[] next = AppSettings.NormalizeKnownPeers(peers.Select(p => p.Endpoint.ToString()).Concat(previous));
            SavedConversation[] old=SavedConversation.Normalize(network.Settings.Conversations);
            SavedConversation[] current=SavedConversation.Normalize(peers.Select(p=>new SavedConversation {
                Endpoint=p.Endpoint.ToString(),Login=p.Login,Host=p.Host,Nickname=p.Nickname,Group=p.Group,Note=p.Note,Pinned=p.Pinned,Hidden=p.Hidden,ConversationNumber=p.ConversationNumber,
                DeviceId=p.DeviceId,ConversationId=p.Id,
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
        private void Remember(Peer peer, ChatRecord record) { recent.Remove(peer.Id); recent.Insert(0, peer.Id); if (recent.Count > 1000) recent.RemoveAt(1000); previews[peer.Id] = (record.Outgoing ? "你：" : "") + record.Text; times[peer.Id] = record.Time; }
        private void OnMessage(IncomingMessage message)
        {
            Ui(() =>
            {
                message.Record.Transfers = message.Files.Select(f => new FileTransferState { Id = f.Id, Name = f.Name, Size = f.Size, Status = "待接收 · 点击接收" }).ToArray();
                if (message.Files.Count > 0) { message.Record.HasAttachments = true; message.Record.AttachmentNames = message.Files.Select(f => f.Name).ToArray(); message.Record.Text += (String.IsNullOrEmpty(message.Record.Text) ? "" : "\n") + "[文件] " + String.Join("、", message.Files.Select(f => f.Name)); }
                // ACK 已由网络层返回；重复邀请不能回退本机状态、增加未读或再次触发自动接收。
                try { if (!store.AppendIncoming(message.Record)) return; }
                catch (Exception e) { status.Text = "记录保存失败：" + e.Message; }
                message.Peer.Hidden=false; Remember(message.Peer, message.Record);
                ChatForm chat; chats.TryGetValue(message.Peer.Id, out chat); bool visible = chat != null && chat == ActiveChat && ContainsFocus;
                if (!visible) { if (!unread.ContainsKey(message.Peer.Id)) unread[message.Peer.Id] = 0; unread[message.Peer.Id]++; }
                bool loaded = false;
                if ((chat == null || chat.IsDisposed) && message.Files.Count > 0) { chat = GetOrCreateChat(message.Peer); loaded = true; }
                if (chat != null && !chat.IsDisposed)
                {
                    if (!loaded) chat.Append(message.Record);
                    if (chats.Values.Sum(c => c.PendingFileCount) + message.Files.Count <= 100) chat.AddAttachments(message);
                    else status.Text = "待接收文件已达 100 个，请处理后让对方重发。";
                }
                latestSender = message.Peer;
                RefreshPeers();
                if (!visible && network.Settings.Notifications) { tray.BalloonTipTitle = message.Peer.DisplayName; tray.BalloonTipText = message.Files.Count > 0 ? "收到文件" : "收到新消息"; tray.ShowBalloonTip(3000); }
            });
        }
        private void OnDelivery(ChatRecord record) { diagnostics.Record(DiagnosticEvent.DeliveryChanged); Ui(() => { TrySave(record); ChatForm chat; if (chats.TryGetValue(record.PeerId, out chat) && !chat.IsDisposed) chat.Delivery(record); }); }
        private void OnTransfer(ChatRecord record, int id, int percent, string status)
        {
            Ui(() => {
                FileTransferState file = record.Transfers == null ? null : record.Transfers.FirstOrDefault(f => f.Id == id);
                if (file == null) return; file.Percent = percent; file.Status = status;
                if (status != "发送中") TrySave(record);
                ChatForm chat; if (chats.TryGetValue(record.PeerId, out chat) && !chat.IsDisposed) chat.Delivery(record);
            });
        }
        internal void TrySave(ChatRecord record) { try { store.Append(record); } catch (Exception e) { status.Text = "记录保存失败：" + e.Message; } }
        internal void SaveDraft(string peerId, string text) { try { store.SaveDraft(peerId, text); } catch (Exception e) { status.Text = "草稿保存失败：" + e.Message; } }
        internal void Sent(Peer peer, ChatRecord record) { TrySave(record); Remember(peer, record); RefreshPeers(); }
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
            if (automaticReceiving || exiting || !network.Settings.AutoReceiveFiles) return;
            automaticReceiving = true;
            try
            {
                // 全局按序接收，未打开的会话也参与；不为每个邀请启动一个并发下载。
                while (!exiting && network.Settings.AutoReceiveFiles)
                {
                    ChatForm next = chats.Values.FirstOrDefault(c => !c.IsDisposed && c.HasAutomaticFile);
                    if (next == null) break;
                    await next.ReceiveNextAutomaticAsync();
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
            string next = filter + "|" + query + "|" + started + "|" + String.Join("|", all.Select(p => p.Id + p.DisplayName + p.Pinned + p.Online + p.Group + p.Endpoint + p.Host)) + "|" + String.Join("|", recent) + "|" + String.Join("|", unread.Select(p => p.Key + p.Value)) + "|" + String.Join("|", previews.Values);
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
            if(conversationMenu==null)conversationMenu=Theme.Menu();
            foreach(ToolStripItem item in conversationMenu.Items.Cast<ToolStripItem>().ToArray())item.Dispose();
            conversationMenu.Items.Clear();
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
            using(ConversationDialog dialog=new ConversationDialog(peer,false))if(dialog.ShowDialog(this)==DialogResult.OK){
                string old=peer.Note;peer.Note=dialog.Note;if(!RememberPeerAddresses())peer.Note=old;
                foreach(ChatForm chat in chats.Values)chat.RefreshPeer();fingerprint=null;RefreshPeers();
            }
        }
        private void DeleteConversation(Peer peer,bool clearOnly)
        {
            using(ConversationDialog dialog=new ConversationDialog(peer,true,clearOnly))if(dialog.ShowDialog(this)==DialogResult.OK){
                ChatForm chat;chats.TryGetValue(peer.Id,out chat);
                if(dialog.DeleteHistory&&chat!=null&&chat.PendingFileCount>0){using(NoticeDialog notice=new NoticeDialog("请先处理文件","此会话还有待接收或传输中的文件。请完成或拒绝后再清空记录。"))notice.ShowDialog(this);return;}
                try {
                    if(dialog.DeleteHistory){store.ClearHistory(peer.Id);if(chat!=null)chat.ClearHistory();previews.Remove(peer.Id);times.Remove(peer.Id);recent.Remove(peer.Id);}
                    bool hidden=peer.Hidden;peer.Hidden=!clearOnly;unread.Remove(peer.Id);
                    if(!RememberPeerAddresses()){peer.Hidden=hidden;fingerprint=null;RefreshPeers();return;}
                    if(!clearOnly&&chat!=null)CloseChat(chat);
                    fingerprint=null;RefreshPeers();
                }catch(Exception e){using(NoticeDialog notice=new NoticeDialog("操作未完成",e.Message))notice.ShowDialog(this);}
            }
        }
        private void RefreshNetwork() { if (started) try { network.Revalidate(); } catch (Exception e) { status.Text = e.Message; } }
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
        internal void OpenSettings()
        {
            bool enabled = false, startupAvailable = startup != null;
            try { enabled = startup != null && startup.IsEnabled; }
            catch (Exception e) { startupAvailable = false; status.Text = "启动项读取失败，其他设置仍可保存：" + e.Message; }
            using (SettingsDialog dialog = new SettingsDialog(network.Settings, store.Root, network.Port, startupAvailable, enabled))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                dialog.Value.Window = network.Settings.Window;
                dialog.Value.KnownPeers = network.Settings.KnownPeers;
                dialog.Value.Conversations = network.Settings.Conversations;
                dialog.Value.DeviceId = network.Settings.DeviceId;
                try { if (startupAvailable) startup.SaveSettings(store, network.Settings, dialog.Value); else store.SaveSettings(dialog.Value); network.Settings = dialog.Value; Theme.Change(dialog.Value.DarkMode,dialog.Value.Accent); foreach(ChatForm chat in chats.Values)chat.ApplyAppearance(); RefreshFilters(); RefreshNetwork(); ReceiveAutomatically(); } catch (Exception e) { status.Text = "设置保存失败：" + e.Message; }
            }
        }
        private void ShowMenu(object sender, EventArgs args)
        {
            // Closed 发生时菜单点击派发可能仍在执行；模态窗口还会开启嵌套消息循环。
            // 菜单跟随主窗口复用和释放，不能在 Closed（包括延迟回调）里销毁它。
            if (mainMenu == null)
            {
                mainMenu = Theme.Menu(); mainMenu.MinimumSize = new Size((int)(236 * DeviceDpi / 96F), 0);
                profileItem = new ProfileMenuItem { Text = network.Settings.Nickname, Detail = "本机 · 端口 " + network.Port, Identity = network.Login };
                profileItem.Click += delegate { OpenSettings(); };
                mainMenu.Items.Add(profileItem);
                mainMenu.Items.Add(new ToolStripSeparator());
                mainMenu.Items.Add("添加联系人", null, delegate { AddPeer(); }); mainMenu.Items.Add("刷新", null, delegate { RefreshNetwork(); });
                nightItem = new ToolStripMenuItem("夜间模式", null, delegate { ToggleNight(); }) { Tag = "switch" };
                mainMenu.Items.Add(nightItem);
                mainMenu.Items.Add("设置", null, delegate { OpenSettings(); });
                mainMenu.Items.Add("已移除的联系人…", null,delegate {using(ForwardDialog dialog=new ForwardDialog(network.Peers.Where(p=>p.Hidden).ToArray(),-1))if(dialog.ShowDialog(this)==DialogResult.OK)OpenChat(dialog.Target);});
                mainMenu.Items.Add("关于", null, delegate { using (AboutDialog dialog = new AboutDialog()) dialog.ShowDialog(this); });
                mainMenu.Items.Add(new ToolStripSeparator());
                // 退出会销毁窗口与菜单，先让当前菜单事件返回再执行。
                mainMenu.Items.Add("退出", null, delegate { Ui(ExitApplication); });
                mainMenu.Opening += delegate
                {
                    profileItem.Text = network.Settings.Nickname; profileItem.Detail = "本机 · 端口 " + network.Port;
                    nightItem.Checked = network.Settings.DarkMode;
                };
            }
            mainMenu.Show((Control)sender, new Point(0, ((Control)sender).Height));
        }
        // 菜单里的夜间开关与设置页共用同一持久化字段，切换后立即重绘并保留当前强调色。
        private void ToggleNight()
        {
            bool dark = !network.Settings.DarkMode;
            network.Settings.DarkMode = dark;
            try { store.SaveSettings(network.Settings); }
            catch (Exception e) { network.Settings.DarkMode = !dark; status.Text = "主题保存失败：" + e.Message; return; }
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
                MinimumSize = new Size(Math.Min(880, area.Width), Math.Min(580, area.Height));
                int width = Math.Min(area.Width, Math.Max(MinimumSize.Width, requested.Width));
                int height = Math.Min(area.Height, Math.Max(MinimumSize.Height, requested.Height));
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(Math.Max(area.Left, Math.Min(requested.X, area.Right - width)), Math.Max(area.Top, Math.Min(requested.Y, area.Bottom - height)), width, height);
                if (saved.Maximized) WindowState = FormWindowState.Maximized;
            }
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
        private void SaveWindowPlacement()
        {
            if (!windowLoaded) return;
            try { store.SaveSettings(network.Settings); }
            catch (Exception e) { status.Text = "窗口状态保存失败：" + e.Message; }
        }
        private void Restore() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = network.Settings.Window != null && network.Settings.Window.Maximized ? FormWindowState.Maximized : FormWindowState.Normal; Activate(); }
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
                windowSaveTimer.Stop(); SaveWindowPlacement(); windowSaveTimer.Dispose();
                diagnostics.Record(DiagnosticEvent.Stopped);network.Dispose();
                try { store.CompletePending(); } catch (IOException e) { status.Text = "记录保存失败：" + e.Message; }
                catch (UnauthorizedAccessException e) { status.Text = "记录保存失败：" + e.Message; }
            }
            if (disposing) { Theme.Changed -= ApplyWallpaper; network.PeersChanged -= OnPeersChanged; network.MessageReceived -= OnMessage; network.DeliveryChanged -= OnDelivery; network.Error -= OnError; network.TransferChanged -= OnTransfer; if (mainMenu != null) mainMenu.Dispose(); if(conversationMenu!=null)conversationMenu.Dispose(); if (tray != null) { tray.Visible = false; if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose(); tray.Dispose(); } foreach (ChatForm chat in chats.Values) chat.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
