using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FeiqLight.Tests
{
    internal static class ResizePerformanceTests
    {
        private static int passed, failed;
        private static string output, label;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
        private static IEnumerable<Control> All(Control parent) { yield return parent; foreach(Control child in parent.Controls) foreach(Control nested in All(child)) yield return nested; }
        private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
        private static void Run(string name,Action action) { try {action();passed++;Console.WriteLine("PASS "+name);} catch(Exception error) {failed++;Console.WriteLine("FAIL "+name+": "+error);} }
        private static void Pump(int milliseconds) { Stopwatch watch=Stopwatch.StartNew(); do {Application.DoEvents();Thread.Sleep(5);}while(watch.ElapsedMilliseconds<milliseconds); }
        private static void Render(Control control,string name) { using(Bitmap image=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,label+"-"+name+".png"),ImageFormat.Png);} }
        private sealed class PaintProbe : NativeWindow,IDisposable
        {
            internal readonly string Name;
            internal double Milliseconds;
            internal PaintProbe(Control control) {Name=control.GetType().Name+":"+control.AccessibleName;AssignHandle(control.Handle);}
            protected override void WndProc(ref Message message) {
                if(message.Msg!=0x000F){base.WndProc(ref message);return;}
                Stopwatch watch=Stopwatch.StartNew();base.WndProc(ref message);Milliseconds+=watch.Elapsed.TotalMilliseconds;
            }
            public void Dispose(){ReleaseHandle();}
        }
        private sealed class SyntheticDragFilter : IMessageFilter
        {
            // SendMessage 注入的拖动使用固定坐标；泵消息时忽略真实桌面指针产生的额外移动。
            public bool PreFilterMessage(ref Message message) { return message.Msg==0x0200; }
        }
        private static ChatRecord[] Records(int count)
        {
            return Enumerable.Range(0,count).Select(index=>new ChatRecord {PeerId="resize",Packet=index+1,Outgoing=index%2==0,Sender="测试",Time=new DateTime(2026,11,17,9,0,0).AddMinutes(index),State="已送达",Text=index+" 中文 English 对话内容 😀 https://example.test/messages/"+index+"?resize=continuous\n边框连续拖动时正文重新换行，保留原来的阅读位置和输入草稿。"}).ToArray();
        }
        private static void Measure(ModernWindow window,string name)
        {
            window.Show();Pump(100);window.Refresh();
            int paints=0,resizes=0;BubbleHistory history=All(window).OfType<BubbleHistory>().FirstOrDefault();
            if(history!=null)history.SizeChanged+=delegate{resizes++;};
            foreach(Control control in All(window))control.Paint+=delegate{paints++;};
            List<double> layout=new List<double>(),paint=new List<double>();Size original=window.Size;
            PaintProbe[] probes=All(window).Where(control=>control.Visible&&control.IsHandleCreated).Select(control=>new PaintProbe(control)).ToArray();
            for(int index=0;index<12;index++) {
                Stopwatch clock=Stopwatch.StartNew();window.Size=new Size(original.Width-12-index*8,original.Height-3-index*3);layout.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();ModernWindow.RedrawLayout(window);paint.Add(clock.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("METRIC "+name+" layout_avg_ms="+layout.Average().ToString("F2")+" paint_avg_ms="+paint.Average().ToString("F2")+" max_frame_ms="+layout.Zip(paint,(first,second)=>first+second).Max().ToString("F2")+" history_resizes="+resizes+" paints="+paints);
            foreach(PaintProbe probe in probes.OrderByDescending(value=>value.Milliseconds).Take(4))Console.WriteLine("PAINT "+name+" "+probe.Name+" avg_ms="+(probe.Milliseconds/12).ToString("F2"));
            foreach(PaintProbe probe in probes)probe.Dispose();
            Render(window,name);
        }
        private static void Mouse(Control control,int message,Point screen)
        {
            Point point=control.PointToClient(screen);SendMessage(control.Handle,message,new IntPtr(message==0x202?0:1),new IntPtr(unchecked((point.Y<<16)|(point.X&65535))));
        }
        // 保留原逐字算法作对照，核验优化后的换行、组合字符、链接和高亮位置。
        private static List<MessageLayout.Run> Reference(string text,Font font,int maxWidth,string query,out Size size)
        {
            const TextFormatFlags flags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
            bool[] highlighted=new bool[text.Length];
            if(!String.IsNullOrEmpty(query))for(int from=0;from<text.Length;) {
                int match=text.IndexOf(query,from,StringComparison.OrdinalIgnoreCase);if(match<0)break;
                for(int index=match;index<match+query.Length;index++)highlighted[index]=true;from=match+query.Length;
            }
            int line=TextRenderer.MeasureText("国Ag",font,Size.Empty,flags).Height+2,x=0,y=0,width=0,linkIndex=0;
            List<MessageLinks.Link> links=MessageLinks.Find(text);List<MessageLayout.Run> runs=new List<MessageLayout.Run>();MessageLayout.Run run=null;
            for(int offset=0;offset<text.Length;) {
                int start=offset;string part;bool emoji=ColorEmoji.Match(text,offset,out part);
                if(!emoji){part=StringInfo.GetNextTextElement(text,offset);int next=offset+part.Length;while(next<text.Length&&text[next]=='\u200d'&&next+1<text.Length){string joined=StringInfo.GetNextTextElement(text,next+1);part+="\u200d"+joined;next+=1+joined.Length;}}
                offset+=part.Length;if(part=="\r")continue;if(part=="\n"||part=="\r\n"){x=0;y+=line;run=null;continue;}if(part=="\t")part="    ";
                while(linkIndex<links.Count&&start>=links[linkIndex].Start+links[linkIndex].Length)linkIndex++;
                string url=linkIndex<links.Count&&start>=links[linkIndex].Start?links[linkIndex].Url:null;bool highlight=highlighted[start];
                if(emoji&&url==null){if(x+line>maxWidth&&x>0){x=0;y+=line;}runs.Add(new MessageLayout.Run {Text=part,Emoji=true,Highlight=highlight,Bounds=new Rectangle(x,y,line,line)});x+=line;width=Math.Max(width,x);run=null;continue;}
                if(run==null||run.Url!=url||run.Highlight!=highlight){run=new MessageLayout.Run {Text="",Url=url,Highlight=highlight,Bounds=new Rectangle(x,y,0,line)};runs.Add(run);}
                int measured=TextRenderer.MeasureText(run.Text+part,font,Size.Empty,flags).Width;
                if(run.Bounds.X+measured>maxWidth&&x>0){x=0;y+=line;if(run.Text.Length>0){run=new MessageLayout.Run {Text="",Url=url,Highlight=highlight,Bounds=new Rectangle(0,y,0,line)};runs.Add(run);}else run.Bounds=new Rectangle(0,y,0,line);measured=TextRenderer.MeasureText(part,font,Size.Empty,flags).Width;}
                run.Text+=part;run.Bounds=new Rectangle(run.Bounds.X,y,measured,line);x=run.Bounds.Right;width=Math.Max(width,x);
            }
            size=new Size(width,y+line);return runs;
        }
        private static void CheckLayout(string text,string query)
        {
            MessageLayout cached=new MessageLayout(text,Theme.Body,400,query);
            foreach(int width in new[]{45,120,400,119,121,400}) {
                MessageLayout actual=cached.Reflow(width);Size expectedSize;List<MessageLayout.Run> expected=Reference(text,Theme.Body,width,query,out expectedSize);
                Check(expectedSize==new Size(actual.Width,actual.Height),"重排尺寸不同："+width+" "+text);
                Check(expected.Count==actual.Runs.Count,"绘制段数不同："+width+" "+text);
                for(int index=0;index<expected.Count;index++) {MessageLayout.Run first=expected[index],second=actual.Runs[index];Check(first.Text==second.Text&&first.Url==second.Url&&first.Bounds==second.Bounds&&first.Emoji==second.Emoji&&first.Highlight==second.Highlight,"换行或样式位置不同："+width+"/"+index+" "+text);}
                cached=actual;
            }
        }
        [STAThread] public static int Main(string[] args)
        {
            string root=Path.GetFullPath(args[0]);output=Path.GetFullPath(args[1]);label=args[2];
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Theme.Configure(false,"rose");
            LocalStore store=new LocalStore(root);Peer peer=new Peer {Endpoint=new IPEndPoint(IPAddress.Loopback,24879),Login="resize",Nickname="中文 English 缩放测试"};
            using(LanService node=new LanService(new AppSettings {Notifications=false,CloseToTray=false,AutoReceiveFiles=false,ChatBackground="orbits"},24878,true,"resize-performance"))using(MainForm main=new MainForm(node,store)) {
                main.Show();main.OpenChat(peer);main.Size=new Size(1020,720);
                BubbleHistory history=All(main.ActiveChat).OfType<BubbleHistory>().Single();history.SetRecords(Records(150));
                ChatEditor editor=All(main.ActiveChat).OfType<ChatEditor>().Single();editor.Text="第一行草稿\nSecond line English";
                Measure(main,"chat-150");main.ExitApplication();
            }
            using(HistoryDialog dialog=new HistoryDialog(store,peer)) {
                dialog.Show();All(dialog).OfType<BubbleHistory>().Single().SetRecords(Records(500),"中文");Measure(dialog,"history-500");dialog.Close();
            }
            using(MessageTextDialog dialog=new MessageTextDialog(String.Join("\n",Records(150).Select(record=>record.Text)))){Measure(dialog,"full-text");dialog.Close();}
            if(args.Length>3&&args[3]=="measure")return 0;
            Run("缓存重排与逐字参考算法一致",()=>{
                foreach(string text in new[]{"", "中文 English 😀 https://example.test/long-path?x=12&size=400\n第二行", "xx ABCabc 😊\nabc", "组合 e\u0301 👨‍👩‍👧‍👦 🇨🇳 中文\r\n末尾\n", "汉字\tEnglish\t emoji 😄", "العربية हिन्दी ไทย AV fi office 12345",new string('长',220)+" 😀", "https://example.test/😀a/longlink 中文 https://second.test/abc"}) CheckLayout(text,"abc");
            });
            Run("重排性能：同批正文、同组宽度对比",()=>{
                ChatRecord[] sample=Records(40);MessageLayout[] cached=sample.Select(record=>new MessageLayout(record.Text,Theme.Body,440,"中文")).ToArray();
                Stopwatch watch=Stopwatch.StartNew();
                foreach(int width in new[]{430,422,414,406,398,390})for(int index=0;index<sample.Length;index++){Size size;Reference(sample[index].Text,Theme.Body,width,"中文",out size);}
                double original=watch.Elapsed.TotalMilliseconds;watch.Restart();
                foreach(int width in new[]{430,422,414,406,398,390})for(int index=0;index<sample.Length;index++)cached[index]=cached[index].Reflow(width);
                Console.WriteLine("METRIC same_text_reflow reference_ms="+original.ToString("F2")+" optimized_ms="+watch.Elapsed.TotalMilliseconds.ToString("F2"));
            });
            Run("连续拖动合并布局，松手尺寸精确落位",()=>{
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(680,480)}) {
                    window.Show();Pump(20);Rectangle initial=window.Bounds;int resizeCount=0;window.SizeChanged+=delegate{resizeCount++;};
                    Point start=window.PointToScreen(new Point(window.Width-2,window.Height-2));Mouse(window,0x201,start);
                    for(int index=1;index<=120;index++)Mouse(window,0x200,new Point(start.X+index,start.Y+index/2));
                    Mouse(window,0x202,new Point(start.X+120,start.Y+60));
                    Check(window.Size==new Size(initial.Width+120,initial.Height+60),"松手后尺寸未精确落位");
                    Check(resizeCount<60,"120 次鼠标移动仍逐次执行布局："+resizeCount);window.Close();
                }
            });
            Run("停住鼠标补齐最后一帧，失去捕获与隐藏取消延迟缩放",()=>{
                var filter=new SyntheticDragFilter();Application.AddMessageFilter(filter);
                try {
                using(ModernWindow window=new ModernWindow {ClientSize=new Size(680,480)}) {
                    window.Show();Pump(30);Point start=window.PointToScreen(new Point(window.Width-2,window.Height-2));Size original=window.Size;
                    Mouse(window,0x201,start);Mouse(window,0x200,new Point(start.X+4,start.Y+2));Mouse(window,0x200,new Point(start.X+20,start.Y+10));
                    Pump(100);Check(window.Size==new Size(original.Width+20,original.Height+10),"鼠标停住时最后一帧未提交："+window.Size+" capture="+window.Capture);
                    Mouse(window,0x200,new Point(start.X+30,start.Y+15));window.Capture=false;Size cancelled=window.Size;Pump(80);Check(window.Size==cancelled&&!ModernWindow.IsResizing(window),"丢失捕获后仍有延迟缩放");
                    start=window.PointToScreen(new Point(window.Width-2,window.Height-2));Mouse(window,0x201,start);Mouse(window,0x200,new Point(start.X+8,start.Y+4));window.Hide();cancelled=window.Size;Pump(80);Check(window.Size==cancelled,"隐藏后仍缩放");window.Close();
                }
                } finally {Application.RemoveMessageFilter(filter);}
            });
            Run("聊天缩放保留阅读锚点、草稿和清晰终态背景",()=>{
                foreach(bool dark in new[]{false,true}) {
                    Theme.Configure(dark,"rose");LocalStore data=new LocalStore(Path.Combine(root,"reading-"+dark));
                    using(LanService node=new LanService(new AppSettings {Notifications=false,CloseToTray=false,ChatBackground="orbits"},24878,true,"reading"))using(MainForm main=new MainForm(node,data)) {
                        main.Show();main.OpenChat(peer);BubbleHistory history=All(main.ActiveChat).OfType<BubbleHistory>().Single();history.SetRecords(Records(150));Pump(40);
                        ChatEditor editor=All(main.ActiveChat).OfType<ChatEditor>().Single();editor.Text="缩放保留的草稿\nSecond line";
                        history.AutoScrollPosition=new Point(0,history.AutoScrollMinSize.Height/2);Pump(30);
                        MethodInfo anchorMethod=typeof(BubbleHistory).GetMethod("TopAnchor",BindingFlags.Instance|BindingFlags.NonPublic);
                        var before=(Tuple<ChatRecord,int>)anchorMethod.Invoke(history,null);
                        Point start=main.PointToScreen(new Point(main.Width-2,main.Height-2));Mouse(main,0x201,start);
                        for(int index=1;index<=8;index++){Mouse(main,0x200,new Point(start.X-index*16,start.Y-index*6));Pump(20);}
                        Mouse(main,0x202,new Point(start.X-128,start.Y-48));Pump(40);
                        var after=(Tuple<ChatRecord,int>)anchorMethod.Invoke(history,null);
                        Check(before.Item1==after.Item1&&Math.Abs(before.Item2-after.Item2)<=1,"缩放改变了正在阅读的消息位置");Check(editor.Text=="缩放保留的草稿\nSecond line","缩放改动草稿");Check(!ModernWindow.IsResizing(history),"松手后仍使用临时背景");
                        Render(main,"drag-final-"+dark);main.WindowState=FormWindowState.Maximized;Pump(30);main.WindowState=FormWindowState.Normal;Pump(30);main.ExitApplication();
                    }
                }
            });
            Console.WriteLine("RESIZE PERFORMANCE: "+passed+" passed, "+failed+" failed");return failed==0?0:1;
        }
    }
}
