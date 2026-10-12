using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;

namespace FeiqLight
{
    /// <summary>双端共用内置图集，避免系统 emoji 字体不同或缺字；不发送头像资料。</summary>
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
        internal static void Draw(Graphics graphics, Rectangle bounds, string identity)
        {
            int index = Index(identity);
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
