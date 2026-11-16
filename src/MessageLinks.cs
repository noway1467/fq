using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FeiqLight
{
    // 只识别网页协议，绝不把聊天中的路径或自定义协议交给 Shell。
    public static class MessageLinks
    {
        public sealed class Link { public int Start, Length; public string Url; }
        private static readonly Regex Pattern=new Regex(@"(?:https?://|www\.)[^\s<>""，。！？；：、]+",RegexOptions.IgnoreCase);
        public static List<Link> Find(string text)
        {
            List<Link> links=new List<Link>();
            foreach(Match m in Pattern.Matches(text??"")) {
                string value=m.Value.TrimEnd('.',',','!','?',';',':','\'','"');
                while(value.EndsWith(")") && value.Split(')').Length>value.Split('(').Length)value=value.Substring(0,value.Length-1);
                string url=value.StartsWith("www.",StringComparison.OrdinalIgnoreCase)?"https://"+value:value;Uri uri;
                if(Uri.TryCreate(url,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https")&&!String.IsNullOrEmpty(uri.Host)&&String.IsNullOrEmpty(uri.UserInfo))
                    links.Add(new Link {Start=m.Index,Length=value.Length,Url=uri.AbsoluteUri});
            }
            return links;
        }
        public static void Open(Control owner,string url)
        {
            Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https")||String.IsNullOrEmpty(uri.Host))return;
            try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
            catch(Exception e) {using(NoticeDialog dialog=new NoticeDialog("无法打开浏览器",e.Message))dialog.ShowDialog(owner);}
        }
    }
    internal sealed class MessageLayout
    {
        internal sealed class Run { public string Text, Url; public Rectangle Bounds; public bool Emoji, Highlight; }
        internal readonly List<Run> Runs=new List<Run>();
        internal int Height, Width, LineHeight;
        // 最后一行已占用的宽度；以换行结尾时为 0，用于判断时间能否与末行并排。
        internal int LastLineRight { get { int top=Height-LineHeight,right=0;foreach(Run run in Runs)if(run.Bounds.Y==top)right=Math.Max(right,run.Bounds.Right);return right; } }
        private const TextFormatFlags Flags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
        private sealed class Segment
        {
            internal string Text = "", Url;
            internal bool Emoji, Highlight, NewLine;
            internal readonly List<int> Ends = new List<int>();
            private readonly Dictionary<long,int> widths = new Dictionary<long,int>();
            internal int Offset(int element) { return element == 0 ? 0 : Ends[element - 1]; }
            internal int Measure(Font font,int start,int end)
            {
                long key = ((long)start << 32) | (uint)end;
                int width;
                if (widths.TryGetValue(key,out width)) return width;
                width = TextRenderer.MeasureText(Text.Substring(Offset(start),Offset(end)-Offset(start)),font,Size.Empty,Flags).Width;
                // 只缓存当前消息的有限测量片段，连续改变宽度不会让缓存无限增长。
                if (widths.Count >= 512) widths.Clear();
                widths[key] = width; return width;
            }
            internal int Fit(Font font,int start,int available)
            {
                int low=start,lowWidth=0,high=Ends.Count,highWidth=Measure(font,start,Ends.Count);
                if(highWidth<=available)return high;
                // 相邻窗口宽度通常只相差几像素，先用已有的测量结果缩小查找区间。
                foreach(KeyValuePair<long,int> measured in widths) {
                    if((int)(measured.Key>>32)!=start)continue;
                    int end=(int)(measured.Key&UInt32.MaxValue);
                    if(measured.Value<=available&&end>low){low=end;lowWidth=measured.Value;}
                    else if(measured.Value>available&&end<high){high=end;highWidth=measured.Value;}
                }
                while(high-low>1) {
                    int estimate=low+Math.Max(1,Math.Min(high-low-1,(int)((long)(available-lowWidth)*(high-low)/Math.Max(1,highWidth-lowWidth))));
                    int width=Measure(font,start,estimate);
                    if(width<=available){low=estimate;lowWidth=width;}else{high=estimate;highWidth=width;}
                }
                return low;
            }
        }
        private readonly List<Segment> segments;
        private readonly Font font;
        internal MessageLayout(string text,Font font,int maxWidth,string query = null)
        {
            this.font = font; segments = new List<Segment>();
            text=text??"";
            bool[] highlighted=new bool[text.Length];
            if(!String.IsNullOrEmpty(query))
                for(int from=0;from<text.Length;) {
                    int match=text.IndexOf(query,from,StringComparison.OrdinalIgnoreCase);if(match<0)break;
                    for(int n=match;n<match+query.Length;n++)highlighted[n]=true;
                    from=match+query.Length;
                }
            LineHeight=TextRenderer.MeasureText("国Ag",font,Size.Empty,Flags).Height+2;
            List<MessageLinks.Link> links=MessageLinks.Find(text);int linkIndex=0;
            Segment segment=null;
            for(int offset=0;offset<text.Length;) {
                int start=offset;string part;bool emoji=ColorEmoji.Match(text,offset,out part);
                if(!emoji) {part=StringInfo.GetNextTextElement(text,offset);int next=offset+part.Length;while(next<text.Length&&text[next]=='\u200d'&&next+1<text.Length){string joined=StringInfo.GetNextTextElement(text,next+1);part+="\u200d"+joined;next+=1+joined.Length;}}
                offset+=part.Length;
                if(part=="\r")continue;
                if(part=="\n"||part=="\r\n"){segments.Add(new Segment {NewLine=true});segment=null;continue;}
                if(part=="\t")part="    ";
                while(linkIndex<links.Count&&start>=links[linkIndex].Start+links[linkIndex].Length)linkIndex++;
                string url=linkIndex<links.Count&&start>=links[linkIndex].Start?links[linkIndex].Url:null;
                bool highlight=highlighted[start];
                if(emoji&&url==null){segments.Add(new Segment {Text=part,Emoji=true,Highlight=highlight});segment=null;continue;}
                if(segment==null||segment.Url!=url||segment.Highlight!=highlight){segment=new Segment {Url=url,Highlight=highlight};segments.Add(segment);}
                segment.Text+=part;segment.Ends.Add(segment.Text.Length);
            }
            Arrange(maxWidth);
        }
        private MessageLayout(MessageLayout previous,int maxWidth)
        {
            font=previous.font;segments=previous.segments;LineHeight=previous.LineHeight;Arrange(maxWidth);
        }
        internal MessageLayout Reflow(int maxWidth) { return new MessageLayout(this,maxWidth); }
        private void Arrange(int maxWidth)
        {
            int x=0,y=0,line=LineHeight;
            foreach(Segment segment in segments)
            {
                if(segment.NewLine){x=0;y+=line;continue;}
                if(segment.Emoji) {
                    if(x+line>maxWidth&&x>0){x=0;y+=line;}
                    Runs.Add(new Run {Text=segment.Text,Emoji=true,Highlight=segment.Highlight,Bounds=new Rectangle(x,y,line,line)});
                    x+=line;Width=Math.Max(Width,x);continue;
                }
                for(int start=0;start<segment.Ends.Count;)
                {
                    int fit=segment.Fit(font,start,maxWidth-x);
                    if(fit==start&&x>0){x=0;y+=line;continue;}
                    int end=Math.Max(start+1,fit),width=segment.Measure(font,start,end);
                    Runs.Add(new Run {Text=segment.Text.Substring(segment.Offset(start),segment.Offset(end)-segment.Offset(start)),Url=segment.Url,Highlight=segment.Highlight,Bounds=new Rectangle(x,y,width,line)});
                    x+=width;Width=Math.Max(Width,x);start=end;
                    if(start<segment.Ends.Count){x=0;y+=line;}
                }
            }
            Height=y+line;
        }
        internal void Draw(Graphics graphics,Point origin,Font font,List<Tuple<Rectangle,string>> hits)
        {
            foreach(Run run in Runs) {
                Rectangle r=run.Bounds;r.Offset(origin);
                if(run.Highlight)using(SolidBrush fill=new SolidBrush(Theme.SearchHighlight))graphics.FillRectangle(fill,r);
                if(run.Emoji){ColorEmoji.Draw(graphics,run.Text,r);continue;}
                TextRenderer.DrawText(graphics,run.Text,font,r,run.Url==null?Theme.Ink:Theme.Link,Flags);
                if(run.Url!=null) {using(Pen pen=new Pen(Theme.Link))graphics.DrawLine(pen,r.Left,r.Bottom-3,r.Right,r.Bottom-3);hits.Add(Tuple.Create(r,run.Url));}
            }
        }
    }
}
