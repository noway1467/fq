using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FeiqLight
{
    // 自绘列表的平滑滚动与覆盖式细滚动条。
    // AutoScrollPosition 返回逻辑落点（滚动动画的终点），绘制与命中测试使用正在过渡的视觉偏移；
    // 因而“发送后到最新 / 收到消息不抢位置”等判断不受动画进度影响。
    // 不用 ScrollWindowEx 位块搬移，固定壁纸、入场动画与悬浮按钮每帧完整重绘，不会出现拼接错位。
    public abstract class SmoothScrollView : Control
    {
        private Size content;
        private double offset, target, lastFrame;
        private readonly Func<bool> scrollStep, barStep;
        private double barActivity = -100000;
        private bool barHover, barDrag;
        private int dragOrigin;
        private double dragOffset;
        protected SmoothScrollView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            scrollStep = ScrollFrame; barStep = BarFrame;
        }
        protected float DpiScale { get { return DeviceDpi / 96F; } }
        protected int Dp(double value) { return (int)Math.Round(value * DpiScale); }
        public Size AutoScrollMinSize { get { return content; } set { content = value; offset = Clamp(offset); target = Clamp(target); Invalidate(); } }
        // 读取为负偏移（与 ScrollableControl 相同），写入使用正坐标并立即生效，会打断正在进行的平滑滚动。
        public Point AutoScrollPosition
        {
            get { return new Point(0, -(int)Math.Round(target)); }
            set { JumpTo(value.Y); }
        }
        // 当前绘制所用的视觉偏移。
        protected int ScrollOffset { get { return (int)Math.Round(offset); } }
        protected int MaxScroll { get { return Math.Max(0, content.Height - ClientSize.Height); } }
        // 滚动动画是否仍在进行；测试与预览截图据此等待落位。
        public bool ScrollAnimating { get { return target != offset; } }
        // 滚动之外仍有过渡动画（如新消息浮现）时由子类扩展，截图与断言据此等待最终画面。
        public virtual bool Busy { get { return ScrollAnimating; } }
        protected virtual void OnScrollChanged() { }
        private double Clamp(double value) { return Math.Max(0, Math.Min(MaxScroll, value)); }
        protected void JumpTo(double y)
        {
            double next = Clamp(y); bool changed = Math.Round(next) != Math.Round(offset);
            offset = target = next; if (changed) OnScrollChanged();
            Invalidate();
        }
        protected void SmoothTo(double y)
        {
            double next = Clamp(y);
            if (!Motion.Enabled || !IsHandleCreated || !Visible) { JumpTo(next); return; }
            target = next;
            // 远距离跳转先贴近目标再滑行，长列表不会滚动数秒。
            double far = ClientSize.Height * 1.5;
            if (Math.Abs(target - offset) > far) { offset = target - Math.Sign(target - offset) * far; OnScrollChanged(); }
            lastFrame = Motion.Now; Motion.Run(scrollStep); ShowScrollbar();
        }
        protected void SmoothBy(double delta) { SmoothTo(target + delta); }
        // 内容在视口上方增减时同步平移逻辑与视觉位置，进行中的滚动动画不被打断。
        protected void OffsetScroll(double delta)
        {
            if (delta == 0) return;
            offset = Clamp(offset + delta); target = Clamp(target + delta); OnScrollChanged(); Invalidate();
        }
        private bool ScrollFrame()
        {
            if (IsDisposed) return false;
            double now = Motion.Now, elapsed = Math.Min(50, Math.Max(1, now - lastFrame)); lastFrame = now;
            target = Clamp(target);
            double before = offset;
            offset += (target - offset) * (1 - Math.Exp(-elapsed / 70.0));
            if (Math.Abs(target - offset) < .5) offset = target;
            if (Math.Round(before) != Math.Round(offset)) OnScrollChanged();
            Invalidate(); return offset != target;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll <= 0) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            double notch = lines < 0 ? ClientSize.Height * .85 : Dp(34) * Math.Max(1, Math.Min(6, lines));
            SmoothBy(-e.Delta / 120.0 * notch);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); offset = Clamp(offset); target = Clamp(target); }
        // 覆盖式滚动条：滚动时出现、静止约一秒后淡出；悬停变粗，可拖动或点击轨道翻页。
        protected void ShowScrollbar() { barActivity = Motion.Now; if (MaxScroll > 0) Motion.Run(barStep); }
        private bool BarFrame() { if (IsDisposed) return false; Invalidate(new Rectangle(ClientSize.Width - Dp(16), 0, Dp(16), ClientSize.Height)); return barDrag || barHover || Motion.Now - barActivity < 1500; }
        private float BarAlpha()
        {
            if (MaxScroll <= 0) return 0;
            if (barDrag || barHover) return 1;
            double idle = Motion.Now - barActivity;
            return idle < 1000 ? 1 : idle > 1400 ? 0 : (float)(1 - (idle - 1000) / 400);
        }
        private Rectangle Track() { int margin = Dp(4); return new Rectangle(ClientSize.Width - Dp(14), margin, Dp(14), Math.Max(1, ClientSize.Height - margin * 2)); }
        private Rectangle Thumb()
        {
            Rectangle track = Track(); int height = Math.Max(Dp(36), (int)((double)track.Height * ClientSize.Height / Math.Max(1, content.Height)));
            height = Math.Min(track.Height, height);
            int y = track.Top + (int)Math.Round((track.Height - height) * (MaxScroll == 0 ? 0 : offset / MaxScroll));
            return new Rectangle(track.Left, y, track.Width, height);
        }
        protected bool InScrollbar(Point point) { return MaxScroll > 0 && point.X >= ClientSize.Width - Dp(14); }
        protected void DrawScrollbar(Graphics g)
        {
            float alpha = BarAlpha(); if (alpha <= 0) return;
            Rectangle thumb = Thumb(); bool wide = barHover || barDrag; int width = Dp(wide ? 8 : 5);
            Rectangle bar = new Rectangle(ClientSize.Width - Dp(4) - width, thumb.Top, width, thumb.Height);
            SmoothingMode old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Rounded(bar, width / 2))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(alpha * (wide ? 150 : 100)), Theme.ScrollThumb))) g.FillPath(brush, path);
            g.SmoothingMode = old;
        }
        // 子类在各自鼠标处理开始处调用；返回 true 表示事件已由滚动条处理。
        protected bool ScrollbarMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !InScrollbar(e.Location)) return false;
            Rectangle thumb = Thumb();
            if (e.Y >= thumb.Top && e.Y < thumb.Bottom) { barDrag = true; dragOrigin = e.Y; dragOffset = offset; JumpTo(offset); Capture = true; }
            else SmoothBy((e.Y < thumb.Top ? -1 : 1) * ClientSize.Height * .9);
            ShowScrollbar(); Invalidate(); return true;
        }
        protected bool ScrollbarMouseMove(MouseEventArgs e)
        {
            bool hover = InScrollbar(e.Location) || barDrag;
            if (hover != barHover) { barHover = hover; ShowScrollbar(); Invalidate(); }
            if (!barDrag) return hover;
            Rectangle track = Track(), thumb = Thumb(); double room = Math.Max(1, track.Height - thumb.Height);
            JumpTo(dragOffset + (e.Y - dragOrigin) * MaxScroll / room); ShowScrollbar(); return true;
        }
        protected bool ScrollbarMouseUp(MouseEventArgs e)
        {
            if (!barDrag) return false;
            barDrag = false; Capture = false; barHover = InScrollbar(e.Location); ShowScrollbar(); return true;
        }
        protected override void OnMouseLeave(EventArgs e) { if (barHover && !barDrag) { barHover = false; ShowScrollbar(); } base.OnMouseLeave(e); }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        // 键盘滚动：子类可在调用 base 前先处理自己的按键。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!e.Handled)
            {
                if (e.KeyCode == Keys.PageDown) { SmoothBy(ClientSize.Height * .9); e.Handled = true; }
                else if (e.KeyCode == Keys.PageUp) { SmoothBy(-ClientSize.Height * .9); e.Handled = true; }
            }
            base.OnKeyDown(e);
        }
    }
}
