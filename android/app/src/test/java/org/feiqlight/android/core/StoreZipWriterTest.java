package org.feiqlight.android.core;

import org.junit.Test;
import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.concurrent.atomic.*;
import java.util.zip.*;
import static org.junit.Assert.*;

public final class StoreZipWriterTest {
    private static final long MODIFIED = 1795853350000L;
    private static final String[] NAMES = {
        "资料 中文/子目录/特殊 +&%#😀.txt", "资料 中文/空文件.txt", "README.txt", "readme.TXT",
        "同名", "同名/子目录/文件.txt", "目录/文.txt", "目录/文.txt", "Case/文件.txt", "case/文件.txt"
    };
    private static final byte[][] CONTENTS = {
        "多级中文与特殊符号 😀 +&%#".getBytes(StandardCharsets.UTF_8), new byte[0], "123456789".getBytes(StandardCharsets.US_ASCII),
        "大小写同名".getBytes(StandardCharsets.UTF_8), "同名文件".getBytes(StandardCharsets.UTF_8), "同名目录内文件".getBytes(StandardCharsets.UTF_8),
        new byte[]{0,1,2,3,(byte)255}, new byte[]{4,5,6}, "Case".getBytes(StandardCharsets.UTF_8), "case".getBytes(StandardCharsets.UTF_8)
    };
    private static StoreZipWriter.Entry entry(String path, byte[] bytes) {
        return new StoreZipWriter.Entry(path, bytes.length, MODIFIED, () -> new ByteArrayInputStream(bytes), input -> {});
    }
    private static byte[] encode(StoreZipWriter writer) throws IOException {
        writer.prepare(() -> {});
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        writer.write((bytes, count) -> output.write(bytes, 0, count), () -> {});
        assertEquals(writer.contentLength(), output.size());
        return output.toByteArray();
    }
    private static long number(byte[] bytes, int offset) { return Integer.toUnsignedLong(ByteBuffer.wrap(bytes, offset, 4).order(ByteOrder.LITTLE_ENDIAN).getInt()); }
    private static boolean eocd(byte[] bytes) { return bytes.length >= 22 && number(bytes, bytes.length - 22) == 0x06054b50; }
    private static byte[] sha(byte[] bytes) throws NoSuchAlgorithmException { return MessageDigest.getInstance("SHA-256").digest(bytes); }
    private interface Operation { void run() throws Exception; }
    private static void rejected(Operation action) throws Exception {
        try { action.run(); fail("未拒绝无效或中断归档"); } catch (IOException expected) { assertNotNull(expected.getMessage()); }
    }
    private static void verifyEntry(ZipEntry entry, InputStream input, String name, byte[] bytes) throws Exception {
        assertEquals(name, entry.getName());assertEquals(ZipEntry.STORED, entry.getMethod());
        byte[] decoded = input.readAllBytes();
        assertArrayEquals(sha(bytes), sha(decoded));assertEquals(bytes.length, entry.getSize());
        CRC32 crc = new CRC32();crc.update(decoded);assertEquals(crc.getValue(), entry.getCrc());
    }
    @Test public void storeUtf8PathsCrcAndShaWorkWithZipFileAndZipInputStream() throws Exception {
        List<StoreZipWriter.Entry> entries = new ArrayList<>();
        for (int index = 0; index < NAMES.length; index++) entries.add(entry(NAMES[index], CONTENTS[index]));
        StoreZipWriter writer = new StoreZipWriter(entries);byte[] bytes = encode(writer);assertTrue(eocd(bytes));
        assertTrue(writer.paths().contains("readme (1).TXT"));assertTrue(writer.paths().contains("同名 (1)"));assertTrue(writer.paths().contains("case (1)/文件.txt"));
        Set<String> unique = new TreeSet<>(String.CASE_INSENSITIVE_ORDER);unique.addAll(writer.paths());assertEquals(writer.paths().size(), unique.size());
        for (String path : writer.paths()) {
            assertTrue(Protocol.safeRelativePath(path));
            for (String other : writer.paths()) assertFalse(other.regionMatches(true,0,path+"/",0,path.length()+1));
        }
        try (ZipInputStream input = new ZipInputStream(new ByteArrayInputStream(bytes))) {
            for (int index = 0; index < CONTENTS.length; index++) verifyEntry(input.getNextEntry(), input, writer.paths().get(index), CONTENTS[index]);
            assertNull(input.getNextEntry());
        }
        Path archive = Files.createTempFile(Path.of(System.getProperty("java.io.tmpdir")), "store-zip-", ".zip");
        try {
            Files.write(archive, bytes);
            try (ZipFile zip = new ZipFile(archive.toFile())) {
                assertEquals(CONTENTS.length,zip.size());Enumeration<? extends ZipEntry> files=zip.entries();int index=0;
                while(files.hasMoreElements()) { ZipEntry item=files.nextElement();try(InputStream input=zip.getInputStream(item)){verifyEntry(item,input,writer.paths().get(index),CONTENTS[index]);}index++; }
            }
        } finally { Files.delete(archive); }
        int offset=0;
        for(int index=0;index<CONTENTS.length;index++) {
            assertEquals(0x04034b50,number(bytes,offset));assertEquals(0x0800,Short.toUnsignedInt(ByteBuffer.wrap(bytes,offset+6,2).order(ByteOrder.LITTLE_ENDIAN).getShort()));
            assertEquals(0,Short.toUnsignedInt(ByteBuffer.wrap(bytes,offset+8,2).order(ByteOrder.LITTLE_ENDIAN).getShort()));
            CRC32 crc=new CRC32();crc.update(CONTENTS[index]);assertEquals(crc.getValue(),number(bytes,offset+14));
            int nameLength=Short.toUnsignedInt(ByteBuffer.wrap(bytes,offset+26,2).order(ByteOrder.LITTLE_ENDIAN).getShort());offset+=30+nameLength+CONTENTS[index].length;
        }
        assertEquals(offset,number(bytes,bytes.length-6));
        CRC32 known=new CRC32();known.update("123456789".getBytes(StandardCharsets.US_ASCII));assertEquals(0xcbf43926L,known.getValue());
    }
    @Test public void limitsAndUnsafePathsAreRejectedWithoutOpeningSources() throws Exception {
        AtomicInteger opens = new AtomicInteger();
        java.util.function.LongFunction<StoreZipWriter.Entry> sized = size -> new StoreZipWriter.Entry("a",size,MODIFIED,()->{opens.incrementAndGet();throw new IOException("不许打开越界来源");},input->{});
        rejected(()->new StoreZipWriter(Collections.emptyList()));
        for(long size:new long[]{-1,StoreZipWriter.ZIP32_SENTINEL,Long.MAX_VALUE,StoreZipWriter.ZIP32_SENTINEL-31,StoreZipWriter.ZIP32_SENTINEL-100})
            rejected(()->new StoreZipWriter(Collections.singletonList(sized.apply(size))));
        assertEquals(StoreZipWriter.MAX_BYTES,new StoreZipWriter(Collections.singletonList(sized.apply(StoreZipWriter.ZIP32_SENTINEL-101))).contentLength());
        List<StoreZipWriter.Entry> entries=new ArrayList<>();for(int index=0;index<1000;index++)entries.add(entry(index+".txt",new byte[0]));new StoreZipWriter(entries);
        List<StoreZipWriter.Entry> deep=new ArrayList<>();String nested=String.join("/",Collections.nCopies(30,"a".repeat(58)));
        for(int index=0;index<1000;index++)deep.add(entry("r"+index+"/"+nested+"/a.txt",new byte[0]));StoreZipWriter planned=new StoreZipWriter(deep);
        assertEquals(1000,planned.paths().size());for(String path:planned.paths())assertTrue(Protocol.safeRelativePath(path));
        entries.add(entry("extra.txt",new byte[0]));rejected(()->new StoreZipWriter(entries));assertEquals(0,opens.get());
        for(String path:new String[]{"../a","/a","a//b","a\\b","C:/a","CON.txt",String.join("/",Collections.nCopies(33,"a")),"a".repeat(241),"bad\ud800"})
            rejected(()->new StoreZipWriter(Collections.singletonList(entry(path,new byte[0]))));
    }
    @Test public void collisionSuffixKeepsLongNamesWithinSafePathLimits() throws Exception {
        String name="a".repeat(236)+".txt";
        StoreZipWriter writer=new StoreZipWriter(Arrays.asList(entry(name,new byte[0]),entry(name,new byte[0])));
        byte[] bytes=encode(writer);assertTrue(eocd(bytes));assertNotEquals(writer.paths().get(0),writer.paths().get(1));
        for(String path:writer.paths())assertTrue(Protocol.safeRelativePath(path));
        try(ZipInputStream input=new ZipInputStream(new ByteArrayInputStream(bytes))){assertNotNull(input.getNextEntry());input.closeEntry();assertNotNull(input.getNextEntry());input.closeEntry();assertNull(input.getNextEntry());}
    }
    @Test public void sameLengthContentChangeBetweenPassesFailsCrcWithoutEocd() throws Exception {
        AtomicReference<byte[]> bytes=new AtomicReference<>("123456789".getBytes(StandardCharsets.US_ASCII));AtomicInteger opens=new AtomicInteger();
        StoreZipWriter writer=new StoreZipWriter(Collections.singletonList(new StoreZipWriter.Entry("a",9,MODIFIED,()->{opens.incrementAndGet();return new ByteArrayInputStream(bytes.get());},input->{})));
        writer.prepare(()->{});bytes.set("023456789".getBytes(StandardCharsets.US_ASCII));ByteArrayOutputStream output=new ByteArrayOutputStream();
        rejected(()->writer.write((data,count)->output.write(data,0,count),()->{}));assertFalse(eocd(output.toByteArray()));assertEquals(2,opens.get());
    }
    @Test public void shortAndLongSourcesFailBothPreflightAndStreamingWithoutEocd() throws Exception {
        for(int delta:new int[]{-1,1}) {
            AtomicReference<byte[]> bytes=new AtomicReference<>(new byte[8]);
            StoreZipWriter.Entry source=new StoreZipWriter.Entry("a",8,MODIFIED,()->new ByteArrayInputStream(bytes.get()),input->{});
            StoreZipWriter writer=new StoreZipWriter(Collections.singletonList(source));writer.prepare(()->{});bytes.set(new byte[8+delta]);
            ByteArrayOutputStream output=new ByteArrayOutputStream();rejected(()->writer.write((data,count)->output.write(data,0,count),()->{}));assertFalse(eocd(output.toByteArray()));
            rejected(()->new StoreZipWriter(Collections.singletonList(source)).prepare(()->{}));
        }
    }
    @Test public void perBlockSourceExpiryStopAndCentralDirectoryChangesNeverWriteEocd() throws Exception {
        for(String trigger:new String[]{"block","expiry","stop","central","disconnect"}) {
            AtomicBoolean invalid=new AtomicBoolean();
            StoreZipWriter.Validator validate=input->{if(invalid.get()&&!(trigger.equals("expiry")||trigger.equals("stop")))throw new IOException("来源已变化");};
            StoreZipWriter.Check check=()->{if(invalid.get()&&(trigger.equals("expiry")||trigger.equals("stop")))throw new ToolsHttp.Failure(410,"临时传输已结束");};
            StoreZipWriter writer=new StoreZipWriter(Collections.singletonList(new StoreZipWriter.Entry("a.bin",524305,MODIFIED,()->new ByteArrayInputStream(new byte[524305]),validate)));
            writer.prepare(check);ByteArrayOutputStream output=new ByteArrayOutputStream();
            rejected(()->writer.write((bytes,count)->{
                if(trigger.equals("disconnect")&&count==262144)throw new IOException("连接已关闭");output.write(bytes,0,count);
                if((trigger.equals("block")||trigger.equals("expiry")||trigger.equals("stop"))&&count==262144||trigger.equals("central")&&number(bytes,0)==0x02014b50)invalid.set(true);
            },check));assertFalse(trigger,eocd(output.toByteArray()));
        }
    }
    @Test public void sourceReadAndOutputBlocksStayBoundedForBothPasses() throws Exception {
        int length=1024*1024+7;AtomicInteger opens=new AtomicInteger(),maxRead=new AtomicInteger(),maxWrite=new AtomicInteger();AtomicLong read=new AtomicLong(),written=new AtomicLong();
        StoreZipWriter writer=new StoreZipWriter(Collections.singletonList(new StoreZipWriter.Entry("stream.bin",length,MODIFIED,()->{
            opens.incrementAndGet();return new InputStream(){int left=length;
                @Override public int read(){if(left==0)return -1;left--;read.incrementAndGet();return 7;}
                @Override public int read(byte[] bytes,int offset,int count){maxRead.accumulateAndGet(count,Math::max);if(left==0)return -1;int size=Math.min(left,count);Arrays.fill(bytes,offset,offset+size,(byte)7);left-=size;read.addAndGet(size);return size;}
            };
        },input->{})));
        writer.prepare(()->{});writer.write((bytes,count)->{maxWrite.accumulateAndGet(count,Math::max);written.addAndGet(count);},()->{});
        assertEquals(2,opens.get());assertEquals(2L*length,read.get());assertEquals(writer.contentLength(),written.get());assertTrue(maxRead.get()<=262144);assertTrue(maxWrite.get()<=262144);
    }
}
