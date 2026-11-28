using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FeiqLight
{
    internal sealed class ToolsService : IDisposable
    {
        internal const int ListScanLimit = 20000;
        internal const int ListScanMilliseconds = 2000;
        internal const long ListSnapshotLifetime = 120000;
        private const int SnapshotLimit = 4, SessionScanLimit = 40000, BrowseIdLimit = 10000;
        private const int ArchiveDirectoryLimit = 1000, ArchiveScanLimit = 10000, ArchiveDepthLimit = 32;
        private sealed class Session
        {
            internal string Token, Address, Mode, Fingerprint;
            internal long Last, NextCapture;
            internal bool Listing;
            internal readonly List<ListSnapshot> Snapshots = new List<ListSnapshot>();
        }
        private sealed class SharedEntry
        {
            internal string Path, Root;
        }
        private sealed class ListItem
        {
            internal SharedEntry Source;
            internal string Name, NormalName;
            internal bool Directory;
            internal long Size, ModifiedTicks;
            internal long? Modified;
        }
        private sealed class ListSnapshot
        {
            internal string TargetId, Name, Query, Sort, Order;
            internal SharedEntry Parent;
            internal int Revision, Scanned;
            internal long Created;
            internal bool Truncated;
            internal readonly List<ListItem> Items = new List<ListItem>();
            internal readonly Dictionary<string, int> Cursors = new Dictionary<string, int>();
            internal readonly Dictionary<int, string> Offsets = new Dictionary<int, string>();
        }
        private sealed class ArchiveLease
        {
            internal SharedEntry Target;
            internal Session Session;
            internal ToolsHttp.Response Response;
            internal Stream Input;
            internal FileSystemWatcher Watcher;
            internal volatile bool Cancelled;
        }
        private sealed class ArchiveFile
        {
            internal ListItem Item;
            internal string Relative;
            internal HandleInfo Identity;
            internal bool HasIdentity;
        }
        private sealed class ArchiveDirectory
        {
            internal string Path, Relative;
            internal int Depth;
        }
        private readonly object gate = new object();
        private readonly object captureGate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<Session> sessions = new List<Session>();
        private readonly Dictionary<string, SharedEntry> entries = new Dictionary<string, SharedEntry>();
        private readonly Dictionary<string, string> entryIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> entryOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> entryNodes = new Dictionary<string, LinkedListNode<string>>();
        private readonly List<ArchiveLease> archives = new List<ArchiveLease>();
        private readonly ToolsHttp http;
        private readonly ToolsPairings pairings;
        private readonly ToolsPreferences preferences;
        private readonly SecureIdentity security;
        private string[] folders = new string[0];
        private string shareCode = "", remoteCode = "";
        private long shareUntil, remoteUntil, attemptsSince;
        private int attempts, remoteEpoch, shareEpoch, shareRevision;
        private bool closed, remoteEnabled, remotePending;
        internal Func<string, bool> ConfirmRemote;
        internal Func<string, int> ConfirmRemoteDevice;
        internal Func<int, byte[]> Capture;
        internal Func<int, uint, byte[]> CaptureDelta;
        internal Func<bool, object> Displays;
        internal Func<string, Func<bool>, string> ClipboardText;
        internal Action<Dictionary<string, object>> Input;
        internal Action ReleaseInput;
        internal int Port { get { return http.Port; } }
        internal string Folder { get { lock (gate) return folders.FirstOrDefault(); } }
        internal string[] Folders { get { lock (gate) return folders.ToArray(); } }
        internal string ShareCode { get { lock (gate) return clock.ElapsedMilliseconds < shareUntil ? shareCode : "已过期，请重新开启"; } }
        internal string RemoteCode { get { lock (gate) return clock.ElapsedMilliseconds < remoteUntil ? remoteCode : "已使用或过期"; } }
        internal bool RemoteActive { get { lock (gate) return sessions.Any(s => s.Mode == "remote" && Valid(s)); } }
        internal string RemoteAddress { get { lock (gate) { Session session = sessions.FirstOrDefault(s => s.Mode == "remote" && Valid(s)); return session == null ? "" : session.Address; } } }
        internal bool RemoteEnabled { get { lock (gate) return remoteEnabled; } }
        internal bool RemoteClipboardRead { get { lock (gate) return preferences.Value.RemoteClipboardRead; } }
        internal void SetRemoteClipboardRead(bool enabled) { lock (gate) preferences.SaveRemoteClipboardRead(enabled); }
        internal string SavedFolder { get { lock (gate) return preferences.Value.Folder; } }
        internal string[] SavedFolders { get { lock (gate) return preferences.Value.Folders.ToArray(); } }
        internal ToolsService(int port, bool loopback, string root = null,SecureIdentity security=null) { this.security=security;pairings = new ToolsPairings(root); preferences = new ToolsPreferences(root); http = new ToolsHttp(port, loopback, Handle,security);if(security!=null)security.TrustRevoked+=OnTrustRevoked; }
        private void OnTrustRevoked(string fingerprint)
        {
            lock(gate)
            {
                bool remote=sessions.Any(session=>session.Mode=="remote"&&session.Fingerprint==fingerprint);
                CancelArchives(archive=>archive.Session.Fingerprint==fingerprint);
                sessions.RemoveAll(session=>session.Fingerprint==fingerprint);
                // 撤信也使正在等待本机确认的登录失效；其他已认证会话保持可用。
                remoteEpoch++;shareEpoch++;remotePending=false;Monitor.PulseAll(gate);
                if(remote&&ReleaseInput!=null)ReleaseInput();
                pairings.ForgetFingerprint(fingerprint);
            }
        }
        internal void NetworkReset(){lock(gate){CancelArchives(archive=>true);sessions.Clear();Monitor.PulseAll(gate);remoteEpoch++;shareEpoch++;remotePending=false;shareCode=ConnectionCode();remoteCode=ConnectionCode();shareUntil=remoteUntil=clock.ElapsedMilliseconds+600000;http.DisconnectClients();if(ReleaseInput!=null)ReleaseInput();}}
        internal string RestoreSettings()
        {
            lock (gate)
            {
                string error = null;
                if (preferences.Value.ShareEnabled)
                    try { StartSharing(preferences.Value.Folders, false); }
                    catch (Exception e) { if (!(e is IOException || e is UnauthorizedAccessException || e is ToolsHttp.HttpError || e is ArgumentException)) throw; error = "共享目录未恢复：" + e.Message; }
                if (preferences.Value.RemoteEnabled) StartRemote(false);
                return error;
            }
        }
        internal static string RandomToken(int bytes)
        {
            byte[] value = new byte[bytes]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(value);
            return BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
        }
        internal static string ConnectionCode()
        {
            // 丢弃尾部余数，避免取模让部分数字更容易出现。
            byte[] bytes = new byte[4]; uint value;
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                do { random.GetBytes(bytes); value = BitConverter.ToUInt32(bytes, 0); } while (value >= 4294000000u);
            return (value % 1000000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
        internal void Share(string path) { StartSharing(new[] { path }, true); }
        internal void StartSavedSharing() { lock (gate) {StartSharing(preferences.Value.Folders, true);shareCode=ConnectionCode();shareUntil=clock.ElapsedMilliseconds+600000;} }
        internal void AddFolders(IEnumerable<string> paths)
        {
            lock (gate) StartSharing(preferences.Value.Folders.Concat(paths).ToArray(), true);
        }
        internal void RemoveFolder(string path)
        {
            lock (gate)
            {
                string[] remaining = preferences.Value.Folders.Where(p => !String.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (remaining.Length == preferences.Value.Folders.Length) return;
                bool enabled = folders.Length > 0 && remaining.Length > 0;
                try {
                    if(enabled)StartSharing(remaining,true);
                    else {EndSharing();preferences.SaveFolders(remaining,false,preferences.Value.RemoteEnabled);}
                }
                catch {EndSharing();throw;}
            }
        }
        private void StartSharing(string[] paths, bool save)
        {
            string[] full = paths.Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .Select(p => p.Length > 3 ? p.TrimEnd(Path.DirectorySeparatorChar) : p).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (full.Length == 0 || full.Length > 20) throw new IOException("请选择 1 至 20 个共享文件夹");
            foreach (string path in full) { CheckNoLinks(path); if (!Directory.Exists(path)) throw new IOException("共享文件夹不存在：" + path); }
            lock (gate)
            {
                if (closed) throw new ObjectDisposedException("ToolsService");
                bool starting=folders.Length==0;string previous=Scope("share");
                if (save) preferences.SaveFolders(full, true, preferences.Value.RemoteEnabled);
                string[] old=folders;folders=full;
                try {if(!starting)pairings.ChangeShareScope(previous,Scope("share"));}
                catch {folders=old;throw;}
                foreach(string id in entries.Where(item=>!Shared(item.Value)).Select(item=>item.Key).ToList())ForgetEntry(id);
                CancelArchives(archive=>!Shared(archive.Target));
                InvalidateListings();
                shareRevision++;
                if(starting){shareCode = ConnectionCode(); shareUntil = clock.ElapsedMilliseconds + 600000;}
            }
        }
        internal void StopSharing() { lock (gate) { EndSharing(); preferences.SaveFolders(preferences.Value.Folders, false, preferences.Value.RemoteEnabled); } }
        private void EndSharing() { shareEpoch++; folders = new string[0]; shareCode = ""; shareUntil = 0; CancelArchives(archive=>true); entries.Clear(); entryIds.Clear(); entryOrder.Clear(); entryNodes.Clear(); sessions.RemoveAll(s => s.Mode == "share"); }
        internal void AllowRemote() { StartRemote(true); }
        private void StartRemote(bool save)
        {
            lock (gate) { if (closed) throw new ObjectDisposedException("ToolsService"); if (save) preferences.SaveFolders(preferences.Value.Folders, preferences.Value.ShareEnabled, true); EndRemote(); remoteEnabled = true; remoteCode = ConnectionCode(); remoteUntil = clock.ElapsedMilliseconds + 600000; }
        }
        internal void StopRemote() { lock (gate) { EndRemote(); preferences.SaveFolders(preferences.Value.Folders, preferences.Value.ShareEnabled, false); } }
        // 锁屏、注销和显示器切换只结束当前会话，不能把用户保存的开机恢复意愿改成关闭。
        internal void SuspendRemote() { lock (gate) EndRemote(); }
        internal void ResumeRemote() { lock (gate) { if (!closed && preferences.Value.RemoteEnabled && !remoteEnabled) StartRemote(false); } }
        private void EndRemote() { remoteEpoch++; remoteEnabled = false; remotePending = false; remoteCode = ""; remoteUntil = 0; sessions.RemoveAll(s => s.Mode == "remote"); http.DisconnectRemoteClients(); Monitor.PulseAll(gate); if (ReleaseInput != null) ReleaseInput(); }
        internal void ForgetPairings() { lock (gate) { pairings.Clear(); shareEpoch++; StopRemote(); CancelArchives(archive=>true); sessions.Clear(); } }
        private string Scope(string mode)
        {
            if (mode == "remote") return "remote";
            if (folders.Length <= 1) return "share:" + (folders.FirstOrDefault() ?? "").ToUpperInvariant();
            // 本机修改共享集合时同步迁移现有配对，保留证书绑定与到期时间。
            return "shares:" + String.Join("|", folders.Select(p => p.ToUpperInvariant()).OrderBy(p => p, StringComparer.Ordinal).Select(p => p.Length + ":" + p));
        }
        private string PairCookie(string mode) { return "fq_pair_" + mode + "_" + Port; }
        private static string Cookie(ToolsHttp.Request request, string name)
        {
            string item = request.Header("Cookie").Split(';').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith(name + "=", StringComparison.Ordinal));
            return item == null ? "" : item.Substring(name.Length + 1);
        }
        private bool Valid(Session session) { return !closed && sessions.Contains(session) && clock.ElapsedMilliseconds - session.Last < (session.Mode == "remote" ? 30000 : 1800000); }
        private Session Authorize(ToolsHttp.Request request, string mode)
        {
            string cookie = request.Header("Cookie").Split(';').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("fq_" + mode + "=", StringComparison.Ordinal));
            string token = cookie == null ? "" : cookie.Substring(cookie.IndexOf('=') + 1);
            if (token.Length == 0) token = request.Header("X-FQ-Token");
            if (token.Length == 0 && (request.Method == "GET" || request.Method == "HEAD") && request.Path == "/file") token = request.Parameter("token");
            lock (gate)
            {
                Session session = sessions.FirstOrDefault(s => s.Mode == mode && s.Token == token && s.Address == request.Address && s.Fingerprint==request.Fingerprint);
                if (session == null || !Valid(session)) throw new ToolsHttp.HttpError(401, "连接已结束，请重新连接");
                if (request.Method == "POST" && request.Header("X-FQ-Token") != token) throw new ToolsHttp.HttpError(403, "请求校验失败");
                if (mode == "remote" && request.Path != "/api/logout") request.BindRemoteSession(session.Token);
                session.Last = clock.ElapsedMilliseconds; return session;
            }
        }
        private void Login(ToolsHttp.Request request, ToolsHttp.Response response, bool reconnect)
        {
            Dictionary<string, object> data = request.Json(); string mode = Text(data, "mode"), code = reconnect ? "" : Text(data, "code").Trim();
            if (mode != "share" && mode != "remote") throw new ToolsHttp.HttpError(400, "未知连接类型");
            if (reconnect && request.Header("X-FQ-Reconnect") != "1") throw new ToolsHttp.HttpError(403, "请求校验失败");
            string expected, scope, remembered = Cookie(request, PairCookie(mode)); int epoch; bool trusted;
            lock (gate)
            {
                foreach(Session expired in sessions.Where(s=>s.Mode=="remote"&&!Valid(s)))http.DisconnectRemoteClients(expired.Token);
                CancelArchives(archive=>!Valid(archive.Session));
                sessions.RemoveAll(s => !Valid(s));
                if (clock.ElapsedMilliseconds - attemptsSince >= 60000) { attemptsSince = clock.ElapsedMilliseconds; attempts = 0; }
                if (++attempts > 8) throw new ToolsHttp.HttpError(429, "尝试过多，请一分钟后重试");
                expected = mode == "share" ? shareCode : remoteCode;
                epoch = mode == "share" ? shareEpoch : remoteEpoch; scope = request.PairScope(Scope(mode));
                trusted = reconnect && pairings.AlwaysAllows(remembered, scope);
                if (closed || (mode == "share" ? folders.Length == 0 : !remoteEnabled)) throw new ToolsHttp.HttpError(403, "对方尚未开启此功能");
                if (reconnect) { if (!pairings.Contains(remembered, scope)) throw new ToolsHttp.HttpError(401, "没有可用的上次连接，请输入连接码"); }
                else if (expected.Length == 0 || code != expected || clock.ElapsedMilliseconds >= (mode == "share" ? shareUntil : remoteUntil)) throw new ToolsHttp.HttpError(403, "连接码错误或已过期");
                if (sessions.Count >= 8) throw new ToolsHttp.HttpError(409, "连接人数已满");
                // 远控码只接受一个请求，确认期间其他请求不能借同一码排队。
                if (mode == "remote")
                {
                    if (remotePending || sessions.Any(s => s.Mode == "remote")) throw new ToolsHttp.HttpError(409, "已有设备正在控制或等待确认");
                    remotePending = true; if (!reconnect) { remoteCode = ""; remoteUntil = 0; }
                }
            }
            try
            {
                int approval = 1;
                if (mode == "remote" && !trusted) approval = ConfirmRemoteDevice != null ? ConfirmRemoteDevice(request.Address) : ConfirmRemote != null && ConfirmRemote(request.Address) ? 1 : 0;
                if (approval == 0) throw new ToolsHttp.HttpError(403, "Windows 未同意本次控制");
                lock (gate)
                {
                    if (closed || (mode == "share" ? shareEpoch : remoteEpoch) != epoch) throw new ToolsHttp.HttpError(403, "共享或控制已关闭");
                    if (sessions.Count >= 8) throw new ToolsHttp.HttpError(409, "连接人数已满");
                    remembered = pairings.Remember(remembered, scope, approval == 2);
                    Session session = new Session { Mode = mode, Address = request.Address, Fingerprint=request.Fingerprint, Token = RandomToken(32), Last = clock.ElapsedMilliseconds };
                    sessions.Add(session); response.Json(new { token = session.Token, mode = mode }, "Set-Cookie: fq_" + mode + "=" + session.Token + "; Path=/; HttpOnly; SameSite=Strict\r\nSet-Cookie: " + PairCookie(mode) + "=" + remembered + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=7776000\r\n");
                }
            }
            finally { if (mode == "remote") lock (gate) { if (remoteEpoch == epoch) remotePending = false; } }
        }
        internal static string Text(Dictionary<string, object> data, string key)
        {
            object value; if (data == null || !data.TryGetValue(key, out value) || !(value is string)) throw new ToolsHttp.HttpError(400, "缺少请求字段"); return (string)value;
        }
        internal static string ValidateClipboardText(string text, bool allowEmpty = false)
        {
            if ((!allowEmpty && text.Length == 0) || text.Length > 4000 || text.IndexOf('\0') >= 0) throw new ToolsHttp.HttpError(400, "剪贴板文字须为 1 至 4000 字，不能包含空字符");
            try { if (new UTF8Encoding(false, true).GetByteCount(text) > 12000) throw new ToolsHttp.HttpError(400, "剪贴板文字超过 12000 字节，请用文件传输"); }
            catch (EncoderFallbackException) { throw new ToolsHttp.HttpError(400, "剪贴板文字编码无效"); }
            return text;
        }
        private void WaitForFrame(Session session, int milliseconds)
        {
            long until = clock.ElapsedMilliseconds + milliseconds;
            lock (gate)
            {
                while (true)
                {
                    if (!Valid(session)) throw new ToolsHttp.HttpError(401, "控制已停止");
                    int left = (int)(until - clock.ElapsedMilliseconds); if (left <= 0) return;
                    Monitor.Wait(gate, left);
                }
            }
        }
        private void Handle(ToolsHttp.Request request, ToolsHttp.Response response)
        {
            if (request.Method != "GET" && request.Method != "HEAD" && request.Method != "POST") throw new ToolsHttp.HttpError(405, "不支持此操作");
            if (request.Method == "GET" && request.Path == "/api/status")
            {
                lock (gate)
                {
                    string sharePair = Cookie(request, PairCookie("share")), remotePair = Cookie(request, PairCookie("remote"));
                    bool sharePaired = !closed && folders.Length > 0 && pairings.Contains(sharePair, request.PairScope(Scope("share")));
                    bool remotePaired = !closed && remoteEnabled && pairings.Contains(remotePair, request.PairScope(Scope("remote")));
                    response.Json(new { remote = remoteEnabled, share = folders.Length > 0, paired = sharePaired, remotePaired = remotePaired, trusted = remotePaired && pairings.AlwaysAllows(remotePair, request.PairScope(Scope("remote"))), platform = "windows", listPaging = true, directoryArchive = true, clipboardWrite = true, clipboardRead = preferences.Value.RemoteClipboardRead });
                }
                return;
            }
            if (request.Method == "POST" && (request.Path == "/api/login" || request.Path == "/api/reconnect")) { Login(request, response, request.Path == "/api/reconnect"); return; }
            if (request.Method == "POST" && request.Path == "/api/logout")
            {
                string mode = Text(request.Json(), "mode"); if (mode != "share" && mode != "remote") throw new ToolsHttp.HttpError(400, "无效类型");
                Session session = Authorize(request, mode); lock (gate) { sessions.Remove(session); CancelArchives(archive=>archive.Session==session); if (mode == "remote") http.DisconnectRemoteClients(session.Token); Monitor.PulseAll(gate); if (mode == "remote" && ReleaseInput != null) ReleaseInput(); } response.Json(new { ok = true }); return;
            }
            if (request.Path == "/api/input" && request.Method == "POST")
            {
                Session session = Authorize(request, "remote"); Dictionary<string, object> data = request.Json();
                lock (gate) { if (!Valid(session)) throw new ToolsHttp.HttpError(401, "控制已停止"); if (Input == null) throw new ToolsHttp.HttpError(503, "控制不可用"); Input(data); }
                response.Json(new { ok = true }); return;
            }
            if (request.Path == "/api/clipboard" && (request.Method == "GET" || request.Method == "POST"))
            {
                Session session = Authorize(request, "remote"); bool read = request.Method == "GET";
                if (read && !RemoteClipboardRead) throw new ToolsHttp.HttpError(403, "对方未开启远控读取剪贴板");
                if (ClipboardText == null) throw new ToolsHttp.HttpError(503, "剪贴板不可用");
                string text = read ? null : ValidateClipboardText(Text(request.Json(), "text"));
                // UI Invoke 期间不持有 gate；UI 定时刷新与停止操作也需要该锁。
                string result = ClipboardText(text, () => { lock (gate) return Valid(session) && (!read || preferences.Value.RemoteClipboardRead); });
                lock (gate)
                {
                    if (!Valid(session)) throw new ToolsHttp.HttpError(401, "控制已停止");
                    if (read && !preferences.Value.RemoteClipboardRead) throw new ToolsHttp.HttpError(403, "对方未开启远控读取剪贴板");
                }
                if (read) response.Json(new { text = result, empty = result.Length == 0 });
                else response.Json(new { ok = true });
                return;
            }
            if (request.Method == "POST") throw new ToolsHttp.HttpError(404, "没有此操作");
            if (request.Path == "/api/displays")
            {
                Authorize(request, "remote"); if (Displays == null) throw new ToolsHttp.HttpError(503, "屏幕列表不可用"); response.Json(Displays(RemoteClipboardRead)); return;
            }
            if (request.Path == "/api/screen")
            {
                Session session = Authorize(request, "remote"); byte[] frame;
                int display; string selected = request.Parameter("display");
                if (selected.Length == 0) display = 0;
                else if (!Int32.TryParse(selected, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out display)) throw new ToolsHttp.HttpError(400, "屏幕编号无效");
                bool delta = request.Parameter("delta") == "1" && CaptureDelta != null;
                uint since = 0;
                if (delta && !UInt32.TryParse(request.Parameter("since"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out since)) throw new ToolsHttp.HttpError(400, "帧序号无效");
                // JPEG 编码不能占用共享授权锁，否则同时播放的视频每段读取都要等截图。
                lock (captureGate)
                {
                    lock (gate) { if (!Valid(session) || (!delta && Capture == null)) throw new ToolsHttp.HttpError(401, "控制已停止"); }
                    if (delta)
                    {
                        WaitForFrame(session, (int)Math.Max(0, session.NextCapture - clock.ElapsedMilliseconds));
                        session.NextCapture = clock.ElapsedMilliseconds + 80;
                        frame = CaptureDelta(display, since);
                        long unchangedUntil = clock.ElapsedMilliseconds + 300;
                        for (int i = 0; frame[4] == 0 && i < 4 && clock.ElapsedMilliseconds < unchangedUntil; i++)
                        {
                            WaitForFrame(session, (int)Math.Min(75, unchangedUntil - clock.ElapsedMilliseconds));
                            frame = CaptureDelta(display, since);
                        }
                    }
                    else frame = Capture(display);
                }
                lock (gate) { if (!Valid(session)) throw new ToolsHttp.HttpError(401, "控制已停止"); }
                response.Bytes(frame, delta ? "application/octet-stream" : "image/jpeg"); return;
            }
            if (request.Path == "/api/list" || request.Path == "/api/files") { ListDirectory(request, response, Authorize(request, "share")); return; }
            if(request.Path=="/api/share-status") {Authorize(request,"share");lock(gate)response.Json(new {revision=shareRevision,listPaging=true,directoryArchive=true});return;}
            if (request.Path == "/file") { FileResponse(request, response, Authorize(request, "share")); return; }
            if (request.Path == "/favicon.ico" || request.Path.StartsWith("/native/", StringComparison.Ordinal)) { response.Bytes(new byte[0], "image/x-icon", 204); return; }
            string asset = request.Path == "/" ? "index.html" : request.Path.TrimStart('/');
            if (asset != "index.html" && asset != "app.js" && asset != "app.css") throw new ToolsHttp.HttpError(404, "没有此页面");
            using (Stream resource = typeof(ToolsService).Assembly.GetManifestResourceStream("FeiqLight.Tools." + asset))
            {
                if (resource == null) throw new ToolsHttp.HttpError(500, "页面资源缺失");
                using (MemoryStream buffer = new MemoryStream()) { resource.CopyTo(buffer); response.Bytes(buffer.ToArray(), asset.EndsWith(".js") ? "application/javascript; charset=utf-8" : asset.EndsWith(".css") ? "text/css; charset=utf-8" : "text/html; charset=utf-8"); }
            }
        }
        private SharedEntry Resolve(string id)
        {
            if (folders.Length == 0) throw new ToolsHttp.HttpError(403, "共享已关闭");
            if (id.Length == 0 && folders.Length == 1) return new SharedEntry { Path=folders[0], Root=folders[0] };
            SharedEntry entry; if (!entries.TryGetValue(id, out entry)||!Shared(entry)) throw new ToolsHttp.HttpError(404, "文件索引已失效，请刷新目录");
            TouchEntry(id); return entry;
        }
        private bool Shared(SharedEntry entry){return entry!=null&&folders.Contains(entry.Root,StringComparer.OrdinalIgnoreCase)&&(String.Equals(entry.Path,entry.Root,StringComparison.OrdinalIgnoreCase)||entry.Path.StartsWith(entry.Root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase));}
        internal static string FolderName(string path) { string name = Path.GetFileName(path.TrimEnd('\\')); return name.Length == 0 ? path : name; }
        private static string EntryKey(SharedEntry entry) { return entry.Root+"\0"+entry.Path; }
        private void TouchEntry(string id)
        {
            LinkedListNode<string> node=entryNodes[id]; entryOrder.Remove(node); entryOrder.AddLast(node);
        }
        private void ForgetEntry(string id)
        {
            entryIds.Remove(EntryKey(entries[id])); entries.Remove(id); entryOrder.Remove(entryNodes[id]); entryNodes.Remove(id);
        }
        private string Id(SharedEntry entry)
        {
            string key=EntryKey(entry),id;
            if(entryIds.TryGetValue(key,out id)){TouchEntry(id);return id;}
            if(entries.Count>=BrowseIdLimit)ForgetEntry(entryOrder.First.Value);
            id=RandomToken(16); entries.Add(id,entry); entryIds.Add(key,id); entryNodes.Add(id,entryOrder.AddLast(id)); return id;
        }
        private void InvalidateListings()
        {
            // 保留短期游标墓碑以返回明确的 revision 冲突，释放旧目录的完整扫描结果。
            foreach(Session session in sessions)foreach(ListSnapshot snapshot in session.Snapshots){snapshot.Items.Clear();snapshot.Scanned=0;}
        }
        private static string NormalName(string name) { return name.Normalize(NormalizationForm.FormC).ToUpperInvariant(); }
        private static ListItem ReadListItem(SharedEntry source, FileSystemInfo info)
        {
            bool directory=(info.Attributes&FileAttributes.Directory)!=0;
            DateTime modified=info.LastWriteTimeUtc;
            string name=FolderName(source.Path);
            DateTime epoch=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc);
            return new ListItem {
                Source=source,Name=name,NormalName=NormalName(name),Directory=directory,
                Size=directory?0:((FileInfo)info).Length,ModifiedTicks=modified.Ticks,
                Modified=modified>epoch?(long?)(modified-epoch).TotalMilliseconds:null
            };
        }
        private static int CompareItems(ListItem first,ListItem second,string sort,string order)
        {
            if(first.Directory!=second.Directory)return first.Directory?-1:1;
            int comparison;
            if(sort=="size"||sort=="mtime")
            {
                long? a=sort=="size"?(first.Size<0?null:(long?)first.Size):first.Modified;
                long? b=sort=="size"?(second.Size<0?null:(long?)second.Size):second.Modified;
                if(a.HasValue!=b.HasValue)return a.HasValue?-1:1;
                comparison=a.HasValue?a.Value.CompareTo(b.Value):0;
            }
            else comparison=StringComparer.Ordinal.Compare(first.NormalName,second.NormalName);
            if(comparison!=0)return order=="desc"?-comparison:comparison;
            comparison=StringComparer.Ordinal.Compare(first.NormalName,second.NormalName);
            if(comparison==0)comparison=StringComparer.Ordinal.Compare(EntryKey(first.Source),EntryKey(second.Source));
            return comparison;
        }
        private void CheckListing(Session session,int revision)
        {
            lock(gate)
            {
                if(!Valid(session))throw new ToolsHttp.HttpError(401,"共享已关闭");
                if(revision!=shareRevision)throw new ToolsHttp.HttpError(409,"共享目录已变化，请从首页刷新");
            }
        }
        private void ListDirectory(ToolsHttp.Request request,ToolsHttp.Response response,Session session)
        {
            bool paging=request.Query.Split('&').Any(part=>new[]{"limit","cursor","q","sort","order"}.Contains(part.Split('=')[0]));
            string target=request.Parameter("id"),cursor=request.Parameter("cursor"),query=request.Parameter("q");
            if(query.Length>256||query.IndexOf('\0')>=0)throw new ToolsHttp.HttpError(400,"搜索参数无效");
            try{query=NormalName(query);}catch(ArgumentException){throw new ToolsHttp.HttpError(400,"搜索文字编码无效");}
            string sort=request.Parameter("sort"),order=request.Parameter("order"),limitText=request.Parameter("limit");
            if(sort.Length==0)sort="name";if(order.Length==0)order="asc";
            int limit=100;
            if(limitText.Length>0&&(!Int32.TryParse(limitText,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out limit)||limit<1))throw new ToolsHttp.HttpError(400,"每页数量须为正整数，最多 200 项");
            limit=Math.Min(limit,200);
            if(query.Length>256||query.IndexOf('\0')>=0||!new[]{"name","size","mtime"}.Contains(sort)||!new[]{"asc","desc"}.Contains(order))throw new ToolsHttp.HttpError(400,"搜索或排序参数无效");
            ListSnapshot snapshot=null;int offset=0;
            lock(gate)
            {
                session.Snapshots.RemoveAll(item=>clock.ElapsedMilliseconds-item.Created>=ListSnapshotLifetime);
                if(cursor.Length>0)
                {
                    snapshot=session.Snapshots.FirstOrDefault(item=>item.Cursors.TryGetValue(cursor,out offset));
                    if(snapshot==null)throw new ToolsHttp.HttpError(410,"目录分页已过期或被淘汰，请从首页刷新");
                    if(snapshot.Revision!=shareRevision||snapshot.TargetId!=target||snapshot.Query!=query||snapshot.Sort!=sort||snapshot.Order!=order)throw new ToolsHttp.HttpError(409,"目录分页条件或共享目录已变化，请从首页刷新");
                }
                else
                {
                    if(session.Listing)throw new ToolsHttp.HttpError(429,"目录正在读取，请稍后刷新");
                    SharedEntry parent=target.Length==0&&folders.Length>1?null:Resolve(target);
                    snapshot=new ListSnapshot { Parent=parent,TargetId=target,Name=parent==null?"共享文件夹":FolderName(parent.Path),Revision=shareRevision,Query=query,Sort=sort,Order=order,Created=clock.ElapsedMilliseconds };
                    session.Listing=true;
                }
            }
            if(cursor.Length==0)
            {
                try
                {
                    ScanDirectory(snapshot,session,response,paging);
                    if(!paging&&snapshot.Truncated)throw new ToolsHttp.HttpError(409,"目录内容已变化，请刷新后重试");
                    if(paging)snapshot.Items.Sort((a,b)=>CompareItems(a,b,sort,order));
                    lock(gate)
                    {
                        CheckListing(session,snapshot.Revision);
                        if(paging)
                        {
                            while(session.Snapshots.Count>=SnapshotLimit||session.Snapshots.Sum(item=>item.Scanned)+snapshot.Scanned>SessionScanLimit)session.Snapshots.RemoveAt(0);
                            session.Snapshots.Add(snapshot);
                        }
                    }
                }
                finally { lock(gate)session.Listing=false; }
            }
            object result;
            lock(gate)
            {
                CheckListing(session,snapshot.Revision);
                List<object> page=new List<object>();
                foreach(ListItem item in snapshot.Items.Skip(offset).Take(paging?limit:1000))
                    page.Add(new {id=Id(item.Source),name=item.Name,dir=item.Directory,size=item.Size,mtime=item.Modified,seek=true});
                if(paging)
                {
                    int next=offset+page.Count;string nextCursor=null;
                    if(next<snapshot.Items.Count&&!snapshot.Offsets.TryGetValue(next,out nextCursor))
                    {nextCursor=RandomToken(32);snapshot.Offsets.Add(next,nextCursor);snapshot.Cursors.Add(nextCursor,next);}
                    result=new {name=snapshot.Name,revision=snapshot.Revision,items=page,id=snapshot.Parent==null?null:Id(snapshot.Parent),nextCursor=nextCursor,truncated=snapshot.Truncated};
                }
                else result=new {name=snapshot.Name,revision=snapshot.Revision,items=page};
            }
            response.Json(result);
        }
        private void ScanDirectory(ListSnapshot snapshot,Session session,ToolsHttp.Response response,bool paging)
        {
            SharedEntry[] roots;
            lock(gate)roots=folders.Select(root=>new SharedEntry {Path=root,Root=root}).ToArray();
            if(snapshot.Parent!=null)
            {
                CheckNoLinks(snapshot.Parent.Path);
                if(!Directory.Exists(snapshot.Parent.Path))throw new ToolsHttp.HttpError(404,"目录已不存在");
            }
            // 枚举和磁盘元数据读取都在授权锁外，慢目录不能挡住移根、视频或远控；
            // 使用 DirectoryInfo.EnumerateFileSystemInfos 直接复用 FindNextFile 元数据，避免每项额外触发多次磁盘 stat。
            IEnumerable<KeyValuePair<SharedEntry,FileSystemInfo>> sources=snapshot.Parent==null
                ? roots.Select(root=>new KeyValuePair<SharedEntry,FileSystemInfo>(root,new DirectoryInfo(root.Path)))
                : new DirectoryInfo(snapshot.Parent.Path).EnumerateFileSystemInfos().Select(info=>new KeyValuePair<SharedEntry,FileSystemInfo>(new SharedEntry {Path=info.FullName,Root=snapshot.Parent.Root},info));
            Stopwatch scan=Stopwatch.StartNew();int characters=0;
            foreach(KeyValuePair<SharedEntry,FileSystemInfo> entry in sources)
            {
                SharedEntry source=entry.Key;FileSystemInfo info=entry.Value;
                CheckListing(session,snapshot.Revision);response.CheckConnection();
                if(!paging&&snapshot.Scanned>=1000)throw new ToolsHttp.HttpError(409,"此目录超过 1000 项，请使用支持分页的网页或共享更小的文件夹");
                if(snapshot.Scanned>=ListScanLimit||scan.ElapsedMilliseconds>=ListScanMilliseconds||characters>=4*1024*1024)
                {
                    if(!paging)throw new ToolsHttp.HttpError(409,"目录读取超过预算，请使用支持分页的网页");
                    snapshot.Truncated=true;break;
                }
                snapshot.Scanned++;characters+=source.Path.Length;
                try
                {
                    FileAttributes attributes=info.Attributes;
                    if((attributes&FileAttributes.ReparsePoint)!=0)continue;
                    ListItem item=ReadListItem(source,info);characters+=item.Name.Length;
                    if(!paging||item.NormalName.Contains(snapshot.Query))snapshot.Items.Add(item);
                }
                catch(FileNotFoundException){snapshot.Truncated=true;}
                catch(DirectoryNotFoundException){snapshot.Truncated=true;}
            }
            if(scan.ElapsedMilliseconds>=ListScanMilliseconds){if(!paging)throw new ToolsHttp.HttpError(409,"目录读取超过预算，请使用支持分页的网页");snapshot.Truncated=true;}
            CheckListing(session,snapshot.Revision);
        }
        internal static void CheckNoLinks(string path)
        {
            for (string part = path; !String.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new ToolsHttp.HttpError(403, "不共享符号链接或目录联接");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;
            internal uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
        }
        [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file,out HandleInfo information);
        private static void VerifyHandle(FileStream input,string root)
        {
            StringBuilder actual=new StringBuilder(32768);uint count=GetFinalPathNameByHandle(input.SafeFileHandle,actual,(uint)actual.Capacity,0);
            string resolved=actual.ToString();if(resolved.StartsWith("\\\\?\\UNC\\"))resolved="\\\\"+resolved.Substring(8);else if(resolved.StartsWith("\\\\?\\"))resolved=resolved.Substring(4);
            if(count==0||count>=actual.Capacity||!resolved.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new ToolsHttp.HttpError(403,"文件已离开共享目录");
        }
        private void CancelArchives(Func<ArchiveLease,bool> selected)
        {
            foreach(ArchiveLease archive in archives.Where(selected).ToArray())
            {
                archive.Cancelled=true;archive.Response.Abort();
                if(archive.Watcher!=null)archive.Watcher.Dispose();
                try {if(archive.Input!=null)archive.Input.Dispose();}
                catch(IOException error){Trace.WriteLine("共享归档关闭来源失败："+error.Message);}
            }
        }
        private void CheckArchive(ArchiveLease archive)
        {
            lock(gate)
            {
                if(archive.Cancelled||!Valid(archive.Session)||!Shared(archive.Target))throw new ToolsHttp.HttpError(401,"共享已关闭、目录已移除或设备信任已撤销");
                archive.Session.Last=clock.ElapsedMilliseconds;
            }
            archive.Response.CheckConnection();
        }
        private Stream OpenArchiveSource(ArchiveLease archive,ArchiveFile source)
        {
            CheckArchive(archive);CheckNoLinks(source.Item.Source.Path);
            FileStream input=new FileStream(source.Item.Source.Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan);
            try
            {
                lock(gate)
                {
                    if(archive.Cancelled||!Valid(archive.Session)||!Shared(archive.Target))throw new ToolsHttp.HttpError(401,"共享目录已移除");
                    archive.Input=input;
                }
                ValidateArchiveSource(archive,source,input);return input;
            }
            catch {input.Dispose();throw;}
        }
        private void ValidateArchiveSource(ArchiveLease archive,ArchiveFile source,Stream stream)
        {
            CheckArchive(archive);
            if(stream==null){using(Stream probe=OpenArchiveSource(archive,source)){}return;}
            string path=source.Item.Source.Path;CheckNoLinks(path);
            FileInfo info=new FileInfo(path);
            if(!info.Exists||info.Length!=source.Item.Size||info.LastWriteTimeUtc.Ticks!=source.Item.ModifiedTicks)throw new ToolsHttp.HttpError(409,"原文件已变化，请重新下载目录");
            FileStream input=(FileStream)stream;VerifyHandle(input,archive.Target.Root);
            HandleInfo handle;
            if(!GetFileInformationByHandle(input.SafeFileHandle,out handle))throw new IOException("无法复核共享文件句柄");
            long length=((long)handle.SizeHigh<<32)|handle.SizeLow;
            long written=((long)(uint)handle.Written.dwHighDateTime<<32)|(uint)handle.Written.dwLowDateTime;
            if((handle.Attributes&(uint)(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0||length!=source.Item.Size||written!=new DateTime(source.Item.ModifiedTicks,DateTimeKind.Utc).ToFileTimeUtc())throw new ToolsHttp.HttpError(409,"原文件已变化，请重新下载目录");
            if(source.HasIdentity&&(handle.Volume!=source.Identity.Volume||handle.IndexHigh!=source.Identity.IndexHigh||handle.IndexLow!=source.Identity.IndexLow))throw new ToolsHttp.HttpError(409,"原文件已被替换，请重新下载目录");
            source.Identity=handle;source.HasIdentity=true;
        }
        private List<ArchiveFile> ScanArchive(ArchiveLease archive)
        {
            List<ArchiveFile> files=new List<ArchiveFile>();Queue<ArchiveDirectory> pending=new Queue<ArchiveDirectory>();
            pending.Enqueue(new ArchiveDirectory {Path=archive.Target.Path,Relative="",Depth=0});
            int directories=1,scanned=0;Stopwatch scan=Stopwatch.StartNew();
            while(pending.Count>0)
            {
                ArchiveDirectory directory=pending.Dequeue();CheckArchive(archive);CheckNoLinks(directory.Path);
                foreach(FileSystemInfo info in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
                {
                    string path=info.FullName;
                    CheckArchive(archive);
                    if(++scanned>ArchiveScanLimit||scan.ElapsedMilliseconds>5000)throw new ToolsHttp.HttpError(409,"目录遍历超过预算，请分目录下载");
                    FileAttributes attributes=info.Attributes;
                    if((attributes&FileAttributes.ReparsePoint)!=0)throw new ToolsHttp.HttpError(403,"目录包含符号链接或目录联接，不能打包");
                    CheckNoLinks(path);
                    string relative=(directory.Relative.Length==0?"":directory.Relative+"/")+info.Name;
                    if(!Protocol.SafeRelativePath(relative))throw new ToolsHttp.HttpError(409,"目录中的文件路径不安全或过长，不能打包");
                    if((attributes&FileAttributes.Directory)!=0)
                    {
                        if(directory.Depth>=ArchiveDepthLimit||++directories>ArchiveDirectoryLimit)throw new ToolsHttp.HttpError(409,"目录层数或目录数量超过上限，请分目录下载");
                        pending.Enqueue(new ArchiveDirectory {Path=path,Relative=relative,Depth=directory.Depth+1});
                    }
                    else
                    {
                        if(files.Count>=StoreZipStream.MaxEntries)throw new ToolsHttp.HttpError(409,StoreZipStream.LimitMessage);
                        files.Add(new ArchiveFile {Item=ReadListItem(new SharedEntry {Path=path,Root=archive.Target.Root},info),Relative=relative});
                    }
                }
            }
            return files;
        }
        private void ArchiveResponse(ToolsHttp.Request request,ToolsHttp.Response response,Session session,SharedEntry target)
        {
            if(request.Parameter("id").Length==0||!Directory.Exists(target.Path))throw new ToolsHttp.HttpError(404,"请选择真实的共享目录");
            if(request.Header("Range").Length>0)throw new ToolsHttp.HttpError(416,"目录打包不支持续传，请重新下载");
            response.KeepAlive=false;
            ArchiveLease lease=new ArchiveLease {Target=target,Session=session,Response=response};
            CheckArchive(lease);
            lock(gate)
            {
                if(!Valid(session)||!Shared(target))throw new ToolsHttp.HttpError(401,"共享目录已移除");
                if(archives.Count(archive=>!archive.Cancelled)>=4||archives.Any(archive=>archive.Session==session&&!archive.Cancelled))throw new ToolsHttp.HttpError(409,"已有目录打包下载正在进行，请等它完成后重试");
                archives.Add(lease);
            }
            try
            {
                StoreZipStream writer;
                try
                {
                    List<ArchiveFile> files=ScanArchive(lease);
                    CheckNoLinks(target.Path);
                    // 先完整拒绝超限或链接目录，再启用变更观察，保证拒绝仍有明确 HTTP 状态。
                    FileSystemWatcher watcher=new FileSystemWatcher(target.Path) {IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size};
                    Action changed=()=>{lock(gate)CancelArchives(archive=>archive==lease);};
                    watcher.Changed+=(sender,args)=>changed();watcher.Created+=(sender,args)=>changed();watcher.Deleted+=(sender,args)=>changed();watcher.Renamed+=(sender,args)=>changed();watcher.Error+=(sender,args)=>changed();
                    lock(gate){if(lease.Cancelled){watcher.Dispose();throw new ToolsHttp.HttpError(401,"共享目录已移除");}lease.Watcher=watcher;}
                    watcher.EnableRaisingEvents=true;
                    writer=new StoreZipStream(files.Select(file=>new StoreZipStream.Entry(file.Relative,file.Item.Size,new DateTime(file.Item.ModifiedTicks,DateTimeKind.Utc),()=>OpenArchiveSource(lease,file),input=>ValidateArchiveSource(lease,file,input))));
                    writer.Prepare(()=>CheckArchive(lease));
                }
                catch(InvalidDataException error){throw new ToolsHttp.HttpError(409,error.Message);}
                catch(IOException){throw new ToolsHttp.HttpError(409,"原文件已变化或无法读取，请重新下载目录");}
                catch(UnauthorizedAccessException){throw new ToolsHttp.HttpError(409,"目录中的文件无法读取，请检查权限");}
                CheckArchive(lease);
                string encoded=Uri.EscapeDataString(FolderName(target.Path)+".zip").Replace("'","%27").Replace("(","%28").Replace(")","%29");
                response.Begin(200,"application/zip",writer.ContentLength,"Accept-Ranges: none\r\nContent-Disposition: attachment; filename*=UTF-8''"+encoded+"\r\n");
                if(request.Method!="HEAD")writer.Write((bytes,count)=>{CheckArchive(lease);response.Write(bytes,count);},()=>CheckArchive(lease));
            }
            finally {lock(gate){archives.Remove(lease);if(lease.Watcher!=null)lease.Watcher.Dispose();if(lease.Input!=null)lease.Input.Dispose();lease.Input=null;}}
        }
        private void FileResponse(ToolsHttp.Request request, ToolsHttp.Response response, Session session)
        {
            SharedEntry entry;lock(gate)entry=Resolve(request.Parameter("id"));string path=entry.Path;
            string archive=request.Parameter("archive");
            if(archive.Length>0){if(archive!="store")throw new ToolsHttp.HttpError(400,"不支持此目录打包格式");ArchiveResponse(request,response,session,entry);return;}
            if (Directory.Exists(path)) throw new ToolsHttp.HttpError(404, "请选择文件");
            CheckNoLinks(path);
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            {
                // 打开句柄后再次核对真实目标，防止检查与打开之间被换成指向目录外的链接。
                VerifyHandle(input,entry.Root);
                long length = input.Length; long[] range = ToolsHttp.Range(request.Header("Range"), length);
                if (range == null) { response.Bytes(new byte[0], "application/octet-stream", 416, "Content-Range: bytes */" + length + "\r\n"); return; }
                string extra = "Accept-Ranges: bytes\r\n";
                if (request.Header("Range").Length > 0) extra += "Content-Range: bytes " + range[0] + "-" + range[1] + "/" + length + "\r\n";
                if (request.Parameter("download") == "1") extra += "Content-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(Path.GetFileName(path)) + "\r\n";
                lock (gate) { if (!Valid(session)||!Shared(entry)) throw new ToolsHttp.HttpError(401, "共享已关闭或目录已移除"); }
                response.Begin(request.Header("Range").Length > 0 ? 206 : 200, Mime(path), Math.Max(0, range[1] - range[0] + 1), extra);
                if (request.Method == "HEAD") return;
                input.Position = range[0]; byte[] buffer = new byte[262144]; long left = range[1] - range[0] + 1;
                while (left > 0)
                {
                    lock (gate) { if (!Valid(session)||!Shared(entry)) return; session.Last = clock.ElapsedMilliseconds; }
                    int read = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length)); if (read == 0) throw new IOException("文件读取中断"); response.Write(buffer, read); left -= read;
                }
            }
        }
        internal static string Mime(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp4": case ".m4v": return "video/mp4"; case ".webm": return "video/webm"; case ".mkv": return "video/x-matroska";
                case ".mov": return "video/quicktime"; case ".mp3": return "audio/mpeg"; case ".ogg": return "audio/ogg";
                case ".wav":return "audio/wav";case ".flac":return "audio/flac";case ".m4a":return "audio/mp4";case ".aac":return "audio/aac";
                case ".png":return "image/png";case ".jpg":case ".jpeg":return "image/jpeg";case ".gif":return "image/gif";case ".webp":return "image/webp";case ".bmp":return "image/bmp";case ".svg":return "image/svg+xml";
                case ".pdf":return "application/pdf";
                case ".txt":case ".md":case ".json":case ".csv":case ".log":case ".xml":return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }
        // 应用退出或覆盖更新只结束会话；只有用户主动停止才修改保存的开关。
        public void Dispose() { if(security!=null)security.TrustRevoked-=OnTrustRevoked;lock (gate) { if (closed) return; closed = true; EndRemote(); EndSharing(); } http.Dispose(); }
    }
}
