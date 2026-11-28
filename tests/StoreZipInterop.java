import org.feiqlight.android.core.StoreZipWriter;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;
import java.util.zip.*;

public final class StoreZipInterop {
    private static String sha(byte[] bytes) throws Exception {
        byte[] digest=MessageDigest.getInstance("SHA-256").digest(bytes);StringBuilder text=new StringBuilder();
        for(byte value:digest)text.append(String.format(Locale.ROOT,"%02x",value&255));return text.toString();
    }
    private static Map<String,String> manifest(Path path) throws IOException {
        Map<String,String> entries=new LinkedHashMap<>();
        for(String line:Files.readAllLines(path,StandardCharsets.UTF_8)) {
            String[] values=line.split("\t");entries.put(new String(Base64.getDecoder().decode(values[0]),StandardCharsets.UTF_8),values[1]);
        }
        return entries;
    }
    private static void entry(ZipEntry entry,InputStream input,Map<String,String> expected,Set<String> seen) throws Exception {
        if(entry==null||entry.getMethod()!=ZipEntry.STORED||!seen.add(entry.getName()))throw new IOException("Store 方法或路径不符");
        byte[] bytes=input.readAllBytes();CRC32 crc=new CRC32();crc.update(bytes);
        if(entry.getCrc()!=crc.getValue()||entry.getSize()!=bytes.length||!sha(bytes).equals(expected.get(entry.getName())))
            throw new IOException("解压 CRC/长度/SHA-256 不符："+entry.getName());
    }
    private static void verify(Path archive,Path manifest) throws Exception {
        Map<String,String> expected=manifest(manifest);Set<String> seen=new HashSet<>();
        try(ZipFile zip=new ZipFile(archive.toFile())) {
            Enumeration<? extends ZipEntry> files=zip.entries();
            while(files.hasMoreElements()) {ZipEntry item=files.nextElement();try(InputStream input=zip.getInputStream(item)){entry(item,input,expected,seen);}}
        }
        if(!seen.equals(expected.keySet()))throw new IOException("ZipFile 条目不完整");
        System.out.println("PASS "+archive.getFileName()+" -> JDK ZipFile: "+seen.size()+" UTF-8 paths, CRC-32 + SHA-256");
        seen.clear();
        try(ZipInputStream input=new ZipInputStream(Files.newInputStream(archive))) {
            ZipEntry item;while((item=input.getNextEntry())!=null)entry(item,input,expected,seen);
        }
        if(!seen.equals(expected.keySet()))throw new IOException("ZipInputStream 条目不完整");
        System.out.println("PASS "+archive.getFileName()+" -> JDK ZipInputStream: "+seen.size()+" UTF-8 paths, CRC-32 + SHA-256");
    }
    private static void generate(Path directory) throws Exception {
        String[] names={"资料 中文/子目录/特殊 +&%#😀.txt","资料 中文/空文件.txt","README.txt","readme.TXT","同名","同名/子目录/文件.txt","目录/文.txt","目录/文.txt","Case/文件.txt","case/文件.txt"};
        byte[][] contents={"多级中文与特殊符号 😀 +&%#".getBytes(StandardCharsets.UTF_8),new byte[0],"123456789".getBytes(StandardCharsets.US_ASCII),"大小写同名".getBytes(StandardCharsets.UTF_8),"同名文件".getBytes(StandardCharsets.UTF_8),"同名目录内文件".getBytes(StandardCharsets.UTF_8),new byte[]{0,1,2,3,(byte)255},new byte[]{4,5,6},"Case".getBytes(StandardCharsets.UTF_8),"case".getBytes(StandardCharsets.UTF_8)};
        List<StoreZipWriter.Entry> entries=new ArrayList<>();
        for(int index=0;index<names.length;index++){byte[] bytes=contents[index];entries.add(new StoreZipWriter.Entry(names[index],bytes.length,1795853350000L,()->new ByteArrayInputStream(bytes),input->{}));}
        StoreZipWriter writer=new StoreZipWriter(entries);writer.prepare(()->{});
        Path archive=directory.resolve("android-store.zip");
        try(OutputStream output=Files.newOutputStream(archive)){writer.write((bytes,count)->output.write(bytes,0,count),()->{});}
        if(Files.size(archive)!=writer.contentLength())throw new IOException("Content-Length 不符");
        List<String> lines=new ArrayList<>();
        for(int index=0;index<contents.length;index++)lines.add(Base64.getEncoder().encodeToString(writer.paths().get(index).getBytes(StandardCharsets.UTF_8))+"\t"+sha(contents[index]));
        Path manifest=directory.resolve("android-store.tsv");Files.write(manifest,lines,StandardCharsets.UTF_8);verify(archive,manifest);
    }
    public static void main(String[] args) throws Exception {
        verify(Path.of(args[0]),Path.of(args[1]));generate(Path.of(args[2]));
    }
}
