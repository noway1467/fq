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
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class UiInteractionTests
    {
        private static int passed, failed;
        private static string root, output;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); } }
        private static IEnumerable<Control> All(Control parent) { yield return parent; foreach(Control child in parent.Controls) foreach(Control nested in All(child)) yield return nested; }
        private static int Port() { TcpListener listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
        private static Peer Contact(string login) { return new Peer { Endpoint=new IPEndPoint(IPAddress.Loopback,24791),Login=login,Nickname=login=="peer"?"中文 English 很长的联系人昵称":"Second contact",Avatar="builtin:2" }; }
        private static LanService Node(LocalStore store) { return new LanService(new AppSettings {AutoReceiveFiles=false,Notifications=false,CloseToTray=false,ReceiveFolder=Path.Combine(store.Root,"received")},Port(),true,"ui-interaction"); }
        private static void Pump(int milliseconds) { Stopwatch timer=Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(5); } while(timer.ElapsedMilliseconds<milliseconds); }
        private static void Mouse(Control control, int message, Point point) { SendMessage(control.Handle,message,new IntPtr(message==0x201?1:0),new IntPtr(unchecked((point.Y<<16)|(point.X&65535)))); }
        private static void Event(Control control, string name, EventArgs args) { control.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(control,new object[]{args}); }
        private static void SetCursor(Control control) { SendMessage(control.Handle,0x20,control.Handle,new IntPtr(0x02000001)); }
        private static Bitmap Snapshot(Control control) { Bitmap image=new Bitmap(control.Width,control.Height); control.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); return image; }
        private static void Render(Control control, string name) { using(Bitmap image=Snapshot(control))image.Save(Path.Combine(output,name+".png"),ImageFormat.Png); }
        private static void Fill(MainForm main)
        {
            Peer peer=Contact("peer"); main.OpenChat(peer);
            All(main).OfType<ConversationList>().Single().SetItems(new[] {
                new ConversationList.Item {Peer=peer,Preview="你：中文 English 😀 长消息预览",Unread=123,Time=DateTime.Now},
                new ConversationList.Item {Peer=Contact("second"),Preview="第二个联系人 · 离线",Time=DateTime.Now}
            });
            main.ActiveChat.Append(new ChatRecord {PeerId=peer.Id,Packet=1,Text="中文 English 界面交互检查\n头像可打开资料，标题及周围空白不触发。",Time=DateTime.Now});
            All(main).OfType<ChatEditor>().Single().Text="第一行草稿\nSecond line English";
        }
        private static int ProfileClicks(Control control)
        {
            int opened=0;
            using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=20}) {
                timer.Tick+=delegate { ContactProfileDialog dialog=Application.OpenForms.OfType<ContactProfileDialog>().FirstOrDefault(); if(dialog==null)return; opened++; dialog.Close(); };
                timer.Start(); Event(control,"OnClick",EventArgs.Empty); timer.Stop();
            }
            return opened;
        }
        [STAThread] public static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]); output=Path.GetFullPath(args[1]);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>2&&args[2]=="preview") {
                Theme.Configure(false,"rose"); LocalStore preview=new LocalStore(Path.Combine(root,"preview"));
                using(LanService node=Node(preview)) using(MainForm main=new MainForm(node,preview)) using(System.Windows.Forms.Timer timeout=new System.Windows.Forms.Timer {Interval=600000}) {
                    main.Text="飞Q · 界面交互验证（隔离资料）"; main.Shown+=delegate {Fill(main);}; timeout.Tick+=delegate {main.ExitApplication();}; timeout.Start(); Application.Run(main);
                }
                return 0;
            }
            Run("边缘光标不写入父窗口、不传染内部控件",()=>{
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(640,480)}) {
                    Panel blank=new Panel {Dock=DockStyle.Fill}; Label label=new Label {Text="标题",Dock=DockStyle.Top}; TextBox text=new TextBox {Dock=DockStyle.Bottom}; Button button=Theme.Button("按钮",null,false);
                    window.Surface.Controls.Add(blank); blank.Controls.Add(label); blank.Controls.Add(text); blank.Controls.Add(button); window.Show(); Pump(20);
                    Mouse(window,0x200,new Point(window.Width-2,window.Height/2)); Check(Cursor.Current==Cursors.SizeWE,"边缘未显示水平缩放光标");
                    Check(window.Cursor==Cursors.Default&&blank.Cursor==Cursors.Default&&label.Cursor==Cursors.Default,"缩放光标被内部控件继承");
                    SetCursor(label); Check(Cursor.Current==Cursors.Default,"标题仍显示缩放光标");
                    SetCursor(text); Check(Cursor.Current==Cursors.IBeam,"文字输入光标被覆盖");
                    SetCursor(button); Check(Cursor.Current==Cursors.Hand,"按钮手形被覆盖");
                    Rectangle before=window.Bounds; Mouse(window,0x201,new Point(200,180)); Mouse(window,0x200,new Point(240,210)); Mouse(window,0x202,new Point(240,210)); Check(window.Bounds==before,"内部空白触发缩放");
                    window.Close();
                }
            });
            Run("聊天标题、状态及空白不打开资料，头像仍可打开",()=>{
                LocalStore store=new LocalStore(Path.Combine(root,"hit-target"));
                using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                    main.Show(); Fill(main); Pump(20);
                    Label name=All(main).OfType<Label>().Single(c=>c.AccessibleName=="聊天联系人昵称");
                    Label status=All(main).OfType<Label>().Single(c=>c.AccessibleName=="聊天联系人状态");
                    PictureBox avatar=All(main).OfType<PictureBox>().Single(c=>c.AccessibleName=="聊天联系人头像");
                    Check(ProfileClicks(name)==0,"点击昵称打开了资料"); Check(ProfileClicks(status)==0&&ProfileClicks(name.Parent)==0,"状态或标题空白打开了资料");
                    Check(ProfileClicks(avatar)==1,"头像不能打开资料"); Check(name.Cursor==Cursors.Default&&avatar.Cursor==Cursors.Hand,"头像和昵称光标未区分");
                    string draft=All(main.ActiveChat).OfType<ChatEditor>().Single().Text; main.OpenChat(Contact("second")); main.OpenChat(Contact("peer"));
                    Check(All(main.ActiveChat).OfType<ChatEditor>().Single().Text==draft,"切换会话丢失草稿"); main.ExitApplication();
                }
            });
            Run("按钮取消按压：失去捕获、禁用和隐藏都能结束反馈",()=>{
                using(Form form=new Form {ClientSize=new Size(300,180)}) using(RoundedButton button=(RoundedButton)Theme.Button("测试按钮",null,false)) {
                    form.Controls.Add(button); form.Show(); Pump(20);
                    FieldInfo pressed=typeof(RoundedButton).GetField("pressed",BindingFlags.Instance|BindingFlags.NonPublic);
                    foreach(string cancel in new[]{"capture","disabled","hidden"}) {
                        button.Enabled=true; button.Visible=true; button.Capture=true;
                        Event(button,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,20,20,0));
                        if(cancel=="capture")button.Capture=false; else if(cancel=="disabled")button.Enabled=false; else button.Visible=false;
                        Check(!(bool)pressed.GetValue(button),"按压残留："+cancel);
                    }
                    button.Visible=true; button.Enabled=true; Pump(300); Render(form,"button-cancel"); form.Close();
                }
            });
            Run("按钮悬停往返连续，长按完成后停止重复重绘",()=>{
                using(Form form=new Form {ClientSize=new Size(300,180)}) using(RoundedButton button=(RoundedButton)Theme.Button("测试按钮",null,false)) {
                    form.Controls.Add(button); form.Show(); Pump(20);
                    Event(button,"OnMouseEnter",EventArgs.Empty); Pump(60); Event(button,"OnMouseLeave",EventArgs.Empty); Pump(30); Event(button,"OnMouseEnter",EventArgs.Empty); Pump(200);
                    Tween hover=(Tween)typeof(RoundedButton).GetField("hover",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(button); Check(hover.Value==1,"快速移入移出后悬停未落位");
                    button.Capture=true; Event(button,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,20,20,0)); Pump(350);
                    Func<bool> rippleStep=(Func<bool>)typeof(RoundedButton).GetField("rippleStep",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(button);
                    Check(!rippleStep(),"长按稳定后仍持续刷新"); button.Capture=false; Pump(300); form.Close();
                }
            });
            Run("会话高亮平滑切换，快速反向与列表重排保持对应联系人",()=>{
                using(Form form=new Form {ClientSize=new Size(336,260)}) using(ConversationList list=new ConversationList {Dock=DockStyle.Fill}) {
                    form.Controls.Add(list); Peer a=Contact("peer"),b=Contact("second"); var items=new[]{new ConversationList.Item {Peer=a},new ConversationList.Item {Peer=b}};
                    list.SetItems(items); form.Show(); Pump(30); int activated=0; list.PeerActivated+=p=>activated++;
                    list.ActivatePeer(0); Check(list.SelectedId==a.Id&&activated==1,"动画延迟会话切换");
                    using(Bitmap first=Snapshot(list))if(Motion.Enabled)Check(first.GetPixel(10,30).ToArgb()!=Theme.Blue.ToArgb(),"选中背景直接跳变");
                    Pump(250); using(Bitmap last=Snapshot(list))Check(last.GetPixel(10,30).ToArgb()==Theme.Blue.ToArgb(),"选中背景未落位");
                    list.ActivatePeer(1); Pump(40); list.ActivatePeer(0); list.SetItems(items.Reverse()); Pump(250);
                    Check(list.SelectedId==a.Id&&activated==3,"重排改变选中会话或重复激活");
                    using(Bitmap last=Snapshot(list))Check(last.GetPixel(10,30).ToArgb()==Theme.Surface.ToArgb()&&last.GetPixel(10,98).ToArgb()==Theme.Blue.ToArgb(),"高亮跟随旧行号");
                    list.SetItems(new ConversationList.Item[0]); Pump(250); Render(form,"empty-list"); form.Close();
                }
            });
            Run("设置移除启动提示，开关取值与禁用状态保留",()=>{
                foreach(bool available in new[]{false,true}) using(SettingsDialog dialog=new SettingsDialog(new AppSettings(),Path.Combine(root,"settings"),24790,available,true)) {
                    dialog.Show(); Pump(30);
                    Check(!All(dialog).OfType<Label>().Any(c=>c.Text.Contains("无需管理员权限")||c.Text.Contains("系统禁用的启动项")||c.Text.Contains("当前运行环境无法配置系统启动项")),"仍显示启动提示");
                    ToggleSwitch startup=All(dialog).OfType<ToggleSwitch>().Single(c=>c.AccessibleName=="开机自启动"); Check(startup.Enabled==available,"启动开关可用状态改变");
                    if(available) { Check(startup.Checked,"现有启动开关状态丢失"); startup.Checked=false; }
                    Render(dialog,"settings-"+available); All(dialog).OfType<Button>().Single(b=>b.Text=="保存").PerformClick(); Check(dialog.Value!=null&&!dialog.Value.StartWithWindows,"保存后的启动项状态错误"); dialog.Close();
                }
            });
            Run("搜索框：提示与输入垂直居中，字体和高度变化后保持对齐",()=>{
                foreach(bool dark in new[]{false,true}) {
                    Theme.Configure(dark,"rose");
                    using(Form form=new Form {ClientSize=new Size(640,240)})using(SearchBox search=new SearchBox {Width=600,Height=58,Location=new Point(20,30)}) {
                        Theme.Apply(form);form.Controls.Add(search);search.Placeholder="搜索消息或文件名";form.Show();Pump(20);Render(form,"search-placeholder-"+dark);
                        foreach(int fontSize in new[]{9,12,15})using(Font font=new Font(Theme.Body.FontFamily,fontSize)) {
                            search.Input.Font=font;
                            foreach(int height in new[]{32,44,58,72}) {
                                search.Height=height;form.PerformLayout();
                                foreach(string text in new[]{"","666","中文 English 很长的搜索内容"}) {
                                    search.Input.Text=text;Pump(10);
                                    Check(Math.Abs(search.Input.Top+search.Input.Height/2.0-search.ClientSize.Height/2.0)<=1,"搜索正文未垂直居中：height="+height+" font="+fontSize+" input="+search.Input.Bounds);
                                    Check(search.Input.Left>=search.Padding.Left&&search.Input.Right<=search.Width-search.Padding.Right,"搜索正文覆盖图标或清除键");
                                }
                            }
                            search.Input.Font=Theme.Body;
                        }
                        search.Height=58;search.Input.Text="666";Render(form,"search-text-"+dark);
                        Event(search.Input,"OnKeyDown",new KeyEventArgs(Keys.Escape));Check(search.Input.TextLength==0,"Escape 清除搜索失效");form.Close();
                    }
                }
            });
            Run("聊天记录窗口：四边等粗、同色且标题分隔线完整",()=>{
                foreach(bool dark in new[]{false,true}) {
                    Theme.Configure(dark,"blue");LocalStore store=new LocalStore(Path.Combine(root,"border-"+dark));
                    using(HistoryDialog dialog=new HistoryDialog(store,Contact("peer"))) {
                        dialog.Show();Pump(30);
                        foreach(Size size in new[]{dialog.MinimumSize,new Size(760,720)}) {
                            dialog.Size=size;dialog.Refresh();Render(dialog,"window-border-"+dark+"-"+size.Width);
                            using(Bitmap image=Snapshot(dialog)) {
                                Color top=image.GetPixel(image.Width/2,0),left=image.GetPixel(0,image.Height/2),right=image.GetPixel(image.Width-1,image.Height/2),bottom=image.GetPixel(image.Width/2,image.Height-1);
                                Check(top.ToArgb()==left.ToArgb()&&left.ToArgb()==right.ToArgb()&&right.ToArgb()==bottom.ToArgb(),"窗口四边颜色或位置不一致");
                                Check(top.ToArgb()!=Theme.Surface.ToArgb(),"边框与背景无法区分");
                                Check(image.GetPixel(image.Width/2,1).ToArgb()!=top.ToArgb()&&image.GetPixel(1,image.Height/2).ToArgb()!=top.ToArgb()&&image.GetPixel(image.Width-2,image.Height/2).ToArgb()!=top.ToArgb()&&image.GetPixel(image.Width/2,image.Height-2).ToArgb()!=top.ToArgb(),"窗口边框粗细不一致或超过一像素");
                                TableLayoutPanel chrome=(TableLayoutPanel)typeof(ModernWindow).GetField("chrome",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(dialog);Point location=dialog.PointToClient(chrome.PointToScreen(Point.Empty));int y=location.Y+chrome.Height-1;
                                Check(image.GetPixel(location.X+8,y).ToArgb()==image.GetPixel(location.X+chrome.Width-8,y).ToArgb(),"标题分隔线在窗口按钮下断开");
                            }
                        }
                        dialog.Close();
                    }
                }
            });
            Run("搜索结果点击定位旧消息，同号收发分开且保留草稿",()=>{
                LocalStore store=new LocalStore(Path.Combine(root,"search-navigation"));Peer peer=Contact("peer");
                ChatRecord[] records=Enumerable.Range(0,360).Select(index=>new ChatRecord {PeerId=peer.Id,Packet=index/2+1,Outgoing=index%2==0,Time=DateTime.Now.AddMinutes(index-360),State="已送达",Text=index==40||index==41?"定位目标关键词":("上下文消息 "+index)}).ToArray();
                foreach(ChatRecord record in records)store.Append(record);
                using(LanService node=Node(store))using(MainForm main=new MainForm(node,store)) {
                    main.Show();main.OpenChat(peer);Pump(30);ChatEditor editor=All(main.ActiveChat).OfType<ChatEditor>().Single();editor.Text="搜索定位保留的草稿";
                    foreach(ChatRecord expected in records.Where(record=>record.Packet==21)) {
                        Exception callbackError=null;
                        using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer {Interval=30}) {
                            timer.Tick+=delegate {
                                HistoryDialog dialog=Application.OpenForms.OfType<HistoryDialog>().FirstOrDefault();if(dialog==null)return;timer.Stop();
                                try {
                                    All(dialog).OfType<TextBox>().Single(control=>control.AccessibleName=="搜索聊天记录").Text="定位目标关键词";
                                    BubbleHistory results=All(dialog).OfType<BubbleHistory>().Single();results.Refresh();
                                    var found=(List<ChatRecord>)typeof(BubbleHistory).GetField("records",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(results);
                                    int index=found.FindIndex(record=>record.Packet==expected.Packet&&record.Outgoing==expected.Outgoing);
                                    var areas=(List<Rectangle>)typeof(BubbleHistory).GetField("bounds",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(results);
                                    Point point=new Point(areas[index].Left+12,areas[index].Top+results.AutoScrollPosition.Y+12);
                                    Mouse(results,0x201,point);Mouse(results,0x202,point);
                                    Check(dialog.LocatedRecord!=null&&dialog.LocatedRecord.Outgoing==expected.Outgoing,"结果点击没有返回准确的消息身份");
                                }catch(Exception error){callbackError=error;dialog.Close();}
                            };
                            timer.Start();All(main.ActiveChat).OfType<Button>().Single(button=>button.AccessibleName=="聊天记录").PerformClick();
                        }
                        if(callbackError!=null)throw callbackError;
                        BubbleHistory history=All(main.ActiveChat).OfType<BubbleHistory>().Single();
                        ChatRecord located=(ChatRecord)typeof(BubbleHistory).GetProperty("SelectedRecord",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history,null);
                        Check(located!=null&&located.Packet==expected.Packet&&located.Outgoing==expected.Outgoing,"定位跳错消息或收发方向");
                        int selected=(int)typeof(BubbleHistory).GetField("selected",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history);
                        Rectangle target=((List<Rectangle>)typeof(BubbleHistory).GetField("bounds",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history))[selected];target.Offset(history.AutoScrollPosition);
                        Check(target.IntersectsWith(history.ClientRectangle),"目标没有出现在聊天视口");Check(history.Text.Contains("上下文消息 359"),"定位丢失了后面的聊天上下文");
                        Check(editor.Text=="搜索定位保留的草稿","定位改写草稿");Render(main,"search-navigation-"+expected.Outgoing);
                    }
                    ChatRecord wrongPeer=new ChatRecord {PeerId="other",Packet=21};Check(!main.ActiveChat.LocateMessage(wrongPeer),"跨会话相同编号被错误定位");
                    using(HistoryDialog pages=new HistoryDialog(store,peer)) {
                        pages.Show();Pump(50);
                        Label count=All(pages).OfType<Label>().Single(label=>label.AccessibleName=="聊天记录搜索结果");
                        Check(count.Text.Contains("第 1 / 4 页"),"没有显示当前页与总页数");
                        All(pages).OfType<Button>().Single(button=>button.AccessibleName=="第 3 页").PerformClick();
                        Check(count.Text.Contains("第 3 / 4 页"),"点击页码没有直达第 3 页");
                        NumericUpDown destination=All(pages).OfType<NumericUpDown>().Single();destination.Value=4;
                        All(pages).OfType<Button>().Single(button=>button.Text=="跳转").PerformClick();
                        Check(count.Text.Contains("第 4 / 4 页"),"输入页码没有直达末页");
                        pages.Size=pages.MinimumSize;Pump(30);Render(pages,"numbered-history-pages");
                        All(pages).OfType<TextBox>().Single(control=>control.AccessibleName=="搜索聊天记录").Text="定位目标关键词";
                        Check(count.Text.Contains("第 1 / 1 页"),"更换关键词后没有回到第 1 页");pages.Close();
                    }
                    for(int number=1000;number<1600;number++)store.Append(new ChatRecord {PeerId=peer.Id,Packet=number,Text=new string('a',7400),State="已收到"});
                    ChatRecord oldest=records.First();
                    var matches=store.ReadArchive(peer.Id,"定位目标关键词",0,null,System.Threading.CancellationToken.None);
                    Check(matches.Total==2 && matches.Records.Count==2,"超过 4 MiB 的历史正文未被搜索");
                    var second=store.ReadArchive(peer.Id,"",1,null,System.Threading.CancellationToken.None);
                    Check(second.Records.Count==100 && second.Records.Last().Packet==1499,"历史翻页重复或漏掉消息");
                    Check(main.ActiveChat.LocateMessage(oldest),"超出最近 500 条的搜索结果不能定位");
                    Button latest=All(main.ActiveChat).OfType<Button>().Single(button=>button.Text=="回到最新消息");Check(latest.Visible,"缺少回到最新消息入口");
                    latest.PerformClick();Check(!latest.Visible,"回到最新消息没有离开旧上下文");
                    store.DeleteRecords(peer.Id,new[]{oldest});Check(!main.ActiveChat.LocateMessage(oldest),"已删除的旧记录被搜索快照复活");
                    main.ExitApplication();Check(store.LoadDraft(peer.Id)=="搜索定位保留的草稿","退出未保留草稿");
                }
            });
            foreach(bool dark in new[]{false,true}) Run("渲染与布局："+(dark?"深色":"浅色")+"、最小/常用/最大化与空数据",()=>{
                Theme.Configure(dark,"rose"); LocalStore store=new LocalStore(Path.Combine(root,"render-"+dark));
                using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                    main.Show(); Fill(main); Pump(300);
                    foreach(Size size in new[]{main.MinimumSize,new Size(1120,760)}) {
                        main.Size=size; Pump(80); ChatEditor editor=All(main).OfType<ChatEditor>().Single(); Check(editor.Width>100&&editor.Text=="第一行草稿\nSecond line English","布局挤占输入框或改变草稿");
                        Check(All(main).Where(c=>c.AccessibleName=="聊天联系人昵称"||c.AccessibleName=="聊天联系人头像").All(c=>c.Parent.ClientRectangle.Contains(c.Bounds)),"标题或头像超出布局"); Render(main,"main-"+dark+"-"+size.Width);
                    }
                    main.WindowState=FormWindowState.Maximized; Pump(80); Render(main,"main-"+dark+"-maximized"); main.WindowState=FormWindowState.Normal;
                    main.CloseChat(main.ActiveChat); All(main).OfType<ConversationList>().Single().SetItems(new ConversationList.Item[0]); Pump(250); Render(main,"main-"+dark+"-empty"); main.ExitApplication();
                }
            });
            Console.WriteLine("UI INTERACTION: "+passed+" passed, "+failed+" failed; animations="+Motion.Enabled); return failed==0?0:1;
        }
    }
}
