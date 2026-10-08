import org.feiqlight.android.core.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;

public final class AndroidInterop {
    private static final String UNICODE=String.join("",Collections.nCopies(400,"𠮷 e\u0301 👩🏽‍💻 👨‍👩‍👧‍👦 🇨🇳 1️⃣ (๑•̀ㅂ•́)و✧\t\r\n"));
    public static void main(String[] args) throws Exception {
        int port=Integer.parseInt(args[0]), windows=Integer.parseInt(args[1]); Path root=Paths.get(args[2]);
        BlockingQueue<LanNode.Message> incoming=new LinkedBlockingQueue<>(), updates=new LinkedBlockingQueue<>();
        LanNode.Listener listener=new LanNode.Listener() {
            public void peersChanged() { }
            public void error(String text) { System.out.println("NOTICE "+text); }
            public void message(LanNode.Peer peer,LanNode.Message message,boolean update) { if(!message.outgoing) incoming.add(message); if(update) updates.add(message); }
        };
        try(LanNode node=new LanNode(port,true,"android-test","JVM-Android-Core","安卓测试😀","互通验证",listener)) {
            node.start(); node.probe(LanNode.endpoint("127.0.0.1:"+windows)); LanNode.Peer peer=null;
            for(int i=0;i<500&&peer==null;i++) { for(LanNode.Peer p:node.peers()) if(p.login.equals("windows-test")) peer=p; Thread.sleep(10); }
            if(peer==null) throw new AssertionError("Windows 发现失败");
            if(!peer.name.equals("Windows 测试👩🏽‍💻")) throw new AssertionError("Windows 昵称乱码");
            for(int i=0;i<5;i++) {
                Thread.sleep(150);long started=System.nanoTime();node.sendMessage(peer,"0",Collections.emptyList());
                LanNode.Message echo=incoming.poll(2,TimeUnit.SECONDS),confirm=updates.poll(2,TimeUnit.SECONDS);
                long elapsed=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started);
                if(echo==null||!echo.text.equals("IDLE_ECHO:0")||confirm==null||!confirm.state.equals("已送达")||elapsed>=1000)throw new AssertionError("空闲单字符往返延迟 "+elapsed+" ms");
                System.out.println("WINDOWS_ZERO_ROUNDTRIP_MS="+elapsed);
            }
            long size=Long.parseLong(args[3]); byte[] bytes=new byte[65536]; for(int i=0;i<bytes.length;i++) bytes[i]=(byte)(i*17+3);
            File source=root.resolve("安卓资料👩🏽‍💻.bin").toFile();
            try(OutputStream out=new BufferedOutputStream(new FileOutputStream(source))) { for(long left=size;left>0;) { int count=(int)Math.min(left,bytes.length);out.write(bytes,0,count);left-=count; } }
            File second=root.resolve("安卓第二份.txt").toFile();Files.write(second.toPath(),"多文件邀请验证 😀".getBytes(java.nio.charset.StandardCharsets.UTF_8));
            node.sendMessage(peer,"安卓→Windows："+UNICODE,Arrays.asList(source,second));
            LanNode.Message ack=updates.poll(8,TimeUnit.SECONDS); if(ack==null||!ack.state.equals("已送达")) throw new AssertionError("Windows ACK 失败");
            LanNode.Message reply=incoming.poll(120,TimeUnit.SECONDS); if(reply==null||!reply.text.equals("Windows→安卓："+UNICODE)||reply.files.size()!=1||!reply.files.get(0).name.equals("Windows资料😀.bin")) throw new AssertionError("Windows 消息或附件解析失败");
            File destination=root.resolve("android-received.bin").toFile();
            long prefix=Math.min(8L*1024*1024,size/2);
            try(OutputStream out=new FileOutputStream(LanNode.partialFile(peer,reply.number,reply.files.get(0),destination))) { for(long left=prefix;left>0;) {int count=(int)Math.min(left,bytes.length);out.write(bytes,0,count);left-=count;} }
            node.receiveFile(peer,reply.number,reply.files.get(0),destination,new LanNode.Transfer(),null);
            if(destination.length()!=size||!Arrays.equals(hash(source),hash(destination))) throw new AssertionError("Windows→Android 文件不一致");
            node.sendMessage(peer,"ANDROID_COMPLETE",Collections.emptyList());
            LanNode.Message done=incoming.poll(10,TimeUnit.SECONDS); if(done==null||!done.text.equals("WINDOWS_COMPLETE")) throw new AssertionError("Android→Windows 文件验证未完成");
            System.out.println("INTEROP PASS: 发现、双向中文/表情/换行、ACK、Android 多附件同条邀请、双向 "+size+" 字节文件非零偏移续传，SHA-256 一致");
        }
    }
    private static byte[] hash(File file)throws Exception {
        java.security.MessageDigest digest=java.security.MessageDigest.getInstance("SHA-256");
        try(InputStream in=new BufferedInputStream(new FileInputStream(file))) { byte[] buffer=new byte[65536];int count;while((count=in.read(buffer))!=-1)digest.update(buffer,0,count); } return digest.digest();
    }
}
