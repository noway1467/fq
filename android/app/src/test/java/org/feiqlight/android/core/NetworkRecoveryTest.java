package org.feiqlight.android.core;

import org.junit.Test;
import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.BooleanSupplier;
import static org.junit.Assert.*;

public final class NetworkRecoveryTest {
    private static Object field(Object target,String name)throws Exception {
        Field f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);
    }
    private static void switchTo(LanNode node,Object key)throws Exception {
        Method method=LanNode.class.getDeclaredMethod("switchNetwork",Object.class);method.setAccessible(true);method.invoke(node,key);
    }
    private static void await(BooleanSupplier condition)throws Exception {
        long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);
        while(!condition.getAsBoolean() && System.nanoTime()<until)Thread.sleep(10);
        assertTrue(condition.getAsBoolean());
    }
    private static int port()throws Exception {
        for(int attempt=0;attempt<32;attempt++) {
            try(ServerSocket tcp=new ServerSocket(0,1,InetAddress.getLoopbackAddress());DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),InetAddress.getLoopbackAddress())){return tcp.getLocalPort();}
            catch(BindException occupied){if(attempt==31)throw occupied;}
        }
        throw new AssertionError("没有可用于 TCP 和 UDP 的回环测试端口");
    }
    private static final class Events implements LanNode.Listener {
        final List<String> errors=new CopyOnWriteArrayList<>(),updates=new CopyOnWriteArrayList<>();
        final List<LanNode.Message> incoming=new CopyOnWriteArrayList<>();
        public void peersChanged() { }
        public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(update)updates.add(message.state);if(!message.outgoing)incoming.add(message);}
        public void error(String text){errors.add(text);}
    }
    private static final class SourceBoundRoute implements LanNode.SocketBinding {
        final InetAddress target=InetAddress.getByName("127.0.0.1"),source=InetAddress.getByName("::1");
        final boolean retry;
        final List<Socket> bound=new CopyOnWriteArrayList<>();
        final java.util.concurrent.atomic.AtomicInteger retryChecks=new java.util.concurrent.atomic.AtomicInteger();
        Runnable beforeRetry=()->{};
        boolean failBinding;
        SourceBoundRoute(boolean retry)throws UnknownHostException{this.retry=retry;}
        public void bind(DatagramSocket socket){ }
        public void bind(Socket socket){fail("出站 TCP 必须提供目标地址");}
        public void bind(Socket socket,InetAddress destination)throws IOException {
            if(!allows(destination))throw new IOException("目标不在受控回环子网内");
            if(failBinding){bound.add(socket);throw new IOException("模拟VPN拒绝源地址绑定");}
            // 两个地址都是回环：IPv6 源绑定后连接 IPv4 会在 connect 阶段失败，隔离宿主网卡和 VPN。
            socket.bind(new InetSocketAddress(source,0));bound.add(socket);
        }
        public boolean allows(InetAddress destination){return destination.equals(target);}
        public boolean shouldRetryWithoutSourceBind(InetAddress destination){retryChecks.incrementAndGet();beforeRetry.run();return retry&&allows(destination);}
        public Collection<InetAddress> broadcasts(){return Collections.emptyList();}
        public void send(DatagramSocket socket,byte[] bytes,InetSocketAddress destination){assertEquals(target,destination.getAddress());}
    }
    private static void discoverTcp(LanNode node,InetSocketAddress endpoint)throws Exception {
        Method method=LanNode.class.getDeclaredMethod("probeTcp",InetSocketAddress.class);method.setAccessible(true);method.invoke(node,endpoint);
    }
    @Test public void newTcpPeerImmediatelySyncsAfterSourceBoundConnectRetryWithoutWaitingForTimer()throws Exception {
        Path directory=Files.createTempDirectory("source-sync-");Events localEvents=new Events(),remoteEvents=new Events();SourceBoundRoute route=new SourceBoundRoute(true);
        String localId="11111111111111111111111111111111",remoteId="22222222222222222222222222222222";
        try(LanNode local=new LanNode(port(),false,"local","phone","手机","",localEvents);
            LanNode remote=new LanNode(port(),true,"remote","pc","电脑","",remoteEvents)) {
            local.setSocketBinding(route);local.setDeviceId(localId);remote.setDeviceId(remoteId);
            local.configureSecurity(directory.resolve("local").toFile(),(name,code)->true);remote.configureSecurity(directory.resolve("remote").toFile(),(name,code)->true);
            assertTrue(local.security().ensureTrusted(remoteId,remote.security().fingerprint,"电脑"));assertTrue(remote.security().ensureTrusted(localId,local.security().fingerprint,"手机"));
            local.start();remote.start();((ScheduledExecutorService)field(local,"timer")).shutdownNow();
            // 手机入站关闭，电脑的消息只保留在待同步队列；没有定时器或 UDP 能替新联系人触发同步。
            ((DatagramSocket)field(local,"udp")).close();((ServerSocket)field(local,"tcp")).close();
            LanNode.Peer localPeer=new LanNode.Peer(new InetSocketAddress(route.target,local.port()),"local","phone","手机","");localPeer.deviceId=localId;localPeer.fingerprint=local.security().fingerprint;
            LanNode.Message queued=remote.sendMessage(localPeer,"新联系人立即同步",Collections.emptyList());
            assertTrue(local.peers().isEmpty());discoverTcp(local,new InetSocketAddress(route.target,remote.port()));
            await(()->localEvents.incoming.stream().anyMatch(message->message.number==queued.number&&message.text.equals("新联系人立即同步")));
            assertEquals(1,local.peers().size());assertEquals("电脑",local.peers().get(0).name);assertTrue(local.peers().get(0).online);
            assertTrue("FQD 与 S 同步没有分别执行连接回退",route.retryChecks.get()>=2);
            for(Socket first:route.bound)assertTrue("源绑定失败的 socket 未关闭",first.isClosed());
            Set<?> probes=(Set<?>)field(local,"probeSockets");await(probes::isEmpty);assertTrue(localEvents.errors.toString(),localEvents.errors.isEmpty());
        }
    }
    @Test public void sourceBoundRetryCompletesTlsVerificationSendingAndSecureFileReceive()throws Exception {
        Path directory=Files.createTempDirectory("source-tls-");Events localEvents=new Events(),remoteEvents=new Events();SourceBoundRoute route=new SourceBoundRoute(true);
        String localId="33333333333333333333333333333333",remoteId="44444444444444444444444444444444";
        byte[] payload=new byte[1024*1024+7];new Random(37).nextBytes(payload);File source=directory.resolve("中文-source.bin").toFile();Files.write(source.toPath(),payload);
        try(LanNode local=new LanNode(port(),false,"local","phone","手机","",localEvents);
            LanNode remote=new LanNode(port(),true,"remote","pc","电脑","",remoteEvents)) {
            local.setSocketBinding(route);local.setDeviceId(localId);remote.setDeviceId(remoteId);
            local.configureSecurity(directory.resolve("local").toFile(),(name,code)->true);remote.configureSecurity(directory.resolve("remote").toFile(),(name,code)->true);
            assertTrue(local.security().ensureTrusted(remoteId,remote.security().fingerprint,"电脑"));assertTrue(remote.security().ensureTrusted(localId,local.security().fingerprint,"手机"));
            local.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(200);local.start();remote.start();discoverTcp(local,new InetSocketAddress(route.target,remote.port()));
            assertEquals(1,local.peers().size());LanNode.Peer remotePeer=local.peers().get(0);local.verifyPeer(remotePeer);
            int before=route.retryChecks.get();LanNode.Message sent=local.sendMessage(remotePeer,"TCP 回退发送文件",Collections.singletonList(source));
            await(()->remoteEvents.incoming.stream().anyMatch(message->message.number==sent.number));assertTrue("安全发送未采用第二次无源地址连接",route.retryChecks.get()>before);
            await(()->!remote.peers().isEmpty());LanNode.Peer localPeer=remote.peers().get(0);
            LanNode.Message offered=remote.sendMessage(localPeer,"下载回退",Collections.singletonList(source));
            await(()->localEvents.incoming.stream().anyMatch(message->message.number==offered.number));
            File destination=directory.resolve("secure-received.bin").toFile();before=route.retryChecks.get();
            try(LanNode.Transfer transfer=new LanNode.Transfer()) {
                local.receiveFile(remotePeer,offered.number,offered.files.get(0),destination,transfer,null);
                assertTrue("安全文件接收未执行受限连接回退",route.retryChecks.get()>before);
                Socket attached=(Socket)field(transfer,"socket");assertNotNull(attached);assertFalse("取消句柄仍指向第一次源绑定 socket",route.bound.contains(attached));assertTrue(attached.isClosed());
            }
            assertArrayEquals(payload,Files.readAllBytes(destination.toPath()));assertEquals(offered.files.get(0).sha256,SecureIdentity.hashFile(destination));
            assertTrue(localEvents.errors.toString(),localEvents.errors.isEmpty());assertTrue(remoteEvents.errors.toString(),remoteEvents.errors.isEmpty());
        }
    }
    @Test public void secureReceiveRetriesOnceReattachesCancellationAndRejectsDisabledOrOutsideRetry()throws Exception {
        Path directory=Files.createTempDirectory("source-file-");byte[] payload={1,2,3,4,5};ExecutorService worker=Executors.newSingleThreadExecutor();
        try {
            for(boolean enabled:new boolean[]{true,false}) {
                SourceBoundRoute route=new SourceBoundRoute(enabled);
                try(ServerSocket server=new ServerSocket(0,1,route.target);LanNode local=new LanNode(port(),false,"local","phone","手机","",new Events());LanNode.Transfer transfer=new LanNode.Transfer()) {
                    local.setSocketBinding(route);TlsFilePeer source=new TlsFilePeer(server);source.authorize(local);LanNode.Peer peer=source.peer;local.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(200);
                    Protocol.Attachment file=new Protocol.Attachment(0,"file.bin",payload.length,1);file.sha256=SecureIdentity.hash(payload);File destination=directory.resolve("received-"+enabled+".bin").toFile();
                    if(enabled) {
                        Future<?> response=worker.submit(()->{try(Socket client=server.accept();Socket tls=source.open(client,file)){tls.getOutputStream().write(payload);assertEquals("STORED\n"+file.sha256,SecureIdentity.readText(tls.getInputStream(),128));}return null;});
                        local.receiveFile(peer,1,file,destination,transfer,null);response.get(3,TimeUnit.SECONDS);
                        assertArrayEquals(payload,Files.readAllBytes(destination.toPath()));Socket attached=(Socket)field(transfer,"socket");assertFalse(route.bound.contains(attached));assertTrue(attached.isClosed());
                    } else {
                        assertThrows(IOException.class,()->local.receiveFile(peer,1,file,destination,transfer,null));server.setSoTimeout(120);assertThrows(SocketTimeoutException.class,server::accept);assertFalse(destination.exists());
                    }
                    assertEquals(1,route.bound.size());assertEquals(1,route.retryChecks.get());assertTrue(route.bound.get(0).isClosed());assertTrue(((Set<?>)field(local,"connections")).isEmpty());
                    LanNode.Peer outside=new LanNode.Peer(new InetSocketAddress("192.168.99.42",server.getLocalPort()),"outside","host","子网外","");
                    outside.deviceId=peer.deviceId;outside.fingerprint=peer.fingerprint;
                    try(LanNode.Transfer denied=new LanNode.Transfer()) {assertThrows(IOException.class,()->local.receiveFile(outside,2,file,directory.resolve("outside-"+enabled+".bin").toFile(),denied,null));}
                    assertEquals("子网外目标不应进入 connect 或默认路由重试",1,route.retryChecks.get());assertEquals(1,route.bound.size());
                }
            }
        } finally {worker.shutdownNow();assertTrue(worker.awaitTermination(3,TimeUnit.SECONDS));}
    }
    @Test public void cancelledOrStoppedSourceBoundTransferNeverCreatesRetryConnection()throws Exception {
        Path directory=Files.createTempDirectory("source-cancel-");
        for(boolean stop:new boolean[]{false,true}) {
            SourceBoundRoute route=new SourceBoundRoute(true);
            try(ServerSocket server=new ServerSocket(0,1,route.target);LanNode local=new LanNode(port(),false,"local","phone","手机","",new Events());LanNode.Transfer transfer=new LanNode.Transfer()) {
                local.setSocketBinding(route);route.beforeRetry=stop?local::stopListening:transfer::close;
                TlsFilePeer source=new TlsFilePeer(server);source.authorize(local);LanNode.Peer peer=source.peer;local.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(200);Protocol.Attachment file=new Protocol.Attachment(0,"cancel.bin",3,1);file.sha256=SecureIdentity.hash(new byte[3]);
                assertThrows(IOException.class,()->local.receiveFile(peer,1,file,directory.resolve("cancel-"+stop+".bin").toFile(),transfer,null));
                server.setSoTimeout(120);assertThrows(SocketTimeoutException.class,server::accept);assertEquals(1,route.bound.size());assertTrue(route.bound.get(0).isClosed());assertTrue(((Set<?>)field(local,"connections")).isEmpty());
            }
        }
    }
    private static void announce(DatagramSocket raw,int port)throws Exception {
        byte[] bytes=Protocol.encode(1,"peer","host",Protocol.ANSWER|Protocol.UTF8,"设备","组");
        raw.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),port));
    }
    @Test public void sourceBindFailureRetriesAndCleans()throws Exception {
        Path directory=Files.createTempDirectory("bind-fail-");byte[] bytes={1,2,3,4};ExecutorService worker=Executors.newSingleThreadExecutor();
        try {
            for(boolean retry:new boolean[]{false,true}) {
                SourceBoundRoute route=new SourceBoundRoute(retry);route.failBinding=true;
                try(ServerSocket server=new ServerSocket(0,1,route.target);LanNode local=new LanNode(port(),false,"local","phone","手机","",new Events());LanNode.Transfer transfer=new LanNode.Transfer()) {
                    local.setSocketBinding(route);TlsFilePeer source=new TlsFilePeer(server);source.authorize(local);LanNode.Peer peer=source.peer;local.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(200);Protocol.Attachment file=new Protocol.Attachment(0,"bind.bin",bytes.length,1);file.sha256=SecureIdentity.hash(bytes);File target=directory.resolve("received-"+retry+".bin").toFile();
                    if(retry) {
                        Future<?> serving=worker.submit(()->{try(Socket socket=server.accept();Socket tls=source.open(socket,file)){tls.getOutputStream().write(bytes);assertEquals("STORED\n"+file.sha256,SecureIdentity.readText(tls.getInputStream(),128));}return null;});
                        local.receiveFile(peer,1,file,target,transfer,null);serving.get(3,TimeUnit.SECONDS);assertArrayEquals(bytes,Files.readAllBytes(target.toPath()));Socket attached=(Socket)field(transfer,"socket");assertFalse("取消句柄仍指向绑定失败的socket",route.bound.contains(attached));assertTrue(attached.isClosed());
                    } else {IOException error=assertThrows(IOException.class,()->local.receiveFile(peer,1,file,target,transfer,null));assertTrue(error.getMessage().contains("源地址绑定"));server.setSoTimeout(120);assertThrows(SocketTimeoutException.class,server::accept);assertFalse(target.exists());}
                    assertEquals(1,route.retryChecks.get());assertEquals(1,route.bound.size());assertTrue(route.bound.get(0).isClosed());assertTrue(((Set<?>)field(local,"connections")).isEmpty());
                    LanNode.Peer outside=new LanNode.Peer(new InetSocketAddress("192.168.99.42",server.getLocalPort()),"outside","host","子网外","");outside.deviceId=peer.deviceId;outside.fingerprint=peer.fingerprint;IOException denied=assertThrows(IOException.class,()->local.receiveFile(outside,2,file,directory.resolve("denied-"+retry).toFile(),transfer,null));assertTrue(denied.getMessage().contains("子网"));assertEquals(1,route.retryChecks.get());assertEquals(1,route.bound.size());assertTrue(((Set<?>)field(local,"connections")).isEmpty());
                }
            }
            for(boolean stop:new boolean[]{false,true}) {
                SourceBoundRoute route=new SourceBoundRoute(true);route.failBinding=true;
                try(ServerSocket server=new ServerSocket(0,1,route.target);LanNode local=new LanNode(port(),false,"local","phone","手机","",new Events());LanNode.Transfer transfer=new LanNode.Transfer()) {
                    local.setSocketBinding(route);route.beforeRetry=stop?local::stopListening:transfer::close;TlsFilePeer source=new TlsFilePeer(server);source.authorize(local);local.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(200);
                    Protocol.Attachment file=new Protocol.Attachment(0,"cancel.bin",1,1);file.sha256=SecureIdentity.hash(new byte[1]);assertThrows(IOException.class,()->local.receiveFile(source.peer,1,file,directory.resolve("cancel-"+stop).toFile(),transfer,null));server.setSoTimeout(120);assertThrows(SocketTimeoutException.class,server::accept);assertEquals(1,route.bound.size());assertTrue(route.bound.get(0).isClosed());assertTrue(((Set<?>)field(local,"connections")).isEmpty());
                }
            }
        }finally{worker.shutdownNow();assertTrue(worker.awaitTermination(3,TimeUnit.SECONDS));}
    }
    @Test public void reverseWaitsAfterSourceBindFailure()throws Exception {
        Path directory=Files.createTempDirectory("bind-reverse-");SourceBoundRoute route=new SourceBoundRoute(false);route.failBinding=true;ExecutorService worker=Executors.newSingleThreadExecutor();
        String localId="55555555555555555555555555555555",remoteId="66666666666666666666666666666666";
        try(LanNode local=new LanNode(port(),false,"local","phone","手机","",new Events());LanNode remote=new LanNode(port(),true,"remote","pc","电脑","",new Events())) {
            local.setSocketBinding(route);local.setDeviceId(localId);remote.setDeviceId(remoteId);local.configureSecurity(directory.resolve("local").toFile(),(name,code)->true);remote.configureSecurity(directory.resolve("remote").toFile(),(name,code)->true);
            assertTrue(local.security().ensureTrusted(remoteId,remote.security().fingerprint,"电脑"));assertTrue(remote.security().ensureTrusted(localId,local.security().fingerprint,"手机"));local.start();remote.start();
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(route.target,remote.port()),"remote","pc","电脑","");peer.deviceId=remoteId;peer.fingerprint=remote.security().fingerprint;
            Future<?> verification=worker.submit(()->{local.verifyPeer(peer);return null;});Object gate=field(local,"gate");Map<?,?> requests=(Map<?,?>)field(local,"reverseRequests");
            await(()->{synchronized(gate){return !requests.isEmpty()&&!route.bound.isEmpty();}});assertFalse("绑定失败后直接结束，未等待认证回连",verification.isDone());String ticket;synchronized(gate){ticket=(String)requests.keySet().iterator().next();}
            Method reverse=LanNode.class.getDeclaredMethod("serveReverse",InetSocketAddress.class,int.class,String.class,String.class,String.class,int.class);reverse.setAccessible(true);
            reverse.invoke(remote,new InetSocketAddress(route.target,local.port()),remote.port(),ticket,localId,local.security().fingerprint,remote.transportEpoch());verification.get(5,TimeUnit.SECONDS);
            Set<?> connections=(Set<?>)field(local,"connections");await(connections::isEmpty);synchronized(gate){assertTrue(requests.isEmpty());}for(Socket failed:route.bound)assertTrue(failed.isClosed());
            LanNode.Peer outside=new LanNode.Peer(new InetSocketAddress("192.168.99.42",remote.port()),"outside","host","子网外","");outside.deviceId=remoteId;outside.fingerprint=remote.security().fingerprint;long before=System.nanoTime();IOException denied=assertThrows(IOException.class,()->local.connectPeer(outside,remote.port()));assertTrue(denied.getMessage().contains("子网"));assertTrue("子网外目标进入8秒回连等待",System.nanoTime()-before<TimeUnit.SECONDS.toNanos(2));synchronized(gate){assertTrue(connections.isEmpty());assertTrue(requests.isEmpty());}
        }finally{worker.shutdownNow();assertTrue(worker.awaitTermination(3,TimeUnit.SECONDS));}
    }
    @Test public void initialTcpAndUdpBindingFailuresRecoverWithoutRestartOrManualRefresh()throws Exception {
        for(boolean blockTcp:new boolean[]{true,false}) {
            int port=port();Events events=new Events();
            try(ServerSocket tcp=blockTcp?new ServerSocket(port,1,InetAddress.getLoopbackAddress()):null;
                DatagramSocket udp=blockTcp?null:new DatagramSocket(port,InetAddress.getLoopbackAddress());
                LanNode node=new LanNode(port,true,"startup","host","启动恢复","组",events);
                DatagramSocket remote=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
                node.start();assertFalse(node.networkAvailable());
                java.util.concurrent.atomic.AtomicReference<String> waiting=new java.util.concurrent.atomic.AtomicReference<>();
                // 自动重试会短暂经过断网状态；协议与重试文案必须来自同一份状态快照。
                await(()->{String state=node.networkStatus();if(state.contains("正在重试"))waiting.compareAndSet(null,state);return waiting.get()!=null;});
                assertTrue("端口错误应标明失败协议："+waiting.get(),waiting.get().contains(blockTcp?"TCP":"UDP"));
                if(tcp!=null)tcp.close();if(udp!=null)udp.close();
                await(node::networkAvailable);
                announce(remote,port);await(()->!node.peers().isEmpty());assertTrue(node.peers().get(0).online);
                assertTrue(events.errors.toString(),events.errors.isEmpty());
            }
        }
    }
    @Test public void closingWhileWaitingForPortCancelsAutomaticRecovery()throws Exception {
        int port=port();Events events=new Events();
        try(ServerSocket occupied=new ServerSocket(port,1,InetAddress.getLoopbackAddress());
            LanNode node=new LanNode(port,true,"cancel-start","host","启动取消","组",events)) {
            node.start();assertFalse(node.networkAvailable());node.close();occupied.close();node.networkChanged();node.refresh();
            assertTrue(((ScheduledExecutorService)field(node,"timer")).isShutdown());
            try(ServerSocket available=new ServerSocket(port,1,InetAddress.getLoopbackAddress());DatagramSocket udp=new DatagramSocket(port,InetAddress.getLoopbackAddress())) {
                assertFalse(node.networkAvailable());
            }
        }
    }
    @Test public void shutdownDuringUdpBindingReleasesPortsBeforeBackgroundCleanup()throws Exception {
        Events events=new Events();int port=port();ExecutorService worker=Executors.newSingleThreadExecutor();
        CountDownLatch binding=new CountDownLatch(1),release=new CountDownLatch(1);
        try(LanNode node=new LanNode(port,false,"closing-bind","host","关闭绑定","",events)) {
            node.setSocketBinding(new LanNode.SocketBinding() {
                public void bind(DatagramSocket socket)throws IOException {
                    binding.countDown();
                    try{if(!release.await(5,TimeUnit.SECONDS))throw new IOException("测试绑定未放行");}
                    catch(InterruptedException error){Thread.currentThread().interrupt();throw new IOException(error);}
                }
                public void bind(Socket socket){fail("测试不能连接真实网卡");}
                public boolean allows(InetAddress address){return address.isLoopbackAddress();}
                public Collection<InetAddress> broadcasts(){return Collections.emptyList();}
            });
            Future<?> startup=worker.submit(()->{node.start();return null;});assertTrue(binding.await(3,TimeUnit.SECONDS));
            DatagramSocket old=(DatagramSocket)field(node,"udp");node.stopListening();
            try(ServerSocket replacement=new ServerSocket(port,1,InetAddress.getLoopbackAddress());
                DatagramSocket udp=new DatagramSocket(port,InetAddress.getLoopbackAddress())) {
                assertEquals("释放端口不能等待旧网络 Binder 返回",1,release.getCount());assertTrue(old.isClosed());
                release.countDown();startup.get(3,TimeUnit.SECONDS);node.close();node.close();
                assertFalse(node.networkAvailable());assertFalse(replacement.isClosed());assertFalse(udp.isClosed());
                assertTrue(((ScheduledExecutorService)field(node,"timer")).isShutdown());
                assertTrue(((ExecutorService)field(node,"fileWorkers")).isShutdown());
                assertTrue(events.errors.toString(),events.errors.isEmpty());
            }
        } finally {release.countDown();worker.shutdownNow();}
    }
    @Test public void stopListeningClosesAcceptedConnectionsBeforeCleanup()throws Exception {
        int port=port();
        try(LanNode node=new LanNode(port,true,"stopping","host","断开测试","",new Events());Socket client=new Socket()) {
            node.start();client.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(),port));client.setSoTimeout(1000);
            client.getOutputStream().write(new byte[]{'F','Q','D','\n'});
            @SuppressWarnings("unchecked") Set<Socket> active=(Set<Socket>)field(node,"connections");Object gate=field(node,"gate");
            await(()->{synchronized(gate){return !active.isEmpty();}});
            node.stopListening();
            try {assertEquals("完整清理排队时旧 TCP 仍可读写",-1,client.getInputStream().read());}
            catch(SocketTimeoutException error){fail("停止监听后已有 TCP 没有关闭");}
            catch(SocketException closed){/* Windows 可用 RST 结束尚有未读数据的连接。 */}
            try(ServerSocket tcp=new ServerSocket(port,1,InetAddress.getLoopbackAddress());DatagramSocket udp=new DatagramSocket(port,InetAddress.getLoopbackAddress())) {
                node.close();assertFalse(tcp.isClosed());assertFalse(udp.isClosed());
            }
        }
    }
    @Test public void tcpDiscoveryCanRestartImmediatelyOnSamePort()throws Exception {
        int port=port();InetAddress loop=InetAddress.getLoopbackAddress();
        try(DatagramSocket remote=new DatagramSocket(0,loop)) {
            for(int i=0;i<3;i++)try(LanNode node=new LanNode(port,true,"restart","host","重开测试","",new Events())) {
                node.start();assertTrue(node.networkStatus(),node.networkAvailable());
                ((ScheduledExecutorService)field(node,"timer")).shutdownNow();
                try(Socket client=new Socket(loop,port)) {
                    client.setSoTimeout(2000);client.getOutputStream().write(new byte[]{'F','Q','D','\n'});
                    ByteArrayOutputStream wire=new ByteArrayOutputStream();
                    wire.write(Protocol.encode(1,"remote","host",Protocol.ENTRY|Protocol.UTF8,"远端",""));
                    wire.write(("FQPORT="+remote.getLocalPort()+"\0").getBytes(java.nio.charset.StandardCharsets.US_ASCII));
                    SecureIdentity.writeFrame(client.getOutputStream(),wire.toByteArray());
                    assertEquals(Protocol.ANSWER,Protocol.parse(SecureIdentity.readFrame(client.getInputStream(),60000)).mode());
                    // 接收服务端先关闭的 FIN，Linux 上会留下占用监听地址的 TIME_WAIT。
                    assertEquals(-1,client.getInputStream().read());
                }
            }
        }
    }
    @Test public void routeReplacementNeverWaitsForOldReceiveMonitorAndRejectsOldOnlineEvidence()throws Exception {
        Events events=new Events();int port=port();ExecutorService worker=Executors.newSingleThreadExecutor();
        CountDownLatch locked=new CountDownLatch(1),release=new CountDownLatch(1);
        try(LanNode node=new LanNode(port,true,"local","host","local","组",events);DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            TlsFilePeer.enable(node);node.start();
            announce(raw,port);await(()->!node.peers().isEmpty());LanNode.Peer peer=node.peers().get(0);
            peer.deviceId="7".repeat(32);peer.fingerprint="c".repeat(64);
            node.sendMessage(peer,"尚未确认",Collections.emptyList());
            DatagramSocket old=(DatagramSocket)field(node,"udp");
            Future<?> holding=worker.submit(()->{synchronized(old){locked.countDown();try{release.await(5,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}}});
            assertTrue(locked.await(2,TimeUnit.SECONDS));
            long before=System.nanoTime();switchTo(node,new Object());
            assertTrue("重建被旧 socket 的接收锁阻塞",System.nanoTime()-before<TimeUnit.SECONDS.toNanos(1));
            assertTrue(old.isClosed());assertNotSame(old,field(node,"udp"));assertFalse(peer.online);
            assertEquals(Collections.singletonList("网络已切换，未确认送达"),events.updates);
            announce(raw,port);await(()->peer.online);assertSame(peer,node.peers().get(0));
            assertTrue(events.errors.toString(),events.errors.isEmpty());release.countDown();holding.get(2,TimeUnit.SECONDS);
        } finally {release.countDown();worker.shutdownNow();}
    }
    @Test public void networkEventsTriggerImmediateDiscoveryAndBoundedFastFollowups()throws Exception {
        Events events=new Events();int port=port();
        try(LanNode node=new LanNode(port,true,"local","host","local","组",events);DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            node.start();raw.setSoTimeout(2500);
            node.rememberEndpoints(Collections.singletonList(new InetSocketAddress(InetAddress.getLoopbackAddress(),raw.getLocalPort())));
            for(int i=0;i<100;i++)node.networkChanged();
            DatagramPacket packet=new DatagramPacket(new byte[2048],2048);raw.receive(packet);
            assertEquals(Protocol.ENTRY,Protocol.parse(Arrays.copyOf(packet.getData(),packet.getLength())).mode());
            // 模拟第一次发现丢包：不回应，仍应在下一秒补探测，不等待原来的 30 秒周期。
            raw.receive(packet);announce(raw,port);await(()->!node.peers().isEmpty());
            assertTrue(node.peers().get(0).online);assertTrue(events.errors.toString(),events.errors.isEmpty());
        }
    }
    @Test public void continuousRefreshCannotKeepSilentPeerOnline()throws Exception {
        Events events=new Events();int port=port();
        try(LanNode node=new LanNode(port,true,"local","host","local","组",events);DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            node.start();announce(raw,port);await(()->!node.peers().isEmpty());LanNode.Peer peer=node.peers().get(0);
            node.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(100);node.verifyMisses=2;
            long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(4);
            while(peer.online && System.nanoTime()<until){node.refresh();Thread.sleep(20);}
            assertFalse("连续刷新一直重置心跳时钟",peer.online);
        }
    }
    @Test public void leavingNetworkCannotRevivePlaintextOffersAndKeepsSourceFile()throws Exception {
        Events events=new Events();int port=port();Path source=Files.createTempFile("network-offer-",".bin");Files.write(source,new byte[]{1,2,3});
        try(LanNode node=new LanNode(port,true,"local","host","local","组",events);DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress());Socket fileClient=new Socket()) {
            node.start();((ScheduledExecutorService)field(node,"timer")).shutdownNow();announce(raw,port);await(()->!node.peers().isEmpty());
            LanNode.Peer peer=node.peers().get(0);assertThrows(IOException.class,()->node.sendMessage(peer,"附件",Collections.singletonList(source.toFile())));
            assertFalse(node.isOffered(source.toFile()));
            fileClient.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(),port));fileClient.setSoTimeout(3000);
            @SuppressWarnings("unchecked") Set<Socket> active=(Set<Socket>)field(node,"connections");Object gate=field(node,"gate");
            await(()->{synchronized(gate){return !active.isEmpty();}});
            switchTo(node,null);
            assertFalse(node.networkAvailable());assertFalse(peer.online);assertFalse(node.isOffered(source.toFile()));
            assertEquals("切网应终止旧文件连接",-1,fileClient.getInputStream().read());assertArrayEquals(new byte[]{1,2,3},Files.readAllBytes(source));
            node.refresh();assertTrue(node.networkAvailable());assertFalse("重连不能复活旧地址的文件授权",node.isOffered(source.toFile()));
        } finally {Files.deleteIfExists(source);}
    }
    @Test public void offlinePeerAutoRecoversWhenReachableWithoutManualRefresh()throws Exception {
        Events events=new Events();int port=port();
        try(LanNode node=new LanNode(port,true,"local","host","local","组",events);DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            node.start();await(node::networkAvailable);announce(raw,port);await(()->!node.peers().isEmpty());LanNode.Peer peer=node.peers().get(0);
            node.heartbeatIdle=TimeUnit.MILLISECONDS.toNanos(80);node.heartbeatRetry=TimeUnit.MILLISECONDS.toNanos(80);node.idleMisses=1;
            await(()->!peer.online);
            raw.setSoTimeout(40);byte[] buf=new byte[2048];
            while(true){try{raw.receive(new DatagramPacket(buf,buf.length));}catch(SocketTimeoutException ignored){break;}}
            raw.setSoTimeout(2500);boolean probed=false;long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(4);
            while(System.nanoTime()<deadline){
                DatagramPacket pkt=new DatagramPacket(buf,buf.length);raw.receive(pkt);
                Protocol.Packet parsed=Protocol.parse(Arrays.copyOf(pkt.getData(),pkt.getLength()));
                if(parsed!=null&&parsed.mode()==Protocol.ENTRY){probed=true;break;}
            }
            assertTrue("离线联系人未在后台心跳周期内自动重探",probed);
            announce(raw,port);await(()->peer.online);
            assertTrue(events.errors.toString(),events.errors.isEmpty());
        }
    }
    @Test public void routePolicyGuardsRealUdpSendAndReceivesTcpDestination()throws Exception {
        Events events=new Events();Path directory=Files.createTempDirectory("route-policy-");
        java.util.concurrent.atomic.AtomicBoolean permitted=new java.util.concurrent.atomic.AtomicBoolean(true);
        java.util.concurrent.atomic.AtomicReference<InetAddress> tcpTarget=new java.util.concurrent.atomic.AtomicReference<>();
        try(LanNode node=new LanNode(port(),false,"policy","host","路由检查","",events);
            DatagramSocket sender=new DatagramSocket(0,InetAddress.getLoopbackAddress());
            DatagramSocket remote=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            node.setSocketBinding(new LanNode.SocketBinding() {
                public void bind(DatagramSocket socket){fail("测试不应扫描或绑定真实网卡");}
                public void bind(Socket socket){fail("TCP 绑定丢失目标地址");}
                public void bind(Socket socket,InetAddress destination)throws IOException {tcpTarget.set(destination);throw new IOException("策略拒绝连接");}
                public boolean allows(InetAddress address){return permitted.get();}
            });
            // 仅注入回环 socket，以执行正式发送路径，同时隔离宿主机网卡与局域网。
            Field udp=LanNode.class.getDeclaredField("udp");udp.setAccessible(true);udp.set(node,sender);
            InetSocketAddress endpoint=new InetSocketAddress(InetAddress.getLoopbackAddress(),remote.getLocalPort());
            node.probe(endpoint);remote.setSoTimeout(1000);
            DatagramPacket packet=new DatagramPacket(new byte[2048],2048);remote.receive(packet);
            assertEquals(Protocol.ENTRY,Protocol.parse(Arrays.copyOf(packet.getData(),packet.getLength())).mode());
            permitted.set(false);
            try {node.probe(endpoint);fail("策略拒绝后仍发送 UDP");}catch(IOException expected){assertTrue(expected.getMessage().contains("局域网"));}
            remote.setSoTimeout(120);
            try {remote.receive(packet);fail("被拒绝的发现包泄露到 socket");}catch(SocketTimeoutException expected){ }
            LanNode.Peer peer=new LanNode.Peer(endpoint,"remote","host","远端","");
            TlsFilePeer.enable(node);peer.deviceId="9".repeat(32);peer.fingerprint="c".repeat(64);
            Protocol.Attachment attachment=new Protocol.Attachment(0,"safe.bin",3,0);attachment.sha256=SecureIdentity.hash(new byte[3]);
            File destination=directory.resolve("received.bin").toFile();
            try(LanNode.Transfer transfer=new LanNode.Transfer()) {
                try {node.receiveFile(peer,1,attachment,destination,transfer,null);fail("拒绝的 TCP 请求仍连接");}
                catch(IOException expected){assertEquals("策略拒绝连接",expected.getMessage());}
            }
            assertEquals(endpoint.getAddress(),tcpTarget.get());
            assertFalse(destination.exists());assertFalse(LanNode.partialFile(peer,1,attachment,destination).exists());
        } finally {Files.deleteIfExists(directory);}
    }
    @Test public void tcpFallbackRecoversAndKeepsPeerOnlineWhenVpnDropsUdp()throws Exception {
        Events ea=new Events(), eb=new Events();
        int pa=port(), pb=port();
        try(LanNode a=new LanNode(pa,true,"alice","host-a","Alice","研发",ea);
            LanNode b=new LanNode(pb,true,"bob","host-b","Bob","测试",eb);
            DatagramSocket dropUdp=new DatagramSocket(new InetSocketAddress(InetAddress.getLoopbackAddress(),0)) {
                @Override public void send(DatagramPacket p) { /* 模拟 VPN 静默拦截或丢弃 UDP 报文 */ }
            }) {
            b.start();
            a.heartbeatIdle=TimeUnit.MILLISECONDS.toNanos(120);
            a.heartbeatRetry=TimeUnit.SECONDS.toNanos(1);
            a.idleMisses=1;
            a.verifyMisses=2;
            InetSocketAddress bEndpoint=new InetSocketAddress(InetAddress.getLoopbackAddress(),pb);
            LanNode.Peer saved=new LanNode.Peer(bEndpoint,"bob","host-b","历史Bob","测试");
            saved.online=false;
            a.rememberPeers(Collections.singletonList(saved));
            a.start();
            Field udpField=LanNode.class.getDeclaredField("udp");udpField.setAccessible(true);
            DatagramSocket realUdp=(DatagramSocket)udpField.get(a);
            udpField.set(a,dropUdp);
            if(realUdp!=null)realUdp.close();
            a.revalidate();
            await(()->saved.online);
            await(()->!b.peers().isEmpty()&&b.peers().get(0).online);
            Thread.sleep(1500);
            assertTrue("UDP 被拦截但 TCP 互通时不应被心跳误判离线",saved.online);
            assertEquals("Bob",saved.name);
            b.close();
            a.revalidate();
            await(()->!saved.online);
            assertTrue(ea.errors.toString(),ea.errors.isEmpty());
        }
    }

    @Test public void blockedBroadcastOnColdStartKeepsManualTcpDiscoveryUntilRouteLoss()throws Exception {
        InetAddress loop=InetAddress.getLoopbackAddress();
        java.util.concurrent.atomic.AtomicBoolean blockUdp=new java.util.concurrent.atomic.AtomicBoolean(true);
        java.util.concurrent.atomic.AtomicBoolean hasLan=new java.util.concurrent.atomic.AtomicBoolean(true);
        LanNode.SocketBinding route=new LanNode.SocketBinding() {
            public Object networkKey(){return hasLan.get()?this:null;}
            public Collection<InetAddress> broadcasts(){return Collections.singletonList(loop);}
            public boolean allows(InetAddress destination){return hasLan.get()&&destination.equals(loop);}
            public void bind(DatagramSocket socket){fail("同一网段的广播失败不应重建监听");}
            public void bind(Socket socket)throws IOException{socket.bind(new InetSocketAddress(loop,0));}
            public void send(DatagramSocket socket,byte[] bytes,InetSocketAddress destination)throws IOException {
                assertEquals(loop,destination.getAddress());
                if(blockUdp.get())throw new SocketException("UDP unavailable");
            }
        };
        try(ServerSocket tcp=new ServerSocket(port(),4,loop);
            DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),loop);
            LanNode local=new LanNode(tcp.getLocalPort(),false,"local","phone","手机","",new Events());
            LanNode remote=new LanNode(port(),true,"remote","pc","电脑","",new Events())) {
            local.setSocketBinding(route);
            for(String name:Arrays.asList("tcp","udp","networkKey")) {
                Field slot=LanNode.class.getDeclaredField(name);slot.setAccessible(true);
                slot.set(local,name.equals("tcp")?tcp:name.equals("udp")?udp:route);
            }
            remote.start();local.start();
            assertTrue("没有历史联系人时，广播失败不应关闭可用的本机监听",local.networkAvailable());
            assertTrue(local.networkStatus().contains("广播"));assertTrue(local.peers().isEmpty());
            local.probe(new InetSocketAddress(loop,remote.port()));
            await(()->!local.peers().isEmpty()&&local.peers().get(0).online);
            assertEquals("电脑",local.peers().get(0).name);
            local.refresh();assertTrue(local.networkStatus().contains("广播"));assertFalse(udp.isClosed());
            blockUdp.set(false);local.refresh();assertEquals("",local.networkStatus());assertSame(udp,field(local,"udp"));
            hasLan.set(false);local.refresh();assertFalse(local.networkAvailable());assertTrue(udp.isClosed());
            assertFalse(local.peers().get(0).online);
        }
    }
}
