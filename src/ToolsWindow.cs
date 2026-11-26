using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FeiqLight
{
    internal sealed class ToolsWindow : ModernWindow
    {
        // 圆角状态胶囊：带柔和底色与小状态点，随共享/远控状态更新。
        private sealed class StatusPill : Control
        {
            private string caption = "";
            private Color tone = Theme.Muted;
            internal StatusPill() { DoubleBuffered = true; ResizeRedraw = true; Height = 26; BackColor = Theme.Ice; }
            internal void Set(string text, Color accent)
            {
                if (caption == text && tone.ToArgb() == accent.ToArgb()) return;
                caption = text ?? ""; tone = accent;
                using (Graphics g = CreateGraphics())
                {
                    float scale = g.DpiX / 96F;
                    int textW = TextRenderer.MeasureText(g, caption, Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    Width = Math.Max((int)(64 * scale), textW + (int)(28 * scale));
                }
                Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics; g.Clear(Parent == null ? BackColor : Parent.BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96F;
                RectangleF pill = new RectangleF(.5F, (Height - 22F * scale) / 2F, Width - 1F, 22F * scale);
                Color fill = Theme.Blend(tone, Theme.Surface, Theme.Dark ? .26F : .14F);
                using (GraphicsPath path = Theme.RoundedF(pill, pill.Height / 2F)) using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                float dot = 6F * scale;
                Color labelTone = Theme.Dark ? Theme.Blend(Color.White, tone, .18F) : tone;
                using (SolidBrush brush = new SolidBrush(labelTone)) g.FillEllipse(brush, pill.Left + 8F * scale, pill.Top + (pill.Height - dot) / 2F, dot, dot);
                Rectangle text = new Rectangle((int)(pill.Left + 18F * scale), (int)pill.Top, Math.Max(1, (int)(pill.Width - 24F * scale)), (int)pill.Height);
                TextRenderer.DrawText(g, caption, Theme.Small, text, labelTone, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
        private readonly ToolsService service;
        private readonly RemoteDesktop desktop = new RemoteDesktop();
        private readonly StatusPill sharePill, remotePill;
        private readonly Label shareCode, remoteCode, connection;
        private readonly Panel emptyPanel;
        private readonly ListBox folders = new ListBox();
        private readonly Button startShare, stopShare, removeFolder, copyCode, stopControl;
        private readonly ToolTip tips = new ToolTip();
        private readonly Font codeFont = new Font("Consolas", 17F, FontStyle.Bold);
        private readonly Timer timer = new Timer { Interval = 1000 };
        private Form banner;
        private bool hotkey;
        private string restoreError;
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
        internal ToolsWindow(int port, bool loopback, string root = null,SecureIdentity security=null)
        {
            Text = "共享与远控"; ClientSize = new Size(820, 720); MinimumSize = new Size(640, 560); UseSecondaryChrome();
            service = new ToolsService(ToolsHttp.PortFor(port), loopback, root,security); service.Capture = desktop.Capture; service.Displays = desktop.Displays; service.Input = desktop.Apply; service.ReleaseInput = desktop.Release; service.ConfirmRemoteDevice = Confirm;
            // 保持 Surface 下仅包含 body 一个顶层 Panel，兼容窗口布局回归检测。
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24, 16, 24, 14), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 90, 48, -1, 114, 52 }) body.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
            Surface.Controls.Add(body);

            // 区块一：共享文件夹状态与 6 位连接码卡片
            RoundedPanel summary = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Theme.Line, Radius = 12, Padding = new Padding(16, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
            TableLayoutPanel summaryRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); summaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270)); summaryRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel shareMeta = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
            shareMeta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shareMeta.RowStyles.Add(new RowStyle(SizeType.Percent, 56)); shareMeta.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            FlowLayoutPanel shareHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            Label shareTitle = Theme.Label("共享文件夹", Theme.Strong, Theme.Ink); shareTitle.AutoSize = true; shareTitle.Margin = new Padding(0, 4, 10, 0); shareHeader.Controls.Add(shareTitle);
            sharePill = new StatusPill { Margin = new Padding(0, 2, 0, 0) }; shareHeader.Controls.Add(sharePill);
            shareMeta.Controls.Add(shareHeader, 0, 0);
            Label shareHint = Theme.Label("只读共享 · 最多 20 个文件夹 · 双击目录项打开本地位置", Theme.Small, Theme.Muted); shareHint.Dock = DockStyle.Fill; shareHint.AutoEllipsis = true; shareHint.Margin = Padding.Empty;
            shareMeta.Controls.Add(shareHint, 0, 1);
            summaryRow.Controls.Add(shareMeta, 0, 0);

            RoundedPanel codeBox = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, Fill = Theme.Surface, Border = Theme.Line, Radius = Theme.ControlRadius, Padding = new Padding(12, 6, 8, 6), Margin = Padding.Empty };
            TableLayoutPanel codeRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); codeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel codeText = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            codeText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); codeText.RowStyles.Add(new RowStyle(SizeType.Absolute, 16)); codeText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label codeCaption = Theme.Label("6 位连接码", Theme.Small, Theme.Muted); codeCaption.Dock = DockStyle.Fill; codeCaption.Margin = Padding.Empty; codeText.Controls.Add(codeCaption, 0, 0);
            shareCode = Theme.Label("—", codeFont, Theme.Ink); shareCode.Dock = DockStyle.Fill; shareCode.TextAlign = ContentAlignment.MiddleLeft; shareCode.Margin = Padding.Empty; shareCode.AccessibleName = "共享连接码"; codeText.Controls.Add(shareCode, 0, 1);
            codeRow.Controls.Add(codeText, 0, 0);
            copyCode = Theme.Button("复制连接码", delegate { try { Clipboard.SetText(service.ShareCode); } catch (ExternalException) { using (NoticeDialog notice = new NoticeDialog("复制失败", "剪贴板正被其他程序占用，请稍后重试。")) notice.ShowDialog(this); } }, false);
            copyCode.Dock = DockStyle.Fill; copyCode.Margin = new Padding(6, 5, 0, 5); StyleCardButton(copyCode, true);
            codeRow.Controls.Add(copyCode, 1, 0); codeBox.Controls.Add(codeRow);
            summaryRow.Controls.Add(codeBox, 1, 0); summary.Controls.Add(summaryRow); body.Controls.Add(summary, 0, 0);

            // 操作工具栏：左侧主/次操作，右侧移除所选
            TableLayoutPanel toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 8), BackColor = Theme.Surface };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel shareActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 2, 0, 2), WrapContents = false, BackColor = Theme.Surface };
            Button addFolder = AddButton(shareActions, "添加文件夹", Choose, true, 112);
            startShare = AddButton(shareActions, "开启共享", delegate { ChangeSettings(service.StartSavedSharing); }, false, 108);
            stopShare = AddButton(shareActions, "停止共享", delegate { ChangeSettings(service.StopSharing); }, false, 96);
            toolbar.Controls.Add(shareActions, 0, 0);
            removeFolder = Theme.Button("移除所选", delegate { string path = folders.SelectedItem as string; if (path != null) ChangeSettings(() => service.RemoveFolder(path)); }, false);
            removeFolder.Dock = DockStyle.Fill; removeFolder.Margin = new Padding(4, 2, 0, 2); StyleCardButton(removeFolder, false);
            toolbar.Controls.Add(removeFolder, 1, 0);
            body.Controls.Add(toolbar, 0, 1);

            // 共享目录列表卡片
            RoundedPanel listFrame = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Surface, Border = Theme.Line, Radius = 12, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 12) };
            folders.Dock = DockStyle.Fill; folders.BorderStyle = BorderStyle.None; folders.BackColor = Theme.Surface; folders.ForeColor = Theme.Ink; folders.IntegralHeight = false; folders.DrawMode = DrawMode.OwnerDrawFixed; folders.ItemHeight = 62; folders.AccessibleName = "共享文件夹列表";
            folders.DrawItem += DrawFolder; folders.SelectedIndexChanged += delegate { removeFolder.Enabled = folders.SelectedItem != null; tips.SetToolTip(folders, folders.SelectedItem as string); };
            folders.DoubleClick += delegate { OpenSelectedFolder(); };
            folders.MouseMove += delegate(object sender, MouseEventArgs e) { int index = folders.IndexFromPoint(e.Location); string path = index < 0 ? "" : (string)folders.Items[index]; if (tips.GetToolTip(folders) != path) tips.SetToolTip(folders, path); };
            Theme.ThemeScrollbars(folders); listFrame.Controls.Add(folders);
            emptyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
            emptyPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; float scale = g.DpiX / 96F;
                float chipSize = 48F * scale;
                float cx = emptyPanel.Width / 2F, cy = Math.Max(chipSize / 2F + 8F * scale, emptyPanel.Height / 2F - 22F * scale);
                RectangleF chip = new RectangleF(cx - chipSize / 2F, cy - chipSize / 2F, chipSize, chipSize);
                using (GraphicsPath path = Theme.RoundedF(chip, 14F * scale)) using (SolidBrush brush = new SolidBrush(Theme.Ice)) g.FillPath(brush, path);
                float inset = 11F * scale;
                Theme.Glyph(g, "folder", new RectangleF(chip.Left + inset, chip.Top + inset, chip.Width - inset * 2F, chip.Height - inset * 2F), Theme.Muted);
                Rectangle titleRect = new Rectangle(16, (int)(chip.Bottom + 10F * scale), Math.Max(1, emptyPanel.Width - 32), (int)(22 * scale));
                TextRenderer.DrawText(g, "暂无共享文件夹", Theme.Section, titleRect, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                Rectangle subRect = new Rectangle(16, titleRect.Bottom + (int)(2 * scale), Math.Max(1, emptyPanel.Width - 32), (int)(20 * scale));
                TextRenderer.DrawText(g, "点击「添加文件夹」选择需要局域网只读共享的本地目录", Theme.Small, subRect, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            listFrame.Controls.Add(emptyPanel); body.Controls.Add(listFrame, 0, 2);

            tips.SetToolTip(addFolder, "支持多选；增减目录后需重新配对。");
            tips.SetToolTip(removeFolder, "仅取消共享，不删除文件；现有连接会断开。");
            tips.SetToolTip(startShare, "连接码有效 10 分钟；更新后现有连接会断开。");
            tips.SetToolTip(shareCode, "共享连接码 · 有效 10 分钟");

            // 区块二：远程控制卡片（所有卡片内按钮均使用 Surface 底 + Line 描边，绝不与卡片底色融在一起）
            RoundedPanel remoteCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Theme.Line, Radius = 12, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 10) };
            TableLayoutPanel remoteBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Ice, Margin = Padding.Empty };
            remoteBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); remoteBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); remoteBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            TableLayoutPanel remoteHeader = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            remoteHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); remoteHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); remoteHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel remoteTitleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
            Label remoteTitle = Theme.Label("远程控制", Theme.Strong, Theme.Ink); remoteTitle.AutoSize = true; remoteTitle.Margin = new Padding(0, 4, 10, 0); remoteTitleRow.Controls.Add(remoteTitle);
            remotePill = new StatusPill { Margin = new Padding(0, 2, 8, 0) }; remoteTitleRow.Controls.Add(remotePill);
            remoteCode = Theme.Label("", codeFont, Theme.Blue); remoteCode.AutoSize = true; remoteCode.Margin = new Padding(4, 0, 0, 0); remoteTitleRow.Controls.Add(remoteCode);
            remoteHeader.Controls.Add(remoteTitleRow, 0, 0);
            Label remoteHint = Theme.Label("Ctrl+Alt+F12 随时停止", Theme.Small, Theme.Muted); remoteHint.Dock = DockStyle.Fill; remoteHint.TextAlign = ContentAlignment.MiddleRight; remoteHint.Margin = Padding.Empty;
            remoteHeader.Controls.Add(remoteHint, 1, 0);
            remoteBody.Controls.Add(remoteHeader, 0, 0);
            tips.SetToolTip(remotePill, "连接需本机确认，已信任的设备除外。Ctrl+Alt+F12 停止。");
            FlowLayoutPanel remoteActions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 0), WrapContents = false, BackColor = Theme.Ice };
            AddButton(remoteActions, "生成远控连接码", delegate { ChangeSettings(service.AllowRemote); }, false, 148);
            stopControl = AddButton(remoteActions, "立即停止远控", StopRemote, false, 134);
            remoteBody.Controls.Add(remoteActions, 0, 1); remoteCard.Controls.Add(remoteBody); body.Controls.Add(remoteCard, 0, 3);

            // 底栏：顶部 1px 细线 + 充足上下间距，彻底避免按钮贴底裁切
            Panel footer = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 2), BackColor = Theme.Surface };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen line = new Pen(Theme.Line)) e.Graphics.DrawLine(line, 0, 0, footer.Width, 0); };
            Button forget = Theme.Button("清除已配对设备", delegate { using (ConfirmDialog prompt = new ConfirmDialog("清除已配对设备？", "将断开连接并撤销信任，下次需重新配对。", "清除配对", true)) if (prompt.ShowDialog(this) == DialogResult.OK) ChangeSettings(service.ForgetPairings); }, false);
            StyleCardButton(forget, false); forget.Dock = DockStyle.Right; forget.Width = 148; forget.Margin = Padding.Empty; footer.Controls.Add(forget);
            connection = Theme.Label("", Theme.Small, Theme.Muted); connection.Dock = DockStyle.Fill; connection.AutoEllipsis = true; connection.Padding = new Padding(0, 0, 12, 0); footer.Controls.Add(connection); connection.BringToFront();
            body.Controls.Add(footer, 0, 4);

            Shown += delegate { using (Graphics graphics = folders.CreateGraphics()) folders.ItemHeight = (int)(62 * graphics.DpiX / 96F); UpdateState(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.DisplaySettingsChanging += OnDisplayChange;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            timer.Tick += delegate { if (!service.RemoteActive) desktop.Release(); UpdateState(); }; timer.Start(); UpdateState();
            PrepareRestoredWindow();
            try { restoreError = service.RestoreSettings(); }
            catch (System.IO.IOException e) { restoreError = e.Message; }
            catch (UnauthorizedAccessException e) { restoreError = e.Message; }
            UpdateState();
        }
        private static void StyleCardButton(Button button, bool accentSoft)
        {
            RoundedButton rounded = button as RoundedButton;
            button.BackColor = accentSoft ? Theme.Sky : Theme.Surface;
            button.ForeColor = accentSoft ? Theme.Blue : Theme.Ink;
            if (rounded != null) rounded.Border = accentSoft ? Theme.Blend(Theme.Blue, Theme.Line, .35F) : Theme.Line;
        }
        internal void PrepareRestoredWindow() { if (!IsHandleCreated) CreateHandle(); }
        internal void NetworkReset(){service.NetworkReset();}
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); hotkey = RegisterHotKey(Handle, 941, 0x4003, 0x7b); }
        private void ChangeSettings(Action change)
        {
            try { change(); restoreError = null; UpdateState(); }
            catch (Exception e) { if (!(e is System.IO.IOException || e is UnauthorizedAccessException || e is ToolsHttp.HttpError || e is ArgumentException)) throw; UpdateState(); using (NoticeDialog notice = new NoticeDialog("操作未完成", e.Message)) notice.ShowDialog(this); }
        }
        private static Button AddButton(FlowLayoutPanel row, string text, Action action, bool primary, int width)
        {
            Button button = Theme.Button(text, delegate { action(); }, primary);
            if (!primary) StyleCardButton(button, false);
            button.Size = new Size(width, 36); button.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(button); return button;
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
        private void OpenSelectedFolder()
        {
            string path = folders.SelectedItem as string;
            if (string.IsNullOrEmpty(path)) return;
            if (!Directory.Exists(path)) { using (NoticeDialog notice = new NoticeDialog("无法打开文件夹", "文件夹不存在或已被移动：\n" + path)) notice.ShowDialog(this); return; }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { if (!(e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is IOException || e is UnauthorizedAccessException)) throw; using (NoticeDialog notice = new NoticeDialog("无法打开文件夹", e.Message)) notice.ShowDialog(this); }
        }
        private void DrawFolder(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= folders.Items.Count) return;
            float scale = e.Graphics.DpiX / 96F; string path = (string)folders.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush back = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(back, e.Bounds);
            RectangleF card = new RectangleF(e.Bounds.Left + 2F * scale, e.Bounds.Top + 3F * scale, Math.Max(4F, e.Bounds.Width - 4F * scale), Math.Max(4F, e.Bounds.Height - 6F * scale));
            using (GraphicsPath cardPath = Theme.RoundedF(card, 9F * scale))
            {
                using (Brush fill = new SolidBrush(selected ? Theme.Sky : Theme.Ice)) e.Graphics.FillPath(fill, cardPath);
                using (Pen border = new Pen(selected ? Theme.Blend(Theme.Blue, Theme.Surface, .48F) : Theme.Line)) e.Graphics.DrawPath(border, cardPath);
            }
            RectangleF badge = new RectangleF(card.Left + 11F * scale, card.Top + (card.Height - 36F * scale) / 2F, 36F * scale, 36F * scale);
            using (GraphicsPath badgePath = Theme.RoundedF(badge, 9F * scale))
            using (Brush badgeFill = new SolidBrush(selected ? Theme.Surface : Theme.Outgoing))
            {
                e.Graphics.FillPath(badgeFill, badgePath);
            }
            float inset = 8F * scale;
            Theme.Glyph(e.Graphics, "folder", new RectangleF(badge.Left + inset, badge.Top + inset, badge.Width - inset * 2F, badge.Height - inset * 2F), Theme.Blue);
            int hintWidth = e.Bounds.Width >= (int)(360 * scale) ? (int)(74 * scale) : 0;
            int textLeft = (int)(badge.Right + 12F * scale);
            int textWidth = Math.Max(1, (int)(card.Right - textLeft - 12F * scale - hintWidth));
            Rectangle text = new Rectangle(textLeft, (int)(card.Top + 8F * scale), textWidth, (int)(22 * scale));
            TextRenderer.DrawText(e.Graphics, ToolsService.FolderName(path), Theme.Section, text, Theme.Ink, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            text.Y += (int)(21 * scale); text.Height = (int)(18 * scale); TextRenderer.DrawText(e.Graphics, path, Theme.Small, text, Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
            if (hintWidth > 0)
            {
                RectangleF chip = new RectangleF(card.Right - hintWidth - 10F * scale, card.Top + (card.Height - 24F * scale) / 2F, hintWidth, 24F * scale);
                if (selected) using (GraphicsPath chipPath = Theme.RoundedF(chip, 12F * scale)) using (Brush chipFill = new SolidBrush(Theme.Surface)) e.Graphics.FillPath(chipFill, chipPath);
                TextRenderer.DrawText(e.Graphics, "双击打开", Theme.Small, Rectangle.Round(chip), selected ? Theme.Blue : Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
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
            banner.Show();
        }
        private void StopRemote() { ChangeSettings(service.StopRemote); }
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionLogoff || e.Reason == SessionSwitchReason.ConsoleDisconnect || e.Reason == SessionSwitchReason.RemoteDisconnect)
            {
                systemSuspended = true; StopForSystemChange();
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon || e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.RemoteConnect)
            { systemSuspended = false; ResumeForSystemChange(); }
        }
        private bool systemSuspended;
        private void OnDisplayChange(object sender, EventArgs e) { StopForSystemChange(); }
        private void OnDisplayChanged(object sender, EventArgs e) { if (!systemSuspended) ResumeForSystemChange(); }
        private void StopForSystemChange()
        {
            service.SuspendRemote();
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)UpdateState);
        }
        private void ResumeForSystemChange() { if (!IsDisposed && !Disposing) { service.ResumeRemote(); if (IsHandleCreated) BeginInvoke((Action)UpdateState); } }
        private void UpdateState()
        {
            bool active = service.RemoteActive;
            if (active && banner == null) ShowBanner();
            string[] saved = service.SavedFolders; bool sharing = service.Folders.Length > 0; string code = service.ShareCode;
            bool validShareCode = sharing && code.Length == 6;
            if (sharing && !validShareCode) sharePill.Set("共享中 · 连接码已过期", Theme.Danger);
            else if (sharing) sharePill.Set("共享中 · " + saved.Length + " 个文件夹", Theme.Online);
            else if (saved.Length == 0) sharePill.Set("未共享", Theme.Muted);
            else sharePill.Set("已停止 · " + saved.Length + " 个文件夹", Theme.Muted);
            shareCode.Text = validShareCode ? code : "—";
            shareCode.ForeColor = validShareCode ? Theme.Ink : Theme.Muted;
            copyCode.Enabled = validShareCode;
            startShare.Enabled = saved.Length > 0; startShare.Text = sharing ? "更新连接码" : "开启共享";
            stopShare.Enabled = sharing; stopControl.Enabled = service.RemoteEnabled;
            RoundedButton stopControlButton = stopControl as RoundedButton;
            stopControl.ForeColor = service.RemoteEnabled ? Theme.Danger : Theme.Ink;
            if (stopControlButton != null) stopControlButton.Border = service.RemoteEnabled ? Theme.Blend(Theme.Danger, Theme.Line, .45F) : Theme.Line;
            if (!folders.Items.Cast<string>().SequenceEqual(saved))
            {
                string selected = folders.SelectedItem as string; int index = folders.SelectedIndex;
                folders.BeginUpdate(); folders.Items.Clear(); folders.Items.AddRange(saved);
                if (saved.Length > 0) folders.SelectedIndex = selected != null && Array.IndexOf(saved, selected) >= 0 ? Array.IndexOf(saved, selected) : Math.Min(Math.Max(0, index), saved.Length - 1);
                folders.EndUpdate();
            }
            removeFolder.Enabled = folders.SelectedItem != null; emptyPanel.Visible = saved.Length == 0; folders.Visible = saved.Length > 0;
            if (active) { remotePill.Set("已连接 · " + service.RemoteAddress, Theme.Online); remoteCode.Text = ""; }
            else if (!service.RemoteEnabled) { remotePill.Set("未开启", Theme.Muted); remoteCode.Text = ""; }
            else if (service.RemoteCode.Length == 6) { remotePill.Set("等待连接 · 连接码", Theme.Blue); remoteCode.Text = service.RemoteCode; }
            else { remotePill.Set("等待已配对设备", Theme.Blue); remoteCode.Text = ""; }
            connection.Text = restoreError ?? "TCP " + service.Port + " · 关闭窗口后继续运行"; connection.ForeColor = restoreError == null ? Theme.Muted : Theme.Danger; tips.SetToolTip(connection, restoreError ?? "关闭窗口只隐藏，需在此处或远控浮条手动停止。已配对设备可在电脑重启后连接上次。");
            if (!active && banner != null) { Form old = banner; banner = null; old.Dispose(); }
        }
        protected override void WndProc(ref Message message) { if (message.Msg == 0x312 && message.WParam.ToInt32() == 941) StopRemote(); base.WndProc(ref message); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            // 这是常驻服务的设置窗口；关闭面板不应撤销连接码或中断当前控制。
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Stop(); timer.Dispose(); tips.Dispose(); codeFont.Dispose(); SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.DisplaySettingsChanging -= OnDisplayChange; SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; if (hotkey) UnregisterHotKey(Handle, 941); service.Dispose(); if (banner != null) { banner.Dispose(); banner = null; } }
            base.Dispose(disposing);
        }
    }
    internal sealed class RemoteBanner : Form
    {
        private readonly ToolTip tips = new ToolTip();
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams value = base.CreateParams; value.ExStyle = (value.ExStyle & ~0x00000080) | 0x08000000 | 0x00040000; value.Style |= 0x00020000 | 0x00080000; return value; } }
        internal RemoteBanner(string address, Action disconnect)
        {
            Text = "飞Q远程控制"; Icon = Theme.AppIcon; FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = true; AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(310, 68); StartPosition = FormStartPosition.Manual; BackColor = Theme.Surface; Padding = new Padding(14, 10, 10, 10); DoubleBuffered = true;
            TableLayoutPanel row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Panel dot = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; dot.Paint += delegate(object sender, PaintEventArgs e) { e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; using (Brush brush = new SolidBrush(Theme.Online)) e.Graphics.FillEllipse(brush, 0, (dot.Height - 8) / 2, 8, 8); }; row.Controls.Add(dot, 0, 0);
            TableLayoutPanel info = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty }; info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); info.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); info.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            foreach (Label label in new[] { Theme.Label("远程控制中", Theme.Strong, Theme.Ink), Theme.Label(address, Theme.Small, Theme.Muted) })
            {
                label.Dock = DockStyle.Fill; label.Margin = Padding.Empty; label.AutoEllipsis = true; label.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } }; info.Controls.Add(label);
            }
            row.Controls.Add(info, 1, 0);
            Button minimize = Theme.IconButton("min", "最小化到任务栏", delegate { MinimizeBanner(); }, false);
            minimize.Dock = DockStyle.Fill; minimize.Margin = new Padding(2, 6, 4, 6); row.Controls.Add(minimize, 2, 0);
            tips.SetToolTip(minimize, "最小化到任务栏，远控继续；点击任务栏恢复");
            Button stop = Theme.IconButton("power", "断开远控", delegate { disconnect(); }, false); stop.ForeColor = Theme.Danger;
            stop.Dock = DockStyle.Fill; stop.Margin = new Padding(2, 6, 4, 6); row.Controls.Add(stop, 3, 0); Controls.Add(row);
            tips.SetToolTip(stop, "断开远控（Ctrl+Alt+F12）");
        }
        private void MinimizeBanner() { WindowState = FormWindowState.Minimized; }
        internal void RestoreBanner() { Show(); WindowState = FormWindowState.Normal; }
        protected override void OnResize(EventArgs e) { base.OnResize(e); TopMost = WindowState != FormWindowState.Minimized; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen border = new Pen(Theme.Line)) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); }
        protected override void OnFormClosing(FormClosingEventArgs e) { base.OnFormClosing(e); if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; MinimizeBanner(); } }
        protected override void Dispose(bool disposing) { if (disposing) tips.Dispose(); base.Dispose(disposing); }
        protected override void WndProc(ref Message message) { if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } base.WndProc(ref message); }
    }
}
