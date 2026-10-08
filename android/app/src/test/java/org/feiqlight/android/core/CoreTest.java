package org.feiqlight.android.core;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;

public final class CoreTest {
    @Test public void outgoingZeroDoesNotWaitForAndroidStyleReceiveMonitor() throws Exception {
        ExecutorService workers=Executors.newFixedThreadPool(2);CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        try(LanNode sender=node(new Inbox(),port(),"idle-sender");DatagramSocket remote=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            java.lang.reflect.Field field=LanNode.class.getDeclaredField("udp");field.setAccessible(true);DatagramSocket socket=(DatagramSocket)field.get(sender);
            // JDK 17 的 DatagramSocket 与 Android 实现不同，固定模拟 Android 接收持锁以免宿主测试漏报。
            workers.submit(()->{synchronized(socket){entered.countDown();try{release.await(5,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}}});
            assertTrue(entered.await(2,TimeUnit.SECONDS));LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),remote.getLocalPort()),"target","host","target","");
            try {
                Future<LanNode.Message> sent=workers.submit(()->sender.sendMessage(peer,"0",Collections.emptyList()));
                LanNode.Message message=sent.get(1,TimeUnit.SECONDS);assertEquals("0",message.text);assertEquals(1,release.getCount());
                remote.setSoTimeout(1000);DatagramPacket packet=new DatagramPacket(new byte[2048],2048);remote.receive(packet);
                assertEquals("0",Protocol.parse(Arrays.copyOf(packet.getData(),packet.getLength())).body);
            } finally {release.countDown();}
        } finally {release.countDown();workers.shutdownNow();assertTrue(workers.awaitTermination(5,TimeUnit.SECONDS));}
    }
    private static final class Inbox implements LanNode.Listener {
        final BlockingQueue<LanNode.Message> messages=new LinkedBlockingQueue<>();
        final BlockingQueue<LanNode.Message> updates=new LinkedBlockingQueue<>();
        final BlockingQueue<String> progress=new LinkedBlockingQueue<>();
        public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state) {progress.add(packet+"/"+file+"/"+percent+"/"+state);}
        public void peersChanged() { }
        public void error(String text) { }
        public void message(LanNode.Peer peer,LanNode.Message message,boolean update) { (update?updates:messages).add(message); }
    }
    private static int port() throws IOException {
        // 节点同时使用 TCP/UDP；只探测 UDP 会偶尔选中已占用的 TCP 端口。
        for(int attempt=0;attempt<20;attempt++)try(ServerSocket tcp=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){try(DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),InetAddress.getLoopbackAddress())){return tcp.getLocalPort();}catch(BindException occupied){/* 重新选择同时空闲的端口。 */}}
        throw new IOException("未找到同时空闲的 TCP/UDP 回环端口");
    }
    private static LanNode node(Inbox inbox,int port,String login) throws IOException {
        LanNode node=new LanNode(port,true,login,"host",login,"测试",inbox); node.start(); return node;
    }
    private static LanNode.Peer waitPeer(LanNode n,String login) throws Exception {
        long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(4);
        while(System.nanoTime()<until) { for(LanNode.Peer p:n.peers()) if(p.login.equals(login)) return p; Thread.sleep(10); }
        throw new AssertionError("未发现联系人 "+login);
    }
    @Test public void utf8AndLegacyRoundTrip() {
        for(int flags:new int[]{Protocol.UTF8,0}) {
            String text=flags==0?"中文:换行\n第二行":"中文 😊:换行\n第二行";
            Protocol.Packet p=Protocol.parse(Protocol.encode(123,"安卓","主机",Protocol.MESSAGE|flags,text,"部门"));
            assertNotNull(p); assertEquals(text,p.body); assertEquals("安卓",p.login); assertEquals("部门",p.extra);
        }
    }
    @Test public void rememberedEndpointsReconnectWithoutBroadcastOrManualProbe() throws Exception {
        int targetPort=port(); InetSocketAddress endpoint=new InetSocketAddress("127.0.0.1",targetPort);
        try(LanNode first=new LanNode(port(),true,"restore-a","host","A","测试",new Inbox())) {
            first.rememberEndpoints(Arrays.asList(endpoint,endpoint,new InetSocketAddress("192.0.2.1",2425)));
            first.start(); assertTrue(first.peers().isEmpty());
            try(LanNode peer=node(new Inbox(),targetPort,"restore-b")) {
                first.refresh(); assertTrue(waitPeer(first,"restore-b").online);
            }
        }
        try(LanNode peer=node(new Inbox(),targetPort,"restore-b"); LanNode next=new LanNode(port(),true,"restore-a","host","A","测试",new Inbox())) {
            next.rememberEndpoints(Collections.singletonList(endpoint)); next.start(); assertTrue(waitPeer(next,"restore-b").online);
        }
    }
    @Test public void rejectsMalformedPackets() {
        for(String wire:new String[]{"","1:a:b:c:32:bad","2:1:b:c:32:bad","1:-1:b:c:32:bad","1:1:b:c:4294967296:bad"}) assertNull(Protocol.parse(wire.getBytes(StandardCharsets.UTF_8)));
        assertNull(Protocol.parse(new byte[60001]));
        try { Protocol.encode(1,"u","h",32,String.join("",Collections.nCopies(60001,"x")),null); fail(); } catch(IllegalArgumentException expected) { }
    }
    @Test public void rejectsDangerousNames() {
        for(String name:new String[]{"../x","..\\x","CON.txt","LPT1","x:","a\0b","x.","x ","","a/b","a\nb"}) assertFalse(name,Protocol.safeName(name));
        assertTrue(Protocol.safeName("项目资料（最终）.zip"));
    }
    @Test public void attachmentRoundTripAndFiltering() {
        List<Protocol.Attachment> files=Protocol.parseFiles("0:文档.txt:1234:20:1:\7"+"1:folder:0:1:2:\7"+"0:重复.txt:0:0:1:\7"+"2:../bad:0:0:1:\7");
        assertEquals(1,files.size()); assertEquals(0x1234,files.get(0).size); assertEquals("文档.txt",Protocol.parseFiles(Protocol.files(files)).get(0).name);
    }
    @Test public void unicodeCorpusExactWireLimitAndLossyEncodingRejected() throws Exception {
        String[] corpus={"ASCII: [] [文件] 内容\t\r\n", "中文𠮷𠀀 (๑•̀ㅂ•́)و✧", "👨‍👩‍👧‍👦 👩🏽‍💻 👍🏿 🇨🇳 1️⃣ ❤️", "e\u0301 العربية עברית \u200f"};
        for(String text:corpus) {
            Protocol.Packet p=Protocol.parse(Protocol.encode(1,"用户😀","主机",Protocol.MESSAGE|Protocol.UTF8,text,null));
            assertNotNull(p); assertEquals(text,p.body); assertEquals("用户😀",p.login);
        }
        int overhead=Protocol.encode(1,"u","h",Protocol.MESSAGE|Protocol.UTF8,"",null).length;
        String text="x".repeat(60000-overhead-4)+"😀";
        assertEquals(text,Protocol.parse(Protocol.encode(1,"u","h",Protocol.MESSAGE|Protocol.UTF8,text,null)).body);
        try { Protocol.encode(1,"u","h",Protocol.MESSAGE|Protocol.UTF8,text+"x",null); fail(); } catch(IllegalArgumentException expected) { }
        for(String invalid:new String[]{"\ud800","\udc00","a\0b"}) try { Protocol.encode(1,"u","h",Protocol.UTF8,invalid,null); fail(); } catch(IllegalArgumentException expected) { }
        try { Protocol.encode(1,"u","h",Protocol.MESSAGE,"😀",null); fail(); } catch(IllegalArgumentException expected) { }
        ByteArrayOutputStream wire=new ByteArrayOutputStream(); wire.write("1:1:u:h:8388640:".getBytes(StandardCharsets.US_ASCII));wire.write(new byte[]{(byte)0xf0,(byte)0x9f,0});
        assertNull(Protocol.parse(wire.toByteArray())); assertEquals("a".repeat(127),Protocol.prefix("a".repeat(127)+"😀",128));
        for(String bad:new String[]{"COM¹.txt","LPT².zip","CON .txt","中".repeat(81)+".txt","bad\ud800.txt"}) assertFalse(bad,Protocol.safeName(bad));
        assertTrue(Protocol.safeName("👩🏽‍💻最终稿.tar.gz"));
    }
    @Test public void emojiIdentitySurvivesDiscoveryAndRefresh() throws Exception {
        try(LanNode a=node(new Inbox(),port(),"a😀");LanNode b=node(new Inbox(),port(),"b👩🏽‍💻")) {
            a.probe(bEndpoint(b)); LanNode.Peer peer=waitPeer(b,"a😀"); assertEquals("a😀",peer.name);
            a.nickname="改名👨‍👩‍👧‍👦"; a.probe(bEndpoint(b));
            long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);while(!peer.name.equals(a.nickname)&&System.nanoTime()<until)Thread.sleep(10);
            assertEquals(a.nickname,peer.name);
        }
    }
    private static InetSocketAddress bEndpoint(LanNode node) throws Exception {
        java.lang.reflect.Field port=LanNode.class.getDeclaredField("port");port.setAccessible(true);
        return new InetSocketAddress(InetAddress.getLoopbackAddress(),port.getInt(node));
    }
    @Test public void fourParallelDownloadsPreserveOpaqueFileFormats() throws Exception {
        Path dir=Files.createTempDirectory("feiq-formats-");ExecutorService workers=Executors.newFixedThreadPool(4);Inbox ia=new Inbox(),ib=new Inbox();
        try(LanNode a=node(ia,port(),"a");LanNode b=node(ib,port(),"b")) {
            a.probe(bEndpoint(b));LanNode.Peer ab=waitPeer(a,"b"),ba=waitPeer(b,"a");
            String[] names={"👩🏽‍💻资料.GIF","压缩.tar.gz","无扩展名","颜文字(｡･ω･｡).webp"};List<File> files=new ArrayList<>();
            for(int i=0;i<names.length;i++) {byte[] bytes=new byte[262144+i];new Random(i).nextBytes(bytes);Path source=dir.resolve(names[i]);Files.write(source,bytes);files.add(source.toFile());}
            a.sendMessage(ab,"",files);LanNode.Message invite=ib.messages.poll(4,TimeUnit.SECONDS);assertNotNull(invite);
            List<Future<?>> futures=new ArrayList<>();
            for(int i=0;i<4;i++) {final int id=i;futures.add(workers.submit(()->{b.receiveFile(ba,invite.number,invite.files.get(id),dir.resolve("received-"+id).toFile(),new LanNode.Transfer(),null);return null;}));}
            for(Future<?> future:futures) future.get(8,TimeUnit.SECONDS);
            for(int i=0;i<4;i++) assertArrayEquals(Files.readAllBytes(files.get(i).toPath()),Files.readAllBytes(dir.resolve("received-"+i)));
        } finally {workers.shutdownNow();try(java.util.stream.Stream<Path> paths=Files.walk(dir)){for(Path p:(Iterable<Path>)paths.sorted(Comparator.reverseOrder())::iterator)Files.delete(p);}}
    }
    @Test public void duplicateDownloadDoesNotTouchActivePartialAndCancelCleansIt() throws Exception {
        Path dir=Files.createTempDirectory("feiq-duplicate-");ExecutorService workers=Executors.newFixedThreadPool(2);CountDownLatch reading=new CountDownLatch(1),release=new CountDownLatch(1);
        try(ServerSocket server=new ServerSocket(0,2,InetAddress.getLoopbackAddress());LanNode receiver=node(new Inbox(),port(),"b")) {
            server.setSoTimeout(5000);Protocol.Attachment file=new Protocol.Attachment(0,"same.bin",200000,1);File target=dir.resolve("same.bin").toFile();
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),server.getLocalPort()),"source","host","source","");LanNode.Transfer transfer=new LanNode.Transfer();
            Future<?> serving=workers.submit(()->{try(Socket socket=server.accept()){requestedOffset(socket);socket.getOutputStream().write(new byte[65536]);release.await(5,TimeUnit.SECONDS);}return null;});
            Future<?> receiving=workers.submit(()->{receiver.receiveFile(peer,7,file,target,transfer,p->{if(p>0)reading.countDown();});return null;});
            assertTrue(reading.await(5,TimeUnit.SECONDS));File part=LanNode.partialFile(peer,7,file,target);long length=part.length();assertTrue(length>0);
            try {receiver.receiveFile(peer,7,file,target,new LanNode.Transfer(),null);fail();}catch(IOException expected){assertTrue(expected.getMessage().contains("同一目标"));}
            assertEquals(length,part.length());transfer.close();release.countDown();
            try {receiving.get(5,TimeUnit.SECONDS);fail();}catch(ExecutionException expected){assertTrue(expected.getCause() instanceof IOException);}
            serving.get(5,TimeUnit.SECONDS);assertFalse(part.exists());assertFalse(target.exists());
        } finally {release.countDown();workers.shutdownNow();try(java.util.stream.Stream<Path> paths=Files.walk(dir)){for(Path p:(Iterable<Path>)paths.sorted(Comparator.reverseOrder())::iterator)Files.delete(p);}}
    }
    @Test public void strictIpv4Endpoint() throws Exception {
        assertEquals(2425,LanNode.endpoint("192.168.1.8").getPort()); assertEquals(32425,LanNode.endpoint("127.0.0.1:32425").getPort());
        for(String s:new String[]{"localhost","1.2.3.256","0.0.0.0","224.0.0.1","1.2.3.4:0","1.2.3.4:65536","1.2.3"}) try { LanNode.endpoint(s); fail(s); } catch(IOException expected) { }
    }
    @Test public void discoveryMessageAckAndExit() throws Exception {
        Inbox ia=new Inbox(), ib=new Inbox(); int pa=port(), pb=port();
        try(LanNode a=node(ia,pa,"a"); LanNode b=node(ib,pb,"b")) {
            a.probe(LanNode.endpoint("127.0.0.1:"+pb)); LanNode.Peer peer=waitPeer(a,"b"); waitPeer(b,"a");
            a.sendMessage(peer,"你好 😊\n换行",Collections.emptyList());
            assertEquals("你好 😊\n换行",ib.messages.poll(4,TimeUnit.SECONDS).text);
            assertEquals("已送达",ia.updates.poll(4,TimeUnit.SECONDS).state);
            b.close(); long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3); while(peer.online&&System.nanoTime()<until) Thread.sleep(10); assertFalse(peer.online);
        }
    }
    @Test public void duplicateMessageIsAckedButDeliveredOnce() throws Exception {
        Inbox inbox=new Inbox(); int p=port();
        try(LanNode n=node(inbox,p,"receiver"); DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            raw.setSoTimeout(3000); byte[] bytes=Protocol.encode(88,"raw","host",Protocol.MESSAGE|Protocol.CHECK|Protocol.UTF8,"一次",null);
            for(int i=0;i<2;i++) { raw.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),p)); byte[] buffer=new byte[1000]; DatagramPacket d=new DatagramPacket(buffer,buffer.length); raw.receive(d); assertEquals(Protocol.ACK,Protocol.parse(Arrays.copyOf(buffer,d.getLength())).mode()); }
            assertNotNull(inbox.messages.poll(2,TimeUnit.SECONDS)); assertNull(inbox.messages.poll(250,TimeUnit.MILLISECONDS));
        }
    }
    @Test public void forgedAckCannotConfirmAndRetriesStop() throws Exception {
        Inbox inbox=new Inbox(); int p=port();
        try(LanNode n=node(inbox,p,"sender"); DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress()); DatagramSocket impostor=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),target.getLocalPort()),"target","host","target","");
            LanNode.Message m=n.sendMessage(peer,"不应确认",Collections.emptyList()); byte[] ack=Protocol.encode(5,"target","host",Protocol.ACK,Long.toString(m.number),null);
            impostor.send(new DatagramPacket(ack,ack.length,InetAddress.getLoopbackAddress(),p)); assertNull(inbox.updates.poll(300,TimeUnit.MILLISECONDS));
            target.setSoTimeout(12000); for(int i=0;i<4;i++) { DatagramPacket d=new DatagramPacket(new byte[1000],1000); target.receive(d); }
            assertTrue(inbox.updates.poll(5,TimeUnit.SECONDS).state.startsWith("未确认送达"));
            target.setSoTimeout(1300); try { target.receive(new DatagramPacket(new byte[1000],1000)); fail("发送超过四次"); } catch(SocketTimeoutException expected) { }
        }
    }
    @Test public void realTcpFilesAndZeroBytes() throws Exception {
        String oldProxy=System.getProperty("socksProxyHost"),oldPort=System.getProperty("socksProxyPort");
        System.setProperty("socksProxyHost","127.0.0.1");System.setProperty("socksProxyPort","9");
        Path dir=Files.createTempDirectory("feiq-java-test-"); Inbox ia=new Inbox(), ib=new Inbox(); int pa=port(),pb=port();
        try(LanNode a=node(ia,pa,"a"); LanNode b=node(ib,pb,"b")) {
            a.probe(LanNode.endpoint("127.0.0.1:"+pb)); LanNode.Peer ab=waitPeer(a,"b"), ba=waitPeer(b,"a");
            for(int size:new int[]{3*1024*1024+7,0}) {
                byte[] payload=new byte[size]; new Random(42).nextBytes(payload); File source=dir.resolve("源"+size+".bin").toFile(); Files.write(source.toPath(),payload);
                a.sendMessage(ab,"文件",Collections.singletonList(source)); LanNode.Message received=ib.messages.poll(3,TimeUnit.SECONDS); File target=dir.resolve("接收"+size+".bin").toFile();
                assertNotNull(ia.updates.poll(3,TimeUnit.SECONDS));assertTrue("ACK 不能成为文件进度",ia.progress.isEmpty());
                b.receiveFile(ba,received.number,received.files.get(0),target,new LanNode.Transfer(),null); assertArrayEquals(payload,Files.readAllBytes(target.toPath()));
                boolean intermediate=false;int last=-1;
                while(true) {
                    String event=ia.progress.poll(3,TimeUnit.SECONDS);assertNotNull("缺少发送进度",event);String[] fields=event.split("/");
                    assertEquals(received.number,Long.parseLong(fields[0]));assertEquals(0,Integer.parseInt(fields[1]));int percent=Integer.parseInt(fields[2]);
                    assertTrue(percent>=last&&percent<=100);last=percent;intermediate|=percent>0&&percent<100;
                    if(fields[3].equals("已发送（非保存确认）")){assertEquals(100,percent);break;}
                }
                if(size>0)assertTrue("大文件只有开始/完成，没有中间进度",intermediate);
                try { b.receiveFile(ba,received.number,received.files.get(0),target,new LanNode.Transfer(),null); fail("覆盖已有文件"); } catch(IOException expected) { }
            }
        } finally {
            if(oldProxy==null)System.clearProperty("socksProxyHost");else System.setProperty("socksProxyHost",oldProxy);
            if(oldPort==null)System.clearProperty("socksProxyPort");else System.setProperty("socksProxyPort",oldPort);
            try(java.util.stream.Stream<Path> paths=Files.walk(dir)) { paths.sorted(Comparator.reverseOrder()).forEach(path->{ try { Files.delete(path); } catch(IOException e) { throw new UncheckedIOException(e); } }); }
        }
    }
    @Test public void changedSourceAndCancellationLeaveNoPartialFile() throws Exception {
        Path dir=Files.createTempDirectory("feiq-java-cancel-"); Inbox ia=new Inbox(),ib=new Inbox(); int pa=port(),pb=port();
        try(LanNode a=node(ia,pa,"a"); LanNode b=node(ib,pb,"b")) {
            a.probe(LanNode.endpoint("127.0.0.1:"+pb)); LanNode.Peer ab=waitPeer(a,"b"),ba=waitPeer(b,"a");
            File source=dir.resolve("source.bin").toFile(); Files.write(source.toPath(),new byte[6000]); a.sendMessage(ab,"",Collections.singletonList(source));
            LanNode.Message m=ib.messages.poll(3,TimeUnit.SECONDS); Files.write(source.toPath(),new byte[1]);
            File target=dir.resolve("target.bin").toFile(); try { b.receiveFile(ba,m.number,m.files.get(0),target,new LanNode.Transfer(),null); fail(); } catch(IOException expected) { }
            Files.write(LanNode.partialFile(ba,m.number,m.files.get(0),target).toPath(),new byte[100]);
            LanNode.Transfer cancelled=new LanNode.Transfer(); cancelled.close(); try { b.receiveFile(ba,m.number,m.files.get(0),target,cancelled,null); fail(); } catch(IOException expected) { }
            assertFalse(target.exists()); assertEquals(1,Objects.requireNonNull(dir.toFile().listFiles()).length);
        } finally { try(java.util.stream.Stream<Path> paths=Files.walk(dir)) { paths.sorted(Comparator.reverseOrder()).forEach(path->{ try { Files.delete(path); } catch(IOException e) { throw new UncheckedIOException(e); } }); } }
    }
    private static long requestedOffset(Socket socket) throws Exception {
        ByteArrayOutputStream wire=new ByteArrayOutputStream(); int b;
        while((b=socket.getInputStream().read())>0) wire.write(b);
        Protocol.Packet request=Protocol.parse(wire.toByteArray());
        return Long.parseLong(request.body.split(":")[2],16);
    }
    @Test public void interruptedDownloadResumesAfterReceiverRestart() throws Exception {
        Path dir=Files.createTempDirectory("feiq-resume-"); byte[] bytes=new byte[1024*1024+7]; new Random(7).nextBytes(bytes);
        ExecutorService worker=Executors.newSingleThreadExecutor();
        try(ServerSocket server=new ServerSocket(0,2,InetAddress.getLoopbackAddress())) {
            server.setSoTimeout(8000);
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),server.getLocalPort()),"sender","host","sender","");
            Protocol.Attachment file=new Protocol.Attachment(0,"large.bin",bytes.length,123); File target=dir.resolve("target.bin").toFile();
            Future<Long> sent=worker.submit(()->{
                try(Socket socket=server.accept()) { assertEquals(0,requestedOffset(socket)); socket.getOutputStream().write(bytes,0,80000); }
                try(Socket socket=server.accept()) { long offset=requestedOffset(socket); socket.getOutputStream().write(bytes,(int)offset,bytes.length-(int)offset); return offset; }
            });
            try(LanNode receiver=node(new Inbox(),port(),"b")) {
                try { receiver.receiveFile(peer,77,file,target,new LanNode.Transfer(),null); fail(); } catch(IOException expected) { }
            }
            File part=LanNode.partialFile(peer,77,file,target); assertEquals(80000,part.length()); assertFalse(target.exists());
            assertNotEquals(part,LanNode.partialFile(peer,78,file,target));
            List<Integer> progress=new ArrayList<>();
            try(LanNode receiver=node(new Inbox(),port(),"b")) { receiver.receiveFile(peer,77,file,target,new LanNode.Transfer(),progress::add); }
            assertEquals(80000L,(long)sent.get(8,TimeUnit.SECONDS)); assertTrue(progress.get(0)>0);
            assertArrayEquals(bytes,Files.readAllBytes(target.toPath())); assertFalse(part.exists());
        } finally { worker.shutdownNow(); try(java.util.stream.Stream<Path> paths=Files.walk(dir)) { paths.sorted(Comparator.reverseOrder()).forEach(p->{try{Files.delete(p);}catch(IOException e){throw new UncheckedIOException(e);}}); } }
    }
    @Test public void offsetsBeyondFourGiBAreNotTruncated() throws Exception {
        Path dir=Files.createTempDirectory("feiq-large-offset-"); Inbox ia=new Inbox(),ib=new Inbox();
        long offset=4L*1024*1024*1024+19; byte[] tail=new byte[65536]; new Random(9).nextBytes(tail); int pa=port(),pb=port();
        try(LanNode a=node(ia,pa,"a"); LanNode b=node(ib,pb,"b")) {
            File source=dir.resolve("large.bin").toFile();
            try(java.nio.channels.FileChannel out=java.nio.channels.FileChannel.open(source.toPath(),StandardOpenOption.CREATE_NEW,StandardOpenOption.WRITE,StandardOpenOption.SPARSE)) { out.position(offset); out.write(java.nio.ByteBuffer.wrap(tail)); }
            a.probe(LanNode.endpoint("127.0.0.1:"+pb));
            LanNode.Peer ab=waitPeer(a,"b"),ba=waitPeer(b,"a"); a.sendMessage(ab,"",Collections.singletonList(source));
            LanNode.Message m=ib.messages.poll(3,TimeUnit.SECONDS); assertNotNull(m); assertEquals(offset+tail.length,m.files.get(0).size);
            for(long invalid:new long[]{-1,source.length()+1}) try(Socket socket=new Socket(InetAddress.getLoopbackAddress(),pa)) {
                socket.setSoTimeout(3000); socket.getOutputStream().write(Protocol.encode(1,"b","host",Protocol.GET_FILE,Long.toHexString(m.number)+":0:"+Long.toHexString(invalid)+":",null));
                assertEquals("越界偏移未被拒绝",-1,socket.getInputStream().read());
            }
            File target=dir.resolve("received.bin").toFile(),part=LanNode.partialFile(ba,m.number,m.files.get(0),target);
            try(java.nio.channels.FileChannel out=java.nio.channels.FileChannel.open(part.toPath(),StandardOpenOption.CREATE_NEW,StandardOpenOption.WRITE,StandardOpenOption.SPARSE)) { out.position(offset-1); out.write(java.nio.ByteBuffer.wrap(new byte[1])); }
            b.receiveFile(ba,m.number,m.files.get(0),target,new LanNode.Transfer(),null); assertEquals(source.length(),target.length());
            try(RandomAccessFile in=new RandomAccessFile(target,"r")) { in.seek(offset); byte[] actual=new byte[tail.length]; in.readFully(actual); assertArrayEquals(tail,actual); }
        } finally { try(java.util.stream.Stream<Path> paths=Files.walk(dir)) { paths.sorted(Comparator.reverseOrder()).forEach(p->{try{Files.delete(p);}catch(IOException e){throw new UncheckedIOException(e);}}); } }
    }
    @Test public void loopbackIsolationAndPortConflict() throws Exception {
        int p=port(); try(LanNode a=node(new Inbox(),p,"a")) {
            try { a.probe(LanNode.endpoint("192.168.1.2")); fail(); } catch(IOException expected) { }
            try(LanNode b=new LanNode(p,true,"b","host","b","",new Inbox())) { try { b.start(); fail(); } catch(IOException expected) { } }
        }
    }
}
