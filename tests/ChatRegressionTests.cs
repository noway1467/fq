using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class ChatRegressionTests
    {
        private static int passed, failed;
        private static string root, output;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Run(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); } }
        private static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target); }
        private sealed class NativeReads : NativeWindow, IDisposable
        {
            internal int StreamOut;
            internal NativeReads(Control editor) { AssignHandle(editor.Handle); }
            protected override void WndProc(ref Message message) { if (message.Msg == 0x044A) StreamOut++; base.WndProc(ref message); }
            public void Dispose() { ReleaseHandle(); }
        }
        private static IEnumerable<Control> All(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in All(child)) yield return nested; } }
        [STAThread] public static int Main(string[] args)
        {
            root = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]); Directory.CreateDirectory(root); Directory.CreateDirectory(output);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Run("旧版协议：新装和缺字段配置默认开启，手动关闭后重启保持", () => {
                LocalStore store = new LocalStore(Path.Combine(root, "settings"));
                Check(store.LoadSettings().AllowLegacyProtocol, "新装未默认开启兼容");
                File.WriteAllText(Path.Combine(store.Root, "settings.json"), "{\"Nickname\":\"旧配置\"}");
                AppSettings settings = store.LoadSettings(); Check(settings.AllowLegacyProtocol && settings.Nickname == "旧配置", "缺字段的旧配置未开启兼容");
                File.WriteAllText(Path.Combine(store.Root, "settings.json"), "{\"Nickname\":\"0.7.0\",\"AllowLegacyProtocol\":false}");
                Check(store.LoadSettings().AllowLegacyProtocol, "0.7.0 默认关闭的旧配置未迁移");
                settings.AllowLegacyProtocol = false; store.SaveSettings(settings);
                Check(!new LocalStore(store.Root).LoadSettings().AllowLegacyProtocol, "重启覆盖了已保存的关闭选择");
                settings.AllowLegacyProtocol = true; store.SaveSettings(settings);
                Check(new LocalStore(store.Root).LoadSettings().AllowLegacyProtocol, "开启未持久保存");
            });
            Run("输入框：读取正文不再导出流，中文、换行、表情和选区与系统控件一致", () => {
                using (Form host = new Form { ClientSize = new Size(640,240) })
                using (ChatEditor editor = new ChatEditor { Dock = DockStyle.Top, Height = 100 })
                using (RichTextBox reference = new RichTextBox { Dock = DockStyle.Bottom, Height = 100 }) {
                    host.Controls.Add(editor); host.Controls.Add(reference); host.Show(); Application.DoEvents();
                    foreach (string sample in new[] { "", "中文输入 English 123", "首行\r\n第二行\n第三行\r末行\t制表", "😀 👩🏽‍💻 e\u0301\n\n结束\n", new string('文', 20000) }) {
                        editor.Text = reference.Text = sample; editor.Select(Math.Min(2, editor.TextLength), Math.Min(3, Math.Max(0, editor.TextLength - 2)));
                        int start = editor.SelectionStart, length = editor.SelectionLength;
                        using (NativeReads reads = new NativeReads(editor)) {
                            Check(editor.Text == reference.Text, "正文或段落格式与系统控件不一致");
                            Check(reads.StreamOut == 0, "读取正文仍触发会打断拼音的 EM_STREAMOUT");
                        }
                        Check(start == editor.SelectionStart && length == editor.SelectionLength, "只读查询改变选区");
                    }
                    host.Close();
                }
            });
            Run("输入法消息：选词时不发送、不保存临时拼音，完成后恢复草稿与布局", () => {
                LocalStore store = new LocalStore(Path.Combine(root,"compose"));
                using (LanService node = new LanService(new AppSettings { AutoReceiveFiles=false,Notifications=false,ReceiveFolder=Path.Combine(root,"received") }, 24250, true, "compose"))
                using (MainForm main = new MainForm(node,store)) {
                    main.Show(); Peer peer = new Peer { Endpoint=new IPEndPoint(IPAddress.Loopback,24251),Login="peer",Nickname="输入法回归",Online=true };
                    main.OpenChat(peer); Application.DoEvents(); ChatForm chat=main.ActiveChat;
                    ChatEditor editor=All(chat).OfType<ChatEditor>().Single(); editor.Text="已确认草稿"; chat.SaveDraft();
                    SendMessage(editor.Handle,0x010D,IntPtr.Zero,IntPtr.Zero); Check(editor.IsComposing,"未识别组合开始");
                    KeyEventArgs enter=new KeyEventArgs(Keys.Enter);
                    typeof(Control).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(editor,new object[]{enter});
                    Check(!enter.SuppressKeyPress && !(bool)Field(chat,"sending"),"选词 Enter 被聊天发送抢走");
                    chat.SaveDraft(); Check(store.LoadDraft(peer.Id)=="已确认草稿","组合期间覆盖已确认草稿");
                    using(NativeReads reads=new NativeReads(editor)) { Check(editor.Text=="已确认草稿","组合期间正文查询损坏"); Check(reads.StreamOut==0,"组合期间仍导出流"); }
                    SendMessage(editor.Handle,0x010E,IntPtr.Zero,IntPtr.Zero); Application.DoEvents(); Check(!editor.IsComposing,"组合结束后未解除状态");
                    editor.AppendText("，中文完成");chat.SaveDraft();Check(store.LoadDraft(peer.Id)=="已确认草稿，中文完成","完成后未恢复保存");
                    main.ExitApplication();
                }
            });
            Run("历史文件：重启后气泡可以继续接收，已拒绝或已保存文件不复活", () => {
                LocalStore store=new LocalStore(Path.Combine(root,"history"));Peer peer=new Peer {Endpoint=new IPEndPoint(IPAddress.Loopback,24351),Login="history",Nickname="历史文件"};
                foreach(int id in new[]{1,2,3})store.AppendIncoming(new ChatRecord {PeerId=peer.Id,Packet=id,Time=DateTime.Now,Text="历史邀请",Transfers=new[]{new FileTransferState {Session="previous-process",Id=0,Name="history.txt",Size=1,Sha256=new string('a',64),Status=id==2?"已拒绝":id==3?"已保存":"待接收 · 点击接收",LocalPath=id==3?Path.Combine(root,"already-saved.txt"):null}}});
                using(LanService node=new LanService(new AppSettings {AutoReceiveFiles=false,Notifications=false},24350,true,"history-local"))
                using(MainForm main=new MainForm(node,store)) {
                    main.Show();main.OpenChat(peer);Application.DoEvents();ChatForm chat=main.ActiveChat;Check(chat.PendingFileCount==1,"已拒绝或已保存文件进入恢复队列");
                    BubbleHistory history=All(chat).OfType<BubbleHistory>().Single();ChatRecord restored=((List<ChatRecord>)Field(history,"records")).Single(r=>r.Packet==1);
                    bool receivable=(bool)typeof(BubbleHistory).GetMethod("Receivable",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{restored,restored.Transfers[0]});
                    Check(receivable,"已恢复邀请的气泡仍不能点击续传");main.ExitApplication();
                }
            });
            Run("气泡：单双行、数字、链接、表情、附件和空时间的底部时间行不重叠", () => {
                foreach(bool dark in new[]{false,true}) foreach(int width in new[]{360,768,1280}) {
                    Theme.Configure(dark,"rose");
                    using(Form host=new Form { ClientSize=new Size(width,760) })
                    using(BubbleHistory history=new BubbleHistory { Dock=DockStyle.Fill }) {
                        host.Controls.Add(history);host.Show();history.SetAvatars("incoming","builtin:1","own","builtin:2");
                        ChatRecord[] records={
                            new ChatRecord {Packet=1,Text="无时间的历史",Time=default(DateTime)},
                            new ChatRecord {Packet=2,Text="0",Time=DateTime.Today.AddHours(13).AddMinutes(10)},
                            new ChatRecord {Packet=3,Text="123",Outgoing=true,State="已送达",Time=DateTime.Now},
                            new ChatRecord {Packet=4,Text="中文消息\nEnglish 第二行 😀",Time=DateTime.Now},
                            new ChatRecord {Packet=5,Text="https://example.com/",Outgoing=true,State="未确认送达",Time=DateTime.Now},
                            new ChatRecord {Packet=6,Text="😀",Time=DateTime.Now},
                            new ChatRecord {Packet=7,Text="附件",Time=DateTime.Now,Transfers=new[]{new FileTransferState {Id=0,Name="长文件名-中文-English.txt",Size=1024,Status="待接收"}}}
                        };
                        history.SetRecords(records);Application.DoEvents();history.Refresh();
                        using(Bitmap image=new Bitmap(history.Width,history.Height)) {
                            history.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                            var bounds=(List<Rectangle>)Field(history,"bounds");var items=(System.Collections.IList)Field(history,"items");
                            for(int i=0;i<records.Length;i++) {
                                Rectangle bubble=bounds[i]; object measure=Field(items[i],"M");Size text=(Size)Field(measure,"TextSize");
                                int padTop=(int)typeof(BubbleHistory).GetProperty("PadTop",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history,null);
                                int padBottom=(int)typeof(BubbleHistory).GetProperty("PadBottom",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history,null);
                                int stampHeight=(int)typeof(BubbleHistory).GetProperty("StampHeight",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(history,null);
                                Check(bubble.Height-padBottom-stampHeight>=padTop+text.Height+3,"时间与正文没有独立间距："+records[i].Text);
                                Check(bubble.Left>=0 && bubble.Right<=history.ClientSize.Width,"气泡横向越界");
                            }
                            image.Save(Path.Combine(output,"bubbles-"+width+"-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
                        }
                        history.SetRecords(new ChatRecord[0]);Application.DoEvents();Check(history.TextLength==0,"空记录未清空");host.Close();
                    }
                }
                Theme.Configure(false,"blue");
            });
            Console.WriteLine("CHAT REGRESSION: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
