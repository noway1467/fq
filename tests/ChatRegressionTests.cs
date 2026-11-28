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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, string text);
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] keys);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] keys);
        [StructLayout(LayoutKind.Sequential)] private struct GetTextOptions { internal uint Bytes, Flags, CodePage; internal IntPtr DefaultChar, UsedDefaultChar; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, ref GetTextOptions options, System.Text.StringBuilder text);
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
        private static void ShiftEnter(ChatEditor editor)
        {
            byte[] original = new byte[256]; Check(GetKeyboardState(original), "无法读取测试线程的键盘状态");
            byte[] shifted = (byte[])original.Clone(); shifted[16] = shifted[160] = 128; shifted[17] = shifted[18] = 0;
            try
            {
                Check(SetKeyboardState(shifted), "无法设置测试线程的 Shift 状态");
                SendMessage(editor.Handle, 0x0100, new IntPtr((int)Keys.Enter), new IntPtr(0x001C0001));
                SendMessage(editor.Handle, 0x0101, new IntPtr((int)Keys.Enter), new IntPtr(unchecked((int)0xC01C0001)));
            }
            finally { SetKeyboardState(original); }
            Application.DoEvents();
        }
        private static string NativeText(ChatEditor editor)
        {
            var text = new System.Text.StringBuilder(editor.TextLength + 1);
            var options = new GetTextOptions { Bytes = checked((uint)(editor.TextLength + 1) * 2), CodePage = 1200 };
            SendMessage(editor.Handle, 0x045E, ref options, text); return text.ToString();
        }
        private static Bitmap Render(Control control)
        {
            Bitmap image = new Bitmap(control.Width, control.Height); control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); return image;
        }
        private static string SaveClipboard()
        {
            for (int attempt = 0; attempt < 5; attempt++) { try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch (ExternalException) { System.Threading.Thread.Sleep(30); } }
            return null;
        }
        private static void RestoreClipboard(string text)
        {
            for (int attempt = 0; attempt < 5; attempt++) { try { if (text != null) Clipboard.SetText(text); else Clipboard.Clear(); return; } catch (ExternalException) { System.Threading.Thread.Sleep(30); } }
        }
        private static void SamePixels(Bitmap actual, Bitmap expected, string reason)
        {
            Check(actual.Size == expected.Size, reason + "：图片尺寸不同");
            for (int y = 0; y < actual.Height; y++) for (int x = 0; x < actual.Width; x++)
                if (actual.GetPixel(x, y) != expected.GetPixel(x, y)) throw new Exception(reason + "：像素不同 " + x + "," + y);
        }
        [STAThread] public static int Main(string[] args)
        {
            root = Path.GetFullPath(args[0]); output = Path.GetFullPath(args[1]); Directory.CreateDirectory(root); Directory.CreateDirectory(output);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Run("图片气泡：比例、贴边、状态标签、文件命中与异步阅读位置", () => {
                string folder=Path.Combine(root,"media-layout");Directory.CreateDirectory(folder);
                string portrait=Path.Combine(folder,"竖图 中文 English.png"),landscape=Path.Combine(folder,"横图.png"),small=Path.Combine(folder,"小图.png");
                string[] paths={portrait,landscape,small};Size[] sizes={new Size(240,480),new Size(600,240),new Size(20,20)};
                for(int n=0;n<paths.Length;n++)using(Bitmap image=new Bitmap(sizes[n].Width,sizes[n].Height))using(Graphics g=Graphics.FromImage(image)) {
                    g.Clear(Color.FromArgb(213,201,175));using(SolidBrush b=new SolidBrush(Color.FromArgb(69,107,100)))g.FillRectangle(b,0,image.Height/2,image.Width,image.Height/2);
                    using(SolidBrush b=new SolidBrush(Color.FromArgb(231,185,102)))g.FillEllipse(b,image.Width*.53F,image.Height*.18F,image.Width*.24F,image.Width*.24F);image.Save(paths[n],ImageFormat.Png);
                }
                foreach(bool dark in new[]{false,true})using(Form host=new Form {ClientSize=new Size(760,860)})using(BubbleHistory history=new BubbleHistory {Dock=DockStyle.Fill}) {
                    Theme.Change(dark,"blue");host.Controls.Add(history);host.Show();Application.DoEvents();history.SetAvatars("media-peer",null,"media-self",null);
                    List<ChatRecord> rows=new List<ChatRecord>();for(int n=0;n<paths.Length;n++)rows.Add(new ChatRecord {Packet=n+1,Outgoing=n!=1,Time=DateTime.Now,State=n==1?"已保存":"已送达",Transfers=new[]{new FileTransferState {Id=0,Name=Path.GetFileName(paths[n]),LocalPath=paths[n],Size=new FileInfo(paths[n]).Length,Status=n==1?"已保存":"等待对方接收"}}});
                    history.SetRecords(rows);history.Refresh();
                    var previews=(Dictionary<string,Bitmap>)Field(history,"previews");DateTime limit=DateTime.UtcNow.AddSeconds(10);
                    while(previews.Count<3&&DateTime.UtcNow<limit){Application.DoEvents();System.Threading.Thread.Sleep(10);history.Refresh();}
                    Check(previews.Count==3&&previews.Values.All(b=>b!=null),"系统缩略图未就绪");history.ScrollToLatest();Application.DoEvents();history.Refresh();
                    var areas=(List<Rectangle>)Field(history,"bounds");var media=(List<Tuple<Rectangle,string>>)Field(history,"mediaHits");
                    foreach(var hit in media) {
                        int index=Array.IndexOf(paths,hit.Item2);if(index<2)Check(Math.Abs((double)hit.Item1.Width/hit.Item1.Height-(index==0?.5:2.5))<.035,"照片宽高比改变");
                        Check(areas[index].Height-hit.Item1.Height<=12,"单图片仍保留说明/时间空行");
                    }
                    var files=(List<Tuple<Rectangle,ChatRecord,int>>)Field(history,"fileHits");Check(files.Count==media.Count,"图片丢失文件命中区");
                    var stamps=(List<Tuple<Rectangle,ChatRecord>>)Field(history,"stampHits");foreach(var stamp in stamps){var file=files.Single(f=>f.Item2==stamp.Item2);Check(file.Item1.Contains(stamp.Item1),"时间标签未叠在图片内");}
                    using(Bitmap image=Render(host))image.Save(Path.Combine(output,"windows-media-polish-"+(dark?"dark":"light")+".png"),ImageFormat.Png);
                    // 加载新照片时，读旧消息的位置不能因上方图片的高度改变而跳走。
                    for(int n=0;n<30;n++)rows.Add(new ChatRecord {Packet=100+n,Text="旧消息阅读位置 "+n,Time=DateTime.Now,State="已收到"});
                    history.SetRecords(rows);history.AutoScrollPosition=new Point(0,400);history.Refresh();
                    MethodInfo top=typeof(BubbleHistory).GetMethod("TopAnchor",BindingFlags.Instance|BindingFlags.NonPublic);var before=(Tuple<ChatRecord,int>)top.Invoke(history,null);
                    string extra=Path.Combine(folder,"异步.png");using(Bitmap image=new Bitmap(180,480))image.Save(extra,ImageFormat.Png);
                    rows[0].Transfers[0].LocalPath=extra;history.Delivery(rows[0]);history.Refresh();
                    typeof(BubbleHistory).GetMethod("LoadPreview",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(history,new object[]{extra});limit=DateTime.UtcNow.AddSeconds(10);
                    while(!previews.ContainsKey(extra)&&DateTime.UtcNow<limit){Application.DoEvents();System.Threading.Thread.Sleep(10);history.Refresh();}
                    Check(previews.ContainsKey(extra)&&previews[extra]!=null,"异步缩略图没有完成");
                    var after=(Tuple<ChatRecord,int>)top.Invoke(history,null);Check(before.Item1==after.Item1&&Math.Abs(before.Item2-after.Item2)<=1,"异步图片重排丢失阅读锚点："+before.Item1.Packet+"/"+before.Item2+" → "+after.Item1.Packet+"/"+after.Item2);host.Close();
                }
                Theme.Change(false,"blue");
            });
            Run("旧配置忽略明文开关，保留资料且不能发送明文", () => {
                LocalStore store = new LocalStore(Path.Combine(root, "settings"));
                string path=Path.Combine(store.Root,"settings.json"),id=new string('a',32);
                foreach(string oldFields in new[]{"",",\"AllowLegacyProtocol\":false",",\"AllowLegacyProtocol\":true,\"SettingsRevision\":1"}) {
                    File.WriteAllText(path,"{\"Nickname\":\"旧配置\",\"DeviceId\":\""+id+"\",\"Port\":34567"+oldFields+"}");
                    AppSettings settings=store.LoadSettings();Check(store.Warning==null&&settings.Nickname=="旧配置"&&settings.DeviceId==id&&settings.Port==34567,"旧配置被重置");
                    using(var node=new LanService(settings,34567,true,"upgrade")) {
                        try { node.SendMessage(new Peer{Endpoint=new IPEndPoint(IPAddress.Loopback,2425)},"test",null);throw new Exception("仍可发送明文"); }
                        catch(IOException error){Check(error.Message.Contains("升级至 0.8"),"缺少升级提示");}
                    }
                    store.SaveSettings(settings);Check(!File.ReadAllText(path).Contains("AllowLegacyProtocol")&&new LocalStore(store.Root).LoadSettings().DeviceId==id,"重启未保留设备身份");
                }
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
            Run("换行输入：真实键盘消息 Shift+Enter、旧草稿及粘贴保留撤销、代理对和长度边界", () => {
                string legacy = "首行\v第二行\r\n第三行\r末行\u2028第五行\u2029😀";
                string normalized = "首行\n第二行\n第三行\n末行\n第五行\n😀";
                LocalStore store = new LocalStore(Path.Combine(root, "line-input"));
                Peer peer = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, 24481), Login = "line-input", Nickname = "换行输入回归", Online = false };
                store.SaveDraft(peer.Id, "seed"); string draft = Directory.GetFiles(store.Root, "*.draft.txt", SearchOption.AllDirectories).Single();
                File.WriteAllText(draft, legacy, new System.Text.UTF8Encoding(false));
                string clipboard = SaveClipboard();
                try
                {
                    using (LanService node = new LanService(new AppSettings { Notifications = false, AutoReceiveFiles = false }, 24480, true, "line-local"))
                    using (MainForm main = new MainForm(node, store))
                    {
                        node.Start(); main.Show(); main.OpenChat(peer); Application.DoEvents();
                        ChatEditor editor = All(main.ActiveChat).OfType<ChatEditor>().Single();
                        Check(editor.Text == normalized && !NativeText(editor).Contains("\v"), "旧草稿未转换成标准段落");
                        using (NativeReads reads = new NativeReads(editor))
                        {
                            editor.Text = "A😀B"; editor.MaxLength = 5; editor.Select(3, 0); ShiftEnter(editor);
                            Check(editor.Text == "A😀\nB" && !NativeText(editor).Contains("\v"), "Shift+Enter 仍插入软换行或被当作发送");
                            Check(editor.SelectionStart == 4 && editor.CanUndo, "换行未保留光标或撤销");
                            editor.Undo(); Check(editor.Text == "A😀B", "撤销换行未恢复原文");
                            editor.MaxLength = 4; editor.Select(editor.TextLength, 0); ShiftEnter(editor);
                            Check(editor.Text == "A😀B" && editor.SelectionStart == 4, "满输入框仍插入换行或改变光标");
                            editor.Select(1, 2); ShiftEnter(editor); Check(editor.Text == "A\nB", "换行没有替换完整表情选区");
                            editor.Undo(); Check(editor.Text == "A😀B", "选区替换不能撤销");
                            editor.MaxLength = 5; editor.Select(2, 0); int caret = editor.SelectionStart; ShiftEnter(editor);
                            Check(editor.Text == (caret == 1 ? "A\n😀B" : "A😀\nB"), "换行没有保留原生控件的表情插入边界");
                            editor.MaxLength = 100; editor.Text = ""; Clipboard.SetText(legacy); Theme.PasteText(editor);
                            Check(editor.Text == normalized && !NativeText(editor).Contains("\v"), "粘贴没有统一换行");
                            editor.MaxLength = 6; editor.Text = "a"; editor.Select(1, 0); Clipboard.SetText("abcd\r\n😀"); Theme.PasteText(editor);
                            Check(editor.Text == "aabcd\n", "粘贴按原始 CRLF 计数或截断了表情");
                            editor.Select(0, 0); Clipboard.SetText("😀"); Theme.PasteText(editor);
                            Check(editor.Text == "aabcd\n" && editor.SelectionStart == 0, "满输入框粘贴改变了原文或光标");
                            editor.MaxLength = 100; editor.Text = "";
                            SendMessage(editor.Handle, 0x00C2, new IntPtr(1), legacy);
                            Check(editor.Text == normalized && editor.Text.Length == editor.TextLength, "原生旧软换行读取破坏了字符索引");
                            Check(reads.StreamOut == 0, "换行、粘贴或撤销触发 EM_STREAMOUT：" + reads.StreamOut);
                        }
                        main.ExitApplication();
                    }
                    File.WriteAllText(draft, new string('a', MessageText.InputLimit - 1) + "😀", new System.Text.UTF8Encoding(false));
                    string loaded = store.LoadDraft(peer.Id); Check(loaded.Length == MessageText.InputLimit - 1 && !Char.IsSurrogate(loaded[loaded.Length - 1]), "旧长草稿截断留下了半个代理对");
                }
                finally { RestoreClipboard(clipboard); }
            });
            Run("换行摘要和布局：六种分隔符、URL、表情及高亮与标准 LF 渲染完全一致", () => {
                string[] separators = { "\n", "\r\n", "\r", "\v", "\u2028", "\u2029" };
                Check(MessageText.NormalizeLineBreaks(null) == "", "空正文未兼容");
                foreach (string separator in separators)
                {
                    string sample = "首行" + separator + "https://example.com/a" + separator + "定位 😀" + separator + "www.example.org/b";
                    string normal = "首行\nhttps://example.com/a\n定位 😀\nwww.example.org/b";
                    Check(MessageText.Preview(sample) == normal, "短摘要遗漏换行符");
                    string longLines = String.Join(separator, Enumerable.Range(1, 12).Select(i => "第" + i + "行"));
                    Check(MessageText.Preview(longLines) == String.Join("\n", Enumerable.Range(1, 8).Select(i => "第" + i + "行")) + "\n… 查看全文（双击）", "八行摘要阈值错误");
                    MessageLayout layout = new MessageLayout(sample, Theme.Body, 500, "定位"), expected = new MessageLayout(normal, Theme.Body, 500, "定位");
                    Check(layout.Height == expected.Height && layout.Height >= layout.LineHeight * 4, "多行正文被挤成一行");
                    Check(layout.Runs.Select(r => r.Text).SequenceEqual(expected.Runs.Select(r => r.Text)) && layout.Runs.Select(r => r.Bounds).SequenceEqual(expected.Runs.Select(r => r.Bounds)), "换行影响分段、表情或高亮索引");
                    Check(layout.Runs.Any(r => r.Text.Contains("定位") && r.Highlight), "搜索高亮索引错位");
                    using (Bitmap image = new Bitmap(520, layout.Height + 4)) using (Bitmap reference = new Bitmap(520, expected.Height + 4))
                    {
                        var hits = new List<Tuple<Rectangle, string>>();
                        using (Graphics g = Graphics.FromImage(image)) { g.Clear(Theme.Surface); layout.Draw(g, Point.Empty, Theme.Body, hits); }
                        using (Graphics g = Graphics.FromImage(reference)) { g.Clear(Theme.Surface); expected.Draw(g, Point.Empty, Theme.Body, new List<Tuple<Rectangle, string>>()); }
                        SamePixels(image, reference, "旧换行渲染出现方块或字符错位");
                        Check(hits.Select(h => h.Item2).Distinct().Count() == 2 && hits.All(h => h.Item1.Top >= layout.LineHeight), "链接命中没有跟随换行");
                    }
                }
            });
            Run("旧历史显示：纯文本和富文本气泡、会话预览、复制及全文均不传播软换行", () => {
                string raw = "首行\v第二行\r第三行\u2028第四行\u2029末行", rich = "https://example.com/\r\n定位😀\vwww.example.org/";
                string plain = "首行\n第二行\n第三行\n第四行\n末行", richPlain = "https://example.com/\n定位😀\nwww.example.org/";
                string clipboard = SaveClipboard();
                try
                {
                    using (Form host = new Form { ClientSize = new Size(768, 600) }) using (BubbleHistory history = new BubbleHistory { Dock = DockStyle.Fill })
                    {
                        host.Controls.Add(history); host.Show();
                        ChatRecord[] legacy = { new ChatRecord { Text = raw }, new ChatRecord { Text = rich } };
                        history.SetRecords(legacy); Application.DoEvents();
                        var items = (System.Collections.IList)Field(history, "items");
                        Check(((MessageLayout)Field(Field(items[0], "M"), "Layout")).SourceText == plain, "纯文本显示未规范化");
                        Check(((MessageLayout)Field(Field(items[1], "M"), "Layout")).SourceText == richPlain, "链接与表情显示未规范化");
                        using (Bitmap actual = Render(history))
                        {
                            history.SetRecords(new[] { new ChatRecord { Text = plain }, new ChatRecord { Text = richPlain } }); Application.DoEvents();
                            using (Bitmap expected = Render(history)) SamePixels(actual, expected, "旧历史气泡与 LF 正文绘制不一致");
                            actual.Save(Path.Combine(output, "legacy-line-breaks.png"), ImageFormat.Png);
                        }
                        history.SetRecords(legacy, "定位"); Application.DoEvents(); Render(history).Dispose();
                        Check(((MessageLayout)Field(Field(((System.Collections.IList)Field(history, "items"))[1], "M"), "Layout")).Runs.Any(r => r.Highlight), "旧历史搜索高亮没有命中");
                        typeof(BubbleHistory).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(history, 0);
                        typeof(BubbleHistory).GetMethod("CopySelected", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(history, null);
                        Check(Clipboard.GetText() == plain && legacy[0].Text == raw, "复制仍含软换行或修改了历史原文");
                        history.SelectAllMessages(); typeof(BubbleHistory).GetMethod("CopySelected", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(history, null);
                        Check(Clipboard.GetText() == plain + "\n" + richPlain, "多选复制传播旧换行");
                        using (MessageTextDialog dialog = new MessageTextDialog(raw))
                        {
                            dialog.Show(host); Application.DoEvents(); RichTextBox full = All(dialog).OfType<RichTextBox>().Single();
                            Check(full.Text == plain, "全文没有规范化旧历史"); full.SelectAll(); full.Copy(); Check(!Clipboard.GetText().Contains("\v"), "全文选段复制仍传播软换行"); dialog.Close();
                        }
                        host.Close();
                    }
                    using (Form host = new Form { ClientSize = new Size(360, 120) }) using (ConversationList list = new ConversationList { Dock = DockStyle.Fill })
                    {
                        host.Controls.Add(list); host.Show(); Peer peer = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, 24491), Login = "preview", Nickname = "旧会话" };
                        list.SetItems(new[] { new ConversationList.Item { Peer = peer, Preview = "你：" + raw } }); Application.DoEvents();
                        using (Bitmap actual = Render(list))
                        {
                            list.SetItems(new[] { new ConversationList.Item { Peer = peer, Preview = "你：" + plain.Replace('\n', ' ') } }); Application.DoEvents();
                            using (Bitmap expected = Render(list)) SamePixels(actual, expected, "会话单行预览出现换行方块");
                        }
                        SavedConversation saved = SavedConversation.Normalize(new[] { new SavedConversation { Endpoint = peer.Endpoint.ToString(), Login = peer.Login, Preview = raw } }).Single();
                        Check(saved.Preview == plain.Replace('\n', ' ') && Protocol.Field(raw) == saved.Preview, "持久化预览或单行协议字段遗漏换行"); host.Close();
                    }
                }
                finally { RestoreClipboard(clipboard); }
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
            Run("取消文本渲染：明暗主题和三种宽度都有红点与标签，状态缓存失效且不出现重发入口", () => {
                foreach (bool dark in new[] { false, true }) foreach (int width in new[] { 360, 768, 1280 })
                {
                    Theme.Configure(dark, "blue");
                    using (Form host = new Form { ClientSize = new Size(width, 420) }) using (BubbleHistory history = new BubbleHistory { Dock = DockStyle.Fill })
                    {
                        host.Controls.Add(history); host.Show(); history.SetAvatars("peer", "builtin:1", "own", "builtin:2");
                        ChatRecord[] records = { new ChatRecord { Packet = 1, Text = "0", Outgoing = true, State = "已送达", Time = DateTime.Today.AddHours(19).AddMinutes(42) }, new ChatRecord { Packet = 2, Text = "123", Outgoing = true, State = "已取消发送", Time = DateTime.Today.AddHours(19).AddMinutes(43) } };
                        history.SetRecords(records); Application.DoEvents();
                        object previous = Field(((System.Collections.IList)Field(history, "items"))[0], "M");
                        records[0].State = "未确认送达"; history.Delivery(records[0]);
                        object failedMeasure = Field(((System.Collections.IList)Field(history, "items"))[0], "M");
                        Check((string)Field(previous, "ContentKey") != (string)Field(failedMeasure, "ContentKey"), "同宽图标的状态变化命中旧缓存");
                        records[0].State = "已取消发送"; history.Delivery(records[0]); Application.DoEvents();
                        history.RetryRequested += delegate { throw new Exception("取消消息不应能重发"); };
                        history.CanCancelSend = r => r.State == "等待确认"; history.CancelSendRequested += delegate { throw new Exception("取消消息不应能再次取消"); };
                        using (Bitmap image = Render(history))
                        {
                            var hits = (List<Tuple<Rectangle, ChatRecord>>)Field(history, "stampHits"); var bounds = (List<Rectangle>)Field(history, "bounds"); var items = (System.Collections.IList)Field(history, "items");
                            Check(hits.Count == 2 && ((System.Collections.IList)Field(history, "retryHits")).Count == 0, "取消文本缺少状态区或仍有重发红色感叹号");
                            for (int i = 0; i < records.Length; i++)
                            {
                                Rectangle stamp = hits[i].Item1, bubble = bounds[i]; object measure = Field(items[i], "M"); Size text = (Size)Field(measure, "TextSize");
                                int padTop = (int)typeof(BubbleHistory).GetProperty("PadTop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(history, null);
                                Check(bubble.Contains(stamp) && bubble.Left >= 0 && bubble.Right <= history.Width, "取消状态区越界");
                                Check(stamp.Top >= bubble.Top + padTop + text.Height + 3, "取消状态行与正文重叠");
                                string label = (string)typeof(BubbleHistory).GetMethod("StatusLabel", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { records[i] });
                                Check(label == "已取消发送" && ((string)Field(measure, "ContentKey")).Contains(records[i].State), "缺少明确标签或缓存没有记录取消状态");
                                int red = 0; for (int y = stamp.Top; y < stamp.Bottom; y++) for (int x = stamp.Left; x < stamp.Right; x++) if (image.GetPixel(x, y).ToArgb() == Theme.Danger.ToArgb()) red++;
                                int prefix = TextRenderer.MeasureText(records[i].Time.ToString("HH:mm"), Theme.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + (int)Math.Round(7 * history.DeviceDpi / 96F);
                                int dotSize = (int)Math.Round(6 * history.DeviceDpi / 96F);
                                int dotRed = 0; for (int y = stamp.Top; y < stamp.Bottom; y++) for (int x = stamp.Left + prefix; x < Math.Min(stamp.Right, stamp.Left + prefix + dotSize); x++) if (image.GetPixel(x, y).ToArgb() == Theme.Danger.ToArgb()) dotRed++;
                                Check(red > 15 && dotRed >= 4, "取消标签或状态点没有实际绘制危险色像素");
                            }
                            image.Save(Path.Combine(output, "canceled-text-" + width + "-" + (dark ? "dark" : "light") + ".png"), ImageFormat.Png);
                        }
                        typeof(BubbleHistory).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(history, 0);
                        history.ContextMenuStrip.Show(history, new Point(10, 10)); Application.DoEvents();
                        Check(!history.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "取消发送").Visible && !history.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "重新发送（可能重复）").Visible, "取消后菜单仍提供发送操作");
                        history.ContextMenuStrip.Close(); host.Close();
                    }
                }
                Theme.Configure(false, "blue");
            });
            Run("气泡选字复制：正文拖选中文/英文/URL/表情/制表符/空行、右键菜单、快捷键、重排和主题渲染", () => {
                string clipboard = SaveClipboard();
                try
                {
                    Action<BubbleHistory, string, MouseEventArgs> mouse = (target, name, ev) => typeof(BubbleHistory).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { ev });
                    foreach (bool dark in new[] { false, true })
                    {
                        Theme.Configure(dark, "blue");
                        using (Form host = new Form { ClientSize = new Size(760, 520) })
                        using (BubbleHistory history = new BubbleHistory { Dock = DockStyle.Fill })
                        {
                            host.Controls.Add(history); host.Show(); history.SetAvatars("peer", "builtin:1", "own", "builtin:2");
                            history.DeleteRequested += delegate { };
                            ChatRecord[] records = {
                                new ChatRecord { Packet = 1, Outgoing = false, Text = "中文拖选 English\n\nhttps://example.com/path\t😀结束", Time = DateTime.Now },
                                new ChatRecord { Packet = 2, Outgoing = true, Text = "发出气泡直接选字 ABC123", State = "已送达", Time = DateTime.Now }
                            };
                            history.SetRecords(records); Application.DoEvents(); history.Refresh();
                            var bounds = (List<Rectangle>)Field(history, "bounds");
                            var items = (System.Collections.IList)Field(history, "items");
                            int padX = (int)typeof(BubbleHistory).GetProperty("PadX", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(history, null);
                            int padTop = (int)typeof(BubbleHistory).GetProperty("PadTop", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(history, null);
                            MessageLayout firstLayout = (MessageLayout)Field(Field(items[0], "M"), "Layout");
                            int top0 = bounds[0].Y + history.AutoScrollPosition.Y;
                            Point origin0 = new Point(bounds[0].X + padX, top0 + padTop);
                            // 鼠标移到正文上显示工字光标，移到链接上保持手形光标。
                            Point plainHover = new Point(origin0.X + 8, origin0.Y + firstLayout.LineHeight / 2);
                            mouse(history, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, plainHover.X, plainHover.Y, 0));
                            Check(history.Cursor == Cursors.IBeam, "正文悬停未显示工字光标");
                            var links = (List<Tuple<Rectangle, string>>)Field(history, "linkHits");
                            Point linkPoint = new Point(links[0].Item1.X + 10, links[0].Item1.Y + links[0].Item1.Height / 2);
                            mouse(history, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, linkPoint.X, linkPoint.Y, 0));
                            Check(history.Cursor == Cursors.Hand, "链接悬停未保持手形光标");

                            // 跨空行、URL、制表符和表情拖选，验证精确切片且保留原始制表符和完整代理对。
                            Point dragEnd = new Point(origin0.X + firstLayout.Width + 10, origin0.Y + firstLayout.Height - firstLayout.LineHeight / 2);
                            mouse(history, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, origin0.X, origin0.Y + 4, 0));
                            mouse(history, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, dragEnd.X, dragEnd.Y, 0));
                            mouse(history, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, dragEnd.X, dragEnd.Y, 0));
                            Check(history.HasTextSelection && history.SelectedTextValue == records[0].Text, "跨行拖选正文不完整：" + history.SelectedTextValue);

                            // 从链接内部起拖只选字，不触发打开链接。
                            Point linkEnd = new Point(links[0].Item1.Right - 4, linkPoint.Y);
                            mouse(history, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, linkPoint.X, linkPoint.Y, 0));
                            mouse(history, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, linkEnd.X, linkEnd.Y, 0));
                            Check(Field(history, "pressedAction") == null, "在链接上拖选没有取消链接点击");
                            mouse(history, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, linkEnd.X, linkEnd.Y, 0));
                            Check(history.HasTextSelection && history.SelectedTextValue.Length > 0 && "https://example.com/path".Contains(history.SelectedTextValue), "链接局部拖选结果错误：" + history.SelectedTextValue);

                            // 发出气泡反向从右向左拖选部分文字，验证 Ctrl+C、右键菜单与缩放重排保持选区。
                            MessageLayout secondLayout = (MessageLayout)Field(Field(items[1], "M"), "Layout");
                            int top1 = bounds[1].Y + history.AutoScrollPosition.Y;
                            Point origin1 = new Point(bounds[1].X + padX, top1 + padTop);
                            Point rightPt = new Point(origin1.X + secondLayout.Width, origin1.Y + secondLayout.LineHeight / 2);
                            Point midPt = new Point(origin1.X + secondLayout.Width / 2, origin1.Y + secondLayout.LineHeight / 2);
                            mouse(history, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, rightPt.X, rightPt.Y, 0));
                            mouse(history, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, midPt.X, midPt.Y, 0));
                            mouse(history, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, midPt.X, midPt.Y, 0));
                            string picked = history.SelectedTextValue;
                            Check(history.HasTextSelection && picked.Length > 0 && picked.Length < records[1].Text.Length && records[1].Text.EndsWith(picked), "反向局部拖选失败：" + picked);
                            using (Bitmap image = Render(history)) image.Save(Path.Combine(output, "inline-text-selection-" + (dark ? "dark" : "light") + ".png"), ImageFormat.Png);

                            // 同一气泡右键保留选区并提供「复制所选文字」与「复制整条消息」。
                            mouse(history, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, midPt.X, midPt.Y, 0));
                            Check(history.HasTextSelection && history.SelectedTextValue == picked, "同一气泡右键清除了文字选区");
                            history.ContextMenuStrip.Show(history, midPt); Application.DoEvents();
                            ToolStripItem copyPicked = history.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "复制所选文字");
                            ToolStripItem copyWhole = history.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "复制整条消息");
                            Check(copyPicked.Visible && copyPicked.Enabled && copyWhole.Visible && copyWhole.Enabled, "右键菜单缺少选段/整条复制项");
                            copyPicked.PerformClick(); history.ContextMenuStrip.Close();
                            Check(Clipboard.GetText() == picked, "右键复制所选文字与选区不一致");

                            // 缩放窗口后选区仍指向同一段文字，Ctrl+C 仍复制选段。
                            Clipboard.SetText("placeholder");
                            host.ClientSize = new Size(460, 520); Application.DoEvents();
                            Check(history.HasTextSelection && history.SelectedTextValue == picked, "缩放后丢失或偏移了文字选区");
                            typeof(BubbleHistory).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(history, new object[] { new KeyEventArgs(Keys.Control | Keys.C) });
                            Check(Clipboard.GetText() == picked, "Ctrl+C 没有复制所选文字");

                            // Esc 清除文字选区后，右键恢复「复制消息」，Ctrl+C 复制整条。
                            typeof(BubbleHistory).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(history, new object[] { new KeyEventArgs(Keys.Escape) });
                            Check(!history.HasTextSelection, "Esc 未清除文字选区");
                            typeof(BubbleHistory).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(history, new object[] { new KeyEventArgs(Keys.Control | Keys.C) });
                            Check(Clipboard.GetText() == records[1].Text, "清除选区后 Ctrl+C 未恢复复制整条消息");
                            history.ContextMenuStrip.Show(history, midPt); Application.DoEvents();
                            Check(history.ContextMenuStrip.Items.Cast<ToolStripItem>().Any(item => item.Text == "复制消息") && !copyWhole.Visible, "清除选区后菜单未恢复");
                            history.ContextMenuStrip.Close(); host.Close();
                        }
                    }
                }
                finally { Theme.Configure(false, "blue"); RestoreClipboard(clipboard); }
            });
            Run("联系人悬停提示：只显示联系人名称，不展示长最近消息", () => {
                using (Form host = new Form { ClientSize = new Size(300, 260) })
                using (ConversationList list = new ConversationList { Dock = DockStyle.Fill })
                {
                    host.Controls.Add(list); host.Show();
                    Peer first = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, 24495), Login = "hover1", Nickname = "好友甲" };
                    Peer second = new Peer { Endpoint = new IPEndPoint(IPAddress.Loopback, 24496), Login = "hover2", Nickname = "phone", DuplicateName = true, ConversationNumber = 3 };
                    string longPreview = "你：" + new string('长', 180) + "尾段消息";
                    list.SetItems(new[] {
                        new ConversationList.Item { Peer = first, Preview = longPreview, Time = DateTime.Now },
                        new ConversationList.Item { Peer = second, Preview = "https://example.com/very/long/url", Time = DateTime.Now }
                    });
                    Application.DoEvents();
                    ToolTip hint = (ToolTip)Field(list, "rowHint");
                    int rowHeight = (int)typeof(ConversationList).GetProperty("RowHeight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(list, null);
                    var move = typeof(ConversationList).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic);
                    var leave = typeof(ConversationList).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic);
                    move.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.None, 0, 40, rowHeight / 2, 0) });
                    string tip0 = hint.GetToolTip(list);
                    Check(tip0 == first.DisplayName && !tip0.Contains("尾段消息") && !tip0.Contains("\n"), "联系人悬停提示仍包含最近消息：" + tip0);
                    move.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.None, 0, 40, rowHeight + rowHeight / 2, 0) });
                    string tip1 = hint.GetToolTip(list);
                    Check(tip1 == "phone · #3" && !tip1.Contains("https://"), "同名编号联系人悬停提示错误：" + tip1);
                    move.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.None, 0, 40, rowHeight * 3, 0) });
                    Check(String.IsNullOrEmpty(hint.GetToolTip(list)), "移入列表空白处未清除提示");
                    move.Invoke(list, new object[] { new MouseEventArgs(MouseButtons.None, 0, 40, rowHeight / 2, 0) });
                    leave.Invoke(list, new object[] { EventArgs.Empty });
                    Check(String.IsNullOrEmpty(hint.GetToolTip(list)), "移出联系人列表未清除提示");
                    host.Close();
                }
            });
            Run("内置表情面板：单页展示全部表情零子控件、小屏平滑滚动、键盘导航与明暗主题渲染", () => {
                try
                {
                    foreach (bool dark in new[] { false, true })
                    {
                        Theme.Configure(dark, "blue");
                        string chosen = null;
                        using (EmojiPicker picker = new EmojiPicker(v => chosen = v))
                        {
                            picker.Prepare(new Size(1280, 720), 96);
                            picker.Show(new Point(40, 40)); Application.DoEvents();
                            Control surface = ((ToolStripControlHost)picker.Items[0]).Control;
                            Control canvas = surface.Controls[0];
                            Check(canvas.Controls.Count == 0, "表情画布不应创建子控件句柄");
                            Check(picker.CategoryCount == 7 && picker.ItemCount == 152, "表情分类或数量不完整");
                            Check(picker.AllItemsVisibleWithoutScroll && !picker.NeedsScroll, "常规桌面工作区未能一页显示全部表情");
                            Check(!picker.HasHorizontalOverflow, "单页表情面板存在横向溢出");
                            using (Bitmap image = Render(picker))
                            {
                                int warm = 0;
                                for (int y = 0; y < image.Height; y++)
                                    for (int x = 0; x < image.Width; x++)
                                    {
                                        Color c = image.GetPixel(x, y);
                                        if (c.R > 180 && c.G > 90 && c.G < 220 && c.B < 100) warm++;
                                    }
                                Check(warm > 200, "单页表情面板未绘制彩色表情像素");
                                image.Save(Path.Combine(output, "emoji-picker-" + (dark ? "dark" : "light") + ".png"), ImageFormat.Png);
                            }
                            Check(picker.PickItemByText("😀") && chosen == "😀", "点击表情未回传正确字符");
                            Check(picker.PickItemByText(picker.LastItemText) && chosen == picker.LastItemText, "点击颜文字未回传正确字符");
                            // 键盘 Home + 方向键与回车选择。
                            var keyDown = canvas.GetType().GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                            chosen = null;
                            keyDown.Invoke(canvas, new object[] { new KeyEventArgs(Keys.Home) });
                            keyDown.Invoke(canvas, new object[] { new KeyEventArgs(Keys.Right) });
                            keyDown.Invoke(canvas, new object[] { new KeyEventArgs(Keys.Enter) });
                            Check(chosen == "😃", "方向键加回车未选中相邻表情：" + chosen);
                            // 极小工作区回退平滑滚动且不横向溢出。
                            foreach (int dpi in new[] { 96, 144, 192 })
                            {
                                picker.Prepare(new Size(320, 220), dpi);
                                Check(picker.Width <= 320 && picker.Height <= 220 && !picker.HasHorizontalOverflow, "小工作区越界或溢出");
                                Check(picker.NeedsScroll && picker.ScrollToLastAndCheckVisible(), "小工作区未能平滑滚动到底部颜文字");
                            }
                            picker.Close();
                        }
                    }
                }
                finally { Theme.Configure(false, "blue"); }
            });
            Console.WriteLine("CHAT REGRESSION: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
