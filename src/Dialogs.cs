using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace FeiqLight
{
    public sealed class NoticeDialog : ModernWindow
    {
        public NoticeDialog(string title, string text)
        {
            Text = title; ClientSize = new Size(420, 245); MinimumSize = new Size(380, 200); ResizeWindow = false; StartPosition = FormStartPosition.CenterParent;
            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 8, 24, 10) };
            RichTextBox details = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Text = text,
                Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Ink, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, AccessibleName = "提示详情" };
            Theme.AttachEditMenu(details); body.Controls.Add(details);
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(18, 8, 18, 10) }; Button done = Theme.Button("确定", delegate { DialogResult = DialogResult.OK; }, true); done.Dock = DockStyle.Right; footer.Controls.Add(done); Surface.Controls.Add(body); Surface.Controls.Add(footer); AcceptButton = done;
        }
    }
    public sealed class ConversationDialog : ModernWindow
    {
        public string Note { get { return note.Text.Trim(); } }
        public bool DeleteHistory { get { return history.Checked; } }
        private readonly TextBox note;
        private readonly ToggleSwitch history;
        public ConversationDialog(Peer peer, bool deleting, bool clearOnly = false)
        {
            Text=clearOnly?"清空聊天记录":deleting?"删除联系人":"设置备注"; ClientSize=new Size(460,290); MinimumSize=new Size(420,290); ResizeWindow=false; StartPosition=FormStartPosition.CenterParent;
            Label hint=Theme.Label(deleting?"仅影响本机，不删除已接收的文件。\n删除列表不是屏蔽；新消息会恢复会话入口。":"仅在本机显示；留空恢复对方昵称。",Theme.Body,Theme.Muted); hint.SetBounds(24,12,410,62); Surface.Controls.Add(hint);
            RoundedInput input=new RoundedInput(); input.SetBounds(24,82,410,44); note=input.Input;note.MaxLength=64;note.Text=peer.Note??"";note.AccessibleName="联系人备注"; input.Visible=!deleting; Surface.Controls.Add(input);
            history=new ToggleSwitch {Text="同时删除聊天记录",Checked=clearOnly,Enabled=!clearOnly,Visible=deleting,AccessibleName="同时删除聊天记录"};history.SetBounds(24,84,410,48);Surface.Controls.Add(history);
            Button cancel=Theme.Button("取消",delegate{DialogResult=DialogResult.Cancel;},false);cancel.SetBounds(232,190,92,40);Surface.Controls.Add(cancel);CancelButton=cancel;
            Button done=Theme.Button(deleting?"确认删除":"保存",delegate{DialogResult=DialogResult.OK;},true);done.SetBounds(338,190,96,40);Surface.Controls.Add(done);AcceptButton=done;
        }
    }
    public sealed class AddressDialog : ModernWindow
    {
        private readonly TextBox address;
        public IPEndPoint Endpoint { get; private set; }
        public AddressDialog(int port)
        {
            Text = "添加联系人"; ClientSize = new Size(420, 260); ResizeWindow = false; MinimumSize = new Size(380, 230); StartPosition = FormStartPosition.CenterParent;
            Label label = Theme.Label("IP 地址", Theme.Body, Theme.Ink); label.SetBounds(24, 16, 350, 30); Surface.Controls.Add(label);
            RoundedInput input = new RoundedInput(); input.SetBounds(24, 52, 370, 44); address = input.Input; address.AccessibleName = "好友 IP 地址"; input.Placeholder = "192.168.1.100:" + port; Surface.Controls.Add(input);
            Label error = Theme.Label("", Theme.Small, Color.Firebrick); error.SetBounds(24, 104, 370, 40); Surface.Controls.Add(error);
            Button add = Theme.Button("添加", delegate { try { Endpoint = Parse(address.Text); DialogResult = DialogResult.OK; } catch (Exception e) { error.Text = e.Message; } }, true); add.SetBounds(298, 162, 96, 38); Surface.Controls.Add(add); AcceptButton = add;
        }
        public static IPEndPoint Parse(string text)
        {
            string[] parts = text.Trim().Split(':'); IPAddress ip; int port = 2425;
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224 || (parts.Length == 2 && !Int32.TryParse(parts[1], out port)) || port < 1024 || port > 65535) throw new ArgumentException("请输入有效 IPv4 地址，端口为 1024–65535。");
            return new IPEndPoint(ip, port);
        }
    }
    public sealed class ForwardDialog : ModernWindow
    {
        public Peer Target { get; private set; }
        public ForwardDialog(Peer[] peers, int attachments = 0)
        {
            Text = attachments<0?"恢复已移除的联系人":"转发到"; ClientSize = new Size(420, 460); MinimumSize = new Size(380, 300); ResizeWindow = false; StartPosition = FormStartPosition.CenterParent;
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(18) };
            ListBox list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = Theme.Body, BackColor = Theme.Surface, ForeColor = Theme.Ink, IntegralHeight = false };
            foreach (Peer peer in peers) list.Items.Add(peer.Nickname + "  ·  " + peer.Endpoint);
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 68;
            list.DrawItem += delegate(object sender, DrawItemEventArgs e) {
                if (e.Index < 0) return; Peer peer = peers[e.Index];
                using (SolidBrush brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Theme.Sky : Theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
                using (Bitmap avatar = Theme.ContactAvatar(42, peer.DisplayName, peer.Online)) e.Graphics.DrawImage(avatar, e.Bounds.Left + 8 - Theme.AvatarHalo(42), e.Bounds.Top + 13 - Theme.AvatarHalo(42));
                TextRenderer.DrawText(e.Graphics, peer.Nickname, Theme.Body, new Rectangle(e.Bounds.Left + 62, e.Bounds.Top + 12, e.Bounds.Width - 72, 24), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, peer.Endpoint.ToString(), Theme.Small, new Rectangle(e.Bounds.Left + 62, e.Bounds.Top + 36, e.Bounds.Width - 72, 22), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            Button send = Theme.Button(attachments<0?"恢复会话":"确认转发", delegate { if (list.SelectedIndex >= 0) { Target = peers[list.SelectedIndex]; DialogResult = DialogResult.OK; } }, true);
            send.Dock = DockStyle.Right; send.Enabled = false; list.SelectedIndexChanged += delegate { send.Enabled = list.SelectedIndex >= 0; }; footer.Controls.Add(send);
            Label hint = Theme.Label(attachments<0?(peers.Length==0?"没有已移除的联系人。":"选择联系人并恢复；保留的历史和草稿仍可查看。"):peers.Length == 0 ? "没有在线联系人，请先连接或添加联系人。" : attachments == 0 ? "转发文本 · 选择联系人后确认发送" : "转发 " + attachments + " 个附件及正文 · 选择联系人", Theme.Small, Theme.Muted); hint.Dock = DockStyle.Top; hint.Height = 48;
            Surface.Padding = new Padding(20); Surface.Controls.Add(list); Surface.Controls.Add(hint); Surface.Controls.Add(footer); AcceptButton = send;
        }
    }
    public sealed class SettingsDialog : ModernWindow
    {
        private readonly Panel body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        public AppSettings Value { get; private set; }
        public SettingsDialog(AppSettings current, string dataPath, int port, bool startupAvailable = false, bool startupEnabled = false)
        {
            Text = "设置"; ClientSize = new Size(520, 720); ResizeWindow = false; MinimumSize = new Size(470, 500); StartPosition = FormStartPosition.CenterParent;
            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 94 };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 24, 0, footer.Width - 24, 0); };
            Surface.Controls.Add(body); Surface.Controls.Add(footer);
            TextBox name = Input("昵称", current.Nickname, 8, 32), group = Input("部门", current.Group, 80, 32), signature = Input("个性签名", current.Signature, 152, 80);
            ToggleSwitch close = new ToggleSwitch { Text = "关闭时收起到托盘", Checked = current.CloseToTray, BackColor = Theme.Surface }; close.SetBounds(24, 232, 458, 40); body.Controls.Add(close);
            ToggleSwitch notifications = new ToggleSwitch { Text = "消息通知", Checked = current.Notifications, BackColor = Theme.Surface }; notifications.SetBounds(24, 274, 458, 40); body.Controls.Add(notifications);
            ToggleSwitch startup = new ToggleSwitch { Text = "开机自启动", AccessibleName = "开机自启动", Checked = startupAvailable ? startupEnabled : current.StartWithWindows, Enabled = startupAvailable, BackColor = Theme.Surface }; startup.SetBounds(24, 275, 458, 40); body.Controls.Add(startup);
            Label startupHint = Theme.Label(startupAvailable ? "登录 Windows 后自动连接并收起到托盘，无需管理员权限。\n移动程序后请重新开启；系统禁用的启动项需在任务管理器恢复。" : "当前运行环境无法配置系统启动项（如回环测试或系统限制）。\n其他设置仍可正常保存。", Theme.Small, Theme.Muted); startupHint.SetBounds(24, 276, 458, 56); body.Controls.Add(startupHint);
            ToggleSwitch auto = new ToggleSwitch { Text = "自动接收文件（无需逐次确认）", AccessibleName = "自动接收文件", Checked = current.AutoReceiveFiles, BackColor = Theme.Surface }; auto.SetBounds(24, 316, 458, 40); body.Controls.Add(auto);
            Label hint = Theme.Label("仅用于可信局域网；不会自动运行文件。\n关闭后，收到文件需手动点击接收。", Theme.Small, Theme.Muted); hint.SetBounds(24, 360, 458, 40); body.Controls.Add(hint);
            TextBox folder = Input("文件保存文件夹", current.ReceiveFolder ?? AppSettings.DefaultReceiveFolder, 404, 240); folder.Parent.Width = 350;
            Button browse = Theme.Button("浏览…", delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "选择飞Q接收文件的保存文件夹", SelectedPath = folder.Text, ShowNewFolderButton = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
            }, false); browse.SetBounds(386, 434, 96, 44); body.Controls.Add(browse);
            Button reset = Theme.Button("恢复默认目录", delegate { folder.Text = AppSettings.DefaultReceiveFolder; }, false); reset.SetBounds(24, 486, 146, 36); body.Controls.Add(reset);
            Label info = Theme.Label("端口 " + port + " · 启动自动连接并查找历史联系人\n资料目录  " + dataPath, Theme.Small, Theme.Muted); info.SetBounds(24, 532, 458, 46); info.AutoEllipsis = true; body.Controls.Add(info);
            ToggleSwitch dark=new ToggleSwitch { Text="夜间模式",AccessibleName="夜间模式",Checked=current.DarkMode,BackColor=Theme.Surface }; dark.SetBounds(24,590,458,40);body.Controls.Add(dark);
            int accentIndex = current.Accent == "green" ? 1 : current.Accent == "purple" ? 2 : 0;
            ToggleSwitch diagnostics=new ToggleSwitch {Text="记录故障诊断（保存后生效）",AccessibleName="记录故障诊断",Checked=current.DiagnosticsEnabled,BackColor=Theme.Surface};diagnostics.SetBounds(24,700,458,40);body.Controls.Add(diagnostics);
            Label privacy=Theme.Label("默认关闭；仅记 UTC 时间与事件码，不记聊天、身份、IP、路径。最多 128 KiB；关闭后保留，可手动清除。",Theme.Small,Theme.Muted);privacy.SetBounds(24,742,458,54);body.Controls.Add(privacy);
            Diagnostics journal=new Diagnostics(dataPath,()=>current.DiagnosticsEnabled);
            Button viewLog=Theme.Button("查看诊断记录",delegate {try {using(NoticeDialog dialog=new NoticeDialog("诊断记录（可选择复制）",journal.Read()))dialog.ShowDialog(this);}catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("读取失败",e.Message))dialog.ShowDialog(this);}},false);viewLog.SetBounds(24,798,180,36);body.Controls.Add(viewLog);
            Button clearLog=Theme.Button("清除诊断记录",delegate {try {journal.Clear();}catch(Exception e){using(NoticeDialog dialog=new NoticeDialog("清除失败",e.Message))dialog.ShowDialog(this);}},false);clearLog.SetBounds(24,838,180,36);body.Controls.Add(clearLog);
            TableLayoutPanel accent = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 48, AccessibleName = "主题颜色" };
            string[] accentNames = { "经典蓝", "青绿色", "紫罗兰" };
            Button[] choices = new Button[3];
            Action refreshAccent = delegate { for (int i = 0; i < choices.Length; i++) { choices[i].Text = (i == accentIndex ? "✓ " : "") + accentNames[i]; choices[i].BackColor = i == accentIndex ? Theme.Sky : Theme.Ice; choices[i].ForeColor = i == accentIndex ? Theme.Blue : Theme.Ink; } };
            for (int i = 0; i < 3; i++)
            {
                int index = i; accent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3));
                choices[i] = Theme.Button(accentNames[i], delegate { accentIndex = index; refreshAccent(); }, false);
                choices[i].AccessibleName = accentNames[i];
                choices[i].Dock = DockStyle.Fill; choices[i].Margin = new Padding(3); accent.Controls.Add(choices[i], i, 0);
            }
            refreshAccent(); accent.Top = 638; body.Controls.Add(accent);
            string backgroundMode=current.ChatBackground??"orbits", backgroundPath=current.BackgroundImage??"";
            int messageSize=Math.Max(9,Math.Min(18,current.ChatFontSize));
            Action refreshPattern=delegate {};Button image=null;
            TableLayoutPanel appearance=new TableLayoutPanel {ColumnCount=1,RowCount=6,Height=458,Top=292,AccessibleName="聊天外观"};
            appearance.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[]{124,116,36,58,62,62})appearance.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            Panel preview=new Panel {Dock=DockStyle.Fill,Margin=new Padding(0,0,0,20),AccessibleName="主题预览"};appearance.Controls.Add(preview,0,0);
            preview.Paint+=delegate(object sender,PaintEventArgs e){
                Color blue=accentIndex==1?Color.FromArgb(34,153,116):accentIndex==2?Color.FromArgb(139,103,215):Color.FromArgb(51,144,236);
                Color surface=accentIndex==1?ColorTranslator.FromHtml(dark.Checked?"#172D29":"#F4FAF4"):accentIndex==2?ColorTranslator.FromHtml(dark.Checked?"#292338":"#FCF7FC"):dark.Checked?Color.FromArgb(30,39,49):Color.White;
                using(SolidBrush b=new SolidBrush(Theme.Blend(blue,surface,dark.Checked?.10F:.15F)))e.Graphics.FillRectangle(b,preview.ClientRectangle);
                Rectangle left=new Rectangle(12,12,Math.Max(120,preview.Width-100),32),right=new Rectangle(76,52,Math.Max(120,preview.Width-90),32);
                using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Rounded(left,10))using(SolidBrush b=new SolidBrush(surface))e.Graphics.FillPath(b,path);
                using(System.Drawing.Drawing2D.GraphicsPath path=Theme.Rounded(right,10))using(SolidBrush b=new SolidBrush(Theme.Blend(blue,surface,dark.Checked?.30F:.17F)))e.Graphics.FillPath(b,path);
                using(Font font=new Font(Theme.Body.FontFamily,messageSize)) {Color ink=dark.Checked?Color.White:Color.FromArgb(23,33,43);TextRenderer.DrawText(e.Graphics,"今天也保持联系",font,left,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);TextRenderer.DrawText(e.Graphics,"消息与输入字号预览",font,right,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
            };
            TableLayoutPanel presets=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};for(int i=0;i<2;i++){presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));presets.RowStyles.Add(new RowStyle(SizeType.Percent,50));}
            string[] presetNames={"晴空 · 经典","薄荷 · 日间","深海 · 夜间","暮紫 · 夜间"};
            for(int i=0;i<4;i++){int choice=i;Button pick=Theme.Button(presetNames[i],delegate {dark.Checked=choice>=2;accentIndex=choice==1?1:choice==3?2:0;backgroundMode=choice==1?"lines":"orbits";refreshAccent();refreshPattern();preview.Invalidate();},false);pick.Dock=DockStyle.Fill;pick.Margin=new Padding(i%2==0?0:6,0,i%2==0?6:0,12);presets.Controls.Add(pick,i%2,i/2);}appearance.Controls.Add(presets,0,1);
            Label sizeLabel=Theme.Label("聊天字号 · "+messageSize+" pt",Theme.Body,Theme.Ink);sizeLabel.Dock=DockStyle.Fill;sizeLabel.Margin=new Padding(0,8,0,0);appearance.Controls.Add(sizeLabel,0,2);
            TrackBar sizeSlider=new TrackBar {Minimum=9,Maximum=18,Value=messageSize,TickFrequency=1,Dock=DockStyle.Fill,AccessibleName="聊天字号",BackColor=Theme.Surface};sizeSlider.ValueChanged+=delegate {messageSize=sizeSlider.Value;sizeLabel.Text="聊天字号 · "+messageSize+" pt";preview.Invalidate();};appearance.Controls.Add(sizeSlider,0,3);
            TableLayoutPanel patterns=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0,4,0,12)};string[] patternNames={"纯色","星环","流线"},patternKeys={"none","orbits","lines"};Button[] patternButtons=new Button[3];
            refreshPattern=delegate {for(int j=0;j<3;j++)patternButtons[j].Text=(backgroundMode==patternKeys[j]?"✓ ":"")+patternNames[j];if(image!=null)image.Text=backgroundMode=="image"?"背景图片 · "+System.IO.Path.GetFileName(backgroundPath):"选择背景图片…";};
            for(int i=0;i<3;i++){int choice=i;patterns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100F/3));patternButtons[i]=Theme.Button(patternNames[i],delegate{backgroundMode=patternKeys[choice];refreshPattern();},false);patternButtons[i].Dock=DockStyle.Fill;patternButtons[i].Margin=new Padding(i==0?0:6,0,i==2?0:6,0);patterns.Controls.Add(patternButtons[i],i,0);}refreshPattern();appearance.Controls.Add(patterns,0,4);
            image=Theme.Button(backgroundMode=="image"?"已选择背景图片 · 更换":"选择背景图片…",delegate {
                using(OpenFileDialog picker=new OpenFileDialog {Title="选择聊天背景（不超过 20 MiB）",Filter="图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif",CheckFileExists=true})if(picker.ShowDialog(this)==DialogResult.OK){
                    try {using(Bitmap validated=ChatWallpaper.Load(picker.FileName)){}backgroundPath=picker.FileName;backgroundMode="image";refreshPattern();}
                    catch(Exception errorImage){using(NoticeDialog notice=new NoticeDialog("无法使用图片",errorImage.Message))notice.ShowDialog(this);}
                }
            },false);image.AutoEllipsis=true;image.Dock=DockStyle.Fill;image.Margin=new Padding(0,0,0,12);refreshPattern();appearance.Controls.Add(image,0,5);body.Controls.Add(appearance);
            dark.CheckedChanged+=delegate {preview.Invalidate();};foreach(Button choice in choices)choice.Click+=delegate{preview.Invalidate();};
            Label error = Theme.Label("", Theme.Small, Color.Firebrick); error.SetBounds(24, 2, 472, 36); error.AccessibleName = "设置错误"; footer.Controls.Add(error);
            Button cancel = Theme.Button("取消", delegate { DialogResult = DialogResult.Cancel; }, false); cancel.SetBounds(290, 44, 86, 38); footer.Controls.Add(cancel); CancelButton = cancel;
            Button save = Theme.Button("保存", delegate
            {
                if (String.IsNullOrWhiteSpace(name.Text)) { error.Text = "昵称不能为空"; name.Focus(); return; }
                try
                {
                    string path = ReceiveStorage.Folder(folder.Text); ReceiveStorage.ValidateWritable(path);
                    Value = new AppSettings { Nickname = Protocol.Field(name.Text.Trim()), Group = String.IsNullOrWhiteSpace(group.Text) ? "我的局域网" : Protocol.Field(group.Text.Trim()), Signature = signature.Text.Trim(), CloseToTray = close.Checked, StartWithWindows = startup.Checked, Notifications = notifications.Checked,DiagnosticsEnabled = diagnostics.Checked,  AutoReceiveFiles = auto.Checked, ReceiveFolder = path, ChatFontSize=messageSize,ChatBackground=backgroundMode,BackgroundImage=backgroundPath,DarkMode=dark.Checked, Accent=accentIndex==1?"green":accentIndex==2?"purple":"blue" };
                    DialogResult = DialogResult.OK;
                }
                catch (Exception e) { error.Text = "保存目录不可用：" + e.Message; folder.Focus(); }
            }, true); save.SetBounds(390, 44, 92, 38); footer.Controls.Add(save); AcceptButton = save;
            // 单列自然流替代绝对坐标堆叠，滚动只作用于内容，保存按钮始终可见。
            // 外观与日常开关相邻，技术资料置底；不让路径信息打断设置操作。
            dark.Top = 290; accent.Top = 291; info.Top = 900;
            Control[] fields = body.Controls.Cast<Control>().OrderBy(c => c.Top).ToArray();
            FlowLayoutPanel layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 8, 24, 20) };
            body.Controls.Clear(); body.Controls.Add(layout);
            foreach (Control field in fields)
            {
                if (field == reset) continue;
                if (field == browse)
                {
                    TableLayoutPanel actions = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 44, Margin = new Padding(0, 0, 0, 12) };
                    actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                    browse.Text = "选择文件夹"; browse.Dock = reset.Dock = DockStyle.Fill; browse.BackColor = reset.BackColor = Theme.Ice;
                    browse.Margin = new Padding(0, 0, 6, 0); reset.Margin = new Padding(6, 0, 0, 0); actions.Controls.Add(browse, 0, 0); actions.Controls.Add(reset, 1, 0); layout.Controls.Add(actions); continue;
                }
                string section = field == fields[0] ? "个人资料" : field == close ? "消息与窗口" : field == auto ? "文件接收" : field == dark ? "外观" : field == diagnostics ? "故障诊断" : field == info ? "本机信息" : null;
                if (section != null) {
                    if(layout.Controls.Count>0) { Panel divider=new Panel { Height=1, BackColor=Theme.Line, Margin=new Padding(0,16,0,16) };layout.Controls.Add(divider); }
                    Label heading = Theme.Label(section, Theme.Section, Theme.Ink); heading.Height = 28; heading.Margin = new Padding(0, 0, 0, 8); layout.Controls.Add(heading);
                }
                if(field is Label && field != hint && field != info && field != startupHint && field != privacy) field.Height=22;
                field.Margin = new Padding(0, 0, 0, field is Label ? 6 : 12); layout.Controls.Add(field);
            }
            Action fit = delegate { int width = Math.Max(260, body.ClientSize.Width - 48 - SystemInformation.VerticalScrollBarWidth); foreach (Control field in layout.Controls) field.Width = width; };
            body.SizeChanged += delegate { fit(); }; fit();
        }
        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Height = Math.Min(Height, Screen.FromControl(this).WorkingArea.Height - 24); }
        private TextBox Input(string title, string value, int y, int max)
        {
            Label label = Theme.Label(title, Theme.Small, Theme.Muted); label.SetBounds(24, y, 458, 26); body.Controls.Add(label);
            RoundedInput input = new RoundedInput(); input.SetBounds(24, y + 30, 458, 40); input.Input.Text = value; input.Input.MaxLength = max; input.Input.AccessibleName = title; body.Controls.Add(input); return input.Input;
        }
    }
    public sealed class HistoryDialog : ModernWindow
    {
        public HistoryDialog(LocalStore store, Peer peer)
        {
            Text = "聊天记录"; ClientSize = new Size(680, 660); MinimumSize = new Size(480, 420); StartPosition = FormStartPosition.CenterParent;
            Panel top = new Panel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(16, 8, 16, 10) }; RoundedInput input = new RoundedInput { Dock = DockStyle.Fill }; input.Input.AccessibleName = "搜索聊天记录"; input.Placeholder = "搜索"; top.Controls.Add(input);
            BubbleHistory body = new BubbleHistory { Dock = DockStyle.Fill }; Surface.Controls.Add(body); Surface.Controls.Add(top);
            try { var records = store.History(peer.Id, 500); Action render = () => { body.Clear(); foreach (var record in records.Where(r => r.Text.IndexOf(input.Input.Text, StringComparison.OrdinalIgnoreCase) >= 0)) body.Append(record); }; input.Input.TextChanged += delegate { render(); }; render(); }
            catch (Exception e) { using (NoticeDialog error = new NoticeDialog("读取失败", e.Message)) error.ShowDialog(this); }
        }
    }
}
