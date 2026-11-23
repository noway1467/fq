using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight
{
    internal sealed class GuestWindow : ModernWindow
    {
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
                    Width = Math.Max((int)(68 * scale), textW + (int)(28 * scale));
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

        private sealed class ShareEntry
        {
            internal string FullPath, Name, Folder, Extension, Kind;
            internal long Size;
            internal static ShareEntry FromPath(string path)
            {
                FileInfo info = new FileInfo(path);
                string ext = (info.Extension ?? "").TrimStart('.').ToUpperInvariant();
                return new ShareEntry {
                    FullPath = info.FullName,
                    Name = info.Name,
                    Folder = info.DirectoryName ?? "",
                    Extension = ext.Length == 0 ? "FILE" : ext.Length > 4 ? ext.Substring(0, 4) : ext,
                    Kind = Classify(ext),
                    Size = info.Exists ? info.Length : 0
                };
            }
            private static string Classify(string ext)
            {
                switch (ext)
                {
                    case "PNG": case "JPG": case "JPEG": case "GIF": case "WEBP": case "BMP": case "SVG": case "HEIC": return "image";
                    case "MP4": case "MKV": case "WEBM": case "MOV": case "AVI": case "FLV": case "WMV": return "video";
                    case "MP3": case "WAV": case "FLAC": case "AAC": case "OGG": case "M4A": return "audio";
                    case "ZIP": case "RAR": case "7Z": case "TAR": case "GZ": case "XZ": return "archive";
                    case "PDF": case "DOC": case "DOCX": case "XLS": case "XLSX": case "PPT": case "PPTX": case "TXT": case "MD": return "doc";
                    case "APK": case "EXE": case "MSI": return "app";
                    default: return "other";
                }
            }
            public override string ToString() { return FullPath; }
        }

        private readonly MainForm main;
        private readonly LanService network;
        private readonly StatusPill statusPill = new StatusPill();
        private readonly Label fileSummary = Theme.Label("0 个文件", Theme.Small, Theme.Muted);
        private readonly Label state = Theme.Label("就绪", Theme.Small, Theme.Muted);
        private readonly Label linkCaption = Theme.Label("访问链接 · HTTP 局域网直连", Theme.Small, Theme.Muted);
        private readonly TextBox linkBox = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None };
        private readonly ListBox files = new ListBox();
        private readonly Panel emptyPanel = new Panel();
        private readonly RoundedPanel summaryCard, listCard, configCard, addressShell, linkCard;
        private readonly ComboBox address = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed };
        private readonly ToggleSwitch upload;
        private readonly Button start, choose, removeBtn, clearBtn, refreshIp, copy, qr, openTasks;
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly Timer copyReset = new Timer { Interval = 1800 };
        private readonly ToolTip tips = new ToolTip();
        private readonly Font monoFont = new Font("Consolas", 9.5F);
        private GuestTransfer session;
        private bool stateError;

        internal GuestWindow(MainForm main, LanService network)
        {
            this.main = main;
            this.network = network;
            Text = "临时浏览器传输";
            ClientSize = new Size(720, 588);
            MinimumSize = new Size(620, 492);
            UseSecondaryChrome();

            var body = new BufferedLayoutTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(22, 12, 22, 14), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int h in new[] { 70, -1, 112, 82, 50 }) body.RowStyles.Add(new RowStyle(h < 0 ? SizeType.Percent : SizeType.Absolute, h < 0 ? 100 : h));
            Surface.Controls.Add(body);

            // 区块一：状态摘要卡片
            summaryCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Theme.Line, Radius = 12, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 0, 0, 10) };
            var summaryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summaryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            summaryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            var titleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            Label heading = Theme.Label("临时浏览器传输", Theme.Strong, Theme.Ink);
            heading.AutoSize = true;
            heading.Margin = new Padding(0, 3, 10, 0);
            titleRow.Controls.Add(heading);
            statusPill.Margin = new Padding(0, 1, 0, 0);
            titleRow.Controls.Add(statusPill);
            summaryLayout.Controls.Add(titleRow, 0, 0);
            Label desc = Theme.Label("浏览器扫码或打开链接即可收发 · 15 分钟有效 · HTTP 明文仅限可信局域网", Theme.Small, Theme.Muted);
            desc.Dock = DockStyle.Fill;
            desc.AutoEllipsis = true;
            desc.Margin = Padding.Empty;
            summaryLayout.Controls.Add(desc, 0, 1);
            summaryCard.Controls.Add(summaryLayout);
            body.Controls.Add(summaryCard, 0, 0);

            // 区块二：待分享文件列表卡片
            listCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Surface, Border = Theme.Line, Radius = 12, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 0, 0, 10) };
            var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            listLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var listHeader = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = new Padding(2, 0, 0, 6) };
            listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 256));
            listHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var listMeta = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            Label listTitle = Theme.Label("待分享文件", Theme.Section, Theme.Ink);
            listTitle.AutoSize = true;
            listTitle.Margin = new Padding(0, 6, 10, 0);
            listMeta.Controls.Add(listTitle);
            fileSummary.AutoSize = true;
            fileSummary.Margin = new Padding(0, 8, 0, 0);
            listMeta.Controls.Add(fileSummary);
            listHeader.Controls.Add(listMeta, 0, 0);

            var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            clearBtn = Theme.Button("清空", delegate { ClearFiles(); }, false);
            clearBtn.Size = new Size(64, 32); clearBtn.Margin = Padding.Empty; StyleCardButton(clearBtn, false);
            removeBtn = Theme.Button("移除", delegate { RemoveSelectedFile(); }, false);
            removeBtn.Size = new Size(64, 32); removeBtn.Margin = new Padding(0, 0, 6, 0); StyleCardButton(removeBtn, false);
            choose = Theme.Button("添加文件", delegate { PickFiles(); }, false);
            choose.Size = new Size(104, 32); choose.Margin = new Padding(0, 0, 6, 0); StyleCardButton(choose, true);
            listButtons.Controls.Add(clearBtn);
            listButtons.Controls.Add(removeBtn);
            listButtons.Controls.Add(choose);
            listHeader.Controls.Add(listButtons, 1, 0);
            listLayout.Controls.Add(listHeader, 0, 0);

            var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
            files.Dock = DockStyle.Fill;
            files.BorderStyle = BorderStyle.None;
            files.DrawMode = DrawMode.OwnerDrawFixed;
            files.ItemHeight = 56;
            files.IntegralHeight = false;
            files.AccessibleName = "临时分享文件";
            files.DrawItem += DrawFileItem;
            files.SelectedIndexChanged += delegate { UpdateActions(); };
            files.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Delete && session == null) { e.Handled = true; RemoveSelectedFile(); } };
            Theme.ThemeScrollbars(files);
            listHost.Controls.Add(files);

            emptyPanel.Dock = DockStyle.Fill;
            emptyPanel.BackColor = Theme.Surface;
            emptyPanel.Cursor = Cursors.Hand;
            emptyPanel.Click += delegate { if (session == null) PickFiles(); };
            emptyPanel.Paint += PaintEmptyFiles;
            listHost.Controls.Add(emptyPanel);
            emptyPanel.BringToFront();
            listLayout.Controls.Add(listHost, 0, 1);
            listCard.Controls.Add(listLayout);
            body.Controls.Add(listCard, 0, 1);

            EnableFileDrop(listCard);
            EnableFileDrop(listHost);
            EnableFileDrop(files);
            EnableFileDrop(emptyPanel);

            // 区块三：接收开关与局域网地址卡片
            configCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Ice, Border = Theme.Line, Radius = 12, Padding = new Padding(16, 6, 16, 8), Margin = new Padding(0, 0, 0, 10) };
            var configLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            configLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            configLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            configLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            configLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            upload = new ToggleSwitch { Dock = DockStyle.Fill, BackColor = Theme.Ice, Text = "允许对方向我发送文件", AccessibleName = "允许浏览器上传", Description = "保存至接收目录 · 限 100 个文件 / 10 GiB", Checked = false, Margin = Padding.Empty };
            configLayout.Controls.Add(upload, 0, 0);

            var divider = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Ice, Margin = Padding.Empty };
            divider.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 0, divider.Height / 2, divider.Width, divider.Height / 2); };
            configLayout.Controls.Add(divider, 0, 1);

            var networkRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            networkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            networkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            networkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            networkRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            Label netTitle = Theme.Label("局域网地址", Theme.Section, Theme.Ink); netTitle.Dock = DockStyle.Fill; netTitle.Margin = new Padding(0, 0, 10, 0);
            networkRow.Controls.Add(netTitle, 0, 0);

            addressShell = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Theme.Ice, Fill = Theme.Surface, Border = Theme.Line, Radius = Theme.ControlRadius, Padding = new Padding(8, 5, 6, 4), Margin = new Padding(0, 4, 6, 4) };
            address.Dock = DockStyle.Fill;
            address.Font = monoFont;
            address.AccessibleName = "临时传输网络地址";
            address.ItemHeight = 22;
            address.DrawItem += DrawAddressItem;
            address.SelectedIndexChanged += delegate { UpdateLinkPreview(); };
            addressShell.Controls.Add(address);
            networkRow.Controls.Add(addressShell, 1, 0);

            refreshIp = Theme.IconButton("refresh", "刷新局域网地址", delegate { ReloadAddresses(); }, false);
            refreshIp.Dock = DockStyle.Fill;
            refreshIp.Margin = new Padding(0, 4, 0, 4);
            StyleCardButton(refreshIp, false);
            tips.SetToolTip(refreshIp, "重新扫描本机局域网地址");
            networkRow.Controls.Add(refreshIp, 2, 0);
            configLayout.Controls.Add(networkRow, 0, 2);
            configCard.Controls.Add(configLayout);
            body.Controls.Add(configCard, 0, 2);

            // 区块四：访问链接与二维码卡片
            linkCard = new RoundedPanel { Dock = DockStyle.Fill, Fill = Theme.Surface, Border = Theme.Line, Radius = 12, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
            var linkLayout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            linkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            linkLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 218));
            linkLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var linkTextCol = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
            linkTextCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            linkTextCol.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            linkTextCol.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            linkCaption.Dock = DockStyle.Fill; linkCaption.AutoEllipsis = true; linkCaption.Margin = Padding.Empty;
            linkTextCol.Controls.Add(linkCaption, 0, 0);
            linkBox.Dock = DockStyle.Fill;
            linkBox.Font = monoFont;
            linkBox.Margin = new Padding(0, 6, 0, 0);
            linkBox.AccessibleName = "临时传输链接";
            Theme.AttachEditMenu(linkBox);
            linkTextCol.Controls.Add(linkBox, 0, 1);
            linkLayout.Controls.Add(linkTextCol, 0, 0);

            var linkActions = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 9, 0, 0) };
            qr = Theme.Button("显示二维码", delegate { OpenQr(); }, false);
            qr.Size = new Size(106, 36); qr.Margin = Padding.Empty; StyleCardButton(qr, false);
            copy = Theme.Button("复制链接", delegate { CopyLink(); }, false);
            copy.Size = new Size(100, 36); copy.Margin = new Padding(0, 0, 8, 0); StyleCardButton(copy, true);
            linkActions.Controls.Add(qr);
            linkActions.Controls.Add(copy);
            linkLayout.Controls.Add(linkActions, 1, 0);
            linkCard.Controls.Add(linkLayout);
            body.Controls.Add(linkCard, 0, 3);

            // 底栏：状态与主操作按钮
            var footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 12, 0, 2) };
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

            var footerRight = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
            start = Theme.Button("开启临时传输", delegate { Toggle(); }, true);
            start.Size = new Size(138, 36); start.Margin = Padding.Empty;
            openTasks = Theme.Button("查看已接收", delegate { main.OpenTransfers(); }, false);
            openTasks.Size = new Size(108, 36); openTasks.Margin = new Padding(0, 0, 8, 0); StyleCardButton(openTasks, false); openTasks.Visible = false;
            footerRight.Controls.Add(start);
            footerRight.Controls.Add(openTasks);
            footer.Controls.Add(footerRight);

            state.Dock = DockStyle.Fill;
            state.AutoEllipsis = true;
            state.Padding = new Padding(0, 0, 12, 0);
            footer.Controls.Add(state);
            state.BringToFront();
            body.Controls.Add(footer, 0, 4);

            ReloadAddresses();
            Shown += delegate { ApplyColors(); UpdateActions(); };
            Theme.Changed += ApplyColors;
            copyReset.Tick += delegate { copyReset.Stop(); if (!IsDisposed) copy.Text = "复制链接"; };
            timer.Tick += delegate {
                if (session != null && !session.Active) { Stop(); SetState("本次临时传输已到期结束", false); }
                else if (session != null) { SetState(StateText(), false); UpdateActions(); }
            };
            timer.Start();
            ApplyColors();
            UpdateActions();
        }

        private static void StyleCardButton(Button button, bool accentSoft)
        {
            RoundedButton rounded = button as RoundedButton;
            button.BackColor = accentSoft ? Theme.Sky : Theme.Surface;
            button.ForeColor = accentSoft ? Theme.Blue : Theme.Ink;
            if (rounded != null) rounded.Border = accentSoft ? Theme.Blend(Theme.Blue, Theme.Line, .35F) : Theme.Line;
        }

        private void ApplyColors()
        {
            summaryCard.Fill = configCard.Fill = Theme.Ice;
            summaryCard.Border = listCard.Border = configCard.Border = addressShell.Border = Theme.Line;
            listCard.Fill = addressShell.Fill = Theme.Surface;
            files.BackColor = emptyPanel.BackColor = Theme.Surface;
            files.ForeColor = Theme.Ink;
            address.BackColor = Theme.Surface;
            address.ForeColor = Theme.Ink;
            upload.BackColor = Theme.Ice;
            linkBox.BackColor = Theme.Surface;
            linkBox.ForeColor = session != null && session.Active ? Theme.Ink : Theme.Muted;
            fileSummary.ForeColor = Theme.Muted;
            state.ForeColor = stateError ? Theme.Danger : Theme.Muted;
            StyleCardButton(choose, true);
            StyleCardButton(removeBtn, false);
            StyleCardButton(clearBtn, false);
            StyleCardButton(refreshIp, false);
            StyleCardButton(copy, true);
            StyleCardButton(qr, false);
            StyleCardButton(openTasks, false);
            using (Graphics g = files.CreateGraphics())
            {
                float scale = g.DpiX / 96F;
                files.ItemHeight = (int)Math.Round(56 * scale);
                address.ItemHeight = (int)Math.Round(22 * scale);
            }
            UpdateLinkCardStyle();
            files.Invalidate();
            emptyPanel.Invalidate();
            address.Invalidate();
        }

        private void UpdateLinkCardStyle()
        {
            bool active = session != null && session.Active;
            linkCard.Fill = Theme.Surface;
            linkCard.Border = active ? Theme.Blend(Theme.Blue, Theme.Line, .48F) : Theme.Line;
            linkCaption.ForeColor = active ? Theme.Blue : Theme.Muted;
            linkBox.ForeColor = active ? Theme.Ink : Theme.Muted;
            linkCard.Invalidate();
        }

        private void ReloadAddresses()
        {
            string previous = address.SelectedItem as string;
            string[] items = network.LoopbackOnly
                ? new[] { "127.0.0.1" }
                : LanNetwork.Scan().Select(l => l.Address.ToString()).Distinct().ToArray();
            address.BeginUpdate();
            address.Items.Clear();
            address.Items.AddRange(items);
            if (previous != null && items.Contains(previous)) address.SelectedItem = previous;
            else if (address.Items.Count > 0) address.SelectedIndex = 0;
            address.EndUpdate();
            UpdateLinkPreview();
        }

        private void PickFiles()
        {
            if (session != null) return;
            using (var picker = new OpenFileDialog { Multiselect = true, Title = "选择临时分享的文件" })
                if (picker.ShowDialog(this) == DialogResult.OK) AddFiles(picker.FileNames);
        }

        private void AddFiles(IEnumerable<string> paths)
        {
            if (session != null) return;
            var existing = new HashSet<string>(files.Items.Cast<ShareEntry>().Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            files.BeginUpdate();
            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw) || !File.Exists(raw) || Directory.Exists(raw)) { skipped++; continue; }
                string full = Path.GetFullPath(raw);
                if (!existing.Add(full)) continue;
                if (files.Items.Count >= 1000) { SetState("单次最多选择 1000 个文件", true); break; }
                files.Items.Add(ShareEntry.FromPath(full));
            }
            files.EndUpdate();
            if (skipped > 0) SetState("已忽略文件夹或不可读取的条目，仅支持普通文件", false);
            else SetState("已就绪，点击「开启临时传输」生成链接", false);
            UpdateActions();
        }

        private void RemoveSelectedFile()
        {
            if (session != null || files.SelectedIndex < 0) return;
            int index = files.SelectedIndex;
            files.Items.RemoveAt(index);
            if (files.Items.Count > 0) files.SelectedIndex = Math.Min(index, files.Items.Count - 1);
            UpdateActions();
        }

        private void ClearFiles()
        {
            if (session != null || files.Items.Count == 0) return;
            files.Items.Clear();
            UpdateActions();
        }

        private void EnableFileDrop(Control target)
        {
            target.AllowDrop = true;
            target.DragEnter += (sender, e) => {
                bool ok = session == null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            };
            target.DragDrop += (sender, e) => {
                if (session != null || e.Data == null) return;
                string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped != null && dropped.Length > 0) AddFiles(dropped);
            };
        }

        private void PaintEmptyFiles(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96F;
            float chipSize = 44F * scale;
            float cx = emptyPanel.Width / 2F;
            float cy = Math.Max(chipSize / 2F + 6F * scale, emptyPanel.Height / 2F - 20F * scale);
            RectangleF chip = new RectangleF(cx - chipSize / 2F, cy - chipSize / 2F, chipSize, chipSize);
            using (GraphicsPath path = Theme.RoundedF(chip, 12F * scale))
            using (SolidBrush brush = new SolidBrush(Theme.Ice)) g.FillPath(brush, path);
            float inset = 10F * scale;
            Theme.Glyph(g, "file", new RectangleF(chip.Left + inset, chip.Top + inset, chip.Width - inset * 2F, chip.Height - inset * 2F), Theme.Blue);
            Rectangle titleRect = new Rectangle(16, (int)(chip.Bottom + 8F * scale), Math.Max(1, emptyPanel.Width - 32), (int)(22 * scale));
            TextRenderer.DrawText(g, "暂无待分享文件", Theme.Section, titleRect, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            Rectangle subRect = new Rectangle(16, titleRect.Bottom + (int)(2 * scale), Math.Max(1, emptyPanel.Width - 32), (int)(20 * scale));
            TextRenderer.DrawText(g, "点击添加或拖入文件（也可仅开启接收）", Theme.Small, subRect, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static void KindColors(string kind, out Color fill, out Color ink)
        {
            bool dark = Theme.Dark;
            switch (kind)
            {
                case "image": fill = dark ? Color.FromArgb(61, 26, 44) : Color.FromArgb(252, 231, 243); ink = dark ? Color.FromArgb(249, 168, 212) : Color.FromArgb(190, 24, 93); break;
                case "video": fill = dark ? Color.FromArgb(43, 33, 69) : Color.FromArgb(237, 233, 254); ink = dark ? Color.FromArgb(196, 181, 253) : Color.FromArgb(109, 40, 217); break;
                case "audio": fill = dark ? Color.FromArgb(18, 53, 41) : Color.FromArgb(209, 250, 229); ink = dark ? Color.FromArgb(110, 231, 183) : Color.FromArgb(4, 120, 87); break;
                case "archive": fill = dark ? Color.FromArgb(60, 34, 17) : Color.FromArgb(255, 237, 213); ink = dark ? Color.FromArgb(253, 186, 116) : Color.FromArgb(194, 65, 12); break;
                case "doc": fill = dark ? Color.FromArgb(21, 49, 68) : Color.FromArgb(224, 242, 254); ink = dark ? Color.FromArgb(125, 211, 252) : Color.FromArgb(3, 105, 161); break;
                case "app": fill = dark ? Color.FromArgb(22, 51, 34) : Color.FromArgb(220, 252, 231); ink = dark ? Color.FromArgb(134, 239, 172) : Color.FromArgb(21, 128, 61); break;
                default: fill = Theme.Sky; ink = Theme.Blue; break;
            }
        }

        private void DrawFileItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= files.Items.Count) return;
            ShareEntry entry = (ShareEntry)files.Items[e.Index];
            float scale = e.Graphics.DpiX / 96F;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Brush back = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(back, e.Bounds);

            RectangleF card = new RectangleF(e.Bounds.Left + 2F * scale, e.Bounds.Top + 3F * scale, Math.Max(4F, e.Bounds.Width - 4F * scale), Math.Max(4F, e.Bounds.Height - 6F * scale));
            using (GraphicsPath cardPath = Theme.RoundedF(card, 9F * scale))
            {
                using (Brush fill = new SolidBrush(selected ? Theme.Sky : Theme.Ice)) e.Graphics.FillPath(fill, cardPath);
                using (Pen border = new Pen(selected ? Theme.Blend(Theme.Blue, Theme.Surface, .48F) : Theme.Line)) e.Graphics.DrawPath(border, cardPath);
            }

            Color badgeFill, badgeInk;
            KindColors(entry.Kind, out badgeFill, out badgeInk);
            RectangleF badge = new RectangleF(card.Left + 10F * scale, card.Top + (card.Height - 34F * scale) / 2F, 34F * scale, 34F * scale);
            using (GraphicsPath badgePath = Theme.RoundedF(badge, 8F * scale))
            using (Brush fill = new SolidBrush(badgeFill)) e.Graphics.FillPath(fill, badgePath);
            TextRenderer.DrawText(e.Graphics, entry.Extension, Theme.Small, Rectangle.Round(badge), badgeInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            int sizeWidth = (int)(76 * scale);
            int textLeft = (int)(badge.Right + 11F * scale);
            int textWidth = Math.Max(1, (int)(card.Right - textLeft - sizeWidth - 16F * scale));
            Rectangle titleRect = new Rectangle(textLeft, (int)(card.Top + 6F * scale), textWidth, (int)(20 * scale));
            TextRenderer.DrawText(e.Graphics, entry.Name, Theme.Section, titleRect, Theme.Ink, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            Rectangle pathRect = new Rectangle(textLeft, titleRect.Bottom + (int)(2 * scale), textWidth, (int)(17 * scale));
            TextRenderer.DrawText(e.Graphics, entry.Folder, Theme.Small, pathRect, Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
            Rectangle sizeRect = new Rectangle((int)(card.Right - sizeWidth - 10F * scale), (int)card.Top, sizeWidth, (int)card.Height);
            TextRenderer.DrawText(e.Graphics, Theme.SizeText(entry.Size), Theme.Small, sizeRect, selected ? Theme.Blue : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private void DrawAddressItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= address.Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush bg = new SolidBrush(selected ? Theme.Sky : Theme.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            string value = Convert.ToString(address.Items[e.Index]);
            TextRenderer.DrawText(e.Graphics, value, monoFont, new Rectangle(e.Bounds.Left + 4, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 8), e.Bounds.Height), selected ? Theme.Blue : Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private string Link()
        {
            if (session == null || !session.Active) throw new IOException("请先开启临时传输。");
            string selectedIp = address.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedIp)) throw new IOException("没有可用的局域网地址。");
            return session.Url(selectedIp);
        }

        private string StateText()
        {
            int left = Math.Max(0, (int)Math.Ceiling((session.Until - DateTime.UtcNow).TotalMinutes));
            int received = session.SavedFiles.Length;
            return "剩余约 " + left + " 分钟 · 已接收 " + received + " 个文件";
        }

        private void SetState(string text, bool error)
        {
            stateError = error;
            state.Text = text;
            state.ForeColor = error ? Theme.Danger : Theme.Muted;
            tips.SetToolTip(state, text);
        }

        private void UpdateLinkPreview()
        {
            if (session != null && session.Active && address.SelectedItem != null)
            {
                linkBox.Text = session.Url((string)address.SelectedItem);
                linkCaption.Text = "访问链接 · 同一局域网扫码或打开";
            }
            else
            {
                linkBox.Text = "开启后生成一次性局域网链接";
                linkCaption.Text = "访问链接 · HTTP 局域网直连";
            }
            UpdateLinkCardStyle();
        }

        private void CopyLink()
        {
            try
            {
                Clipboard.SetText(Link());
                copy.Text = "已复制";
                copyReset.Stop();
                copyReset.Start();
                SetState("链接已复制到剪贴板 · " + StateText(), false);
            }
            catch (ExternalException) { SetState("剪贴板正被占用，请直接从上方链接框复制", true); }
            catch (Exception e) { SetState(e.Message, true); }
        }

        private void OpenQr()
        {
            try { Process.Start(new ProcessStartInfo(Link() + "&qr") { UseShellExecute = true }); }
            catch (Exception e) { SetState("无法打开浏览器显示二维码：" + e.Message, true); }
        }

        private void Toggle()
        {
            if (session != null) { Stop(); return; }
            try
            {
                if (address.SelectedItem == null) ReloadAddresses();
                if (address.SelectedItem == null) throw new IOException("没有可用的局域网，请先连接 Wi-Fi、热点或以太网。");
                session = new GuestTransfer(files.Items.Cast<ShareEntry>().Select(e => e.FullPath), network.Settings.ReceiveFolder, upload.Checked, network.LoopbackOnly);
                session.Received = (path, name) => main.Ui(() => {
                    main.GuestReceived(path, name);
                    if (!IsDisposed && session != null && session.Active) { SetState(StateText(), false); UpdateActions(); }
                });
                SetState(StateText(), false);
                UpdateActions();
            }
            catch (Exception e) { SetState(e.Message, true); }
        }

        private void UpdateActions()
        {
            bool active = session != null && session.Active;
            int count = files.Items.Count;
            long totalSize = files.Items.Cast<ShareEntry>().Sum(e => e.Size);
            fileSummary.Text = count == 0 ? "0 个文件" : count + " 个文件 · " + Theme.SizeText(totalSize);
            emptyPanel.Visible = count == 0;
            files.Visible = count > 0;

            if (active)
            {
                int left = Math.Max(0, (int)Math.Ceiling((session.Until - DateTime.UtcNow).TotalMinutes));
                statusPill.Set("开放中 · 剩余 " + left + " 分钟", Theme.Online);
            }
            else statusPill.Set("未开启", Theme.Muted);

            start.Text = active ? "结束临时传输" : "开启临时传输";
            start.BackColor = active ? Theme.Danger : Theme.Blue;
            start.ForeColor = Color.White;
            start.Invalidate();

            choose.Enabled = !active;
            removeBtn.Enabled = !active && files.SelectedIndex >= 0;
            clearBtn.Enabled = !active && count > 0;
            upload.Enabled = !active;
            refreshIp.Enabled = !active;
            copy.Enabled = qr.Enabled = active;
            openTasks.Visible = session != null && session.SavedFiles.Length > 0;
            UpdateLinkPreview();
        }

        internal void Stop()
        {
            copyReset.Stop();
            copy.Text = "复制链接";
            if (session != null)
            {
                session.Dispose();
                SetState(session.CleanupError ?? "临时传输已结束，完整文件已保留", session.CleanupError != null);
                session = null;
            }
            UpdateActions();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Theme.Changed -= ApplyColors;
                timer.Stop(); timer.Dispose();
                copyReset.Stop(); copyReset.Dispose();
                tips.Dispose();
                monoFont.Dispose();
                if (session != null) { session.Dispose(); session = null; }
            }
            base.Dispose(disposing);
        }
    }
}
