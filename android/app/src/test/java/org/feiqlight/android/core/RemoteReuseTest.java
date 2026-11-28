package org.feiqlight.android.core;

import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import javax.net.ssl.SSLSocket;
import static org.junit.Assert.*;

/** 真实 TLS 回环，不依赖 Android UI 或宿主机局域网。 */
public final class RemoteReuseTest {
    private static final String SERVER_ID="d".repeat(32),CLIENT_ID="e".repeat(32);
    @Rule public final TemporaryFolder temporary=new TemporaryFolder(new File(System.getProperty("java.io.tmpdir")));
    private static final class Reply {
        int code;
        String header="";
        byte[] body=new byte[0];
        String cookies(){
            List<String> values=new ArrayList<>();
            for(String line:header.split("\r\n"))if(line.startsWith("Set-Cookie: "))values.add(line.substring(12).split(";",2)[0]);
            return String.join("; ",values);
        }
    }
    private static final class Incoming {
        String method,path;
        final Map<String,String> headers=new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
        String header(String name){return headers.getOrDefault(name,"");}
    }
    private static final class Peer implements AutoCloseable {
        final SecureIdentity server,client;
        final AtomicInteger handshakes=new AtomicInteger(),screens=new AtomicInteger(),inputs=new AtomicInteger(),sessions=new AtomicInteger();
        final AtomicInteger dropScreen=new AtomicInteger(),dropInput=new AtomicInteger();
        final AtomicReference<String> fault=new AtomicReference<>("");
        final AtomicBoolean holdLogin=new AtomicBoolean();
        final CountDownLatch screenEntered=new CountDownLatch(1);
        volatile CountDownLatch releaseScreen=new CountDownLatch(0);
        volatile CountDownLatch releaseLogin=new CountDownLatch(0);
        volatile boolean legacy,closed;
        volatile int screenDelay,reject;
        volatile String token="";
        private final ServerSocket listener=new ServerSocket(0,12,InetAddress.getByName("127.0.0.1"));
        private final Map<Socket,SSLSocket> sockets=new HashMap<>();
        private final ExecutorService workers=Executors.newCachedThreadPool();
        Peer(File root)throws Exception{
            server=new SecureIdentity(new File(root,"server"));client=new SecureIdentity(new File(root,"client"));
            server.confirmation=client.confirmation=(name,code)->true;
            assertTrue(server.ensureTrusted(CLIENT_ID,client.fingerprint,"client"));assertTrue(client.ensureTrusted(SERVER_ID,server.fingerprint,"server"));
            workers.submit(this::accept);
        }
        int port(){return listener.getLocalPort();}
        ToolsGateway gateway()throws IOException{
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getByName("127.0.0.1"),port()),"server","server","server","");peer.deviceId=SERVER_ID;peer.fingerprint=server.fingerprint;
            return new ToolsGateway(client,peer,port(),null);
        }
        private void accept(){
            try{while(!closed){Socket raw=listener.accept();synchronized(sockets){if(closed){raw.close();return;}sockets.put(raw,null);}workers.submit(()->serve(raw));}}
            catch(IOException ignored){assertTrue(closed);}
        }
        private void serve(Socket raw){
            SSLSocket tls=null;
            try{
                raw.setSoTimeout(5000);raw.setTcpNoDelay(true);tls=server.server(raw,true);
                assertEquals(client.fingerprint,SecureIdentity.remoteFingerprint(tls));handshakes.incrementAndGet();
                synchronized(sockets){sockets.put(raw,tls);}
                for(int index=0;index<16;index++){
                    Incoming request=readRequest(tls.getInputStream());OutputStream output=tls.getOutputStream();
                    if(request.path.equals("/api/login")||request.path.equals("/api/reconnect")){
                        token="remote-session-"+sessions.incrementAndGet();String body="{\"token\":\""+token+"\",\"mode\":\"remote\"}",extra="Set-Cookie: fq_remote="+token+"; Path=/; HttpOnly; SameSite=Strict\r\n";
                        if(holdLogin.getAndSet(false)){write(output,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+body.length()+"\r\nConnection: close\r\n"+extra+"\r\n");releaseLogin.await(5,TimeUnit.SECONDS);write(output,body);}else send(output,200,body,false,extra);return;
                    }
                    if(Arrays.stream(request.header("Cookie").split(";")).map(String::trim).noneMatch(value->value.equals("fq_remote="+token))){send(output,401,"{}",false,"");return;}
                    boolean screen=request.method.equals("GET")&&request.path.equals("/api/screen"),input=request.method.equals("POST")&&request.path.equals("/api/input");
                    if(screen){screens.incrementAndGet();screenEntered.countDown();releaseScreen.await(5,TimeUnit.SECONDS);if(screenDelay>0)Thread.sleep(screenDelay);if(dropScreen.getAndSet(0)!=0)return;}
                    if(input){if(!request.header("X-FQ-Token").equals(token)){send(output,403,"{}",false,"");return;}inputs.incrementAndGet();if(dropInput.getAndSet(0)!=0)return;}
                    if(reject!=0){send(output,reject,"{}",false,"");return;}
                    String next=fault.getAndSet("");
                    if(next.equals("duplicate")){write(output,"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\n{}");return;}
                    if(next.equals("truncated")){write(output,"HTTP/1.1 200 OK\r\nContent-Length: 20\r\nConnection: keep-alive\r\n\r\n{}");return;}
                    boolean reuse=!legacy&&(screen||input)&&index<15&&request.header("Connection").equals("keep-alive");send(output,200,"{}",reuse,"");if(!reuse)return;
                }
            }catch(IOException ignored){/* 测试主动制造 EOF、撤销与截断。 */}
            catch(InterruptedException error){Thread.currentThread().interrupt();}
            finally{try{if(tls!=null)tls.close();raw.close();}catch(IOException ignored){}synchronized(sockets){sockets.remove(raw);}}
        }
        void closeIdle()throws IOException{
            Map<Socket,SSLSocket> copy;synchronized(sockets){copy=new HashMap<>(sockets);}
            for(Map.Entry<Socket,SSLSocket> entry:copy.entrySet()){if(entry.getValue()!=null)entry.getValue().close();entry.getKey().close();}
        }
        @Override public void close()throws Exception{
            closed=true;listener.close();synchronized(sockets){for(Socket raw:sockets.keySet())raw.close();}
            releaseScreen.countDown();releaseLogin.countDown();workers.shutdown();assertTrue("TLS 回环线程未退出",workers.awaitTermination(6,TimeUnit.SECONDS));
        }
    }
    private static String readHeader(InputStream input)throws IOException{
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();int last=0;
        while(bytes.size()<16384){int value=input.read();if(value<0)throw new EOFException();bytes.write(value);last=(last<<8)|value;if(last==0x0d0a0d0a)return bytes.toString(StandardCharsets.US_ASCII);}
        throw new IOException("回环响应头过长");
    }
    private static Incoming readRequest(InputStream input)throws IOException{
        String[] lines=readHeader(input).split("\r\n");String[] first=lines[0].split(" ");Incoming request=new Incoming();request.method=first[0];request.path=first[1].split("\\?",2)[0];
        for(int i=1;i<lines.length;i++){int colon=lines[i].indexOf(':');request.headers.put(lines[i].substring(0,colon),lines[i].substring(colon+1).trim());}
        int length=Integer.parseInt(request.headers.getOrDefault("Content-Length","0"));assertEquals(length,input.readNBytes(length).length);return request;
    }
    private static void write(OutputStream output,String text)throws IOException{output.write(text.getBytes(StandardCharsets.US_ASCII));output.flush();}
    private static void send(OutputStream output,int code,String body,boolean reuse,String extra)throws IOException{
        write(output,"HTTP/1.1 "+code+" Result\r\nContent-Type: application/json\r\nContent-Length: "+body.length()+"\r\nConnection: "+(reuse?"keep-alive":"close")+"\r\n"+extra+"\r\n"+body);
    }
    private static Reply call(String url,String path,String cookie,String body,String token)throws Exception{
        URI uri=URI.create(url);String access=uri.getQuery().substring("access=".length());Reply reply=new Reply();
        try(Socket raw=new Socket(Proxy.NO_PROXY)){
            raw.connect(new InetSocketAddress("127.0.0.1",uri.getPort()),4000);raw.setSoTimeout(5000);raw.setTcpNoDelay(true);
            String request=(body==null?"GET":"POST")+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+uri.getPort()+"\r\nX-FQ-Gateway: "+access+"\r\nCookie: "+cookie+"\r\nConnection: close\r\n";
            if(body!=null)request+="Content-Type: application/json\r\nContent-Length: "+body.length()+"\r\nX-FQ-Token: "+token+"\r\n";
            write(raw.getOutputStream(),request+"\r\n"+(body==null?"":body));ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];
            try{int count;while((count=raw.getInputStream().read(buffer))>=0)bytes.write(buffer,0,count);}catch(IOException ignored){/* 断流时保留已经收到的响应头和正文。 */}
            String text=bytes.toString(StandardCharsets.US_ASCII);int split=text.indexOf("\r\n\r\n");if(split<0)return reply;
            reply.header=text.substring(0,split);reply.code=Integer.parseInt(text.split(" ",3)[1]);reply.body=Arrays.copyOfRange(bytes.toByteArray(),split+4,bytes.size());return reply;
        }catch(SocketException ignored){return reply;}
    }
    private static String login(String url)throws Exception{Reply reply=call(url,"/api/login","","{\"mode\":\"remote\"}","");assertEquals(200,reply.code);return reply.cookies();}
    private static Reply screen(String url,String cookie)throws Exception{return call(url,"/api/screen",cookie,null,"");}
    private static Reply input(String url,String cookie,String token)throws Exception{return call(url,"/api/input",cookie,"{}",token);}
    @Test public void realTlsHandshakesFallFrom24To2AndLegacyCloseWorks()throws Exception{
        int legacy,pooled;
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            peer.legacy=true;String url=gateway.url(),cookie=login(url);int before=peer.handshakes.get();
            for(int i=0;i<12;i++){assertEquals(200,screen(url,cookie).code);assertEquals(200,input(url,cookie,peer.token).code);}legacy=peer.handshakes.get()-before;assertEquals(24,legacy);
        }
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);int before=peer.handshakes.get();
            for(int i=0;i<12;i++){Reply frame=screen(url,cookie);assertEquals(200,frame.code);assertTrue(frame.header.contains("Connection: close"));assertFalse(frame.header.contains("keep-alive"));assertEquals(200,input(url,cookie,peer.token).code);}pooled=peer.handshakes.get()-before;assertEquals(2,pooled);assertEquals(12,peer.inputs.get());
        }
        System.out.println("METRIC Android requests=24 legacy_tls="+legacy+" pooled_tls="+pooled+" reduction=91.67%");
    }
    @Test public void inputDoesNotQueueBehind300msScreen()throws Exception{
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);assertEquals(200,input(url,cookie,peer.token).code);peer.screenDelay=300;
            ExecutorService caller=Executors.newSingleThreadExecutor();
            try{Future<Reply> frame=caller.submit(()->screen(url,cookie));assertTrue(peer.screenEntered.await(2,TimeUnit.SECONDS));long start=System.nanoTime();assertEquals(200,input(url,cookie,peer.token).code);long ms=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start);assertTrue("输入排在静止画面后："+ms,ms<200);assertFalse(frame.isDone());assertEquals(200,frame.get(2,TimeUnit.SECONDS).code);System.out.println("METRIC Android stationary_screen_ms=300 input_ms="+ms);}
            finally{caller.shutdownNow();}
        }
    }
    @Test public void staleIdleScreenRetriesOnceButFreshEofDoesNotRetryAgain()throws Exception{
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);assertEquals(200,screen(url,cookie).code);int before=peer.handshakes.get();peer.closeIdle();assertEquals(200,screen(url,cookie).code);assertEquals(before+1,peer.handshakes.get());
            peer.dropScreen.set(1);assertEquals(200,screen(url,cookie).code);int screens=peer.screens.get();peer.closeIdle();peer.dropScreen.set(1);assertNotEquals(200,screen(url,cookie).code);assertEquals(screens+1,peer.screens.get());
        }
    }
    @Test public void executedInputIsNeverReplayedAndTruncatedScreenAfterHeadersIsNotRetried()throws Exception{
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);assertEquals(200,input(url,cookie,peer.token).code);int inputs=peer.inputs.get();peer.dropInput.set(1);assertNotEquals(200,input(url,cookie,peer.token).code);assertEquals(inputs+1,peer.inputs.get());
            assertEquals(200,screen(url,cookie).code);int screens=peer.screens.get();peer.fault.set("truncated");Reply truncated=screen(url,cookie);assertEquals(200,truncated.code);assertEquals(2,truncated.body.length);assertEquals(screens+1,peer.screens.get());int handshakes=peer.handshakes.get();assertEquals(200,screen(url,cookie).code);assertEquals(handshakes+1,peer.handshakes.get());
        }
    }
    @Test public void duplicateLengthAndUnauthorizedRepliesNeverReturnToPoolOrRetry()throws Exception{
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);assertEquals(200,screen(url,cookie).code);peer.fault.set("duplicate");assertNotEquals(200,screen(url,cookie).code);int handshakes=peer.handshakes.get();assertEquals(200,screen(url,cookie).code);assertEquals(handshakes+1,peer.handshakes.get());
            for(int code:new int[]{403,401}){int screens=peer.screens.get();peer.reject=code;assertEquals(code,screen(url,cookie).code);assertEquals(screens+1,peer.screens.get());peer.reject=0;if(code==403)assertEquals(200,screen(url,cookie).code);}
        }
    }
    @Test public void cookieAndGatewayInstancesCannotShareConnections()throws Exception{
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),old=login(url),oldToken=peer.token;assertEquals(200,screen(url,old).code);String next=login(url);int screens=peer.screens.get();assertNotEquals(200,screen(url,old).code);assertEquals(screens,peer.screens.get());assertNotEquals(200,call(url,"/api/logout",old,"{}",oldToken).code);assertEquals(200,screen(url,next).code);int handshakes=peer.handshakes.get();
            try(ToolsGateway second=peer.gateway()){assertEquals(200,screen(second.url(),next).code);assertEquals(handshakes+1,peer.handshakes.get());}
        }
    }
    @Test public void newSessionIsPublishedBeforeBrowserReceivesCookieHeaders()throws Exception{
        try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);assertEquals(200,screen(url,cookie).code);peer.holdLogin.set(true);peer.releaseLogin=new CountDownLatch(1);URI uri=URI.create(url);
            try(Socket browser=new Socket("127.0.0.1",uri.getPort())){
                browser.setSoTimeout(4000);write(browser.getOutputStream(),"POST /api/reconnect HTTP/1.1\r\nHost: 127.0.0.1:"+uri.getPort()+"\r\nX-FQ-Gateway: "+uri.getQuery().substring("access=".length())+"\r\nCookie: "+cookie+"\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
                try{Reply headers=new Reply();headers.header=readHeader(browser.getInputStream());assertEquals(200,screen(url,headers.cookies()).code);}
                finally{peer.releaseLogin.countDown();}
                browser.getInputStream().readAllBytes();
            }
        }
    }
    @Test public void stopAndRevocationCloseActiveAndIdleConnectionsAndWakeWaiters()throws Exception{
        for(boolean revoke:new boolean[]{false,true})try(Peer peer=new Peer(temporary.newFolder());ToolsGateway gateway=peer.gateway()){
            String url=gateway.url(),cookie=login(url);assertEquals(200,input(url,cookie,peer.token).code);peer.releaseScreen=new CountDownLatch(1);ExecutorService caller=Executors.newFixedThreadPool(2);
            try{Future<Reply> active=caller.submit(()->screen(url,cookie));assertTrue(peer.screenEntered.await(2,TimeUnit.SECONDS));Future<Reply> waiter=caller.submit(()->screen(url,cookie));Thread.sleep(30);long start=System.nanoTime();if(revoke)peer.client.forget(SERVER_ID);else gateway.close();assertNotEquals(200,active.get(500,TimeUnit.MILLISECONDS).code);assertNotEquals(200,waiter.get(500,TimeUnit.MILLISECONDS).code);long ms=TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start);assertTrue(ms<500);System.out.println("METRIC Android gateway_"+(revoke?"revoke":"stop")+"_ms="+ms);}
            finally{peer.releaseScreen.countDown();caller.shutdownNow();}
        }
    }
    @Test public void idleSlotsCountAgainstNodeLimit8AndNetworkGenerationCannotReuseThem()throws Exception{
        File root=temporary.newFolder();
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};
        try(Peer peer=new Peer(root);LanNode node=new LanNode(org.feiqlight.android.TestPorts.chat(),true,"pool-node","host","pool-node","",listener)){
            node.configureSecurity(new File(root,"client"),peer.client,(name,code)->true);node.setDeviceId(CLIENT_ID);node.start();
            LanNode.Peer target=new LanNode.Peer(new InetSocketAddress("127.0.0.1",peer.port()),"server","host","server","");target.deviceId=SERVER_ID;target.fingerprint=peer.server.fingerprint;
            try(ToolsGateway gateway=new ToolsGateway(peer.client,target,peer.port(),null,node)){
                String url=gateway.url(),cookie=login(url);assertEquals(200,screen(url,cookie).code);assertEquals(200,input(url,cookie,peer.token).code);
                java.lang.reflect.Field field=LanNode.class.getDeclaredField("connections");field.setAccessible(true);Set<?> connections=(Set<?>)field.get(node);assertEquals(2,connections.size());List<Socket> extra=new ArrayList<>();
                try{for(int i=0;i<6;i++)extra.add(node.connectPeer(target,peer.port()));try(Socket ignored=node.connectPeer(target,peer.port())){fail("复用绕过节点上限 8");}catch(IOException expected){}assertEquals(8,connections.size());}
                finally{for(Socket raw:extra){raw.close();node.releaseConnection(raw);}}
                java.lang.reflect.Method reset=LanNode.class.getDeclaredMethod("switchNetwork",Object.class);reset.setAccessible(true);reset.invoke(node,new Object[]{null});int screens=peer.screens.get();assertNotEquals(200,screen(url,cookie).code);assertEquals(screens,peer.screens.get());
                long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(1);while(!connections.isEmpty()&&System.nanoTime()<until)Thread.sleep(5);assertTrue("切网后旧槽未释放节点额度",connections.isEmpty());
            }
        }
    }
}
