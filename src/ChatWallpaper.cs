using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace FeiqLight
{
    internal static class ChatWallpaper
    {
        internal sealed class Asset { internal Bitmap Image; internal string Key; internal int Users; }
        private static readonly Dictionary<string,Asset> cache=new Dictionary<string,Asset>(StringComparer.OrdinalIgnoreCase);
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
        // 渲染结果按尺寸、图案、主题版本缓存：同一窗口的多个会话、滚动与动画帧只做一次位图拷贝。
        private sealed class Rendered { internal Bitmap Image; internal string Key; internal long Used; }
        private static readonly List<Rendered> rendered=new List<Rendered>();
        private static long useCounter;
        internal static void Draw(Graphics g,Rectangle area,string pattern,Asset image,float scale)
        {
            if(area.Width<=0||area.Height<=0)return;
            string key=area.Width+"x"+area.Height+"|"+pattern+"|"+Theme.Version+"|"+scale+"|"+(image==null?"":image.Key);
            Rendered hit=null;foreach(Rendered item in rendered)if(item.Key==key){hit=item;break;}
            if(hit==null) {
                // 先记使用序号再淘汰，刚插入的条目不会被当成最久未用而当场释放。
                hit=new Rendered {Key=key,Image=Render(area.Size,pattern,image==null?null:image.Image,scale),Used=++useCounter};rendered.Add(hit);
                // 最多保留四份（多显示器、历史窗口与主窗口），按最久未用淘汰。
                while(rendered.Count>4){Rendered oldest=rendered[0];foreach(Rendered item in rendered)if(item.Used<oldest.Used)oldest=item;rendered.Remove(oldest);if(!Object.ReferenceEquals(oldest,hit))oldest.Image.Dispose();}
            }
            hit.Used=++useCounter;
            g.DrawImageUnscaled(hit.Image,area.Location);
        }
        internal static Bitmap Render(Size size,string pattern,Image image,float scale)
        {
            Bitmap result=new Bitmap(Math.Max(1,size.Width),Math.Max(1,size.Height),PixelFormat.Format32bppPArgb);
            using(Graphics g=Graphics.FromImage(result)) {
                Rectangle area=new Rectangle(Point.Empty,result.Size);
                if(pattern=="image"&&image!=null) {
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    double ratio=Math.Max((double)area.Width/image.Width,(double)area.Height/image.Height);
                    int w=(int)Math.Ceiling(image.Width*ratio),h=(int)Math.Ceiling(image.Height*ratio);
                    g.DrawImage(image,new Rectangle((area.Width-w)/2,(area.Height-h)/2,w,h));
                    using(SolidBrush veil=new SolidBrush(Color.FromArgb(Theme.Dark?90:55,Theme.Wallpaper)))g.FillRectangle(veil,area);
                    return result;
                }
                if(pattern=="none") {using(SolidBrush solid=new SolidBrush(Theme.Wallpaper))g.FillRectangle(solid,area);return result;}
                Gradient(g,area);
                if(pattern=="lines")Waves(g,area,scale);else Doodles(g,area,scale);
            }
            return result;
        }
        // 四色自由渐变：低分辨率逐点混色后双三次放大，柔和且与窗口尺寸无关。
        private static readonly PointF[] Anchors={new PointF(.80F,.10F),new PointF(.30F,.28F),new PointF(.18F,.92F),new PointF(.70F,.74F)};
        private static void Gradient(Graphics g,Rectangle area)
        {
            const int w=40,h=52;Color[] colors=Theme.WallpaperColors;
            using(Bitmap small=new Bitmap(w,h,PixelFormat.Format32bppArgb)) {
                float aspect=(float)area.Width/Math.Max(1,area.Height);
                for(int y=0;y<h;y++)for(int x=0;x<w;x++) {
                    float u=(x+.5F)/w,v=(y+.5F)/h;double r=0,gr=0,b=0,total=0;
                    for(int i=0;i<4;i++) {
                        double dx=(u-Anchors[i].X)*aspect,dy=v-Anchors[i].Y,d=Math.Sqrt(dx*dx+dy*dy);
                        double weight=Math.Max(0,1.05-d);weight=Math.Pow(weight,3.2)+1e-6;
                        r+=colors[i].R*weight;gr+=colors[i].G*weight;b+=colors[i].B*weight;total+=weight;
                    }
                    small.SetPixel(x,y,Color.FromArgb((int)(r/total),(int)(gr/total),(int)(b/total)));
                }
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                using(ImageAttributes attributes=new ImageAttributes()) {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(small,area,0,0,w,h,GraphicsUnit.Pixel,attributes);
                }
            }
        }
        private static Color PatternColor(int alpha) { return Theme.Dark?Color.FromArgb(alpha*2/3,255,255,255):Color.FromArgb(alpha,Theme.Blend(Color.Black,Theme.Blue,.55F)); }
        // 涂鸦图案：固定种子的抖动网格平铺，聊天、文件、纸飞机等局域网主题线稿，每块可无缝拼接。
        private static readonly string[] Shapes={"chat","send","heart","file","smile","star","clip","cloud","image","music","bell","coffee","folder","sparkle","pin","monitor"};
        private static void Doodles(Graphics g,Rectangle area,float scale)
        {
            int cell=(int)Math.Round(78*scale),columns=4,rows=4,tile=cell*columns;
            using(Bitmap pattern=new Bitmap(tile,tile,PixelFormat.Format32bppPArgb)) {
                using(Graphics p=Graphics.FromImage(pattern)) {
                    p.SmoothingMode=SmoothingMode.AntiAlias;Random random=new Random(20261027);Color ink=PatternColor(30);
                    for(int row=0;row<rows;row++)for(int column=0;column<columns;column++) {
                        int index=(row*columns+column+row*3)%Shapes.Length;float size=(float)((26+random.NextDouble()*10)*scale);
                        float x=column*cell+(cell-size)/2F+(float)((random.NextDouble()-.5)*cell*.34),y=row*cell+(cell-size)/2F+(float)((random.NextDouble()-.5)*cell*.34);
                        if(row%2==1)x+=cell/2F;if(x+size>tile)x-=tile;
                        float angle=(float)((random.NextDouble()-.5)*50);
                        for(int wrap=0;wrap<2;wrap++) {
                            float ox=wrap==0?x:x+tile;if(wrap==1&&x>=0)break;
                            GraphicsState state=p.Save();p.TranslateTransform(ox+size/2,y+size/2);p.RotateTransform(angle);
                            Theme.Glyph(p,Shapes[index],new RectangleF(-size/2,-size/2,size,size),ink);p.Restore(state);
                        }
                        // 小点缀填补空隙，避免图案呈规则方阵。
                        using(SolidBrush dot=new SolidBrush(PatternColor(24))) {float d=(float)(3.2*scale);p.FillEllipse(dot,column*cell+(float)(random.NextDouble()*cell*.8),row*cell+(float)(random.NextDouble()*cell*.8),d,d);}
                    }
                }
                using(TextureBrush brush=new TextureBrush(pattern,WrapMode.Tile))g.FillRectangle(brush,area);
            }
        }
        private static void Waves(Graphics g,Rectangle area,float scale)
        {
            g.SmoothingMode=SmoothingMode.AntiAlias;int gap=Math.Max(18,(int)(34*scale));
            for(int band=-2,line=0;band*gap<area.Height+gap*2;band++,line++) {
                using(Pen pen=new Pen(PatternColor(line%3==0?34:20),Math.Max(1F,(line%3==0?1.6F:1.1F)*scale))) {
                    List<PointF> points=new List<PointF>();double phase=band*.55,amplitude=(10+(line%4)*3)*scale,length=(260+(line%3)*60)*scale;
                    for(float x=-20;x<=area.Width+20;x+=6*scale)points.Add(new PointF(x,(float)(band*gap+Math.Sin(x/length*Math.PI*2+phase)*amplitude+Math.Sin(x/(length*.43)+phase*1.7)*amplitude*.35)));
                    if(points.Count>1)g.DrawCurve(pen,points.ToArray(),.5F);
                }
            }
        }
    }
}
