using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FeiqLight
{
    public sealed class ChatForm : Form
    {
        private sealed class ReceivedFile { public long Packet; public Attachment File; public ChatRecord Record; public readonly HashSet<string> PartialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public bool AutomaticPending = true; public string Status = "待接收"; public override string ToString() { return File.Name; } }
        private readonly Peer peer;
        private readonly LanService network;
        private readonly LocalStore store;
        private readonly MainForm main;
        private readonly BubbleHistory history = new BubbleHistory();
        private readonly RichTextBox editor = new RichTextBox();
        private readonly ListBox files = new ListBox();
        private readonly Label queued = Theme.Label("", Theme.Small, Theme.Muted);
        private readonly Label fileStatus = Theme.Label("", Theme.Small, Theme.Muted);
        private readonly Button receive, reject, fileButton;
        private readonly PictureBox avatar;
        private readonly Label peerName = Theme.Label("", Theme.Title, Theme.Ink);
        private readonly Label peerStatus = Theme.Label("", Theme.Small, Theme.Muted);
        private string avatarName;
        private bool avatarOnline;
        private Font chatFont;
        internal void ApplyAppearance() {
            int size=Math.Max(9,Math.Min(18,network.Settings.ChatFontSize));
            // Control.Font 会忽略值相同的赋值；只有字号真正改变时才替换并释放旧字体，避免控件仍引用已释放对象。
            if(chatFont==null||chatFont.Size!=size) {
                Font old=chatFont;chatFont=new Font(Theme.Body.FontFamily,size);
                editor.Font=chatFont;history.Font=chatFont;if(old!=null)old.Dispose();
            }
            ResizeComposer();
            history.SetWallpaper(network.Settings.ChatBackground,network.Settings.BackgroundImage);
        }
        private readonly TableLayoutPanel middle, root, side, queue;
        private readonly List<string> attachments = new List<string>();
        private CancellationTokenSource receiving;
        private ReceivedFile activeFile;
        private readonly System.Windows.Forms.Timer draftTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private bool drawer;
        private ContextMenuStrip chatMenu, fileMenu;
        private EmojiPicker emojiPicker;
        public ChatForm(Peer peer, LanService network, LocalStore store, MainForm main)
        {
            this.peer = peer; this.network = network; this.store = store; this.main = main;
            Theme.Apply(this); DoubleBuffered = true; Text = peer.Nickname; FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(760, 720); MinimumSize = Size.Empty;
            root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); Controls.Add(root);
            TableLayoutPanel head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Padding = new Padding(18, 8, 14, 8), Margin = Padding.Empty, BackColor = Theme.Surface };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 3; i++) head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            avatar = new PictureBox { Image = Theme.ContactAvatar(46, peer.BaseDisplayName, peer.Online), AccessibleName = "聊天联系人头像", Size = new Size(56, 56), Margin = Padding.Empty, SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left }; head.Controls.Add(avatar, 0, 0);
            TableLayoutPanel identity = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty }; identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); identity.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); identity.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            peerName.Dock = DockStyle.Fill; peerName.AutoEllipsis = true; peerName.AccessibleName = "聊天联系人昵称"; identity.Controls.Add(peerName, 0, 0);
            peerStatus.Dock = DockStyle.Fill; peerStatus.AccessibleName = "聊天联系人状态"; identity.Controls.Add(peerStatus, 0, 1); head.Controls.Add(identity, 1, 0);
            avatarName = peer.BaseDisplayName; avatarOnline = peer.Online; RefreshPeer();
            Button search = Theme.IconButton("search", "聊天记录", ShowHistory, false); search.Dock = DockStyle.Fill; search.Margin = Padding.Empty; head.Controls.Add(search, 2, 0);
            fileButton = Theme.IconButton("inbox", "收到的文件", delegate { drawer = !drawer; UpdateFilesPanel(); }, false); fileButton.Dock = DockStyle.Fill; fileButton.Margin = Padding.Empty; head.Controls.Add(fileButton, 3, 0);
            Button menu = Theme.IconButton("more", "会话菜单", ShowMenu, false); menu.Dock = DockStyle.Fill; menu.Margin = Padding.Empty; head.Controls.Add(menu, 4, 0); root.Controls.Add(head, 0, 0);
            middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            history.RetryRequested += RetryMessage;
            history.FileRequested += (record, id) => {
                ReceivedFile item = files.Items.Cast<ReceivedFile>().FirstOrDefault(f => f.Packet == record.Packet && f.File.Id == id);
                if (item == null) { main.FileReceiveStatus("此文件邀请已结束；未保存的附件请让对方重新发送。"); return; }
                if (receiving != null) { if (activeFile == item) receiving.Cancel(); return; }
                files.SelectedItem = item; ReceiveFile(this, EventArgs.Empty);
            };
            history.ForwardRequested += delegate(ChatRecord record) { main.Forward(record); };
            history.Dock = DockStyle.Fill; history.Margin = Padding.Empty; middle.Controls.Add(history, 0, 0);
            side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12), Margin = Padding.Empty, BackColor = Theme.Surface }; side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); side.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); side.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); side.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); side.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Label fileTitle = Theme.Label("文件", Theme.Title, Theme.Ink); fileTitle.Dock = DockStyle.Fill; side.Controls.Add(fileTitle, 0, 0);
            files.Dock = DockStyle.Fill; files.BorderStyle = BorderStyle.None; files.DrawMode = DrawMode.OwnerDrawFixed; files.ItemHeight = 60; files.IntegralHeight = false; files.AccessibleName = "收到的文件列表"; files.DrawItem += DrawFile; side.Controls.Add(files, 0, 1);
            ConfigureFileMenu();
            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            receive = Theme.Button("接收", ReceiveFile, true); reject = Theme.Button("拒绝", RejectFile, false); receive.Dock = reject.Dock = DockStyle.Fill; receive.Enabled = reject.Enabled = false; actions.Controls.Add(receive, 0, 0); actions.Controls.Add(reject, 1, 0); side.Controls.Add(actions, 0, 2);
            files.SelectedIndexChanged += delegate { if (receiving == null) receive.Enabled = reject.Enabled = files.SelectedItem != null; };
            fileStatus.Dock = DockStyle.Fill; fileStatus.AutoEllipsis = true; side.Controls.Add(fileStatus, 0, 3); middle.Controls.Add(side, 1, 0); root.Controls.Add(middle, 0, 1); UpdateFilesPanel();
            queue = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(18, 1, 8, 1), Margin = Padding.Empty, Visible = false }; queue.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); queue.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40)); queue.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            queued.Dock = DockStyle.Fill; queued.AutoEllipsis = true; queue.Controls.Add(queued, 0, 0); Button remove = Theme.IconButton("close", "移除附件", delegate { attachments.Clear(); UpdateQueued(); }, false); remove.Dock = DockStyle.Fill; remove.Margin = Padding.Empty; queue.Controls.Add(remove, 1, 0); root.Controls.Add(queue, 0, 2);
            TableLayoutPanel compose = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(10, 11, 14, 11), Margin = Padding.Empty, BackColor = Theme.Surface };
            compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); compose.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Button emoji = Theme.IconButton("smile", "表情", ShowEmoji, false); emoji.Dock = DockStyle.Fill; emoji.Margin = Padding.Empty; compose.Controls.Add(emoji, 0, 0);
            RoundedPanel input = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Radius = Theme.ControlRadius, Padding = new Padding(15, 13, 12, 6), Margin = new Padding(4, 1, 6, 1) };
            editor.BorderStyle = BorderStyle.None; editor.Dock = DockStyle.Fill; editor.BackColor = Theme.Ice; editor.Font = new Font(Theme.Body.FontFamily, 10F); editor.MaxLength = 18000; editor.AccessibleName = "消息输入框"; editor.DetectUrls = false; editor.ScrollBars = RichTextBoxScrollBars.None;
            Theme.AttachEditMenu(editor);
            editor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) { e.SuppressKeyPress = true; Theme.PasteText(editor); }
                else if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendMessage(); }
            }; input.Controls.Add(editor); compose.Controls.Add(input, 1, 0);
            draftTimer.Tick += delegate { draftTimer.Stop(); SaveDraft(); };
            editor.TextChanged += delegate { draftTimer.Stop(); draftTimer.Start(); ResizeComposer(); };
            editor.Resize += delegate { ResizeComposer(); };
            Button file = Theme.IconButton("clip", "发送文件", SelectFiles, false); file.Dock = DockStyle.Fill; file.Margin = Padding.Empty; compose.Controls.Add(file, 2, 0);
            Button send = Theme.IconButton("send", "发送消息", delegate { SendMessage(); }, true); send.Dock = DockStyle.Fill; send.Margin = new Padding(3); compose.Controls.Add(send, 3, 0); root.Controls.Add(compose, 0, 3);
            AllowDrop = true; DragEnter += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; }; DragDrop += delegate(object sender, DragEventArgs e) { string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[]; if (paths != null) QueueFiles(paths); };
            ApplyAppearance();
            try { foreach (ChatRecord record in store.History(peer.Id, 150)) Append(record.Outgoing ? network.OutgoingRecord(peer.Id, record.Packet) ?? record : record); editor.Text = store.LoadDraft(peer.Id); } catch (Exception e) { Error("本地记录", e.Message); }
        }
        private bool resizingComposer;
        private void ResizeComposer()
        {
            if(resizingComposer || IsDisposed || Disposing || editor.IsDisposed || editor.Disposing || root==null || root.RowStyles.Count<4 || !editor.IsHandleCreated)return;
            resizingComposer=true;
            try {
                float scale=DeviceDpi/96F; int lines=Math.Max(1,editor.GetLineFromCharIndex(editor.TextLength)+1);
                // 短消息不显示原生滚动箭头；输入区随内容增长，长文本仍能用光标和滚轮滚动。
                int height=Math.Max((int)(76*scale),Math.Min(5,lines)*editor.Font.Height+(int)(44*scale));
                root.RowStyles[3].Height=Math.Min(height,Math.Max((int)(76*scale),ClientSize.Height/3));
            } finally {resizingComposer=false;}
        }
        public void SaveDraft() { draftTimer.Stop(); main.SaveDraft(peer.Id, editor.Text); }
        public void RefreshPeer()
        {
            Text = peer.DisplayName; peerName.Text = peer.DisplayName; peerStatus.Text = (peer.Online ? "在线" : "离线") + (peer.DuplicateName ? peer.NameSuffix+" · "+peer.Endpoint : "");
            peerName.AccessibleDescription=peer.DisplayName+" · "+peer.Id;
            avatar.AccessibleDescription = peer.Online ? "在线" : "离线";
            if (avatarName != peer.BaseDisplayName || avatarOnline != peer.Online)
            {
                Image old = avatar.Image; avatar.Image = Theme.ContactAvatar(46, peer.BaseDisplayName, peer.Online);
                avatarName = peer.BaseDisplayName; avatarOnline = peer.Online; if (old != null) old.Dispose();
            }
        }
        public void FocusEditor() { editor.Focus(); }
        public void Append(ChatRecord record) { history.Append(record); }
        public void ClearHistory() { history.Clear(); }
        public void Delivery(ChatRecord record) { history.Delivery(record); }
        public void AddAttachments(IncomingMessage message)
        {
            foreach (Attachment file in message.Files) { if (files.Items.Count >= 100) { fileStatus.Text = "待接收文件已达上限"; break; } files.Items.Add(new ReceivedFile { Packet = message.Record.Packet, File = file, Record = message.Record }); }
            if (files.Items.Count > 0 && files.SelectedIndex < 0) files.SelectedIndex = 0;
            drawer = files.Items.Count > 0; UpdateFilesPanel(); main.ReceiveAutomatically();
        }
        private void UpdateFilesPanel() { bool visible = drawer && files.Items.Count > 0; side.Visible = visible; middle.ColumnStyles[1].Width = visible ? 220 * DeviceDpi / 96F : 0; fileButton.Enabled = files.Items.Count > 0; }
        private void DrawFile(object sender, DrawItemEventArgs e)
        {
            // 接收完成移除条目后，WinForms 仍可能派发已排队的旧绘制索引。
            if (e.Index < 0 || e.Index >= files.Items.Count) return; ReceivedFile item = (ReceivedFile)files.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
            using(SolidBrush background=new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(background,e.Bounds);
            using (System.Drawing.Drawing2D.GraphicsPath path = Theme.Rounded(new Rectangle(e.Bounds.X + 1, e.Bounds.Y + 2, Math.Max(4, e.Bounds.Width - 2), Math.Max(4, e.Bounds.Height - 4)), 10))
            using (SolidBrush b = new SolidBrush(selected ? Theme.Sky : Theme.Surface)) e.Graphics.FillPath(b, path);
            int pad = 8; Theme.Glyph(e.Graphics, "file", new Rectangle(e.Bounds.Left + pad, e.Bounds.Top + 12, 28, 28), Theme.Blue);
            Rectangle name = new Rectangle(e.Bounds.Left + 44, e.Bounds.Top + 9, e.Bounds.Width - 48, 23); TextRenderer.DrawText(e.Graphics, item.File.Name, Theme.Body, name, Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            name.Y += 25; TextRenderer.DrawText(e.Graphics, Theme.SizeText(item.File.Size) + " · " + item.Status, Theme.Small, name, Theme.Muted);
        }
        private void SendMessage()
        {
            if (String.IsNullOrWhiteSpace(editor.Text) && attachments.Count == 0) return;
            try { ChatRecord record = network.SendMessage(peer, editor.Text, attachments); main.Sent(peer, record); Append(record); Delivery(record); editor.Clear(); SaveDraft(); attachments.Clear(); UpdateQueued(); editor.Focus(); history.ScrollToLatest(); }
            catch (Exception e) { Error("发送失败", e.Message); }
        }
        internal void ForwardContent(string text, string[] paths)
        {
            // 转发独立发送，不读取或清空目标会话正在编辑的草稿和附件。
            ChatRecord record = network.SendMessage(peer, text, paths);
            main.Sent(peer, record); Append(record); Delivery(record); history.ScrollToLatest();
        }
        private void RetryMessage(ChatRecord previous)
        {
            if(!(previous.State??"").Contains("未确认")) return;
            try {
                string[] paths=previous.GetLocalFiles();string text=previous.Text;
                if(paths.Any(p=>!File.Exists(p))) throw new IOException("原附件已移动或删除，请重新选择文件发送。");
                // 普通消息也可能含有“[文件] ”，只能剥离本机附件路径对应的末尾展示标签。
                if(paths.Length>0) {
                    string suffix="[文件] "+String.Join("、",paths.Select(Path.GetFileName));
                    if(text.EndsWith(suffix,StringComparison.Ordinal)) { text=text.Substring(0,text.Length-suffix.Length); if(text.EndsWith("\n")) text=text.Substring(0,text.Length-1); }
                }
                // 新报价使用新的消息号；原记录立即标记，防止重复点击同一条失败消息。
                ChatRecord sent=network.SendMessage(peer,text,paths);main.Sent(peer,sent);Append(sent);history.ScrollToLatest();
                previous.State="已重新发送（新消息）";main.TrySave(previous);Delivery(previous);
            } catch(Exception e) {Error("重发失败",e.Message);}
        }
        private void QueueFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (Directory.Exists(path)) { Error("未添加文件夹", "请压缩后发送。"); continue; }
                if (attachments.Count >= 20) { Error("附件数量", "一次最多 20 个文件。"); break; }
                if (File.Exists(path) && !attachments.Contains(path)) attachments.Add(path);
            } UpdateQueued();
        }
        private void UpdateQueued() { queue.Visible = attachments.Count > 0; root.RowStyles[2].Height = attachments.Count > 0 ? 42 * DeviceDpi / 96F : 0; queued.Text = String.Join("、", attachments.Select(Path.GetFileName)); }
        private void SelectFiles(object sender, EventArgs e) { using (OpenFileDialog dialog = new OpenFileDialog { Title = "发送文件", Multiselect = true, CheckFileExists = true }) if (dialog.ShowDialog(this) == DialogResult.OK) QueueFiles(dialog.FileNames); }
        private void ShowEmoji(object sender, EventArgs e)
        {
            if (emojiPicker == null) emojiPicker = new EmojiPicker(delegate(string text) { if(!InsertEmoji(text)) Error("输入已满","空间不足，未插入表情；请先删去部分文字。"); });
            emojiPicker.ShowFor((Control)sender);
        }
        internal bool InsertEmoji(string text)
        {
            if(editor.TextLength-editor.SelectionLength+text.Length>editor.MaxLength) return false;
            editor.SelectedText=text; editor.Focus(); return true;
        }
        private void ShowMenu(object sender, EventArgs e)
        {
            if (chatMenu == null)
            {
                chatMenu = Theme.Menu(); chatMenu.MinimumSize = new Size((int)(220 * DeviceDpi / 96F), 0);
                chatMenu.Items.Add("发送文件", null, SelectFiles); chatMenu.Items.Add("聊天记录", null, ShowHistory);
                chatMenu.Items.Add(new ToolStripSeparator());
                chatMenu.Items.Add("管理会话…",null,delegate { main.ShowConversationMenu(peer); });
                ToolStripMenuItem font = new ToolStripMenuItem("字号");
                foreach (int size in Enumerable.Range(9, 10)) { int value = size; font.DropDownItems.Add(new ToolStripMenuItem(size.ToString(), null, delegate { main.SetChatFontSize(value); }) { Tag = size }); }
                ToolStripDropDownMenu sizes = (ToolStripDropDownMenu)font.DropDown; sizes.ShowImageMargin = false; sizes.ShowCheckMargin = true; sizes.MinimumSize = new Size((int)(112 * DeviceDpi / 96F), 0);
                Action updateSize = delegate { foreach (ToolStripMenuItem item in font.DropDownItems) item.Checked = (int)item.Tag == network.Settings.ChatFontSize; };
                chatMenu.Opening += delegate { updateSize(); }; font.DropDownOpening += delegate { updateSize(); };
                chatMenu.Items.Add(font);
                chatMenu.Items.Add("外观设置…",null,delegate {main.OpenSettings();});
                chatMenu.Items.Add(new ToolStripSeparator());
                chatMenu.Items.Add("关闭会话", null, delegate { main.CloseChat(this); });
            }
            ShowPopup(chatMenu, (Control)sender);
        }
        // 弹出菜单由会话持有；隐藏会话仍保留它，最终在 Dispose 中统一释放。
        private static void ShowPopup(ContextMenuStrip menu, Control source) { menu.Show(source, new Point(source.Width, source.Height + 4), ToolStripDropDownDirection.BelowLeft); }
        private void ShowHistory(object sender, EventArgs e) { using (HistoryDialog dialog = new HistoryDialog(store, peer)) dialog.ShowDialog(main); }
        private void Error(string title, string text) { using (NoticeDialog dialog = new NoticeDialog(title, text)) dialog.ShowDialog(main); }
        public int PendingFileCount { get { return files.Items.Count; } }
        public bool HasAutomaticFile { get { return receiving == null && files.Items.Cast<ReceivedFile>().Any(f => f.AutomaticPending); } }
        public Task ReceiveNextAutomaticAsync()
        {
            return ReceiveAsync(files.Items.Cast<ReceivedFile>().First(f => f.AutomaticPending), true);
        }
        private void ConfigureFileMenu()
        {
            fileMenu = Theme.Menu();
            ToolStripItem accept = fileMenu.Items.Add("接收文件", null, ReceiveFile);
            ToolStripItem refuse = fileMenu.Items.Add("拒绝本次文件邀请", null, RejectFile);
            ToolStripItem cancel = fileMenu.Items.Add("取消接收", null, delegate { if (receiving != null) receiving.Cancel(); });
            fileMenu.Opening += delegate { accept.Enabled = refuse.Enabled = receiving == null && files.SelectedItem != null; cancel.Enabled = receiving != null; };
            files.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Right && receiving == null) files.SelectedIndex = files.IndexFromPoint(e.Location); };
            files.DoubleClick += delegate { if (receiving == null) ReceiveFile(files, EventArgs.Empty); }; files.ContextMenuStrip = fileMenu;
        }
        private async void ReceiveFile(object sender, EventArgs e)
        {
            if (receiving != null) { receiving.Cancel(); return; }
            ReceivedFile selected = files.SelectedItem as ReceivedFile; if (selected == null) return;
            await ReceiveAsync(selected, false);
        }
        private async Task ReceiveAsync(ReceivedFile selected, bool automatic)
        {
            if (receiving != null || IsDisposed) return;
            selected.AutomaticPending = false; selected.Status = "接收中"; files.SelectedItem = selected;
            CancellationTokenSource cancel = new CancellationTokenSource(); receiving = cancel; activeFile = selected;
            FileState(selected, 0, "连接中 · 点击取消");
            receive.Text = "取消"; receive.Enabled = true; reject.Enabled = false; files.Enabled = false; files.Invalidate();
            try
            {
                string destination = ReceiveStorage.Destination(network.Settings.ReceiveFolder, selected.File.Name);
                selected.PartialPaths.Add(LanService.PartialPath(peer, selected.Packet, selected.File, destination));
                fileStatus.Text = "连接中";
                string saved = await network.ReceiveFileAsync(peer, selected.Packet, selected.File, destination, new Progress<int>(p => { if (!IsDisposed && receiving == cancel && activeFile == selected) FileState(selected, p, "接收中 · 点击取消"); }), cancel.Token, true);
                if (IsDisposed) return;
                selected.Record.LocalFiles = selected.Record.GetLocalFiles().Concat(new[] { saved }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                FileState(selected, 100, "已保存", saved);
                int remaining=files.Items.Cast<ReceivedFile>().Count(f=>f.Packet==selected.Packet)-1;
                selected.Record.State=remaining==0?"已保存":"已接收 "+selected.Record.GetLocalFiles().Length+" / "+(selected.Record.GetLocalFiles().Length+remaining);
                // 完成状态与本地附件回写原邀请，不另造一条聊天消息或重复媒体卡片。
                main.TrySave(selected.Record); history.Delivery(selected.Record);
                files.Items.Remove(selected); if (files.Items.Count > 0 && files.SelectedIndex < 0) files.SelectedIndex = 0;
                UpdateFilesPanel(); fileStatus.Text = "已保存"; main.FileReceiveStatus("文件已保存：" + Path.GetFileName(saved));
            }
            catch (OperationCanceledException) { if (!IsDisposed) { selected.Status = "已取消，可重试"; fileStatus.Text = "已取消，可重新接收"; FileState(selected, 0, "已取消 · 点击重试"); main.TrySave(selected.Record); } }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    selected.Status = "失败，可续传"; fileStatus.Text = "重试时继续已有断点";
                    FileState(selected, null, "接收失败 · 点击续传"); main.TrySave(selected.Record);
                    main.FileReceiveStatus("接收失败：" + selected.File.Name + " — " + error.GetBaseException().Message);
                    // 后台自动接收失败不能弹窗抢焦点，也不能无限重试同一个失败任务。
                    if (!automatic) Error("接收失败", error.GetBaseException().Message);
                }
            }
            finally
            {
                cancel.Dispose(); receiving = null; activeFile = null;
                if (!IsDisposed) { receive.Text = "接收"; receive.Enabled = reject.Enabled = files.SelectedItem != null; files.Enabled = true; files.Invalidate(); main.ReceiveAutomatically(); }
            }
        }
        private void RejectFile(object sender, EventArgs e)
        {
            ReceivedFile selected = files.SelectedItem as ReceivedFile; if (selected == null) return;
            try
            {
                network.RejectFiles(peer, selected.Packet);
                foreach (ReceivedFile item in files.Items.Cast<ReceivedFile>().Where(f => f.Packet == selected.Packet).ToList())
                { foreach (string part in item.PartialPaths) if (File.Exists(part)) File.Delete(part); FileState(item, 0, "已拒绝"); files.Items.Remove(item); }
                main.TrySave(selected.Record);
                UpdateFilesPanel(); fileStatus.Text = "已拒绝";
            }
            catch (Exception error) { Error("拒绝失败", error.Message); }
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; main.CloseChat(this); return; } if (receiving != null) receiving.Cancel(); base.OnFormClosing(e); }
        private void FileState(ReceivedFile item, int? percent, string status, string path = null)
        {
            FileTransferState state = item.Record.Transfers == null ? null : item.Record.Transfers.FirstOrDefault(f => f.Id == item.File.Id);
            if (state == null) return;
            if (percent.HasValue) state.Percent = percent.Value; state.Status = status; if (path != null) state.LocalPath = path;
            history.Delivery(item.Record);
        }
        protected override void Dispose(bool disposing) { if (disposing && !IsDisposed) { SaveDraft(); draftTimer.Dispose(); if (emojiPicker != null) emojiPicker.Dispose(); if (chatMenu != null) chatMenu.Dispose(); if (fileMenu != null) fileMenu.Dispose(); if (receiving != null) receiving.Cancel(); if (avatar != null && avatar.Image != null) avatar.Image.Dispose(); } base.Dispose(disposing); if(disposing&&chatFont!=null){chatFont.Dispose();chatFont=null;} }
    }
}
