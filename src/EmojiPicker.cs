using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    // 所有表情在同一滚动区，分类仅作标题；网格按工作区和 DPI 排布。
    internal sealed class EmojiPicker : ToolStripDropDown
    {
        private sealed class EmojiGrid : FlowLayoutPanel
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
            private Point lastDisplayOffset;
            public EmojiGrid()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
            }
            internal void FlushScrollRepaint()
            {
                if (!IsHandleCreated || IsDisposed) return;
                // ScrollWindowEx 在裁剪边界移动子窗口句柄时会把边缘旧像素搬移进子控件内部；
                // 每次滚动后立即使子控件失效并同步完成重绘，消除快速滚动残影。
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0185);
            }
            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                Point current = DisplayRectangle.Location;
                if (current != lastDisplayOffset) { lastDisplayOffset = current; FlushScrollRepaint(); }
            }
            protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); FlushScrollRepaint(); }
            protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); FlushScrollRepaint(); }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x0115 || m.Msg == 0x0114 || m.Msg == 0x020A) FlushScrollRepaint();
            }
        }
        private readonly Panel surface = new Panel();
        private readonly EmojiGrid grid = new EmojiGrid();
        private readonly List<Button> buttons = new List<Button>();
        private readonly ToolStripControlHost host;
        private readonly Font emojiFont = new Font("Segoe UI Emoji", 16F);
        private readonly Action<string> picked;
        private int dpi = 96;
        private Size available = new Size(800, 600);
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
            grid.AutoScroll = true; grid.WrapContents = true; grid.Margin = Padding.Empty;
            grid.AccessibleName = "表情网格"; Theme.ThemeScrollbars(grid); surface.Controls.Add(grid);
            host = new ToolStripControlHost(surface) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            Items.Add(host); ArrangeGrid();
        }
        private int D(int value) { return (int)Math.Round(value * dpi / 96F); }
        internal void Prepare(Size bounds, int targetDpi)
        {
            available = bounds; dpi = targetDpi; ArrangeGrid();
        }
        private void ArrangeGrid()
        {
            int width = Math.Max(1, Math.Min(D(368), available.Width - 2));
            int padding = D(8);
            int usable = Math.Max(1, width - padding * 2 - SystemInformation.VerticalScrollBarWidth);
            int height = Math.Max(1, Math.Min(available.Height - 2, D(336)));
            surface.SuspendLayout(); grid.SuspendLayout();
            try
            {
                foreach (Control child in grid.Controls.Cast<Control>().ToArray()) child.Dispose();
                grid.Controls.Clear(); buttons.Clear(); grid.AutoScrollPosition = Point.Empty;
                BackColor = surface.BackColor = grid.BackColor = Theme.Surface;
                surface.Size = new Size(width, height);
                grid.SetBounds(padding, padding, Math.Max(1, width - padding * 2), Math.Max(1, height - padding * 2));
                for(int group = 0; group < Categories.Length; group++) {
                    bool faces = group < Categories.Length - 1;
                    string[] entries = Entries[group].Split(faces ? ' ' : '|');
                    int columns = Math.Max(1, usable / D(faces ? 40 : 160));
                    int cellWidth = usable / columns, cellHeight = D(44);
                    Label heading = Theme.Label(Categories[group], Theme.Small, Theme.Muted);
                    heading.BackColor = Theme.Surface;
                    heading.Size = new Size(usable, D(28)); heading.Margin = new Padding(0, group == 0 ? 0 : D(8), 0, 0); grid.Controls.Add(heading); grid.SetFlowBreak(heading, true);
                    for(int i = 0; i < entries.Length; i++) {
                        string text = entries[i]; int index = buttons.Count;
                        Button button = new ColorEmoji.EmojiButton { Text = text, AccessibleName = text, TabIndex = index, FlatStyle = FlatStyle.Flat,
                            Font = faces ? emojiFont : Theme.Body, ForeColor = Theme.Ink, BackColor = Theme.Surface,
                            UseVisualStyleBackColor = false, Margin = new Padding(D(2)), Padding = Padding.Empty,
                            Size = new Size(Math.Max(1, cellWidth - D(4)), Math.Max(1, cellHeight - D(4))) };
                        button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Theme.Sky;
                        button.Click += delegate { Close(); picked(text); };
                        button.KeyDown += delegate(object sender, KeyEventArgs e) {
                            int next = index;
                            if(e.KeyCode == Keys.Right) next++; else if(e.KeyCode == Keys.Left) next--;
                            else if(e.KeyCode == Keys.Down) next += columns; else if(e.KeyCode == Keys.Up) next -= columns; else return;
                            next = Math.Max(0, Math.Min(buttons.Count - 1, next)); buttons[next].Focus(); grid.ScrollControlIntoView(buttons[next]); grid.FlushScrollRepaint(); e.Handled = e.SuppressKeyPress = true;
                        };
                        buttons.Add(button); grid.Controls.Add(button);
                        if(i == entries.Length - 1) grid.SetFlowBreak(button, true);
                    }
                }
                host.Size = surface.Size; Size = new Size(width + 2, height + 2);
            }
            finally { grid.ResumeLayout(true); surface.ResumeLayout(true); }
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
            if(buttons.Count > 0) buttons[0].Focus();
        }
        protected override void Dispose(bool disposing) { if (disposing) emojiFont.Dispose(); base.Dispose(disposing); }
    }
}
