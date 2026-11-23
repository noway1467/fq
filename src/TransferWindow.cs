using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
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
        private readonly Button action, more;
        private readonly Timer timer = new Timer { Interval = 500 };
        private readonly Timer windowSaveTimer = new Timer { Interval = 500 };
        private readonly ToolTip tips = new ToolTip();
        private readonly ContextMenuStrip menu = Theme.Menu();
        private readonly TabStrip tabs = new TabStrip { Dock = DockStyle.Fill, Margin = Padding.Empty };
        private readonly RoundedButton[] filters;
        private readonly TextBox query;
        private int filter;
        private string signature;
        private string localFileSignature;
        private bool localFileExists;
        private bool windowLoaded, placementPending;
        private WindowPlacement placement;
        private FormWindowState refreshWindowState;
        internal bool RestoreMaximized { get { return placement != null && placement.Maximized; } }

        internal TransferWindow(MainForm main)
        {
            this.main = main; Text = "传输任务"; ClientSize = new Size(860, 640); MinimumSize = new Size(620, 440); UseSecondaryChrome();
            var body = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24, 12, 24, 16), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 46, 42, 32, -1, 56 }) body.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
            Surface.Controls.Add(body);
            var search = new SearchBox { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Placeholder = "搜索文件或设备" };
            query = search.Input; query.AccessibleName = "搜索传输任务"; query.TextChanged += delegate { RefreshRows(); }; body.Controls.Add(search, 0, 0);
            string[] labels = { "全部", "进行中", "待处理", "已完成" }; filters = new RoundedButton[labels.Length];
            for (int i = 0; i < labels.Length; i++) {
                int selected = i; filters[i] = (RoundedButton)Theme.Button(labels[i], delegate { filter = selected; UpdateTabs(); RefreshRows(); }, false);
                filters[i].Size = new Size(100, 40); filters[i].Margin = Padding.Empty; tabs.Add(filters[i]);
            }
            body.Controls.Add(tabs, 0, 1); summary.Dock = DockStyle.Fill; summary.AutoEllipsis = true; body.Controls.Add(summary, 0, 2);
            list.Dock = DockStyle.Fill; list.BorderStyle = BorderStyle.None; list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 96;
            list.IntegralHeight = false; list.AccessibleName = "传输任务列表"; list.DrawItem += DrawItem; list.SelectedIndexChanged += delegate { localFileSignature = null; UpdateActions(); };
            list.DoubleClick += delegate { RunAction(); }; list.KeyDown += (sender, e) => { if(e.KeyCode==Keys.Enter){e.Handled=true;RunAction();} };
            Theme.ThemeScrollbars(list);var listFrame=new Panel { Dock=DockStyle.Fill,Margin=Padding.Empty };listFrame.Controls.Add(list);empty.Dock=DockStyle.Fill;empty.TextAlign=ContentAlignment.MiddleCenter;listFrame.Controls.Add(empty);empty.BringToFront();body.Controls.Add(listFrame, 0, 3);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            detail.Dock = DockStyle.Fill; detail.AutoEllipsis = true; footer.Controls.Add(detail, 0, 0);
            action = Theme.Button("继续接收", delegate { RunAction(); }, true); action.Dock = DockStyle.Fill; action.Margin = new Padding(8, 0, 4, 0); footer.Controls.Add(action, 1, 0);
            more = Theme.IconButton("more", "任务操作", delegate { ShowActions(); }, false); more.Dock = DockStyle.Fill; more.Margin = Padding.Empty; footer.Controls.Add(more, 2, 0); body.Controls.Add(footer, 0, 4);
            Shown += async delegate { ApplyColors();try { await main.LoadTransfers(); if(!IsDisposed)RefreshRows(); } catch(Exception error){if(!IsDisposed)summary.Text="读取失败："+error.Message;} };
            Theme.Changed += ApplyColors;
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
        private void UpdateTabs() { tabs.Active=filter;for(int i=0;i<filters.Length;i++)filters[i].Active=i==filter; }
        private void ApplyColors() { list.BackColor=empty.BackColor=Theme.Surface;list.ForeColor=Theme.Ink;summary.ForeColor=detail.ForeColor=empty.ForeColor=Theme.Muted;using(Graphics g=list.CreateGraphics())list.ItemHeight=(int)(100*g.DpiX/96F);list.Invalidate(); }
        private void RefreshRows()
        {
            Item previous=list.SelectedItem as Item;
            var rows=main.TransferRecords.OrderByDescending(r=>r.Time).ThenByDescending(r=>r.Packet).ThenBy(r=>r.PeerId,StringComparer.Ordinal).SelectMany(r=>(r.Transfers??new FileTransferState[0]).Select(f=>new Item{Record=r,File=f,Peer=main.TransferPeer(r.PeerId)==null?r.Sender:main.TransferPeer(r.PeerId).DisplayName}))
                .Where(i=>filter==0||filter==1&&i.Active||filter==2&&!i.Complete&&!i.Active||filter==3&&i.Complete)
                .Where(i=>(i.File.Name+" "+i.Peer).IndexOf(query.Text.Trim(),StringComparison.OrdinalIgnoreCase)>=0).ToArray();
            // 文件状态会在原对象上变化，因此缓存字符串快照，而不是对比共享对象的当前字段。
            foreach (Item row in rows)
                row.Fingerprint = String.Join("\0", new[] { row.Key, row.File.Name, row.File.Size.ToString(), row.Peer, row.State, row.File.Percent.ToString(), row.StatusText, row.File.LocalPath, row.File.Error });
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
                item.Record = rows[i].Record; item.File = rows[i].File; item.Peer = rows[i].Peer; item.Fingerprint = rows[i].Fingerprint;
                list.Invalidate(list.GetItemRectangle(i));
            }
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
            string meta=(item.Record.Outgoing?"发送给 ":"来自 ")+item.Peer+" · "+Theme.SizeText(item.File.Size);
            TextRenderer.DrawText(g,meta,Theme.Small,new Rectangle(left,y+(int)(26*s),width,(int)(20*s)),Theme.Muted,flags);
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
            bool actionVisible = item != null, moreVisible = item != null && item.Record.PeerId != "browser-guests";
            if (action.Visible != actionVisible) action.Visible = actionVisible;
            if (more.Visible != moreVisible) more.Visible = moreVisible;
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
                detailText = local ? item.File.LocalPath : String.IsNullOrEmpty(item.File.Error) ? item.State : item.File.Error;
            }
            if (detail.Text != detailText) detail.Text = detailText;
            string tooltip = item == null ? "" : detailText;
            if (tips.GetToolTip(detail) != tooltip) tips.SetToolTip(detail, tooltip);
        }
        private void RunAction()
        {
            Item item=list.SelectedItem as Item;if(item==null)return;
            try {
                if(!String.IsNullOrEmpty(item.File.LocalPath)&&File.Exists(item.File.LocalPath))System.Diagnostics.Process.Start("explorer.exe","/select,\""+item.File.LocalPath+"\"");
                else if(item.Record.Outgoing){Peer peer=main.TransferPeer(item.Record.PeerId);if(peer!=null){main.Restore();main.OpenChat(peer);}}
                else if(!item.Ended)main.TransferAction(item.Record,item.File.Id,main.ReceivingTask(item.Record,item.File.Id));
            }catch(Exception error){using(var notice=new NoticeDialog("任务未完成",error.Message))notice.ShowDialog(this);}
            localFileSignature = null; RefreshRows();
        }
        private void ShowActions()
        {
            foreach(ToolStripItem entry in menu.Items.Cast<ToolStripItem>().ToArray())entry.Dispose();menu.Items.Clear();Item item=list.SelectedItem as Item;if(item==null)return;
            menu.Items.Add("查看所在会话",null,delegate {Peer peer=main.TransferPeer(item.Record.PeerId);if(peer!=null){main.Restore();main.OpenChat(peer);}});
            if(main.CanCancelTransfer(item.Record))menu.Items.Add("取消这批发送",null,delegate {try{main.CancelTransfer(item.Record);}catch(Exception error){using(var notice=new NoticeDialog("取消未完成",error.Message))notice.ShowDialog(this);}});
            if(!item.Record.Outgoing&&!item.Ended) {
                menu.Items.Add("暂停并保留断点",null,delegate {RunTransferAction(item,true);});
                menu.Items.Add("继续所有未完成接收",null,delegate {foreach(Item pending in list.Items.Cast<Item>().Where(i=>!i.Record.Outgoing&&!i.Ended&&!i.Active).ToArray())if(!RunTransferAction(pending,false))break;});
                menu.Items.Add("暂停当前列表中的接收",null,delegate {foreach(Item pending in list.Items.Cast<Item>().Where(i=>!i.Record.Outgoing&&!i.Ended).ToArray())if(!RunTransferAction(pending,true))break;});
            }
            menu.Show(more,new Point(more.Width,0),ToolStripDropDownDirection.AboveLeft);
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
