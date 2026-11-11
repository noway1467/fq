using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FeiqLight
{
    /// <summary>双端共用内置图集，也支持保存为小尺寸副本的自选头像。</summary>
    public static class EmojiAvatars
    {
        private static readonly Bitmap atlas;
        private static readonly string[][] catalog;
        static EmojiAvatars()
        {
            var assembly = typeof(EmojiAvatars).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream("FeiqLight.Avatars.png"))
            using (Bitmap source = new Bitmap(stream)) atlas = new Bitmap(source);
            using (Stream stream = assembly.GetManifestResourceStream("FeiqLight.Avatars.txt"))
            using (StreamReader reader = new StreamReader(stream))
                catalog = Array.ConvertAll(reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), line => line.Split('|'));
        }
        public static int Count { get { return catalog.Length; } }
        public static int Index(string identity)
        {
            // 明确使用 UTF-16 / uint32，不能用进程相关的 GetHashCode；与 Android 无符号取模一致。
            uint hash = 0;
            foreach (char c in identity ?? "") hash = unchecked(hash * 31 + c);
            return (int)(hash % (uint)Count);
        }
        public static string Description(string identity) { return catalog[Index(identity)][1]; }
        public static string DescriptionAt(int index) { return catalog[index][1]; }
        public static string FromLocalImage(string path)
        {
            if(new FileInfo(path).Length > 20 * 1024 * 1024) throw new IOException("请选择不超过 20 MiB 的头像图片。");
            using(Image source = Image.FromFile(path)) {
                if((long)source.Width * source.Height > 40000000) throw new IOException("头像图片不能超过 4000 万像素。");
                using(Bitmap image = new Bitmap(64, 64)) {
                    using(Graphics g = Graphics.FromImage(image)) {
                        g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        int side = Math.Min(source.Width, source.Height);
                        g.DrawImage(source, new Rectangle(0, 0, 64, 64), new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                    }
                    ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    foreach(long quality in new long[] { 85, 70, 55, 40 }) using(MemoryStream bytes = new MemoryStream()) using(EncoderParameters parameters = new EncoderParameters(1)) {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality); image.Save(bytes, jpeg, parameters);
                        if(bytes.Length <= 4096) return "image:" + Convert.ToBase64String(bytes.ToArray());
                    }
                    throw new IOException("图片无法缩成头像，请换一张图片。");
                }
            }
        }
        internal static void Draw(Graphics graphics, Rectangle bounds, string identity, string avatar = null)
        {
            int index = Index(identity), chosen;
            if(avatar != null && avatar.StartsWith("builtin:", StringComparison.Ordinal) && Int32.TryParse(avatar.Substring(8), out chosen) && chosen >= 0 && chosen < Count) index = chosen;
            if(avatar != null && avatar.StartsWith("image:", StringComparison.Ordinal)) {
                try {
                    using(MemoryStream bytes = new MemoryStream(Convert.FromBase64String(avatar.Substring(6)))) using(Image image = Image.FromStream(bytes)) {
                        if(image.Width == 64 && image.Height == 64) {
                            GraphicsState imageState = graphics.Save();
                            try { using(GraphicsPath circle = new GraphicsPath()) { circle.AddEllipse(bounds); graphics.SetClip(circle, CombineMode.Intersect); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, bounds); } }
                            finally { graphics.Restore(imageState); }
                            return;
                        }
                    }
                } catch(ArgumentException) { } catch(System.Runtime.InteropServices.ExternalException) { }
            }
            Color background = Color.FromArgb(unchecked((int)0xff000000) | Int32.Parse(catalog[index][2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            using (SolidBrush brush = new SolidBrush(background))
                graphics.FillEllipse(brush, bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2);
            int size = (int)Math.Round(bounds.Width * .72F);
            Rectangle target = new Rectangle(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
            GraphicsState state = graphics.Save();
            try {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(atlas, target, new Rectangle(index % 6 * 80 + 4, index / 6 * 80 + 4, 72, 72), GraphicsUnit.Pixel);
            } finally { graphics.Restore(state); }
        }
    }
}
