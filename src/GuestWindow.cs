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
        private sealed class BufferedCanvas : Panel
        {
            internal BufferedCanvas() { DoubleBuffered = true; ResizeRedraw = true; }
        }

        private sealed class StatusChip : Control
        {
            private string caption = "";
            private Color fill = Theme.Surface;
            private Color fore = Theme.Muted;
            private Color dotColor = Theme.Muted;

            internal StatusChip()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                Height = 24;
                BackColor = Theme.Canvas;
            }

            internal void Set(string text, bool active)
            {
                string nextCaption = text ?? "";
                Color nextFill = active ? Theme.Blend(Theme.Online, Theme.Surface, Theme.Dark ? .26F : .14F) : Theme.Surface;
                Color nextFore = active ? (Theme.Dark ? Theme.Blend(Color.White, Theme.Online, .32F) : Theme.Blend(Theme.Ink, Theme.Online, .62F)) : Theme.Muted;
                Color nextDot = active ? Theme.Online : Theme.Muted;
                if (caption == nextCaption && fill.ToArgb() == nextFill.ToArgb() && fore.ToArgb() == nextFore.ToArgb() && dotColor.ToArgb() == nextDot.ToArgb()) return;
                caption = nextCaption;
                fill = nextFill;
                fore = nextFore;
                dotColor = nextDot;
                UpdateExtent();
                Invalidate();
            }

            internal void UpdateExtent()
            {
                using (Graphics g = CreateGraphics())
                {
                    float scale = g.DpiX / 96F;
                    int textW = TextRenderer.MeasureText(g, caption, Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    Height = Math.Max(22, (int)Math.Round(24 * scale));
                    Width = Math.Max((int)Math.Round(64 * scale), textW + (int)Math.Round(26 * scale));
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Parent == null ? Theme.Canvas : Parent.BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96F;
                RectangleF chip = new RectangleF(.5F, .5F, Width - 1F, Height - 1F);
                using (GraphicsPath path = Theme.RoundedF(chip, 6F * scale))
                using (SolidBrush brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
                float dot = 6F * scale;
                using (SolidBrush brush = new SolidBrush(dotColor))
                    g.FillEllipse(brush, chip.Left + 8F * scale, chip.Top + (chip.Height - dot) / 2F, dot, dot);
                Rectangle textRect = new Rectangle((int)(chip.Left + 18F * scale), (int)chip.Top, Math.Max(1, (int)(chip.Width - 24F * scale)), (int)chip.Height);
                TextRenderer.DrawText(g, caption, Theme.Small, textRect, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        // 自绘局域网地址选择器：替代原生 ComboBox（避免固定行高裁切 IP 底边文字）。
        private sealed class AddressPicker : Control
        {
            private readonly List<string> items = new List<string>();
            private readonly ContextMenuStrip menu = Theme.Menu();
            private readonly Font font;
            private int selectedIndex = -1;
            private bool hovered;

            internal event EventHandler SelectedIndexChanged;
            internal string SelectedItem { get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; } }

            internal AddressPicker(Font monoFont)
            {
                font = monoFont;
                DoubleBuffered = true;
                ResizeRedraw = true;
                TabStop = true;
                Cursor = Cursors.Hand;
                AccessibleName = "临时传输网络地址";
                AccessibleRole = AccessibleRole.ComboBox;
            }

            internal void SetItems(string[] next)
            {
                string previous = SelectedItem;
                items.Clear();
                if (next != null) items.AddRange(next);
                if (previous != null) selectedIndex = items.IndexOf(previous);
                else selectedIndex = -1;
                if (selectedIndex < 0 && items.Count > 0) selectedIndex = 0;
                Text = SelectedItem ?? "";
                Invalidate();
            }

            private void SelectIndex(int index)
            {
                if (index < 0 || index >= items.Count || index == selectedIndex) return;
                selectedIndex = index;
                Text = SelectedItem ?? "";
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            internal void OpenMenu()
            {
                if (!Enabled || items.Count == 0) return;
                foreach (ToolStripItem old in menu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
                menu.Items.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    int index = i;
                    var item = new ToolStripMenuItem(items[i]) { Checked = i == selectedIndex };
                    item.Click += delegate { SelectIndex(index); };
                    menu.Items.Add(item);
                }
                menu.MinimumSize = new Size(Math.Max(140, Width), 0);
                menu.Show(this, new Point(0, Height + 2));
            }

            protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
            protected override void OnClick(EventArgs e) { Focus(); OpenMenu(); base.OnClick(e); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down)
                {
                    e.Handled = true;
                    OpenMenu();
                }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Parent == null ? Theme.Canvas : Parent.BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = g.DpiX / 96F;
                RectangleF box = new RectangleF(.5F, .5F, Math.Max(4F, Width - 1F), Math.Max(4F, Height - 1F));
                Color fill = Enabled ? (hovered ? Theme.Blend(Theme.Ink, Theme.Surface, .03F) : Theme.Surface) : Theme.SurfaceContainerLow;
                using (GraphicsPath path = Theme.RoundedF(box, 8F * scale))
                {
                    using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                    if (Focused) using (Pen pen = new Pen(Theme.Blue, 1.5F)) g.DrawPath(pen, path);
                }
                int chevronW = (int)Math.Round(28 * scale);
                int padLeft = (int)Math.Round(12 * scale);
                string label = SelectedItem ?? "暂无可用局域网地址";
                Color ink = SelectedItem == null || !Enabled ? Theme.Muted : Theme.Ink;
                Rectangle textRect = new Rectangle(padLeft, 0, Math.Max(1, Width - padLeft - chevronW), Height);
                TextRenderer.DrawText(g, label, font, textRect, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                float cx = Width - 14F * scale;
                float cy = Height / 2F;
                using (Pen arrow = new Pen(Theme.Muted, 1.5F * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(arrow, new[] {
                        new PointF(cx - 4F * scale, cy - 1.5F * scale),
                        new PointF(cx, cy + 2.5F * scale),
                        new PointF(cx + 4F * scale, cy - 1.5F * scale)
                    });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) menu.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class ShareEntry
        {
            internal string FullPath, Name, Folder, Extension, Kind, Relative;
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
        private readonly Font monoFont = new Font("Consolas", 9.5F);
        private readonly Panel headerStrip = new BufferedCanvas();
        private readonly StatusChip statusChip = new StatusChip();
        private readonly Label headerHint = Theme.Label("浏览器扫码或打开链接即可收发 · HTTP 明文仅限可信局域网", Theme.Small, Theme.Muted);
        private readonly Panel workspace = new BufferedCanvas();
        private readonly RoundedPanel filesPane = new RoundedPanel { Radius = 12, Border = Color.Empty };
        private readonly Panel filesHeader = new BufferedCanvas();
        private readonly Label listTitle = Theme.Label("待分享文件", Theme.Section, Theme.Ink);
        private readonly Label fileSummary = Theme.Label("0 个文件", Theme.Small, Theme.Muted);
        private readonly Panel listHost = new BufferedCanvas();
        private readonly ListBox files = new ListBox();
        private readonly Panel emptyPanel = new BufferedCanvas();
        private readonly Panel inspectorPane = new BufferedCanvas();
        private readonly RoundedPanel linkBlock = new RoundedPanel { Radius = 12, Border = Color.Empty };
        private readonly Label linkCaption = Theme.Label("访问链接 · HTTP 局域网直连", Theme.Small, Theme.Muted);
        private readonly RoundedPanel linkFieldWrap = new RoundedPanel { Radius = 8, Border = Color.Empty };
        private readonly TextBox linkBox = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None };
        private readonly ToggleSwitch upload = new ToggleSwitch { Text = "允许对方向我发送文件", AccessibleName = "允许浏览器上传", Description = "保存至接收目录 · 限 1000 个文件 / 100 GiB", Checked = false };
        private readonly ToggleSwitch allowText = new ToggleSwitch { Text = "允许文本互传", AccessibleName = "允许文本互传", Checked = false };
        private readonly Label netTitle = Theme.Label("局域网地址", Theme.Section, Theme.Ink);
        private readonly AddressPicker addressPicker;
        private readonly Label lifetimeTitle = Theme.Label("链接有效期（分钟，1–1440）", Theme.Small, Theme.Muted);
        private readonly RoundedInput lifetimeField = new RoundedInput();
        private readonly TextBox lifetime;
        private readonly Button saveLifetime;
        private readonly Panel footerBar = new BufferedCanvas();
        private readonly Label state = Theme.Label("就绪", Theme.Small, Theme.Muted);
        private readonly Button choose, chooseFolder, removeBtn, clearBtn, refreshIp, copy, qr, openTasks, start, textTransfer;
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly Timer copyReset = new Timer { Interval = 1800 };
        private readonly Timer windowSaveTimer = new Timer { Interval = 500 };
        private readonly ToolTip tips = new ToolTip();
        private volatile GuestTransfer session;
        private GuestTextWindow textWindow;
        internal Func<FileInfo, FileAttributes> ReadAttributes = info => info.Attributes;
        private bool hasReceivedFiles;
        private bool stateError;
        private bool windowLoaded, placementPending;
        private WindowPlacement placement;

        internal bool RestoreMaximized { get { return placement != null && placement.Maximized; } }

        internal GuestWindow(MainForm main, LanService network)
        {
            lifetime = lifetimeField.Input;
            lifetime.MaxLength = 8; lifetime.AccessibleName = "链接有效期（分钟）";
            this.main = main;
            this.network = network;
            lifetime.Text = network.Settings.GuestLifetimeMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            saveLifetime = Theme.Button("保存有效期", delegate {
                try { SaveLifetime(); SetState("有效期已保存，下次开启时使用；重启不会恢复旧链接", false); }
                catch (Exception error) { SetState(error.Message, true); lifetime.Focus(); }
            }, false);
            tips.SetToolTip(lifetime, "默认 60 分钟，最多 24 小时。只保存时长，不恢复旧链接。");
            Text = "临时浏览器传输";
            ClientSize = new Size(920, 620);
            MinimumSize = new Size(680, 580);
            UseSecondaryChrome();

            addressPicker = new AddressPicker(monoFont);
            addressPicker.SelectedIndexChanged += delegate { UpdateLinkPreview(); };

            // 顶栏直接落在 Canvas 背景上，不用线条硬隔断。
            headerHint.AutoEllipsis = true;
            headerHint.TextAlign = ContentAlignment.MiddleLeft;
            headerStrip.Controls.Add(statusChip);
            headerStrip.Controls.Add(allowText);
            headerStrip.Controls.Add(headerHint);

            // 主体工作区卡片：白底/深卡衬于 Canvas 之上，突出核心文件操作区。
            listTitle.AutoSize = true;
            fileSummary.AutoSize = false;fileSummary.AutoEllipsis=true;
            choose = Theme.IconButton("plus", "添加文件", delegate { PickFiles(); }, false);
            chooseFolder = Theme.IconButton("folder", "添加文件夹", delegate {
                if(session!=null)return;
                try { string[] selected=SharedFolderPicker.Show(this);if(selected.Length>0)AddFiles(selected); }
                catch(COMException error){SetState("无法选择文件夹："+error.Message,true);}
            }, false);
            tips.SetToolTip(chooseFolder,"添加文件夹（包含子目录文件，不发送空文件夹）");
            removeBtn = Theme.IconButton("trash", "移除所选", delegate { RemoveSelectedFile(); }, false);
            clearBtn = Theme.IconButton("eraser", "清空列表", delegate { ClearFiles(); }, false);
            tips.SetToolTip(choose, "添加待分享文件");
            tips.SetToolTip(removeBtn, "移除选中文件");
            tips.SetToolTip(clearBtn, "清空待分享列表");
            filesHeader.Controls.Add(listTitle);
            filesHeader.Controls.Add(fileSummary);
            filesHeader.Controls.Add(choose);
            filesHeader.Controls.Add(chooseFolder);
            filesHeader.Controls.Add(removeBtn);
            filesHeader.Controls.Add(clearBtn);

            files.BorderStyle = BorderStyle.None;
            files.DrawMode = DrawMode.OwnerDrawFixed;
            files.ItemHeight = 56;
            files.IntegralHeight = false;
            files.AccessibleName = "临时分享文件";
            files.DrawItem += DrawFileItem;
            files.SelectedIndexChanged += delegate { UpdateActions(); };
            files.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Delete && session == null) { e.Handled = true; RemoveSelectedFile(); } };
            Theme.ThemeScrollbars(files);

            emptyPanel.Cursor = Cursors.Hand;
            emptyPanel.Click += delegate { if (session == null) PickFiles(); };
            emptyPanel.Paint += PaintEmptyFiles;

            files.Dock = DockStyle.Fill;
            emptyPanel.Dock = DockStyle.Fill;
            listHost.Controls.Add(files);
            listHost.Controls.Add(emptyPanel);
            emptyPanel.BringToFront();

            filesPane.Controls.Add(filesHeader);
            filesPane.Controls.Add(listHost);

            EnableFileDrop(filesPane);
            EnableFileDrop(listHost);
            EnableFileDrop(files);
            EnableFileDrop(emptyPanel);

            // 侧栏控制区：独立 elevated 链接卡片 + 轻量开关与网卡选择器。
            linkCaption.AutoEllipsis = true;
            linkCaption.TextAlign = ContentAlignment.MiddleLeft;
            linkBox.Font = monoFont;
            linkBox.AccessibleName = "临时传输链接";
            Theme.AttachEditMenu(linkBox);
            linkFieldWrap.Controls.Add(linkBox);
            linkFieldWrap.Layout += delegate { LayoutLinkBox(); };
            linkBox.FontChanged += delegate { LayoutLinkBox(); };

            copy = Theme.Button("复制链接", delegate { CopyLink(); }, false);
            ((RoundedButton)copy).Glyph = "copy";
            ((RoundedButton)copy).ShowLabel = true;
            qr = Theme.Button("二维码", delegate { OpenQr(); }, false);
            ((RoundedButton)qr).Glyph = "qr";
            ((RoundedButton)qr).ShowLabel = true;
            linkBlock.Controls.Add(linkCaption);
            linkBlock.Controls.Add(linkFieldWrap);
            linkBlock.Controls.Add(copy);
            linkBlock.Controls.Add(qr);

            refreshIp = Theme.IconButton("refresh", "刷新局域网地址", delegate { ReloadAddresses(); }, false);
            tips.SetToolTip(refreshIp, "重新扫描本机局域网地址");
            netTitle.AutoSize = true;

            inspectorPane.Controls.Add(linkBlock);
            inspectorPane.Controls.Add(upload);
            inspectorPane.Controls.Add(netTitle);
            inspectorPane.Controls.Add(addressPicker);
            inspectorPane.Controls.Add(refreshIp);
            inspectorPane.Controls.Add(lifetimeTitle);
            inspectorPane.Controls.Add(lifetimeField);
            inspectorPane.Controls.Add(saveLifetime);

            workspace.Controls.Add(filesPane);
            workspace.Controls.Add(inspectorPane);

            // 底栏延续 Canvas 层次，无冗余分割线。
            state.AutoEllipsis = true;
            state.TextAlign = ContentAlignment.MiddleLeft;
            openTasks = Theme.Button("查看已接收", delegate { main.OpenTransfers("browser-guests"); }, false);
            openTasks.Visible = false;
            textTransfer = Theme.Button("文本互传…", delegate { OpenText(); }, false);
            tips.SetToolTip(allowText,"仅本次会话开放；文本不进聊天历史，会话结束自动清空。");
            start = Theme.Button("开启临时传输", delegate { Toggle(); }, true);
            footerBar.Controls.Add(state);
            footerBar.Controls.Add(openTasks);
            footerBar.Controls.Add(textTransfer);
            footerBar.Controls.Add(start);

            Surface.Controls.Add(headerStrip);
            Surface.Controls.Add(workspace);
            Surface.Controls.Add(footerBar);

            windowSaveTimer.Tick += delegate { windowSaveTimer.Stop(); SaveWindowPlacement(); };
            LocationChanged += TrackWindowPlacement;
            SizeChanged += TrackWindowPlacement;
            Surface.SizeChanged += delegate { Relayout(); };
            Shown += delegate { ApplyColors(); Relayout(); UpdateActions(); };
            DpiChangedAfterParent += delegate { ApplyColors(); Relayout(); };
            Theme.Changed += ApplyColors;
            network.NetworkReset += CloseNetworkSession;
            copyReset.Tick += delegate { copyReset.Stop(); if (!IsDisposed) copy.Text = "复制链接"; };
            timer.Tick += delegate
            {
                if (session != null && !session.Active) { Stop(); SetState("本次临时传输已到期结束", false); }
                else if (session != null) { SetState(StateText(), false); UpdateActions(); }
            };
            timer.Start();

            ReloadAddresses();
            ApplyColors();
            UpdateActions();
            Relayout();
        }

        private float CurrentDpiScale()
        {
            if (IsDisposed) return 1F;
            using (Graphics g = CreateGraphics()) return Math.Max(1F, g.DpiX / 96F);
        }

        private int MeasureButtonWidth(Button button, float scale, int extra, int minimum)
        {
            int textW = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
            return Math.Max((int)Math.Round(minimum * scale), textW + (int)Math.Round(extra * scale));
        }

        private void LayoutLinkBox()
        {
            int padX = Math.Max(8, (int)Math.Round(10 * CurrentDpiScale()));
            int boxH = Math.Max(16, linkBox.PreferredHeight);
            Rectangle bounds = new Rectangle(padX, Math.Max(0, (linkFieldWrap.ClientSize.Height - boxH) / 2), Math.Max(10, linkFieldWrap.ClientSize.Width - padX * 2), boxH);
            if (linkBox.Bounds != bounds) linkBox.Bounds = bounds;
        }

        private void Relayout()
        {
            if (IsDisposed || Surface.ClientSize.Width <= 0 || Surface.ClientSize.Height <= 0) return;
            float s = CurrentDpiScale();
            int w = Surface.ClientSize.Width;
            int h = Surface.ClientSize.Height;
            int outer = (int)Math.Round(18 * s);
            int headerH = (int)Math.Round(42 * s);
            int footerH = (int)Math.Round(58 * s);

            SuspendLayout();
            try
            {
                headerStrip.Bounds = new Rectangle(0, 0, w, headerH);
                statusChip.UpdateExtent();
                statusChip.Location = new Point(outer, (headerH - statusChip.Height) / 2);
                int textW = (int)Math.Round(160 * s), textH = allowText.PreferredHeightFor(textW);
                allowText.Bounds = new Rectangle(statusChip.Right + (int)Math.Round(12 * s), Math.Max(0,(headerH-textH)/2), textW, textH);
                int hintX = allowText.Right + (int)Math.Round(12 * s);
                headerHint.Bounds = new Rectangle(hintX, 0, Math.Max(40, w - hintX - outer), headerH);

                footerBar.Bounds = new Rectangle(0, Math.Max(headerH, h - footerH), w, footerH);
                int btnH = (int)Math.Round(36 * s);
                int btnY = (footerH - btnH) / 2;
                int startW = MeasureButtonWidth(start, s, 32, 122);
                start.Bounds = new Rectangle(w - outer - startW, btnY, startW, btnH);
                int leftLimit = start.Left - (int)Math.Round(10 * s);
                if (openTasks.Visible)
                {
                    int tasksW = MeasureButtonWidth(openTasks, s, 26, 96);
                    openTasks.Bounds = new Rectangle(start.Left - (int)Math.Round(10 * s) - tasksW, btnY, tasksW, btnH);
                    leftLimit = openTasks.Left - (int)Math.Round(12 * s);
                }
                int textButtonW = MeasureButtonWidth(textTransfer, s, 26, 94);
                textTransfer.Bounds = new Rectangle(leftLimit - textButtonW, btnY, textButtonW, btnH);
                leftLimit = textTransfer.Left - (int)Math.Round(12 * s);
                state.Bounds = new Rectangle(outer, 0, Math.Max(40, leftLimit - outer), footerH);

                int wsH = Math.Max(120, footerBar.Top - headerH);
                workspace.Bounds = new Rectangle(0, headerH, w, wsH);
                bool split = w >= (int)Math.Round(680 * s);
                int gap = (int)Math.Round(14 * s);

                if (split)
                {
                    int inspW = Math.Max((int)Math.Round(296 * s), Math.Min((int)Math.Round(376 * s), (int)Math.Round(w * 0.37)));
                    int filesX = outer;
                    int filesW = Math.Max(220, w - outer * 2 - inspW - gap);
                    int inspX = filesX + filesW + gap;
                    filesPane.Bounds = new Rectangle(filesX, 0, filesW, wsH);
                    inspectorPane.Bounds = new Rectangle(inspX, 0, Math.Max(140, w - outer - inspX), wsH);
                    LayoutFilesPane(filesW, wsH, s);
                    LayoutInspectorVertical(inspectorPane.Width, wsH, s);
                }
                else
                {
                    int leftColW = Math.Max(180, (w - outer * 2 - (int)Math.Round(16 * s)) / 2);
                    int toggleH = upload.PreferredHeightFor(leftColW);
                    int pickerH = (int)Math.Round(34 * s);
                    int settingsNeeded = (int)Math.Round(2 * s) + toggleH + (int)Math.Round(10 * s) + netTitle.PreferredHeight + (int)Math.Round(6 * s) + pickerH;
                    int fieldH = Math.Max((int)Math.Round(36 * s), linkBox.PreferredHeight + (int)Math.Round(14 * s));
                    int linkNeeded = (int)Math.Round(12 * s) + (int)Math.Round(20 * s) + (int)Math.Round(8 * s) + fieldH + (int)Math.Round(10 * s) + (int)Math.Round(34 * s) + (int)Math.Round(14 * s);
                    int filesMinimum = (int)Math.Round(42 * s) + 2 * (int)Math.Round(6 * s) + 110;
                    int inspectorNeeded = Math.Max(settingsNeeded, linkNeeded) + (int)(8 * s) + (int)(32 * s);
                    int filesH = Math.Max(filesMinimum, wsH - inspectorNeeded - gap);
                    filesPane.Bounds = new Rectangle(outer, 0, Math.Max(200, w - outer * 2), filesH);
                    inspectorPane.Bounds = new Rectangle(outer, filesPane.Bottom + gap, Math.Max(200, w - outer * 2), Math.Max(80, wsH - filesH - gap));
                    LayoutFilesPane(filesPane.Width, filesH, s);
                    LayoutInspectorHorizontal(inspectorPane.Width, inspectorPane.Height, s);
                }
            }
            finally { ResumeLayout(true); }
        }

        private void LayoutFilesPane(int pw, int ph, float s)
        {
            // 内缩子控件矩形，确保主卡片四周 12px 圆角不被内嵌方框盖住。
            int padX = (int)Math.Round(16 * s);
            int padY = (int)Math.Round(6 * s);
            int hdrH = (int)Math.Round(42 * s);
            int innerW = Math.Max(80, pw - padX * 2);
            filesHeader.Bounds = new Rectangle(padX, padY, innerW, hdrH);

            int iconSize = (int)Math.Round(32 * s);
            int btnY = (hdrH - iconSize) / 2;
            int gap = (int)Math.Round(6 * s);
            clearBtn.Bounds = new Rectangle(innerW - iconSize, btnY, iconSize, iconSize);
            removeBtn.Bounds = new Rectangle(clearBtn.Left - gap - iconSize, btnY, iconSize, iconSize);
            chooseFolder.Bounds = new Rectangle(removeBtn.Left - gap - iconSize, btnY, iconSize, iconSize);
            choose.Bounds = new Rectangle(chooseFolder.Left - gap - iconSize, btnY, iconSize, iconSize);

            int titleW = listTitle.PreferredWidth;
            listTitle.Bounds = new Rectangle(0, (hdrH - listTitle.PreferredHeight) / 2, titleW, listTitle.PreferredHeight);
            int sumX = listTitle.Right + (int)Math.Round(10 * s);
            int sumMaxW = Math.Max(0, choose.Left - (int)Math.Round(8 * s) - sumX);
            fileSummary.Bounds = new Rectangle(sumX, (hdrH - fileSummary.PreferredHeight) / 2, sumMaxW, fileSummary.PreferredHeight);

            listHost.Bounds = new Rectangle(padX, filesHeader.Bottom, innerW, Math.Max(40, ph - filesHeader.Bottom - padY));
        }

        private void LayoutLinkCard(int x, int y, int cw, float s, bool compact = false)
        {
            int cardPad = (int)Math.Round(14 * s);
            int innerW = Math.Max(80, cw - cardPad * 2);
            int capH = (int)Math.Round(20 * s);
            int fieldH = Math.Max((int)Math.Round(36 * s), linkBox.PreferredHeight + (int)Math.Round(14 * s));
            int btnH = (int)Math.Round(34 * s);
            int gap = (int)Math.Round(8 * s);
            int btnW = Math.Max(60, (innerW - gap) / 2);

            linkCaption.Bounds = new Rectangle(cardPad, (int)Math.Round((compact ? 4 : 12) * s), innerW, capH);
            linkFieldWrap.Bounds = new Rectangle(cardPad, linkCaption.Bottom + (int)Math.Round((compact ? 2 : 8) * s), innerW, fieldH);
            LayoutLinkBox();
            int btnY = linkFieldWrap.Bottom + (int)Math.Round((compact ? 3 : 10) * s);
            copy.Bounds = new Rectangle(cardPad, btnY, btnW, btnH);
            qr.Bounds = new Rectangle(copy.Right + gap, btnY, Math.Max(60, innerW - btnW - gap), btnH);
            linkBlock.Bounds = new Rectangle(x, y, cw, qr.Bottom + (int)Math.Round((compact ? 4 : 14) * s));
        }

        private void LayoutInspectorVertical(int pw, int ph, float s)
        {
            LayoutLinkCard(0, 0, pw, s);

            int toggleTop = linkBlock.Bottom + (int)Math.Round(10 * s);
            int toggleH = upload.PreferredHeightFor(pw);
            upload.Bounds = new Rectangle(0, toggleTop, pw, toggleH);
            int netTop = upload.Bottom + (int)Math.Round(8 * s);
            netTitle.Bounds = new Rectangle(0, netTop, pw, netTitle.PreferredHeight);
            int pickerH = (int)Math.Round(36 * s);
            int refreshSize = pickerH;
            int pickerTop = netTitle.Bottom + (int)Math.Round(8 * s);
            refreshIp.Bounds = new Rectangle(pw - refreshSize, pickerTop, refreshSize, refreshSize);
            addressPicker.Bounds = new Rectangle(0, pickerTop, Math.Max(80, pw - refreshSize - (int)Math.Round(8 * s)), pickerH);
            LayoutLifetime(pw, addressPicker.Bottom, s);
        }

        private void LayoutInspectorHorizontal(int pw, int ph, float s)
        {
            int colGap = (int)Math.Round(16 * s);
            int leftW = Math.Max(180, (pw - colGap) / 2);
            int rightX = leftW + colGap;
            int rightW = Math.Max(180, pw - rightX);

            int toggleH = upload.PreferredHeightFor(leftW);
            upload.Bounds = new Rectangle(0, (int)Math.Round(2 * s), leftW, toggleH);
            int netTop = upload.Bottom + (int)Math.Round(10 * s);
            netTitle.Bounds = new Rectangle(0, netTop, leftW, netTitle.PreferredHeight);
            int pickerH = (int)Math.Round(34 * s);
            int refreshSize = pickerH;
            int pickerTop = netTitle.Bottom + (int)Math.Round(6 * s);
            refreshIp.Bounds = new Rectangle(leftW - refreshSize, pickerTop, refreshSize, refreshSize);
            addressPicker.Bounds = new Rectangle(0, pickerTop, Math.Max(80, leftW - refreshSize - (int)Math.Round(8 * s)), pickerH);

            LayoutLinkCard(rightX, 0, rightW, s);
            if (Math.Max(addressPicker.Bottom, linkBlock.Bottom) + (int)(8 * s) + (int)(32 * s) > ph)
            {
                LayoutLinkCard(rightX, 0, rightW, s, true);
                int rowTop = upload.Bottom + (int)Math.Round(4 * s);
                int labelWidth = netTitle.PreferredWidth;
                int pickerX = labelWidth + (int)Math.Round(4 * s);
                netTitle.Bounds = new Rectangle(0, rowTop, labelWidth, pickerH);
                refreshIp.Top = rowTop;
                addressPicker.Bounds = new Rectangle(pickerX, rowTop, refreshIp.Left - (int)Math.Round(4 * s) - pickerX, pickerH);
            }
            int lifetimeTop = Math.Max(addressPicker.Bottom, linkBlock.Bottom) + (int)(8 * s);
            int buttonWidth = (int)(110 * s), inputWidth = (int)(72 * s), inputGap = (int)(10 * s);
            saveLifetime.Bounds = new Rectangle(pw - buttonWidth, lifetimeTop, buttonWidth, (int)(32 * s));
            lifetimeField.Padding = new Padding((int)(10 * s), 0, (int)(10 * s), 0);
            lifetimeField.Bounds = new Rectangle(saveLifetime.Left - inputGap - inputWidth, lifetimeTop, inputWidth, saveLifetime.Height);
            lifetimeTitle.Bounds = new Rectangle(0, lifetimeTop + (int)(5 * s), lifetimeField.Left - inputGap, (int)(24 * s));
        }

        private void LayoutLifetime(int width, int top, float scale)
        {
            lifetimeTitle.Bounds = new Rectangle(0, top + (int)(6 * scale), width, (int)(20 * scale));
            int buttonWidth = (int)(110 * scale), gap = (int)(10 * scale);
            saveLifetime.Bounds = new Rectangle(width - buttonWidth, lifetimeTitle.Bottom + (int)(2 * scale), buttonWidth, (int)(32 * scale));
            lifetimeField.Padding = new Padding((int)(10 * scale), 0, (int)(10 * scale), 0);
            lifetimeField.Bounds = new Rectangle(0, saveLifetime.Top, Math.Max(40, width - buttonWidth - gap), saveLifetime.Height);
        }

        private void SaveLifetime()
        {
            int minutes;
            if (!Int32.TryParse(lifetime.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes) || minutes < 1 || minutes > AppSettings.MaxGuestLifetimeMinutes)
                throw new ArgumentException("请输入 1–1440 的整数分钟数（最多 24 小时）。");
            main.SaveGuestLifetime(minutes);
        }

        private static void StyleToolbarIcon(Button button, bool accentTonal)
        {
            RoundedButton rounded = button as RoundedButton;
            if (rounded == null) return;
            rounded.Border = Color.Empty;
            rounded.Radius = 8;
            if (accentTonal)
            {
                button.BackColor = Theme.Sky;
                button.ForeColor = Theme.OnPrimaryContainer;
                rounded.HoverBack = Theme.Blend(Theme.Blue, Theme.Sky, .12F);
                rounded.HoverFore = Theme.OnPrimaryContainer;
            }
            else
            {
                button.BackColor = Theme.Surface;
                button.ForeColor = Theme.Ink;
                rounded.HoverBack = Theme.Hover;
                rounded.HoverFore = Theme.Ink;
            }
        }

        private static void StyleActionButton(Button button, int toneKind, int radius)
        {
            // toneKind: 0=surface on Canvas, 1=Ice tonal inside Surface card, 2=Sky tonal primary, 3=Danger filled, 4=Primary filled
            RoundedButton rounded = button as RoundedButton;
            if (rounded != null) { rounded.Border = Color.Empty; rounded.Radius = radius; }
            switch (toneKind)
            {
                case 4: button.BackColor = Theme.Blue; button.ForeColor = Color.White; break;
                case 3: button.BackColor = Theme.Danger; button.ForeColor = Color.White; break;
                case 2: button.BackColor = Theme.Sky; button.ForeColor = Theme.OnPrimaryContainer; break;
                case 1: button.BackColor = Theme.Ice; button.ForeColor = Theme.Ink; break;
                default: button.BackColor = Theme.Surface; button.ForeColor = Theme.Ink; break;
            }
        }

        private void ApplyColors()
        {
            if (IsDisposed) return;
            Surface.BackColor = Theme.Canvas;
            headerStrip.BackColor = headerHint.BackColor = Theme.Canvas;
            headerHint.ForeColor = Theme.Muted;
            workspace.BackColor = inspectorPane.BackColor = Theme.Canvas;
            footerBar.BackColor = state.BackColor = Theme.Canvas;
            state.ForeColor = stateError ? Theme.Danger : Theme.Muted;

            filesPane.BackColor = Theme.Canvas;
            filesPane.Fill = Theme.Surface;
            filesHeader.BackColor = listHost.BackColor = files.BackColor = emptyPanel.BackColor = Theme.Surface;
            listTitle.BackColor = fileSummary.BackColor = Theme.Surface;
            listTitle.ForeColor = Theme.Ink;
            fileSummary.ForeColor = Theme.Muted;
            files.ForeColor = Theme.Ink;

            upload.BackColor = Theme.Canvas;
            allowText.BackColor = Theme.Canvas;
            netTitle.BackColor = Theme.Canvas;
            netTitle.ForeColor = Theme.Ink;
            lifetimeTitle.BackColor = Theme.Canvas;
            lifetimeTitle.ForeColor = Theme.Muted;
            lifetimeField.BackColor = Theme.Canvas;
            lifetime.BackColor = Theme.Ice;
            lifetime.ForeColor = lifetimeField.Enabled ? Theme.Ink : Theme.Muted;
            lifetimeField.Invalidate();
            StyleActionButton(saveLifetime, 0, 8);

            linkBlock.BackColor = Theme.Canvas;
            linkBlock.Fill = Theme.Surface;
            linkCaption.BackColor = Theme.Surface;
            linkFieldWrap.BackColor = Theme.Surface;
            linkFieldWrap.Fill = Theme.Ice;
            linkBox.BackColor = Theme.Ice;

            StyleToolbarIcon(choose, true);
            StyleToolbarIcon(chooseFolder, false);
            StyleToolbarIcon(removeBtn, false);
            StyleToolbarIcon(clearBtn, false);
            clearBtn.ForeColor = Theme.Muted;

            StyleActionButton(copy, 2, 8);
            StyleActionButton(qr, 1, 8);
            StyleActionButton(refreshIp, 0, 8);
            refreshIp.ForeColor = Theme.Muted;
            StyleActionButton(openTasks, 0, 8);
            StyleActionButton(textTransfer, 0, 8);
            bool active = session != null && session.Active;
            StyleActionButton(start, active ? 3 : 4, 8);

            using (Graphics g = files.CreateGraphics())
            {
                float scale = g.DpiX / 96F;
                files.ItemHeight = (int)Math.Round(56 * scale);
            }
            UpdateStatusChip();
            UpdateLinkCardStyle();
            statusChip.Invalidate();
            addressPicker.Invalidate();
            files.Invalidate();
            emptyPanel.Invalidate();
            filesPane.Invalidate();
            headerStrip.Invalidate();
            footerBar.Invalidate();
        }

        private void UpdateStatusChip()
        {
            bool active = session != null && session.Active;
            if (active)
            {
                int left = (int)Math.Ceiling(session.RemainingMilliseconds / 60000.0);
                statusChip.Set("开放中 · 剩余 " + left + " 分钟", true);
            }
            else statusChip.Set("未开启", false);
        }

        private void UpdateLinkCardStyle()
        {
            bool active = session != null && session.Active;
            linkBlock.Fill = Theme.Surface;
            linkBlock.Border = active ? Theme.Blue : Color.Empty;
            linkCaption.ForeColor = active ? Theme.Blue : Theme.Muted;
            linkBox.ForeColor = active ? Theme.Ink : Theme.Muted;
            linkBlock.Invalidate();
        }

        internal void ReloadAddresses()
        {
            if (session != null) return;
            string[] items = network.LoopbackOnly
                ? new[] { "127.0.0.1" }
                : LanNetwork.Scan().Select(l => l.Address.ToString()).Distinct().ToArray();
            addressPicker.SetItems(items);
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
                if (string.IsNullOrWhiteSpace(raw)) { skipped++; continue; }
                try
                {
                    var expanded=SendBatch.Expand(new[]{raw});
                    if(files.Items.Count+expanded.Count(item=>!existing.Contains(item.Path))>1000){files.EndUpdate();UpdateActions();SetState("单次最多选择 1000 个文件",true);return;}
                    foreach(var item in expanded) {
                        FileInfo info=new FileInfo(item.Path);
                        if((ReadAttributes(info)&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
                        if(!existing.Add(info.FullName))continue;
                        ShareEntry entry=ShareEntry.FromPath(info.FullName);entry.Relative=item.Relative;
                        if(item.Relative!=null)entry.Folder=item.Relative;
                        files.Items.Add(entry);
                    }
                }
                catch (IOException) { skipped++; }
                catch (UnauthorizedAccessException) { skipped++; }
            }
            files.EndUpdate();
            if (skipped > 0) SetState("已忽略空文件夹、链接及名称不安全或不可读取的条目", false);
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
            target.DragEnter += (sender, e) =>
            {
                bool ok = session == null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            };
            target.DragDrop += (sender, e) =>
            {
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
            int w = emptyPanel.Width, h = emptyPanel.Height;
            if (w < 24 || h < 24) return;

            int textWidth = Math.Max(40, w - (int)(48 * scale));
            int titleH = TextRenderer.MeasureText(g, "暂无待分享文件", Theme.Section, new Size(textWidth, 0), TextFormatFlags.NoPrefix).Height;
            int subH = TextRenderer.MeasureText(g, "点击添加或拖入文件（也可仅开启接收）", Theme.Small, new Size(textWidth, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            bool showIcon = h >= titleH + subH + (int)(74 * scale);
            float chipSize = showIcon ? 44F * scale : 0F;
            float gapAfterChip = showIcon ? 12F * scale : 0F;
            float gapBetweenText = 4F * scale;
            float totalH = chipSize + gapAfterChip + titleH + gapBetweenText + subH;
            float startY = Math.Max(6F * scale, (h - totalH) / 2F);

            if (showIcon)
            {
                float cx = w / 2F;
                RectangleF chip = new RectangleF(cx - chipSize / 2F, startY, chipSize, chipSize);
                using (GraphicsPath path = Theme.RoundedF(chip, 10F * scale))
                using (SolidBrush brush = new SolidBrush(Theme.Ice)) g.FillPath(brush, path);
                float inset = 10F * scale;
                Theme.Glyph(g, "file", new RectangleF(chip.Left + inset, chip.Top + inset, chip.Width - inset * 2F, chip.Height - inset * 2F), Theme.Blue);
                startY = chip.Bottom + gapAfterChip;
            }

            Rectangle titleRect = new Rectangle((int)(24 * scale), (int)startY, textWidth, titleH + 2);
            TextRenderer.DrawText(g, "暂无待分享文件", Theme.Section, titleRect, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
            Rectangle subRect = new Rectangle((int)(24 * scale), titleRect.Bottom + (int)gapBetweenText, textWidth, subH + 4);
            TextRenderer.DrawText(g, "点击添加或拖入文件（也可仅开启接收）", Theme.Small, subRect, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
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
                default: fill = Theme.Sky; ink = Theme.OnPrimaryContainer; break;
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

            if (selected)
            {
                RectangleF highlight = new RectangleF(e.Bounds.Left, e.Bounds.Top + 2F * scale, Math.Max(4F, e.Bounds.Width), Math.Max(4F, e.Bounds.Height - 4F * scale));
                using (GraphicsPath path = Theme.RoundedF(highlight, 8F * scale))
                using (Brush fill = new SolidBrush(Theme.Sky)) e.Graphics.FillPath(fill, path);
            }

            Color badgeFill, badgeInk;
            KindColors(entry.Kind, out badgeFill, out badgeInk);
            RectangleF badge = new RectangleF(e.Bounds.Left + 8F * scale, e.Bounds.Top + (e.Bounds.Height - 34F * scale) / 2F, 34F * scale, 34F * scale);
            using (GraphicsPath badgePath = Theme.RoundedF(badge, 8F * scale))
            using (Brush fill = new SolidBrush(badgeFill)) e.Graphics.FillPath(fill, badgePath);
            TextRenderer.DrawText(e.Graphics, entry.Extension, Theme.Small, Rectangle.Round(badge), badgeInk, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

            int sizeWidth = (int)(74 * scale);
            int textLeft = (int)(badge.Right + 12F * scale);
            int textWidth = Math.Max(1, e.Bounds.Right - textLeft - sizeWidth - (int)(14 * scale));
            Rectangle titleRect = new Rectangle(textLeft, e.Bounds.Top + (int)(8 * scale), textWidth, (int)(21 * scale));
            TextRenderer.DrawText(e.Graphics, entry.Name, Theme.Section, titleRect, selected ? Theme.OnPrimaryContainer : Theme.Ink, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            Rectangle pathRect = new Rectangle(textLeft, titleRect.Bottom + (int)(2 * scale), textWidth, (int)(17 * scale));
            TextRenderer.DrawText(e.Graphics, entry.Folder, Theme.Small, pathRect, Theme.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
            Rectangle sizeRect = new Rectangle(e.Bounds.Right - sizeWidth - (int)(8 * scale), e.Bounds.Top, sizeWidth, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, Theme.SizeText(entry.Size), Theme.Small, sizeRect, selected ? Theme.OnPrimaryContainer : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            if (!selected && e.Index < files.Items.Count - 1)
                using (Pen sep = new Pen(Theme.Line)) e.Graphics.DrawLine(sep, textLeft, e.Bounds.Bottom - 1, e.Bounds.Right - (int)(8 * scale), e.Bounds.Bottom - 1);
        }

        private string Link()
        {
            if (session == null || !session.Active) throw new IOException("请先开启临时传输。");
            string selectedIp = addressPicker.SelectedItem;
            if (string.IsNullOrEmpty(selectedIp)) throw new IOException("没有可用的局域网地址。");
            return session.Url(selectedIp);
        }

        private string StateText()
        {
            int left = (int)Math.Ceiling(session.RemainingMilliseconds / 60000.0);
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
            if (session != null && session.Active && addressPicker.SelectedItem != null)
            {
                linkBox.Text = session.Url(addressPicker.SelectedItem);
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
                ReloadAddresses();
                if (addressPicker.SelectedItem == null) throw new IOException("没有可用的局域网，请先连接 Wi-Fi、热点或以太网。");
                SaveLifetime();
                session = new GuestTransfer(files.Items.Cast<ShareEntry>().Select(e => new SendBatch.Item{Path=e.FullPath,Relative=e.Relative}), network.Settings.ReceiveFolder, upload.Checked, network.LoopbackOnly, network.Settings.GuestLifetimeMinutes, allowText.Checked);
                session.Received = (path, name) => main.Ui(() =>
                {
                    if (!IsDisposed) hasReceivedFiles = true;
                    main.GuestReceived(path, name);
                    if (!IsDisposed)
                    {
                        if (session != null && session.Active) SetState(StateText(), false);
                        UpdateActions();
                    }
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

            UpdateStatusChip();
            statusChip.Location = new Point(statusChip.Left, (headerStrip.Height - statusChip.Height) / 2);
            allowText.Left = statusChip.Right + (int)Math.Round(12 * CurrentDpiScale());
            int hintX = allowText.Right + (int)Math.Round(12 * CurrentDpiScale());
            headerHint.Bounds = new Rectangle(hintX, 0, Math.Max(40, headerStrip.Width - hintX - (int)Math.Round(18 * CurrentDpiScale())), headerStrip.Height);

            start.Text = active ? "结束临时传输" : "开启临时传输";
            StyleActionButton(start, active ? 3 : 4, 8);
            start.Invalidate();

            choose.Enabled = chooseFolder.Enabled = !active;
            removeBtn.Enabled = !active && files.SelectedIndex >= 0;
            clearBtn.Enabled = !active && count > 0;
            upload.Enabled = allowText.Enabled = !active;
            textTransfer.Enabled = active && session.AllowText;
            lifetimeField.Enabled = saveLifetime.Enabled = !active;
            lifetime.ForeColor = active ? Theme.Muted : Theme.Ink;
            lifetimeField.Invalidate();
            refreshIp.Enabled = !active;
            addressPicker.Enabled = !active;
            copy.Enabled = qr.Enabled = active;
            bool tasksWasVisible = openTasks.Visible;
            openTasks.Visible = hasReceivedFiles || (session != null && session.SavedFiles.Length > 0);
            if (tasksWasVisible != openTasks.Visible) Relayout();
            UpdateLinkPreview();
        }

        internal void Stop()
        {
            if(textWindow!=null){textWindow.ClearAndClose();textWindow=null;}
            copyReset.Stop();
            copy.Text = "复制链接";
            if (session != null)
            {
                if (session.SavedFiles.Length > 0) hasReceivedFiles = true;
                session.Dispose();
                SetState(session.CleanupError ?? "临时传输已结束，完整文件已保留", session.CleanupError != null);
                session = null;
                allowText.Checked = false;
            }
            ReloadAddresses();
            UpdateActions();
        }
        private void OpenText()
        {
            GuestTransfer current=session;
            if(current==null||!current.Active||!current.AllowText)return;
            if(textWindow==null||textWindow.IsDisposed)textWindow=new GuestTextWindow(current,()=>session==current&&current.Active);
            textWindow.Show(this);textWindow.Activate();
        }
        private void CloseNetworkSession()
        {
            GuestTransfer current=session;
            if(current!=null)current.Dispose();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Rectangle work = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(680, work.Width), Math.Min(580, work.Height));
            WindowPlacement saved = main.GuestWindowPlacement;
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
            else if (Width > work.Width - 16 || Height > work.Height - 16)
            {
                int width = Math.Min(work.Width - 16, Math.Max(MinimumSize.Width, Width));
                int height = Math.Min(work.Height - 16, Math.Max(MinimumSize.Height, Height));
                Size = new Size(width, height);
            }
            windowLoaded = true;
            Relayout();
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
            windowSaveTimer.Stop();
            windowSaveTimer.Start();
        }

        private void SaveWindowPlacement()
        {
            if (!windowLoaded || placement == null || !placementPending || main.IsDisposed || main.Disposing) return;
            main.SaveGuestWindowPlacement(placement);
            placementPending = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            TrackWindowPlacement(this, EventArgs.Empty);
            windowSaveTimer.Stop();
            if (placementPending && !main.IsDisposed && !main.Disposing) SaveWindowPlacement();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed)
            {
                windowSaveTimer.Stop();
                if (placementPending && !main.IsDisposed && !main.Disposing) SaveWindowPlacement();
                windowSaveTimer.Dispose();
                Theme.Changed -= ApplyColors;
                network.NetworkReset -= CloseNetworkSession;
                timer.Stop(); timer.Dispose();
                copyReset.Stop(); copyReset.Dispose();
                tips.Dispose();
                monoFont.Dispose();
                if(textWindow!=null){textWindow.ClearAndClose();textWindow=null;}
                if (session != null) { session.Dispose(); session = null; }
            }
            base.Dispose(disposing);
        }
    }
}
