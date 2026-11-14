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
        public String method,path,query,address;
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
        private final boolean keepAlive;
        private long remaining;
        boolean started;
        Response(OutputStream output,boolean head,AtomicLong progress,boolean keepAlive) {this.output=output;this.head=head;this.progress=progress;this.keepAlive=keepAlive;}
        public void begin(int code,String type,long length,String extra) throws IOException {
            String header="HTTP/1.1 "+code+" "+(code==200?"OK":code==206?"Partial Content":"Error")+"\r\nContent-Type: "+type+"\r\nContent-Length: "+length+"\r\nConnection: "+(keepAlive?"keep-alive\r\nKeep-Alive: timeout=3, max=16":"close")+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'\r\n"+extra+"\r\n";
            started=true;remaining=head?0:length;output.write(header.getBytes(StandardCharsets.US_ASCII));progress.set(System.nanoTime());
        }
        public void bytes(int code,String type,byte[] bytes,String extra) throws IOException {begin(code,type,bytes.length,extra);write(bytes,bytes.length);}
        public void json(String json,String extra) throws IOException {bytes(200,"application/json; charset=utf-8",json.getBytes(StandardCharsets.UTF_8),extra);}
        // 本机确认最多等待 30 秒；只给该请求增加等待时间，不放宽视频写入超时。
        public void waitingForUser(){progress.set(System.nanoTime()+TimeUnit.SECONDS.toNanos(20));}
        public void write(byte[] bytes,int length) throws IOException {if(!head){output.write(bytes,0,length);remaining-=length;}progress.set(System.nanoTime());}
    }
    public interface Handler {void handle(Request request,Response response) throws Exception;}
    private final ServerSocket server;
    private final Handler handler;
    private final Set<Socket> clients=new HashSet<>();
    private final ScheduledExecutorService timeouts=Executors.newSingleThreadScheduledExecutor(r->{Thread t=new Thread(r,"shared-files-timeouts");t.setDaemon(true);return t;});
    private final ExecutorService workers=Executors.newFixedThreadPool(12,r->{Thread t=new Thread(r,"shared-files-http");t.setDaemon(true);return t;});
    private boolean closed;
    public ToolsHttp(int port,boolean loopback,Handler handler) throws IOException {
        this.handler=handler;server=new ServerSocket();
        try {server.bind(new InetSocketAddress(loopback?InetAddress.getByName("127.0.0.1"):InetAddress.getByName("0.0.0.0"),port),16);}
        catch(IOException e){server.close();workers.shutdownNow();timeouts.shutdownNow();throw e;}
        Thread accept=new Thread(this::accept,"shared-files-listener");accept.setDaemon(true);accept.start();
    }
    public int port() {return server.getLocalPort();}
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
            timeout=timeouts.scheduleWithFixedDelay(()->{if(System.nanoTime()-progress.get()>TimeUnit.SECONDS.toNanos(15))try{socket.close();}catch(IOException ignored){}},1,1,TimeUnit.SECONDS);
            socket.setSoTimeout(5000);socket.setTcpNoDelay(true);socket.setSendBufferSize(262144);
            // 将请求头批量读入内存，避免每个字节都跨一次系统调用。
            RequestInput deadline=new RequestInput(socket);InputStream input=new BufferedInputStream(deadline,8192);
            for(int index=0;index<16;index++){
            deadline.begin(index==0?5000:3000);
            Request request=read(input);boolean reuse=index<15&&request.path.equals("/file")&&(request.method.equals("GET")||request.method.equals("HEAD"))&&request.header("Connection").equalsIgnoreCase("keep-alive");
            response=new Response(socket.getOutputStream(),request.method.equals("HEAD"),progress,reuse);
            request.address=socket.getInetAddress().getHostAddress();String expected=socket.getLocalAddress().getHostAddress()+":"+port();
            if(!request.header("Host").equals(expected))throw new Failure(403,"请使用设备的 IPv4 地址访问");
            if(!request.header("Origin").isEmpty()&&!request.header("Origin").equals("http://"+expected))throw new Failure(403,"不允许跨站请求");
            handler.handle(request,response);
            if(!reuse||!response.started||response.remaining!=0)break;socket.setSoTimeout(3000);
            }
        } catch(Exception e) {
            if(response!=null&&!response.started)try {
                String text=e instanceof Failure?e.getMessage():"文件不可用或连接已关闭";
                response.bytes(e instanceof Failure?((Failure)e).code:500,"application/json; charset=utf-8",("{\"error\":\""+text.replace("\\","\\\\").replace("\"","\\\"").replace("\n"," ").replace("\r"," ")+"\"}").getBytes(StandardCharsets.UTF_8),"");
            }catch(IOException ignored){/* 对端已经关闭，不能再发送错误响应。 */}
        } finally {if(timeout!=null)timeout.cancel(false);try{socket.close();}catch(IOException ignored){}synchronized(clients){clients.remove(socket);}}
    }
    private static Request read(InputStream input) throws IOException {
        ByteArrayOutputStream header=new ByteArrayOutputStream();int last=0;
        while(header.size()<16384){int value=input.read();if(value<0)throw new EOFException();header.write(value);last=(last<<8)|value;if(last==0x0d0a0d0a)break;}
        if(last!=0x0d0a0d0a)throw new IOException("请求头过长");
        String[] lines=header.toString("US-ASCII").split("\r\n");String[] first=lines[0].split(" ");
        if(first.length!=3||!first[2].equals("HTTP/1.1")||!first[1].startsWith("/")||first[1].startsWith("//"))throw new IOException("无效请求");
        String[] target=first[1].split("\\?",2);Request request=new Request();request.method=first[0];request.path=target[0];request.query=target.length==2?target[1]:"";
        for(int i=1;i<lines.length;i++){int colon=lines[i].indexOf(':');if(colon<=0)throw new IOException("无效请求头");String key=lines[i].substring(0,colon);if(request.headers.containsKey(key))throw new IOException("重复请求头");request.headers.put(key,lines[i].substring(colon+1).trim());}
        int length=0;String value=request.header("Content-Length");
        if(!value.isEmpty()){if(!value.matches("[0-9]{1,5}"))throw new IOException("无效长度");length=Integer.parseInt(value);}
        if(length>16384||!request.header("Transfer-Encoding").isEmpty())throw new IOException("无效请求体");
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
        synchronized(clients){if(closed)return;closed=true;try{server.close();}catch(IOException ignored){}for(Socket socket:clients)try{socket.close();}catch(IOException ignored){}workers.shutdownNow();timeouts.shutdownNow();}
    }
}
