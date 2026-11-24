using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FeiqLight
{
    internal sealed class TransferSourceStrip : Control
    {
        internal sealed class Source
        {
            internal string Id, Name, Avatar, Glyph;
            internal DateTime LatestTime;
        }

        private Source[] items = new Source[0];
        private string selectedId = "";
        private int offset, hover = -1, pressIndex = -1, pressX, pressOffset;
        private bool pressed, dragging;
        private float scale = 1F;
        private readonly ToolTip tips = new ToolTip();
        internal event Action<string> SourceSelected;
        internal IList<Source> Items { get { return Array.AsReadOnly(items); } }
        internal string SelectedId { get { return selectedId; } }
        private int Dp(int value) { return (int)Math.Round(value * scale); }
        private int ItemWidth { get { return Math.Max(1, Dp(72)); } }
        // 溢出时不占用两侧空间放翻页箭头：滚轮、按住拖动或方向键滚动，边缘露出的半个头像即溢出提示。
        private Rectangle Viewport { get { return ClientRectangle; } }
        private int MaximumOffset { get { return Math.Max(0, items.Length * ItemWidth - Viewport.Width); } }

        internal TransferSourceStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Surface;
            AccessibleName = "传输任务来源";
            AccessibleRole = AccessibleRole.List;
            AccessibleDescription = "鼠标滚轮或按住拖动横向滚动，左右键切换来源";
            Theme.Changed += ApplyColors;
        }

        internal void SetItems(Source[] sources, string selection)
        {
            // 时间只影响来源排序；展示未变时保留原快照，500ms 进度刷新不触发横排重绘。
            bool changed = items.Length != sources.Length;
            if (!changed)
                for (int i = 0; i < items.Length; i++)
                    if (items[i].Id != sources[i].Id || items[i].Name != sources[i].Name || items[i].Avatar != sources[i].Avatar || items[i].Glyph != sources[i].Glyph) { changed = true; break; }
            bool selectionChanged = selectedId != selection;
            if (!changed && !selectionChanged) return;
            int previous = Array.FindIndex(items, item => item.Id == selectedId);
            if (changed) { items = sources; hover = -1; tips.SetToolTip(this, null); }
            selectedId = selection;
            EnsureSelectedVisible();
            if (changed)
            {
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }
            else
            {
                if (previous >= 0) Invalidate(ItemBounds(previous));
                int current = Array.FindIndex(items, item => item.Id == selectedId);
                if (current >= 0) Invalidate(ItemBounds(current));
            }
            if (selectionChanged) AccessibilityNotifyClients(AccessibleEvents.Selection, Array.FindIndex(items, item => item.Id == selectedId));
        }

        private Rectangle ItemBounds(int index) { return new Rectangle(Viewport.Left + index * ItemWidth - offset, 0, ItemWidth, ClientSize.Height); }
        private int IndexAt(Point point)
        {
            if (!Viewport.Contains(point)) return -1;
            int index = (point.X - Viewport.Left + offset) / ItemWidth;
            return index >= 0 && index < items.Length ? index : -1;
        }
        private void Choose(int index)
        {
            if (index < 0 || index >= items.Length) return;
            Focus();
            if (SourceSelected != null) SourceSelected(items[index].Id);
        }
        private void ScrollTo(int value)
        {
            int next = Math.Max(0, Math.Min(MaximumOffset, value));
            if (next == offset) return;
            offset = next;
            hover = -1;
            tips.SetToolTip(this, null);
            Invalidate();
        }
        internal void EnsureSelectedVisible()
        {
            int index = Array.FindIndex(items, item => item.Id == selectedId);
            int next = Math.Max(0, Math.Min(MaximumOffset, offset));
            if (index >= 0)
            {
                int left = index * ItemWidth, right = left + ItemWidth;
                if (left < next) next = left;
                else if (right > next + Viewport.Width) next = right - Viewport.Width;
            }
            ScrollTo(next);
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); EnsureSelectedVisible(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateScale(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateScale(); }
        private void UpdateScale()
        {
            // 系统 DPI 感知的 .NET Framework 窗口可能仍报告 DeviceDpi=96，绘制与布局统一取实际 DC。
            using (Graphics graphics = CreateGraphics()) scale = Math.Max(DeviceDpi, graphics.DpiX) / 96F;
            EnsureSelectedVisible(); Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            // 松开时才选择；按住横向拖动超过系统阈值只滚动，不改选中来源。
            pressed = true; dragging = false; pressIndex = IndexAt(e.Location); pressX = e.X; pressOffset = offset;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !pressed) return;
            bool click = !dragging; pressed = dragging = false;
            if (click && pressIndex >= 0 && IndexAt(e.Location) == pressIndex) Choose(pressIndex);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) pressed = dragging = false; }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(offset - e.Delta * ItemWidth * 2 / 120);
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pressed && (e.Button & MouseButtons.Left) != 0)
            {
                if (!dragging && Math.Abs(e.X - pressX) >= SystemInformation.DragSize.Width) dragging = true;
                if (dragging) { ScrollTo(pressOffset - (e.X - pressX)); return; }
            }
            int next = IndexAt(e.Location);
            Cursor = next >= 0 ? Cursors.Hand : Cursors.Default;
            string hint = next >= 0 ? items[next].Name : null;
            if (tips.GetToolTip(this) != hint) tips.SetToolTip(this, hint);
            if (hover == next) return;
            int previous = hover; hover = next;
            if (previous >= 0) Invalidate(ItemBounds(previous));
            if (hover >= 0) Invalidate(ItemBounds(hover));
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover < 0) return;
            int previous = hover; hover = -1; Invalidate(ItemBounds(previous));
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int index = Array.FindIndex(items, item => item.Id == selectedId);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) Choose(Math.Max(0, Math.Min(items.Length - 1, index + (e.KeyCode == Keys.Left ? -1 : 1))));
            else if (e.KeyCode == Keys.Home) Choose(0);
            else if (e.KeyCode == Keys.End) Choose(items.Length - 1);
            else return;
            e.Handled = e.SuppressKeyPress = true;
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        private void ApplyColors() { BackColor = Theme.Surface; Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface); g.SmoothingMode = SmoothingMode.AntiAlias;
            GraphicsState state = g.Save();
            g.SetClip(Viewport, CombineMode.Intersect);
            int size = Dp(40), halo = Theme.AvatarHalo(size);
            for (int i = Math.Max(0, offset / ItemWidth); i < items.Length; i++)
            {
                Rectangle row = ItemBounds(i);
                if (row.Left >= Viewport.Right) break;
                Source item = items[i]; bool selected = item.Id == selectedId;
                if (i == hover) using (GraphicsPath path = Theme.Rounded(Rectangle.Inflate(row, -Dp(2), -Dp(2)), Dp(8))) using (SolidBrush brush = new SolidBrush(Theme.Hover)) g.FillPath(brush, path);
                Rectangle face = new Rectangle(row.Left + (ItemWidth - size) / 2, Dp(6), size, size);
                if (!String.IsNullOrEmpty(item.Glyph))
                {
                    using (SolidBrush brush = new SolidBrush(Theme.Ice)) g.FillEllipse(brush, face);
                    Theme.Glyph(g, item.Glyph, Rectangle.Inflate(face, -Dp(10), -Dp(10)), Theme.Blue);
                }
                else g.DrawImageUnscaled(Theme.CachedAvatar(size, item.Id, false, item.Avatar), face.Left - halo, face.Top - halo);
                if (selected) using (Pen pen = new Pen(Theme.Blue, Dp(2))) g.DrawEllipse(pen, Rectangle.Inflate(face, Dp(3), Dp(3)));
                Rectangle name = new Rectangle(row.Left + Dp(2), face.Bottom + Dp(6), ItemWidth - Dp(4), Dp(20));
                TextRenderer.DrawText(g, item.Name, Theme.Small, name, selected ? Theme.Blue : Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (selected && Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, name, Theme.Blue, Theme.Surface);
            }
            g.Restore(state);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new StripAccessibility(this); }
        private sealed class StripAccessibility : ControlAccessibleObject
        {
            private readonly TransferSourceStrip owner;
            internal StripAccessibility(TransferSourceStrip owner) : base(owner) { this.owner = owner; }
            public override int GetChildCount() { return owner.items.Length; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < owner.items.Length ? new SourceAccessibility(owner, index) : null; }
            public override AccessibleObject GetSelected() { return GetChild(Array.FindIndex(owner.items, item => item.Id == owner.selectedId)); }
            public override AccessibleObject GetFocused() { return owner.Focused ? GetSelected() : null; }
            public override AccessibleObject HitTest(int x, int y)
            {
                Point point = owner.PointToClient(new Point(x, y));
                int index = owner.IndexAt(point);
                if (index >= 0) return GetChild(index);
                return owner.ClientRectangle.Contains(point) ? this : null;
            }
        }
        private sealed class SourceAccessibility : AccessibleObject
        {
            private readonly TransferSourceStrip owner;
            private readonly string id, name;
            private int Index { get { return Array.FindIndex(owner.items, item => item.Id == id); } }
            internal SourceAccessibility(TransferSourceStrip owner, int index) { this.owner = owner; id = owner.items[index].Id; name = owner.items[index].Name; }
            public override AccessibleObject Parent { get { return owner.AccessibilityObject; } }
            public override string Name { get { int index = Index; return (index < 0 ? name : owner.items[index].Name) + (id == owner.selectedId ? " · 已选中" : " · 未选中"); } }
            public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
            public override string DefaultAction { get { return "选择"; } }
            public override Rectangle Bounds { get { int index = Index; return index < 0 ? Rectangle.Empty : owner.RectangleToScreen(Rectangle.Intersect(owner.ItemBounds(index), owner.Viewport)); } }
            public override AccessibleStates State
            {
                get
                {
                    // 无障碍客户端可能继续持有已移除来源的对象，不能让旧索引指向别的联系人。
                    int index = Index;
                    if (index < 0) return AccessibleStates.Offscreen | AccessibleStates.Unavailable;
                    AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (owner.items[index].Id == owner.selectedId) state |= AccessibleStates.Selected | AccessibleStates.Checked | (owner.Focused ? AccessibleStates.Focused : AccessibleStates.None);
                    if (!owner.Visible || !owner.ItemBounds(index).IntersectsWith(owner.Viewport)) state |= AccessibleStates.Offscreen;
                    return state;
                }
            }
            public override void DoDefaultAction() { owner.Choose(Index); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { Theme.Changed -= ApplyColors; tips.Dispose(); } base.Dispose(disposing); }
    }
}
