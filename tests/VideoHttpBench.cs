using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using FeiqLight;

internal static class VideoHttpBench
{
    private static byte[] Get(int port, string path, string cookie, string body, string range, out double milliseconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        using (TcpClient socket = new TcpClient())
        {
            socket.Connect(IPAddress.Loopback, port); socket.ReceiveTimeout = 10000;
            byte[] data = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            string header = (body == null ? "GET " : "POST ") + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nCookie: " + cookie + "\r\nUser-Agent: video-benchmark\r\nConnection: close\r\n";
            if (range != null) header += "Range: " + range + "\r\n";
            if (body != null) header += "Content-Type: application/json\r\nContent-Length: " + data.Length + "\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header + "\r\n"); Stream stream = socket.GetStream(); stream.Write(head, 0, head.Length); stream.Write(data, 0, data.Length);
            using (MemoryStream result = new MemoryStream())
            {
                stream.CopyTo(result); byte[] all = result.ToArray(); int split = 0;
                while (split + 3 < all.Length && !(all[split] == 13 && all[split + 1] == 10 && all[split + 2] == 13 && all[split + 3] == 10)) split++;
                string response = Encoding.ASCII.GetString(all, 0, split);
                if (!response.StartsWith("HTTP/1.1 200") && !response.StartsWith("HTTP/1.1 206")) throw new Exception(response);
                milliseconds = watch.Elapsed.TotalMilliseconds; return all.Skip(split + 4).ToArray();
            }
        }
    }
    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); string file = Path.Combine(root, "stream.mp4");
        byte[] block = Enumerable.Range(0, 262144).Select(i => (byte)(i * 31)).ToArray();
        using (FileStream output = File.Create(file)) for (int i = 0; i < 256; i++) output.Write(block, 0, block.Length);
        JavaScriptSerializer json = new JavaScriptSerializer(); double duration;
        using (ToolsService server = new ToolsService(0, true))
        {
            server.Share(root);
            var login = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(Encoding.UTF8.GetString(Get(server.Port, "/api/login", "", "{\"mode\":\"share\",\"code\":\"" + server.ShareCode + "\"}", null, out duration)));
            string cookie = "fq_share=" + login["token"];
            var listing = json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(Encoding.UTF8.GetString(Get(server.Port, "/api/list", cookie, null, null, out duration)));
            var item = ((System.Collections.IEnumerable)listing["items"]).Cast<System.Collections.Generic.Dictionary<string, object>>().Single(i => (string)i["name"] == "stream.mp4"); string url = "/file?id=" + item["id"];
            double[] times = new double[101];
            for (int i = 0; i < times.Length; i++) { byte[] data = Get(server.Port, url, cookie, null, "bytes=8388608-8389631", out times[i]); if (data.Length != 1024 || !data.SequenceEqual(block.Take(1024))) throw new Exception("Range 内容错误"); }
            Array.Sort(times); Console.WriteLine("RANGE_1KiB_MEDIAN_MS=" + times[50].ToString("F3") + " P95_MS=" + times[95].ToString("F3"));
            for (int i = 0; i < 3; i++) { byte[] data = Get(server.Port, url, cookie, null, null, out duration); if (data.Length != 67108864 || data[12345] != block[12345]) throw new Exception("全文件读取错误"); Console.WriteLine("READ_64MiB_MS=" + duration.ToString("F1") + " MiB_PER_SEC=" + (64000 / duration).ToString("F1")); }
        }
        return 0;
    }
}
