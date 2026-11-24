using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FeiqLight
{
    internal sealed class TransferWindow : ModernWindow
    {
        private sealed class Item
        {
            internal ChatRecord Record;
            internal FileTransferState File;
            internal string Peer;
            internal string Meta;
            internal string MetaSuffix;
            internal string Fingerprint;
            internal string Key { get { return Record.PeerId + "/" + Record.Packet + "/" + Record.Outgoing + "/" + File.Id; } }
            internal string State { get { return File.DisplayStatus() ?? "等待接收"; } }
            internal bool Complete { get { return State == "已保存" || State == "对方已校验保存"; } }
            internal bool Ended { get { return Complete || State.Contains("已拒绝") || State.Contains("已取消发送") || State == "本机文件已删除"; } }
            internal bool Active { get { return State.Contains("中") && !State.Contains("中断") && !Ended; } }
            internal string StatusText
            {
                get
                {
                    string state = State;
                    if (File.BytesPerSecond > 0 && Active)
                    {
                        double seconds = File.Size * (1 - File.Percent / 100.0) / File.BytesPerSecond;
                        state += " · " + Theme.SizeText((long)File.BytesPerSecond) + "/s · 剩余约 " + (seconds >= 60 ? Math.Ceiling(seconds / 60) + " 分钟" : Math.Max(1, Math.Ceiling(seconds)) + " 秒");
                    }
                    return state;
                }
            }
            public override string ToString() { return File.Name + " · " + Peer + " · " + State; }
        }
        private sealed class TransferListBox : ListBox
        {
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x0014)
                {
                    // DrawItem 不透明地覆盖整行；擦除这些区域会让每次进度更新先闪出空白。
                    Rectangle blank = ClientRectangle;
                    if (Items.Count > 0)
                    {
                        int top = Math.Max(0, TopIndex);
                        int step = Math.Max(1, ItemHeight);
                        int lastVisible = Math.Min(Items.Count - 1, top + ClientSize.Height / step);
                        if (lastVisible >= 0)
                        {
                            int bottom = Math.Max(blank.Top, Math.Min(blank.Bottom, GetItemRectangle(lastVisible).Bottom));
                            blank = Rectangle.FromLTRB(blank.Left, bottom, blank.Right, blank.Bottom);
                        }
                    }
                    if (blank.Height > 0 && message.WParam != IntPtr.Zero)
                        using (Graphics graphics = Graphics.FromHdc(message.WParam))
                        using (var brush = new SolidBrush(BackColor)) graphics.FillRectangle(brush, blank);
                    message.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref message);
            }
        }
        private readonly MainForm main;
        private readonly ListBox list = new TransferListBox();
        private readonly Label summary = Theme.Label("正在读取传输记录…", Theme.Small, Theme.Muted);
        private readonly Label detail = Theme.Label("选择任务查看保存位置和操作", Theme.Small, Theme.Muted);
        private readonly Label empty = Theme.Label("暂无传输任务\n收到或发送的文件会显示在这里", Theme.Body, Theme.Muted);
        private readonly Button action, secondary, more;
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly Timer windowSaveTimer = new Timer { Interval = 500 };
        private readonly ToolTip tips = new ToolTip();
        private readonly ContextMenuStrip menu = Theme.Menu();
        private readonly TabStrip tabs = new TabStrip { Dock = DockStyle.Fill, Margin = Padding.Empty };
        private readonly RoundedButton[] filters;
        private readonly TextBox query;
        private readonly SearchBox search;
        private readonly Button searchButton;
        private readonly BufferedLayoutTable body;
        private readonly TransferSourceStrip sources = new TransferSourceStrip { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
        private string selectedSourceId = "";
        private bool searchExpanded;
        internal Func<DateTime> LocalNow = () => DateTime.Now;
        internal string SelectedSourceId { get { return selectedSourceId; } }
        private int filter;
        private string signature;
        private string localFileSignature;
        private bool localFileExists;
        private bool taskOperationPending;
        private Point? contextPoint;
        private bool windowLoaded, placementPending;
        private WindowPlacement placement;
        private FormWindowState refreshWindowState;
        internal bool RestoreMaximized { get { return placement != null && placement.Maximized; } }

        internal TransferWindow(MainForm main)
        {
            this.main = main; Text = "传输任务"; ClientSize = new Size(860, 640); MinimumSize = new Size(620, 440); UseSecondaryChrome();
            body = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(24, 12, 24, 16), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 80, 0, 42, 32, -1, 56 }) body.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
            Surface.Controls.Add(body);
            var sourceHeader = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            sourceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); sourceHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sources.SourceSelected += SelectSource; sourceHeader.Controls.Add(sources, 0, 0);
            searchButton = Theme.IconButton("search", "搜索", delegate { SetSearchExpanded(!searchExpanded); }, false); searchButton.Dock = DockStyle.Top; searchButton.Margin = new Padding(8, 6, 0, 0); searchButton.AccessibleDescription = "展开搜索"; tips.SetToolTip(searchButton, "搜索"); sourceHeader.Controls.Add(searchButton, 1, 0); body.Controls.Add(sourceHeader, 0, 0);
            search = new SearchBox { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Placeholder = "搜索文件或设备", Visible = false };
            query = search.Input; query.AccessibleName = "搜索传输任务";
            query.TextChanged += delegate { if (!searchExpanded && query.TextLength > 0) { query.Clear(); return; } RefreshRows(); }; body.Controls.Add(search, 0, 1);
            string[] labels = { "全部", "进行中", "待处理", "已完成" }; filters = new RoundedButton[labels.Length];
            for (int i = 0; i < labels.Length; i++) {
                int selected = i; filters[i] = (RoundedButton)Theme.Button(labels[i], delegate { filter = selected; UpdateTabs(); RefreshRows(); }, false);
                filters[i].Size = new Size(100, 40); filters[i].Margin = Padding.Empty; tabs.Add(filters[i]);
            }
            body.Controls.Add(tabs, 0, 2); summary.Dock = DockStyle.Fill; summary.AutoEllipsis = true; body.Controls.Add(summary, 0, 3);
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 96;
            list.IntegralHeight = false; list.AccessibleName = "传输任务列表"; list.DrawItem += DrawItem; list.SelectedIndexChanged += delegate { localFileSignature = null; UpdateActions(); };
            list.DoubleClick += delegate { RunAction(); }; list.KeyDown += (sender, e) => { if(e.KeyCode==Keys.Enter){e.Handled=true;RunAction();} };
            list.MouseDown += (sender,e) => {
                if(e.Button!=MouseButtons.Right)return;
                int index=list.IndexFromPoint(e.Location);if(index<0)return;
                list.SelectedIndex=index;contextPoint=e.Location;ShowActions();
            };
            Theme.ThemeScrollbars(list);var listFrame=new Panel { Dock=DockStyle.Fill,Margin=Padding.Empty };listFrame.Controls.Add(list);empty.Dock=DockStyle.Fill;empty.TextAlign=ContentAlignment.MiddleCenter;listFrame.Controls.Add(empty);empty.BringToFront();body.Controls.Add(listFrame, 0, 4);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            detail.Dock = DockStyle.Fill; detail.AutoEllipsis = true; footer.Controls.Add(detail, 0, 0);
            action = Theme.Button("继续接收", delegate { RunAction(); }, true); action.Dock = DockStyle.Fill; action.Margin = new Padding(8, 0, 4, 0); footer.Controls.Add(action, 1, 0);
            secondary = Theme.Button("打开文件", delegate { RunSecondary(); }, false); secondary.Dock = DockStyle.Fill; secondary.Margin = new Padding(4, 0, 4, 0); footer.Controls.Add(secondary, 2, 0);
            more = Theme.IconButton("more", "任务操作", delegate { contextPoint=null;ShowActions(); }, false); more.Dock = DockStyle.Fill; more.Margin = Padding.Empty; footer.Controls.Add(more, 3, 0); body.Controls.Add(footer, 0, 5);
            Shown += async delegate { ApplyColors();try { await main.LoadTransfers(); if(!IsDisposed)RefreshRows(); } catch(Exception error){if(!IsDisposed)summary.Text="读取失败："+error.Message;} };
            Theme.Changed += ApplyColors;
            Surface.SizeChanged += delegate { LayoutSections(); };
            timer.Tick += delegate { if (Visible && WindowState != FormWindowState.Minimized) RefreshRows(); };
            windowSaveTimer.Tick += delegate { windowSaveTimer.Stop(); SaveWindowPlacement(); };
            LocationChanged += TrackWindowPlacement; SizeChanged += TrackWindowPlacement;
            VisibleChanged += delegate { if (Visible && WindowState != FormWindowState.Minimized) { localFileSignature = null; RefreshRows(); TrackWindowPlacement(this, EventArgs.Empty); } };
            SizeChanged += delegate
            {
                bool restored = refreshWindowState == FormWindowState.Minimized && WindowState != FormWindowState.Minimized;
                refreshWindowState = WindowState;
                if (restored && Visible) RefreshRows();
            };
            timer.Start(); UpdateTabs(); ApplyColors();
        }
        internal void SelectSource(string id)
        {
            selectedSourceId = id ?? "";
            RefreshRows();
            sources.EnsureSelectedVisible();
        }
        private void SetSearchExpanded(bool expanded)
        {
            if (searchExpanded != expanded)
            {
                searchExpanded = expanded;
                if (!expanded) query.Clear();
                search.Visible = expanded;
                LayoutSections();
                searchButton.AccessibleDescription = expanded ? "收起搜索" : "展开搜索";
            }
            if (expanded) query.Focus();
            else searchButton.Focus();
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { SetSearchExpanded(true); return true; }
            if (keyData == Keys.Escape && searchExpanded) { SetSearchExpanded(false); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        internal static string FormatTime(DateTime time, DateTime now)
        {
            if (time.Year == 1 || time.ToUniversalTime() == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)) return "";
            DateTime local = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
            string format = local.Date == now.Date ? "HH:mm" : local.Year == now.Year ? "MM-dd HH:mm" : "yyyy-MM-dd HH:mm";
            return local.ToString(format, CultureInfo.InvariantCulture);
        }
        private void UpdateTabs() { tabs.Active=filter;for(int i=0;i<filters.Length;i++)filters[i].Active=i==filter; }
        private void ApplyColors() { list.BackColor=empty.BackColor=Theme.Surface;list.ForeColor=Theme.Ink;summary.ForeColor=detail.ForeColor=empty.ForeColor=Theme.Muted;using(Graphics g=list.CreateGraphics())list.ItemHeight=(int)(100*g.DpiX/96F);LayoutSections();list.Invalidate(); }
        private void LayoutSections()
        {
            if(body==null||!body.IsHandleCreated)return;
            float scale;using(Graphics graphics=list.CreateGraphics())scale=Math.Max(DeviceDpi,graphics.DpiX)/96F;
            int sourceHeight=(int)Math.Round(76*scale);
            bool compact=Surface.ClientSize.Height-sourceHeight-(searchExpanded?46:0)-42-32-56-28<list.ItemHeight;
            // 最小高度优先保留一整行；收紧固定区间距，底栏仍保留四列与原操作。
            var padding=new Padding(24,compact?6:12,24,compact?6:16);
            int[] heights={sourceHeight,searchExpanded?(compact?38:46):0,compact?34:42,compact?24:32,-1,compact?44:56};
            body.SuspendLayout();
            if(body.Padding!=padding)body.Padding=padding;
            for(int i=0;i<heights.Length;i++)if(heights[i]>=0&&body.RowStyles[i].Height!=heights[i])body.RowStyles[i].Height=heights[i];
            body.ResumeLayout(true);
        }
        private void RefreshRows()
        {
            Item previous=list.SelectedItem as Item;
            // 历史反序列化保留 UTC，当前会话使用本地时间；先归到同一时区再比较最近记录。
            var records=main.TransferRecords.OrderByDescending(r=>r.Time.Kind==DateTimeKind.Utc?r.Time.ToLocalTime():r.Time).ThenByDescending(r=>r.Packet).ThenBy(r=>r.PeerId,StringComparer.Ordinal).ToArray();
            var sourceItems=records.Where(record=>record.Transfers!=null&&record.Transfers.Length>0).GroupBy(record=>record.PeerId,StringComparer.Ordinal).Select(group=>
            {
                ChatRecord latest=group.First(); Peer peer=main.TransferPeer(latest.PeerId);
                return new TransferSourceStrip.Source { Id=latest.PeerId,Name=latest.PeerId=="browser-guests"?"浏览器访客":peer==null?latest.Sender:peer.DisplayName,Avatar=peer==null?null:peer.Avatar,Glyph=latest.PeerId=="browser-guests"?"link":"",LatestTime=latest.Time.Kind==DateTimeKind.Utc?latest.Time.ToLocalTime():latest.Time };
            }).OrderByDescending(source=>source.LatestTime).ThenBy(source=>source.Name,StringComparer.Ordinal).ThenBy(source=>source.Id,StringComparer.Ordinal).ToList();
            if(selectedSourceId.Length>0&&!sourceItems.Any(source=>source.Id==selectedSourceId))
            {
                Peer peer=main.TransferPeer(selectedSourceId); TransferSourceStrip.Source previousSource=sources.Items.FirstOrDefault(source=>source.Id==selectedSourceId);
                sourceItems.Insert(0,new TransferSourceStrip.Source { Id=selectedSourceId,Name=selectedSourceId=="browser-guests"?"浏览器访客":peer==null?(previousSource==null?selectedSourceId:previousSource.Name):peer.DisplayName,Avatar=peer==null?null:peer.Avatar,Glyph=selectedSourceId=="browser-guests"?"link":"" });
            }
            sourceItems.Insert(0,new TransferSourceStrip.Source { Id="",Name="全部",Glyph="transfer" }); sources.SetItems(sourceItems.ToArray(),selectedSourceId);
            DateTime now=LocalNow();
            var rows=records.SelectMany(r=>(r.Transfers??new FileTransferState[0]).Select(f=>new Item{Record=r,File=f,Peer=main.TransferPeer(r.PeerId)==null?r.Sender:main.TransferPeer(r.PeerId).DisplayName}))
                .Where(i=>selectedSourceId.Length==0||i.Record.PeerId==selectedSourceId)
                .Where(i=>filter==0||filter==1&&i.Active||filter==2&&!i.Complete&&!i.Active||filter==3&&i.Complete)
                .Where(i=>(i.File.Name+" "+i.Peer).IndexOf(query.Text.Trim(),StringComparison.OrdinalIgnoreCase)>=0).ToArray();
            // 文件状态会在原对象上变化，因此缓存字符串快照，而不是对比共享对象的当前字段。
            foreach (Item row in rows)
            {
                string time=FormatTime(row.Record.Time,now);
                row.MetaSuffix=" · "+Theme.SizeText(row.File.Size)+(time.Length>0?" · "+time:"");
                row.Meta=(row.Record.Outgoing?"发送给 ":"来自 ")+row.Peer+row.MetaSuffix;
                row.Fingerprint = String.Join("\0", new[] { row.Key, row.File.Name, row.File.Size.ToString(), row.Peer, row.Meta, row.State, row.File.Percent.ToString(), row.StatusText, row.File.LocalPath, row.File.Error });
            }
            string next=String.Join("|",rows.Select(i=>i.Key));
            if(signature!=next) {
                signature=next;int top=list.TopIndex;list.BeginUpdate();list.Items.Clear();list.Items.AddRange(rows);
                if(previous!=null)list.SelectedIndex=Array.FindIndex(rows,i=>i.Key==previous.Key);
                if(list.Items.Count>0)list.TopIndex=Math.Max(0,Math.Min(top,list.Items.Count-1));list.EndUpdate();
            }
            else for (int i = 0; i < rows.Length; i++)
            {
                Item item = (Item)list.Items[i];
                if (item.Fingerprint == rows[i].Fingerprint) continue;
                item.Record = rows[i].Record; item.File = rows[i].File; item.Peer = rows[i].Peer; item.Meta = rows[i].Meta; item.MetaSuffix = rows[i].MetaSuffix; item.Fingerprint = rows[i].Fingerprint;
                list.Invalidate(list.GetItemRectangle(i));
            }
            if(list.Items.Count>0&&list.SelectedIndex<0)list.SelectedIndex=0;
            bool emptyVisible = rows.Length == 0;
            string emptyText = String.IsNullOrEmpty(query.Text) ? "暂无传输任务\n收到或发送的文件会显示在这里" : "没有匹配的文件或设备";
            if (empty.Visible != emptyVisible) empty.Visible = emptyVisible;
            if (empty.Text != emptyText) empty.Text = emptyText;
            double total=rows.Sum(i=>(double)i.File.Size),done=rows.Sum(i=>i.File.Size*(i.Complete?1:Math.Max(0,Math.Min(100,i.File.Percent))/100.0));
            string summaryText = rows.Length+" 个文件 · "+rows.Count(i=>i.Complete)+" 个已完成"+(total>0?" · "+Theme.SizeText((long)done)+" / "+Theme.SizeText((long)total):"");
            if (summary.Text != summaryText) summary.Text = summaryText;
            UpdateActions();
        }
        private void DrawItem(object sender,DrawItemEventArgs e)
        {
            if(e.Index<0||e.Index>=list.Items.Count)return;Item item=(Item)list.Items[e.Index];Graphics g=e.Graphics;float s=g.DpiX/96F;
            bool selected=(e.State&DrawItemState.Selected)!=0;
            using(var brush=new SolidBrush(selected?Theme.Sky:Theme.Surface))g.FillRectangle(brush,e.Bounds);
            int x=e.Bounds.Left+(int)(12*s),y=e.Bounds.Top+(int)(12*s),icon=(int)(40*s),left=x+icon+(int)(14*s),width=e.Bounds.Right-left-(int)(16*s);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using(var path=Theme.Rounded(new Rectangle(x,y,icon,icon),10))using(var brush=new SolidBrush(Theme.Ice))g.FillPath(brush,path);
            Theme.Glyph(g,"file",new Rectangle(x+(int)(9*s),y+(int)(9*s),(int)(22*s),(int)(22*s)),Theme.Blue);
            var flags=TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter;
            TextRenderer.DrawText(g,item.File.Name,Theme.Strong,new Rectangle(left,y,width,(int)(24*s)),Theme.Ink,flags);
            // 给大小和时间保留完整尾部，超长联系人名称单独省略，避免日期随整行省略消失。
            string metaPrefix=(item.Record.Outgoing?"发送给 ":"来自 ")+item.Peer;
            var metaFlags=flags|TextFormatFlags.NoPadding;
            var measureFlags=TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
            int metaLeft=left+(int)(3*s),metaWidth=Math.Max(0,width-(int)(3*s));
            int suffixWidth=Math.Min(metaWidth,TextRenderer.MeasureText(g,item.MetaSuffix,Theme.Small,Size.Empty,measureFlags).Width);
            int prefixWidth=Math.Min(Math.Max(0,metaWidth-suffixWidth),TextRenderer.MeasureText(g,metaPrefix,Theme.Small,Size.Empty,measureFlags).Width);
            if(prefixWidth>0)TextRenderer.DrawText(g,metaPrefix,Theme.Small,new Rectangle(metaLeft,y+(int)(26*s),prefixWidth,(int)(20*s)),Theme.Muted,metaFlags);
            TextRenderer.DrawText(g,item.MetaSuffix,Theme.Small,new Rectangle(metaLeft+prefixWidth,y+(int)(26*s),suffixWidth,(int)(20*s)),Theme.Muted,measureFlags|TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g,item.StatusText,Theme.Small,new Rectangle(left,y+(int)(48*s),width,(int)(20*s)),item.Complete?Theme.Online:item.Active?Theme.Blue:Theme.Muted,flags);
            if(!item.Ended) {
                Rectangle bar=new Rectangle(left,e.Bounds.Bottom-(int)(10*s),width,Math.Max(2,(int)(3*s)));
                using(var brush=new SolidBrush(Theme.Line))g.FillRectangle(brush,bar);
                bar.Width=(int)(width*Math.Max(0,Math.Min(100,item.File.Percent))/100.0);using(var brush=new SolidBrush(Theme.Blue))g.FillRectangle(brush,bar);
            }
        }
        private void UpdateActions()
        {
            Item item = list.SelectedItem as Item;
            bool actionVisible = item != null, moreVisible = item != null;
            if (action.Visible != actionVisible) action.Visible = actionVisible;
            if (more.Visible != moreVisible) more.Visible = moreVisible;
            bool secondaryVisible=false;
            string detailText = "选择任务查看保存位置和操作";
            if (item != null)
            {
                string nextLocalSignature = String.Join("\0", new[] { signature, item.Key, item.File.LocalPath, item.State });
                // 定时进度刷新不重复访问磁盘；选中项、列表、路径或任务状态改变时重新核对。
                if (localFileSignature != nextLocalSignature)
                {
                    localFileSignature = nextLocalSignature;
                    localFileExists = !String.IsNullOrEmpty(item.File.LocalPath) && File.Exists(item.File.LocalPath);
                }
                bool local = localFileExists;
                bool receiving = main.ReceivingTask(item.Record, item.File.Id);
                string actionText = local ? "打开文件位置" : receiving ? "暂停接收" : item.Record.Outgoing ? "查看会话" : "继续接收";
                bool actionEnabled = local || receiving || item.Record.Outgoing || !item.Ended;
                if (action.Text != actionText) action.Text = actionText;
                if (action.Enabled != actionEnabled) action.Enabled = actionEnabled;
                string secondaryText=null;
                if(item.Record.Outgoing&&main.CanCancelTransfer(item.Record))secondaryText="取消发送";
                else if(local)secondaryText="打开文件";
                else if(!item.Record.Outgoing&&!item.Ended)secondaryText=receiving?"取消接收":"拒绝";
                secondaryVisible=secondaryText!=null;
                if(secondaryVisible)
                {
                    if(secondary.Text!=secondaryText)secondary.Text=secondaryText;
                    Color color=secondaryText=="打开文件"?Theme.Ink:Theme.Danger;if(secondary.ForeColor!=color)secondary.ForeColor=color;
                    bool enabled=!taskOperationPending;if(secondary.Enabled!=enabled)secondary.Enabled=enabled;
                }
                detailText = local ? item.File.LocalPath : String.IsNullOrEmpty(item.File.Error) ? item.State : item.File.Error;
                if (!local && item.Complete && !item.Record.Outgoing && !String.IsNullOrEmpty(item.File.LocalPath)) detailText = "本机文件已移动或删除：" + item.File.LocalPath;
            }
            if(secondary.Visible!=secondaryVisible)secondary.Visible=secondaryVisible;
            if (detail.Text != detailText) detail.Text = detailText;
            string tooltip = item == null ? "" : detailText;
            if (tips.GetToolTip(detail) != tooltip) tips.SetToolTip(detail, tooltip);
        }
        private async void RunAction()
        {
            Item item=list.SelectedItem as Item;if(item==null)return;
            try {
                if(!String.IsNullOrEmpty(item.File.LocalPath)&&File.Exists(item.File.LocalPath))BubbleHistory.RevealFile(this,item.File.LocalPath);
                else if(item.Record.Outgoing)await ShowConversation(item);
                else if(!item.Ended)main.TransferAction(item.Record,item.File.Id,main.ReceivingTask(item.Record,item.File.Id));
            }catch(Exception error){if(IsDisposed||Disposing)main.FileReceiveStatus("任务未完成："+error.Message);else using(var notice=new NoticeDialog("任务未完成",error.Message))notice.ShowDialog(this);}
            if(!IsDisposed){localFileSignature = null; RefreshRows();}
        }
        private async Task ShowConversation(Item item)
        {
            Peer peer=main.TransferPeer(item.Record.PeerId);
            if(peer==null)return;
            main.Restore();main.OpenChat(peer);
            await main.ActiveChat.LocateMessageAsync(item.Record);
        }
        private async void RunSecondary()
        {
            Item item=list.SelectedItem as Item;if(item==null||taskOperationPending)return;
            try
            {
                if(item.Record.Outgoing&&main.CanCancelTransfer(item.Record))main.CancelTransfer(item.Record);
                else if(!String.IsNullOrEmpty(item.File.LocalPath)&&File.Exists(item.File.LocalPath))MediaPreview.OpenFile(this,item.File.LocalPath,false);
                else if(!item.Record.Outgoing&&!item.Ended)await RunTaskOperation(item,!main.ReceivingTask(item.Record,item.File.Id));
            }
            catch(Exception error)
            {
                if(IsDisposed||Disposing)main.FileReceiveStatus("任务未完成："+error.Message);
                else using(var notice=new NoticeDialog("任务未完成",error.Message))notice.ShowDialog(this);
            }
            if(!IsDisposed){localFileSignature=null;RefreshRows();}
        }
        private async Task RunTaskOperation(Item item,bool reject)
        {
            if(taskOperationPending)return;
            // 模态循环结束后线程可能没有同步上下文；await 之后的界面收尾必须回到 UI 线程。
            if(!InvokeRequired&&System.Threading.SynchronizationContext.Current==null)System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            taskOperationPending=true;secondary.Enabled=false;
            try
            {
                if(reject)
                {
                    using(var confirm=new ConfirmDialog("拒绝这批文件？","将拒绝这条消息中尚未保存的附件，并清理接收断点；已保存文件和聊天记录保留。","拒绝",true))
                        if(confirm.ShowDialog(this)!=DialogResult.OK)return;
                    await main.RejectTransfer(item.Record);
                }
                else await main.CancelReceiveTask(item.Record,item.File.Id);
            }
            catch(Exception error)
            {
                // 等待下载线程退出期间窗口可能已关闭；已释放的窗口不能再作弹窗宿主，否则未处理异常会结束进程。
                if(IsDisposed||Disposing)main.FileReceiveStatus("任务未完成："+error.Message);
                else using(var notice=new NoticeDialog("任务未完成",error.Message))notice.ShowDialog(this);
            }
            finally{taskOperationPending=false;if(!IsDisposed){localFileSignature=null;RefreshRows();}}
        }
        private void ShowActions()
        {
            foreach(ToolStripItem entry in menu.Items.Cast<ToolStripItem>().ToArray())entry.Dispose();menu.Items.Clear();Item item=list.SelectedItem as Item;if(item==null)return;
            bool local=!String.IsNullOrEmpty(item.File.LocalPath)&&File.Exists(item.File.LocalPath);
            if(local)
            {
                menu.Items.Add("打开文件",null,delegate {MediaPreview.OpenFile(this,item.File.LocalPath,false);});
                menu.Items.Add("打开方式…",null,delegate {MediaPreview.OpenFile(this,item.File.LocalPath,true);});
                menu.Items.Add("打开文件位置",null,delegate {BubbleHistory.RevealFile(this,item.File.LocalPath);});
                if(!item.Record.Outgoing)menu.Items.Add("删除本机文件",null,delegate {main.DeleteTransferFile(item.Record,item.File.LocalPath);localFileSignature=null;if(!IsDisposed)RefreshRows();}).ForeColor=Theme.Danger;
            }
            else if(!item.Record.Outgoing&&!item.Ended) {
                bool receiving=main.ReceivingTask(item.Record,item.File.Id);
                menu.Items.Add(receiving?"暂停并保留断点":"继续接收",null,delegate {RunTransferAction(item,receiving);});
                menu.Items.Add("取消接收并清理断点",null,async delegate {await RunTaskOperation(item,false);}).ForeColor=Theme.Danger;
                menu.Items.Add("拒绝这批文件",null,async delegate {await RunTaskOperation(item,true);}).ForeColor=Theme.Danger;
                menu.Items.Add("继续当前列表中的接收",null,delegate {foreach(Item pending in list.Items.Cast<Item>().Where(i=>!i.Record.Outgoing&&!i.Ended&&!main.ReceivingTask(i.Record,i.File.Id)).ToArray())if(!RunTransferAction(pending,false))break;});
                menu.Items.Add("暂停当前列表中的接收",null,delegate {foreach(Item pending in list.Items.Cast<Item>().Where(i=>!i.Record.Outgoing&&!i.Ended).ToArray())if(!RunTransferAction(pending,true))break;});
            }
            else if(!item.Record.Outgoing&&item.State=="本机文件已删除"&&item.Record.PeerId!="browser-guests")menu.Items.Add("重新接收",null,delegate {RunTransferAction(item,false);});
            if(item.Record.PeerId!="browser-guests")menu.Items.Add("查看所在会话",null,async delegate {try{await ShowConversation(item);}catch(Exception error){if(IsDisposed||Disposing)main.FileReceiveStatus("无法定位："+error.Message);else using(var notice=new NoticeDialog("无法定位",error.Message))notice.ShowDialog(this);}});
            if(main.CanCancelTransfer(item.Record))menu.Items.Add("取消这批发送",null,delegate {try{main.CancelTransfer(item.Record);}catch(Exception error){using(var notice=new NoticeDialog("取消未完成",error.Message))notice.ShowDialog(this);}}).ForeColor=Theme.Danger;
            Point? point=contextPoint;contextPoint=null;
            if(menu.Items.Count==0)return;
            if(point.HasValue)menu.Show(list,point.Value);
            else menu.Show(more,new Point(more.Width,0),ToolStripDropDownDirection.AboveLeft);
        }
        private bool RunTransferAction(Item item,bool pause)
        {
            try {main.TransferAction(item.Record,item.File.Id,pause);return true;}
            catch(Exception error){using(var notice=new NoticeDialog("任务未完成",error.Message))notice.ShowDialog(this);return false;}
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            WindowPlacement saved = main.TransferWindowPlacement;
            placement = saved;
            if (saved != null && saved.Width > 0 && saved.Height > 0)
            {
                Rectangle requested = new Rectangle(Math.Max(-100000, Math.Min(100000, saved.X)), Math.Max(-100000, Math.Min(100000, saved.Y)), Math.Min(100000, saved.Width), Math.Min(100000, saved.Height));
                Rectangle area = Screen.FromRectangle(requested).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
                int width = Math.Min(area.Width, Math.Max(MinimumSize.Width, requested.Width));
                int height = Math.Min(area.Height, Math.Max(MinimumSize.Height, requested.Height));
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(Math.Max(area.Left, Math.Min(requested.X, area.Right - width)), Math.Max(area.Top, Math.Min(requested.Y, area.Bottom - height)), width, height);
                if (saved.Maximized) WindowState = FormWindowState.Maximized;
            }
            windowLoaded = true;
            TrackWindowPlacement(this, EventArgs.Empty);
        }
        private void TrackWindowPlacement(object sender, EventArgs e)
        {
            if (!windowLoaded || !Visible || WindowState == FormWindowState.Minimized) return;
            Rectangle normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (normal.Width <= 0 || normal.Height <= 0) return;
            bool maximized = WindowState == FormWindowState.Maximized;
            if (placement != null && placement.X == normal.X && placement.Y == normal.Y && placement.Width == normal.Width && placement.Height == normal.Height && placement.Maximized == maximized) return;
            placement = new WindowPlacement { X = normal.X, Y = normal.Y, Width = normal.Width, Height = normal.Height, Maximized = maximized };
            placementPending = true;
            windowSaveTimer.Stop(); windowSaveTimer.Start();
        }
        private void SaveWindowPlacement()
        {
            if (!windowLoaded || placement == null || !placementPending || main.IsDisposed || main.Disposing) return;
            main.SaveTransferWindowPlacement(placement);
            placementPending = false;
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            TrackWindowPlacement(this, EventArgs.Empty); windowSaveTimer.Stop();
            if (placementPending && !main.IsDisposed && !main.Disposing) SaveWindowPlacement();
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                windowSaveTimer.Stop(); if (placementPending && !main.IsDisposed && !main.Disposing) SaveWindowPlacement(); windowSaveTimer.Dispose();
                Theme.Changed -= ApplyColors; timer.Dispose(); tips.Dispose(); menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
