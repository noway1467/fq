using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    internal sealed class ToolsPreferences
    {
        internal sealed class State
        {
            public string Folder { get; set; }
            public string[] Folders { get; set; }
            public bool ShareEnabled { get; set; }
            public bool RemoteEnabled { get; set; }
        }
        private readonly string path;
        internal State Value { get; private set; }
        internal ToolsPreferences(string root)
        {
            Value = new State { Folders = new string[0] };
            if (root == null) return;
            path = Path.Combine(root, "tools-settings.json");
            if (!File.Exists(path)) return;
            try { Value = new JavaScriptSerializer().Deserialize<State>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (ArgumentException) { throw new IOException("共享与远控设置损坏，未恢复服务"); }
            if (Value == null) throw new IOException("共享与远控设置格式无效");
            // 旧版只保存 Folder；读取时迁移，不在启动时覆盖原配置。
            if (Value.Folders == null) Value.Folders = String.IsNullOrEmpty(Value.Folder) ? new string[0] : new[] { Value.Folder };
            if (Value.Folders.Any(String.IsNullOrWhiteSpace) || Value.Folders.Length > 20 || (Value.ShareEnabled && Value.Folders.Length == 0)) throw new IOException("共享与远控设置格式无效");
            Value.Folder = Value.Folders.FirstOrDefault();
        }
        internal void Save(string folder, bool shareEnabled, bool remoteEnabled)
        {
            SaveFolders(String.IsNullOrEmpty(folder) ? new string[0] : new[] { folder }, shareEnabled, remoteEnabled);
        }
        internal void SaveFolders(string[] folders, bool shareEnabled, bool remoteEnabled)
        {
            State next = new State { Folder = folders.FirstOrDefault(), Folders = folders.ToArray(), ShareEnabled = shareEnabled, RemoteEnabled = remoteEnabled };
            if (path != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(next), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            Value = next;
        }
    }
}
