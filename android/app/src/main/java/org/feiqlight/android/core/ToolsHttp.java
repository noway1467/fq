package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;

/** 只处理有界 HTTP 请求；共享与旧聊天协议分端口运行。 */
public final class ToolsHttp implements AutoCloseable {
    public static final class Failure extends IOException {
        private static final long serialVersionUID=1L;
        public final int code;
        public Failure(int code,String text) {super(text);this.code=code;}
    }
    public static final class Request {
        public String method,path,query,address,fingerprint;
        private RemoteAuthorization remoteAuthorized;
        public void bindRemoteSession(String token)throws IOException {remoteAuthorized.bind(token);}
        public String pairScope(String scope){return fingerprint==null?scope:scope+"|tls="+fingerprint;}
        public byte[] body;
        public final Map<String,String> headers=new TreeMap<>(String.CASE_INSENSITIVE_ORDER);
        public String header(String key) {return headers.getOrDefault(key,"");}
        public String parameter(String key) throws IOException {
            for(String part:query.split("&")) {String[] pair=part.split("=",2);if(pair[0].equals(key))return URLDecoder.decode(pair.length==2?pair[1]:"","UTF-8");}return "";
        }
    }
    public static final class Response {
        private final OutputStream output;
        private final boolean head;
        private final AtomicLong progress;
        private boolean keepAlive;
        private final Socket socket;
        private final Socket disconnect;
        private long connectionCheck;
        private long remaining;
        boolean started;
        Response(OutputStream output,boolean head,AtomicLong progress,boolean keepAlive) {this(output,head,progress,keepAlive,null);}
        Response(OutputStream output,boolean head,AtomicLong progress,boolean keepAlive,Socket socket) {this(output,head,progress,keepAlive,socket,socket);}
        Response(OutputStream output,boolean head,AtomicLong progress,boolean keepAlive,Socket socket,Socket disconnect) {this.output=output;this.head=head;this.progress=progress;this.keepAlive=keepAlive;this.socket=socket;this.disconnect=disconnect;}
        public void begin(int code,String type,long length,String extra) throws IOException {
            if(code>=300)keepAlive=false;
            String header="HTTP/1.1 "+code+" "+(code==200?"OK":code==206?"Partial Content":"Error")+"\r\nContent-Type: "+type+"\r\nContent-Length: "+length+"\r\nConnection: "+(keepAlive?"keep-alive\r\nKeep-Alive: timeout=3, max=16":"close")+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n"+extra+"\r\n";
            started=true;remaining=head?0:length;output.write(header.getBytes(StandardCharsets.US_ASCII));progress.set(System.nanoTime());
        }
        public void bytes(int code,String type,byte[] bytes,String extra) throws IOException {begin(code,type,bytes.length,extra);write(bytes,bytes.length);}
        public void json(String json,String extra) throws IOException {bytes(200,"application/json; charset=utf-8",json.getBytes(StandardCharsets.UTF_8),extra);}
        // 本机确认最多等待 30 秒；只给该请求增加等待时间，不放宽视频写入超时。
        public void waitingForUser(){progress.set(System.nanoTime()+TimeUnit.SECONDS.toNanos(20));}
        // CRC 预检还未发送响应头，用实际读取进度避免被误判为空闲连接。
        public void processing()throws IOException{checkConnection();progress.set(System.nanoTime());}
        public void disableReuse(){keepAlive=false;}
        public void abort()throws IOException{if(disconnect!=null)disconnect.close();}
        public void checkConnection()throws IOException {
            if(socket==null)return;
            if(socket.isClosed())throw new IOException("下载连接已关闭");
            if(keepAlive)return;
            long now=System.nanoTime();
            if(now-connectionCheck<TimeUnit.MILLISECONDS.toNanos(100))return;
            connectionCheck=now;
            int timeout=socket.getSoTimeout();
            try {
                // 只用于 Connection: close 的归档；预检期间尚无输出，需主动发现对端 EOF。
                socket.setSoTimeout(1);
                if(socket.getInputStream().read()<0)throw new IOException("下载连接已关闭");
                throw new IOException("打包下载连接收到额外数据");
            }catch(SocketTimeoutException expected){/* 没有后续输入表示下载连接仍然开放。 */}
            finally{if(!socket.isClosed())socket.setSoTimeout(timeout);}
        }
        public void write(byte[] bytes,int length) throws IOException {if(!head){output.write(bytes,0,length);remaining-=length;}progress.set(System.nanoTime());}
    }
    public interface Handler {void handle(Request request,Response response) throws Exception;}
    private interface RemoteAuthorization {void bind(String token)throws IOException;}
    private final ServerSocket server;
    private final Handler handler;
    private final Set<Socket> clients=new HashSet<>();
    private final Map<Socket,String> remoteClients=new HashMap<>();
    private final Map<Socket,String> fingerprints=new HashMap<>();
    private final ScheduledExecutorService timeouts=Executors.newSingleThreadScheduledExecutor(r->{Thread t=new Thread(r,"shared-files-timeouts");t.setDaemon(true);return t;});
    private final ExecutorService workers=Executors.newFixedThreadPool(12,r->{Thread t=new Thread(r,"shared-files-http");t.setDaemon(true);return t;});
    private boolean closed;
    private final SecureIdentity security;
    private final LanNode.SocketBinding binding;
    private final int bodyLimit;
    private final java.util.function.Consumer<String> revoked=this::disconnectFingerprint;
    public ToolsHttp(int port,boolean loopback,Handler handler) throws IOException {
        this(port,loopback,handler,null);
    }
    public ToolsHttp(int port,boolean loopback,Handler handler,SecureIdentity security) throws IOException {
        this(port,loopback,handler,security,null);
    }
    public ToolsHttp(int port,boolean loopback,Handler handler,SecureIdentity security,LanNode.SocketBinding binding) throws IOException {
        this(port,loopback,handler,security,binding,16384);
    }
    public ToolsHttp(int port,boolean loopback,Handler handler,SecureIdentity security,LanNode.SocketBinding binding,int bodyLimit) throws IOException {
        if(bodyLimit<0||bodyLimit>1048576)throw new IllegalArgumentException("请求体上限无效");this.bodyLimit=bodyLimit;
        this.security=security;
        this.binding=binding;
        this.handler=handler;server=new ServerSocket();
        try {server.bind(new InetSocketAddress(loopback?InetAddress.getByName("127.0.0.1"):InetAddress.getByName("0.0.0.0"),port),16);}
        catch(IOException e){server.close();workers.shutdownNow();timeouts.shutdownNow();throw e;}
        if(security!=null){security.observeRevocation(revoked);security.registerReverseListener(port(),this::acceptReverse);}
        Thread accept=new Thread(this::accept,"shared-files-listener");accept.setDaemon(true);accept.start();
    }
    public int port() {return server.getLocalPort();}
    private void acceptReverse(Socket socket){synchronized(clients){if(closed||clients.size()>=12){try{socket.close();}catch(IOException ignored){}return;}clients.add(socket);}handle(socket);}
    public static int portFor(int chatPort) {return chatPort==65535?65534:chatPort+1;}
    private void accept() {
        try {while(true) {Socket socket=server.accept();synchronized(clients){if(closed||clients.size()>=12){socket.close();if(closed)return;continue;}clients.add(socket);workers.execute(()->handle(socket));}}}
        catch(IOException e){close();}
    }
    private void handle(Socket socket) {
        Response response=null;
        AtomicLong progress=new AtomicLong(System.nanoTime());
        ScheduledFuture<?> timeout=null;
        try {
            if(binding!=null){if(!binding.allows(socket.getInetAddress()))return;try{binding.bindAccepted(socket);}catch(IOException ignored){}}
            timeout=timeouts.scheduleWithFixedDelay(()->{if(System.nanoTime()-progress.get()>TimeUnit.SECONDS.toNanos(15))try{socket.close();}catch(IOException ignored){}},1,1,TimeUnit.SECONDS);
            socket.setSoTimeout(5000);socket.setTcpNoDelay(true);socket.setSendBufferSize(262144);
            Socket transport=security==null?socket:security.server(socket,true);
            String fingerprint=security==null?null:SecureIdentity.remoteFingerprint((javax.net.ssl.SSLSocket)transport);
            synchronized(clients){fingerprints.put(socket,fingerprint);}
            if(security!=null&&!security.isTrustedFingerprint(fingerprint))throw new IOException("请先从联系人核对设备身份");
            // 将请求头批量读入内存，避免每个字节都跨一次系统调用。
            RequestInput deadline=new RequestInput(transport);InputStream input=new BufferedInputStream(deadline,8192);
            for(int index=0;index<16;index++){
            deadline.begin(index==0?5000:3000);
            Request request=read(input,bodyLimit);
            boolean requestedReuse=index<15&&request.header("Connection").equalsIgnoreCase("keep-alive");
            boolean fileReuse=requestedReuse&&request.path.equals("/file")&&(request.method.equals("GET")||request.method.equals("HEAD"));
            boolean remoteReuse=requestedReuse&&((request.path.equals("/api/screen")&&request.method.equals("GET"))||(request.path.equals("/api/input")&&request.method.equals("POST")));
            request.fingerprint=fingerprint;if(security!=null&&!security.isTrustedFingerprint(fingerprint))throw new IOException("设备信任已撤销");
            response=new Response(transport.getOutputStream(),request.method.equals("HEAD"),progress,fileReuse,transport,socket);
            Response currentResponse=response;
            request.remoteAuthorized=token->{
                synchronized(clients){
                    if(closed||!clients.contains(socket))throw new IOException("连接已关闭");
                    String previous=remoteClients.get(socket);
                    if(previous!=null&&!previous.equals(token))throw new Failure(401,"远控会话已变更");
                    remoteClients.put(socket,token);currentResponse.keepAlive=remoteReuse;
                }
            };
            request.address=socket.getInetAddress().getHostAddress();String expected=socket.getLocalAddress().getHostAddress()+":"+port();
            if(!request.header("Host").equals(expected))throw new Failure(403,"请使用设备的 IPv4 地址访问");
            if(!request.header("Origin").isEmpty()&&!request.header("Origin").equals((security==null?"http://":"https://")+expected))throw new Failure(403,"不允许跨站请求");
            handler.handle(request,response);
            if(!response.keepAlive||!response.started||response.remaining!=0)break;socket.setSoTimeout(3000);
            }
        } catch(Exception e) {
            if(response!=null&&!response.started)try {
                String text=e instanceof Failure?e.getMessage():"文件不可用或连接已关闭";
                response.bytes(e instanceof Failure?((Failure)e).code:500,"application/json; charset=utf-8",("{\"error\":\""+text.replace("\\","\\\\").replace("\"","\\\"").replace("\n"," ").replace("\r"," ")+"\"}").getBytes(StandardCharsets.UTF_8),"");
            }catch(IOException ignored){/* 对端已经关闭，不能再发送错误响应。 */}
        } finally {if(timeout!=null)timeout.cancel(false);try{socket.close();}catch(IOException ignored){}synchronized(clients){clients.remove(socket);remoteClients.remove(socket);fingerprints.remove(socket);}}
    }
    private static Request read(InputStream input,int bodyLimit) throws IOException {
        ByteArrayOutputStream header=new ByteArrayOutputStream();int last=0;
        while(header.size()<16384){int value=input.read();if(value<0)throw new EOFException();header.write(value);last=(last<<8)|value;if(last==0x0d0a0d0a)break;}
        if(last!=0x0d0a0d0a)throw new IOException("请求头过长");
        String[] lines=header.toString("US-ASCII").split("\r\n");String[] first=lines[0].split(" ");
        if(first.length!=3||!first[2].equals("HTTP/1.1")||!first[1].startsWith("/")||first[1].startsWith("//"))throw new IOException("无效请求");
        String[] target=first[1].split("\\?",2);Request request=new Request();request.method=first[0];request.path=target[0];request.query=target.length==2?target[1]:"";
        for(int i=1;i<lines.length;i++){int colon=lines[i].indexOf(':');if(colon<=0)throw new IOException("无效请求头");String key=lines[i].substring(0,colon);if(request.headers.containsKey(key))throw new IOException("重复请求头");request.headers.put(key,lines[i].substring(colon+1).trim());}
        int length=0;String value=request.header("Content-Length");
        if(!value.isEmpty()){if(!value.matches("[0-9]{1,7}"))throw new IOException("无效长度");length=Integer.parseInt(value);}
        if(length>bodyLimit||!request.header("Transfer-Encoding").isEmpty())throw new IOException("无效请求体");
        request.body=new byte[length];int offset=0;while(offset<length){int count=input.read(request.body,offset,length-offset);if(count<0)throw new EOFException();offset+=count;}return request;
    }
    public static long[] range(String header,long size) {
        if(header.isEmpty())return new long[]{0,size-1};
        if(!header.matches("bytes=[0-9]*-[0-9]*")||size<=0)return null;
        String[] parts=header.substring(6).split("-",-1);
        try {
            if(parts[0].isEmpty()){long suffix=Long.parseLong(parts[1]);return suffix>0?new long[]{Math.max(0,size-suffix),size-1}:null;}
            long start=Long.parseLong(parts[0]),end=parts[1].isEmpty()?size-1:Long.parseLong(parts[1]);
            return start>=size||end<start?null:new long[]{start,Math.min(end,size-1)};
        }catch(NumberFormatException e){return null;}
    }
    @Override public void close() {
        synchronized(clients){
            if(closed)return;
            closed=true;
            // accept 退出也会关闭本实例；注销必须只执行一次，并早于端口释放。
            if(security!=null){security.removeRevocationObserver(revoked);security.unregisterReverseListener(port());}
            try{server.close();}catch(IOException ignored){}
            for(Socket socket:clients)try{socket.close();}catch(IOException ignored){}
            workers.shutdownNow();timeouts.shutdownNow();
        }
    }
    public void disconnectClients(){synchronized(clients){for(Socket socket:clients)try{socket.close();}catch(IOException ignored){}}}
    private void disconnectFingerprint(String fingerprint){synchronized(clients){for(Map.Entry<Socket,String> item:fingerprints.entrySet())if(fingerprint.equals(item.getValue()))try{item.getKey().close();}catch(IOException ignored){}}}
    public void disconnectRemoteClients(){disconnectRemoteClients(null);}
    public void disconnectRemoteClients(String token){
        synchronized(clients){for(Map.Entry<Socket,String> item:remoteClients.entrySet())
            if(token==null||token.equals(item.getValue()))try{item.getKey().close();}catch(IOException ignored){}
        }
    }
}
