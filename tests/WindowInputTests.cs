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
    internal static class WindowInputTests
    {
        private static int passed, failed;
        private static string root, output, label;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr window, Point point, uint flags);
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); } }
        private static System.Collections.Generic.IEnumerable<Control> All(Control parent) { foreach(Control child in parent.Controls) { yield return child; foreach(Control nested in All(child)) yield return nested; } }
        private static void Render(Control control, string name) { using(Bitmap image=new Bitmap(control.Width,control.Height)) { control.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.Combine(output,label+"-"+name+".png"),ImageFormat.Png); } }
        private static Peer Peer() { return new Peer { Endpoint=new IPEndPoint(IPAddress.Loopback,24791),Login="input-peer",Nickname="中文 English 输入回归",Online=true }; }
        private static LanService Node(LocalStore store) { return new LanService(new AppSettings {AutoReceiveFiles=false,Notifications=false,CloseToTray=false,ReceiveFolder=Path.Combine(store.Root,"received")},24790,true,"window-input"); }
        private static void ShiftEnter(ChatEditor editor)
        {
            byte[] original = new byte[256]; Check(GetKeyboardState(original), "无法读取测试键盘状态"); byte[] keys = (byte[])original.Clone(); keys[16] = keys[160] = 128; keys[17] = keys[18] = 0;
            try { Check(SetKeyboardState(keys), "无法设置测试 Shift 状态"); SendMessage(editor.Handle, 0x0100, new IntPtr(13), new IntPtr(0x001C0001)); SendMessage(editor.Handle, 0x0101, new IntPtr(13), new IntPtr(unchecked((int)0xC01C0001))); }
            finally { SetKeyboardState(original); }
            Application.DoEvents();
        }
        [STAThread] public static int Main(string[] args)
        {
            root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);label=args[2];
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Run("窗口：边缘保留客户区鼠标事件、最大化还原及固定对话框",()=>{
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(640,480)}) {
                    window.Show();Application.DoEvents();
                    int w=window.ClientSize.Width,h=window.ClientSize.Height;
                    Point[] points={new Point(3,h/2),new Point(w-4,h/2),new Point(w/2,3),new Point(w/2,h-4),new Point(3,3),new Point(w-4,3),new Point(3,h-4),new Point(w-4,h-4)};
                    for(int i=0;i<points.Length;i++) {
                        Check(ChildWindowFromPointEx(window.Handle,points[i],5)==window.Handle,"子控件遮住缩放区域："+points[i]);
                        Point screen=window.PointToScreen(points[i]);
                        int result=(int)SendMessage(window.Handle,0x84,IntPtr.Zero,new IntPtr(unchecked((screen.Y<<16)|(screen.X&65535))));
                        Check(result==1,"边缘未返回客户区鼠标事件："+points[i]+" -> "+result);
                    }
                    Size normal=window.Size;
                    SendMessage(window.Handle,0x112,new IntPtr(0xF030),IntPtr.Zero);Application.DoEvents();
                    Check(window.WindowState==FormWindowState.Maximized,"最大化失败");
                    SendMessage(window.Handle,0x112,new IntPtr(0xF120),IntPtr.Zero);Application.DoEvents();
                    Check(window.WindowState==FormWindowState.Normal,"还原失败");
                    Check(window.Size==normal,"最大化还原改变了普通窗口尺寸");
                    window.ResizeWindow=false;Application.DoEvents();
                    Check((GetWindowLong(window.Handle,-16)&0x40000)==0,"固定对话框仍可被系统缩放");window.Close();
                }
            });
            Run("窗口：按住边缘和角落实际改变尺寸并遵守最小尺寸",()=>{
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(640,480),MinimumSize=new Size(420,320)}) {
                    window.Show();Application.DoEvents();Size original=window.Size;
                    Point right=new Point(window.ClientSize.Width-1,window.ClientSize.Height/2);
                    IntPtr point=new IntPtr(unchecked((right.Y<<16)|(right.X&65535)));
                    SendMessage(window.Handle,0x0201,new IntPtr(1),point);
                    SendMessage(window.Handle,0x0200,new IntPtr(1),new IntPtr(unchecked((right.Y<<16)|((right.X+48)&65535))));
                    SendMessage(window.Handle,0x0202,IntPtr.Zero,new IntPtr(unchecked((right.Y<<16)|((right.X+48)&65535))));
                    Application.DoEvents();Check(window.Width==original.Width+48&&window.Height==original.Height,"拖动右边框没有改变窗口尺寸");
                    Point corner=new Point(window.ClientSize.Width-1,window.ClientSize.Height-1);
                    point=new IntPtr(unchecked((corner.Y<<16)|(corner.X&65535)));
                    SendMessage(window.Handle,0x0201,new IntPtr(1),point);
                    SendMessage(window.Handle,0x0200,new IntPtr(1),new IntPtr(unchecked(((corner.Y+32)<<16)|((corner.X+40)&65535))));
                    SendMessage(window.Handle,0x0202,IntPtr.Zero,new IntPtr(unchecked(((corner.Y+32)<<16)|((corner.X+40)&65535))));
                    Application.DoEvents();Check(window.Width==original.Width+88&&window.Height==original.Height+32,"拖动右下角没有同时改变宽高");
                    Point bottom=new Point(window.ClientSize.Width/2,window.ClientSize.Height-1);
                    point=new IntPtr(unchecked((bottom.Y<<16)|(bottom.X&65535)));
                    SendMessage(window.Handle,0x0201,new IntPtr(1),point);
                    SendMessage(window.Handle,0x0200,new IntPtr(1),new IntPtr(unchecked(((bottom.Y-2000)<<16)|(bottom.X&65535))));
                    SendMessage(window.Handle,0x0202,IntPtr.Zero,new IntPtr(unchecked(((bottom.Y-2000)<<16)|(bottom.X&65535))));
                    Application.DoEvents();Check(window.Height==window.MinimumSize.Height,"手动缩放未遵守最小高度");window.Close();
                }
            });
            foreach(bool dark in new[]{false,true}) foreach(int size in new[]{9,10,14,18}) {
                bool theme=dark;int fontSize=size;
                Run("输入："+(theme?"深色":"浅色")+" / "+fontSize+"pt 两到五行逐字输入与高度上限",()=>{
                    Theme.Configure(theme,"blue");LocalStore store=new LocalStore(Path.Combine(root,"input-"+theme+fontSize));
                    using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                        node.Settings.ChatFontSize=fontSize;main.Show();main.OpenChat(Peer());Application.DoEvents();
                        ChatEditor editor=All(main.ActiveChat).OfType<ChatEditor>().Single();editor.Focus();
                        Check(editor.ForeColor.ToArgb()==Theme.Ink.ToArgb(),"输入正文颜色未应用当前主题");
                        int contentHeight=0;editor.ContentsResized+=delegate(object sender,ContentsResizedEventArgs e){contentHeight=e.NewRectangle.Height;};
                        for(int lines=2;lines<=5;lines++) {
                            editor.Text="第 1 行 English"; editor.Select(editor.TextLength, 0);
                            for (int line = 2; line <= lines; line++) { ShiftEnter(editor); editor.SelectedText = "第 " + line + " 行 English"; }
                            Check(editor.Text == String.Join("\n", Enumerable.Range(1, lines).Select(n => "第 " + n + " 行 English")), "Shift+Enter 的实际输入与段落文本不一致");
                            Application.DoEvents();editor.Select(editor.TextLength,0);editor.ScrollToCaret();Application.DoEvents();
                            Point first=editor.GetPositionFromCharIndex(0),last=editor.GetPositionFromCharIndex(editor.TextLength-1);
                            Console.WriteLine("METRIC font="+fontSize+" lines="+lines+" editor="+editor.ClientSize+" fontHeight="+editor.Font.Height+" content="+contentHeight+" first="+first+" last="+last);
                            Render(main,"input-"+theme+fontSize+"-"+lines);
                            bool capped=contentHeight>editor.ClientSize.Height;
                            if(capped) {
                                Check(editor.Parent.Parent.Height>=main.ActiveChat.ClientSize.Height/3-1,"未达到高度上限就裁切正文");
                                Check(last.Y>=0&&last.Y<editor.ClientSize.Height,"高度受限后不能到达末行");
                                continue;
                            }
                            Check(first.Y>=0&&editor.FirstVisibleLine==0,"光标移到末尾后首行滚出顶部");
                            Check(last.Y+editor.Font.Height<=editor.ClientSize.Height,"末行空间不足，会触发原生自动滚动");
                            int scrolls=0;EventHandler onScroll=delegate {scrolls++;};editor.VScroll+=onScroll;
                            foreach(char ch in "新增 abc123") { SendMessage(editor.Handle,0x0102,new IntPtr(ch),IntPtr.Zero);Application.DoEvents();Check(editor.GetPositionFromCharIndex(0)==first,"逐字输入让已有首行移动"); }
                            editor.VScroll-=onScroll;Check(scrolls==0,"逐字输入发生原生滚动："+scrolls);
                        }
                        main.ExitApplication();
                    }
                });
            }
            Run("布局与恢复：最小、常用、最大化；自动换行、长草稿、删除与重启",()=>{
                Theme.Configure(false,"blue");LocalStore store=new LocalStore(Path.Combine(root,"restore"));Peer peer=Peer();Rectangle saved;
                using(LanService node=Node(store)) using(MainForm main=new MainForm(node,store)) {
                    main.Show();main.OpenChat(peer);Application.DoEvents();ChatEditor editor=All(main.ActiveChat).OfType<ChatEditor>().Single();
                    foreach(int mode in new[]{0,1,2}) {
                        main.WindowState=FormWindowState.Normal;main.Size=mode==0?main.MinimumSize:new Size(1120,760);if(mode==2)main.WindowState=FormWindowState.Maximized;Application.DoEvents();
                        editor.Text="";Application.DoEvents();Render(main,"layout-"+mode+"-empty");
                        editor.Text=new string('文',160)+" English 😀";Application.DoEvents();Render(main,"layout-"+mode+"-wrap");
                        Check(main.ActiveChat.ClientRectangle.Contains(main.ActiveChat.RectangleToClient(editor.RectangleToScreen(editor.ClientRectangle))),"输入框超出聊天区");
                        Check(editor.GetLineFromCharIndex(editor.TextLength)>0,"长中文没有自动换行");
                    }
                    editor.Text=String.Join("\n",Enumerable.Range(0,100).Select(n=>"长草稿 "+n));Application.DoEvents();editor.Select(editor.TextLength,0);editor.ScrollToCaret();
                    Check(editor.FirstVisibleLine>0,"长草稿不能滚动到末尾");editor.Text="首行\n第二行";Application.DoEvents();
                    Check(editor.FirstVisibleLine==0&&editor.GetPositionFromCharIndex(0).Y>=0,"删短后首行没有恢复");
                    main.WindowState=FormWindowState.Normal;main.Size=new Size(960,640);Application.DoEvents();saved=main.Bounds;main.ActiveChat.SaveDraft();main.ExitApplication();
                }
                AppSettings restored=store.LoadSettings();Check(restored.Window.Width==saved.Width&&restored.Window.Height==saved.Height,"缩放尺寸未保存");
                using(LanService node=new LanService(restored,24790,true,"window-input")) using(MainForm main=new MainForm(node,store)) {
                    main.Show();main.OpenChat(peer);Application.DoEvents();Check(main.Size==saved.Size,"重启尺寸未恢复");Check(All(main.ActiveChat).OfType<ChatEditor>().Single().Text=="首行\n第二行","重启多行草稿未恢复");main.ExitApplication();
                }
            });
            Console.WriteLine("WINDOW INPUT: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
