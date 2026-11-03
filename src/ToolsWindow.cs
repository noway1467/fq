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
        private readonly Label share, remote, connection;
        private readonly Timer timer = new Timer { Interval = 1000 };
        private Form banner;
        private bool hotkey;
        private string restoreError;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        internal ToolsWindow(int port, bool loopback, string root = null)
        {
            Text = "共享与远控"; ClientSize = new Size(600, 472); MinimumSize = new Size(540, 440); UseSecondaryChrome();
            service = new ToolsService(ToolsHttp.PortFor(port), loopback, root); service.Capture = desktop.Capture; service.Displays = desktop.Displays; service.Input = desktop.Apply; service.ReleaseInput = desktop.Release; service.ConfirmRemoteDevice = Confirm;
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 8, Padding = new Padding(24), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 32, 60, 48, 40, 48, 48, 32, 48 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            scroll.Controls.Add(body); Surface.Controls.Add(scroll);
            AddLabel(body, "共享文件夹", 32, true);
            share = AddLabel(body, "未共享", 60, false);
            AddButtons(body, "开启 / 更换文件夹", Choose, "停止共享", delegate { ChangeSettings(service.StopSharing); });
            AddLabel(body, "远程控制", 40, true);
            remote = AddLabel(body, "未开启", 48, false);
            AddButtons(body, "生成远控连接码", delegate { ChangeSettings(service.AllowRemote); }, "立即停止远控", StopRemote);
            connection = AddLabel(body, "", 32, false);
            Button forget = Theme.Button("清除已配对设备", delegate { try { service.ForgetPairings(); UpdateState(); } catch (System.IO.IOException e) { using (NoticeDialog notice = new NoticeDialog("无法清除配对", e.Message)) notice.ShowDialog(this); } }, false);
            forget.Width = 190; body.Controls.Add(forget);
            Shown += delegate { UpdateState(); };
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
            catch (Exception e) { if (!(e is System.IO.IOException || e is UnauthorizedAccessException)) throw; UpdateState(); using (NoticeDialog notice = new NoticeDialog("设置未保存", e.Message)) notice.ShowDialog(this); }
        }
        private static Label AddLabel(TableLayoutPanel panel, string text, int height, bool heading)
        {
            Label label = Theme.Label(text, heading ? Theme.Section : Theme.Body, heading ? Theme.Ink : Theme.Muted); label.Height = height; label.Dock = DockStyle.Fill; label.Margin = Padding.Empty; panel.Controls.Add(label); return label;
        }
        private static void AddButtons(TableLayoutPanel panel, string first, Action action, string second, Action other)
        {
            FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 48, Margin = Padding.Empty, WrapContents = false };
            Button one = Theme.Button(first, delegate { action(); }, true), two = Theme.Button(second, delegate { other(); }, false);
            one.Width = 190; two.Width = 160; row.Controls.Add(one); row.Controls.Add(two); panel.Controls.Add(row);
        }
        private void Choose()
        {
            if (service.Folder == null && !String.IsNullOrEmpty(service.SavedFolder))
            {
                try { service.Share(service.SavedFolder); restoreError = null; UpdateState(); return; }
                catch (Exception e) { if (!(e is System.IO.IOException || e is UnauthorizedAccessException || e is ToolsHttp.HttpError)) throw; using (NoticeDialog notice = new NoticeDialog("原目录不可用", e.Message)) notice.ShowDialog(this); }
            }
            using (FolderBrowserDialog picker = new FolderBrowserDialog { Description = "选择只读共享的文件夹（包含子文件夹）", SelectedPath = service.SavedFolder ?? "", ShowNewFolderButton = false })
                if (picker.ShowDialog(this) == DialogResult.OK) try { service.Share(picker.SelectedPath); restoreError = null; UpdateState(); } catch (Exception e) { using (NoticeDialog notice = new NoticeDialog("无法共享", e.Message)) notice.ShowDialog(this); }
        }
        private int Confirm(string address)
        {
            if (IsDisposed || !IsHandleCreated) return 0;
            try { return (int)Invoke(new Func<int>(() =>
            {
                if (IsDisposed) return 0;
                using (ModernWindow prompt = new ModernWindow { Text = "是否允许远程控制？", MinimumSize = new Size(400, 220), ClientSize = new Size(520, 256), ResizeWindow = false, TopMost = true })
                using (Timer timeout = new Timer { Interval = 30000 })
                {
                    Label description = Theme.Label(address + " 请求控制此电脑", Theme.Section, Theme.Ink); description.Dock = DockStyle.Fill; description.Padding = new Padding(24);
                    FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, Padding = new Padding(20, 4, 0, 0) };
                    Button deny = Theme.Button("拒绝", delegate { prompt.DialogResult = DialogResult.Cancel; }, false);
                    CheckBox always = new CheckBox { Text = "始终允许此设备", Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(24, 0, 0, 0), ForeColor = Theme.Ink, Font = Theme.Body };
                    buttons.Controls.Add(deny); buttons.Controls.Add(Theme.Button("允许本次控制", delegate { prompt.DialogResult = DialogResult.OK; }, true)); prompt.CancelButton = deny;
                    prompt.Surface.Controls.Add(description); prompt.Surface.Controls.Add(always); prompt.Surface.Controls.Add(buttons); timeout.Tick += delegate { prompt.DialogResult = DialogResult.Cancel; }; timeout.Start();
                    return prompt.ShowDialog(this) == DialogResult.OK ? always.Checked ? 2 : 1 : 0;
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
            share.Text = service.Folder == null ? (String.IsNullOrEmpty(service.SavedFolder) ? "未共享" : service.SavedFolder + "\n已停止") : service.Folder + "\n连接码  " + service.ShareCode;
            remote.Text = active ? "已连接  " + service.RemoteAddress : !service.RemoteEnabled ? "未开启" : service.RemoteCode.Length == 6 ? "连接码  " + service.RemoteCode : "等待已配对设备";
            connection.Text = restoreError ?? "TCP " + service.Port;
            if (!active && banner != null) { Form old = banner; banner = null; old.Dispose(); }
        }
        protected override void WndProc(ref Message message) { if (message.Msg == 0x312 && message.WParam.ToInt32() == 941) StopRemote(); base.WndProc(ref message); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.DisplaySettingsChanging -= OnDisplayChange; if (hotkey) UnregisterHotKey(Handle, 941); service.Dispose(); if (banner != null) { banner.Dispose(); banner = null; } }
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
