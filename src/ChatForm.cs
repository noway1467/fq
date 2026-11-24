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
        private sealed class ReceivedFile { public long Packet; public Attachment File; public ChatRecord Record; public readonly HashSet<string> PartialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public readonly Dictionary<string, string> PartialRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public bool AutomaticPending = true, OfferCanceled, Rejected, CancelRequested; public string Status = "待接收"; public override string ToString() { return File.Name; } }
        private readonly Peer peer;
        private readonly LanService network;
        private readonly LocalStore store;
        private readonly MainForm main;
        private readonly BubbleHistory history = new BubbleHistory();
        private readonly Button returnToLatest;
        private bool showingArchive;
        private int locationVersion;
        private readonly ChatEditor editor = new ChatEditor();
        private readonly ListBox files = new ListBox();
        private readonly Label queued = Theme.Label("", Theme.Small, Theme.Muted);
        private readonly Label fileStatus = Theme.Label("", Theme.Small, Theme.Muted);
        private readonly Button emojiButton, attachButton, sendButton;
        private readonly PictureBox avatar;
        private readonly Label peerName = Theme.Label("", Theme.Title, Theme.Ink);
        private readonly Label peerStatus = Theme.Label("", Theme.Small, Theme.Muted);
        private string avatarIdentity;
        private string avatarValue;
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
            history.SetAvatars(peer.Id, peer.Avatar, "device:" + network.Settings.DeviceId, network.Settings.Avatar);
        }
        private readonly TableLayoutPanel root, head, compose, queue;
        private readonly Panel middle;
        private readonly DropOverlay dropOverlay = new DropOverlay();
        private readonly List<string> attachments = new List<string>();
        private readonly HashSet<string> pastedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource receiving;
        private TaskCompletionSource<bool> receivingFinished;
        private ReceivedFile activeFile;
        private bool pauseRequested;
        private readonly System.Windows.Forms.Timer draftTimer = new System.Windows.Forms.Timer { Interval = 500 };
        private readonly ToolTip headTips = new ToolTip();
        private ContextMenuStrip chatMenu;
        private EmojiPicker emojiPicker;
        private readonly RoundedPanel inputShell = new RoundedPanel { Fill = Theme.Ice, Radius = 22, Padding = new Padding(16, 12, 16, 12) };
        public ChatForm(Peer peer, LanService network, LocalStore store, MainForm main)
        {
            this.peer = peer; this.network = network; this.store = store; this.main = main;
            Theme.Apply(this); DoubleBuffered = true; Text = peer.Nickname; FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(760, 720); MinimumSize = Size.Empty;
            root = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, BackColor = Theme.Surface };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); Controls.Add(root);
            head = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Padding = new Padding(16, 8, 12, 8), Margin = Padding.Empty, BackColor = Theme.Surface };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); for (int i = 0; i < 3; i++) head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            avatar = new PictureBox { Image = Theme.ContactAvatar(46, peer.Id, peer.Online, peer.Avatar), AccessibleName = "聊天联系人头像", Size = new Size(56, 56), Margin = Padding.Empty, SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left }; head.Controls.Add(avatar, 0, 0);
            TableLayoutPanel identity = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty }; identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); identity.RowStyles.Add(new RowStyle(SizeType.Percent, 56)); identity.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            peerName.Dock = DockStyle.Fill; peerName.AutoEllipsis = true; peerName.AccessibleName = "聊天联系人昵称"; identity.Controls.Add(peerName, 0, 0);
            peerStatus.Dock = DockStyle.Fill; peerStatus.AccessibleName = "聊天联系人状态"; identity.Controls.Add(peerStatus, 0, 1); head.Controls.Add(identity, 1, 0);
            avatarIdentity = peer.Id; avatarValue = peer.Avatar; avatarOnline = peer.Online; RefreshPeer();
            avatar.Cursor = Cursors.Hand;
            avatar.Click += delegate { main.ShowContactProfile(peer); };
            Button transferButton = Theme.IconButton("transfer", "传输任务", delegate { main.OpenTransfers(); }, false); transferButton.Dock = DockStyle.Fill; transferButton.Margin = Padding.Empty; head.Controls.Add(transferButton, 2, 0);
            Button search = Theme.IconButton("search", "聊天记录", ShowHistory, false); search.Dock = DockStyle.Fill; search.Margin = Padding.Empty; head.Controls.Add(search, 3, 0);
            Button menu = Theme.IconButton("more", "会话菜单", ShowMenu, false); menu.Dock = DockStyle.Fill; menu.Margin = Padding.Empty; head.Controls.Add(menu, 4, 0);
            foreach (Button button in new[] { transferButton, search, menu }) headTips.SetToolTip(button, button.AccessibleName);
            root.Controls.Add(head, 0, 0);
            head.Paint += delegate(object sender, PaintEventArgs e) { using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 0, head.Height - 1, head.Width, head.Height - 1); };
            middle = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Wallpaper };
            history.RetryRequested += RetryMessage;
            history.CanCancelSend = record => record != null && record.Outgoing && network.CanCancelOutgoing(peer.Id, record.Packet);
            history.CancelSendRequested += CancelSendMessage;
            history.AvatarClicked += outgoing => { if (outgoing) main.OpenSettings(); else main.ShowContactProfile(peer); };
            history.FileRequested += async (record, id) => {
                ReceivedFile item = files.Items.Cast<ReceivedFile>().FirstOrDefault(f => f.Packet == record.Packet && f.File.Id == id);
                if (item == null) {
                    FileTransferState state = (record.Transfers ?? new FileTransferState[0]).FirstOrDefault(f => f.Id == id);
                    if (!record.Outgoing && state != null && state.Status == "本机文件已删除") QueueTask(record, id);
                    else main.FileReceiveStatus("此文件邀请已结束；未保存的附件请让对方重新发送。");
                    return;
                }
                if (receiving != null)
                {
                    // 事件处理器没有上层调用者；断点清理失败必须在此收口，不能成为未处理异常。
                    if (activeFile == item)
                        try { await CancelReceiveTask(record, id); }
                        catch (Exception error) { if (IsDisposed) main.FileReceiveStatus("取消接收未完成：" + error.Message); else Error("取消接收未完成", error.Message); }
                    return;
                }
                await ReceiveAsync(item, false);
            };
            history.ForwardRequested += delegate(ChatRecord record) { main.Forward(record); };
            history.DeleteRequested += DeleteMessages;
            history.DeleteFileRequested += (record,path)=>main.DeleteReceivedFile(peer,record,path);
            history.DeleteFilesRequested += records => main.DeleteReceivedFiles(peer, records);
            history.Dock = DockStyle.Fill; history.Margin = Padding.Empty; middle.Controls.Add(history);
            returnToLatest=Theme.Button("回到最新消息",delegate {LoadLatest();},false);
            returnToLatest.Dock=DockStyle.Bottom;returnToLatest.Visible=false;middle.Controls.Add(returnToLatest);
            dropOverlay.Dock = DockStyle.Fill; middle.Controls.Add(dropOverlay); dropOverlay.BringToFront();
            // ListBox 仅保留旧接收状态机的任务集合，不再加入聊天控件树。
            files.Visible = false;
            TableLayoutPanel selectionBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, ColumnCount = 5, RowCount = 1, Padding = new Padding(12, 8, 12, 8), Visible = false, BackColor = Theme.Surface, AccessibleName = "消息多选操作栏" };
            selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach(int width in new[] { 72, 92, 76, 72 }) selectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            selectionBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label selectedCount = Theme.Label("已选 0 条", Theme.Section, Theme.Ink); selectedCount.Dock = DockStyle.Fill; selectedCount.AutoEllipsis = true; selectionBar.Controls.Add(selectedCount, 0, 0);
            Button selectAll = Theme.Button("全选", delegate { history.SelectAllMessages(); }, false);
            Button deleteSelected = Theme.Button("删除", delegate { history.DeleteMarkedOrSelected(); }, false); deleteSelected.ForeColor = Theme.Danger;
            Button deleteFiles = Theme.Button("删除文件", delegate { history.DeleteSelectedFiles(); }, false); deleteFiles.AccessibleName = "批量删除本机文件"; deleteFiles.ForeColor = Theme.Danger;
            Button cancelSelection = Theme.Button("取消", delegate { history.EndSelection(); }, false);
            int selectionColumn = 1;
            foreach(Button button in new[] { selectAll, deleteFiles, deleteSelected, cancelSelection }) { button.Dock = DockStyle.Fill; button.Margin = new Padding(4, 0, 0, 0); selectionBar.Controls.Add(button, selectionColumn++, 0); }
            history.SelectionChanged += delegate { selectionBar.Visible = history.SelectionMode; selectedCount.Text = "已选 " + history.SelectionCount + " 条"; deleteSelected.Enabled = history.SelectionCount > 0; deleteFiles.Enabled = history.SelectionCount > 0 && history.HasSelectedReceivedFiles; };
            middle.Controls.Add(selectionBar);
            root.Controls.Add(middle, 0, 1);
            queue = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(18, 1, 10, 1), Margin = Padding.Empty, Visible = false }; queue.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); queue.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38)); queue.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            queued.Dock = DockStyle.Fill; queued.AutoEllipsis = true; queue.Controls.Add(queued, 0, 0); Button remove = Theme.IconButton("close", "移除附件", delegate { ClearPastedImages(); attachments.Clear(); UpdateQueued(); }, false); remove.Dock = DockStyle.Fill; remove.Margin = Padding.Empty; queue.Controls.Add(remove, 1, 0); root.Controls.Add(queue, 0, 2);
            compose = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(10, 9, 14, 9), Margin = Padding.Empty, BackColor = Theme.Surface };
            compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); compose.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58)); compose.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            emojiButton = Theme.IconButton("smile", "表情", ShowEmoji, false); emojiButton.Anchor = AnchorStyles.None; emojiButton.Size = new Size(40, 40); emojiButton.Margin = Padding.Empty; compose.Controls.Add(emojiButton, 0, 0);
            inputShell.Dock = DockStyle.Fill; inputShell.Margin = new Padding(2, 1, 4, 1); inputShell.Cursor = Cursors.IBeam;
            inputShell.MouseDown += delegate { editor.Focus(); };
            inputShell.MouseWheel += delegate(object sender, MouseEventArgs e) { editor.ScrollByWheel(e.Delta); };
            editor.BorderStyle = BorderStyle.None; editor.Dock = DockStyle.Fill; editor.BackColor = Theme.Ice; editor.ForeColor = Theme.Ink; editor.Font = new Font(Theme.Body.FontFamily, 10F); editor.MaxLength = MessageText.InputLimit; editor.AccessibleName = "消息输入框"; editor.DetectUrls = false; editor.ScrollBars = RichTextBoxScrollBars.None; editor.Placeholder = "输入消息…";
            Theme.AttachEditMenu(editor, PasteClipboard);
            editor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (editor.IsComposing) return;
                if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) { e.SuppressKeyPress = true; PasteClipboard(); }
                else if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (e.Shift) { if (!editor.ReadOnly) editor.SelectedText = "\n"; }
                    else SendMessage();
                }
            }; inputShell.Controls.Add(editor); compose.Controls.Add(inputShell, 1, 0);
            draftTimer.Tick += delegate { draftTimer.Stop(); SaveDraft(); };
            editor.HandleCreated += delegate { ResizeComposer(); };
            editor.ContentsResized += delegate { ResizeComposer(); };
            editor.TextChanged += delegate { draftTimer.Stop(); if (editor.IsComposing) return; draftTimer.Start(); ResizeComposer(); UpdateQueued(); };
            editor.CompositionEnded += delegate { draftTimer.Stop(); draftTimer.Start(); ResizeComposer(); UpdateQueued(); };
            editor.Resize += delegate { ResizeComposer(); };
            attachButton = Theme.IconButton("clip", "发送文件", SelectFiles, false); attachButton.Anchor = AnchorStyles.None; attachButton.Size = new Size(40, 40); attachButton.Margin = Padding.Empty; compose.Controls.Add(attachButton, 2, 0);
            sendButton = Theme.IconButton("send", "发送消息", delegate { SendMessage(); }, true); sendButton.Anchor = AnchorStyles.None; sendButton.Size = new Size(52, 40); sendButton.Margin = Padding.Empty; compose.Controls.Add(sendButton, 3, 0); root.Controls.Add(compose, 0, 3);
            compose.Paint += delegate(object sender, PaintEventArgs e) { using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 0, 0, compose.Width, 0); };
            editor.EnableAutoDragDrop = false;
            RegisterFileDrop(this);
            ApplyAppearance();
            try { var records = store.History(peer.Id, 150).Select(record => { ChatRecord outgoing=record.Outgoing?network.OutgoingRecord(peer.Id,record.Packet):null;if(outgoing!=null&&record.State=="已送达")outgoing.State=record.State;return outgoing??record; }).ToList(); history.SetRecords(records); editor.Text = store.LoadDraft(peer.Id);
                foreach(ChatRecord record in records.Where(r=>!r.Outgoing))foreach(FileTransferState file in record.Transfers??new FileTransferState[0]) {
                    if(files.Items.Count>=1000)break;
                    if(file.Status=="已拒绝"||file.Status=="对方已取消发送"||file.Status=="本机文件已删除"||file.Status=="已保存"||!String.IsNullOrEmpty(file.LocalPath)||!SecureIdentity.ValidFingerprint(file.Sha256))continue;
                    file.Status=file.Status=="已暂停"?"已暂停":"已恢复 · 点击续传";
                    files.Items.Add(new ReceivedFile{Packet=record.Packet,Record=record,File=new Attachment{Id=file.Id,Name=file.Name,Size=file.Size,Modified=file.Modified,Sha256=file.Sha256,RelativePath=file.RelativePath},AutomaticPending=false,Status=file.Status});
                }
                if(files.Items.Count>0)files.SelectedIndex=0;
            } catch (Exception e) { Error("本地记录", e.Message); }
        }
        private bool resizingComposer;
        private float composeScale;
        private void ResizeComposer()
        {
            if(resizingComposer || IsDisposed || Disposing || editor.IsDisposed || editor.Disposing || editor.IsComposing || root==null || root.RowStyles.Count<4 || !editor.IsHandleCreated)return;
            resizingComposer=true;
            try {
                float scale; using (Graphics g = editor.CreateGraphics()) scale = Math.Max(DeviceDpi, g.DpiX) / 96F;
                if (Math.Abs(composeScale - scale) > .01F)
                {
                    composeScale = scale;
                    int iconButton = (int)Math.Round(40 * scale), sendWidth = (int)Math.Round(52 * scale), sendHeight = (int)Math.Round(40 * scale);
                    compose.Padding = new Padding((int)Math.Round(10 * scale), (int)Math.Round(9 * scale), (int)Math.Round(14 * scale), (int)Math.Round(9 * scale));
                    compose.ColumnStyles[0].Width = (int)Math.Round(44 * scale);
                    compose.ColumnStyles[2].Width = (int)Math.Round(44 * scale);
                    compose.ColumnStyles[3].Width = (int)Math.Round(58 * scale);
                    inputShell.Padding = new Padding((int)Math.Round(16 * scale), (int)Math.Round(12 * scale), (int)Math.Round(16 * scale), (int)Math.Round(12 * scale));
                    emojiButton.Size = new Size(iconButton, iconButton);
                    attachButton.Size = new Size(iconButton, iconButton);
                    sendButton.Size = new Size(sendWidth, sendHeight);
                }
                int lines=Math.Max(1,editor.GetLineFromCharIndex(editor.TextLength)+1);
                // Font.Height 不包含 RichEdit 实际 DPI/字体回退的行高；高度不足会让每次输入先滚走首行再被拉回。
                int contentHeight=editor.ContentHeight;
                if(lines>5)contentHeight=editor.GetPositionFromCharIndex(editor.GetFirstCharIndexFromLine(5)).Y-editor.GetPositionFromCharIndex(0).Y;
                int padding=compose.Padding.Vertical+inputShell.Margin.Vertical+inputShell.Padding.Vertical;
                int height=Math.Max((int)Math.Round(82*scale),contentHeight+padding);
                float nextHeight=Math.Min(height,Math.Max((int)Math.Round(82*scale),ClientSize.Height/3));
                if(Math.Abs(root.RowStyles[3].Height-nextHeight)>.5F) root.RowStyles[3].Height=nextHeight;
            } finally {resizingComposer=false;}
        }
        public void SaveDraft() { draftTimer.Stop(); if (editor.IsComposing) { if (!Disposing) draftTimer.Start(); return; } main.SaveDraft(peer.Id, editor.Text); }
        public void RefreshPeer()
        {
            history.SetAvatars(peer.Id, peer.Avatar, "device:" + network.Settings.DeviceId, network.Settings.Avatar);
            Text = peer.DisplayName; peerName.Text = peer.DisplayName;
            peerStatus.Text = (peer.Online ? "在线" : "离线") + (peer.Fingerprint==null?" · 旧版明文":" · TLS 加密") + (peer.DuplicateName ? peer.NameSuffix+" · "+peer.Endpoint : "");
            peerStatus.ForeColor = peer.Online ? Theme.Online : Theme.Muted;
            peerName.AccessibleDescription=peer.DisplayName+" · "+peer.Id;
            avatar.AccessibleDescription = peer.Online ? "在线" : "离线";
            if (avatarIdentity != peer.Id || avatarOnline != peer.Online || avatarValue != peer.Avatar)
            {
                Image old = avatar.Image; avatar.Image = Theme.ContactAvatar(46, peer.Id, peer.Online, peer.Avatar);
                avatarIdentity = peer.Id; avatarValue = peer.Avatar; avatarOnline = peer.Online; if (old != null) old.Dispose();
            }
        }
        public void FocusEditor() { editor.Focus(); }
        public void Append(ChatRecord record) {
            if(showingArchive){if(record.Outgoing)LoadLatest();return;}
            history.Append(record);
        }
        private void LoadLatest() {
            locationVersion++;
            var records=store.History(peer.Id,150).Select(record=>{ChatRecord outgoing=record.Outgoing?network.OutgoingRecord(peer.Id,record.Packet):null;if(outgoing!=null&&record.State=="已送达")outgoing.State=record.State;return outgoing??record;});
            history.SetRecords(records);showingArchive=false;returnToLatest.Visible=false;
        }
        public void ClearHistory() { locationVersion++;showingArchive=false;returnToLatest.Visible=false;history.Clear(); }
        private void DeleteMessages(ChatRecord[] records) { main.DeleteMessages(peer, records); }
        private void CancelSendMessage(ChatRecord record)
        {
            if(record==null||!record.Outgoing||!network.CanCancelOutgoing(peer.Id,record.Packet))return;
            using(ConfirmDialog confirm=new ConfirmDialog("取消发送？",record.HasAttachments?"将停止发送并撤销对方尚未接收的文件邀请；本机原文件会保留。":"将停止继续发送这条尚未确认送达的消息。","取消发送",true)) {
                if(confirm.ShowDialog(main)!=DialogResult.OK)return;
                try { if(!network.CancelOutgoing(peer.Id,record.Packet,record.State,true))main.FileReceiveStatus("此消息已发送完成或已结束，无需取消。"); }
                catch(Exception error) { Error("取消发送失败",error.Message); }
            }
        }
        internal void RemoveMessages(ChatRecord[] records) { history.RemoveRecords(records); }
        internal void EndSelection() { history.EndSelection(); }
        internal bool HasPendingMessages(ChatRecord[] records) {
            return records.Any(r => (!r.Outgoing && files.Items.Cast<ReceivedFile>().Any(f => f.Packet == r.Packet)) ||
                (r.Outgoing&&network.HasActiveOutgoingTransfer(peer.Id,r.Packet)) ||
                (r.Transfers ?? new FileTransferState[0]).Any(f => ((f.DisplayStatus() ?? "").Contains("中") && !(f.DisplayStatus() ?? "").Contains("中断") && f.Percent < 100)));
        }
        public void Delivery(ChatRecord record) { history.Delivery(record); }
        internal bool TaskReceiving(long packet, int fileId) { return receiving != null && activeFile != null && activeFile.Packet == packet && activeFile.File.Id == fileId; }
        internal void PauseTask(ChatRecord record, int fileId)
        {
            if(record.Outgoing||record.PeerId!=peer.Id)return;
            ReceivedFile item = files.Items.Cast<ReceivedFile>().FirstOrDefault(f => f.Packet == record.Packet && f.File.Id == fileId);
            if(item!=null)record=item.Record;
            FileTransferState state=(record.Transfers??new FileTransferState[0]).FirstOrDefault(f=>f.Id==fileId);
            if(state==null||!String.IsNullOrEmpty(state.LocalPath)||new[]{"已拒绝","对方已取消发送","本机文件已删除","已保存"}.Contains(state.Status)||item!=null&&(item.Rejected||item.CancelRequested))return;
            if(item==null){item=new ReceivedFile{Packet=record.Packet,Record=record,File=new Attachment{Id=state.Id,Name=state.Name,Size=state.Size,Modified=state.Modified,Sha256=state.Sha256,RelativePath=state.RelativePath}};files.Items.Add(item);}
            item.AutomaticPending = false;
            if (TaskReceiving(record.Packet, fileId)) { pauseRequested = true; receiving.Cancel(); }
            else { item.Status = "已暂停"; FileState(item, null, "已暂停"); main.TrySave(item.Record); }
        }
        internal async Task CancelReceiveTask(ChatRecord record, int fileId)
        {
            if(record.Outgoing||record.PeerId!=peer.Id)return;
            ReceivedFile item=files.Items.Cast<ReceivedFile>().FirstOrDefault(f=>f.Packet==record.Packet&&f.File.Id==fileId);
            if(item!=null)record=item.Record;
            FileTransferState state=(record.Transfers??new FileTransferState[0]).FirstOrDefault(f=>f.Id==fileId);
            if(state==null||!String.IsNullOrEmpty(state.LocalPath)||new[]{"已保存","本机文件已删除","已拒绝","对方已取消发送"}.Contains(state.Status))return;
            if(item==null){item=new ReceivedFile{Packet=record.Packet,Record=record,File=new Attachment{Id=state.Id,Name=state.Name,Size=state.Size,Modified=state.Modified,Sha256=state.Sha256,RelativePath=state.RelativePath}};files.Items.Add(item);}
            if(item.Rejected||item.CancelRequested)return;
            item.AutomaticPending=false;item.CancelRequested=true;
            if(!main.InvokeRequired&&SynchronizationContext.Current==null)SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try
            {
                if(TaskReceiving(record.Packet,fileId))
                {
                    Task finished=receivingFinished.Task;pauseRequested=false;receiving.Cancel();
                    // 下载任务完成后独占句柄才释放，不能在 Cancel() 后立刻删除断点。
                    await finished;
                }
                if(IsDisposed||item.Rejected||item.OfferCanceled||!String.IsNullOrEmpty(state.LocalPath)||state.Status=="已保存")return;
                DeletePartials(item);item.Status="已取消，可重试";
                FileState(item,0,"已取消 · 点击重试");main.TrySave(item.Record);
                main.FileReceiveStatus("已取消接收并清理断点。");
            }
            finally{item.CancelRequested=false;}
        }
        internal async Task RejectTransfer(ChatRecord record)
        {
            if(record.Outgoing||record.PeerId!=peer.Id)return;
            ReceivedFile[] pending=files.Items.Cast<ReceivedFile>().Where(f=>f.Packet==record.Packet).ToArray();
            record=pending.Select(f=>f.Record).FirstOrDefault()??record;
            foreach(ReceivedFile item in pending){item.AutomaticPending=false;item.Rejected=true;}
            if(!main.InvokeRequired&&SynchronizationContext.Current==null)SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if(receiving!=null&&activeFile!=null&&activeFile.Packet==record.Packet)
            {
                activeFile.Rejected=true;Task finished=receivingFinished.Task;pauseRequested=false;receiving.Cancel();
                await finished;
            }
            if(IsDisposed)return;
            bool hasSaved=record.GetLocalFiles().Length>0||(record.Transfers??new FileTransferState[0]).Any(f=>!String.IsNullOrEmpty(f.LocalPath)||f.Status=="已保存"||f.Status=="本机文件已删除"||f.SavedBefore);
            Exception cleanupError=null;
            foreach(FileTransferState state in record.Transfers??new FileTransferState[0])
            {
                if(!String.IsNullOrEmpty(state.LocalPath)||state.Status=="已保存"||state.Status=="本机文件已删除")continue;
                ReceivedFile item=pending.FirstOrDefault(f=>f.File.Id==state.Id)??new ReceivedFile{Packet=record.Packet,Record=record,File=new Attachment{Id=state.Id,Name=state.Name,Size=state.Size,Modified=state.Modified,Sha256=state.Sha256,RelativePath=state.RelativePath}};
                try{DeletePartials(item);}catch(Exception error){if(cleanupError==null)cleanupError=error;}
                // 重新接收途中拒绝：此前已保存过的附件回到删除态，不抹掉接收历史。
                state.LocalPath=null;state.Percent=0;state.Status=state.SavedBefore?"本机文件已删除":"已拒绝";state.BytesPerSecond=0;state.Error=null;state.MarkCurrent();
            }
            foreach(ReceivedFile item in pending)files.Items.Remove(item);
            if(files.Items.Count>0&&files.SelectedIndex<0)files.SelectedIndex=0;
            if(!hasSaved)record.State="已拒绝";
            main.TrySave(record);history.Delivery(record);fileStatus.Text="已拒绝";
            main.FileReceiveStatus("已拒绝这批尚未保存的附件。");
            try{network.RejectFiles(peer,record.Packet);}
            catch(Exception error){main.FileReceiveStatus("已本地拒收并清理断点；拒绝通知未送达："+error.Message);}
            if(cleanupError!=null)throw new IOException("已本地拒收；断点清理未完成："+cleanupError.Message,cleanupError);
        }
        internal void QueueTask(ChatRecord record, int fileId)
        {
            if(record.Outgoing||record.PeerId!=peer.Id)return;
            ReceivedFile item = files.Items.Cast<ReceivedFile>().FirstOrDefault(f => f.Packet == record.Packet && f.File.Id == fileId);
            if(item!=null)record=item.Record;
            FileTransferState state = (record.Transfers ?? new FileTransferState[0]).FirstOrDefault(f => f.Id == fileId);
            if (state == null || !String.IsNullOrEmpty(state.LocalPath) || new[] { "已拒绝", "对方已取消发送", "已保存" }.Contains(state.Status) || item!=null&&(item.Rejected||item.CancelRequested)) return;
            if (item == null) {
                item = new ReceivedFile { Packet = record.Packet, Record = record, File = new Attachment { Id = state.Id, Name = state.Name, Size = state.Size, Modified = state.Modified, Sha256 = state.Sha256, RelativePath = state.RelativePath } };
                files.Items.Add(item);
            }
            if(TaskReceiving(record.Packet,fileId))return;
            if(state.Status=="本机文件已删除")state.SavedBefore=true;
            item.AutomaticPending = true; item.Status = "排队中"; FileState(item, null, "排队中"); main.TrySave(item.Record);
            main.ReceiveAutomatically();
        }
        public void AddAttachments(IncomingMessage message)
        {
            foreach (Attachment file in message.Files) {ReceivedFile restored=files.Items.Cast<ReceivedFile>().FirstOrDefault(f=>f.Packet==message.Record.Packet&&f.File.Id==file.Id);if(restored!=null){restored.AutomaticPending=true;continue;}if (files.Items.Count >= 1000) { fileStatus.Text = "待接收文件已达上限"; break; } files.Items.Add(new ReceivedFile { Packet = message.Record.Packet, File = file, Record = message.Record }); }
            if (files.Items.Count > 0 && files.SelectedIndex < 0) files.SelectedIndex = 0;
            main.ReceiveAutomatically();
        }
        private bool sending;
        private async void SendMessage()
        {
            if (sending || editor.IsComposing || String.IsNullOrWhiteSpace(editor.Text) && attachments.Count == 0) return;
            sending=true;sendButton.Enabled=false;string text=editor.Text;string[] selected=attachments.ToArray();
            try {
                if(selected.Any(Directory.Exists)||selected.Length>20||MessageText.AsFile(text)&&selected.Length>=20)await Task.Run(()=>network.SendBatchContent(peer,text,selected,store.Root,(record,count,total)=>main.BeginInvoke((Action)(()=>{main.Sent(peer,record);if(!IsDisposed){Append(record);Delivery(record);fileStatus.Text="邀请已发送 "+count+" / "+total;}}))));
                else {ChatRecord record=await Task.Run(()=>network.SendContent(peer,text,selected,store.Root));main.Sent(peer,record);if(!IsDisposed){Append(record);Delivery(record);}}
                if(IsDisposed)return;pastedImages.ExceptWith(selected);if(editor.Text==text)editor.Clear();SaveDraft();attachments.RemoveAll(p=>selected.Contains(p));UpdateQueued();editor.Focus();history.ScrollToLatest(); }
            catch (Exception e) { if(!IsDisposed)Error("发送失败", e.Message); }
            finally {sending=false;if(!IsDisposed)sendButton.Enabled=true;}
        }
        internal async void ForwardContent(string text, string[] paths)
        {
            // 转发独立发送，不读取或清空目标会话正在编辑的草稿和附件。
            if(sending)return;sending=true;sendButton.Enabled=false;
            try {
                if(paths.Any(Directory.Exists)||paths.Length>20||MessageText.AsFile(text)&&paths.Length>=20)await Task.Run(()=>network.SendBatchContent(peer,text,paths,store.Root,(record,count,total)=>main.BeginInvoke((Action)(()=>{main.Sent(peer,record);if(!IsDisposed){Append(record);Delivery(record);history.ScrollToLatest();}}))));
                else {ChatRecord record=await Task.Run(()=>network.SendContent(peer,text,paths,store.Root));main.Sent(peer,record);if(!IsDisposed){Append(record);Delivery(record);history.ScrollToLatest();}}
            }catch(Exception error){if(!IsDisposed)Error("发送失败",error.GetBaseException().Message);}
            finally{sending=false;if(!IsDisposed)sendButton.Enabled=true;}
        }
        private async void RetryMessage(ChatRecord previous)
        {
            if(sending||!(previous.State??"").Contains("未确认")) return;
            sending=true;sendButton.Enabled=false;
            try {
                string[] paths=previous.GetLocalFiles();
                if(paths.Any(p=>!File.Exists(p))) throw new IOException("原附件已移动或删除，请重新选择文件发送。");
                // 复用消息号和原记录，ACK 只更新原气泡，不再追加第二条消息。
                await Task.Run(()=>network.RetryMessage(peer,previous));main.TrySave(previous);if(!IsDisposed){Delivery(previous);history.ScrollToLatest();}
            } catch(Exception e) {if(!IsDisposed)Error("重发失败",e.Message);}
            finally{sending=false;if(!IsDisposed)sendButton.Enabled=true;}
        }
        private void QueueFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (attachments.Count >= 1000) { Error("附件数量", "单批最多 1000 项，请分批发送。"); break; }
                if ((File.Exists(path)||Directory.Exists(path)) && !attachments.Contains(path)) attachments.Add(path);
            } UpdateQueued();
        }
        internal void QueueSharedFiles(IEnumerable<string> paths){QueueFiles(paths);}
        private void PasteClipboard()
        {
            if (editor.ReadOnly) return;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    if(attachments.Count>=20)throw new InvalidOperationException("一次最多 20 个文件，请先发送或移除附件。");
                    string folder = Path.Combine(store.Root, "pasted-images");
                    Directory.CreateDirectory(folder);
                    long used=Directory.GetFiles(folder).Sum(p=>new FileInfo(p).Length);
                    const long limit=128L*1024*1024;
                    using(Image image=Clipboard.GetImage()) {
                        if(image==null)return;
                        if((long)image.Width*image.Height>40000000)throw new IOException("图片超过 4000 万像素，请保存为文件后发送。");
                        using(MemoryStream encoded=new MemoryStream()) {
                            image.Save(encoded,System.Drawing.Imaging.ImageFormat.Png);
                            if(used+encoded.Length>limit)throw new IOException("粘贴图片缓存已达 128 MiB，请将图片保存为文件后发送。已有图片保留于：\n"+folder);
                            string path=Path.Combine(folder,"粘贴图片-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".png");
                            try {using(FileStream output=new FileStream(path,FileMode.CreateNew,FileAccess.Write)){encoded.Position=0;encoded.CopyTo(output);}pastedImages.Add(path);attachments.Add(path);}
                            catch {if(File.Exists(path))File.Delete(path);throw;}
                        }
                    }
                    UpdateQueued();
                    return;
                }
                Theme.PasteText(editor);
            }
            catch (Exception error) { Error("粘贴失败", error.Message); }
        }
        private void ClearPastedImages()
        {
            // 只清理本会话尚未发送的副本；已发图片保留给文件服务、历史打开和向外拖动。
            foreach(string path in pastedImages.ToArray())try {File.Delete(path);pastedImages.Remove(path);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        private void RegisterFileDrop(Control target)
        {
            target.AllowDrop=true;target.DragEnter+=FileDragEnter;target.DragOver+=FileDragEnter;target.DragLeave+=FileDragLeave;target.DragDrop+=FileDropped;
            foreach(Control child in target.Controls)RegisterFileDrop(child);
        }
        private void FileDragEnter(object sender,DragEventArgs e)
        {
            bool copy=!history.IsDraggingFile&&e.Data!=null&&e.Data.GetDataPresent(DataFormats.FileDrop)&&(e.AllowedEffect&DragDropEffects.Copy)!=0;
            e.Effect=copy?DragDropEffects.Copy:DragDropEffects.None;
            if(copy&&sender!=dropOverlay&&IsHandleCreated)dropOverlay.Visible=true;
        }
        private void FileDragLeave(object sender,EventArgs e)
        {
            // 子控件之间移动也会触发 DragLeave；仅当指针真的离开窗口时才收起提示。
            if(sender==dropOverlay||!ClientRectangle.Contains(PointToClient(Cursor.Position)))dropOverlay.Visible=false;
        }
        private void FileDropped(object sender,DragEventArgs e)
        {
            // RichEdit 若收到 Copy 会继续插入 OLE 占位空格；文件已由本程序处理，拒绝它的默认插入。
            e.Effect=DragDropEffects.None; dropOverlay.Visible=false;
            if(history.IsDraggingFile||e.Data==null||(e.AllowedEffect&DragDropEffects.Copy)==0)return;
            string[] paths=e.Data.GetData(DataFormats.FileDrop) as string[];
            if(paths==null||paths.Length==0)return;
            if(!network.Settings.AutoSendDroppedFiles){QueueFiles(paths);if(!(sender is RichTextBox))e.Effect=DragDropEffects.Copy;return;}
            // 自动发送只使用这次拖入的附件，不把未完成的文字和已排队附件一起发出去。
            try {ForwardContent("",paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());if(!(sender is RichTextBox))e.Effect=DragDropEffects.Copy;}
            catch(Exception error){Error("拖入文件发送失败",error.Message);}
        }
        private void UpdateQueued() { bool longText=MessageText.AsFile(editor.Text);bool show=attachments.Count > 0 || longText;queue.Visible = show; root.RowStyles[2].Height = show ? 40 * DeviceDpi / 96F : 0; queued.Text = (longText?"长文本将作为 TXT 附件发送  ":"")+String.Join("、", attachments.Select(Path.GetFileName)); }
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
                chatMenu = Theme.Menu(); chatMenu.MinimumSize = new Size((int)(236 * DeviceDpi / 96F), 0);
                chatMenu.Items.Add("发送文件", null, SelectFiles); chatMenu.Items.Add("聊天记录", null, ShowHistory);
                chatMenu.Items.Add("发送文件夹…",null,delegate {using(FolderBrowserDialog dialog=new FolderBrowserDialog{Description="选择发送的文件夹，保留内部目录结构"})if(dialog.ShowDialog(this)==DialogResult.OK)QueueFiles(new[]{dialog.SelectedPath});});
                chatMenu.Items.Add("浏览共享文件…", null, delegate { main.OpenShared(peer); });
                chatMenu.Items.Add("控制 Android 手机…", null, delegate { main.OpenPhone(peer); });
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
        private async void ShowHistory(object sender, EventArgs e)
        {
            int request=++locationVersion;
            using (HistoryDialog dialog = new HistoryDialog(store, peer, main))
                if(dialog.ShowDialog(main)==DialogResult.OK&&dialog.LocatedRecord!=null) {
                    main.OpenChat(peer);
                    try {
                        ChatRecord target=dialog.LocatedRecord;
                        if(store.LargeHistory(peer.Id)) {
                            var context=await Task.Run(()=>store.ReadArchive(peer.Id,"",0,target,CancellationToken.None));
                            if(IsDisposed || main.ActiveChat!=this || request!=locationVersion)return;
                            if(!ShowLocation(target,context.Records,context.HasNewer))Error("无法定位","这条消息已删除。");
                        } else if(!LocateMessage(target))Error("无法定位","这条消息已删除。");
                    }
                    catch(IOException error){Error("读取聊天记录失败",error.Message);}
                    catch(UnauthorizedAccessException error){Error("读取聊天记录失败",error.Message);}
                }
        }
        internal bool LocateMessage(ChatRecord target)
        {
            if(target.PeerId!=peer.Id)return false;
            var records=store.History(peer.Id,500);
            bool older=!records.Any(record=>record.Packet==target.Packet&&record.Outgoing==target.Outgoing);
            if(older)records=store.ReadArchive(peer.Id,"",0,target,CancellationToken.None).Records;
            return ShowLocation(target,records,older);
        }
        private bool ShowLocation(ChatRecord target,List<ChatRecord> records,bool older)
        {
            if(!records.Any(record=>record.Packet==target.Packet&&record.Outgoing==target.Outgoing))return false;
            showingArchive=older;returnToLatest.Visible=older;
            history.RecordLimit=500;history.CharacterLimit=Int32.MaxValue;
            history.SetRecords(records.Select(record=>{ChatRecord outgoing=record.Outgoing?network.OutgoingRecord(peer.Id,record.Packet):null;if(outgoing!=null&&record.State=="已送达")outgoing.State=record.State;return outgoing??record;}));
            return history.RevealRecord(target);
        }
        private void Error(string title, string text) { using (NoticeDialog dialog = new NoticeDialog(title, text)) dialog.ShowDialog(main); }
        public int PendingFileCount { get { return files.Items.Count; } }
        public bool HasAutomaticFile { get { return receiving == null && files.Items.Cast<ReceivedFile>().Any(f => f.AutomaticPending); } }
        internal bool HasQueuedFile { get { return receiving == null && files.Items.Cast<ReceivedFile>().Any(f => f.AutomaticPending && f.Status == "排队中"); } }
        internal Task ReceiveNextQueuedAsync() { return ReceiveAsync(files.Items.Cast<ReceivedFile>().First(f => f.AutomaticPending && f.Status == "排队中"), true); }
        public Task ReceiveNextAutomaticAsync()
        {
            return ReceiveAsync(files.Items.Cast<ReceivedFile>().First(f => f.AutomaticPending), true);
        }
        private sealed class UiProgress : IProgress<TransferProgress>
        {
            private readonly MainForm host;
            private readonly Action<TransferProgress> callback;
            public UiProgress(MainForm host, Action<TransferProgress> callback) { this.host = host; this.callback = callback; }
            public void Report(TransferProgress value) { host.Ui(() => callback(value)); }
        }
        private async Task ReceiveAsync(ReceivedFile selected, bool automatic)
        {
            if (receiving != null || IsDisposed || selected.OfferCanceled || selected.Rejected || selected.CancelRequested) return;
            if (!main.InvokeRequired && SynchronizationContext.Current == null) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            selected.AutomaticPending = false; selected.Status = "接收中"; files.SelectedItem = selected; pauseRequested = false;
            CancellationTokenSource cancel = new CancellationTokenSource(); receiving = cancel; activeFile = selected;
            var finished = new TaskCompletionSource<bool>(); receivingFinished = finished;
            FileState(selected, 0, "连接中 · 点击取消");
            try
            {
                string receiveRoot = network.Settings.ReceiveFolder;
                string destination = SendBatch.Destination(receiveRoot, selected.File);
                string part = LanService.PartialPath(peer, selected.Packet, selected.File, destination);
                selected.PartialPaths.Add(part); selected.PartialRoots[part] = receiveRoot;
                fileStatus.Text = "连接中";
                string saved = await network.ReceiveFileAsync(peer, selected.Packet, selected.File, destination, null, cancel.Token, true, new UiProgress(main, p => {
                    if (!IsDisposed && receiving == cancel && !cancel.IsCancellationRequested && activeFile == selected && !selected.OfferCanceled && !selected.Rejected && !selected.CancelRequested && selected.Status != "已保存") {
                        FileState(selected, p.Percent, p.Stage ?? "接收中 · 点击取消", null, p.BytesPerSecond);
                        selected.Status = p.Percent + "% · " + Theme.SizeText((long)p.BytesPerSecond) + "/s";
                        fileStatus.Text = selected.Status; files.Invalidate();
                    }
                }), () => pauseRequested && !selected.OfferCanceled && !selected.Rejected && !selected.CancelRequested);
                if (IsDisposed) return;
                selected.Status = "已保存";
                selected.Record.LocalFiles = selected.Record.GetLocalFiles().Concat(new[] { saved }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                FileState(selected, 100, "已保存", saved);
                int remaining=files.Items.Cast<ReceivedFile>().Count(f=>f.Packet==selected.Packet&&f!=selected);
                selected.Record.State=selected.OfferCanceled&&selected.Record.Transfers.Any(f=>String.IsNullOrEmpty(f.LocalPath)&&f.Status=="对方已取消发送")?"对方已取消发送":remaining==0?"已保存":"已接收 "+selected.Record.GetLocalFiles().Length+" / "+(selected.Record.GetLocalFiles().Length+remaining);
                // 完成状态与本地附件回写原邀请，不另造一条聊天消息或重复媒体卡片。
                main.TrySave(selected.Record); history.Delivery(selected.Record);
                files.SelectedIndex=-1;files.Items.Remove(selected); if (files.Items.Count > 0) files.SelectedIndex = 0;
                fileStatus.Text = "已保存"; main.FileReceiveStatus("文件已保存：" + Path.GetFileName(saved));
            }
            catch (OfferRevokedException) { if (!IsDisposed) OfferCanceled(selected.Packet); }
            catch (OperationCanceledException) { if (!IsDisposed && !selected.OfferCanceled && !selected.Rejected) { selected.Status = pauseRequested ? "已暂停" : "已取消，可重试"; fileStatus.Text = pauseRequested ? "已暂停，保留断点" : "已取消，可重新接收"; FileState(selected, pauseRequested ? (int?)null : 0, pauseRequested ? "已暂停" : "已取消 · 点击重试"); main.TrySave(selected.Record); } }
            catch (Exception error)
            {
                if (!IsDisposed && !selected.OfferCanceled && !selected.Rejected && !selected.CancelRequested)
                {
                    selected.Status = "失败，可续传"; fileStatus.Text = "重试时继续已有断点";
                    FileTransferState failed=selected.Record.Transfers.FirstOrDefault(f=>f.Id==selected.File.Id);if(failed!=null)failed.Error=error.GetBaseException().Message;
                    FileState(selected, null, "接收失败 · 点击续传"); main.TrySave(selected.Record);
                    // 更新卡片期间可能派发取消回调，旧失败提示不能覆盖已生效的取消状态。
                    if (selected.OfferCanceled) return;
                    main.FileReceiveStatus("接收失败：" + selected.File.Name + " — " + error.GetBaseException().Message);
                    // 后台自动接收失败不能弹窗抢焦点，也不能无限重试同一个失败任务。
                    if (!automatic) Error("接收失败", error.GetBaseException().Message);
                }
            }
            finally
            {
                if (selected.OfferCanceled)
                {
                    // 取消回调先移除队列；下载 finally 释放独占句柄后再兜底清理。
                    try {
                        DeletePartials(selected); main.DeleteIncomingPartials(peer, selected.Record);
                        // 下载退出后写回最终状态，收敛插入取消通知之前已经开始的失败回调。
                        main.CancelIncomingOffer(peer, selected.Record, false);
                    }
                    catch (Exception e) { main.FileReceiveStatus("对方已取消发送；断点清理未完成：" + e.Message); }
                }
                cancel.Dispose(); receiving = null; activeFile = null; receivingFinished = null;
                finished.TrySetResult(true);
                if (!IsDisposed) main.ReceiveAutomatically();
            }
        }
        private void DeletePartials(ReceivedFile item)
        {
            var paths = new HashSet<string>(item.PartialPaths, StringComparer.OrdinalIgnoreCase);
            string destination = SendBatch.DestinationPath(network.Settings.ReceiveFolder, item.File);
            paths.Add(LanService.PartialPath(peer, item.Packet, item.File, destination));
            foreach (string part in paths)
            {
                string root;
                if (!item.PartialRoots.TryGetValue(part, out root)) root = network.Settings.ReceiveFolder;
                ReceiveStorage.DeletePartialAndEmptyParents(root, part, item.File.RelativePath);
            }
        }
        internal void OfferCanceled(long packet)
        {
            bool active = activeFile != null && activeFile.Packet == packet && receiving != null;
            if (active) { activeFile.OfferCanceled = true; receiving.Cancel(); }
            List<ReceivedFile> pending = files.Items.Cast<ReceivedFile>().Where(f => f.Packet == packet).ToList();
            ChatRecord record = active ? activeFile.Record : pending.Select(f => f.Record).FirstOrDefault() ?? store.IncomingAttachments(peer.Id, packet).FirstOrDefault();
            if (record == null) return;
            foreach (ReceivedFile item in pending)
            {
                item.OfferCanceled = true; item.AutomaticPending = false; item.Status = "对方已取消发送";
                if (item != activeFile)
                    try { DeletePartials(item); }
                    catch (Exception e) { main.FileReceiveStatus("对方已取消发送；断点清理未完成：" + e.Message); }
                files.Items.Remove(item);
            }
            main.CancelIncomingOffer(peer, record, !active); history.Delivery(record);
            fileStatus.Text = (record.Transfers ?? new FileTransferState[0]).Any(f => f.Status == "对方已取消发送") ? "对方已取消发送" : "原邀请已失效，请让对方重新发送";
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; main.CloseChat(this); return; } if (receiving != null) receiving.Cancel(); base.OnFormClosing(e); }
        private void FileState(ReceivedFile item, int? percent, string status, string path = null, double bytesPerSecond = 0)
        {
            FileTransferState state = item.Record.Transfers == null ? null : item.Record.Transfers.FirstOrDefault(f => f.Id == item.File.Id);
            if (state == null) return;
            if (!String.IsNullOrEmpty(state.LocalPath) && status != "已保存") return;
            // 传输失败/进度可能晚于取消通知派发；终态只能被真实保存完成推进。
            if ((state.Status == "对方已取消发送" || state.Status == "已拒绝") && status != state.Status && status != "已保存") return;
            if (percent.HasValue) state.Percent = percent.Value; state.Status = status; state.BytesPerSecond = bytesPerSecond; if (path != null) state.LocalPath = path;
            state.MarkCurrent();if(status=="已保存"||status.StartsWith("连接中"))state.Error=null;
            main.TrackTransfer(item.Record);
            history.Delivery(item.Record);
        }
        protected override void Dispose(bool disposing) { if (disposing && !IsDisposed) { ClearPastedImages(); SaveDraft(); draftTimer.Dispose(); headTips.Dispose(); if (emojiPicker != null) emojiPicker.Dispose(); if (chatMenu != null) chatMenu.Dispose(); if (receiving != null) receiving.Cancel(); files.Dispose(); fileStatus.Dispose(); if (avatar != null && avatar.Image != null) avatar.Image.Dispose(); } base.Dispose(disposing); if(disposing&&chatFont!=null){chatFont.Dispose();chatFont=null;} }
    }
}
