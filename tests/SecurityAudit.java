import org.feiqlight.android.core.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.BooleanSupplier;

/** 用 APK 的正式通信核心和真实回环套接字复现攻击，不访问宿主局域网。 */
public final class SecurityAudit {
    private static final String ID="0123456789abcdef0123456789abcdef";
    private static InetAddress receiver,attacker,local;
    private static Path root;
    private static boolean baseline;
    private static int passed,failed;
    private interface Test {void run()throws Exception;}
    private static void check(boolean condition,String reason){if(!condition)throw new AssertionError(reason);}
    private static void await(BooleanSupplier condition,String reason)throws Exception {
        long until=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(2500);
        while(!condition.getAsBoolean()&&System.nanoTime()<until)Thread.sleep(10);
        check(condition.getAsBoolean(),reason);
    }
    private static int port()throws IOException {
        try(ServerSocket tcp=new ServerSocket(0,1,local);DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),local)){return tcp.getLocalPort();}
    }
    private static final class Inbox implements LanNode.Listener {
        final Map<Long,String> states=new ConcurrentHashMap<>();
        final List<String> errors=new CopyOnWriteArrayList<>();
        public void peersChanged(){}
        public void message(LanNode.Peer peer,LanNode.Message message,boolean update){states.put(message.number,message.state);}
        public void error(String text){errors.add(text);}
    }
    private static LanNode node(Inbox inbox)throws IOException {
        LanNode node=new LanNode(port(),true,"audit-local","audit-host","漏洞验证","",inbox);node.start();return node;
    }
    private static void send(DatagramSocket socket,int port,int mode,String body,String login)throws IOException {
        byte[] bytes=Protocol.encode(900,login,"audit-host",mode|Protocol.UTF8,body,null,ID);
        socket.send(new DatagramPacket(bytes,bytes.length,local,port));
    }
    private static Protocol.Packet message(DatagramSocket socket,long number,int timeout)throws IOException {
        long until=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(timeout);
        while(System.nanoTime()<until){
            socket.setSoTimeout((int)Math.max(1,TimeUnit.NANOSECONDS.toMillis(until-System.nanoTime())));
            DatagramPacket raw=new DatagramPacket(new byte[60001],60001);
            try{socket.receive(raw);}catch(SocketTimeoutException e){return null;}
            Protocol.Packet p=Protocol.parse(Arrays.copyOf(raw.getData(),raw.getLength()));
            if(p!=null&&p.mode()==Protocol.MESSAGE&&p.number==number)return p;
        }
        return null;
    }
    private static byte[] download(int port,long packet,InetAddress from)throws IOException {
        try(Socket socket=new Socket(Proxy.NO_PROXY)){
            socket.bind(new InetSocketAddress(from,0));socket.connect(new InetSocketAddress(local,port),1500);socket.setSoTimeout(1500);
            socket.getOutputStream().write(Protocol.encode(1,"peer","audit-host",Protocol.GET_FILE|Protocol.UTF8,Long.toHexString(packet)+":0:0:",null));
            return socket.getInputStream().readAllBytes();
        }catch(SocketException|SocketTimeoutException e){return new byte[0];}
    }
    private static boolean httpAvailable(int port)throws IOException {
        try(Socket socket=new Socket(Proxy.NO_PROXY)){
            socket.connect(new InetSocketAddress(local,port),1500);socket.setSoTimeout(1500);
            socket.getOutputStream().write(("GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
            String line=new BufferedReader(new InputStreamReader(socket.getInputStream(),StandardCharsets.US_ASCII)).readLine();
            return line!=null&&line.startsWith("HTTP/1.1 200 ");
        }catch(SocketException|SocketTimeoutException e){return false;}
    }
    private static final class SlowClients implements AutoCloseable {
        private final List<Socket> clients=new ArrayList<>();
        private final Thread drip;
        private volatile boolean stopped;
        SlowClients(int port,int count,boolean http)throws IOException {
            for(int i=0;i<count;i++){
                Socket socket=new Socket(Proxy.NO_PROXY);clients.add(socket);socket.bind(new InetSocketAddress(attacker,0));socket.connect(new InetSocketAddress(local,port),1500);
                String prefix=!http?"1:":i%2==0?"GET /api/status HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nX-Audit: ":"POST /api/login HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nContent-Type: application/json\r\nContent-Length: 16384\r\n\r\n";
                socket.getOutputStream().write(prefix.getBytes(StandardCharsets.US_ASCII));
            }
            drip=new Thread(()->{
                while(!stopped){
                    for(Socket socket:clients)try{socket.getOutputStream().write('x');}catch(IOException expected){}
                    try{Thread.sleep(200);}catch(InterruptedException stop){return;}
                }
            },"audit-drip");drip.setDaemon(true);drip.start();
        }
        public void close()throws Exception {stopped=true;for(Socket socket:clients)socket.close();drip.join(2000);check(!drip.isAlive(),"慢连接线程未退出");}
    }
    private static void spoofRetry()throws Exception {
        byte[] content="仅原接收者可下载的测试文件".getBytes(StandardCharsets.UTF_8);Path file=root.resolve("authorized.bin");Files.write(file,content);Inbox inbox=new Inbox();
        try(LanNode node=node(inbox);DatagramSocket good=new DatagramSocket(0,receiver);DatagramSocket bad=new DatagramSocket(0,attacker)){
            send(good,node.port(),Protocol.ANSWER,"原接收者","peer");await(()->node.peers().size()==1,"未收到真实发现包");
            LanNode.Peer peer=node.peers().get(0);LanNode.Message record=node.sendMessage(peer,"AUDIT-PRIVATE-MESSAGE",Collections.singletonList(file.toFile()));
            check(message(good,record.number,1500)!=null,"原接收者没有收到首发消息");
            send(bad,node.port(),Protocol.ANSWER,"冒用身份","impostor");await(()->peer.endpoint.equals(bad.getLocalSocketAddress())&&peer.login.equals("impostor"),"没有走到实际身份更新路径");
            Protocol.Packet leaked=message(bad,record.number,3400);
            send(bad,node.port(),Protocol.ACK,Long.toString(record.number),"impostor");Thread.sleep(250);
            boolean forgedAck="已送达".equals(inbox.states.get(record.number));
            System.out.println("EVIDENCE retry_leaked="+(leaked!=null)+" forged_ack_accepted="+forgedAck);
            check((leaked!=null)==baseline&&forgedAck==baseline,"重发泄露或伪造 ACK 结果与预期不符");
            if(!baseline){
                check(message(good,record.number,500)!=null,"固定接收地址后没有正常重发");
                send(good,node.port(),Protocol.ACK,Long.toString(record.number),"peer");await(()->"已送达".equals(inbox.states.get(record.number)),"原接收者的有效 ACK 被拒绝");
            }
            check(download(node.port(),record.number,attacker).length==0,"冒用设备身份获得附件下载权限");
            check(Arrays.equals(content,download(node.port(),record.number,receiver)),"原接收者失去附件权限");
            check(inbox.errors.isEmpty(),inbox.errors.toString());
        }
    }
    private static void slowFile()throws Exception {
        byte[] content="slow-file-authorized".getBytes(StandardCharsets.UTF_8);Path file=root.resolve("slow.bin");Files.write(file,content);Inbox inbox=new Inbox();
        try(LanNode node=node(inbox);DatagramSocket good=new DatagramSocket(0,receiver)){
            send(good,node.port(),Protocol.ANSWER,"原接收者","peer");await(()->node.peers().size()==1,"未收到发现包");
            LanNode.Message record=node.sendMessage(node.peers().get(0),"文件占位检查",Collections.singletonList(file.toFile()));
            check(Arrays.equals(content,download(node.port(),record.number,receiver)),"攻击前合法下载失败");
            try(SlowClients slow=new SlowClients(node.port(),4,false)){
                Thread.sleep(700);check(download(node.port(),record.number,receiver).length==0,"没有成功填满附件服务名额");
                Thread.sleep(5600);boolean recovered=Arrays.equals(content,download(node.port(),record.number,receiver));
                System.out.println("EVIDENCE file_recovered_while_dripping="+recovered);check(recovered!=baseline,"慢附件请求总时限未生效或基线未复现");
            }
            check(Arrays.equals(content,download(node.port(),record.number,receiver)),"攻击连接关闭后文件服务未恢复");
            check(inbox.errors.isEmpty(),inbox.errors.toString());
        }
    }
    private static void slowHttp()throws Exception {
        try(ToolsHttp server=new ToolsHttp(0,true,(request,response)->response.json("{\"ok\":true}",""))){
            check(httpAvailable(server.port()),"攻击前 HTTP 服务不可用");
            try(SlowClients slow=new SlowClients(server.port(),12,true)){
                Thread.sleep(700);check(!httpAvailable(server.port()),"没有成功填满 HTTP 服务名额");
                Thread.sleep(5600);boolean recovered=httpAvailable(server.port());
                System.out.println("EVIDENCE http_recovered_while_dripping="+recovered);check(recovered!=baseline,"HTTP 请求头和正文总时限未生效或基线未复现");
            }
            check(httpAvailable(server.port()),"攻击连接关闭后 HTTP 服务未恢复");
        }
    }
    private static void run(String name,Test test){
        try{test.run();passed++;System.out.println("PASS "+name);}catch(Exception|AssertionError e){failed++;System.out.println("FAIL "+name);e.printStackTrace(System.out);}
    }
    public static void main(String[] args)throws Exception {
        root=Paths.get(args[0]).toAbsolutePath();Files.createDirectories(root);baseline=args.length>1&&args[1].equals("baseline");
        local=InetAddress.getByName("127.0.0.1");receiver=InetAddress.getByName("127.0.0.2");attacker=InetAddress.getByName("127.0.0.3");
        run("冒用设备 ID 的重发截获、ACK 与附件权限",SecurityAudit::spoofRetry);
        run("4 个未认证慢附件请求与合法下载",SecurityAudit::slowFile);
        run("12 个慢 HTTP 请求头/正文与合法请求",SecurityAudit::slowHttp);
        System.out.println("RESULT Android mode="+(baseline?"baseline":"fixed")+" passed="+passed+" failed="+failed);
        if(failed>0)System.exit(1);
    }
}
