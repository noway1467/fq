using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FeiqLight
{
    internal sealed class ToolsService : IDisposable
    {
        private sealed class Session { internal string Token, Address, Mode; internal long Last; }
        private readonly object gate = new object();
        private readonly object captureGate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<Session> sessions = new List<Session>();
        private readonly Dictionary<string, string> entries = new Dictionary<string, string>();
        private readonly ToolsHttp http;
        private readonly ToolsPairings pairings;
        private readonly ToolsPreferences preferences;
        private string[] folders = new string[0];
        private string shareCode = "", remoteCode = "";
        private long shareUntil, remoteUntil, attemptsSince;
        private int attempts, remoteEpoch, shareEpoch;
        private bool closed, remoteEnabled, remotePending;
        internal Func<string, bool> ConfirmRemote;
        internal Func<string, int> ConfirmRemoteDevice;
        internal Func<int, byte[]> Capture;
        internal Func<object> Displays;
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
        internal string SavedFolder { get { lock (gate) return preferences.Value.Folder; } }
        internal string[] SavedFolders { get { lock (gate) return preferences.Value.Folders.ToArray(); } }
        internal ToolsService(int port, bool loopback, string root = null) { pairings = new ToolsPairings(root); preferences = new ToolsPreferences(root); http = new ToolsHttp(port, loopback, Handle); }
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
        internal void StartSavedSharing() { lock (gate) StartSharing(preferences.Value.Folders, true); }
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
                // 移除必须立即撤销访问，即使配置保存失败，也不能继续暴露被移除的目录。
                EndSharing();
                preferences.SaveFolders(remaining, enabled, preferences.Value.RemoteEnabled);
                if (enabled) StartSharing(remaining, false);
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
                if (save) preferences.SaveFolders(full, true, preferences.Value.RemoteEnabled);
                EndSharing(); folders = full;
                shareCode = ConnectionCode(); shareUntil = clock.ElapsedMilliseconds + 600000;
            }
        }
        internal void StopSharing() { lock (gate) { EndSharing(); preferences.SaveFolders(preferences.Value.Folders, false, preferences.Value.RemoteEnabled); } }
        private void EndSharing() { shareEpoch++; folders = new string[0]; shareCode = ""; shareUntil = 0; entries.Clear(); sessions.RemoveAll(s => s.Mode == "share"); }
        internal void AllowRemote() { StartRemote(true); }
        private void StartRemote(bool save)
        {
            lock (gate) { if (closed) throw new ObjectDisposedException("ToolsService"); if (save) preferences.SaveFolders(preferences.Value.Folders, preferences.Value.ShareEnabled, true); EndRemote(); remoteEnabled = true; remoteCode = ConnectionCode(); remoteUntil = clock.ElapsedMilliseconds + 600000; }
        }
        internal void StopRemote() { lock (gate) { EndRemote(); preferences.SaveFolders(preferences.Value.Folders, preferences.Value.ShareEnabled, false); } }
        private void EndRemote() { remoteEpoch++; remoteEnabled = false; remotePending = false; remoteCode = ""; remoteUntil = 0; sessions.RemoveAll(s => s.Mode == "remote"); if (ReleaseInput != null) ReleaseInput(); }
        internal void ForgetPairings() { lock (gate) { pairings.Clear(); shareEpoch++; StopRemote(); sessions.Clear(); } }
        private string Scope(string mode)
        {
            if (mode == "remote") return "remote";
            if (folders.Length <= 1) return "share:" + (folders.FirstOrDefault() ?? "").ToUpperInvariant();
            // 配对绑定整个目录集合；追加目录不能让旧配对悄悄获得新目录权限。
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
            lock (gate)
            {
                Session session = sessions.FirstOrDefault(s => s.Mode == mode && s.Token == token && s.Address == request.Address);
                if (session == null || !Valid(session)) throw new ToolsHttp.HttpError(401, "连接已结束，请重新连接");
                if (request.Method == "POST" && request.Header("X-FQ-Token") != token) throw new ToolsHttp.HttpError(403, "请求校验失败");
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
                sessions.RemoveAll(s => !Valid(s));
                if (clock.ElapsedMilliseconds - attemptsSince >= 60000) { attemptsSince = clock.ElapsedMilliseconds; attempts = 0; }
                if (++attempts > 8) throw new ToolsHttp.HttpError(429, "尝试过多，请一分钟后重试");
                expected = mode == "share" ? shareCode : remoteCode;
                epoch = mode == "share" ? shareEpoch : remoteEpoch; scope = Scope(mode);
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
                    Session session = new Session { Mode = mode, Address = request.Address, Token = RandomToken(32), Last = clock.ElapsedMilliseconds };
                    sessions.Add(session); response.Json(new { token = session.Token, mode = mode }, "Set-Cookie: fq_" + mode + "=" + session.Token + "; Path=/; HttpOnly; SameSite=Strict\r\nSet-Cookie: " + PairCookie(mode) + "=" + remembered + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=7776000\r\n");
                }
            }
            finally { if (mode == "remote") lock (gate) { if (remoteEpoch == epoch) remotePending = false; } }
        }
        internal static string Text(Dictionary<string, object> data, string key)
        {
            object value; if (data == null || !data.TryGetValue(key, out value) || !(value is string)) throw new ToolsHttp.HttpError(400, "缺少请求字段"); return (string)value;
        }
        private void Handle(ToolsHttp.Request request, ToolsHttp.Response response)
        {
            if (request.Method != "GET" && request.Method != "HEAD" && request.Method != "POST") throw new ToolsHttp.HttpError(405, "不支持此操作");
            if (request.Method == "GET" && request.Path == "/api/status")
            {
                lock (gate)
                {
                    string sharePair = Cookie(request, PairCookie("share")), remotePair = Cookie(request, PairCookie("remote"));
                    bool sharePaired = !closed && folders.Length > 0 && pairings.Contains(sharePair, Scope("share"));
                    bool remotePaired = !closed && remoteEnabled && pairings.Contains(remotePair, Scope("remote"));
                    response.Json(new { remote = remoteEnabled, share = folders.Length > 0, paired = sharePaired, remotePaired = remotePaired, trusted = remotePaired && pairings.AlwaysAllows(remotePair, Scope("remote")), platform = "windows" });
                }
                return;
            }
            if (request.Method == "POST" && (request.Path == "/api/login" || request.Path == "/api/reconnect")) { Login(request, response, request.Path == "/api/reconnect"); return; }
            if (request.Method == "POST" && request.Path == "/api/logout")
            {
                string mode = Text(request.Json(), "mode"); if (mode != "share" && mode != "remote") throw new ToolsHttp.HttpError(400, "无效类型");
                Session session = Authorize(request, mode); lock (gate) { sessions.Remove(session); if (mode == "remote" && ReleaseInput != null) ReleaseInput(); } response.Json(new { ok = true }); return;
            }
            if (request.Path == "/api/input" && request.Method == "POST")
            {
                Session session = Authorize(request, "remote"); Dictionary<string, object> data = request.Json();
                lock (gate) { if (!Valid(session)) throw new ToolsHttp.HttpError(401, "控制已停止"); if (Input == null) throw new ToolsHttp.HttpError(503, "控制不可用"); Input(data); }
                response.Json(new { ok = true }); return;
            }
            if (request.Method == "POST") throw new ToolsHttp.HttpError(404, "没有此操作");
            if (request.Path == "/api/displays")
            {
                Authorize(request, "remote"); if (Displays == null) throw new ToolsHttp.HttpError(503, "屏幕列表不可用"); response.Json(Displays()); return;
            }
            if (request.Path == "/api/screen")
            {
                Session session = Authorize(request, "remote"); byte[] frame;
                int display; string selected = request.Parameter("display");
                if (selected.Length == 0) display = 0;
                else if (!Int32.TryParse(selected, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out display)) throw new ToolsHttp.HttpError(400, "屏幕编号无效");
                // JPEG 编码不能占用共享授权锁，否则同时播放的视频每段读取都要等截图。
                lock (captureGate)
                {
                    lock (gate) { if (!Valid(session) || Capture == null) throw new ToolsHttp.HttpError(401, "控制已停止"); }
                    frame = Capture(display);
                }
                lock (gate) { if (!Valid(session)) throw new ToolsHttp.HttpError(401, "控制已停止"); }
                response.Bytes(frame, "image/jpeg"); return;
            }
            if (request.Path == "/api/list")
            {
                Session session = Authorize(request, "share");
                lock (gate)
                {
                    if (!Valid(session)) throw new ToolsHttp.HttpError(401, "共享已关闭");
                    if (request.Parameter("id").Length == 0 && folders.Length > 1)
                    {
                        response.Json(new { name = "共享文件夹", items = folders.Select(p => new { id = Id(p), name = FolderName(p), dir = true, size = 0, seek = true }).ToArray() });
                        return;
                    }
                    string path = Resolve(request.Parameter("id")); CheckNoLinks(path);
                    if (!Directory.Exists(path)) throw new ToolsHttp.HttpError(404, "目录已不存在");
                    string[] children = Directory.EnumerateFileSystemEntries(path).Take(1001).ToArray();
                    if (children.Length > 1000) throw new ToolsHttp.HttpError(409, "此目录超过 1000 项，请共享更小的文件夹");
                    List<object> items = new List<object>();
                    foreach (string child in children)
                    {
                        FileAttributes attributes = File.GetAttributes(child); if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        bool directory = (attributes & FileAttributes.Directory) != 0; string id = Id(child);
                        items.Add(new { id = id, name = Path.GetFileName(child), dir = directory, size = directory ? 0 : new FileInfo(child).Length, seek = true });
                    }
                    response.Json(new { name = Path.GetFileName(path.TrimEnd('\\')), items = items });
                }
                return;
            }
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
        private string Resolve(string id)
        {
            if (folders.Length == 0) throw new ToolsHttp.HttpError(403, "共享已关闭");
            if (id.Length == 0 && folders.Length == 1) return folders[0];
            string path; if (!entries.TryGetValue(id, out path)) throw new ToolsHttp.HttpError(404, "文件索引已失效，请刷新目录"); return path;
        }
        internal static string FolderName(string path) { string name = Path.GetFileName(path.TrimEnd('\\')); return name.Length == 0 ? path : name; }
        private string Id(string path)
        {
            foreach (KeyValuePair<string, string> pair in entries) if (pair.Value == path) return pair.Key;
            if (entries.Count >= 10000) throw new ToolsHttp.HttpError(409, "浏览项已达上限，请重新开启共享");
            string id = RandomToken(16); entries.Add(id, path); return id;
        }
        internal static void CheckNoLinks(string path)
        {
            for (string part = path; !String.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new ToolsHttp.HttpError(403, "不共享符号链接或目录联接");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
        private void FileResponse(ToolsHttp.Request request, ToolsHttp.Response response, Session session)
        {
            string path; string[] roots;
            lock (gate) { path = Resolve(request.Parameter("id")); roots = folders.ToArray(); }
            if (Directory.Exists(path)) throw new ToolsHttp.HttpError(404, "请选择文件");
            CheckNoLinks(path);
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
            {
                // 打开句柄后再次核对真实目标，防止检查与打开之间被换成指向目录外的链接。
                StringBuilder actual = new StringBuilder(32768); uint count = GetFinalPathNameByHandle(input.SafeFileHandle, actual, (uint)actual.Capacity, 0);
                string resolved = actual.ToString(); if (resolved.StartsWith("\\\\?\\UNC\\")) resolved = "\\\\" + resolved.Substring(8); else if (resolved.StartsWith("\\\\?\\")) resolved = resolved.Substring(4);
                if (count == 0 || count >= actual.Capacity || !roots.Any(root => resolved.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) throw new ToolsHttp.HttpError(403, "文件已离开共享目录");
                long length = input.Length; long[] range = ToolsHttp.Range(request.Header("Range"), length);
                if (range == null) { response.Bytes(new byte[0], "application/octet-stream", 416, "Content-Range: bytes */" + length + "\r\n"); return; }
                string extra = "Accept-Ranges: bytes\r\n";
                if (request.Header("Range").Length > 0) extra += "Content-Range: bytes " + range[0] + "-" + range[1] + "/" + length + "\r\n";
                if (request.Parameter("download") == "1") extra += "Content-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(Path.GetFileName(path)) + "\r\n";
                lock (gate) { if (!Valid(session)) throw new ToolsHttp.HttpError(401, "共享已关闭"); }
                response.Begin(request.Header("Range").Length > 0 ? 206 : 200, Mime(path), Math.Max(0, range[1] - range[0] + 1), extra);
                if (request.Method == "HEAD") return;
                input.Position = range[0]; byte[] buffer = new byte[262144]; long left = range[1] - range[0] + 1;
                while (left > 0)
                {
                    lock (gate) { if (!Valid(session)) return; session.Last = clock.ElapsedMilliseconds; }
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
                default: return "application/octet-stream";
            }
        }
        // 应用退出或覆盖更新只结束会话；只有用户主动停止才修改保存的开关。
        public void Dispose() { lock (gate) { if (closed) return; closed = true; EndRemote(); EndSharing(); } http.Dispose(); }
    }
}
