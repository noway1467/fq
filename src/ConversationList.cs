using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    public sealed class ConversationList : SmoothScrollView
    {
        public sealed class Item { public Peer Peer; public string Preview; public int Unread; public DateTime Time; }
        private List<Item> items = new List<Item>();
        public IList<Item> Items { get { return items.AsReadOnly(); } }
        public event Action<Peer> PeerActivated;
        public event Action<Peer> PeerMenuRequested;
        public string SelectedId;
        // 空列表提示：无联系人与搜索无结果使用不同文案。
        public string EmptyText = "暂无会话", EmptyHint = "同一局域网内启动飞Q的设备会自动出现";
        private int hover = -1;
        private int RowHeight { get { return Dp(68); } }
        private int ListTop { get { return Dp(4); } }
        public ConversationList() { BackColor = Theme.Surface; TabStop = true; AccessibleName = "会话列表"; AccessibleRole = AccessibleRole.List; }
        public void SetItems(IEnumerable<Item> value) { items = value.ToList(); AutoScrollMinSize = new Size(0, items.Count * RowHeight + ListTop * 2); if (hover >= items.Count) hover = -1; Invalidate(); }
        public void ActivatePeer(int index)
        {
            if (index < 0 || index >= items.Count) return; SelectedId = items[index].Peer.Id; Invalidate(); if (PeerActivated != null) PeerActivated(items[index].Peer);
        }
        private int IndexAt(int y) { int row = y + ScrollOffset - ListTop; return row < 0 ? -1 : row / RowHeight; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int next = ScrollbarMouseMove(e) ? -1 : IndexAt(e.Y); if (next >= items.Count) next = -1;
            if (next != hover) { hover = next; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); ScrollbarMouseUp(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (ScrollbarMouseDown(e)) return;
            int index = IndexAt(e.Y);
            if (e.Button == MouseButtons.Left) { Focus(); ActivatePeer(index); }
            else if (e.Button == MouseButtons.Right && index >= 0 && index < items.Count && PeerMenuRequested != null) PeerMenuRequested(items[index].Peer);
        }
        protected override void OnScrollChanged() { Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int index = items.FindIndex(i => i.Peer.Id == SelectedId);
            if((e.KeyCode==Keys.Apps || (e.Shift&&e.KeyCode==Keys.F10))&&index>=0&&PeerMenuRequested!=null){PeerMenuRequested(items[index].Peer);e.Handled=true;}
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                int next = Math.Max(0, Math.Min(items.Count - 1, index + (e.KeyCode == Keys.Down ? 1 : -1)));
                if (items.Count > 0)
                {
                    SelectedId = items[next].Peer.Id; int top = ListTop + next * RowHeight, bottom = top + RowHeight;
                    if (top < -AutoScrollPosition.Y) SmoothTo(top - ListTop); else if (bottom > -AutoScrollPosition.Y + ClientSize.Height) SmoothTo(bottom + ListTop - ClientSize.Height);
                    Invalidate();
                }
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Enter) { ActivatePeer(index); e.Handled = true; } base.OnKeyDown(e);
        }
        private static string RowTime(DateTime time)
        {
            if (time.Date == DateTime.Today) return time.ToString("HH:mm");
            if (time.Date == DateTime.Today.AddDays(-1)) return "昨天";
            if ((DateTime.Today - time.Date).TotalDays < 7) return new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)time.DayOfWeek];
            return time.Year == DateTime.Today.Year ? time.ToString("M/d") : time.ToString("yyyy/M/d");
        }
        private const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        private const TextFormatFlags Tight = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(Theme.Surface); g.SmoothingMode = SmoothingMode.AntiAlias;
            int avatar = Dp(50), pad = Dp(10), halo = Theme.AvatarHalo(avatar);
            if (items.Count == 0)
            {
                int icon = Dp(46); Rectangle mark = new Rectangle((ClientSize.Width - icon) / 2, ClientSize.Height / 2 - icon - Dp(18), icon, icon);
                Theme.Glyph(g, EmptyText.Contains("没有找到") ? "search" : "chat", mark, Theme.Blend(Theme.Muted, Theme.Surface, .6F));
                TextRenderer.DrawText(g, EmptyText, Theme.Section, new Rectangle(Dp(16), mark.Bottom + Dp(12), ClientSize.Width - Dp(32), Dp(22)), Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, EmptyHint, Theme.Small, new Rectangle(Dp(20), mark.Bottom + Dp(36), ClientSize.Width - Dp(40), Dp(40)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                return;
            }
            int first = Math.Max(0, IndexAt(0));
            for (int i = first; i < items.Count; i++)
            {
                Item item = items[i]; Rectangle row = new Rectangle(0, ListTop + i * RowHeight - ScrollOffset, ClientSize.Width, RowHeight);
                if (row.Top > ClientSize.Height) break; if (row.Bottom < 0) continue;
                bool selected = item.Peer.Id == SelectedId;
                Color back = selected ? Theme.Blue : i == hover ? Theme.Hover : Theme.Surface;
                Rectangle highlight = new Rectangle(row.Left + Dp(6), row.Top + Dp(1), Math.Max(1, row.Width - Dp(12)), row.Height - Dp(2));
                if (back != Theme.Surface) using (GraphicsPath path = Theme.Rounded(highlight, Dp(10))) using (SolidBrush b = new SolidBrush(back)) g.FillPath(b, path);
                Color ink = selected ? Color.White : Theme.Ink, muted = selected ? Color.FromArgb(225, 255, 255, 255) : Theme.Muted;
                Rectangle face = new Rectangle(pad + Dp(4), row.Top + (RowHeight - avatar) / 2, avatar, avatar);
                g.DrawImageUnscaled(Theme.CachedAvatar(avatar, item.Peer.Id, false), face.X - halo, face.Y - halo);
                if (item.Peer.Online)
                {
                    // 在线圆点外圈取行底色，选中与悬停时都与背景衔接。
                    float dot = Dp(15); RectangleF ring = new RectangleF(face.Right - dot + Dp(1), face.Bottom - dot + Dp(1), dot, dot);
                    using (SolidBrush b = new SolidBrush(back)) g.FillEllipse(b, ring);
                    using (SolidBrush b = new SolidBrush(selected ? Color.White : Theme.Online)) g.FillEllipse(b, RectangleF.Inflate(ring, -Dp(2.5), -Dp(2.5)));
                }
                int left = face.Right + Dp(12), right = row.Right - Dp(16);
                string time = item.Time == default(DateTime) ? "" : RowTime(item.Time);
                int timeWidth = time.Length == 0 ? 0 : TextRenderer.MeasureText(g, time, Theme.Small, Size.Empty, Tight).Width;
                Rectangle name = new Rectangle(left, row.Top + Dp(13), Math.Max(Dp(20), right - left - timeWidth - Dp(8)), Dp(22));
                // 为编号单独留宽，长昵称省略时仍能分清同名会话。
                string caption = item.Peer.BaseDisplayName, suffix = item.Peer.NameSuffix;
                int suffixWidth = String.IsNullOrEmpty(suffix) ? 0 : TextRenderer.MeasureText(g, suffix, Theme.Small, Size.Empty, Line).Width + Dp(4);
                Rectangle captionBounds = name; captionBounds.Width = Math.Max(1, name.Width - suffixWidth);
                TextRenderer.DrawText(g, caption, Theme.Strong, captionBounds, ink, Line);
                if (suffixWidth > 0) { int offset = Math.Min(captionBounds.Width, TextRenderer.MeasureText(g, caption, Theme.Strong, Size.Empty, Line).Width) + Dp(4); TextRenderer.DrawText(g, suffix, Theme.Small, new Rectangle(name.X + offset, name.Y + Dp(1), suffixWidth, name.Height), muted, Line); }
                if (timeWidth > 0) TextRenderer.DrawText(g, time, Theme.Small, new Rectangle(right - timeWidth - Dp(2), name.Y, timeWidth + Dp(2), name.Height), item.Unread > 0 && !selected ? Theme.Blue : muted, Tight | TextFormatFlags.VerticalCenter);
                int badgeWidth = 0;
                if (item.Unread > 0)
                {
                    string count = item.Unread > 99 ? "99+" : item.Unread.ToString();
                    int width = Math.Max(Dp(22), TextRenderer.MeasureText(g, count, Theme.Small, Size.Empty, Line).Width + Dp(12)); badgeWidth = width + Dp(8);
                    Rectangle badge = new Rectangle(right - width, row.Top + Dp(37), width, Dp(22));
                    using (GraphicsPath pill = Theme.Rounded(badge, badge.Height / 2)) using (SolidBrush b = new SolidBrush(selected ? Color.White : Theme.Blue)) g.FillPath(b, pill);
                    TextRenderer.DrawText(g, count, Theme.Small, badge, selected ? Theme.Blue : Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                else if (item.Peer.Pinned) { int size = Dp(17); badgeWidth = size + Dp(8); Theme.Glyph(g, "pin", new Rectangle(right - size, row.Top + Dp(39), size, size), muted); }
                Rectangle preview = new Rectangle(left, row.Top + Dp(37), Math.Max(Dp(20), right - left - badgeWidth), Dp(22));
                string text = (item.Preview ?? "").Replace('\r', ' ').Replace('\n', ' ');
                if (text.StartsWith("你：") && text.Length > 2)
                {
                    // 自己发出的预览前缀用强调色区分，正文保持次要色。
                    int prefix = TextRenderer.MeasureText(g, "你：", Theme.Body, Size.Empty, Line).Width;
                    TextRenderer.DrawText(g, "你：", Theme.Body, new Rectangle(preview.X, preview.Y, Math.Min(prefix, preview.Width), preview.Height), selected ? Color.White : Theme.Blue, Line);
                    TextRenderer.DrawText(g, text.Substring(2), Theme.Body, new Rectangle(preview.X + prefix, preview.Y, Math.Max(1, preview.Width - prefix), preview.Height), muted, Line);
                }
                else TextRenderer.DrawText(g, text, Theme.Body, preview, muted, Line);
            }
            DrawScrollbar(g);
        }
    }
}
