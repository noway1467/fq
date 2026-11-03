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
    internal sealed class RemoteDesktop
    {
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] internal MouseInput Mouse; [FieldOffset(0)] internal KeyboardInput Key; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeInput { internal uint Type; internal InputUnion Value; }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        private bool dragging;
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
        internal object Displays() { return new { items = Screens().Select((s, index) => new { id = index, name = (s.Primary ? "主屏幕" : "屏幕 " + (index + 1)) + " · " + s.Bounds.Width + " × " + s.Bounds.Height }).ToArray() }; }
        internal byte[] Capture(int display = 0)
        {
            EnsureDesktop(); Rectangle screen = Bounds(display);
            if ((long)screen.Width * screen.Height > 40000000) throw new ToolsHttp.HttpError(409, "主屏幕尺寸过大");
            using (Bitmap full = new Bitmap(screen.Width, screen.Height))
            {
                using (Graphics graphics = Graphics.FromImage(full))
                {
                    graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
                    Point cursor = Cursor.Position;
                    using (Pen outline = new Pen(Color.Black, 4)) graphics.DrawEllipse(outline, cursor.X - screen.X - 8, cursor.Y - screen.Y - 8, 16, 16);
                    using (Pen pointer = new Pen(Color.White, 2)) graphics.DrawEllipse(pointer, cursor.X - screen.X - 8, cursor.Y - screen.Y - 8, 16, 16);
                }
                int width = Math.Min(1600, screen.Width), height = Math.Max(1, screen.Height * width / screen.Width);
                using (Bitmap scaled = new Bitmap(full, width, height))
                using (MemoryStream output = new MemoryStream())
                using (EncoderParameters parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                    scaled.Save(output, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters); return output.ToArray();
                }
            }
        }
        private static NativeInput Key(ushort key, bool up, bool unicode = false)
        {
            return new NativeInput { Type = 1, Value = new InputUnion { Key = new KeyboardInput { Key = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0, Flags = (up ? 2U : 0U) | (unicode ? 4U : 0U) } } };
        }
        private static void Send(params NativeInput[] values)
        {
            if (SendInput((uint)values.Length, values, Marshal.SizeOf(typeof(NativeInput))) != values.Length) throw new ToolsHttp.HttpError(409, "无法输入；请在电脑上退出管理员或系统安全窗口");
        }
        private static void Mouse(uint flags, uint data = 0, int x = 0, int y = 0) { Send(new NativeInput { Value = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data, X = x, Y = y } } }); }
        private static void Chord(params ushort[] keys)
        {
            try { Send(keys.Select(k => Key(k, false)).ToArray()); }
            finally { Send(keys.Reverse().Select(k => Key(k, true)).ToArray()); }
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
    }
}
