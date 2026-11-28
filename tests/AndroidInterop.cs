using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using FeiqLight;

public static class AndroidInteropHost
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    private sealed class RateProgress : IProgress<TransferProgress>
    {
        public bool Observed;
        public void Report(TransferProgress value) { if(value.BytesPerSecond>0)Observed=true; Thread.Sleep(8); }
    }
    private static readonly string UnicodeText=String.Concat(Enumerable.Repeat("𠮷 e\u0301 👩🏽‍💻 👨‍👩‍👧‍👦 🇨🇳 1️⃣ (๑•̀ㅂ•́)و✧\t\r\n",400));
    public static int Main(string[] args)
    {
        // 测试失败写入日志和退出码，不让 Windows 错误对话框阻塞无人值守回归。
        SetErrorMode(0x0001 | 0x0002);
        try { return Run(args); }
        catch(Exception error) { Console.Error.WriteLine("INTEROP HOST FAIL: "+error.GetType().Name+": "+error.Message);Console.Error.WriteLine(error.StackTrace);return 1; }
    }
    private static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        long size = Int64.Parse(args[2]); byte[] bytes = new byte[65536]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 17 + 3);
        string source = Path.Combine(args[1], "Windows资料😀.bin");
        string avatarImage;
        using(var image = new System.Drawing.Bitmap(128,128)) using(var graphics = System.Drawing.Graphics.FromImage(image)) using(var encoded = new MemoryStream()) {
            graphics.Clear(System.Drawing.Color.Coral);image.Save(encoded,System.Drawing.Imaging.ImageFormat.Jpeg);File.WriteAllBytes(Path.Combine(args[1],"avatar.jpg"),encoded.ToArray());avatarImage="image:"+Convert.ToBase64String(encoded.ToArray());
        }
        using (FileStream output = File.Create(source)) { for (long left = size; left > 0;) { int count = (int)Math.Min(left, bytes.Length); output.Write(bytes, 0, count); left -= count; } }
        Exception error = null; bool received = false; ManualResetEvent complete = new ManualResetEvent(false);
        using (LanService node = new LanService(new AppSettings { Avatar = "builtin:15", Nickname = "Windows 测试👩🏽‍💻", Group = "互通验证", ReceiveFolder = Path.Combine(args[1], "received") }, Int32.Parse(args[0]), true, "windows-test"))
        {
            int outgoingRate=0;node.TransferChanged+=(record,id,percent,state,rate)=>{if(rate>0)Interlocked.Exchange(ref outgoingRate,1);};
            node.Error+=text=>Console.Error.WriteLine("NOTICE "+text);
            node.ConfigureSecurity(Path.Combine(args[1],"windows-security"),(name,code)=>true);
            node.MessageReceived += async message =>
            {
                try
                {
                    if (message.Record.Text == "0") { if(message.Peer.Avatar!="builtin:5")throw new Exception("Android 内置头像未同步");node.SendMessage(message.Peer, "IDLE_ECHO:0", null); return; }
                    if (message.Record.Text == "REQUEST_AVATAR_IMAGE") { node.Settings.Avatar=avatarImage;node.Refresh();node.SendMessage(message.Peer,"AVATAR_IMAGE_READY",null);return; }
                    if (message.Record.Text == "VERIFY_AVATAR_IMAGE") { if(message.Peer.Avatar!=avatarImage)throw new Exception("Android 图片头像未同步");node.SendMessage(message.Peer,"AVATAR_IMAGE_OK",null);return; }
                    if (message.Record.Text == "REQUEST_TEXT_FILE") { node.SendContent(message.Peer, "Windows 自动 TXT："+UnicodeText, null, args[1]); return; }
                    if (message.Record.Text == "ANDROID_TXT") {
                        if(message.Files.Count!=1)throw new Exception("缺少 Android TXT 附件");
                        string target=Path.Combine(args[1],"windows-long-text.txt");
                        await node.ReceiveFileAsync(message.Peer,message.Record.Packet,message.Files[0],target,null,CancellationToken.None);
                        if(File.ReadAllText(target,Encoding.UTF8)!="Android 自动 TXT："+UnicodeText)throw new Exception("Android TXT 原文不一致");
                        node.SendMessage(message.Peer,"ANDROID_TXT_OK",null);return;
                    }
                    if (message.Record.Text == "ANDROID_COMPLETE")
                    {
                        if (!received) throw new Exception("尚未收到安卓文件");
                        if(size>64L*1024*1024&&Interlocked.CompareExchange(ref outgoingRate,0,0)==0)throw new Exception("Windows 发送缺少实际速率");
                        node.SendMessage(message.Peer, "WINDOWS_COMPLETE", null); complete.Set(); return;
                    }
                    if (message.Record.Text != "安卓→Windows："+UnicodeText || message.Files.Count != 2 || message.Files[0].Name!="安卓资料👩🏽‍💻.bin" || message.Peer.Nickname!="安卓测试😀") throw new Exception("安卓文本、昵称或附件解析失败");
                    string path = Path.Combine(args[1], "windows-received.bin");
                    long prefix = Math.Min(8L * 1024 * 1024, size / 2);
                    using (FileStream output = File.Create(LanService.PartialPath(message.Peer, message.Record.Packet, message.Files[0], path))) { for (long left = prefix; left > 0;) { int count = (int)Math.Min(left, bytes.Length); output.Write(bytes, 0, count); left -= count; } }
                    RateProgress progress=new RateProgress();
                    await node.ReceiveFileAsync(message.Peer, message.Record.Packet, message.Files[0], path, null, CancellationToken.None, false, size>64L*1024*1024?progress:null);
                    if(size>64L*1024*1024) {if(!progress.Observed)throw new Exception("Windows 接收缺少实际速率");Console.WriteLine("WINDOWS_RATE PASS: 接收报告正速率，非零断点续传");}
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
            node.Start(); VerifyPlaintextBoundary(node); Console.WriteLine("READY");
            if (!complete.WaitOne(180000)) { Console.WriteLine("FAIL: 等待安卓超时"); return 1; }
            if (error != null) { Console.WriteLine(error); return 1; }
            Thread.Sleep(500); Console.WriteLine("WINDOWS INTEROP PASS"); return 0;
        }
    }
    internal static void VerifyPlaintextBoundary(LanService node)
    {
        var endpoint=new IPEndPoint(IPAddress.Loopback,node.Port);
        int deliveries=0;Action<ChatRecord> delivery=record=>Interlocked.Increment(ref deliveries);
        Action<IncomingMessage> incoming=message=>Interlocked.Increment(ref deliveries);
        node.DeliveryChanged+=delivery;node.MessageReceived+=incoming;
        try {
            var handle=typeof(LanService).GetMethod("Handle",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var pending=(System.Collections.IDictionary)typeof(LanService).GetField("pending",flags).GetValue(node);
            var itemType=typeof(LanService).GetNestedType("Pending",System.Reflection.BindingFlags.NonPublic);
            var item=Activator.CreateInstance(itemType,new object[]{new Peer{Endpoint=endpoint,Login="old-fixture"}});
            var waiting=new ChatRecord{Packet=1,State="等待确认"};itemType.GetField("Record").SetValue(item,waiting);
            lock(typeof(LanService).GetField("gate",flags).GetValue(node)){
                pending.Add(1L,item);
                try{
                    foreach(uint mode in new[]{Protocol.SendMessage,Protocol.ReceiveMessage,Protocol.ReleaseFiles})
                        handle.Invoke(node,new object[]{new Packet{Command=mode,Number=1,Body="1",Login="old-fixture",Host="test"},endpoint,false});
                    if(!pending.Contains(1L)||waiting.State!="等待确认")throw new Exception("明文 ACK 修改了待确认消息");
                }finally{pending.Remove(1L);}
            }
            if(deliveries!=0||node.Peers.Any(peer=>peer.Login=="old-fixture"))throw new Exception("明文业务进入消息或联系人状态");
            foreach(byte[] request in new[]{new byte[]{(byte)'1'},Protocol.Encode(1,"old-fixture","test",Protocol.GetFile,"1:0:0:",null)})using(var client=new TcpClient()) {
                client.Connect(endpoint);client.ReceiveTimeout=1500;
                NetworkStream stream=client.GetStream();stream.Write(request,0,request.Length);
                try {if(stream.ReadByte()!=-1)throw new Exception("明文文件入口返回了内容");}
                catch(IOException error){var socket=error.InnerException as SocketException;if(socket==null||socket.SocketErrorCode!=SocketError.ConnectionReset)throw;}
            }
        } finally {node.DeliveryChanged-=delivery;node.MessageReceived-=incoming;}
        Console.WriteLine("PASS Windows 拒绝明文 SENDMSG / ACK / RELEASE / GET_FILE");
    }
}
