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
        internal sealed class EmojiButton : Button
        {
            private bool hovering;
            protected override void OnMouseEnter(EventArgs e){hovering=true;Invalidate();base.OnMouseEnter(e);}
            protected override void OnMouseLeave(EventArgs e){hovering=false;Invalidate();base.OnMouseLeave(e);}
            protected override void OnPaint(PaintEventArgs e)
            {
                if(!indices.ContainsKey(Text)){base.OnPaint(e);return;}
                e.Graphics.Clear(hovering?Theme.Sky:Theme.Surface);int size=Math.Min(Width,Height)-8;
                Draw(e.Graphics,Text,new Rectangle((Width-size)/2,(Height-size)/2,size,size));
                if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,ClientRectangle);
            }
        }
    }
}
