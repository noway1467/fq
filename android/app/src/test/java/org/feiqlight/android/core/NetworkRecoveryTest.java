package org.feiqlight.android.core;

import org.junit.Test;
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
        long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
        while(!condition.getAsBoolean() && System.nanoTime()<until)Thread.sleep(10);
        assertTrue(condition.getAsBoolean());
    }
    private static int port()throws Exception {
        try(ServerSocket tcp=new ServerSocket(0,1,InetAddress.getLoopbackAddress());DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),InetAddress.getLoopbackAddress())){return tcp.getLocalPort();}
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
            node.start();announce(raw,port);await(()->!node.peers().isEmpty());LanNode.Peer peer=node.peers().get(0);
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
}
