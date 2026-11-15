package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import javax.net.ssl.SSLSocket;

/** 浏览器只连接回环入口，跨设备请求固定证书并绑定物理局域网。 */
public final class ToolsGateway implements AutoCloseable {
    private final ToolsHttp http;
    private final SecureIdentity security;
    private final InetSocketAddress endpoint;
    private final LanNode.SocketBinding binding;
    private final String fingerprint,deviceId,access=UUID.randomUUID().toString()+UUID.randomUUID(),prefix;
    private final java.util.function.Consumer<String> revoked=this::onRevoked;
    private final Set<Socket> sockets=new HashSet<>();
    private boolean closed;
    public ToolsGateway(SecureIdentity identity,LanNode.Peer peer,int port,LanNode.SocketBinding binding)throws IOException {
        security=identity;deviceId=peer.deviceId;fingerprint=peer.fingerprint;endpoint=new InetSocketAddress(peer.endpoint.getAddress(),port);this.binding=binding;
        if(!security.isTrusted(deviceId,fingerprint))throw new IOException("请先核对设备身份");
        prefix="g"+SecureIdentity.hash((peer.deviceId+":"+port).getBytes(StandardCharsets.UTF_8)).substring(0,16)+"_";
        http=new ToolsHttp(0,true,this::relay);
        security.observeRevocation(revoked);
    }
    private void onRevoked(String value){if(value.equals(fingerprint))close();}
    public String url(){return "http://127.0.0.1:"+http.port()+"/?access="+access;}
    private void relay(ToolsHttp.Request request,ToolsHttp.Response response)throws IOException {
        if(!security.isTrusted(deviceId,fingerprint))throw new ToolsHttp.Failure(403,"设备信任已撤销");
        String guard="fq_gateway_"+http.port();
        if((request.method.equals("GET")||request.method.equals("HEAD"))&&request.path.equals("/")&&request.parameter("access").equals(access)){
            response.bytes(303,"text/plain",new byte[0],"Location: /?gateway="+access+"\r\nSet-Cookie: "+guard+"="+access+"; Path=/; HttpOnly; SameSite=Lax\r\n");return;
        }
        List<String> cookies=new ArrayList<>();boolean cookieAllowed=false;
        for(String item:request.header("Cookie").split(";")){String cookie=item.trim();if(cookie.equals(guard+"="+access))cookieAllowed=true;if(cookie.startsWith(prefix))cookies.add(cookie.substring(prefix.length()));}
        boolean tokenAllowed=request.parameter("gateway").equals(access)||request.parameter("access").equals(access)||request.header("X-FQ-Gateway").equals(access)||request.header("Referer").contains("gateway="+access)||request.header("Referer").contains("access="+access);
        boolean staticShell=(request.method.equals("GET")||request.method.equals("HEAD"))&&(request.path.equals("/app.css")||request.path.equals("/app.js")||request.path.equals("/favicon.ico"));
        if(!cookieAllowed&&!tokenAllowed&&!staticShell)throw new ToolsHttp.Failure(403,"请从飞Q联系人重新打开");
        // 远控登录可能等待对端本机确认，回环入口须保留相同的人工等待窗口。
        if(request.method.equals("POST")&&(request.path.equals("/api/login")||request.path.equals("/api/reconnect")))response.waitingForUser();
        Socket raw=new Socket(Proxy.NO_PROXY);synchronized(sockets){if(closed){raw.close();throw new IOException("连接已关闭");}sockets.add(raw);}
        try {
            if(binding!=null)binding.bind(raw,endpoint.getAddress());raw.connect(endpoint,8000);raw.setSoTimeout(45000);
            try(SSLSocket tls=security.client(raw,fingerprint)) {
                String host=endpoint.getAddress().getHostAddress()+":"+endpoint.getPort();Map<String,String> headers=new TreeMap<>(String.CASE_INSENSITIVE_ORDER);headers.putAll(request.headers);
                headers.put("Host",host);headers.put("Connection","close");headers.put("Cookie",String.join("; ",cookies));if(headers.containsKey("Origin"))headers.put("Origin","https://"+host);
                String forwardedQuery=request.path.equals("/")&&(request.parameter("gateway").equals(access)||request.parameter("access").equals(access))?"":request.query;
                StringBuilder text=new StringBuilder(request.method+" "+request.path+(forwardedQuery.isEmpty()?"":"?"+forwardedQuery)+" HTTP/1.1\r\n");for(Map.Entry<String,String> item:headers.entrySet())text.append(item.getKey()).append(": ").append(item.getValue()).append("\r\n");text.append("\r\n");
                OutputStream out=tls.getOutputStream();out.write(text.toString().getBytes(StandardCharsets.US_ASCII));out.write(request.body);out.flush();
                InputStream input=tls.getInputStream();String[] lines=readHeader(input).split("\r\n");String[] status=lines[0].split(" ",3);int code=Integer.parseInt(status[1]);long length=-1;String type="application/octet-stream";StringBuilder extra=new StringBuilder();
                if(!cookieAllowed&&tokenAllowed)extra.append("Set-Cookie: ").append(guard).append("=").append(access).append("; Path=/; HttpOnly; SameSite=Lax\r\n");
                for(int i=1;i<lines.length;i++){int colon=lines[i].indexOf(':');if(colon<1)throw new IOException("响应头无效");String name=lines[i].substring(0,colon),value=lines[i].substring(colon+1).trim();
                    if(name.equalsIgnoreCase("Content-Length"))length=Long.parseLong(value);
                    else if(name.equalsIgnoreCase("Content-Type"))type=value;
                    else if(name.equalsIgnoreCase("Set-Cookie"))extra.append("Set-Cookie: ").append(prefix).append(value.replace("; Secure","").replace("SameSite=Strict","SameSite=Lax")).append("\r\n");
                    else if(name.equalsIgnoreCase("Content-Range")||name.equalsIgnoreCase("Accept-Ranges")||name.equalsIgnoreCase("Content-Disposition"))extra.append(name).append(": ").append(value).append("\r\n");
                }
                if(length<0)throw new IOException("响应长度无效");response.begin(code,type,length,extra.toString());if(request.method.equals("HEAD"))return;
                byte[] buffer=new byte[65536];while(length>0){int count=input.read(buffer,0,(int)Math.min(buffer.length,length));if(count<0)throw new EOFException();response.write(buffer,count);length-=count;}
            }
        }finally{raw.close();synchronized(sockets){sockets.remove(raw);}}
    }
    private static String readHeader(InputStream input)throws IOException {ByteArrayOutputStream bytes=new ByteArrayOutputStream();int last=0;while(bytes.size()<16384){int value=input.read();if(value<0)throw new EOFException();bytes.write(value);last=(last<<8)|value;if(last==0x0d0a0d0a)return bytes.toString("US-ASCII");}throw new IOException("响应头过长");}
    @Override public void close(){security.removeRevocationObserver(revoked);http.close();synchronized(sockets){closed=true;for(Socket socket:sockets)try{socket.close();}catch(IOException ignored){}}}
}
