package org.feiqlight.android.core;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.time.*;
import java.util.*;
import java.util.zip.CRC32;

/** Store 不使用 Data Descriptor，先读取 CRC，兼容 ZipInputStream 且无需临时 ZIP。 */
public final class StoreZipWriter {
    public static final int MAX_ENTRIES = 1000;
    public static final long ZIP32_SENTINEL = 0xFFFFFFFFL;
    public static final long MAX_BYTES = ZIP32_SENTINEL - 1;
    public static final String LIMIT_MESSAGE = "内容超过单包 ZIP 上限，请分目录或逐件下载";

    public interface Opener { InputStream open() throws IOException; }
    public interface Check { void check() throws IOException; }
    public interface Validator { void validate(InputStream input) throws IOException; }
    public interface Sink { void write(byte[] bytes, int length) throws IOException; }

    public static final class Entry {
        public final String path;
        public final long size, modified;
        public final Opener open;
        // input 为 null 时复核已关闭的来源；否则还可复核当前读取句柄。
        public final Validator validate;

        public Entry(String path, long size, long modified, Opener open, Validator validate) {
            this.path = path;
            this.size = size;
            this.modified = modified;
            this.open = open;
            this.validate = validate;
        }
    }

    private static final class PlannedEntry {
        final Entry source;
        final byte[] name;
        final long offset;
        final int time, date;
        long crc;

        PlannedEntry(Entry source, String path, long offset) {
            this.source = source;
            name = path.getBytes(StandardCharsets.UTF_8);
            this.offset = offset;
            LocalDateTime modified = LocalDateTime.ofInstant(Instant.ofEpochMilli(source.modified), ZoneId.systemDefault());
            if (modified.getYear() < 1980) modified = LocalDateTime.of(1980, 1, 1, 0, 0);
            if (modified.getYear() > 2107) modified = LocalDateTime.of(2107, 12, 31, 23, 59, 58);
            time = (modified.getHour() << 11) | (modified.getMinute() << 5) | (modified.getSecond() / 2);
            date = ((modified.getYear() - 1980) << 9) | (modified.getMonthValue() << 5) | modified.getDayOfMonth();
        }
    }
    private static final class DirectoryNode {
        String name = "";
        final Map<String,DirectoryNode> children = new HashMap<>();
        final Set<String> names = new TreeSet<>(String.CASE_INSENSITIVE_ORDER);
    }

    private final List<PlannedEntry> entries = new ArrayList<>();
    private final long centralOffset, centralSize, contentLength;
    private final byte[] buffer;
    private boolean prepared;

    public StoreZipWriter(Iterable<Entry> sources) throws IOException {
        List<Entry> selected = new ArrayList<>();
        for (Entry source : sources) {
            if (selected.size() >= MAX_ENTRIES || source.size < 0 || source.size >= ZIP32_SENTINEL)
                throw new IOException(LIMIT_MESSAGE);
            if (!Protocol.safeRelativePath(source.path)) throw new IOException("文件路径不安全，无法打包");
            selected.add(source);
        }
        if (selected.isEmpty()) throw new IOException("本次分享没有可打包的文件");

        DirectoryNode root = new DirectoryNode();
        // 先预留目录名，避免文件与目录同名使 Windows 解压失败。
        for (Entry source : selected) {
            String[] parts = source.path.split("/");
            String parent = "";
            DirectoryNode node = root;
            for (int index = 0; index < parts.length - 1; index++) {
                DirectoryNode child = node.children.get(parts[index]);
                if (child == null) {
                    child = new DirectoryNode();
                    child.name = unique(parent, parts[index], true, node.names);
                    node.children.put(parts[index], child);
                }
                parent = join(parent, child.name);
                node = child;
            }
        }

        long offset = 0, directoryBytes = 0;
        for (Entry source : selected) {
            String[] parts = source.path.split("/");
            String parent = "";
            DirectoryNode node = root;
            for (int index = 0; index < parts.length - 1; index++) { node = node.children.get(parts[index]); parent = join(parent, node.name); }
            String path = join(parent, unique(parent, parts[parts.length - 1], false, node.names));
            PlannedEntry entry = new PlannedEntry(source, path, offset);
            entries.add(entry);
            offset += 30L + entry.name.length + source.size;
            directoryBytes += 46L + entry.name.length;
            if (offset >= ZIP32_SENTINEL) throw new IOException(LIMIT_MESSAGE);
        }
        centralOffset = offset;
        centralSize = directoryBytes;
        contentLength = offset + directoryBytes + 22;
        if (contentLength >= ZIP32_SENTINEL) throw new IOException(LIMIT_MESSAGE);
        buffer = new byte[262144];
    }

    public long contentLength() { return contentLength; }
    public List<String> paths() {
        List<String> paths = new ArrayList<>();
        for (PlannedEntry entry : entries) paths.add(new String(entry.name, StandardCharsets.UTF_8));
        return Collections.unmodifiableList(paths);
    }

    private static String join(String parent, String name) { return parent.isEmpty() ? name : parent + "/" + name; }

    private static String unique(String parent, String name, boolean directory, Set<String> used) throws IOException {
        String path = join(parent, name);
        if (Protocol.safeRelativePath(path) && used.add(name)) return name;
        int dot = directory ? -1 : name.lastIndexOf('.');
        String extension = dot < 0 ? "" : name.substring(dot);
        String stem = name.substring(0, name.length() - extension.length());
        for (int suffix = 1; suffix <= MAX_ENTRIES * 32; suffix++) {
            String shortened = stem;
            String candidate = join(parent, shortened + " (" + suffix + ")" + extension);
            while (!Protocol.safeRelativePath(candidate) && !shortened.isEmpty()) {
                int end = shortened.length() - 1;
                if (end > 0 && Character.isLowSurrogate(shortened.charAt(end)) && Character.isHighSurrogate(shortened.charAt(end - 1))) end--;
                shortened = shortened.substring(0, end);
                candidate = join(parent, shortened + " (" + suffix + ")" + extension);
            }
            if (!Protocol.safeRelativePath(candidate)) throw new IOException("同名文件避让后路径过长，请分目录下载");
            String leaf = shortened + " (" + suffix + ")" + extension;
            if (used.add(leaf)) return leaf;
        }
        throw new IOException("同名文件过多，无法打包");
    }

    public void prepare(Check check) throws IOException {
        prepared = false;
        for (PlannedEntry entry : entries) {
            check.check();
            entry.source.validate.validate(null);
            try (InputStream input = entry.source.open.open()) { entry.crc = read(entry, input, check, null); }
        }
        validateAll(check);
        prepared = true;
    }

    public void write(Sink output, Check check) throws IOException {
        if (!prepared) throw new IllegalStateException("打包前必须完成文件预检");
        for (PlannedEntry entry : entries) {
            check.check();
            try (InputStream input = entry.source.open.open()) {
                entry.source.validate.validate(input);
                check.check();
                byte[] local = header(entry, false);
                output.write(local, local.length);
                if (read(entry, input, check, output) != entry.crc) throw new IOException("原文件内容已变化，请重新选择");
            }
        }
        validateAll(check);
        for (PlannedEntry entry : entries) {
            check.check();
            entry.source.validate.validate(null);
            check.check();
            byte[] central = header(entry, true);
            output.write(central, central.length);
        }
        // 发生异常直接离开，不能在 finally 中补写会让半包变成完整 ZIP 的尾记录。
        validateAll(check);
        byte[] end = new byte[22];
        put32(end, 0, 0x06054b50);
        put16(end, 8, entries.size());
        put16(end, 10, entries.size());
        put32(end, 12, centralSize);
        put32(end, 16, centralOffset);
        check.check();
        output.write(end, end.length);
    }

    private long read(PlannedEntry entry, InputStream input, Check check, Sink output) throws IOException {
        long left = entry.source.size;
        CRC32 crc = new CRC32();
        while (left > 0) {
            check.check();
            entry.source.validate.validate(input);
            int count = input.read(buffer, 0, (int)Math.min(left, buffer.length));
            if (count <= 0) throw new EOFException("原文件长度已变化，请重新选择");
            check.check();
            entry.source.validate.validate(input);
            crc.update(buffer, 0, count);
            if (output != null) { check.check(); output.write(buffer, count); }
            left -= count;
        }
        check.check();
        entry.source.validate.validate(input);
        if (input.read() != -1) throw new IOException("原文件长度已变化，请重新选择");
        check.check();
        entry.source.validate.validate(input);
        return crc.getValue();
    }

    private void validateAll(Check check) throws IOException {
        for (PlannedEntry entry : entries) { check.check(); entry.source.validate.validate(null); }
        check.check();
    }

    private static byte[] header(PlannedEntry entry, boolean central) {
        byte[] header = new byte[(central ? 46 : 30) + entry.name.length];
        put32(header, 0, central ? 0x02014b50 : 0x04034b50);
        int start = central ? 2 : 0;
        if (central) put16(header, 4, 20);
        put16(header, 4 + start, 20);
        put16(header, 6 + start, 0x0800);
        put16(header, 10 + start, entry.time);
        put16(header, 12 + start, entry.date);
        put32(header, 14 + start, entry.crc);
        put32(header, 18 + start, entry.source.size);
        put32(header, 22 + start, entry.source.size);
        put16(header, 26 + start, entry.name.length);
        if (central) put32(header, 42, entry.offset);
        System.arraycopy(entry.name, 0, header, central ? 46 : 30, entry.name.length);
        return header;
    }

    private static void put16(byte[] bytes, int offset, int value) {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >>> 8);
    }
    private static void put32(byte[] bytes, int offset, long value) {
        for (int index = 0; index < 4; index++) bytes[offset + index] = (byte)(value >>> (index * 8));
    }
}
