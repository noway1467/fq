using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    // 单画布零子控件表情面板：常规屏幕一页展示全部表情无需滚动，极小工作区自动回退平滑滚动。
    internal sealed class EmojiPicker : ToolStripDropDown
    {
        private sealed class Cell
        {
            internal string Text;
            internal bool Face;
            internal int Group;
            internal Rectangle Bounds;
        }
        private sealed class Heading
        {
            internal string Text;
            internal Rectangle Bounds;
        }
        private sealed class Divider
        {
            internal Point Start, End;
        }
        private sealed class EmojiCanvas : SmoothScrollView
        {
            internal readonly List<Heading> Headings = new List<Heading>();
            internal readonly List<Cell> Cells = new List<Cell>();
            internal readonly List<Divider> Dividers = new List<Divider>();
            internal Font KaomojiFont = Theme.Small;
            internal Font EmojiFont;
            internal int HoverIndex = -1;
            internal int FocusIndex = 0;
            private int pressedIndex = -1;
            private readonly Action<string> onPick;
            internal EmojiCanvas(Action<string> onPick)
            {
                this.onPick = onPick;
                AccessibleName = "表情网格";
                AccessibleRole = AccessibleRole.List;
                TabStop = true;
            }
            internal bool NeedsScroll { get { return MaxScroll > 0; } }
            internal void ResetInteraction()
            {
                HoverIndex = -1;
                pressedIndex = -1;
                FocusIndex = Cells.Count > 0 ? 0 : -1;
                JumpTo(0);
                Invalidate();
            }
            internal Rectangle VisualBounds(Rectangle contentRect)
            {
                Rectangle r = contentRect;
                r.Offset(0, -ScrollOffset);
                return r;
            }
            internal int HitTest(Point point)
            {
                if (InScrollbar(point)) return -1;
                Point contentPoint = new Point(point.X, point.Y + ScrollOffset);
                for (int i = 0; i < Cells.Count; i++)
                    if (Cells[i].Bounds.Contains(contentPoint)) return i;
                return -1;
            }
            internal void EnsureVisible(int index)
            {
                if (index < 0 || index >= Cells.Count) return;
                Rectangle r = Cells[index].Bounds;
                int pad = Dp(6);
                if (r.Top - pad < ScrollOffset) JumpTo(r.Top - pad);
                else if (r.Bottom + pad > ScrollOffset + ClientSize.Height) JumpTo(r.Bottom + pad - ClientSize.Height);
            }
            private void InvalidateCell(int index)
            {
                if (index < 0 || index >= Cells.Count) return;
                Rectangle r = VisualBounds(Cells[index].Bounds);
                r.Inflate(2, 2);
                Invalidate(r);
            }
            private void SetHover(int index)
            {
                if (HoverIndex == index) return;
                int old = HoverIndex;
                HoverIndex = index;
                InvalidateCell(old);
                InvalidateCell(HoverIndex);
                Cursor = HoverIndex >= 0 ? Cursors.Hand : Cursors.Default;
            }
            private void SetFocusIndex(int index)
            {
                if (index < 0 || index >= Cells.Count) return;
                int old = FocusIndex;
                FocusIndex = index;
                EnsureVisible(FocusIndex);
                InvalidateCell(old);
                InvalidateCell(FocusIndex);
            }
            private int MoveSpatial(Keys key)
            {
                if (Cells.Count == 0) return -1;
                if (FocusIndex < 0 || FocusIndex >= Cells.Count) return 0;
                if (key == Keys.Home) return 0;
                if (key == Keys.End) return Cells.Count - 1;
                Rectangle cur = Cells[FocusIndex].Bounds;
                int cx = cur.X + cur.Width / 2, cy = cur.Y + cur.Height / 2;
                int best = -1;
                long bestScore = Int64.MaxValue;
                for (int i = 0; i < Cells.Count; i++)
                {
                    if (i == FocusIndex) continue;
                    Rectangle b = Cells[i].Bounds;
                    int x = b.X + b.Width / 2, y = b.Y + b.Height / 2;
                    int dx = x - cx, dy = y - cy;
                    bool valid =
                        (key == Keys.Right && dx > 2 && Math.Abs(dy) <= Math.Max(cur.Height, b.Height) / 2) ||
                        (key == Keys.Left && dx < -2 && Math.Abs(dy) <= Math.Max(cur.Height, b.Height) / 2) ||
                        (key == Keys.Down && dy > 2) ||
                        (key == Keys.Up && dy < -2);
                    if (!valid) continue;
                    long primary = (key == Keys.Left || key == Keys.Right) ? Math.Abs(dx) : Math.Abs(dy);
                    long secondary = (key == Keys.Left || key == Keys.Right) ? Math.Abs(dy) : Math.Abs(dx);
                    long score = primary * 16 + secondary * 64;
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                if (best >= 0) return best;
                if (key == Keys.Right) return Math.Min(Cells.Count - 1, FocusIndex + 1);
                if (key == Keys.Left) return Math.Max(0, FocusIndex - 1);
                return FocusIndex;
            }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys code = keyData & Keys.KeyCode;
                if (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down || code == Keys.Enter || code == Keys.Space || code == Keys.Home || code == Keys.End) return true;
                return base.IsInputKey(keyData);
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    if (FocusIndex >= 0 && FocusIndex < Cells.Count)
                    {
                        string value = Cells[FocusIndex].Text;
                        e.Handled = e.SuppressKeyPress = true;
                        onPick(value);
                        return;
                    }
                }
                else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
                {
                    SetFocusIndex(MoveSpatial(e.KeyCode));
                    e.Handled = e.SuppressKeyPress = true;
                    return;
                }
                base.OnKeyDown(e);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (ScrollbarMouseMove(e)) { SetHover(-1); return; }
                SetHover(HitTest(e.Location));
            }
            protected override void OnMouseLeave(EventArgs e)
            {
                SetHover(-1);
                base.OnMouseLeave(e);
            }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (ScrollbarMouseDown(e)) return;
                if (e.Button != MouseButtons.Left) return;
                Focus();
                int hit = HitTest(e.Location);
                pressedIndex = hit;
                if (hit >= 0) SetFocusIndex(hit);
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (ScrollbarMouseUp(e)) return;
                if (e.Button != MouseButtons.Left) return;
                int hit = HitTest(e.Location);
                int down = pressedIndex;
                pressedIndex = -1;
                if (hit >= 0 && hit == down && hit < Cells.Count) onPick(Cells[hit].Text);
            }
            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Rectangle clip = e.ClipRectangle;
                using (SolidBrush bg = new SolidBrush(Theme.Surface)) g.FillRectangle(bg, clip);
                int dy = -ScrollOffset;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen dividerPen = new Pen(Theme.Line))
                {
                    foreach (Divider d in Dividers)
                    {
                        Point p1 = new Point(d.Start.X, d.Start.Y + dy), p2 = new Point(d.End.X, d.End.Y + dy);
                        if (Math.Max(p1.Y, p2.Y) < clip.Top - 1 || Math.Min(p1.Y, p2.Y) > clip.Bottom + 1) continue;
                        g.DrawLine(dividerPen, p1, p2);
                    }
                }
                const TextFormatFlags headingFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                const TextFormatFlags cellTextFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                foreach (Heading h in Headings)
                {
                    Rectangle r = h.Bounds; r.Offset(0, dy);
                    if (!r.IntersectsWith(clip)) continue;
                    TextRenderer.DrawText(g, h.Text, Theme.Small, r, Theme.Muted, headingFlags);
                }
                InterpolationMode prevInterp = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int radius = Math.Max(4, Dp(7));
                try
                {
                    for (int i = 0; i < Cells.Count; i++)
                    {
                        Cell cell = Cells[i];
                        Rectangle r = cell.Bounds; r.Offset(0, dy);
                        if (!r.IntersectsWith(clip)) continue;
                        bool hovered = i == HoverIndex;
                        bool focused = Focused && i == FocusIndex;
                        if (hovered || focused)
                        {
                            Rectangle pill = Rectangle.Inflate(r, -1, -1);
                            if (pill.Width > 2 && pill.Height > 2)
                            {
                                using (GraphicsPath path = Theme.Rounded(pill, radius))
                                {
                                    if (hovered) using (SolidBrush fill = new SolidBrush(Theme.Sky)) g.FillPath(fill, path);
                                    if (focused) using (Pen ring = new Pen(Theme.Blue, 1F)) g.DrawPath(ring, path);
                                }
                            }
                        }
                        if (cell.Face)
                        {
                            int icon = Math.Max(14, Math.Min(r.Width - Dp(8), r.Height - Dp(8)));
                            Rectangle iconRect = new Rectangle(r.X + (r.Width - icon) / 2, r.Y + (r.Height - icon) / 2, icon, icon);
                            if (!ColorEmoji.Draw(g, cell.Text, iconRect))
                                TextRenderer.DrawText(g, cell.Text, EmojiFont ?? Theme.Body, r, Theme.Ink, cellTextFlags);
                        }
                        else
                        {
                            TextRenderer.DrawText(g, cell.Text, KaomojiFont ?? Theme.Small, r, Theme.Ink, cellTextFlags);
                        }
                    }
                }
                finally { g.InterpolationMode = prevInterp; }
                DrawScrollbar(g);
            }
        }
        private readonly Panel surface = new Panel();
        private readonly EmojiCanvas grid;
        private readonly ToolStripControlHost host;
        private readonly Font emojiFont = new Font("Segoe UI Emoji", 14F);
        private readonly Action<string> picked;
        private int dpi = 96, builtDpi = -1, builtTheme = -1;
        private Size available = new Size(800, 600), builtAvailable = Size.Empty;
        private static readonly string[] Categories = { "笑脸与心情", "手势与人物", "爱心与庆祝", "动物与自然", "食物与饮料", "活动与物品", "颜文字" };
        private static readonly string[] Entries = {
            "😀 😃 😄 😁 😆 😅 😂 🤣 😊 😇 🙂 🙃 😉 😌 😍 🥰 😘 😋 😎 🤔 😭 😴 😡 🥺",
            "👍 👎 👏 🙌 🤝 🙏 👋 ✋ 👌 ✌️ 💪 👀 🧠 👩 👨 🧑 👧 👦 👶 👩‍💻 👨‍💻 👨‍👩‍👧‍👦 👍🏽 🫶",
            "❤️ 🧡 💛 💚 💙 💜 🖤 🤍 💕 💖 💗 💝 💔 💯 🔥 ✨ ⭐ 🌟 🎉 🎊 🎈 🎁 🎂 🥳",
            "🐶 🐱 🐭 🐹 🐰 🦊 🐻 🐼 🐨 🐯 🦁 🐸 🐷 🐵 🐔 🐧 🦄 🦋 🌸 🌹 🌻 🌈 ☀️ 🌙",
            "🍎 🍊 🍋 🍌 🍉 🍇 🍓 🍒 🥑 🍞 🥐 🍔 🍟 🍕 🍜 🍣 🍙 🍰 🍪 🍫 🍦 ☕ 🍵 🧋",
            "⚽ 🏀 🎾 🏆 🎮 🎯 🎵 🎧 🎤 🎸 🚀 ✈️ 🚗 🚲 🏠 🌍 💡 📚 ✏️ 📎 📁 ✅ ❌ 🇨🇳",
            "(๑•̀ㅂ•́)و✧|(｡･ω･｡)|ヾ(≧▽≦*)o|(´▽`ʃ♡ƪ)|╰(*°▽°*)╯|ಥ_ಥ|(づ￣3￣)づ|¯\\_(ツ)_/¯"
        };
        public EmojiPicker(Action<string> picked)
        {
            this.picked = picked; AutoSize = false; Padding = new Padding(1); Margin = Padding.Empty;
            AccessibleName = "表情选择面板";
            grid = new EmojiCanvas(PickValue) { Dock = DockStyle.Fill, EmojiFont = emojiFont };
            surface.Controls.Add(grid);
            host = new ToolStripControlHost(surface) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            Items.Add(host); ArrangeGrid();
        }
        private void PickValue(string text)
        {
            Close();
            if (picked != null) picked(text);
        }
        private int D(int value) { return (int)Math.Round(value * dpi / 96F); }
        internal int CategoryCount { get { return grid.Headings.Count; } }
        internal int ItemCount { get { return grid.Cells.Count; } }
        internal bool NeedsScroll { get { return grid.NeedsScroll; } }
        internal bool HasHorizontalOverflow
        {
            get
            {
                int w = grid.ClientSize.Width;
                return grid.Headings.Any(h => h.Bounds.Right > w) || grid.Cells.Any(c => c.Bounds.Right > w);
            }
        }
        internal int FirstGroupColumnCount { get { return grid.Cells.Where(c => c.Group == 0).Select(c => c.Bounds.X).Distinct().Count(); } }
        internal bool AllItemsVisibleWithoutScroll
        {
            get
            {
                if (grid.NeedsScroll) return false;
                Rectangle view = grid.ClientRectangle;
                return grid.Cells.Count > 0 && grid.Cells.All(c => view.Contains(grid.VisualBounds(c.Bounds)));
            }
        }
        internal string LastItemText { get { return grid.Cells.Count == 0 ? "" : grid.Cells[grid.Cells.Count - 1].Text; } }
        internal bool ScrollToLastAndCheckVisible()
        {
            if (grid.Cells.Count == 0) return false;
            int last = grid.Cells.Count - 1;
            grid.EnsureVisible(last);
            return grid.ClientRectangle.Contains(grid.VisualBounds(grid.Cells[last].Bounds));
        }
        internal bool PickItemByText(string text)
        {
            int index = grid.Cells.FindIndex(c => c.Text == text);
            if (index < 0) return false;
            grid.EnsureVisible(index);
            Rectangle r = grid.VisualBounds(grid.Cells[index].Bounds);
            Point center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            var down = typeof(EmojiCanvas).GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var up = typeof(EmojiCanvas).GetMethod("OnMouseUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            down.Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0) });
            up.Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, center.X, center.Y, 0) });
            return true;
        }
        internal void Prepare(Size bounds, int targetDpi)
        {
            available = bounds; dpi = targetDpi; ArrangeGrid();
        }
        private void ArrangeGrid()
        {
            if (builtDpi == dpi && builtAvailable == available && builtTheme == Theme.Version && grid.Cells.Count > 0)
            {
                grid.ResetInteraction();
                return;
            }
            builtDpi = dpi; builtAvailable = available; builtTheme = Theme.Version;
            BackColor = Theme.Line;
            surface.BackColor = grid.BackColor = Theme.Surface;
            grid.KaomojiFont = Theme.Small;

            int pad = D(10);
            int maxInnerW = Math.Max(1, available.Width - 2);
            int maxInnerH = Math.Max(1, available.Height - 2);

            // 双栏分类（每类 8×3 = 24 个表情）+ 底部全宽颜文字（4×2），在常规屏幕下一整页直接展示全部 152 项。
            int pairCellW = D(32), pairCellH = D(32), pairGapX = D(16), pairHeadH = D(20), pairGapY = D(6);
            int pairColW = pairCellW * 8;
            int onePageW = pad * 2 + pairColW * 2 + pairGapX;
            int kaomojiH = D(28);
            int onePageH = pad * 2 + 3 * (pairHeadH + pairCellH * 3) + 2 * pairGapY + D(10) + pairHeadH + kaomojiH * 2;

            bool twoColumn = maxInnerW >= onePageW;
            grid.Headings.Clear();
            grid.Cells.Clear();
            grid.Dividers.Clear();

            int width, height, contentH;
            if (twoColumn)
            {
                bool needScroll = maxInnerH < onePageH;
                int barReserve = needScroll ? D(12) : 0;
                int usable = Math.Max(1, Math.Min(onePageW, maxInnerW) - pad * 2 - barReserve);
                int colW = Math.Max(8, (usable - pairGapX) / 2);
                int cellW = Math.Max(1, colW / 8);
                int actualColW = cellW * 8;
                int rightX = pad + actualColW + pairGapX;
                width = Math.Min(maxInnerW, pad * 2 + actualColW * 2 + pairGapX + barReserve);
                int y = pad;
                int dividerTop = y;
                for (int pair = 0; pair < 3; pair++)
                {
                    int leftGroup = pair * 2, rightGroup = pair * 2 + 1;
                    grid.Headings.Add(new Heading { Text = Categories[leftGroup], Bounds = new Rectangle(pad, y, actualColW, pairHeadH) });
                    grid.Headings.Add(new Heading { Text = Categories[rightGroup], Bounds = new Rectangle(rightX, y, actualColW, pairHeadH) });
                    y += pairHeadH;
                    string[] leftItems = Entries[leftGroup].Split(' ');
                    string[] rightItems = Entries[rightGroup].Split(' ');
                    for (int i = 0; i < leftItems.Length; i++)
                    {
                        int col = i % 8, row = i / 8;
                        grid.Cells.Add(new Cell { Text = leftItems[i], Face = true, Group = leftGroup, Bounds = new Rectangle(pad + col * cellW, y + row * pairCellH, cellW, pairCellH) });
                    }
                    for (int i = 0; i < rightItems.Length; i++)
                    {
                        int col = i % 8, row = i / 8;
                        grid.Cells.Add(new Cell { Text = rightItems[i], Face = true, Group = rightGroup, Bounds = new Rectangle(rightX + col * cellW, y + row * pairCellH, cellW, pairCellH) });
                    }
                    y += 3 * pairCellH;
                    if (pair < 2) y += pairGapY;
                }
                int dividerX = pad + actualColW + pairGapX / 2;
                grid.Dividers.Add(new Divider { Start = new Point(dividerX, dividerTop + D(2)), End = new Point(dividerX, y - D(2)) });

                y += D(5);
                int fullW = actualColW * 2 + pairGapX;
                grid.Dividers.Add(new Divider { Start = new Point(pad, y), End = new Point(pad + fullW, y) });
                y += D(5);

                grid.Headings.Add(new Heading { Text = Categories[6], Bounds = new Rectangle(pad, y, fullW, pairHeadH) });
                y += pairHeadH;
                string[] kaomojis = Entries[6].Split('|');
                int kCols = 4, kCellW = Math.Max(1, fullW / kCols);
                for (int i = 0; i < kaomojis.Length; i++)
                {
                    int col = i % kCols, row = i / kCols;
                    grid.Cells.Add(new Cell { Text = kaomojis[i], Face = false, Group = 6, Bounds = new Rectangle(pad + col * kCellW, y + row * kaomojiH, kCellW, kaomojiH) });
                }
                y += ((kaomojis.Length + kCols - 1) / kCols) * kaomojiH + pad;
                contentH = y;
                height = Math.Max(1, Math.Min(maxInnerH, contentH));
            }
            else
            {
                // 极窄工作区回退到单栏自适应流式排布，并预留覆盖滚动条宽度，绝不横向越界。
                width = Math.Max(1, Math.Min(D(368), maxInnerW));
                height = Math.Max(1, Math.Min(maxInnerH, D(336)));
                int scrollPad = D(12);
                int usable = Math.Max(1, width - pad * 2 - scrollPad);
                int y = pad;
                for (int group = 0; group < Categories.Length; group++)
                {
                    bool faces = group < Categories.Length - 1;
                    string[] entries = Entries[group].Split(faces ? ' ' : '|');
                    int cols = Math.Max(1, usable / D(faces ? 36 : 132));
                    int cellW = Math.Max(1, usable / cols);
                    int cellH = D(faces ? 36 : 30);
                    if (group > 0) y += D(6);
                    grid.Headings.Add(new Heading { Text = Categories[group], Bounds = new Rectangle(pad, y, usable, D(22)) });
                    y += D(22);
                    for (int i = 0; i < entries.Length; i++)
                    {
                        int col = i % cols, row = i / cols;
                        grid.Cells.Add(new Cell { Text = entries[i], Face = faces, Group = group, Bounds = new Rectangle(pad + col * cellW, y + row * cellH, cellW, cellH) });
                    }
                    y += ((entries.Length + cols - 1) / cols) * cellH;
                }
                contentH = y + pad;
                height = Math.Max(1, Math.Min(maxInnerH, contentH));
            }

            surface.Size = new Size(width, height);
            grid.Size = surface.Size;
            grid.AutoScrollMinSize = new Size(0, contentH);
            grid.ResetInteraction();
            host.Size = surface.Size;
            Size = new Size(width + 2, height + 2);
        }
        internal static Point Fit(Rectangle anchor, Size popup, Rectangle work)
        {
            int x = Math.Max(work.Left, Math.Min(anchor.Left, work.Right - popup.Width));
            int y = anchor.Top - popup.Height - 6;
            if (y < work.Top && anchor.Bottom + popup.Height + 6 <= work.Bottom) y = anchor.Bottom + 6;
            return new Point(x, Math.Max(work.Top, Math.Min(y, work.Bottom - popup.Height)));
        }
        public void ShowFor(Control source)
        {
            Rectangle work = Screen.FromControl(source).WorkingArea;
            int targetDpi = source.DeviceDpi;
            using (Graphics g = source.CreateGraphics()) targetDpi = Math.Max(targetDpi, (int)Math.Round(g.DpiX));
            Prepare(new Size(Math.Max(1, work.Width - 16), Math.Max(1, work.Height - 16)), targetDpi);
            Show(Fit(source.RectangleToScreen(source.ClientRectangle), Size, work));
            grid.Focus();
        }
        protected override void Dispose(bool disposing) { if (disposing) emojiFont.Dispose(); base.Dispose(disposing); }
    }
}
