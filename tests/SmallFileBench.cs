using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using FeiqLight;

public static class SmallFileBench
{
    private static string root;
    private static void Mark(string name) { File.WriteAllText(Path.Combine(root, name), "ready", new UTF8Encoding(false)); }
    private static void Wait(string name)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, name)))
        {
            if (File.Exists(Path.Combine(root, "failed"))) throw new IOException("对端基准失败，请查看日志。");
            if (deadline.Elapsed.TotalMinutes > 10) throw new IOException("等待基准边界超时：" + name);
            Thread.Sleep(5);
        }
    }
    private static void WaitStored(LanService node, int count)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        var field = typeof(LanService).GetField("offers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        object gate = typeof(LanService).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(node);
        while (true)
        {
            lock (gate)
            {
                var offers = (System.Collections.IDictionary)field.GetValue(node);
                int stored = 0, active = 0;
                foreach (object offer in offers.Values)
                {
                    Type type = offer.GetType();
                    if ((string)type.GetField("TransferState").GetValue(offer) == "对方已校验保存") stored++;
                    object clients = type.GetField("ActiveClients").GetValue(offer);
                    active += (int)clients.GetType().GetProperty("Count").GetValue(clients, null);
                }
                var dirty = typeof(LanService).GetField("unflushedOfferCompletions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (SmallFileBenchMetrics.StoredCount == count && active == 0 && (dirty == null || (int)dirty.GetValue(node) == 0))
                {
                    Console.WriteLine("STORED 唯一件数=" + SmallFileBenchMetrics.StoredCount + "，当前完成态=" + stored);
                    return;
                }
            }
            if (deadline.Elapsed.TotalMinutes > 2) throw new IOException("发送端没有收到全部 STORED：" + SmallFileBenchMetrics.StoredCount + "/" + count);
            Thread.Sleep(5);
        }
    }
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        root = args[2];
        try { Run(Int32.Parse(args[0]), Int32.Parse(args[1]), Int32.Parse(args[3]), Int32.Parse(args[4])); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Mark("failed"); return 1; }
    }
    private static void Run(int port, int androidPort, int count, int size)
    {
        string sourceRoot = Path.Combine(root, "ws"), receiveRoot = Path.Combine(root, "wr");
        Directory.CreateDirectory(sourceRoot); Directory.CreateDirectory(receiveRoot);
        var paths = new List<string>();
        for (int index = 0; index < count; index++)
        {
            byte[] bytes = new byte[size];
            for (int offset = 0; offset < size; offset++) bytes[offset] = (byte)(offset * 17 + index);
            string path = Path.Combine(sourceRoot, "Win-" + index.ToString("D4") + ".bin");
            File.WriteAllBytes(path, bytes); paths.Add(path);
        }
        var incoming = new ConcurrentQueue<IncomingMessage>();
        using (var node = new LanService(new AppSettings { Nickname = "Windows 基准", ReceiveFolder = receiveRoot }, port, true, "wp5-windows"))
        {
            node.ConfigureSecurity(Path.Combine(root, "wk"), (name, code) => true);
            node.MessageReceived += message => { if (message.Files.Count > 0) incoming.Enqueue(message); };
            node.Error += text => Console.Error.WriteLine("NOTICE " + text);
            node.Start(); Mark("windows-ready"); Wait("android-ready");
            Peer peer = node.Peers.Single(value => value.Endpoint.Port == androidPort);
            var sent = new List<ChatRecord>();
            for (int offset = 0; offset < paths.Count; offset += 20)
                sent.Add(node.SendMessage(peer, "小文件基准", paths.Skip(offset).Take(20).ToList()));
            Stopwatch delivery = Stopwatch.StartNew();
            while (sent.Any(record => record.State != "已送达"))
            {
                if (delivery.Elapsed.TotalMinutes > 2) throw new IOException("Windows 邀请未送达。");
                Thread.Sleep(5);
            }
            // 排空首次邀请产生的延迟同步，测量只覆盖传输阶段。
            Thread.Sleep(1500); Mark("windows-offers-ready"); Wait("wa-start");
            SmallFileBenchMetrics.Start(); Mark("wa-measuring"); Wait("wa-received"); WaitStored(node, count);
            SmallFileBenchMetrics.Write(Path.Combine(root, "wa-windows.json"), 0, new List<double>()); Mark("wa-finished");
            Console.WriteLine("PASS Windows→Android 全部 SHA-256 / STORED 完成");

            Wait("android-offers-ready");
            var files = new List<Tuple<long, Attachment>>();
            IncomingMessage incomingMessage;
            while (incoming.TryDequeue(out incomingMessage))
                files.AddRange(incomingMessage.Files.Select(file => Tuple.Create(incomingMessage.Record.Packet, file)));
            if (files.Count != count) throw new IOException("Android 邀请件数错误：" + files.Count);
            files = files.OrderBy(item => item.Item2.Name, StringComparer.Ordinal).ToList();
            Mark("aw-start"); Wait("aw-measuring"); SmallFileBenchMetrics.Start();
            Stopwatch total = Stopwatch.StartNew(); var latencies = new List<double>();
            foreach (var item in files)
            {
                Stopwatch timing = Stopwatch.StartNew();
                node.ReceiveFileAsync(peer, item.Item1, item.Item2, Path.Combine(receiveRoot, item.Item2.Name), null, CancellationToken.None).GetAwaiter().GetResult();
                latencies.Add(timing.Elapsed.TotalMilliseconds);
            }
            double elapsed = total.Elapsed.TotalMilliseconds;
            Mark("aw-received"); Wait("aw-finished");
            SmallFileBenchMetrics.Write(Path.Combine(root, "aw-windows.json"), elapsed, latencies);
            foreach (var item in files)
            {
                string path = Path.Combine(receiveRoot, item.Item2.Name);
                if (new FileInfo(path).Length != size || SecureIdentity.HashFile(path) != item.Item2.Sha256) throw new IOException("Windows 内容验证失败。");
            }
            if (Directory.GetFiles(receiveRoot).Length != count) throw new IOException("Windows 留下重复文件或断点。");
            Mark("windows-finished"); Console.WriteLine("PASS Android→Windows 只保存一份且无断点残留");
        }
    }
}
