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
        public string DeviceId;
        public string Avatar;
        public string Fingerprint;
        public bool ReverseConnections;
        public int ReplyPort;
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
        internal const int MaxPacketBytes = 60000;
        internal const int MaxHeaderColonOffset = 2048;
        internal const int MaxHeaderFieldChars = 128;
        internal const int MaxFileNameUtf8Bytes = 240;
        internal const int MaxFileNameChars = 240;
        internal const int MaxRelativePathChars = 2048;
        internal const int MaxRelativePathSegments = 32;
        internal const int MaxParsedAttachments = 100;
        internal const int MaxAvatarJpegBytes = 16384;
        internal const int MaxAttachmentsPerMessage = 20;
        internal const int HelloFrameBytes = 4096;
        public static readonly Encoding Legacy = Encoding.GetEncoding(936);
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding StrictLegacy = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        public static string Field(string value)
        {
            return MessageText.NormalizeLineBreaks(value).Replace(":", "_").Replace("\0", "").Replace('\n', ' ');
        }
        public static string Prefix(string value, int length)
        {
            int end = Math.Min(value.Length, length);
            if (end > 0 && end < value.Length && Char.IsHighSurrogate(value[end - 1]) && Char.IsLowSurrogate(value[end])) end--;
            return value.Substring(0, end);
        }
        public static string NormalizeDeviceId(string value)
        {
            if (value == null || value.Length != 32 || !AsciiNumber(value, true)) return null;
            return NormalizeStoredDeviceId(value);
        }
        internal static string NormalizeStoredDeviceId(string value)
        {
            Guid id; return value != null && Guid.TryParseExact(value, "N", out id) && id != Guid.Empty ? id.ToString("N") : null;
        }
        private static bool AsciiNumber(string text, bool hex)
        {
            return text.Length != 0 && text.All(ch => ch >= '0' && ch <= '9' || hex &&
                (ch >= 'a' && ch <= 'f' || ch >= 'A' && ch <= 'F'));
        }
        private static byte[] DecodeBase64(string text)
        {
            byte[] bytes = Convert.FromBase64String(text);
            if (Convert.ToBase64String(bytes) != text) throw new FormatException("Base64 不是标准带填充格式。");
            return bytes;
        }
        public static string NormalizeAvatar(string value)
        {
            if(value == "default") return value;
            int index;
            if(value != null && value.StartsWith("builtin:", StringComparison.Ordinal)
                && Int32.TryParse(value.Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out index)
                && index >= 0 && index < 24 && value == "builtin:" + index.ToString(CultureInfo.InvariantCulture)) return value;
            if(value == null || !value.StartsWith("image:", StringComparison.Ordinal) || value.Length > 21854) return null;
            try {
                byte[] bytes = DecodeBase64(value.Substring(6));
                if(bytes.Length < 12 || bytes.Length > MaxAvatarJpegBytes || bytes[0] != 255 || bytes[1] != 216 || bytes[bytes.Length - 2] != 255 || bytes[bytes.Length - 1] != 217) return null;
                // 先读取 JPEG 尺寸，再交给图像解码器；网络输入不能声明超大画布消耗内存。
                for(int offset = 2; offset + 8 < bytes.Length;) {
                    if(bytes[offset++] != 255) return null;
                    while(offset < bytes.Length && bytes[offset] == 255) offset++;
                    if(offset + 2 >= bytes.Length) return null;
                    int marker = bytes[offset++], length = (bytes[offset] << 8) | bytes[offset + 1];
                    if(length < 2 || offset + length > bytes.Length) return null;
                    if(marker == 192) {
                        if(length < 8 || bytes[offset + 2] != 8) return null;
                        int height = (bytes[offset + 3] << 8) | bytes[offset + 4], width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                        return (width == 64 || width == 128) && height == width ? "image:" + Convert.ToBase64String(bytes) : null;
                    }
                    if(marker == 218 || marker == 217) return null;
                    offset += length;
                }
            } catch(FormatException) { }
            return null;
        }
        public static byte[] Encode(long number, string login, string host, uint command, string body, string extra, string deviceId = null, string avatar = null, string fingerprint = null)
        {
            if (number < 0 || Field(login).Length > MaxHeaderFieldChars || Field(host).Length > MaxHeaderFieldChars) throw new ArgumentException("无效的报文头。");
            if ((body ?? "").IndexOf('\0') >= 0 || (extra ?? "").IndexOf('\0') >= 0) throw new ArgumentException("消息不能包含空字符。");
            Encoding encoding = (command & Utf8) != 0 ? StrictUtf8 : StrictLegacy;
            string wire = "1:" + number.ToString(CultureInfo.InvariantCulture) + ":" + Field(login) + ":" + Field(host) + ":" + command.ToString(CultureInfo.InvariantCulture) + ":" + (body ?? "") + "\0";
            if (extra != null || deviceId != null || avatar != null) wire += (extra ?? "") + "\0";
            if (NormalizeDeviceId(deviceId) != null) wire += "FQID=" + NormalizeDeviceId(deviceId) + "\0";
            if(avatar != null) {
                string normalized = NormalizeAvatar(avatar); if(normalized == null) throw new ArgumentException("头像数据无效。");
                wire += "FQAV=" + normalized + "\0";
            }
            if (fingerprint != null) wire += "FQSEC=" + fingerprint + "\0";
            byte[] result;
            try { result = encoding.GetBytes(wire); }
            catch (EncoderFallbackException) { throw new ArgumentException((command & Utf8) != 0 ? "文本包含不完整的 Unicode 字符，请重新输入。" : "对方未声明 UTF-8 支持，当前文字或文件名无法无损发送，请刷新联系人或更换内容。"); }
            if (result.Length > MaxPacketBytes) throw new ArgumentException("消息过长，请分段发送（最多约 18,000 个汉字）。");
            return result;
        }
        public static bool TryParse(byte[] bytes, out Packet packet)
        {
            packet = null;
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaxPacketBytes) return false;
            int[] colon = new int[5]; int found = 0;
            for (int i = 0; i < bytes.Length && found < 5; i++) if (bytes[i] == 58) colon[found++] = i;
            if (found < 5 || colon[4] > MaxHeaderColonOffset) return false;
            string header = Encoding.ASCII.GetString(bytes, 0, colon[4]);
            string[] fields = header.Split(':'); uint command; long number;
            if (fields[0] != "1" || !Int64.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0 || !UInt32.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out command)) return false;
            Encoding encoding = (command & Utf8) != 0 ? StrictUtf8 : StrictLegacy;
            string decoded;
            try { decoded = encoding.GetString(bytes); } catch (DecoderFallbackException) { return false; }
            string[] parts = decoded.Split(new[] { ':' }, 6);
            string[] body = parts[5].Split('\0');
            packet = new Packet { Number = number, Login = parts[2], Host = parts[3], Command = command, Body = body[0], Extra = body.Length > 1 ? body[1] : "" };
            packet.DeviceId = body.Length > 2 && body[2].StartsWith("FQID=", StringComparison.Ordinal) ? NormalizeDeviceId(body[2].Substring(5)) : null;
            for(int i = 2; i < body.Length; i++) if(body[i].StartsWith("FQAV=", StringComparison.Ordinal)) packet.Avatar = NormalizeAvatar(body[i].Substring(5));
            for(int i = 2; i < body.Length; i++) if(body[i].StartsWith("FQSEC=", StringComparison.Ordinal)) { string value=body[i].Substring(6); if(value.Length==64&&value.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f')) packet.Fingerprint=value; }
            foreach (string field in body.Skip(2)) {
                if (!field.StartsWith("FQPORT=", StringComparison.Ordinal)) continue;
                string digits = field.Substring(7); int port;
                if (AsciiNumber(digits, false) && Int32.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    && port >= 1024 && port <= 65535) packet.ReplyPort = port;
            }
            packet.ReverseConnections = body.Skip(2).Contains("FQREV=1");
            return packet.Login.Length <= MaxHeaderFieldChars && packet.Host.Length <= MaxHeaderFieldChars;
        }
        public static bool SafeFileName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameChars || name.EndsWith(".") || name.EndsWith(" ")) return false;
            try { if (StrictUtf8.GetByteCount(name) > MaxFileNameUtf8Bytes) return false; } catch (EncoderFallbackException) { return false; }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(Char.IsControl)) return false;
            if (name != Path.GetFileName(name)) return false;
            string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant().Replace('¹', '1').Replace('²', '2').Replace('³', '3');
            return !(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }).Contains(stem);
        }
        public static string FileList(IEnumerable<Attachment> files)
        {
            return String.Join("", files.Select(f => f.Id.ToString(CultureInfo.InvariantCulture) + ":" + f.Name.Replace(":", "::") + ":" + f.Size.ToString("x") + ":" + f.Modified.ToString("x") + ":1:" + (f.Sha256??"") + ":"+(f.RelativePath==null?"":Convert.ToBase64String(StrictUtf8.GetBytes(f.RelativePath)))+":\a"));
        }
        public static bool SafeRelativePath(string value){return value!=null&&value.Length<=MaxRelativePathChars&&value.Split('/').Length<=MaxRelativePathSegments&&value.Split('/').All(SafeFileName);}
        public static List<Attachment> ParseFiles(string text)
        {
            List<Attachment> result = new List<Attachment>();
            foreach (string row in (text ?? "").Split('\a').Take(MaxParsedAttachments))
            {
                string[] f = row.Split(':'); int id; long size, time; uint attr;
                if (f.Length < 5 || !AsciiNumber(f[0], false) || !Int32.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) || !SafeFileName(f[1])) continue;
                if (!AsciiNumber(f[2], true) || !Int64.TryParse(f[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size) || size < 0) continue;
                if (!AsciiNumber(f[3], true) || !Int64.TryParse(f[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out time)) continue;
                if (!AsciiNumber(f[4], true) || !UInt32.TryParse(f[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out attr) || (attr & 255) != 1) continue;
                if (result.Any(x => x.Id == id)) continue;
                string hash=f.Length>5?f[5]:""; if(hash.Length>0&&(hash.Length!=64||!hash.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f')))continue;
                string relative=null;if(f.Length>6&&f[6].Length>0){try{relative=StrictUtf8.GetString(DecodeBase64(f[6]));}catch(FormatException){continue;}catch(DecoderFallbackException){continue;}if(!SafeRelativePath(relative)||relative.Split('/').Last()!=f[1])continue;}
                result.Add(new Attachment { Id = id, Name = f[1], Size = size, Modified = time, Sha256=hash.Length==0?null:hash,RelativePath=relative });
            }
            return result;
        }
    }
}
