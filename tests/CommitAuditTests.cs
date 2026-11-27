using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class CommitAuditTests
    {
        private static string root;
        private static int passed, failed;
        private static void Check(bool value, string reason) { if(!value) throw new Exception(reason); }
        private static object PrivateField(object target, string name) { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target); }
        private static System.Collections.Generic.IEnumerable<Control> All(Control parent)
        {
            foreach(Control child in parent.Controls) { yield return child; foreach(Control item in All(child)) yield return item; }
        }
        private static Button Button(Control parent, string text) { return All(parent).OfType<Button>().Single(b => b.Text.TrimStart('✓', ' ') == text); }
        private static void Appearance(SettingsDialog dialog) { All(dialog).OfType<Button>().Single(b => b.AccessibleName == "设置标签：外观").PerformClick(); Application.DoEvents(); }
        private static AppSettings Settings(string name) { return new AppSettings { Nickname = name, Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(root, "received") }; }
        private static void Await(Task task, int timeoutMs = 5000)
        {
            Stopwatch clock=Stopwatch.StartNew();
            while(!task.IsCompleted && clock.ElapsedMilliseconds<timeoutMs) { Application.DoEvents(); Thread.Sleep(5); }
            Check(task.IsCompleted,"异步操作没有结束");task.GetAwaiter().GetResult();Application.DoEvents();
        }
        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return port;
        }
        private static void Pump(Func<bool> done, string reason, int timeoutMs = 8000)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < timeoutMs) { Application.DoEvents(); Thread.Sleep(15); }
            Check(done(), reason);
        }
        private static bool DeleteWithConfirm(MainForm main, Peer peer, ChatRecord[] records)
        {
            bool confirmed = false;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                timer.Tick += delegate {
                    ConfirmDialog confirm = Application.OpenForms.OfType<ConfirmDialog>().FirstOrDefault();
                    if (confirm != null && !confirmed) { confirmed = true; Button(confirm, "删除").PerformClick(); }
                    NoticeDialog notice = Application.OpenForms.OfType<NoticeDialog>().FirstOrDefault();
                    if (notice != null) { Console.WriteLine("DETAIL 删除提示：" + notice.Text + " " + String.Join(" ", All(notice).OfType<TextBoxBase>().Select(box => box.Text))); Button(notice, "确定").PerformClick(); }
                };
                timer.Start();
                bool result = main.DeleteMessages(peer, records);
                return result && confirmed;
            }
        }
        private static bool DeleteBlockedByNotice(MainForm main, Peer peer, ChatRecord[] records)
        {
            bool noticed = false;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                timer.Tick += delegate {
                    NoticeDialog notice = Application.OpenForms.OfType<NoticeDialog>().FirstOrDefault();
                    if (notice != null) { noticed = true; timer.Stop(); Button(notice, "确定").PerformClick(); }
                    ConfirmDialog confirm = Application.OpenForms.OfType<ConfirmDialog>().FirstOrDefault();
                    if (confirm != null) { timer.Stop(); Button(confirm, "取消").PerformClick(); }
                };
                timer.Start();
                bool deleted = main.DeleteMessages(peer, records);
                return !deleted && noticed;
            }
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch(Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        private static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(value); }
        private static Task Receive(ChatForm chat, long packet, int fileId)
        {
            ListBox pending = (ListBox)Field(chat, "files");
            object item = pending.Items.Cast<object>().Single(value => (long)Field(value, "Packet") == packet && ((Attachment)Field(value, "File")).Id == fileId);
            return (Task)typeof(ChatForm).GetMethod("ReceiveAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chat, new[] { item, (object)true });
        }
        private static string WriteLargeFile(string folder, string name)
        {
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, name);
            byte[] bytes = new byte[1024 * 1024]; new Random(71).NextBytes(bytes);
            using (FileStream stream = File.Create(path)) for (int i = 0; i < 64; i++) stream.Write(bytes, 0, bytes.Length);
            return path;
        }
        private static Attachment AttachmentFrom(FileTransferState file) { return new Attachment { Id = file.Id, Name = file.Name, Size = file.Size, Modified = file.Modified, Sha256 = file.Sha256, RelativePath = file.RelativePath }; }
        private static string Partial(LanService receiver, Peer peer, ChatRecord record, int id)
        {
            Attachment file = AttachmentFrom(record.Transfers.Single(value => value.Id == id));
            return LanService.PartialPath(peer, record.Packet, file, SendBatch.DestinationPath(receiver.Settings.ReceiveFolder, file));
        }
        private sealed class ReceivePair : IDisposable
        {
            internal readonly LanService Sender, Receiver;
            internal readonly MainForm Main;
            internal readonly LocalStore Store;
            internal readonly Peer Target, Back;
            internal ReceivePair(string name, bool secure = true)
            {
                string folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
                Store = new LocalStore(Path.Combine(folder, "store"));
                AppSettings sendSettings = Settings("取消发送端"), receiveSettings = Settings("断点接收端");
                sendSettings.ReceiveFolder = Path.Combine(folder, "sender-received"); receiveSettings.ReceiveFolder = Path.Combine(folder, "received");
                Sender = new LanService(sendSettings, FreePort(), true, "partial-sender");
                Receiver = new LanService(receiveSettings, FreePort(), true, "partial-receiver");
                Main = new MainForm(Receiver, Store);
                if (secure) { Sender.ConfigureSecurity(Path.Combine(folder, "sender-security"), (peer, code) => true); Receiver.ConfigureSecurity(Path.Combine(folder, "receiver-security"), (peer, code) => true); }
                Sender.Start(); Receiver.Start(); Main.Show(); Main.NetworkStarted(); Sender.Probe(new IPEndPoint(IPAddress.Loopback, Receiver.Port));
                Pump(() => Sender.Peers.Any(peer => peer.Login == "partial-receiver") && Receiver.Peers.Any(peer => peer.Login == "partial-sender"), "断点测试双方未发现");
                Target = Sender.Peers.Single(peer => peer.Login == "partial-receiver"); Back = Receiver.Peers.Single(peer => peer.Login == "partial-sender");
                if (secure) Sender.VerifyPeer(Target);
                Main.OpenChat(Back);
            }
            internal ChatRecord Send(params string[] files)
            {
                ChatRecord outgoing = Sender.SendMessage(Target, "断点清理验证", files);
                Pump(() => Store.IncomingAttachments(Back.Id, outgoing.Packet).Count == 1 && outgoing.State == "已送达", "断点邀请未送达");
                return Store.IncomingAttachments(Back.Id, outgoing.Packet).Single();
            }
            internal ChatRecord Stored(long packet) { return Store.IncomingAttachments(Back.Id, packet).Single(); }
            public void Dispose() { Main.ExitApplication(); Main.Dispose(); Receiver.Dispose(); Sender.Dispose(); Application.DoEvents(); }
        }
        private static ChatRecord InterruptedRecord(Peer peer, long packet, string saved, string relative = null)
        {
            return new ChatRecord { PeerId = peer.Id, Packet = packet, Sender = peer.Nickname, Text = "中断邀请", Time = DateTime.Now, State = "已收到", HasAttachments = true,
                LocalFiles = new[] { saved }, Transfers = new[] {
                    new FileTransferState { Id = 0, Name = Path.GetFileName(saved), Size = 3, LocalPath = saved, Status = "已保存", Percent = 100 },
                    new FileTransferState { Id = 1, Name = "未完成.bin", Size = 4096, Modified = 123, Sha256 = new string('a', 64), RelativePath = relative, Status = "接收失败 · 点击续传", Session = "previous-session" }
                } };
        }
        private static void ClearWithConfirm(MainForm main, Peer peer, bool clearOnly)
        {
            bool confirmed = false;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                timer.Tick += delegate {
                    ConversationDialog dialog = Application.OpenForms.OfType<ConversationDialog>().FirstOrDefault();
                    if (dialog != null && !confirmed) { confirmed = true; All(dialog).OfType<ToggleSwitch>().Single().Checked = true; Button(dialog, "确认删除").PerformClick(); }
                    NoticeDialog notice = Application.OpenForms.OfType<NoticeDialog>().FirstOrDefault();
                    if (notice != null) { Console.WriteLine("DETAIL 清空提示：" + notice.Text + " " + String.Join(" ", All(notice).OfType<TextBoxBase>().Select(box => box.Text))); Button(notice, "确定").PerformClick(); }
                };
                timer.Start(); typeof(MainForm).GetMethod("DeleteConversation", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, new object[] { peer, clearOnly });
            }
            Check(confirmed, "清空会话确认未执行");
        }
        private static void PartialRegression()
        {
            string large = WriteLargeFile(Path.Combine(root, "partial-sources"), "large.bin"), small = Path.Combine(root, "partial-sources", "saved.txt");
            File.WriteAllText(small, "已保存文件必须保留", new UTF8Encoding(false));
            Run("0.7.19 发送方中途取消：TLS 与明文即时终止、清断点并保留同批已保存文件", delegate {
                foreach (bool secure in new[] { true, false }) using (ReceivePair pair = new ReceivePair("cancel-live-" + secure, secure))
                {
                    ChatRecord record = pair.Send(small, large);
                    Await(Receive(pair.Main.ActiveChat, record.Packet, 0)); string saved = pair.Stored(record.Packet).Transfers[0].LocalPath;
                    string part = Partial(pair.Receiver, pair.Back, record, 1);
                    // 回环可在一次 UI 消息泵或一次进度采样间隔内收完；独立观察真实落盘数据后立即取消。
                    Task<long> canceled = Task.Run(() => {
                        Stopwatch clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < 8000) {
                            if (File.Exists(part)) {
                                long bytes = new FileInfo(part).Length;
                                if (bytes > 0 && bytes < record.Transfers[1].Size) {
                                    Check(pair.Sender.HasActiveOutgoingTransfer(pair.Target.Id, record.Packet), "取消前没有真实发送流");
                                    Check(pair.Sender.CancelOutgoing(pair.Target.Id, record.Packet, "已送达", true), "取消失败");
                                    return bytes;
                                }
                            }
                            Thread.Sleep(1);
                        }
                        throw new Exception("取消前没有非空活跃断点");
                    });
                    Task download = Receive(pair.Main.ActiveChat, record.Packet, 1);
                    Await(canceled, 10000); Await(download, 15000);
                    Console.WriteLine("DETAIL " + (secure ? "TLS" : "明文") + " 中途取消偏移=" + canceled.Result);
                    Pump(() => !File.Exists(part) && pair.Main.ActiveChat.PendingFileCount == 0 && pair.Stored(record.Packet).Transfers[1].Status == "对方已取消发送", "中途取消没有收敛");
                    Check(pair.Stored(record.Packet).State == "对方已取消发送", "消息仍提示续传");
                    string footer = ((Label)Field(pair.Main, "status")).Text;
                    Console.WriteLine("DETAIL 取消完成主窗口提示=" + footer);
                    Check(!footer.Contains("接收失败") && !footer.Contains("保留断点"), "取消后主窗口仍提示接收失败或保留断点：" + footer);
                    Check(File.ReadAllBytes(saved).SequenceEqual(File.ReadAllBytes(small)) && pair.Stored(record.Packet).Transfers[0].Status == "已保存", "取消误删或改写已保存附件");
                    Check(!Application.OpenForms.OfType<NoticeDialog>().Any(), "取消后台接收弹了错误窗口");
                    using (Bitmap image = new Bitmap(pair.Main.Width, pair.Main.Height)) { pair.Main.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "partial-canceled-" + secure + ".png")); }
                }
            });
            Run("0.7.19 通知丢失后续传读取 REVOKED、删除断点且重启不复活待接收项", delegate {
                using (ReceivePair pair = new ReceivePair("cancel-revoked"))
                {
                    ChatRecord record = pair.Send(small, large); Await(Receive(pair.Main.ActiveChat, record.Packet, 0));
                    string saved = pair.Stored(record.Packet).Transfers[0].LocalPath, part = Partial(pair.Receiver, pair.Back, record, 1);
                    File.WriteAllBytes(part, new byte[4096]); Check(pair.Sender.CancelOutgoing(pair.Target.Id, record.Packet, "已送达", false), "无通知撤报价失败");
                    Await(Receive(pair.Main.ActiveChat, record.Packet, 1), 15000);
                    Check(!File.Exists(part) && pair.Stored(record.Packet).Transfers[1].Status == "对方已取消发送" && pair.Main.ActiveChat.PendingFileCount == 0, "REVOKED 未清理断点或待接收队列");
                    Check(File.Exists(saved) && pair.Stored(record.Packet).Transfers[0].Status == "已保存", "REVOKED 误改已保存附件");
                    pair.Main.ActiveChat.Dispose(); pair.Main.OpenChat(pair.Back); Check(pair.Main.ActiveChat.PendingFileCount == 0, "重建会话复活已取消邀请");
                    Check(!Application.OpenForms.OfType<NoticeDialog>().Any(), "REVOKED 弹了错误窗口");
                }
            });
            Run("0.7.19 普通连接中断保留非空断点，并从原偏移续传通过 SHA-256", delegate {
                using (ReceivePair pair = new ReceivePair("ordinary-interruption"))
                {
                    ChatRecord record = pair.Send(large); string part = Partial(pair.Receiver, pair.Back, record, 0);
                    Task interrupted = Receive(pair.Main.ActiveChat, record.Packet, 0);
                    Pump(() => File.Exists(part) && new FileInfo(part).Length > 0 && !interrupted.IsCompleted, "断网前没有非空断点");
                    object gate = Field(pair.Sender, "gate"); List<TcpClient> active;
                    lock (gate) { object offer = ((System.Collections.IDictionary)Field(pair.Sender, "offers"))[record.Packet + ":0"]; active = ((IEnumerable<TcpClient>)Field(offer, "ActiveClients")).ToList(); }
                    Check(active.Count > 0, "没有可中断的真实发送连接"); foreach (TcpClient socket in active) socket.Close();
                    Await(interrupted, 15000); Check(File.Exists(part) && new FileInfo(part).Length > 0 && new FileInfo(part).Length < new FileInfo(large).Length, "普通断网误删断点或测试已收完");
                    long offset = new FileInfo(part).Length; Check(pair.Stored(record.Packet).Transfers[0].Status == "接收失败 · 点击续传", "普通断网被当成取消");
                    Await(Receive(pair.Main.ActiveChat, record.Packet, 0), 20000); string saved = pair.Stored(record.Packet).Transfers[0].LocalPath;
                    Check(!File.Exists(part) && SecureIdentity.HashFile(saved) == SecureIdentity.HashFile(large), "续传 SHA-256 不一致"); Console.WriteLine("DETAIL 普通中断续传偏移=" + offset);
                }
            });
            Run("0.7.19 重启恢复后拒绝与明文断网拒绝先本地清断点及空目录", delegate {
                foreach (bool plain in new[] { false, true })
                {
                    string folder = Path.Combine(root, "reject-" + plain); Directory.CreateDirectory(folder); string saved = Path.Combine(folder, "saved.bin"); File.WriteAllBytes(saved, new byte[] { 1, 2, 3 });
                    AppSettings settings = Settings("拒收"); settings.ReceiveFolder = Path.Combine(folder, "received");
                    Peer peer = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, FreePort()), Login = "offline", Host = "host", Nickname = "离线发送方", DeviceId = "11111111111111111111111111111111" };
                    LocalStore store = new LocalStore(Path.Combine(folder, "store")); ChatRecord record = InterruptedRecord(peer, 9001, saved, "文件夹/空目录/未完成.bin");
                    FileTransferState nonempty = new FileTransferState { Id = 2, Name = "第二个.bin", Size = 4096, Modified = 123, Sha256 = new string('b', 64), RelativePath = "文件夹/保留目录/第二个.bin", Status = "接收失败 · 点击续传" };
                    record.Transfers = record.Transfers.Concat(new[] { nonempty }).ToArray(); store.Append(record);
                    using (LanService receiver = new LanService(settings, FreePort(), true, "reject-receiver")) using (MainForm main = new MainForm(receiver, new LocalStore(store.Root)))
                    {
                        if (!plain) receiver.ConfigureSecurity(Path.Combine(folder, "security"), (name, code) => false);
                        foreach (FileTransferState file in record.Transfers.Where(file => file.LocalPath == null)) { string destination = SendBatch.Destination(settings.ReceiveFolder, AttachmentFrom(file)); File.WriteAllBytes(LanService.PartialPath(peer, record.Packet, AttachmentFrom(file), destination), new byte[128]); }
                        string emptyPart = Partial(receiver, peer, record, 1), keepPart = Partial(receiver, peer, record, 2), keptDirectory = Path.GetDirectoryName(keepPart);
                        File.WriteAllText(Path.Combine(keptDirectory, "保留.txt"), "保留"); main.Show(); main.OpenChat(peer);
                        Check(main.ActiveChat.PendingFileCount == 2, "重启未恢复未完成附件");
                        Check(((ICollection<string>)Field(((ListBox)Field(main.ActiveChat, "files")).Items[0], "PartialPaths")).Count == 0, "重启测试错误地预填了内存路径");
                        Await(main.RejectTransfer(record)); Application.DoEvents();
                        ChatRecord result = store.IncomingAttachments(peer.Id, record.Packet).Single();
                        Check(!File.Exists(emptyPart) && !File.Exists(keepPart) && !Directory.Exists(Path.GetDirectoryName(emptyPart)), "拒绝后残留断点或空子目录");
                        Check(Directory.Exists(settings.ReceiveFolder) && File.Exists(Path.Combine(keptDirectory, "保留.txt")) && File.Exists(saved), "拒绝误删非空目录或已保存文件");
                        Check(result.State == record.State && result.Transfers[1].Status == "已拒绝" && result.Transfers[0].Status == "已保存" && main.ActiveChat.PendingFileCount == 0, "拒收未本地持久化或覆盖同批已保存记录");
                        main.ExitApplication();
                    }
                }
                Attachment absent = new Attachment { Name = "不存在.bin", RelativePath = "没有创建/子目录/不存在.bin" }; string absentRoot = Path.Combine(root, "pure-destination");
                SendBatch.DestinationPath(absentRoot, absent); Check(!Directory.Exists(absentRoot), "纯路径计算创建了目录");
            });
            Run("0.7.19 删除入站消息与清空全部历史清断点，保留正式文件与其他会话", delegate {
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    string folder = Path.Combine(root, "delete-partials-" + mode); Directory.CreateDirectory(folder); string saved = Path.Combine(folder, "saved.bin"); File.WriteAllBytes(saved, new byte[] { 1, 2, 3 });
                    AppSettings settings = Settings("历史断点"); settings.ReceiveFolder = Path.Combine(folder, "received");
                    Peer peer = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, FreePort()), Login = "history", Host = "host", Nickname = "历史联系人" }; LocalStore store = new LocalStore(Path.Combine(folder, "store"));
                    ChatRecord record = InterruptedRecord(peer, 100, saved, "旧目录/子目录/未完成.bin"); store.Append(record);
                    for (int i = 0; i < 250; i++) store.Append(new ChatRecord { PeerId = peer.Id, Packet = 1000 + i, Text = new string('x', 18000), Time = DateTime.Now, State = "已收到" });
                    Check(!store.History(peer.Id, 150).Any(value => value.Packet == record.Packet), "历史清理测试没有越过读取页/尾部上限");
                    using (LanService receiver = new LanService(settings, FreePort(), true, "history-receiver")) using (MainForm main = new MainForm(receiver, store))
                    {
                        string destination = SendBatch.Destination(settings.ReceiveFolder, AttachmentFrom(record.Transfers[1])), part = Partial(receiver, peer, record, 1); File.WriteAllBytes(part, new byte[128]);
                        string other = Path.Combine(settings.ReceiveFolder, ".feiq-" + new string('c', 64) + ".part"); File.WriteAllBytes(other, new byte[128]); main.Show();
                        if (mode == 0) Check(DeleteWithConfirm(main, peer, new[] { record }), "删除中断入站消息失败"); else ClearWithConfirm(main, peer, mode == 1);
                        Check(!File.Exists(part) && !Directory.Exists(Path.GetDirectoryName(destination)), "历史删除未清理旧断点和空目录");
                        Check(File.Exists(saved) && File.Exists(other), "历史删除误删正式文件或其他断点");
                        Check(!store.IncomingAttachments(peer.Id).Any(value => value.Packet == record.Packet), "历史记录未删除"); main.ExitApplication();
                    }
                }
            });
            Run("0.7.19 取消鉴权、后台会话、数字拒绝兼容及过期断点命名/占用边界", delegate {
                using (ReceivePair pair = new ReceivePair("cancel-security"))
                {
                    ChatRecord record = pair.Send(small, large); Await(Receive(pair.Main.ActiveChat, record.Packet, 0)); string saved = pair.Stored(record.Packet).Transfers[0].LocalPath, part = Partial(pair.Receiver, pair.Back, record, 1); File.WriteAllBytes(part, new byte[128]);
                    bool barrier = false; pair.Receiver.MessageReceived += message => { if (message.Record.Text == "cancel-barrier") barrier = true; };
                    using (UdpClient attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    {
                        foreach (string fingerprint in new[] { pair.Sender.Security.Fingerprint, null })
                        {
                            byte[] bytes = Protocol.Encode(81001, pair.Sender.Login, pair.Sender.Host, Protocol.ReleaseFiles | Protocol.Utf8, "cancel:" + record.Packet, null, pair.Sender.Settings.DeviceId, null, fingerprint);
                            attacker.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, pair.Receiver.Port));
                        }
                        byte[] stripped = Protocol.Encode(81002, pair.Sender.Login, pair.Sender.Host, Protocol.ReleaseFiles | Protocol.Utf8, "cancel:" + record.Packet, null);
                        attacker.Send(stripped, stripped.Length, new IPEndPoint(IPAddress.Loopback, pair.Receiver.Port));
                        byte[] marker = Protocol.Encode(81003, "attacker", "host", Protocol.SendMessage | Protocol.Utf8, "cancel-barrier", null, "33333333333333333333333333333333");
                        attacker.Send(marker, marker.Length, new IPEndPoint(IPAddress.Loopback, pair.Receiver.Port)); Pump(() => barrier, "伪造取消报文没有被完整处理");
                    }
                    Check(File.Exists(part) && pair.Stored(record.Packet).Transfers[1].Status != "对方已取消发送", "伪造明文取消破坏了 TLS 会话");
                    AppSettings attackSettings = Settings("冒用证书"); attackSettings.DeviceId = pair.Sender.Settings.DeviceId;
                    using (LanService attacker = new LanService(attackSettings, FreePort(), true, pair.Sender.Login))
                    {
                        attacker.ConfigureSecurity(Path.Combine(root, "impostor-security"), (name, code) => true); attacker.Start();
                        Peer target = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, pair.Receiver.Port), DeviceId = pair.Receiver.Settings.DeviceId, Fingerprint = pair.Receiver.Security.Fingerprint, Login = pair.Receiver.Login, Nickname = "接收方" };
                        bool blocked = false; try { typeof(LanService).GetMethod("SendReleaseFiles", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(attacker, new object[] { target, "cancel:" + record.Packet }); }
                        catch (TargetInvocationException e) { Check(e.InnerException is IOException || e.InnerException is System.Security.Authentication.AuthenticationException, "冒用验证因无关错误失败：" + e.InnerException); blocked = true; }
                        Check(blocked && pair.Receiver.Security.IsTrusted(pair.Sender.Settings.DeviceId, pair.Sender.Security.Fingerprint), "冒用证书覆盖了原配对");
                    }
                    Check(File.Exists(part) && pair.Stored(record.Packet).Transfers[1].Status != "对方已取消发送", "冒用证书篡改了状态");
                    pair.Main.ActiveChat.Dispose(); Check(pair.Sender.CancelOutgoing(pair.Target.Id, record.Packet, "已送达", true), "后台会话取消失败");
                    Pump(() => !File.Exists(part) && pair.Stored(record.Packet).Transfers[1].Status == "对方已取消发送", "会话未打开时取消没有清理持久化历史"); Check(File.Exists(saved) && pair.Stored(record.Packet).Transfers[0].Status == "已保存", "后台取消误删已保存文件");
                    ChatRecord refused = pair.Send(large); pair.Receiver.RejectFiles(pair.Back, refused.Packet);
                    Pump(() => pair.Sender.OutgoingRecord(pair.Target.Id, refused.Packet) == null, "纯数字拒绝通知未撤销报价");
                    long legacyNumber; Check(!Int64.TryParse("cancel:" + refused.Packet, out legacyNumber), "旧版数字分支会误解析取消编码");
                    string owned = Partial(pair.Receiver, pair.Back, refused, 0); File.WriteAllBytes(owned, new byte[128]);
                    using (FileStream owner = new FileStream(owned, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        bool failedOpen = false; try { Await(pair.Receiver.ReceiveFileAsync(pair.Back, refused.Packet, AttachmentFrom(refused.Transfers[0]), SendBatch.DestinationPath(pair.Receiver.Settings.ReceiveFolder, AttachmentFrom(refused.Transfers[0])), null, CancellationToken.None)); } catch (IOException) { failedOpen = true; }
                        Check(failedOpen && File.Exists(owned) && owner.Length == 128, "未取得断点所有权却清理了其他任务文件");
                    }
                }
                string folder = Path.Combine(root, "expired-partials"); Directory.CreateDirectory(Path.Combine(folder, "nested")); DateTime now = DateTime.UtcNow;
                string expired = Path.Combine(folder, "nested", ".feiq-" + new string('a', 64) + ".part"), fresh = Path.Combine(folder, ".feiq-" + new string('b', 64) + ".part"), busy = Path.Combine(folder, ".feiq-" + new string('c', 64) + ".part"), ordinary = Path.Combine(folder, "ordinary.part"), invalid = Path.Combine(folder, ".feiq-" + new string('g', 64) + ".part");
                foreach (string path in new[] { expired, fresh, busy, ordinary, invalid }) { File.WriteAllBytes(path, new byte[128]); File.SetLastWriteTimeUtc(path, now.AddHours(-25)); } File.SetLastWriteTimeUtc(fresh, now.AddHours(-23));
                using (FileStream owner = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(ReceiveStorage.CleanExpiredPartials(folder, now) == 1, "过期清理数量不正确");
                Check(!File.Exists(expired) && new[] { fresh, busy, ordinary, invalid }.All(File.Exists), "过期清理误删新断点、占用断点或普通文件");
                string outside = Path.Combine(root, "outside.part"); File.WriteAllBytes(outside, new byte[128]); bool escaped = false;
                try { ReceiveStorage.DeletePartialAndEmptyParents(folder, outside, "nested/file.bin"); } catch (IOException) { escaped = true; } Check(escaped && File.Exists(outside), "清理越过接收根目录");
            });
        }
        [STAThread] public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs eventArgs){
                var error=eventArgs.ExceptionObject as Exception;
                Console.WriteLine("FATAL "+(error==null?eventArgs.ExceptionObject.GetType().FullName:error.GetType().FullName));
                if(error!=null){Console.WriteLine("MESSAGE "+error.Message);Console.WriteLine("STACK "+error.StackTrace);}
            };
            root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string picture=Path.Combine(root,"头像与背景.png");
            using(Bitmap image=new Bitmap(128,96)) { using(Graphics g=Graphics.FromImage(image))g.Clear(Color.SeaGreen);image.Save(picture,ImageFormat.Png); }
            string avatar=EmojiAvatars.FromLocalImage(picture);
            Run("连接端口：旧配置、验证、保存、明暗渲染及其他设置保留",delegate {
                var store=new LocalStore(Path.Combine(root,"ports"));
                File.WriteAllText(Path.Combine(store.Root,"settings.json"),"{\"Nickname\":\"旧配置\",\"Signature\":\"保留旧签名\"}",new UTF8Encoding(false));
                Check(store.LoadSettings().Port==2425&&!store.LoadSettings().HasConfiguredPort,"旧配置默认端口错误");
                Check(new AppSettings{Port=0}.Port==2425&&new AppSettings{Port=65536}.Port==2425,"非法旧值没有回退");
                AppSettings settings=store.LoadSettings();settings.ReceiveFolder=Path.Combine(store.Root,"received");
                foreach(bool dark in new[]{false,true}) {
                    Theme.Change(dark,"slate");
                    using(var dialog=new SettingsDialog(settings,store.Root,2425)) {
                        dialog.Show();Application.DoEvents();
                        TextBox port=All(dialog).OfType<TextBox>().Single(box=>box.AccessibleName=="端口");
                        Check(!All(dialog).Any(field=>field.AccessibleName=="个性签名"||field.Text=="个性签名"),"无效签名入口仍存在");
                        Check(All(dialog).Any(field=>field.Text=="连接")&&port.Visible,"通用页缺少连接设置");
                        foreach(string invalid in new[]{"","abc","1023","65536","-1","30.1"}) {port.Text=invalid;Button(dialog,"保存").PerformClick();Check(dialog.Value==null&&dialog.DialogResult!=DialogResult.OK,"非法端口被保存："+invalid);}
                        port.Text="3000";Appearance(dialog);All(dialog).OfType<Button>().Single(button=>button.AccessibleName=="设置标签：通用").PerformClick();
                        Check(port.Text=="3000","跨页丢失端口");Application.DoEvents();
                        using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,dialog.ClientRectangle);image.Save(Path.Combine(root,"settings-port-"+(dark?"dark":"light")+".png"));}
                        Button(dialog,"保存").PerformClick();Check(dialog.Value.Port==3000&&dialog.Value.Signature=="保留旧签名","端口或旧字段丢失");
                        store.SaveSettings(dialog.Value);settings=store.LoadSettings();Check(settings.Port==3000,"重读端口丢失");
                    }
                }
                using(var dialog=new SettingsDialog(settings,store.Root,2425)){dialog.Show();Application.DoEvents();Button(dialog,"保存").PerformClick();Check(dialog.Value.Port==3000,"再次保存被当前端口覆盖");}
                Check(AddressDialog.Parse("127.0.0.1",3000).Port==3000&&AddressDialog.Parse("127.0.0.1:2425",3000).Port==2425,"手动添加默认值错误");
                Theme.Change(false,"blue");
            });
            Run("新增壁纸保存、重新读取及再次写回保留配置",delegate {
                LocalStore store=new LocalStore(Path.Combine(root,"settings"));AppSettings settings=Settings("配置审计");
                string identity=settings.DeviceId;
                foreach(string pattern in new[]{"none","orbits","lines","grid","aurora","image"}) {
                    settings.ChatBackground=pattern;settings.Accent="slate";settings.Avatar="builtin:3";store.SaveSettings(settings);
                    AppSettings restored=new LocalStore(store.Root).LoadSettings();
                    Check(restored.ChatBackground==pattern,"重启丢失背景："+pattern);
                    Check(restored.DeviceId==identity && restored.Accent=="slate" && restored.Avatar=="builtin:3","重启改变其他设置");
                    store.SaveSettings(restored);Check(store.LoadSettings().ChatBackground==pattern,"再次写回丢失背景");
                }
                settings.ChatBackground="unknown";store.SaveSettings(settings);Check(store.LoadSettings().ChatBackground=="orbits","未知值应沿用旧版默认背景");
            });
            Run("异步头像禁止提前确认且后选默认头像不被覆盖",delegate {
                using(AvatarPickerDialog dialog=new AvatarPickerDialog("builtin:2","audit-avatar")) {
                    dialog.Show();Application.DoEvents();
                    TaskCompletionSource<string> pending=new TaskCompletionSource<string>();Task loading=dialog.ImportImageAsync(pending.Task);
                    Check(!Button(dialog,"使用此头像").Enabled,"头像读取中仍可确认旧值");
                    Button(dialog,"使用默认头像").PerformClick();Check(Button(dialog,"使用此头像").Enabled,"后选默认头像未解除等待");
                    pending.SetResult(avatar);Await(loading);Check(dialog.SelectedValue=="default","旧异步结果覆盖默认头像");
                    pending=new TaskCompletionSource<string>();loading=dialog.ImportImageAsync(pending.Task);pending.SetResult(avatar);Await(loading);
                    Check(dialog.SelectedValue==avatar && Button(dialog,"使用此头像").Enabled,"最新图片没有应用");
                    dialog.Close();
                }
            });
            Run("关闭头像窗口后异步失败不弹旧窗口或访问已释放控件",delegate {
                AvatarPickerDialog dialog=new AvatarPickerDialog("default","closed-avatar");dialog.Show();Application.DoEvents();
                TaskCompletionSource<string> pending=new TaskCompletionSource<string>();Task loading=dialog.ImportImageAsync(pending.Task);
                dialog.Close();dialog.Dispose();pending.SetException(new IOException("隔离测试读取失败"));Await(loading);
                Check(!Application.OpenForms.OfType<NoticeDialog>().Any(),"已关闭窗口仍弹出旧错误");
            });
            Run("异步背景禁止提前保存且后选图案不被覆盖",delegate {
                using(SettingsDialog dialog=new SettingsDialog(Settings("背景审计"),root,2425)) {
                    dialog.Show();Application.DoEvents();Appearance(dialog);
                    TaskCompletionSource<bool> pending=new TaskCompletionSource<bool>();Task loading=dialog.ImportBackgroundAsync(picture,pending.Task);
                    Check(!Button(dialog,"保存").Enabled,"背景校验中仍能保存旧值");
                    Button(dialog,"网格").PerformClick();Check(Button(dialog,"保存").Enabled,"后选图案仍被旧任务阻塞");
                    pending.SetResult(true);Await(loading);Button(dialog,"保存").PerformClick();
                    Check(dialog.Value!=null && dialog.Value.ChatBackground=="grid" && dialog.Value.BackgroundImage=="","旧图片覆盖了后选图案");
                    dialog.Close();
                }
                using(SettingsDialog dialog=new SettingsDialog(Settings("背景成功"),root,2425)) {
                    dialog.Show();Application.DoEvents();
                    Await(dialog.ImportBackgroundAsync(picture,Task.Run(delegate { using(Bitmap image=ChatWallpaper.Load(picture)) { } })));
                    Button(dialog,"保存").PerformClick();Check(dialog.Value.ChatBackground=="image" && dialog.Value.BackgroundImage==picture,"新图片未保存");dialog.Close();
                }
            });
            Run("只修改壁纸时主窗口立即应用并保持原主题",delegate {
                Theme.Configure(false,"blue");LocalStore store=new LocalStore(Path.Combine(root,"main"));
                using(LanService node=new LanService(Settings("主窗审计"),2425,true,"audit"))
                using(MainForm main=new MainForm(node,store)) {
                    main.Show();Application.DoEvents();int theme=Theme.Version;Exception failure=null;
                    using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=30 }) {
                        timer.Tick+=delegate {
                            SettingsDialog dialog=Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault();if(dialog==null)return;timer.Stop();
                            try { Appearance(dialog);Button(dialog,"极光").PerformClick();Button(dialog,"保存").PerformClick(); }
                            catch(Exception error) { failure=error;dialog.DialogResult=DialogResult.Cancel; }
                        };
                        timer.Start();main.OpenSettings();
                    }
                    if(failure!=null)throw failure;
                    object host=typeof(MainForm).GetField("chatHost",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(main);
                    Check((string)host.GetType().GetField("Pattern").GetValue(host)=="aurora","未打开会话时主窗仍显示旧壁纸");
                    Check(Theme.Version==theme,"只改壁纸重建了主题");Check(store.LoadSettings().ChatBackground=="aurora","主窗背景重启丢失");
                    main.ExitApplication();
                }
            });
            Run("超长表情草稿只重绘可见区域且原文、选区和系统 DLL 路径正确",delegate {
                string text=String.Concat(Enumerable.Repeat("😀🌟 中文 English line\n",30000));
                using(Font font=new Font(Theme.Body.FontFamily,10F))
                using(Form host=new Form { ClientSize=new Size(480,220) })
                using(ChatEditor editor=new ChatEditor { Dock=DockStyle.Fill,Font=font,MaxLength=MessageText.InputLimit }) {
                    host.Controls.Add(editor);host.Show();editor.Text=text;editor.Select(text.Length-8,4);editor.ScrollToCaret();Application.DoEvents();
                    int selection=editor.SelectionStart,length=editor.SelectionLength;
                    ProcessModule rich=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m=>m.ModuleName.Equals("msftedit.dll",StringComparison.OrdinalIgnoreCase));
                    Check(Path.GetFullPath(rich.FileName).Equals(Path.Combine(Environment.SystemDirectory,"msftedit.dll"),StringComparison.OrdinalIgnoreCase),"RichEdit 未从系统目录加载");
                    foreach(bool dark in new[]{false,true}) {
                        Theme.Configure(dark,"blue");editor.BackColor=Theme.Ice;editor.ForeColor=Theme.Ink;
                        Stopwatch clock=Stopwatch.StartNew();
                        using(Bitmap image=new Bitmap(editor.Width,editor.Height)) {
                            for(int i=0;i<3;i++)editor.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                            Check(clock.ElapsedMilliseconds<5000,"三次可见区域重绘耗时过长："+clock.ElapsedMilliseconds+" ms");
                            Console.WriteLine("DETAIL "+text.Length+" 字符，三次重绘 "+clock.ElapsedMilliseconds+" ms");
                            image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"audit-editor-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
                        }
                        Check(editor.Text==text && editor.SelectionStart==selection && editor.SelectionLength==length,"绘制改变原文或选区");
                    }
                    host.Close();Theme.Configure(false,"blue");
                }
            });
            Run("删除已接收文件保留消息、其他文件并持久化状态",delegate {
                LocalStore store=new LocalStore(Path.Combine(root,"delete-file-store"));string selected=Path.Combine(root,"已接收.txt"),other=Path.Combine(root,"另一个.txt");File.WriteAllText(selected,"selected");File.WriteAllText(other,"other");
                ChatRecord record=new ChatRecord{PeerId="received-peer",Packet=1,Text="保留消息",State="已保存",Time=DateTime.Now,HasAttachments=true,LocalFiles=new[]{selected,other},Transfers=new[]{new FileTransferState{Id=0,Name="已接收.txt",LocalPath=selected,Status="已保存",Percent=100},new FileTransferState{Id=1,Name="另一个.txt",LocalPath=other,Status="已保存",Percent=100}}};store.Append(record);
                bool rejected=false;try{store.DeleteReceivedFile(record,picture);}catch(IOException){rejected=true;}Check(rejected&&File.Exists(picture),"可删除消息之外的文件");
                store.DeleteReceivedFile(record,selected);Check(!File.Exists(selected)&&File.Exists(other),"删除范围错误");ChatRecord saved=new LocalStore(store.Root).History(record.PeerId,10).Single();Check(saved.Text=="保留消息"&&saved.GetLocalFiles().SequenceEqual(new[]{other})&&saved.Transfers[0].Status=="本机文件已删除","重启丢失消息或删除状态");
                saved.Outgoing=true;rejected=false;try{store.DeleteReceivedFile(saved,other);}catch(IOException){rejected=true;}Check(rejected&&File.Exists(other),"删除入口允许删除发送原文件");
            });
            Run("接收附件菜单区分删除文件与删除消息且发送原文件无删除入口",delegate {
                foreach(bool dark in new[]{false,true}){
                    Theme.Configure(dark,"blue");
                    using(Form host=new Form{ClientSize=new Size(520,420)})using(BubbleHistory history=new BubbleHistory{Dock=DockStyle.Fill}){
                        string file=Path.Combine(root,"另一个.txt");ChatRecord record=new ChatRecord{PeerId="menu-peer",Packet=1,Time=DateTime.Now,Text="已接收文件",State="已保存",LocalFiles=new[]{file}};
                        string selected=null;history.DeleteRequested+=records=>{};history.DeleteFileRequested+=(message,path)=>selected=path;host.Controls.Add(history);host.Show();history.SetRecords(new[]{record});
                        typeof(BubbleHistory).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(history,0);
                        history.ContextMenuStrip.Show(history,new Point(20,40));Application.DoEvents();
                        ToolStripMenuItem remove=history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item=>item.Text=="删除本机文件");Check(remove.Visible,"接收附件缺少删除文件入口");
                        using(Bitmap image=new Bitmap(history.ContextMenuStrip.Width,history.ContextMenuStrip.Height)){history.ContextMenuStrip.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"delete-file-menu-"+(dark?"dark":"light")+".png"),ImageFormat.Png);}
                        remove.PerformClick();Check(selected==file&&File.Exists(file),"菜单选错文件或未确认就删文件");history.ContextMenuStrip.Close();
                        record.Outgoing=true;history.ContextMenuStrip.Show(history,new Point(20,40));Application.DoEvents();Check(!remove.Visible,"发送原文件暴露删除入口");history.ContextMenuStrip.Close();host.Close();
                    }
                }
                Theme.Configure(false,"blue");
            });
            Run("普通文本取消发送经菜单确认立即落盘，晚到失败不覆盖且会话重开、重启保持", delegate {
                LocalStore store = new LocalStore(Path.Combine(root, "text-cancel")); Peer peer;
                using (UdpClient offline = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    peer = new Peer { Endpoint = (IPEndPoint)offline.Client.LocalEndPoint, Login = "offline-text", Host = "host", Nickname = "离线文本", Utf8 = true };
                    using (LanService sender = new LanService(Settings("文本取消"), FreePort(), true, "text-cancel")) using (MainForm main = new MainForm(sender, store))
                    {
                        sender.Start(); main.Show(); main.OpenChat(peer); Application.DoEvents();
                        ChatRecord record = sender.SendContent(peer, "0", new string[0], store.Root); main.Sent(peer, record); main.ActiveChat.Append(record);
                        BubbleHistory history = All(main.ActiveChat).OfType<BubbleHistory>().Single();
                        typeof(BubbleHistory).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(history, 0);
                        history.ContextMenuStrip.Show(history, new Point(10, 10)); Application.DoEvents();
                        ToolStripItem cancel = history.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "取消发送"); Check(cancel.Visible, "普通文本缺少取消发送菜单");
                        bool confirmed = false;
                        using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 20 })
                        {
                            timer.Tick += delegate { ConfirmDialog dialog = Application.OpenForms.OfType<ConfirmDialog>().FirstOrDefault(); if (dialog != null && !confirmed) { confirmed = true; Button(dialog, "取消发送").PerformClick(); } };
                            timer.Start(); cancel.PerformClick(); history.ContextMenuStrip.Close();
                        }
                        Check(confirmed && record.State == "已取消发送", "未经过确认或取消未即时更新状态");
                        Pump(() => store.History(peer.Id, 10).Single().State == "已取消发送", "取消状态未持久化");
                        Check(!sender.CanCancelOutgoing(peer.Id, record.Packet), "取消后仍在待重试队列");
                        Task lateFailure = Task.Run(() => typeof(LanService).GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(sender, new object[] { record, "发送失败，未确认送达" }));
                        Await(lateFailure); Check(record.State == "已取消发送", "晚到失败改写了取消终态");
                        byte[] ack = Protocol.Encode(1, peer.Login, peer.Host, Protocol.ReceiveMessage, record.Packet.ToString(), null);
                        offline.Send(ack, ack.Length, new IPEndPoint(IPAddress.Loopback, sender.Port));
                        Stopwatch clock = Stopwatch.StartNew(); bool resent = false;
                        while (clock.ElapsedMilliseconds < 2300)
                        {
                            while (offline.Available > 0)
                            {
                                IPEndPoint remote = null; Packet packet;
                                if (Protocol.TryParse(offline.Receive(ref remote), out packet) && packet.Mode == Protocol.SendMessage && packet.Number == record.Packet && clock.ElapsedMilliseconds > 100) resent = true;
                            }
                            Application.DoEvents(); Thread.Sleep(15);
                        }
                        Check(!resent && record.State == "已取消发送", "取消后重试或迟到 ACK 覆盖终态");
                        main.ActiveChat.Dispose(); main.OpenChat(peer); Application.DoEvents();
                        BubbleHistory reopened = All(main.ActiveChat).OfType<BubbleHistory>().Single(); Check(((List<ChatRecord>)PrivateField(reopened, "records")).Single().State == "已取消发送", "重开会话丢失取消状态");
                        using (HistoryDialog archive = new HistoryDialog(store, peer))
                        {
                            archive.Show(main); Application.DoEvents(); BubbleHistory body = All(archive).OfType<BubbleHistory>().Single();
                            Check(((List<ChatRecord>)PrivateField(body, "records")).Single().State == "已取消发送", "历史搜索丢失取消状态");
                            using (Bitmap image = new Bitmap(body.Width, body.Height)) { body.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(Path.GetTempPath(), "canceled-text-history.png"), ImageFormat.Png); }
                            archive.Close();
                        }
                        main.ExitApplication();
                    }
                }
                using (LanService restarted = new LanService(store.LoadSettings(), FreePort(), true, "text-cancel")) using (MainForm main = new MainForm(restarted, new LocalStore(store.Root)))
                {
                    main.Show(); main.OpenChat(peer); Application.DoEvents();
                    BubbleHistory history = All(main.ActiveChat).OfType<BubbleHistory>().Single(); Check(((List<ChatRecord>)PrivateField(history, "records")).Single().State == "已取消发送", "程序重启把取消状态恢复成未确认");
                    Check(!restarted.CanCancelOutgoing(peer.Id, ((List<ChatRecord>)PrivateField(history, "records")).Single().Packet), "重启复活了取消重试"); main.ExitApplication();
                }
            });
            Run("TLS 普通文本取消关闭正在握手的连接，真实异步异常不会覆盖取消或重新重试", delegate {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                try
                {
                    LocalStore store = new LocalStore(Path.Combine(root, "tls-text-cancel"));
                    Peer peer = new Peer { Endpoint = (IPEndPoint)listener.LocalEndpoint, Login = "blocked-tls", Nickname = "阻塞 TLS", DeviceId = Guid.NewGuid().ToString("N"), Fingerprint = new string('a', 64), Utf8 = true };
                    using (LanService sender = new LanService(Settings("TLS 文本取消"), FreePort(), true, "tls-text-cancel")) using (MainForm main = new MainForm(sender, store))
                    {
                        sender.ConfigureSecurity(Path.Combine(store.Root, "security"), (name, code) => true); sender.Start(); main.Show();
                        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
                        ChatRecord record = sender.SendContent(peer, "取消握手中的文本", new string[0], store.Root); main.Sent(peer, record);
                        Await(accepted); using (TcpClient remote = accepted.Result)
                        {
                            remote.ReceiveTimeout = 3000; byte[] header = new byte[4]; int count = 0;
                            while (count < header.Length) { int read = remote.GetStream().Read(header, count, header.Length - count); Check(read > 0, "TLS 前导读取未完成"); count += read; }
                            Check(Encoding.ASCII.GetString(header) == "FQ2\n", "发送端尚未进入 TLS 连接");
                            object gate = PrivateField(sender, "gate"), pending;
                            lock (gate) pending = ((System.Collections.IDictionary)PrivateField(sender, "pending"))[record.Packet];
                            Check(pending != null && sender.CancelOutgoing(peer.Id, record.Packet, record.State, true), "正在握手的文本不能取消");
                            Pump(() => (int)PrivateField(pending, "Sending") == 0, "取消后异步 TLS 线程未结束");
                            Check(record.State == "已取消发送" && store.History(peer.Id, 10).Single().State == "已取消发送" && !sender.CanCancelOutgoing(peer.Id, record.Packet), "真实 TLS 失败覆盖取消状态或重新加入队列");
                        }
                        main.ExitApplication();
                    }
                }
                finally { listener.Stop(); }
            });
            Run("离线发送文件可取消发送、撤销持久化报价并阻止上线后投递与拉取",delegate {
                string senderSec=Path.Combine(root,"cancel-offline-sender-sec"), receiverSec=Path.Combine(root,"cancel-offline-receiver-sec");
                string source=Path.Combine(root,"离线取消文件.bin"); File.WriteAllBytes(source,Enumerable.Range(0,4096).Select(i=>(byte)(i%251)).ToArray());
                int senderPort=FreePort(), receiverPort=FreePort();
                AppSettings senderSettings=Settings("发送端"), receiverSettings=Settings("接收端");
                using(LanService sender=new LanService(senderSettings,senderPort,true,"cancel-sender")) {
                    sender.ConfigureSecurity(senderSec,(name,code)=>true); sender.Start();
                    Peer target;
                    using(LanService receiver=new LanService(receiverSettings,receiverPort,true,"cancel-receiver")) {
                        receiver.ConfigureSecurity(receiverSec,(name,code)=>true); receiver.Start();
                        sender.Probe(new IPEndPoint(IPAddress.Loopback,receiverPort));
                        Pump(()=>sender.Peers.Any(p=>p.Login=="cancel-receiver"&&p.Online),"未发现加密接收端");
                        target=sender.Peers.Single(p=>p.Login=="cancel-receiver"); sender.VerifyPeer(target);
                    }
                    ChatRecord record=sender.SendContent(target,"离线带文件",new[]{source},senderSec);
                    Check(sender.CanCancelOutgoing(target.Id,record.Packet),"离线发文件应允许取消发送");
                    Check(sender.HasUndeliveredOutgoing(target.Id,record.Packet,record.State),"离线发文件应识别为未送达");
                    using(Form host=new Form{ClientSize=new Size(520,420)})using(BubbleHistory history=new BubbleHistory{Dock=DockStyle.Fill}) {
                        ChatRecord canceledTarget=null;
                        history.CanCancelSend=r=>r!=null&&r.Outgoing&&sender.CanCancelOutgoing(target.Id,r.Packet);
                        history.CancelSendRequested+=r=>canceledTarget=r;
                        host.Controls.Add(history);host.Show();history.SetRecords(new[]{record});
                        typeof(BubbleHistory).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(history,0);
                        history.ContextMenuStrip.Show(history,new Point(20,40));Application.DoEvents();
                        ToolStripMenuItem cancelItem=history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item=>item.Text=="取消发送");
                        Check(cancelItem.Visible,"出站未完成消息菜单缺少取消发送");
                        cancelItem.PerformClick();Check(canceledTarget==record,"点击取消发送未触发回调");
                        history.ContextMenuStrip.Close();host.Close();
                    }
                    Check(sender.CancelOutgoing(target.Id,record.Packet,record.State,true),"取消离线发送返回 false");
                    Check(record.State=="已取消发送","取消后消息状态未变为已取消发送："+record.State);
                    Check(!sender.CanCancelOutgoing(target.Id,record.Packet),"取消后仍显示可取消");
                    Check(File.Exists(source),"取消发送误删本机原文件");
                    string offersJson=File.ReadAllText(Path.Combine(senderSec,"transfer-offers.json"),Encoding.UTF8);
                    Check(!offersJson.Contains("\"Packet\":"+record.Packet),"持久化 transfer-offers.json 未撤销报价");
                    int receivedCount=0;
                    using(LanService receiver=new LanService(receiverSettings,receiverPort,true,"cancel-receiver")) {
                        receiver.ConfigureSecurity(receiverSec,(name,code)=>true);
                        receiver.MessageReceived+=msg=>Interlocked.Increment(ref receivedCount);
                        receiver.Start();
                        receiver.Probe(new IPEndPoint(IPAddress.Loopback,senderPort));
                        Pump(()=>receiver.Peers.Any(p=>p.Login=="cancel-sender"&&p.Online),"接收端重连后未发现发送端");
                        Thread.Sleep(400);
                        Check(receivedCount==0,"已取消的离线消息在对方上线后仍被投递");
                        Peer backPeer=receiver.Peers.Single(p=>p.Login=="cancel-sender");
                        bool failedPull=false;
                        try { Await(receiver.ReceiveFileAsync(backPeer,record.Packet,new Attachment{Id=0,Name=Path.GetFileName(source),Size=4096,Sha256=record.Transfers[0].Sha256},Path.Combine(root,"不应收到.bin"),null,CancellationToken.None)); }
                        catch(Exception) { failedPull=true; }
                        Check(failedPull,"对方上线后仍拉取到了已取消的离线文件");
                    }
                }
            });
            Run("离线未送达消息直接删除撤销重试与报价，已送达未传消息删除保留对方报价",delegate {
                string senderSec=Path.Combine(root,"del-sender-sec"), receiverSec=Path.Combine(root,"del-receiver-sec");
                LocalStore store=new LocalStore(Path.Combine(root,"del-sender-store"));
                string fDelivered=Path.Combine(root,"已送达保留.bin"), fDeliveredCancel=Path.Combine(root,"已送达后取消.bin");
                string fOfflinePending=Path.Combine(root,"离线等待直删.bin"), fOfflineFailed=Path.Combine(root,"离线未确认直删.bin");
                byte[] payload=Enumerable.Range(0,2048).Select(i=>(byte)(i%241)).ToArray();
                foreach(string path in new[]{fDelivered,fDeliveredCancel,fOfflinePending,fOfflineFailed})File.WriteAllBytes(path,payload);
                int senderPort=FreePort(), receiverPort=FreePort();
                AppSettings senderSettings=Settings("删除发送端"), receiverSettings=Settings("删除接收端");
                using(LanService sender=new LanService(senderSettings,senderPort,true,"del-sender"))
                using(MainForm main=new MainForm(sender,store)) {
                    sender.ConfigureSecurity(senderSec,(name,code)=>true); sender.Start(); main.Show(); main.NetworkStarted();
                    Peer target;
                    using(LanService receiver=new LanService(receiverSettings,receiverPort,true,"del-receiver")) {
                        IncomingMessage gotDelivered=null, gotCancel=null;
                        receiver.ConfigureSecurity(receiverSec,(name,code)=>true);
                        receiver.MessageReceived+=msg=>{if(msg.Record.Text.Contains("保留报价"))gotDelivered=msg; else if(msg.Record.Text.Contains("取消已送达"))gotCancel=msg;};
                        receiver.Start();
                        sender.Probe(new IPEndPoint(IPAddress.Loopback,receiverPort));
                        Pump(()=>sender.Peers.Any(p=>p.Login=="del-receiver"&&p.Online),"未发现删除测试接收端");
                        target=sender.Peers.Single(p=>p.Login=="del-receiver"); sender.VerifyPeer(target);
                        main.OpenChat(target);
                        ChatRecord rDelivered=sender.SendContent(target,"保留报价",new[]{fDelivered},senderSec);
                        main.Sent(target,rDelivered); main.ActiveChat.Append(rDelivered);
                        Pump(()=>rDelivered.State=="已送达"&&gotDelivered!=null,"已送达消息未确认");
                        Check(DeleteWithConfirm(main,target,new[]{rDelivered}),"已送达未传出站文件消息未能直接删除");
                        Check(!store.History(target.Id,20).Any(r=>r.Packet==rDelivered.Packet),"删除后历史仍存在记录");
                        Check(File.Exists(fDelivered),"删除消息误删原文件");
                        Peer fromReceiver=receiver.Peers.Single(p=>p.Login=="del-sender");
                        string savedPath=Path.Combine(root,"对方仍可下载.bin");
                        Await(receiver.ReceiveFileAsync(fromReceiver,gotDelivered.Record.Packet,gotDelivered.Files[0],savedPath,null,CancellationToken.None));
                        Check(File.ReadAllBytes(savedPath).SequenceEqual(payload),"删除已送达本机记录破坏了对方下载权限");
                        ChatRecord rCancel=sender.SendContent(target,"取消已送达",new[]{fDeliveredCancel},senderSec);
                        main.Sent(target,rCancel); main.ActiveChat.Append(rCancel);
                        Pump(()=>rCancel.State=="已送达"&&gotCancel!=null,"第二条消息未送达");
                        Check(sender.CancelOutgoing(target.Id,rCancel.Packet,rCancel.State,true),"取消已送达文件邀请失败");
                        Pump(()=>rCancel.Transfers[0].Status=="已取消发送","已送达取消后附件状态未更新");
                        Check(rCancel.State=="已送达","取消已送达文件的报价不应篡改消息已送达状态");
                        bool rejectedAfterCancel=false;
                        try { Await(receiver.ReceiveFileAsync(fromReceiver,gotCancel.Record.Packet,gotCancel.Files[0],Path.Combine(root,"取消后不应下载.bin"),null,CancellationToken.None)); }
                        catch(Exception) { rejectedAfterCancel=true; }
                        Check(rejectedAfterCancel,"取消已送达文件邀请后对方仍能下载");
                    }
                    ChatRecord rOff1=sender.SendContent(target,"离线等待直删",new[]{fOfflinePending},senderSec);
                    main.Sent(target,rOff1); main.ActiveChat.Append(rOff1);
                    Check(DeleteWithConfirm(main,target,new[]{rOff1}),"离线等待确认带文件消息未能直接删除");
                    Check(!sender.CanCancelOutgoing(target.Id,rOff1.Packet),"直接删除未送达消息后报价未撤销");
                    Check(File.Exists(fOfflinePending),"删除离线消息误删原文件");
                    ChatRecord rOff2=sender.SendContent(target,"离线未确认直删",new[]{fOfflineFailed},senderSec);
                    main.Sent(target,rOff2); main.ActiveChat.Append(rOff2);
                    rOff2.State="未确认送达，请检查网络后重发";
                    Check(DeleteWithConfirm(main,target,new[]{rOff2}),"离线未确认送达带文件消息未能直接删除");
                    Check(!sender.CanCancelOutgoing(target.Id,rOff2.Packet),"删除未确认送达消息后报价未撤销");
                    string offersJson=File.ReadAllText(Path.Combine(senderSec,"transfer-offers.json"),Encoding.UTF8);
                    Check(!offersJson.Contains("\"Packet\":"+rOff1.Packet)&&!offersJson.Contains("\"Packet\":"+rOff2.Packet),"直接删除离线消息后 transfer-offers.json 仍残留报价");
                    int offlineReceived=0;
                    using(LanService receiver=new LanService(receiverSettings,receiverPort,true,"del-receiver")) {
                        receiver.ConfigureSecurity(receiverSec,(name,code)=>true);
                        receiver.MessageReceived+=msg=>Interlocked.Increment(ref offlineReceived);
                        receiver.Start();
                        receiver.Probe(new IPEndPoint(IPAddress.Loopback,senderPort));
                        Pump(()=>receiver.Peers.Any(p=>p.Login=="del-sender"&&p.Online),"接收端未上线");
                        Thread.Sleep(400);
                        Check(offlineReceived==0,"已删除的离线消息在对方上线后仍被发出");
                    }
                    main.ExitApplication();
                }
            });
            Run("附件完成状态跨重启与旧任务格式恢复保持一致",delegate {
                string senderRoot=Path.Combine(root,"completed-restart-sender"),receiverRoot=Path.Combine(root,"completed-restart-receiver"),source=Path.Combine(root,"completed-restart.bin");File.WriteAllBytes(source,new byte[]{1,2,3});
                AppSettings senderSettings=Settings("完成发送端"),receiverSettings=Settings("完成接收端");long packet;string peerId;
                using(LanService sender=new LanService(senderSettings,FreePort(),true,"completed-sender"))
                using(LanService receiver=new LanService(receiverSettings,FreePort(),true,"completed-receiver")){
                    sender.ConfigureSecurity(senderRoot,(name,code)=>true);receiver.ConfigureSecurity(receiverRoot,(name,code)=>true);sender.Start();receiver.Start();IncomingMessage incoming=null;receiver.MessageReceived+=message=>incoming=message;
                    sender.TransferChanged+=(record,file,percent,state,rate)=>{FileTransferState transfer=record.Transfers.Single(value=>value.Id==file);transfer.Status=state;transfer.Percent=percent;};
                    sender.Probe(new IPEndPoint(IPAddress.Loopback,receiver.Port));Pump(()=>sender.Peers.Any(peer=>peer.Login=="completed-receiver"),"接收端未发现");Peer target=sender.Peers.Single(peer=>peer.Login=="completed-receiver");sender.VerifyPeer(target);peerId=target.Id;
                    ChatRecord sent=sender.SendMessage(target,"保存确认",new[]{source});packet=sent.Packet;Pump(()=>incoming!=null&&sent.State=="已送达","邀请未送达");Peer back=receiver.Peers.Single(peer=>peer.Login=="completed-sender");
                    Await(receiver.ReceiveFileAsync(back,packet,incoming.Files[0],Path.Combine(root,"completed-restart-copy.bin"),null,CancellationToken.None));
                    object offersGate=typeof(LanService).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(sender);
                    // 与后台原子落盘共用锁，避免检查文件时碰到写入句柄；仍等待实际完成状态进入文件。
                    Pump(()=>{lock(offersGate)return File.ReadAllText(Path.Combine(senderRoot,"transfer-offers.json"),Encoding.UTF8).Contains("\"TransferState\":\"对方已校验保存\"");},"完成状态未落盘");Check(!sender.CanCancelOutgoing(peerId,packet),"已完成文件仍可取消");
                }
                for(int generation=0;generation<2;generation++){
                    using(LanService restored=new LanService(senderSettings,FreePort(),true,"completed-sender")){
                        restored.ConfigureSecurity(senderRoot,(name,code)=>true);Check(!restored.CanCancelOutgoing(peerId,packet),"重启或旧格式丢失完成状态");Check(!restored.CancelOutgoing(peerId,packet,"已送达",true),"完成报价被误取消");Check(restored.OutgoingRecord(peerId,packet)!=null,"完成报价丢失");
                    }
                    if(generation==0){string stored=Path.Combine(senderRoot,"transfer-offers.json");var parser=new System.Web.Script.Serialization.JavaScriptSerializer();object[] legacy=(object[])parser.DeserializeObject(File.ReadAllText(stored,Encoding.UTF8));foreach(Dictionary<string,object> entry in legacy){entry.Remove("TransferState");entry.Remove("Percent");}File.WriteAllText(stored,parser.Serialize(legacy),new UTF8Encoding(false));}
                }
            });
            Run("明文发送完成不泄漏活跃流计数，正在发送中阻断删除且取消可中止传输流",delegate {
                LocalStore store=new LocalStore(Path.Combine(root,"active-stream-store"));
                string smallFile=Path.Combine(root,"明文小文件.bin"), largeFile=Path.Combine(root,"传输中取消.bin");
                byte[] smallBytes=Enumerable.Range(0,1024).Select(i=>(byte)(i%251)).ToArray();
                byte[] largeBytes=new byte[4*1024*1024]; new Random(17).NextBytes(largeBytes);
                File.WriteAllBytes(smallFile,smallBytes); File.WriteAllBytes(largeFile,largeBytes);
                int senderPort=FreePort(), receiverPort=FreePort();
                using(LanService sender=new LanService(Settings("明文发"),senderPort,true,"plain-sender"))
                using(LanService receiver=new LanService(Settings("明文收"),receiverPort,true,"plain-receiver"))
                using(MainForm main=new MainForm(sender,store)) {
                    IncomingMessage gotSmall=null, gotLarge=null;
                    receiver.MessageReceived+=msg=>{if(msg.Files.Count>0&&msg.Files[0].Name=="明文小文件.bin")gotSmall=msg;else if(msg.Files.Count>0&&msg.Files[0].Name=="传输中取消.bin")gotLarge=msg;};
                    sender.Start(); receiver.Start(); main.Show(); main.NetworkStarted();
                    sender.Probe(new IPEndPoint(IPAddress.Loopback,receiverPort));
                    Pump(()=>sender.Peers.Any(p=>p.Login=="plain-receiver"&&p.Online)&&receiver.Peers.Any(p=>p.Login=="plain-sender"&&p.Online),"明文双端未互发现");
                    Peer target=sender.Peers.Single(p=>p.Login=="plain-receiver"), back=receiver.Peers.Single(p=>p.Login=="plain-sender");
                    main.OpenChat(target);
                    ChatRecord rSmall=sender.SendContent(target,"小文件",new[]{smallFile},store.Root);
                    main.Sent(target,rSmall); main.ActiveChat.Append(rSmall);
                    Pump(()=>gotSmall!=null&&rSmall.State=="已送达","小文件邀请未送达");
                    string smallSaved=Path.Combine(root,"小文件已收.bin");
                    Await(receiver.ReceiveFileAsync(back,gotSmall.Record.Packet,gotSmall.Files[0],smallSaved,null,CancellationToken.None));
                    Check(File.ReadAllBytes(smallSaved).SequenceEqual(smallBytes),"小文件校验不符");
                    Pump(()=>!sender.HasActiveOutgoingTransfer(target.Id,rSmall.Packet)&&!sender.HasActiveOutgoingTransfer(target.Id),"明文正常发完后泄漏了 ActiveClients 计数");
                    Check(DeleteWithConfirm(main,target,new[]{rSmall}),"明文正常发完的消息因计数泄漏被阻断删除");

                    ChatRecord rLarge=sender.SendContent(target,"大文件",new[]{largeFile},store.Root);
                    main.Sent(target,rLarge); main.ActiveChat.Append(rLarge);
                    Pump(()=>gotLarge!=null&&rLarge.State=="已送达","大文件邀请未送达");
                    using(TcpClient stall=new TcpClient(AddressFamily.InterNetwork)) {
                        stall.ReceiveBufferSize=4096;
                        stall.Connect(IPAddress.Loopback,senderPort);
                        byte[] req=Protocol.Encode(999,"plain-receiver","host",Protocol.GetFile|Protocol.Utf8,rLarge.Packet.ToString("x")+":0:0:",null);
                        stall.GetStream().Write(req,0,req.Length);
                        byte[] first=new byte[16];
                        Check(stall.GetStream().Read(first,0,first.Length)>0,"未能启动明文发送流");
                        Pump(()=>sender.HasActiveOutgoingTransfer(target.Id,rLarge.Packet),"活动发送流未登记");
                        Check(DeleteBlockedByNotice(main,target,new[]{rLarge}),"正在发送中的文件消息未被阻断删除");
                        Check(sender.CancelOutgoing(target.Id,rLarge.Packet,rLarge.State,true),"取消正在发送中的文件失败");
                        Pump(()=>!sender.HasActiveOutgoingTransfer(target.Id,rLarge.Packet),"取消后活动发送流未释放");
                        Pump(()=>rLarge.Transfers[0].Status=="已取消发送","取消后附件状态未更新为已取消发送");
                        Thread.Sleep(200); Application.DoEvents();
                        Check(rLarge.Transfers[0].Status=="已取消发送","取消发送状态被发送中断覆盖："+rLarge.Transfers[0].Status);
                    }
                    Check(DeleteWithConfirm(main,target,new[]{rLarge}),"取消发送后仍不能删除消息");
                    main.ExitApplication();
                }
            });
            PartialRegression();
            Console.WriteLine("RESULT passed="+passed+" failed="+failed);
            return failed==0?0:1;
        }
    }
}
