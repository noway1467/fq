using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static partial class TransferFeatureTests
    {
        private static ChatRecord SavedFile(LocalStore store, long packet, string path, bool outgoing = false, string peer = "cleanup-peer")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "接收文件 " + packet);
            var record = new ChatRecord { PeerId = peer, Packet = packet, Time = DateTime.Now, Text = "[文件] " + Path.GetFileName(path), State = "已保存", Outgoing = outgoing, HasAttachments = true, LocalFiles = new[] { path }, Transfers = new[] { new FileTransferState { Id = 0, Name = Path.GetFileName(path), Size = new FileInfo(path).Length, LocalPath = path, Status = "已保存", Percent = 100 } } };
            store.Append(record); return record;
        }
        private static void SettingsCleanupRegressions(bool layoutOnly = false)
        {
            if (!layoutOnly) {
            Run("清理只处理确认前列出的文件，同一条消息后来收完的附件保留", () => {
                var store = new LocalStore(Path.Combine(root, "cleanup-confirm-range"));
                using (var network = new LanService(new AppSettings(), FreePort(), true, "cleanup-range"))
                using (var main = new MainForm(network, store)) {
                    main.Show();
                    string first = Path.Combine(store.Root, "原附件.txt"), later = Path.Combine(store.Root, "后来收到.txt");
                    var record = SavedFile(store, 1, first); main.TrackTransfer(record);
                    Task cleanup = null;
                    string result = FinishDialogs(() => cleanup = main.CleanReceivedFiles(main), "清理已接收文件？", "清理文件", () => cleanup.IsCompleted, () => {
                        File.WriteAllText(later, "后来收到"); record.LocalFiles = new[] { first, later };
                        record.Transfers = record.Transfers.Concat(new[] { new FileTransferState { Id = 1, Name = "后来收到.txt", LocalPath = later, Size = new FileInfo(later).Length, Status = "已保存" } }).ToArray();
                        main.TrySave(record);
                    });
                    var stored = new LocalStore(store.Root).IncomingAttachments(record.PeerId).Single();
                    Check(!File.Exists(first) && File.Exists(later) && stored.Transfers[1].LocalPath == later && result.Contains("已删除 1 个"), "清理越过确认范围：" + result);
                }
            });
            Run("仅剩已移走文件时可订正旧状态，确认期间重新出现的同名文件保留", () => {
                var store = new LocalStore(Path.Combine(root, "cleanup-missing-only"));
                using (var network = new LanService(new AppSettings(), FreePort(), true, "cleanup-missing"))
                using (var main = new MainForm(network, store)) {
                    main.Show();
                    string missing = Path.Combine(store.Root, "已移走.txt"), returned = Path.Combine(store.Root, "同名.txt");
                    var first = SavedFile(store, 1, missing); SavedFile(store, 2, returned);
                    DateTime stamp = File.GetLastWriteTimeUtc(returned); byte[] bytes = File.ReadAllBytes(returned);
                    File.Delete(missing); File.Delete(returned);
                    Task cleanup = null;
                    FinishDialogs(() => cleanup = main.CleanReceivedFiles(main), "清理已接收文件？", "更新记录", () => cleanup.IsCompleted, () => {
                        File.WriteAllBytes(returned, bytes); File.SetLastWriteTimeUtc(returned, stamp);
                    });
                    var stored = new LocalStore(store.Root).IncomingAttachments(first.PeerId);
                    Check(stored.Single(r => r.Packet == 1).Transfers[0].Status == "本机文件已删除" && stored.Single(r => r.Packet == 2).Transfers[0].LocalPath == returned && File.Exists(returned), "缺失状态未落盘或误删了后来出现的文件");
                }
            });
            Run("清理跨保存目录、重启及清空聊天后登记，保留未登记文件和发送原件", () => {
                var store = new LocalStore(Path.Combine(root, "cleanup-directories"));
                string folderA = Path.Combine(store.Root, "旧目录"), folderB = Path.Combine(store.Root, "新目录");
                var first = SavedFile(store, 1, Path.Combine(folderA, "接收.txt"));
                store.SaveSettings(new AppSettings { ReceiveFolder = folderA });
                store.ClearHistory(first.PeerId);
                store.SaveSettings(new AppSettings { ReceiveFolder = folderB });
                var second = SavedFile(store, 2, Path.Combine(folderB, "接收.txt"));
                var guest = SavedFile(store, 3, Path.Combine(folderA, "访客.txt"), false, "browser-guests");
                var outgoing = SavedFile(store, 4, Path.Combine(folderB, "发送原件.txt"), true);
                string otherA = Path.Combine(folderA, "用户文件.txt"), otherB = Path.Combine(folderB, "用户文件.txt"), partial = Path.Combine(folderB, ".feiq-pending.part");
                foreach (string path in new[] { otherA, otherB, partial }) File.WriteAllText(path, "保留");
                store = new LocalStore(store.Root); var records = store.ReceivedFileRecords();
                var planned = store.DeleteReceivedFiles(records, false, null, true);
                Check(planned.DeletedCount == 3 && File.Exists(first.GetLocalFiles()[0]), "预览范围错误或提前删除");
                var result = store.DeleteReceivedFiles(records, true, null, true);
                Check(result.DeletedCount == 3 && result.FailedCount == 0, result.Text);
                Check(new[] { otherA, otherB, partial, outgoing.GetLocalFiles()[0] }.All(File.Exists), "误删非接收文件");
                Check(!File.Exists(first.GetLocalFiles()[0]) && !File.Exists(second.GetLocalFiles()[0]) && !File.Exists(guest.GetLocalFiles()[0]), "漏清旧目录或访客文件");
                var reopened = new LocalStore(store.Root);
                Check(reopened.History(first.PeerId, 10).All(r => r.Packet != 1), "清理恢复了已清空的消息");
                Check(reopened.History(first.PeerId, 10).Single(r => r.Packet == 2).Transfers[0].Status == "本机文件已删除", "删除状态未持久化");
                Check(reopened.LoadSettings().ReceiveFolder == folderB, "清理改写了自定义保存目录");
            });
            Run("清理保护修改过的同名文件、忙碌附件、占用文件并继续其余项", () => {
                var store = new LocalStore(Path.Combine(root, "cleanup-protection"));
                var changed = SavedFile(store, 1, Path.Combine(store.Root, "modified.txt"));
                var busy = SavedFile(store, 2, Path.Combine(store.Root, "busy.txt"));
                var locked = SavedFile(store, 3, Path.Combine(store.Root, "locked.txt"));
                var ready = SavedFile(store, 4, Path.Combine(store.Root, "%OS% ready.txt"));
                string changedPath = changed.LocalFiles[0], busyPath = busy.LocalFiles[0], lockedPath = locked.LocalFiles[0], readyPath = ready.LocalFiles[0];
                File.WriteAllText(changedPath, "用户后来替换的新文件，必须保留"); busy.Transfers[0].Status = "保存中";
                var alias = new ChatRecord { PeerId = "alias", Packet = 9, HasAttachments = true, LocalFiles = new[] { busyPath } };
                ReceivedFilesDeletionResult result;
                using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    result = store.DeleteReceivedFiles(new[] { changed, alias, busy, locked, ready }, true, (r, f) => f.Status == "保存中", true);
                Check(result.DeletedCount == 1 && result.FailedCount == 2 && result.SkippedBusyCount == 1, result.Text);
                Check(File.Exists(changedPath) && File.Exists(busyPath) && File.Exists(lockedPath) && !File.Exists(readyPath), "保护范围错误");
                result = store.DeleteReceivedFiles(new[] { locked }, true, null, true); Check(result.DeletedCount == 1, "解除占用后无法重试");
            });
            Run("旧版历史导入仅使用最新入站登记，缺失文件不扫描目录补猜", () => {
                var store = new LocalStore(Path.Combine(root, "cleanup-legacy"));
                var saved = SavedFile(store, 1, Path.Combine(store.Root, "旧版本接收.txt"));
                string index = Path.Combine(store.Root, "received-files.json"); File.Delete(index);
                store = new LocalStore(store.Root); var records = store.ReceivedFileRecords();
                Check(store.DeleteReceivedFiles(records, true, null, true).DeletedCount == 1, "旧版登记未迁移");
                File.WriteAllText(saved.GetLocalFiles()[0], "同名的其他文件");
                store = new LocalStore(store.Root); records = store.ReceivedFileRecords();
                Check(store.DeleteReceivedFiles(records, true, null, true).DeletedCount == 0 && File.Exists(saved.GetLocalFiles()[0]), "旧完成行恢复后误删同名文件");
                File.WriteAllText(index, "broken index");
                bool rejected = false; try { new LocalStore(store.Root).ReceivedFileRecords(); } catch (IOException) { rejected = true; }
                Check(rejected && File.ReadAllText(index) == "broken index", "损坏登记被悄悄覆盖");
            });
            }
            foreach (bool dark in new[] { false, true })
            {
                string theme = dark ? "dark" : "light"; Theme.Configure(dark, "blue");
                Run("远控浮条 " + theme + "：渲染、最小化继续会话、任务栏恢复及断开", () => {
                    int disconnected = 0;
                    using (var banner = new RemoteBanner("192.168.0.103", () => disconnected++))
                    {
                        banner.Show(); PumpFor(40);
                        foreach (Button button in Controls(banner).OfType<Button>()) Check(button.Parent.ClientRectangle.Contains(button.Bounds), "浮条按钮越界");
                        SaveSnapshot(banner, "remote-banner-" + theme);
                        var flags = (CreateParams)typeof(RemoteBanner).GetProperty("CreateParams", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(banner, null);
                        Check(banner.ShowInTaskbar && (flags.ExStyle & 0x00040000) != 0 && (flags.ExStyle & 0x80) == 0 && (flags.ExStyle & 0x08000000) != 0, "任务栏或不抢焦点标志错误");
                        Controls(banner).OfType<Button>().Single(b => b.AccessibleName == "最小化到任务栏").PerformClick(); PumpFor(20);
                        Check(banner.WindowState == FormWindowState.Minimized && !banner.TopMost && disconnected == 0, "最小化错误断开或仍然置顶");
                        // SC_RESTORE 是任务栏恢复窗口使用的系统命令。
                        Message restore = Message.Create(banner.Handle, 0x112, new IntPtr(0xf120), IntPtr.Zero);
                        typeof(RemoteBanner).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(banner, new object[] { restore }); PumpFor(20);
                        Check(banner.Visible && banner.WindowState == FormWindowState.Normal && banner.TopMost, "任务栏未恢复浮条");
                        Controls(banner).OfType<Button>().Single(b => b.AccessibleName == "断开远控").PerformClick(); Check(disconnected == 1, "断开按钮无效");
                        banner.Close(); Check(banner.WindowState == FormWindowState.Minimized && disconnected == 1, "关闭浮条改变了连接");
                    }
                    using (var banner = new RemoteBanner("很长的设备地址 / Long remote address 192.168.100.100", () => { })) { banner.Show(); PumpFor(20); SaveSnapshot(banner, "remote-banner-long-" + theme); }
                });
                Run("托盘菜单 " + theme + "：紧凑宽度、反复打开及完整文字", () => {
                    var store = new LocalStore(Path.Combine(root, "compact-menu-" + theme));
                    using (var network = new LanService(new AppSettings { CloseToTray = false }, FreePort(), true, "compact-menu"))
                    using (var main = new MainForm(network, store)) {
                        var menu = Field<NotifyIcon>(main, "tray").ContextMenuStrip;
                        float scale; using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96F;
                        for (int i = 0; i < 3; i++) {
                            menu.Show(new Point(100, 100)); PumpFor(20);
                            Check(menu.Width >= 120 * scale && menu.Width <= 150 * scale, "托盘菜单过宽：" + menu.Width);
                            foreach (ToolStripMenuItem item in menu.Items) Check(Theme.MenuTextLeft(menu, scale) + TextRenderer.MeasureText(item.Text, item.Font).Width + Theme.MenuTrailing(item, scale) <= menu.Width, "菜单文字被截断");
                            if (i == 0) SaveMenu(menu, "compact-tray-" + theme); menu.Close();
                        }
                    }
                });
                if (layoutOnly) continue;
                Run("设置与聊天菜单 " + theme + "：外观直达、纯会话管理、文件清理确认及空态", () => {
                    var store = new LocalStore(Path.Combine(root, "cleanup-ui-" + theme));
                    var settings = new AppSettings { CloseToTray = false, Notifications = false, AutoReceiveFiles = false, DarkMode = dark, ReceiveFolder = Path.Combine(store.Root, "Custom 中文 English"), Conversations = new[] { new SavedConversation { Endpoint = "127.0.0.1:" + FreePort(), Login = "ui-peer", Nickname = "中文 English 很长的联系人名称" } } };
                    using (var network = new LanService(settings, FreePort(), true, "cleanup-ui"))
                    using (var main = new MainForm(network, store))
                    {
                        main.Show(); Peer peer = network.Peers.Single(); main.OpenChat(peer); PumpFor(40);
                        main.ShowConversationMenu(peer, true); var management = Field<ContextMenuStrip>(main, "conversationMenu");
                        Check(management.Items.Cast<ToolStripItem>().Select(i => i.Text).SequenceEqual(new[] { "设置备注…", "置顶会话", "清空聊天记录…", "删除联系人…" }), "管理菜单仍包含非会话操作");
                        SaveMenu(management, "conversation-management-" + theme); management.Close();
                        main.ShowConversationMenu(peer); Check(management.Items.Cast<ToolStripItem>().Any(i => i.Text == "浏览共享文件…"), "联系人右键功能被移除"); management.Close();
                        ChatForm chat = Field<System.Collections.Generic.Dictionary<string, ChatForm>>(main, "chats")[peer.Id];
                        Controls(chat).OfType<Button>().Single(b => b.AccessibleName == "会话菜单").PerformClick();
                        var menu = Field<ContextMenuStrip>(chat, "chatMenu"); bool appearance = false;
                        using (var timer = new System.Windows.Forms.Timer { Interval = 25 }) {
                            timer.Tick += delegate { var dialog = Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault(); if (dialog == null) return; timer.Stop(); appearance = Controls(dialog).Single(c => c.AccessibleName == "设置内容：外观").Visible; SaveSnapshot(dialog, "settings-appearance-" + theme); dialog.Close(); };
                            timer.Start(); menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "外观设置…").PerformClick();
                        }
                        Check(appearance, "聊天入口没有直接打开外观"); menu.Close();
                        var saved = SavedFile(store, 5, Path.Combine(settings.ReceiveFolder, "接收.txt"), false, peer.Id); string path = saved.LocalFiles[0];
                        using (var dialog = new SettingsDialog(settings, store.Root, network.Port, false, false, SettingsPage.Files, main.CleanReceivedFiles)) {
                            dialog.Show(main); PumpFor(25); SaveSnapshot(dialog, "settings-files-" + theme);
                            var clear = Controls(dialog).OfType<Button>().Single(b => b.Text == "清理已接收文件");
                            string text = FinishDialogs(() => clear.PerformClick(), "清理已接收文件？", "清理文件", () => clear.Enabled, () => SaveSnapshot(Application.OpenForms.OfType<ConfirmDialog>().Single(), "cleanup-confirm-" + theme));
                            Check(!File.Exists(path) && text.Contains("已删除 1 个"), "清理入口未实际完成：" + text);
                            text = FinishDialogs(() => clear.PerformClick(), null, null, () => clear.Enabled);
                            Check(text.Contains("没有可清理"), "空态提示不正确");
                            dialog.Close();
                        }
                        main.Close();
                    }
                });
            }
        }
    }
}
