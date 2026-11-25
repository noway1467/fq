using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static partial class TransferFeatureTests
    {
        private static string root,output;
        private static int passed,failed;
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.I1)] private static extern bool CreateSymbolicLink(string link,string target,int flags);
        private static void SaveSnapshot(Form form,string name){form.Refresh();Application.DoEvents();Thread.Sleep(80);using(var image=new Bitmap(form.Width,form.Height)){using(Graphics g=Graphics.FromImage(image)){IntPtr dc=g.GetHdc();try{Check(PrintWindow(form.Handle,dc,2),"窗口渲染失败："+name);}finally{g.ReleaseHdc(dc);}}image.Save(Path.Combine(output,name+".png"),System.Drawing.Imaging.ImageFormat.Png);}}
        private static void Check(bool value,string message){if(!value)throw new Exception(message);}
        private static void Run(string name,Action test){try{test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+" "+e);}}
        private static int FreePort(){var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();return port;}
        private static void Wait(Func<bool> condition,string message){for(int i=0;i<800;i++){if(condition())return;Thread.Sleep(10);}throw new Exception(message);}
        private sealed class Reply
        {
            internal int Code;internal byte[] Body;internal string Headers;
        }
        private static Reply Request(int port,string path,string token=null,byte[] body=null,string method="GET",string extra="")
        {
            using(var client=new TcpClient("127.0.0.1",port)) {
                client.ReceiveTimeout=10000;byte[] payload=body??new byte[0];string headers=method+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nConnection: close\r\n"+extra;
                if(token!=null)headers+="X-FQ-Guest: "+token+"\r\n";
                headers+="Content-Length: "+payload.Length+"\r\n\r\n";Stream stream=client.GetStream();byte[] head=Encoding.ASCII.GetBytes(headers);stream.Write(head,0,head.Length);stream.Write(payload,0,payload.Length);
                using(var data=new MemoryStream()){byte[] buffer=new byte[8192];int count;while((count=stream.Read(buffer,0,buffer.Length))>0)data.Write(buffer,0,count);byte[] bytes=data.ToArray();int split=-1;
                    for(int i=0;i+3<bytes.Length;i++)if(bytes[i]==13&&bytes[i+1]==10&&bytes[i+2]==13&&bytes[i+3]==10){split=i;break;}
                    Check(split>=0,"缺少HTTP响应");string text=Encoding.ASCII.GetString(bytes,0,split);return new Reply{Code=Int32.Parse(text.Split(' ')[1]),Headers=text,Body=bytes.Skip(split+4).ToArray()};
                }
            }
        }
        private static Reply Json(GuestTransfer server,string path,object body){return Request(server.Port,path,server.Token,Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body)),"POST","Content-Type: application/json\r\n");}
        private static System.Collections.Generic.Dictionary<string,object> Data(Reply reply){return (System.Collections.Generic.Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(reply.Body));}
        private static System.Collections.Generic.IEnumerable<Control> Controls(Control parent){foreach(Control child in parent.Controls){yield return child;foreach(Control nested in Controls(child))yield return nested;}}
        private static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(target);}
        private static void Tick(System.Windows.Forms.Timer timer){typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(timer,new object[]{EventArgs.Empty});}
        private static void PumpFor(int milliseconds)
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(clock.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(10);}
            Application.DoEvents();
        }
        private static void Pump(Func<bool> ready,string message)
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            while(!ready()&&clock.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Sleep(10);}
            Check(ready(),message);
        }
        private static string FinishDialogs(Action action,string confirmTitle,string confirmButton,Func<bool> finished=null,Action beforeConfirm=null)
        {
            string noticeText=null;bool confirmed=false;Exception callbackError=null;
            using(var timer=new System.Windows.Forms.Timer{Interval=20})
            {
                timer.Tick+=delegate {
                    try
                    {
                        ConfirmDialog confirm=Application.OpenForms.OfType<ConfirmDialog>().FirstOrDefault();
                        if(confirm!=null)
                        {
                            Check(confirmTitle!=null&&confirm.Text==confirmTitle,"出现意外确认框："+confirm.Text);
                            if(beforeConfirm!=null)beforeConfirm();confirmed=true;
                            Controls(confirm).OfType<Button>().Single(button=>button.Text==confirmButton).PerformClick();
                        }
                        NoticeDialog notice=Application.OpenForms.OfType<NoticeDialog>().FirstOrDefault();
                        if(notice!=null)
                        {
                            noticeText=notice.Text+"\n"+String.Join(" ",Controls(notice).OfType<TextBoxBase>().Select(box=>box.Text));
                            Controls(notice).OfType<Button>().Single(button=>button.Text=="确定").PerformClick();
                        }
                    }
                    catch(Exception error){callbackError=error;foreach(Form dialog in Application.OpenForms.Cast<Form>().Where(form=>form is ConfirmDialog||form is NoticeDialog).ToArray())dialog.Close();}
                };
                timer.Start();action();
                if(finished!=null)Pump(()=>callbackError!=null||finished(),"文件操作未完成");
                Check(callbackError==null,"弹窗处理失败："+callbackError);
                if(confirmTitle!=null)Check(confirmed,"操作未显示确认框："+confirmTitle);
            }
            return noticeText;
        }
        private static int TransferIndex(ListBox list,long packet,int fileId)
        {
            return list.Items.Cast<object>().Select((item,index)=>new{Item=item,Index=index}).Single(entry=>Field<ChatRecord>(entry.Item,"Record").Packet==packet&&Field<FileTransferState>(entry.Item,"File").Id==fileId).Index;
        }
        private static void RightClickTransfer(ListBox list,int index)
        {
            list.TopIndex=index;Rectangle bounds=list.GetItemRectangle(index);
            typeof(Control).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Right,1,bounds.Left+12,bounds.Top+20,0)});
            Check(list.SelectedIndex==index,"右键没有先选中目标任务");
        }
        private static void SaveMenu(ContextMenuStrip menu,string name)
        {
            using(var image=new Bitmap(menu.Width,menu.Height)){menu.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,name+".png"),System.Drawing.Imaging.ImageFormat.Png);}
        }
        private static void CheckPlacement(WindowPlacement placement,Rectangle bounds,bool maximized,string message)
        {
            Check(placement!=null&&placement.X==bounds.X&&placement.Y==bounds.Y&&placement.Width==bounds.Width&&placement.Height==bounds.Height&&placement.Maximized==maximized,message);
        }
        private static Bitmap EraseBackground(ListBox list)
        {
            var image=new Bitmap(Math.Max(1,list.ClientSize.Width),Math.Max(1,list.ClientSize.Height));
            using(Graphics graphics=Graphics.FromImage(image))
            {
                graphics.Clear(Color.Magenta);IntPtr dc=graphics.GetHdc();
                try
                {
                    object[] arguments={Message.Create(list.Handle,0x0014,dc,IntPtr.Zero)};
                    list.GetType().GetMethod("WndProc",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(list,arguments);
                    Check(((Message)arguments[0]).Result==(IntPtr)1,"背景擦除未由防闪烁列表接管");
                }
                finally{graphics.ReleaseHdc(dc);}
            }
            return image;
        }
        private sealed class BoundaryListMessages : NativeWindow,IDisposable
        {
            internal bool TopError,ZeroHeight;
            internal BoundaryListMessages(IntPtr handle){AssignHandle(handle);}
            protected override void WndProc(ref Message message)
            {
                if(message.Msg==0x018e&&TopError){message.Result=(IntPtr)(-1);return;}
                if(message.Msg==0x01a1&&ZeroHeight){message.Result=IntPtr.Zero;return;}
                base.WndProc(ref message);
            }
            public void Dispose(){ReleaseHandle();}
        }
        private sealed class Progress : IProgress<TransferProgress> { internal Action<TransferProgress> Update;public void Report(TransferProgress value){Update(value);} }
        private static void PauseResume()
        {
            byte[] content=new byte[4*1024*1024];new Random(17).NextBytes(content);TcpListener listener=new TcpListener(IPAddress.Loopback,0);listener.Start();long resumed=-1;
            Task serving=Task.Run(()=>{try{for(int attempt=0;attempt<2;attempt++)using(TcpClient socket=listener.AcceptTcpClient()){
                socket.ReceiveTimeout=5000;Stream stream=socket.GetStream();byte[] request=new byte[4096];int n=stream.Read(request,0,request.Length);string text=Encoding.UTF8.GetString(request,0,n).TrimEnd('\0');string[] fields=text.Split(':');long offset=Convert.ToInt64(fields[fields.Length-2],16);if(attempt==1)resumed=offset;
                try{for(long pos=offset;pos<content.Length;pos+=32768){int count=(int)Math.Min(32768,content.Length-pos);stream.Write(content,(int)pos,count);Thread.Sleep(2);}}catch(IOException){}
            }}finally{listener.Stop();}});
            var peer=new Peer{Endpoint=(IPEndPoint)listener.LocalEndpoint,Login="source",Host="host",Nickname="发送方",Utf8=true};var file=new Attachment{Id=1,Name="pause.bin",Size=content.Length,Modified=1};string path=Path.Combine(root,"pause.bin"),partial=LanService.PartialPath(peer,1,file,path);
            using(var node=new LanService(new AppSettings(),FreePort(),true,"receiver"))using(var cancel=new CancellationTokenSource()){
                var progress=new Progress{Update=value=>{if(value.Percent>=10)cancel.Cancel();}};
                try{node.ReceiveFileAsync(peer,1,file,path,null,cancel.Token,true,progress,()=>true).GetAwaiter().GetResult();throw new Exception("暂停未中止下载");}catch(OperationCanceledException){}
                long size=new FileInfo(partial).Length;Check(size>0&&size<content.Length,"暂停未保留非空断点");
                string saved=node.ReceiveFileAsync(peer,1,file,path,null,CancellationToken.None,true).GetAwaiter().GetResult();Check(resumed==size,"续传未使用落盘偏移");Check(File.ReadAllBytes(saved).SequenceEqual(content),"续传内容变化");
            }
            Check(serving.Wait(5000),"发送测试未结束");
        }
        private static void BatchDeleteRegression()
        {
            foreach(bool dark in new[]{false,true})
            {
                Theme.Configure(dark,"blue");var store=new LocalStore(Path.Combine(root,"batch-delete-"+dark));
                var settings=new AppSettings{CloseToTray=false,Notifications=false,AutoReceiveFiles=false,ReceiveFolder=Path.Combine(store.Root,"received"),Conversations=new[]{new SavedConversation{Endpoint="127.0.0.1:"+FreePort(),Login="batch-peer",Nickname="批量删除 中文 English"}}};
                using(var network=new LanService(settings,FreePort(),true,"batch-delete"))using(var main=new MainForm(network,store))
                {
                    Peer peer=network.Peers.Single();string saved=Path.Combine(store.Root,"已保存.txt"),missing=Path.Combine(store.Root,"外部已删.txt"),original=Path.Combine(store.Root,"发送原文件.txt");
                    File.WriteAllText(saved,"received");File.WriteAllText(missing,"removed externally");File.Delete(missing);File.WriteAllText(original,"sender original");
                    var pending=new FileTransferState{Id=4,Name="待接收.bin",Size=4096,Modified=1,Sha256=new string('a',64),RelativePath="未接收/待接收.bin",Status="已暂停",Percent=40};
                    var incoming=new ChatRecord{PeerId=peer.Id,Packet=11,Sender=peer.Nickname,Text="保留整条消息",Time=DateTime.Now,State="已收到",HasAttachments=true,LocalFiles=new[]{saved,missing},Transfers=new[]{
                        new FileTransferState{Id=0,Name="已保存.txt",LocalPath=saved,Status="已保存",Percent=100},
                        new FileTransferState{Id=1,Name="外部已删.txt",LocalPath=missing,Status="已保存",Percent=100},
                        new FileTransferState{Id=2,Name="以前已删.txt",Status="本机文件已删除"},
                        new FileTransferState{Id=3,Name="旧保存记录.txt",Status="已保存",Percent=100},pending}};
                    string alias=Path.Combine(store.Root,"规范化目录","..",Path.GetFileName(saved));
                    var duplicate=new ChatRecord{PeerId=peer.Id,Packet=12,Sender=peer.Nickname,Text="同路径附件",Time=DateTime.Now,HasAttachments=true,LocalFiles=new[]{alias},Transfers=new[]{new FileTransferState{Id=0,Name="已保存.txt",LocalPath=alias,Status="已保存",Percent=100}}};
                    var outgoing=new ChatRecord{PeerId=peer.Id,Packet=13,Sender="自己",Text="原文件保留",Time=DateTime.Now,Outgoing=true,State="已送达",HasAttachments=true,LocalFiles=new[]{original},Transfers=new[]{new FileTransferState{Id=0,Name="发送原文件.txt",LocalPath=original,Status="等待对方接收"}}};
                    foreach(ChatRecord record in new[]{incoming,duplicate,outgoing}){store.Append(record);main.TrackTransfer(record);}
                    var attachment=new Attachment{Id=pending.Id,Name=pending.Name,Size=pending.Size,Modified=pending.Modified,Sha256=pending.Sha256,RelativePath=pending.RelativePath};
                    string partial=LanService.PartialPath(peer,incoming.Packet,attachment,SendBatch.Destination(settings.ReceiveFolder,attachment));File.WriteAllBytes(partial,new byte[128]);
                    main.Show();main.OpenChat(peer);ChatForm chat=main.ActiveChat;BubbleHistory history=Field<BubbleHistory>(chat,"history");
                    Button deleteFiles=Controls(chat).OfType<Button>().Single(button=>button.AccessibleName=="批量删除本机文件");
                    history.SetRecords(new[]{outgoing});history.SelectAllMessages();Check(!deleteFiles.Enabled,"只选发送原文件时仍可批量删文件");
                    history.SetRecords(new[]{incoming,duplicate,outgoing});history.SelectAllMessages();typeof(BubbleHistory).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(history,0);
                    Check(deleteFiles.Enabled&&deleteFiles.ForeColor.ToArgb()==Theme.Danger.ToArgb(),"混合入站附件未启用危险色删除文件按钮");
                    TableLayoutPanel bar=Controls(chat).OfType<TableLayoutPanel>().Single(panel=>panel.AccessibleName=="消息多选操作栏");
                    Check(bar.ColumnCount==5&&new[]{"全选","删除","取消"}.All(text=>bar.Controls.OfType<Button>().Any(button=>button.Text==text)),"旧多选按钮文案或列数被破坏");
                    main.ClientSize=new Size(720,620);Application.DoEvents();foreach(Button button in bar.Controls.OfType<Button>())Check(bar.ClientRectangle.Contains(button.Bounds)&&button.Width>=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding).Width+8,"720px多选按钮裁切："+button.Text);
                    SaveSnapshot(main,"batch-selection-"+(dark?"dark":"light")+"-720");
                    history.ContextMenuStrip.Show(history,new Point(20,40));Application.DoEvents();Check(history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item=>item.Text=="删除所选本机文件").Enabled,"多选右键缺少删除文件操作");SaveMenu(history.ContextMenuStrip,"batch-menu-"+(dark?"dark":"light"));history.ContextMenuStrip.Close();
                    FinishDialogs(()=>deleteFiles.PerformClick(),"删除选中的本机文件？","取消");Check(File.Exists(saved)&&incoming.Transfers[1].LocalPath==missing&&incoming.Transfers[3].Status=="已保存"&&history.SelectionMode,"确认取消后删除、订正了文件或退出多选");
                    string feedback=FinishDialogs(()=>deleteFiles.PerformClick(),"删除选中的本机文件？","删除文件",null,()=>{
                        Check(File.Exists(saved)&&File.Exists(original)&&incoming.Transfers[1].LocalPath==missing,"确认之前已经修改文件");
                        pending.Status="接收中";
                    });
                    string result="已删除 1 个本机文件，3 个已删除跳过。";
                    Check(feedback!=null&&feedback.StartsWith("文件删除完成\n")&&feedback.Contains(result)&&feedback.Contains("1 个正在接收或处理的文件已跳过"),"删除统计错误："+feedback);
                    Check(Field<Label>(main,"status").Text.Contains(result)&&history.SelectionMode&&feedback.Contains("1 个正在接收或处理的文件已跳过"),"活跃附件没有逐项跳过并保留多选");
                    Check(!File.Exists(saved)&&File.ReadAllText(original)=="sender original"&&File.Exists(partial)&&pending.Status=="接收中","批量删除误删发送原文件、断点或活跃接收状态");
                    ChatRecord[] restored=new LocalStore(store.Root).History(peer.Id,10).ToArray();Check(restored.Length==3&&restored.Single(record=>record.Packet==11).Text=="保留整条消息","文件删除同时删除了聊天记录");
                    Check(restored.Where(record=>!record.Outgoing).SelectMany(record=>record.Transfers).Where(file=>file.Id!=4).All(file=>file.LocalPath==null&&file.Status=="本机文件已删除"&&file.Percent==0),"重启丢失文件删除或失效路径订正");
                    var unreceived=new ChatRecord{PeerId=peer.Id,Packet=21,Text="尚未接收",Time=DateTime.Now,HasAttachments=true,Transfers=new[]{new FileTransferState{Id=0,Name="未保存.bin",Status="已暂停",Percent=30}}};
                    main.TrySave(unreceived);history.SetRecords(new[]{unreceived});history.SelectAllMessages();feedback=FinishDialogs(()=>deleteFiles.PerformClick(),null,null);
                    Check(feedback!=null&&feedback.Contains("没有可删除的本机文件")&&feedback.Contains("传输任务")&&history.SelectionMode&&unreceived.Transfers[0].Status=="已暂停","未接收附件没有正确提示或被改写");
                    unreceived.Transfers[0].Status="接收中";feedback=FinishDialogs(()=>deleteFiles.PerformClick(),null,null);
                    Check(feedback!=null&&feedback.Contains("没有可删除")&&feedback.Contains("1 个正在接收或处理的文件已跳过")&&history.SelectionMode,"仅有活跃文件时没有说明跳过并保留多选");
                    main.ExitApplication();
                }
            }
        }
        private static void BatchDeleteFailureRegression()
        {
            var store=new LocalStore(Path.Combine(root,"batch-delete-failure"));
            string gone=Path.Combine(store.Root,"edge","gone"),keep=Path.Combine(store.Root,"edge","keep");Directory.CreateDirectory(gone);Directory.CreateDirectory(keep);
            Func<long,string[],ChatRecord> make=(packet,paths)=>new ChatRecord{PeerId="edge-peer",Packet=packet,Sender="peer",Text="files "+packet,Time=DateTime.Now,State="已保存",HasAttachments=true,LocalFiles=paths,Transfers=paths.Select((path,index)=>new FileTransferState{Id=index,Name=Path.GetFileName(path),LocalPath=path,Status="已保存",Percent=100}).ToArray()};
            string a=Path.Combine(gone,"a.txt"),b=Path.Combine(keep,"b.txt"),locked=Path.Combine(keep,"占用.txt"),c=Path.Combine(keep,"c.txt"),env=Path.Combine(keep,"%OS%.txt"),single=Path.Combine(keep,"%TEMP% 单个.txt");
            foreach(string path in new[]{a,b,locked,c,env,single})File.WriteAllText(path,"x");
            Directory.Delete(gone,true);
            ChatRecord missingParent=make(1,new[]{a}),mixed=make(2,new[]{b,locked,c}),named=make(3,new[]{env}),one=make(4,new[]{single});
            ReceivedFilesDeletionResult planned=store.DeleteReceivedFiles(new[]{missingParent,mixed,named},false);
            Check(planned.DeletedCount==4&&planned.SkippedDeletedCount==1&&planned.FailedCount==0&&File.Exists(b)&&mixed.Transfers[0].Status=="已保存","预览统计错误或提前修改文件");
            ReceivedFilesDeletionResult result;
            using(new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.None))result=store.DeleteReceivedFiles(new[]{missingParent,mixed,named});
            Check(result.DeletedCount==3&&result.SkippedDeletedCount==1&&result.FailedCount==1&&result.Text.StartsWith("已删除 3 个本机文件，1 个已删除跳过。")&&result.Text.Contains("1 个删除失败")&&result.Text.Contains("占用.txt"),"占用或父目录缺失时统计错误："+result.Text);
            Check(!File.Exists(b)&&!File.Exists(c)&&!File.Exists(env)&&File.Exists(locked),"占用文件中断了同批其余文件，或含环境变量写法的文件名未删除");
            Check(missingParent.Transfers[0].Status=="本机文件已删除"&&missingParent.Transfers[0].LocalPath==null&&mixed.Transfers[0].Status=="本机文件已删除"&&mixed.Transfers[2].Status=="本机文件已删除"&&mixed.Transfers[1].Status=="已保存"&&mixed.Transfers[1].LocalPath==locked&&named.Transfers[0].Status=="本机文件已删除","失败项被错标或成功项未订正");
            ChatRecord restored=new LocalStore(store.Root).History("edge-peer",10).Single(record=>record.Packet==2);
            Check(restored.Transfers[1].LocalPath==locked&&restored.Transfers[1].Status=="已保存"&&restored.Transfers[0].Status=="本机文件已删除"&&restored.GetLocalFiles().Single()==locked,"部分失败后的落盘状态错误");
            result=store.DeleteReceivedFiles(new[]{mixed});
            Check(result.DeletedCount==1&&result.SkippedDeletedCount==2&&result.FailedCount==0&&!File.Exists(locked)&&mixed.Transfers[1].Status=="本机文件已删除","解除占用后重试未完成："+result.Text);
            store.DeleteReceivedFile(one,single);Check(!File.Exists(single)&&one.Transfers[0].Status=="本机文件已删除","单个删除仍展开文件名中的环境变量");
            string saving=Path.Combine(keep,"保存中.txt"),other=Path.Combine(keep,"另一份.txt");File.WriteAllText(saving,"active");File.WriteAllText(other,"ready");
            ChatRecord active=make(5,new[]{saving,other}),aliasRecord=make(6,new[]{Path.Combine(keep,"..","keep","保存中.txt")});active.Transfers[0].Status="保存中";
            result=store.DeleteReceivedFiles(new[]{aliasRecord,active},true,(record,file)=>file.Status=="保存中");
            Check(result.DeletedCount==1&&result.SkippedBusyCount==1&&File.ReadAllText(saving)=="active"&&!File.Exists(other)&&active.Transfers[0].Status=="保存中"&&aliasRecord.Transfers[0].LocalPath!=null,"100%保存中或同路径旧记录未受到保护");
        }
        private static void ExpiredReReceiveRegression()
        {
            Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"expired-rereceive"));
            var settings=new AppSettings{CloseToTray=false,Notifications=false,AutoReceiveFiles=false,ReceiveFolder=Path.Combine(store.Root,"received"),Conversations=new[]{new SavedConversation{Endpoint="127.0.0.1:"+FreePort(),Login="expired-peer",Nickname="失效邀请设备"}}};
            using(var network=new LanService(settings,FreePort(),true,"expired-rereceive"))using(var main=new MainForm(network,store))
            {
                Peer peer=network.Peers.Single();string sha=new string('c',64);
                Func<int,string,string,bool,FileTransferState> file=(id,name,status,before)=>new FileTransferState{Id=id,Name=name,Size=4096,Modified=1,Sha256=sha,RelativePath=name,Status=status,Percent=status=="已暂停"?30:0,SavedBefore=before};
                // 未打开会话：暂停中的重新接收遇到撤销通知，只恢复删除态。
                var closed=new ChatRecord{PeerId=peer.Id,Packet=201,Sender=peer.Nickname,Text="[文件] 重收.bin",Time=DateTime.Now,State="已保存",HasAttachments=true,Transfers=new[]{file(0,"重收.bin","已暂停",true),file(1,"早已删除.bin","本机文件已删除",false)}};
                var mixed=new ChatRecord{PeerId=peer.Id,Packet=202,Sender=peer.Nickname,Text="[文件] 混合",Time=DateTime.Now,State="已接收 1 / 2",HasAttachments=true,Transfers=new[]{file(0,"重收2.bin","已暂停",true),file(1,"未接收.bin","已暂停",false)}};
                var opened=new ChatRecord{PeerId=peer.Id,Packet=203,Sender=peer.Nickname,Text="[文件] 会话内重收.bin",Time=DateTime.Now,State="已保存",HasAttachments=true,Transfers=new[]{file(0,"会话内重收.bin","本机文件已删除",false)}};
                foreach(ChatRecord record in new[]{closed,mixed,opened}){store.Append(record);main.TrackTransfer(record);}
                var attachment=new Attachment{Id=0,Name="重收.bin",Size=4096,Modified=1,Sha256=sha,RelativePath="重收.bin"};
                string partial=LanService.PartialPath(peer,201,attachment,SendBatch.Destination(settings.ReceiveFolder,attachment));File.WriteAllBytes(partial,new byte[64]);
                main.Show();
                main.CancelIncomingOffer(peer,closed,true);
                Check(closed.Transfers.All(item=>item.Status=="本机文件已删除"&&item.Percent==0)&&closed.State=="已保存"&&!File.Exists(partial),"失效邀请把已接收历史改成对方取消或残留断点");
                Check(Field<Label>(main,"status").Text.Contains("原邀请已失效"),"失效邀请缺少准确提示："+Field<Label>(main,"status").Text);
                ChatRecord saved=new LocalStore(store.Root).History(peer.Id,10).Single(record=>record.Packet==201);
                Check(saved.Transfers[0].Status=="本机文件已删除"&&saved.Transfers[0].SavedBefore&&saved.State=="已保存","恢复的删除态或标记未持久化");
                main.CancelIncomingOffer(peer,mixed,true);
                Check(mixed.Transfers[0].Status=="本机文件已删除"&&mixed.Transfers[1].Status=="对方已取消发送"&&mixed.State=="对方已取消发送"&&Field<Label>(main,"status").Text.Contains("对方已取消发送"),"同批真正未接收的附件未标记对方取消");
                // 会话内点「重新接收」后对方撤销：先记下曾保存，再恢复删除态。
                main.OpenChat(peer);ChatForm chat=main.ActiveChat;chat.QueueTask(opened,0);
                Check(opened.Transfers[0].SavedBefore&&opened.Transfers[0].Status!="本机文件已删除","重新接收未登记曾保存标记");
                chat.OfferCanceled(203);PumpFor(400);
                Check(opened.Transfers[0].Status=="本机文件已删除"&&opened.State=="已保存"&&Field<Label>(chat,"fileStatus").Text.Contains("原邀请已失效"),"会话内重新接收失效后状态错误："+opened.Transfers[0].Status+" / "+opened.State);
                Check(new LocalStore(store.Root).History(peer.Id,10).Single(record=>record.Packet==203).Transfers[0].Status=="本机文件已删除","会话内恢复未落盘");
                main.ExitApplication();
            }
        }
        private static void TransferOperationsRegression()
        {
            foreach(bool dark in new[]{false,true})
            {
                Theme.Configure(dark,"blue");var store=new LocalStore(Path.Combine(root,"transfer-actions-"+dark));
                var settings=new AppSettings{CloseToTray=false,Notifications=false,AutoReceiveFiles=false,ReceiveFolder=Path.Combine(store.Root,"received"),Conversations=new[]{new SavedConversation{Endpoint="127.0.0.1:"+FreePort(),Login="actions-peer",Nickname="传输操作设备"}}};
                using(var network=new LanService(settings,FreePort(),true,"transfer-actions"))using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                {
                    Peer peer=network.Peers.Single();string saved=Path.Combine(store.Root,"聊天接收.txt"),guestFile=Path.Combine(store.Root,"访客接收.txt");File.WriteAllText(saved,"saved");File.WriteAllText(guestFile,"guest");
                    var file=new FileTransferState{Id=0,Name="拒绝.bin",Status="已暂停",Size=4096,Modified=1,Sha256=new string('a',64),RelativePath="拒绝/空子目录/拒绝.bin",Percent=30};
                    var record=new ChatRecord{PeerId=peer.Id,Packet=101,Text="混合状态邀请",Sender=peer.Nickname,Time=DateTime.Now,State="已接收 1 / 3",HasAttachments=true,LocalFiles=new[]{saved},Transfers=new[]{file,new FileTransferState{Id=1,Name="聊天接收.txt",LocalPath=saved,Status="已保存",Percent=100},new FileTransferState{Id=2,Name="之前删除.txt",Status="本机文件已删除"}}};
                    var canceled=new ChatRecord{PeerId=peer.Id,Packet=102,Text="清理暂停断点",Sender=peer.Nickname,Time=DateTime.Now.AddMinutes(-2),HasAttachments=true,Transfers=new[]{new FileTransferState{Id=0,Name="暂停.bin",Status="已暂停",Size=4096,Modified=1,Sha256=new string('b',64),RelativePath="暂停/暂停.bin",Percent=40}}};
                    var guest=new ChatRecord{PeerId="browser-guests",Packet=103,Sender="浏览器访客",Text="访客接收记录",Time=DateTime.Now.AddMinutes(-1),State="已保存",HasAttachments=true,LocalFiles=new[]{guestFile},Transfers=new[]{new FileTransferState{Id=0,Name="访客接收.txt",LocalPath=guestFile,Status="已保存",Percent=100}}};
                    foreach(ChatRecord item in new[]{record,canceled,guest}){store.Append(item);main.TrackTransfer(item);}
                    Func<ChatRecord,string> partPath=item=>{FileTransferState state=item.Transfers[0];var attachment=new Attachment{Id=state.Id,Name=state.Name,Size=state.Size,Modified=state.Modified,Sha256=state.Sha256,RelativePath=state.RelativePath};return LanService.PartialPath(peer,item.Packet,attachment,SendBatch.Destination(settings.ReceiveFolder,attachment));};
                    string rejectedPart=partPath(record),canceledPart=partPath(canceled);File.WriteAllBytes(rejectedPart,new byte[128]);File.WriteAllBytes(canceledPart,new byte[128]);
                    main.Show();window.Show();PumpFor(60);ListBox list=Field<ListBox>(window,"list");Button secondary=Field<Button>(window,"secondary"),more=Field<Button>(window,"more");
                    Check(main.ActiveChat==null&&list.SelectedIndex==0&&secondary.Visible&&secondary.Text=="拒绝"&&Field<Button>(window,"action").Text=="继续接收","默认任务未选中或拒绝次操作缺失");
                    var footer=(TableLayoutPanel)secondary.Parent;Check(footer.ColumnCount==4&&footer.GetColumn(secondary)==2&&footer.GetColumn(more)==3,"次操作未位于主操作和菜单之间");
                    window.ClientSize=new Size(620,560);Application.DoEvents();foreach(Button button in footer.Controls.OfType<Button>().Where(button=>button.Visible))Check(footer.ClientRectangle.Contains(button.Bounds),"窄窗传输操作越界："+button.Text);SaveSnapshot(window,"transfer-refuse-"+(dark?"dark":"light"));
                    FinishDialogs(()=>secondary.PerformClick(),"拒绝这批文件？","拒绝",()=>store.IncomingAttachments(peer.Id,101).Single().Transfers[0].Status=="已拒绝");
                    ChatRecord refused=store.IncomingAttachments(peer.Id,101).Single();Check(refused.State==record.State&&refused.Transfers[1].Status=="已保存"&&refused.Transfers[2].Status=="本机文件已删除"&&File.Exists(saved)&&!File.Exists(rejectedPart)&&!Directory.Exists(Path.GetDirectoryName(rejectedPart)),"未打开会话拒绝误改已存附件或残留断点");
                    typeof(TransferWindow).GetMethod("RefreshRows",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);
                    RightClickTransfer(list,TransferIndex(list,101,1));ContextMenuStrip menu=Field<ContextMenuStrip>(window,"menu");Check(menu.Visible&&secondary.Text=="打开文件"&&Field<Button>(window,"action").Text=="打开文件位置","已保存文件次操作或右键菜单缺失");
                    foreach(string text in new[]{"打开文件","打开方式…","打开文件位置","删除本机文件","查看所在会话"})Check(menu.Items.Cast<ToolStripItem>().Any(item=>item.Text==text),"已存任务缺少菜单："+text);SaveMenu(menu,"transfer-saved-menu-"+(dark?"dark":"light"));
                    FinishDialogs(()=>menu.Items.Cast<ToolStripItem>().Single(item=>item.Text=="删除本机文件").PerformClick(),"删除这个文件？","删除文件");menu.Close();Check(!File.Exists(saved)&&store.IncomingAttachments(peer.Id,101).Single().Transfers[1].Status=="本机文件已删除","任务菜单删除文件未执行或未落盘");
                    more.PerformClick();Check(menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="重新接收"),"已删除聊天附件缺少重新接收入口");menu.Close();
                    RightClickTransfer(list,TransferIndex(list,102,0));Check(menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="继续接收")&&menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="继续当前列表中的接收")&&menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="暂停当前列表中的接收"),"待处理任务菜单缺少调度操作");
                    FinishDialogs(()=>menu.Items.Cast<ToolStripItem>().Single(item=>item.Text=="取消接收并清理断点").PerformClick(),null,null,()=>store.IncomingAttachments(peer.Id,102).Single().Transfers[0].Status=="已取消 · 点击重试");menu.Close();Check(!File.Exists(canceledPart)&&store.IncomingAttachments(peer.Id,102).Single().Transfers[0].Percent==0,"取消暂停任务未清断点或清进度");
                    RightClickTransfer(list,TransferIndex(list,103,0));Check(more.Visible&&menu.Visible&&secondary.Text=="打开文件"&&!menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="查看所在会话"),"访客文件菜单缺失或显示了假会话操作");SaveMenu(menu,"transfer-guest-menu-"+(dark?"dark":"light"));
                    FinishDialogs(()=>menu.Items.Cast<ToolStripItem>().Single(item=>item.Text=="删除本机文件").PerformClick(),"删除这个文件？","删除文件");menu.Close();Check(!File.Exists(guestFile)&&new LocalStore(store.Root).AttachmentHistory("browser-guests").Single().Transfers[0].Status=="本机文件已删除","访客本机文件删除未持久化");
                    window.Close();main.ExitApplication();
                }
            }
        }
        private static void ActiveTransferOperationsRegression()
        {
            foreach(bool reject in new[]{false,true})
            {
                Console.WriteLine("DETAIL 活跃接收开始："+(reject?"拒绝":"取消"));
                Theme.Configure(reject,"blue");var store=new LocalStore(Path.Combine(root,"active-actions-"+reject));TcpListener server=new TcpListener(IPAddress.Loopback,0);server.Start();
                using(var release=new ManualResetEventSlim(false))
                {
                    Task serving=Task.Run(()=>{try{using(TcpClient socket=server.AcceptTcpClient()){Stream stream=socket.GetStream();byte[] request=new byte[4096];stream.Read(request,0,request.Length);byte[] bytes=new byte[131072];stream.Write(bytes,0,bytes.Length);release.Wait(5000);}}catch(IOException){}catch(SocketException){}});
                    var settings=new AppSettings{CloseToTray=false,Notifications=false,AutoReceiveFiles=false,ReceiveFolder=Path.Combine(store.Root,"received"),Conversations=new[]{new SavedConversation{Endpoint=server.LocalEndpoint.ToString(),Login="active-peer",Nickname="真实停滞发送端"}}};
                    try
                    {
                        using(var network=new LanService(settings,FreePort(),true,"active-actions"))using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                        {
                            Peer peer=network.Peers.Single();string saved=Path.Combine(store.Root,"此前已存.txt");File.WriteAllText(saved,"keep");
                            var active=new FileTransferState{Id=0,Name="活跃.bin",Size=1048576,Modified=1,Sha256=new string('a',64),RelativePath="活跃/空目录/活跃.bin",Status="已暂停"};
                            var paused=new FileTransferState{Id=1,Name="同批暂停.bin",Size=4096,Modified=1,Sha256=new string('b',64),RelativePath="另一断点/同批暂停.bin",Status="已暂停",Percent=30};
                            var record=new ChatRecord{PeerId=peer.Id,Packet=201,Sender=peer.Nickname,Text="活跃接收取消边界",Time=DateTime.Now,State="已接收 1 / 4",HasAttachments=true,LocalFiles=new[]{saved},Transfers=new[]{active,paused,new FileTransferState{Id=2,Name="此前已存.txt",Status="已保存",Percent=100,LocalPath=saved},new FileTransferState{Id=3,Name="此前已删.txt",Status="本机文件已删除"}}};
                            store.Append(record);main.TrackTransfer(record);
                            Func<FileTransferState,string> partPath=file=>{var attachment=new Attachment{Id=file.Id,Name=file.Name,Size=file.Size,Modified=file.Modified,Sha256=file.Sha256,RelativePath=file.RelativePath};return LanService.PartialPath(peer,record.Packet,attachment,SendBatch.Destination(settings.ReceiveFolder,attachment));};
                            string activePart=partPath(active),pausedPart=partPath(paused);File.WriteAllBytes(pausedPart,new byte[128]);
                            main.Show();main.OpenChat(peer);Console.WriteLine("DETAIL 活跃接收排队");main.TransferAction(record,0,false);Pump(()=>main.ReceivingTask(record,0)&&File.Exists(activePart)&&new FileInfo(activePart).Length>0,"真实接收未形成非空活跃断点");Console.WriteLine("DETAIL 活跃断点已形成");
                            string disposable=Path.Combine(store.Root,"传输期间删除 中文 English.txt");File.WriteAllText(disposable,"delete independently");
                            var completed=new ChatRecord{PeerId=peer.Id,Packet=202,Text="独立已保存消息",Time=DateTime.Now,HasAttachments=true,LocalFiles=new[]{disposable},Transfers=new[]{new FileTransferState{Id=0,Name=Path.GetFileName(disposable),LocalPath=disposable,Status="已保存",Percent=100}}};main.TrySave(completed);
                            FinishDialogs(()=>main.DeleteTransferFile(completed,disposable),"删除这个文件？","删除文件");
                            Check(!File.Exists(disposable)&&main.ReceivingTask(record,0)&&File.Exists(activePart)&&File.Exists(saved),"删除独立文件中断了正在接收的文件");
                            Check(new LocalStore(store.Root).History(peer.Id,20).Single(item=>item.Packet==202).Transfers[0].Status=="本机文件已删除","传输期间删除未持久化");
                            window.Show();PumpFor(50);ListBox list=Field<ListBox>(window,"list");list.SelectedIndex=TransferIndex(list,201,0);Check(Field<Button>(window,"secondary").Text=="取消接收"&&Field<Button>(window,"action").Text=="暂停接收","活跃接收主次操作文案错误");
                            Console.WriteLine("DETAIL 传输窗口已选中活跃任务");if(reject)
                            {
                                RightClickTransfer(list,list.SelectedIndex);ContextMenuStrip menu=Field<ContextMenuStrip>(window,"menu");Check(menu.Items.Cast<ToolStripItem>().Any(item=>item.Text=="暂停并保留断点"),"活跃接收菜单没有暂停操作");
                                FinishDialogs(()=>menu.Items.Cast<ToolStripItem>().Single(item=>item.Text=="拒绝这批文件").PerformClick(),"拒绝这批文件？","拒绝",()=>!main.ReceivingTask(record,0)&&main.ActiveChat.PendingFileCount==0);menu.Close();
                            }
                            else
                            {
                                Field<Button>(window,"secondary").PerformClick();Pump(()=>!main.ReceivingTask(record,0)&&!File.Exists(activePart)&&store.IncomingAttachments(peer.Id,201).Single().Transfers[0].Status=="已取消 · 点击重试","直接取消接收未停止真实下载或清断点");
                            }
                            Console.WriteLine("DETAIL 活跃操作完成");ChatRecord stored=new LocalStore(store.Root).IncomingAttachments(peer.Id,201).Single();Check(!File.Exists(activePart)&&!Directory.Exists(Path.GetDirectoryName(activePart))&&File.ReadAllText(saved)=="keep"&&stored.Transfers[2].Status=="已保存"&&stored.Transfers[3].Status=="本机文件已删除","活跃取消/拒绝误删已保存文件、覆盖终态或残留断点目录");
                            Check(reject?stored.Transfers[0].Status=="已拒绝"&&stored.Transfers[1].Status=="已拒绝"&&!File.Exists(pausedPart):stored.Transfers[0].Percent==0&&stored.Transfers[1].Status=="已暂停"&&File.Exists(pausedPart)&&main.ActiveChat.PendingFileCount==2,"活跃操作清理范围或持久化状态错误");
                            Check(!Application.OpenForms.OfType<NoticeDialog>().Any()&&!Field<Label>(main,"status").Text.Contains("接收失败"),"正常取消或拒绝出现迟到失败提示");SaveSnapshot(window,"transfer-active-"+(reject?"reject-dark":"cancel-light"));window.Close();main.ExitApplication();
                        }
                    }
                    finally{release.Set();server.Stop();Check(serving.Wait(5000),"停滞接收测试进程未退出");}
                }
            }
            Theme.Configure(false,"blue");
        }
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            // 正式程序始终处于 Application.Run 的同步上下文中；测试没有常驻消息循环，模态弹窗结束会卸载自动安装的上下文，
            // 使 await 之后的界面代码落到线程池。固定安装一份，保持与正式程序相同的线程模型。
            
            root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);Directory.CreateDirectory(output);
            if(args.Length>2&&(args[2]=="sources"||args[2]=="source-layout"||args[2]=="source-location"))
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);WindowsFormsSynchronizationContext.AutoInstall=false;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                if(args[2]=="source-layout")Run("来源横排渲染：明暗主题、860与620最小尺寸、头像、搜索展开与溢出滚动",TransferSourceLayoutRegression);
                else if(args[2]=="source-location")Run("文件会话定位：旧历史异步读取、收发身份、已删除提示和旧回调隔离",TransferMessageLocationRegression);
                else TransferSourceRegressions();
                Console.WriteLine("TOTAL "+passed+" passed, "+failed+" failed"); return failed==0?0:1;
            }
            if(args.Length>2&&args[2]=="active-actions")
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);WindowsFormsSynchronizationContext.AutoInstall=false;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Run("传输任务真实停滞TCP：直接取消与活跃拒绝释放句柄、保留同批终态",ActiveTransferOperationsRegression);
                Console.WriteLine("TOTAL "+passed+" passed, "+failed+" failed");return failed==0?0:1;
            }
            string sample=Path.Combine(root,"资料 中文 English.txt");File.WriteAllText(sample,"访客下载原文 😀",new UTF8Encoding(false));
            if(args.Length>2&&args[2]=="server"){
                using(var server=new GuestTransfer(new[]{sample},Path.Combine(root,"browser-received"),true,true)){
                    File.WriteAllText(Path.Combine(output,"guest-url.txt"),server.Url("127.0.0.1"));Console.WriteLine("READY local guest test server");
                    for(int i=0;i<1200&&!File.Exists(Path.Combine(output,"stop-server"));i++)Thread.Sleep(1000);
                }return 0;
            }
            Run("访客鉴权、越权、跨站、HEAD与Range",()=>{
                using(var server=new GuestTransfer(new[]{sample},Path.Combine(root,"receive"),false,true)){
                    Check(Request(server.Port,"/api/guest").Code==403,"无链接授权仍可读取");Check(Request(server.Port,"/api/guest",server.Token,null,"GET","Origin: http://evil.invalid\r\n").Code==403,"跨站未拒绝");
                    var files=(object[])Data(Request(server.Port,"/api/guest",server.Token))["files"];string id=(string)((System.Collections.Generic.Dictionary<string,object>)files[0])["id"];
                    Reply all=Request(server.Port,"/file?id="+id,server.Token);Check(all.Body.SequenceEqual(File.ReadAllBytes(sample)),"下载字节变化");Check(Request(server.Port,"/file?id="+id,server.Token,null,"HEAD").Body.Length==0,"HEAD含正文");
                    Reply part=Request(server.Port,"/file?id="+id,server.Token,null,"GET","Range: bytes=2-7\r\n");Check(part.Code==206&&part.Body.SequenceEqual(all.Body.Skip(2).Take(6)),"Range边界错误");
                    Check(Request(server.Port,"/file?id=../outside",server.Token).Code==404,"路径越界");Check(Json(server,"/api/prepare",new{name="x",size=1}).Code==403,"只读会话允许上传");
                    foreach(string asset in new[]{"/","/guest.js","/guest.css","/qrcodegen.js"})Check(Request(server.Port,asset).Code==200,"网页资源缺失 "+asset);
                }
            });
            Run("访客分块续传、同名避让与完整提交",()=>{
                string folder=Path.Combine(root,"uploads");byte[] bytes=Encoding.UTF8.GetBytes("新内容 😀 分块上传");
                using(var server=new GuestTransfer(new string[0],folder,true,true)){
                    Check(Directory.Exists(folder),"首次接收未创建目标目录");File.WriteAllText(Path.Combine(folder,"same.txt"),"original");
                    string id=(string)Data(Json(server,"/api/prepare",new{name="same.txt",size=bytes.Length}))["id"];
                    Check(Request(server.Port,"/api/chunk?id="+id+"&offset=0",server.Token,bytes.Take(5).ToArray(),"POST").Code==200,"首块失败");
                    Check(Json(server,"/api/finish?id="+id,new{}).Code==409,"截断文件被提交");Check(Convert.ToInt64(Data(Request(server.Port,"/api/upload-status?id="+id,server.Token))["offset"])==5,"断点位置错误");
                    Check(Request(server.Port,"/api/chunk?id="+id+"&offset=0",server.Token,bytes,"POST").Code==409,"重复偏移未拒绝");
                    Check(Request(server.Port,"/api/chunk?id="+id+"&offset=5",server.Token,bytes.Skip(5).ToArray(),"POST").Code==200,"续传失败");Check(Json(server,"/api/finish?id="+id,new{}).Code==200,"提交失败");
                    Check(File.ReadAllText(Path.Combine(folder,"same.txt"))=="original","覆盖了原文件");Check(server.SavedFiles.Length==1&&File.ReadAllBytes(server.SavedFiles[0]).SequenceEqual(bytes),"保存内容不一致");
                    Check(Json(server,"/api/prepare",new{name="../escape",size=1}).Code==400,"接受恶意文件名");
                    string empty=(string)Data(Json(server,"/api/prepare",new{name="empty.txt",size=0}))["id"];Check(Json(server,"/api/finish?id="+empty,new{}).Code==200,"零字节文件失败");
                    Json(server,"/api/prepare",new{name="unfinished.bin",size=99});server.Dispose();Check(!Directory.EnumerateFiles(folder,"*.part").Any(),"关闭遗留未完成断点");Check(File.ReadAllText(Path.Combine(folder,"same.txt"))=="original","清理删除了用户文件");
                }
            });
            Run("访客取消归还配额和有效文件名额，重复取消不重复归还",()=>{
                using(var server=new GuestTransfer(new string[0],Path.Combine(root,"cancel-quota"),true,true))
                {
                    long large=6L*1024*1024*1024;
                    Reply prepared=Json(server,"/api/prepare",new{name="large.bin",size=large});Check(prepared.Code==200,"大文件准备失败");string id=(string)Data(prepared)["id"];
                    Check(Json(server,"/api/prepare",new{name="blocked.bin",size=large}).Code==409,"未取消时未限制总配额");
                    Check(Json(server,"/api/cancel?id="+id,new{}).Code==200,"取消大文件失败");
                    Reply replacement=Json(server,"/api/prepare",new{name="replacement.bin",size=large});Check(replacement.Code==200,"取消后未归还大文件配额");
                    Check(Json(server,"/api/cancel?id="+id,new{}).Code==410,"重复取消未保持取消终态");
                    Check(Json(server,"/api/prepare",new{name="overflow.bin",size=5L*1024*1024*1024}).Code==409,"重复取消多归还了配额");
                    Check(Json(server,"/api/cancel?id="+(string)Data(replacement)["id"],new{}).Code==200,"取消替换任务失败");
                    var ids=new List<string>();for(int i=0;i<100;i++){Reply ready=Json(server,"/api/prepare",new{name="empty-"+i+".txt",size=0});Check(ready.Code==200,"有效文件名额过早用完");ids.Add((string)Data(ready)["id"]);}
                    Check(Json(server,"/api/prepare",new{name="extra.txt",size=0}).Code==409,"超过100个有效任务仍获准上传");
                    Check(Json(server,"/api/finish?id="+ids[0],new{}).Code==200,"完整任务无法保存");Check(Json(server,"/api/cancel?id="+ids[0],new{}).Code==409,"已保存文件被取消");
                    Check(Json(server,"/api/cancel?id="+ids[1],new{}).Code==200,"取消有效任务失败");Check(Json(server,"/api/prepare",new{name="new-empty.txt",size=0}).Code==200,"取消后未释放100个文件名额");
                }
            });
            Run("访客取消历史有硬上限，禁止无限准备任务耗内存",()=>{
                using(var server=new GuestTransfer(new string[0],Path.Combine(root,"cancel-history"),true,true))
                {
                    for(int i=0;i<1024;i++){Reply ready=Json(server,"/api/prepare",new{name="empty.txt",size=0});Check(ready.Code==200,"取消历史过早阻断有效名额");Check(Json(server,"/api/cancel?id="+(string)Data(ready)["id"],new{}).Code==200,"取消历史任务失败");}
                    Check(Json(server,"/api/prepare",new{name="excess.txt",size=0}).Code==409,"任务历史没有1024条内存上限");
                }
            });
            Run("暂停保留断点并从实际偏移续传",PauseResume);
            Run("TLS接收暂停保留断点并完成SHA-256校验",()=>{
                string source=Path.Combine(root,"tls-source.bin"),target=Path.Combine(root,"tls-received.bin");
                byte[] block=new byte[1024*1024];new Random(73).NextBytes(block);using(FileStream outputFile=File.Create(source))for(int i=0;i<32;i++)outputFile.Write(block,0,block.Length);
                using(var sender=new LanService(new AppSettings{DeviceId=new string('a',32)},FreePort(),true,"pause-sender"))
                using(var receiver=new LanService(new AppSettings{DeviceId=new string('b',32)},FreePort(),true,"pause-receiver"))
                using(var cancel=new CancellationTokenSource()) {
                    sender.ConfigureSecurity(Path.Combine(root,"pause-sender-security"),(name,code)=>true);receiver.ConfigureSecurity(Path.Combine(root,"pause-receiver-security"),(name,code)=>true);sender.Start();receiver.Start();
                    sender.Probe(new IPEndPoint(IPAddress.Loopback,receiver.Port));receiver.Probe(new IPEndPoint(IPAddress.Loopback,sender.Port));Wait(()=>sender.Peers.Count>0&&receiver.Peers.Count>0,"TLS测试未发现设备");
                    IncomingMessage incoming=null;receiver.MessageReceived+=message=>incoming=message;sender.SendMessage(sender.Peers[0],"暂停",new[]{source});Wait(()=>incoming!=null,"TLS邀请未到达");
                    Attachment file=incoming.Files[0];string part=LanService.PartialPath(incoming.Peer,incoming.Record.Packet,file,target);
                    Task<string> download=receiver.ReceiveFileAsync(incoming.Peer,incoming.Record.Packet,file,target,null,cancel.Token,true,null,()=>true);
                    var clock=System.Diagnostics.Stopwatch.StartNew();while(!download.IsCompleted&&clock.ElapsedMilliseconds<8000){if(File.Exists(part)&&new FileInfo(part).Length>0){cancel.Cancel();break;}Thread.Sleep(1);}
                    try{download.GetAwaiter().GetResult();throw new Exception("未在传输期间截住暂停");}catch(OperationCanceledException){}
                    Check(File.Exists(part)&&new FileInfo(part).Length>0,"TLS暂停断点丢失");receiver.ReceiveFileAsync(incoming.Peer,incoming.Record.Packet,file,target,null,CancellationToken.None,true).GetAwaiter().GetResult();
                    Check(SecureIdentity.HashFile(target)==file.Sha256,"TLS暂停续传校验失败");
                }
            });
            Run("跨会话附件历史含旧页且忽略删除记录",()=>{
                var store=new LocalStore(Path.Combine(root,"archive"));var record=new ChatRecord{PeerId="test",Packet=1,Text="附件",HasAttachments=true,Transfers=new[]{new FileTransferState{Id=1,Name="one",Size=8}}};store.Append(record);
                for(int i=2;i<302;i++)store.Append(new ChatRecord{PeerId="test",Packet=i,Text="普通文字",Time=DateTime.Now});Check(store.AttachmentHistory("test").Count==1,"旧页附件遗漏");store.DeleteRecords("test",new[]{record});Check(store.AttachmentHistory("test").Count==0,"删除消息被任务页复活");
            });
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);WindowsFormsSynchronizationContext.AutoInstall=false;SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            TransferSourceRegressions();
            Run("多选删除：入站文件统计、出站保护、断点保留、确认取消及重启状态",BatchDeleteRegression);
            Run("多选删除：父目录缺失、中途占用继续其余文件并可重试、文件名含环境变量写法",BatchDeleteFailureRegression);
            Run("重新接收遇到失效邀请：恢复本机文件已删除、不改写接收历史并持久化",ExpiredReReceiveRegression);
            Run("传输任务：默认选中、直接拒绝、暂停断点清理、右键和访客文件删除",TransferOperationsRegression);
            Run("传输任务真实停滞TCP：直接取消与活跃拒绝释放句柄、保留同批终态",ActiveTransferOperationsRegression);
            Run("传输背景边界：极小高度、原生TopIndex=-1和ItemHeight=0不崩溃",()=>{
                Type type=typeof(TransferWindow).GetNestedType("TransferListBox",BindingFlags.NonPublic);
                using(var host=new Form())using(var list=(ListBox)Activator.CreateInstance(type,true))
                {
                    list.IntegralHeight=false;list.DrawMode=DrawMode.Normal;list.Items.Add("边界任务");host.Controls.Add(list);host.Show();Application.DoEvents();
                    using(var forced=new BoundaryListMessages(list.Handle))
                    {
                        foreach(int height in new[]{0,1,12})foreach(bool zero in new[]{false,true})
                        {
                            list.Size=new Size(120,height);forced.TopError=true;forced.ZeroHeight=zero;
                            Check(list.TopIndex==-1,"未模拟原生LB_ERR边界");if(zero)Check(list.ItemHeight==0,"未模拟原生零行高边界");
                            using(Bitmap image=EraseBackground(list))Check(image.Width>0,"背景边界绘制未完成");
                        }
                    }
                    host.Close();
                }
            });
            Run("传输排序与文件存在性：同时间稳定次序、静止和进度刷新复用缓存",()=>{
                Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"stable-actions"));DateTime time=DateTime.Now;string cached=Path.Combine(root,"cached-local.bin");File.WriteAllText(cached,"cache");
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"stable"))
                using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                {
                    main.Show();var file=new FileTransferState{Id=1,Name="本地文件.bin",Size=5,Status="接收中",LocalPath=cached};
                    main.TrackTransfer(new ChatRecord{PeerId="b",Packet=10,Sender="B",Time=time,Transfers=new[]{file}});
                    main.TrackTransfer(new ChatRecord{PeerId="a",Packet=10,Sender="A",Time=time,Transfers=new[]{new FileTransferState{Id=1,Name="同时间.txt",Size=1,Status="已暂停"}}});
                    main.TrackTransfer(new ChatRecord{PeerId="z",Packet=11,Sender="Z",Time=time,Transfers=new[]{new FileTransferState{Id=1,Name="较新.txt",Size=1,Status="已暂停"}}});
                    window.Show();PumpFor(80);ListBox list=Field<ListBox>(window,"list");Check(list.Items.Cast<object>().Select(item=>Field<ChatRecord>(item,"Record").PeerId).SequenceEqual(new[]{"z","a","b"}),"相同时间的任务排序不稳定");
                    list.SelectedIndex=2;Button action=Field<Button>(window,"action");Check(action.Text=="打开文件位置","已有文件未显示打开操作");File.Delete(cached);
                    var timer=Field<System.Windows.Forms.Timer>(window,"timer");Tick(timer);Check(action.Text=="打开文件位置","未变化刷新仍同步查询磁盘");file.Percent=30;Tick(timer);Check(action.Text=="打开文件位置","仅进度变化重复查询本地文件");
                    list.SelectedIndex=1;list.SelectedIndex=2;Check(action.Text=="继续接收","重新选择未核验文件存在性");File.WriteAllText(cached,"cache");file.Status="已暂停";Tick(timer);Check(action.Text=="打开文件位置","任务状态变化未重新核验文件");
                    window.Close();main.ExitApplication();
                }
            });
            Run("访客窗口：重复打开恢复同一个最小化窗口",()=>{
                var store=new LocalStore(Path.Combine(root,"guest-restore"));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"guest-restore"))using(var main=new MainForm(network,store))
                {
                    main.Show();MethodInfo open=typeof(MainForm).GetMethod("OpenGuest",BindingFlags.Instance|BindingFlags.NonPublic);open.Invoke(main,null);Application.DoEvents();GuestWindow guest=Field<GuestWindow>(main,"guestWindow");Check(guest!=null&&guest.Visible,"访客窗口未打开");
                    guest.WindowState=FormWindowState.Minimized;Application.DoEvents();open.Invoke(main,null);Application.DoEvents();Check(Object.ReferenceEquals(guest,Field<GuestWindow>(main,"guestWindow"))&&guest.WindowState==FormWindowState.Normal&&guest.Visible,"重复打开未恢复已有访客窗口");guest.Close();main.ExitApplication();
                }
            });
            Run("访客窗口：过滤不安全条目，开启、停止和重开刷新地址且保留接收入口",()=>{
                Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"guest-refresh"));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"guest-refresh"))
                using(var main=new MainForm(network,store))
                {
                    main.Show();MethodInfo open=typeof(MainForm).GetMethod("OpenGuest",BindingFlags.Instance|BindingFlags.NonPublic);open.Invoke(main,null);Application.DoEvents();GuestWindow guest=Field<GuestWindow>(main,"guestWindow");
                    Control picker=Controls(guest).Single(c=>c.AccessibleName=="临时传输网络地址");MethodInfo setItems=picker.GetType().GetMethod("SetItems",BindingFlags.Instance|BindingFlags.NonPublic);
                    setItems.Invoke(picker,new object[]{new[]{"192.0.2.90"}});open.Invoke(main,null);Check(picker.Text=="127.0.0.1","唤醒闲置窗口未剔除失效IP");
                    string unsafeName=Path.Combine(root,new string('测',81)+".txt");File.WriteAllText(unsafeName,"名称超过UTF-8字节上限");
                    Check(!Protocol.SafeFileName(Path.GetFileName(unsafeName)),"非法文件名测试资料不成立");
                    typeof(GuestWindow).GetMethod("AddFiles",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(guest,new object[]{new[]{sample,unsafeName,root,Path.Combine(root,"missing-file.txt")}});
                    Check(Field<ListBox>(guest,"files").Items.Count==1,"拖入非法条目未被跳过");Check(Field<Label>(guest,"state").Text.Contains("已忽略"),"跳过条目没有提示");
                    Field<ToggleSwitch>(guest,"upload").Checked=true;setItems.Invoke(picker,new object[]{new[]{"192.0.2.91"}});Field<Button>(guest,"start").PerformClick();
                    GuestTransfer session=Field<GuestTransfer>(guest,"session");Check(session!=null&&picker.Text=="127.0.0.1","开启前未刷新失效IP");
                    string id=(string)Data(Json(session,"/api/prepare",new{name="received.txt",size=0}))["id"];Check(Json(session,"/api/finish?id="+id,new{}).Code==200,"窗口接收文件失败");
                    setItems.Invoke(picker,new object[]{new[]{"192.0.2.92"}});guest.Stop();Check(picker.Text=="127.0.0.1","停止时未刷新局域网地址");
                    Check(Field<Button>(guest,"openTasks").Visible,"停止后隐藏了查看已接收入口");PumpFor(100);Check(main.TransferRecords.Any(r=>r.PeerId=="browser-guests"),"完成回调未保存访客任务");
                    Field<Button>(guest,"openTasks").PerformClick();Application.DoEvents();Check(Field<TransferWindow>(main,"transferWindow").Visible,"结束后的查看已接收按钮不能打开任务页");
                    SaveSnapshot(guest,"guest-stopped-received-light");guest.Close();main.ExitApplication();
                }
            });
            Run("访客拖入符号链接时跳过链接并保留普通文件",()=>{
                string link=Path.Combine(root,"guest-link.txt");
                bool createdSymlink=CreateSymbolicLink(link,sample,2);
                int linkError=createdSymlink?0:Marshal.GetLastWin32Error();
                if(!createdSymlink){Check(linkError==1314,"符号链接测试资料创建失败，Win32="+linkError);File.WriteAllText(link,"reparse-fallback");}
                var store=new LocalStore(Path.Combine(root,"guest-links"));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"guest-links"))
                using(var main=new MainForm(network,store))using(var guest=new GuestWindow(main,network))
                {
                    if(!createdSymlink){string fullLink=Path.GetFullPath(link);guest.ReadAttributes=info=>String.Equals(info.FullName,fullLink,StringComparison.OrdinalIgnoreCase)?(info.Attributes|FileAttributes.ReparsePoint):info.Attributes;}
                    main.Show();guest.Show();typeof(GuestWindow).GetMethod("AddFiles",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(guest,new object[]{new[]{sample,link}});
                    Check(Field<ListBox>(guest,"files").Items.Count==1,"符号链接混入待分享文件");Check(Field<Label>(guest,"state").Text.Contains("链接"),"跳过链接没有提示");guest.Close();main.ExitApplication();
                }
            });
            Run("已保存入站文件移除后显示路径提示，接收中操作保持原契约",()=>{
                foreach(bool dark in new[]{false,true})
                {
                    Theme.Configure(dark,"blue");var store=new LocalStore(Path.Combine(root,"missing-saved-"+dark));string local=Path.Combine(store.Root,"moved.bin");File.WriteAllText(local,"file");
                    using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,DarkMode=dark,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"missing-saved"))
                    using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                    {
                        main.Show();var file=new FileTransferState{Id=0,Name="已保存.bin",Size=4,Status="已保存",Percent=100,LocalPath=local};main.TrackTransfer(new ChatRecord{PeerId="browser-guests",Packet=1,Sender="浏览器访客",Time=DateTime.Now,Transfers=new[]{file}});
                        window.Show();PumpFor(60);ListBox list=Field<ListBox>(window,"list");list.SelectedIndex=0;Check(Field<Button>(window,"action").Text=="打开文件位置","已有文件打开操作异常");
                        File.Delete(local);list.SelectedIndex=-1;list.SelectedIndex=0;Check(Field<Label>(window,"detail").Text=="本机文件已移动或删除："+local,"缺失的已保存文件仍显示保存成功");
                        Check(!Field<Button>(window,"action").Enabled,"已完成且缺失文件仍可重新接收");SaveSnapshot(window,"transfer-missing-saved-"+(dark?"dark":"light"));
                        file.Status="接收中";typeof(TransferWindow).GetMethod("RefreshRows",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);Check(Field<Button>(window,"action").Text=="继续接收","未完成文件受已保存提示逻辑影响");
                        window.Close();main.ExitApplication();
                    }
                }
                Theme.Configure(false,"blue");
            });
            Run("传输定时刷新：静止零重绘、仅刷新变化行、隐藏和最小化恢复",()=>{
                Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"redraw"));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"redraw"))
                using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                {
                    main.Show();var file=new FileTransferState{Id=1,Name="中文 English-transfer.bin",Size=10485760,Percent=20,Status="接收中",BytesPerSecond=1048576};
                    main.TrackTransfer(new ChatRecord{PeerId="browser-guests",Packet=1,Sender="浏览器访客",Time=DateTime.Now,Transfers=new[]{file}});
                    main.TrackTransfer(new ChatRecord{PeerId="other",Packet=2,Sender="另一台设备",Time=DateTime.Now.AddMinutes(-1),Transfers=new[]{new FileTransferState{Id=1,Name="waiting.txt",Size=1024,Status="已暂停"}}});
                    window.Show();PumpFor(650);ListBox list=Controls(window).OfType<ListBox>().Single();Check(list.Items.Count==2,"未加载两条测试任务");list.SelectedIndex=0;Application.DoEvents();
                    var invalidated=new List<Rectangle>();int textChanges=0,visibilityChanges=0,ticks=0,sourceRedraws=0;
                    TransferSourceStrip sourceStrip=Field<TransferSourceStrip>(window,"sources");TransferSourceStrip.Source[] sourceSnapshot=Field<TransferSourceStrip.Source[]>(sourceStrip,"items");sourceStrip.Invalidated+=delegate{sourceRedraws++;};
                    list.Invalidated+=(sender,e)=>invalidated.Add(e.InvalidRect);
                    foreach(Label label in new[]{Field<Label>(window,"summary"),Field<Label>(window,"detail"),Field<Label>(window,"empty")})label.TextChanged+=delegate{textChanges++;};
                    Button action=Field<Button>(window,"action"),more=Field<Button>(window,"more");action.TextChanged+=delegate{textChanges++;};action.VisibleChanged+=delegate{visibilityChanges++;};more.VisibleChanged+=delegate{visibilityChanges++;};
                    System.Windows.Forms.Timer timer=Field<System.Windows.Forms.Timer>(window,"timer");timer.Tick+=delegate{ticks++;};
                    PumpFor(1150);Check(ticks>=2,"真实500ms定时器未运行");Check(invalidated.Count==0,"无变化仍触发列表重绘");Check(textChanges==0&&visibilityChanges==0,"静止时反复修改摘要或操作控件");Check(more.Visible,"访客任务缺少文件操作菜单");
                    Check(sourceRedraws==0&&Object.ReferenceEquals(sourceSnapshot,Field<TransferSourceStrip.Source[]>(sourceStrip,"items")),"静止时来源横排被重建或重绘");
                    file.Percent=35;Tick(timer);Check(invalidated.Count==1&&invalidated[0]==list.GetItemRectangle(0),"进度变化未局限于对应任务行");Check(Field<Label>(window,"summary").Text.Contains("3.5 MB"),"总摘要未随进度更新");
                    Check(sourceRedraws==0&&Object.ReferenceEquals(sourceSnapshot,Field<TransferSourceStrip.Source[]>(sourceStrip,"items")),"进度刷新重建或重绘了来源横排");
                    invalidated.Clear();file.BytesPerSecond+=1;Tick(timer);Check(invalidated.Count==0,"显示速度与剩余时间未变仍重绘");
                    file.Status="已暂停";Tick(timer);Check(invalidated.Count==1&&invalidated[0]==list.GetItemRectangle(0),"状态变化没有精确刷新");Check(Field<Label>(window,"detail").Text=="已暂停","任务详情未更新状态");
                    invalidated.Clear();file.Error="连接中断，请继续接收";Tick(timer);Check(invalidated.Count==1&&Field<Label>(window,"detail").Text==file.Error,"错误变化未更新任务和详情");
                    invalidated.Clear();file.LocalPath=sample;Tick(timer);Check(invalidated.Count==1&&action.Text=="打开文件位置","保存位置变化未更新任务操作");Check(Field<ToolTip>(window,"tips").GetToolTip(Field<Label>(window,"detail"))==sample,"保存位置提示未更新");
                    object selected=list.SelectedItem;string fingerprint=Field<string>(selected,"Fingerprint");window.Hide();Application.DoEvents();invalidated.Clear();file.Percent++;Tick(timer);
                    Check(invalidated.Count==0&&Field<string>(selected,"Fingerprint")==fingerprint,"隐藏时仍定时刷新");window.Show();Check(Field<string>(selected,"Fingerprint")!=fingerprint,"重新显示未立即刷新");
                    window.WindowState=FormWindowState.Minimized;Application.DoEvents();fingerprint=Field<string>(selected,"Fingerprint");invalidated.Clear();file.Percent++;Tick(timer);
                    Check(invalidated.Count==0&&Field<string>(selected,"Fingerprint")==fingerprint,"最小化时仍定时刷新");window.WindowState=FormWindowState.Normal;
                    Check(Field<string>(selected,"Fingerprint")!=fingerprint,"从最小化恢复未立即刷新");Check(Object.ReferenceEquals(selected,list.SelectedItem),"进度刷新丢失选中条目");
                    window.Close();main.ExitApplication();
                }
            });
            Run("传输列表背景：保留条目像素、填充尾部与空列表、保留原生绘制",()=>{
                foreach(bool dark in new[]{false,true})
                {
                    Theme.Configure(dark,"blue");var store=new LocalStore(Path.Combine(root,"erase-"+dark));
                    using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"erase"))
                    using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                    {
                        main.Show();main.TrackTransfer(new ChatRecord{PeerId="one",Packet=1,Sender="测试设备",Time=DateTime.Now,Transfers=new[]{new FileTransferState{Id=1,Name="文件.txt",Size=5,Status="接收中"}}});
                        window.Show();PumpFor(80);ListBox list=Controls(window).OfType<ListBox>().Single();Check(list.Items.Count==1,"背景测试任务缺失");
                        Check(list.DrawMode==DrawMode.OwnerDrawFixed,"破坏原生OwnerDrawFixed模式");Check(!(bool)typeof(Control).GetMethod("GetStyle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(list,new object[]{ControlStyles.UserPaint}),"错误开启UserPaint");
                        int bottom=list.GetItemRectangle(0).Bottom;Check(bottom<list.ClientSize.Height,"测试窗口没有尾部空白");
                        using(Bitmap image=EraseBackground(list)){Check(image.GetPixel(2,2).ToArgb()==Color.Magenta.ToArgb(),"背景擦除清空了自绘条目区域");Check(image.GetPixel(2,bottom+1).ToArgb()==list.BackColor.ToArgb(),"尾部空白未使用当前主题背景");}
                        Controls(window).OfType<TextBox>().Single().Text="不存在";Check(list.Items.Count==0,"空列表测试过滤失败");
                        using(Bitmap image=EraseBackground(list)){Check(image.GetPixel(2,2).ToArgb()==list.BackColor.ToArgb()&&image.GetPixel(2,image.Height-2).ToArgb()==list.BackColor.ToArgb(),"空列表未填充整个客户区");}
                        window.Close();main.ExitApplication();
                    }
                }
            });
            Run("聊天顶部：传输入口、既有控件查找、提示、窄窗和明暗图标",()=>{
                foreach(bool dark in new[]{false,true})
                {
                    Theme.Configure(dark,"blue");var store=new LocalStore(Path.Combine(root,"chat-transfer-"+dark));
                    var settings=new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received"),Conversations=new[]{new SavedConversation{Endpoint="127.0.0.1:"+FreePort(),Login="chat-peer",Nickname="中文 English 很长的聊天联系人名称"}}};
                    using(var network=new LanService(settings,FreePort(),true,"chat-transfer"))using(var main=new MainForm(network,store))
                    {
                        main.Show();main.OpenChat(network.Peers.Single());ChatForm chat=main.ActiveChat;Application.DoEvents();
                        string[] names={"传输任务","聊天记录","会话菜单"};Button[] buttons=names.Select(name=>Controls(chat).OfType<Button>().Single(button=>button.AccessibleName==name)).ToArray();
                        TableLayoutPanel head=Field<TableLayoutPanel>(chat,"head");Check(head.ColumnCount==5,"顶部栏列数未更新");Check(!Controls(chat).Any(control=>control.AccessibleName=="收到的文件"||control.AccessibleName=="收到的文件列表"),"聊天页仍保留收到文件按钮或抽屉");
                        for(int i=0;i<buttons.Length;i++){Check(head.GetColumn(buttons[i])==i+2,"顶部按钮顺序错误");Check(Field<ToolTip>(chat,"headTips").GetToolTip(buttons[i])==names[i],"顶部按钮缺少提示");}
                        Check(((RoundedButton)buttons[0]).Glyph=="transfer"&&Theme.MenuGlyph("传输任务…")=="transfer"&&Theme.MenuGlyph("临时浏览器传输…")=="link","传输或访客菜单图标未匹配");
                        foreach(int width in new[]{1120,720})
                        {
                            main.ClientSize=new Size(width,680);Application.DoEvents();
                            foreach(Button button in buttons)Check(button.Visible&&button.Width>0&&head.ClientRectangle.Contains(button.Bounds),"窄窗顶部按钮溢出或隐藏："+button.AccessibleName);
                            using(var image=new Bitmap(head.Width,head.Height)){head.DrawToBitmap(image,new Rectangle(Point.Empty,head.Size));image.Save(Path.Combine(output,"chat-header-"+(dark?"dark":"light")+"-"+width+".png"));}
                        }
                        buttons[0].PerformClick();Application.DoEvents();TransferWindow window=Field<TransferWindow>(main,"transferWindow");Check(window!=null&&window.Visible,"聊天传输按钮未打开任务窗口");
                        window.WindowState=FormWindowState.Minimized;buttons[0].PerformClick();Check(Object.ReferenceEquals(window,Field<TransferWindow>(main,"transferWindow"))&&window.WindowState==FormWindowState.Normal,"重复点击未恢复已有传输窗口");
                        window.Close();main.ExitApplication();
                    }
                }
            });
            Run("传输窗口记忆：防抖、等值事件不保存、关闭落盘与重启恢复",()=>{
                Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"transfer-placement"));Rectangle area=Screen.PrimaryScreen.WorkingArea;
                Rectangle wanted=new Rectangle(area.Left+20,area.Top+24,Math.Min(820,area.Width-40),Math.Min(580,area.Height-48));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"placement"))
                using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                {
                    main.Show();window.Show();window.Bounds=wanted;
                    Pump(()=>store.LoadSettings().TransferWindow!=null&&store.LoadSettings().TransferWindow.X==wanted.X,"传输窗口防抖未自动落盘");CheckPlacement(store.LoadSettings().TransferWindow,wanted,false,"位置或大小未持久化");
                    System.Windows.Forms.Timer save=Field<System.Windows.Forms.Timer>(window,"windowSaveTimer");Check(!save.Enabled,"保存后防抖定时器未停止");
                    typeof(TransferWindow).GetMethod("TrackWindowPlacement",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{window,EventArgs.Empty});Check(!save.Enabled,"等值位置事件重复启动保存");
                    wanted.Offset(4,6);window.Bounds=wanted;window.Close();CheckPlacement(store.LoadSettings().TransferWindow,wanted,false,"关闭未立即保存最新位置");main.ExitApplication();
                }
                using(var network=new LanService(store.LoadSettings(),FreePort(),true,"placement-restored"))using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                {
                    main.Show();window.Show();Application.DoEvents();Check(window.StartPosition==FormStartPosition.Manual&&window.Bounds==wanted,"重启未恢复普通窗口几何："+window.Bounds);window.Close();main.ExitApplication();
                }
            });
            Run("传输窗口落盘边界：未变化关闭不写盘、主窗销毁后不保存",()=>{
                var store=new LocalStore(Path.Combine(root,"placement-close-boundary"));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"close-boundary"))using(var main=new MainForm(network,store))
                {
                    main.Show();string settings=Path.Combine(store.Root,"settings.json");
                    using(var window=new TransferWindow(main))
                    {
                        window.Show();Pump(()=>!Field<bool>(window,"placementPending"),"初始窗口位置未保存");DateTime sentinel=DateTime.UtcNow.AddHours(-2);File.SetLastWriteTimeUtc(settings,sentinel);DateTime before=File.GetLastWriteTimeUtc(settings);
                        window.Close();Check(File.GetLastWriteTimeUtc(settings)==before,"未移动或缩放窗口仍在关闭时写盘");
                    }
                    using(var window=new TransferWindow(main))
                    {
                        window.Show();Application.DoEvents();Check(!Field<bool>(window,"placementPending"),"相同已保存位置不应待落盘");window.Location=new Point(window.Left+3,window.Top+3);Check(Field<bool>(window,"placementPending"),"移动未生成待落盘状态");
                        main.Dispose();DateTime sentinel=DateTime.UtcNow.AddHours(-3);File.SetLastWriteTimeUtc(settings,sentinel);DateTime before=File.GetLastWriteTimeUtc(settings);
                        window.Dispose();Check(File.GetLastWriteTimeUtc(settings)==before,"主窗已销毁时仍保存传输窗口位置");
                    }
                }
            });
            Run("传输窗口记忆：最大化、最小化关闭、重开与还原尺寸",()=>{
                var store=new LocalStore(Path.Combine(root,"transfer-maximized"));Rectangle area=Screen.PrimaryScreen.WorkingArea;
                Rectangle wanted=new Rectangle(area.Left+32,area.Top+32,Math.Min(800,area.Width-64),Math.Min(560,area.Height-64));
                var settings=new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received"),TransferWindow=new WindowPlacement{X=wanted.X,Y=wanted.Y,Width=wanted.Width,Height=wanted.Height}};
                using(var network=new LanService(settings,FreePort(),true,"maximized"))using(var main=new MainForm(network,store))
                {
                    main.Show();main.OpenTransfers();TransferWindow window=Field<TransferWindow>(main,"transferWindow");Application.DoEvents();Check(window.Bounds==wanted,"初始记忆尺寸未恢复");
                    window.WindowState=FormWindowState.Maximized;window.WindowState=FormWindowState.Minimized;main.OpenTransfers();Check(window.WindowState==FormWindowState.Maximized,"防抖落盘前重开丢失最大化状态");
                    window.WindowState=FormWindowState.Minimized;Application.DoEvents();window.Close();
                    CheckPlacement(store.LoadSettings().TransferWindow,wanted,true,"最小化关闭覆盖了最大化或普通尺寸");main.OpenTransfers();window=Field<TransferWindow>(main,"transferWindow");Application.DoEvents();Check(window.WindowState==FormWindowState.Maximized,"重开未恢复最大化");
                    window.WindowState=FormWindowState.Minimized;main.OpenTransfers();Check(window.WindowState==FormWindowState.Maximized,"打开已有最小化窗口丢失最大化状态");
                    window.WindowState=FormWindowState.Normal;Application.DoEvents();Check(window.Bounds==wanted,"最大化还原后尺寸变化："+window.Bounds);window.Close();CheckPlacement(store.LoadSettings().TransferWindow,wanted,false,"还原后仍保存为最大化");main.ExitApplication();
                }
            });
            Run("传输窗口记忆：旧配置默认值、异常尺寸与屏外钳制",()=>{
                var store=new LocalStore(Path.Combine(root,"transfer-offscreen"));File.WriteAllText(Path.Combine(store.Root,"settings.json"),"{\"Nickname\":\"旧配置\"}",new UTF8Encoding(false));Check(store.LoadSettings().TransferWindow==null,"旧配置未保持默认null");
                foreach(int size in new[]{0,-1,Int32.MaxValue})
                {
                    var settings=new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received"),TransferWindow=new WindowPlacement{X=Int32.MaxValue,Y=Int32.MinValue,Width=size,Height=size}};
                    using(var network=new LanService(settings,FreePort(),true,"offscreen"))using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                    {
                        main.Show();window.Show();Application.DoEvents();Check(window.Width>=window.MinimumSize.Width&&window.Height>=window.MinimumSize.Height,"窗口未尊重最小尺寸");
                        if(size>0)Check(Screen.FromControl(window).WorkingArea.Contains(window.Bounds),"极大或屏外配置未钳制回工作区");else Check(window.StartPosition==FormStartPosition.CenterScreen,"无效尺寸未回退默认位置");
                        Rectangle actual=window.Bounds;window.Close();CheckPlacement(store.LoadSettings().TransferWindow,actual,false,"校正后的窗口几何未保存");main.ExitApplication();
                    }
                }
            });
            Run("保存其他设置保留传输窗口记忆",()=>{
                var store=new LocalStore(Path.Combine(root,"transfer-settings"));Rectangle area=Screen.PrimaryScreen.WorkingArea;Rectangle wanted=new Rectangle(area.Left+16,area.Top+16,780,540);
                var settings=new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received"),TransferWindow=new WindowPlacement{X=wanted.X,Y=wanted.Y,Width=wanted.Width,Height=wanted.Height,Maximized=true}};
                using(var network=new LanService(settings,FreePort(),true,"settings"))using(var main=new MainForm(network,store))
                {
                    main.Show();Application.DoEvents();
                    using(var save=new System.Windows.Forms.Timer{Interval=40})
                    {
                        save.Tick+=delegate{SettingsDialog dialog=Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault();if(dialog==null)return;save.Stop();Controls(dialog).OfType<Button>().Single(button=>button.Text=="保存").PerformClick();};
                        save.Start();main.OpenSettings();
                    }
                    CheckPlacement(network.Settings.TransferWindow,wanted,true,"保存设置替换了内存中的传输窗口记忆");CheckPlacement(store.LoadSettings().TransferWindow,wanted,true,"保存设置丢失持久化的传输窗口记忆");main.ExitApplication();
                }
            });
            Run("传输页明暗主题、窄窗、搜索与空状态",()=>{
                foreach(bool dark in new[]{false,true}){
                    Theme.Configure(dark,"blue");var settings=new AppSettings{CloseToTray=false,ReceiveFolder=Path.Combine(root,"ui-received")};var store=new LocalStore(Path.Combine(root,"ui-"+dark));
                    using(var network=new LanService(settings,FreePort(),true,"ui"))using(var main=new MainForm(network,store))using(var window=new TransferWindow(main)){
                        main.Show();for(int i=0;i<6;i++)main.TrackTransfer(new ChatRecord{PeerId="device:"+i,Packet=i,Sender="我的设备 "+i,Time=DateTime.Now.AddMinutes(-i),Outgoing=i%2==0,Transfers=new[]{new FileTransferState{Id=1,Name=i==0?"很长的中文文件名称与 English-2026-Project-delivery-资料整理.zip":"项目资料 "+i+".pdf",Size=134217728,Percent=i*15,Status=i==0?"接收中":i==1?"已保存":i==2?"已暂停":"接收失败 · 点击续传",BytesPerSecond=i==0?10485760:0}}});
                        window.Show();Application.DoEvents();Thread.Sleep(600);Application.DoEvents();ListBox list=Controls(window).OfType<ListBox>().Single();Check(list.Items.Count==6,"任务未聚合");list.SelectedIndex=0;
                        foreach(int width in new[]{860,620}){window.ClientSize=new Size(width,560);Application.DoEvents();using(var image=new Bitmap(window.Width,window.Height)){window.DrawToBitmap(image,new Rectangle(Point.Empty,window.Size));image.Save(Path.Combine(output,"transfers-"+(dark?"dark":"light")+"-"+width+".png"));}}
                        TextBox input=Controls(window).OfType<TextBox>().Single();input.Text="不存在的文件";Application.DoEvents();Check(list.Items.Count==0,"搜索未过滤");Check(Controls(window).OfType<Label>().Any(l=>l.Visible&&l.Text.Contains("没有匹配")),"空状态未显示");window.Close();main.ExitApplication();
                    }
                }
            });
            Run("访客窗口记忆：防抖、关闭落盘、最大化恢复与保留设置",()=>{
                Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"guest-placement"));Rectangle area=Screen.PrimaryScreen.WorkingArea;
                Rectangle wanted=new Rectangle(area.Left+24,area.Top+24,Math.Min(920,area.Width-48),Math.Min(600,area.Height-48));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"guest-place"))
                using(var main=new MainForm(network,store))using(var guest=new GuestWindow(main,network))
                {
                    main.Show();guest.Show();guest.Bounds=wanted;
                    Pump(()=>store.LoadSettings().GuestWindow!=null&&store.LoadSettings().GuestWindow.X==wanted.X,"访客窗口防抖未自动落盘");CheckPlacement(store.LoadSettings().GuestWindow,wanted,false,"访客位置或大小未持久化");
                    wanted.Offset(6,4);guest.Bounds=wanted;guest.Close();CheckPlacement(store.LoadSettings().GuestWindow,wanted,false,"关闭未立即保存访客位置");main.ExitApplication();
                }
                using(var network=new LanService(store.LoadSettings(),FreePort(),true,"guest-restore-place"))using(var main=new MainForm(network,store))
                {
                    main.Show();MethodInfo open=typeof(MainForm).GetMethod("OpenGuest",BindingFlags.Instance|BindingFlags.NonPublic);open.Invoke(main,null);Application.DoEvents();
                    GuestWindow guest=Field<GuestWindow>(main,"guestWindow");Check(guest.StartPosition==FormStartPosition.Manual&&guest.Bounds==wanted,"重启未恢复访客窗口几何："+guest.Bounds);
                    guest.WindowState=FormWindowState.Maximized;guest.WindowState=FormWindowState.Minimized;open.Invoke(main,null);Application.DoEvents();
                    Check(guest.WindowState==FormWindowState.Maximized,"最小化重开丢失访客最大化状态");
                    guest.WindowState=FormWindowState.Minimized;Application.DoEvents();guest.Close();
                    CheckPlacement(store.LoadSettings().GuestWindow,wanted,true,"最小化关闭覆盖访客最大化状态");
                    using(var save=new System.Windows.Forms.Timer{Interval=40})
                    {
                        save.Tick+=delegate{SettingsDialog dialog=Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault();if(dialog==null)return;save.Stop();Controls(dialog).OfType<Button>().Single(button=>button.Text=="保存").PerformClick();};
                        save.Start();main.OpenSettings();
                    }
                    CheckPlacement(store.LoadSettings().GuestWindow,wanted,true,"保存设置丢失访客窗口记忆");main.ExitApplication();
                }
            });
            Run("访客窗口与文字弹窗：MD3分层、明暗适配、防裁切与无左图校验",()=>{
                string docA=Path.Combine(root,"项目需求说明-20261124.pdf"),docB=Path.Combine(root,"设计稿_最终确定版.png"),docC=Path.Combine(root,"归档打包-VeryLongAssetName-Final.zip");
                File.WriteAllBytes(docA,new byte[245760]);File.WriteAllBytes(docB,new byte[1048576]);File.WriteAllBytes(docC,new byte[4194304]);
                foreach(bool dark in new[]{false,true})
                {
                    Theme.Configure(dark,"blue");string theme=dark?"dark":"light";
                    var store=new LocalStore(Path.Combine(root,"guest-ui-"+theme));
                    using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,DarkMode=dark,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"guest-ui-"+theme))
                    using(var main=new MainForm(network,store))using(var guest=new GuestWindow(main,network))
                    {
                        main.Show();guest.Show();Application.DoEvents();
                        Check(guest.ClientSize.Width>=880&&guest.ClientSize.Height>=580,"访客默认窗口过小："+guest.ClientSize);
                        RoundedPanel filesPane=Field<RoundedPanel>(guest,"filesPane");Panel inspectorPane=Field<Panel>(guest,"inspectorPane"),emptyPanel=Field<Panel>(guest,"emptyPanel");
                        Control picker=Controls(guest).Single(c=>c.AccessibleName=="临时传输网络地址");
                        Check(picker.Text=="127.0.0.1"&&picker.Height>=30,"网络地址选择器文本或高度异常："+picker.Text+" "+picker.Height);
                        Check(!filesPane.Bounds.IntersectsWith(inspectorPane.Bounds),"双栏模式下文件区与控制栏重叠");
                        Check(inspectorPane.Left>filesPane.Right,"默认尺寸未启用左右双栏布局："+filesPane.Bounds+" / "+inspectorPane.Bounds);
                        Color filesFill=filesPane.Fill;Check(filesFill.ToArgb()!=inspectorPane.BackColor.ToArgb(),"主文件区与外层底色无层次差异");
                        Check(emptyPanel.Height>=140,"默认空状态高度不足："+emptyPanel.Height);
                        foreach(Button b in Controls(guest).OfType<Button>().Where(b=>b.Visible))Check(b.Parent.ClientRectangle.Contains(b.Bounds),"访客按钮越界："+b.Text);
                        SaveSnapshot(guest,"guest-empty-"+theme+"-default");

                        guest.Size=guest.MinimumSize;Application.DoEvents();
                        Check(!filesPane.Bounds.IntersectsWith(inspectorPane.Bounds),"窄窗堆叠模式下文件区与控制栏重叠");
                        Check(emptyPanel.Height>=110,"窄窗空状态高度过小导致文字裁切："+emptyPanel.Height);
                        Check(picker.Bounds.Right<=inspectorPane.Width&&picker.Height>=30,"窄窗地址栏异常："+picker.Bounds);
                        foreach(Button b in Controls(guest).OfType<Button>().Where(b=>b.Visible))Check(b.Parent.ClientRectangle.Contains(b.Bounds),"窄窗按钮越界："+b.Text);
                        if(!dark)SaveSnapshot(guest,"guest-empty-light-min");

                        guest.ClientSize=new Size(920,620);Application.DoEvents();
                        typeof(GuestWindow).GetMethod("AddFiles",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(guest,new object[]{new[]{docA,docB,docC}});
                        Controls(guest).OfType<Button>().Single(b=>b.Text=="开启临时传输").PerformClick();Application.DoEvents();
                        TextBox linkBox=Controls(guest).OfType<TextBox>().Single(t=>t.AccessibleName=="临时传输链接");
                        Check(linkBox.Text.StartsWith("http://127.0.0.1:"),"激活后链接未生成："+linkBox.Text);
                        SaveSnapshot(guest,"guest-active-"+theme+"-split");
                        guest.Close();main.ExitApplication();
                    }
                    using(var confirm=new ConfirmDialog("删除这条消息？","仅删除这台电脑上的消息记录，对方的记录和已保存的附件文件会保留。","删除",true))
                    {
                        confirm.Show();Application.DoEvents();RichTextBox details=Controls(confirm).OfType<RichTextBox>().Single(r=>r.AccessibleName=="确认详情");
                        Check(details.Width>=340,"确认弹窗文本仍被左侧多余图标挤压宽度："+details.Width);
                        SaveSnapshot(confirm,"confirm-delete-"+theme);confirm.Close();
                    }
                    if(!dark)
                    {
                        using(var remote=new ConfirmDialog("是否允许远程控制？","127.0.0.1 请求查看屏幕并操作此电脑。\n可随时按 Ctrl+Alt+F12 停止。","允许本次控制",false,"始终允许此设备")){remote.Show();Application.DoEvents();SaveSnapshot(remote,"confirm-remote-light");remote.Close();}
                        using(var fileDel=new ConfirmDialog("删除这个文件？",docA+"\n\n仅删除这个本机文件，聊天记录与对方文件保留。此操作无法撤销。","删除文件",true)){fileDel.Show();Application.DoEvents();SaveSnapshot(fileDel,"confirm-file-light");fileDel.Close();}
                        using(var notice=new NoticeDialog("无法共享文件夹","文件夹可能已被移动，或当前账号没有读取权限。\n请选择仍可访问的目录后重试。")){notice.Show();Application.DoEvents();RichTextBox d=Controls(notice).OfType<RichTextBox>().Single(r=>r.AccessibleName=="提示详情");Check(d.Width>=340,"提示弹窗存在多余左栏挤压");SaveSnapshot(notice,"notice-light");notice.Close();}
                        using(var fwd=new ForwardDialog(new[]{new Peer{Nickname="研发工作站",Endpoint=new IPEndPoint(IPAddress.Parse("192.168.1.24"),2425),Online=true},new Peer{Nickname="设计组电脑",Endpoint=new IPEndPoint(IPAddress.Parse("192.168.1.38"),2425),Online=true}})){fwd.Show();Application.DoEvents();SaveSnapshot(fwd,"forward-light");fwd.Close();}
                    }
                }
                Theme.Configure(false,"blue");
            });
            Run("长说明触顶后自动滚动并保留中文、英文和尾部原文",()=>{
                foreach(bool dark in new[]{false,true})
                {
                    Theme.Configure(dark,"blue");using(var dialog=new ConfirmDialog("长说明","短说明","继续",false))
                    {
                        dialog.Show();Application.DoEvents();RichTextBox details=Controls(dialog).OfType<RichTextBox>().Single(r=>r.AccessibleName=="确认详情");Check(details.ScrollBars==RichTextBoxScrollBars.None,"短说明误开滚动条");
                        string text=String.Join("\n",Enumerable.Range(1,70).Select(i=>i+" 行 中文 English 😀"))+"\n尾部不会丢失";details.Text=text;PumpFor(80);
                        DialogLayout.FitTextHeight(dialog,details.Parent,2000,490);PumpFor(80);float scale;using(Graphics g=dialog.CreateGraphics())scale=Math.Max(1F,g.DpiY/96F);
                        int limit=Math.Min(Screen.FromControl(dialog).WorkingArea.Height-(int)Math.Round(64*scale),(int)Math.Round(490*scale));Check(dialog.ClientSize.Height<=limit,"弹窗超过当前DPI或屏幕工作区上限");
                        Check(details.ScrollBars==RichTextBoxScrollBars.Vertical,"说明触顶后没有启用滚动条");Check(details.Text.Replace("\r\n","\n")==text,"长说明原文被裁掉或改写");
                        details.Select(details.TextLength,0);details.ScrollToCaret();Application.DoEvents();Point last=details.GetPositionFromCharIndex(details.TextLength-1);Check(last.Y>=0&&last.Y<details.ClientSize.Height,"长说明尾部无法滚动到可见区域");
                        SaveSnapshot(dialog,"confirm-long-scroll-"+(dark?"dark":"light"));dialog.Close();
                    }
                }
                Theme.Configure(false,"blue");
            });
            Run("富文本配对码、昵称标点与主题切换保持可读，关闭悬停色正确",()=>{
                Theme.Configure(false,"blue");using(var regular=new ModernWindow())using(var confirm=new ConfirmDialog("核对安全身份","正在与「研发，评审；设备」配对。\nA1B2 C3D4 E5F6 7890\n请核对两端。","信任",false))
                {
                    var panel=new RoundedPanel{Fill=Color.White,Border=Color.FromArgb(Theme.Line.ToArgb()),Size=new Size(80,40)};regular.Surface.Controls.Add(panel);regular.Show();confirm.Show();Application.DoEvents();
                    RoundedButton fixedClose=Controls(confirm).OfType<RoundedButton>().Single(b=>b.AccessibleName=="关闭窗口"),redClose=Controls(regular).OfType<RoundedButton>().Single(b=>b.AccessibleName=="关闭窗口");
                    RichTextBox details=Controls(confirm).OfType<RichTextBox>().Single(r=>r.AccessibleName=="确认详情");string raw=details.Text;
                    details.Select(raw.IndexOf("配对。",StringComparison.Ordinal),1);Check(details.SelectionColor.ToArgb()==Theme.Ink.ToArgb(),"昵称引号中的分号或逗号误刷灰后文");
                    foreach(bool dark in new[]{false,true,false})
                    {
                        Theme.Change(dark,"blue");Color fixedBack=fixedClose.HoverBack,fixedFore=fixedClose.HoverFore,redBack=redClose.HoverBack,redFore=redClose.HoverFore,fill=panel.Fill,border=panel.Border;
                        Check(fixedBack.ToArgb()==Theme.Hover.ToArgb()&&fixedFore.ToArgb()==Theme.Ink.ToArgb(),"固定弹窗关闭键悬停颜色未更新");
                        Check(redBack.ToArgb()==Color.FromArgb(232,17,35).ToArgb()&&redFore.ToArgb()==Color.White.ToArgb(),"系统红色关闭键白字被映射为主题背景");
                        Check(fill.ToArgb()==Theme.Surface.ToArgb()&&border.ToArgb()==Theme.Line.ToArgb(),"命名Color的面板填充或边框没有按ARGB更新");
                        details.Select(raw.IndexOf("A1B2",StringComparison.Ordinal),4);Check(details.SelectionColor.ToArgb()==Theme.OnPrimaryContainer.ToArgb()&&details.SelectionFont.FontFamily.Name=="Consolas","实际十六进制配对码没有高亮");
                        details.Select(raw.IndexOf("请核对",StringComparison.Ordinal),3);Check(details.SelectionColor.ToArgb()==Theme.Blend(Theme.Ink,Theme.Muted,.60F).ToArgb(),"富文本解释段沿用了旧主题颜色");
                        int start=details.SelectionStart;Theme.Change(dark,"green");Check(details.SelectionStart==start&&details.SelectionLength==3,"主题切换丢失富文本选区");Check(details.Text==raw,"主题切换改变配对原文");
                        SaveSnapshot(confirm,"confirm-pair-"+(dark?"dark":"light"));
                    }
                    confirm.Close();regular.Close();
                }
                foreach(string code in new[]{"123456","123 456"})using(var dialog=new ConfirmDialog("配对","核对配对码：\n"+code+"\n两端应一致。","信任",false))
                {
                    dialog.Show();Application.DoEvents();RichTextBox details=Controls(dialog).OfType<RichTextBox>().Single(r=>r.AccessibleName=="确认详情");details.Select(details.Text.IndexOf(code,StringComparison.Ordinal),code.Length);Check(details.SelectionColor.ToArgb()==Theme.OnPrimaryContainer.ToArgb(),"纯数字或分组配对码被解释段刷灰");dialog.Close();
                }
                Theme.Change(false,"blue");
            });
            Console.WriteLine("TOTAL "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
