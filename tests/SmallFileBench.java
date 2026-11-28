import org.feiqlight.android.core.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;

public final class SmallFileBench {
    private static Path root;
    private static void mark(String name)throws IOException {Files.writeString(root.resolve(name),"ready");}
    private static void waitFor(String name)throws Exception {
        long until=System.nanoTime()+TimeUnit.MINUTES.toNanos(10);
        while(!Files.isRegularFile(root.resolve(name))) {
            if(Files.exists(root.resolve("failed")))throw new IOException("对端基准失败，请查看日志");
            if(System.nanoTime()>=until)throw new IOException("等待基准边界超时："+name);
            Thread.sleep(5);
        }
    }
    @SuppressWarnings("unchecked") private static void waitStored(LanNode node,int count)throws Exception {
        var gateField=LanNode.class.getDeclaredField("gate");gateField.setAccessible(true);
        var offersField=LanNode.class.getDeclaredField("offers");offersField.setAccessible(true);
        java.lang.reflect.Field dirty=null;
        try{dirty=LanNode.class.getDeclaredField("unflushedOfferCompletions");dirty.setAccessible(true);}catch(NoSuchFieldException oldVersion){ }
        long until=System.nanoTime()+TimeUnit.MINUTES.toNanos(2);
        while(true) {
            int stored=0,active=0;
            synchronized(gateField.get(node)) {
                for(Object offer:((Map<String,Object>)offersField.get(node)).values()) {
                    var state=offer.getClass().getDeclaredField("state");state.setAccessible(true);
                    var sockets=offer.getClass().getDeclaredField("activeSockets");sockets.setAccessible(true);
                    if("对方已校验保存".equals(state.get(offer)))stored++;
                    active+=((Set<?>)sockets.get(offer)).size();
                }
            }
            if(SmallFileBenchMetrics.storedCount()==count&&active==0&&(dirty==null||(int)dirty.get(node)==0)) {
                System.out.println("STORED 唯一件数="+SmallFileBenchMetrics.storedCount()+"，当前完成态="+stored);return;
            }
            if(System.nanoTime()>=until)throw new IOException("Android 没收到全部 STORED："+SmallFileBenchMetrics.storedCount()+"/"+count);
            Thread.sleep(5);
        }
    }
    public static void main(String[] args)throws Exception {
        root=Paths.get(args[2]);
        try { run(Integer.parseInt(args[0]),Integer.parseInt(args[1]),Integer.parseInt(args[3]),Integer.parseInt(args[4])); }
        catch(Exception|AssertionError error){error.printStackTrace();mark("failed");System.exit(1);}
    }
    private static void run(int port,int windows,int count,int size)throws Exception {
        BlockingQueue<LanNode.Message> incoming=new LinkedBlockingQueue<>();
        AtomicInteger delivered=new AtomicInteger();
        LanNode.Listener listener=new LanNode.Listener() {
            public void peersChanged() { }
            public void error(String text) {System.err.println("NOTICE "+text);}
            public void message(LanNode.Peer peer,LanNode.Message message,boolean update) {
                if(!message.outgoing&&!message.files.isEmpty())incoming.add(message);
                if(message.outgoing&&update&&"已送达".equals(message.state))delivered.incrementAndGet();
            }
        };
        Path sources=Files.createDirectories(root.resolve("as")),received=Files.createDirectories(root.resolve("ar"));
        List<File> paths=new ArrayList<>();
        for(int index=0;index<count;index++) {
            byte[] bytes=new byte[size];for(int offset=0;offset<size;offset++)bytes[offset]=(byte)(offset*17+index);
            Path path=sources.resolve(String.format(Locale.ROOT,"安卓-%04d.bin",index));Files.write(path,bytes);paths.add(path.toFile());
        }
        try(LanNode node=new LanNode(port,true,"wp5-android","JVM-Android-Core","Android 基准","",listener)) {
            node.setDeviceId("1234567890abcdef1234567890abcdef");
            node.configureSecurity(root.resolve("ak").toFile(),(name,code)->true);
            node.start();node.probe(LanNode.endpoint("127.0.0.1:"+windows));
            LanNode.Peer peer=null;
            for(int attempt=0;attempt<500&&peer==null;attempt++) {
                for(LanNode.Peer candidate:node.peers())if(candidate.endpoint.getPort()==windows)peer=candidate;
                if(peer==null)Thread.sleep(10);
            }
            if(peer==null)throw new AssertionError("Windows 发现失败");
            node.verifyPeer(peer);mark("android-ready");waitFor("windows-offers-ready");
            List<LanNode.Message> messages=new ArrayList<>();incoming.drainTo(messages);
            class Item {long packet;Protocol.Attachment file;Item(long packet,Protocol.Attachment file){this.packet=packet;this.file=file;}}
            List<Item> files=new ArrayList<>();
            for(LanNode.Message message:messages)for(Protocol.Attachment file:message.files)files.add(new Item(message.number,file));
            files.sort(Comparator.comparing(item->item.file.name));
            if(files.size()!=count)throw new AssertionError("Windows 邀请件数错误："+files.size());
            mark("wa-start");waitFor("wa-measuring");SmallFileBenchMetrics.start();
            long started=System.nanoTime();List<Double> latencies=new ArrayList<>();
            for(Item item:files) {
                long timing=System.nanoTime();
                node.receiveFile(peer,item.packet,item.file,received.resolve(item.file.name).toFile(),new LanNode.Transfer(),null);
                latencies.add((System.nanoTime()-timing)/1000000.0);
            }
            double elapsed=(System.nanoTime()-started)/1000000.0;
            mark("wa-received");waitFor("wa-finished");
            SmallFileBenchMetrics.write(root.resolve("wa-android.json").toFile(),elapsed,latencies);
            for(Item item:files)if(Files.size(received.resolve(item.file.name))!=size||!SecureIdentity.hashFile(received.resolve(item.file.name).toFile()).equals(item.file.sha256))throw new AssertionError("Android 内容验证失败");
            try(var entries=Files.list(received)){if(entries.count()!=count)throw new AssertionError("Android 留下重复文件或断点");}
            System.out.println("PASS Windows→Android 只保存一份且无断点残留");

            for(int offset=0;offset<paths.size();offset+=20)node.sendMessage(peer,"小文件基准",paths.subList(offset,Math.min(paths.size(),offset+20)));
            long until=System.nanoTime()+TimeUnit.MINUTES.toNanos(2);
            while(delivered.get()<(count+19)/20) {
                if(System.nanoTime()>=until)throw new AssertionError("Android 邀请未送达");
                Thread.sleep(5);
            }
            Thread.sleep(1500);mark("android-offers-ready");waitFor("aw-start");
            SmallFileBenchMetrics.start();mark("aw-measuring");waitFor("aw-received");waitStored(node,count);
            SmallFileBenchMetrics.write(root.resolve("aw-android.json").toFile(),0,new ArrayList<>());mark("aw-finished");
            waitFor("windows-finished");System.out.println("PASS Android→Windows 全部 SHA-256 / STORED 完成");
        }
    }
}
