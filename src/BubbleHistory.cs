using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    // 仅绘制可见消息，避免为几百条记录各创建一组原生窗口句柄。
    public sealed class BubbleHistory : ScrollableControl
    {
        private readonly List<ChatRecord> records = new List<ChatRecord>();
        private readonly List<Rectangle> bounds = new List<Rectangle>();
        private readonly List<MessageLayout> textLayouts=new List<MessageLayout>();
        private readonly List<Tuple<Rectangle,string>> linkHits=new List<Tuple<Rectangle,string>>();
        private readonly List<Tuple<Rectangle,ChatRecord,int>> fileHits=new List<Tuple<Rectangle,ChatRecord,int>>();
        private int selected = -1;
        private string wallpaperPattern="none";
        private ChatWallpaper.Asset wallpaperImage;
        public void SetWallpaper(string pattern,string path) {
            ChatWallpaper.Asset next=null;if(pattern=="image")try{next=ChatWallpaper.Acquire(path);}catch(Exception){pattern="orbits";}
            ChatWallpaper.Release(wallpaperImage);wallpaperImage=next;wallpaperPattern=pattern;Invalidate();
        }
        protected override void OnPaintBackground(PaintEventArgs e) { ChatWallpaper.Draw(e.Graphics,ClientRectangle,wallpaperPattern,wallpaperImage==null?null:wallpaperImage.Image,DeviceDpi/96F); }
        private readonly System.Collections.Generic.Dictionary<string,Bitmap> previews = new System.Collections.Generic.Dictionary<string,Bitmap>();
        private readonly System.Collections.Generic.HashSet<string> pending = new System.Collections.Generic.HashSet<string>();
        private readonly System.Collections.Generic.List<Tuple<Rectangle,string>> mediaHits = new System.Collections.Generic.List<Tuple<Rectangle,string>>();
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
        private float scale = 1F;
        private int previousHeight;
        public event Action<ChatRecord> RetryRequested;
        public event Action<ChatRecord> ForwardRequested;
        public event Action<ChatRecord,int> FileRequested;
        public int TextLength { get { return Text.Length; } }
        public BubbleHistory()
        {
            DoubleBuffered = true; AutoScroll = true; TabStop = true; BackColor = Theme.Wallpaper;
            Font=Theme.Body; AccessibleRole = AccessibleRole.List; AccessibleName = "聊天记录";
            ContextMenuStrip menu = Theme.Menu();
            ToolStripItem copy = menu.Items.Add("复制消息", null, delegate { CopySelected(); });
            ToolStripItem forward = menu.Items.Add("转发…", null, delegate { if (SelectedRecord != null && ForwardRequested != null) ForwardRequested(SelectedRecord); });
            ToolStripMenuItem open=new ToolStripMenuItem("打开文件"),openWith=new ToolStripMenuItem("打开方式…");
            menu.Items.Add(open);menu.Items.Add(openWith);
            open.Click+=delegate {if(open.Tag is string)MediaPreview.OpenFile(this,(string)open.Tag,false);};
            openWith.Click+=delegate {if(openWith.Tag is string)MediaPreview.OpenFile(this,(string)openWith.Tag,true);};
            ToolStripMenuItem folder = new ToolStripMenuItem("打开文件所在目录");
            folder.Click += delegate { if (folder.Tag is string) RevealFile(this, (string)folder.Tag); };
            ToolStripItem path = menu.Items.Add("复制文件路径", null, delegate { ChatRecord record = SelectedRecord; if (record != null) CopyText(String.Join(Environment.NewLine, record.GetLocalFiles())); });
            menu.Items.Insert(1, folder);
            ToolStripItem retry=menu.Items.Add("重新发送（可能重复）",null,delegate {if(SelectedRecord!=null&&RetryRequested!=null)RetryRequested(SelectedRecord);});
            menu.Opening += delegate
            {
                forward.Enabled = SelectedRecord != null && ForwardRequested != null;
                ChatRecord record = SelectedRecord; retry.Visible=RetryRequested!=null&&record!=null&&record.Outgoing&&(record.State??"").Contains("未确认"); copy.Enabled = record != null && !String.IsNullOrEmpty(record.Text);
                string[] paths = record == null ? new string[0] : record.GetLocalFiles();
                folder.Visible = path.Visible = open.Visible = openWith.Visible = paths.Length > 0; folder.Tag = null;
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
        private int D(int value) { return (int)Math.Round(value * scale); }
        public void Clear() { records.Clear(); bounds.Clear(); textLayouts.Clear(); linkHits.Clear(); mediaHits.Clear(); Text = ""; AutoScrollMinSize = Size.Empty; selected = -1; Invalidate(); }
        public bool HasDelivery(string state) { return records.Any(r => r.State == state); }
        public void ScrollToLatest() { AutoScrollPosition = new Point(0, AutoScrollMinSize.Height); Invalidate(); }
        public void Append(ChatRecord record)
        {
            bool bottom = records.Count == 0 || -AutoScrollPosition.Y + ClientSize.Height >= AutoScrollMinSize.Height - D(70);
            records.Add(record);
            while (records.Count > 200 || records.Sum(r => r.Text.Length) > 200000) { records.RemoveAt(0); selected--; }
            Text = String.Join("\n", records.Select(r => r.Sender + "  " + r.Text));
            Arrange(); if (bottom) AutoScrollPosition = new Point(0, AutoScrollMinSize.Height); Invalidate();
        }
        public void Delivery(ChatRecord record)
        {
            ChatRecord current = records.FirstOrDefault(r => r.Packet == record.Packet && r.Outgoing == record.Outgoing);
            if (current != null) { current.State = record.State; current.LocalFiles = record.LocalFiles; current.Transfers = record.Transfers; Arrange(); Invalidate(); }
        }
        protected override void OnResize(EventArgs e)
        {
            bool bottom = previousHeight == 0 || -AutoScrollPosition.Y + previousHeight >= AutoScrollMinSize.Height - D(55);
            base.OnResize(e); Arrange(); previousHeight = ClientSize.Height;
            if (bottom) AutoScrollPosition = new Point(0, AutoScrollMinSize.Height); Invalidate();
        }
        // MeasureText 默认会为最长单词扩宽矩形；编辑控件规则才能在长 URL/路径内部换行。
        private const TextFormatFlags MessageFormat = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
        private void Arrange()
        {
            if (ClientSize.Width < 50) return;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96F;
            bounds.Clear(); textLayouts.Clear(); int y = D(22), max = Math.Max(D(100), (int)((ClientSize.Width - SystemInformation.VerticalScrollBarWidth) * .77));
            foreach (ChatRecord r in records)
            {
                string display = r.DisplayText();
                MessageLayout layout=MessageLinks.Find(display).Count>0||ColorEmoji.Contains(display)?new MessageLayout(display,Font,max-D(28)):null;
                textLayouts.Add(layout);
                Size text = display.Length == 0 ? Size.Empty : layout==null?TextRenderer.MeasureText(display, Font, new Size(max - D(28), Int32.MaxValue), MessageFormat):new Size(layout.Width,layout.Height);
                Size state = TextRenderer.MeasureText(Stamp(r), Theme.Small, new Size(max - D(28), Int32.MaxValue), MessageFormat);
                int width = Math.Max(D(110), Math.Min(max, Math.Max(text.Width, state.Width) + D(28)));
                if(r.GetLocalFiles().Any(MediaPreview.Supported)) width=Math.Min(max,Math.Max(width,D(260)));
                bool cards = r.Transfers != null && r.Transfers.Length > 0;
                if(cards) width=Math.Min(max,Math.Max(width,D(320)));
                int height = text.Height + state.Height + D(24) + (cards ? r.Transfers.Sum(f => D(64) + (CardMedia(f) ? D(150) : 0)) : r.GetLocalFiles().Count(MediaPreview.Supported) * D(150));
                bounds.Add(new Rectangle(r.Outgoing ? ClientSize.Width - SystemInformation.VerticalScrollBarWidth - D(16) - width : D(16), y, width, height)); y += height + D(12);
            }
            AutoScrollMinSize = new Size(0, y + D(18));
        }
        // 尾角与主体使用同一轮廓，选中描边不会在接缝处穿过气泡。
        internal static GraphicsPath BubblePath(Rectangle body, int radius, int tail, bool outgoing)
        {
            int w = body.Width, h = body.Height, d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(w - d, 0, d, d, 270, 90);
            path.AddLine(w, radius, w, radius + 2);
            path.AddBezier(w, radius + 2, w + tail / 2F, radius + 2, w + tail, radius - tail / 2F, w + tail, radius - tail / 2F);
            path.AddBezier(w + tail, radius - tail / 2F, w + tail, radius + tail, w, radius + tail * 2, w, radius + tail * 2);
            path.AddArc(w - d, h - d, d, d, 0, 90);
            path.AddArc(0, h - d, d, d, 90, 90); path.CloseFigure();
            using (Matrix transform = outgoing ? new Matrix(1, 0, 0, 1, body.X, body.Y) : new Matrix(-1, 0, 0, 1, body.Right, body.Y)) path.Transform(transform);
            return path;
        }
        private static string Stamp(ChatRecord r) { return r.Time.ToString("HH:mm") + (r.Outgoing ? "  " + r.State : "  " + r.Sender + (r.GetLocalFiles().Length>0 && r.State!="已收到" ? " · "+r.State : "")); }
        private static bool CardMedia(FileTransferState file) { return !String.IsNullOrEmpty(file.LocalPath) && MediaPreview.Supported(file.LocalPath); }
        protected override void OnPaint(PaintEventArgs e)
        {
            mediaHits.Clear(); linkHits.Clear(); fileHits.Clear(); base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < records.Count && i < bounds.Count; i++)
            {
                Rectangle r = bounds[i]; r.Offset(AutoScrollPosition); if (!r.IntersectsWith(ClientRectangle)) continue;
                ChatRecord record = records[i];
                using (GraphicsPath path = BubblePath(r, D(Theme.BubbleRadius), D(7), record.Outgoing))
                {
                    using (SolidBrush b = new SolidBrush(record.Outgoing ? Theme.Sky : Theme.Surface)) e.Graphics.FillPath(b, path);
                    if (i == selected && Focused) using (Pen p = new Pen(Theme.Blue)) e.Graphics.DrawPath(p, path);
                }
                Rectangle content = new Rectangle(r.X + D(14), r.Y + D(12), r.Width - D(28), r.Height - D(24));
                Size stamp = TextRenderer.MeasureText(Stamp(record), Theme.Small, new Size(content.Width, Int32.MaxValue), MessageFormat);
                content.Height -= stamp.Height;
                bool cards = record.Transfers != null && record.Transfers.Length > 0;
                string[] media=cards ? new string[0] : record.GetLocalFiles().Where(MediaPreview.Supported).ToArray();
                int cardHeight = cards ? record.Transfers.Sum(f => D(64) + (CardMedia(f) ? D(150) : 0)) : 0;
                content.Height -= cardHeight;
                content.Height-=media.Length*D(150);
                for(int n=0;n<media.Length;n++) {
                    Rectangle preview=new Rectangle(content.X,content.Bottom+n*D(150),content.Width,D(140));
                    using(SolidBrush background=new SolidBrush(Theme.Ice)) e.Graphics.FillRectangle(background,preview); Bitmap bitmap;
                    if(previews.TryGetValue(media[n],out bitmap)&&bitmap!=null) {
                        double ratio=Math.Min((double)preview.Width/bitmap.Width,(double)preview.Height/bitmap.Height);int w=(int)(bitmap.Width*ratio),h=(int)(bitmap.Height*ratio);
                        e.Graphics.DrawImage(bitmap,new Rectangle(preview.X+(preview.Width-w)/2,preview.Y+(preview.Height-h)/2,w,h));
                    } else {LoadPreview(media[n]);TextRenderer.DrawText(e.Graphics,"预览不可用 / 加载中",Theme.Small,preview,Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
                    if(MediaPreview.Video(media[n])) TextRenderer.DrawText(e.Graphics,"▶ 点击播放",Theme.Body,preview,Theme.Blue,TextFormatFlags.Bottom|TextFormatFlags.HorizontalCenter);
                    mediaHits.Add(Tuple.Create(preview,media[n]));
                }
                if(i<textLayouts.Count&&textLayouts[i]!=null)textLayouts[i].Draw(e.Graphics,content.Location,Font,linkHits);
                else TextRenderer.DrawText(e.Graphics, record.DisplayText(), Font, content, Theme.Ink, MessageFormat);
                if(cards) {
                    int y=content.Bottom;
                    foreach(FileTransferState file in record.Transfers) {
                        int top=y;
                        TextRenderer.DrawText(e.Graphics,file.Name,Font,new Rectangle(content.X,y,content.Width,D(24)),Theme.Ink,TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix); y+=D(25);
                        if(CardMedia(file)) { DrawCardPreview(e.Graphics,new Rectangle(content.X,y,content.Width,D(140)),file.LocalPath); y+=D(150); }
                        string label=Theme.SizeText(file.Size)+" · "+file.Percent+"% · "+file.DisplayStatus();
                        TextRenderer.DrawText(e.Graphics,label,Theme.Small,new Rectangle(content.X,y,content.Width,D(23)),Theme.Muted,TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix); y+=D(25);
                        Rectangle bar=new Rectangle(content.X,y,content.Width,D(4));
                        using(SolidBrush track=new SolidBrush(Theme.Line)) e.Graphics.FillRectangle(track,bar);
                        bar.Width=(int)(bar.Width*Math.Max(0,Math.Min(100,file.Percent))/100.0);
                        using(SolidBrush fill=new SolidBrush(Theme.Blue)) e.Graphics.FillRectangle(fill,bar);
                        y+=D(14); fileHits.Add(Tuple.Create(new Rectangle(content.X,top,content.Width,y-top),record,file.Id));
                    }
                }
                TextRenderer.DrawText(e.Graphics, Stamp(record), Theme.Small, new Rectangle(content.X, r.Bottom - stamp.Height - D(9), content.Width, stamp.Height), Theme.Muted, TextFormatFlags.Right | MessageFormat);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus(); Point point = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
            selected = -1;
            for(int i=0;i<bounds.Count;i++) using(GraphicsPath path=BubblePath(bounds[i],D(Theme.BubbleRadius),D(7),records[i].Outgoing))
                if(path.IsVisible(point)) { selected=i;break; }
            Invalidate();
            if(e.Button==MouseButtons.Left) {var link=linkHits.FirstOrDefault(item=>item.Item1.Contains(e.Location));if(link!=null){MessageLinks.Open(this,link.Item2);return;}var hit=mediaHits.FirstOrDefault(item=>item.Item1.Contains(e.Location));if(hit!=null)MediaPreview.Open(this,hit.Item2);}
            if(e.Button==MouseButtons.Left) {
                var hit=fileHits.FirstOrDefault(item=>item.Item1.Contains(e.Location));
                if(hit!=null && !mediaHits.Any(item=>item.Item1.Contains(e.Location))) {
                    FileTransferState file=hit.Item2.Transfers.First(f=>f.Id==hit.Item3);
                    if(!String.IsNullOrEmpty(file.LocalPath)) MediaPreview.OpenFile(this,file.LocalPath,false);
                    else if(!hit.Item2.Outgoing && FileRequested!=null) FileRequested(hit.Item2,hit.Item3);
                }
            }
        }
        private void DrawCardPreview(Graphics g, Rectangle preview, string path)
        {
            using(SolidBrush background=new SolidBrush(Theme.Ice)) g.FillRectangle(background,preview);
            Bitmap bitmap;
            if(previews.TryGetValue(path,out bitmap)&&bitmap!=null) {
                double ratio=Math.Min((double)preview.Width/bitmap.Width,(double)preview.Height/bitmap.Height);int w=(int)(bitmap.Width*ratio),h=(int)(bitmap.Height*ratio);
                g.DrawImage(bitmap,new Rectangle(preview.X+(preview.Width-w)/2,preview.Y+(preview.Height-h)/2,w,h));
            } else {LoadPreview(path);TextRenderer.DrawText(g,"预览不可用 / 加载中",Theme.Small,preview,Theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
            mediaHits.Add(Tuple.Create(preview,path));
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor=linkHits.Any(h=>h.Item1.Contains(e.Location))?Cursors.Hand:Cursors.Default; }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e);if(records!=null&&bounds!=null&&textLayouts!=null){Arrange();Invalidate();} }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelected(); e.Handled = true; }
            if (e.KeyCode == Keys.End) AutoScrollPosition = new Point(0, AutoScrollMinSize.Height);
            if (e.KeyCode == Keys.Home) AutoScrollPosition = Point.Empty;
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
        protected override void Dispose(bool disposing) { if(disposing) {ChatWallpaper.Release(wallpaperImage);wallpaperImage=null;foreach(Bitmap bitmap in previews.Values) if(bitmap!=null)bitmap.Dispose();previews.Clear();} if (disposing && ContextMenuStrip != null) ContextMenuStrip.Dispose(); base.Dispose(disposing); }
    }
}
