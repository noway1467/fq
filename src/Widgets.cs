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
        public string Identity = "", Detail = "";
        public ProfileMenuItem() { Tag = "profile"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; float scale = g.DpiX / 96F; g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle area = new Rectangle(Point.Empty, Size);
            if (Selected) using (GraphicsPath path = Theme.RoundedF(new RectangleF(1, 1, Width - 2, Height - 2), 8 * scale)) using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
            int avatar = (int)(42 * scale), halo = Theme.AvatarHalo(avatar), left = (int)(12 * scale), top = (Height - avatar) / 2;
            g.DrawImageUnscaled(Theme.CachedAvatar(avatar, Identity, false), left - halo, top - halo);
            int textLeft = left + avatar + (int)(12 * scale);
            TextRenderer.DrawText(g, Text, Theme.Strong, new Rectangle(textLeft, top + (int)(1 * scale), Math.Max(1, Width - textLeft - (int)(10 * scale)), (int)(22 * scale)), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, Detail, Theme.Small, new Rectangle(textLeft, top + (int)(23 * scale), Math.Max(1, Width - textLeft - (int)(10 * scale)), (int)(18 * scale)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
    // 聊天输入框：空内容时绘制提示语；RichEdit 不支持系统 cue banner。
    public sealed class ChatEditor : RichTextBox
    {
        public string Placeholder = "输入消息…";
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (TextLength <= 1) Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && TextLength == 0 && !String.IsNullOrEmpty(Placeholder) && IsHandleCreated)
                using (Graphics g = CreateGraphics())
                    TextRenderer.DrawText(g, Placeholder, Font, new Rectangle(1, 0, ClientSize.Width - 2, Font.Height + 4), Theme.Blend(Theme.Muted, BackColor, .85F), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
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
