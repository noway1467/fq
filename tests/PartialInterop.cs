using FeiqLight;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class PartialInterop
{
    private sealed class Incoming
    {
        internal IncomingMessage Message;
        internal string Partial, Saved;
        internal bool Canceled;
        internal readonly CancellationTokenSource Cancel = new CancellationTokenSource();
        internal readonly TaskCompletionSource<bool> Notification = new TaskCompletionSource<bool>();
        internal readonly TaskCompletionSource<bool> Control = new TaskCompletionSource<bool>();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    private static void InterruptStream(LanService node, long packet)
    {
        List<TcpClient> sockets;
        lock (Field(node, "gate"))
        {
            object offer = ((System.Collections.IDictionary)Field(node, "offers"))[packet + ":1"];
            Check(offer != null, "普通断网前报价已结束");
            sockets = ((IEnumerable<TcpClient>)offer.GetType().GetField("ActiveClients").GetValue(offer)).ToList();
        }
        Check(sockets.Count > 0, "普通断网前没有真实发送连接");
        foreach (TcpClient socket in sockets) socket.Close();
    }
    private static async Task<long> WaitForPartial(string part, Task download, long size)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 8000)
        {
            Check(!download.IsCompleted, "尚未截住中途状态，下载已经结束");
            if (File.Exists(part))
            {
                long bytes = new FileInfo(part).Length;
                if (bytes > 0 && bytes < size) return bytes;
            }
            await Task.Delay(1);
        }
        throw new Exception("活跃下载没有产生非空断点");
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
        AppSettings settings = new AppSettings { Nickname = "Windows 断点互通", Notifications = false, ReceiveFolder = root };
        using (LanService node = new LanService(settings, Int32.Parse(args[0]), true, "windows-partial"))
        {
            node.ConfigureSecurity(Path.Combine(root, "windows-security"), (name, code) => true);
            string small = Path.Combine(root, "windows-saved.txt"), large = Path.Combine(root, "windows-pending.bin");
            File.WriteAllText(small, "已保存正式文件");
            byte[] block = new byte[1024 * 1024]; new Random(71).NextBytes(block);
            using (FileStream file = File.Create(large)) for (int i = 0; i < 64; i++) file.Write(block, 0, block.Length);
            ConcurrentDictionary<long, Incoming> incoming = new ConcurrentDictionary<long, Incoming>();
            Exception failure = null; int passed = 0; ManualResetEventSlim complete = new ManualResetEventSlim(false);
            node.OfferCanceled += (peer, packet) => {
                try
                {
                    Incoming item;
                    Check(incoming.TryGetValue(packet, out item) && item.Message.Peer.Id == peer.Id, "取消通知匹配了错误身份或包号");
                    Check(File.Exists(item.Partial) && new FileInfo(item.Partial).Length > 0, "通知前没有非空断点");
                    item.Canceled = true; item.Cancel.Cancel(); item.Notification.TrySetResult(true);
                }
                catch (Exception e) { failure = e; complete.Set(); }
            };
            node.MessageReceived += async message => {
                try
                {
                    string body = message.Record.Text;
                    if (body == "WINDOWS_NOTIFY" || body == "WINDOWS_REVOKED" || body == "WINDOWS_INTERRUPTED") { node.SendMessage(message.Peer, body + "_OFFER", new[] { small, large }); return; }
                    if (body.StartsWith("CANCEL:", StringComparison.Ordinal) || body.StartsWith("CANCEL_SILENT:", StringComparison.Ordinal))
                    {
                        bool silent = body.StartsWith("CANCEL_SILENT:", StringComparison.Ordinal); long packet = Int64.Parse(body.Substring(silent ? 14 : 7));
                        if (!silent) Check(node.HasActiveOutgoingTransfer(message.Peer.Id, packet), "Windows 中途取消前发送流已结束");
                        Check(node.CancelOutgoing(message.Peer.Id, packet, "已送达", !silent), "Windows 报价未撤销");
                        node.SendMessage(message.Peer, (silent ? "SILENT_DONE:" : "CANCEL_DONE:") + packet, null); return;
                    }
                    if (body.StartsWith("INTERRUPT:", StringComparison.Ordinal))
                    {
                        long packet = Int64.Parse(body.Substring(10)); InterruptStream(node, packet);
                        node.SendMessage(message.Peer, "INTERRUPTED:" + packet, null); return;
                    }
                    if (body == "WINDOWS_NOTIFY_OK" || body == "WINDOWS_REVOKED_OK" || body == "WINDOWS_INTERRUPTED_OK") { Interlocked.Increment(ref passed); Console.WriteLine("PASS Windows→Android TLS " + body); return; }
                    if (body.StartsWith("SILENT_CANCELED:", StringComparison.Ordinal)) { incoming[Int64.Parse(body.Substring(16))].Control.TrySetResult(true); return; }
                    if (body.StartsWith("INTERRUPTED:", StringComparison.Ordinal)) { incoming[Int64.Parse(body.Substring(12))].Control.TrySetResult(true); return; }
                    if (body.Length == 0 && message.Files.Count == 2 && message.Files[1].Name.StartsWith("ANDROID_", StringComparison.Ordinal)) body = Path.GetFileNameWithoutExtension(message.Files[1].Name);
                    if (body == "ANDROID_NOTIFY" || body == "ANDROID_REVOKED" || body == "ANDROID_INTERRUPTED")
                    {
                        Check(message.Files.Count == 2, "Android 邀请缺少同批两份附件");
                        string saved = Path.Combine(root, body + "-saved.txt"), destination = Path.Combine(root, body + "-pending.bin");
                        await node.ReceiveFileAsync(message.Peer, message.Record.Packet, message.Files[0], saved, null, CancellationToken.None);
                        string part = LanService.PartialPath(message.Peer, message.Record.Packet, message.Files[1], destination);
                        Incoming item = new Incoming { Message = message, Saved = saved, Partial = part }; incoming[message.Record.Packet] = item;
                        if (body == "ANDROID_REVOKED")
                        {
                            File.WriteAllBytes(part, new byte[128]);
                            node.SendMessage(message.Peer, "PLEASE_CANCEL_SILENT:" + message.Record.Packet, null);
                            Task winner = await Task.WhenAny(item.Control.Task, Task.Delay(8000)); Check(winner == item.Control.Task, "Android 无通知撤报价未确认");
                            bool revoked = false; try { await node.ReceiveFileAsync(message.Peer, message.Record.Packet, message.Files[1], destination, null, CancellationToken.None); } catch (OfferRevokedException) { revoked = true; }
                            Check(revoked && !File.Exists(part) && File.ReadAllText(saved) == "Android 正式文件", "Android→Windows REVOKED 未清断点或误删正式文件");
                        }
                        else
                        {
                            Task<string> download = node.ReceiveFileAsync(message.Peer, message.Record.Packet, message.Files[1], destination, null, item.Cancel.Token);
                            long startedOffset = await WaitForPartial(part, download, message.Files[1].Size);
                            node.SendMessage(message.Peer, (body == "ANDROID_NOTIFY" ? "PLEASE_CANCEL:" : "PLEASE_INTERRUPT:") + message.Record.Packet, null);
                            bool interrupted = false;
                            try { await download; }
                            catch (OperationCanceledException) { Check(body == "ANDROID_NOTIFY", "普通断网被当成主动取消"); interrupted = true; }
                            catch (IOException error) { Check(!(error is OfferRevokedException), "普通流中断收到 REVOKED"); interrupted = true; }
                            Check(interrupted, "中途操作前下载已完成");
                            if (body == "ANDROID_NOTIFY")
                            {
                                Task winner = await Task.WhenAny(item.Notification.Task, Task.Delay(8000)); Check(winner == item.Notification.Task, "Android TLS 取消通知未到达");
                                // 发送流可能先于取消通知断开；下载释放句柄后按通知清理保留下来的断点。
                                ReceiveStorage.DeletePartialAndEmptyParents(root, part, null);
                                Check(item.Canceled && !File.Exists(part) && !File.Exists(destination), "活跃取消未终止下载、清断点或错误发布了半成品");
                            }
                            else
                            {
                                Task winner = await Task.WhenAny(item.Control.Task, Task.Delay(8000)); Check(winner == item.Control.Task, "Android 普通断网未确认");
                                Check(!item.Canceled && !item.Cancel.IsCancellationRequested && File.Exists(part), "普通断网误清断点或收到取消通知");
                                long offset = new FileInfo(part).Length; Check(offset > 0 && offset < message.Files[1].Size, "普通断网断点长度无效");
                                await node.ReceiveFileAsync(message.Peer, message.Record.Packet, message.Files[1], destination, null, CancellationToken.None);
                                Check(!File.Exists(part) && SecureIdentity.HashFile(destination) == message.Files[1].Sha256, "Android→Windows 续传 SHA-256 不一致");
                                Console.WriteLine("DETAIL Android→Windows 续传偏移=" + offset);
                            }
                            Check(File.ReadAllText(saved) == "Android 正式文件", "中途操作改写了同批正式文件");
                            Console.WriteLine("DETAIL Android→Windows 中途操作起点=" + startedOffset);
                        }
                        item.Cancel.Dispose();
                        Interlocked.Increment(ref passed); Console.WriteLine("PASS Android→Windows TLS " + body); node.SendMessage(message.Peer, body + "_OK", null);
                        return;
                    }
                    if (body == "DONE") { Check(Volatile.Read(ref passed) == 6, "双向六项互通没有全部完成"); node.SendMessage(message.Peer, "DONE_OK", null); complete.Set(); }
                }
                catch (Exception e) { failure = e; complete.Set(); }
            };
            node.Start(); Console.WriteLine("READY");
            if (!complete.Wait(110000)) { Console.WriteLine("FAIL 双端定向互通超时"); return 1; }
            if (failure != null) { Console.WriteLine("FAIL " + failure); return 1; }
            Thread.Sleep(300); Console.WriteLine("WINDOWS PARTIAL INTEROP: 6 passed, 0 failed"); return 0;
        }
    }
}
