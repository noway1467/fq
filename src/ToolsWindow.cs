using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FeiqLight
{
    internal sealed class ToolsWindow : ModernWindow
    {
        private readonly ToolsService service;
        private readonly RemoteDesktop desktop = new RemoteDesktop();
        private readonly Label share, shareCode, remote, connection, empty;
        private readonly ListBox folders = new ListBox();
        private readonly Button startShare, stopShare, removeFolder, copyCode, stopControl;
        private readonly ToolTip tips = new ToolTip();
        private readonly Font codeFont = new Font("Consolas", 20F, FontStyle.Bold);
        private readonly Timer timer = new Timer { Interval = 1000 };
        private Form banner;
        private bool hotkey;
        private string restoreError;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        internal ToolsWindow(int port, bool loopback, string root = null)
        {
            Text = "共享与远控"; ClientSize = new Size(840, 740); MinimumSize = new Size(680, 620); UseSecondaryChrome();
            service = new ToolsService(ToolsHttp.PortFor(port), loopback, root); service.Capture = desktop.Capture; service.Displays = desktop.Displays; service.Input = desktop.Apply; service.ReleaseInput = desktop.Release; service.ConfirmRemoteDevice = Confirm;
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(24, 16, 24, 12), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 36, 80, -1, 52, 24, 152, 44 }) body.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
            Surface.Controls.Add(body);
            AddLabel(body, "共享文件夹", true);
            RoundedPanel summary = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Radius = Theme.ControlRadius, Padding = new Padding(16, 8, 12, 8), Margin = new Padding(0, 0, 0, 12) };
            TableLayoutPanel summaryRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156)); summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); summaryRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            share = Theme.Label("未共享", Theme.Body, Theme.Ink); share.Dock = DockStyle.Fill; share.Margin = Padding.Empty; summaryRow.Controls.Add(share, 0, 0);
            shareCode = Theme.Label("—", codeFont, Theme.Ink); shareCode.Dock = DockStyle.Fill; shareCode.TextAlign = ContentAlignment.MiddleCenter; shareCode.Margin = Padding.Empty; shareCode.AccessibleName = "共享连接码"; summaryRow.Controls.Add(shareCode, 1, 0);
            copyCode = Theme.Button("复制连接码", delegate { try { Clipboard.SetText(service.ShareCode); } catch (ExternalException) { using (NoticeDialog notice = new NoticeDialog("复制失败", "剪贴板正被其他程序占用，请稍后重试。")) notice.ShowDialog(this); } }, false); copyCode.Dock = DockStyle.Fill; copyCode.Margin = new Padding(4, 4, 0, 4); summaryRow.Controls.Add(copyCode, 2, 0); summary.Controls.Add(summaryRow); body.Controls.Add(summary, 0, 1);
            RoundedPanel listFrame = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Surface, Border = Theme.Line, Radius = Theme.ControlRadius, Padding = new Padding(8), Margin = Padding.Empty };
            folders.Dock = DockStyle.Fill; folders.BorderStyle = BorderStyle.None; folders.BackColor = Theme.Surface; folders.ForeColor = Theme.Ink; folders.IntegralHeight = false; folders.DrawMode = DrawMode.OwnerDrawFixed; folders.ItemHeight = 60; folders.AccessibleName = "共享文件夹列表";
            folders.DrawItem += DrawFolder; folders.SelectedIndexChanged += delegate { removeFolder.Enabled = folders.SelectedItem != null; tips.SetToolTip(folders, folders.SelectedItem as string); };
            folders.MouseMove += delegate(object sender, MouseEventArgs e) { int index = folders.IndexFromPoint(e.Location); string path = index < 0 ? "" : (string)folders.Items[index]; if (tips.GetToolTip(folders) != path) tips.SetToolTip(folders, path); };
            Theme.ThemeScrollbars(folders); listFrame.Controls.Add(folders);
            empty = Theme.Label("暂无共享文件夹", Theme.Body, Theme.Muted); empty.TextAlign = ContentAlignment.MiddleCenter; empty.Dock = DockStyle.Fill; listFrame.Controls.Add(empty); body.Controls.Add(listFrame, 0, 2);
            FlowLayoutPanel shareActions = Actions();
            Button addFolder = AddButton(shareActions, "添加文件夹", Choose, true, 128);
            startShare = AddButton(shareActions, "开启共享", delegate { ChangeSettings(service.StartSavedSharing); }, false, 128);
            stopShare = AddButton(shareActions, "停止共享", delegate { ChangeSettings(service.StopSharing); }, false, 108);
            removeFolder = AddButton(shareActions, "移除所选", delegate { string path = folders.SelectedItem as string; if (path != null) ChangeSettings(() => service.RemoveFolder(path)); }, false, 108);
            body.Controls.Add(shareActions, 0, 3);
            Label hint = AddLabel(body, "只读共享 · 最多 20 个文件夹", false); hint.Font = Theme.Small;
            tips.SetToolTip(addFolder, "支持多选；增减目录后需重新配对。");
            tips.SetToolTip(removeFolder, "仅取消共享，不删除文件；现有连接会断开。");
            tips.SetToolTip(startShare, "连接码有效 10 分钟；更新后现有连接会断开。");
            tips.SetToolTip(shareCode, "共享连接码 · 有效 10 分钟");
            RoundedPanel remoteCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Radius = Theme.ControlRadius, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 8, 0, 8) };
            TableLayoutPanel remoteBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Ice, Margin = Padding.Empty }; remoteBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); foreach (int height in new[] { 28, 36, 48 }) remoteBody.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            AddLabel(remoteBody, "远程控制", true); remote = AddLabel(remoteBody, "未开启", false);
            tips.SetToolTip(remote, "连接需本机确认，已信任的设备除外。Ctrl+Alt+F12 停止。");
            FlowLayoutPanel remoteActions = Actions(); remoteActions.BackColor = Theme.Ice; AddButton(remoteActions, "生成远控连接码", delegate { ChangeSettings(service.AllowRemote); }, false, 156); stopControl = AddButton(remoteActions, "立即停止远控", StopRemote, false, 144); remoteBody.Controls.Add(remoteActions); remoteCard.Controls.Add(remoteBody); body.Controls.Add(remoteCard, 0, 5);
            Panel footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Button forget = Theme.Button("清除已配对设备", delegate { using (ConfirmDialog prompt = new ConfirmDialog("清除已配对设备？", "将断开连接并撤销信任，下次需重新配对。", "清除配对", true)) if (prompt.ShowDialog(this) == DialogResult.OK) ChangeSettings(service.ForgetPairings); }, false);
            forget.Dock = DockStyle.Right; forget.Width = 156; forget.Margin = Padding.Empty; footer.Controls.Add(forget);
            connection = Theme.Label("", Theme.Small, Theme.Muted); connection.Dock = DockStyle.Fill; connection.AutoEllipsis = true; footer.Controls.Add(connection); connection.BringToFront(); body.Controls.Add(footer, 0, 6);
            Shown += delegate { using (Graphics graphics = folders.CreateGraphics()) folders.ItemHeight = (int)(60 * graphics.DpiX / 96F); UpdateState(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanging += OnDisplayChange;
            timer.Tick += delegate { if (!service.RemoteActive) desktop.Release(); UpdateState(); }; timer.Start(); UpdateState();
            PrepareRestoredWindow();
            try { restoreError = service.RestoreSettings(); }
            catch (System.IO.IOException e) { restoreError = e.Message; }
            catch (UnauthorizedAccessException e) { restoreError = e.Message; }
            UpdateState();
        }
        internal void PrepareRestoredWindow() { if (!IsHandleCreated) CreateHandle(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); hotkey = RegisterHotKey(Handle, 941, 0x4003, 0x7b); }
        private void ChangeSettings(Action change)
        {
            try { change(); restoreError = null; UpdateState(); }
            catch (Exception e) { if (!(e is System.IO.IOException || e is UnauthorizedAccessException || e is ToolsHttp.HttpError || e is ArgumentException)) throw; UpdateState(); using (NoticeDialog notice = new NoticeDialog("操作未完成", e.Message)) notice.ShowDialog(this); }
        }
        private static Label AddLabel(TableLayoutPanel panel, string text, bool heading)
        {
            Label label = Theme.Label(text, heading ? Theme.Strong : Theme.Body, heading ? Theme.Ink : Theme.Muted); label.Dock = DockStyle.Fill; label.Margin = Padding.Empty; panel.Controls.Add(label); return label;
        }
        private static FlowLayoutPanel Actions() { return new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 4), WrapContents = false }; }
        private static Button AddButton(FlowLayoutPanel row, string text, Action action, bool primary, int width)
        {
            Button button = Theme.Button(text, delegate { action(); }, primary); button.Size = new Size(width, 36); button.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(button); return button;
        }
        private void Choose()
        {
            try
            {
                string[] selected = SharedFolderPicker.Show(this);
                if (selected.Length > 0) ChangeSettings(() => service.AddFolders(selected));
            }
            catch (COMException e) { using (NoticeDialog notice = new NoticeDialog("无法打开文件夹选择器", e.Message)) notice.ShowDialog(this); }
        }
        private void DrawFolder(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= folders.Items.Count) return;
            float scale = e.Graphics.DpiX / 96F; string path = (string)folders.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush fill = new SolidBrush(selected ? Theme.Sky : Theme.Surface)) e.Graphics.FillRectangle(fill, e.Bounds);
            Theme.Glyph(e.Graphics, "folder", new Rectangle(e.Bounds.Left + (int)(12 * scale), e.Bounds.Top + (int)(18 * scale), (int)(24 * scale), (int)(24 * scale)), Theme.Blue);
            Rectangle text = new Rectangle(e.Bounds.Left + (int)(48 * scale), e.Bounds.Top + (int)(8 * scale), Math.Max(1, e.Bounds.Width - (int)(60 * scale)), (int)(24 * scale));
            TextRenderer.DrawText(e.Graphics, ToolsService.FolderName(path), Theme.Section, text, Theme.Ink, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            text.Y += (int)(24 * scale); text.Height = (int)(20 * scale); TextRenderer.DrawText(e.Graphics, path, Theme.Small, text, Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }
        private int Confirm(string address)
        {
            if (IsDisposed || !IsHandleCreated) return 0;
            try { return (int)Invoke(new Func<int>(() =>
            {
                if (IsDisposed) return 0;
                using (ConfirmDialog prompt = new ConfirmDialog("是否允许远程控制？", address + " 将能查看屏幕并操作电脑。\nCtrl+Alt+F12 停止。", "允许本次控制", false, "始终允许此设备"))
                using (Timer timeout = new Timer { Interval = 30000 })
                {
                    prompt.TopMost = true; timeout.Tick += delegate { prompt.DialogResult = DialogResult.Cancel; }; timeout.Start();
                    return prompt.ShowDialog(this) == DialogResult.OK ? prompt.OptionChecked ? 2 : 1 : 0;
                }
            })); }
            catch (InvalidOperationException) { return 0; }
        }
        private void ShowBanner()
        {
            if (banner != null) return;
            banner = new RemoteBanner(service.RemoteAddress, StopRemote);
            Rectangle area = Screen.FromControl(this).WorkingArea; banner.Location = new Point(area.Right - banner.Width - 16, area.Top + 16);
            banner.FormClosing += delegate { StopRemote(); }; banner.Show();
        }
        private void StopRemote() { ChangeSettings(service.StopRemote); }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
            {
                StopForSystemChange();
            }
        }
        private void OnDisplayChange(object sender, EventArgs e) { StopForSystemChange(); }
        private void StopForSystemChange()
        {
            try { service.StopRemote(); }
            catch (Exception e) { if (!(e is System.IO.IOException || e is UnauthorizedAccessException)) throw; restoreError = "远控已停止，但设置未保存：" + e.Message; }
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)UpdateState);
        }
        private void UpdateState()
        {
            bool active = service.RemoteActive;
            if (active && banner == null) ShowBanner();
            string[] saved = service.SavedFolders; bool sharing = service.Folders.Length > 0; string code = service.ShareCode;
            share.Text = sharing ? "共享中 · " + saved.Length + " 个文件夹" : saved.Length == 0 ? "未共享" : "已停止 · " + saved.Length + " 个文件夹";
            shareCode.Text = sharing && code.Length == 6 ? code : "—"; copyCode.Enabled = sharing && code.Length == 6;
            if (sharing && code.Length != 6) share.Text = "共享中 · 连接码已过期";
            startShare.Enabled = saved.Length > 0; startShare.Text = sharing ? "更新连接码" : "开启共享"; stopShare.Enabled = sharing; stopControl.Enabled = service.RemoteEnabled;
            if (!folders.Items.Cast<string>().SequenceEqual(saved))
            {
                string selected = folders.SelectedItem as string; int index = folders.SelectedIndex;
                folders.BeginUpdate(); folders.Items.Clear(); folders.Items.AddRange(saved);
                if (saved.Length > 0) folders.SelectedIndex = selected != null && Array.IndexOf(saved, selected) >= 0 ? Array.IndexOf(saved, selected) : Math.Min(Math.Max(0, index), saved.Length - 1);
                folders.EndUpdate();
            }
            removeFolder.Enabled = folders.SelectedItem != null; empty.Visible = saved.Length == 0; folders.Visible = saved.Length > 0;
            remote.Text = active ? "已连接 · " + service.RemoteAddress : !service.RemoteEnabled ? "未开启" : service.RemoteCode.Length == 6 ? "连接码  " + service.RemoteCode : "等待已配对设备";
            connection.Text = restoreError ?? "TCP " + service.Port; connection.ForeColor = restoreError == null ? Theme.Muted : Theme.Danger; tips.SetToolTip(connection, restoreError ?? "仅限可信局域网使用");
            if (!active && banner != null) { Form old = banner; banner = null; old.Dispose(); }
        }
        protected override void WndProc(ref Message message) { if (message.Msg == 0x312 && message.WParam.ToInt32() == 941) StopRemote(); base.WndProc(ref message); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); tips.Dispose(); codeFont.Dispose(); SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.DisplaySettingsChanging -= OnDisplayChange; if (hotkey) UnregisterHotKey(Handle, 941); service.Dispose(); if (banner != null) { banner.Dispose(); banner = null; } }
            base.Dispose(disposing);
        }
    }
    internal sealed class RemoteBanner : Form
    {
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080; return value; } }
        internal RemoteBanner(string address, Action disconnect)
        {
            Text = "飞Q远程控制"; FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(288, 64); StartPosition = FormStartPosition.Manual; BackColor = Theme.Surface; Padding = new Padding(12, 8, 8, 8); DoubleBuffered = true;
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Panel dot = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; dot.Paint += delegate(object sender, PaintEventArgs e) { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using (Brush brush = new SolidBrush(Theme.Online)) e.Graphics.FillEllipse(brush, 0, (dot.Height - 8) / 2, 8, 8); }; row.Controls.Add(dot, 0, 0);
            TableLayoutPanel info = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty }; info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); info.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); info.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            foreach (Label label in new[] { Theme.Label("远控中", Theme.Body, Theme.Ink), Theme.Label(address, Theme.Small, Theme.Muted) })
            {
                label.Dock = DockStyle.Fill; label.Margin = Padding.Empty; label.AutoEllipsis = true; label.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } }; info.Controls.Add(label);
            }
            row.Controls.Add(info, 1, 0); Button stop = Theme.Button("断开", delegate { disconnect(); }, true); stop.Dock = DockStyle.Fill; stop.Margin = new Padding(4, 0, 0, 0); row.Controls.Add(stop, 2, 0); Controls.Add(row);
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen border = new Pen(Theme.Line)) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); }
        protected override void WndProc(ref Message message) { if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } base.WndProc(ref message); }
    }
}
