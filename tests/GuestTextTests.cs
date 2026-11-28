using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static partial class TransferFeatureTests
    {
        private static void ResetTextRate(GuestTransfer server,bool published)
        {
            typeof(GuestTransfer).GetField(published?"nextPublishedText":"nextReceivedText",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,0L);
        }
        private static void GuestTextRegressions()
        {
            Run("文本独立权限、仅文本启动、JSON正文和访客收件箱隔离",()=>{
                string destination=Path.Combine(root,"text-only");
                using(var server=new GuestTransfer(new string[0],destination,false,true,allowText:true)) {
                    var status=Data(Request(server.Port,"/api/guest",server.Token));
                    Check(server.Active&&(bool)status["text"]&&!(bool)status["upload"],"仅文本会话没有独立开放");
                    Check(Request(server.Port,"/api/text?token="+server.Token).Code==403,"文本 API 接受了 URL 令牌");
                    Check(Request(server.Port,"/api/text",null,Encoding.UTF8.GetBytes("{\"text\":\"private\"}"),"POST","Content-Type: application/json\r\n").Code==403,"无令牌可投递文本");
                    Check(Json(server,"/api/text",new{text="访客 A 的私人投递"}).Code==200,"访客投递失败");
                    Check(server.Texts(false).Single().Text=="访客 A 的私人投递","主机没有收到访客文本");
                    Check(((object[])Data(Request(server.Port,"/api/text?after=0",server.Token))["items"]).Length==0,"访客 B 看到了访客 A 收件箱");
                    string published="<img src=x onerror=alert(1)>\nhttps://example.invalid/?a=中文&b=😀";server.PublishText(published);
                    var items=(object[])Data(Request(server.Port,"/api/text?after=0",server.Token))["items"];
                    Check(items.Length==1&&(string)((System.Collections.Generic.Dictionary<string,object>)items[0])["text"]==published,"主机发布内容被改写");
                    Check(((object[])Data(Request(server.Port,"/api/text?after=1",server.Token))["items"]).Length==0,"增量读取重复返回");
                    foreach(string value in new[]{"-1","abc","65","99999999999999999"})Check(Request(server.Port,"/api/text?after="+value,server.Token).Code==400,"接受非法读取位置");
                    Check(Request(server.Port,"/api/text",server.Token,null,"GET","Origin: http://evil.invalid\r\n").Code==403,"文本读取绕过 Origin");
                    Check(Request(server.Port,"/api/text",server.Token,null,"GET",host:"evil.invalid").Code==403,"文本读取绕过 Host");
                    Check(Request(server.Port,"/api/text",server.Token,Encoding.UTF8.GetBytes("{\"text\":\"wrong host\"}"),"POST","Content-Type: application/json\r\n",host:"evil.invalid").Code==403,"文本投递绕过 Host");
                    Check(Request(server.Port,"/api/text",server.Token,Encoding.UTF8.GetBytes("{\"text\":\"cross site\"}"),"POST","Content-Type: application/json\r\nOrigin: http://evil.invalid\r\n").Code==403,"文本发送绕过 Origin");
                    Check(Json(server,"/api/prepare",new{name="blocked.txt",size=0}).Code==403,"文本权限开放了文件上传");
                    Check(!Directory.Exists(destination)&&server.SavedFiles.Length==0,"文本产生了磁盘接收资料");
                }
                using(var server=new GuestTransfer(new string[0],Path.Combine(root,"text-off"),true,true)) {
                    Check(Request(server.Port,"/api/text",server.Token).Code==403&&Json(server,"/api/text",new{text="x"}).Code==403,"文件接收隐式开放了文本");
                    try{server.PublishText("x");throw new Exception("关闭文本仍可主机发布");}catch(ToolsHttp.HttpError error){Check(error.Code==403,"权限错误状态");}
                }
            });
            Run("文本双方向 4KiB、64条、128KiB硬限额及频控",()=>{
                foreach(bool published in new[]{false,true}) {
                    using(var server=new GuestTransfer(new string[0],Path.Combine(root,"text-limits"),false,true,allowText:true)) {
                        foreach(string invalid in new[]{"","\0\0"}) {
                            if(published)try{server.PublishText(invalid);throw new Exception("接受空文本");}catch(ToolsHttp.HttpError error){Check(error.Code==400,"空文本状态");}
                            else Check(Json(server,"/api/text",new{text=invalid}).Code==400,"接受空文本");
                        }
                        string limit=String.Concat(Enumerable.Repeat("😀",1024));
                        Action<string,int> send=(text,code)=>{
                            if(published) {try{server.PublishText(text);Check(code==200,"没有拒绝越界");}catch(ToolsHttp.HttpError error){Check(error.Code==code,"主机错误状态 "+error.Code+" expected "+code);}}
                            else Check(Json(server,"/api/text",new{text=text}).Code==code,"访客错误状态");
                        };
                        send(limit+"a",413);send(limit,200);
                        typeof(GuestTransfer).GetField(published?"nextPublishedText":"nextReceivedText",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,Int64.MaxValue);
                        send("频控",429);ResetTextRate(server,published);
                        for(int i=1;i<32;i++){ResetTextRate(server,published);send(limit,200);}
                        ResetTextRate(server,published);send("多一个字节",409);Check(server.Texts(published).Length==32,"累计限额改变队列");
                    }
                    using(var server=new GuestTransfer(new string[0],Path.Combine(root,"text-count"),false,true,allowText:true)) {
                        for(int i=0;i<64;i++){ResetTextRate(server,published);if(published)server.PublishText("x");else Check(Json(server,"/api/text",new{text="x"}).Code==200,"64条之前拒绝");}
                        ResetTextRate(server,published);
                        if(published)try{server.PublishText("x");throw new Exception("第65条没有拒绝");}catch(ToolsHttp.HttpError error){Check(error.Code==409,"条数限额状态");}
                        else Check(Json(server,"/api/text",new{text="x"}).Code==409,"第65条没有拒绝");
                        Check(server.Texts(published).Length==64,"条数限额改变队列");
                    }
                }
            });
            Run("文本结束与到期清内存，迟到发布不能复活",()=>{
                foreach(bool expire in new[]{false,true})using(var server=new GuestTransfer(new string[0],Path.Combine(root,"text-end"),false,true,allowText:true)) {
                    server.PublishText("发布");Check(Json(server,"/api/text",new{text="收到"}).Code==200,"投递失败");int ended=0;server.TextEnded+=()=>ended++;
                    if(expire){typeof(GuestTransfer).GetField("lifetimeMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(server,0L);Check(!server.Active,"到期仍活跃");Check(Json(server,"/api/text",new{text="迟到"}).Code==410,"到期仍投递");}
                    else server.Dispose();
                    Check(server.Texts(true).Length==0&&server.Texts(false).Length==0&&ended==1,"结束未立即清空");
                    Check(Field<int>(server,"publishedTextBytes")==0&&Field<int>(server,"receivedTextBytes")==0,"结束残留文本字节计数");
                    try{server.PublishText("迟到发布");throw new Exception("迟到发布复活");}catch(ToolsHttp.HttpError error){Check(error.Code==410,"迟到状态");}
                    server.Dispose();Check(ended==1,"结束通知不幂等");
                }
            });
        }
        private static void GuestTextUiRegression()
        {
            Run("文本独立小窗：明暗收发、复制、默认关闭、切网清界面与历史隔离",()=>{
                foreach(bool dark in new[]{false,true}) {
                    Theme.Configure(dark,"blue");var store=new LocalStore(Path.Combine(root,"text-ui-"+dark));
                    using(var network=new LanService(new AppSettings{CloseToTray=false,Notifications=false,DarkMode=dark,ReceiveFolder=Path.Combine(store.Root,"received")},FreePort(),true,"text-ui"))
                    using(var main=new MainForm(network,store)) {
                        main.Show();main.PrepareNetworkCallbacks();typeof(MainForm).GetMethod("OpenGuest",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(main,null);
                        GuestWindow window=Field<GuestWindow>(main,"guestWindow");Application.DoEvents();Check(!Field<ToggleSwitch>(window,"allowText").Checked,"文本开关不是默认关闭");
                        Field<ToggleSwitch>(window,"allowText").Checked=true;Field<Button>(window,"start").PerformClick();
                        GuestTransfer session=Field<GuestTransfer>(window,"session");Check(session!=null&&session.Active&&!Field<ToggleSwitch>(window,"upload").Checked,"仅文本 UI 无法启动");
                        Field<Button>(window,"textTransfer").PerformClick();Application.DoEvents();GuestTextWindow panel=Field<GuestTextWindow>(window,"textWindow");Check(panel!=null&&panel.Visible,"独立小窗未打开");
                        SaveSnapshot(panel,"guest-text-empty-"+(dark?"dark":"light"));
                        Field<ChatEditor>(panel,"draft").Text="主机 中文 English 😀\n<svg onload=alert(1)>";Field<Button>(panel,"send").PerformClick();
                        Check(Json(session,"/api/text",new{text="访客收件文本"}).Code==200,"UI 投递失败");Tick(Field<System.Windows.Forms.Timer>(panel,"timer"));
                        GuestTextWindow.TextHistory history=Field<GuestTextWindow.TextHistory>(panel,"history");
                        Check(history.Items.Count==2&&Field<ChatEditor>(panel,"draft").Text.Length==0,"小窗未收发或发送后没有清稿");
                        Check(history.Items[0].Entry.Text.Contains("\n")&&Field<ChatEditor>(panel,"draft").ScrollBars==RichTextBoxScrollBars.None,"多行文本或 App 输入组件契约错误");
                        foreach(Button button in new[]{Field<Button>(panel,"send"),Field<Button>(panel,"copy")})Check(button.Parent.ClientRectangle.Contains(button.Bounds)&&button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+24,"文本按钮越界或文案裁切："+button.Text);
                        Check(!main.TransferRecords.Any(),"文本进入了聊天或文件历史");
                        SaveSnapshot(panel,"guest-text-"+(dark?"dark":"light"));
                        IDataObject clipboard=Clipboard.GetDataObject();try{Field<Button>(panel,"copy").PerformClick();Check(Clipboard.GetText().Contains("主机 中文 English"),"复制文本失败");}finally{if(clipboard!=null)Clipboard.SetDataObject(clipboard,true);}
                        ResetTextRate(session,false);string longText=String.Join("\n",Enumerable.Repeat("中文 English 1234567890 😀",70))+"\n最后一行 TAIL";
                        Check(Json(session,"/api/text",new{text=longText}).Code==200,"长文本投递失败");Tick(Field<System.Windows.Forms.Timer>(panel,"timer"));
                        panel.Size=panel.MinimumSize;Application.DoEvents();
                        foreach(Control control in new[]{Field<Control>(panel,"historyShell"),Field<Control>(panel,"composer"),Field<Control>(panel,"send"),Field<Control>(panel,"copy"),Field<Control>(panel,"status")})Check(control.Parent.ClientRectangle.Contains(control.Bounds),"窄窗文本控件越界："+control.Bounds);
                        Check(!Field<Control>(panel,"historyShell").Bounds.IntersectsWith(Field<Control>(panel,"composer").Bounds),"窄窗文本卡片与输入区重叠");
                        foreach(Button button in new[]{Field<Button>(panel,"send"),Field<Button>(panel,"copy")})Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+24,"窄窗文本按钮裁切："+button.Text);
                        Check(history.AutoScrollMinSize.Height>history.Height,"长文本没有可滚动内容");history.SelectedIndex=2;history.AutoScrollPosition=new Point(0,history.AutoScrollMinSize.Height);
                        ChatEditor editor=Field<ChatEditor>(panel,"draft");editor.Text=String.Join("\n",Enumerable.Repeat("未发送的草稿 中文 English",30));editor.ScrollByWheel(-120);Check(editor.FirstVisibleLine>0,"App 输入框无法滚动长草稿");
                        SaveSnapshot(panel,"guest-text-long-min-"+(dark?"dark":"light"));
                        ChatEditor staleDraft=Field<ChatEditor>(panel,"draft");
                        System.Threading.Tasks.Task.Run(()=>Field<Action>(network,"NetworkReset")()).GetAwaiter().GetResult();
                        Check(!session.Active&&session.Texts(false).Length==0&&session.Texts(true).Length==0,"切网在界面回调前没有同步清内存");Application.DoEvents();
                        Check(Field<GuestTransfer>(window,"session")==null&&!panel.Visible&&staleDraft.Text.Length==0&&history.Items.Count==0,"切网残留文本小窗或内容");
                        Check(session.Texts(false).Length==0&&session.Texts(true).Length==0,"切网没有清队列");
                        window.Close();main.ExitApplication();
                    }
                }
            });
        }
    }
}
