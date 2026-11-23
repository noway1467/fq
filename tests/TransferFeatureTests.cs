using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
            Console.WriteLine("TOTAL "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
