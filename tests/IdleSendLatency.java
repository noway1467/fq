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
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){}public void error(String s){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
        ExecutorService sender=Executors.newSingleThreadExecutor(r->new Thread(r,"idle-test-send"));
        ScheduledExecutorService wake=Executors.newSingleThreadScheduledExecutor();boolean passed=false;
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode node=new LanNode(port(),true,"idle-send","test","test","test",listener)) {
            node.start();target.setSoTimeout(1500);
            java.lang.reflect.Field field=LanNode.class.getDeclaredField("udp");field.setAccessible(true);DatagramSocket udp=(DatagramSocket)field.get(node);
            // 故意先让接收线程进入空闲阻塞，不能用发现报文提前把它唤醒。
            Thread.sleep(300);
            ScheduledFuture<?> waker=wake.schedule(()->{try(DatagramSocket socket=new DatagramSocket()){socket.send(new DatagramPacket(new byte[]{0},1,InetAddress.getLoopbackAddress(),udp.getLocalPort()));System.out.println("WAKE incoming datagram after "+wakeDelay+" ms");}catch(Exception e){throw new RuntimeException(e);}},wakeDelay,TimeUnit.MILLISECONDS);
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),target.getLocalPort()),"target","test","test","test");peer.utf8=true;
            long start=System.nanoTime();Future<?> sent=sender.submit(()->{try{node.sendMessage(peer,"0",Collections.emptyList());}catch(Exception e){throw new RuntimeException(e);}});
            try {sent.get(700,TimeUnit.MILLISECONDS);}catch(TimeoutException blocked) {
                for(Map.Entry<Thread,StackTraceElement[]> item:Thread.getAllStackTraces().entrySet())if(item.getKey().getName().equals("idle-test-send")||item.getKey().getName().equals("feiq-udp")) {
                    System.out.println("THREAD "+item.getKey().getName()+" "+item.getKey().getState());for(StackTraceElement frame:item.getValue())System.out.println("  "+frame);
                }
                sent.get(wakeDelay+3000,TimeUnit.MILLISECONDS);
            }
            long elapsed=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start);
            DatagramPacket packet=new DatagramPacket(new byte[65536],65536);target.receive(packet);Protocol.Packet message=Protocol.parse(Arrays.copyOf(packet.getData(),packet.getLength()));
            if(message==null||!message.body.equals("0"))throw new AssertionError("实际 UDP 内容错误");
            System.out.println("REAL_UDP_SEND_0_MS="+elapsed);passed=elapsed<1000;
            if(passed)waker.cancel(false);
            System.out.println(passed?"IDLE_SEND_PASS: no inbound packet needed":"IDLE_SEND_FAIL: outgoing waited for inbound packet");
            // 关闭套接字会解除阻塞接收，不使用强制终止或接触用户局域网。
        } finally {wake.shutdownNow();sender.shutdownNow();wake.awaitTermination(5,TimeUnit.SECONDS);sender.awaitTermination(5,TimeUnit.SECONDS);}
        if(!passed)throw new AssertionError("空闲发送 0 超过 1 秒");
    }
}
