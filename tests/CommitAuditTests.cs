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
        private static System.Collections.Generic.IEnumerable<Control> All(Control parent)
        {
            foreach(Control child in parent.Controls) { yield return child; foreach(Control item in All(child)) yield return item; }
        }
        private static Button Button(Control parent, string text) { return All(parent).OfType<Button>().Single(b => b.Text.TrimStart('✓', ' ') == text); }
        private static void Appearance(SettingsDialog dialog) { All(dialog).OfType<Button>().Single(b => b.AccessibleName == "设置标签：外观").PerformClick(); Application.DoEvents(); }
        private static AppSettings Settings(string name) { return new AppSettings { Nickname = name, Notifications = false, AutoReceiveFiles = false, ReceiveFolder = Path.Combine(root, "received") }; }
        private static void Await(Task task)
        {
            Stopwatch clock=Stopwatch.StartNew();
            while(!task.IsCompleted && clock.ElapsedMilliseconds<5000) { Application.DoEvents(); Thread.Sleep(5); }
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
                    if (confirm != null) { confirmed = true; timer.Stop(); Button(confirm, "删除").PerformClick(); }
                    NoticeDialog notice = Application.OpenForms.OfType<NoticeDialog>().FirstOrDefault();
                    if (notice != null) { timer.Stop(); Button(notice, "确定").PerformClick(); }
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
        [STAThread] public static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string picture=Path.Combine(root,"头像与背景.png");
            using(Bitmap image=new Bitmap(128,96)) { using(Graphics g=Graphics.FromImage(image))g.Clear(Color.SeaGreen);image.Save(picture,ImageFormat.Png); }
            string avatar=EmojiAvatars.FromLocalImage(picture);
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
                    Pump(()=>File.ReadAllText(Path.Combine(senderRoot,"transfer-offers.json"),Encoding.UTF8).Contains("\"TransferState\":\"对方已校验保存\""),"完成状态未落盘");Check(!sender.CanCancelOutgoing(peerId,packet),"已完成文件仍可取消");
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
            Console.WriteLine("RESULT passed="+passed+" failed="+failed);
            return failed==0?0:1;
        }
    }
}
