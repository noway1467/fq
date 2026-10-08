using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace FeiqLight
{
    internal static class ChatWallpaper
    {
        internal sealed class Asset { internal Bitmap Image; internal string Key; internal int Users; }
        private static readonly System.Collections.Generic.Dictionary<string,Asset> cache=new System.Collections.Generic.Dictionary<string,Asset>(StringComparer.OrdinalIgnoreCase);
        internal static Asset Acquire(string path)
        {
            FileInfo file=new FileInfo(path);string key=file.FullName+"|"+file.LastWriteTimeUtc.Ticks+"|"+file.Length;Asset asset;
            // 多个已打开会话共享同一张背景；不为每个联系人重复分配十几 MiB 位图。
            if(!cache.TryGetValue(key,out asset)){asset=new Asset {Image=Load(path),Key=key};cache.Add(key,asset);}asset.Users++;return asset;
        }
        internal static void Release(Asset asset)
        {
            if(asset!=null&&--asset.Users==0){cache.Remove(asset.Key);asset.Image.Dispose();}
        }
        internal static Bitmap Load(string path)
        {
            if(String.IsNullOrEmpty(path)||!File.Exists(path))throw new IOException("背景图片不存在，请重新选择。");
            if(new FileInfo(path).Length>20*1024*1024)throw new IOException("请选择不超过 20 MiB 的图片。");
            using(Image image=Image.FromFile(path)) {
                if((long)image.Width*image.Height>40000000)throw new IOException("请选择不超过 4000 万像素的图片。");
                double ratio=Math.Min(1,1920.0/Math.Max(image.Width,image.Height));
                return new Bitmap(image,Math.Max(1,(int)(image.Width*ratio)),Math.Max(1,(int)(image.Height*ratio)));
            }
        }
        internal static void Draw(Graphics g,Rectangle area,string pattern,Image image,float scale)
        {
            if(area.Width<=0||area.Height<=0)return;
            using(SolidBrush background=new SolidBrush(Theme.Wallpaper))g.FillRectangle(background,area);
            if(pattern=="image"&&image!=null) {
                double ratio=Math.Max((double)area.Width/image.Width,(double)area.Height/image.Height);
                int w=(int)(image.Width*ratio),h=(int)(image.Height*ratio);
                g.DrawImage(image,new Rectangle(area.X+(area.Width-w)/2,area.Y+(area.Height-h)/2,w,h));
                using(SolidBrush veil=new SolidBrush(Color.FromArgb(65,Theme.Wallpaper)))g.FillRectangle(veil,area);
                return;
            }
            if(pattern=="none")return;
            using(LinearGradientBrush wash=new LinearGradientBrush(area,Theme.Wallpaper,Theme.Blend(Theme.Blue,Theme.Wallpaper,.10F),55F))g.FillRectangle(wash,area);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            using(Pen pen=new Pen(Theme.Blend(Theme.Blue,Theme.Wallpaper,.14F),Math.Max(1,scale))) {
                int step=Math.Max(40,(int)(96*scale)),r=(int)(18*scale);
                for(int y=-step;y<area.Height+step;y+=step)for(int x=-step;x<area.Width+step;x+=step){
                    int xx=x+((y/step)%2)*step/2;
                    if(pattern=="lines"){g.DrawArc(pen,xx,y,step,step,10,120);g.DrawArc(pen,xx+step/2,y+step/2,step,step,190,120);}
                    else {g.DrawEllipse(pen,xx,y,r,r);g.DrawLine(pen,xx+r*2,y+r*2,xx+r*2+7*scale,y+r*2);g.DrawLine(pen,xx+r*2,y+r*2,xx+r*2,y+r*2+7*scale);}
                }
            }
        }
    }
}
