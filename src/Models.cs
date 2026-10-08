using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    public sealed class AppSettings
    {
        public string Nickname { get; set; }
        public string Group { get; set; }
        public string Signature { get; set; }
        public bool CloseToTray { get; set; }
        public bool StartWithWindows { get; set; }
        public bool Notifications { get; set; }
        public bool DiagnosticsEnabled { get; set; }
        public bool DarkMode { get; set; }
        public string Accent { get; set; }
        public int ChatFontSize { get; set; }
        public string ChatBackground { get; set; }
        public string BackgroundImage { get; set; }
        public bool AutoReceiveFiles { get; set; }
        public string ReceiveFolder { get; set; }
        public WindowPlacement Window { get; set; }
        public string[] KnownPeers { get; set; }
        public SavedConversation[] Conversations { get; set; }
        public static string DefaultReceiveFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "飞Q接收文件"); } }
        public AppSettings()
        {
            Nickname = Environment.UserName; Group = "我的局域网";
            Signature = "在同一个局域网，简单聊一聊。";
            CloseToTray = true; Notifications = true; AutoReceiveFiles = true; ReceiveFolder = DefaultReceiveFolder;
            StartWithWindows = false; ChatFontSize=10; ChatBackground="orbits"; BackgroundImage="";
            KnownPeers = new string[0];
            Conversations = new SavedConversation[0];
        }
        public static IPEndPoint ParseKnownPeer(string value)
        {
            string[] parts = (value ?? "").Split(':'); IPAddress address; int port;
            if (parts.Length != 2 || parts[0].Split('.').Length != 4 || !IPAddress.TryParse(parts[0], out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || !Int32.TryParse(parts[1], out port) || port < 1024 || port > 65535) return null;
            return new IPEndPoint(address, port);
        }
        public static string[] NormalizeKnownPeers(IEnumerable<string> values)
        {
            return (values ?? new string[0]).Select(ParseKnownPeer).Where(p => p != null).Select(p => p.ToString()).Distinct().Take(2000).ToArray();
        }
    }
    public sealed class SavedConversation
    {
        public string Endpoint { get; set; }
        public string Login { get; set; }
        public string Host { get; set; }
        public string Nickname { get; set; }
        public string Group { get; set; }
        public string Preview { get; set; }
        public long MessageUtcTicks { get; set; }
        public int Unread { get; set; }
        public string Note { get; set; }
        public bool Pinned { get; set; }
        public bool Hidden { get; set; }
        [ScriptIgnore] public string Id { get { return Endpoint + "/" + Login; } }
        public Peer OfflinePeer()
        {
            // 历史资料不是当前在线或编码能力的证据；真正收到报文后再更新同一个 Peer。
            return new Peer { Endpoint=AppSettings.ParseKnownPeer(Endpoint),Login=Login,Host=Host,Nickname=Nickname,Group=Group,Online=false,Utf8=false,Note=Note,Pinned=Pinned,Hidden=Hidden };
        }
        public bool SameAs(SavedConversation other)
        {
            return Endpoint==other.Endpoint&&Login==other.Login&&Host==other.Host&&Nickname==other.Nickname&&Group==other.Group&&Preview==other.Preview&&MessageUtcTicks==other.MessageUtcTicks&&Unread==other.Unread&&Note==other.Note&&Pinned==other.Pinned&&Hidden==other.Hidden;
        }
        public static SavedConversation[] Normalize(IEnumerable<SavedConversation> values)
        {
            Dictionary<string,SavedConversation> result=new Dictionary<string,SavedConversation>();
            foreach(SavedConversation item in values??new SavedConversation[0])
            {
                if(item==null||item.Login==null||item.Login.Length>128||Protocol.Field(item.Login)!=item.Login)continue;
                IPEndPoint endpoint=AppSettings.ParseKnownPeer(item.Endpoint);if(endpoint==null)continue;
                SavedConversation clean=new SavedConversation { Endpoint=endpoint.ToString(),Login=item.Login,
                    Note=Protocol.Prefix((item.Note??"").Trim(),64),Pinned=item.Pinned,Hidden=item.Hidden,
                    Host=Protocol.Prefix(item.Host??"",128),Nickname=Protocol.Prefix(String.IsNullOrWhiteSpace(item.Nickname)?item.Login:item.Nickname,128),
                    Group=Protocol.Prefix(item.Group??"我的局域网",128),Preview=Protocol.Prefix((item.Preview??"").Replace('\0',' ').Replace('\r',' ').Replace('\n',' '),160),
                    MessageUtcTicks=item.MessageUtcTicks>0&&item.MessageUtcTicks<=DateTime.MaxValue.Ticks?item.MessageUtcTicks:0,Unread=Math.Max(0,Math.Min(1000000,item.Unread)) };
                result[clean.Id]=clean;if(result.Count>=2000)break;
            }
            return result.Values.OrderBy(c=>c.Id,StringComparer.Ordinal).ToArray();
        }
    }
    public sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }
    public static class ReceiveStorage
    {
        public static string Folder(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("请选择文件保存文件夹。");
            string path = Environment.ExpandEnvironmentVariables(value.Trim());
            // 拒绝驱动器相对路径和设备路径，避免保存位置依赖进程当前目录。
            if (!Path.IsPathRooted(path) || Path.GetPathRoot(path) == @"\" || Path.GetPathRoot(path) == "/" || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") || (path.Length > 1 && path[1] == ':' && (path.Length < 3 || (path[2] != '\\' && path[2] != '/')))) throw new ArgumentException("请输入完整的文件夹路径。");
            return Path.GetFullPath(path);
        }
        public static string Destination(string folder, string name)
        {
            if (!Protocol.SafeFileName(name)) throw new IOException("不安全的文件名。");
            string root = Folder(folder); Directory.CreateDirectory(root);
            return Path.Combine(root, name);
        }
        public static void ValidateWritable(string folder)
        {
            string path = Folder(folder); Directory.CreateDirectory(path);
            // 只创建并清理本次探测文件，不修改文件夹内已有内容。
            string probe = Path.Combine(path, ".feiq-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }
    public sealed class Peer
    {
        public string Id { get { return Endpoint + "/" + Login; } }
        public IPEndPoint Endpoint;
        public string Login;
        public string Host;
        public string Nickname;
        public string Group;
        public string Note;
        public bool Pinned, Hidden;
        public string DisplayName { get { return String.IsNullOrWhiteSpace(Note) ? Nickname : Note; } }
        public bool Utf8;
        public bool Online;
        public DateTime LastSeen;
        public override string ToString() { return Nickname; }
    }
    public sealed class ChatRecord
    {
        public long Packet { get; set; }
        public string PeerId { get; set; }
        public string Sender { get; set; }
        public string Text { get; set; }
        public DateTime Time { get; set; }
        public bool Outgoing { get; set; }
        public string State { get; set; }
        // 只由本机发送/接收流程写入，不从对方的消息正文推导可打开的路径。
        public string[] LocalFiles { get; set; }
        public bool HasAttachments { get; set; }
        public string[] AttachmentNames { get; set; }
        public string ForwardText()
        {
            string[] paths = GetLocalFiles();
            if (HasAttachments && (AttachmentNames == null || paths.Length != AttachmentNames.Length))
                throw new InvalidOperationException("请先接收全部附件，或从单个已保存记录转发文件。");
            if (paths.Any(p => !File.Exists(p))) throw new IOException("附件已移动或删除，请重新选择文件。");
            string text = Text ?? "";
            if (paths.Length > 0)
            {
                if (!Outgoing && Sender == "文件") return "";
                string suffix = "[文件] " + String.Join("、", AttachmentNames ?? paths.Select(Path.GetFileName).ToArray());
                if (text.EndsWith(suffix, StringComparison.Ordinal)) { text = text.Substring(0, text.Length - suffix.Length); if (text.EndsWith("\n", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1); }
            }
            if (String.IsNullOrWhiteSpace(text) && paths.Length == 0) throw new InvalidOperationException("此记录没有可转发内容。");
            return text;
        }
        public string[] GetLocalFiles()
        {
            if (LocalFiles != null) return LocalFiles.Where(p => !String.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // 兼容旧版由本机生成的接收完成记录；普通聊天中的路径不当作附件。
            if (!Outgoing && Sender == "文件" && State == "已保存" && (Text ?? "").StartsWith("已保存：")) return new[] { Text.Substring(4) };
            return new string[0];
        }
    }
    public sealed class Attachment
    {
        public int Id;
        public string Name;
        public long Size;
        public long Modified;
    }
    public sealed class IncomingMessage
    {
        public Peer Peer;
        public ChatRecord Record;
        public List<Attachment> Files = new List<Attachment>();
    }
    public sealed class LocalStore
    {
        private readonly object gate = new object();
        public readonly string Root;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength=16*1024*1024 };
        private readonly Dictionary<string, ChatRecord> activePending = new Dictionary<string, ChatRecord>();
        private readonly Dictionary<string, DateTime> clearedThrough = new Dictionary<string, DateTime>();
        private volatile bool preserveUnreadableSettings;
        public string Warning { get; private set; }
        public LocalStore(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); }
        public AppSettings LoadSettings()
        {
            string path = Path.Combine(Root, "settings.json");
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                if(new FileInfo(path).Length>16*1024*1024)throw new IOException("配置文件超过大小限制。");
                AppSettings s = json.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8));
                if (s == null || String.IsNullOrWhiteSpace(s.Nickname)) throw new FormatException();
                if (String.IsNullOrWhiteSpace(s.ReceiveFolder)) s.ReceiveFolder = AppSettings.DefaultReceiveFolder;
                s.KnownPeers = AppSettings.NormalizeKnownPeers(s.KnownPeers);
                s.Conversations = SavedConversation.Normalize(s.Conversations);
                s.ChatFontSize=Math.Max(9,Math.Min(18,s.ChatFontSize));
                if(s.ChatBackground!="none"&&s.ChatBackground!="lines"&&s.ChatBackground!="image")s.ChatBackground="orbits";
                return s;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is ArgumentException || e is InvalidOperationException || e is FormatException)) throw;
                preserveUnreadableSettings=true;
                Warning = "个人配置无法读取，已使用默认值；原文件未删除。";
                return new AppSettings();
            }
        }
        public void SaveSettings(AppSettings settings)
        {
            lock (gate)
            {
                string path = Path.Combine(Root, "settings.json"), tmp = path + ".tmp";
                File.WriteAllText(tmp, json.Serialize(settings), new UTF8Encoding(false));
                string backup=preserveUnreadableSettings&&File.Exists(path)?Path.Combine(Root,"settings-recovery-"+Guid.NewGuid().ToString("N").Substring(0,12)+".json"):null;
                // 自动保存窗口/会话也会写配置；读取失败后的第一次写回必须保留原字节，不能只在提示里说“未删除”。
                if (File.Exists(path)) File.Replace(tmp, path, backup); else File.Move(tmp, path);
                preserveUnreadableSettings=false;
                if(backup!=null)Warning="原配置已保留恢复备份："+Path.GetFileName(backup);
            }
        }
        private string HistoryPath(string peerId)
        {
            using (SHA256 sha = SHA256.Create())
            {
                string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(peerId))).Replace("-", "");
                return Path.Combine(Root, "history-" + key + ".jsonl");
            }
        }
        public string LoadDraft(string peerId)
        {
            lock (gate)
            {
                string path = Path.ChangeExtension(HistoryPath(peerId), ".draft.txt");
                if (!File.Exists(path)) return "";
                if (new FileInfo(path).Length > 144000) throw new IOException("草稿文件超过大小限制。");
                string text = File.ReadAllText(path, Encoding.UTF8); return text.Substring(0, Math.Min(text.Length, 18000));
            }
        }
        public void SaveDraft(string peerId, string text)
        {
            if ((text ?? "").Length > 18000) throw new ArgumentException("草稿超过长度限制。");
            lock (gate)
            {
                string path = Path.ChangeExtension(HistoryPath(peerId), ".draft.txt");
                if (String.IsNullOrEmpty(text)) { if (File.Exists(path)) File.Delete(path); return; }
                string temporary = path + ".tmp";
                try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        public void Append(ChatRecord record)
        {
            lock (gate)
            {
                DateTime cutoff;
                if(clearedThrough.TryGetValue(record.PeerId,out cutoff)&&record.Time<=cutoff)return;
                byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(record) + "\n");
                using (FileStream stream = new FileStream(HistoryPath(record.PeerId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    // 保留坏尾行原文，仅补记录分隔符；完整但缺换行的末条也能继续读取。
                    if (stream.Length > 0) { stream.Position = stream.Length - 1; int last = stream.ReadByte(); if (last != '\n') stream.WriteByte((byte)'\n'); }
                    stream.Position = stream.Length; stream.Write(bytes, 0, bytes.Length);
                }
                string key = record.PeerId + "/" + record.Packet;
                if (record.Outgoing)
                {
                    if (record.State == "等待确认") activePending[key] = record;
                    else activePending.Remove(key);
                }
            }
        }
        public void CompletePending()
        {
            lock (gate)
            {
                foreach (ChatRecord record in activePending.Values.ToList())
                {
                    if (record.State == "等待确认") record.State = "已退出，未确认送达";
                    Append(record);
                }
            }
        }
        public void ClearHistory(string peerId)
        {
            lock(gate)
            {
                string path=HistoryPath(peerId);
                if(File.Exists(path))File.Delete(path);
                // ACK 和退出收尾不应把刚清空的旧消息重新写回。
                clearedThrough[peerId]=DateTime.Now;
                foreach(string key in activePending.Where(p=>p.Value.PeerId==peerId).Select(p=>p.Key).ToArray())activePending.Remove(key);
            }
        }
        public List<ChatRecord> History(string peerId, int limit)
        {
            lock (gate)
            {
                if (limit < 1 || limit > 10000) throw new ArgumentOutOfRangeException("limit");
                LinkedList<ChatRecord> result = new LinkedList<ChatRecord>();
                Dictionary<string, LinkedListNode<ChatRecord>> index = new Dictionary<string, LinkedListNode<ChatRecord>>();
                string path = HistoryPath(peerId);
                if (!File.Exists(path)) return result.ToList();
                // 只扫描文件尾部，避免多年聊天记录拖慢打开窗口；原始历史不截断。
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    long offset = Math.Max(0, stream.Length - 4 * 1024 * 1024); stream.Position = offset;
                    if (offset > 0) reader.ReadLine();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            ChatRecord item = json.Deserialize<ChatRecord>(line);
                            if (item == null || item.PeerId != peerId || item.Text == null) continue;
                            // JavaScriptSerializer 读回的是 UTC；聊天显示统一本地时间，保留同一个时间点。
                            if(item.Time.Kind==DateTimeKind.Utc)item.Time=item.Time.ToLocalTime();
                            string key = item.Outgoing + ":" + item.Packet;
                            LinkedListNode<ChatRecord> old;
                            if (index.TryGetValue(key, out old)) old.Value = item;
                            else index[key] = result.AddLast(item);
                            if (result.Count > limit) { ChatRecord first = result.First.Value; index.Remove(first.Outgoing + ":" + first.Packet); result.RemoveFirst(); }
                        }
                        catch (ArgumentException) { /* 中断写入的一行不能使其余历史不可读。 */ }
                        catch (InvalidOperationException) { }
                    }
                }
                // 只恢复不属于当前进程的等待记录；仍在重试的消息不能被误判为失败。
                foreach (ChatRecord record in result)
                    if (record.Outgoing && record.State == "等待确认" && !activePending.ContainsKey(record.PeerId + "/" + record.Packet))
                    { record.State = "上次退出，未确认送达"; Append(record); }
                // 旧版本同时存了邀请和独立完成行。仅在原邀请已持有同一路径时隐藏冗余行；不删原始历史。
                HashSet<string> attached=new HashSet<string>(result.Where(r=>!r.Outgoing&&r.Sender!="文件").SelectMany(r=>r.GetLocalFiles()),StringComparer.OrdinalIgnoreCase);
                return result.Where(r=>r.Outgoing||r.Sender!="文件"||r.State!="已保存"||!(r.Text??"").StartsWith("已保存：",StringComparison.Ordinal)||r.GetLocalFiles().Length==0||!r.GetLocalFiles().All(attached.Contains)).ToList();
            }
        }
    }
}
