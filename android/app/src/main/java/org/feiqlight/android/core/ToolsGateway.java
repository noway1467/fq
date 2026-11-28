package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import javax.net.ssl.SSLSocket;

/** 浏览器只连接回环入口，跨设备请求固定证书并绑定物理局域网。 */
public final class ToolsGateway implements AutoCloseable {
    private static final class Connection {
        Socket raw;
        SSLSocket tls;
        int requests;
        long idleSince;
        boolean pooled,closed;
    }
    private static final class Slot {Connection connection;Lease owner;}
    private static final class Lease {Slot slot;String cookie;long generation;}
    private final ToolsHttp http;
    private final SecureIdentity security;
    private final InetSocketAddress endpoint;
    private final LanNode.SocketBinding binding;
    private final LanNode node;
    private final LanNode.Peer peer;
    private final String fingerprint,deviceId,access=UUID.randomUUID().toString()+UUID.randomUUID(),prefix;
    private final java.util.function.Consumer<String> revoked=this::onRevoked;
    private final Set<Connection> connections=new HashSet<>();
    private final Map<LanNode.ConnectCancellation,Boolean> connecting=new HashMap<>();
    private final Object gate=new Object();
    private final Slot screen=new Slot(),input=new Slot();
    private final int networkEpoch;
    private static final ScheduledExecutorService idle=Executors.newScheduledThreadPool(2,r->{Thread thread=new Thread(r,"remote-tls-idle");thread.setDaemon(true);return thread;});
    private final ScheduledFuture<?> idleTask;
    private long lastActivity=System.nanoTime();
    private int activeRequests;
    static final long SCREEN_RESPONSE_LIMIT=16*1024*1024,INPUT_RESPONSE_LIMIT=64*1024;
    // 网关实例固定设备、指纹、端口与网络代次；画面、输入各自绑定 Cookie 和会话代次。
    private String sessionCookie;
    private long sessionGeneration;
    private boolean closed;
    public ToolsGateway(SecureIdentity identity,LanNode.Peer peer,int port,LanNode.SocketBinding binding)throws IOException {
        this(identity,peer,port,binding,null);
    }
    public ToolsGateway(SecureIdentity identity,LanNode.Peer peer,int port,LanNode.SocketBinding binding,LanNode node)throws IOException {
        this.node=node;networkEpoch=node==null?0:node.transportEpoch();this.peer=new LanNode.Peer(peer.endpoint,peer.login,peer.host,peer.name,peer.group);this.peer.deviceId=peer.deviceId;this.peer.fingerprint=peer.fingerprint;
        security=identity;deviceId=peer.deviceId;fingerprint=peer.fingerprint;endpoint=new InetSocketAddress(peer.endpoint.getAddress(),port);this.binding=binding;
        if(!security.isTrusted(deviceId,fingerprint))throw new IOException("请先核对设备身份");
        prefix="g"+SecureIdentity.hash((peer.deviceId+":"+port+":"+fingerprint).getBytes(StandardCharsets.UTF_8)).substring(0,16)+"_";
        http=new ToolsHttp(0,true,this::relay);
        security.observeRevocation(revoked);
        idleTask=idle.scheduleWithFixedDelay(this::expireIdle,1,1,TimeUnit.SECONDS);
    }
    private void onRevoked(String value){if(value.equals(fingerprint))close();}
    public String url(){return "http://127.0.0.1:"+http.port()+"/?access="+access;}
    public boolean isClosed(){synchronized(gate){return closed;}}
    public boolean matches(LanNode.Peer value,int port){
        if(!available())return false;
        synchronized(gate){if(!deviceId.equals(value.deviceId)||!fingerprint.equals(value.fingerprint)||endpoint.getPort()!=port)return false;lastActivity=System.nanoTime();return true;}
    }
    private void relay(ToolsHttp.Request request,ToolsHttp.Response response)throws IOException {
        synchronized(gate){activeRequests++;lastActivity=System.nanoTime();}
        try{relayRequest(request,response);}
        finally{synchronized(gate){activeRequests--;lastActivity=System.nanoTime();}}
    }
    private void relayRequest(ToolsHttp.Request request,ToolsHttp.Response response)throws IOException {
        if(!available())throw new ToolsHttp.Failure(403,"连接已关闭或设备信任已撤销");
        String guard="fq_gateway_"+http.port();
        if((request.method.equals("GET")||request.method.equals("HEAD"))&&request.path.equals("/")&&request.parameter("access").equals(access)){
            response.bytes(303,"text/plain",new byte[0],"Location: /?gateway="+access+"\r\nSet-Cookie: "+guard+"="+access+"; Path=/; HttpOnly; SameSite=Lax\r\n");return;
        }
        List<String> cookies=new ArrayList<>();boolean cookieAllowed=false;
        for(String item:request.header("Cookie").split(";")){String cookie=item.trim();if(cookie.equals(guard+"="+access))cookieAllowed=true;if(cookie.startsWith(prefix))cookies.add(cookie.substring(prefix.length()));}
        boolean tokenAllowed=request.parameter("gateway").equals(access)||request.parameter("access").equals(access)||request.header("X-FQ-Gateway").equals(access);
        boolean staticShell=(request.method.equals("GET")||request.method.equals("HEAD"))&&(request.path.equals("/app.css")||request.path.equals("/app.js")||request.path.equals("/favicon.ico"));
        if(!cookieAllowed&&!tokenAllowed&&!staticShell)throw new ToolsHttp.Failure(403,"请从飞Q联系人重新打开");
        // 远控登录可能等待对端本机确认，回环入口须保留相同的人工等待窗口。
        if(request.method.equals("POST")&&(request.path.equals("/api/login")||request.path.equals("/api/reconnect")))response.waitingForUser();
        String remoteCookie=remoteCookie(cookies);
        long generation;synchronized(gate){generation=sessionGeneration;}
        boolean logout=request.method.equals("POST")&&request.path.equals("/api/logout")&&!remoteCookie.isEmpty()&&request.header("X-FQ-Token").equals(remoteCookie.substring(remoteCookie.indexOf('=')+1));
        if(logout){
            synchronized(gate){if(sessionCookie!=null&&!sessionCookie.equals(remoteCookie))throw new ToolsHttp.Failure(401,"远控会话已变更");generation=sessionGeneration;}
            if(!changeSession("",generation))throw new ToolsHttp.Failure(401,"远控会话已变更");
            generation++;
        }
        Lease lease=null;
        if(!remoteCookie.isEmpty()&&((request.method.equals("GET")&&request.path.equals("/api/screen"))||(request.method.equals("POST")&&request.path.equals("/api/input"))))
            lease=acquire(request.path.equals("/api/screen")?screen:input,remoteCookie);
        Connection connection=null;
        boolean reusable=false,downstreamStarted=false,authenticationRejected=false;
        int rejection=0;
        ScheduledFuture<?> monitor=null,readDeadline=null;
        java.util.concurrent.atomic.AtomicReference<Connection> active=new java.util.concurrent.atomic.AtomicReference<>();
        java.util.concurrent.atomic.AtomicBoolean timedOut=new java.util.concurrent.atomic.AtomicBoolean();
        try {
                String host=endpoint.getAddress().getHostAddress()+":"+endpoint.getPort();Map<String,String> headers=new TreeMap<>(String.CASE_INSENSITIVE_ORDER);headers.putAll(request.headers);
                headers.put("Host",host);headers.put("Connection",lease==null?"close":"keep-alive");headers.put("Cookie",String.join("; ",cookies));if(headers.containsKey("Origin"))headers.put("Origin","https://"+host);
                headers.remove("X-FQ-Gateway");headers.remove("Referer");
                boolean archive=request.path.equals("/file")&&request.parameter("archive").equals("store");
                boolean remoteResponse=request.path.equals("/api/screen")||request.path.equals("/api/input");
                headers.remove("X-FQ-Archive-Progress");if(archive)headers.put("X-FQ-Archive-Progress","1");
                String requestId=UUID.randomUUID().toString().replace("-","");headers.put("X-FQ-Request-ID",requestId);
                if(archive)monitor=idle.scheduleWithFixedDelay(()->{try{response.checkConnection();}catch(IOException error){Connection value=active.get();if(value!=null)closeConnection(value);}},100,100,TimeUnit.MILLISECONDS);
                String forwardedQuery=String.join("&",Arrays.stream(request.query.split("&")).filter(p->!p.startsWith("gateway=")&&!p.startsWith("access=")).toArray(String[]::new));
                StringBuilder text=new StringBuilder(request.method+" "+request.path+(forwardedQuery.isEmpty()?"":"?"+forwardedQuery)+" HTTP/1.1\r\n");for(Map.Entry<String,String> item:headers.entrySet())text.append(item.getKey()).append(": ").append(item.getValue()).append("\r\n");text.append("\r\n");
            for(int attempt=0;attempt<2;attempt++){
                connection=takeConnection(lease);active.set(connection);boolean reused=connection.requests>0;
                if(remoteResponse&&readDeadline==null)readDeadline=idle.schedule(()->{timedOut.set(true);Connection value=active.get();if(value!=null)closeConnection(value);},10,TimeUnit.SECONDS);
                if(timedOut.get())throw new SocketTimeoutException("远控响应超时");
                try{
                SSLSocket tls=connection.tls;
                OutputStream out=tls.getOutputStream();out.write(text.toString().getBytes(StandardCharsets.US_ASCII));out.write(request.body);out.flush();
                InputStream upstream=tls.getInputStream();String header;
                do{
                    header=readHeader(upstream);
                    if(header.startsWith("HTTP/1.1 102 ")){
                        if(!archive||!header.contains("\r\nX-FQ-Archive-Progress: 1\r\n"))throw new IOException("归档进展响应无效");
                        response.processing();
                    }
                }while(header.startsWith("HTTP/1.1 102 "));
                String[] lines=header.split("\r\n");String[] status=lines[0].split(" ",3);
                if(status.length!=3||!status[0].equals("HTTP/1.1")||!status[1].matches("[2-5][0-9]{2}"))throw new IOException("响应状态无效");
                int code=Integer.parseInt(status[1]);long length=-1;String type="application/octet-stream";StringBuilder extra=new StringBuilder();
                boolean keepAlive=false,close=false;List<String> nextCookies=new ArrayList<>();
                String responseId=null;
                authenticationRejected=code==401||code==403;
                if(authenticationRejected)rejection=code;
                if(!cookieAllowed&&tokenAllowed)extra.append("Set-Cookie: ").append(guard).append("=").append(access).append("; Path=/; HttpOnly; SameSite=Lax\r\n");
                for(int i=1;i<lines.length;i++){int colon=lines[i].indexOf(':');if(colon<1)throw new IOException("响应头无效");String name=lines[i].substring(0,colon),value=lines[i].substring(colon+1).trim();
                    if(name.equalsIgnoreCase("Content-Length")){
                        if(length>=0||!value.matches("[0-9]{1,19}"))throw new IOException("响应长度重复或无效");
                        try{length=Long.parseLong(value);}catch(NumberFormatException error){throw new IOException("响应长度无效",error);}
                    }
                    else if(name.equalsIgnoreCase("Connection")){for(String token:value.split(",")){if(token.trim().equalsIgnoreCase("close"))close=true;if(token.trim().equalsIgnoreCase("keep-alive"))keepAlive=true;}}
                    else if(name.equalsIgnoreCase("Transfer-Encoding"))throw new IOException("响应帧边界无效");
                    else if(name.equalsIgnoreCase("X-FQ-Request-ID")){if(responseId!=null||!value.equals(requestId))throw new IOException("上游响应不属于当前请求");responseId=value;}
                    else if(name.equalsIgnoreCase("Content-Type"))type=value;
                    else if(name.equalsIgnoreCase("Set-Cookie")){nextCookies.add(value.split(";",2)[0]);extra.append("Set-Cookie: ").append(prefix).append(value.replace("; Secure","").replace("SameSite=Strict","SameSite=Lax")).append("\r\n");}
                    else if(name.equalsIgnoreCase("Content-Range")||name.equalsIgnoreCase("Accept-Ranges")||name.equalsIgnoreCase("Content-Disposition"))extra.append(name).append(": ").append(value).append("\r\n");
                }
                if(length<0)throw new IOException("响应长度无效");
                if(reused&&responseId==null)throw new IOException("复用响应缺少请求归属标识");
                if(remoteResponse&&length>(request.path.equals("/api/screen")?SCREEN_RESPONSE_LIMIT:INPUT_RESPONSE_LIMIT))throw new IOException("远控响应超过长度上限");
                String nextCookie=remoteCookie(nextCookies);
                if(!available()||(lease!=null&&!current(lease)))throw new IOException("远控会话已变更");
                if(request.method.equals("POST")&&(request.path.equals("/api/login")||request.path.equals("/api/reconnect"))&&code==200&&!nextCookie.isEmpty()){
                    // 下游 Cookie 可马上触发新请求，新会话必须先于响应头发布。
                    if(!changeSession(nextCookie,generation))throw new IOException("远控会话已变更");
                }
                downstreamStarted=true;response.begin(code,type,length,extra.toString());
                if(!request.method.equals("HEAD")){
                    byte[] buffer=new byte[65536];while(length>0){int count=upstream.read(buffer,0,(int)Math.min(buffer.length,length));if(count<0)throw new EOFException("响应正文被截断");response.write(buffer,count);length-=count;}
                }
                connection.requests++;
                if(lease!=null&&authenticationRejected)changeSession(code==401?"":lease.cookie,lease.generation);
                else if(logout&&code==200)changeSession("",generation);
                else if(logout)changeSession(remoteCookie,generation);
                reusable=lease!=null&&code==200&&keepAlive&&!close&&nextCookie.isEmpty()&&connection.requests<16&&responseId!=null&&!timedOut.get();
                break;
                }catch(IOException error){
                    // JVM 也可能将陈旧连接的 EOF 报为 socket reset/abort；超时和 TLS 协议错误不重试。
                    boolean eof=staleConnection(error);
                    if(!eof||timedOut.get()||attempt!=0||lease==null||!request.method.equals("GET")||!reused||downstreamStarted||authenticationRejected||!current(lease))throw error;
                    closeConnection(connection);connection=null;
                }
            }
        }finally{
            active.set(null);if(monitor!=null)monitor.cancel(false);if(readDeadline!=null)readDeadline.cancel(false);
            if(lease!=null&&authenticationRejected)changeSession(rejection==401?"":lease.cookie,lease.generation);
            boolean returned=false;
            if(lease!=null){
                reusable=reusable&&current(lease);
                synchronized(gate){if(lease.slot.owner==lease){
                    lease.slot.owner=null;
                    if(reusable&&connection!=null&&!connection.closed&&lease.generation==sessionGeneration){connection.idleSince=System.nanoTime();lease.slot.connection=connection;returned=true;}
                    gate.notifyAll();
                }}
            }
            if(!returned&&connection!=null)closeConnection(connection);
        }
    }
    private boolean available(){
        if(!security.isTrusted(deviceId,fingerprint)||(node!=null&&node.transportEpoch()!=networkEpoch)){close();return false;}
        synchronized(gate){return !closed;}
    }
    private boolean current(Lease lease){
        if(!available())return false;
        synchronized(gate){return lease.generation==sessionGeneration&&lease.cookie.equals(sessionCookie)&&lease.slot.owner==lease;}
    }
    private Lease acquire(Slot slot,String cookie)throws IOException{
        long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);
        long waitingGeneration=-1;
        while(available()){
            synchronized(gate){
                if(closed)break;
                if(sessionCookie==null){sessionCookie=cookie;sessionGeneration++;}
                if(!cookie.equals(sessionCookie))throw new ToolsHttp.Failure(401,"远控会话已变更");
                if(waitingGeneration<0)waitingGeneration=sessionGeneration;
                else if(waitingGeneration!=sessionGeneration)throw new ToolsHttp.Failure(401,"远控会话已变更");
                if(slot.owner==null){Lease lease=new Lease();lease.slot=slot;lease.cookie=cookie;lease.generation=sessionGeneration;slot.owner=lease;return lease;}
                long left=until-System.nanoTime();if(left<=0)throw new IOException("远控请求等待超时");
                try{TimeUnit.NANOSECONDS.timedWait(gate,left);}catch(InterruptedException error){Thread.currentThread().interrupt();throw new IOException("远控请求已取消",error);}
            }
        }
        throw new IOException("连接已关闭");
    }
    private Connection takeConnection(Lease lease)throws IOException{
        Connection connection=null;
        if(lease!=null){
            if(!current(lease))throw new IOException("远控会话已变更");
            synchronized(gate){if(lease.generation!=sessionGeneration||lease.slot.owner!=lease)throw new IOException("远控会话已变更");connection=lease.slot.connection;lease.slot.connection=null;}
            if(connection!=null&&System.nanoTime()-connection.idleSince>=TimeUnit.SECONDS.toNanos(2)){closeConnection(connection);connection=null;}
        }
        if(connection!=null)return connection;
        connection=new Connection();connection.pooled=lease!=null;
        LanNode.ConnectCancellation cancellation=new LanNode.ConnectCancellation();
        synchronized(gate){if(closed||(lease!=null&&lease.generation!=sessionGeneration))throw new IOException("连接已关闭");connecting.put(cancellation,lease!=null);}
        try{
            connection.raw=node==null?new Socket(Proxy.NO_PROXY):node.connectPeer(peer,endpoint.getPort(),cancellation);
            synchronized(gate){if(closed||connections.size()>=12||(lease!=null&&lease.generation!=sessionGeneration))throw new IOException("连接已关闭或请求过多");connections.add(connection);}
            Socket raw=connection.raw;raw.setSoTimeout(45000);raw.setTcpNoDelay(true);
            if(node==null){if(binding!=null)binding.bind(raw,endpoint.getAddress());raw.connect(endpoint,8000);}
            connection.tls=security.client(raw,fingerprint);
            if(!available()||(lease!=null&&!current(lease)))throw new IOException("远控会话已变更");
            return connection;
        }catch(IOException|RuntimeException error){closeConnection(connection);throw error;}
        finally{synchronized(gate){connecting.remove(cancellation);}}
    }
    private void closeConnection(Connection connection){
        boolean release;
        synchronized(gate){release=!connection.closed;connection.closed=true;connections.remove(connection);}
        // 先关闭底层 socket，立刻打断 TLS 读写；只释放一次节点额度。
        if(connection.raw==null)return;
        try{connection.raw.close();}catch(IOException ignored){}
        if(release&&node!=null)node.releaseConnection(connection.raw);
        if(connection.tls!=null)try{connection.tls.close();}catch(IOException ignored){}
    }
    private boolean changeSession(String cookie,long expectedGeneration){
        List<Connection> stale=new ArrayList<>();
        List<LanNode.ConnectCancellation> pending=new ArrayList<>();
        synchronized(gate){
            if(closed||sessionGeneration!=expectedGeneration)return false;
            sessionCookie=cookie;sessionGeneration++;
            for(Connection connection:connections)if(connection.pooled)stale.add(connection);
            for(Map.Entry<LanNode.ConnectCancellation,Boolean> item:connecting.entrySet())if(item.getValue())pending.add(item.getKey());
            screen.connection=input.connection=null;screen.owner=input.owner=null;gate.notifyAll();
        }
        for(Connection connection:stale)closeConnection(connection);
        for(LanNode.ConnectCancellation cancellation:pending)cancellation.close();
        return true;
    }
    private void expireIdle(){
        if(!available())return;
        boolean expiredGateway;synchronized(gate){expiredGateway=activeRequests==0&&System.nanoTime()-lastActivity>=TimeUnit.MINUTES.toNanos(10);}
        if(expiredGateway){close();return;}
        List<Connection> expired=new ArrayList<>();
        synchronized(gate){for(Slot slot:new Slot[]{screen,input})
            if(slot.owner==null&&slot.connection!=null&&System.nanoTime()-slot.connection.idleSince>=TimeUnit.SECONDS.toNanos(2)){expired.add(slot.connection);slot.connection=null;}
        }
        for(Connection connection:expired)closeConnection(connection);
    }
    private static String remoteCookie(List<String> cookies){
        String found="";
        for(String cookie:cookies)if((cookie.startsWith("fq_remote=")||cookie.startsWith("fq_phone="))&&cookie.length()>cookie.indexOf('=')+1){if(!found.isEmpty())return "";found=cookie;}
        return found;
    }
    static boolean staleConnection(IOException error){
        if(error instanceof EOFException)return true;
        if(!(error instanceof SocketException))return false;
        String message=error.getMessage();if(message==null)return false;
        message=message.toLowerCase(Locale.ROOT);
        return message.equals("connection reset")||message.equals("connection reset by peer")||message.startsWith("connection reset by peer: ")
            ||message.equals("connection aborted")||message.equals("software caused connection abort")||message.equals("socket is closed for writing")
            ||message.equals("an established connection was aborted by the software in your host machine")
            ||message.equals("你的主机中的软件中止了一个已建立的连接。")||message.equals("远程主机强迫关闭了一个现有的连接。");
    }
    private static String readHeader(InputStream input)throws IOException {ByteArrayOutputStream bytes=new ByteArrayOutputStream();int last=0;while(bytes.size()<16384){int value=input.read();if(value<0)throw new EOFException();bytes.write(value);last=(last<<8)|value;if(last==0x0d0a0d0a)return bytes.toString("US-ASCII");}throw new IOException("响应头过长");}
    @Override public void close(){
        List<Connection> active;
        List<LanNode.ConnectCancellation> pending;
        synchronized(gate){
            if(closed)return;closed=true;sessionGeneration++;
            active=new ArrayList<>(connections);screen.connection=input.connection=null;screen.owner=input.owner=null;gate.notifyAll();
            pending=new ArrayList<>(connecting.keySet());
        }
        security.removeRevocationObserver(revoked);idleTask.cancel(false);http.close();
        for(Connection connection:active)closeConnection(connection);
        for(LanNode.ConnectCancellation cancellation:pending)cancellation.close();
    }
}
