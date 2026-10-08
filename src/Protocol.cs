using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FeiqLight
{
    public sealed class Packet
    {
        public long Number;
        public string Login;
        public string Host;
        public uint Command;
        public string Body;
        public string Extra;
        public uint Mode { get { return Command & 255; } }
    }
    public static class Protocol
    {
        public const uint Entry = 1, Exit = 2, AnswerEntry = 3, Absence = 4;
        public const uint SendMessage = 32, ReceiveMessage = 33, GetFile = 96, ReleaseFiles = 97;
        public const uint SendCheck = 0x100, FileAttach = 0x200000, Utf8 = 0x800000, CapUtf8 = 0x1000000;
        public static readonly Encoding Legacy = Encoding.GetEncoding(936);
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding StrictLegacy = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        public static string Field(string value)
        {
            return (value ?? "").Replace(":", "_").Replace("\0", "").Replace("\n", " ").Replace("\r", " ");
        }
        public static string Prefix(string value, int length)
        {
            int end = Math.Min(value.Length, length);
            if (end > 0 && end < value.Length && Char.IsHighSurrogate(value[end - 1]) && Char.IsLowSurrogate(value[end])) end--;
            return value.Substring(0, end);
        }
        public static byte[] Encode(long number, string login, string host, uint command, string body, string extra)
        {
            if (number < 0 || Field(login).Length > 128 || Field(host).Length > 128) throw new ArgumentException("无效的报文头。");
            if ((body ?? "").IndexOf('\0') >= 0 || (extra ?? "").IndexOf('\0') >= 0) throw new ArgumentException("消息不能包含空字符。");
            Encoding encoding = (command & Utf8) != 0 ? StrictUtf8 : StrictLegacy;
            string wire = "1:" + number.ToString(CultureInfo.InvariantCulture) + ":" + Field(login) + ":" + Field(host) + ":" + command.ToString(CultureInfo.InvariantCulture) + ":" + (body ?? "") + "\0";
            if (extra != null) wire += extra + "\0";
            byte[] result;
            try { result = encoding.GetBytes(wire); }
            catch (EncoderFallbackException) { throw new ArgumentException((command & Utf8) != 0 ? "文本包含不完整的 Unicode 字符，请重新输入。" : "对方未声明 UTF-8 支持，当前文字或文件名无法无损发送，请刷新联系人或更换内容。"); }
            if (result.Length > 60000) throw new ArgumentException("消息过长，请分段发送（最多约 18,000 个汉字）。");
            return result;
        }
        public static bool TryParse(byte[] bytes, out Packet packet)
        {
            packet = null;
            if (bytes == null || bytes.Length < 12 || bytes.Length > 60000) return false;
            int[] colon = new int[5]; int found = 0;
            for (int i = 0; i < bytes.Length && found < 5; i++) if (bytes[i] == 58) colon[found++] = i;
            if (found < 5 || colon[4] > 2048) return false;
            string header = Encoding.ASCII.GetString(bytes, 0, colon[4]);
            string[] fields = header.Split(':'); uint command; long number;
            if (fields[0] != "1" || !Int64.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0 || !UInt32.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out command)) return false;
            Encoding encoding = (command & Utf8) != 0 ? StrictUtf8 : StrictLegacy;
            string decoded;
            try { decoded = encoding.GetString(bytes); } catch (DecoderFallbackException) { return false; }
            string[] parts = decoded.Split(new[] { ':' }, 6);
            string[] body = parts[5].Split('\0');
            packet = new Packet { Number = number, Login = parts[2], Host = parts[3], Command = command, Body = body[0], Extra = body.Length > 1 ? body[1] : "" };
            return packet.Login.Length <= 128 && packet.Host.Length <= 128;
        }
        public static bool SafeFileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 240 || name.EndsWith(".") || name.EndsWith(" ")) return false;
            try { if (StrictUtf8.GetByteCount(name) > 240) return false; } catch (EncoderFallbackException) { return false; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(Char.IsControl)) return false;
            if (name != Path.GetFileName(name)) return false;
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant().Replace('¹', '1').Replace('²', '2').Replace('³', '3');
            return !(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }).Contains(stem);
        }
        public static string FileList(IEnumerable<Attachment> files)
        {
            return String.Join("", files.Select(f => f.Id.ToString(CultureInfo.InvariantCulture) + ":" + f.Name.Replace(":", "::") + ":" + f.Size.ToString("x") + ":" + f.Modified.ToString("x") + ":1:\a"));
        }
        public static List<Attachment> ParseFiles(string text)
        {
            List<Attachment> result = new List<Attachment>();
            foreach (string row in (text ?? "").Split('\a').Take(100))
            {
                string[] f = row.Split(':'); int id; long size, time; uint attr;
                if (f.Length < 5 || !Int32.TryParse(f[0], out id) || id < 0 || !SafeFileName(f[1])) continue;
                if (!Int64.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) || size < 0) continue;
                if (!Int64.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out time)) continue;
                if (!UInt32.TryParse(f[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out attr) || (attr & 255) != 1) continue;
                if (result.Any(x => x.Id == id)) continue;
                result.Add(new Attachment { Id = id, Name = f[1], Size = size, Modified = time });
            }
            return result;
        }
    }
}
