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
