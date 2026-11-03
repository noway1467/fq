using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class VideoGateBench
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static byte[] Request(int port, string path, string cookie = "", string data = null)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path); request.Proxy = null; request.Timeout = 10000; request.KeepAlive = false; request.Headers["Cookie"] = cookie;
        if (path.StartsWith("/file")) request.AddRange(40, 59);
        if (data != null) { request.Method = "POST"; request.ContentType = "application/json"; byte[] bytes = Encoding.UTF8.GetBytes(data); using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length); }
        using (WebResponse response = request.GetResponse()) using (Stream input = response.GetResponseStream()) using (MemoryStream output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
    }
    private static Dictionary<string, object> Data(byte[] bytes) { return Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes)); }
    private static int Main(string[] args)
    {
        Type type = Assembly.Load(File.ReadAllBytes(args[0])).GetType("FeiqLight.ToolsService"); const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object server = type.GetConstructor(flags, null, new[] { typeof(int), typeof(bool), typeof(string) }, null).Invoke(new object[] { 0, true, null });
        using ((IDisposable)server)
        using (ManualResetEvent entered = new ManualResetEvent(false))
        {
            string root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "video.mp4"), Enumerable.Range(0,256).Select(i=>(byte)i).ToArray());
            type.GetMethod("Share", flags).Invoke(server, new object[] { root }); int port = (int)type.GetProperty("Port", flags).GetValue(server, null);
            string shareCode = (string)type.GetProperty("ShareCode", flags).GetValue(server, null);
            string share = "fq_share=" + Data(Request(port,"/api/login","",Json.Serialize(new {mode="share",code=shareCode})))["token"];
            var items=((System.Collections.IEnumerable)Data(Request(port,"/api/list",share))["items"]).Cast<Dictionary<string,object>>(); string id=(string)items.Single(i=>(string)i["name"]=="video.mp4")["id"];
            type.GetField("ConfirmRemote", flags).SetValue(server, new Func<string,bool>(address=>true)); type.GetMethod("AllowRemote",flags).Invoke(server,null);
            string remoteCode=(string)type.GetProperty("RemoteCode",flags).GetValue(server,null),remote="fq_remote="+Data(Request(port,"/api/login","",Json.Serialize(new {mode="remote",code=remoteCode})))["token"];
            type.GetField("Capture",flags).SetValue(server,new Func<int,byte[]>(display=>{entered.Set();Thread.Sleep(600);return new byte[16];}));
            for(int i=0;i<3;i++){
                entered.Reset();Task<byte[]> capture=Task.Run(()=>Request(port,"/api/screen",remote));if(!entered.WaitOne(5000))throw new Exception("捕获未开始");
                Stopwatch clock=Stopwatch.StartNew();byte[] bytes=Request(port,"/file?id="+id,share);double ms=clock.Elapsed.TotalMilliseconds;
                if(!bytes.SequenceEqual(Enumerable.Range(40,20).Select(v=>(byte)v)))throw new Exception("Range 内容错误");capture.Wait();Console.WriteLine("RANGE_DURING_600MS_CAPTURE_MS="+ms.ToString("F1"));
            }
        }
        return 0;
    }
}
