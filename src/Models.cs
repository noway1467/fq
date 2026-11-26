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
        public string DeviceId { get; set; }
        public string Avatar { get; set; }
        public string Nickname { get; set; }
        public string Group { get; set; }
        public string Signature { get; set; }
        public bool CloseToTray { get; set; }
        public bool StartWithWindows { get; set; }
        public bool Notifications { get; set; }
        public bool DiagnosticsEnabled { get; set; }
        public bool DarkMode { get; set; }
        public bool FollowSystemTheme { get; set; }
        public string Accent { get; set; }
        public int ChatFontSize { get; set; }
        public string ChatBackground { get; set; }
        public string BackgroundImage { get; set; }
        public bool AutoReceiveFiles { get; set; }
        public bool AutoSendDroppedFiles { get; set; }
        public bool AllowLegacyProtocol { get; set; }
        public int SettingsRevision { get; set; }
        public string ReceiveFolder { get; set; }
        public WindowPlacement Window { get; set; }
        public WindowPlacement TransferWindow { get; set; }
        public WindowPlacement GuestWindow { get; set; }
        public int SidebarWidth { get; set; }
        public string[] KnownPeers { get; set; }
        public SavedConversation[] Conversations { get; set; }
        public static string DefaultReceiveFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "飞Q接收文件"); } }
        public AppSettings()
        {
            DeviceId = Guid.NewGuid().ToString("N");
            Avatar = "default";
            Nickname = Environment.UserName; Group = "我的局域网";
            Signature = "在同一个局域网，简单聊一聊。";
            CloseToTray = true; Notifications = true; AutoReceiveFiles = true; ReceiveFolder = DefaultReceiveFolder;
            StartWithWindows = false; AutoSendDroppedFiles = false; AllowLegacyProtocol = true; ChatFontSize=10; ChatBackground="orbits"; BackgroundImage="";
            KnownPeers = new string[0];
            Conversations = new SavedConversation[0];
            SidebarWidth = 336;
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
        public string DeviceId { get; set; }
        public string Avatar { get; set; }
        public string Fingerprint { get; set; }
        public string ConversationId { get; set; }
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
        public int ConversationNumber { get; set; }
        [ScriptIgnore] public string Id { get { return ConversationId ?? (DeviceId == null ? Endpoint + "/" + Login : "device:" + DeviceId); } }
        public Peer OfflinePeer()
        {
            // 历史资料不是当前在线或编码能力的证据；真正收到报文后再更新同一个 Peer。
            return new Peer { ConversationId=Id,DeviceId=DeviceId,Avatar=Avatar,Fingerprint=Fingerprint,Endpoint=AppSettings.ParseKnownPeer(Endpoint),Login=Login,Host=Host,Nickname=Nickname,Group=Group,Online=false,Utf8=false,Note=Note,Pinned=Pinned,Hidden=Hidden,ConversationNumber=ConversationNumber };
        }
        public bool SameAs(SavedConversation other)
        {
            return Fingerprint==other.Fingerprint&&DeviceId==other.DeviceId&&Avatar==other.Avatar&&Id==other.Id&&Endpoint==other.Endpoint&&Login==other.Login&&Host==other.Host&&Nickname==other.Nickname&&Group==other.Group&&Preview==other.Preview&&MessageUtcTicks==other.MessageUtcTicks&&Unread==other.Unread&&Note==other.Note&&Pinned==other.Pinned&&Hidden==other.Hidden&&ConversationNumber==other.ConversationNumber;
        }
        public static SavedConversation[] Normalize(IEnumerable<SavedConversation> values)
        {
            Dictionary<string,SavedConversation> result=new Dictionary<string,SavedConversation>();
            foreach(SavedConversation item in values??new SavedConversation[0])
            {
                if(item==null||item.Login==null||item.Login.Length>128||Protocol.Field(item.Login)!=item.Login)continue;
                IPEndPoint endpoint=AppSettings.ParseKnownPeer(item.Endpoint);if(endpoint==null)continue;
                SavedConversation clean=new SavedConversation { Endpoint=endpoint.ToString(),Login=item.Login,
                    DeviceId=Protocol.NormalizeDeviceId(item.DeviceId),Avatar=Protocol.NormalizeAvatar(item.Avatar),Fingerprint=SecureIdentity.ValidFingerprint(item.Fingerprint)?item.Fingerprint:null,ConversationId=String.IsNullOrEmpty(item.ConversationId)?null:Protocol.Prefix(item.ConversationId,256),
                    Note=Protocol.Prefix((item.Note??"").Trim(),64),Pinned=item.Pinned,Hidden=item.Hidden,ConversationNumber=item.ConversationNumber>0&&item.ConversationNumber<=1000000?item.ConversationNumber:0,
                    Host=Protocol.Prefix(item.Host??"",128),Nickname=Protocol.Prefix(String.IsNullOrWhiteSpace(item.Nickname)?item.Login:item.Nickname,128),
                    Group=Protocol.Prefix(item.Group??"我的局域网",128),Preview=Protocol.Prefix(MessageText.NormalizeLineBreaks(item.Preview).Replace('\0',' ').Replace('\n',' '),160),
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
        internal static bool LinkedDirectory(string directory)
        {
            for (string current = directory; current != null; current = Path.GetDirectoryName(current))
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        public static void DeletePartialAndEmptyParents(string receiveRoot, string partPath, string relativePath)
        {
            string root = Folder(receiveRoot);
            if (root.Length > Path.GetPathRoot(root).Length) root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string part = Path.GetFullPath(partPath);
            if (!part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                relativePath != null && !Protocol.SafeRelativePath(relativePath) || LinkedDirectory(Path.GetDirectoryName(part)))
                throw new IOException("断点路径不在接收目录内或包含链接，未清理。");
            if (File.Exists(part))
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("断点文件不能是链接，未清理。");
                File.Delete(part);
            }
            if (relativePath == null) return;
            string directory = Path.GetDirectoryName(part);
            while (!String.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            {
                if (directory == null || !directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    if (!Directory.Exists(directory)) { directory = Path.GetDirectoryName(directory); continue; }
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(directory).Any()) return;
                    Directory.Delete(directory);
                    directory = Path.GetDirectoryName(directory);
                }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
            }
        }
        public static int CleanExpiredPartials(string receiveRoot, DateTime utcNow)
        {
            string root = Folder(receiveRoot);
            if (!Directory.Exists(root) || LinkedDirectory(root)) return 0;
            int removed = 0;
            Stack<string> folders = new Stack<string>(); folders.Push(root);
            while (folders.Count > 0)
            {
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(folders.Pop()); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string path in entries)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0) { folders.Push(path); continue; }
                        string name = Path.GetFileName(path);
                        if (name.Length != 75 || !name.StartsWith(".feiq-", StringComparison.Ordinal) || !name.EndsWith(".part", StringComparison.Ordinal) ||
                            name.Substring(6, 64).Any(c => !Uri.IsHexDigit(c)) || File.GetLastWriteTimeUtc(path) >= utcNow.AddHours(-24)) continue;
                        // 活跃接收持有独占句柄；占用、权限不足或并发变更均留给下一次清理。
                        using (FileStream probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        if (File.GetLastWriteTimeUtc(path) >= utcNow.AddHours(-24)) continue;
                        File.Delete(path); removed++;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return removed;
        }
    }
    public sealed class Peer
    {
        public string DeviceId, ConversationId;
        public string Fingerprint;
        public string Avatar;
        public string Id { get { return ConversationId ?? (DeviceId == null ? Endpoint + "/" + Login : "device:" + DeviceId); } }
        public IPEndPoint Endpoint;
        public string Login;
        public string Host;
        public string Nickname;
        public string Group;
        public string Note;
        public bool Pinned, Hidden;
        public int ConversationNumber;
        public bool DuplicateName;
        public string BaseDisplayName { get { return (String.IsNullOrWhiteSpace(Note) ? Nickname : Note) ?? ""; } }
        public string NameSuffix { get { return DuplicateName ? " · #" + ConversationNumber : ""; } }
        public string DisplayName { get { return BaseDisplayName + NameSuffix; } }
        public static void UpdateConversationNames(IEnumerable<Peer> peers)
        {
            // 使用完整联系人集合（含隐藏项），编号不随筛选、上线或列表排序变化。
            List<Peer> all=peers.ToList(); HashSet<int> seen=new HashSet<int>();
            HashSet<int> used=new HashSet<int>(all.Where(p=>p.ConversationNumber>0&&p.ConversationNumber<=1000000).Select(p=>p.ConversationNumber));
            int next=used.DefaultIfEmpty(0).Max();
            foreach(Peer peer in all.OrderBy(p=>p.Id,StringComparer.Ordinal))
            {
                if(peer.ConversationNumber<=0||peer.ConversationNumber>1000000||!seen.Add(peer.ConversationNumber))
                {
                    // 损坏配置带来极大编号时回填空号，不能生成下次读取又被判无效的值。
                    do {next=next>=1000000?1:next+1;} while(used.Contains(next));
                    peer.ConversationNumber=next;used.Add(next);seen.Add(next);
                }
                peer.DuplicateName=false;
            }
            foreach(var group in all.GroupBy(p=>p.BaseDisplayName.Trim(),StringComparer.OrdinalIgnoreCase))
                if(group.Skip(1).Any())foreach(Peer peer in group)peer.DuplicateName=true;
        }
        public bool Utf8;
        public bool Online;
        public DateTime LastSeen;
        // 在线复核只用单调时钟和连续未回应的单播探测数；改系统时间或休眠恢复不会造成长期假在线或集体掉线。
        internal TimeSpan Seen, Probed; internal int Misses;
        public override string ToString() { return Nickname; }
    }
    public sealed class TransferProgress
    {
        public readonly int Percent;
        public readonly double BytesPerSecond;
        public readonly string Stage;
        public TransferProgress(int percent, double bytesPerSecond, string stage = null) { Percent = percent; BytesPerSecond = bytesPerSecond; Stage = stage; }
    }
    internal sealed class TransferRate
    {
        private long previousBytes;
        private double previousTime;
        public double BytesPerSecond { get; private set; }
        internal TransferRate(long offset) { previousBytes = offset; }
        internal bool Update(long bytes, double milliseconds)
        {
            double elapsed = milliseconds - previousTime;
            if (elapsed < 250) return false;
            BytesPerSecond = (bytes - previousBytes) * 1000.0 / elapsed;
            previousBytes = bytes; previousTime = milliseconds; return true;
        }
    }
    public sealed class FileTransferState
    {
        private static readonly string currentSession = Guid.NewGuid().ToString("N");
        public FileTransferState() { Session = currentSession; }
        public string Session { get; set; }
        public int Id { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public long Modified { get; set; }
        public string Sha256 { get; set; }
        public string RelativePath { get; set; }
        public int Percent { get; set; }
        public string Status { get; set; }
        public string LocalPath { get; set; }
        [ScriptIgnore] public double BytesPerSecond { get; set; }
        public string Error { get; set; }
        // 曾经保存过又删除本机文件；重新接收遇到邀请失效时据此恢复「本机文件已删除」，不改写成对方取消。
        public bool SavedBefore { get; set; }
        internal void MarkCurrent() { Session=currentSession; }
        public string DisplayStatus() {
            if (Session != currentSession && (Status == "等待对方接收" || (Status ?? "").Contains("中") || (Status ?? "").StartsWith("待接收"))) return "历史邀请 · 无活动传输";
            return Status;
        }
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
        public FileTransferState[] Transfers { get; set; }
        public bool Deleted { get; set; }
        [ScriptIgnore] internal bool HasReceivedAttachments { get { return !Outgoing && ((Transfers != null && Transfers.Length > 0) || HasAttachments || GetLocalFiles().Length > 0); } }
        public string DisplayText()
        {
            string text = Text ?? "";
            if (Transfers == null || Transfers.Length == 0) return text;
            string suffix = "[文件] " + String.Join("、", Transfers.Select(f => f.Name));
            return text.EndsWith(suffix, StringComparison.Ordinal) ? text.Substring(0, text.Length - suffix.Length).TrimEnd('\r', '\n') : text;
        }
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
    internal sealed class ReceivedFilesDeletionResult
    {
        internal int DeletedCount, SkippedDeletedCount, UnreceivedCount, FailedCount, SkippedBusyCount;
        internal string FirstError;
        internal string Text
        {
            get
            {
                string text = "已删除 " + DeletedCount + " 个本机文件，" + SkippedDeletedCount + " 个已删除跳过。";
                if (SkippedBusyCount > 0) text += SkippedBusyCount + " 个正在接收或处理的文件已跳过。";
                if (FailedCount > 0) text += FailedCount + " 个删除失败，文件与记录已保留：" + FirstError;
                return UnreceivedCount == 0 ? text : text + "（另有 " + UnreceivedCount + " 个未接收附件未处理）";
            }
        }
    }
    internal sealed class ReceivedFileRegistration
    {
        public string Path { get; set; }
        public string PeerId { get; set; }
        public long Packet { get; set; }
        public long Length { get; set; }
        public long WriteUtcTicks { get; set; }
    }
    public sealed class Attachment
    {
        public int Id;
        public string Name;
        public long Size;
        public long Modified;
        public string Sha256;
        public string RelativePath;
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
        private readonly Dictionary<string, HashSet<long>> incomingPackets = new Dictionary<string, HashSet<long>>();
        private readonly Dictionary<string, HashSet<string>> deletedRecords = new Dictionary<string, HashSet<string>>();
        private Dictionary<string, ReceivedFileRegistration> receivedFiles;
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
                s.DeviceId = Protocol.NormalizeDeviceId(s.DeviceId) ?? Guid.NewGuid().ToString("N");
                s.Avatar = Protocol.NormalizeAvatar(s.Avatar) ?? "default";
                if (String.IsNullOrWhiteSpace(s.ReceiveFolder)) s.ReceiveFolder = AppSettings.DefaultReceiveFolder;
                s.KnownPeers = AppSettings.NormalizeKnownPeers(s.KnownPeers);
                s.Conversations = SavedConversation.Normalize(s.Conversations);
                s.ChatFontSize=Math.Max(9,Math.Min(18,s.ChatFontSize));
                if(s.ChatBackground!="none"&&s.ChatBackground!="lines"&&s.ChatBackground!="grid"&&s.ChatBackground!="aurora"&&s.ChatBackground!="image")s.ChatBackground="orbits";
                // 0.7.0 没记录修订号，升级后无法区分默认关闭和用户主动关闭；只迁移这一次。
                if (s.SettingsRevision < 1) { s.AllowLegacyProtocol = true; s.SettingsRevision = 1; }
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
                if (settings.SettingsRevision < 1) settings.SettingsRevision = 1;
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
                if (new FileInfo(path).Length > 4L * MessageText.InputLimit) throw new IOException("草稿文件超过大小限制。");
                string text = File.ReadAllText(path, Encoding.UTF8); return Protocol.Prefix(text, MessageText.InputLimit);
            }
        }
        public void SaveDraft(string peerId, string text)
        {
            if ((text ?? "").Length > MessageText.InputLimit) throw new ArgumentException("草稿超过长度限制。");
            lock (gate)
            {
                string path = Path.ChangeExtension(HistoryPath(peerId), ".draft.txt");
                if (String.IsNullOrEmpty(text)) { if (File.Exists(path)) File.Delete(path); return; }
                string temporary = path + ".tmp";
                try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        public bool AppendIncoming(ChatRecord record)
        {
            if (record == null || record.Outgoing) throw new ArgumentException("这里只接受入站消息。", "record");
            lock (gate)
            {
                DateTime cutoff;
                if (clearedThrough.TryGetValue(record.PeerId, out cutoff) && record.Time <= cutoff) return false;
                if (DeletedKeys(record.PeerId).Contains(RecordKey(record))) return false;
                HashSet<long> packets;
                if (!incomingPackets.TryGetValue(record.PeerId, out packets))
                {
                    packets = new HashSet<long>();
                    string path = HistoryPath(record.PeerId);
                    // 显示历史只读尾部，但查重不能漏掉尾部窗口外已保存的邀请。每个缓存会话只流式建索引一次。
                    if (File.Exists(path))
                        foreach (string line in File.ReadLines(path, Encoding.UTF8))
                            try
                            {
                                ChatRecord item = json.Deserialize<ChatRecord>(line);
                                if (item != null && !item.Outgoing && item.PeerId == record.PeerId && item.Text != null) packets.Add(item.Packet);
                            }
                            catch (ArgumentException) { /* 保留原始坏行，不让残缺记录阻断后续合法消息。 */ }
                            catch (InvalidOperationException) { }
                    // 不为所有离线联系人常驻索引；被逐出的会话下次从原历史重建，不牺牲去重正确性。
                    if (incomingPackets.Count >= 32) incomingPackets.Clear();
                    incomingPackets[record.PeerId] = packets;
                }
                if (packets.Contains(record.Packet)) return false;
                Append(record);
                return true;
            }
        }
        public void Append(ChatRecord record)
        {
            lock (gate)
            {
                if (DeletedKeys(record.PeerId).Contains(RecordKey(record))) return;
                DateTime cutoff;
                if(clearedThrough.TryGetValue(record.PeerId,out cutoff)&&record.Time<=cutoff)return;
                RegisterReceivedFiles(record);
                byte[] bytes = new UTF8Encoding(false).GetBytes(json.Serialize(record) + "\n");
                using (FileStream stream = new FileStream(HistoryPath(record.PeerId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    // 保留坏尾行原文，仅补记录分隔符；完整但缺换行的末条也能继续读取。
                    if (stream.Length > 0) { stream.Position = stream.Length - 1; int last = stream.ReadByte(); if (last != '\n') stream.WriteByte((byte)'\n'); }
                    stream.Position = stream.Length; stream.Write(bytes, 0, bytes.Length);
                }
                string key = record.PeerId + "/" + record.Packet;
                HashSet<long> packets;
                if (!record.Outgoing && incomingPackets.TryGetValue(record.PeerId, out packets)) packets.Add(record.Packet);
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
                // 原子改写为仅含消息身份的删除标记；失败时保留原历史与内存索引。
                DeleteHistoryRecords(peerId,null);
                incomingPackets.Remove(peerId);
                deletedRecords.Remove(peerId);
                // ACK 和退出收尾不应把刚清空的旧消息重新写回。
                clearedThrough[peerId]=DateTime.Now;
                foreach(string key in activePending.Where(p=>p.Value.PeerId==peerId).Select(p=>p.Key).ToArray())activePending.Remove(key);
            }
        }
        public void DeleteRecords(string peerId, IEnumerable<ChatRecord> records)
        {
            if (String.IsNullOrEmpty(peerId)) throw new ArgumentException("会话不能为空。", "peerId");
            ChatRecord[] selected = records.ToArray();
            if (selected.Any(r => r == null || r.PeerId != peerId)) throw new ArgumentException("只能删除当前会话的消息。", "records");
            HashSet<string> keys = new HashSet<string>(selected.Select(RecordKey), StringComparer.Ordinal);
            if (keys.Count == 0) return;
            DeleteHistoryRecords(peerId,keys);
        }
        private void DeleteHistoryRecords(string peerId,HashSet<string> keys)
        {
            lock (gate)
            {
                string path = HistoryPath(peerId);
                if (!File.Exists(path)) return;
                // 清空消息仍保留接收文件登记，换目录或重启后也能从设置清理。
                foreach (ChatRecord record in ReadReceivedHistory(path).Where(r => keys == null || keys.Contains(RecordKey(r)))) RegisterReceivedFiles(record);
                // 历史文件名含 64 位摘要；追加临时后缀会让原本可用的资料目录超过 Windows 路径上限。
                string temporary = Path.Combine(Root, "history-delete-" + Guid.NewGuid().ToString("N") + ".tmp");
                HashSet<string> written = new HashSet<string>();
                try
                {
                    using (StreamReader reader = new StreamReader(path, Encoding.UTF8))
                    using (StreamWriter writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            ChatRecord item = null;
                            try
                            {
                                item = json.Deserialize<ChatRecord>(line);
                            }
                            catch (ArgumentException) { }
                            catch (InvalidOperationException) { }
                            if (item != null && item.PeerId == peerId && (keys==null||keys.Contains(RecordKey(item))))
                            {
                                // 留下不含正文或附件路径的消息号，重启后的重复包和旧 ACK 也不能恢复已删除消息。
                                if (written.Add(RecordKey(item))) writer.WriteLine(json.Serialize(new ChatRecord { PeerId = peerId, Packet = item.Packet, Outgoing = item.Outgoing, Text = "", Deleted = true }));
                            }
                            else if(keys!=null)writer.WriteLine(line);
                        }
                    }
                    File.Replace(temporary, path, null);
                    HashSet<string> deleted;
                    if (deletedRecords.TryGetValue(peerId, out deleted)) deleted.UnionWith(written);
                    foreach(string key in activePending.Where(p=>p.Value.PeerId==peerId&&written.Contains(RecordKey(p.Value))).Select(p=>p.Key).ToArray())activePending.Remove(key);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        internal void DeleteReceivedFile(ChatRecord record,string path)
        {
            lock (gate)
            {
                if(record.Outgoing||String.IsNullOrEmpty(path)||!Path.IsPathRooted(path))throw new IOException("只能删除这条消息中已接收的本机文件。");
                // 登记路径是真实文件名，不能像接收目录设置那样展开 %OS% 之类的环境变量。
                string fullPath=Path.GetFullPath(path);
                string[] registered=record.GetLocalFiles().Concat((record.Transfers??new FileTransferState[0]).Select(f=>f.LocalPath).Where(p=>!String.IsNullOrEmpty(p))).ToArray();
                if(!registered.Any(p=>Path.IsPathRooted(p)&&String.Equals(Path.GetFullPath(p),fullPath,StringComparison.OrdinalIgnoreCase)))throw new IOException("只能删除这条消息中已接收的本机文件。");
                if(Directory.Exists(fullPath))throw new IOException("不能通过附件入口删除文件夹。");
                LoadReceivedFiles();
                // 父目录或磁盘已不存在时文件必然已不在，按已删除订正记录。
                try{File.Delete(fullPath);}catch(DirectoryNotFoundException){}catch(DriveNotFoundException){}
                if (receivedFiles.Remove(fullPath)) SaveReceivedFiles();
                record.LocalFiles=registered.Where(p=>!Path.IsPathRooted(p)||!String.Equals(Path.GetFullPath(p),fullPath,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach(FileTransferState file in record.Transfers??new FileTransferState[0])if(!String.IsNullOrEmpty(file.LocalPath)&&String.Equals(Path.GetFullPath(file.LocalPath),fullPath,StringComparison.OrdinalIgnoreCase)){file.LocalPath=null;file.Status="本机文件已删除";file.Percent=0;file.BytesPerSecond=0;file.Error=null;file.MarkCurrent();}
                if((record.Transfers==null||record.Transfers.Length==0)&&record.LocalFiles.Length==0){record.HasAttachments=true;record.State="本机文件已删除";}
                if (!record.Deleted) Append(record);
            }
        }
        internal ReceivedFilesDeletionResult DeleteReceivedFiles(IEnumerable<ChatRecord> selected, bool apply = true, Func<ChatRecord,FileTransferState,bool> inUse = null, bool registeredOnly = false)
        {
            var result=new ReceivedFilesDeletionResult();
            var handledPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var handledRecords=new HashSet<string>(StringComparer.Ordinal);
            ChatRecord[] records=selected.Where(r=>r.HasReceivedAttachments).ToArray();
            var busyFiles=new HashSet<FileTransferState>(records.SelectMany(record=>(record.Transfers??new FileTransferState[0]).Where(file=>inUse!=null&&inUse(record,file))));
            // 同一路径可能出现在多条历史里；必须先保护所有活跃文件，再逐条删除。
            var busyPaths=new HashSet<string>(busyFiles.Where(file=>!String.IsNullOrEmpty(file.LocalPath)).Select(file=>Path.GetFullPath(file.LocalPath)),StringComparer.OrdinalIgnoreCase);
            lock(gate)
            {
                foreach(ChatRecord record in records)
                {
                    if(!handledRecords.Add(record.PeerId+"/"+record.Packet))continue;
                    result.SkippedBusyCount+=(record.Transfers??new FileTransferState[0]).Where(busyFiles.Contains).Select(file=>file.Id).Distinct().Count();
                    // 保留删除前的附件快照；单文件删除会同步改写同路径附件，不能据改写后的状态重复计数。
                    var files=(record.Transfers??new FileTransferState[0]).Select(f=>Tuple.Create(f,f.LocalPath,f.Status)).ToArray();
                    string[] paths=record.GetLocalFiles().Concat(files.Select(f=>f.Item2).Where(p=>!String.IsNullOrEmpty(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var recordPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(string path in paths)
                    {
                        // 单个文件被占用或路径失效只记失败，同批其余文件继续处理。
                        string fullPath=null;bool exists=false;
                        try
                        {
                            if(!Path.IsPathRooted(path))throw new IOException("已登记的接收文件路径无效，未删除该文件。");
                            fullPath=Path.GetFullPath(path);
                            if(!recordPaths.Add(fullPath))continue;
                            if(busyPaths.Contains(fullPath))continue;
                            if(Directory.Exists(fullPath))throw new IOException("不能通过附件入口删除文件夹。");
                            exists=File.Exists(fullPath);
                            if (registeredOnly && exists) ValidateReceivedFile(fullPath);
                            if(apply)DeleteReceivedFile(record,fullPath);
                        }
                        catch(Exception error)
                        {
                            if(!(error is IOException||error is UnauthorizedAccessException||error is ArgumentException||error is NotSupportedException||error is System.Security.SecurityException))throw;
                            if(handledPaths.Add(fullPath??path)){result.FailedCount++;if(result.FirstError==null){string trimmed=path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);int sep=trimmed.LastIndexOfAny(new[]{Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar});result.FirstError=(sep>=0?trimmed.Substring(sep+1):trimmed)+" — "+error.Message;}}
                            continue;
                        }
                        if(handledPaths.Add(fullPath)) {if(exists)result.DeletedCount++;else result.SkippedDeletedCount++;}
                    }
                    bool corrected=false;
                    foreach(var entry in files.Where(f=>String.IsNullOrEmpty(f.Item2)&&!busyFiles.Contains(f.Item1)).GroupBy(f=>f.Item1.Id).Select(g=>g.First()))
                    {
                        FileTransferState file=entry.Item1;
                        if(entry.Item3=="已保存"||entry.Item3=="本机文件已删除")
                        {
                            result.SkippedDeletedCount++;
                            if(apply&&(file.Status!="本机文件已删除"||file.Percent!=0||file.BytesPerSecond!=0||file.Error!=null)){file.LocalPath=null;file.Status="本机文件已删除";file.Percent=0;file.BytesPerSecond=0;file.Error=null;file.MarkCurrent();corrected=true;}
                        }
                        else result.UnreceivedCount++;
                    }
                    if(files.Length==0&&paths.Length==0)
                    {
                        int count=Math.Max(1,(record.AttachmentNames??new string[0]).Length);
                        if(record.State=="已保存"||record.State=="本机文件已删除"){result.SkippedDeletedCount+=count;if(apply){record.LocalFiles=new string[0];record.State="本机文件已删除";corrected=true;}}
                        else result.UnreceivedCount+=count;
                    }
                    if(corrected && !record.Deleted)Append(record);
                }
            }
            return result;
        }
        private void LoadReceivedFiles()
        {
            if (receivedFiles != null) return;
            string path = Path.Combine(Root, "received-files.json");
            var loaded = new Dictionary<string, ReceivedFileRegistration>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                ReceivedFileRegistration[] entries;
                try { entries = json.Deserialize<ReceivedFileRegistration[]>(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception error) {
                    if (!(error is ArgumentException || error is InvalidOperationException)) throw;
                    throw new IOException("接收文件登记无法读取，原记录已保留，未清理文件。", error);
                }
                if (entries == null) throw new IOException("接收文件登记为空或损坏，未清理文件。");
                foreach (ReceivedFileRegistration entry in entries)
                {
                    if (entry == null || String.IsNullOrEmpty(entry.Path) || !Path.IsPathRooted(entry.Path) || String.IsNullOrEmpty(entry.PeerId))
                        throw new IOException("接收文件登记无效，原记录已保留，未清理文件。");
                    loaded[Path.GetFullPath(entry.Path)] = entry;
                }
            }
            receivedFiles = loaded;
        }
        private void SaveReceivedFiles()
        {
            SecureIdentity.AtomicBytes(Path.Combine(Root, "received-files.json"), Encoding.UTF8.GetBytes(json.Serialize(receivedFiles.Values.ToArray())));
        }
        private void RegisterReceivedFiles(ChatRecord record)
        {
            if (record.Outgoing || record.Deleted || !record.HasReceivedAttachments) return;
            string[] paths = record.GetLocalFiles().Concat((record.Transfers ?? new FileTransferState[0]).Where(f => f.Status == "已保存" && !String.IsNullOrEmpty(f.LocalPath)).Select(f => f.LocalPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 0) return;
            LoadReceivedFiles(); bool changed = false;
            foreach (string path in paths)
            {
                if (!Path.IsPathRooted(path)) continue;
                string fullPath = Path.GetFullPath(path);
                if (receivedFiles.ContainsKey(fullPath) || !File.Exists(fullPath)) continue;
                var info = new FileInfo(fullPath);
                FileTransferState transfer = (record.Transfers ?? new FileTransferState[0]).FirstOrDefault(f => String.Equals(f.LocalPath, path, StringComparison.OrdinalIgnoreCase));
                // 升级导入旧记录时，长度已不符的同名文件不能认作原接收文件。
                if (transfer != null && transfer.Size != info.Length) continue;
                receivedFiles.Add(fullPath, new ReceivedFileRegistration { Path = fullPath, PeerId = record.PeerId, Packet = record.Packet, Length = info.Length, WriteUtcTicks = info.LastWriteTimeUtc.Ticks });
                changed = true;
            }
            if (changed) SaveReceivedFiles();
        }
        private List<ChatRecord> ReadReceivedHistory(string path)
        {
            var latest = new Dictionary<string, ChatRecord>();
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                ChatRecord record;
                try { record = json.Deserialize<ChatRecord>(line); }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (record == null || record.Outgoing || String.IsNullOrEmpty(record.PeerId) || !String.Equals(HistoryPath(record.PeerId), path, StringComparison.OrdinalIgnoreCase)) continue;
                latest[record.PeerId + "/" + record.Packet] = record;
            }
            return latest.Values.Where(r => !r.Deleted && r.HasReceivedAttachments).ToList();
        }
        internal ChatRecord[] ReceivedFileRecords()
        {
            lock (gate)
            {
                LoadReceivedFiles();
                // 只读取本程序的历史登记；绝不扫描默认或自定义接收目录。
                var records = Directory.EnumerateFiles(Root, "history-*.jsonl").SelectMany(ReadReceivedHistory).ToList();
                foreach (ChatRecord record in records) RegisterReceivedFiles(record);
                var known = new HashSet<string>(records.Select(r => r.PeerId + "/" + r.Packet));
                foreach (var group in receivedFiles.Values.GroupBy(f => f.PeerId + "/" + f.Packet).Where(g => !known.Contains(g.Key)))
                    records.Add(new ChatRecord { PeerId = group.First().PeerId, Packet = group.First().Packet, Deleted = true, HasAttachments = true, State = "已保存", LocalFiles = group.Select(f => f.Path).ToArray() });
                return records.ToArray();
            }
        }
        private void ValidateReceivedFile(string path)
        {
            LoadReceivedFiles(); ReceivedFileRegistration entry;
            if (!receivedFiles.TryGetValue(path, out entry)) throw new IOException("未找到可靠的接收文件登记，已保留。");
            if (ReceiveStorage.LinkedDirectory(Path.GetDirectoryName(path)) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("保存路径已变为链接，已保留。");
            var info = new FileInfo(path);
            if (info.Length != entry.Length || info.LastWriteTimeUtc.Ticks != entry.WriteUtcTicks)
                throw new IOException("文件在接收后已被修改或替换，已保留。");
        }
        private static string RecordKey(ChatRecord record) { return record.Outgoing + ":" + record.Packet; }
        private HashSet<string> DeletedKeys(string peerId)
        {
            HashSet<string> keys;
            if (deletedRecords.TryGetValue(peerId, out keys)) return keys;
            keys = new HashSet<string>();
            string path = HistoryPath(peerId);
            if (File.Exists(path)) foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (!line.Contains("\"Deleted\":true")) continue;
                try { ChatRecord item = json.Deserialize<ChatRecord>(line); if (item != null && item.PeerId == peerId && item.Deleted) keys.Add(RecordKey(item)); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (deletedRecords.Count >= 32) deletedRecords.Clear();
            deletedRecords[peerId] = keys; return keys;
        }
        internal List<ChatRecord> IncomingAttachments(string peerId, long? packet = null)
        {
            return AttachmentHistory(peerId, packet, true);
        }
        internal List<ChatRecord> AttachmentHistory(string peerId, long? packet = null, bool incomingOnly = false)
        {
            lock (gate)
            {
                string path = HistoryPath(peerId);
                var records = new Dictionary<string, ChatRecord>();
                if (!File.Exists(path)) return records.Values.ToList();
                HashSet<string> deleted = DeletedKeys(peerId);
                // 清空与后台取消必须覆盖全部历史，不能沿用聊天页的尾部读取上限。
                foreach (string line in File.ReadLines(path, Encoding.UTF8))
                {
                    try
                    {
                        ChatRecord record = json.Deserialize<ChatRecord>(line);
                        if (record == null || record.PeerId != peerId || incomingOnly && record.Outgoing || packet.HasValue && record.Packet != packet.Value) continue;
                        if (record.Deleted || deleted.Contains(RecordKey(record))) { records.Remove(RecordKey(record)); continue; }
                        if(record.Transfers!=null&&record.Transfers.Length>0)records[RecordKey(record)] = record;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
                return records.Values.Where(record => record.Transfers != null && record.Transfers.Length > 0).ToList();
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
                HashSet<string> deleted = DeletedKeys(peerId);
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
                            if (item == null || item.PeerId != peerId || item.Text == null || item.Deleted || deleted.Contains(RecordKey(item))) continue;
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
        private sealed class ArchiveEntry
        {
            internal long Line;
            internal bool Matches, SavedNotice;
            internal string[] Paths;
        }
        internal bool LargeHistory(string peerId)
        {
            FileInfo file=new FileInfo(HistoryPath(peerId));
            return file.Exists && file.Length>256*1024;
        }
        internal sealed class ArchivePage
        {
            internal const int Size=100;
            internal readonly List<ChatRecord> Records=new List<ChatRecord>();
            internal int Total, Page;
            internal bool HasNewer;
        }
        internal ArchivePage ReadArchive(string peerId,string query,int page,ChatRecord target,System.Threading.CancellationToken cancel)
        {
            ArchivePage result=new ArchivePage();
            FileStream stream;
            lock(gate)
            {
                string path=HistoryPath(peerId);
                if(!File.Exists(path))return result;
                stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            }
            using(stream)
            using(StreamReader reader=new StreamReader(stream,Encoding.UTF8))
            {
                var parser=new JavaScriptSerializer { MaxJsonLength=16*1024*1024 };
                var order=new List<ArchiveEntry>();
                var index=new Dictionary<string,ArchiveEntry>(StringComparer.Ordinal);
                string line;long number=0;
                while((line=reader.ReadLine())!=null)
                {
                    cancel.ThrowIfCancellationRequested();number++;
                    ChatRecord record;
                    try {record=parser.Deserialize<ChatRecord>(line);}
                    catch(ArgumentException){continue;}catch(InvalidOperationException){continue;}
                    if(record==null||record.PeerId!=peerId||record.Text==null)continue;
                    string key=RecordKey(record);ArchiveEntry entry;
                    if(!index.TryGetValue(key,out entry)){entry=new ArchiveEntry();index.Add(key,entry);order.Add(entry);}
                    if(record.Deleted){entry.Line=-1;continue;}
                    if(entry.Line<0)continue;
                    entry.Line=number;
                    entry.Matches=query.Length==0||record.Text.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0||
                        (record.Transfers??new FileTransferState[0]).Any(file=>(file.Name??"").IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0);
                    entry.Paths=record.Outgoing?null:record.GetLocalFiles();
                    entry.SavedNotice=record.Sender=="文件" && record.State=="已保存" && record.Text.StartsWith("已保存：",StringComparison.Ordinal);
                }
                var attached=new HashSet<string>(order.Where(entry=>entry.Line>0 && !entry.SavedNotice && entry.Paths!=null).SelectMany(entry=>entry.Paths),StringComparer.OrdinalIgnoreCase);
                order.RemoveAll(entry=>entry.Line<0 || !entry.Matches || entry.SavedNotice && entry.Paths!=null && entry.Paths.Length>0 && entry.Paths.All(attached.Contains));
                result.Total=order.Count;
                if(order.Count==0)return result;
                const int pageSize=ArchivePage.Size;
                result.Page=Math.Min(page,(order.Count-1)/pageSize);
                int end=order.Count-result.Page*pageSize,start=Math.Max(0,end-pageSize);
                if(target!=null)
                {
                    ArchiveEntry anchor;
                    if(!index.TryGetValue(RecordKey(target),out anchor))return result;
                    int position=order.IndexOf(anchor);if(position<0)return result;
                    if(position>=order.Count-500){start=Math.Max(0,order.Count-500);end=order.Count;}
                    else{start=Math.Max(0,position-75);end=Math.Min(order.Count,start+150);}
                }
                result.HasNewer=end<order.Count;
                var slots=new Dictionary<long,int>();
                for(int position=start;position<end;position++){slots.Add(order[position].Line,position-start);result.Records.Add(null);}
                reader.DiscardBufferedData();stream.Position=0;number=0;
                while(slots.Count>0 && (line=reader.ReadLine())!=null)
                {
                    cancel.ThrowIfCancellationRequested();int slot;
                    if(!slots.TryGetValue(++number,out slot))continue;
                    ChatRecord record=parser.Deserialize<ChatRecord>(line);
                    if(record.Time.Kind==DateTimeKind.Utc)record.Time=record.Time.ToLocalTime();
                    if(record.Outgoing && record.State=="等待确认")lock(gate){if(!activePending.ContainsKey(peerId+"/"+record.Packet))record.State="上次退出，未确认送达";}
                    result.Records[slot]=record;slots.Remove(number);
                }
                if(slots.Count!=0)throw new IOException("聊天记录已更改，请重新搜索。");
            }
            return result;
        }
    }
}
