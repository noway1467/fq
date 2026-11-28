using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FeiqLight
{
    // 独立端口，不改变旧版聊天和附件协议；不使用需要管理员 URL 保留的 HttpListener。
    internal sealed class ToolsHttp : IDisposable
    {
        internal sealed class Request
        {
            internal string Method, Path, Query, Address, Fingerprint;
            internal Action<string> RemoteAuthorized;
            internal void BindRemoteSession(string token) { RemoteAuthorized(token); }
            internal string PairScope(string scope){return Fingerprint==null?scope:scope+"|tls="+Fingerprint;}
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal byte[] Body;
            internal string Header(string key) { string value; return Headers.TryGetValue(key, out value) ? value : ""; }
            internal string Parameter(string key)
            {
                foreach (string item in Query.Split('&')) { string[] pair = item.Split(new[] { '=' }, 2); if (pair[0] == key) return Uri.UnescapeDataString((pair.Length == 2 ? pair[1] : "").Replace("+", " ")); }
                return "";
            }
            internal Dictionary<string, object> Json()
            {
                if (!Header("Content-Type").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) throw new HttpError(400, "请求格式错误");
                try { return new JavaScriptSerializer { MaxJsonLength = 16384, RecursionLimit = 8 }.Deserialize<Dictionary<string, object>>(new UTF8Encoding(false, true).GetString(Body)); }
                catch (Exception e) { if (!(e is ArgumentException || e is InvalidOperationException)) throw; throw new HttpError(400, "请求内容无效"); }
            }
        }
        internal sealed class Response
        {
            private readonly Stream stream;
            private readonly bool head;
            internal bool KeepAlive;
            private readonly Func<bool> connected;
            private readonly Action disconnect;
            private long remaining;
            private long lastProgress;
            internal bool ArchiveProgress;
            internal string RequestId = "";
            internal bool Complete { get { return Started && remaining == 0; } }
            internal bool Started;
            internal Response(Stream stream, bool head, bool keepAlive = false, Func<bool> connected = null, Action disconnect = null) { this.stream = stream; this.head = head; KeepAlive = keepAlive; this.connected = connected; this.disconnect = disconnect; }
            internal void CheckConnection() { if (connected != null && !connected()) throw new IOException("下载连接已关闭。"); }
            internal void Abort() { if (disconnect != null) disconnect(); }
            internal void Processing()
            {
                CheckConnection();
                if (Started || !ArchiveProgress) return;
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (lastProgress != 0 && now - lastProgress < System.Diagnostics.Stopwatch.Frequency) return;
                lastProgress = now;
                byte[] progress = Encoding.ASCII.GetBytes("HTTP/1.1 102 Processing\r\nX-FQ-Archive-Progress: 1\r\n\r\n");
                stream.Write(progress, 0, progress.Length); stream.Flush();
            }
            internal void Begin(int code, string type, long size, string extra)
            {
                if (Started || size < 0) throw new IOException("响应状态或长度无效。");
                if (code >= 300) KeepAlive = false;
                if (RequestId.Length > 0) extra += "X-FQ-Request-ID: " + RequestId + "\r\n";
                string reason = code == 200 ? "OK" : code == 206 ? "Partial Content" : "Error";
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + size.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: " + (KeepAlive ? "keep-alive\r\nKeep-Alive: timeout=3, max=16" : "close") + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n" + extra + "\r\n");
                Started = true; remaining = head ? 0 : size; stream.Write(header, 0, header.Length);
            }
            internal void Bytes(byte[] bytes, string type, int code = 200, string extra = "") { Begin(code, type, bytes.LongLength, extra); Write(bytes, bytes.Length); }
            internal void Json(object value, string extra = "") { Bytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)), "application/json; charset=utf-8", 200, extra); }
            internal void Write(byte[] bytes, int count)
            {
                if (count < 0 || count > bytes.Length || !Started || (!head && count > remaining)) throw new IOException("响应正文超出声明长度。");
                if (!head && count > 0) { stream.Write(bytes, 0, count); remaining -= count; }
            }
        }
        internal sealed class HttpError : Exception
        {
            internal readonly int Code;
            internal readonly string ErrorCode;
            internal HttpError(int code, string message, string errorCode = null) : base(message) { Code = code; ErrorCode = errorCode; }
        }
        private readonly TcpListener listener;
        private readonly Action<Request, Response> handler;
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly Dictionary<TcpClient, string> remoteClients = new Dictionary<TcpClient, string>();
        private readonly Dictionary<TcpClient, string> fingerprints = new Dictionary<TcpClient, string>();
        private readonly object gate = new object();
        private bool closed;
        private readonly SecureIdentity security;
        private readonly bool loopback;
        private readonly int bodyLimit;
        internal readonly int Port;
        internal ToolsHttp(int port, bool loopback, Action<Request, Response> handler, SecureIdentity security = null, int bodyLimit = 16384)
        {
            if(bodyLimit<0||bodyLimit>1048576)throw new ArgumentOutOfRangeException("bodyLimit");this.bodyLimit=bodyLimit;
            this.security=security;this.loopback=loopback;this.handler = handler; listener = new TcpListener(loopback ? IPAddress.Loopback : IPAddress.Any, port); listener.Start(16);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port; Task.Run((Action)Accept);
            if(security!=null){security.TrustRevoked+=OnTrustRevoked;security.RegisterReverseListener(Port,AcceptReverse);}
        }
        private void AcceptReverse(TcpClient client)
        {
            lock(gate){if(closed||clients.Count>=12){client.Close();return;}clients.Add(client);}
            Handle(client);
        }
        private void OnTrustRevoked(string fingerprint){DisconnectClients(fingerprint);}
        internal void DisconnectClients(){lock(gate)foreach(TcpClient client in clients)client.Close();}
        private void DisconnectClients(string fingerprint){lock(gate)foreach(KeyValuePair<TcpClient,string> client in fingerprints)if(client.Value==fingerprint)client.Key.Close();}
        internal void DisconnectRemoteClients(string token = null)
        {
            lock (gate) foreach (KeyValuePair<TcpClient, string> item in remoteClients)
                if (token == null || item.Value == token) item.Key.Close();
        }
        internal static int PortFor(int chatPort) { return chatPort == 65535 ? 65534 : chatPort + 1; }
        private void Accept()
        {
            try { while (true) { TcpClient client = listener.AcceptTcpClient(); lock (gate) { if (closed || clients.Count >= 12) { client.Close(); if (closed) return; continue; } clients.Add(client); } Task.Run(() => Handle(client)); } }
            catch (SocketException) { lock (gate) { if (!closed) throw; } }
            catch (ObjectDisposedException) { }
        }
        private void Handle(TcpClient client)
        {
            Response response = null;
            Stream transport = null;
            try
            {
                if(!loopback&&LanNetwork.Route(LanNetwork.Scan(),((IPEndPoint)client.Client.RemoteEndPoint).Address)==null)return;
                client.ReceiveTimeout = 5000; client.SendTimeout = 10000; client.NoDelay = true; client.SendBufferSize = 262144;
                Stream stream = transport = security==null?(Stream)client.GetStream():security.Server(client,true);
                string fingerprint=security==null?null:SecureIdentity.RemoteFingerprint((System.Net.Security.SslStream)stream);
                lock(gate)fingerprints[client]=fingerprint;
                if(security!=null&&!security.IsTrustedFingerprint(fingerprint))throw new IOException("请先从联系人核对设备身份。");
                // 请求头逐字解析，但不再为每个字节调用一次 socket 读取。
                RequestInput deadline = new RequestInput(client,stream);
                BufferedStream input = new BufferedStream(deadline, 8192);
                for (int index = 0; index < 16; index++)
                {
                    deadline.Begin(index == 0 ? 5000 : 3000);
                    Request request = Read(input,bodyLimit);
                    request.Fingerprint=fingerprint;
                    if(security!=null&&!security.IsTrustedFingerprint(fingerprint))throw new IOException("设备信任已撤销。");
                    bool requestedReuse = index < 15 && request.Header("Connection").Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
                    bool fileReuse = requestedReuse && request.Path == "/file" && (request.Method == "GET" || request.Method == "HEAD");
                    bool remoteReuse = requestedReuse && ((request.Path == "/api/screen" && request.Method == "GET") || (request.Path == "/api/input" && request.Method == "POST"));
                    Socket acceptedSocket=client.Client;
                    response = new Response(stream, request.Method == "HEAD", fileReuse, () => !acceptedSocket.Poll(0, SelectMode.SelectRead) || acceptedSocket.Available > 0, client.Close);
                    string requestId = request.Header("X-FQ-Request-ID");
                    if (requestId.Length > 0 && (requestId.Length != 32 || !System.Text.RegularExpressions.Regex.IsMatch(requestId, "\\A[0-9a-f]{32}\\z"))) throw new HttpError(400, "请求标识无效");
                    response.RequestId = requestId;
                    Response currentResponse = response;
                    request.RemoteAuthorized = token => {
                        // 业务授权锁先于连接登记锁，停止与登记不会留下漏关的连接。
                        lock (gate)
                        {
                            if (closed || !clients.Contains(client)) throw new IOException("连接已关闭。");
                            string previous;
                            if (remoteClients.TryGetValue(client, out previous) && previous != token) throw new HttpError(401, "远控会话已变更");
                            remoteClients[client] = token;
                            currentResponse.KeepAlive = remoteReuse;
                        }
                    };
                    request.Address = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    string expected = ((IPEndPoint)client.Client.LocalEndPoint).Address+":"+Port;
                    if (request.Header("Host") != expected) throw new HttpError(403, "请使用设备的 IPv4 地址访问");
                    string origin = request.Header("Origin");
                    if (origin.Length > 0 && origin != (security==null?"http://":"https://") + expected) throw new HttpError(403, "不允许跨站请求");
                    handler(request, response);
                    stream.Flush();
                    if (!response.KeepAlive || !response.Complete) break;
                    client.ReceiveTimeout = 3000;
                }
            }
            catch (Exception e)
            {
                if (response != null && !response.Started) try
                {
                    HttpError error = e as HttpError;
                    Dictionary<string,object> details=new Dictionary<string,object>{{"error",error==null?"操作失败：文件不可用或连接已关闭":error.Message}};
                    if(error!=null&&error.ErrorCode!=null)details["code"]=error.ErrorCode;
                    response.Bytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(details)), "application/json; charset=utf-8", error == null ? 500 : error.Code);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            finally { try { if (transport != null) transport.Dispose(); } finally { client.Close(); lock (gate) { clients.Remove(client); remoteClients.Remove(client); fingerprints.Remove(client); } } }
        }
        private static Request Read(Stream stream,int bodyLimit)
        {
            List<byte> bytes = new List<byte>(); int last = 0;
            while (bytes.Count < 16384)
            {
                int value = stream.ReadByte(); if (value < 0) throw new IOException("请求中断"); bytes.Add((byte)value);
                last = (last << 8) | value; if (last == 0x0d0a0d0a) break;
            }
            if (last != 0x0d0a0d0a) throw new IOException("请求头过长");
            string[] lines = Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || first[2] != "HTTP/1.1" || !first[1].StartsWith("/") || first[1].StartsWith("//")) throw new IOException("无效请求");
            string[] target = first[1].Split(new[] { '?' }, 2);
            Request request = new Request { Method = first[0], Path = target[0], Query = target.Length == 2 ? target[1] : "" };
            for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
            {
                int colon = lines[i].IndexOf(':'); if (colon <= 0) throw new IOException("无效请求头");
                string name = lines[i].Substring(0, colon); if (request.Headers.ContainsKey(name)) throw new IOException("重复请求头");
                request.Headers.Add(name, lines[i].Substring(colon + 1).Trim());
            }
            int length = 0;
            if (request.Header("Transfer-Encoding").Length > 0 || (request.Headers.ContainsKey("Content-Length") && !Int32.TryParse(request.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out length)) || length < 0 || length > bodyLimit) throw new IOException("请求体过长或格式无效");
            request.Body = new byte[length]; int offset = 0;
            while (offset < length) { int count = stream.Read(request.Body, offset, length - offset); if (count == 0) throw new IOException("请求中断"); offset += count; }
            return request;
        }
        internal static long[] Range(string header, long size)
        {
            if (header.Length == 0) return new[] { 0L, size - 1 };
            long start, end; string[] parts = header.StartsWith("bytes=", StringComparison.Ordinal) ? header.Substring(6).Split('-') : new string[0];
            if (parts.Length != 2 || size <= 0) return null;
            if (parts[0].Length == 0)
            {
                if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end <= 0) return null;
                return new[] { Math.Max(0, size - end), size - 1 };
            }
            if (!Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= size) return null;
            if (parts[1].Length == 0) end = size - 1;
            else if (!Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)) return null;
            return end < start ? null : new[] { start, Math.Min(end, size - 1) };
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                // 先注销本实例，再释放端口；旧实例重复关闭不能误删新实例的回连入口。
                if(security!=null){security.TrustRevoked-=OnTrustRevoked;security.UnregisterReverseListener(Port);}
                listener.Stop();
                foreach (TcpClient client in clients) client.Close();
            }
        }
    }
}
