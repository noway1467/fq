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
        internal sealed class Run { public string Text, Url; public Rectangle Bounds; public bool Emoji; }
        internal readonly List<Run> Runs=new List<Run>();
        internal int Height, Width;
        private const TextFormatFlags Flags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
        internal MessageLayout(string text,Font font,int maxWidth)
        {
            int line=TextRenderer.MeasureText("国Ag",font,Size.Empty,Flags).Height+2,x=0,y=0;
            List<MessageLinks.Link> links=MessageLinks.Find(text);int linkIndex=0;
            // 测量整段而不是逐字累加，保留字距；超长链接按文本元素安全换行。
            Run run=null;
            text=text??"";
            for(int offset=0;offset<text.Length;) {
                int start=offset;string part;bool emoji=ColorEmoji.Match(text,offset,out part);
                if(!emoji) {part=StringInfo.GetNextTextElement(text,offset);int next=offset+part.Length;while(next<text.Length&&text[next]=='\u200d'&&next+1<text.Length){string joined=StringInfo.GetNextTextElement(text,next+1);part+="\u200d"+joined;next+=1+joined.Length;}}
                offset+=part.Length;
                if(part=="\r")continue;
                if(part=="\n"||part=="\r\n"){x=0;y+=line;run=null;continue;}
                if(part=="\t")part="    ";
                while(linkIndex<links.Count&&start>=links[linkIndex].Start+links[linkIndex].Length)linkIndex++;
                string url=linkIndex<links.Count&&start>=links[linkIndex].Start?links[linkIndex].Url:null;
                if(emoji&&url==null){if(x+line>maxWidth&&x>0){x=0;y+=line;}Runs.Add(new Run {Text=part,Emoji=true,Bounds=new Rectangle(x,y,line,line)});x+=line;Width=Math.Max(Width,x);run=null;continue;}
                if(run==null||run.Url!=url){run=new Run {Text="",Url=url,Bounds=new Rectangle(x,y,0,line)};Runs.Add(run);}
                int w=TextRenderer.MeasureText(run.Text+part,font,Size.Empty,Flags).Width;
                if(run.Bounds.X+w>maxWidth&&x>0){
                    x=0;y+=line;run=new Run {Text="",Url=url,Bounds=new Rectangle(0,y,0,line)};Runs.Add(run);
                    w=TextRenderer.MeasureText(part,font,Size.Empty,Flags).Width;
                }
                run.Text+=part;run.Bounds=new Rectangle(run.Bounds.X,y,w,line);x=run.Bounds.Right;Width=Math.Max(Width,x);
            }
            Height=y+line;
        }
        internal void Draw(Graphics graphics,Point origin,Font font,List<Tuple<Rectangle,string>> hits)
        {
            foreach(Run run in Runs) {
                Rectangle r=run.Bounds;r.Offset(origin);
                if(run.Emoji){ColorEmoji.Draw(graphics,run.Text,r);continue;}
                TextRenderer.DrawText(graphics,run.Text,font,r,run.Url==null?Theme.Ink:Theme.Blue,Flags);
                if(run.Url!=null) {using(Pen pen=new Pen(Theme.Blue))graphics.DrawLine(pen,r.Left,r.Bottom-3,r.Right,r.Bottom-3);hits.Add(Tuple.Create(r,run.Url));}
            }
        }
    }
}
