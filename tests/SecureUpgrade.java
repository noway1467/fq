import org.feiqlight.android.core.*;
import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;

public final class SecureUpgrade {
    private static int passed,failed;
    private interface Test {void run()throws Exception;}
    private static void run(String name,Test test){try{test.run();passed++;System.out.println("PASS "+name);}catch(Exception|AssertionError e){failed++;System.out.println("FAIL "+name+": "+e);e.printStackTrace(System.out);}}
    private static void check(boolean value,String message){if(!value)throw new AssertionError(message);}
    private static int port()throws IOException{try(ServerSocket socket=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){return socket.getLocalPort();}}
    private static final class Node implements AutoCloseable {
        final LanNode node;final BlockingQueue<LanNode.Message> incoming=new LinkedBlockingQueue<>();
        Node(File root,int port,String id)throws IOException{
            node=new LanNode(port,true,id,"Android",id,"test",new LanNode.Listener(){public void peersChanged(){}public void error(String text){System.out.println("NOTICE "+text);}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(!message.outgoing)incoming.add(message);}});
            node.setDeviceId(id);node.configureSecurity(root,(name,code)->true);node.start();
        }
        public void close(){node.close();}
    }
    private static LanNode.Peer find(Node a,Node b)throws Exception {a.node.probe(new InetSocketAddress("127.0.0.1",b.node.port()));for(int i=0;i<500;i++){for(LanNode.Peer peer:a.node.peers())if(peer.endpoint.getPort()==b.node.port())return peer;Thread.sleep(10);}throw new AssertionError("发现超时");}
    private static String http(int port,String path,String cookie)throws IOException {try(Socket socket=new Socket("127.0.0.1",port)){socket.setSoTimeout(10000);socket.getOutputStream().write(("GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nConnection: close\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII));return new String(socket.getInputStream().readAllBytes(),java.nio.charset.StandardCharsets.UTF_8);}}
    private static String delayedLogin(int port,String cookie)throws IOException{try(Socket socket=new Socket("127.0.0.1",port)){socket.setSoTimeout(25000);socket.getOutputStream().write(("POST /api/login HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII));return new String(socket.getInputStream().readAllBytes(),java.nio.charset.StandardCharsets.UTF_8);}}
    public static void main(String[] args)throws Exception {
        Path root=Paths.get(args[0]);Files.createDirectories(root);
        run("目录协议与恶意路径拒绝",()->{Protocol.Attachment file=new Protocol.Attachment(0,"中文😀.txt",5,1);file.sha256="a".repeat(64);file.relativePath="目录/子目录/中文😀.txt";check(Protocol.parseFiles(Protocol.files(Collections.singletonList(file))).get(0).relativePath.equals(file.relativePath),"目录丢失");for(String value:new String[]{"../secret","/root/a","a//b","a/CON","a/../b","a\\b"})check(!Protocol.safeRelativePath(value),"接受危险路径");});
        run("TLS 配对、双重启恢复、哈希失败不提交与拒绝持久化",()->{
            int ap=port(),bp=port();String aid="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",bid="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";byte[] bytes=new byte[1024*1024+7];for(int i=0;i<bytes.length;i++)bytes[i]=(byte)(i*17);File source=root.resolve("source.bin").toFile();Files.write(source.toPath(),bytes);LanNode.Message message;String fingerprint;
            try(Node a=new Node(root.resolve("a").toFile(),ap,aid);Node b=new Node(root.resolve("b").toFile(),bp,bid)){
                LanNode.Peer peer=find(a,b);a.node.verifyPeer(peer);fingerprint=a.node.security().fingerprint;check(a.node.security().isTrusted(bid,b.node.security().fingerprint),"信任未记录");
                a.node.sendMessage(peer,"secure",Collections.singletonList(source));message=b.incoming.poll(5,TimeUnit.SECONDS);check(message!=null,"消息未收到");
            }
            try(Node a=new Node(root.resolve("a").toFile(),ap,aid);Node b=new Node(root.resolve("b").toFile(),bp,bid)){
                check(a.node.security().fingerprint.equals(fingerprint),"身份变化");LanNode.Peer peer=find(b,a);a.node.security().confirmation=(name,code)->{throw new IOException("重复配对");};b.node.security().confirmation=(name,code)->{throw new IOException("重复配对");};
                Protocol.Attachment file=message.files.get(0);File destination=root.resolve("resumed.bin").toFile();Files.write(LanNode.partialFile(peer,message.number,file,destination).toPath(),Arrays.copyOf(bytes,123456));b.node.receiveFile(peer,message.number,file,destination,new LanNode.Transfer(),null);check(Arrays.equals(bytes,Files.readAllBytes(destination.toPath())),"续传字节不一致");
                File corrupt=root.resolve("corrupt.bin").toFile();Files.write(LanNode.partialFile(peer,message.number,file,corrupt).toPath(),new byte[123456]);boolean rejected=false;try{b.node.receiveFile(peer,message.number,file,corrupt,new LanNode.Transfer(),null);}catch(IOException e){rejected=true;}check(rejected&&!corrupt.exists(),"错误哈希仍提交文件");
                b.node.reject(peer,message.number);
            }
            try(Node a=new Node(root.resolve("a").toFile(),ap,aid)){check(!a.node.offering("device:"+bid,message.number,0),"重启复活拒绝邀请");}
        });
        run("拒绝错误证书和未允许的旧版明文",()->{
            try(Node a=new Node(root.resolve("pin-a").toFile(),port(),"cccccccccccccccccccccccccccccccc");Node b=new Node(root.resolve("pin-b").toFile(),port(),"dddddddddddddddddddddddddddddddd")){
                LanNode.Peer peer=find(a,b);String pin=peer.fingerprint;peer.fingerprint="f".repeat(64);boolean rejected=false;try{a.node.verifyPeer(peer);}catch(IOException e){rejected=true;}check(rejected,"接受错误证书");peer.fingerprint=null;rejected=false;try{a.node.sendMessage(peer,"secret",Collections.emptyList());}catch(IOException e){rejected=true;}check(rejected,"发送明文");peer.fingerprint=pin;
            }
        });
        run("证书撤销清理已保存文件授权",()->{
            String aid="11111111111111111111111111111111",bid="22222222222222222222222222222222";File file=root.resolve("revoke.txt").toFile();Files.writeString(file.toPath(),"revoke");
            try(Node a=new Node(root.resolve("revoke-a").toFile(),port(),aid);Node b=new Node(root.resolve("revoke-b").toFile(),port(),bid)){
                LanNode.Peer peer=find(a,b);a.node.verifyPeer(peer);LanNode.Message sent=a.node.sendMessage(peer,"撤销",Collections.singletonList(file));check(b.incoming.poll(5,TimeUnit.SECONDS)!=null,"邀请未到达");a.node.forgetSecurity(peer);check(!a.node.offering(peer.id(),sent.number,0),"撤销后仍有文件授权");check(!a.node.security().isTrustedFingerprint(b.node.security().fingerprint),"撤销后仍信任证书");
            }
        });
        run("HTTPS 回环网关鉴权与流式转发",()->{
            SecureIdentity server=new SecureIdentity(root.resolve("https-server").toFile()),client=new SecureIdentity(root.resolve("https-client").toFile());
            server.confirmation=(name,code)->true;client.confirmation=(name,code)->true;server.ensureTrusted("ffffffffffffffffffffffffffffffff",client.fingerprint,"client");client.ensureTrusted("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",server.fingerprint,"server");
            try(ToolsHttp http=new ToolsHttp(0,true,(request,response)->{if(request.path.equals("/api/login")){response.waitingForUser();Thread.sleep(16000);}response.bytes(200,"text/plain","protected".getBytes(java.nio.charset.StandardCharsets.UTF_8),"");},server)){
                LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress("127.0.0.1",2425),"peer","host","name","group");peer.deviceId="eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";peer.fingerprint=server.fingerprint;
                try(ToolsGateway gateway=new ToolsGateway(client,peer,http.port(),null)){
                    URI uri=URI.create(gateway.url());check(http(uri.getPort(),"/","").contains("403"),"未授权进入网关");String response=http(uri.getPort(),uri.getRawPath()+"?"+uri.getRawQuery(),"");String cookie=Arrays.stream(response.split("\r\n")).filter(s->s.startsWith("Set-Cookie: ")).findFirst().orElseThrow().substring(12).split(";")[0];check(http(uri.getPort(),"/",cookie).contains("protected"),"TLS 转发失败");
                    check(delayedLogin(uri.getPort(),cookie).contains("protected"),"网关在人工确认期间提前断开");
                    server.forget("ffffffffffffffffffffffffffffffff");check(!server.isTrustedFingerprint(client.fingerprint),"撤销未移除指纹");check(!http(uri.getPort(),"/",cookie).contains("protected"),"撤销后仍可读取共享");
                }
            }
        });
        System.out.println("ANDROID SECURE: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);
    }
}
