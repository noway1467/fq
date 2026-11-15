using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
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
            Console.WriteLine("RESULT passed="+passed+" failed="+failed);
            return failed==0?0:1;
        }
    }
}
