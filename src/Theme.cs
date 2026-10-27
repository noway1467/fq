using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight
{
    public static class Theme
    {
        public static Color Blue = Color.FromArgb(51, 144, 236);
        public static Color Sky = Color.FromArgb(230, 241, 252);
        public static Color Ice = Color.FromArgb(241, 244, 247);
        public static Color Line = Color.FromArgb(231, 236, 240);
        public static Color Ink = Color.FromArgb(23, 33, 43);
        public static Color Muted = Color.FromArgb(115, 131, 146);
        public static Color Wallpaper = Color.FromArgb(209, 223, 245);
        public static Color Surface = Color.White;
        // 派生角色：发出气泡、悬停底色、危险操作与覆盖滚动条，均随主题统一换算，不在页面里另造颜色。
        public static Color Outgoing = Color.FromArgb(224, 238, 252);
        public static Color Hover = Color.FromArgb(243, 245, 246);
        public static Color Danger = Color.FromArgb(225, 62, 62);
        public static Color ScrollThumb = Color.FromArgb(23, 33, 43);
        public static Color Online = Color.FromArgb(60, 179, 79);
        public static Color[] WallpaperColors = { Color.FromArgb(214, 233, 250), Color.FromArgb(198, 214, 246), Color.FromArgb(203, 229, 238), Color.FromArgb(219, 214, 245) };
        public static bool Dark { get; private set; }
        public static string AccentKey { get; private set; }
        // 主题版本号：缓存（壁纸、头像）比对它即可得知配色已变化。
        public static int Version { get; private set; }
        public const int ControlRadius = 10;
        public const int BubbleRadius = 15;
        // 字段初值只是占位；启动即按默认浅色经典蓝换算全部角色，避免与 Configure 的结果漂移。
        static Theme() { Configure(false, "blue"); }
        public static void Configure(bool dark,string accent)
        {
            Dark=dark; AccentKey=accent=="green"||accent=="purple"?accent:"blue"; Version++;
            string[] palette;
            if(AccentKey=="green") palette=dark?new[]{"#2CB385","#14221D","#E2F1E8","#8FAE9F","#1E3129","#29423A","#0F241C","#16382B","#0E2A21","#1B3A31"}:new[]{"#229974","#F7FBF8","#1D3328","#5E7368","#EAF2EC","#DCE8DF","#DAECCE","#BEDEBE","#E2EAC8","#C8E2CD"};
            else if(AccentKey=="purple") palette=dark?new[]{"#9A78E6","#211B2E","#EEE7F6","#A897BA","#2D253D","#3A3050","#1C1330","#2D1E47","#25162F","#2A2045"}:new[]{"#8B67D7","#FCF9FD","#33263D","#6E6478","#F2EBF6","#E7DDEC","#ECDEF6","#D7CCF2","#F2E0EC","#DED6F4"};
            else palette=dark?new[]{"#3390EC","#17212B","#E9EEF3","#8A9AAB","#22303D","#2B3A48","#0E1A28","#16294A","#0C1F2B","#1C2141"}:new[]{"#3390EC","#FFFFFF","#17212B","#64727F","#F1F4F7","#E7ECF0","#D6E9FA","#C6D6F6","#CBE5EE","#DBD6F5"};
            Blue=Hex(palette[0]);Surface=Hex(palette[1]);Ink=Hex(palette[2]);Muted=Hex(palette[3]);Ice=Hex(palette[4]);Line=Hex(palette[5]);
            WallpaperColors=new[]{Hex(palette[6]),Hex(palette[7]),Hex(palette[8]),Hex(palette[9])};
            Wallpaper=Color.FromArgb((WallpaperColors[0].R+WallpaperColors[1].R+WallpaperColors[2].R+WallpaperColors[3].R)/4,(WallpaperColors[0].G+WallpaperColors[1].G+WallpaperColors[2].G+WallpaperColors[3].G)/4,(WallpaperColors[0].B+WallpaperColors[1].B+WallpaperColors[2].B+WallpaperColors[3].B)/4);
            Sky=Blend(Blue,Surface,dark?.30F:.17F);
            Outgoing=Blend(Blue,Surface,dark?.42F:.15F);
            Hover=Blend(Ink,Surface,dark?.08F:.05F);
            Danger=dark?Color.FromArgb(255,110,110):Color.FromArgb(225,62,62);
            ScrollThumb=dark?Color.FromArgb(200,210,220):Color.FromArgb(40,50,60);
            Online=dark?Color.FromArgb(88,196,104):Color.FromArgb(60,179,79);
        }
        private static Color Hex(string value) { return ColorTranslator.FromHtml(value); }
        public static Color Blend(Color foreground, Color background, float amount)
        {
            return Color.FromArgb((int)(foreground.R*amount+background.R*(1-amount)),(int)(foreground.G*amount+background.G*(1-amount)),(int)(foreground.B*amount+background.B*(1-amount)));
        }
        // 发出气泡上的时间与状态：与强调色同色系，深浅主题都保持可读。
        public static Color OutgoingMeta { get { return Dark?Blend(Blue,Ink,.38F):Blend(Blue,Ink,.62F); } }
        public static Color Link { get { return Dark?Blend(Color.White,Blue,.28F):Blue; } }
        // 日期胶囊等服务提示：取壁纸平均色压暗，浅底深底都能衬白字。
        public static Color ServiceFill { get { return Color.FromArgb(Dark?150:165,Blend(Color.Black,Wallpaper,Dark?.45F:.42F)); } }
        public static void Change(bool dark,string accent)
        {
            Color[] old={Blue,Surface,Ink,Muted,Ice,Line,Wallpaper,Sky,Outgoing,Hover};Configure(dark,accent);
            Color[] next={Blue,Surface,Ink,Muted,Ice,Line,Wallpaper,Sky,Outgoing,Hover};
            foreach(Form form in Application.OpenForms) Recolor(form,old,next);
            Action handler=Changed;if(handler!=null)handler();
        }
        public static event Action Changed;
        private static void Recolor(Control control,Color[] old,Color[] next)
        {
            // 背景与前景分表换算：深浅主题的 Surface 与 Ink 可能同值（如 #17212B），
            // 若共用一张表，前景会被误当成背景角色，切换后出现白字白底。
            Color back=control.BackColor,fore=control.ForeColor;
            if(back.ToArgb()==old[0].ToArgb())control.BackColor=next[0];
            else if(back.ToArgb()==old[1].ToArgb())control.BackColor=next[1];
            else for(int i=4;i<old.Length;i++)if(back.ToArgb()==old[i].ToArgb()){control.BackColor=next[i];break;}
            if(fore.ToArgb()==old[2].ToArgb())control.ForeColor=next[2];
            else if(fore.ToArgb()==old[3].ToArgb())control.ForeColor=next[3];
            else if(fore.ToArgb()==old[0].ToArgb())control.ForeColor=next[0];
            RoundedPanel panel=control as RoundedPanel;if(panel!=null)for(int i=0;i<old.Length;i++)if(panel.Fill==old[i]){panel.Fill=next[i];break;}
            if(control.ContextMenuStrip!=null){control.ContextMenuStrip.BackColor=Surface;control.ContextMenuStrip.ForeColor=Ink;}
            if(control.IsHandleCreated&&(control is TextBoxBase||control is ListBox||(control is ScrollableControl&&((ScrollableControl)control).AutoScroll)))ApplyScrollTheme(control);
            foreach(Control child in control.Controls)Recolor(child,old,next);control.Invalidate();
        }
        public static readonly Color Green = Color.FromArgb(73, 167, 107);
        public static Color SearchHighlight { get { return Blend(Color.FromArgb(244,185,42),Surface,Dark?.42F:.48F); } }
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9F);
        public static readonly Font Title = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
        public static readonly Font Section = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        public static readonly Font Strong = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8F);
        public static readonly Icon AppIcon = MakeIcon();
        // 托盘按系统小图标尺寸从同一 ICO 取图（100% 为 16、125% 为 20），不让 Shell 把 32 像素图标缩小发虚。
        public static readonly Icon TrayIcon = new Icon(AppIcon, SystemInformation.SmallIconSize);
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
            if (click != null) button.Click += click;
            return button;
        }
        public static Button IconButton(string icon, string label, EventHandler click, bool primary)
        {
            RoundedButton button = (RoundedButton)Button(label, click, primary); button.Glyph = icon; button.AccessibleName = label; button.Size = new Size(40, 40); button.BackColor = primary ? Blue : Surface; button.ForeColor = primary ? Color.White : Muted; button.Radius = primary ? 20 : ControlRadius; return button;
        }
        private static readonly ModernMenuRenderer menuRenderer = new ModernMenuRenderer();
        public static ContextMenuStrip Menu()
        {
            ContextMenuStrip menu=new ContextMenuStrip { Renderer = menuRenderer, ForeColor=Ink, Font = Body, ShowImageMargin = true, Padding = new Padding(6), BackColor = Theme.Surface, DropShadowEnabled=true };
            menu.Opening+=delegate { StyleMenu(menu); }; PrepareDropDown(menu); return menu;
        }
        // 弹层淡入并请求 Windows 11 圆角；旧系统忽略圆角属性，仍保留阴影与描边。
        internal static void PrepareDropDown(ToolStripDropDown menu)
        {
            if(menu.Tag as string=="feiq-prepared")return; if(menu.Tag==null)menu.Tag="feiq-prepared";
            menu.HandleCreated+=delegate{RoundWindow(menu.Handle,3);};
            double start=0;Func<bool> fade=delegate{
                if(menu.IsDisposed)return false;double t=Motion.Progress(start,130);
                if(!menu.Visible){menu.Opacity=1;return false;}
                menu.Opacity=t>=1?1:.2+.8*Motion.EaseOut(t);return t<1;
            };
            menu.Opening+=delegate{if(Motion.Enabled)menu.Opacity=.2;};
            menu.Opened+=delegate{start=Motion.Now;if(Motion.Enabled)Motion.Run(fade);else menu.Opacity=1;};
            menu.Closed+=delegate{menu.Opacity=1;};
        }
        // Opening 时统一配色与尺寸；托盘菜单在 Opening 之前就按当前尺寸定位，需在创建后先调用一次。
        internal static void StyleMenu(ToolStripDropDown menu)
        {
            menu.BackColor=Surface; menu.ForeColor=Ink; menu.Font=Body;
            // 与渲染器同取实际绘制 DPI：未启用每监视器 DPI 时 DeviceDpi 恒为 96，125% 下会比绘制的图标栏和文字窄一截。
            float scale; using(Graphics screen=Graphics.FromHwnd(IntPtr.Zero)) scale=screen.DpiX/96F;
            int content=0;
            foreach(ToolStripItem item in menu.Items)
            {
                item.Padding=item is ToolStripSeparator?new Padding(0,(int)(4*scale),0,(int)(4*scale)):new Padding((int)(10*scale),(int)(8*scale),(int)(18*scale),(int)(8*scale));
                item.Margin=Padding.Empty;
                ToolStripMenuItem child=item as ToolStripMenuItem;
                if(child!=null && child.HasDropDownItems) { child.DropDown.Renderer=menu.Renderer; PrepareDropDown(child.DropDown); StyleMenu(child.DropDown); }
                // 资料项自绘并省略过长昵称，不参与撑宽；隐藏项也计入，Opening 中切换可见性时宽度不跳。
                if(child==null || item.Tag as string=="profile") continue;
                int need=MenuTextLeft(menu,scale)+TextRenderer.MeasureText(child.Text,item.Font).Width+MenuTrailing(item,scale);
                if(!String.IsNullOrEmpty(child.ShortcutKeyDisplayString)) need+=(int)(24*scale)+TextRenderer.MeasureText(child.ShortcutKeyDisplayString,item.Font).Width;
                content=Math.Max(content,need);
            }
            // 原生下拉菜单只按共享文字区求宽且会把 Padding 改回 (图标栏,2,1,2)，自设的项宽不会撑开窗口。
            // 宽度写回 MinimumSize，窗口与菜单项同宽；顶层菜单至少 200，子菜单沿用调用方给的最小宽度。
            int width=Math.Max(Math.Max(menu.MinimumSize.Width,menu.OwnerItem==null?(int)(200*scale):0),Math.Max(content,menu.GetPreferredSize(Size.Empty).Width));
            menu.MinimumSize=new Size(width,0);
            // 菜单项从 x=0 开始（图标栏占用的左 Padding 在布局时扣除），只需让出右侧 1 像素边距。
            foreach(ToolStripItem item in menu.Items) {
                item.AutoSize=false;item.Size=new Size(Math.Max((int)(96*scale),width-menu.Padding.Right),item is ToolStripSeparator?(int)(9*scale):item.Tag as string=="profile"?(int)(62*scale):(int)(36*scale));
            }
        }
        // 自绘菜单的横向排布：正文从图标栏后开始，右侧依次让出快捷键、子菜单箭头或开关；测宽与绘制共用。
        internal static int MenuTextLeft(ToolStrip owner,float scale) { ToolStripDropDownMenu menu=owner as ToolStripDropDownMenu; return (int)((menu!=null&&!menu.ShowImageMargin?32:46)*scale); }
        internal static int MenuTrailing(ToolStripItem item,float scale)
        {
            ToolStripMenuItem menuItem=item as ToolStripMenuItem;
            if(item.Tag as string=="switch") return (int)(56*scale);
            return (int)((menuItem!=null&&menuItem.HasDropDownItems?40:16)*scale);
        }
        // 只有会丢弃数据或结束程序的操作标红；收起会话不是破坏性操作。
        public static bool DestructiveMenu(string text) { return text=="退出" || text=="断开" || text.StartsWith("删除") || text.StartsWith("清空") || text.StartsWith("拒绝"); }
        public static string MenuGlyph(string text)
        {
            if(text.StartsWith("复制文件路径")) return "link";
            if(text.StartsWith("复制")) return "copy";
            if(text=="剪切") return "cut";
            if(text=="粘贴") return "paste";
            if(text=="撤销") return "undo";
            if(text=="全选") return "select";
            if(text=="删除") return "trash";
            if(text.StartsWith("查看全文")) return "doc";
            if(text.Contains("目录") || text.Contains("位置")) return "folder";
            if(text.StartsWith("打开方式")) return "apps";
            if(text=="打开文件") return "open";
            if(text=="打开") return "chat";
            if(text.Contains("转发")) return "forward";
            if(text.Contains("刷新") || text.Contains("重发") || text.Contains("重新发送")) return "refresh";
            if(text=="发送文件") return "clip";
            if(text.StartsWith("接收")) return "download";
            if(text.Contains("备注")) return "edit";
            if(text.Contains("已移除")) return "people";
            if(text.Contains("添加联系人")) return "user-plus";
            if(text.Contains("管理会话") || text.Contains("联系人")) return text.StartsWith("删除")?"trash":"person";
            if(text.StartsWith("清空")) return "eraser";
            if(text.Contains("外观设置")) return "palette";
            if(text.Contains("字号")) return "type";
            if(text.Contains("聊天记录")) return "history";
            if(text.Contains("夜间")) return "moon";
            if(text.Contains("设置")) return "settings";
            if(text.Contains("置顶")) return "pin";
            if(text=="关于") return "info";
            if(text=="退出") return "power";
            if(text.Contains("添加")) return "plus";
            if(text.StartsWith("取消") || text.Contains("关闭") || DestructiveMenu(text)) return "close";
            if(text.Contains("记录")) return "history";
            // 数值和未知菜单项不使用省略号冒充语义图标。
            return "";
        }
        public static void AttachEditMenu(TextBoxBase input, Action pasteOverride = null)
        {
            ContextMenuStrip menu = Menu();
            ToolStripMenuItem undo = new ToolStripMenuItem("撤销", null, delegate { input.Undo(); }) { ShortcutKeyDisplayString = "Ctrl+Z" };
            ToolStripMenuItem cut = new ToolStripMenuItem("剪切", null, delegate { ClipboardAction(input.Cut); }) { ShortcutKeyDisplayString = "Ctrl+X" };
            ToolStripMenuItem copy = new ToolStripMenuItem("复制", null, delegate { ClipboardAction(input.Copy); }) { ShortcutKeyDisplayString = "Ctrl+C" };
            ToolStripMenuItem paste = new ToolStripMenuItem("粘贴", null, delegate { if (pasteOverride != null) pasteOverride(); else PasteText(input); }) { ShortcutKeyDisplayString = "Ctrl+V" };
            ToolStripMenuItem delete = new ToolStripMenuItem("删除", null, delegate { input.SelectedText = ""; });
            ToolStripMenuItem all = new ToolStripMenuItem("全选", null, delegate { input.SelectAll(); }) { ShortcutKeyDisplayString = "Ctrl+A" };
            menu.Items.Add(undo); menu.Items.Add(new ToolStripSeparator()); menu.Items.AddRange(new ToolStripItem[] { cut, copy, paste, delete }); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(all);
            menu.Opening += delegate
            {
                undo.Enabled = !input.ReadOnly && input.CanUndo; cut.Enabled = delete.Enabled = !input.ReadOnly && input.SelectionLength > 0;
                copy.Enabled = input.SelectionLength > 0; all.Enabled = input.TextLength > 0; paste.Enabled = false;
                ClipboardAction(delegate { paste.Enabled = !input.ReadOnly && (Clipboard.ContainsText() || (pasteOverride != null && Clipboard.ContainsImage())); });
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
        public static void Glyph(Graphics g, string name, Rectangle bounds, Color color) { Glyph(g, name, (RectangleF)bounds, color); }
        // 24 单位网格的线性图标：统一 1.8 线宽、圆头圆角，缩放到任意尺寸仍保持同一视觉重量。
        public static void Glyph(Graphics g, string name, RectangleF bounds, Color color)
        {
            if (String.IsNullOrEmpty(name) || bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState old = g.Save(); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float size = Math.Min(bounds.Width, bounds.Height); g.TranslateTransform(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2); g.ScaleTransform(size / 24F, size / 24F);
            using (Pen p = new Pen(color, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (SolidBrush b = new SolidBrush(color))
            {
                switch (name)
                {
                    case "menu": g.DrawLine(p, 4.5F, 7, 19.5F, 7); g.DrawLine(p, 4.5F, 12, 19.5F, 12); g.DrawLine(p, 4.5F, 17, 19.5F, 17); break;
                    case "more": foreach (float y in new[] { 5.5F, 12F, 18.5F }) g.FillEllipse(b, 10.2F, y - 1.8F, 3.6F, 3.6F); break;
                    case "plus": g.DrawLine(p, 12, 5, 12, 19); g.DrawLine(p, 5, 12, 19, 12); break;
                    case "close": g.DrawLine(p, 6.5F, 6.5F, 17.5F, 17.5F); g.DrawLine(p, 17.5F, 6.5F, 6.5F, 17.5F); break;
                    case "min": using (Pen thin = new Pen(color, 1.2F)) g.DrawLine(thin, 6.5F, 12.5F, 17.5F, 12.5F); break;
                    case "max": using (Pen thin = new Pen(color, 1.2F)) using (GraphicsPath path = RoundedF(new RectangleF(6.5F, 6.5F, 11, 11), 1.5F)) g.DrawPath(thin, path); break;
                    case "code": g.DrawLines(p, new[] { new PointF(8, 7), new PointF(3, 12), new PointF(8, 17) }); g.DrawLines(p, new[] { new PointF(16, 7), new PointF(21, 12), new PointF(16, 17) }); g.DrawLine(p, 13.5F, 5, 10.5F, 19); break;
                    case "search": g.DrawEllipse(p, 4, 4, 12.5F, 12.5F); g.DrawLine(p, 14.8F, 14.8F, 20, 20); break;
                    case "send":
                        using (GraphicsPath plane = new GraphicsPath())
                        {
                            plane.AddPolygon(new[] { new PointF(3.2F, 11.4F), new PointF(20.8F, 3.6F), new PointF(14F, 20.8F), new PointF(11.1F, 13.6F) });
                            g.FillPath(b, plane); using (Pen join = new Pen(color, 1.4F) { LineJoin = LineJoin.Round }) g.DrawPath(join, plane);
                        }
                        break;
                    case "smile": g.DrawEllipse(p, 3, 3, 18, 18); g.FillEllipse(b, 8.1F, 8.6F, 2.2F, 2.6F); g.FillEllipse(b, 13.7F, 8.6F, 2.2F, 2.6F); g.DrawArc(p, 7.6F, 8.4F, 8.8F, 8.2F, 25, 130); break;
                    case "clip":
                        g.TranslateTransform(12, 12); g.RotateTransform(45); g.TranslateTransform(-12, -12);
                        using (GraphicsPath clip = new GraphicsPath())
                        {
                            clip.AddLine(15, 8, 15, 16.5F); clip.AddArc(9, 13.5F, 6, 6, 0, 180); clip.AddLine(9, 16.5F, 9, 6.5F); clip.AddArc(9, 4.5F, 4, 4, 180, 180); clip.AddLine(13, 6.5F, 13, 15.5F);
                            g.DrawPath(p, clip);
                        }
                        break;
                    case "refresh": g.DrawArc(p, 4.5F, 4.5F, 15, 15, 35, 285); g.DrawLines(p, new[] { new PointF(19.6F, 4.4F), new PointF(19.4F, 8.9F), new PointF(15F, 8.6F) }); break;
                    case "file": g.DrawLines(p, new[] { new PointF(13.5F, 3), new PointF(7, 3), new PointF(5.5F, 4.5F), new PointF(5.5F, 19.5F), new PointF(7, 21), new PointF(17, 21), new PointF(18.5F, 19.5F), new PointF(18.5F, 8), new PointF(13.5F, 3), new PointF(13.5F, 8), new PointF(18.5F, 8) }); break;
                    case "doc": g.DrawLine(p, 5, 6, 19, 6); g.DrawLine(p, 5, 10, 19, 10); g.DrawLine(p, 5, 14, 19, 14); g.DrawLine(p, 5, 18, 13, 18); break;
                    case "copy": using (GraphicsPath front = RoundedF(new RectangleF(8.5F, 8.5F, 11.5F, 12.5F), 2.2F)) g.DrawPath(p, front); g.DrawLines(p, new[] { new PointF(15.5F, 5), new PointF(15.5F, 4.8F), new PointF(14, 3.3F), new PointF(5.5F, 3.3F), new PointF(4, 4.8F), new PointF(4, 14.5F), new PointF(5.5F, 16), new PointF(5.8F, 16) }); break;
                    case "cut": g.DrawEllipse(p, 3.5F, 14, 6, 6); g.DrawEllipse(p, 14.5F, 14, 6, 6); g.DrawLine(p, 8.2F, 15.2F, 18.5F, 3.5F); g.DrawLine(p, 15.8F, 15.2F, 5.5F, 3.5F); break;
                    case "paste": using (GraphicsPath board = RoundedF(new RectangleF(5, 5, 14, 16.5F), 2)) g.DrawPath(p, board); using (GraphicsPath clip = RoundedF(new RectangleF(9, 2.8F, 6, 4.2F), 1.2F)) g.DrawPath(p, clip); g.DrawLine(p, 9, 12, 15, 12); g.DrawLine(p, 9, 16, 15, 16); break;
                    case "undo": g.DrawArc(p, 5.5F, 7.5F, 14, 12.5F, 205, 285); g.DrawLines(p, new[] { new PointF(4, 4.5F), new PointF(4.3F, 10), new PointF(9.6F, 9.6F) }); break;
                    case "select": using (GraphicsPath frame = RoundedF(new RectangleF(3.5F, 3.5F, 17, 17), 3)) g.DrawPath(p, frame); g.DrawLine(p, 7.5F, 8.5F, 16.5F, 8.5F); g.DrawLine(p, 7.5F, 12, 16.5F, 12); g.DrawLine(p, 7.5F, 15.5F, 13, 15.5F); break;
                    case "folder": using (GraphicsPath folder = new GraphicsPath()) { folder.AddLines(new[] { new PointF(3.5F, 18), new PointF(3.5F, 6.5F), new PointF(5, 5), new PointF(9.2F, 5), new PointF(11.2F, 7.5F), new PointF(19, 7.5F), new PointF(20.5F, 9), new PointF(20.5F, 18), new PointF(19, 19.5F), new PointF(5, 19.5F) }); folder.CloseFigure(); g.DrawPath(p, folder); } break;
                    case "settings":
                        using (GraphicsPath gear = new GraphicsPath())
                        {
                            List<PointF> points = new List<PointF>();
                            for (int k = 0; k < 8; k++) { double a = k * 45; points.Add(Polar(9.2, a - 9.5)); points.Add(Polar(9.2, a + 9.5)); points.Add(Polar(7, a + 17)); points.Add(Polar(7, a + 28)); }
                            gear.AddPolygon(points.ToArray()); g.DrawPath(p, gear); g.DrawEllipse(p, 9, 9, 6, 6);
                        }
                        break;
                    case "history": g.DrawArc(p, 3.8F, 3.8F, 16.4F, 16.4F, 215, 300); g.DrawLines(p, new[] { new PointF(3.2F, 4.2F), new PointF(4.6F, 8.7F), new PointF(9F, 7.4F) }); g.DrawLines(p, new[] { new PointF(12, 7.5F), new PointF(12, 12), new PointF(15, 13.8F) }); break;
                    case "clock": g.DrawEllipse(p, 3.5F, 3.5F, 17, 17); g.DrawLines(p, new[] { new PointF(12, 7.5F), new PointF(12, 12), new PointF(15, 13.8F) }); break;
                    case "person": g.DrawEllipse(p, 8.3F, 3.5F, 7.4F, 7.4F); using (GraphicsPath body = new GraphicsPath()) { body.AddBezier(4.5F, 20.5F, 4.5F, 15.6F, 8, 13.8F, 12, 13.8F); body.AddBezier(12, 13.8F, 16, 13.8F, 19.5F, 15.6F, 19.5F, 20.5F); g.DrawPath(p, body); } break;
                    case "people": g.DrawEllipse(p, 5, 4.5F, 6.5F, 6.5F); g.DrawBezier(p, 2.5F, 19.5F, 2.5F, 15.2F, 5, 13.6F, 8.2F, 13.6F); g.DrawBezier(p, 8.2F, 13.6F, 11.4F, 13.6F, 14, 15.2F, 14, 19.5F); g.DrawArc(p, 13.5F, 5, 5.5F, 5.5F, 250, 230); g.DrawBezier(p, 16.5F, 13.7F, 19.2F, 14, 21.5F, 15.6F, 21.5F, 19.5F); break;
                    case "user-plus": g.DrawEllipse(p, 5.3F, 3.5F, 7.4F, 7.4F); g.DrawBezier(p, 1.8F, 20.5F, 1.8F, 15.6F, 5.2F, 13.8F, 9, 13.8F); g.DrawBezier(p, 9, 13.8F, 12.8F, 13.8F, 16.2F, 15.6F, 16.2F, 20.5F); g.DrawLine(p, 19, 6, 19, 12); g.DrawLine(p, 16, 9, 22, 9); break;
                    case "type": g.DrawLines(p, new[] { new PointF(3, 19), new PointF(8.5F, 5), new PointF(14, 19) }); g.DrawLine(p, 5, 14.5F, 12, 14.5F); g.DrawLine(p, 16.5F, 10, 22, 10); g.DrawLine(p, 19.25F, 10, 19.25F, 19); break;
                    case "palette":
                        using (GraphicsPath pal = new GraphicsPath()) { pal.AddArc(3, 3, 18, 18, 75, 285); pal.AddBezier(21, 12, 21, 15.5F, 16.5F, 14.5F, 15.5F, 17); pal.AddBezier(15.5F, 17, 14.7F, 19.5F, 15.2F, 21, 13.4F, 20.9F); g.DrawPath(p, pal); }
                        g.FillEllipse(b, 6.6F, 10.4F, 2.6F, 2.6F); g.FillEllipse(b, 8.8F, 6, 2.6F, 2.6F); g.FillEllipse(b, 13.6F, 6, 2.6F, 2.6F); break;
                    case "inbox":
                    case "download": g.DrawLine(p, 12, 4, 12, 14.5F); g.DrawLines(p, new[] { new PointF(8, 10.5F), new PointF(12, 14.5F), new PointF(16, 10.5F) }); g.DrawLines(p, new[] { new PointF(4, 14.5F), new PointF(4, 18.5F), new PointF(5.5F, 20), new PointF(18.5F, 20), new PointF(20, 18.5F), new PointF(20, 14.5F) }); break;
                    case "pin": g.DrawLines(p, new[] { new PointF(9, 3.5F), new PointF(15, 3.5F), new PointF(14.2F, 9.6F), new PointF(17.5F, 13.6F), new PointF(6.5F, 13.6F), new PointF(9.8F, 9.6F), new PointF(9, 3.5F) }); g.DrawLine(p, 12, 13.6F, 12, 20.5F); break;
                    case "info": g.DrawEllipse(p, 3, 3, 18, 18); g.DrawLine(p, 12, 11, 12, 16.5F); g.FillEllipse(b, 10.9F, 6.6F, 2.2F, 2.2F); break;
                    case "error": g.FillEllipse(b, 2.5F, 2.5F, 19, 19); using (Pen white = new Pen(Color.White, 2.2F) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(white, 12, 7, 12, 13); using (SolidBrush white = new SolidBrush(Color.White)) g.FillEllipse(white, 10.8F, 15.4F, 2.4F, 2.4F); break;
                    case "check": g.DrawLines(p, new[] { new PointF(5, 12.5F), new PointF(10, 17.5F), new PointF(19, 7) }); break;
                    case "chevron-down": g.DrawLines(p, new[] { new PointF(6, 9.5F), new PointF(12, 15.5F), new PointF(18, 9.5F) }); break;
                    case "chevron-right": g.DrawLines(p, new[] { new PointF(9.5F, 6), new PointF(15.5F, 12), new PointF(9.5F, 18) }); break;
                    case "play": using (GraphicsPath tri = new GraphicsPath()) { tri.AddPolygon(new[] { new PointF(8.5F, 5.5F), new PointF(19, 12), new PointF(8.5F, 18.5F) }); g.FillPath(b, tri); using (Pen join = new Pen(color, 1.6F) { LineJoin = LineJoin.Round }) g.DrawPath(join, tri); } break;
                    case "moon": using (GraphicsPath moon = new GraphicsPath()) { moon.AddBezier(20, 14.2F, 18.6F, 18.2F, 15.2F, 20.6F, 11.6F, 20.6F); moon.AddBezier(11.6F, 20.6F, 7, 20.6F, 3.4F, 16.9F, 3.4F, 12.3F); moon.AddBezier(3.4F, 12.3F, 3.4F, 8.5F, 6, 5.2F, 9.7F, 4.2F); moon.AddBezier(9.7F, 4.2F, 8.4F, 10, 13.6F, 16, 20, 14.2F); g.DrawPath(p, moon); } break;
                    case "power": g.DrawArc(p, 4.5F, 4.5F, 15, 15, -55, 290); g.DrawLine(p, 12, 3, 12, 11); break;
                    case "edit": g.DrawLines(p, new[] { new PointF(4, 20), new PointF(4.8F, 15.6F), new PointF(15.4F, 5), new PointF(19, 8.6F), new PointF(8.4F, 19.2F), new PointF(4, 20) }); g.DrawLine(p, 13.2F, 7.2F, 16.8F, 10.8F); break;
                    case "trash": g.DrawLine(p, 4, 6.5F, 20, 6.5F); g.DrawLines(p, new[] { new PointF(9.5F, 6.5F), new PointF(9.5F, 4), new PointF(14.5F, 4), new PointF(14.5F, 6.5F) }); g.DrawLines(p, new[] { new PointF(6, 6.5F), new PointF(7, 19.5F), new PointF(8.4F, 20.8F), new PointF(15.6F, 20.8F), new PointF(17, 19.5F), new PointF(18, 6.5F) }); g.DrawLine(p, 10, 10.5F, 10, 16.5F); g.DrawLine(p, 14, 10.5F, 14, 16.5F); break;
                    case "eraser": g.DrawLines(p, new[] { new PointF(8.6F, 20), new PointF(4.2F, 15.6F), new PointF(14.2F, 5.6F), new PointF(20.2F, 11.6F), new PointF(11.8F, 20), new PointF(20, 20) }); g.DrawLine(p, 9, 10.8F, 15, 16.8F); break;
                    case "forward": using (GraphicsPath arrow = new GraphicsPath()) { arrow.AddBezier(4, 19, 4, 12.5F, 8.5F, 10, 15.5F, 10); g.DrawPath(p, arrow); } g.DrawLines(p, new[] { new PointF(12, 5.5F), new PointF(16.5F, 10), new PointF(12, 14.5F) }); break;
                    case "open": g.DrawLines(p, new[] { new PointF(19, 13.5F), new PointF(19, 18), new PointF(17.5F, 19.5F), new PointF(6.5F, 19.5F), new PointF(5, 18), new PointF(5, 7), new PointF(6.5F, 5.5F), new PointF(10.5F, 5.5F) }); g.DrawLine(p, 11.5F, 12.5F, 19.5F, 4.5F); g.DrawLines(p, new[] { new PointF(14, 4.5F), new PointF(19.5F, 4.5F), new PointF(19.5F, 10) }); break;
                    case "apps": foreach (PointF corner in new[] { new PointF(4, 4), new PointF(13.5F, 4), new PointF(4, 13.5F), new PointF(13.5F, 13.5F) }) using (GraphicsPath tile = RoundedF(new RectangleF(corner.X, corner.Y, 6.5F, 6.5F), 1.6F)) g.DrawPath(p, tile); break;
                    case "link": g.TranslateTransform(12, 12); g.RotateTransform(-45); g.TranslateTransform(-12, -12); using (GraphicsPath a = RoundedF(new RectangleF(2.5F, 8.8F, 10.5F, 6.4F), 3.2F)) g.DrawPath(p, a); using (GraphicsPath c = RoundedF(new RectangleF(11, 8.8F, 10.5F, 6.4F), 3.2F)) g.DrawPath(p, c); break;
                    case "chat": using (GraphicsPath bubble = new GraphicsPath()) { bubble.AddArc(3, 4, 6, 6, 180, 90); bubble.AddArc(15, 4, 6, 6, 270, 90); bubble.AddArc(15, 11.5F, 6, 6, 0, 90); bubble.AddLine(18, 17.5F, 10, 17.5F); bubble.AddLine(10, 17.5F, 6, 21); bubble.AddLine(6, 21, 6, 17.4F); bubble.AddArc(3, 11.5F, 6, 6, 90, 90); bubble.CloseFigure(); g.DrawPath(p, bubble); } break;
                    case "image": using (GraphicsPath frame = RoundedF(new RectangleF(3.5F, 4.5F, 17, 15), 2.5F)) g.DrawPath(p, frame); g.DrawEllipse(p, 7, 8, 3.2F, 3.2F); g.DrawLines(p, new[] { new PointF(4.5F, 17.5F), new PointF(9.5F, 12.8F), new PointF(12.8F, 15.6F), new PointF(15.3F, 13.2F), new PointF(19.6F, 17) }); break;
                    case "bell": using (GraphicsPath bell = new GraphicsPath()) { bell.AddBezier(5.5F, 17, 7, 15, 6.5F, 13, 6.5F, 10.5F); bell.AddBezier(6.5F, 10.5F, 6.5F, 7, 9, 4.5F, 12, 4.5F); bell.AddBezier(12, 4.5F, 15, 4.5F, 17.5F, 7, 17.5F, 10.5F); bell.AddBezier(17.5F, 10.5F, 17.5F, 13, 17, 15, 18.5F, 17); bell.CloseFigure(); g.DrawPath(p, bell); } g.DrawArc(p, 10, 17.5F, 4, 3.5F, 0, 180); break;
                    case "pulse": g.DrawLines(p, new[] { new PointF(2.5F, 12.5F), new PointF(7, 12.5F), new PointF(9.6F, 6), new PointF(14, 18.5F), new PointF(16.4F, 12.5F), new PointF(21.5F, 12.5F) }); break;
                    case "sun": g.DrawEllipse(p, 8, 8, 8, 8); for (int k = 0; k < 8; k++) { PointF a = Polar(6.2, k * 45), c = Polar(8.6, k * 45); g.DrawLine(p, a, c); } break;
                    case "arrow-down": g.DrawLines(p, new[] { new PointF(6.5F, 10), new PointF(12, 15.5F), new PointF(17.5F, 10) }); break;
                    case "arrow-up": g.DrawLine(p, 12, 19, 12, 5.5F); g.DrawLines(p, new[] { new PointF(6.5F, 11), new PointF(12, 5.5F), new PointF(17.5F, 11) }); break;
                    // 以下仅用于聊天壁纸涂鸦。
                    case "heart": using (GraphicsPath heart = new GraphicsPath()) { heart.AddBezier(12, 20, 5, 15.5F, 3, 12, 3, 8.8F); heart.AddBezier(3, 8.8F, 3, 6, 5.2F, 4, 7.8F, 4); heart.AddBezier(7.8F, 4, 9.6F, 4, 11.2F, 5.1F, 12, 6.8F); heart.AddBezier(12, 6.8F, 12.8F, 5.1F, 14.4F, 4, 16.2F, 4); heart.AddBezier(16.2F, 4, 18.8F, 4, 21, 6, 21, 8.8F); heart.AddBezier(21, 8.8F, 21, 12, 19, 15.5F, 12, 20); g.DrawPath(p, heart); } break;
                    case "star": { PointF[] star = new PointF[10]; for (int k = 0; k < 10; k++) star[k] = Polar(k % 2 == 0 ? 9.5 : 4.2, k * 36 - 90); using (GraphicsPath shape = new GraphicsPath()) { shape.AddPolygon(star); g.DrawPath(p, shape); } } break;
                    case "sparkle": using (GraphicsPath spark = new GraphicsPath()) { spark.AddBezier(12, 3, 12.8F, 9, 15, 11.2F, 21, 12); spark.AddBezier(21, 12, 15, 12.8F, 12.8F, 15, 12, 21); spark.AddBezier(12, 21, 11.2F, 15, 9, 12.8F, 3, 12); spark.AddBezier(3, 12, 9, 11.2F, 11.2F, 9, 12, 3); g.DrawPath(p, spark); } break;
                    case "cloud": using (GraphicsPath cloud = new GraphicsPath()) { cloud.AddArc(3, 10.5F, 7, 7, 90, 180); cloud.AddArc(6.5F, 5, 9, 9, 190, 150); cloud.AddArc(13, 8.5F, 8, 8, 260, 190); cloud.CloseFigure(); g.DrawPath(p, cloud); } break;
                    case "music": g.DrawEllipse(p, 4.5F, 15, 5, 4.2F); g.DrawEllipse(p, 14.5F, 13, 5, 4.2F); g.DrawLines(p, new[] { new PointF(9.5F, 17), new PointF(9.5F, 5.5F), new PointF(19.5F, 3.5F), new PointF(19.5F, 15) }); g.DrawLine(p, 9.5F, 9, 19.5F, 7); break;
                    case "coffee": g.DrawLines(p, new[] { new PointF(4.5F, 9), new PointF(5.5F, 17.5F), new PointF(7.5F, 19.5F), new PointF(13, 19.5F), new PointF(15, 17.5F), new PointF(16, 9), new PointF(4.5F, 9) }); g.DrawArc(p, 14.5F, 10.5F, 5.5F, 5, -90, 180); g.DrawBezier(p, 8, 3.5F, 7, 5, 9, 5.5F, 8, 7); g.DrawBezier(p, 12, 3.5F, 11, 5, 13, 5.5F, 12, 7); break;
                    case "monitor": using (GraphicsPath screen = RoundedF(new RectangleF(3, 4, 18, 12.5F), 2)) g.DrawPath(p, screen); g.DrawLine(p, 12, 16.5F, 12, 20); g.DrawLine(p, 8, 20, 16, 20); break;
                }
            }
            g.Restore(old);
        }
        private static PointF Polar(double radius, double degrees) { double a = degrees * Math.PI / 180; return new PointF((float)(12 + Math.Cos(a) * radius), (float)(12 + Math.Sin(a) * radius)); }
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
        public static Bitmap ContactAvatar(int size, string identity, bool online)
        {
            int halo=AvatarHalo(size), extent=size+halo*2;
            Bitmap image=new Bitmap(extent,extent);
            using(Graphics g=Graphics.FromImage(image))
            {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                EmojiAvatars.Draw(g,new Rectangle(halo,halo,size,size),identity);
                // 在线：右下角绿点，外圈取界面底色与头像分隔；离线不加任何标记，头像主体像素保持一致。
                if(online)
                {
                    float dot=Math.Max(9F,size*.3F),ring=Math.Max(1.5F,size*.055F);
                    RectangleF outer=new RectangleF(halo+size-dot*.86F,halo+size-dot*.86F,dot,dot);
                    using(SolidBrush b=new SolidBrush(Surface))g.FillEllipse(b,outer);
                    using(SolidBrush b=new SolidBrush(Online))g.FillEllipse(b,RectangleF.Inflate(outer,-ring,-ring));
                }
            }
            return image;
        }
        // 列表与选择器反复绘制同一头像：按尺寸、身份和在线状态复用位图，调用方不得释放返回值。
        private static readonly Dictionary<string, Bitmap> avatars = new Dictionary<string, Bitmap>();
        internal static Bitmap CachedAvatar(int size, string identity, bool online)
        {
            string key = size + "|" + (online ? "1" : "0") + "|" + identity; Bitmap image;
            if (avatars.TryGetValue(key, out image)) return image;
            if (avatars.Count >= 192) { foreach (Bitmap stale in avatars.Values) stale.Dispose(); avatars.Clear(); }
            image = ContactAvatar(size, identity, online); avatars[key] = image; return image;
        }
        public static Color AvatarColor(string name)
        {
            Color[] colors = { Color.FromArgb(102, 169, 224), Color.FromArgb(126, 155, 219), Color.FromArgb(101, 188, 165), Color.FromArgb(220, 161, 105), Color.FromArgb(163, 140, 201) };
            uint hash = 0; foreach (char c in name ?? "") hash = unchecked(hash * 31 + c);
            return colors[hash % colors.Length];
        }
        public static GraphicsPath Rounded(Rectangle bounds, int radius) { return RoundedF(bounds, radius); }
        public static GraphicsPath RoundedF(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath(); radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
            if (radius <= .5F) { path.AddRectangle(bounds); return path; }
            float d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        // 程序图标与 Android res/drawable/ic_feiq.xml 同一几何（48 单位网格）：经典蓝圆底 + 白色对话气泡，气泡中部镂空一道横线。
        // 颜色固定，不随主题强调色变化；气泡顶点取整到像素，16/20/24 像素的托盘与任务栏小图标边缘仍然清晰。
        public static Bitmap AppLogo(int size)
        {
            Bitmap image = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(image)) DrawLogo(g, new RectangleF(0, 0, size, size));
            return image;
        }
        public static void DrawLogo(Graphics g, RectangleF bounds)
        {
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float size = Math.Min(bounds.Width, bounds.Height); g.TranslateTransform(bounds.X, bounds.Y);
            Func<float, float> at = v => (float)Math.Floor(v * size / 48F + .5F);
            using (SolidBrush blue = new SolidBrush(Color.FromArgb(51, 144, 236))) g.FillEllipse(blue, 2 * size / 48F, 2 * size / 48F, 44 * size / 48F, 44 * size / 48F);
            using (SolidBrush white = new SolidBrush(Color.White))
            using (GraphicsPath bubble = new GraphicsPath(FillMode.Alternate))
            {
                bubble.AddPolygon(new[] { new PointF(at(12), at(13)), new PointF(at(36), at(13)), new PointF(at(36), at(30)), new PointF(at(23), at(30)), new PointF(at(16), at(36)), new PointF(at(16), at(30)), new PointF(at(12), at(30)) });
                bubble.AddRectangle(RectangleF.FromLTRB(at(18), at(19), at(30), Math.Max(at(19) + 1, at(22))));
                g.FillPath(white, bubble);
            }
            g.Restore(state);
        }
        private static Icon MakeIcon()
        {
            using (System.IO.MemoryStream stream = new System.IO.MemoryStream(AppIconBytes()))
            using (Icon icon = new Icon(stream, new Size(32,32))) return (Icon)icon.Clone();
        }
        public static byte[] AppIconBytes()
        {
            // 不序列化 GetHicon 的设备相关位图：该路径可能丢失颜色/透明度。
            // EXE 与运行时图标共用显式 32-bit BGRA、多尺寸 ICO，完全不依赖主题和显示设备。
            // 20/40 对应 125% 缩放下的小/大图标，托盘与窗口不必再从相邻尺寸缩放。
            int[] sizes={16,20,24,32,40,48,64,128,256};
            using(System.IO.MemoryStream stream=new System.IO.MemoryStream())
            using(System.IO.BinaryWriter writer=new System.IO.BinaryWriter(stream))
            {
                writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);
                int offset=6+16*sizes.Length;
                foreach(int size in sizes) {
                    int length=40+size*size*4+((size+31)/32)*4*size;
                    writer.Write((byte)(size==256?0:size));writer.Write((byte)(size==256?0:size));writer.Write((ushort)0);
                    writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(length);writer.Write(offset);offset+=length;
                }
                foreach(int size in sizes) using(Bitmap bitmap=AppLogo(size)) {
                    int stride=((size+31)/32)*4;
                    writer.Write(40);writer.Write(size);writer.Write(size*2);writer.Write((ushort)1);writer.Write((ushort)32);
                    writer.Write(0);writer.Write(size*size*4);writer.Write(0);writer.Write(0);writer.Write(0);writer.Write(0);
                    for(int y=size-1;y>=0;y--)for(int x=0;x<size;x++) {Color c=bitmap.GetPixel(x,y);writer.Write(c.B);writer.Write(c.G);writer.Write(c.R);writer.Write(c.A);}
                    for(int y=size-1;y>=0;y--) {byte[] mask=new byte[stride];for(int x=0;x<size;x++)if(bitmap.GetPixel(x,y).A==0)mask[x/8]|=(byte)(128>>(x%8));writer.Write(mask);}
                }
                return stream.ToArray();
            }
        }
        // Windows 10 1809+ 的原生滚动条跟随深浅主题；旧系统忽略主题名，仍保持可用。
        public static void ThemeScrollbars(Control control)
        {
            control.HandleCreated += delegate { ApplyScrollTheme(control); };
            if (control.IsHandleCreated) ApplyScrollTheme(control);
        }
        private static void ApplyScrollTheme(Control control)
        {
            try { SetWindowTheme(control.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        internal static bool Windows11 { get { return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000; } }
        internal static void RoundWindow(IntPtr handle, int preference)
        {
            try { DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int)); }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string application, string list);
    }
    public sealed class RoundedButton : Button
    {
        public string Glyph = "";
        public int Radius = Theme.ControlRadius;
        // 标签样式：无底色，激活时强调色文字与指示条由容器绘制；Badge 为未读计数。
        public bool Tab, Active;
        public int Badge;
        // 窗口关闭键等特殊悬停色；为空时按底色自动加深。
        public Color HoverBack = Color.Empty, HoverFore = Color.Empty;
        private readonly Tween hover;
        private Point ripple;
        private double rippleStart = -100000, rippleEnd = -100000;
        private bool pressed;
        private readonly Func<bool> rippleStep;
        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            hover = new Tween(this, 150, 0);
            rippleStep = delegate { if (IsDisposed) return false; Invalidate(); return pressed || Motion.Now - rippleEnd < 420; };
        }
        protected override void OnMouseEnter(EventArgs e) { hover.Set(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover.Set(0); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled && Motion.Enabled) { ripple = e.Location; rippleStart = Motion.Now; rippleEnd = Double.MaxValue; pressed = true; Motion.Run(rippleStep); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { if (pressed) { pressed = false; rippleEnd = Math.Max(Motion.Now, rippleStart + 180); } base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        private Color ParentBack() { Control parent = Parent; while (parent != null && parent.BackColor.A < 255) parent = parent.Parent; return parent == null ? Theme.Surface : parent.BackColor; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; Color back = ParentBack(); g.Clear(back); g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96F; double h = hover.Value;
            bool primary = !Tab && BackColor.ToArgb() == Theme.Blue.ToArgb();
            Color fill = Tab ? back : BackColor;
            Color hovered = !HoverBack.IsEmpty ? HoverBack : primary ? Theme.Blend(Color.Black, fill, .12F) : Theme.Blend(Theme.Ink, fill, Theme.Dark ? .10F : .06F);
            // 禁用态与容器底色混合而不是统一刷成灰块，按钮轮廓仍可辨认。
            if (Enabled) fill = Motion.Mix(fill, hovered, Tab ? 0 : h); else if (!Tab) fill = Theme.Blend(fill, back, primary ? .45F : .6F);
            RectangleF area = new RectangleF(.5F, .5F, Width - 1, Height - 1);
            float radius = Math.Min(Radius * scale, Math.Min(area.Width, area.Height) / 2);
            using (GraphicsPath path = Theme.RoundedF(area, radius))
            {
                if (fill.ToArgb() != back.ToArgb()) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                double since = Motion.Now - rippleStart;
                if (since >= 0 && since < 100000 && (pressed || Motion.Now - rippleEnd < 420))
                {
                    double grow = Motion.EaseOut(Math.Min(1, since / 380)), fade = pressed ? 1 : Math.Max(0, 1 - (Motion.Now - rippleEnd) / 420);
                    float reach = (float)(Math.Sqrt(Width * Width + Height * Height) * (.25 + .85 * grow));
                    Region clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                    using (SolidBrush wave = new SolidBrush(Color.FromArgb((int)(fade * (primary || !HoverBack.IsEmpty ? 46 : Theme.Dark ? 34 : 22)), primary || !HoverBack.IsEmpty ? Color.White : Theme.Ink)))
                        g.FillEllipse(wave, ripple.X - reach, ripple.Y - reach, reach * 2, reach * 2);
                    g.Clip = clip;
                }
                if (Focused && ShowFocusCues) using (Pen ring = new Pen(Theme.Blend(Theme.Blue, back, .7F), 2 * scale)) using (GraphicsPath inner = Theme.RoundedF(RectangleF.Inflate(area, -1.5F * scale, -1.5F * scale), Math.Max(1, radius - 1.5F * scale))) g.DrawPath(ring, inner);
            }
            Color color = Enabled ? ForeColor : Theme.Blend(ForeColor, fill, .45F);
            if (Tab) color = Active ? Theme.Blue : Motion.Mix(Theme.Muted, Theme.Ink, h);
            else if (!HoverFore.IsEmpty && Enabled) color = Motion.Mix(ForeColor, HoverFore, h);
            if (Glyph.Length > 0)
            {
                float icon = Math.Min(22 * scale, Math.Min(Width, Height) - 10 * scale);
                Theme.Glyph(g, Glyph, new RectangleF((Width - icon) / 2F, (Height - icon) / 2F, icon, icon), color);
                return;
            }
            Rectangle text = new Rectangle(1, 1, Math.Max(2, Width - 2), Math.Max(2, Height - 2));
            if (Tab && Badge > 0)
            {
                string count = Badge > 99 ? "99+" : Badge.ToString();
                Size label = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding), badge = TextRenderer.MeasureText(g, count, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
                int gap = (int)(5 * scale), pill = Math.Max((int)(18 * scale), badge.Width + (int)(10 * scale)), total = label.Width + gap + pill, left = (Width - total) / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, label.Width + 2, Height), color, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                RectangleF bubble = new RectangleF(left + label.Width + gap, (Height - 18 * scale) / 2F, pill, 18 * scale);
                using (GraphicsPath shape = Theme.RoundedF(bubble, bubble.Height / 2)) using (SolidBrush b = new SolidBrush(Active ? Theme.Blue : Theme.Blend(Theme.Muted, back, .75F))) g.FillPath(b, shape);
                TextRenderer.DrawText(g, count, Theme.Small, Rectangle.Round(bubble), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            TextRenderer.DrawText(g, Text, Font, text, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
    public sealed class RoundedPanel : Panel
    {
        public int Radius = 14;
        public Color Fill = Theme.Ice;
        public Color Border = Color.Empty;
        public RoundedPanel() { DoubleBuffered = true; BackColor = Theme.Surface; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); if (Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.RoundedF(new RectangleF(.5F, .5F, Width - 1, Height - 1), Math.Min(Radius * e.Graphics.DpiX / 96F, Math.Min(Width, Height) / 2F)))
            {
                using (SolidBrush b = new SolidBrush(Fill)) e.Graphics.FillPath(b, path);
                if (!Border.IsEmpty) using (Pen pen = new Pen(Border)) e.Graphics.DrawPath(pen, path);
            }
        }
    }
    public sealed class RoundedInput : RoundedInputBase { }
    public class RoundedInputBase : Panel
    {
        public readonly TextBox Input = new TextBox();
        private readonly Tween focus;
        public RoundedInputBase()
        {
            DoubleBuffered = true; ResizeRedraw = true; BackColor = Theme.Surface; Padding = new Padding(15, 11, 15, 9); Height = 44;
            Input.BorderStyle = BorderStyle.None; Input.BackColor = Theme.Ice; Input.ForeColor = Theme.Ink; Input.Dock = DockStyle.Fill; Input.Font = Theme.Body; Controls.Add(Input); Theme.AttachEditMenu(Input);
            focus = new Tween(this, 160, 0);
            Input.Enter += delegate { focus.Set(1); }; Input.Leave += delegate { focus.Set(0); };
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); if (Width < 4 || Height < 4) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            using (GraphicsPath path = Theme.RoundedF(new RectangleF(.5F, .5F, Width - 1, Height - 1), Math.Min(Theme.ControlRadius * scale, Math.Min(Width, Height) / 2F)))
            {
                using (SolidBrush b = new SolidBrush(Input.BackColor)) e.Graphics.FillPath(b, path);
                double f = focus.Value; if (f > 0) using (Pen pen = new Pen(Color.FromArgb((int)(255 * f), Theme.Blue), 1.5F * scale)) e.Graphics.DrawPath(pen, path);
            }
        }
        public string Placeholder { set { if (!Input.IsHandleCreated) Input.CreateControl(); SendMessage(Input.Handle, 0x1501, new IntPtr(1), value); } }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int msg, IntPtr w, string text);
    }
    public sealed class ToggleSwitch : CheckBox
    {
        // 说明文字随开关一起绘制，长说明自动换行；开关滑块随状态平滑过渡。
        public string Description = "";
        private readonly Tween knob, hover;
        public ToggleSwitch() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); AutoSize = false; Height = 42; Cursor = Cursors.Hand; knob = new Tween(this, 170, 0); hover = new Tween(this, 140, 0); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); knob.Set(Checked ? 1 : 0, false); }
        protected override void OnCheckedChanged(EventArgs e) { knob.Set(Checked ? 1 : 0); base.OnCheckedChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { hover.Set(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover.Set(0); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        public int PreferredHeightFor(int width)
        {
            float scale = DeviceDpi / 96F; int textWidth = Math.Max(40, width - (int)(60 * scale));
            int title = TextRenderer.MeasureText(Text, Font, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            int detail = String.IsNullOrEmpty(Description) ? 0 : TextRenderer.MeasureText(Description, Theme.Small, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + (int)(3 * scale);
            return Math.Max((int)(40 * scale), title + detail + (int)(18 * scale));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; float scale = g.DpiX / 96F;
            int w = (int)(38 * scale), h = (int)(22 * scale); double k = knob.Value;
            int textWidth = Math.Max(10, Width - w - (int)(22 * scale));
            Size title = TextRenderer.MeasureText(g, Text, Font, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size detail = String.IsNullOrEmpty(Description) ? Size.Empty : TextRenderer.MeasureText(g, Description, Theme.Small, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int block = title.Height + (detail.Height > 0 ? detail.Height + (int)(3 * scale) : 0), top = Math.Max(0, (Height - block) / 2);
            Color ink = Enabled ? Theme.Ink : Theme.Blend(Theme.Ink, BackColor, .5F);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, top, textWidth, title.Height), ink, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            if (detail.Height > 0) TextRenderer.DrawText(g, Description, Theme.Small, new Rectangle(0, top + title.Height + (int)(3 * scale), textWidth, detail.Height), Enabled ? Theme.Muted : Theme.Blend(Theme.Muted, BackColor, .5F), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            RectangleF track = new RectangleF(Width - w - 2 * scale, top + Math.Min(title.Height, block) / 2F - h / 2F, w, h);
            if (detail.Height == 0) track.Y = (Height - h) / 2F;
            Color off = Theme.Blend(Theme.Muted, BackColor, Theme.Dark ? .45F : .38F), on = Theme.Blue;
            Color fill = Motion.Mix(off, on, k); if (!Enabled) fill = Theme.Blend(fill, BackColor, .5F); else if (hover.Value > 0) fill = Motion.Mix(fill, Theme.Blend(Theme.Ink, fill, .12F), hover.Value * .6);
            using (GraphicsPath path = Theme.RoundedF(track, h / 2F)) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
            float dot = h - 6 * scale, x = track.Left + 3 * scale + (float)((track.Width - dot - 6 * scale) * k);
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillEllipse(shadow, x, track.Top + 3.6F * scale, dot, dot);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, x, track.Top + 3 * scale, dot, dot);
            if (Focused && ShowFocusCues) using (Pen ring = new Pen(Theme.Blend(Theme.Blue, BackColor, .6F), 2 * scale)) using (GraphicsPath outline = Theme.RoundedF(RectangleF.Inflate(track, 2.5F * scale, 2.5F * scale), h / 2F + 2.5F * scale)) g.DrawPath(ring, outline);
        }
    }
    // 自绘滑杆：保留 TrackBar 的取值、键盘与无障碍语义，点击轨道直接跳到对应值并可拖动。
    public sealed class ThemedSlider : TrackBar
    {
        private bool dragging;
        public ThemedSlider() { SetStyle(ControlStyles.UserPaint | ControlStyles.UserMouse | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); TickStyle = TickStyle.None; AutoSize = false; Height = 36; Cursor = Cursors.Hand; }
        protected override void OnValueChanged(EventArgs e) { base.OnValueChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        private float Pad { get { return 12 * DeviceDpi / 96F; } }
        private int ValueAt(int x) { float span = Math.Max(1, Width - Pad * 2); double ratio = Math.Max(0, Math.Min(1, (x - Pad) / span)); return Minimum + (int)Math.Round(ratio * (Maximum - Minimum)); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); dragging = true; Capture = true; Value = ValueAt(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Value = ValueAt(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias; float scale = g.DpiX / 96F;
            float left = Pad, right = Width - Pad, y = Height / 2F, ratio = Maximum == Minimum ? 0 : (float)(Value - Minimum) / (Maximum - Minimum), x = left + (right - left) * ratio;
            using (Pen track = new Pen(Theme.Line, 4 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(track, left, y, right, y);
            using (Pen done = new Pen(Theme.Blue, 4 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) if (x > left) g.DrawLine(done, left, y, x, y);
            using (SolidBrush tick = new SolidBrush(Theme.Blend(Theme.Muted, BackColor, .45F)))
                for (int v = Minimum; v <= Maximum && Maximum - Minimum <= 24; v++) { float tx = left + (right - left) * (v - Minimum) / Math.Max(1, Maximum - Minimum); if (tx > x + 6 * scale) g.FillEllipse(tick, tx - 1.5F * scale, y - 1.5F * scale, 3 * scale, 3 * scale); }
            float knob = 18 * scale;
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(45, 0, 0, 0))) g.FillEllipse(shadow, x - knob / 2, y - knob / 2 + scale, knob, knob);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, x - knob / 2, y - knob / 2, knob, knob);
            using (Pen ring = new Pen(Theme.Blue, 3 * scale)) g.DrawEllipse(ring, x - knob / 2 + 1.5F * scale, y - knob / 2 + 1.5F * scale, knob - 3 * scale, knob - 3 * scale);
            if (Focused && ShowFocusCues) using (Pen focus = new Pen(Theme.Blend(Theme.Blue, BackColor, .35F), 4 * scale)) g.DrawEllipse(focus, x - knob / 2 - 3 * scale, y - knob / 2 - 3 * scale, knob + 6 * scale, knob + 6 * scale);
        }
    }
    public sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
    {
        public ModernMenuRenderer() { RoundedEdges = false; }
        private static Color ItemColor(ToolStripItem item) { return !item.Enabled?Theme.Blend(Theme.Muted,Theme.Surface,.6F):Theme.DestructiveMenu(item.Text)?Theme.Danger:Theme.Ink; }
        private static float Scale(Graphics g) { return g.DpiX/96F; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            ToolStripMenuItem menuItem=e.Item as ToolStripMenuItem; float scale=Scale(e.Graphics);
            bool shortcut=menuItem!=null&&!String.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString)&&e.Text==menuItem.ShortcutKeyDisplayString&&e.Text!=e.Item.Text;
            e.TextColor=shortcut?Theme.Blend(Theme.Muted,Theme.Surface,.85F):ItemColor(e.Item);
            // 原生文字区是各项共用、按最长文字量出的窄框；正文移到固定图标栏后时不能再从它扣宽度，
            // 改为按本项实际宽度排布：正文左对齐到右侧留白前，快捷键右对齐到同一位置，不再截掉末字。
            int left=shortcut?e.TextRectangle.Left:Math.Max(e.TextRectangle.Left,Theme.MenuTextLeft(e.ToolStrip,scale)),right=e.Item.Width-Theme.MenuTrailing(e.Item,scale);
            e.TextRectangle=new Rectangle(left,0,Math.Max(1,right-left),e.Item.Height);
            e.TextFormat|=TextFormatFlags.VerticalCenter;if(!shortcut)e.TextFormat|=TextFormatFlags.EndEllipsis;base.OnRenderItemText(e);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { if(e.Item.Tag as string=="switch")return; float scale=Scale(e.Graphics);int size=(int)(18*scale); Theme.Glyph(e.Graphics,"check",new Rectangle((int)(14*scale),(e.Item.Height-size)/2,size,size),Theme.Blue); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
            float scale=Scale(e.Graphics);int size=(int)(14*scale);Rectangle area=e.ArrowRectangle;
            // 子菜单箭头贴本项右缘，与正文为它让出的位置一致；原生箭头区跟随共享文字区，会落在菜单中间。
            if(e.Item is ToolStripMenuItem&&e.Item.IsOnDropDown)area=new Rectangle(e.Item.Width-(int)(16*scale)-size,0,size,e.Item.Height);
            Theme.Glyph(e.Graphics,"chevron-right",new Rectangle(area.Left+(area.Width-size)/2,area.Top+(area.Height-size)/2,size,size),Theme.Blend(Theme.Muted,Theme.Surface,.9F));
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { float scale=Scale(e.Graphics); using(Pen p=new Pen(Theme.Line)) e.Graphics.DrawLine(p,(int)(12*scale),e.Item.Height/2,e.Item.Width-(int)(12*scale),e.Item.Height/2); }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {e.Graphics.Clear(Theme.Surface);}
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // Windows 11 由 DWM 绘制圆角与描边；旧系统窗口为直角，只画贴边细线，不画会露出方角的圆角框。
            if(Theme.Windows11)return;
            using(Pen pen=new Pen(Theme.Dark?Theme.Blend(Color.White,Theme.Surface,.12F):Theme.Blend(Color.Black,Theme.Surface,.12F))) e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            float scale=Scale(e.Graphics); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            if(e.Item.Selected && e.Item.Enabled) {
                RectangleF r = new RectangleF(1,1,e.Item.Width-2,e.Item.Height-2);
                using(GraphicsPath path=Theme.RoundedF(r,7*scale)) using(SolidBrush b=new SolidBrush(Theme.DestructiveMenu(e.Item.Text)?Theme.Blend(Theme.Danger,Theme.Surface,.10F):Theme.Hover)) e.Graphics.FillPath(b,path);
            }
            ToolStripMenuItem item=e.Item as ToolStripMenuItem;
            if(item!=null && (!item.Checked||item.Tag as string=="switch") && item.Image==null) { int size=(int)(19*scale); Theme.Glyph(e.Graphics,Theme.MenuGlyph(item.Text),new Rectangle((int)(14*scale),(item.Height-size)/2,size,size),item.Enabled&&!Theme.DestructiveMenu(item.Text)?Theme.Blend(Theme.Muted,Theme.Ink,.35F):ItemColor(item)); }
            // 带开关的菜单项（如夜间模式）在右侧绘制状态，避免用勾选冒充开关。
            if(item!=null && item.Tag as string=="switch") {
                RectangleF track=new RectangleF(item.Width-(16+30)*scale,(item.Height-17*scale)/2,30*scale,17*scale);
                using(GraphicsPath path=Theme.RoundedF(track,track.Height/2))using(SolidBrush b=new SolidBrush(item.Checked?Theme.Blue:Theme.Blend(Theme.Muted,Theme.Surface,.4F)))e.Graphics.FillPath(b,path);
                float dot=track.Height-5*scale;using(SolidBrush b=new SolidBrush(Color.White))e.Graphics.FillEllipse(b,item.Checked?track.Right-dot-2.5F*scale:track.Left+2.5F*scale,track.Top+2.5F*scale,dot,dot);
            }
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
        private bool secondaryWindow;
        protected void UseSecondaryChrome()
        {
            secondaryWindow=true;Padding=new Padding(3);chrome.Height=56;caption.Font=Theme.Section;caption.ForeColor=Theme.Ink;
            Control actions=chrome.GetControlFromPosition(1,0);if(actions!=null)actions.Padding=new Padding(0,10,0,0);
            chrome.Paint+=delegate(object sender,PaintEventArgs e){using(Pen line=new Pen(Theme.Line))e.Graphics.DrawLine(line,0,chrome.Height-1,chrome.Width,chrome.Height-1);};
            Invalidate();
        }
        private bool resizeWindow = true;
        public bool ResizeWindow
        {
            get { return resizeWindow; }
            set { resizeWindow = value; if (minimize != null) { minimize.Visible = maximize.Visible = value; chrome.ColumnStyles[1].Width = value ? 138 : 46; chrome.Height = value ? 36 : 54; caption.Font = value ? Theme.Small : Theme.Title; caption.ForeColor = value ? Theme.Muted : Theme.Ink; caption.Padding = new Padding(value ? 15 : 22, value ? 0 : 6, 0, 0); } if(IsHandleCreated)UpdateStyles(); }
        }
        protected override CreateParams CreateParams
        {
            get {
                CreateParams value=base.CreateParams;
                // 无边框只负责外观；保留 Shell 所需的系统菜单与最小化能力，任务栏再次点击才能收起。
                if(ResizeWindow)value.Style|=0x00080000|0x00020000|0x00010000;
                return value;
            }
        }
        public ModernWindow()
        {
            Theme.Apply(this); DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); FormBorderStyle = FormBorderStyle.None; Padding = new Padding(1); StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(400, 300);
            Controls.Add(Surface); Controls.Add(chrome); chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138)); chrome.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); caption.Dock = DockStyle.Fill; caption.Padding = new Padding(15, 0, 0, 0); caption.Margin = Padding.Empty; chrome.Controls.Add(caption, 0, 0);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 138, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
            minimize = Theme.IconButton("min", "最小化", delegate { WindowState = FormWindowState.Minimized; }, false);
            maximize = Theme.IconButton("max", "最大化", delegate { if (ResizeWindow) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; }, false);
            RoundedButton close = (RoundedButton)Theme.IconButton("close", "关闭窗口", delegate { Close(); }, false);
            // 标题栏按钮沿用系统惯例：直角、关闭键悬停为红底白字。
            close.HoverBack = Color.FromArgb(232, 17, 35); close.HoverFore = Color.White;
            foreach (Button button in new[] { minimize, maximize, close }) { button.Margin = Padding.Empty; button.Size = new Size(46, 32); ((RoundedButton)button).Radius = 0; actions.Controls.Add(button); }
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
            Theme.RoundWindow(Handle, 2);
        }
        private void Drag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m); if (m.Msg != 0x84 || !ResizeWindow || WindowState != FormWindowState.Normal) return;
            long v = m.LParam.ToInt64(); Point p = PointToClient(new Point(unchecked((short)(v & 65535)), unchecked((short)((v >> 16) & 65535))));
            int edge = 6; bool l = p.X < edge, r = p.X >= Width - edge, t = p.Y < edge, b = p.Y >= Height - edge;
            if (l || r || t || b) m.Result = new IntPtr(t ? (l ? 13 : r ? 14 : 12) : b ? (l ? 16 : r ? 17 : 15) : l ? 10 : 11);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            using(Pen p=new Pen(secondaryWindow?Theme.Blend(Theme.Blue,Theme.Line,.55F):Theme.Blend(Theme.Ink,Theme.Surface,Theme.Dark?.22F:.16F),secondaryWindow?2:1))e.Graphics.DrawRectangle(p,secondaryWindow?1:0,secondaryWindow?1:0,Width-(secondaryWindow?3:1),Height-(secondaryWindow?3:1));
            if(secondaryWindow)using(SolidBrush accent=new SolidBrush(Theme.Blue))e.Graphics.FillRectangle(accent,0,0,Width,3);
        }
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
