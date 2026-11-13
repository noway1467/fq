using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FeiqLight
{
    // 仅本地渲染；发送和复制始终保留 Unicode，不把表情转换成附件。
    internal static class ColorEmoji
    {
        private static readonly Bitmap atlas;
        private static readonly Dictionary<string,int> indices=new Dictionary<string,int>(StringComparer.Ordinal);
        private static readonly string[] longest;
        static ColorEmoji()
        {
            var assembly=typeof(ColorEmoji).Assembly;
            using(Stream stream=assembly.GetManifestResourceStream("FeiqLight.EmojiAtlas.png"))
            using(Bitmap source=new Bitmap(stream)) atlas=new Bitmap(source);
            using(Stream stream=assembly.GetManifestResourceStream("FeiqLight.EmojiAtlas.txt"))
            using(StreamReader reader=new StreamReader(stream)) {string line;while((line=reader.ReadLine())!=null)if(line.Length>0)indices[line]=indices.Count;}
            longest=indices.Keys.OrderByDescending(s=>s.Length).ToArray();
        }
        internal static bool Contains(string text) {return longest.Any(s=>(text??"").Contains(s));}
        internal static bool Match(string text,int offset,out string emoji)
        {
            foreach(string value in longest) if(offset+value.Length<=text.Length&&String.CompareOrdinal(text,offset,value,0,value.Length)==0) {
                int end=offset+value.Length;
                // 未收录的肤色/连字序列整体退回系统字体，不把一个组合拆成彩色碎片。
                if(end<text.Length&&(text[end]=='\u200d'||text[end]=='\ufe0f'||(Char.IsSurrogatePair(text,end)&&Char.ConvertToUtf32(text,end)>=0x1f3fb&&Char.ConvertToUtf32(text,end)<=0x1f3ff)))continue;
                emoji=value;return true;
            }
            emoji=null;return false;
        }
        internal static bool Draw(Graphics graphics,string emoji,Rectangle bounds)
        {
            int index;if(!indices.TryGetValue(emoji,out index))return false;
            graphics.DrawImage(atlas,bounds,new Rectangle(index%12*72,index/12*72,72,72),GraphicsUnit.Pixel);return true;
        }
        internal static int MeasureSingleLine(Graphics graphics,string text,Font font)
        {
            if(String.IsNullOrEmpty(text))return 0;
            const TextFormatFlags plain=TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding;
            if(!Contains(text))return TextRenderer.MeasureText(graphics,text,font,Size.Empty,plain).Width;
            int total=0,pos=0,runStart=0,emojiSize=Math.Max(12,font.Height);
            while(pos<text.Length)
            {
                string emoji;
                if(Match(text,pos,out emoji))
                {
                    if(pos>runStart)total+=TextRenderer.MeasureText(graphics,text.Substring(runStart,pos-runStart),font,Size.Empty,plain).Width;
                    total+=emojiSize+2;pos+=emoji.Length;runStart=pos;continue;
                }
                pos+=Char.IsSurrogatePair(text,pos)?2:1;
            }
            if(pos>runStart)total+=TextRenderer.MeasureText(graphics,text.Substring(runStart,pos-runStart),font,Size.Empty,plain).Width;
            return total;
        }
        internal static void DrawSingleLine(Graphics graphics,string text,Font font,Rectangle bounds,Color color,TextFormatFlags flags)
        {
            if(String.IsNullOrEmpty(text)||bounds.Width<=0||bounds.Height<=0)return;
            if(!Contains(text)){TextRenderer.DrawText(graphics,text,font,bounds,color,flags);return;}
            const TextFormatFlags plain=TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPadding;
            bool ellipsis=(flags&TextFormatFlags.EndEllipsis)!=0;
            int emojiSize=Math.Max(12,Math.Min(bounds.Height,font.Height)),x=bounds.X,right=bounds.Right;
            int ellipsisWidth=ellipsis?TextRenderer.MeasureText(graphics,"…",font,Size.Empty,plain).Width:0;
            int limit=ellipsis?Math.Max(bounds.X,right-ellipsisWidth):right;
            var prevInterp=graphics.InterpolationMode;
            graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            try
            {
                int pos=0,runStart=0;
                while(pos<text.Length&&x<right)
                {
                    string emoji;
                    if(Match(text,pos,out emoji))
                    {
                        if(pos>runStart)
                        {
                            string chunk=text.Substring(runStart,pos-runStart);
                            int w=TextRenderer.MeasureText(graphics,chunk,font,Size.Empty,plain).Width;
                            if(ellipsis&&x+w>limit&&pos<text.Length)
                            {
                                TextRenderer.DrawText(graphics,chunk,font,new Rectangle(x,bounds.Y,Math.Max(1,right-x),bounds.Height),color,plain|TextFormatFlags.EndEllipsis);
                                return;
                            }
                            TextRenderer.DrawText(graphics,chunk,font,new Rectangle(x,bounds.Y,Math.Max(1,Math.Min(w+4,right-x)),bounds.Height),color,plain);
                            x+=w;
                        }
                        if(x+emojiSize>(ellipsis?limit:right))
                        {
                            if(ellipsis)TextRenderer.DrawText(graphics,"…",font,new Rectangle(x,bounds.Y,Math.Max(1,right-x),bounds.Height),color,plain);
                            return;
                        }
                        Draw(graphics,emoji,new Rectangle(x,bounds.Y+(bounds.Height-emojiSize)/2,emojiSize,emojiSize));
                        x+=emojiSize+2;pos+=emoji.Length;runStart=pos;continue;
                    }
                    pos+=Char.IsSurrogatePair(text,pos)?2:1;
                }
                if(pos>runStart&&x<right)
                    TextRenderer.DrawText(graphics,text.Substring(runStart,pos-runStart),font,new Rectangle(x,bounds.Y,Math.Max(1,right-x),bounds.Height),color,ellipsis?(plain|TextFormatFlags.EndEllipsis):plain);
            }
            finally{graphics.InterpolationMode=prevInterp;}
        }
        internal sealed class EmojiButton : Button
        {
            private bool hovering;
            public EmojiButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            }
            protected override void OnMouseEnter(EventArgs e){hovering=true;Invalidate();base.OnMouseEnter(e);}
            protected override void OnMouseLeave(EventArgs e){hovering=false;Invalidate();base.OnMouseLeave(e);}
            protected override void OnPaintBackground(PaintEventArgs pevent) { }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(hovering?Theme.Sky:Theme.Surface);
                if(!indices.ContainsKey(Text))
                    TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
                else {
                    int size=Math.Min(Width,Height)-8;
                    Draw(e.Graphics,Text,new Rectangle((Width-size)/2,(Height-size)/2,size,size));
                }
                if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle);
            }
        }
    }
}
