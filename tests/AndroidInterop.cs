using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using FeiqLight;

public static class AndroidInteropHost
{
    private static readonly string UnicodeText=String.Concat(Enumerable.Repeat("𠮷 e\u0301 👩🏽‍💻 👨‍👩‍👧‍👦 🇨🇳 1️⃣ (๑•̀ㅂ•́)و✧\t\r\n",400));
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        long size = Int64.Parse(args[2]); byte[] bytes = new byte[65536]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 17 + 3);
        string source = Path.Combine(args[1], "Windows资料😀.bin");
        using (FileStream output = File.Create(source)) { for (long left = size; left > 0;) { int count = (int)Math.Min(left, bytes.Length); output.Write(bytes, 0, count); left -= count; } }
        Exception error = null; bool received = false; ManualResetEvent complete = new ManualResetEvent(false);
        using (LanService node = new LanService(new AppSettings { Nickname = "Windows 测试👩🏽‍💻", Group = "互通验证" }, Int32.Parse(args[0]), true, "windows-test"))
        {
            node.MessageReceived += async message =>
            {
                try
                {
                    if (message.Record.Text == "ANDROID_COMPLETE")
                    {
                        if (!received) throw new Exception("尚未收到安卓文件");
                        node.SendMessage(message.Peer, "WINDOWS_COMPLETE", null); complete.Set(); return;
                    }
                    if (message.Record.Text != "安卓→Windows："+UnicodeText || message.Files.Count != 2 || message.Files[0].Name!="安卓资料👩🏽‍💻.bin" || message.Peer.Nickname!="安卓测试😀") throw new Exception("安卓文本、昵称或附件解析失败");
                    string path = Path.Combine(args[1], "windows-received.bin");
                    long prefix = Math.Min(8L * 1024 * 1024, size / 2);
                    using (FileStream output = File.Create(LanService.PartialPath(message.Peer, message.Record.Packet, message.Files[0], path))) { for (long left = prefix; left > 0;) { int count = (int)Math.Min(left, bytes.Length); output.Write(bytes, 0, count); left -= count; } }
                    await node.ReceiveFileAsync(message.Peer, message.Record.Packet, message.Files[0], path, null, CancellationToken.None);
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                    using (FileStream expected = File.OpenRead(source))
                    using (FileStream actual = File.OpenRead(path))
                        if (actual.Length != size || !hash.ComputeHash(expected).SequenceEqual(hash.ComputeHash(actual))) throw new Exception("Android→Windows 文件不一致");
                    string second=Path.Combine(args[1],"windows-second.txt");
                    await node.ReceiveFileAsync(message.Peer,message.Record.Packet,message.Files[1],second,null,CancellationToken.None);
                    if(File.ReadAllText(second,Encoding.UTF8)!="多文件邀请验证 😀") throw new Exception("同条邀请第二个附件不一致");
                    received = true;
                    node.SendMessage(message.Peer, "Windows→安卓："+UnicodeText, new[] { source });
                }
                catch (Exception e) { error = e; complete.Set(); }
            };
            node.Start(); Console.WriteLine("READY");
            if (!complete.WaitOne(180000)) { Console.WriteLine("FAIL: 等待安卓超时"); return 1; }
            if (error != null) { Console.WriteLine(error); return 1; }
            Thread.Sleep(500); Console.WriteLine("WINDOWS INTEROP PASS"); return 0;
        }
    }
}
