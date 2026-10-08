using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    // 单个轻量弹层：分类不展开成长菜单，网格按工作区和 DPI 排布，空间不足时仅纵向滚动。
    internal sealed class EmojiPicker : ToolStripDropDown
    {
        private readonly Panel surface = new Panel();
        private readonly ComboBox category = new ComboBox();
        private readonly FlowLayoutPanel grid = new FlowLayoutPanel();
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
            category.DropDownStyle = ComboBoxStyle.DropDownList; category.Font = Theme.Body;
            category.AccessibleName = "表情分类"; category.Items.AddRange(Categories); category.TabIndex = 0;
            grid.AutoScroll = true; grid.WrapContents = true; grid.Margin = Padding.Empty;
            grid.AccessibleName = "表情网格"; surface.Controls.Add(category); surface.Controls.Add(grid);
            host = new ToolStripControlHost(surface) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            Items.Add(host); category.SelectedIndexChanged += delegate { ArrangeGrid(); };
            category.SelectedIndex = 0;
        }
        private int D(int value) { return (int)Math.Round(value * dpi / 96F); }
        internal void Prepare(Size bounds, int targetDpi)
        {
            available = bounds; dpi = targetDpi; ArrangeGrid();
        }
        private void ArrangeGrid()
        {
            if (category.SelectedIndex < 0) return;
            bool faces = category.SelectedIndex < Categories.Length - 1;
            string[] entries = Entries[category.SelectedIndex].Split(faces ? ' ' : '|');
            int width = Math.Max(1, Math.Min(D(368), available.Width - 2));
            int padding = D(8), header = Math.Max(D(36), category.PreferredHeight + D(8));
            int usable = Math.Max(1, width - padding * 2 - SystemInformation.VerticalScrollBarWidth);
            int columns = Math.Max(1, usable / D(faces ? 40 : 160));
            int cellWidth = usable / columns, cellHeight = D(44);
            int rows = (entries.Length + columns - 1) / columns;
            int height = Math.Max(1, Math.Min(available.Height - 2, header + padding * 2 + Math.Min(D(264), rows * cellHeight)));
            surface.SuspendLayout(); grid.SuspendLayout();
            try
            {
                foreach (Control child in grid.Controls.Cast<Control>().ToArray()) child.Dispose();
                grid.Controls.Clear(); grid.AutoScrollPosition = Point.Empty;
                BackColor = surface.BackColor = grid.BackColor = Theme.Surface;
                category.BackColor = Theme.Ice; category.ForeColor = Theme.Ink;
                surface.Size = new Size(width, height); category.SetBounds(padding, padding, Math.Max(1, width - padding * 2), category.PreferredHeight);
                grid.SetBounds(padding, padding + header, Math.Max(1, width - padding * 2), Math.Max(1, height - header - padding * 2));
                for (int i = 0; i < entries.Length; i++)
                {
                    string text = entries[i]; int index = i;
                    Button button = new ColorEmoji.EmojiButton { Text = text, AccessibleName = text, TabIndex = i + 1, FlatStyle = FlatStyle.Flat,
                        Font = faces ? emojiFont : Theme.Body, ForeColor = Theme.Ink, BackColor = Theme.Surface,
                        UseVisualStyleBackColor = false, Margin = new Padding(D(2)), Padding = Padding.Empty,
                        Size = new Size(Math.Max(1, cellWidth - D(4)), Math.Max(1, cellHeight - D(4))) };
                    button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Theme.Sky;
                    button.Click += delegate { Close(); picked(text); };
                    button.KeyDown += delegate(object sender, KeyEventArgs e) {
                        int next = index;
                        if (e.KeyCode == Keys.Right) next++; else if (e.KeyCode == Keys.Left) next--;
                        else if (e.KeyCode == Keys.Down) next += columns; else if (e.KeyCode == Keys.Up) next -= columns; else return;
                        next = Math.Max(0, Math.Min(grid.Controls.Count - 1, next)); grid.Controls[next].Focus(); grid.ScrollControlIntoView(grid.Controls[next]); e.Handled = e.SuppressKeyPress = true;
                    };
                    grid.Controls.Add(button);
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
            Prepare(new Size(Math.Max(1, work.Width - 16), Math.Max(1, work.Height - 16)), source.DeviceDpi);
            Show(Fit(source.RectangleToScreen(source.ClientRectangle), Size, work));
            category.Focus();
        }
        protected override void Dispose(bool disposing) { if (disposing) emojiFont.Dispose(); base.Dispose(disposing); }
    }
}
