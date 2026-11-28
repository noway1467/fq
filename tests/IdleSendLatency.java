import org.feiqlight.android.core.*;
import java.net.*;
import java.util.*;
import java.util.concurrent.*;

/** 独立运行：JDK 11 与 Android 的 receive() 均持有 DatagramSocket 对象监视器。 */
public final class IdleSendLatency {
    private static int port() throws Exception {
        for(int i=0;i<30;i++)try(ServerSocket tcp=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            try(DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),InetAddress.getLoopbackAddress())){return tcp.getLocalPort();}catch(BindException occupied){}
        }
        throw new AssertionError("没有空闲回环端口");
    }
    public static void main(String[] args) throws Exception {
        int wakeDelay=args.length==0?2500:Integer.parseInt(args[0]);
        System.out.println("RUNTIME "+System.getProperty("java.version")+" receiveSynchronized="+java.lang.reflect.Modifier.isSynchronized(DatagramSocket.class.getMethod("receive",DatagramPacket.class).getModifiers()));
        BlockingQueue<String> incoming=new LinkedBlockingQueue<>();
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){}public void error(String s){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(!message.outgoing&&!update)incoming.add(message.text);}};
        ExecutorService sender=Executors.newSingleThreadExecutor(r->new Thread(r,"idle-test-send"));
        ScheduledExecutorService wake=Executors.newSingleThreadScheduledExecutor();boolean passed=false;
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode node=new LanNode(port(),true,"idle-send","test","test","test",listener);LanNode remote=new LanNode(port(),true,"target","test","test","test",listener)) {
            node.setDeviceId("1".repeat(32));remote.setDeviceId("2".repeat(32));
            node.configureSecurity(java.nio.file.Files.createTempDirectory("idle-send-").toFile(),(name,code)->true);remote.configureSecurity(java.nio.file.Files.createTempDirectory("idle-receive-").toFile(),(name,code)->true);
            node.start();remote.start();target.setSoTimeout(1500);
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+remote.port()),"target","test","test","test");peer.deviceId="2".repeat(32);peer.fingerprint=remote.security().fingerprint;node.verifyPeer(peer);
            java.lang.reflect.Field field=LanNode.class.getDeclaredField("udp");field.setAccessible(true);DatagramSocket udp=(DatagramSocket)field.get(node);
            // 故意先让接收线程进入空闲阻塞，不能用发现报文提前把它唤醒。
            Thread.sleep(300);
            ScheduledFuture<?> waker=wake.schedule(()->{try(DatagramSocket socket=new DatagramSocket()){socket.send(new DatagramPacket(new byte[]{0},1,InetAddress.getLoopbackAddress(),udp.getLocalPort()));System.out.println("WAKE incoming datagram after "+wakeDelay+" ms");}catch(Exception e){throw new RuntimeException(e);}},wakeDelay,TimeUnit.MILLISECONDS);
            long start=System.nanoTime();Future<?> sent=sender.submit(()->{try{node.sendMessage(peer,"0",Collections.emptyList());}catch(Exception e){throw new RuntimeException(e);}});
            try {sent.get(700,TimeUnit.MILLISECONDS);}catch(TimeoutException blocked) {
                for(Map.Entry<Thread,StackTraceElement[]> item:Thread.getAllStackTraces().entrySet())if(item.getKey().getName().equals("idle-test-send")||item.getKey().getName().equals("feiq-udp")) {
                    System.out.println("THREAD "+item.getKey().getName()+" "+item.getKey().getState());for(StackTraceElement frame:item.getValue())System.out.println("  "+frame);
                }
                sent.get(wakeDelay+3000,TimeUnit.MILLISECONDS);
            }
            if(!"0".equals(incoming.poll(1,TimeUnit.SECONDS)))throw new AssertionError("TLS 未送达内容");
            long elapsed=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start);
            System.out.println("REAL_TLS_SEND_0_MS="+elapsed);passed=elapsed<1000;
            if(passed)waker.cancel(false);
            System.out.println(passed?"IDLE_SEND_PASS: no inbound packet needed":"IDLE_SEND_FAIL: outgoing waited for inbound packet");
            java.lang.reflect.Method switchNetwork=LanNode.class.getDeclaredMethod("switchNetwork",Object.class);switchNetwork.setAccessible(true);
            start=System.nanoTime();
            Future<?> switched=sender.submit(()->{try{switchNetwork.invoke(node,new Object[]{null});node.refresh();}catch(Exception e){throw new RuntimeException(e);}});
            switched.get(1,TimeUnit.SECONDS);elapsed=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start);
            if(!udp.isClosed()||field.get(node)==udp)throw new AssertionError("切网仍复用旧 UDP socket");
            DatagramPacket packet=new DatagramPacket(new byte[65536],65536);node.probe(new InetSocketAddress(InetAddress.getLoopbackAddress(),target.getLocalPort()));target.receive(packet);
            if(Protocol.parse(Arrays.copyOf(packet.getData(),packet.getLength())).mode()!=Protocol.ENTRY)throw new AssertionError("新 UDP socket 无法发送发现报文");
            byte[] answer=Protocol.encode(1,"discovery-fixture","test",Protocol.ANSWER|Protocol.UTF8,"test","test");
            target.send(new DatagramPacket(answer,answer.length,InetAddress.getLoopbackAddress(),packet.getPort()));
            long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(1);while(node.peers().stream().noneMatch(found->found.login.equals("discovery-fixture")&&found.online)&&System.nanoTime()<until)Thread.sleep(10);
            if(node.peers().stream().noneMatch(found->found.login.equals("discovery-fixture")&&found.online))throw new AssertionError("新 UDP socket 无法接收应答");
            System.out.println("IDLE_NETWORK_SWITCH_MS="+elapsed+"; new socket send/receive PASS");
            // 关闭套接字会解除阻塞接收，不使用强制终止或接触用户局域网。
        } finally {wake.shutdownNow();sender.shutdownNow();wake.awaitTermination(5,TimeUnit.SECONDS);sender.awaitTermination(5,TimeUnit.SECONDS);}
        if(!passed)throw new AssertionError("空闲发送 0 超过 1 秒");
    }
}
