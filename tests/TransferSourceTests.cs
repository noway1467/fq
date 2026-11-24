using System;
using System.Collections.Generic;
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
        private static bool CommandKey(Control control, Keys keys)
        {
            object[] arguments = { Message.Create(control.Handle, 0x0100, (IntPtr)(int)(keys & Keys.KeyCode), IntPtr.Zero), keys };
            return (bool)typeof(Control).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, arguments);
        }
        private static void StripMouse(TransferSourceStrip strip, string method, MouseButtons button, int x, int y)
        {
            typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { new MouseEventArgs(button, button == MouseButtons.None ? 0 : 1, x, y, 0) });
        }
        private static void TransferSourceEntryRegression()
        {
            Theme.Configure(false, "blue");
            var store = new LocalStore(Path.Combine(root, "source-entry"));
            var settings = new AppSettings { CloseToTray = false, Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(store.Root, "received"), Conversations = new[] {
                new SavedConversation { DeviceId = new string('1', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "entry-a", Nickname = "入口设备 A" },
                new SavedConversation { DeviceId = new string('2', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "entry-b", Nickname = "没有文件记录的设备 B" } } };
            using (var network = new LanService(settings, FreePort(), true, "source-entry"))
            using (var main = new MainForm(network, store))
            {
                Peer first = network.Peers.Single(peer => peer.Login == "entry-a"), second = network.Peers.Single(peer => peer.Login == "entry-b");
                main.TrackTransfer(new ChatRecord { PeerId = first.Id, Packet = 1, Time = DateTime.Now, Sender = first.DisplayName, Transfers = new[] { new FileTransferState { Id = 0, Name = "入口.txt", Status = "已保存" } } });
                main.Show();
                Button menuButton = Controls(main).OfType<Button>().Single(button => button.AccessibleName == "菜单");
                menuButton.PerformClick();
                ContextMenuStrip mainMenu = Field<ContextMenuStrip>(main, "mainMenu");
                mainMenu.Items.Cast<ToolStripItem>().Single(item => item.Text == "传输任务…").PerformClick(); mainMenu.Close();
                TransferWindow window = Field<TransferWindow>(main, "transferWindow");
                Application.DoEvents(); Check(window.Visible && window.SelectedSourceId == "", "主菜单初次进入没有选中全部");
                window.Close();

                main.OpenChat(first);
                Controls(main.ActiveChat).OfType<Button>().Single(button => button.AccessibleName == "传输任务").PerformClick();
                window = Field<TransferWindow>(main, "transferWindow");
                Check(window.SelectedSourceId == first.Id, "聊天入口没有选中当前联系人");
                Field<Button>(window, "searchButton").PerformClick();
                TextBox query = Field<TextBox>(window, "query"); query.Text = "入口";
                Field<RoundedButton[]>(window, "filters")[3].PerformClick();
                main.OpenChat(second);
                window.WindowState = FormWindowState.Minimized;
                Controls(main.ActiveChat).OfType<Button>().Single(button => button.AccessibleName == "传输任务").PerformClick();
                Check(Object.ReferenceEquals(window, Field<TransferWindow>(main, "transferWindow")) && window.WindowState == FormWindowState.Normal && window.SelectedSourceId == second.Id, "重复聊天入口未恢复原窗口并重设来源");
                Check(query.Visible && query.Text == "入口" && Field<int>(window, "filter") == 3, "切换入口丢失了已展开搜索或状态筛选");
                TransferSourceStrip strip = Field<TransferSourceStrip>(window, "sources");
                Check(strip.Items[1].Id == second.Id && Field<ListBox>(window, "list").Items.Count == 0, "没有记录的会话来源未保留在全部之后");
                menuButton.PerformClick(); mainMenu.Items.Cast<ToolStripItem>().Single(item => item.Text == "传输任务…").PerformClick(); mainMenu.Close();
                Check(window.SelectedSourceId == second.Id && query.Text == "入口" && Field<int>(window, "filter") == 3, "主菜单重入重置了来源、搜索或状态");

                using (var guest = new GuestWindow(main, network))
                {
                    guest.Show();
                    Button received = Field<Button>(guest, "openTasks"); received.Visible = true; received.PerformClick();
                    Check(window.SelectedSourceId == "browser-guests" && Object.ReferenceEquals(window, Field<TransferWindow>(main, "transferWindow")), "访客入口未重设来源或重复创建任务窗口");
                    Check(!strip.Items.Any(item => item.Id == second.Id) && strip.Items[1].Name == "浏览器访客", "切走后仍保留无记录联系人或丢失无记录访客选中项");
                    Check(query.Text == "入口" && Field<int>(window, "filter") == 3, "访客入口改变了搜索或状态");
                    window.Close(); received.PerformClick();
                    window = Field<TransferWindow>(main, "transferWindow");
                    Check(window.SelectedSourceId == "browser-guests" && !Field<TextBox>(window, "query").Visible, "访客初次进入没有选中访客或搜索没有默认折叠");
                    window.Close(); guest.Close();
                }
                main.OpenTransfers(); window = Field<TransferWindow>(main, "transferWindow");
                Check(window.SelectedSourceId == "", "关闭重开后持久化了来源选择");
                window.Close(); main.ExitApplication();
            }
        }
        private static void SourceSnapshotCheck(TransferWindow window, TransferSourceStrip strip, Action action)
        {
            Field<Button>(window, "searchButton").Focus(); Application.DoEvents();
            TransferSourceStrip.Source[] snapshot = Field<TransferSourceStrip.Source[]>(strip, "items"); int redraws = 0;
            InvalidateEventHandler observer = (sender, e) => redraws++;
            strip.Invalidated += observer;
            action(); Tick(Field<System.Windows.Forms.Timer>(window, "timer"));
            strip.Invalidated -= observer;
            Check(Object.ReferenceEquals(snapshot, Field<TransferSourceStrip.Source[]>(strip, "items")) && redraws == 0, "状态、搜索或静止刷新重建了来源横排");
        }
        private static void TransferSourceCollectionRegression()
        {
            Theme.Configure(false, "blue");
            var store = new LocalStore(Path.Combine(root, "source-collection"));
            var settings = new AppSettings { CloseToTray = false, Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(store.Root, "received"), Conversations = new[] {
                new SavedConversation { DeviceId = new string('3', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "collection-a", Nickname = "设备 A", Avatar = "builtin:1" },
                new SavedConversation { DeviceId = new string('4', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "collection-b", Nickname = "设备 B" },
                new SavedConversation { DeviceId = new string('5', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "collection-empty", Nickname = "空设备" } } };
            using (var network = new LanService(settings, FreePort(), true, "source-collection"))
            using (var main = new MainForm(network, store))
            using (var window = new TransferWindow(main))
            {
                Peer first = network.Peers.Single(peer => peer.Login == "collection-a"), second = network.Peers.Single(peer => peer.Login == "collection-b"), noFiles = network.Peers.Single(peer => peer.Login == "collection-empty");
                DateTime time = DateTime.Now;
                var records = new[] {
                    new ChatRecord { PeerId = first.Id, Packet = 1, Sender = "旧昵称 A", Time = time.AddHours(-1).ToUniversalTime() },
                    new ChatRecord { PeerId = first.Id, Packet = 2, Sender = "旧昵称 A", Time = time.AddDays(-1) },
                    new ChatRecord { PeerId = second.Id, Packet = 3, Sender = "旧昵称 B", Time = time.AddHours(-2) },
                    new ChatRecord { PeerId = "removed:b", Packet = 4, Sender = "同名旧设备", Time = time.AddHours(-3) },
                    new ChatRecord { PeerId = "removed:a", Packet = 5, Sender = "同名旧设备", Time = time.AddHours(-3) },
                    new ChatRecord { PeerId = "removed:z", Packet = 6, Sender = "A 旧设备", Time = time.AddHours(-3) },
                    new ChatRecord { PeerId = "browser-guests", Packet = 7, Sender = "浏览器访客", Time = time.AddMinutes(-30) } };
                foreach (ChatRecord record in records) { record.Transfers = new[] { new FileTransferState { Id = 0, Name = "文件 " + record.Packet, Status = "已暂停" } }; main.TrackTransfer(record); }
                main.TrackTransfer(new ChatRecord { PeerId = noFiles.Id, Packet = 99, Time = time, Transfers = new FileTransferState[0] });
                main.Show(); window.Show(); PumpFor(80);
                TransferSourceStrip strip = Field<TransferSourceStrip>(window, "sources");
                string[] expected = { "", "browser-guests", first.Id, second.Id, "removed:z", "removed:a", "removed:b" };
                Check(strip.Items.Select(item => item.Id).SequenceEqual(expected), "来源没有按最近时间、名称、ID 稳定排序或同设备出现重复项");
                Check(strip.Items[0].Name == "全部" && strip.Items[0].Glyph == "transfer" && strip.Items[1].Glyph == "link", "全部或访客来源未复用既有图标");
                Check(strip.Items.Single(item => item.Id == first.Id).Name == first.DisplayName && strip.Items.Single(item => item.Id == first.Id).Avatar == first.Avatar, "来源没有复用当前联系人名称和头像");
                Check(strip.Items.Single(item => item.Id == "removed:a").Name == "同名旧设备" && strip.Items.Single(item => item.Id == "removed:a").Avatar == null, "已不存在的联系人未保留回退名称与默认头像");
                SourceSnapshotCheck(window, strip, () => { Field<RoundedButton[]>(window, "filters")[3].PerformClick(); Field<Button>(window, "searchButton").PerformClick(); Field<TextBox>(window, "query").Text = "没有匹配"; });
                Field<Button>(window, "searchButton").PerformClick(); Field<RoundedButton[]>(window, "filters")[0].PerformClick();
                first.Note = "刚改名的设备"; first.Avatar = "builtin:7"; Tick(Field<System.Windows.Forms.Timer>(window, "timer"));
                Check(strip.Items.Single(item => item.Id == first.Id).Name == first.DisplayName && strip.Items.Single(item => item.Id == first.Id).Avatar == "builtin:7", "刷新未同步改名或头像");
                Check(Field<ListBox>(window, "list").Items.Cast<object>().Where(item => Field<ChatRecord>(item, "Record").PeerId == first.Id).All(item => Field<string>(item, "Peer") == first.DisplayName), "来源名称与行内名称不同源");
                window.SelectSource(noFiles.Id);
                Check(strip.Items[1].Id == noFiles.Id && strip.AccessibilityObject.GetSelected().Name == "空设备 · 已选中", "无记录选中项顺序或无障碍选中名称错误");
                AccessibleObject removedSelection = strip.AccessibilityObject.GetSelected(); window.SelectSource("");
                Check(!strip.Items.Any(item => item.Id == noFiles.Id) && (removedSelection.State & AccessibleStates.Unavailable) != 0, "切走后无记录来源未移除或旧无障碍对象仍可用");
                records[3].Time = time.AddMinutes(1); Tick(Field<System.Windows.Forms.Timer>(window, "timer"));
                Check(strip.Items[1].Id == "removed:b", "来源最近记录改变后没有重新排序");
                window.Close(); main.ExitApplication();
            }
        }
        private static void TransferSourceFilterRegression()
        {
            Theme.Configure(false, "blue"); var store = new LocalStore(Path.Combine(root, "source-filter"));
            using (var network = new LanService(new AppSettings { CloseToTray = false, Notifications = false, ReceiveFolder = Path.Combine(store.Root, "received") }, FreePort(), true, "source-filter"))
            using (var main = new MainForm(network, store))
            using (var window = new TransferWindow(main))
            {
                string[] peers = { "filter:a", "filter:a", "filter:a", "filter:b", "filter:b", "browser-guests" };
                string[] names = { "report.txt", "report-done.txt", "other.txt", "report.txt", "other.txt", "report-guest.txt" };
                string[] states = { "接收中", "已保存", "已暂停", "接收中", "已暂停", "已保存" };
                for (int i = 0; i < peers.Length; i++) main.TrackTransfer(new ChatRecord { PeerId = peers[i], Sender = peers[i] == "filter:a" ? "甲设备" : peers[i] == "filter:b" ? "乙设备" : "浏览器访客", Packet = i + 1, Time = DateTime.Now.AddMinutes(-i), Transfers = new[] { new FileTransferState { Id = 0, Name = names[i], Size = 1024, Status = states[i], Percent = i == 0 ? 25 : states[i] == "已保存" ? 100 : 0 } } });
                main.Show(); window.Show(); PumpFor(80);
                ListBox list = Field<ListBox>(window, "list"); TransferSourceStrip strip = Field<TransferSourceStrip>(window, "sources");
                window.SelectSource("filter:a"); Field<Button>(window, "searchButton").PerformClick(); Field<TextBox>(window, "query").Text = "REPORT"; Field<RoundedButton[]>(window, "filters")[1].PerformClick();
                Check(list.Items.Count == 1 && Field<ChatRecord>(list.Items[0], "Record").Packet == 1, "来源、状态、大小写搜索没有取交集");
                Check(Field<Label>(window, "summary").Text == "1 个文件 · 0 个已完成 · 256 B / 1.0 KB", "汇总没有使用当前显示列表");
                window.SelectSource("filter:b"); Check(list.Items.Count == 1 && Field<ChatRecord>(list.Items[0], "Record").Packet == 4 && Field<TextBox>(window, "query").Visible, "切换来源丢失搜索或没有切换交集");
                Field<RoundedButton[]>(window, "filters")[2].PerformClick(); Check(list.Items.Count == 0 && Field<Label>(window, "empty").Text == "没有匹配的文件或设备", "交集空状态改变旧文案");
                Field<TextBox>(window, "query").Text = "乙设备"; Check(list.Items.Count == 1 && Field<ChatRecord>(list.Items[0], "Record").Packet == 5, "搜索不匹配来源名称");
                Check(strip.Items.Count == 4, "交集筛选改变了横排来源构成");
                window.SelectSource("browser-guests"); Field<RoundedButton[]>(window, "filters")[3].PerformClick(); Field<TextBox>(window, "query").Text = "report";
                Check(list.Items.Count == 1 && Field<ChatRecord>(list.Items[0], "Record").PeerId == "browser-guests", "访客来源不能叠加状态和搜索");
                Field<Button>(window, "searchButton").PerformClick(); window.SelectSource("");
                Check(list.Items.Count == 2 && Field<Label>(window, "summary").Text.StartsWith("2 个文件 · 2 个已完成"), "全部来源没有保留状态筛选或统计当前列表");
                window.Close(); main.ExitApplication();
            }
        }
        private static void TransferSearchRegression()
        {
            Theme.Configure(false, "blue"); var store = new LocalStore(Path.Combine(root, "search-button"));
            using (var network = new LanService(new AppSettings { CloseToTray = false, Notifications = false, ReceiveFolder = Path.Combine(store.Root, "received") }, FreePort(), true, "search-button"))
            using (var main = new MainForm(network, store))
            using (var window = new TransferWindow(main))
            {
                main.TrackTransfer(new ChatRecord { PeerId = "search:a", Packet = 1, Sender = "搜索设备", Time = DateTime.Now, Transfers = new[] { new FileTransferState { Id = 0, Name = "visible.txt", Status = "已保存" } } });
                main.Show(); window.Show(); PumpFor(80);
                Button button = Field<Button>(window, "searchButton"); TextBox query = Field<TextBox>(window, "query"); ListBox list = Field<ListBox>(window, "list");
                Check(button.Visible && button.AccessibleName == "搜索" && Field<ToolTip>(window, "tips").GetToolTip(button) == "搜索" && ((RoundedButton)button).Glyph == "search", "搜索按钮图标、提示或无障碍名称错误");
                Check(!query.Visible && query.AccessibleName == "搜索传输任务" && Field<BufferedLayoutTable>(window, "body").RowStyles[1].Height == 0, "搜索默认没有折叠或改变原输入框无障碍名");
                query.Text = "隐形筛选"; Check(query.TextLength == 0 && list.Items.Count == 1, "隐藏的搜索词仍可过滤列表");
                button.PerformClick(); Check(query.Visible && query.Focused, "搜索按钮展开后没有聚焦输入框");
                query.Text = "不存在"; Check(list.Items.Count == 0, "展开搜索没有过滤");
                button.PerformClick(); Check(!query.Visible && query.TextLength == 0 && list.Items.Count == 1 && button.Focused, "再次点击没有收起、清空或恢复列表");
                Check(CommandKey(query, Keys.Control | Keys.F) && query.Visible && query.Focused, "Ctrl+F 没有通过原生控件命令链展开并聚焦搜索");
                window.SelectSource("search:a"); Field<RoundedButton[]>(window, "filters")[3].PerformClick(); query.Text = "不存在";
                Check(CommandKey(query, Keys.Escape) && !query.Visible && query.TextLength == 0 && list.Items.Count == 1 && window.Visible, "Esc 没有收起并清空搜索或误关闭任务窗口");
                Check(window.SelectedSourceId == "search:a" && Field<int>(window, "filter") == 3, "收起搜索修改了来源或状态");
                Check(CommandKey(Field<TransferSourceStrip>(window, "sources"), Keys.Control | Keys.F) && query.Focused, "焦点在来源横排时 Ctrl+F 无效");
                query.Text = "visible"; button.Focus(); Check(CommandKey(button, Keys.Control | Keys.F) && query.Focused && query.Text == "visible", "已展开 Ctrl+F 没有重新聚焦或意外清词");
                Check(CommandKey(button, Keys.Escape) && !query.Visible && query.TextLength == 0, "焦点在搜索按钮时 Esc 没有收起并清空");
                window.Close(); main.ExitApplication();
            }
        }
        private static void TransferTimestampRegression()
        {
            DateTime now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Local);
            Check(TransferWindow.FormatTime(new DateTime(2026, 11, 25, 3, 4, 0), now) == "03:04", "当天时间没有补零或使用 24 小时制");
            Check(TransferWindow.FormatTime(new DateTime(2026, 1, 2, 3, 4, 0), now) == "01-02 03:04", "当年其他日期格式错误");
            Check(TransferWindow.FormatTime(new DateTime(2025, 1, 2, 3, 4, 0), now) == "2025-01-02 03:04", "往年日期格式错误");
            Check(TransferWindow.FormatTime(default(DateTime), now) == "" && TransferWindow.FormatTime(default(DateTime).ToLocalTime(), now) == "" && TransferWindow.FormatTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), now) == "", "缺失或零时间显示了默认年份");
            Check(TransferWindow.FormatTime(now.AddHours(-1).ToUniversalTime(), now) == "11:00", "UTC 记录没有按本地时区显示");
            Theme.Configure(false, "blue"); var store = new LocalStore(Path.Combine(root, "row-time"));
            using (var network = new LanService(new AppSettings { CloseToTray = false, Notifications = false, ReceiveFolder = Path.Combine(store.Root, "received") }, FreePort(), true, "row-time"))
            using (var main = new MainForm(network, store))
            using (var window = new TransferWindow(main))
            {
                window.LocalNow = () => now;
                main.TrackTransfer(new ChatRecord { PeerId = "time:a", Packet = 1, Sender = "时刻设备", Time = now.AddHours(-1), Transfers = new[] { new FileTransferState { Id = 0, Name = "today.txt", Size = 1024, Status = "已暂停" } } });
                main.TrackTransfer(new ChatRecord { PeerId = "time:a", Packet = 2, Sender = "时刻设备", Time = default(DateTime), Outgoing = true, Transfers = new[] { new FileTransferState { Id = 0, Name = "missing.txt", Size = 1024, Status = "等待对方接收" } } });
                main.Show(); window.Show(); PumpFor(80);
                ListBox list = Field<ListBox>(window, "list"); object today = list.Items[0], missing = list.Items[1];
                Check(Field<string>(today, "Meta") == "来自 时刻设备 · 1.0 KB · 11:00" && Field<string>(missing, "Meta") == "发送给 时刻设备 · 1.0 KB", "行副文字方向、大小、时间或缺失时间分隔错误");
                string before = Field<string>(today, "Fingerprint"); var invalidated = new List<Rectangle>(); int sourceRedraws = 0;
                list.Invalidated += (sender, e) => invalidated.Add(e.InvalidRect); Field<TransferSourceStrip>(window, "sources").Invalidated += delegate { sourceRedraws++; };
                Tick(Field<System.Windows.Forms.Timer>(window, "timer")); Check(invalidated.Count == 0 && sourceRedraws == 0, "时间显示引入静止重绘");
                now = now.AddDays(1); Tick(Field<System.Windows.Forms.Timer>(window, "timer"));
                Check(invalidated.Count == 1 && invalidated[0] == list.GetItemRectangle(0) && Field<string>(today, "Fingerprint") != before && Field<string>(today, "Meta").EndsWith("11-25 11:00") && sourceRedraws == 0, "跨天未只刷新时间改变行或重绘了来源横排");
                window.Close(); main.ExitApplication();
            }
        }
        private static void TransferBatchScopeRegression()
        {
            Theme.Configure(false, "blue"); var store = new LocalStore(Path.Combine(root, "batch-scope"));
            var settings = new AppSettings { CloseToTray = false, Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(store.Root, "received"), Conversations = new[] {
                new SavedConversation { DeviceId = new string('6', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "scope-a", Nickname = "批量设备 A" },
                new SavedConversation { DeviceId = new string('7', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "scope-b", Nickname = "批量设备 B" } } };
            using (var network = new LanService(settings, FreePort(), true, "batch-scope"))
            using (var main = new MainForm(network, store))
            using (var window = new TransferWindow(main))
            {
                Peer first = network.Peers.Single(peer => peer.Login == "scope-a"), second = network.Peers.Single(peer => peer.Login == "scope-b");
                string[] names = { "scope-receive.bin", "other-paused.bin", "scope-active.bin", "scope-done.bin", "scope-outgoing.bin", "scope-other-peer.bin", "scope-other-active.bin", "other-queued.bin" };
                string[] states = { "已暂停", "已暂停", "连接中", "已保存", "等待对方接收", "已暂停", "连接中", "排队中" };
                var records = new List<ChatRecord>();
                for (int i = 0; i < names.Length; i++)
                {
                    var record = new ChatRecord { PeerId = i == 5 || i == 6 ? second.Id : first.Id, Packet = 301 + i, Sender = "批量记录", Time = DateTime.Now.AddMinutes(-i), Text = names[i], Outgoing = i == 4, HasAttachments = true, Transfers = new[] { new FileTransferState { Id = 0, Name = names[i], Size = 4096, Modified = 1, Status = states[i] } } };
                    records.Add(record); store.Append(record); main.TrackTransfer(record);
                }
                // 保持调度器处于已有接收中，只核验菜单写入了哪些任务，避免不存在的发送方影响断言。
                typeof(MainForm).GetField("automaticReceiving", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(main, true);
                main.Show(); window.Show(); PumpFor(80); Field<System.Windows.Forms.Timer>(window, "timer").Stop();
                window.SelectSource(first.Id); Field<RoundedButton[]>(window, "filters")[2].PerformClick(); Field<Button>(window, "searchButton").PerformClick(); Field<TextBox>(window, "query").Text = "scope";
                ListBox list = Field<ListBox>(window, "list");
                Check(list.Items.Count == 2, "批量继续夹具没有形成来源、状态和搜索交集");
                RightClickTransfer(list, TransferIndex(list, 301, 0)); ContextMenuStrip menu = Field<ContextMenuStrip>(window, "menu");
                menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "继续当前列表中的接收").PerformClick(); menu.Close();
                Check(records[0].Transfers[0].Status == "排队中" && records.Skip(1).Select(record => record.Transfers[0].Status).SequenceEqual(states.Skip(1)), "继续改变了搜索、状态、来源外任务或发送任务");
                Check(store.IncomingAttachments(first.Id, 301).Single().Transfers[0].Status == "排队中", "继续当前列表没有持久化排队状态");
                Field<RoundedButton[]>(window, "filters")[1].PerformClick();
                Check(list.Items.Count == 2 && list.Items.Cast<object>().All(item => new long[] { 301, 303 }.Contains(Field<ChatRecord>(item, "Record").Packet)), "批量暂停夹具没有形成进行中交集");
                RightClickTransfer(list, TransferIndex(list, 301, 0)); menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "暂停当前列表中的接收").PerformClick(); menu.Close();
                Check(records[0].Transfers[0].Status == "已暂停" && records[2].Transfers[0].Status == "已暂停", "暂停没有处理当前列表接收");
                foreach (int index in new[] { 1, 3, 4, 5, 6, 7 }) Check(records[index].Transfers[0].Status == states[index], "暂停越过当前列表或终态边界：" + names[index]);
                Check(store.IncomingAttachments(first.Id, 303).Single().Transfers[0].Status == "已暂停", "暂停当前列表没有持久化状态");
                window.Close(); main.ExitApplication();
            }
        }
        private static void TransferMessageLocationRegression()
        {
            Theme.Configure(false, "blue"); var store = new LocalStore(Path.Combine(root, "transfer-location"));
            var settings = new AppSettings { CloseToTray = false, Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(store.Root, "received"), Conversations = new[] {
                new SavedConversation { DeviceId = new string('8', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "location-a", Nickname = "定位设备 A" },
                new SavedConversation { DeviceId = new string('9', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "location-b", Nickname = "定位设备 B" } } };
            using (var network = new LanService(settings, FreePort(), true, "transfer-location"))
            using (var main = new MainForm(network, store))
            {
                Peer peer = network.Peers.Single(value => value.Login == "location-a"), other = network.Peers.Single(value => value.Login == "location-b");
                var incoming = new ChatRecord { PeerId = peer.Id, Packet = 17, Sender = peer.DisplayName, Text = "很旧的入站文件", Time = DateTime.Now.AddDays(-2), HasAttachments = true, Transfers = new[] { new FileTransferState { Id = 0, Name = "incoming-old.bin", Status = "已暂停" } } };
                var outgoing = new ChatRecord { PeerId = peer.Id, Packet = 17, Sender = "自己", Text = "相同编号的出站文件", Time = incoming.Time.AddMinutes(1), Outgoing = true, HasAttachments = true, Transfers = new[] { new FileTransferState { Id = 0, Name = "outgoing-old.bin", Status = "等待对方接收" } } };
                store.Append(incoming); store.Append(outgoing);
                for (int i = 0; i < 620; i++) store.Append(new ChatRecord { PeerId = peer.Id, Packet = 1000 + i, Sender = peer.DisplayName, Time = DateTime.Now.AddMinutes(-620 + i), Text = "历史上下文 " + i + " " + new string('文', 300), State = "已收到" });
                Check(store.LargeHistory(peer.Id), "定位夹具没有触发大历史异步读取");
                main.TrackTransfer(incoming); main.TrackTransfer(outgoing); main.Show();
                Rectangle workArea = Screen.FromControl(main).WorkingArea;
                main.Bounds = new Rectangle(workArea.Left + 20, workArea.Top + 20, Math.Min(1000, workArea.Width - 40), Math.Min(700, workArea.Height - 40));
                main.OpenChat(peer);
                ChatForm chat = main.ActiveChat; ChatEditor editor = Field<ChatEditor>(chat, "editor"); editor.Text = "定位时保留草稿";
                main.OpenChat(other); main.OpenTransfers(peer.Id);
                TransferWindow window = Field<TransferWindow>(main, "transferWindow"); PumpFor(80);
                ListBox list = Field<ListBox>(window, "list");
                int incomingIndex = list.Items.Cast<object>().Select((item, index) => new { Item = item, Index = index }).Single(entry => !Field<ChatRecord>(entry.Item, "Record").Outgoing).Index;
                RightClickTransfer(list, incomingIndex); ContextMenuStrip menu = Field<ContextMenuStrip>(window, "menu");
                menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "查看所在会话").PerformClick(); menu.Close();
                BubbleHistory history = Field<BubbleHistory>(chat, "history");
                PropertyInfo selectedRecord = typeof(BubbleHistory).GetProperty("SelectedRecord", BindingFlags.Instance | BindingFlags.NonPublic);
                Func<ChatRecord> located = () => (ChatRecord)selectedRecord.GetValue(history, null);
                Pump(() => main.ActiveChat == chat && located() != null && !located().Outgoing && located().Packet == 17, "查看所在会话没有定位到旧入站文件消息");
                int selected = Field<int>(history, "selected"); Rectangle bounds = Field<List<Rectangle>>(history, "bounds")[selected]; bounds.Offset(history.AutoScrollPosition);
                Check(bounds.IntersectsWith(history.ClientRectangle) && Field<Button>(chat, "returnToLatest").Visible && editor.Text == "定位时保留草稿", "定位目标不可见、缺少回到最新入口或改写草稿");
                SaveSnapshot(main, "transfer-location-old-light");
                list.SelectedIndex = list.Items.Cast<object>().Select((item, index) => new { Item = item, Index = index }).Single(entry => Field<ChatRecord>(entry.Item, "Record").Outgoing).Index;
                Check(Field<Button>(window, "action").Text == "查看会话", "出站主按钮文案契约改变");
                Field<Button>(window, "action").PerformClick();
                Pump(() => located() != null && located().Outgoing && located().Packet == 17, "出站查看会话没有定位正确收发身份");
                Task pending = chat.LocateMessageAsync(incoming); main.OpenChat(other);
                Pump(() => pending.IsCompleted, "旧定位后台读取没有结束"); pending.GetAwaiter().GetResult();
                Check(main.ActiveChat != chat && located().Outgoing && !Application.OpenForms.OfType<NoticeDialog>().Any(), "切换会话后旧定位回调改变了界面或弹出提示");

                store.DeleteRecords(peer.Id, new[] { outgoing }); main.OpenChat(other);
                bool noticed = false; string feedback = null;
                using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
                {
                    timer.Tick += delegate {
                        NoticeDialog notice = Application.OpenForms.OfType<NoticeDialog>().FirstOrDefault(); if (notice == null) return;
                        feedback = notice.Text + "\n" + String.Join(" ", Controls(notice).OfType<TextBoxBase>().Select(box => box.Text)); noticed = true;
                        Controls(notice).OfType<Button>().Single(button => button.Text == "确定").PerformClick();
                    };
                    timer.Start(); RightClickTransfer(list, list.SelectedIndex);
                    menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "查看所在会话").PerformClick(); menu.Close();
                    Pump(() => noticed, "文件消息已删除时没有显示定位失败提示");
                }
                Check(feedback == "无法定位\n这条消息已删除。" && main.ActiveChat == chat && editor.Text == "定位时保留草稿", "定位失败提示改变、关闭了会话或丢失草稿：" + feedback);
                window.Close(); main.ExitApplication();
            }
        }
        private static void TransferSourceLayoutRegression()
        {
            foreach (bool dark in new[] { false, true })
            {
                Theme.Configure(dark, "blue"); string theme = dark ? "dark" : "light";
                var store = new LocalStore(Path.Combine(root, "source-layout-" + theme));
                string portraitPath = Path.Combine(store.Root, "自定义头像.png");
                using (var portrait = new Bitmap(64, 64)) { using (Graphics graphics = Graphics.FromImage(portrait)) graphics.Clear(Color.Coral); portrait.Save(portraitPath); }
                var settings = new AppSettings { CloseToTray = false, Notifications = false, ReceiveFolder = Path.Combine(store.Root, "received"), Conversations = new[] {
                    new SavedConversation { DeviceId = new string('a', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "layout-a", Nickname = "自定义头像 中文 English 超长联系人名称", Avatar = EmojiAvatars.FromLocalImage(portraitPath) },
                    new SavedConversation { DeviceId = new string('b', 32), Endpoint = "127.0.0.1:" + FreePort(), Login = "layout-b", Nickname = "内置头像", Avatar = "builtin:4" } } };
                using (var network = new LanService(settings, FreePort(), true, "source-layout"))
                using (var main = new MainForm(network, store))
                using (var window = new TransferWindow(main))
                {
                    Peer first = network.Peers.Single(peer => peer.Login == "layout-a"), second = network.Peers.Single(peer => peer.Login == "layout-b");
                    first.Note = new string('长', 64);
                    DateTime recordTime = new DateTime(DateTime.Now.Year - 1, 11, 20, 12, 0, 0);
                    for (int i = 0; i < 20; i++) main.TrackTransfer(new ChatRecord { PeerId = i == 0 ? first.Id : i == 1 ? second.Id : i == 2 ? "browser-guests" : "layout:" + i, Packet = i, Sender = i == 2 ? "浏览器访客" : "历史设备 " + i + " English", Time = recordTime.AddMinutes(-i), Transfers = new[] { new FileTransferState { Id = 0, Name = "资料 中文 English 项目交付记录 " + i + ".zip", Size = 1048576, Status = i == 0 ? "接收中" : "已暂停", Percent = i == 0 ? 30 : 0 } } });
                    main.Show(); window.Show(); PumpFor(80); TransferSourceStrip strip = Field<TransferSourceStrip>(window, "sources"); ListBox list = Field<ListBox>(window, "list");
                    Check(strip.Controls.Count == 0 && (bool)typeof(Control).GetMethod("GetStyle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { ControlStyles.OptimizedDoubleBuffer }), "来源横排新增了子句柄或没有双缓冲");
                    foreach (Size size in new[] { new Size(860, 640), new Size(620, 440) })
                    {
                        window.ClientSize = size; Application.DoEvents(); window.SelectSource("");
                        Check(strip.AccessibilityObject.GetChildCount() == strip.Items.Count && strip.RectangleToClient(strip.AccessibilityObject.GetChild(0).Bounds).Left == 0, "来源横排仍为翻页箭头保留子项或两侧空间");
                        Check(Field<int>(strip, "offset") == 0, "选中全部后没有滚动到第一项");
                        int oldOffset = Field<int>(strip, "offset");
                        typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120) });
                        Check(Field<int>(strip, "offset") > oldOffset && window.SelectedSourceId == "", "鼠标滚轮没有横向滚动或改变了选中来源");
                        window.SelectSource(""); Check(Field<int>(strip, "offset") == 0, "重入同一来源没有把已选中项滚回可见范围");
                        typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, -120) });
                        oldOffset = Field<int>(strip, "offset"); int dragX = strip.Width / 2;
                        StripMouse(strip, "OnMouseDown", MouseButtons.Left, dragX, 20); StripMouse(strip, "OnMouseMove", MouseButtons.Left, dragX - 150, 20);
                        Check(Field<int>(strip, "offset") == oldOffset + 150, "按住向左拖动没有按位移横向滚动");
                        StripMouse(strip, "OnMouseMove", MouseButtons.Left, dragX - 50, 20); StripMouse(strip, "OnMouseUp", MouseButtons.Left, dragX - 50, 20);
                        Check(Field<int>(strip, "offset") == oldOffset + 50 && window.SelectedSourceId == "", "向右拖回没有跟随位移或拖动松开误选了来源");
                        StripMouse(strip, "OnMouseMove", MouseButtons.None, dragX - 200, 20);
                        Check(Field<int>(strip, "offset") == oldOffset + 50, "松开后的鼠标移动仍在滚动来源");
                        var endKey = new KeyEventArgs(Keys.End); typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { endKey });
                        Check(endKey.Handled && strip.SelectedId == strip.Items.Last().Id && strip.AccessibilityObject.GetSelected().Bounds.Width >= 64, "键盘末项选择没有自动滚动到完整可见");
                        string last = strip.SelectedId; var leftKey = new KeyEventArgs(Keys.Left); typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { leftKey });
                        Check(leftKey.Handled && strip.SelectedId != last, "键盘左右键没有切换来源");
                        var rightKey = new KeyEventArgs(Keys.Right); typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(strip, new object[] { rightKey });
                        Check(rightKey.Handled && strip.SelectedId == last, "键盘右键没有切回下一来源");
                        window.SelectSource(""); SaveSnapshot(window, "transfer-sources-" + theme + "-" + size.Width + "-collapsed");
                        AccessibleObject avatar = strip.AccessibilityObject.GetChild(1); Point click = strip.PointToClient(new Point(avatar.Bounds.Left + avatar.Bounds.Width / 2, avatar.Bounds.Top + 20));
                        StripMouse(strip, "OnMouseDown", MouseButtons.Left, click.X, click.Y);
                        Check(window.SelectedSourceId == "", "按下尚未松开就切换了来源");
                        StripMouse(strip, "OnMouseMove", MouseButtons.Left, click.X + 1, click.Y); StripMouse(strip, "OnMouseUp", MouseButtons.Left, click.X + 1, click.Y);
                        Check(window.SelectedSourceId == first.Id && list.Items.Count == 1 && (strip.AccessibilityObject.GetSelected().State & AccessibleStates.Selected) != 0, "来源点按、单选状态或列表切换错误");
                        using (Graphics graphics = list.CreateGraphics()) Check(TextRenderer.MeasureText(graphics, first.DisplayName, Theme.Small).Width > list.Width, "长联系人夹具不足以覆盖行内时间被挤掉的边界");
                        Check(Field<string>(list.Items[0], "MetaSuffix").EndsWith(TransferWindow.FormatTime(recordTime, DateTime.Now)), "行内时间尾部没有保留完整往年日期");
                        Field<Button>(window, "searchButton").PerformClick(); Field<TextBox>(window, "query").Text = "资料"; Application.DoEvents();
                        var body = Field<BufferedLayoutTable>(window, "body");
                        foreach (Control control in body.Controls) Check(body.ClientRectangle.Contains(control.Bounds), "来源或搜索新增区域越界：" + control.GetType().Name);
                        Control[] regions = body.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
                        for (int i = 0; i < regions.Length; i++) for (int j = i + 1; j < regions.Length; j++) Check(!regions[i].Bounds.IntersectsWith(regions[j].Bounds), "最小窗口区域重叠");
                        Check(list.Height >= list.ItemHeight && Field<TextBox>(window, "query").Bounds.Height > 0, "展开搜索后列表或输入框不可用：窗口=" + size + "，列表=" + list.Size + "，行高=" + list.ItemHeight);
                        foreach (Button button in Controls(window).OfType<Button>().Where(control => control.Visible))
                        {
                            if (!button.Parent.ClientRectangle.Contains(button.Bounds)) SaveSnapshot(window, "transfer-layout-boundary-" + theme + "-" + size.Width);
                            Check(button.Parent.ClientRectangle.Contains(button.Bounds), "按钮越界：" + button.Text + " / " + button.AccessibleName + "，按钮=" + button.Bounds + "，最小尺寸=" + button.MinimumSize + "，父控件=" + button.Parent.ClientRectangle + "，列表=" + list.Size + "，行高=" + list.ItemHeight);
                        }
                        using (var image = new Bitmap(strip.Width, strip.Height))
                        {
                            strip.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                            Rectangle selectedBounds = strip.RectangleToClient(strip.AccessibilityObject.GetSelected().Bounds);
                            int emphasis = 0; for (int x = Math.Max(0, selectedBounds.Left); x < Math.Min(selectedBounds.Right, image.Width); x++) for (int y = 0; y < Math.Min(64, image.Height); y++) if (image.GetPixel(x, y).ToArgb() == Theme.Blue.ToArgb()) emphasis++;
                            Check(emphasis > 60, "明暗主题选中头像没有清晰强调色描边");
                        }
                        SaveSnapshot(window, "transfer-sources-" + theme + "-" + size.Width + "-expanded");
                        using (var capture = new Bitmap(Path.Combine(output, "transfer-sources-" + theme + "-" + size.Width + "-expanded.png")))
                        {
                            Point origin = list.PointToScreen(Point.Empty); origin.Offset(-window.Left, -window.Top);
                            Rectangle row = list.GetItemRectangle(0); int ink = 0;
                            for (int x = origin.X + row.Left + row.Width * 2 / 3; x < origin.X + row.Right - 12; x++)
                                for (int y = origin.Y + row.Top + row.Height * 2 / 5; y < origin.Y + row.Top + row.Height * 3 / 5; y++)
                                    if (capture.GetPixel(x, y).ToArgb() == Theme.Muted.ToArgb()) ink++;
                            Check(ink > 30, "长名称行右侧没有绘制大小和时间文字");
                        }
                        Field<Button>(window, "searchButton").PerformClick();
                    }
                    window.SelectSource(first.Id); Field<Button>(window, "searchButton").PerformClick(); Field<TextBox>(window, "query").Text = "资料";
                    Theme.Change(!dark, "green");
                    Check(strip.BackColor.ToArgb() == Theme.Surface.ToArgb() && Field<TextBox>(window, "query").BackColor.ToArgb() == Theme.Ice.ToArgb() && Field<Button>(window, "searchButton").ForeColor.ToArgb() == Theme.Muted.ToArgb() && window.SelectedSourceId == first.Id && list.Items.Count == 1, "运行中主题切换漏了新控件或丢失筛选状态");
                    window.Close(); main.ExitApplication();
                }
            }
            Theme.Configure(false, "blue");
        }
        private static void TransferSourceRegressions()
        {
            Run("来源入口：会话、访客、主菜单初次与重复进入、无记录选中项", TransferSourceEntryRegression);
            Run("来源集合：最近时间、名称与ID排序、当前头像改名、无记录保留和切走移除", TransferSourceCollectionRegression);
            Run("来源筛选：来源、状态、文件名与来源名称搜索交集及当前列表汇总", TransferSourceFilterRegression);
            Run("搜索按钮：默认折叠、展开聚焦、再次点击、Esc、Ctrl+F与收起清空", TransferSearchRegression);
            Run("行内时间：三种本地格式、零时间省略、跨天仅刷新变化行", TransferTimestampRegression);
            Run("批量继续与暂停：只处理来源、状态和搜索交集中的接收", TransferBatchScopeRegression);
            Run("文件会话定位：旧历史异步读取、收发身份、已删除提示和旧回调隔离", TransferMessageLocationRegression);
            Run("来源横排渲染：明暗主题、860与620最小尺寸、头像、搜索展开与溢出滚动", TransferSourceLayoutRegression);
        }
    }
}
