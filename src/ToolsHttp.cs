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
            internal string Method, Path, Query, Address;
            internal readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal byte[] Body;
            internal string Header(string key) { string value; return Headers.TryGetValue(key, out value) ? value : ""; }
            internal string Parameter(string key)
            {
                foreach (string item in Query.Split('&')) { string[] pair = item.Split(new[] { '=' }, 2); if (pair[0] == key) return Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : ""); }
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
            private readonly bool keepAlive;
            private long remaining;
            internal bool Complete { get { return Started && remaining == 0; } }
            internal bool Started;
            internal Response(Stream stream, bool head, bool keepAlive = false) { this.stream = stream; this.head = head; this.keepAlive = keepAlive; }
            internal void Begin(int code, string type, long size, string extra)
            {
                string reason = code == 200 ? "OK" : code == 206 ? "Partial Content" : "Error";
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: " + type + "\r\nContent-Length: " + size.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: " + (keepAlive ? "keep-alive\r\nKeep-Alive: timeout=3, max=16" : "close") + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n" + extra + "\r\n");
                Started = true; remaining = head ? 0 : size; stream.Write(header, 0, header.Length);
            }
            internal void Bytes(byte[] bytes, string type, int code = 200, string extra = "") { Begin(code, type, bytes.LongLength, extra); Write(bytes, bytes.Length); }
            internal void Json(object value, string extra = "") { Bytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)), "application/json; charset=utf-8", 200, extra); }
            internal void Write(byte[] bytes, int count) { if (!head) { stream.Write(bytes, 0, count); remaining -= count; } }
        }
        internal sealed class HttpError : Exception
        {
            internal readonly int Code;
            internal HttpError(int code, string message) : base(message) { Code = code; }
        }
        private readonly TcpListener listener;
        private readonly Action<Request, Response> handler;
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly object gate = new object();
        private bool closed;
        internal readonly int Port;
        internal ToolsHttp(int port, bool loopback, Action<Request, Response> handler)
        {
            this.handler = handler; listener = new TcpListener(loopback ? IPAddress.Loopback : IPAddress.Any, port); listener.Start(16);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port; Task.Run((Action)Accept);
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
            try
            {
                client.ReceiveTimeout = 5000; client.SendTimeout = 10000; client.NoDelay = true; client.SendBufferSize = 262144;
                NetworkStream stream = client.GetStream();
                // 请求头逐字解析，但不再为每个字节调用一次 socket 读取。
                RequestInput deadline = new RequestInput(client);
                BufferedStream input = new BufferedStream(deadline, 8192);
                for (int index = 0; index < 16; index++)
                {
                    deadline.Begin(index == 0 ? 5000 : 3000);
                    Request request = Read(input);
                    bool reuse = index < 15 && request.Path == "/file" && (request.Method == "GET" || request.Method == "HEAD") && request.Header("Connection").Equals("keep-alive", StringComparison.OrdinalIgnoreCase);
                    response = new Response(stream, request.Method == "HEAD", reuse);
                    request.Address = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    string expected = client.Client.LocalEndPoint.ToString();
                    if (request.Header("Host") != expected) throw new HttpError(403, "请使用设备的 IPv4 地址访问");
                    string origin = request.Header("Origin");
                    if (origin.Length > 0 && origin != "http://" + expected) throw new HttpError(403, "不允许跨站请求");
                    handler(request, response);
                    if (!reuse || !response.Complete) break;
                    client.ReceiveTimeout = 3000;
                }
            }
            catch (Exception e)
            {
                if (response != null && !response.Started) try
                {
                    HttpError error = e as HttpError;
                    response.Bytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { error = error == null ? "操作失败：文件不可用或连接已关闭" : error.Message })), "application/json; charset=utf-8", error == null ? 500 : error.Code);
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
            finally { client.Close(); lock (gate) clients.Remove(client); }
        }
        private static Request Read(Stream stream)
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
            if (request.Header("Transfer-Encoding").Length > 0 || (request.Headers.ContainsKey("Content-Length") && !Int32.TryParse(request.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out length)) || length < 0 || length > 16384) throw new IOException("请求体过长或格式无效");
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
        public void Dispose() { lock (gate) { if (closed) return; closed = true; listener.Stop(); foreach (TcpClient client in clients) client.Close(); } }
    }
}
