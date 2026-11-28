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
    @Test public void legacyDiscoveryPreservesHistoryKeyButCannotEnableBusinessFrames() throws Exception {
        int port=port();Inbox inbox=new Inbox();
        try(DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode local=new LanNode(port,true,"local","host","local","组",inbox)) {
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),raw.getLocalPort()),"old","host","设备","组");
            String key=peer.id();local.rememberPeers(Collections.singletonList(peer));local.start();String id="0123456789abcdef0123456789abcdef";
            java.lang.reflect.Method handle=LanNode.class.getDeclaredMethod("handle",Protocol.Packet.class,InetSocketAddress.class);handle.setAccessible(true);
            handle.invoke(local,Protocol.parse(Protocol.encode(1,"old","host",Protocol.ANSWER|Protocol.UTF8,"设备","组",id)),peer.endpoint);
            handle.invoke(local,Protocol.parse(Protocol.encode(2,"old","host",Protocol.ANSWER|Protocol.UTF8,"设备","组")),peer.endpoint);
            assertEquals(1,local.peers().size());assertSame(peer,local.peers().get(0));assertEquals(key,peer.id());assertEquals(id,peer.deviceId);
            assertTrue(assertThrows(IOException.class,()->local.sendMessage(peer,"消息",Collections.emptyList())).getMessage().contains("升级至 0.8"));
            for(int mode:new int[]{Protocol.MESSAGE,Protocol.ACK,Protocol.RELEASE})handle.invoke(local,Protocol.parse(Protocol.encode(3,"old","host",mode,"1",null)),peer.endpoint);
            assertTrue(inbox.updates.isEmpty());assertTrue(inbox.messages.isEmpty());assertTrue(inbox.canceled.isEmpty());
        }
    }
    @Test public void secureFileRequestCarriesIdentityInsideFrame() throws Exception {
        int port=port();
        try(LanNode node=new LanNode(port,true,"id","host","name","group",new Inbox())) {
            TlsFilePeer.enable(node);node.setDeviceId("0123456789abcdef0123456789abcdef");
            java.lang.reflect.Method encode=LanNode.class.getDeclaredMethod("encode",int.class,String.class,String.class);encode.setAccessible(true);
            byte[] tcp=(byte[])encode.invoke(node,Protocol.GET_FILE,"1:0:0:",null);
            Protocol.Packet request=Protocol.parse(tcp);assertEquals(96,request.mode());assertEquals("0123456789abcdef0123456789abcdef",request.deviceId);assertEquals(node.security().fingerprint,request.fingerprint);
        }
    }
    @Test public void stableIdentitySurvivesEndpointAndLoginChangesWithoutMergingNames() throws Exception {
        String id="0123456789abcdef0123456789abcdef";
        Protocol.Packet decoded=Protocol.parse(Protocol.encode(1,"a","h",Protocol.ENTRY|Protocol.UTF8,"同名","组",id));
        assertEquals(id,decoded.deviceId);assertEquals("组",decoded.extra);assertNull(Protocol.normalizeDeviceId("../invalid"));
        try(DatagramSocket first=new DatagramSocket(0,InetAddress.getLoopbackAddress());DatagramSocket moved=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode local=new LanNode(port(),true,"local","host","local","组",new Inbox())) {
            LanNode.Peer old=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),first.getLocalPort()),"old","host","同名","组");String key=old.id();
            local.rememberPeers(Collections.singletonList(old));local.start();
            java.lang.reflect.Field field=LanNode.class.getDeclaredField("port");field.setAccessible(true);int port=field.getInt(local);
            byte[] bytes=Protocol.encode(2,"old","host",Protocol.ANSWER|Protocol.UTF8,"同名","组",id);first.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),port));
            long end=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);while(old.deviceId==null&&System.nanoTime()<end)Thread.sleep(10);
            assertEquals(id,old.deviceId);assertEquals(key,old.id());
            bytes=Protocol.encode(3,"new","host2",Protocol.ANSWER|Protocol.UTF8,"同名","组",id);moved.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),port));
            assertSame(old,waitPeer(local,"new"));assertEquals(key,old.id());assertEquals(moved.getLocalPort(),old.endpoint.getPort());assertEquals(1,local.peers().size());
            bytes=Protocol.encode(4,"another","host",Protocol.ANSWER|Protocol.UTF8,"同名","组","1123456789abcdef0123456789abcdef");first.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),port));
            assertNotEquals(old.id(),waitPeer(local,"another").id());assertEquals(2,local.peers().size());
        }
    }
    @Test public void retryKeepsPacketAndTimeAndReceiverDeduplicates() throws Exception {
        Inbox ia=new Inbox(),ib=new Inbox();int pa=port(),pb=port();
        try(LanNode a=node(ia,pa,"a");LanNode b=node(ib,pb,"b")) {
            a.probe(new InetSocketAddress("127.0.0.1",pb));LanNode.Peer peer=waitPeer(a,"b");
            LanNode.Message sent=a.sendMessage(peer,"重发仍是一条",Collections.emptyList());
            assertNotNull(ia.updates.poll(4,TimeUnit.SECONDS));assertNotNull(ib.messages.poll(4,TimeUnit.SECONDS));
            sent.state="未确认送达";LanNode.Message retry=a.retryMessage(peer,sent);
            assertEquals(sent.number,retry.number);assertEquals(sent.time,retry.time);
            assertNull(ib.messages.poll(400,TimeUnit.MILLISECONDS));
            assertEquals("等待确认",ia.updates.poll(2,TimeUnit.SECONDS).state);assertEquals("已送达",ia.updates.poll(2,TimeUnit.SECONDS).state);
        }
    }
    @Test public void outgoingZeroDoesNotWaitForAndroidStyleReceiveMonitor() throws Exception {
        ExecutorService workers=Executors.newFixedThreadPool(2);CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        Inbox received=new Inbox();try(LanNode sender=node(new Inbox(),port(),"idle-sender");LanNode remote=node(received,port(),"target")) {
            sender.probe(bEndpoint(remote));LanNode.Peer peer=waitPeer(sender,"target");sender.verifyPeer(peer);
            java.lang.reflect.Field field=LanNode.class.getDeclaredField("udp");field.setAccessible(true);DatagramSocket socket=(DatagramSocket)field.get(sender);
            // JDK 17 的 DatagramSocket 与 Android 实现不同，固定模拟 Android 接收持锁以免宿主测试漏报。
            workers.submit(()->{synchronized(socket){entered.countDown();try{release.await(5,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}}});
            assertTrue(entered.await(2,TimeUnit.SECONDS));
            try {
                Future<LanNode.Message> sent=workers.submit(()->sender.sendMessage(peer,"0",Collections.emptyList()));
                LanNode.Message message=sent.get(1,TimeUnit.SECONDS);assertEquals("0",message.text);assertEquals(1,release.getCount());
                assertEquals("0",received.messages.poll(1,TimeUnit.SECONDS).text);
            } finally {release.countDown();}
        } finally {release.countDown();workers.shutdownNow();assertTrue(workers.awaitTermination(5,TimeUnit.SECONDS));}
    }
    private static final class Inbox implements LanNode.Listener {
        final BlockingQueue<LanNode.Message> messages=new LinkedBlockingQueue<>();
        final BlockingQueue<LanNode.Message> updates=new LinkedBlockingQueue<>();
        final BlockingQueue<String> progress=new LinkedBlockingQueue<>();
        final BlockingQueue<String> canceled=new LinkedBlockingQueue<>();
        public void offerCanceled(LanNode.Peer peer,long packet) {canceled.add(peer.id()+"/"+packet);}
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
        LanNode node=new LanNode(port,true,login,"host",login,"测试",inbox);TlsFilePeer.enable(node);node.start();return node;
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
            TlsFilePeer sender=new TlsFilePeer(server);sender.authorize(receiver);LanNode.Peer peer=sender.peer;LanNode.Transfer transfer=new LanNode.Transfer();file.sha256=SecureIdentity.hash(new byte[200000]);
            Future<?> serving=workers.submit(()->{try(Socket socket=server.accept();Socket tls=sender.open(socket,file)){tls.getOutputStream().write(new byte[65536]);release.await(5,TimeUnit.SECONDS);}return null;});
            Future<?> receiving=workers.submit(()->{receiver.receiveFile(peer,7,file,target,transfer,null);return null;});
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);while(LanNode.partialFile(peer,7,file,target).length()==0&&System.nanoTime()<deadline)Thread.sleep(5);if(LanNode.partialFile(peer,7,file,target).length()>0)reading.countDown();
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
    @Test public void silentPeerGoesOfflineWhileRespondingPeerStaysOnline() throws Exception {
        Inbox inbox=new Inbox(); int pa=port(),pb=port();
        try(LanNode a=node(inbox,pa,"watcher"); LanNode b=node(new Inbox(),pb,"alive"); DatagramSocket silent=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            a.probe(LanNode.endpoint("127.0.0.1:"+pb)); LanNode.Peer alive=waitPeer(a,"alive"); assertTrue(alive.online);
            byte[] entry=Protocol.encode(70,"silent","host",Protocol.ENTRY|Protocol.UTF8,"静默节点",null);
            silent.send(new DatagramPacket(entry,entry.length,InetAddress.getLoopbackAddress(),pa));
            LanNode.Peer ghost=waitPeer(a,"silent"); long idle=a.heartbeatIdle,retry=a.heartbeatRetry; int misses=a.idleMisses;
            try {
                // 只改本次测试的心跳参数：静默节点必须判离线，同一时间里能应答的节点必须保持在线。
                a.heartbeatIdle=TimeUnit.MILLISECONDS.toNanos(100); a.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(50); a.idleMisses=1;
                long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);
                while(ghost.online&&System.nanoTime()<until) Thread.sleep(20);
                assertFalse("静默节点长期假在线",ghost.online);
                assertTrue("能应答的节点被心跳误判离线",alive.online);
            } finally { a.heartbeatIdle=idle; a.heartbeatRetry=retry; a.idleMisses=misses; }
        }
    }
    @Test public void repeatedPlaintextMessagesAreNotDeliveredOrAcknowledged() throws Exception {
        Inbox inbox=new Inbox(); int p=port();
        try(LanNode n=node(inbox,p,"receiver"); DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            raw.setSoTimeout(3000); byte[] bytes=Protocol.encode(88,"raw","host",Protocol.MESSAGE|Protocol.CHECK|Protocol.UTF8,"一次",null);
            for(int i=0;i<2;i++)raw.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),p));
            raw.setSoTimeout(250);assertThrows(SocketTimeoutException.class,()->raw.receive(new DatagramPacket(new byte[1000],1000)));assertTrue(inbox.messages.isEmpty());
        }
    }
    @Test public void plaintextAckCannotConfirmAnOfflineTlsMessageAndRetriesStop() throws Exception {
        Inbox inbox=new Inbox(); int p=port();
        try(LanNode n=node(inbox,p,"sender")) {
            n.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(200);LanNode.Peer peer;
            try(LanNode remote=node(new Inbox(),port(),"target")){n.probe(bEndpoint(remote));peer=waitPeer(n,"target");n.verifyPeer(peer);}
            LanNode.Message m=n.sendMessage(peer,"不应确认",Collections.emptyList());
            var handle=LanNode.class.getDeclaredMethod("handle",Protocol.Packet.class,InetSocketAddress.class);handle.setAccessible(true);
            handle.invoke(n,Protocol.parse(Protocol.encode(5,"target","host",Protocol.ACK,Long.toString(m.number),null)),peer.endpoint);
            assertNull(inbox.updates.poll(300,TimeUnit.MILLISECONDS));assertTrue(inbox.updates.poll(18,TimeUnit.SECONDS).state.startsWith("未确认送达"));assertFalse(n.canCancelOutgoing(peer.id(),m.number));
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
                b.receiveFile(ba,received.number,received.files.get(0),target,new LanNode.Transfer(),percent->{if(percent==0)try{Thread.sleep(450);}catch(InterruptedException interrupted){throw new AssertionError(interrupted);}});assertArrayEquals(payload,Files.readAllBytes(target.toPath()));
                boolean intermediate=false;int last=-1;
                while(true) {
                    String event=ia.progress.poll(3,TimeUnit.SECONDS);assertNotNull("缺少发送进度",event);String[] fields=event.split("/");
                    assertEquals(received.number,Long.parseLong(fields[0]));assertEquals(0,Integer.parseInt(fields[1]));int percent=Integer.parseInt(fields[2]);
                    assertTrue(percent>=last&&percent<=100);last=percent;intermediate|=percent>0&&percent<100;
                    if(fields[3].equals("对方已校验保存")){assertEquals(100,percent);break;}
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
    @Test public void interruptedDownloadResumesAfterReceiverRestart() throws Exception {
        Path dir=Files.createTempDirectory("feiq-resume-"); byte[] bytes=new byte[1024*1024+7]; new Random(7).nextBytes(bytes);
        ExecutorService worker=Executors.newSingleThreadExecutor();
        try(ServerSocket server=new ServerSocket(0,2,InetAddress.getLoopbackAddress())) {
            server.setSoTimeout(8000);
            TlsFilePeer source=new TlsFilePeer(server);LanNode.Peer peer=source.peer;
            Protocol.Attachment file=new Protocol.Attachment(0,"large.bin",bytes.length,123);file.sha256=SecureIdentity.hash(bytes);File target=dir.resolve("target.bin").toFile();
            Future<Long> sent=worker.submit(()->{
                try(Socket socket=server.accept();Socket tls=source.open(socket,file)) { assertEquals(0,source.offset);tls.getOutputStream().write(bytes,0,80000); }
                try(Socket socket=server.accept();Socket tls=source.open(socket,file)) { long offset=source.offset;tls.getOutputStream().write(bytes,(int)offset,bytes.length-(int)offset);assertEquals("STORED\n"+file.sha256,SecureIdentity.readText(tls.getInputStream(),128));return offset; }
            });
            try(LanNode receiver=node(new Inbox(),port(),"b")) {
                source.authorize(receiver);
                try { receiver.receiveFile(peer,77,file,target,new LanNode.Transfer(),null); fail(); } catch(IOException expected) { }
            }
            File part=LanNode.partialFile(peer,77,file,target); assertEquals(80000,part.length()); assertFalse(target.exists());
            assertNotEquals(part,LanNode.partialFile(peer,78,file,target));
            List<Integer> progress=new ArrayList<>();
            try(LanNode receiver=node(new Inbox(),port(),"b")) { source.authorize(receiver);receiver.receiveFile(peer,77,file,target,new LanNode.Transfer(),progress::add); }
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
            try(LanNode b=new LanNode(p,true,"b","host","b","",new Inbox())) {
                b.start();assertFalse("端口被占用时不能假报已连接",b.networkAvailable());assertTrue(b.networkStatus().contains("端口"));
                assertTrue("原节点不应受等待中的节点影响",a.networkAvailable());
            }
        }
    }
    @Test public void offlineOutgoingFileCancelAndUndeliveredRevocationWork() throws Exception {
        Path dir=Files.createTempDirectory("feiq-java-cancel-out-");
        File source=dir.resolve("离线取消.bin").toFile();
        byte[] payload=new byte[2048]; new Random(23).nextBytes(payload); Files.write(source.toPath(),payload);
        Inbox ia=new Inbox(); int pa=port(), pb=port();
        try(LanNode sender=node(ia,pa,"sender")) {
            LanNode.Peer target;
            try(LanNode receiver=node(new Inbox(),pb,"receiver")) {
                sender.probe(LanNode.endpoint("127.0.0.1:"+pb));
                target=waitPeer(sender,"receiver");
            }
            LanNode.Message sent=sender.sendMessage(target,"离线文件",Collections.singletonList(source));
            assertTrue(sender.canCancelOutgoing(target.id(),sent.number));
            assertTrue(sender.hasUndeliveredOutgoing(target.id(),sent.number,sent.state));
            assertTrue(sender.cancelOutgoing(target.id(),sent.number,sent.state,true));
            assertFalse(sender.canCancelOutgoing(target.id(),sent.number));
            assertFalse(sender.offering(target.id(),sent.number,0));
            assertTrue("取消发送不能删除原文件",source.isFile());
            String progress=ia.progress.poll(3,TimeUnit.SECONDS); assertNotNull(progress); assertTrue(progress.endsWith("/已取消发送"));
            LanNode.Message update=ia.updates.poll(3,TimeUnit.SECONDS); assertNotNull(update); assertEquals("已取消发送",update.state);

            LanNode.Message directDel=sender.sendMessage(target,"离线直删",Collections.singletonList(source));
            assertTrue(sender.offering(target.id(),directDel.number,0));
            assertTrue(sender.cancelUndeliveredOutgoing(target.id(),directDel.number,"等待确认",false));
            assertFalse(sender.offering(target.id(),directDel.number,0));

            LanNode.Message peerClear=sender.sendMessage(target,"离线清会话",Collections.singletonList(source));
            assertTrue(sender.offering(target.id(),peerClear.number,0));
            sender.cancelUndeliveredOutgoingForPeer(target.id(),Collections.singleton(peerClear.number));
            assertFalse(sender.offering(target.id(),peerClear.number,0));

            Inbox ib=new Inbox();
            try(LanNode receiver=node(ib,pb,"receiver")) {
                receiver.probe(LanNode.endpoint("127.0.0.1:"+pa));
                LanNode.Peer back=waitPeer(receiver,"sender");
                assertNull("已取消的离线消息不应在对方上线后投递",ib.messages.poll(400,TimeUnit.MILLISECONDS));
                try { receiver.receiveFile(back,sent.number,sent.files.get(0),dir.resolve("不应收到.bin").toFile(),new LanNode.Transfer(),null); fail("已撤销报价仍被拉取"); }
                catch(IOException expected) { }
            }
        } finally { try(java.util.stream.Stream<Path> paths=Files.walk(dir)) { paths.sorted(Comparator.reverseOrder()).forEach(p->{try{Files.delete(p);}catch(IOException e){throw new UncheckedIOException(e);}}); } }
    }
    @Test public void cancelReleaseUsesIdentity() throws Exception {
        Inbox inbox=new Inbox();
        try(LanNode receiver=node(inbox,port(),"receiver")) {
            LanNode.Peer sender=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"sender","host","发送方","");sender.deviceId="1".repeat(32);
            LanNode.Peer other=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"other","host","其他设备","");other.deviceId="2".repeat(32);
            receiver.rememberPeers(Arrays.asList(sender,other));
            java.lang.reflect.Method handle=LanNode.class.getDeclaredMethod("handle",Protocol.Packet.class,InetSocketAddress.class,boolean.class);handle.setAccessible(true);
            for(String body:new String[]{"cancel:","cancel:0","cancel:-1","cancel:+12","cancel: 12","cancel:9223372036854775808"})handle.invoke(receiver,Protocol.parse(Protocol.encode(10,"sender","host",Protocol.RELEASE|Protocol.UTF8,body,null,sender.deviceId)),sender.endpoint,false);
            handle.invoke(receiver,Protocol.parse(Protocol.encode(11,"sender","host",Protocol.RELEASE|Protocol.UTF8,"cancel:12",null,other.deviceId)),sender.endpoint,false);
            handle.invoke(receiver,Protocol.parse(Protocol.encode(12,"other","host",Protocol.RELEASE|Protocol.UTF8,"cancel:12",null,sender.deviceId)),sender.endpoint,false);
            handle.invoke(receiver,Protocol.parse(Protocol.encode(13,"sender","host",Protocol.RELEASE|Protocol.UTF8,"cancel:12",null,sender.deviceId,null,"a".repeat(64))),sender.endpoint,false);
            assertTrue("无效或冒用报文触发了取消",inbox.canceled.isEmpty());
            handle.invoke(receiver,Protocol.parse(Protocol.encode(14,"sender","host",Protocol.RELEASE|Protocol.UTF8,"cancel:12",null,sender.deviceId)),sender.endpoint,false);
            assertTrue("明文取消不能产生状态变化",inbox.canceled.isEmpty());
            sender.fingerprint="b".repeat(64);
            handle.invoke(receiver,Protocol.parse(Protocol.encode(15,"sender","host",Protocol.RELEASE|Protocol.UTF8,"cancel:12",null,sender.deviceId)),sender.endpoint,false);
            assertTrue("明文取消进入了已具备证书的会话",inbox.canceled.isEmpty());
        }
    }
    @Test public void numericReleaseStillRejects() throws Exception {
        Path dir=Files.createTempDirectory("feiq-release-");Inbox out=new Inbox(),in=new Inbox();
        try(LanNode sender=node(out,port(),"sender");LanNode receiver=node(in,port(),"receiver")) {
            File file=dir.resolve("source.bin").toFile();Files.write(file.toPath(),new byte[]{1,2,3});sender.probe(LanNode.endpoint("127.0.0.1:"+receiver.port()));LanNode.Peer target=waitPeer(sender,"receiver"),back=waitPeer(receiver,"sender");
            LanNode.Message sent=sender.sendMessage(target,"拒绝兼容",Collections.singletonList(file));assertNotNull(in.messages.poll(3,TimeUnit.SECONDS));receiver.reject(back,sent.number);
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);while(sender.offering(target.id(),sent.number,0)&&System.nanoTime()<deadline)Thread.sleep(10);
            assertFalse(sender.offering(target.id(),sent.number,0));assertTrue(out.progress.stream().anyMatch(value->value.endsWith("/对方已拒绝")));assertTrue(in.canceled.isEmpty());assertTrue(file.isFile());
            try {Long.parseLong("cancel:"+sent.number);fail("旧版纯数字分支不应解析新取消编码");}catch(NumberFormatException expected){ }
        } finally {Files.deleteIfExists(dir.resolve("source.bin"));Files.delete(dir);}
    }
    @Test public void tlsCompletionClearsActiveStreamAndPlaintextCannotReopenOrCancelIt() throws Exception {
        Path dir=Files.createTempDirectory("feiq-java-active-cancel-");
        File small=dir.resolve("small.bin").toFile(), large=dir.resolve("large.bin").toFile();
        byte[] smallBytes=new byte[1024], largeBytes=new byte[4*1024*1024];
        new Random(31).nextBytes(smallBytes); new Random(37).nextBytes(largeBytes);
        Files.write(small.toPath(),smallBytes); Files.write(large.toPath(),largeBytes);
        Inbox ia=new Inbox(), ib=new Inbox(); int pa=port(), pb=port();
        try(LanNode sender=node(ia,pa,"sender"); LanNode receiver=node(ib,pb,"receiver")) {
            sender.probe(LanNode.endpoint("127.0.0.1:"+pb));
            LanNode.Peer target=waitPeer(sender,"receiver"), back=waitPeer(receiver,"sender");
            LanNode.Message mSmall=sender.sendMessage(target,"小文件",Collections.singletonList(small));
            LanNode.Message gotSmall=ib.messages.poll(3,TimeUnit.SECONDS); assertNotNull(gotSmall);
            assertEquals("已送达",ia.updates.poll(3,TimeUnit.SECONDS).state);
            File smallOut=dir.resolve("small-out.bin").toFile();
            receiver.receiveFile(back,gotSmall.number,gotSmall.files.get(0),smallOut,new LanNode.Transfer(),null);
            assertArrayEquals(smallBytes,Files.readAllBytes(smallOut.toPath()));
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
            while(sender.hasActiveOutgoingTransfer(target.id(),mSmall.number)&&System.nanoTime()<deadline)Thread.sleep(10);
            assertFalse("明文正常发完不应泄漏 activeSockets",sender.hasActiveOutgoingTransfer(target.id(),mSmall.number));
            assertFalse("cancelUndeliveredOutgoing 不能撤销已送达报价",sender.cancelUndeliveredOutgoing(target.id(),mSmall.number,"已送达",false));

            LanNode.Message mLarge=sender.sendMessage(target,"大文件",Collections.singletonList(large));
            LanNode.Message gotLarge=ib.messages.poll(3,TimeUnit.SECONDS); assertNotNull(gotLarge);
            assertEquals("已送达",ia.updates.poll(3,TimeUnit.SECONDS).state);
            ia.progress.clear();
            try(Socket stall=new Socket()) {
                stall.setReceiveBufferSize(4096);
                stall.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(),pa),3000);
                stall.getOutputStream().write(Protocol.encode(77,"receiver","host",Protocol.GET_FILE|Protocol.UTF8,Long.toHexString(mLarge.number)+":0:0:",null));
                try{assertEquals(-1,stall.getInputStream().read());}catch(SocketException reset){assertFalse(stall.isClosed());}
                assertFalse("明文不能打开已经配对的邀请",sender.hasActiveOutgoingTransfer(target.id(),mLarge.number));
                assertTrue(sender.cancelOutgoing(target.id(),mLarge.number,"已送达",true));
                long stopDeadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
                while(sender.hasActiveOutgoingTransfer(target.id(),mLarge.number)&&System.nanoTime()<stopDeadline)Thread.sleep(10);
                assertFalse("取消后活动流未释放",sender.hasActiveOutgoingTransfer(target.id(),mLarge.number));
                Thread.sleep(150);
                List<String> events=new ArrayList<>(); ia.progress.drainTo(events);
                assertTrue("缺少已取消发送进度事件",events.stream().anyMatch(e->e.endsWith("/已取消发送")));
                assertEquals("取消发送状态不应被发送中断覆盖",events.get(events.size()-1).endsWith("/已取消发送"),true);
            }
        } finally { try(java.util.stream.Stream<Path> paths=Files.walk(dir)) { paths.sorted(Comparator.reverseOrder()).forEach(p->{try{Files.delete(p);}catch(IOException e){throw new UncheckedIOException(e);}}); } }
    }
}
