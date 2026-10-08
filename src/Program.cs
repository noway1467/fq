using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FeiqLight
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                int port = 2425; bool loopback = false, autoStart = false;
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeiqLight"), name = null, peerAddress = null;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--loopback") loopback = true;
                    else if (args[i] == "--autostart") autoStart = true;
                    else if (args[i] == "--port" && i + 1 < args.Length) port = Int32.Parse(args[++i]);
                    else if (args[i] == "--profile" && i + 1 < args.Length) root = args[++i];
                    else if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
                    else if (args[i] == "--peer" && i + 1 < args.Length) peerAddress = args[++i];
                    else throw new ArgumentException("未知参数：" + args[i]);
                }
                bool created;
                string profileKey;
                using (SHA256 sha = SHA256.Create()) profileKey = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).Replace("-", "");
                using (Mutex instance = new Mutex(true, "Local\\FeiqLight-Port-" + port, out created))
                {
                    if (!created) { if (!autoStart) Notice("已在运行", "此端口已被占用，请从托盘打开现有窗口。"); return; }
                    bool profileCreated;
                    using (Mutex profileLock = new Mutex(true, "Local\\FeiqLight-Profile-" + profileKey, out profileCreated))
                    {
                        if (!profileCreated) { if (!autoStart) Notice("数据目录已占用", "多实例请使用不同的 --profile 目录。"); return; }
                        LocalStore store = new LocalStore(root); AppSettings settings = store.LoadSettings();
                        Theme.Configure(settings.DarkMode,settings.Accent);
                        if (!String.IsNullOrWhiteSpace(name)) settings.Nickname = name;
                        string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                        WindowsStartup startup = loopback || String.IsNullOrWhiteSpace(startupFolder) ? null : new WindowsStartup(startupFolder, Application.ExecutablePath, store.Root, port, false);
                        using (LanService network = new LanService(settings, port, loopback, loopback ? "test-" + port : null))
                        using (MainForm main = new MainForm(network, store, startup, autoStart))
                        {
                            try
                            {
                                // 通信生命周期不依赖窗口 Shown；隐藏启动或托盘运行也必须在线。
                                main.PrepareNetworkCallbacks();
                                network.Start();
                                if (peerAddress != null) network.Probe(AddressDialog.Parse(peerAddress));
                            }
                            catch (Exception e)
                            {
                                new Diagnostics(store.Root,()=>settings.DiagnosticsEnabled).Record(DiagnosticEvent.NetworkError);
                                Notice("连接失败", e.Message + "\n\n请检查端口 " + port + " 是否被占用。");
                                return;
                            }
                            main.NetworkStarted();
                            main.Shown += delegate { main.NetworkStarted(); };
                            Application.Run(main);
                        }
                        profileLock.ReleaseMutex();
                    }
                    instance.ReleaseMutex();
                }
            }
            catch (Exception e) { Notice("启动失败", e.Message); }
        }
        private static void Notice(string title, string text) { using (NoticeDialog dialog = new NoticeDialog(title, text)) dialog.ShowDialog(); }
    }
}
