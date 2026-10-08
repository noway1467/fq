using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FeiqLight
{
    public static class Theme
    {
        public static Color Blue = Color.FromArgb(51, 144, 236);
        public static Color Sky = Color.FromArgb(230, 241, 252);
        public static Color Ice = Color.FromArgb(241, 244, 247);
        public static Color Line = Color.FromArgb(233, 237, 241);
        public static Color Ink = Color.FromArgb(23, 33, 43);
        public static Color Muted = Color.FromArgb(115, 131, 146);
        public static Color Wallpaper = Color.FromArgb(239, 243, 247);
        public static Color Surface = Color.White;
        // 两端共用同一组视觉角色：操作色、轻选中、输入面、分隔线，不按页面另造颜色。
        public const int ControlRadius = 10;
        public const int BubbleRadius = 14;
        public static void Configure(bool dark,string accent)
        {
            Blue=accent=="green"?Color.FromArgb(34,153,116):accent=="purple"?Color.FromArgb(139,103,215):Color.FromArgb(51,144,236);
            Surface=dark?Color.FromArgb(30,39,49):Color.White;
            Ink=dark?Color.FromArgb(230,235,241):Color.FromArgb(23,33,43);
            Muted=dark?Color.FromArgb(166,182,199):Color.FromArgb(115,131,146);
            Ice=dark?Color.FromArgb(42,54,68):Color.FromArgb(241,244,247);
            Line=dark?Color.FromArgb(59,73,88):Color.FromArgb(233,237,241);
            Wallpaper=dark?Color.FromArgb(19,28,38):Color.FromArgb(239,243,247);
            if(accent=="green") {
                Surface=ColorTranslator.FromHtml(dark?"#172D29":"#F4FAF4");Ink=ColorTranslator.FromHtml(dark?"#E4F3E9":"#20382D");Muted=ColorTranslator.FromHtml(dark?"#A3C4B6":"#627D6A");Ice=ColorTranslator.FromHtml(dark?"#28433B":"#E4EFE2");Line=ColorTranslator.FromHtml(dark?"#36564B":"#D4E3CF");Wallpaper=ColorTranslator.FromHtml(dark?"#10221C":"#D5E5CC");
            } else if(accent=="purple") {
                Surface=ColorTranslator.FromHtml(dark?"#292338":"#FCF7FC");Ink=ColorTranslator.FromHtml(dark?"#F0E8F7":"#38283F");Muted=ColorTranslator.FromHtml(dark?"#BEB0D0":"#84708D");Ice=ColorTranslator.FromHtml(dark?"#3A304B":"#EFE5F1");Line=ColorTranslator.FromHtml(dark?"#50405F":"#E4D5E8");Wallpaper=ColorTranslator.FromHtml(dark?"#1A1227":"#E6D8ED");
            }
            Sky=Blend(Blue,Surface,dark?.30F:.17F);
        }
        public static Color Blend(Color foreground, Color background, float amount)
        {
            return Color.FromArgb((int)(foreground.R*amount+background.R*(1-amount)),(int)(foreground.G*amount+background.G*(1-amount)),(int)(foreground.B*amount+background.B*(1-amount)));
        }
        public static void Change(bool dark,string accent)
        {
            Color[] old={Blue,Surface,Ink,Muted,Ice,Line,Wallpaper,Sky}; Configure(dark,accent);
            Color[] next={Blue,Surface,Ink,Muted,Ice,Line,Wallpaper,Sky};
            foreach(Form form in Application.OpenForms) Recolor(form,old,next);
        }
        private static void Recolor(Control control,Color[] old,Color[] next)
        {
            for(int i=0;i<old.Length;i++) if(control.BackColor.ToArgb()==old[i].ToArgb()){control.BackColor=next[i];break;}
            for(int i=0;i<old.Length;i++) if(control.ForeColor.ToArgb()==old[i].ToArgb()&&!(control is Button&&control.ForeColor==Color.White)){control.ForeColor=next[i];break;}
            RoundedPanel panel=control as RoundedPanel;if(panel!=null)for(int i=0;i<old.Length;i++)if(panel.Fill==old[i]){panel.Fill=next[i];break;}
            if(control.ContextMenuStrip!=null){control.ContextMenuStrip.BackColor=Surface;control.ContextMenuStrip.ForeColor=Ink;}
            foreach(Control child in control.Controls)Recolor(child,old,next);control.Invalidate();
        }
        public static readonly Color Green = Color.FromArgb(73, 167, 107);
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font Title = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
        public static readonly Font Section = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8F);
        public static readonly Icon AppIcon = MakeIcon();
        public static void Apply(Form form)
        {
            form.Font = Body; form.ForeColor = Ink; form.BackColor = Theme.Surface; form.Icon = AppIcon;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.AutoScaleDimensions = new SizeF(96, 96);
        }
        public static Button Button(string text, EventHandler click, bool primary)
        {
            Button button = new RoundedButton { Text = text, Font = Body, AutoSize = false, Size = new Size(88, 36), MinimumSize = new Size(32, 30), FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Ice, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new Padding(4) };
            button.FlatAppearance.BorderColor = primary ? Blue : Line;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(39, 125, 214) : Ice;
            if (click != null) button.Click += click;
            return button;
        }
        public static Button IconButton(string icon, string label, EventHandler click, bool primary)
        {
            RoundedButton button = (RoundedButton)Button(label, click, primary); button.Glyph = icon; button.AccessibleName = label; button.Size = new Size(40, 40); button.BackColor = primary ? Blue : Surface; button.ForeColor = primary ? Color.White : Muted; button.Radius = primary ? 20 : ControlRadius; return button;
        }
        public static ContextMenuStrip Menu()
        {
            ContextMenuStrip menu=new ContextMenuStrip { Renderer = new ModernMenuRenderer(), ForeColor=Ink, Font = Body, ShowImageMargin = true, Padding = new Padding(6), BackColor = Theme.Surface };
            menu.Opening+=delegate { StyleMenu(menu); }; return menu;
        }
        private static void StyleMenu(ToolStripDropDown menu)
        {
            menu.BackColor=Surface; menu.ForeColor=Ink; menu.Font=Body;
            float scale=menu.DeviceDpi/96F; menu.Padding=new Padding((int)(8*scale));
            foreach(ToolStripItem item in menu.Items)
            {
                item.Padding=item is ToolStripSeparator?new Padding(0,(int)(4*scale),0,(int)(4*scale)):new Padding((int)(12*scale),(int)(8*scale),(int)(18*scale),(int)(8*scale));
                ToolStripMenuItem child=item as ToolStripMenuItem;
                if(child!=null && child.HasDropDownItems) { child.DropDown.Renderer=menu.Renderer; StyleMenu(child.DropDown); }
            }
        }
        public static bool DestructiveMenu(string text) { return text=="退出" || text=="断开" || text.StartsWith("删除") || text.StartsWith("清空") || text.StartsWith("拒绝"); }
        public static string MenuGlyph(string text)
        {
            if(text.StartsWith("复制")) return "copy";
            if(text=="剪切") return "cut";
            if(text=="粘贴") return "paste";
            if(text=="撤销") return "undo";
            if(text=="全选") return "select";
            if(text.Contains("目录") || text.Contains("位置")) return "folder";
            if(text.Contains("转发")) return "send";
            if(text.Contains("刷新") || text.Contains("重发") || text.Contains("重新发送")) return "refresh";
            if(text=="发送文件") return "clip";
            if(text.Contains("管理会话") || text.Contains("联系人")) return text.Contains("添加")?"plus":"person";
            if(text.Contains("外观设置")) return "palette";
            if(text.Contains("字号")) return "type";
            if(text.Contains("聊天记录")) return "history";
            if(text.Contains("设置")) return "settings";
            if(text.Contains("置顶")) return "pin";
            if(text=="关于") return "info";
            if(text.Contains("添加")) return "plus";
            if(text.Contains("关闭") || DestructiveMenu(text)) return "close";
            if(text.Contains("记录")) return "history";
            // 数值和未知菜单项不使用省略号冒充语义图标。
            return "";
        }
        public static void AttachEditMenu(TextBoxBase input)
        {
            ContextMenuStrip menu = Menu();
            ToolStripMenuItem undo = new ToolStripMenuItem("撤销", null, delegate { input.Undo(); }) { ShortcutKeyDisplayString = "Ctrl+Z" };
            ToolStripMenuItem cut = new ToolStripMenuItem("剪切", null, delegate { ClipboardAction(input.Cut); }) { ShortcutKeyDisplayString = "Ctrl+X" };
            ToolStripMenuItem copy = new ToolStripMenuItem("复制", null, delegate { ClipboardAction(input.Copy); }) { ShortcutKeyDisplayString = "Ctrl+C" };
            ToolStripMenuItem paste = new ToolStripMenuItem("粘贴", null, delegate { PasteText(input); }) { ShortcutKeyDisplayString = "Ctrl+V" };
            ToolStripMenuItem delete = new ToolStripMenuItem("删除", null, delegate { input.SelectedText = ""; });
            ToolStripMenuItem all = new ToolStripMenuItem("全选", null, delegate { input.SelectAll(); }) { ShortcutKeyDisplayString = "Ctrl+A" };
            menu.Items.Add(undo); menu.Items.Add(new ToolStripSeparator()); menu.Items.AddRange(new ToolStripItem[] { cut, copy, paste, delete }); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(all);
            menu.Opening += delegate
            {
                undo.Enabled = !input.ReadOnly && input.CanUndo; cut.Enabled = delete.Enabled = !input.ReadOnly && input.SelectionLength > 0;
                copy.Enabled = input.SelectionLength > 0; all.Enabled = input.TextLength > 0; paste.Enabled = false;
                ClipboardAction(delegate { paste.Enabled = !input.ReadOnly && Clipboard.ContainsText(); });
            };
            input.ContextMenuStrip = menu; input.Disposed += delegate { menu.Dispose(); };
        }
        private static void ClipboardAction(Action action)
        {
            try { action(); } catch (System.Runtime.InteropServices.ExternalException) { System.Media.SystemSounds.Beep.Play(); }
        }
        public static void PasteText(TextBoxBase input)
        {
            if (input.ReadOnly) return;
            ClipboardAction(delegate
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText(); int available = Math.Max(0, input.MaxLength - input.TextLength + input.SelectionLength);
                int count = Math.Min(text.Length, available);
                if (count > 0 && count < text.Length && Char.IsHighSurrogate(text[count - 1])) count--;
                input.SelectedText = text.Substring(0, count);
            });
        }
        public static void Glyph(Graphics g, string name, Rectangle bounds, Color color)
        {
            var old = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias;
            float size = Math.Min(bounds.Width, bounds.Height); g.TranslateTransform(bounds.X, bounds.Y); g.ScaleTransform(size / 24F, size / 24F);
            using (Pen p = new Pen(color, 1.7F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (name == "menu") { for (int y = 6; y <= 18; y += 6) g.DrawLine(p, 4, y, 20, y); }
                else if (name == "more") { using (SolidBrush b = new SolidBrush(color)) for (int y = 4; y <= 18; y += 7) g.FillEllipse(b, 10, y, 3, 3); }
                else if (name == "plus") { g.DrawLine(p, 12, 4, 12, 20); g.DrawLine(p, 4, 12, 20, 12); }
                else if (name == "close") { g.DrawLine(p, 6, 6, 18, 18); g.DrawLine(p, 18, 6, 6, 18); }
                else if (name == "min") g.DrawLine(p, 5, 15, 19, 15);
                else if (name == "max") g.DrawRectangle(p, 5, 5, 14, 14);
                else if (name == "search") { g.DrawEllipse(p, 4, 3, 12, 12); g.DrawLine(p, 15, 15, 21, 21); }
                else if (name == "send") { g.DrawPolygon(p, new[] { new PointF(3, 3), new PointF(22, 12), new PointF(3, 21), new PointF(6, 12) }); g.DrawLine(p, 6, 12, 20, 12); }
                else if (name == "smile") { g.DrawEllipse(p, 3, 3, 18, 18); g.DrawArc(p, 7, 8, 10, 9, 0, 180); g.DrawLine(p, 8, 8, 8, 9); g.DrawLine(p, 16, 8, 16, 9); }
                else if (name == "refresh") { g.DrawArc(p, 4, 4, 16, 16, 35, 290); g.DrawLines(p, new[] { new PointF(16, 3), new PointF(20, 4), new PointF(19, 8) }); }
                else if (name == "file") { g.DrawLines(p, new[] { new PointF(7, 2), new PointF(15, 2), new PointF(20, 7), new PointF(20, 22), new PointF(5, 22), new PointF(5, 2), new PointF(7, 2) }); g.DrawLine(p, 9, 12, 16, 12); g.DrawLine(p, 9, 16, 16, 16); }
                else if (name == "clip") { g.DrawArc(p, 7, 2, 10, 10, 180, 180); g.DrawLine(p, 7, 7, 7, 16); g.DrawArc(p, 7, 11, 8, 10, 0, 180); g.DrawLine(p, 15, 16, 15, 7); g.DrawLine(p, 11, 7, 11, 15); }
                else if (name == "copy") { g.DrawRectangle(p,8,8,12,13); g.DrawLines(p,new[]{new Point(5,16),new Point(3,16),new Point(3,3),new Point(15,3),new Point(15,5)}); }
                else if (name == "cut") { g.DrawEllipse(p,3,14,6,6);g.DrawEllipse(p,15,14,6,6);g.DrawLine(p,7,15,19,3);g.DrawLine(p,17,15,5,3); }
                else if (name == "paste") { g.DrawRectangle(p,5,5,14,16);g.DrawRectangle(p,9,2,6,5);g.DrawLine(p,9,12,15,12);g.DrawLine(p,9,16,15,16); }
                else if (name == "undo") { g.DrawArc(p,5,7,15,13,200,290);g.DrawLines(p,new[]{new Point(3,3),new Point(3,10),new Point(10,10)}); }
                else if (name == "select") { g.DrawRectangle(p,3,3,18,18);g.DrawLine(p,7,8,17,8);g.DrawLine(p,7,12,17,12);g.DrawLine(p,7,16,14,16); }
                else if (name == "folder") { g.DrawPolygon(p,new[]{new Point(3,6),new Point(9,6),new Point(11,9),new Point(21,9),new Point(21,20),new Point(3,20)}); }
                else if (name == "settings") { g.DrawEllipse(p,5,5,14,14);g.DrawEllipse(p,9,9,6,6);for(int angle=0;angle<360;angle+=45){double a=angle*Math.PI/180;g.DrawLine(p,12+(float)Math.Cos(a)*8,12+(float)Math.Sin(a)*8,12+(float)Math.Cos(a)*10,12+(float)Math.Sin(a)*10);} }
                else if (name == "history") { g.DrawArc(p,4,4,16,16,210,310);g.DrawLines(p,new[]{new Point(2,4),new Point(2,10),new Point(8,10)});g.DrawLines(p,new[]{new Point(12,7),new Point(12,12),new Point(16,14)}); }
                else if (name == "person") { g.DrawEllipse(p,8,3,8,8);g.DrawArc(p,4,14,16,12,180,180);g.DrawLine(p,4,20,20,20); }
                else if (name == "type") { g.DrawLines(p,new[]{new Point(3,19),new Point(9,4),new Point(15,19)});g.DrawLine(p,5,14,13,14);g.DrawLine(p,17,10,23,10);g.DrawLine(p,20,10,20,19); }
                else if (name == "palette") { g.DrawArc(p,3,3,18,18,90,290);g.DrawBezier(p,20,15,12,13,18,22,12,21);using(SolidBrush b=new SolidBrush(color)){g.FillEllipse(b,7,6,2,2);g.FillEllipse(b,13,5,2,2);g.FillEllipse(b,6,12,2,2);g.FillEllipse(b,17,9,2,2);} }
                else if (name == "inbox") { g.DrawLines(p,new[]{new Point(3,13),new Point(3,20),new Point(21,20),new Point(21,13)});g.DrawLine(p,12,3,12,15);g.DrawLines(p,new[]{new Point(8,11),new Point(12,15),new Point(16,11)}); }
                else if (name == "pin") { g.DrawLines(p,new[]{new Point(8,3),new Point(16,3),new Point(15,10),new Point(19,15),new Point(5,15),new Point(9,10),new Point(8,3)});g.DrawLine(p,12,15,12,22); }
                else if (name == "info") { g.DrawEllipse(p,3,3,18,18);g.DrawLine(p,12,11,12,17);g.DrawLine(p,12,7,12,7.5F); }
                else if (name == "check") g.DrawLines(p,new[]{new Point(5,12),new Point(10,17),new Point(19,7)});
            }
            g.Restore(old);
        }
        public static Label Label(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
        }
        public static string SizeText(long size)
        {
            if (size >= 1073741824) return (size / 1073741824.0).ToString("0.0") + " GB";
            if (size >= 1048576) return (size / 1048576.0).ToString("0.0") + " MB";
            if (size >= 1024) return (size / 1024.0).ToString("0.0") + " KB";
            return size + " B";
        }
        public static Bitmap Avatar(int size) { return Avatar(size, "Q"); }
        public static Bitmap Avatar(int size, string name)
        {
            Bitmap image = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(image))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(AvatarColor(name))) g.FillEllipse(b, 1, 1, size - 2, size - 2);
                string initial = String.IsNullOrEmpty(name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(name);
                using (Font font = new Font(Body.FontFamily, size * .38F, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush brush = new SolidBrush(Color.White))
                using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(initial, font, brush, new RectangleF(0, 0, size, size), format);
            }
            return image;
        }
        public static int AvatarHalo(int size) { return (int)Math.Ceiling(size * .1F); }
        public static Bitmap ContactAvatar(int size, string name, bool online)
        {
            int halo=AvatarHalo(size), extent=size+halo*2;
            Bitmap image=new Bitmap(extent,extent);
            using(Graphics g=Graphics.FromImage(image))
            {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                // 单独预留外发光画布，头像原图最后覆盖，内部颜色和字形不会被光晕染色。
                if(online) using(GraphicsPath path=new GraphicsPath())
                {
                    path.AddEllipse(0,0,extent,extent);
                    using(PathGradientBrush glow=new PathGradientBrush(path))
                    {
                        glow.CenterColor=Color.FromArgb(125,51,144,236);glow.SurroundColors=new[]{Color.FromArgb(0,51,144,236)};
                        float focus=(size-3F)/extent;glow.FocusScales=new PointF(focus,focus);
                        g.FillPath(glow,path);
                    }
                }
                using(Bitmap face=Avatar(size,name)) g.DrawImageUnscaled(face,halo,halo);
            }
            return image;
        }
        public static Color AvatarColor(string name)
        {
            Color[] colors = { Color.FromArgb(102, 169, 224), Color.FromArgb(126, 155, 219), Color.FromArgb(101, 188, 165), Color.FromArgb(220, 161, 105), Color.FromArgb(163, 140, 201) };
            uint hash = 0; foreach (char c in name ?? "") hash = unchecked(hash * 31 + c);
            return colors[hash % colors.Length];
        }
        public static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath(); int d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        private static Icon MakeIcon()
        {
            // 图标由几何绘制生成，不依赖原版素材。
            using (Bitmap bitmap = Avatar(32))
            {
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    }
    public sealed class RoundedButton : Button
    {
        public string Glyph = "";
        public int Radius = Theme.ControlRadius;
        private bool hover;
        public RoundedButton() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color background = !Enabled ? Theme.Ice : hover ? Theme.Blend(Theme.Ink, BackColor, .06F) : BackColor;
            e.Graphics.Clear(Parent == null ? Theme.Surface : Parent.BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 2), Math.Max(2, Height - 2));
            using (GraphicsPath path = Theme.Rounded(r, Math.Min((int)(Radius * e.Graphics.DpiX / 96F), Math.Min(r.Width, r.Height) / 2)))
            { using (SolidBrush b = new SolidBrush(background)) e.Graphics.FillPath(b, path); if (Focused && ShowFocusCues) using (Pen p = new Pen(Theme.Blue)) e.Graphics.DrawPath(p, path); }
            Color color = Enabled ? ForeColor : Theme.Muted;
            if (Glyph.Length > 0) { int size = (int)(22 * e.Graphics.DpiX / 96F); Theme.Glyph(e.Graphics, Glyph, new Rectangle((Width - size) / 2, (Height - size) / 2, size, size), color); }
            else TextRenderer.DrawText(e.Graphics, Text, Font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    public sealed class RoundedPanel : Panel
    {
        public int Radius = 14;
        public Color Fill = Theme.Ice;
        public RoundedPanel() { DoubleBuffered = true; BackColor = Theme.Surface; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); if (Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min((int)(Radius * e.Graphics.DpiX / 96F), Math.Min(Width, Height) / 2)))
            using (SolidBrush b = new SolidBrush(Fill)) e.Graphics.FillPath(b, path);
        }
    }
    public sealed class RoundedInput : RoundedInputBase { }
    public class RoundedInputBase : Panel
    {
        public readonly TextBox Input = new TextBox();
        public RoundedInputBase()
        {
            DoubleBuffered = true; BackColor = Theme.Surface; Padding = new Padding(15, 11, 15, 9); Height = 44;
            Input.BorderStyle = BorderStyle.None; Input.BackColor = Theme.Ice; Input.ForeColor = Theme.Ink; Input.Dock = DockStyle.Fill; Input.Font = Theme.Body; Controls.Add(Input); Theme.AttachEditMenu(Input);
            Input.Enter += delegate { Invalidate(); }; Input.Leave += delegate { Invalidate(); };
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); if (Width < 4 || Height < 4) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min((int)(Theme.ControlRadius * e.Graphics.DpiX / 96F), Math.Min(Width, Height) / 2)))
            { using (SolidBrush b = new SolidBrush(Theme.Ice)) e.Graphics.FillPath(b, path); if (ContainsFocus) using (Pen pen = new Pen(Theme.Blue)) e.Graphics.DrawPath(pen, path); }
        }
        public string Placeholder { set { if (!Input.IsHandleCreated) Input.CreateControl(); SendMessage(Input.Handle, 0x1501, new IntPtr(1), value); } }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr w, string text);
    }
    public sealed class ToggleSwitch : CheckBox
    {
        public ToggleSwitch() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); AutoSize = false; Height = 42; Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; int w = (int)(36 * e.Graphics.DpiX / 96F), h = w / 2;
            Rectangle r = new Rectangle(Width - w - 4, (Height - h) / 2, w, h);
            using (GraphicsPath path = Theme.Rounded(r, h / 2)) using (SolidBrush b = new SolidBrush(Checked ? Theme.Blue : Theme.Line)) e.Graphics.FillPath(b, path);
            using (SolidBrush b = new SolidBrush(Color.White)) e.Graphics.FillEllipse(b, Checked ? r.Right - h + 2 : r.Left + 2, r.Top + 2, h - 4, h - 4);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 0, Width - w - 14, Height), Theme.Ink, TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }
    }
    public sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        private static Color ItemColor(ToolStripItem item) { return !item.Enabled?Theme.Blend(Theme.Muted,Theme.Surface,.65F):Theme.DestructiveMenu(item.Text)?Color.FromArgb(210,78,84):Theme.Ink; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor=ItemColor(e.Item); e.TextRectangle=new Rectangle(e.TextRectangle.X,0,e.TextRectangle.Width,e.Item.Height); e.TextFormat|=TextFormatFlags.VerticalCenter; base.OnRenderItemText(e); }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { int size=(int)(18*e.Graphics.DpiX/96F); Theme.Glyph(e.Graphics,"check",new Rectangle((int)(10*e.Graphics.DpiX/96F),(e.Item.Height-size)/2,size,size),Theme.Blue); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor=ItemColor(e.Item); base.OnRenderArrow(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using(Pen p=new Pen(Theme.Line)) e.Graphics.DrawLine(p,32,e.Item.Height/2,e.Item.Width-10,e.Item.Height/2); }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {e.Graphics.Clear(Theme.Surface);}
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { e.Graphics.SmoothingMode=SmoothingMode.AntiAlias; using(Pen pen=new Pen(Theme.Line)) using(GraphicsPath path=Theme.Rounded(new Rectangle(0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1),Theme.ControlRadius)) e.Graphics.DrawPath(pen,path); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if(e.Item.Selected && e.Item.Enabled) {
                Rectangle r = new Rectangle(2,1,e.Item.Width-4,e.Item.Height-2);
                using(GraphicsPath path=Theme.Rounded(r,7)) using(SolidBrush b=new SolidBrush(Theme.Sky)) e.Graphics.FillPath(b,path);
            }
            ToolStripMenuItem item=e.Item as ToolStripMenuItem;
            if(item!=null && !item.Checked && item.Image==null) { int size=(int)(18*e.Graphics.DpiX/96F); Theme.Glyph(e.Graphics,Theme.MenuGlyph(item.Text),new Rectangle((int)(10*e.Graphics.DpiX/96F),(item.Height-size)/2,size,size),ItemColor(item)); }
        }
    }
    public class ModernWindow : Form
    {
        private sealed class BufferedPanel : Panel { public BufferedPanel() { DoubleBuffered=true; ResizeRedraw=true; } }
        private sealed class BufferedTable : TableLayoutPanel { public BufferedTable() { DoubleBuffered=true; ResizeRedraw=true; } }
        public readonly Panel Surface = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        private readonly TableLayoutPanel chrome = new BufferedTable { Dock = DockStyle.Top, Height = 36, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        private readonly Label caption = Theme.Label("", Theme.Small, Theme.Muted);
        private readonly Button minimize, maximize;
        private bool resizeWindow = true;
        public bool ResizeWindow
        {
            get { return resizeWindow; }
            set { resizeWindow = value; if (minimize != null) { minimize.Visible = maximize.Visible = value; chrome.ColumnStyles[1].Width = value ? 126 : 42; chrome.Height = value ? 36 : 52; caption.Font = value ? Theme.Small : Theme.Title; caption.ForeColor = value ? Theme.Muted : Theme.Ink; } }
        }
        public ModernWindow()
        {
            Theme.Apply(this); DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); FormBorderStyle = FormBorderStyle.None; Padding = new Padding(1); StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(400, 300);
            Controls.Add(Surface); Controls.Add(chrome); chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126)); chrome.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); caption.Dock = DockStyle.Fill; caption.Padding = new Padding(15, 0, 0, 0); caption.Margin = Padding.Empty; chrome.Controls.Add(caption, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 126, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
            minimize = Theme.IconButton("min", "最小化", delegate { WindowState = FormWindowState.Minimized; }, false);
            maximize = Theme.IconButton("max", "最大化", delegate { if (ResizeWindow) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }, false);
            Button close = Theme.IconButton("close", "关闭窗口", delegate { Close(); }, false);
            foreach (Button button in new[] { minimize, maximize, close }) { button.Margin = Padding.Empty; button.Size = new Size(40, 32); actions.Controls.Add(button); }
            actions.Dock = DockStyle.Fill; actions.Margin = Padding.Empty; chrome.Controls.Add(actions, 1, 0); caption.MouseDown += Drag; chrome.MouseDown += Drag;
            caption.DoubleClick += delegate { if (ResizeWindow) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; };
            TextChanged += delegate { caption.Text = Text; };
            LocationChanged += delegate { MaximizedBounds = Screen.FromControl(this).WorkingArea; };
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
        }
        // 各自绘控件与容器独立双缓冲；不强制整棵原生子窗口 WS_EX_COMPOSITED，
        // 避免遮挡/恢复时原生编辑器与滚动控件的呈现被同一合成周期牵制。
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 交给 DWM 处理圆角，不能在每个 WM_SIZE 重建 Region（会强制全窗重绘）。
            // 旧系统不支持此属性时保留方角，不影响缩放与输入。
            try { int preference = 2; DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        private void Drag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m); if (m.Msg != 0x84 || !ResizeWindow || WindowState != FormWindowState.Normal) return;
            long v = m.LParam.ToInt64(); Point p = PointToClient(new Point(unchecked((short)(v & 65535)), unchecked((short)((v >> 16) & 65535))));
            int edge = 6; bool l = p.X < edge, r = p.X >= Width - edge, t = p.Y < edge, b = p.Y >= Height - edge;
            if (l || r || t || b) m.Result = new IntPtr(t ? (l ? 13 : r ? 14 : 12) : b ? (l ? 16 : r ? 17 : 15) : l ? 10 : 11);
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    }
    public sealed class SkyPanel : Panel
    {
        public SkyPanel() { DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1) return;
            e.Graphics.Clear(Theme.Surface);
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }
}
