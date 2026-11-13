using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FeiqLight
{
    // 带放大镜与清除键的搜索框：清除键为绘制区域而非额外按钮，不改变窗口的按钮与输入框结构。
    public sealed class SearchBox : RoundedInputBase
    {
        private bool clearHover;
        public SearchBox()
        {
            Padding = new Padding(38, 11, 34, 9); Cursor = Cursors.IBeam;
            Input.TextChanged += delegate { Invalidate(); };
            Input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && Input.TextLength > 0) { Input.Clear(); e.Handled = e.SuppressKeyPress = true; } };
        }
        private Rectangle ClearBounds { get { int size = (int)(26 * DeviceDpi / 96F); return new Rectangle(Width - size - (int)(6 * DeviceDpi / 96F), (Height - size) / 2, size, size); } }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e); float scale = e.Graphics.DpiX / 96F; int icon = (int)(18 * scale);
            Theme.Glyph(e.Graphics, "search", new Rectangle((int)(13 * scale), (Height - icon) / 2, icon, icon), Theme.Muted);
            if (Input.TextLength > 0)
            {
                Rectangle clear = ClearBounds;
                if (clearHover) using (SolidBrush b = new SolidBrush(Theme.Blend(Theme.Ink, Input.BackColor, .08F))) e.Graphics.FillEllipse(b, clear);
                Theme.Glyph(e.Graphics, "close", Rectangle.Inflate(clear, -(int)(7 * scale), -(int)(7 * scale)), Theme.Muted);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); bool hover = Input.TextLength > 0 && ClearBounds.Contains(e.Location); Cursor = hover ? Cursors.Hand : Cursors.IBeam; if (hover != clearHover) { clearHover = hover; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (clearHover) { clearHover = false; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Input.TextLength > 0 && ClearBounds.Contains(e.Location)) Input.Clear();
            Input.Focus();
        }
    }
    // 标签条：文字标签 + 底部滑动指示条（随选中项平滑移动）。
    public sealed class TabStrip : Panel
    {
        private readonly List<RoundedButton> tabs = new List<RoundedButton>();
        private int active;
        private double fromX, fromW, start = -100000;
        private readonly Func<bool> step;
        public TabStrip()
        {
            DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface;
            step = delegate { if (IsDisposed) return false; Invalidate(); return Motion.Progress(start, 220) < 1; };
        }
        public void Add(RoundedButton tab) { tab.Tab = true; tabs.Add(tab); Controls.Add(tab); LayoutTabs(); }
        public int Active
        {
            get { return active; }
            set
            {
                if (value < 0 || value >= tabs.Count) return;
                RectangleF current = Indicator();
                fromX = current.X; fromW = current.Width; active = value;
                for (int i = 0; i < tabs.Count; i++) { tabs[i].Active = i == active; tabs[i].Invalidate(); }
                start = Motion.Now; if (IsHandleCreated && Visible && Motion.Enabled) Motion.Run(step); else start = -100000;
                Invalidate();
            }
        }
        protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); LayoutTabs(); }
        private void LayoutTabs()
        {
            if (tabs.Count == 0) return;
            float scale = DeviceDpi / 96F; int gap = (int)(2 * scale), height = Math.Max(1, ClientSize.Height - (int)(3 * scale)), x = Padding.Left;
            foreach (RoundedButton tab in tabs)
            {
                int width = Math.Max((int)(52 * scale), TextRenderer.MeasureText(tab.Text, tab.Font).Width + (int)(26 * scale) + (tab.Badge > 0 ? (int)(26 * scale) : 0));
                tab.SetBounds(x, 0, width, height); tab.Margin = Padding.Empty; x += width + gap;
            }
        }
        public void Relayout() { LayoutTabs(); Invalidate(); }
        private RectangleF Target()
        {
            if (active >= tabs.Count) return RectangleF.Empty;
            Rectangle bounds = tabs[active].Bounds; float scale = DeviceDpi / 96F, width = Math.Max(18 * scale, bounds.Width - 22 * scale);
            return new RectangleF(bounds.X + (bounds.Width - width) / 2, ClientSize.Height - 3 * scale, width, 3 * scale);
        }
        private RectangleF Indicator()
        {
            RectangleF target = Target(); double t = Motion.EaseOut(Motion.Progress(start, 220));
            if (t >= 1 || fromW <= 0) return target;
            return new RectangleF((float)(fromX + (target.X - fromX) * t), target.Y, (float)(fromW + (target.Width - fromW) * t), target.Height);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
            RectangleF bar = Indicator(); if (bar.Width <= 0) return;
            using (GraphicsPath path = new GraphicsPath())
            {
                float r = bar.Height; path.AddArc(bar.X, bar.Y, r * 2, r * 2, 180, 90); path.AddArc(bar.Right - r * 2, bar.Y, r * 2, r * 2, 270, 90);
                path.AddLine(bar.Right, bar.Bottom, bar.X, bar.Bottom); path.CloseFigure();
                using (SolidBrush b = new SolidBrush(Theme.Blue)) e.Graphics.FillPath(b, path);
            }
        }
    }
    // 会话区背景：未打开会话时显示壁纸与提示胶囊。
    public sealed class ChatHost : Panel
    {
        public string Pattern = "orbits";
        public string Hint = "选择联系人开始聊天";
        public ChatHost() { DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Wallpaper; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            ChatWallpaper.Draw(e.Graphics, ClientRectangle, Pattern == "image" ? "orbits" : Pattern, null, DeviceDpi / 96F);
            if (Controls.Count > 0 && HasVisibleChild()) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; float scale = DeviceDpi / 96F;
            Size text = TextRenderer.MeasureText(Hint, Theme.Body); Rectangle pill = new Rectangle((Width - text.Width - (int)(28 * scale)) / 2, (Height - (int)(32 * scale)) / 2, text.Width + (int)(28 * scale), (int)(32 * scale));
            using (GraphicsPath path = Theme.Rounded(pill, pill.Height / 2)) using (SolidBrush b = new SolidBrush(Theme.ServiceFill)) e.Graphics.FillPath(b, path);
            TextRenderer.DrawText(e.Graphics, Hint, Theme.Body, pill, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        private bool HasVisibleChild() { foreach (Control child in Controls) if (child.Visible) return true; return false; }
    }
    // 主菜单顶部的个人资料条目：头像、昵称与本机地址，点击进入设置。
    public sealed class ProfileMenuItem : ToolStripMenuItem
    {
        public string Identity = "", Detail = "", Avatar;
        public ProfileMenuItem() { Tag = "profile"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float scale = g.DpiX / 96F; g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle area = new Rectangle(Point.Empty, Size);
            if (Selected) using (GraphicsPath path = Theme.RoundedF(new RectangleF(1, 1, Width - 2, Height - 2), 8 * scale)) using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
            int avatar = (int)(42 * scale), halo = Theme.AvatarHalo(avatar), left = (int)(12 * scale), top = (Height - avatar) / 2;
            g.DrawImageUnscaled(Theme.CachedAvatar(avatar, Identity, false, Avatar), left - halo, top - halo);
            int textLeft = left + avatar + (int)(12 * scale);
            TextRenderer.DrawText(g, Text, Theme.Strong, new Rectangle(textLeft, top + (int)(1 * scale), Math.Max(1, Width - textLeft - (int)(10 * scale)), (int)(22 * scale)), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, Detail, Theme.Small, new Rectangle(textLeft, top + (int)(23 * scale), Math.Max(1, Width - textLeft - (int)(10 * scale)), (int)(18 * scale)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
    // 聊天输入框：空内容时绘制提示语；RichEdit 不支持系统 cue banner。
    public sealed class ChatEditor : RichTextBox
    {
        public string Placeholder = "输入消息…";
        private static readonly IntPtr RichEdit50 = LoadLibrary("msftedit.dll");
        private bool hadSelection;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct TextArea { internal int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, ref TextArea area);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool HideCaret(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowCaret(IntPtr handle);
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; if (RichEdit50 != IntPtr.Zero) cp.ClassName = "RICHEDIT50W"; return cp; } }
        private Rectangle textArea;
        internal int FirstVisibleLine { get { return IsHandleCreated ? (int)SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero) : 0; } }
        private int LastLineTop()
        {
            if (TextLength == 0) return 0;
            int top = GetPositionFromCharIndex(TextLength - 1).Y;
            if (Text[TextLength - 1] == '\n') top += Font.Height;
            return top;
        }
        private void AlignText()
        {
            if (!IsHandleCreated || IsDisposed || Disposing || ClientSize.Width < 4) return;
            // RichEdit 的正文和提示语共用排版框；单行居中，多行从顶部开始，避免输入后文字跳位。
            int top = GetLineFromCharIndex(TextLength) == 0 ? Math.Max(0, (ClientSize.Height - Font.Height) / 2) : 0;
            Rectangle next = Rectangle.FromLTRB(1, top, ClientSize.Width - 1, ClientSize.Height);
            if (next != textArea)
            {
                textArea = next;
                TextArea area = new TextArea { Left = next.Left, Top = next.Top, Right = next.Right, Bottom = next.Bottom };
                SendMessage(Handle, 0x00B3, IntPtr.Zero, ref area); Invalidate();
            }
            // 输入框增高或删字后若底部仍有余量，自动把被滚出顶部的首行拉回可视区。
            while (TextLength > 0 && FirstVisibleLine > 0 && LastLineTop() + Font.Height * 2 <= ClientSize.Height)
            {
                int before = FirstVisibleLine;
                SendMessage(Handle, 0x0115, new IntPtr(0), IntPtr.Zero);
                if (FirstVisibleLine >= before) break;
            }
        }
        internal void ScrollByWheel(int delta)
        {
            if (!IsHandleCreated || IsDisposed || Disposing || delta == 0 || TextLength == 0) return;
            int wheelLines = SystemInformation.MouseWheelScrollLines;
            int stepLines = wheelLines > 0 ? Math.Min(6, wheelLines) : 3;
            int steps = Math.Max(1, Math.Abs(delta) / 120) * stepLines;
            for (int i = 0; i < steps; i++)
            {
                if (delta > 0)
                {
                    if (FirstVisibleLine <= 0 && GetPositionFromCharIndex(0).Y >= textArea.Top) break;
                    int before = FirstVisibleLine;
                    SendMessage(Handle, 0x0115, new IntPtr(0), IntPtr.Zero);
                    if (FirstVisibleLine >= before && GetPositionFromCharIndex(0).Y >= textArea.Top) break;
                }
                else
                {
                    if (LastLineTop() + Font.Height <= ClientSize.Height) break;
                    int before = FirstVisibleLine;
                    SendMessage(Handle, 0x0115, new IntPtr(1), IntPtr.Zero);
                    if (FirstVisibleLine <= before) break;
                }
            }
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollByWheel(e.Delta);
            base.OnMouseWheel(e);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); textArea = Rectangle.Empty; AlignText(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); AlignText(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); textArea = Rectangle.Empty; AlignText(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AlignText(); if (TextLength <= 1) Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnVScroll(EventArgs e) { base.OnVScroll(e); if (ColorEmoji.Contains(Text)) Invalidate(false); }
        protected override void OnSelectionChanged(EventArgs e)
        {
            base.OnSelectionChanged(e);
            bool active = SelectionLength > 0;
            if ((active || hadSelection) && ColorEmoji.Contains(Text)) Invalidate(false);
            hadSelection = active;
        }
        private void PaintColorEmojis(Graphics g)
        {
            string text = Text;
            if (!ColorEmoji.Contains(text)) return;
            Rectangle clip = textArea.Width > 0 ? textArea : ClientRectangle;
            if (clip.Width <= 0 || clip.Height <= 0) return;
            int lineHeight = Math.Max(12, Font.Height);
            int selStart = SelectionStart, selEnd = selStart + SelectionLength;
            bool showSelection = (Focused || !HideSelection) && selEnd > selStart;
            GraphicsState state = g.Save();
            try
            {
                Rectangle clearClip = Rectangle.Intersect(ClientRectangle, new Rectangle(clip.X - 2, clip.Y - 3, clip.Width + 4, clip.Height + 6));
                g.SetClip(clearClip);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var cells = new System.Collections.Generic.List<Tuple<string, Rectangle, bool>>();
                int pos = 0;
                while (pos < text.Length)
                {
                    string emoji;
                    if (!ColorEmoji.Match(text, pos, out emoji)) { pos += Char.IsSurrogatePair(text, pos) ? 2 : 1; continue; }
                    Point p = GetPositionFromCharIndex(pos);
                    if (p.Y + lineHeight >= clip.Top - 2 && p.Y <= clip.Bottom + 2 && p.X <= clip.Right + 2)
                    {
                        Point next = GetPositionFromCharIndex(pos + emoji.Length);
                        int slotW = next.Y == p.Y && next.X > p.X ? next.X - p.X : Math.Max(12, lineHeight - 2);
                        if (p.X + slotW >= clip.Left - 2)
                        {
                            bool selected = showSelection && pos < selEnd && pos + emoji.Length > selStart;
                            cells.Add(Tuple.Create(emoji, new Rectangle(p.X, p.Y, slotW, lineHeight), selected));
                        }
                    }
                    pos += emoji.Length;
                }
                if (cells.Count == 0) return;
                using (SolidBrush normalBg = new SolidBrush(BackColor))
                using (SolidBrush selectedBg = new SolidBrush(SystemColors.Highlight))
                {
                    foreach (var cell in cells)
                        g.FillRectangle(cell.Item3 ? selectedBg : normalBg, cell.Item2.X - 1, cell.Item2.Y - 2, cell.Item2.Width + 2, cell.Item2.Height + 3);
                    foreach (var cell in cells)
                    {
                        int size = Math.Min(cell.Item2.Height - 2, Math.Max(10, cell.Item2.Width));
                        ColorEmoji.Draw(g, cell.Item1, new Rectangle(cell.Item2.X + (cell.Item2.Width - size) / 2, cell.Item2.Y + (cell.Item2.Height - size) / 2, size, size));
                    }
                }
            }
            finally { g.Restore(state); }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, MousePosition.X, MousePosition.Y, delta));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            if ((m.Msg == 0x000F || m.Msg == 0x0318) && IsHandleCreated)
            {
                if (TextLength == 0)
                {
                    if (!String.IsNullOrEmpty(Placeholder))
                    {
                        using (Graphics g = m.Msg == 0x0318 && m.WParam != IntPtr.Zero ? Graphics.FromHdc(m.WParam) : CreateGraphics())
                            TextRenderer.DrawText(g, Placeholder, Font, new Rectangle(GetPositionFromCharIndex(0), new Size(Math.Max(1, textArea.Width), Font.Height)), Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    }
                }
                else if (ColorEmoji.Contains(Text))
                {
                    bool hid = m.Msg == 0x000F && HideCaret(Handle);
                    try
                    {
                        using (Graphics g = m.Msg == 0x0318 && m.WParam != IntPtr.Zero ? Graphics.FromHdc(m.WParam) : CreateGraphics())
                            PaintColorEmojis(g);
                    }
                    finally { if (hid) ShowCaret(Handle); }
                }
            }
        }
    }
    // 拖入文件时覆盖消息区的投放提示；自身也接收投放。
    public sealed class DropOverlay : Control
    {
        public string Title = "松开以添加附件", Detail = "附件会先加入待发送列表";
        public DropOverlay() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Visible = false; AccessibleName = "文件投放区域"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float scale = DeviceDpi / 96F; g.SmoothingMode = SmoothingMode.AntiAlias;
            ChatWallpaper.Draw(g, ClientRectangle, "none", null, scale);
            using (SolidBrush veil = new SolidBrush(Color.FromArgb(Theme.Dark ? 150 : 170, Theme.Surface))) g.FillRectangle(veil, ClientRectangle);
            RectangleF zone = RectangleF.Inflate(ClientRectangle, -18 * scale, -18 * scale);
            using (GraphicsPath path = Theme.RoundedF(zone, 18 * scale))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Theme.Dark ? 60 : 36, Theme.Blue))) g.FillPath(b, path);
                using (Pen dash = new Pen(Theme.Blue, 2 * scale) { DashStyle = DashStyle.Dash, DashPattern = new[] { 4F, 3F } }) g.DrawPath(dash, path);
            }
            int icon = (int)(52 * scale); Rectangle mark = new Rectangle((Width - icon) / 2, Height / 2 - icon - (int)(10 * scale), icon, icon);
            Theme.Glyph(g, "download", mark, Theme.Blue);
            TextRenderer.DrawText(g, Title, Theme.Title, new Rectangle(0, mark.Bottom + (int)(10 * scale), Width, (int)(28 * scale)), Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Detail, Theme.Body, new Rectangle(0, mark.Bottom + (int)(40 * scale), Width, (int)(22 * scale)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
