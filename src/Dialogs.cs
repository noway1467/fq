using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace FeiqLight
{
    internal static class DialogLayout
    {
        internal static Panel Body(ModernWindow window)
        {
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 16) };
            window.Surface.Controls.Add(body); return body;
        }
        internal static void Actions(ModernWindow window, Button primary, Button secondary = null)
        {
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(24, 16, 24, 20) };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 24, 0, footer.Width - 24, 0); };
            FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            foreach (Button button in new[] { primary, secondary }.Where(b => b != null))
            {
                button.Size = new Size(Math.Max(96, TextRenderer.MeasureText(button.Text, button.Font).Width + 32), 40);
                button.Margin = new Padding(8, 0, 0, 0); row.Controls.Add(button);
            }
            footer.Controls.Add(row); window.Surface.Controls.Add(footer); window.AcceptButton = primary; window.CancelButton = secondary ?? primary;
        }
        internal static RoundedPanel TextCard(string text, string accessible)
        {
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Radius = Theme.ControlRadius, Padding = new Padding(16, 12, 16, 12) };
            RichTextBox details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Text = text ?? "", Font = Theme.Body, BackColor = Theme.Ice, ForeColor = Theme.Ink, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, AccessibleName = accessible };
            Theme.AttachEditMenu(details); Theme.ThemeScrollbars(details); card.Controls.Add(details); return card;
        }
    }
    internal sealed class ConfirmDialog : ModernWindow
    {
        private readonly ToggleSwitch option;
        internal bool OptionChecked { get { return option != null && option.Checked; } }
        internal ConfirmDialog(string title, string text, string confirm, bool danger, string optionText = null)
        {
            Text = title; ResizeWindow = false; ClientSize = new Size(540, optionText == null ? 316 : 372); MinimumSize = new Size(440, 280); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this); body.Controls.Add(DialogLayout.TextCard(text, "确认详情"));
            if (optionText != null) { option = new ToggleSwitch { Text = optionText, Dock = DockStyle.Bottom, Height = 52, AccessibleName = optionText }; body.Controls.Add(option); }
            Button cancel = Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false);
            Button done = Theme.Button(confirm, delegate { DialogResult = DialogResult.OK; }, true); if (danger) done.BackColor = Theme.Danger;
            DialogLayout.Actions(this, done, cancel); Shown += delegate { cancel.Focus(); };
        }
    }
    public sealed class AboutDialog : ModernWindow
    {
        public const string RepositoryUrl="https://github.com/noway1467/fq";
        private readonly Bitmap logo=Theme.AppLogo(160);
        public AboutDialog()
        {
            Text="关于飞Q";ResizeWindow=false;ClientSize=new Size(460,430);MinimumSize=new Size(400,400);StartPosition=FormStartPosition.CenterParent;
            Panel footer=new Panel {Dock=DockStyle.Bottom,Height=62,Padding=new Padding(24,8,24,16)};
            Button done=Theme.Button("确定",delegate {DialogResult=DialogResult.OK;},true);done.Dock=DockStyle.Right;footer.Controls.Add(done);AcceptButton=done;CancelButton=done;
            TableLayoutPanel body=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(24,10,24,8)};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int height in new[]{96,34,78,80})body.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            TableLayoutPanel brand=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,86));brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));brand.RowStyles.Add(new RowStyle(SizeType.Percent,56));brand.RowStyles.Add(new RowStyle(SizeType.Percent,44));
            Panel icon=new Panel {Dock=DockStyle.Fill,Margin=new Padding(0,4,16,12),AccessibleName="飞Q图标"};icon.Paint+=delegate(object sender,PaintEventArgs e){e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;int size=Math.Min(icon.Width,icon.Height);e.Graphics.DrawImage(logo,new Rectangle(0,0,size,size));};brand.Controls.Add(icon,0,0);brand.SetRowSpan(icon,2);
            Label title=Theme.Label("飞Q",Theme.Title,Theme.Ink);title.Font=new Font(Theme.Body.FontFamily,17F,FontStyle.Bold);title.Dock=DockStyle.Fill;brand.Controls.Add(title,1,0);
            Label version=Theme.Label("Windows  "+typeof(AboutDialog).Assembly.GetName().Version.ToString(3),Theme.Small,Theme.Muted);version.Dock=DockStyle.Fill;brand.Controls.Add(version,1,1);body.Controls.Add(brand,0,0);
            Label intro=Theme.Label("局域网聊天与文件传输 · 无需服务器",Theme.Body,Theme.Muted);intro.Dock=DockStyle.Fill;intro.Margin=Padding.Empty;body.Controls.Add(intro,0,1);
            RoundedPanel card=new RoundedPanel {Dock=DockStyle.Fill,Fill=Theme.Ice,Radius=Theme.ControlRadius,Padding=new Padding(14,8,14,8),Margin=new Padding(0,0,0,10)};
            Panel sourceIcon=new Panel {Dock=DockStyle.Left,Width=38,BackColor=Theme.Ice};sourceIcon.Paint+=delegate(object sender,PaintEventArgs e){Theme.Glyph(e.Graphics,"code",new Rectangle(0,(sourceIcon.Height-26)/2,26,26),Theme.Blue);};
            LinkLabel repository=new LinkLabel {Dock=DockStyle.Fill,Text="GitHub 仓库\ngithub.com/noway1467/fq",Font=Theme.Body,BackColor=Theme.Ice,LinkColor=Theme.Blue,ActiveLinkColor=Theme.Blue,VisitedLinkColor=Theme.Blue,TextAlign=ContentAlignment.MiddleLeft,LinkBehavior=LinkBehavior.HoverUnderline,AccessibleName="GitHub 仓库：github.com/noway1467/fq"};repository.Links.Add(0,repository.Text.Length,RepositoryUrl);repository.LinkClicked+=delegate {MessageLinks.Open(repository,RepositoryUrl);};card.Controls.Add(repository);card.Controls.Add(sourceIcon);body.Controls.Add(card,0,2);
            Label note=Theme.Label("仅用于可信局域网 · 基础协议明文\n内置表情与头像：Twemoji · CC BY 4.0\n© Twitter, Inc. 及贡献者；许可随程序内置。",Theme.Small,Theme.Muted);note.Dock=DockStyle.Fill;note.Margin=Padding.Empty;body.Controls.Add(note,0,3);
            Surface.Controls.Add(body);Surface.Controls.Add(footer);
        }
        protected override void Dispose(bool disposing) {if(disposing)logo.Dispose();base.Dispose(disposing);}
    }
    public sealed class NoticeDialog : ModernWindow
    {
        public NoticeDialog(string title, string text) : this(title, text, null, null) { }
        public NoticeDialog(string title, string text, string linkText, string linkUrl)
        {
            bool hasLink = !String.IsNullOrWhiteSpace(linkText) && !String.IsNullOrWhiteSpace(linkUrl);
            int textHeight = TextRenderer.MeasureText(text ?? "", Theme.Body, new Size(440, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
            Text = title; ResizeWindow = false; ClientSize = new Size(520, Math.Max(248, Math.Min(480, textHeight + 192 + (hasLink ? 40 : 0)))); MinimumSize = new Size(400, 220); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this); body.Controls.Add(DialogLayout.TextCard(text, "提示详情"));
            if (hasLink)
            {
                Panel linkPanel = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 8, 0, 0) };
                LinkLabel link = new LinkLabel { Dock = DockStyle.Fill, Text = linkText, Font = Theme.Body, BackColor = Color.Transparent, LinkColor = Theme.Blue, ActiveLinkColor = Theme.Blue, VisitedLinkColor = Theme.Blue, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = linkText };
                link.Links.Add(0, link.Text.Length, linkUrl);
                link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e) { MessageLinks.Open(link, e.Link.LinkData as string); };
                linkPanel.Controls.Add(link); body.Controls.Add(linkPanel);
            }
            Button done = Theme.Button("确定", delegate { DialogResult = DialogResult.OK; }, true); DialogLayout.Actions(this, done);
        }
    }
    public sealed class MessageTextDialog : ModernWindow
    {
        public MessageTextDialog(string text)
        {
            Text = "消息全文"; ClientSize = new Size(640, 560); MinimumSize = new Size(380, 280); StartPosition = FormStartPosition.CenterParent;
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(20, 12, 20, 10) };
            Button close = Theme.Button("关闭", delegate { DialogResult = DialogResult.OK; }, false); close.Dock = DockStyle.Right; footer.Controls.Add(close);
            Label hint = Theme.Label("拖动选择文字 · Ctrl+C 复制 · Ctrl+A 全选", Theme.Small, Theme.Muted); hint.Dock = DockStyle.Fill; hint.AutoEllipsis = true; footer.Controls.Add(hint); hint.BringToFront();
            RichTextBox content = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Theme.Body,
                BackColor = Theme.Surface, ForeColor = Theme.Ink, DetectUrls = false, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Vertical,
                Text = text ?? "", AccessibleName = "消息全文（可选择复制）" };
            Theme.AttachEditMenu(content); Theme.ThemeScrollbars(content);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Radius = Theme.ControlRadius, Padding = new Padding(14, 12, 10, 10), Margin = new Padding(20, 6, 20, 0) };
            content.BackColor = Theme.Ice; card.Controls.Add(content);
            Panel frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 24, 8) }; frame.Controls.Add(card);
            Surface.Controls.Add(frame); Surface.Controls.Add(footer);
            CancelButton = close; Shown += delegate { content.Focus(); content.Select(0, 0); };
        }
    }
    public sealed class ConversationDialog : ModernWindow
    {
        public string Note { get { return note.Text.Trim(); } }
        public bool DeleteHistory { get { return history.Checked; } }
        private readonly TextBox note;
        private readonly ToggleSwitch history;
        public ConversationDialog(Peer peer, bool deleting, bool clearOnly = false)
        {
            Text=clearOnly?"清空聊天记录":deleting?"删除联系人":"设置备注"; ResizeWindow=false; ClientSize=new Size(520,332); MinimumSize=new Size(420,300); StartPosition=FormStartPosition.CenterParent;
            Panel body=DialogLayout.Body(this);
            Label hint=Theme.Label(clearOnly?"仅清空本机聊天记录，不删除已接收的文件。":deleting?"仅从本机列表移除，不删除已接收的文件。\n新消息仍会恢复此会话。":"仅在本机显示；留空恢复对方昵称。",Theme.Body,Theme.Muted); hint.Dock=DockStyle.Top;hint.Height=64;body.Controls.Add(hint);
            RoundedInput input=new RoundedInput {Dock=DockStyle.Top,Height=44};note=input.Input;note.MaxLength=64;note.Text=peer.Note??"";note.AccessibleName="联系人备注";input.Visible=!deleting;body.Controls.Add(input);input.BringToFront();
            history=new ToggleSwitch {Text="同时删除聊天记录",Checked=clearOnly,Enabled=!clearOnly,Visible=deleting,AccessibleName="同时删除聊天记录",Dock=DockStyle.Top,Height=48};body.Controls.Add(history);history.BringToFront();
            Button cancel=Theme.Button("取消",delegate{DialogResult=DialogResult.Cancel;},false);
            Button done=Theme.Button(deleting?"确认删除":"保存",delegate{DialogResult=DialogResult.OK;},true);DialogLayout.Actions(this,done,cancel);
            if(deleting) done.BackColor=Theme.Danger;
        }
    }
    public sealed class AddressDialog : ModernWindow
    {
        private readonly TextBox address;
        public IPEndPoint Endpoint { get; private set; }
        public AddressDialog(int port)
        {
            Text = "添加联系人"; ResizeWindow = false; ClientSize = new Size(520, 332); MinimumSize = new Size(400, 280); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this);
            Label label = Theme.Label("对方的 IPv4 地址", Theme.Section, Theme.Ink); label.Dock=DockStyle.Top;label.Height=32;body.Controls.Add(label);
            RoundedInput input = new RoundedInput {Dock=DockStyle.Top,Height=44}; address = input.Input; address.AccessibleName = "好友 IP 地址"; input.Placeholder = "192.168.1.100:" + port; body.Controls.Add(input);input.BringToFront();
            Label error = Theme.Label("端口可省略，默认 2425。", Theme.Small, Theme.Muted); error.Dock=DockStyle.Top;error.Height=56;body.Controls.Add(error);error.BringToFront();
            Button add = Theme.Button("添加", delegate { try { Endpoint = Parse(address.Text); DialogResult = DialogResult.OK; } catch (ArgumentException e) { error.Text = e.Message; error.ForeColor=Theme.Danger; address.Focus(); } }, true);
            Button cancel=Theme.Button("取消",delegate {DialogResult=DialogResult.Cancel;},false);DialogLayout.Actions(this,add,cancel);Shown+=delegate {address.Focus();};
        }
        public static IPEndPoint Parse(string text)
        {
            string[] parts = text.Trim().Split(':'); IPAddress ip; int port = 2425;
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224 || (parts.Length == 2 && !Int32.TryParse(parts[1], out port)) || port < 1024 || port > 65535) throw new ArgumentException("请输入有效 IPv4 地址，端口为 1024–65535。");
            return new IPEndPoint(ip, port);
        }
    }
    public sealed class ForwardDialog : ModernWindow
    {
        public Peer Target { get; private set; }
        public ForwardDialog(Peer[] peers, int attachments = 0)
        {
            Text = attachments<0?"恢复已移除的联系人":"转发到"; ClientSize = new Size(430, 470); MinimumSize = new Size(380, 300); ResizeWindow = false; StartPosition = FormStartPosition.CenterParent;
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(20, 10, 20, 12) };
            ListBox list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Ink, IntegralHeight = false };
            foreach (Peer peer in peers) list.Items.Add(peer.DisplayName + "  ·  " + peer.Endpoint);
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 66;
            list.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                if (e.Index < 0) return; Peer peer = peers[e.Index]; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.Sky : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                Bitmap avatar = Theme.CachedAvatar(42, peer.Id, false, peer.Avatar); e.Graphics.DrawImageUnscaled(avatar, e.Bounds.Left + 10 - Theme.AvatarHalo(42), e.Bounds.Top + 12 - Theme.AvatarHalo(42));
                TextRenderer.DrawText(e.Graphics, peer.DisplayName, Theme.Section, new Rectangle(e.Bounds.Left + 62, e.Bounds.Top + 12, e.Bounds.Width - 72, 24), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, (peer.DuplicateName?"#"+peer.ConversationNumber+" · ":"")+peer.Endpoint, Theme.Small, new Rectangle(e.Bounds.Left + 62, e.Bounds.Top + 36, e.Bounds.Width - 72, 22), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            };
            Button send = Theme.Button(attachments<0?"恢复会话":"确认转发", delegate { if (list.SelectedIndex >= 0) { Target = peers[list.SelectedIndex]; DialogResult = DialogResult.OK; } }, true);
            send.Dock = DockStyle.Right; send.Enabled = false; list.SelectedIndexChanged += delegate { send.Enabled = list.SelectedIndex >= 0; }; footer.Controls.Add(send);
            Label hint = Theme.Label(attachments<0?(peers.Length==0?"没有已移除的联系人。":"选择联系人并恢复；保留的历史和草稿仍可查看。"):peers.Length == 0 ? "没有在线联系人，请先连接或添加联系人。" : attachments == 0 ? "转发文本 · 选择联系人后确认发送" : "转发 " + attachments + " 个附件及正文 · 选择联系人", Theme.Small, Theme.Muted); hint.Dock = DockStyle.Top; hint.Height = 46; hint.Padding = new Padding(22, 8, 22, 0);
            Panel frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 0) }; frame.Controls.Add(list);
            Surface.Controls.Add(frame); Surface.Controls.Add(hint); Surface.Controls.Add(footer); AcceptButton = send;
        }
    }
    internal sealed class AvatarPickerDialog : ModernWindow
    {
        private sealed class AvatarChoice : Button
        {
            internal bool Active;
            private readonly Bitmap picture;
            internal AvatarChoice(int index) {
                picture = Theme.ContactAvatar(44, "", false, "builtin:" + index);
                FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
                AccessibleName = "内置头像：" + EmojiAvatars.DescriptionAt(index);
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
            protected override void OnPaint(PaintEventArgs e) {
                e.Graphics.Clear(Theme.Surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle area = new Rectangle(1, 1, Width - 3, Height - 3);
                using(GraphicsPath path = Theme.Rounded(area, Theme.ControlRadius)) {
                    using(SolidBrush brush = new SolidBrush(Active ? Theme.Sky : Theme.Ice)) e.Graphics.FillPath(brush, path);
                    if(Active || Focused) using(Pen border = new Pen(Theme.Blue, 2)) e.Graphics.DrawPath(border, path);
                }
                int size = Math.Min(Width - 8, Height - 8); e.Graphics.DrawImage(picture, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size));
            }
            protected override void Dispose(bool disposing) { if(disposing) picture.Dispose(); base.Dispose(disposing); }
        }
        private readonly PictureBox preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "所选头像预览" };
        private readonly AvatarChoice[] choices = new AvatarChoice[24];
        private readonly string identity;
        public string SelectedValue { get; private set; }
        internal AvatarPickerDialog(string value, string identity)
        {
            this.identity = identity; Text = "更换头像"; ResizeWindow = false; ClientSize = new Size(520, 560); MinimumSize = new Size(480, 560); StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); foreach(int height in new[] { 76, 248, 44, 28 }) content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.Controls.Add(preview, 0, 0);
            Label title = Theme.Label("选一个喜欢的头像\n也可以使用自己的图片", Theme.Body, Theme.Ink); title.Dock = DockStyle.Fill; title.Padding = new Padding(12, 0, 0, 0); top.Controls.Add(title, 1, 0); content.Controls.Add(top, 0, 0);
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 4, Margin = new Padding(0, 4, 0, 8), AccessibleName = "内置头像列表" };
            for(int i = 0; i < 6; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6));
            for(int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            for(int i = 0; i < choices.Length; i++) { int index = i; AvatarChoice choice = new AvatarChoice(i) { Dock = DockStyle.Fill, Margin = new Padding(4) }; choice.Click += delegate { SetAvatar("builtin:" + index); }; choices[i] = choice; grid.Controls.Add(choice, i % 6, i / 6); }
            content.Controls.Add(grid, 0, 1);
            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            Button local = Theme.Button("选择本地图片…", delegate {
                using(OpenFileDialog picker = new OpenFileDialog { Title = "选择头像图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif", CheckFileExists = true }) if(picker.ShowDialog(this) == DialogResult.OK) {
                    try { ImportImage(picker.FileName); }
                    catch(Exception error) { using(NoticeDialog notice = new NoticeDialog("无法使用图片", error.Message)) notice.ShowDialog(this); }
                }
            }, false); local.Dock = DockStyle.Fill; local.Margin = new Padding(0, 0, 6, 4); actions.Controls.Add(local, 0, 0);
            Button reset = Theme.Button("使用默认头像", delegate { SetAvatar("default"); }, false); reset.Dock = DockStyle.Fill; reset.Margin = new Padding(6, 0, 0, 4); actions.Controls.Add(reset, 1, 0); content.Controls.Add(actions, 0, 2);
            Label hint = Theme.Label("回到设置页点击保存后生效。", Theme.Small, Theme.Muted); hint.Dock = DockStyle.Fill; content.Controls.Add(hint, 0, 3);
            Panel body = DialogLayout.Body(this); body.Controls.Add(content);
            DialogLayout.Actions(this, Theme.Button("使用此头像", delegate { DialogResult = DialogResult.OK; }, true), Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false));
            SetAvatar(Protocol.NormalizeAvatar(value) ?? "default");
        }
        private void SetAvatar(string value) {
            SelectedValue = value; Image old = preview.Image; preview.Image = Theme.ContactAvatar(64, identity, false, value); if(old != null) old.Dispose();
            for(int i = 0; i < choices.Length; i++) { choices[i].Active = value == "builtin:" + i; choices[i].Invalidate(); }
        }
        internal void ImportImage(string path) { SetAvatar(EmojiAvatars.FromLocalImage(path)); }
        protected override void Dispose(bool disposing) { if(disposing && preview.Image != null) { preview.Image.Dispose(); preview.Image = null; } base.Dispose(disposing); }
    }
    public sealed class SettingsDialog : ModernWindow
    {
        private readonly string[] tabNames={"通用","外观","文件","诊断"};
        private readonly string[] tabIcons={"person","palette","folder","pulse"};
        private readonly FlowLayoutPanel[] pages=new FlowLayoutPanel[4];
        private readonly SettingsTabButton[] tabs=new SettingsTabButton[4];
        private int selectedTab;
        private void SelectTab(int index) {
            selectedTab=index;body.SuspendLayout();body.AutoScrollPosition=Point.Empty;
            for(int i=0;i<pages.Length;i++){pages[i].Visible=i==index;tabs[i].Active=i==index;tabs[i].AccessibleDescription=i==index?"当前标签":"切换标签";tabs[i].Invalidate();}
            if(pageTitle!=null)pageTitle.Text=tabNames[index];
            body.ResumeLayout(true);
        }
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
            if(keyData==(Keys.Control|Keys.Tab)||keyData==(Keys.Control|Keys.Shift|Keys.Tab)){int next=(selectedTab+((keyData&Keys.Shift)!=0?3:1))%4;SelectTab(next);tabs[next].Focus();return true;}
            return base.ProcessCmdKey(ref msg,keyData);
        }
        // 侧栏导航项：图标 + 文字，选中时整行浅色底；方向键在标签之间移动。
        private sealed class SettingsTabButton : Button {
            public bool Active;
            public string Icon = "";
            private readonly Tween hover;
            public SettingsTabButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;BackColor=Theme.Surface;Font=Theme.Body;AccessibleRole=AccessibleRole.PageTab;TextAlign=ContentAlignment.MiddleLeft;SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.AllPaintingInWmPaint,true);hover=new Tween(this,140,0);}
            protected override void OnMouseEnter(EventArgs e){hover.Set(1);base.OnMouseEnter(e);}
            protected override void OnMouseLeave(EventArgs e){hover.Set(0);base.OnMouseLeave(e);}
            protected override bool IsInputKey(Keys keyData){Keys key=keyData&Keys.KeyCode;return key==Keys.Left||key==Keys.Right||key==Keys.Up||key==Keys.Down||base.IsInputKey(keyData);}
            protected override void OnPaint(PaintEventArgs e){
                Graphics g=e.Graphics; g.Clear(Theme.Surface); g.SmoothingMode=SmoothingMode.AntiAlias; float scale=g.DpiX/96F;
                Color fill=Active?Theme.Sky:Motion.Mix(Theme.Surface,Theme.Hover,hover.Value);
                Rectangle area=new Rectangle(8,(int)(3*scale),Width-16,Height-(int)(6*scale));
                using(GraphicsPath path=Theme.Rounded(area,(int)(9*scale)))using(SolidBrush b=new SolidBrush(fill))g.FillPath(b,path);
                Color ink=Active?Theme.Blue:Theme.Ink;
                int icon=(int)(19*scale); Theme.Glyph(g,Icon,new Rectangle((int)(22*scale),(Height-icon)/2,icon,icon),ink);
                TextRenderer.DrawText(g,Text,Active?Theme.Strong:Font,new Rectangle((int)(52*scale),0,Width-(int)(58*scale),Height),ink,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
                if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(area,-3,-3),Theme.Blue,fill);
            }
        }
        private readonly Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface, Padding = new Padding(0, 0, 0, 8) };
        private Label pageTitle;
        public AppSettings Value { get; private set; }
        public SettingsDialog(AppSettings current, string dataPath, int port, bool startupAvailable = false, bool startupEnabled = false)
        {
            Text = "设置"; ClientSize = new Size(720, 720); ResizeWindow = false; MinimumSize = new Size(560, 500); StartPosition = FormStartPosition.CenterParent;
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(24, 12, 24, 16), BackColor = Theme.Surface, AccessibleName = "设置操作栏" };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            Panel nav = new Panel { Dock = DockStyle.Left, Width = 186, BackColor = Theme.Surface };
            nav.Paint += delegate(object sender, PaintEventArgs e) { using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, nav.Width - 1, 16, nav.Width - 1, nav.Height - 16); };
            pageTitle = Theme.Label("通用", new Font(Theme.Body.FontFamily, 15F, FontStyle.Bold), Theme.Ink); pageTitle.SetBounds(206, 16, 400, 34); pageTitle.Parent = null;
            Panel header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Surface };
            pageTitle.Dock = DockStyle.Fill; pageTitle.Padding = new Padding(28, 12, 0, 0); header.Controls.Add(pageTitle);
            Surface.Controls.Add(body); Surface.Controls.Add(header); Surface.Controls.Add(footer); Surface.Controls.Add(nav);
            string avatarValue = Protocol.NormalizeAvatar(current.Avatar) ?? "default";
            string avatarIdentity = "device:" + current.DeviceId;
            PictureBox avatarPreview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = Theme.ContactAvatar(64, avatarIdentity, false, avatarValue), AccessibleName = "个人头像" };
            avatarPreview.Disposed += delegate { if(avatarPreview.Image != null) avatarPreview.Image.Dispose(); };
            TableLayoutPanel avatarRow = new TableLayoutPanel { Top = -1, Height = 76, ColumnCount = 2, RowCount = 1, AccessibleName = "个人头像设置" };
            avatarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); avatarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); avatarRow.Controls.Add(avatarPreview, 0, 0);
            Button avatarButton = Theme.Button("更换头像…", delegate {
                using(AvatarPickerDialog picker = new AvatarPickerDialog(avatarValue, avatarIdentity)) if(picker.ShowDialog(this) == DialogResult.OK) {
                    avatarValue = picker.SelectedValue; Image old = avatarPreview.Image; avatarPreview.Image = Theme.ContactAvatar(64, avatarIdentity, false, avatarValue); old.Dispose();
                }
            }, false); avatarButton.Width = 128; avatarButton.Anchor = AnchorStyles.Left; avatarRow.Controls.Add(avatarButton, 1, 0); body.Controls.Add(avatarRow);
            TextBox name = Input("昵称", current.Nickname, 8, 32), group = Input("部门", current.Group, 80, 32), signature = Input("个性签名", current.Signature, 152, 80);
            ToggleSwitch close = new ToggleSwitch { Text = "关闭时收起到托盘", Description = "关闭窗口后仍在后台接收消息，可从托盘图标恢复。", Checked = current.CloseToTray, BackColor = Theme.Surface }; close.SetBounds(26, 232, 458, 54); body.Controls.Add(close);
            ToggleSwitch notifications = new ToggleSwitch { Text = "消息通知", Description = "窗口不在前台时用系统气泡提示新消息。", Checked = current.Notifications, BackColor = Theme.Surface }; notifications.SetBounds(26, 274, 458, 54); body.Controls.Add(notifications);
            ToggleSwitch startup = new ToggleSwitch { Text = "开机自启动", AccessibleName = "开机自启动", Description = "登录 Windows 后自动连接并收起到托盘。", Checked = startupAvailable ? startupEnabled : current.StartWithWindows, Enabled = startupAvailable, BackColor = Theme.Surface }; startup.SetBounds(26, 275, 458, 54); body.Controls.Add(startup);
            Label startupHint = Theme.Label(startupAvailable ? "无需管理员权限；移动程序后请重新开启。\n系统禁用的启动项需在任务管理器恢复。" : "当前运行环境无法配置系统启动项（如回环测试或系统限制）。\n其他设置仍可正常保存。", Theme.Small, Theme.Muted); startupHint.SetBounds(26, 276, 458, 52); body.Controls.Add(startupHint);
            ToggleSwitch auto = new ToggleSwitch { Text = "自动接收文件", AccessibleName = "自动接收文件", Description = "收到文件邀请时直接保存到接收文件夹；关闭后需手动点击接收。", Checked = current.AutoReceiveFiles, BackColor = Theme.Surface }; auto.SetBounds(26, 316, 458, 54); body.Controls.Add(auto);
            Label hint = Theme.Label("仅用于可信局域网；不会自动运行文件。", Theme.Small, Theme.Muted); hint.SetBounds(26, 360, 458, 24); body.Controls.Add(hint);
            ToggleSwitch autoDrop=new ToggleSwitch {Text="拖入文件自动发送",AccessibleName="拖入文件自动发送",Description="默认关闭：拖入后先加入待发送列表。开启后仅发送本次拖入的文件，不夹带草稿或已排队附件。",Checked=current.AutoSendDroppedFiles,BackColor=Theme.Surface};autoDrop.SetBounds(26,520,458,68);body.Controls.Add(autoDrop);
            Label dropHint=Theme.Label("文件只在点击发送或开启自动发送后传输。",Theme.Small,Theme.Muted);dropHint.SetBounds(26,521,458,24);body.Controls.Add(dropHint);
            TextBox folder = Input("文件保存文件夹", current.ReceiveFolder ?? AppSettings.DefaultReceiveFolder, 404, 240); folder.Parent.Width = 350;
            Button browse = Theme.Button("选择文件夹", delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择飞Q接收文件的保存文件夹", SelectedPath = folder.Text, ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            }, false); browse.SetBounds(386, 434, 96, 44); body.Controls.Add(browse);
            Button reset = Theme.Button("恢复默认目录", delegate { folder.Text = AppSettings.DefaultReceiveFolder; }, false); reset.SetBounds(26, 486, 146, 36); body.Controls.Add(reset);
            Label info = Theme.Label("端口 " + port + " · 启动后自动连接并查找历史联系人\n资料目录  " + dataPath, Theme.Small, Theme.Muted); info.SetBounds(26, 532, 458, 46); info.AutoEllipsis = true; body.Controls.Add(info);
            ToggleSwitch dark=new ToggleSwitch { Text="夜间模式",AccessibleName="夜间模式",Description="界面与聊天背景切换为深色，阅读更省眼。",Checked=current.DarkMode,BackColor=Theme.Surface }; dark.SetBounds(26,590,458,54);body.Controls.Add(dark);
            ToggleSwitch follow=new ToggleSwitch { Text="跟随系统切换",AccessibleName="跟随系统切换",Description="自动跟随 Windows 的日间和夜间设置；关闭后可手动选择。",Checked=current.FollowSystemTheme,BackColor=Theme.Surface };follow.Top=290;body.Controls.Add(follow);
            Action followTheme=delegate {dark.Enabled=!follow.Checked;if(follow.Checked)dark.Checked=Theme.SystemDark;};follow.CheckedChanged+=delegate {followTheme();};followTheme();
            int accentIndex = current.Accent == "green" ? 1 : current.Accent == "purple" ? 2 : 0;
            ToggleSwitch diagnostics=new ToggleSwitch {Text="记录故障诊断",AccessibleName="记录故障诊断",Description="保存后生效；仅记录时间与事件码，不含聊天内容、身份、IP 或路径。",Checked=current.DiagnosticsEnabled,BackColor=Theme.Surface};diagnostics.SetBounds(26,700,458,64);body.Controls.Add(diagnostics);
            Label privacy=Theme.Label("最多 128 KiB，自动轮转；关闭后保留，可随时清除。",Theme.Small,Theme.Muted);privacy.SetBounds(26,742,458,28);body.Controls.Add(privacy);
            Diagnostics journal=new Diagnostics(dataPath,()=>current.DiagnosticsEnabled);
            Button viewLog=Theme.Button("查看诊断记录",delegate {try {using(NoticeDialog dialog=new NoticeDialog("诊断记录（可选择复制）",journal.Read()))dialog.ShowDialog(this);}catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("读取失败",e.Message))dialog.ShowDialog(this);}},false);viewLog.SetBounds(26,798,168,36);body.Controls.Add(viewLog);
            Button clearLog=Theme.Button("清除诊断记录",delegate {try {journal.Clear();}catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("清除失败",e.Message))dialog.ShowDialog(this);}},false);clearLog.SetBounds(204,798,168,36);body.Controls.Add(clearLog);
            TableLayoutPanel accent = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 44, AccessibleName = "主题颜色", Margin = new Padding(0, 4, 0, 10) };
            string[] accentNames = { "经典蓝", "青绿色", "紫罗兰" };
            Button[] choices = new Button[3];
            Action refreshAccent = delegate { for (int i = 0; i < choices.Length; i++) { choices[i].Text = (i == accentIndex ? "✓ " : "") + accentNames[i]; choices[i].BackColor = i == accentIndex ? Theme.Sky : Theme.Ice; choices[i].ForeColor = i == accentIndex ? Theme.Blue : Theme.Ink; choices[i].Invalidate(); } };
            for (int i = 0; i < 3; i++)
            {
                int index = i; accent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
                choices[i] = Theme.Button(accentNames[i], delegate { accentIndex = index; refreshAccent(); }, false);
                choices[i].AccessibleName = accentNames[i];
                choices[i].Dock = DockStyle.Fill; choices[i].Margin = new Padding(3); accent.Controls.Add(choices[i], i, 0);
            }
            refreshAccent(); accent.Top = 638; body.Controls.Add(accent);
            string backgroundMode=current.ChatBackground??"orbits", backgroundPath=current.BackgroundImage??"";
            int messageSize=Math.Max(9,Math.Min(18,current.ChatFontSize));
            Action refreshPattern=delegate {};Button image=null;
            TableLayoutPanel appearance=new TableLayoutPanel {ColumnCount=1,RowCount=6,Height=458,Top=292,AccessibleName="聊天外观"};
            appearance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{128,120,36,52,58,62})appearance.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            Panel preview=new Panel {Dock=DockStyle.Fill,Margin=new Padding(0,0,0,18),AccessibleName="主题预览"};appearance.Controls.Add(preview,0,0);
            preview.Paint+=delegate(object sender,PaintEventArgs e){
                Color blue=accentIndex==1?Color.FromArgb(34,153,116):accentIndex==2?Color.FromArgb(139,103,215):Color.FromArgb(51,144,236);
                Color surface=accentIndex==1?ColorTranslator.FromHtml(dark.Checked?"#172D29":"#F4FAF4"):accentIndex==2?ColorTranslator.FromHtml(dark.Checked?"#292338":"#FCF7FC"):dark.Checked?Color.FromArgb(30,39,49):Color.White;
                using(GraphicsPath frame=Theme.Rounded(new Rectangle(0,0,preview.Width-1,preview.Height-1),Theme.ControlRadius))using(SolidBrush b=new SolidBrush(Theme.Blend(blue,surface,dark.Checked?.10F:.15F)))e.Graphics.FillPath(b,frame);
                Rectangle left=new Rectangle(12,12,Math.Max(120,preview.Width-100),32),right=new Rectangle(76,52,Math.Max(120,preview.Width-90),32);
                using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Rounded(left,10))using(SolidBrush b=new SolidBrush(surface))e.Graphics.FillPath(b,path);
                using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Rounded(right,10))using(SolidBrush b=new SolidBrush(Theme.Blend(blue,surface,dark.Checked?.30F:.17F)))e.Graphics.FillPath(b,path);
                using(Font font=new Font(Theme.Body.FontFamily,messageSize)) {Color ink=dark.Checked?Color.White:Color.FromArgb(23,33,43);TextRenderer.DrawText(e.Graphics,"今天也保持联系",font,left,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);TextRenderer.DrawText(e.Graphics,"消息与输入字号预览",font,right,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
            };
            TableLayoutPanel presets=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};for(int i=0;i<2;i++){presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));presets.RowStyles.Add(new RowStyle(SizeType.Percent,50));}
            string[] presetNames={"晴空 · 经典","薄荷 · 日间","深海 · 夜间","暮紫 · 夜间"};
            for(int i=0;i<4;i++){int choice=i;Button pick=Theme.Button(presetNames[i],delegate {follow.Checked=false;dark.Checked=choice>=2;accentIndex=choice==1?1:choice==3?2:0;backgroundMode=choice==1?"lines":"orbits";refreshAccent();refreshPattern();preview.Invalidate();},false);pick.Dock=DockStyle.Fill;pick.Margin=new Padding(i%2==0?0:6,0,i%2==0?6:0,12);presets.Controls.Add(pick,i%2,i/2);}appearance.Controls.Add(presets,0,1);
            Label sizeLabel=Theme.Label("聊天字号 · "+messageSize+" pt",Theme.Body,Theme.Ink);sizeLabel.Dock=DockStyle.Fill;sizeLabel.Margin=new Padding(0,4,0,0);appearance.Controls.Add(sizeLabel,0,2);
            ThemedSlider sizeSlider=new ThemedSlider {Minimum=9,Maximum=18,Value=messageSize,Dock=DockStyle.Fill,AccessibleName="聊天字号",BackColor=Theme.Surface};sizeSlider.ValueChanged+=delegate {messageSize=sizeSlider.Value;sizeLabel.Text="聊天字号 · "+messageSize+" pt";preview.Invalidate();};appearance.Controls.Add(sizeSlider,0,3);
            TableLayoutPanel patterns=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,2,0,10)};string[] patternNames={"纯色","星环","流线"},patternKeys={"none","orbits","lines"};Button[] patternButtons=new Button[3];
            refreshPattern=delegate {for(int j=0;j<3;j++)patternButtons[j].Text=(backgroundMode==patternKeys[j]?"✓ ":"")+patternNames[j];if(image!=null)image.Text=backgroundMode=="image"?"背景图片 · "+System.IO.Path.GetFileName(backgroundPath):"选择背景图片…";};
            for(int i=0;i<3;i++){int choice=i;patterns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/3));patternButtons[i]=Theme.Button(patternNames[i],delegate{backgroundMode=patternKeys[choice];refreshPattern();},false);patternButtons[i].Dock=DockStyle.Fill;patternButtons[i].Margin=new Padding(i==0?0:6,0,i==2?0:6,0);patterns.Controls.Add(patternButtons[i],i,0);}refreshPattern();appearance.Controls.Add(patterns,0,4);
            image=Theme.Button(backgroundMode=="image"?"已选择背景图片 · 更换":"选择背景图片…",delegate {
                using(OpenFileDialog picker=new OpenFileDialog {Title="选择聊天背景（不超过 20 MiB）",Filter="图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif",CheckFileExists=true})if(picker.ShowDialog(this)==DialogResult.OK){
                    try {using(Bitmap validated=ChatWallpaper.Load(picker.FileName)){}backgroundPath=picker.FileName;backgroundMode="image";refreshPattern();}
                    catch(Exception errorImage){using(NoticeDialog notice=new NoticeDialog("无法使用图片",errorImage.Message))notice.ShowDialog(this);}
                }
            },false);image.AutoEllipsis=true;image.Dock=DockStyle.Fill;image.Margin=new Padding(0,0,0,10);refreshPattern();appearance.Controls.Add(image,0,5);body.Controls.Add(appearance);
            dark.CheckedChanged+=delegate {preview.Invalidate();};foreach(Button choice in choices)choice.Click+=delegate{preview.Invalidate();};
            // 错误提示独占一行；空 Label 也会遮住兄弟控件，不能与按钮使用重叠坐标。
            Label error = Theme.Label("", Theme.Small, Theme.Danger); error.Dock = DockStyle.Fill; error.AutoEllipsis = true; error.Visible = false; error.AccessibleName = "设置错误"; footer.Controls.Add(error);
            FlowLayoutPanel actionRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            footer.Controls.Add(actionRow);
            error.TextChanged += delegate {
                error.Visible = error.Text.Length > 0;
                float scale = actionRow.Height / 40F;
                int detail = TextRenderer.MeasureText(error.Text, error.Font, new Size(Math.Max(1, footer.Width - footer.Padding.Horizontal), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                footer.Height = actionRow.Height + footer.Padding.Vertical + (error.Visible ? Math.Max((int)(32 * scale), detail + (int)(8 * scale)) : (int)(4 * scale));
            };
            Button cancel = Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false); cancel.Size = new Size(92, 40); CancelButton = cancel;
            Button save = Theme.Button("保存", delegate
            {
                if (String.IsNullOrWhiteSpace(name.Text)) { error.Text = "昵称不能为空"; SelectTab(0); name.Focus(); return; }
                try
                {
                    string path = ReceiveStorage.Folder(folder.Text); ReceiveStorage.ValidateWritable(path);
                    Value = new AppSettings { Avatar = avatarValue, Nickname = Protocol.Field(name.Text.Trim()), Group = String.IsNullOrWhiteSpace(group.Text) ? "我的局域网" : Protocol.Field(group.Text.Trim()), Signature = signature.Text.Trim(), CloseToTray = close.Checked, StartWithWindows = startup.Checked, Notifications = notifications.Checked,DiagnosticsEnabled = diagnostics.Checked, AutoSendDroppedFiles=autoDrop.Checked, AutoReceiveFiles = auto.Checked, ReceiveFolder = path, ChatFontSize=messageSize,ChatBackground=backgroundMode,BackgroundImage=backgroundPath,DarkMode=dark.Checked, FollowSystemTheme=follow.Checked, Accent=accentIndex==1?"green":accentIndex==2?"purple":"blue" };
                    DialogResult = DialogResult.OK;
                }
                catch (Exception e) { error.Text = "保存目录不可用：" + e.Message; SelectTab(2); folder.Focus(); }
            }, true); save.Size = new Size(96, 40); save.Margin = Padding.Empty; cancel.Margin = new Padding(0, 0, 12, 0); actionRow.Controls.Add(save); actionRow.Controls.Add(cancel); AcceptButton = save;
            // 页面只创建一次，切换仅改变可见性，尚未保存的控件状态不会丢失。
            dark.Top = 290; accent.Top = 291; info.Top = 900;
            Control[] fields = body.Controls.Cast<Control>().OrderBy(c => c.Top).ToArray();
            body.Controls.Clear();
            for(int i=0;i<pages.Length;i++) {
                pages[i]=new FlowLayoutPanel {FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(28,10,28,26),Visible=false,AccessibleName="设置内容："+tabNames[i],BackColor=Theme.Surface};
                body.Controls.Add(pages[i]);
            }
            int page=0;
            foreach (Control field in fields)
            {
                if(field==dark)page=1;else if(field==auto)page=2;else if(field==diagnostics||field==info)page=3;
                FlowLayoutPanel layout=pages[page];
                if (field == reset) continue;
                if (field == browse)
                {
                    TableLayoutPanel actions = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 44, Margin = new Padding(0, 0, 0, 12) };
                    actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                    browse.Dock = reset.Dock = DockStyle.Fill;
                    browse.Margin = new Padding(0, 0, 6, 0); reset.Margin = new Padding(6, 0, 0, 0); actions.Controls.Add(browse, 0, 0); actions.Controls.Add(reset, 1, 0); layout.Controls.Add(actions); continue;
                }
                string section = field == fields[0] ? "个人资料" : field == close ? "消息与窗口" : field == auto ? "文件接收" : field == autoDrop ? "文件发送" : field == dark ? "聊天外观" : field == diagnostics ? "故障诊断" : field == info ? "本机信息" : null;
                if (section != null) {
                    if(layout.Controls.Count>0) { Panel divider=new Panel { Height=1, BackColor=Theme.Line, Margin=new Padding(0,12,0,12) };layout.Controls.Add(divider); }
                    Label heading = Theme.Label(section, Theme.Section, Theme.Ink); heading.Height = 26; heading.Margin = new Padding(0, 0, 0, 6); layout.Controls.Add(heading);
                }
                if(field is Label && field != hint && field != info && field != startupHint && field != privacy && field != dropHint) field.Height=22;
                if(field is ToggleSwitch) field.Height=((ToggleSwitch)field).PreferredHeightFor(458);
                field.Margin = new Padding(0, 0, 0, field is Label ? 4 : 8); layout.Controls.Add(field);
            }
            // 侧栏：4 个分类自上而下排列，与内容区之间用细线分隔。
            for(int i=0;i<tabs.Length;i++) {
                int index=i;
                tabs[i]=new SettingsTabButton {Text=tabNames[i],Icon=tabIcons[i],AccessibleName="设置标签："+tabNames[i]};
                tabs[i].SetBounds(0, 18+i*46, nav.Width, 42);
                tabs[i].Click+=delegate {SelectTab(index);};
                tabs[i].KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right||e.KeyCode==Keys.Up||e.KeyCode==Keys.Down){int step=e.KeyCode==Keys.Right||e.KeyCode==Keys.Down?1:3;int next=(selectedTab+step)%4;SelectTab(next);tabs[next].Focus();e.Handled=true;}};
                nav.Controls.Add(tabs[i]);
            }
            Label navFooter=Theme.Label("飞Q 设置",Theme.Small,Theme.Muted); navFooter.SetBounds(20, nav.Height-34, nav.Width-30, 22); navFooter.Anchor=AnchorStyles.Left|AnchorStyles.Bottom; nav.Controls.Add(navFooter);
            Action fit = delegate {
                int width = Math.Max(160, body.Width - (int)(56 * DeviceDpi / 96F) - SystemInformation.VerticalScrollBarWidth);
                foreach(FlowLayoutPanel layout in pages) {
                    layout.SuspendLayout();
                    foreach (Control field in layout.Controls) {
                        field.Width = width;
                        ToggleSwitch toggle = field as ToggleSwitch;
                        if(toggle != null) field.Height = toggle.PreferredHeightFor(width);
                        else if(field == startupHint || field == hint || field == dropHint || field == privacy || field == info)
                            field.Height = TextRenderer.MeasureText(field.Text, field.Font, new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + 4;
                    }
                    layout.ResumeLayout(true);
                }
            };
            body.SizeChanged += delegate { fit(); }; fit();SelectTab(0);
            Theme.ThemeScrollbars(body);
        }
        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Height = Math.Min(Height, Screen.FromControl(this).WorkingArea.Height - 24); }
        private TextBox Input(string title, string value, int y, int max)
        {
            Label label = Theme.Label(title, Theme.Small, Theme.Muted); label.SetBounds(26, y, 458, 24); body.Controls.Add(label);
            RoundedInput input = new RoundedInput(); input.SetBounds(26, y + 28, 458, 40); input.Input.Text = value; input.Input.MaxLength = max; input.Input.AccessibleName = title; body.Controls.Add(input); return input.Input;
        }
    }
    public sealed class HistoryDialog : ModernWindow
    {
        public HistoryDialog(LocalStore store, Peer peer, MainForm main = null)
        {
            Text = "聊天记录 · " + peer.DisplayName; UseSecondaryChrome();ShowInTaskbar=false; ClientSize = new Size(760, 720); MinimumSize = new Size(520, 460); StartPosition = FormStartPosition.CenterParent;
            Surface.BackColor = Theme.Wallpaper;
            Panel top = new Panel { Dock = DockStyle.Top, Height = 106, Padding = new Padding(22, 10, 22, 12), BackColor = Theme.Surface };
            Label summary=Theme.Label("",Theme.Small,Theme.Muted);summary.Dock=DockStyle.Bottom;summary.Height=26;summary.AccessibleName="聊天记录搜索结果";top.Controls.Add(summary);
            SearchBox input = new SearchBox { Dock = DockStyle.Fill }; input.Input.AccessibleName = "搜索聊天记录"; input.Input.MaxLength=200;input.Placeholder = "搜索消息或文件名"; top.Controls.Add(input);input.BringToFront();
            BubbleHistory body = new BubbleHistory { Dock = DockStyle.Fill, Margin = new Padding(10), BackColor = Theme.Wallpaper, RecordLimit=500, CharacterLimit=Int32.MaxValue };
            Panel frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 10), BackColor = Theme.Wallpaper }; frame.Controls.Add(body); Surface.Controls.Add(frame); Surface.Controls.Add(top);
            try { var records = store.History(peer.Id, 500); Action render = () => {
                string query=input.Input.Text;
                var matches=records.Where(r=>(r.Text??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0||(r.Transfers??new FileTransferState[0]).Any(f=>(f.Name??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0)).ToArray();
                // 批量排版一次，逐条追加会在每次输入时重排整个记录。
                body.SetRecords(matches, query);
                summary.Text=query.Length==0?"最近 "+records.Count+" 条记录":"找到 "+matches.Length+" 条匹配记录 · 关键词已高亮";
            }; input.Input.TextChanged += delegate { render(); };
                if(main != null) body.DeleteRequested += delegate(ChatRecord[] selected) {
                    if(main.DeleteMessages(peer, selected)) { records.RemoveAll(r => selected.Any(s => s.Packet == r.Packet && s.Outgoing == r.Outgoing)); render(); }
                };
                render(); }
            catch (Exception e) { using (NoticeDialog error = new NoticeDialog("读取失败", e.Message)) error.ShowDialog(this); }
        }
    }
}
