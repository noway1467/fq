using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class ResizeLayoutTests
    {
        private static int passed, failed;
        private static string root, output;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, IntPtr rect, bool erase);
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); } }
        private static System.Collections.Generic.IEnumerable<Control> All(Control parent) { yield return parent; foreach(Control child in parent.Controls) foreach(Control nested in All(child)) yield return nested; }
        private static void Mouse(Control control, int message, Point screen) { Point p=control.PointToClient(screen);SendMessage(control.Handle,message,new IntPtr(message==0x202?0:1),new IntPtr(unchecked((p.Y<<16)|(p.X&65535)))); }
        private static Peer Peer() { return new Peer { Endpoint=new IPEndPoint(IPAddress.Loopback,24781),Login="resize-peer",Nickname="中文 English 很长的联系人名字",Online=true }; }
        private static LanService Node(LocalStore store) { return new LanService(new AppSettings {AutoReceiveFiles=false,Notifications=false,CloseToTray=false,ReceiveFolder=Path.Combine(store.Root,"received")},24780,true,"resize-layout"); }
        private static void Render(MainForm main, string name) { using(Bitmap image=new Bitmap(main.Width,main.Height)) { main.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,name+".png"),ImageFormat.Png); } }
        private static void Fill(MainForm main)
        {
            Peer peer=Peer();main.OpenChat(peer);
            All(main).OfType<ConversationList>().Single().SetItems(new[] {
                new ConversationList.Item {Peer=peer,Preview="你：很长的中文预览 English preview 😀",Unread=123,Time=DateTime.Now},
                new ConversationList.Item {Peer=new Peer {Endpoint=new IPEndPoint(IPAddress.Loopback,24782),Login="english",Nickname="English long contact name",Pinned=true},Preview="离线联系人 · 历史消息",Time=DateTime.Now}
            });
            foreach(RoundedButton tab in All(main).OfType<TabStrip>().Single().Controls) if(tab.Text=="未读")tab.Badge=123;
            All(main).OfType<TabStrip>().Single().Relayout();
            main.ActiveChat.Append(new ChatRecord {PeerId=peer.Id,Packet=1,Text="连续调整窗口和联系人栏宽度\n中文 English 长消息自动换行，不应留下旧画面。",Time=DateTime.Now,State="已送达"});
            All(main).OfType<ChatEditor>().Single().Text="第一行草稿\nSecond line English";
        }
        private static void DragSidebar(MainForm main, int width)
        {
            Control divider=All(main).Single(c=>c.AccessibleName=="调整联系人栏宽度");
            Point start=divider.PointToScreen(new Point(divider.Width/2,divider.Height/2));
            Point end=new Point(start.X+width-divider.Left,start.Y);
            Mouse(divider,0x201,start);Mouse(divider,0x200,end);Mouse(divider,0x202,end);
        }
        [STAThread] public static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>3&&args[3]=="preview") {
                LocalStore store=new LocalStore(Path.Combine(root,"preview"));
                using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) using(Timer timeout=new Timer {Interval=600000}) {
                    main.Text="飞Q · 缩放与联系人栏验证（隔离资料）";
                    main.Shown+=delegate {Fill(main);};
                    timeout.Tick+=delegate {main.ExitApplication();};timeout.Start();Application.Run(main);
                    Console.WriteLine("PREVIEW CLOSED width="+main.Width+" sidebar="+node.Settings.SidebarWidth);
                }
                return 0;
            }
            Run("连续缩放：每一步完成全部可见子窗口重绘，不等待空闲消息",()=>{
                LocalStore store=new LocalStore(Path.Combine(root,"paint"));
                using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                    main.Show();main.OpenChat(Peer());Application.DoEvents();
                    ChatEditor editor=All(main).OfType<ChatEditor>().Single();editor.Text="第一行中文\nSecond line English";Application.DoEvents();main.Refresh();
                    Point origin=main.PointToScreen(new Point(main.Width-2,main.Height-2));Mouse(main,0x201,origin);
                    for(int i=1;i<=16;i++) {
                        Mouse(main,0x200,new Point(origin.X+(i%2==0?-1:1)*i*4,origin.Y+(i%2==0?-1:1)*i*3));
                        string[] dirty=All(main).Where(c=>c.Visible&&c.IsHandleCreated&&GetUpdateRect(c.Handle,IntPtr.Zero,false)).Select(c=>c.GetType().Name+":"+c.AccessibleName).ToArray();
                        Check(dirty.Length==0,"第 "+i+" 帧仍滞留旧画面："+String.Join(", ",dirty));
                    }
                    Mouse(main,0x202,main.PointToScreen(new Point(main.Width-2,main.Height-2)));main.ExitApplication();
                }
            });
            Run("缩放中丢失鼠标捕获后停止改变尺寸",()=>{
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(640,480)}) {
                    window.Show();Application.DoEvents();Point origin=window.PointToScreen(new Point(window.Width-2,window.Height/2));Mouse(window,0x201,origin);window.Capture=false;Size size=window.Size;
                    Mouse(window,0x200,new Point(origin.X-40,origin.Y));Check(window.Size==size,"捕获已丢失，窗口仍跟随鼠标缩放");window.Close();
                }
            });
            Run("四边四角：方向光标、固定对边与最大化还原",()=>{
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(640,480)}) {
                    window.Show();Application.DoEvents();Rectangle original=window.Bounds;
                    Point[] starts={new Point(1,240),new Point(638,240),new Point(320,1),new Point(320,478),new Point(1,1),new Point(638,1),new Point(1,478),new Point(638,478)};
                    Cursor[] cursors={Cursors.SizeWE,Cursors.SizeWE,Cursors.SizeNS,Cursors.SizeNS,Cursors.SizeNWSE,Cursors.SizeNESW,Cursors.SizeNESW,Cursors.SizeNWSE};
                    for(int i=0;i<starts.Length;i++) {
                        window.Bounds=original;Point start=window.PointToScreen(starts[i]);Mouse(window,0x200,start);Check(Cursor.Current==cursors[i],"方向光标错误："+i);
                        Mouse(window,0x201,start);Point end=new Point(start.X+20,start.Y+16);Mouse(window,0x200,end);Mouse(window,0x202,end);
                        bool left=starts[i].X==1,right=starts[i].X==638,top=starts[i].Y==1,bottom=starts[i].Y==478;
                        Check(window.Bounds==Rectangle.FromLTRB(original.Left+(left?20:0),original.Top+(top?16:0),original.Right+(right?20:0),original.Bottom+(bottom?16:0)),"拖动未固定对边："+i);
                    }
                    Rectangle normal=window.Bounds;window.WindowState=FormWindowState.Maximized;Application.DoEvents();window.WindowState=FormWindowState.Normal;Application.DoEvents();Check(window.Bounds==normal,"最大化还原改变尺寸");window.Close();
                }
            });
            foreach(bool dark in new[]{false,true}) {
                bool theme=dark;
                Run("联系人栏："+(theme?"深色":"浅色")+"拖动、窄栏、布局与键盘",()=>{
                    Theme.Configure(theme,"blue");LocalStore store=new LocalStore(Path.Combine(root,"sidebar-"+theme));
                    using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                        main.Show();Fill(main);Application.DoEvents();
                        Control divider=All(main).Single(c=>c.AccessibleName=="调整联系人栏宽度");
                        foreach(int width in new[]{200,240,336,480}) {
                            DragSidebar(main,width);Check(Math.Abs(divider.Left-width)<=1,"拖动宽度不准确："+divider.Left);
                            TabStrip tabs=All(main).OfType<TabStrip>().Single();Check(tabs.Controls.Cast<Control>().All(c=>tabs.ClientRectangle.Contains(c.Bounds)),"筛选标签超出联系人栏");
                            ChatEditor editor=All(main).OfType<ChatEditor>().Single();Check(editor.Width>100&&editor.Text=="第一行草稿\nSecond line English","输入框空间不足或草稿改变");
                            Render(main,"sidebar-"+theme+"-"+width);
                        }
                        DragSidebar(main,-100);Check(node.Settings.SidebarWidth==200,"未限制最小宽度");DragSidebar(main,10000);Check(node.Settings.SidebarWidth==480,"未限制最大宽度");
                        main.Size=main.MinimumSize;Application.DoEvents();Check(main.Width==720&&main.Height==520,"小窗口尺寸未开放");Check(main.ActiveChat.Width>=420,"联系人栏挤占聊天区");Render(main,"minimum-"+theme);
                        main.Size=new Size(1120,760);Application.DoEvents();Check(divider.Left==480&&node.Settings.SidebarWidth==480,"临时缩窗覆盖偏好宽度");
                        SendMessage(divider.Handle,0x100,new IntPtr((int)Keys.Home),IntPtr.Zero);Check(node.Settings.SidebarWidth==336,"Home 未恢复默认宽度");
                        SendMessage(divider.Handle,0x100,new IntPtr((int)Keys.Left),IntPtr.Zero);Check(node.Settings.SidebarWidth==328,"方向键未调整宽度");
                        main.WindowState=FormWindowState.Maximized;Application.DoEvents();Render(main,"maximized-"+theme);
                        main.WindowState=FormWindowState.Normal;All(main).OfType<ConversationList>().Single().SetItems(new ConversationList.Item[0]);DragSidebar(main,200);Render(main,"empty-"+theme);main.ExitApplication();
                    }
                });
            }
            Run("联系人宽度：旧配置、异常值、重启和设置保存",()=>{
                Theme.Configure(false,"blue");LocalStore store=new LocalStore(Path.Combine(root,"restore"));
                File.WriteAllText(Path.Combine(store.Root,"settings.json"),"{\"Nickname\":\"旧配置\"}");Check(store.LoadSettings().SidebarWidth==336,"旧配置默认值错误");
                using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                    main.Show();Application.DoEvents();DragSidebar(main,248);
                    using(Timer save=new Timer {Interval=40}) {
                        save.Tick+=delegate {SettingsDialog dialog=Application.OpenForms.OfType<SettingsDialog>().FirstOrDefault();if(dialog==null)return;save.Stop();All(dialog).OfType<Button>().Single(b=>b.Text=="保存").PerformClick();};
                        save.Start();main.OpenSettings();
                    }
                    Check(node.Settings.SidebarWidth==248,"保存其他设置覆盖宽度");main.ExitApplication();
                }
                AppSettings restored=store.LoadSettings();Check(restored.SidebarWidth==248,"退出未保存宽度");
                using(LanService node=new LanService(restored,24780,true,"restore")) using(MainForm main=new MainForm(node,store)) {
                    main.Show();Application.DoEvents();Check(All(main).Single(c=>c.AccessibleName=="调整联系人栏宽度").Left==248,"重启未恢复宽度");main.ExitApplication();
                }
                foreach(int invalid in new[]{0,-1,Int32.MaxValue}) using(LanService node=Node(store)) {
                    node.Settings.SidebarWidth=invalid;
                    using(MainForm main=new MainForm(node,store)) {main.Show();Application.DoEvents();int width=All(main).Single(c=>c.AccessibleName=="调整联系人栏宽度").Left;Check(width>=200&&width<=480,"异常配置未约束");main.ExitApplication();}
                }
            });
            Console.WriteLine("RESIZE LAYOUT: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
