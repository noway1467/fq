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
        public void peersChanged() { }
        public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(update)updates.add(message.state);}
        public void error(String text){errors.add(text);}
    }
    private static void announce(DatagramSocket raw,int port)throws Exception {
        byte[] bytes=Protocol.encode(1,"peer","host",Protocol.ANSWER|Protocol.UTF8,"设备","组");
        raw.send(new DatagramPacket(bytes,bytes.length,InetAddress.getLoopbackAddress(),port));
    }
    @Test public void initialTcpAndUdpBindingFailuresRecoverWithoutRestartOrManualRefresh()throws Exception {
        for(boolean blockTcp:new boolean[]{true,false}) {
            int port=port();Events events=new Events();
            try(ServerSocket tcp=blockTcp?new ServerSocket(port,1,InetAddress.getLoopbackAddress()):null;
                DatagramSocket udp=blockTcp?null:new DatagramSocket(port,InetAddress.getLoopbackAddress());
                LanNode node=new LanNode(port,true,"startup","host","启动恢复","组",events);
                DatagramSocket remote=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
                node.start();assertFalse(node.networkAvailable());assertTrue(node.networkStatus().contains("正在重试"));
                assertTrue("端口错误应标明失败协议："+node.networkStatus(),node.networkStatus().contains(blockTcp?"TCP":"UDP"));
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
            node.start();((ScheduledExecutorService)field(node,"timer")).shutdownNow();
            announce(raw,port);await(()->!node.peers().isEmpty());LanNode.Peer peer=node.peers().get(0);
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
    @Test public void leavingNetworkRevokesOldFileAuthorizationButKeepsSourceFile()throws Exception {
        Events events=new Events();int port=port();Path source=Files.createTempFile("network-offer-",".bin");Files.write(source,new byte[]{1,2,3});
        try(LanNode node=new LanNode(port,true,"local","host","local","组",events);DatagramSocket raw=new DatagramSocket(0,InetAddress.getLoopbackAddress());Socket fileClient=new Socket()) {
            node.start();((ScheduledExecutorService)field(node,"timer")).shutdownNow();announce(raw,port);await(()->!node.peers().isEmpty());
            LanNode.Peer peer=node.peers().get(0);LanNode.Message message=node.sendMessage(peer,"附件",Collections.singletonList(source.toFile()));
            assertTrue(node.offering(peer.id(),message.number,0));
            fileClient.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(),port));fileClient.setSoTimeout(3000);
            @SuppressWarnings("unchecked") Set<Socket> active=(Set<Socket>)field(node,"connections");Object gate=field(node,"gate");
            await(()->{synchronized(gate){return !active.isEmpty();}});
            switchTo(node,null);
            assertFalse(node.networkAvailable());assertFalse(peer.online);assertFalse(node.offering(peer.id(),message.number,0));
            assertEquals("切网应终止旧文件连接",-1,fileClient.getInputStream().read());assertArrayEquals(new byte[]{1,2,3},Files.readAllBytes(source));
            node.refresh();assertTrue(node.networkAvailable());assertFalse("重连不能复活旧地址的文件授权",node.offering(peer.id(),message.number,0));
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
            Protocol.Attachment attachment=new Protocol.Attachment(0,"safe.bin",3,0);
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
