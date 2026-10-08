using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    // 与正式程序共用界面和服务，只把网络与数据目录隔离，避免烟测触碰真实局域网。
    internal static class UiHarness
    {
        [STAThread]
        private static void Main()
        {
#if CLIENT_A
            const int port = 32425, other = 32426; const string name = "测试节点 A";
#else
            const int port = 32426, other = 32425; const string name = "测试节点 B";
#endif
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data", "FeiqLight-ui-" + Guid.NewGuid().ToString("N"));
            LocalStore store = new LocalStore(root);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-profile-" + port + ".txt"), root);
            AppSettings settings = new AppSettings { Nickname = name, Group = "界面验证", Signature = "仅回环地址 · 不连接局域网", Notifications = false, ReceiveFolder = Path.Combine(root, "received") };
            using (LanService service = new LanService(settings, port, true, "test-" + port))
            using (MainForm form = new MainForm(service, store))
            {
                form.Text += " · " + name;
                form.Shown += delegate { service.Start(); service.Probe(new IPEndPoint(IPAddress.Loopback, other)); form.NetworkStarted(); };
                using (Timer renderer = new Timer { Interval = 1000 })
                {
                    Dictionary<Form, string> rendered = new Dictionary<Form, string>();
                    renderer.Tick += delegate
                    {
                        foreach (Form window in Application.OpenForms.Cast<Form>().ToList())
                        {
                            if (!window.Visible || window.IsDisposed) continue;
                            string signature = Signature(window), old;
                            if (rendered.TryGetValue(window, out old) && signature == old) continue;
                            rendered[window] = signature;
                            using (Bitmap image = new Bitmap(window.Width, window.Height))
                            {
                                window.DrawToBitmap(image, new Rectangle(Point.Empty, window.Size));
                                image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-" + window.GetType().Name + "-" + port + ".png"), ImageFormat.Png);
                            }
                        }
                    };
                    renderer.Start(); Application.Run(form);
                }
            }
        }
        private static string Signature(Control control)
        {
            return control.Text + control.Size + control.Visible + String.Join("|", control.Controls.Cast<Control>().Select(Signature));
        }
    }
}
