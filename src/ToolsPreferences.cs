using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    internal sealed class ToolsPreferences
    {
        internal sealed class State
        {
            public string Folder { get; set; }
            public bool ShareEnabled { get; set; }
            public bool RemoteEnabled { get; set; }
        }
        private readonly string path;
        internal State Value { get; private set; }
        internal ToolsPreferences(string root)
        {
            Value = new State();
            if (root == null) return;
            path = Path.Combine(root, "tools-settings.json");
            if (!File.Exists(path)) return;
            try { Value = new JavaScriptSerializer().Deserialize<State>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (ArgumentException) { throw new IOException("共享与远控设置损坏，未恢复服务"); }
            if (Value == null || (Value.ShareEnabled && String.IsNullOrEmpty(Value.Folder))) throw new IOException("共享与远控设置格式无效");
        }
        internal void Save(string folder, bool shareEnabled, bool remoteEnabled)
        {
            State next = new State { Folder = folder, ShareEnabled = shareEnabled, RemoteEnabled = remoteEnabled };
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
