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
    private static int toolPorts()throws IOException{for(int port=21000;port<45000;port+=3){List<ServerSocket> sockets=new ArrayList<>();try{for(int i=0;i<3;i++)sockets.add(new ServerSocket(port+i,1,InetAddress.getLoopbackAddress()));return port;}catch(IOException ignored){}finally{for(ServerSocket socket:sockets)socket.close();}}throw new IOException("没有可用测试端口");}
    private static final class Node implements AutoCloseable {
        final LanNode node;final BlockingQueue<LanNode.Message> incoming=new LinkedBlockingQueue<>();
        Node(File root,int port,String id)throws IOException{
            node=new LanNode(port,true,id,"Android",id,"test",new LanNode.Listener(){public void peersChanged(){}public void error(String text){System.out.println("NOTICE "+text);}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(!message.outgoing)incoming.add(message);}});
            node.setDeviceId(id);node.configureSecurity(root,(name,code)->true);node.start();
        }
        public void close(){node.close();}
    }
    private static LanNode.Peer find(Node a,Node b)throws Exception {a.node.probe(new InetSocketAddress("127.0.0.1",b.node.port()));for(int i=0;i<500;i++){for(LanNode.Peer peer:a.node.peers())if(peer.endpoint.getPort()==b.node.port())return peer;Thread.sleep(10);}throw new AssertionError("发现超时");}
    private static Object field(LanNode node,String name)throws Exception{java.lang.reflect.Field value=LanNode.class.getDeclaredField(name);value.setAccessible(true);return value.get(node);}
    private static void raw(Node sender,Node receiver,byte[] bytes)throws Exception{((DatagramSocket)field(sender.node,"udp")).send(new DatagramPacket(bytes,bytes.length,new InetSocketAddress("127.0.0.1",receiver.node.port())));}
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
                LanNode.Peer peer=find(a,b);String pin=peer.fingerprint;peer.fingerprint="f".repeat(64);boolean rejected=false;try{a.node.verifyPeer(peer);}catch(IOException e){rejected=true;}check(rejected,"接受错误证书");check(a.node.allowLegacyProtocol,"未默认开启兼容");a.node.allowLegacyProtocol=false;peer.fingerprint=null;rejected=false;try{a.node.sendMessage(peer,"secret",Collections.emptyList());}catch(IOException e){rejected=true;}check(rejected,"关闭后仍发送明文");peer.fingerprint=pin;
            }
        });
        run("已配对会话拒绝省略身份的明文消息和退出报文",()->{
            String aid="12121212121212121212121212121212",bid="34343434343434343434343434343434";
            try(Node a=new Node(root.resolve("strip-a").toFile(),port(),aid);Node b=new Node(root.resolve("strip-b").toFile(),port(),bid)){
                LanNode.Peer peer=find(a,b);a.node.verifyPeer(peer);
                raw(b,a,Protocol.encode(93001,bid,"Android",Protocol.MESSAGE|Protocol.UTF8,"stripped-message",null));
                raw(b,a,Protocol.encode(93002,bid,"Android",Protocol.EXIT|Protocol.UTF8,"stripped-exit",null));
                raw(b,a,Protocol.encode(93003,bid,"Android",Protocol.MESSAGE|Protocol.UTF8,"legacy-barrier",null,"56565656565656565656565656565656"));
                List<String> texts=new ArrayList<>();long end=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);
                while(!texts.contains("legacy-barrier")&&System.nanoTime()<end){LanNode.Message message=a.incoming.poll(100,TimeUnit.MILLISECONDS);if(message!=null)texts.add(message.text);}
                check(texts.contains("legacy-barrier"),"旧设备兼容消息未处理");check(!texts.contains("stripped-message"),"省略设备 ID 的明文混入已信任会话");check(peer.online,"省略身份的退出报文把已信任联系人下线");
            }
        });
        run("文件任务写盘失败不留下重试消息或发送授权",()->{
            for(boolean direct:new boolean[]{false,true}) {
                File source=root.resolve("write-failure-"+direct+".txt").toFile();Files.writeString(source.toPath(),"未发送的测试文件");Path senderRoot=root.resolve("write-failure-a-"+direct);
                try(Node a=new Node(senderRoot.toFile(),port(),"78787878787878787878787878787878");Node b=new Node(root.resolve("write-failure-b-"+direct).toFile(),port(),"90909090909090909090909090909090")) {
                    LanNode.Peer peer=find(a,b);a.node.verifyPeer(peer);Files.createDirectory(senderRoot.resolve("transfer-offers.dat.new"));boolean rejected=false;
                    try {
                        if(!direct)a.node.sendMessage(peer,"不能后台补发",Collections.singletonList(source));
                        else a.node.sendSources(peer,Collections.singletonList(new LanNode.Source(){public String key(){return "write-failure";}public String name(){return source.getName();}public long size(){return source.length();}public long modified(){return source.lastModified();}public InputStream open(long offset)throws IOException{FileInputStream stream=new FileInputStream(source);stream.getChannel().position(offset);return stream;}}));
                    }catch(IOException e){rejected=true;}
                    check(rejected,"未复现写盘失败");check(((Map<?,?>)field(a.node,"pending")).isEmpty()&&((Map<?,?>)field(a.node,"offers")).isEmpty(),"报告发送失败后仍保留自动重试或附件授权：direct="+direct);
                }
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
                    URI uri=URI.create(gateway.url());check(http(uri.getPort(),"/","").contains("403"),"未授权进入网关");String response=http(uri.getPort(),uri.getRawPath()+"?"+uri.getRawQuery(),"");
                    check(response.contains("Location: /?gateway=")&&response.contains("SameSite=Lax"),"外部浏览器跳转未携带网关参数或 SameSite=Lax");
                    String location=Arrays.stream(response.split("\r\n")).filter(s->s.startsWith("Location: ")).findFirst().orElseThrow().substring(10).trim();
                    String withoutCookie=http(uri.getPort(),location,"");
                    check(withoutCookie.contains("protected")&&withoutCookie.contains("Set-Cookie: "),"外部浏览器丢弃重定向 Cookie 后无法打开共享页");
                    String cookie=Arrays.stream(response.split("\r\n")).filter(s->s.startsWith("Set-Cookie: ")).findFirst().orElseThrow().substring(12).split(";")[0];check(http(uri.getPort(),"/",cookie).contains("protected"),"TLS 转发失败");
                    check(delayedLogin(uri.getPort(),cookie).contains("protected"),"网关在人工确认期间提前断开");
                    server.forget("ffffffffffffffffffffffffffffffff");check(!server.isTrustedFingerprint(client.fingerprint),"撤销未移除指纹");check(!http(uri.getPort(),"/",cookie).contains("protected"),"撤销后仍可读取共享");
                }
            }
        });
        run("VPN 单向连接受阻时通过反向同步送达消息与推送文件",()->{
            String aid="abababababababababababababababab",bid="cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";
            byte[] payload=new byte[256*1024+19];for(int i=0;i<payload.length;i++)payload[i]=(byte)(i*31+7);
            File source=root.resolve("vpn-push-source.bin").toFile();Files.write(source.toPath(),payload);
            try(Node a=new Node(root.resolve("vpn-a").toFile(),port(),aid);Node b=new Node(root.resolve("vpn-b").toFile(),port(),bid)){
                LanNode.Peer peerB=find(a,b),peerA=find(b,a);
                a.node.verifyPeer(peerB);
                // 关闭 B 的 TCP 监听与 UDP 套接字，模拟 VPN 拦截所有入站连接（A 无法直连 B，仅 B 可主动连 A）。
                ((ServerSocket)field(b.node,"tcp")).close();
                a.node.sendMessage(peerB,"VPN反向同步文本",Collections.emptyList());
                LanNode.Message gotText=b.incoming.poll(6,TimeUnit.SECONDS);
                check(gotText!=null&&"VPN反向同步文本".equals(gotText.text),"单向受阻时反向同步未送达文本消息");
                b.node.sendMessage(peerA,"VPN文件邀请",Collections.singletonList(source));
                LanNode.Message offerMsg=a.incoming.poll(6,TimeUnit.SECONDS);
                check(offerMsg!=null&&offerMsg.files.size()==1,"单向受阻时文件邀请未送达");
                File destination=root.resolve("vpn-pushed.bin").toFile();
                a.node.receiveFile(peerB,offerMsg.number,offerMsg.files.get(0),destination,new LanNode.Transfer(),null);
                check(Arrays.equals(payload,Files.readAllBytes(destination.toPath())),"单向受阻时反向推送文件内容不一致");
            }
        });
        run("VPN 回连身份核验、共享远控端口、正文与拒绝",()->{
            String aid="01010101010101010101010101010101",bid="02020202020202020202020202020202";
            try(Node a=new Node(root.resolve("reverse-a").toFile(),port(),aid);Node b=new Node(root.resolve("reverse-b").toFile(),toolPorts(),bid)){
                LanNode.Peer peerB=find(a,b),peerA=find(b,a);a.node.verifyPeer(peerB);
                java.lang.reflect.Field connector=LanNode.class.getDeclaredField("peerConnector");connector.setAccessible(true);
                connector.set(a.node,java.lang.reflect.Proxy.newProxyInstance(LanNode.class.getClassLoader(),new Class<?>[]{connector.getType()},(proxy,method,values)->{throw new ConnectException("模拟单向入站阻断");}));
                a.node.verifyPeer(peerB);check(peerB.online,"反向身份核验未完成");
                String payload="z".repeat(256*1024+13);
                for(int servicePort:new int[]{b.node.port()==65535?65534:b.node.port()+1,b.node.port()>=65534?b.node.port()-2:b.node.port()+2})
                try(ToolsHttp http=new ToolsHttp(servicePort,true,(request,response)->{check(request.fingerprint.equals(a.node.security().fingerprint),"回连丢失客户端身份");response.bytes(200,"text/plain",payload.getBytes(java.nio.charset.StandardCharsets.UTF_8),"");},b.node.security());ToolsGateway gateway=new ToolsGateway(a.node.security(),peerB,servicePort,null,a.node)){
                    URI uri=URI.create(gateway.url());String redirect=http(uri.getPort(),uri.getRawPath()+"?"+uri.getRawQuery(),"");String cookie=Arrays.stream(redirect.split("\r\n")).filter(s->s.startsWith("Set-Cookie: ")).findFirst().orElseThrow().substring(12).split(";")[0];
                    check(http(uri.getPort(),"/",cookie).endsWith(payload),"共享/远控回连正文不完整");
                }
                File source=root.resolve("reverse-reject.txt").toFile();Files.writeString(source.toPath(),"拒绝文件");LanNode.Message sent=b.node.sendMessage(peerA,"拒绝",Collections.singletonList(source));check(a.incoming.poll(5,TimeUnit.SECONDS)!=null,"文件邀请未到达");a.node.reject(peerB,sent.number);check(!b.node.offering(peerA.id(),sent.number,0),"回连拒绝未撤销文件授权");
                a.node.forgetSecurity(peerB);check(((Map<?,?>)field(a.node,"reverseRequests")).isEmpty(),"撤销后残留回连请求");
            }
        });
        run("重复关闭旧共享服务不能移除重建后的回连入口",()->{
            try(Node a=new Node(root.resolve("lifecycle-a").toFile(),port(),"03030303030303030303030303030303");Node b=new Node(root.resolve("lifecycle-b").toFile(),toolPorts(),"04040404040404040404040404040404")){
                LanNode.Peer peer=find(a,b);a.node.verifyPeer(peer);java.lang.reflect.Field connector=LanNode.class.getDeclaredField("peerConnector");connector.setAccessible(true);
                connector.set(a.node,java.lang.reflect.Proxy.newProxyInstance(LanNode.class.getClassLoader(),new Class<?>[]{connector.getType()},(proxy,method,values)->{throw new ConnectException("模拟单向阻断");}));
                int port=b.node.port()+1;ToolsHttp old=new ToolsHttp(port,true,(request,response)->response.json("{}",""),b.node.security());old.close();
                try(ToolsHttp current=new ToolsHttp(port,true,(request,response)->response.bytes(200,"text/plain","rebuilt".getBytes(java.nio.charset.StandardCharsets.UTF_8),""),b.node.security());ToolsGateway gateway=new ToolsGateway(a.node.security(),peer,port,null,a.node)){
                    old.close();URI uri=URI.create(gateway.url());String redirect=http(uri.getPort(),uri.getRawPath()+"?"+uri.getRawQuery(),"");String cookie=Arrays.stream(redirect.split("\r\n")).filter(s->s.startsWith("Set-Cookie: ")).findFirst().orElseThrow().substring(12).split(";")[0];
                    check(http(uri.getPort(),"/",cookie).endsWith("rebuilt"),"新服务无法通过回连接收真实 HTTP 请求");
                }
            }
        });
        run("历史文件授权失效只更新附件卡片且不阻止连接",()->{
            File folder=root.resolve("unavailable-source").toFile();String aid="05050505050505050505050505050505",bid="06060606060606060606060606060606";
            try(Node a=new Node(folder,port(),aid);Node b=new Node(root.resolve("unavailable-peer").toFile(),port(),bid)) {
                LanNode.Peer peer=find(a,b);a.node.verifyPeer(peer);
                a.node.sendSources(peer,Collections.singletonList(new LanNode.Source(){public String key(){return "content://expired/document/old";}public String name(){return "旧文件.txt";}public long size(){return 3;}public long modified(){return 1000;}public InputStream open(long offset){return new ByteArrayInputStream(new byte[]{1,2,3});}}));
            }
            for(int i=0;i<2;i++) {
                List<String> errors=new ArrayList<>(),states=new ArrayList<>();
                try(LanNode restored=new LanNode(port(),true,"restored","host","恢复","",new LanNode.Listener(){public void peersChanged(){}public void error(String text){errors.add(text);}public void message(LanNode.Peer p,LanNode.Message m,boolean u){}public void fileProgress(LanNode.Peer p,long packet,long file,int percent,String state,double rate){states.add(state);}})) {
                    restored.setDeviceId(aid);restored.setSourceResolver(key->{throw new IOException("来源没有持久只读授权");});restored.configureSecurity(folder,(name,code)->true);restored.start();
                    check(errors.isEmpty(),"启动重复弹出旧附件错误");check(states.contains("文件已删除或不可用"),"没有更新失效附件状态");check(restored.networkAvailable(),"旧附件阻止了连接恢复");
                }
            }
        });
        run("离线加密出站文件取消发送、未送达删除撤销与持久化报价格式兼容",()->{
            int ap=port(),bp=port();String aid="07070707070707070707070707070707",bid="08080808080808080808080808080808";
            File senderRoot=root.resolve("cancel-sec-a").toFile(),receiverRoot=root.resolve("cancel-sec-b").toFile();
            byte[] payload=new byte[4096];new Random(51).nextBytes(payload);
            File source=root.resolve("cancel-sec.bin").toFile();Files.write(source.toPath(),payload);
            try(Node a=new Node(senderRoot,ap,aid)) {
                LanNode.Peer target;
                try(Node b=new Node(receiverRoot,bp,bid)) { target=find(a,b); a.node.verifyPeer(target); }
                LanNode.Message cancelled=a.node.sendMessage(target,"离线加密取消",Collections.singletonList(source));
                check(a.node.canCancelOutgoing(target.id(),cancelled.number),"离线加密发送应允许取消");
                check(a.node.hasUndeliveredOutgoing(target.id(),cancelled.number,cancelled.state),"离线加密发送应识别为未送达");
                check(a.node.cancelOutgoing(target.id(),cancelled.number,cancelled.state,true),"离线加密取消发送失败");
                check(!a.node.canCancelOutgoing(target.id(),cancelled.number)&&!a.node.offering(target.id(),cancelled.number,0),"取消后未撤销加密报价");
                check(source.isFile(),"取消发送不能删除本机原文件");
                try(DataInputStream in=new DataInputStream(new FileInputStream(new File(senderRoot,"transfer-offers.dat")))) {
                    check(in.readInt()==2&&in.readInt()==0,"取消后持久化格式或条数不正确");
                }
                LanNode.Message directDeleted=a.node.sendMessage(target,"离线直删加密",Collections.singletonList(source));
                check(a.node.cancelUndeliveredOutgoing(target.id(),directDeleted.number,"等待确认",false),"删除未送达加密消息未撤销报价");
                check(!a.node.offering(target.id(),directDeleted.number,0),"未送达加密报价仍残留");
                try(DataInputStream in=new DataInputStream(new FileInputStream(new File(senderRoot,"transfer-offers.dat")))) {
                    check(in.readInt()==2&&in.readInt()==0,"直删未送达后持久化报价未清空");
                }
                LanNode.Message deliveredMsg;
                try(Node b=new Node(receiverRoot,bp,bid)) {
                    LanNode.Peer back=find(b,a);
                    check(b.incoming.poll(500,TimeUnit.MILLISECONDS)==null,"已取消或已删除的离线加密消息不应在上线后投递");
                    boolean blocked=false;try{b.node.receiveFile(back,cancelled.number,cancelled.files.get(0),root.resolve("should-not-download.bin").toFile(),new LanNode.Transfer(),null);}catch(IOException expected){blocked=true;}
                    check(blocked,"已撤销加密报价仍可下载");
                    LanNode.Peer onlineTarget=find(a,b);
                    deliveredMsg=a.node.sendMessage(onlineTarget,"已送达保留报价",Collections.singletonList(source));
                    LanNode.Message received=b.incoming.poll(5,TimeUnit.SECONDS);check(received!=null&&received.number==deliveredMsg.number,"已送达加密邀请未到达");
                    long waitEnd=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
                    while(a.node.hasUndeliveredOutgoing(onlineTarget.id(),deliveredMsg.number,deliveredMsg.state)&&System.nanoTime()<waitEnd)Thread.sleep(10);
                    check(!a.node.cancelUndeliveredOutgoing(onlineTarget.id(),deliveredMsg.number,"已送达",false),"删除已送达消息不能撤销对方下载报价");
                    check(a.node.offering(onlineTarget.id(),deliveredMsg.number,0),"已送达报价被误撤销");
                    File downloaded=root.resolve("kept-delivered.bin").toFile();
                    b.node.receiveFile(back,received.number,received.files.get(0),downloaded,new LanNode.Transfer(),null);
                    check(Arrays.equals(payload,Files.readAllBytes(downloaded.toPath())),"已送达加密文件下载内容不一致");
                }
            }
        });
        System.out.println("ANDROID SECURE: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);
    }
}
