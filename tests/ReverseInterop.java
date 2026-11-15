import org.feiqlight.android.core.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.function.BooleanSupplier;

public final class ReverseInterop {
    private static void waitFor(BooleanSupplier done)throws Exception{for(int i=0;i<600;i++){if(done.getAsBoolean())return;Thread.sleep(100);}throw new IOException("互通等待超时");}
    private static String call(int port,String path,String cookie,String body)throws IOException{
        try(Socket socket=new Socket("127.0.0.1",port)){
            socket.setSoTimeout(20000);byte[] content=body==null?new byte[0]:body.getBytes(StandardCharsets.UTF_8);
            socket.getOutputStream().write(((body==null?"GET":"POST")+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nContent-Type: application/json\r\nContent-Length: "+content.length+"\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));socket.getOutputStream().write(content);
            return new String(socket.getInputStream().readAllBytes(),StandardCharsets.UTF_8);
        }
    }
    public static void main(String[] args)throws Exception {
        Path root=Paths.get(args[0]);int port=Integer.parseInt(args[1]),windows=Integer.parseInt(Files.readString(root.resolve("windows-ready")));
        try(LanNode node=new LanNode(port,true,"java","android","Java","test",new LanNode.Listener(){public void peersChanged(){}public void message(LanNode.Peer p,LanNode.Message m,boolean u){}public void error(String text){}})){
            node.setDeviceId("22222222222222222222222222222222");node.configureSecurity(root.resolve("java-security").toFile(),(name,code)->true);node.start();
            ToolsHttp.Handler handler=(request,response)->{if(!node.security().isTrustedFingerprint(request.fingerprint))throw new IOException("未认证请求");response.bytes(200,"text/plain",request.method.equals("POST")?request.body:"j".repeat(131077).getBytes(StandardCharsets.UTF_8),"");};
            try(ToolsHttp share=new ToolsHttp(port+1,true,handler,node.security());ToolsHttp remote=new ToolsHttp(port+2,true,handler,node.security())){
                Files.writeString(root.resolve("java-ready"),Integer.toString(port));waitFor(()->Files.exists(root.resolve("windows-done")));
                LanNode.Peer peer=node.peers().stream().filter(p->"11111111111111111111111111111111".equals(p.deviceId)).findFirst().orElseThrow();
                java.lang.reflect.Field connector=LanNode.class.getDeclaredField("peerConnector");connector.setAccessible(true);connector.set(node,java.lang.reflect.Proxy.newProxyInstance(LanNode.class.getClassLoader(),new Class<?>[]{connector.getType()},(proxy,method,values)->{throw new ConnectException("模拟单向阻断");}));
                node.verifyPeer(peer);
                for(int targetPort:new int[]{windows+1,windows+2})try(ToolsGateway gateway=new ToolsGateway(node.security(),peer,targetPort,null,node)){
                    URI uri=URI.create(gateway.url());String redirect=call(uri.getPort(),uri.getRawPath()+"?"+uri.getRawQuery(),"",null);String cookie=Arrays.stream(redirect.split("\r\n")).filter(s->s.startsWith("Set-Cookie: ")).findFirst().orElseThrow().substring(12).split(";")[0];
                    if(!call(uri.getPort(),"/",cookie,null).endsWith("w".repeat(131077)))throw new IOException("Java -> Windows 共享/画面不完整");
                    if(!call(uri.getPort(),"/api/input",cookie,"{\"text\":\"中文😀\"}").endsWith("{\"text\":\"中文😀\"}"))throw new IOException("Java -> Windows 输入丢失");
                }
                System.out.println("PASS Java -> Windows: 回连身份核验、共享/远控双端口、多段正文、UTF-8 POST");Files.writeString(root.resolve("java-done"),"done");
            }
        }
    }
}
