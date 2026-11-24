using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FeiqLight
{
    internal static class DialogLayout
    {
        private static readonly Font ProseFont = new Font("Microsoft YaHei UI", 9.5F);
        private static readonly Font EmphasisFont = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        private static readonly Font MonoFont = new Font("Consolas", 9F);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PARAFORMAT2
        {
            public int cbSize;
            public uint dwMask;
            public ushort wNumbering;
            public ushort wReserved;
            public int dxStartIndent;
            public int dxRightIndent;
            public int dxOffset;
            public ushort wAlignment;
            public short cTabCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] rgxTabs;
            public int dySpaceBefore;
            public int dySpaceAfter;
            public int dyLineSpacing;
            public short sStyle;
            public byte bLineSpacingRule;
            public byte bOutlineLevel;
            public ushort wShadingWeight;
            public ushort wShadingStyle;
            public ushort wNumberingStart;
            public ushort wNumberingStyle;
            public ushort wNumberingTab;
            public ushort wBorderSpace;
            public ushort wBorderWidth;
            public ushort wBorders;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr w, ref PARAFORMAT2 pf);

        // 只读详情框：保留原生拖选、Ctrl+C/Ctrl+A 与滚轮，隐藏闪烁插入符；支持 MD3 行距与重点文本修饰。
        private sealed class ReadOnlyDetailsBox : RichTextBox
        {
            [DllImport("user32.dll")] private static extern bool HideCaret(IntPtr handle);
            private readonly bool formatted;

            internal ReadOnlyDetailsBox(bool formatted)
            {
                this.formatted = formatted;
                ReadOnly = true; BorderStyle = BorderStyle.None; DetectUrls = false; TabStop = false; Cursor = Cursors.IBeam;
                Theme.Changed += RefreshTypography;
            }
            private void SuppressCaret() { if (IsHandleCreated) HideCaret(Handle); }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                if (formatted) ApplyTypography();
                SuppressCaret();
            }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); SuppressCaret(); }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); SuppressCaret(); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); SuppressCaret(); }
            protected override void OnSelectionChanged(EventArgs e) { base.OnSelectionChanged(e); SuppressCaret(); }
            protected override void WndProc(ref Message m) { base.WndProc(ref m); if (m.Msg == 0x0007 || m.Msg == 0x0201 || m.Msg == 0x0202) SuppressCaret(); }
            private void RefreshTypography() { if (formatted && IsHandleCreated) ApplyTypography(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) Theme.Changed -= RefreshTypography;
                base.Dispose(disposing);
            }

            private void ApplyTypography()
            {
                string raw = Text;
                if (String.IsNullOrEmpty(raw)) return;
                int selectionStart = SelectionStart, selectionLength = SelectionLength;
                // RichEdit 内部按单个换行符计算字符索引，先规整 \r\n 避免高亮与分段偏移错位。
                string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
                PARAFORMAT2 pf = new PARAFORMAT2();
                pf.cbSize = Marshal.SizeOf(typeof(PARAFORMAT2));
                pf.dwMask = 0x00000100; // PFM_LINESPACING
                pf.bLineSpacingRule = 5;
                pf.dyLineSpacing = 27; // 1.35x 行距
                SelectAll();
                SelectionFont = ProseFont;
                SelectionColor = Theme.Ink;
                SendMessage(Handle, 0x0447, IntPtr.Zero, ref pf);

                // 修饰次要解释性分句与路径/IP/快捷键，突出重点而不更改实际 Text 内容。
                Color secondary = Theme.Blend(Theme.Ink, Theme.Muted, .60F);
                int semi = -1, comma = -1, quoted = 0;
                for (int i = 0; i < normalized.Length; i++)
                {
                    char c = normalized[i];
                    if (c == '「' || c == '『') quoted++;
                    else if (c == '」' || c == '』') quoted = Math.Max(0, quoted - 1);
                    else if (quoted == 0)
                    {
                        if (c == '；' && semi < 0) semi = i;
                        if (c == '，' && comma < 0) comma = i;
                    }
                }
                int breakIdx = normalized.IndexOf('\n');
                if (semi > 0 && semi + 1 < normalized.Length && (breakIdx < 0 || semi < breakIdx))
                {
                    Select(semi, normalized.Length - semi);
                    SelectionColor = secondary;
                }
                else
                {
                    if (comma > 0 && normalized.Length - comma > 10 && breakIdx < 0)
                    {
                        Select(comma, normalized.Length - comma);
                        SelectionColor = secondary;
                    }
                    else if (breakIdx > 0 && breakIdx + 1 < normalized.Length)
                    {
                        string firstLine = normalized.Substring(0, breakIdx).Trim();
                        bool firstIsPath = firstLine.Contains("\\") || firstLine.Contains(":/") || Regex.IsMatch(firstLine, @"^\d+\.\d+\.\d+\.\d+");
                        if (firstIsPath)
                        {
                            Select(0, breakIdx);
                            SelectionColor = Theme.OnPrimaryContainer;
                        }
                        Select(breakIdx + 1, normalized.Length - breakIdx - 1);
                        SelectionColor = firstIsPath ? Theme.Ink : secondary;
                    }
                }
                foreach (Match m in Regex.Matches(normalized, @"(Ctrl\+Alt\+F12|(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?|[「『][^」』]{1,32}[」』])"))
                {
                    Select(m.Index, m.Length);
                    SelectionFont = m.Value.StartsWith("「") || m.Value.StartsWith("『") ? EmphasisFont : MonoFont;
                    SelectionColor = Theme.OnPrimaryContainer;
                }
                foreach (Match m in Regex.Matches(normalized, @"(?<=^|[，；\n\s])(?:这台电脑|本机|不可恢复|无法撤销)"))
                {
                    Select(m.Index, m.Length);
                    SelectionFont = EmphasisFont;
                    SelectionColor = Theme.Ink;
                }
                foreach (Match m in Regex.Matches(normalized, @"(?m)^[ \t]*(?:\d{4,}|\d{2,}(?:[ \t-]+\d{2,})+|[A-Fa-f0-9]{4}(?:[ \t-]+[A-Fa-f0-9]{4}){3})[ \t]*$"))
                {
                    Select(m.Index, m.Length);
                    SelectionFont = MonoFont;
                    SelectionColor = Theme.OnPrimaryContainer;
                }
                Select(selectionStart, selectionLength);
            }
        }

        internal static Panel Body(ModernWindow window)
        {
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 4, 24, 14) };
            window.Surface.Controls.Add(body); return body;
        }
        internal static void StyleSecondary(Button button)
        {
            RoundedButton rounded = button as RoundedButton;
            button.BackColor = Theme.Surface;
            button.ForeColor = Theme.Ink;
            if (rounded != null) { rounded.Border = Theme.OutlineVariant; rounded.Radius = 8; }
        }
        internal static void Actions(ModernWindow window, Button primary, Button secondary = null)
        {
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(24, 12, 24, 12), BackColor = Theme.SurfaceContainerLow };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen line = new Pen(Theme.OutlineVariant)) e.Graphics.DrawLine(line, 0, 0, footer.Width, 0); };
            FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.SurfaceContainerLow };
            RoundedButton primaryRounded = primary as RoundedButton;
            if (primaryRounded != null) primaryRounded.Radius = 8;
            if (secondary != null) StyleSecondary(secondary);
            foreach (Button button in new[] { primary, secondary }.Where(b => b != null))
            {
                button.Size = new Size(Math.Max(88, TextRenderer.MeasureText(button.Text, button.Font).Width + 36), 36);
                button.Margin = new Padding(10, 0, 0, 0); row.Controls.Add(button);
            }
            footer.Controls.Add(row); window.Surface.Controls.Add(footer); window.AcceptButton = primary; window.CancelButton = secondary ?? primary;
        }
        internal static int MeasureBodyHeight(string text, int width, bool prose = false)
        {
            Font font = prose ? ProseFont : Theme.Body;
            int raw = TextRenderer.MeasureText(text ?? "", font, new Size(Math.Max(120, width), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
            return prose ? (int)Math.Ceiling(raw * 1.44) + 12 : raw;
        }
        internal static Control ReadOnlyText(string text, string accessible, bool framed, Action<int> onContentHeight = null)
        {
            Color surface = framed ? Theme.Ice : Theme.Surface;
            ReadOnlyDetailsBox details = new ReadOnlyDetailsBox(!framed)
            {
                Dock = DockStyle.Fill, Text = text ?? "", Font = framed ? Theme.Body : ProseFont, BackColor = surface,
                ForeColor = Theme.Ink,
                ScrollBars = framed ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None,
                AccessibleName = accessible
            };
            Theme.AttachEditMenu(details); Theme.ThemeScrollbars(details);
            if (!framed)
            {
                Panel wrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 2) };
                if (onContentHeight != null)
                {
                    bool adjusting = false;
                    details.ContentsResized += delegate(object sender, ContentsResizedEventArgs e)
                    {
                        if (adjusting || e.NewRectangle.Height <= 0) return;
                        adjusting = true;
                        try { onContentHeight(e.NewRectangle.Height + wrap.Padding.Vertical); }
                        finally { adjusting = false; }
                    };
                }
                wrap.Controls.Add(details); return wrap;
            }
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Color.Empty, Radius = 8, Padding = new Padding(14, 12, 10, 12) };
            card.Controls.Add(details); return card;
        }
        internal static void FitTextHeight(Form form, Control wrap, int neededHeight, int maxHeight)
        {
            if (form.IsDisposed || wrap.IsDisposed || !wrap.IsHandleCreated) return;
            float scale;
            using (Graphics g = form.CreateGraphics()) scale = Math.Max(1F, g.DpiY / 96F);
            int limit = Math.Max(1, Math.Min(Screen.FromControl(form).WorkingArea.Height - (int)Math.Round(64 * scale), (int)Math.Round(maxHeight * scale)));
            int delta = neededHeight + 6 - wrap.Height;
            int target = Math.Min(limit, form.ClientSize.Height + Math.Max(0, delta));
            if ((delta > 2 || form.ClientSize.Height > limit) && target != form.ClientSize.Height) form.ClientSize = new Size(form.ClientSize.Width, target);
            RichTextBox details = wrap.Controls.OfType<RichTextBox>().FirstOrDefault();
            if (details != null)
            {
                RichTextBoxScrollBars scrollBars = neededHeight > wrap.ClientSize.Height ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.None;
                if (details.ScrollBars != scrollBars) details.ScrollBars = scrollBars;
            }
        }
        internal static RoundedPanel OptionCard(ToggleSwitch toggle)
        {
            toggle.Dock = DockStyle.Fill; toggle.BackColor = Theme.Ice;
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Bottom, Height = 46, Fill = Theme.Ice, Border = Color.Empty, Radius = 8, Padding = new Padding(14, 2, 12, 2), Margin = new Padding(0, 8, 0, 0) };
            card.Controls.Add(toggle); return card;
        }
    }
    internal sealed class ConfirmDialog : ModernWindow
    {
        private readonly ToggleSwitch option;
        internal bool OptionChecked { get { return option != null && option.Checked; } }
        internal ConfirmDialog(string title, string text, string confirm, bool danger, string optionText = null)
        {
            int rawMeasured = DialogLayout.MeasureBodyHeight(text, 404, false);
            bool framed = rawMeasured > 140 || (text ?? "").Length > 240;
            int width = framed ? 512 : 452;
            int measured = DialogLayout.MeasureBodyHeight(text, width - 48, !framed);
            int contentHeight = Math.Max(34, Math.Min(240, measured + (framed ? 26 : 4)));
            int optionHeight = optionText == null ? 0 : 56;
            Text = title; ResizeWindow = false;
            ClientSize = new Size(width, Math.Max(154, Math.Min(476, 128 + contentHeight + optionHeight)));
            MinimumSize = new Size(340, 150); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this);
            Control content = null;
            content = DialogLayout.ReadOnlyText(text, "确认详情", framed, h => DialogLayout.FitTextHeight(this, content, h, 490));
            body.Controls.Add(content);
            if (optionText != null)
            {
                Panel gap = new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Theme.Surface };
                option = new ToggleSwitch { Text = optionText, AccessibleName = optionText };
                RoundedPanel optionCard = DialogLayout.OptionCard(option);
                body.Controls.Add(gap); body.Controls.Add(optionCard);
            }
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
            Text="关于飞Q";ResizeWindow=false;ClientSize=new Size(432,374);MinimumSize=new Size(380,340);StartPosition=FormStartPosition.CenterParent;
            TableLayoutPanel body=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(22,8,22,12)};
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int height in new[]{78,28,72,68})body.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            TableLayoutPanel brand=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,76));brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));brand.RowStyles.Add(new RowStyle(SizeType.Percent,56));brand.RowStyles.Add(new RowStyle(SizeType.Percent,44));
            Panel icon=new Panel {Dock=DockStyle.Fill,Margin=new Padding(0,4,14,8),AccessibleName="飞Q图标"};icon.Paint+=delegate(object sender,PaintEventArgs e){e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;int size=Math.Min(icon.Width,icon.Height);e.Graphics.DrawImage(logo,new Rectangle(0,0,size,size));};brand.Controls.Add(icon,0,0);brand.SetRowSpan(icon,2);
            Label title=Theme.Label("飞Q",Theme.Title,Theme.Ink);title.Font=new Font(Theme.Body.FontFamily,16F,FontStyle.Bold);title.Dock=DockStyle.Fill;brand.Controls.Add(title,1,0);
            Label version=Theme.Label("Windows  "+typeof(AboutDialog).Assembly.GetName().Version.ToString(3),Theme.Small,Theme.Muted);version.Dock=DockStyle.Fill;brand.Controls.Add(version,1,1);body.Controls.Add(brand,0,0);
            Label intro=Theme.Label("局域网聊天与文件传输 · 无需服务器",Theme.Body,Theme.Muted);intro.Dock=DockStyle.Fill;intro.Margin=Padding.Empty;body.Controls.Add(intro,0,1);
            RoundedPanel card=new RoundedPanel {Dock=DockStyle.Fill,Fill=Theme.Ice,Border=Color.Empty,Radius=8,Padding=new Padding(14,8,14,8),Margin=new Padding(0,4,0,8)};
            Panel sourceIcon=new Panel {Dock=DockStyle.Left,Width=36,BackColor=Theme.Ice};sourceIcon.Paint+=delegate(object sender,PaintEventArgs e){Theme.Glyph(e.Graphics,"code",new Rectangle(0,(sourceIcon.Height-24)/2,24,24),Theme.Blue);};
            LinkLabel repository=new LinkLabel {Dock=DockStyle.Fill,Text="GitHub 仓库\ngithub.com/noway1467/fq",Font=Theme.Body,BackColor=Theme.Ice,LinkColor=Theme.OnPrimaryContainer,ActiveLinkColor=Theme.Blue,VisitedLinkColor=Theme.OnPrimaryContainer,TextAlign=ContentAlignment.MiddleLeft,LinkBehavior=LinkBehavior.HoverUnderline,AccessibleName="GitHub 仓库：github.com/noway1467/fq"};repository.Links.Add(0,repository.Text.Length,RepositoryUrl);repository.LinkClicked+=delegate {MessageLinks.Open(repository,RepositoryUrl);};card.Controls.Add(repository);card.Controls.Add(sourceIcon);body.Controls.Add(card,0,2);
            Label note=Theme.Label("新版设备默认 TLS 加密 · 首次核对配对码\n内置表情与头像：Twemoji · CC BY 4.0\n© Twitter, Inc. 及贡献者；许可随程序内置。",Theme.Small,Theme.Muted);note.Dock=DockStyle.Fill;note.Margin=Padding.Empty;body.Controls.Add(note,0,3);
            Surface.Controls.Add(body);
            Button done=Theme.Button("确定",delegate {DialogResult=DialogResult.OK;},true);
            DialogLayout.Actions(this,done);
        }
        protected override void Dispose(bool disposing) {if(disposing)logo.Dispose();base.Dispose(disposing);}
    }
    public sealed class NoticeDialog : ModernWindow
    {
        public NoticeDialog(string title, string text) : this(title, text, null, null) { }
        public NoticeDialog(string title, string text, string linkText, string linkUrl)
        {
            bool hasLink = !String.IsNullOrWhiteSpace(linkText) && !String.IsNullOrWhiteSpace(linkUrl);
            int rawMeasured = DialogLayout.MeasureBodyHeight(text, 404, false);
            bool framed = rawMeasured > 140 || (text ?? "").Length > 240;
            int width = framed ? 528 : 452;
            int measured = DialogLayout.MeasureBodyHeight(text, width - 48, !framed);
            int contentHeight = Math.Max(34, Math.Min(290, measured + (framed ? 28 : 4)));
            Text = title; ResizeWindow = false;
            ClientSize = new Size(width, Math.Max(154, Math.Min(476, 128 + contentHeight + (hasLink ? 34 : 0))));
            MinimumSize = new Size(340, 150); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this);
            Control content = null;
            content = DialogLayout.ReadOnlyText(text, "提示详情", framed, h => DialogLayout.FitTextHeight(this, content, h, 490));
            body.Controls.Add(content);
            if (hasLink)
            {
                Panel linkPanel = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(0, 6, 0, 0) };
                LinkLabel link = new LinkLabel { Dock = DockStyle.Fill, Text = linkText, Font = Theme.Body, BackColor = Color.Transparent, LinkColor = Theme.Blue, ActiveLinkColor = Theme.Blue, VisitedLinkColor = Theme.Blue, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = linkText };
                link.Links.Add(0, link.Text.Length, linkUrl);
                link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e) { MessageLinks.Open(link, e.Link.LinkData as string); };
                linkPanel.Controls.Add(link); body.Controls.Add(linkPanel);
            }
            Button done = Theme.Button("确定", delegate { DialogResult = DialogResult.OK; }, true); DialogLayout.Actions(this, done);
            Shown += delegate { done.Focus(); };
        }
    }
    public sealed class MessageTextDialog : ModernWindow
    {
        public MessageTextDialog(string text)
        {
            Text = "消息全文"; ClientSize = new Size(640, 540); MinimumSize = new Size(380, 280); StartPosition = FormStartPosition.CenterParent;
            RichTextBox content = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Theme.Body,
                BackColor = Theme.Ice, ForeColor = Theme.Ink, DetectUrls = false, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Vertical,
                Text = MessageText.NormalizeLineBreaks(text), AccessibleName = "消息全文（可选择复制）" };
            Theme.AttachEditMenu(content); Theme.ThemeScrollbars(content);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Color.Empty, Radius = 8, Padding = new Padding(14, 12, 10, 10) };
            card.Controls.Add(content);
            Panel frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 8, 22, 12) }; frame.Controls.Add(card);
            Surface.Controls.Add(frame);
            Button close = Theme.Button("关闭", delegate { DialogResult = DialogResult.OK; }, false);
            DialogLayout.StyleSecondary(close);
            DialogLayout.Actions(this, close);
            Shown += delegate { content.Focus(); content.Select(0, 0); };
        }
    }
    internal sealed class ContactProfileDialog : ModernWindow
    {
        private readonly PictureBox avatar;
        internal ContactProfileDialog(Peer peer, Func<string, bool> save)
        {
            bool department = !String.IsNullOrWhiteSpace(peer.Group);
            Text = "联系人资料"; ResizeWindow = false; ClientSize = new Size(408, department ? 458 : 398); MinimumSize = new Size(356, 360); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this); body.AutoScroll = true;
            // 内容独立滚动，较小工作区也能查看 IP，底部保存区始终可见。
            TableLayoutPanel fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = department ? 7 : 5, Margin = Padding.Empty };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in department ? new[] { 124, 24, 48, 24, 34, 24, 34 } : new[] { 124, 24, 48, 24, 34 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            avatar = new PictureBox { Image = Theme.ContactAvatar(96, peer.Id, peer.Online, peer.Avatar), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(116, 116), Anchor = AnchorStyles.None, Margin = Padding.Empty, Cursor = Cursors.Hand, AccessibleName = "联系人资料头像", AccessibleDescription = "点击查看大图" }; fields.Controls.Add(avatar, 0, 0);
            avatar.Click += delegate {
                try { using(AvatarPreviewDialog preview = new AvatarPreviewDialog(peer.Id, peer.Avatar)) preview.ShowDialog(this); }
                catch(Exception error) { using(NoticeDialog notice = new NoticeDialog("无法查看头像", error.Message)) notice.ShowDialog(this); }
            };
            Label name = Theme.Label("昵称", Theme.Small, Theme.Muted); name.Dock = DockStyle.Fill; fields.Controls.Add(name, 0, 1);
            RoundedInput input = new RoundedInput { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) }; input.Input.MaxLength = 64; input.Input.Text = String.IsNullOrEmpty(peer.Note) ? peer.Nickname : peer.Note; input.Input.AccessibleName = "联系人资料昵称"; input.Placeholder = "留空恢复对方昵称"; fields.Controls.Add(input, 0, 2);
            if (department) {
                Label title = Theme.Label("部门", Theme.Small, Theme.Muted); title.Dock = DockStyle.Fill; fields.Controls.Add(title, 0, 3);
                Label group = Theme.Label(peer.Group, Theme.Body, Theme.Ink); group.Dock = DockStyle.Fill; group.AutoEllipsis = true; group.AccessibleName = "联系人资料部门"; fields.Controls.Add(group, 0, 4);
            }
            int addressRow = department ? 5 : 3;
            Label addressTitle = Theme.Label("IP 地址", Theme.Small, Theme.Muted); addressTitle.Dock = DockStyle.Fill; fields.Controls.Add(addressTitle, 0, addressRow);
            Label address = Theme.Label(peer.Endpoint.ToString(), Theme.Body, Theme.Ink); address.Dock = DockStyle.Fill; address.AccessibleName = "联系人资料地址"; fields.Controls.Add(address, 0, addressRow + 1);
            body.Controls.Add(fields);
            Button done = Theme.Button("保存", delegate { string value = input.Input.Text.Trim(); if (save(value == peer.Nickname ? "" : value)) DialogResult = DialogResult.OK; }, true);
            DialogLayout.Actions(this, done, Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false));
        }
        protected override void Dispose(bool disposing) { if (disposing && avatar.Image != null) { avatar.Image.Dispose(); avatar.Image = null; } base.Dispose(disposing); }
    }
    internal sealed class AvatarPreviewDialog : ModernWindow
    {
        private readonly PictureBox picture;
        internal AvatarPreviewDialog(string identity, string avatar)
        {
            Text = "头像"; ClientSize = new Size(460, 500); MinimumSize = new Size(280, 320); StartPosition = FormStartPosition.CenterParent;
            Bitmap image;
            string value = Protocol.NormalizeAvatar(avatar);
            if(value != null && value.StartsWith("image:", StringComparison.Ordinal)) {
                using(var bytes = new System.IO.MemoryStream(Convert.FromBase64String(value.Substring(6)))) using(Image source = Image.FromStream(bytes)) image = new Bitmap(source);
            } else image = Theme.ContactAvatar(320, identity, false, value);
            picture = new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "头像大图" };
            Panel body = DialogLayout.Body(this); body.Controls.Add(picture);
            Button close = Theme.Button("关闭", delegate { DialogResult = DialogResult.OK; }, false);
            DialogLayout.StyleSecondary(close);
            DialogLayout.Actions(this, close);
        }
        protected override void Dispose(bool disposing) { if(disposing && picture.Image != null) { picture.Image.Dispose(); picture.Image = null; } base.Dispose(disposing); }
    }
    public sealed class ConversationDialog : ModernWindow
    {
        public string Note { get { return note.Text.Trim(); } }
        public bool DeleteHistory { get { return history.Checked; } }
        private readonly TextBox note;
        private readonly ToggleSwitch history;
        public ConversationDialog(Peer peer, bool deleting, bool clearOnly = false)
        {
            Text=clearOnly?"清空聊天记录":deleting?"删除联系人":"设置备注"; ResizeWindow=false;
            string message=clearOnly?"仅清空本机聊天记录，已接收文件保留。":"仅从本机列表移除，已接收文件保留；收到新消息会自动恢复。";
            int introHeight=deleting?Math.Max(38,DialogLayout.MeasureBodyHeight(message,408,true)+4):0;
            ClientSize=new Size(456,deleting?194+introHeight:206); MinimumSize=new Size(368,188); StartPosition=FormStartPosition.CenterParent;
            Panel body=DialogLayout.Body(this);
            if(deleting) {
                Control intro=DialogLayout.ReadOnlyText(message,"操作说明",false);
                intro.Dock=DockStyle.Top;intro.Height=introHeight;
                body.Controls.Add(intro);
            } else {
                Label caption=Theme.Label("备注名称",Theme.Section,Theme.Ink);caption.Dock=DockStyle.Top;caption.Height=26;body.Controls.Add(caption);
            }
            RoundedInput input=new RoundedInput {Dock=DockStyle.Top,Height=44};note=input.Input;note.MaxLength=64;note.Text=peer.Note??"";note.AccessibleName="联系人备注";input.Placeholder="留空恢复对方昵称";input.Visible=!deleting;
            Panel inputGap=new Panel {Dock=DockStyle.Top,Height=6,BackColor=Theme.Surface,Visible=!deleting};
            body.Controls.Add(inputGap);inputGap.BringToFront();body.Controls.Add(input);input.BringToFront();
            history=new ToggleSwitch {Text="同时删除聊天记录",Checked=clearOnly,Enabled=!clearOnly,AccessibleName="同时删除聊天记录"};
            RoundedPanel optionCard=DialogLayout.OptionCard(history);optionCard.Dock=DockStyle.Top;optionCard.Visible=deleting;
            Panel optionGap=new Panel {Dock=DockStyle.Top,Height=10,BackColor=Theme.Surface,Visible=deleting};
            body.Controls.Add(optionGap);optionGap.BringToFront();body.Controls.Add(optionCard);optionCard.BringToFront();
            Button cancel=Theme.Button("取消",delegate{DialogResult=DialogResult.Cancel;},false);
            Button done=Theme.Button(deleting?"确认删除":"保存",delegate{DialogResult=DialogResult.OK;},true);DialogLayout.Actions(this,done,cancel);
            if(deleting) done.BackColor=Theme.Danger;
            Shown+=delegate { if(deleting) cancel.Focus(); else note.Focus(); };
        }
    }
    public sealed class AddressDialog : ModernWindow
    {
        private readonly TextBox address;
        public IPEndPoint Endpoint { get; private set; }
        public AddressDialog(int port)
        {
            Text = "添加联系人"; ResizeWindow = false; ClientSize = new Size(436, 224); MinimumSize = new Size(368, 210); StartPosition = FormStartPosition.CenterParent;
            Panel body = DialogLayout.Body(this);
            Label label = Theme.Label("对方 IPv4 地址", Theme.Section, Theme.Ink); label.Dock=DockStyle.Top;label.Height=26;body.Controls.Add(label);
            RoundedInput input = new RoundedInput {Dock=DockStyle.Top,Height=44}; address = input.Input; address.AccessibleName = "好友 IP 地址"; input.Placeholder = "例如 192.168.1.100（默认端口 " + port + "）"; body.Controls.Add(input);input.BringToFront();
            Label error = Theme.Label("", Theme.Small, Theme.Danger); error.Dock=DockStyle.Top;error.Height=28;error.Padding=new Padding(2,6,0,0);error.TextAlign=ContentAlignment.TopLeft;body.Controls.Add(error);error.BringToFront();
            Button add = Theme.Button("添加", delegate { try { Endpoint = Parse(address.Text); DialogResult = DialogResult.OK; } catch (ArgumentException e) { error.Text = e.Message; address.Focus(); } }, true);
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
            Text = attachments<0?"恢复已移除的联系人":attachments>0?"转发 "+attachments+" 个附件":"转发到"; ClientSize = new Size(432, 456); MinimumSize = new Size(368, 320); ResizeWindow = false; StartPosition = FormStartPosition.CenterParent;
            ListBox list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Theme.Body, BackColor = Theme.SurfaceContainerLow, ForeColor = Theme.Ink, IntegralHeight = false };
            foreach (Peer peer in peers) list.Items.Add(peer.DisplayName + "  ·  " + peer.Endpoint);
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 58;
            list.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                if (e.Index < 0) return; Peer peer = peers[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0; float scale = e.Graphics.DpiX / 96F;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush clear = new SolidBrush(Theme.SurfaceContainerLow)) e.Graphics.FillRectangle(clear, e.Bounds);
                if (selected)
                {
                    RectangleF card = new RectangleF(e.Bounds.Left + 4F * scale, e.Bounds.Top + 2F * scale, Math.Max(4F, e.Bounds.Width - 8F * scale), Math.Max(4F, e.Bounds.Height - 4F * scale));
                    using (GraphicsPath path = Theme.RoundedF(card, 6F * scale))
                    using (SolidBrush brush = new SolidBrush(Theme.Sky)) e.Graphics.FillPath(brush, path);
                }
                Bitmap avatar = Theme.CachedAvatar(40, peer.Id, false, peer.Avatar); e.Graphics.DrawImageUnscaled(avatar, e.Bounds.Left + (int)(12F * scale) - Theme.AvatarHalo(40), e.Bounds.Top + (e.Bounds.Height - 40) / 2 - Theme.AvatarHalo(40));
                int textLeft = e.Bounds.Left + (int)(62F * scale), textWidth = Math.Max(1, e.Bounds.Right - textLeft - (int)(12F * scale));
                TextRenderer.DrawText(e.Graphics, peer.DisplayName, Theme.Section, new Rectangle(textLeft, e.Bounds.Top + (int)(8F * scale), textWidth, (int)(22 * scale)), selected ? Theme.OnPrimaryContainer : Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, (peer.DuplicateName?"#"+peer.ConversationNumber+" · ":"")+peer.Endpoint, Theme.Small, new Rectangle(textLeft, e.Bounds.Top + (int)(30F * scale), textWidth, (int)(18 * scale)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (!selected && e.Index < peers.Length - 1) using (Pen sep = new Pen(Theme.OutlineVariant)) e.Graphics.DrawLine(sep, textLeft, e.Bounds.Bottom - 1, e.Bounds.Right - (int)(12F * scale), e.Bounds.Bottom - 1);
                if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
            };
            Theme.ThemeScrollbars(list);
            Button send = Theme.Button(attachments<0?"恢复会话":"确认转发", delegate { if (list.SelectedIndex >= 0) { Target = peers[list.SelectedIndex]; DialogResult = DialogResult.OK; } }, true);
            Button cancel = Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false);
            send.Enabled = false; list.SelectedIndexChanged += delegate { send.Enabled = list.SelectedIndex >= 0; };
            Panel frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 4, 24, 14) };
            RoundedPanel listShell = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.SurfaceContainerLow, Border = Color.Empty, Radius = 8, Padding = new Padding(4) };
            if (peers.Length == 0) {
                Label empty = Theme.Label(attachments<0?"没有已移除的联系人":"当前没有在线联系人", Theme.Body, Theme.Muted); empty.Dock = DockStyle.Fill; empty.BackColor = Theme.SurfaceContainerLow; empty.TextAlign = ContentAlignment.MiddleCenter; listShell.Controls.Add(empty);
            }
            listShell.Controls.Add(list);
            if (peers.Length == 0) list.Visible = false;
            frame.Controls.Add(listShell);
            Surface.Controls.Add(frame); DialogLayout.Actions(this, send, cancel);
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
        private readonly Button localImage, useAvatar;
        private int selectionVersion;
        public string SelectedValue { get; private set; }
        internal AvatarPickerDialog(string value, string identity)
        {
            this.identity = identity; Text = "更换头像"; ResizeWindow = false; ClientSize = new Size(496, 476); MinimumSize = new Size(460, 460); StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            RoundedPanel topCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Color.Empty, Radius = 8, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 10) };
            TableLayoutPanel top = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            preview.BackColor = Theme.Ice;
            top.Controls.Add(preview, 0, 0);
            localImage = Theme.Button("选择本地图片…", async delegate {
                using(OpenFileDialog picker = new OpenFileDialog { Title = "选择头像图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif", CheckFileExists = true }) if(picker.ShowDialog(this) == DialogResult.OK) {
                    Activate();
                    string chosen = picker.FileName;
                    await ImportImageAsync(System.Threading.Tasks.Task.Run(() => EmojiAvatars.FromLocalImage(chosen)));
                } else Activate();
            }, false); localImage.Dock = DockStyle.Fill; localImage.Margin = new Padding(12, 10, 6, 10); DialogLayout.StyleSecondary(localImage); top.Controls.Add(localImage, 1, 0);
            Button reset = Theme.Button("使用默认头像", delegate { SetAvatar("default"); }, false); reset.Dock = DockStyle.Fill; reset.Margin = new Padding(6, 10, 0, 10); DialogLayout.StyleSecondary(reset); top.Controls.Add(reset, 2, 0);
            topCard.Controls.Add(top);
            content.Controls.Add(topCard, 0, 0);

            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 4, Margin = Padding.Empty, AccessibleName = "内置头像列表" };
            for(int i = 0; i < 6; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 6));
            for(int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            for(int i = 0; i < choices.Length; i++) { int index = i; AvatarChoice choice = new AvatarChoice(i) { Dock = DockStyle.Fill, Margin = new Padding(4) }; choice.Click += delegate { SetAvatar("builtin:" + index); }; choices[i] = choice; grid.Controls.Add(choice, i % 6, i / 6); }
            content.Controls.Add(grid, 0, 1);

            Panel body = DialogLayout.Body(this); body.Controls.Add(content);
            useAvatar = Theme.Button("使用此头像", delegate { DialogResult = DialogResult.OK; }, true);
            DialogLayout.Actions(this, useAvatar, Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false));
            SetAvatar(Protocol.NormalizeAvatar(value) ?? "default");
        }
        private void SetAvatar(string value) {
            selectionVersion++; localImage.Enabled = useAvatar.Enabled = true; localImage.Text = "选择本地图片…";
            SelectedValue = value; Image old = preview.Image; preview.Image = Theme.ContactAvatar(64, identity, false, value); if(old != null) old.Dispose();
            for(int i = 0; i < choices.Length; i++) { choices[i].Active = value == "builtin:" + i; choices[i].Invalidate(); }
        }
        internal void ImportImage(string path) { SetAvatar(EmojiAvatars.FromLocalImage(path)); }
        private bool CurrentImage(int version) { return !IsDisposed && !Disposing && Visible && selectionVersion == version; }
        internal async System.Threading.Tasks.Task ImportImageAsync(System.Threading.Tasks.Task<string> image)
        {
            int version = ++selectionVersion;
            localImage.Enabled = useAvatar.Enabled = false; localImage.Text = "处理图片中…";
            try {
                string value = await image;
                // 后选的内置/默认头像优先；取消或关闭窗口后的结果也不能再回写。
                if(CurrentImage(version)) SetAvatar(value);
            }
            catch(Exception error) {
                if(CurrentImage(version)) using(NoticeDialog notice = new NoticeDialog("无法使用图片", error.Message)) notice.ShowDialog(this);
            }
            finally {
                if(CurrentImage(version)) { localImage.Enabled = useAvatar.Enabled = true; localImage.Text = "选择本地图片…"; }
            }
        }
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
        private string backgroundMode, backgroundPath;
        private int backgroundVersion;
        private Button image, save;
        private Action refreshPattern;
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
            }, false); avatarButton.Width = 118; avatarButton.Anchor = AnchorStyles.Left; DialogLayout.StyleSecondary(avatarButton); avatarRow.Controls.Add(avatarButton, 1, 0); body.Controls.Add(avatarRow);
            TextBox name = Input("昵称", current.Nickname, 8, 32), group = Input("部门", current.Group, 80, 32), signature = Input("个性签名", current.Signature, 152, 80);
            ToggleSwitch close = new ToggleSwitch { Text = "关闭时收起到托盘", Checked = current.CloseToTray, BackColor = Theme.Surface }; close.SetBounds(26, 232, 458, 42); body.Controls.Add(close);
            ToggleSwitch notifications = new ToggleSwitch { Text = "新消息气泡通知", Checked = current.Notifications, BackColor = Theme.Surface }; notifications.SetBounds(26, 274, 458, 42); body.Controls.Add(notifications);
            ToggleSwitch startup = new ToggleSwitch { Text = "开机自启动", AccessibleName = "开机自启动", Checked = startupAvailable ? startupEnabled : current.StartWithWindows, Enabled = startupAvailable, BackColor = Theme.Surface }; startup.SetBounds(26, 275, 458, 42); body.Controls.Add(startup);
            ToggleSwitch auto = new ToggleSwitch { Text = "自动接收文件", AccessibleName = "自动接收文件", Description = "仅用于可信局域网，不自动运行文件", Checked = current.AutoReceiveFiles, BackColor = Theme.Surface }; auto.SetBounds(26, 316, 458, 52); body.Controls.Add(auto);
            ToggleSwitch autoDrop=new ToggleSwitch {Text="拖入文件自动发送",AccessibleName="拖入文件自动发送",Description="仅发本次拖入文件，不夹带草稿或已排队附件",Checked=current.AutoSendDroppedFiles,BackColor=Theme.Surface};autoDrop.SetBounds(26,520,458,52);body.Controls.Add(autoDrop);
            ToggleSwitch legacy=new ToggleSwitch {Text="允许旧版明文协议",AccessibleName="允许旧版明文协议",Description="兼容旧设备明文；已配对设备与共享远控始终加密",Checked=current.AllowLegacyProtocol,BackColor=Theme.Surface};legacy.SetBounds(26,280,458,52);body.Controls.Add(legacy);
            TextBox folder = Input("文件保存文件夹", current.ReceiveFolder ?? AppSettings.DefaultReceiveFolder, 404, 240); folder.Parent.Width = 350;
            Button browse = Theme.Button("选择文件夹", delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择飞Q接收文件的保存文件夹", SelectedPath = folder.Text, ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            }, false); browse.SetBounds(386, 434, 96, 40); DialogLayout.StyleSecondary(browse); body.Controls.Add(browse);
            Button reset = Theme.Button("恢复默认目录", delegate { folder.Text = AppSettings.DefaultReceiveFolder; }, false); reset.SetBounds(26, 486, 146, 36); DialogLayout.StyleSecondary(reset); body.Controls.Add(reset);
            Label info = Theme.Label("端口 " + port + " · 资料目录 " + dataPath, Theme.Small, Theme.Muted); info.SetBounds(26, 532, 458, 40); info.AutoEllipsis = true; body.Controls.Add(info);
            ToggleSwitch dark=new ToggleSwitch { Text="夜间模式",AccessibleName="夜间模式",Checked=current.DarkMode,BackColor=Theme.Surface }; dark.SetBounds(26,590,458,42);body.Controls.Add(dark);
            ToggleSwitch follow=new ToggleSwitch { Text="跟随系统主题",AccessibleName="跟随系统切换",Checked=current.FollowSystemTheme,BackColor=Theme.Surface };follow.Top=290;body.Controls.Add(follow);
            Action followTheme=delegate {dark.Enabled=!follow.Checked;if(follow.Checked)dark.Checked=Theme.SystemDark;};follow.CheckedChanged+=delegate {followTheme();};followTheme();
            string[] accentKeys = { "blue", "green", "purple", "amber", "rose", "slate" };
            string[] accentNames = { "经典蓝", "青绿色", "紫罗兰", "琥珀金", "绯樱粉", "墨韵灰" };
            int accentIndex = Math.Max(0, Array.IndexOf(accentKeys, Theme.NormalizeAccent(current.Accent)));
            ToggleSwitch diagnostics=new ToggleSwitch {Text="记录故障诊断",AccessibleName="记录故障诊断",Description="仅记时间与事件码（限 128 KiB），不含聊天与路径",Checked=current.DiagnosticsEnabled,BackColor=Theme.Surface};diagnostics.SetBounds(26,700,458,52);body.Controls.Add(diagnostics);
            Diagnostics journal=new Diagnostics(dataPath,()=>current.DiagnosticsEnabled);
            Button viewLog=Theme.Button("查看诊断记录",delegate {try {using(NoticeDialog dialog=new NoticeDialog("诊断记录（可选择复制）",journal.Read()))dialog.ShowDialog(this);}catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("读取失败",e.Message))dialog.ShowDialog(this);}},false);viewLog.SetBounds(26,798,168,36);DialogLayout.StyleSecondary(viewLog);body.Controls.Add(viewLog);
            Button clearLog=Theme.Button("清除诊断记录",delegate {try {journal.Clear();}catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("清除失败",e.Message))dialog.ShowDialog(this);}},false);clearLog.SetBounds(204,798,168,36);DialogLayout.StyleSecondary(clearLog);body.Controls.Add(clearLog);
            TableLayoutPanel accent = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, Height = 104, AccessibleName = "主题颜色", Margin = new Padding(0, 4, 0, 10) };
            for (int r = 0; r < 2; r++) accent.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            Button[] choices = new Button[accentKeys.Length];
            Action refreshAccent = delegate { for (int i = 0; i < choices.Length; i++) { choices[i].Text = (i == accentIndex ? "✓ " : "") + accentNames[i]; choices[i].BackColor = i == accentIndex ? Theme.Sky : Theme.Ice; choices[i].ForeColor = i == accentIndex ? Theme.Blue : Theme.Ink; choices[i].Invalidate(); } };
            for (int i = 0; i < 3; i++) accent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
            for (int i = 0; i < choices.Length; i++)
            {
                int index = i;
                choices[i] = Theme.Button(accentNames[i], delegate { accentIndex = index; refreshAccent(); }, false);
                choices[i].AccessibleName = accentNames[i];
                choices[i].Dock = DockStyle.Fill; choices[i].Margin = new Padding(3, 5, 3, 5); accent.Controls.Add(choices[i], i % 3, i / 3);
            }
            refreshAccent(); accent.Top = 638; body.Controls.Add(accent);
            backgroundMode=current.ChatBackground??"orbits"; backgroundPath=current.BackgroundImage??"";
            int messageSize=Math.Max(9,Math.Min(18,current.ChatFontSize));
            refreshPattern=delegate {};
            TableLayoutPanel appearance=new TableLayoutPanel {ColumnCount=1,RowCount=6,Height=552,Top=292,AccessibleName="聊天外观"};
            appearance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{128,216,36,52,58,62})appearance.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            Panel preview=new Panel {Dock=DockStyle.Fill,Margin=new Padding(0,0,0,18),AccessibleName="主题预览"};appearance.Controls.Add(preview,0,0);
            preview.Paint+=delegate(object sender,PaintEventArgs e){
                string[] hex=Theme.PaletteHex(dark.Checked,accentKeys[accentIndex]);
                Color blue=Theme.Hex(hex[0]),surface=Theme.Hex(hex[1]),ink=Theme.Hex(hex[2]);
                Color w0=Theme.Hex(hex[6]),w1=Theme.Hex(hex[7]),w2=Theme.Hex(hex[8]),w3=Theme.Hex(hex[9]);
                Color wall=Color.FromArgb((w0.R+w1.R+w2.R+w3.R)/4,(w0.G+w1.G+w2.G+w3.G)/4,(w0.B+w1.B+w2.B+w3.B)/4);
                using(GraphicsPath frame=Theme.Rounded(new Rectangle(0,0,preview.Width-1,preview.Height-1),Theme.ControlRadius))using(SolidBrush b=new SolidBrush(wall))e.Graphics.FillPath(b,frame);
                Rectangle left=new Rectangle(12,12,Math.Max(120,preview.Width-100),32),right=new Rectangle(76,52,Math.Max(120,preview.Width-90),32);
                using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Rounded(left,10))using(SolidBrush b=new SolidBrush(surface))e.Graphics.FillPath(b,path);
                using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Rounded(right,10))using(SolidBrush b=new SolidBrush(Theme.Blend(blue,surface,dark.Checked?.42F:.15F)))e.Graphics.FillPath(b,path);
                using(Font font=new Font(Theme.Body.FontFamily,messageSize)) {TextRenderer.DrawText(e.Graphics,"今天也保持联系",font,left,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);TextRenderer.DrawText(e.Graphics,"消息与输入字号预览",font,right,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
            };
            TableLayoutPanel presets=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=4,Margin=new Padding(0,0,0,6)};
            for(int i=0;i<2;i++)presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            for(int i=0;i<4;i++)presets.RowStyles.Add(new RowStyle(SizeType.Percent,25));
            string[] presetNames={"晴空 · 经典","薄荷 · 日间","暖阳 · 日间","绯樱 · 日间","深海 · 夜间","森幽 · 夜间","暮紫 · 夜间","石墨 · 夜间"};
            string[] presetAccents={"blue","green","amber","rose","blue","green","purple","slate"};
            bool[] presetDark={false,false,false,false,true,true,true,true};
            string[] presetPatterns={"orbits","lines","aurora","orbits","orbits","lines","aurora","grid"};
            for(int i=0;i<presetNames.Length;i++){int choice=i;Button pick=Theme.Button(presetNames[i],delegate {follow.Checked=false;dark.Checked=presetDark[choice];accentIndex=Math.Max(0,Array.IndexOf(accentKeys,presetAccents[choice]));SelectBackground(presetPatterns[choice]);refreshAccent();preview.Invalidate();},false);pick.Dock=DockStyle.Fill;pick.Margin=new Padding(i%2==0?0:6,0,i%2==0?6:0,12);presets.Controls.Add(pick,i%2,i/2);}appearance.Controls.Add(presets,0,1);
            Label sizeLabel=Theme.Label("聊天字号 · "+messageSize+" pt",Theme.Body,Theme.Ink);sizeLabel.Dock=DockStyle.Fill;sizeLabel.Margin=new Padding(0,4,0,0);appearance.Controls.Add(sizeLabel,0,2);
            ThemedSlider sizeSlider=new ThemedSlider {Minimum=9,Maximum=18,Value=messageSize,Dock=DockStyle.Fill,AccessibleName="聊天字号",BackColor=Theme.Surface};sizeSlider.ValueChanged+=delegate {messageSize=sizeSlider.Value;sizeLabel.Text="聊天字号 · "+messageSize+" pt";preview.Invalidate();};appearance.Controls.Add(sizeSlider,0,3);
            TableLayoutPanel patterns=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,Margin=new Padding(0,2,0,10)};
            string[] patternNames={"纯色","星环","流线","网格","极光"},patternKeys={"none","orbits","lines","grid","aurora"};
            Button[] patternButtons=new Button[patternKeys.Length];
            refreshPattern=delegate {for(int j=0;j<patternKeys.Length;j++)patternButtons[j].Text=(backgroundMode==patternKeys[j]?"✓ ":"")+patternNames[j];if(image!=null)image.Text=backgroundMode=="image"?"背景图片 · "+System.IO.Path.GetFileName(backgroundPath):"选择背景图片…";};
            for(int i=0;i<patternKeys.Length;i++){int choice=i;patterns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/patternKeys.Length));patternButtons[i]=Theme.Button(patternNames[i],delegate{SelectBackground(patternKeys[choice]);},false);patternButtons[i].Dock=DockStyle.Fill;patternButtons[i].Margin=new Padding(i==0?0:6,0,i==patternKeys.Length-1?0:6,0);patterns.Controls.Add(patternButtons[i],i,0);}refreshPattern();appearance.Controls.Add(patterns,0,4);
            image=Theme.Button(backgroundMode=="image"?"已选择背景图片 · 更换":"选择背景图片…",async delegate {
                using(OpenFileDialog picker=new OpenFileDialog {Title="选择聊天背景（不超过 20 MiB）",Filter="图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif",CheckFileExists=true})if(picker.ShowDialog(this)==DialogResult.OK){
                    Activate();
                    string chosen=picker.FileName;
                    await ImportBackgroundAsync(chosen,System.Threading.Tasks.Task.Run(delegate {using(Bitmap validated=ChatWallpaper.Load(chosen)){}}));
                } else Activate();
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
            Button cancel = Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false); cancel.Size = new Size(90, 38); DialogLayout.StyleSecondary(cancel); CancelButton = cancel;
            save = Theme.Button("保存", delegate
            {
                if (String.IsNullOrWhiteSpace(name.Text)) { error.Text = "昵称不能为空"; SelectTab(0); name.Focus(); return; }
                try
                {
                    string path = ReceiveStorage.Folder(folder.Text); ReceiveStorage.ValidateWritable(path);
                    Value = new AppSettings { Avatar = avatarValue, Nickname = Protocol.Field(name.Text.Trim()), Group = String.IsNullOrWhiteSpace(group.Text) ? "我的局域网" : Protocol.Field(group.Text.Trim()), Signature = signature.Text.Trim(), CloseToTray = close.Checked, StartWithWindows = startup.Checked, Notifications = notifications.Checked,DiagnosticsEnabled = diagnostics.Checked, AutoSendDroppedFiles=autoDrop.Checked, AllowLegacyProtocol=legacy.Checked, AutoReceiveFiles = auto.Checked, ReceiveFolder = path, ChatFontSize=messageSize,ChatBackground=backgroundMode,BackgroundImage=backgroundPath,DarkMode=dark.Checked, FollowSystemTheme=follow.Checked, Accent=accentKeys[accentIndex] };
                    DialogResult = DialogResult.OK;
                }
                catch (Exception e) { error.Text = "保存目录不可用：" + e.Message; SelectTab(2); folder.Focus(); }
            }, true); save.Size = new Size(92, 38); save.Margin = Padding.Empty; cancel.Margin = new Padding(0, 0, 10, 0); actionRow.Controls.Add(save); actionRow.Controls.Add(cancel); AcceptButton = save;
            // 页面只创建一次，切换仅改变可见性，尚未保存的控件状态不会丢失。
            dark.Top = 290; accent.Top = 291; info.Top = 900;
            Control[] fields = body.Controls.Cast<Control>().OrderBy(c => c.Top).ToArray();
            body.Controls.Clear();
            for(int i=0;i<pages.Length;i++) {
                pages[i]=new FlowLayoutPanel {FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(24,8,24,20),Visible=false,AccessibleName="设置内容："+tabNames[i],BackColor=Theme.Surface};
                body.Controls.Add(pages[i]);
            }
            int page=0;
            foreach (Control field in fields)
            {
                if(field==dark)page=1;else if(field==auto)page=2;else if(field==diagnostics||field==info)page=3;
                FlowLayoutPanel layout=pages[page];
                if (field == reset || field == clearLog) continue;
                if (field == browse)
                {
                    TableLayoutPanel actions = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 40, Margin = new Padding(0, 2, 0, 10) };
                    actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                    browse.Dock = reset.Dock = DockStyle.Fill;
                    browse.Margin = new Padding(0, 0, 6, 0); reset.Margin = new Padding(6, 0, 0, 0); actions.Controls.Add(browse, 0, 0); actions.Controls.Add(reset, 1, 0); layout.Controls.Add(actions); continue;
                }
                if (field == viewLog)
                {
                    TableLayoutPanel logActions = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 40, Margin = new Padding(0, 4, 0, 8) };
                    logActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); logActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                    viewLog.Dock = clearLog.Dock = DockStyle.Fill;
                    viewLog.Margin = new Padding(0, 0, 6, 0); clearLog.Margin = new Padding(6, 0, 0, 0); logActions.Controls.Add(viewLog, 0, 0); logActions.Controls.Add(clearLog, 1, 0); layout.Controls.Add(logActions); continue;
                }
                string section = field == fields[0] ? "个人资料" : field == close ? "消息与窗口" : field == auto ? "文件接收" : field == autoDrop ? "文件发送" : field == dark ? "聊天外观" : field == diagnostics ? "故障诊断" : field == info ? "本机信息" : null;
                if (section != null) {
                    if(layout.Controls.Count>0) { Panel divider=new Panel { Height=1, BackColor=Theme.Line, Margin=new Padding(0,10,0,10) };layout.Controls.Add(divider); }
                    Label heading = Theme.Label(section, Theme.Section, Theme.Ink); heading.Height = 24; heading.Margin = new Padding(0, 0, 0, 4); layout.Controls.Add(heading);
                }
                if(field is Label && field != info) field.Height=20;
                if(field is ToggleSwitch) field.Height=((ToggleSwitch)field).PreferredHeightFor(458);
                field.Margin = new Padding(0, 0, 0, field is Label ? 4 : 6); layout.Controls.Add(field);
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
                int width = Math.Max(160, body.Width - (int)(48 * DeviceDpi / 96F) - SystemInformation.VerticalScrollBarWidth);
                foreach(FlowLayoutPanel layout in pages) {
                    layout.SuspendLayout();
                    foreach (Control field in layout.Controls) {
                        field.Width = width;
                        ToggleSwitch toggle = field as ToggleSwitch;
                        if(toggle != null) field.Height = toggle.PreferredHeightFor(width);
                        else if(field == info)
                            field.Height = TextRenderer.MeasureText(field.Text, field.Font, new Size(width, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + 4;
                    }
                    layout.ResumeLayout(true);
                }
            };
            body.SizeChanged += delegate { fit(); }; fit();SelectTab(0);
            Theme.ThemeScrollbars(body);
        }
        private void SelectBackground(string pattern)
        {
            backgroundVersion++; backgroundMode=pattern; image.Enabled=save.Enabled=true; refreshPattern();
        }
        private bool CurrentBackground(int version) { return !IsDisposed && !Disposing && Visible && backgroundVersion==version; }
        internal async System.Threading.Tasks.Task ImportBackgroundAsync(string path,System.Threading.Tasks.Task validation)
        {
            int version=++backgroundVersion; image.Enabled=save.Enabled=false; image.Text="读取背景图片中…";
            try {
                await validation;
                if(CurrentBackground(version)) { backgroundPath=path; SelectBackground("image"); }
            }
            catch(Exception error) {
                if(CurrentBackground(version)) using(NoticeDialog notice=new NoticeDialog("无法使用图片",error.Message)) notice.ShowDialog(this);
            }
            finally {
                if(CurrentBackground(version)) { image.Enabled=save.Enabled=true; refreshPattern(); }
            }
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
        internal ChatRecord LocatedRecord { get; private set; }
        private System.Threading.CancellationTokenSource reading;
        public HistoryDialog(LocalStore store, Peer peer, MainForm main = null)
        {
            Text = "聊天记录 · " + peer.DisplayName; UseSecondaryChrome();ShowInTaskbar=false; ClientSize = new Size(760, 720); MinimumSize = new Size(520, 460); StartPosition = FormStartPosition.CenterParent;
            Surface.BackColor = Theme.Wallpaper;
            Panel top = new Panel { Dock = DockStyle.Top, Height = 106, Padding = new Padding(22, 10, 22, 12), BackColor = Theme.Surface };
            Label summary=Theme.Label("",Theme.Small,Theme.Muted);summary.Dock=DockStyle.Bottom;summary.Height=26;summary.AccessibleName="聊天记录搜索结果";top.Controls.Add(summary);
            SearchBox input = new SearchBox { Dock = DockStyle.Fill }; input.Input.AccessibleName = "搜索聊天记录"; input.Input.MaxLength=200;input.Placeholder = "搜索消息或文件名"; top.Controls.Add(input);input.BringToFront();
            BubbleHistory body = new BubbleHistory { Dock = DockStyle.Fill, Margin = new Padding(10), BackColor = Theme.Wallpaper, RecordLimit=500, CharacterLimit=Int32.MaxValue };
            body.RecordActivated += delegate(ChatRecord record) {LocatedRecord=record;DialogResult=DialogResult.OK;};
            Panel frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 10), BackColor = Theme.Wallpaper }; frame.Controls.Add(body); Surface.Controls.Add(frame); Surface.Controls.Add(top);
            FlowLayoutPanel pages=new FlowLayoutPanel {Dock=DockStyle.Bottom, Height=48, Padding=new Padding(16,4,0,4), WrapContents=false, Visible=false, BackColor=Theme.Surface};
            FlowLayoutPanel numbers=new FlowLayoutPanel {AutoSize=true,WrapContents=false,Margin=Padding.Empty};pages.Controls.Add(numbers);
            int page=0,totalPages=0;Action render=null;
            NumericUpDown destination=new NumericUpDown {Minimum=1,Maximum=1,Value=1,Width=72,Font=Theme.Body,TextAlign=HorizontalAlignment.Center,AccessibleName="跳转页码",Margin=new Padding(12,7,4,0),BackColor=Theme.Surface,ForeColor=Theme.Ink};
            Action jump=delegate {
                int requested;
                if(!Int32.TryParse(destination.Text,out requested)||requested<1||requested>totalPages){summary.Text="请输入 1 至 "+totalPages+" 的页码";destination.Focus();destination.Select(0,destination.Text.Length);return;}
                page=requested-1;render();
            };
            Button go=Theme.Button("跳转",delegate {jump();},false);go.Width=64;go.Height=32;
            destination.KeyDown+=delegate(object sender,KeyEventArgs key){if(key.KeyCode==Keys.Enter){key.SuppressKeyPress=true;jump();}};
            pages.Controls.Add(destination);pages.Controls.Add(go);Surface.Controls.Add(pages);
            render=async delegate {
                if(reading!=null)reading.Cancel();
                var request=new System.Threading.CancellationTokenSource();reading=request;
                string query=input.Input.Text;int wanted=page;pages.Enabled=false;
                try {
                    LocalStore.ArchivePage found;
                    if(store.LargeHistory(peer.Id)) {
                        summary.Text="正在读取聊天记录…";body.SetRecords(new ChatRecord[0],query);
                        found=await System.Threading.Tasks.Task.Run(()=>store.ReadArchive(peer.Id,query,wanted,null,request.Token),request.Token);
                    } else found=store.ReadArchive(peer.Id,query,wanted,null,request.Token);
                    if(request.IsCancellationRequested || IsDisposed)return;
                    page=found.Page;body.SetRecords(found.Records,query);
                    totalPages=found.Total==0?0:(found.Total-1)/LocalStore.ArchivePage.Size+1;
                    while(numbers.Controls.Count>0)numbers.Controls[0].Dispose();
                    int first=Math.Max(1,Math.Min(page,totalPages-2));
                    for(int number=first;number<=Math.Min(totalPages,first+2);number++) {
                        int chosen=number;
                        Button pick=Theme.Button(number.ToString(),delegate {if(page!=chosen-1){page=chosen-1;render();}},chosen==page+1);
                        pick.AccessibleName="第 "+number+" 页";pick.Width=Math.Max(40,TextRenderer.MeasureText(pick.Text,pick.Font).Width+12);pick.Height=32;numbers.Controls.Add(pick);
                    }
                    destination.Maximum=Math.Max(1,totalPages);destination.Value=page+1;pages.Enabled=true;pages.Visible=totalPages>1;
                    summary.Text=(query.Length==0?"共 ":"找到 ")+found.Total+" 条"+(query.Length==0?"记录":"匹配记录")+(totalPages==0?"":" · 第 "+(page+1)+" / "+totalPages+" 页");
                } catch(OperationCanceledException) { }
                catch(Exception error){if(!request.IsCancellationRequested && !IsDisposed){body.SetRecords(new ChatRecord[0]);summary.Text="读取失败："+error.Message;}}
                finally{if(reading==request)reading=null;request.Dispose();}
            };
            input.Input.TextChanged+=delegate{page=0;render();};
            if(main!=null)body.DeleteRequested+=delegate(ChatRecord[] selected){if(main.DeleteMessages(peer,selected))render();};
            render();
        }
        protected override void Dispose(bool disposing){if(disposing && reading!=null)reading.Cancel();base.Dispose(disposing);}
    }
}
