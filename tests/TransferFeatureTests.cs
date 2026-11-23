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
    internal static class TransferFeatureTests
    {
        private static string root,output;
        private static int passed,failed;
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
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
        private static T Field<T>(object target,string name){return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target);}
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
        [STAThread] private static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);Directory.CreateDirectory(output);
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
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
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
            Run("传输定时刷新：静止零重绘、仅刷新变化行、隐藏和最小化恢复",()=>{
                Theme.Configure(false,"blue");var store=new LocalStore(Path.Combine(root,"redraw"));
                using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"redraw"))
                using(var main=new MainForm(network,store))using(var window=new TransferWindow(main))
                {
                    main.Show();var file=new FileTransferState{Id=1,Name="中文 English-transfer.bin",Size=10485760,Percent=20,Status="接收中",BytesPerSecond=1048576};
                    main.TrackTransfer(new ChatRecord{PeerId="browser-guests",Packet=1,Sender="浏览器访客",Time=DateTime.Now,Transfers=new[]{file}});
                    main.TrackTransfer(new ChatRecord{PeerId="other",Packet=2,Sender="另一台设备",Time=DateTime.Now.AddMinutes(-1),Transfers=new[]{new FileTransferState{Id=1,Name="waiting.txt",Size=1024,Status="已暂停"}}});
                    window.Show();PumpFor(650);ListBox list=Controls(window).OfType<ListBox>().Single();Check(list.Items.Count==2,"未加载两条测试任务");list.SelectedIndex=0;Application.DoEvents();
                    var invalidated=new List<Rectangle>();int textChanges=0,visibilityChanges=0,ticks=0;
                    list.Invalidated+=(sender,e)=>invalidated.Add(e.InvalidRect);
                    foreach(Label label in new[]{Field<Label>(window,"summary"),Field<Label>(window,"detail"),Field<Label>(window,"empty")})label.TextChanged+=delegate{textChanges++;};
                    Button action=Field<Button>(window,"action"),more=Field<Button>(window,"more");action.TextChanged+=delegate{textChanges++;};action.VisibleChanged+=delegate{visibilityChanges++;};more.VisibleChanged+=delegate{visibilityChanges++;};
                    System.Windows.Forms.Timer timer=Field<System.Windows.Forms.Timer>(window,"timer");timer.Tick+=delegate{ticks++;};
                    PumpFor(1150);Check(ticks>=2,"真实500ms定时器未运行");Check(invalidated.Count==0,"无变化仍触发列表重绘");Check(textChanges==0&&visibilityChanges==0,"静止时反复修改摘要或操作控件");Check(!more.Visible,"访客任务错误显示会话操作");
                    file.Percent=35;Tick(timer);Check(invalidated.Count==1&&invalidated[0]==list.GetItemRectangle(0),"进度变化未局限于对应任务行");Check(Field<Label>(window,"summary").Text.Contains("3.5 MB"),"总摘要未随进度更新");
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
                        string[] names={"传输任务","聊天记录","收到的文件","会话菜单"};Button[] buttons=names.Select(name=>Controls(chat).OfType<Button>().Single(button=>button.AccessibleName==name)).ToArray();
                        TableLayoutPanel head=Field<TableLayoutPanel>(chat,"head");Check(head.ColumnCount==6,"顶部栏列数未更新");
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
            Console.WriteLine("TOTAL "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
