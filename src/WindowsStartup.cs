using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FeiqLight
{
    // 只管理当前用户、当前资料目录的启动快捷方式；测试传入项目内隔离目录。
    public sealed class WindowsStartup
    {
        private readonly string executable, arguments, marker;
        public string ShortcutPath { get; private set; }
        public WindowsStartup(string startupFolder, string executablePath, string profile, int port, bool loopback)
        {
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
            executable = Path.GetFullPath(executablePath); profile = Path.GetFullPath(profile);
            string key;
            using (SHA256 hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(profile.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).Replace("-", "").Substring(0, 12);
            marker = "FeiqLight startup / " + key;
            ShortcutPath = Path.Combine(Path.GetFullPath(startupFolder), "飞Q-" + key + ".lnk");
            arguments = "--autostart --port " + port.ToString(CultureInfo.InvariantCulture) + " --profile " + QuotePath(profile) + (loopback ? " --loopback" : "");
        }
        private static string QuotePath(string value)
        {
            // Windows 命令行中结束引号前的反斜杠必须加倍，包括盘符根目录。
            return "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
        }
        private static object Member(object target, string name, BindingFlags flags, params object[] args)
        {
            return target.GetType().InvokeMember(name, flags, null, target, args);
        }
        private void WithShortcut(string path, Action<object> action)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = Member(shell, "CreateShortcut", BindingFlags.InvokeMethod, path); action(link);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
        }
        private string Read(string name)
        {
            string value = null; WithShortcut(ShortcutPath, link => value = (string)Member(link, name, BindingFlags.GetProperty)); return value;
        }
        public bool IsEnabled
        {
            get { return File.Exists(ShortcutPath) && Read("Description") == marker && String.Equals(Read("TargetPath"), executable, StringComparison.OrdinalIgnoreCase) && Read("Arguments") == arguments; }
        }
        public void SetEnabled(bool enabled)
        {
            bool exists = File.Exists(ShortcutPath);
            if (exists && Read("Description") != marker) throw new IOException("同名启动项不属于飞Q，未覆盖或删除；请先检查启动文件夹。");
            if (!enabled) { if (exists) File.Delete(ShortcutPath); return; }
            if (!File.Exists(executable)) throw new IOException("当前程序文件已移动，请从新位置重新运行后开启自启动。");
            if (IsEnabled) return;
            string folder = Path.GetDirectoryName(ShortcutPath); Directory.CreateDirectory(folder);
            string temporary = Path.Combine(folder, "~fq-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".lnk");
            try
            {
                WithShortcut(temporary, link =>
                {
                    Member(link, "TargetPath", BindingFlags.SetProperty, executable);
                    Member(link, "Arguments", BindingFlags.SetProperty, arguments);
                    Member(link, "WorkingDirectory", BindingFlags.SetProperty, Path.GetDirectoryName(executable));
                    Member(link, "Description", BindingFlags.SetProperty, marker);
                    Member(link, "WindowStyle", BindingFlags.SetProperty, 1);
                    Member(link, "Save", BindingFlags.InvokeMethod);
                });
                // 先完成并释放 COM 文件句柄，再原子替换；失败不损坏已有启动项。
                if (exists) File.Replace(temporary, ShortcutPath, null); else File.Move(temporary, ShortcutPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void SaveSettings(LocalStore store, AppSettings previous, AppSettings next)
        {
            store.SaveSettings(next);
            try { SetEnabled(next.StartWithWindows); }
            catch (Exception error)
            {
                try { store.SaveSettings(previous); }
                catch (Exception rollback) { throw new IOException("启动项更新失败，且配置回滚失败：" + error.Message + "；" + rollback.Message, error); }
                throw new IOException("启动项更新失败，已保留原设置：" + error.Message, error);
            }
        }
    }
}
