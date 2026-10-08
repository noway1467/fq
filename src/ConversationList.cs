using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    public sealed class ConversationList : ScrollableControl
    {
        public sealed class Item { public Peer Peer; public string Preview; public int Unread; public DateTime Time; }
        private List<Item> items = new List<Item>();
        public IList<Item> Items { get { return items.AsReadOnly(); } }
        public event Action<Peer> PeerActivated;
        public event Action<Peer> PeerMenuRequested;
        public string SelectedId;
        private int hover = -1;
        private int RowHeight { get { return (int)(80 * DeviceDpi / 96F); } }
        public ConversationList() { DoubleBuffered = true; AutoScroll = true; BackColor = Theme.Surface; TabStop = true; AccessibleName = "会话列表"; AccessibleRole = AccessibleRole.List; }
        public void SetItems(IEnumerable<Item> value) { items = value.ToList(); AutoScrollMinSize = new Size(0, items.Count * RowHeight); Invalidate(); }
        public void ActivatePeer(int index)
        {
            if (index < 0 || index >= items.Count) return; SelectedId = items[index].Peer.Id; Invalidate(); if (PeerActivated != null) PeerActivated(items[index].Peer);
        }
        protected override void OnMouseMove(MouseEventArgs e) { int next = (e.Y - AutoScrollPosition.Y) / RowHeight; if (next != hover) { hover = next; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int index=(e.Y-AutoScrollPosition.Y)/RowHeight; if (e.Button == MouseButtons.Left) { Focus(); ActivatePeer(index); } else if(e.Button==MouseButtons.Right && index>=0 && index<items.Count && PeerMenuRequested!=null)PeerMenuRequested(items[index].Peer); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int index = items.FindIndex(i => i.Peer.Id == SelectedId);
            if((e.KeyCode==Keys.Apps || (e.Shift&&e.KeyCode==Keys.F10))&&index>=0&&PeerMenuRequested!=null){PeerMenuRequested(items[index].Peer);e.Handled=true;}
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) { int next = Math.Max(0, Math.Min(items.Count - 1, index + (e.KeyCode == Keys.Down ? 1 : -1))); if (items.Count > 0) { SelectedId = items[next].Peer.Id; AutoScrollPosition = new Point(0, Math.Max(0, (next + 1) * RowHeight - ClientSize.Height)); Invalidate(); } e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { ActivatePeer(index); e.Handled = true; } base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); float s = DeviceDpi / 96F; int pad = (int)(15 * s), avatar = (int)(52 * s); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (items.Count == 0) TextRenderer.DrawText(e.Graphics, "暂无会话", Theme.Body, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i]; Rectangle row = new Rectangle(0, i * RowHeight + AutoScrollPosition.Y, ClientSize.Width, RowHeight); if (!row.IntersectsWith(ClientRectangle)) continue;
                bool selected = item.Peer.Id == SelectedId;
                Rectangle highlight = new Rectangle(row.Left + (int)(8*s), row.Top + (int)(3*s), Math.Max(1,row.Width - (int)(16*s)), row.Height - (int)(6*s));
                using (GraphicsPath path = Theme.Rounded(highlight,(int)(Theme.ControlRadius*s))) using (SolidBrush b = new SolidBrush(selected ? Theme.Sky : i == hover ? Theme.Ice : Theme.Surface)) e.Graphics.FillPath(b, path);
                using (Bitmap image = Theme.ContactAvatar(avatar, item.Peer.DisplayName, item.Peer.Online)) e.Graphics.DrawImageUnscaled(image, pad - Theme.AvatarHalo(avatar), row.Top + (RowHeight - avatar) / 2 - Theme.AvatarHalo(avatar));
                int right = (int)(60 * s); Rectangle name = new Rectangle(pad * 2 + avatar, row.Top + (int)(17 * s), Math.Max(20, row.Width - pad * 3 - avatar - right), (int)(25 * s));
                using (Font font = new Font(Theme.Body.FontFamily, 10F, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, (item.Peer.Pinned?"↑ ":"")+item.Peer.DisplayName, font, name, Theme.Ink, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                name.Y += (int)(27 * s); name.Width += right - (item.Unread > 0 ? (int)(35 * s) : 0);
                TextRenderer.DrawText(e.Graphics, item.Preview.Replace('\n', ' '), Theme.Body, name, Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                if (item.Time != default(DateTime)) TextRenderer.DrawText(e.Graphics, item.Time.ToString("HH:mm"), Theme.Small, new Rectangle(row.Right - right - pad, row.Top + (int)(17 * s), right, (int)(22 * s)), Theme.Muted, TextFormatFlags.Right);
                if (item.Unread > 0)
                {
                    Rectangle badge = new Rectangle(row.Right - (int)(40 * s), row.Top + (int)(44 * s), (int)(25 * s), (int)(23 * s));
                    using (SolidBrush b = new SolidBrush(Theme.Blue)) e.Graphics.FillEllipse(b, badge);
                    TextRenderer.DrawText(e.Graphics, item.Unread > 99 ? "99+" : item.Unread.ToString(), Theme.Small, badge, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }
}
