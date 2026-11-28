using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FeiqLight
{
    // Store + Data Descriptor 不被 JDK ZipInputStream 接受；先读 CRC，再定长流式发送。
    internal sealed class StoreZipStream
    {
        internal const int MaxEntries = 1000;
        internal const long Zip32Sentinel = 0xFFFFFFFFL;
        internal const long MaxBytes = Zip32Sentinel - 1;
        internal const string LimitMessage = "内容超过单包 ZIP 上限，请分目录或逐件下载。";

        internal sealed class Entry
        {
            internal readonly string Path;
            internal readonly long Size;
            internal readonly DateTime ModifiedUtc;
            internal readonly Func<Stream> Open;
            // input 为 null 时复核已关闭的来源；否则还可复核当前读取句柄。
            internal readonly Action<Stream> Validate;

            internal Entry(string path, long size, DateTime modifiedUtc, Func<Stream> open, Action<Stream> validate)
            {
                Path = path;
                Size = size;
                ModifiedUtc = modifiedUtc;
                Open = open;
                Validate = validate;
            }
        }

        private sealed class PlannedEntry
        {
            internal Entry Source;
            internal byte[] Name;
            internal long Offset;
            internal ushort Time, Date;
            internal uint Crc;
        }
        private sealed class DirectoryNode
        {
            internal string Name = "";
            internal readonly Dictionary<string,DirectoryNode> Children = new Dictionary<string,DirectoryNode>(StringComparer.Ordinal);
            internal readonly HashSet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly List<PlannedEntry> entries = new List<PlannedEntry>();
        private readonly long centralOffset, centralSize;
        private readonly byte[] buffer;
        private static readonly uint[] CrcTable = BuildCrcTable();
        private bool prepared;
        internal long ContentLength { get; private set; }
        internal string[] Paths { get { return entries.Select(entry => Encoding.UTF8.GetString(entry.Name)).ToArray(); } }

        internal StoreZipStream(IEnumerable<Entry> sources)
        {
            var selected = new List<Entry>();
            foreach (Entry source in sources)
            {
                if (selected.Count >= MaxEntries || source.Size < 0 || source.Size >= Zip32Sentinel)
                    throw new InvalidDataException(LimitMessage);
                if (!Protocol.SafeRelativePath(source.Path)) throw new InvalidDataException("文件路径不安全，无法打包。");
                selected.Add(source);
            }
            if (selected.Count == 0) throw new InvalidDataException("本次分享没有可打包的文件。");

            var root = new DirectoryNode();
            // 先预留目录名，避免文件与目录同名使 Windows 解压失败。
            foreach (Entry source in selected)
            {
                string[] parts = source.Path.Split('/');
                string parent = "";
                DirectoryNode node = root;
                for (int index = 0; index < parts.Length - 1; index++)
                {
                    DirectoryNode child;
                    if (!node.Children.TryGetValue(parts[index], out child))
                    {
                        child = new DirectoryNode { Name = Unique(parent, parts[index], true, node.Names) };
                        node.Children.Add(parts[index], child);
                    }
                    parent = Join(parent, child.Name);
                    node = child;
                }
            }

            long offset = 0, directoryBytes = 0;
            foreach (Entry source in selected)
            {
                string[] parts = source.Path.Split('/');
                string parent = "";
                DirectoryNode node = root;
                for (int index = 0; index < parts.Length - 1; index++) { node = node.Children[parts[index]]; parent = Join(parent, node.Name); }
                string name = Join(parent, Unique(parent, parts[parts.Length - 1], false, node.Names));
                byte[] encoded = Encoding.UTF8.GetBytes(name);
                DateTime modified = source.ModifiedUtc.ToLocalTime();
                if (modified.Year < 1980) modified = new DateTime(1980, 1, 1);
                if (modified.Year > 2107) modified = new DateTime(2107, 12, 31, 23, 59, 58);
                entries.Add(new PlannedEntry {
                    Source = source, Name = encoded, Offset = offset,
                    Time = (ushort)((modified.Hour << 11) | (modified.Minute << 5) | (modified.Second / 2)),
                    Date = (ushort)(((modified.Year - 1980) << 9) | (modified.Month << 5) | modified.Day)
                });
                offset += 30L + encoded.Length + source.Size;
                directoryBytes += 46L + encoded.Length;
                if (offset >= Zip32Sentinel) throw new InvalidDataException(LimitMessage);
            }
            centralOffset = offset;
            centralSize = directoryBytes;
            ContentLength = offset + directoryBytes + 22;
            if (ContentLength >= Zip32Sentinel) throw new InvalidDataException(LimitMessage);
            buffer = new byte[262144];
        }

        private static string Join(string parent, string name) { return parent.Length == 0 ? name : parent + "/" + name; }

        private static string Unique(string parent, string name, bool directory, HashSet<string> used)
        {
            string path = Join(parent, name);
            if (Protocol.SafeRelativePath(path) && used.Add(name)) return name;
            string extension = directory ? "" : System.IO.Path.GetExtension(name);
            string stem = name.Substring(0, name.Length - extension.Length);
            for (int suffix = 1; suffix <= MaxEntries * 32; suffix++)
            {
                string shortened = stem;
                string candidate = Join(parent, shortened + " (" + suffix + ")" + extension);
                while (!Protocol.SafeRelativePath(candidate) && shortened.Length > 0)
                {
                    int end = shortened.Length - 1;
                    if (end > 0 && Char.IsLowSurrogate(shortened[end]) && Char.IsHighSurrogate(shortened[end - 1])) end--;
                    shortened = shortened.Substring(0, end);
                    candidate = Join(parent, shortened + " (" + suffix + ")" + extension);
                }
                if (!Protocol.SafeRelativePath(candidate)) throw new InvalidDataException("同名文件避让后路径过长，请分目录下载。");
                string leaf = shortened + " (" + suffix + ")" + extension;
                if (used.Add(leaf)) return leaf;
            }
            throw new InvalidDataException("同名文件过多，无法打包。");
        }

        internal void Prepare(Action check)
        {
            prepared = false;
            foreach (PlannedEntry entry in entries)
            {
                check();
                entry.Source.Validate(null);
                using (Stream input = entry.Source.Open()) entry.Crc = Read(entry, input, check, null);
            }
            ValidateAll(check);
            prepared = true;
        }

        internal void Write(Action<byte[], int> write, Action check)
        {
            if (!prepared) throw new InvalidOperationException("打包前必须完成文件预检。");
            foreach (PlannedEntry entry in entries)
            {
                check();
                using (Stream input = entry.Source.Open())
                {
                    entry.Source.Validate(input);
                    check();
                    byte[] local = Header(entry, false);
                    write(local, local.Length);
                    if (Read(entry, input, check, write) != entry.Crc) throw new IOException("原文件内容已变化，请重新选择。");
                }
            }
            ValidateAll(check);
            foreach (PlannedEntry entry in entries)
            {
                check();
                entry.Source.Validate(null);
                check();
                byte[] central = Header(entry, true);
                write(central, central.Length);
            }
            // 发生异常直接离开，不能在 finally 中补写会让半包变成完整 ZIP 的尾记录。
            ValidateAll(check);
            byte[] end = new byte[22];
            Put32(end, 0, 0x06054b50);
            Put16(end, 8, (ushort)entries.Count);
            Put16(end, 10, (ushort)entries.Count);
            Put32(end, 12, (uint)centralSize);
            Put32(end, 16, (uint)centralOffset);
            check();
            write(end, end.Length);
        }

        private uint Read(PlannedEntry entry, Stream input, Action check, Action<byte[], int> write)
        {
            long left = entry.Source.Size;
            uint crc = UInt32.MaxValue;
            while (left > 0)
            {
                check();
                entry.Source.Validate(input);
                int count = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                if (count == 0) throw new EndOfStreamException("原文件长度已变化，请重新选择。");
                check();
                entry.Source.Validate(input);
                for (int index = 0; index < count; index++) crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[index]) & 255];
                if (write != null) { check(); write(buffer, count); }
                left -= count;
            }
            check();
            entry.Source.Validate(input);
            if (input.ReadByte() != -1) throw new IOException("原文件长度已变化，请重新选择。");
            check();
            entry.Source.Validate(input);
            return crc ^ UInt32.MaxValue;
        }

        private void ValidateAll(Action check)
        {
            foreach (PlannedEntry entry in entries) { check(); entry.Source.Validate(null); }
            check();
        }

        private static byte[] Header(PlannedEntry entry, bool central)
        {
            byte[] header = new byte[(central ? 46 : 30) + entry.Name.Length];
            Put32(header, 0, central ? 0x02014b50U : 0x04034b50U);
            int start = central ? 2 : 0;
            if (central) Put16(header, 4, 20);
            Put16(header, 4 + start, 20);
            Put16(header, 6 + start, 0x0800);
            Put16(header, 10 + start, entry.Time);
            Put16(header, 12 + start, entry.Date);
            Put32(header, 14 + start, entry.Crc);
            Put32(header, 18 + start, (uint)entry.Source.Size);
            Put32(header, 22 + start, (uint)entry.Source.Size);
            Put16(header, 26 + start, (ushort)entry.Name.Length);
            if (central) Put32(header, 42, (uint)entry.Offset);
            Buffer.BlockCopy(entry.Name, 0, header, central ? 46 : 30, entry.Name.Length);
            return header;
        }

        private static void Put16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }
        private static void Put32(byte[] bytes, int offset, uint value)
        {
            for (int index = 0; index < 4; index++) bytes[offset + index] = (byte)(value >> (index * 8));
        }
        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                uint value = index;
                for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320U);
                table[index] = value;
            }
            return table;
        }
    }
}
