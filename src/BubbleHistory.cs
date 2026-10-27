using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    // 仅绘制可见消息，避免为几百条记录各创建一组原生窗口句柄。
    // 同向、五分钟内的连续消息合为一组：组内圆角收窄，只有最后一条带尾角；跨天插入日期胶囊。
    public sealed class BubbleHistory : SmoothScrollView
    {
        private sealed class Measure { internal string Key, Text; internal MessageLayout Layout; internal Size TextSize; internal int TextMax, ContentWidth, Width, Height; internal bool InlineStamp; }
        private sealed class Item { internal string DateLabel; internal Rectangle DateBounds; internal bool First, Last; internal Measure M; }
        private readonly List<ChatRecord> records = new List<ChatRecord>();
        private readonly List<Rectangle> bounds = new List<Rectangle>();
        private readonly List<MessageLayout> textLayouts=new List<MessageLayout>();
        private readonly List<Item> items=new List<Item>();
        private Dictionary<ChatRecord,Measure> measures=new Dictionary<ChatRecord,Measure>();
        private readonly List<Tuple<Rectangle,string>> linkHits=new List<Tuple<Rectangle,string>>();
        private readonly List<Tuple<Rectangle,ChatRecord,int>> fileHits=new List<Tuple<Rectangle,ChatRecord,int>>();
        private readonly List<Tuple<Rectangle,ChatRecord>> stampHits=new List<Tuple<Rectangle,ChatRecord>>();
        private readonly List<Tuple<Rectangle,int>> retryHits=new List<Tuple<Rectangle,int>>();
        private string dragFilePath;
        private Point dragStart;
        private bool fileDragStarted;
        internal bool IsDraggingFile { get; private set; }
        private Action pressedAction;
        private string highlightText = "";
        internal int RecordLimit = 200;
        internal int CharacterLimit = 200000;
        public string HighlightText { get { return highlightText; } set { highlightText = value ?? ""; Arrange(); Invalidate(); } }
        private int selected = -1;
        private string wallpaperPattern="none";
        private ChatWallpaper.Asset wallpaperImage;
        // 新消息入场：整列内容从旧位置平滑上移；跳到最新按钮的显隐；离开底部后新到消息计数。
        private double appearFrom, appearStart;
        private readonly Func<bool> appearStep;
        private readonly Tween jump;
        private int unseen;
        private double scrolledAt = -100000;
        private readonly ToolTip tips = new ToolTip { InitialDelay = 450, ReshowDelay = 120, AutoPopDelay = 8000 };
        private string tipText;
        public void SetWallpaper(string pattern,string path) {
            ChatWallpaper.Asset next=null;if(pattern=="image")try{next=ChatWallpaper.Acquire(path);}catch(Exception){pattern="orbits";}
            ChatWallpaper.Release(wallpaperImage);wallpaperImage=next;wallpaperPattern=pattern;Invalidate();
        }
        private readonly Dictionary<string,Bitmap> previews = new Dictionary<string,Bitmap>();
        private readonly HashSet<string> pending = new HashSet<string>();
        private readonly List<Tuple<Rectangle,string>> mediaHits = new List<Tuple<Rectangle,string>>();
        private void LoadPreview(string path)
        {
            if(previews.ContainsKey(path)||pending.Contains(path)||pending.Count>=8||previews.Count>=64) return;
            pending.Add(path);
            System.Threading.Tasks.Task.Run(()=>MediaPreview.Thumbnail(path)).ContinueWith(task=>{
                Bitmap bitmap=task.Status==System.Threading.Tasks.TaskStatus.RanToCompletion?task.Result:null;
                if(IsDisposed||!IsHandleCreated) {if(bitmap!=null)bitmap.Dispose();return;}
                try {BeginInvoke((MethodInvoker)delegate{pending.Remove(path);if(IsDisposed){if(bitmap!=null)bitmap.Dispose();return;}previews[path]=bitmap;Invalidate();});}
                catch(InvalidOperationException){if(bitmap!=null)bitmap.Dispose();}
            });
        }
        private int previousHeight;
        public event Action<ChatRecord> RetryRequested;
        public event Action<ChatRecord> ForwardRequested;
        public event Action<ChatRecord,int> FileRequested;
        public int TextLength { get { return Text.Length; } }
        public BubbleHistory()
        {
            TabStop = true; BackColor = Theme.Wallpaper;
            Font=Theme.Body; AccessibleRole = AccessibleRole.List; AccessibleName = "聊天记录";
            jump = new Tween(this, 180, 0);
            appearStep = delegate { if (IsDisposed) return false; Invalidate(); return Motion.Now - appearStart < 280; };
            ContextMenuStrip menu = Theme.Menu();
            ToolStripItem copy = menu.Items.Add("复制消息", null, delegate { CopySelected(); });
            ((ToolStripMenuItem)copy).ShortcutKeyDisplayString = "Ctrl+C";
            ToolStripItem full = menu.Items.Add("查看全文 / 选段复制", null, delegate { ShowFullText(); });
            ToolStripItem forward = menu.Items.Add("转发…", null, delegate { if (SelectedRecord != null && ForwardRequested != null) ForwardRequested(SelectedRecord); });
            ToolStripSeparator fileGroup = new ToolStripSeparator(), retryGroup = new ToolStripSeparator();
            menu.Items.Add(fileGroup);
            ToolStripMenuItem open=new ToolStripMenuItem("打开文件"),openWith=new ToolStripMenuItem("打开方式…");
            menu.Items.Add(open);menu.Items.Add(openWith);
            open.Click+=delegate {if(open.Tag is string)MediaPreview.OpenFile(this,(string)open.Tag,false);};
            openWith.Click+=delegate {if(openWith.Tag is string)MediaPreview.OpenFile(this,(string)openWith.Tag,true);};
            ToolStripMenuItem folder = new ToolStripMenuItem("打开文件所在目录");
            folder.Click += delegate { if (folder.Tag is string) RevealFile(this, (string)folder.Tag); };
            menu.Items.Add(folder);
            ToolStripItem path = menu.Items.Add("复制文件路径", null, delegate { ChatRecord record = SelectedRecord; if (record != null) CopyText(String.Join(Environment.NewLine, record.GetLocalFiles())); });
            menu.Items.Add(retryGroup);
            ToolStripItem retry=menu.Items.Add("重新发送（可能重复）",null,delegate {if(SelectedRecord!=null&&RetryRequested!=null)RetryRequested(SelectedRecord);});
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                ChatRecord record = SelectedRecord;
                // 未选中气泡时不弹出全是禁用项的空菜单。
                if (record == null) { e.Cancel = true; return; }
                forward.Enabled = ForwardRequested != null;
                full.Enabled = !String.IsNullOrEmpty(record.DisplayText());
                retry.Visible=retryGroup.Visible=RetryRequested!=null&&record.Outgoing&&(record.State??"").Contains("未确认"); copy.Enabled = !String.IsNullOrEmpty(record.Text);
                string[] paths = record.GetLocalFiles();
                fileGroup.Visible = folder.Visible = path.Visible = open.Visible = openWith.Visible = paths.Length > 0; folder.Tag = null;
                foreach(ToolStripMenuItem action in new[]{open,openWith}){foreach(ToolStripItem child in action.DropDownItems.Cast<ToolStripItem>().ToArray())child.Dispose();action.DropDownItems.Clear();action.Tag=paths.Length==1?paths[0]:null;if(paths.Length>1)foreach(string local in paths){string file=local;bool choose=action==openWith;action.DropDownItems.Add(Protocol.Prefix(Path.GetFileName(file),48).Replace("&","&&"),null,delegate{MediaPreview.OpenFile(this,file,choose);});}}
                foreach (ToolStripItem item in folder.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
                folder.DropDownItems.Clear();
                if (paths.Length == 1) folder.Tag = paths[0];
                else foreach (string local in paths)
                {
                    string file = local, name = Path.GetFileName(file), caption = Protocol.Prefix(name, 48);
                    if(caption.Length<name.Length) caption += "…";
                    ToolStripItem item = folder.DropDownItems.Add(caption.Replace("&", "&&"), null, delegate { RevealFile(this, file); }); item.ToolTipText = file;
                }
            };
            ContextMenuStrip = menu;
        }
        private ChatRecord SelectedRecord { get { return selected >= 0 && selected < records.Count ? records[selected] : null; } }
        public static void RevealFile(Control owner, string path)
        {
            try
            {
                if (!Path.IsPathRooted(path) || !File.Exists(path)) throw new IOException("文件已移动、删除或尚未接收完成。\n" + path);
                // 仅打开资源管理器并选中文件，不启动附件，也不经过 cmd 或 PowerShell。
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Path.GetFullPath(path) + "\"") { UseShellExecute = true });
            }
            catch (Exception e) { using (NoticeDialog dialog = new NoticeDialog("无法定位文件", e.Message)) dialog.ShowDialog(owner.FindForm()); }
        }
        public void Clear() { records.Clear(); bounds.Clear(); textLayouts.Clear(); items.Clear(); measures.Clear(); linkHits.Clear(); mediaHits.Clear(); fileHits.Clear(); stampHits.Clear(); retryHits.Clear(); pressedAction=null; dragFilePath=null; Text = ""; AutoScrollMinSize = Size.Empty; selected = -1; unseen = 0; appearFrom = 0; Invalidate(); }
        public bool HasDelivery(string state) { return records.Any(r => r.State == state); }
        // 自己发送后回到最新：近处平滑滚动，远处先贴近再滑行；逻辑位置立即落到底部。
        public void ScrollToLatest() { unseen = 0; SmoothTo(AutoScrollMinSize.Height); }
        // 批量载入：只排版一次，打开会话或搜索几百条记录不再逐条重排。
        public void SetRecords(IEnumerable<ChatRecord> values) { SetRecords(values, highlightText); }
        public void SetRecords(IEnumerable<ChatRecord> values, string highlight)
        {
            records.Clear(); records.AddRange(values); highlightText = highlight ?? "";
            Trim(); selected = -1; unseen = 0; appearFrom = 0; pressedAction = null; dragFilePath = null;
            UpdateText(); Arrange(); JumpTo(AutoScrollMinSize.Height); Invalidate();
        }
        private void Trim()
        {
            int characters = records.Sum(r => (r.Text ?? "").Length);
            while (records.Count > RecordLimit || (records.Count > 1 && characters > CharacterLimit)) { characters -= (records[0].Text ?? "").Length; records.RemoveAt(0); selected--; }
        }
        private void UpdateText() { Text = String.Join("\n", records.Select(r => r.Sender + "  " + r.Text)); }
        private bool AtBottom(int tolerance) { return -AutoScrollPosition.Y + ClientSize.Height >= AutoScrollMinSize.Height - tolerance; }
        // 浏览旧消息时以视口顶部的记录为锚：上方重排、裁剪旧记录都不会让正在阅读的内容跳动。
        private Tuple<ChatRecord,int> TopAnchor()
        {
            int top = -AutoScrollPosition.Y;
            for (int i = 0; i < bounds.Count; i++) if (bounds[i].Bottom > top) return Tuple.Create(records[i], bounds[i].Y - top);
            return null;
        }
        private void Restore(Tuple<ChatRecord,int> anchor)
        {
            if (anchor == null) return;
            int index = records.IndexOf(anchor.Item1); if (index < 0 || index >= bounds.Count) return;
            OffsetScroll(bounds[index].Y - anchor.Item2 - (-AutoScrollPosition.Y));
        }
        private int ScreenY(int index) { return index >= 0 && index < bounds.Count ? bounds[index].Y - ScrollOffset + Shift : 0; }
        public override bool Busy { get { return base.Busy || appearFrom != 0; } }
        private int Shift { get { if (appearFrom == 0) return 0; double t = Motion.Progress(appearStart, 280); if (t >= 1) { appearFrom = 0; return 0; } return (int)Math.Round(appearFrom * (1 - Motion.EaseOut(t))); } }
        public void Append(ChatRecord record)
        {
            bool bottom = records.Count == 0 || AtBottom(Dp(70));
            Tuple<ChatRecord,int> anchor = bottom ? null : TopAnchor();
            int before = records.Count > 0 ? ScreenY(records.Count - 1) : 0; ChatRecord last = records.Count > 0 ? records[records.Count - 1] : null;
            records.Add(record); Trim(); UpdateText(); Arrange();
            if (bottom)
            {
                JumpTo(AutoScrollMinSize.Height);
                int index = last == null ? -1 : records.IndexOf(last);
                if (index >= 0 && Motion.Enabled && IsHandleCreated && Visible)
                {
                    int moved = before - (bounds[index].Y - ScrollOffset);
                    if (moved > 0 && moved < ClientSize.Height * 2) { appearFrom = moved; appearStart = Motion.Now; Motion.Run(appearStep); }
                }
            }
            else { Restore(anchor); if (!record.Outgoing) unseen++; UpdateJump(); }
            Invalidate();
        }
        public void Delivery(ChatRecord record)
        {
            ChatRecord current = records.FirstOrDefault(r => r.Packet == record.Packet && r.Outgoing == record.Outgoing);
            if (current == null) return;
            current.State = record.State; current.LocalFiles = record.LocalFiles; current.Transfers = record.Transfers;
            bool bottom = AtBottom(Dp(55)); Tuple<ChatRecord,int> anchor = bottom ? null : TopAnchor();
            Arrange(); if (bottom) JumpTo(AutoScrollMinSize.Height); else Restore(anchor); Invalidate();
        }
        protected override void OnResize(EventArgs e)
        {
            bool bottom = previousHeight == 0 || -AutoScrollPosition.Y + previousHeight >= AutoScrollMinSize.Height - Dp(55);
            Tuple<ChatRecord,int> anchor = bottom ? null : TopAnchor();
            base.OnResize(e); Arrange(); previousHeight = ClientSize.Height;
            if (bottom) JumpTo(AutoScrollMinSize.Height); else Restore(anchor); Invalidate();
        }
        // MeasureText 默认会为最长单词扩宽矩形；编辑控件规则才能在长 URL/路径内部换行。
        private const TextFormatFlags MessageFormat = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        private const TextFormatFlags Tight = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        private int PadX { get { return Dp(11); } }
        private int PadTop { get { return Dp(7); } }
        private int PadBottom { get { return Dp(7); } }
        private int FileRow { get { return Dp(52); } }
        private int MediaHeight { get { return Dp(172); } }
        private int Caption { get { return Dp(20); } }
        private int stampHeight, stampDpi;
        private int StampHeight { get { if (stampDpi != DeviceDpi || stampHeight == 0) { stampDpi = DeviceDpi; stampHeight = TextRenderer.MeasureText("08:00", Theme.Small, Size.Empty, Tight).Height; } return stampHeight; } }
        private static readonly Font ExtensionFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold), CompactExtensionFont = new Font("Microsoft YaHei UI", 7F, FontStyle.Bold);
        private static string StampTime(ChatRecord r) { return r.Time == default(DateTime) ? "" : r.Time.ToString("HH:mm"); }
        private int StampWidth(ChatRecord r) { return TextRenderer.MeasureText(StampTime(r), Theme.Small, Size.Empty, Tight).Width + (StatusGlyph(r).Length > 0 ? Dp(19) : 0); }
        // 状态图标只表达本机能确认的事实：时钟=等待 ACK，单勾=对方程序已收到（不是已读），红色感叹号=未确认送达。
        private static string StatusGlyph(ChatRecord r)
        {
            if (!r.Outgoing) return "";
            string state = r.State ?? "";
            if (state.Contains("未确认") || state.Contains("失败")) return "error";
            if (state == "等待确认") return "clock";
            if (state.Contains("送达")) return "check";
            return "";
        }
        private static string StateDescription(ChatRecord r)
        {
            string state = r.State ?? "";
            if (!r.Outgoing) return state;
            if (state == "等待确认") return "等待对方确认";
            if (state.Contains("送达") && !state.Contains("未确认")) return state + "（对方程序已收到，不代表已读）";
            return state;
        }
        private static bool Failed(ChatRecord r) { return StatusGlyph(r) == "error"; }
        private static bool CardMedia(FileTransferState file) { return !String.IsNullOrEmpty(file.LocalPath) && MediaPreview.Supported(file.LocalPath); }
        private static bool Continues(ChatRecord previous, ChatRecord next)
        {
            if (previous.Outgoing != next.Outgoing || (!next.Outgoing && previous.Sender != next.Sender)) return false;
            double minutes = (next.Time - previous.Time).TotalMinutes; return minutes > -1 && minutes < 5;
        }
        private static string DateLabel(DateTime time)
        {
            DateTime today = DateTime.Today;
            if (time.Date == today) return "今天";
            if (time.Date == today.AddDays(-1)) return "昨天";
            return time.Year == today.Year ? time.ToString("M月d日") : time.ToString("yyyy年M月d日");
        }
        private int spacerFontKey; private float spacerWidth;
        private string Spacer(int width)
        {
            int key = Font.GetHashCode() ^ DeviceDpi;
            if (spacerFontKey != key || spacerWidth <= 0) { spacerFontKey = key; spacerWidth = Math.Max(1F, TextRenderer.MeasureText(new string(' ', 20), Font, Size.Empty, Tight).Width / 20F); }
            // 不换行空格把时间与状态的位置预留在最后一行末尾，放不下时连同末词一起换行。
            return new string(' ', (int)Math.Ceiling(width / spacerWidth) + 1);
        }
        private Measure MeasureRecord(ChatRecord r, int maxBubble)
        {
            string display = DisplayText(r);
            bool cards = r.Transfers != null && r.Transfers.Length > 0;
            string[] media = cards ? new string[0] : r.GetLocalFiles().Where(MediaPreview.Supported).ToArray();
            int stamp = StampWidth(r);
            string key = display + "\u0001" + maxBubble + "\u0001" + Font.Name + Font.Size + "\u0001" + highlightText + "\u0001" + stamp + "\u0001" + (cards ? String.Join("|", r.Transfers.Select(f => CardMedia(f) ? "m" : "f")) : "-") + "\u0001" + media.Length + "\u0001" + DeviceDpi;
            Measure m; if (measures.TryGetValue(r, out m) && m.Key == key) return m;
            m = new Measure { Key = key, TextMax = Math.Max(Dp(40), maxBubble - PadX * 2) };
            bool attachments = cards || media.Length > 0;
            if (display.Length > 0)
            {
                if (highlightText.Length > 0 || MessageLinks.Find(display).Count > 0 || ColorEmoji.Contains(display))
                {
                    m.Layout = new MessageLayout(display, Font, m.TextMax, highlightText);
                    m.TextSize = new Size(m.Layout.Width, m.Layout.Height);
                    m.InlineStamp = !attachments && m.Layout.LastLineRight + Dp(8) + stamp <= m.TextMax;
                }
                else
                {
                    m.InlineStamp = !attachments;
                    m.Text = m.InlineStamp ? display + Spacer(stamp + Dp(6)) : display;
                    m.TextSize = TextRenderer.MeasureText(m.Text, Font, new Size(m.TextMax, Int32.MaxValue), MessageFormat);
                }
            }
            int content = m.TextSize.Width;
            if (!m.InlineStamp) content = Math.Max(content, stamp);
            if (cards) content = Math.Max(content, r.Transfers.Any(CardMedia) ? Dp(288) : Dp(240));
            if (media.Length > 0) content = Math.Max(content, Dp(288));
            m.ContentWidth = Math.Min(Math.Max(content, Dp(24)), m.TextMax);
            int height = PadTop + m.TextSize.Height;
            if (cards) height += (display.Length > 0 ? Dp(6) : Dp(1)) + r.Transfers.Sum(f => CardMedia(f) ? MediaHeight + Caption + Dp(4) : FileRow) + Dp(4) * (r.Transfers.Length - 1);
            if (media.Length > 0) height += (display.Length > 0 ? Dp(6) : Dp(1)) + media.Length * MediaHeight + Dp(6) * (media.Length - 1);
            if (!m.InlineStamp) height += Dp(3) + StampHeight;
            m.Height = height + PadBottom; m.Width = m.ContentWidth + PadX * 2;
            return m;
        }
        private Size PillSize(string text, Font font) { Size size = TextRenderer.MeasureText(text, font, Size.Empty, Tight); return new Size(size.Width + Dp(20), size.Height + Dp(8)); }
        private void Arrange()
        {
            if (ClientSize.Width < 50) return;
            int width = ClientSize.Width, side = Dp(14), tail = Dp(7);
            int maxBubble = Math.Max(Dp(120), Math.Min(Dp(540), (int)((width - side * 2 - tail) * .78)));
            bounds.Clear(); textLayouts.Clear(); items.Clear();
            Dictionary<ChatRecord,Measure> next = new Dictionary<ChatRecord,Measure>();
            int y = Dp(10); DateTime day = DateTime.MinValue;
            for (int i = 0; i < records.Count; i++)
            {
                ChatRecord r = records[i]; Item item = new Item();
                // 时间为空（旧记录或导入数据）时不画“0001年”日期胶囊，也不显示 00:00。
                bool newDay = r.Time != default(DateTime) && (i == 0 || r.Time.Date != day);
                if (newDay)
                {
                    item.DateLabel = DateLabel(r.Time); day = r.Time.Date; if (i > 0) y += Dp(10);
                    Size pill = PillSize(item.DateLabel, Theme.Small); item.DateBounds = new Rectangle((width - pill.Width) / 2, y, pill.Width, pill.Height); y += pill.Height + Dp(10);
                }
                else y += i > 0 && Continues(records[i - 1], r) ? Dp(3) : Dp(10);
                item.First = item.DateLabel != null || !Continues(records[i - 1], r);
                Measure m = MeasureRecord(r, maxBubble); next[r] = m; item.M = m;
                bounds.Add(new Rectangle(r.Outgoing ? width - side - tail - m.Width : side + tail, y, m.Width, m.Height));
                textLayouts.Add(m.Layout); items.Add(item); y += m.Height;
            }
            for (int i = 0; i < items.Count; i++) items[i].Last = i == items.Count - 1 || !items[i + 1].First;
            measures = next; y += Dp(14);
            // 内容不足一屏时贴底显示，与聊天输入区相邻。
            int pad = Math.Max(0, ClientSize.Height - y);
            if (pad > 0) for (int i = 0; i < bounds.Count; i++) { Rectangle r = bounds[i]; r.Offset(0, pad); bounds[i] = r; if (items[i].DateLabel != null) { Rectangle d = items[i].DateBounds; d.Offset(0, pad); items[i].DateBounds = d; } }
            AutoScrollMinSize = new Size(0, y + pad); UpdateJump();
        }
        // 尾角与主体使用同一轮廓，选中描边不会在接缝处穿过气泡。
        internal static GraphicsPath BubblePath(Rectangle body, int radius, int tail, bool outgoing) { return BubbleShape(body, radius, radius, radius, tail, outgoing); }
        // 远离尾角一侧固定大圆角；尾角一侧按组内位置使用小圆角，组尾画出向外弯的尾巴。
        internal static GraphicsPath BubbleShape(RectangleF body, float radius, float joinTop, float joinBottom, float tail, bool outgoing)
        {
            float w = body.Width, h = body.Height; GraphicsPath path = new GraphicsPath();
            Corner(path, 0, 0, Math.Min(radius, h / 2), 180);
            Corner(path, w, 0, Math.Min(joinTop, h / 2), 270);
            if (tail > 0)
            {
                float rise = Math.Min(h - Math.Min(joinTop, h / 2), tail * 2.4F);
                path.AddLine(w, Math.Min(joinTop, h / 2), w, h - rise);
                path.AddBezier(w, h - rise, w, h - rise * .32F, w + tail * .42F, h - tail * .06F, w + tail, h);
                path.AddLine(w + tail, h, w - 1, h);
            }
            else Corner(path, w, h, Math.Min(joinBottom, h / 2), 0);
            Corner(path, 0, h, Math.Min(radius, h / 2), 90);
            path.CloseFigure();
            using (Matrix transform = outgoing ? new Matrix(1, 0, 0, 1, body.X, body.Y) : new Matrix(-1, 0, 0, 1, body.Right, body.Y)) path.Transform(transform);
            return path;
        }
        private static void Corner(GraphicsPath path, float x, float y, float r, float start)
        {
            if (r < .5F) { path.AddLine(x, y, x, y); return; }
            float d = r * 2, left = start == 180 || start == 90 ? x : x - d, top = start == 180 || start == 270 ? y : y - d;
            path.AddArc(left, top, d, d, start, 90);
        }
        private GraphicsPath Shape(int index, Rectangle body)
        {
            Item item = index < items.Count ? items[index] : null; float big = Dp(Theme.BubbleRadius), small = Dp(6);
            if (item == null) return BubblePath(body, Dp(Theme.BubbleRadius), Dp(7), records[index].Outgoing);
            return BubbleShape(body, big, item.First ? big : small, small, item.Last ? Dp(7) : 0, records[index].Outgoing);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ChatWallpaper.Draw(g, ClientRectangle, wallpaperPattern, wallpaperImage, DpiScale);
            mediaHits.Clear(); linkHits.Clear(); fileHits.Clear(); stampHits.Clear(); retryHits.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int dy = -ScrollOffset + Shift, first = FirstVisible(-dy - Dp(48));
            for (int i = first; i < records.Count && i < bounds.Count; i++)
            {
                Item item = items[i];
                if (item.DateLabel != null) { Rectangle date = item.DateBounds; date.Offset(0, dy); if (date.Top > ClientSize.Height) break; if (date.Bottom >= 0) DrawPill(g, date, item.DateLabel, 255); }
                Rectangle r = bounds[i]; r.Offset(0, dy);
                if (r.Top > ClientSize.Height) break; if (r.Bottom < -Dp(4)) continue;
                DrawMessage(g, i, r, item);
            }
            DrawFloatingDate(g, dy);
            DrawJump(g);
            DrawScrollbar(g);
        }
        private int FirstVisible(int top)
        {
            int low = 0, high = bounds.Count - 1, answer = bounds.Count;
            while (low <= high) { int middle = (low + high) / 2; if (bounds[middle].Bottom >= top) { answer = middle; high = middle - 1; } else low = middle + 1; }
            // 日期胶囊位于气泡上方，向前多取一条以免顶部胶囊被跳过。
            return Math.Max(0, answer - 1);
        }
        private void DrawPill(Graphics g, Rectangle bounds, string text, int alpha)
        {
            Color fill = Theme.ServiceFill;
            using (GraphicsPath path = Theme.RoundedF(bounds, bounds.Height / 2F)) using (SolidBrush b = new SolidBrush(Color.FromArgb(fill.A * alpha / 255, fill))) g.FillPath(b, path);
            if (alpha >= 255) TextRenderer.DrawText(g, text, Theme.Small, bounds, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | Tight);
            else using (SolidBrush ink = new SolidBrush(Color.FromArgb(alpha, Color.White))) using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) g.DrawString(text, Theme.Small, ink, bounds, format);
        }
        // 滚动时顶部浮出当前日期，停下约一秒后淡出。
        private void DrawFloatingDate(Graphics g, int dy)
        {
            double idle = Motion.Now - scrolledAt; if (idle > 1300 || MaxScroll <= 0 || bounds.Count == 0) return;
            int index = FirstVisible(Dp(8) - dy); if (index < bounds.Count - 1 && bounds[index].Bottom + dy < Dp(8)) index++;
            if (index >= records.Count) return;
            string label = DateLabel(records[index].Time);
            for (int i = Math.Max(0, index - 1); i <= Math.Min(items.Count - 1, index + 1); i++)
                if (items[i].DateLabel == label) { Rectangle own = items[i].DateBounds; own.Offset(0, dy); if (own.Top > -own.Height && own.Top < Dp(40)) return; }
            Size size = PillSize(label, Theme.Small); Rectangle pill = new Rectangle((ClientSize.Width - size.Width) / 2, Dp(8), size.Width, size.Height);
            int alpha = idle < 1000 ? 255 : (int)(255 * (1 - (idle - 1000) / 300)); if (alpha <= 0) return;
            DrawPill(g, pill, label, alpha);
        }
        private void DrawMessage(Graphics g, int i, Rectangle r, Item item)
        {
            ChatRecord record = records[i]; Measure m = item.M; bool outgoing = record.Outgoing;
            if (i == selected && Focused) using (SolidBrush band = new SolidBrush(Color.FromArgb(Theme.Dark ? 52 : 34, Theme.Blue))) g.FillRectangle(band, 0, r.Top - Dp(2), ClientSize.Width, r.Height + Dp(4));
            using (GraphicsPath path = Shape(i, r))
            {
                using (GraphicsPath shadow = (GraphicsPath)path.Clone()) using (Matrix down = new Matrix(1, 0, 0, 1, 0, Math.Max(1, Dp(1))))
                { shadow.Transform(down); using (SolidBrush b = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 26, Theme.Dark ? Color.Black : Theme.Blend(Color.Black, Theme.Blue, .7F)))) g.FillPath(b, shadow); }
                using (SolidBrush b = new SolidBrush(outgoing ? Theme.Outgoing : Theme.Surface)) g.FillPath(b, path);
            }
            Point origin = new Point(r.X + PadX, r.Y + PadTop);
            if (m.Layout != null) m.Layout.Draw(g, origin, Font, linkHits);
            else if (!String.IsNullOrEmpty(m.Text)) TextRenderer.DrawText(g, m.Text, Font, new Rectangle(origin, new Size(m.TextMax, m.TextSize.Height + Dp(2))), Theme.Ink, MessageFormat);
            int y = origin.Y + m.TextSize.Height + (m.TextSize.Height > 0 ? Dp(6) : Dp(1));
            Color meta = outgoing ? Theme.OutgoingMeta : Theme.Muted, back = outgoing ? Theme.Outgoing : Theme.Surface;
            if (record.Transfers != null && record.Transfers.Length > 0)
                foreach (FileTransferState file in record.Transfers)
                {
                    Rectangle area = new Rectangle(origin.X, y, m.ContentWidth, CardMedia(file) ? MediaHeight + Caption + Dp(4) : FileRow);
                    if (CardMedia(file)) DrawMediaCard(g, area, record, file, meta);
                    else DrawFileCard(g, area, record, file, meta, back);
                    fileHits.Add(Tuple.Create(area, record, file.Id)); y = area.Bottom + Dp(4);
                }
            else
            {
                string[] media = record.GetLocalFiles().Where(MediaPreview.Supported).ToArray();
                foreach (string path in media) { Rectangle preview = new Rectangle(origin.X, y, m.ContentWidth, MediaHeight); DrawPreview(g, preview, path, null); y = preview.Bottom + Dp(6); }
            }
            int stampWidth = StampWidth(record), stampHeight = StampHeight;
            Rectangle stamp = m.InlineStamp ? new Rectangle(r.Right - PadX - stampWidth, origin.Y + m.TextSize.Height - stampHeight - Dp(1), stampWidth, stampHeight)
                : new Rectangle(r.Right - PadX - stampWidth, r.Bottom - PadBottom - stampHeight + Dp(1), stampWidth, stampHeight);
            DrawStamp(g, stamp, record, meta);
            stampHits.Add(Tuple.Create(stamp, record));
            if (outgoing && Failed(record))
            {
                int size = Dp(20); Rectangle badge = new Rectangle(r.X - Dp(10) - size, r.Bottom - size - Dp(2), size, size);
                Theme.Glyph(g, "error", badge, Theme.Danger); retryHits.Add(Tuple.Create(Rectangle.Inflate(badge, Dp(3), Dp(3)), i));
            }
        }
        private void DrawStamp(Graphics g, Rectangle stamp, ChatRecord record, Color meta)
        {
            string glyph = StatusGlyph(record), time = StampTime(record);
            int iconSize = Dp(15), textWidth = stamp.Width - (glyph.Length > 0 ? Dp(19) : 0);
            TextRenderer.DrawText(g, time, Theme.Small, new Rectangle(stamp.X, stamp.Y, textWidth, stamp.Height), meta, Tight | TextFormatFlags.VerticalCenter);
            if (glyph.Length == 0) return;
            Rectangle icon = new Rectangle(stamp.Right - iconSize, stamp.Y + (stamp.Height - iconSize) / 2, iconSize, iconSize);
            if (glyph == "error") Theme.Glyph(g, "error", icon, Theme.Danger);
            else Theme.Glyph(g, glyph, icon, glyph == "check" ? (Theme.Dark ? Theme.Blend(Color.White, Theme.Blue, .55F) : Theme.Blue) : meta);
        }
        private static bool Active(FileTransferState file) { string status = file.DisplayStatus() ?? ""; return status.Contains("中") && !status.Contains("中断") && file.Percent < 100; }
        private static bool Receivable(ChatRecord record, FileTransferState file) { string status = file.DisplayStatus() ?? ""; return !record.Outgoing && String.IsNullOrEmpty(file.LocalPath) && (status.StartsWith("待接收") || status.Contains("重试") || status.Contains("续传") || status.Contains("点击接收")); }
        private void DrawFileCard(Graphics g, Rectangle area, ChatRecord record, FileTransferState file, Color meta, Color back)
        {
            int circle = Dp(44); Rectangle icon = new Rectangle(area.X, area.Y + (area.Height - circle) / 2, circle, circle);
            bool active = Active(file), ended = !active && !Receivable(record, file) && String.IsNullOrEmpty(file.LocalPath) && !record.Outgoing;
            Color fill = ended ? Theme.Blend(Theme.Muted, back, .55F) : Theme.Blue;
            using (SolidBrush b = new SolidBrush(fill)) g.FillEllipse(b, icon);
            if (active)
            {
                RectangleF ring = RectangleF.Inflate(icon, -Dp(5), -Dp(5));
                using (Pen track = new Pen(Color.FromArgb(70, Color.White), 2.2F * DpiScale)) g.DrawEllipse(track, ring);
                float sweep = 360F * Math.Max(2, Math.Min(100, file.Percent)) / 100F;
                using (Pen done = new Pen(Color.White, 2.2F * DpiScale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(done, ring, -90, sweep);
                Theme.Glyph(g, record.Outgoing ? "arrow-up" : "close", Rectangle.Inflate(icon, -Dp(14), -Dp(14)), Color.White);
            }
            else if (Receivable(record, file)) Theme.Glyph(g, "download", Rectangle.Inflate(icon, -Dp(11), -Dp(11)), Color.White);
            else
            {
                string extension = Path.GetExtension(file.Name ?? "").TrimStart('.').ToUpperInvariant();
                if (extension.Length > 0 && extension.Length <= 4) TextRenderer.DrawText(g, extension, extension.Length > 3 ? CompactExtensionFont : ExtensionFont, icon, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | Tight);
                else Theme.Glyph(g, "file", Rectangle.Inflate(icon, -Dp(12), -Dp(12)), Color.White);
            }
            int left = icon.Right + Dp(10), width = Math.Max(Dp(20), area.Right - left);
            Rectangle name = new Rectangle(left, area.Y + Dp(7), width, Dp(20));
            if (highlightText.Length > 0)
            {
                GraphicsState clip = g.Save(); g.SetClip(name, CombineMode.Intersect);
                new MessageLayout(file.Name, Theme.Section, Int32.MaxValue / 2, highlightText).Draw(g, name.Location, Theme.Section, new List<Tuple<Rectangle,string>>()); g.Restore(clip);
            }
            else TextRenderer.DrawText(g, file.Name, Theme.Section, name, Theme.Ink, TextFormatFlags.EndEllipsis | Tight | TextFormatFlags.VerticalCenter);
            string label = Theme.SizeText(file.Size) + (active ? " · " + file.Percent + "%" : "") + " · " + file.DisplayStatus();
            TextRenderer.DrawText(g, label, Theme.Small, new Rectangle(left, area.Y + Dp(28), width, Dp(18)), meta, TextFormatFlags.EndEllipsis | Tight | TextFormatFlags.VerticalCenter);
        }
        private void DrawMediaCard(Graphics g, Rectangle area, ChatRecord record, FileTransferState file, Color meta)
        {
            Rectangle preview = new Rectangle(area.X, area.Y, area.Width, MediaHeight);
            DrawPreview(g, preview, file.LocalPath, Active(file) ? file : null);
            string label = Theme.SizeText(file.Size) + " · " + file.DisplayStatus();
            Size labelSize = TextRenderer.MeasureText(label, Theme.Small, Size.Empty, Tight);
            int labelWidth = Math.Min(labelSize.Width, area.Width / 2);
            Rectangle caption = new Rectangle(area.X, preview.Bottom + Dp(4), area.Width, Caption);
            TextRenderer.DrawText(g, file.Name, Theme.Small, new Rectangle(caption.X, caption.Y, Math.Max(1, caption.Width - labelWidth - Dp(8)), caption.Height), Theme.Ink, TextFormatFlags.EndEllipsis | Tight | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, label, Theme.Small, new Rectangle(caption.Right - labelWidth, caption.Y, labelWidth, caption.Height), meta, TextFormatFlags.EndEllipsis | Tight | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
        // 预览按“铺满裁切”显示并圆角；系统缩略图异步生成，未就绪时显示占位而不阻塞绘制。
        private void DrawPreview(Graphics g, Rectangle preview, string path, FileTransferState progress)
        {
            using (GraphicsPath shape = Theme.RoundedF(preview, Dp(10)))
            {
                using (SolidBrush background = new SolidBrush(Theme.Blend(Theme.Ink, Theme.Surface, Theme.Dark ? .12F : .06F))) g.FillPath(background, shape);
                Bitmap bitmap;
                if (previews.TryGetValue(path, out bitmap) && bitmap != null)
                {
                    double ratio = Math.Max((double)preview.Width / bitmap.Width, (double)preview.Height / bitmap.Height);
                    int w = (int)Math.Ceiling(bitmap.Width * ratio), h = (int)Math.Ceiling(bitmap.Height * ratio);
                    GraphicsState state = g.Save(); g.SetClip(shape, CombineMode.Intersect); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bitmap, new Rectangle(preview.X + (preview.Width - w) / 2, preview.Y + (preview.Height - h) / 2, w, h)); g.Restore(state);
                }
                else
                {
                    if (!previews.ContainsKey(path)) LoadPreview(path);
                    int icon = Dp(34); Theme.Glyph(g, MediaPreview.Video(path) ? "play" : "image", new Rectangle(preview.X + (preview.Width - icon) / 2, preview.Y + (preview.Height - icon) / 2 - Dp(8), icon, icon), Theme.Muted);
                    TextRenderer.DrawText(g, previews.ContainsKey(path) ? "预览不可用" : "正在生成预览", Theme.Small, new Rectangle(preview.X, preview.Y + preview.Height / 2 + Dp(16), preview.Width, Dp(18)), Theme.Muted, TextFormatFlags.HorizontalCenter | Tight);
                }
            }
            int button = Dp(46); Rectangle center = new Rectangle(preview.X + (preview.Width - button) / 2, preview.Y + (preview.Height - button) / 2, button, button);
            if (progress != null)
            {
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillEllipse(shade, center);
                RectangleF ring = RectangleF.Inflate(center, -Dp(5), -Dp(5));
                using (Pen done = new Pen(Color.White, 2.4F * DpiScale) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(done, ring, -90, 360F * Math.Max(2, Math.Min(100, progress.Percent)) / 100F);
            }
            else if (MediaPreview.Video(path) && previews.ContainsKey(path) && previews[path] != null)
            {
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillEllipse(shade, center);
                Theme.Glyph(g, "play", Rectangle.Inflate(center, -Dp(13), -Dp(13)), Color.White);
            }
            mediaHits.Add(Tuple.Create(preview, path));
        }
        private Rectangle JumpBounds() { int size = Dp(42); return new Rectangle(ClientSize.Width - size - Dp(18), ClientSize.Height - size - Dp(14), size, size); }
        private void UpdateJump() { jump.Set(MaxScroll - ScrollOffset > Math.Max(Dp(120), ClientSize.Height / 2) ? 1 : 0); }
        private void DrawJump(Graphics g)
        {
            double v = jump.Value; if (v <= .01) return;
            Rectangle bounds = JumpBounds(); float grow = (float)(.75 + .25 * v);
            RectangleF circle = new RectangleF(bounds.X + bounds.Width * (1 - grow) / 2, bounds.Y + bounds.Height * (1 - grow) / 2 + (float)((1 - v) * Dp(10)), bounds.Width * grow, bounds.Height * grow);
            int alpha = (int)(255 * v);
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(alpha * 40 / 255, Color.Black))) g.FillEllipse(shadow, circle.X, circle.Y + Dp(1.5), circle.Width, circle.Height);
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(alpha, Theme.Surface))) g.FillEllipse(fill, circle);
            Theme.Glyph(g, "arrow-down", RectangleF.Inflate(circle, -circle.Width * .26F, -circle.Height * .26F), Color.FromArgb(alpha, Theme.Muted));
            if (unseen > 0)
            {
                string count = unseen > 99 ? "99+" : unseen.ToString();
                SizeF text = g.MeasureString(count, Theme.Small); float width = Math.Max(Dp(20), text.Width + Dp(6));
                RectangleF badge = new RectangleF(circle.X + (circle.Width - width) / 2, circle.Y - Dp(10), width, Dp(20));
                using (GraphicsPath pill = Theme.RoundedF(badge, badge.Height / 2)) using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, Theme.Blue))) g.FillPath(b, pill);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(alpha, Color.White))) using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) g.DrawString(count, Theme.Small, ink, badge, format);
            }
        }
        private bool JumpVisible { get { return jump.Target > 0; } }
        protected override void OnScrollChanged()
        {
            scrolledAt = Motion.Now; if (MaxScroll - ScrollOffset < Dp(24)) unseen = 0;
            UpdateJump(); Invalidate();
        }
        private string DisplayText(ChatRecord record)
        {
            string text=record.DisplayText(), preview=MessageText.Preview(text);
            if(highlightText.Length==0||preview.IndexOf(highlightText,StringComparison.OrdinalIgnoreCase)>=0)return preview;
            int index=text.IndexOf(highlightText,StringComparison.OrdinalIgnoreCase);if(index<0)return preview;
            // 命中摘要之外时展示上下文，搜索结果不应只剩一条看不见关键词的气泡。
            int start=Math.Max(0,index-60);if(start>0&&Char.IsLowSurrogate(text[start]))start--;
            int end=Math.Min(text.Length,index+Math.Max(highlightText.Length,180));if(end<text.Length&&Char.IsHighSurrogate(text[end-1]))end--;
            return (start>0?"… ":"")+text.Substring(start,end-start)+(end<text.Length?" …":"");
        }
        private int HitBubble(Point client)
        {
            Point point = new Point(client.X, client.Y + ScrollOffset - Shift);
            for (int i = FirstVisible(point.Y - Dp(40)); i < bounds.Count; i++)
            {
                if (bounds[i].Top > point.Y + Dp(4)) break;
                if (!Rectangle.Inflate(bounds[i], Dp(8), Dp(2)).Contains(point)) continue;
                using (GraphicsPath path = Shape(i, bounds[i])) if (path.IsVisible(point)) return i;
            }
            return -1;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if (ScrollbarMouseDown(e)) return;
            pressedAction=null;dragFilePath=null;fileDragStarted=false;
            if (JumpVisible && JumpBounds().Contains(e.Location)) { if (e.Button == MouseButtons.Left) ScrollToLatest(); return; }
            var retry = retryHits.FirstOrDefault(item => item.Item1.Contains(e.Location));
            if (retry != null) { selected = retry.Item2; Invalidate(); ContextMenuStrip.Show(this, e.Location); return; }
            selected = HitBubble(e.Location);
            Invalidate();
            if(e.Button!=MouseButtons.Left)return;
            dragStart=e.Location;
            var link=linkHits.FirstOrDefault(item=>item.Item1.Contains(e.Location));
            if(link!=null){pressedAction=delegate{MessageLinks.Open(this,link.Item2);};return;}
            var fileHit=fileHits.FirstOrDefault(item=>item.Item1.Contains(e.Location));
            if(fileHit!=null) {
                FileTransferState file=fileHit.Item2.Transfers.FirstOrDefault(f=>f.Id==fileHit.Item3);if(file==null)return;
                dragFilePath=ValidDragPath(file.LocalPath);
                pressedAction=delegate {
                    if(!String.IsNullOrEmpty(file.LocalPath))MediaPreview.OpenFile(this,file.LocalPath,false);
                    else if(!fileHit.Item2.Outgoing&&FileRequested!=null)FileRequested(fileHit.Item2,fileHit.Item3);
                };
            } else {
                var media=mediaHits.FirstOrDefault(item=>item.Item1.Contains(e.Location));
                if(media!=null){dragFilePath=ValidDragPath(media.Item2);pressedAction=delegate{MediaPreview.Open(this,media.Item2);};}
                else if(SelectedRecord!=null&&SelectedRecord.GetLocalFiles().Length==1)dragFilePath=ValidDragPath(SelectedRecord.GetLocalFiles()[0]);
            }
            if(pressedAction!=null||dragFilePath!=null)Capture=true;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (ScrollbarMouseUp(e)) return;
            if (e.Button != MouseButtons.Left) return;
            Action action=pressedAction;
            bool click=!fileDragStarted&&!MovedPastDragThreshold(e.Location);
            pressedAction=null;dragFilePath=null;fileDragStarted=false;Capture=false;
            if(click&&action!=null)action();
        }
        private static string ValidDragPath(string path) { return !String.IsNullOrEmpty(path)&&Path.IsPathRooted(path)&&File.Exists(path)?Path.GetFullPath(path):null; }
        private bool MovedPastDragThreshold(Point point) { Size size=SystemInformation.DragSize;return !new Rectangle(dragStart.X-size.Width/2,dragStart.Y-size.Height/2,size.Width,size.Height).Contains(point); }
        internal static DataObject FileDragData(string path) {
            string valid=ValidDragPath(path);return valid==null?null:new DataObject(DataFormats.FileDrop,new[]{valid});
        }
        private void ShowFullText()
        {
            ChatRecord record = SelectedRecord;
            if (record != null && !String.IsNullOrEmpty(record.DisplayText()))
                using (MessageTextDialog dialog = new MessageTextDialog(record.DisplayText())) dialog.ShowDialog(FindForm());
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && !InScrollbar(e.Location) && !(JumpVisible && JumpBounds().Contains(e.Location)) && !mediaHits.Any(h => h.Item1.Contains(e.Location)) && !fileHits.Any(h => h.Item1.Contains(e.Location))) ShowFullText();
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if (ScrollbarMouseMove(e) && e.Button == MouseButtons.Left && dragFilePath == null) { Cursor = Cursors.Default; return; }
            bool hand = linkHits.Any(h=>h.Item1.Contains(e.Location))||fileHits.Any(h=>h.Item1.Contains(e.Location))||mediaHits.Any(h=>h.Item1.Contains(e.Location))||retryHits.Any(h=>h.Item1.Contains(e.Location))||(JumpVisible&&JumpBounds().Contains(e.Location));
            Cursor=hand?Cursors.Hand:Cursors.Default;
            var stamp = stampHits.FirstOrDefault(h => h.Item1.Contains(e.Location));
            string tip = stamp == null ? null : stamp.Item2.Time.ToString("yyyy-MM-dd HH:mm:ss") + (String.IsNullOrEmpty(stamp.Item2.State) ? "" : " · " + StateDescription(stamp.Item2));
            if (tip != tipText) { tipText = tip; tips.SetToolTip(this, tip); }
            if(e.Button==MouseButtons.Left&&dragFilePath!=null&&!fileDragStarted&&MovedPastDragThreshold(e.Location)) {
                DataObject data=FileDragData(dragFilePath);fileDragStarted=true;pressedAction=null;Capture=false;
                try { IsDraggingFile=true;if(data!=null)DoDragDrop(data,DragDropEffects.Copy); }
                finally { IsDraggingFile=false;dragFilePath=null; }
            }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e);if(records!=null&&bounds!=null&&textLayouts!=null){spacerWidth=0;Arrange();Invalidate();} }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { ShowFullText(); e.Handled = true; }
            if (e.KeyCode == Keys.End) { ScrollToLatest(); e.Handled = true; }
            if (e.KeyCode == Keys.Home) { SmoothTo(0); e.Handled = true; }
            if (e.KeyCode == Keys.Down) { SmoothBy(Dp(64)); e.Handled = true; }
            if (e.KeyCode == Keys.Up) { SmoothBy(-Dp(64)); e.Handled = true; }
            base.OnKeyDown(e);
        }
        private void CopySelected()
        {
            ChatRecord record = SelectedRecord; if (record != null) CopyText(record.Text);
        }
        private static void CopyText(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { System.Media.SystemSounds.Beep.Play(); }
        }
        protected override void Dispose(bool disposing) { if(disposing) {ChatWallpaper.Release(wallpaperImage);wallpaperImage=null;foreach(Bitmap bitmap in previews.Values) if(bitmap!=null)bitmap.Dispose();previews.Clear();tips.Dispose();} if (disposing && ContextMenuStrip != null) ContextMenuStrip.Dispose(); base.Dispose(disposing); }
    }
}
