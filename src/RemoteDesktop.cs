using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FeiqLight
{
    internal sealed class RemoteDesktop : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] internal MouseInput Mouse; [FieldOffset(0)] internal KeyboardInput Key; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeInput { internal uint Type; internal InputUnion Value; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        private bool dragging;
        private readonly object frameGate = new object();
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        private Bitmap fullBitmap, scaledBitmap;
        private byte[] previousPixels, currentPixels;
        private int cachedDisplay = -1, cachedWidth, cachedHeight;
        private uint sequence;
        private bool disposed;
        private static void EnsureDesktop()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero) throw new ToolsHttp.HttpError(409, "桌面已锁定或正在显示系统安全窗口，请在电脑上处理");
            CloseDesktop(desktop);
        }
        private static Screen[] Screens() { return Screen.AllScreens.OrderByDescending(s => s.Primary).ThenBy(s => s.DeviceName, StringComparer.Ordinal).ToArray(); }
        private static Rectangle Bounds(int display)
        {
            Screen[] screens = Screens(); if (display < 0 || display >= screens.Length) throw new ToolsHttp.HttpError(409, "显示器已变化，请重新连接"); return screens[display].Bounds;
        }
        internal object Displays(bool clipboardRead) { return new { clipboardWrite = true, clipboardRead = clipboardRead, chord = true, items = Screens().Select((s, index) => new { id = index, name = (s.Primary ? "主屏幕" : "屏幕 " + (index + 1)) + " · " + s.Bounds.Width + " × " + s.Bounds.Height }).ToArray() }; }
        internal byte[] Capture(int display = 0)
        {
            lock (frameGate) return EncodeJpeg(CaptureBitmap(display));
        }
        internal byte[] CaptureDelta(int display, uint since)
        {
            lock (frameGate) return EncodeFrame(CaptureBitmap(display), display, since);
        }
        private Bitmap CaptureBitmap(int display)
        {
            if (disposed) throw new ObjectDisposedException("RemoteDesktop");
            EnsureDesktop(); Rectangle screen = Bounds(display);
            if ((long)screen.Width * screen.Height > 40000000) throw new ToolsHttp.HttpError(409, "主屏幕尺寸过大");
            int width = Math.Min(1600, screen.Width), height = Math.Max(1, screen.Height * width / screen.Width);
            if (height > UInt16.MaxValue) throw new ToolsHttp.HttpError(409, "屏幕高度过大");
            if (fullBitmap == null || fullBitmap.Size != screen.Size)
            {
                if (fullBitmap != null) fullBitmap.Dispose();
                fullBitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppRgb);
                cachedDisplay = -1;
            }
            if (scaledBitmap == null || scaledBitmap.Width != width || scaledBitmap.Height != height)
            {
                if (scaledBitmap != null) scaledBitmap.Dispose();
                scaledBitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            }
            using (Graphics graphics = Graphics.FromImage(fullBitmap)) graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            using (Graphics graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.DrawImage(fullBitmap, new Rectangle(0, 0, width, height));
                Point cursor = Cursor.Position;
                float x = (cursor.X - screen.X) * width / (float)screen.Width, y = (cursor.Y - screen.Y) * height / (float)screen.Height;
                using (Pen outline = new Pen(Color.Black, 4)) graphics.DrawEllipse(outline, x - 8, y - 8, 16, 16);
                using (Pen pointer = new Pen(Color.White, 2)) graphics.DrawEllipse(pointer, x - 8, y - 8, 16, 16);
            }
            return scaledBitmap;
        }
        private static byte[] EncodeJpeg(Bitmap bitmap)
        {
            using (MemoryStream output = new MemoryStream())
            using (EncoderParameters parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                bitmap.Save(output, JpegCodec, parameters); return output.ToArray();
            }
        }
        // 使用相同编码路径处理真实截图与确定性像素夹具；不依赖宿主机桌面变化来判断脏块。
        internal byte[] EncodeFrame(Bitmap bitmap, int display, uint since)
        {
            lock (frameGate)
            {
                if (disposed) throw new ObjectDisposedException("RemoteDesktop");
                int width = bitmap.Width, height = bitmap.Height, rowBytes = width * 4;
                bool full = since == 0 || since != sequence || display != cachedDisplay || width != cachedWidth || height != cachedHeight;
                if (currentPixels == null || currentPixels.Length != rowBytes * height)
                {
                    currentPixels = new byte[rowBytes * height]; previousPixels = new byte[currentPixels.Length]; full = true;
                }
                BitmapData pixels = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(pixels.Scan0, y * pixels.Stride), currentPixels, y * rowBytes, rowBytes);
                }
                finally { bitmap.UnlockBits(pixels); }
                List<Rectangle> changed = new List<Rectangle>(); long area = 0;
                if (!full)
                {
                    for (int y = 0; y < height; y += 128)
                        for (int x = 0; x < width; x += 128)
                        {
                            Rectangle tile = new Rectangle(x, y, Math.Min(128, width - x), Math.Min(128, height - y));
                            bool dirty = false;
                            for (int row = y; row < tile.Bottom && !dirty; row++)
                                for (int i = row * rowBytes + x * 4; i < row * rowBytes + tile.Right * 4; i++)
                                    if (currentPixels[i] != previousPixels[i]) { dirty = true; break; }
                            if (dirty) { changed.Add(tile); area += tile.Width * tile.Height; }
                        }
                    full = area * 100 > (long)width * height * 55;
                }
                if (full) { changed.Clear(); changed.Add(new Rectangle(0, 0, width, height)); }
                uint next = changed.Count == 0 ? sequence : sequence == UInt32.MaxValue ? 1 : sequence + 1;
                using (MemoryStream output = new MemoryStream())
                {
                    output.Write(new byte[] { 70, 81, 70, 49, (byte)(full ? 1 : changed.Count == 0 ? 0 : 2), 0, 0, 0 }, 0, 8);
                    WriteNumber(output, next, 4); WriteNumber(output, (uint)width, 2); WriteNumber(output, (uint)height, 2); WriteNumber(output, (uint)changed.Count, 2);
                    foreach (Rectangle tile in changed)
                    {
                        byte[] jpeg;
                        if (full) jpeg = EncodeJpeg(bitmap);
                        else using (Bitmap part = bitmap.Clone(tile, PixelFormat.Format32bppRgb)) jpeg = EncodeJpeg(part);
                        WriteNumber(output, (uint)tile.X, 2); WriteNumber(output, (uint)tile.Y, 2); WriteNumber(output, (uint)tile.Width, 2); WriteNumber(output, (uint)tile.Height, 2); WriteNumber(output, (uint)jpeg.Length, 4);
                        output.Write(jpeg, 0, jpeg.Length);
                    }
                    // 只有全部编码成功后才推进基线；丢帧客户端会用旧序号触发整帧恢复。
                    if (changed.Count > 0) { byte[] swap = previousPixels; previousPixels = currentPixels; currentPixels = swap; }
                    sequence = next; cachedDisplay = display; cachedWidth = width; cachedHeight = height;
                    return output.ToArray();
                }
            }
        }
        private static void WriteNumber(Stream stream, uint value, int bytes)
        {
            for (int shift = (bytes - 1) * 8; shift >= 0; shift -= 8) stream.WriteByte((byte)(value >> shift));
        }
        private static NativeInput Key(ushort key, bool up, bool unicode = false)
        {
            bool extended = !unicode && (key == 0x5b || (key >= 0x21 && key <= 0x28) || key == 0x2d || key == 0x2e);
            return new NativeInput { Type = 1, Value = new InputUnion { Key = new KeyboardInput { Key = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0, Flags = (up ? 2U : 0U) | (unicode ? 4U : 0U) | (extended ? 1U : 0U) } } };
        }
        private static void Send(params NativeInput[] values)
        {
            if (SendInput((uint)values.Length, values, Marshal.SizeOf(typeof(NativeInput))) != values.Length) throw new ToolsHttp.HttpError(409, "无法输入；请在电脑上退出管理员或系统安全窗口");
        }
        private static void Mouse(uint flags, uint data = 0, int x = 0, int y = 0) { Send(new NativeInput { Value = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data, X = x, Y = y } } }); }
        private static void Chord(params ushort[] keys)
        {
            try { Send(keys.Select(k => Key(k, false)).ToArray()); }
            finally
            {
                // 即使按下只成功一部分，也逐个尽力释放，且不覆盖原来的输入失败原因。
                foreach (ushort key in keys.Reverse()) SendInput(1, new[] { Key(key, true) }, Marshal.SizeOf(typeof(NativeInput)));
            }
        }
        internal static ushort[] ParseChord(Dictionary<string, object> data)
        {
            object value;
            System.Collections.IList modifiers = data.TryGetValue("modifiers", out value) ? value as System.Collections.IList : null;
            if (modifiers == null || modifiers.Count > 4) throw new ToolsHttp.HttpError(400, "修饰键最多 4 个");
            string[] names = { "ctrl", "alt", "shift", "win" }; ushort[] codes = { 0x11, 0x12, 0x10, 0x5b };
            HashSet<string> selected = new HashSet<string>();
            foreach (object modifier in modifiers)
            {
                string name = modifier as string;
                if (name == null || !names.Contains(name)) throw new ToolsHttp.HttpError(400, "不支持的修饰键");
                selected.Add(name);
            }
            string main = ToolsService.Text(data, "key"); ushort key = 0;
            if (main.Length == 1 && ((main[0] >= 'a' && main[0] <= 'z') || (main[0] >= '0' && main[0] <= '9'))) key = (ushort)Char.ToUpperInvariant(main[0]);
            else
            {
                string[] special = { "enter", "escape", "tab", "backspace", "delete", "insert", "space", "up", "down", "left", "right", "home", "end", "pageup", "pagedown", "win" };
                ushort[] specialCodes = { 0x0d, 0x1b, 0x09, 0x08, 0x2e, 0x2d, 0x20, 0x26, 0x28, 0x25, 0x27, 0x24, 0x23, 0x21, 0x22, 0x5b };
                int index = Array.IndexOf(special, main);
                if (index >= 0) key = specialCodes[index];
                for (int i = 1; i <= 12; i++) if (main == "f" + i) key = (ushort)(0x6f + i);
            }
            if (key == 0 || (main == "win" && selected.Count > 0)) throw new ToolsHttp.HttpError(400, "不支持的主键");
            if (selected.Contains("ctrl") && selected.Contains("alt") && (main == "delete" || main == "f12")) throw new ToolsHttp.HttpError(400, "系统安全组合键和本机停止热键不支持远程模拟");
            List<ushort> keys = new List<ushort>();
            for (int i = 0; i < names.Length; i++) if (selected.Contains(names[i])) keys.Add(codes[i]);
            keys.Add(key); return keys.ToArray();
        }
        internal static int Number(Dictionary<string, object> data, string key, int min, int max)
        {
            object value; if (!data.TryGetValue(key, out value) || !(value is int) || (int)value < min || (int)value > max) throw new ToolsHttp.HttpError(400, "坐标或滚动距离无效"); return (int)value;
        }
        internal void Apply(Dictionary<string, object> data)
        {
            EnsureDesktop(); string type = ToolsService.Text(data, "type");
            int display = data.ContainsKey("display") ? Number(data, "display", 0, 31) : 0;
            if (type == "move")
            {
                int x = Number(data, "x", -2000, 2000), y = Number(data, "y", -2000, 2000); Rectangle area = Bounds(display); Point cursor = Cursor.Position;
                if (!area.Contains(cursor)) cursor = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
                Cursor.Position = new Point(Math.Max(area.Left, Math.Min(area.Right - 1, cursor.X + x)), Math.Max(area.Top, Math.Min(area.Bottom - 1, cursor.Y + y))); return;
            }
            if (type == "point")
            {
                int x = Number(data, "x", 0, 10000), y = Number(data, "y", 0, 10000); Rectangle screen = Bounds(display);
                Cursor.Position = new Point(screen.X + (int)((screen.Width - 1) * x / 10000L), screen.Y + (int)((screen.Height - 1) * y / 10000L)); return;
            }
            if (type == "wheel") { Mouse(0x800, unchecked((uint)Number(data, "y", -1200, 1200))); return; }
            if (type == "click" || type == "right") { uint down = type == "click" ? 2U : 8U; try { Mouse(down); } finally { Mouse(down * 2); } return; }
            if (type == "drag") { if (!dragging) { Mouse(2); dragging = true; } return; }
            if (type == "release") { Release(); return; }
            if (type == "chord") { Chord(ParseChord(data)); return; }
            if (type == "text")
            {
                string text = ToolsService.Text(data, "text").Replace("\r\n", "\n");
                if (text.Length > 4000 || text.Any(c => Char.IsControl(c) && c != '\n' && c != '\t')) throw new ToolsHttp.HttpError(400, "文字最多 4000 字，不能包含控制字符");
                try { new System.Text.UTF8Encoding(false, true).GetByteCount(text); } catch (System.Text.EncoderFallbackException) { throw new ToolsHttp.HttpError(400, "文字编码无效"); }
                foreach (char c in text)
                {
                    // 换行使用 Shift+Enter，避免多行提示词在 Codex 中被提前提交。
                    if (c == '\n') Chord(0x10, 0x0d); else if (c == '\t') Chord(0x09); else Send(Key(c, false, true), Key(c, true, true));
                }
                return;
            }
            if (type == "key")
            {
                switch (ToolsService.Text(data, "key"))
                {
                    case "enter": Chord(0x0d); return; case "newline": Chord(0x10, 0x0d); return; case "ctrlenter": Chord(0x11, 0x0d); return;
                    case "escape": Chord(0x1b); return; case "tab": Chord(0x09); return; case "backspace": Chord(0x08); return;
                    case "selectall": Chord(0x11, 0x41); return; case "copy": Chord(0x11, 0x43); return; case "paste": Chord(0x11, 0x56); return;
                    case "undo": Chord(0x11, 0x5a); return; case "find": Chord(0x11, 0x46); return; case "switch": Chord(0x12, 0x09); return;
                    case "left": Chord(0x25); return; case "up": Chord(0x26); return; case "right": Chord(0x27); return; case "down": Chord(0x28); return;
                    case "pageup": Chord(0x21); return; case "pagedown": Chord(0x22); return; case "home": Chord(0x24); return; case "end": Chord(0x23); return;
                }
            }
            throw new ToolsHttp.HttpError(400, "不支持的控制操作");
        }
        internal void Release()
        {
            if (!dragging) return; dragging = false;
            // 桌面锁定后输入可能被系统拒绝；释放请求仍尽力发出，不能阻止会话撤销。
            NativeInput[] release = { new NativeInput { Value = new InputUnion { Mouse = new MouseInput { Flags = 4 } } } };
            SendInput(1, release, Marshal.SizeOf(typeof(NativeInput)));
        }
        public void Dispose()
        {
            lock (frameGate)
            {
                if (disposed) return; disposed = true; Release();
                if (fullBitmap != null) fullBitmap.Dispose();
                if (scaledBitmap != null) scaledBitmap.Dispose();
                fullBitmap = scaledBitmap = null; previousPixels = currentPixels = null;
            }
        }
    }
}
