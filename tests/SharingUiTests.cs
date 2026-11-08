using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class SharingUiTests
    {
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        private static string output;
        private static int passed, failed;
        private static IEnumerable<Control> All(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in All(child)) yield return nested; } }
        private static Button Button(Control parent, string text) { return All(parent).OfType<Button>().Single(b => b.Text == text); }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); } }
        private static void Layout(Form form)
        {
            Application.DoEvents();
            foreach (Button button in All(form).OfType<Button>().Where(b => b.Visible)) Check(button.Parent.ClientRectangle.Contains(button.Bounds), "按钮越界：" + button.Text + " " + button.Bounds);
            foreach (ScrollableControl scroll in All(form).OfType<ScrollableControl>().Where(c => c.Visible && c.AutoScroll)) Check(!scroll.HorizontalScroll.Visible, "出现多余的横向滚动条");
        }
        private static void Snapshot(Form form, string name)
        {
            form.Refresh(); Application.DoEvents(); Thread.Sleep(100);
            // 原生窗口渲染只读取本测试窗口，不依赖抢占前台，也不截取被遮挡的其他程序。
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap)) { IntPtr dc = g.GetHdc(); try { Check(PrintWindow(form.Handle, dc, 2), "原生窗口渲染失败"); } finally { g.ReleaseHdc(dc); } }
                bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
            File.WriteAllLines(Path.Combine(output, name + "-layout.txt"), All(form).Where(c => c.Visible).Select(c => c.GetType().Name + " " + c.AccessibleName + " " + c.Bounds + " parent=" + c.Parent.ClientSize));
        }
        [STAThread] private static int Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output); string data = Path.GetFullPath(args[1]); Directory.CreateDirectory(data);
            if (args.Length > 2 && args[2] == "preview") { Preview(data); return 0; }
            foreach (bool dark in new[] { false, true })
            {
                Theme.Configure(dark, "blue"); string theme = dark ? "dark" : "light";
                Run("共享窗口：" + theme + " 空列表、多个长路径、最小/常用/最大化及操作", () => {
                    string profile = Path.Combine(data, theme, "profile");
                    using (ToolsWindow window = new ToolsWindow(-1, true, profile))
                    {
                        window.Show(); Layout(window); Check(!Button(window, "开启共享").Enabled && !Button(window, "移除所选").Enabled, "空状态按钮未禁用"); Snapshot(window, "sharing-empty-" + theme);
                        ToolsService service = (ToolsService)typeof(ToolsWindow).GetField("service", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
                        string[] paths = new[] { Path.Combine(data, theme, "共享资料"), Path.Combine(data, theme, "Design assets - English"), Path.Combine(data, theme, "这是用于验证长目录名称不会遮挡操作按钮的共享资料目录") };
                        foreach (string path in paths) Directory.CreateDirectory(path); service.AddFolders(paths); typeof(ToolsWindow).GetMethod("UpdateState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
                        ListBox list = All(window).OfType<ListBox>().Single(); Check(list.Items.Count == 3 && Button(window, "复制连接码").Enabled, "列表或连接码未更新");
                        Layout(window); Snapshot(window, "sharing-" + theme);
                        window.Size = window.MinimumSize; Layout(window); Check(list.Height >= list.ItemHeight, "最小尺寸连一行目录都显示不全"); Snapshot(window, "sharing-minimum-" + theme);
                        window.WindowState = FormWindowState.Maximized; Layout(window); Snapshot(window, "sharing-maximized-" + theme); window.WindowState = FormWindowState.Normal;
                        Button(window, "停止共享").PerformClick(); Check(service.Folder == null, "停止无效"); list.SelectedIndex = 1; Button(window, "移除所选").PerformClick(); Check(service.SavedFolders.Length == 2 && service.Folder == null && Directory.Exists(paths[1]), "移除重开共享或删除目录");
                        Button(window, "开启共享").PerformClick(); Check(service.Folders.Length == 2, "重新开启丢目录"); window.Close();
                    }
                });
                Run("常用弹窗：" + theme + " 错误校验、长正文滚动、确认/取消和主题", () => {
                    using (AddressDialog address = new AddressDialog(2425))
                    {
                        address.Show(); Button(address, "添加").PerformClick(); Layout(address); Check(address.DialogResult != DialogResult.OK && All(address).OfType<Label>().Any(l => l.Text.Contains("有效 IPv4")), "无效输入未提示"); Snapshot(address, "dialog-address-" + theme); Button(address, "取消").PerformClick();
                    }
                    Peer peer = new Peer { Nickname = "共享文件测试", Endpoint = new IPEndPoint(IPAddress.Loopback, 2425), Login = "test" };
                    using (ConversationDialog note = new ConversationDialog(peer, false)) { note.Show(); Layout(note); All(note).OfType<TextBox>().Single().Text = "测试备注"; Snapshot(note, "dialog-note-" + theme); Button(note, "保存").PerformClick(); Check(note.Note == "测试备注", "备注未保留"); }
                    using (ConversationDialog remove = new ConversationDialog(peer, true)) { remove.Show(); Layout(remove); Snapshot(remove, "dialog-remove-" + theme); Button(remove, "取消").PerformClick(); }
                    using (NoticeDialog notice = new NoticeDialog("无法共享文件夹", "文件夹可能已被移动，或当前账号没有读取权限。\n请选择仍可访问的目录后重试。")) { notice.Show(); Layout(notice); Snapshot(notice, "dialog-notice-" + theme); Button(notice, "确定").PerformClick(); }
                    string text = String.Concat(Enumerable.Repeat("中文错误详情 / Very long path with details 👩🏽‍💻\r\n", 80));
                    using (NoticeDialog notice = new NoticeDialog("完整错误详情", text))
                    {
                        notice.Show(); Layout(notice); RichTextBox details = All(notice).OfType<RichTextBox>().Single(); Check(details.ReadOnly && details.Text.Replace("\r", "") == text.Replace("\r", ""), "正文丢失或可编辑"); details.Select(details.TextLength, 0); details.ScrollToCaret(); Check(details.GetPositionFromCharIndex(details.TextLength - 1).Y < details.Height, "长正文不能滚到最后"); Snapshot(notice, "dialog-long-" + theme); notice.Close();
                    }
                    using (ConfirmDialog confirm = new ConfirmDialog("是否允许远程控制？", "127.0.0.1 请求查看屏幕并操作此电脑。\n可随时按 Ctrl+Alt+F12 停止。", "允许本次控制", false, "始终允许此设备"))
                    {
                        confirm.Show(); Layout(confirm); Check(!confirm.OptionChecked, "默认信任对方"); Snapshot(confirm, "dialog-confirm-" + theme); All(confirm).OfType<ToggleSwitch>().Single().Checked = true; Check(confirm.OptionChecked, "选项未生效"); Button(confirm, "取消").PerformClick(); Check(confirm.DialogResult == DialogResult.Cancel, "取消失效");
                    }
                });
                Run("聊天输入：" + theme + " 提示/正文同位置、字号变化、换行和长文滚动", () => {
                    AppSettings settings = new AppSettings { CloseToTray = false, Notifications = false, DarkMode = dark };
                    using (LanService network = new LanService(settings, 2425, true, "ui-polish"))
                    using (MainForm main = new MainForm(network, new LocalStore(Path.Combine(data, theme, "chat"))))
                    using (ChatForm chat = new ChatForm(new Peer { Nickname = "输入布局测试", Endpoint = new IPEndPoint(IPAddress.Loopback, 2425), Login = "test", Online = true }, network, new LocalStore(Path.Combine(data, theme, "chat")), main))
                    {
                        chat.Show(); chat.ClientSize = new Size(900, 560); Application.DoEvents(); ChatEditor input = All(chat).OfType<ChatEditor>().Single();
                        foreach (int size in new[] { 9, 14, 18 })
                        {
                            settings.ChatFontSize = size; chat.ApplyAppearance(); input.Clear(); Application.DoEvents(); Point hint = input.GetPositionFromCharIndex(0);
                            Check(Math.Abs(input.Top + hint.Y + input.Font.Height / 2 - input.Parent.Height / 2) <= 4, "空提示未垂直居中，字号 " + size + " origin=" + hint + " font=" + input.Font.Height + " editor=" + input.Bounds + " shell=" + input.Parent.Size);
                            Snapshot(chat, "composer-" + size + "-" + theme); input.Text = "输入消息 / Hello"; Application.DoEvents(); Check(Math.Abs(input.GetPositionFromCharIndex(0).Y - hint.Y) <= 1, "输入前后跳位");
                        }
                        input.Text = String.Join("\n", Enumerable.Range(1, 40).Select(i => "第 " + i + " 行 / multiline")); Application.DoEvents(); input.Select(input.TextLength, 0); input.ScrollToCaret(); Point end = input.GetPositionFromCharIndex(input.TextLength - 1); Check(end.Y >= 0 && end.Y < input.Height, "多行文本无法滚动到末尾"); input.Clear();
                    }
                });
            }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
        private static void Preview(string data)
        {
            string[] paths = new[] { Path.Combine(data, "共享资料"), Path.Combine(data, "Design assets") };
            foreach (string path in paths) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "阅读说明.txt"), "只用于多目录浏览兼容性验证。", new System.Text.UTF8Encoding(false)); }
            Directory.CreateDirectory(Path.Combine(data, "待添加 A")); Directory.CreateDirectory(Path.Combine(data, "待添加 B"));
            new ToolsPreferences(Path.Combine(data, "profile")).SaveFolders(paths, true, false);
            using (ToolsWindow window = new ToolsWindow(-1, true, Path.Combine(data, "profile")))
            using (System.Windows.Forms.Timer exit = new System.Windows.Forms.Timer { Interval = 600000 })
            {
                ToolsService service = (ToolsService)typeof(ToolsWindow).GetField("service", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
                File.WriteAllText(Path.Combine(output, "preview-server.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { port = service.Port, share = service.ShareCode }), new System.Text.UTF8Encoding(false));
                exit.Tick += delegate { window.Close(); }; exit.Start(); Application.Run(window);
            }
        }
    }
}
