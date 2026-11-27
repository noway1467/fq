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
        final BlockingQueue<String> canceled=new LinkedBlockingQueue<>(),states=new LinkedBlockingQueue<>();
        volatile java.util.function.BiConsumer<LanNode.Peer,Long> cancellation;
        Node(File root,int port,String id)throws IOException{
            node=new LanNode(port,true,id,"Android",id,"test",new LanNode.Listener(){public void peersChanged(){}public void error(String text){System.out.println("NOTICE "+text);}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(!message.outgoing)incoming.add(message);}public void offerCanceled(LanNode.Peer peer,long packet){canceled.add(peer.id()+"/"+packet);java.util.function.BiConsumer<LanNode.Peer,Long> callback=cancellation;if(callback!=null)callback.accept(peer,packet);}public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state,double rate){states.add(packet+"/"+file+"/"+state);}});
            node.setDeviceId(id);node.configureSecurity(root,(name,code)->true);node.start();
        }
        public void close(){node.close();}
    }
    private static LanNode.Peer find(Node a,Node b)throws Exception {a.node.probe(new InetSocketAddress("127.0.0.1",b.node.port()));for(int i=0;i<500;i++){for(LanNode.Peer peer:a.node.peers())if(peer.endpoint.getPort()==b.node.port())return peer;Thread.sleep(10);}throw new AssertionError("发现超时");}
    private static Object field(LanNode node,String name)throws Exception{java.lang.reflect.Field value=LanNode.class.getDeclaredField(name);value.setAccessible(true);return value.get(node);}
    private static void raw(Node sender,Node receiver,byte[] bytes)throws Exception{((DatagramSocket)field(sender.node,"udp")).send(new DatagramPacket(bytes,bytes.length,new InetSocketAddress("127.0.0.1",receiver.node.port())));}
    private static String http(int port,String path,String cookie)throws IOException {try(Socket socket=new Socket("127.0.0.1",port)){socket.setSoTimeout(10000);socket.getOutputStream().write(("GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nConnection: close\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII));return new String(socket.getInputStream().readAllBytes(),java.nio.charset.StandardCharsets.UTF_8);}}
    private static String delayedLogin(int port,String cookie)throws IOException{try(Socket socket=new Socket("127.0.0.1",port)){socket.setSoTimeout(25000);socket.getOutputStream().write(("POST /api/login HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII));return new String(socket.getInputStream().readAllBytes(),java.nio.charset.StandardCharsets.UTF_8);}}
    private static void disconnectTests(Path root) {
        run("断开：迟到的配对确认不能保存信任",()->{
            for(boolean outgoing:new boolean[]{false,true}) {
                Path profile=root.resolve("disconnect-pair-"+outgoing);
                ExecutorService worker=Executors.newSingleThreadExecutor();
                CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
                try(Node a=new Node(profile.toFile(),port(),"15151515151515151515151515151515");
                    Node b=new Node(root.resolve("disconnect-remote-"+outgoing).toFile(),port(),"16161616161616161616161616161616")) {
                    LanNode.Peer peerB=find(a,b),peerA=find(b,a);String fingerprint=b.node.security().fingerprint;
                    a.node.security().confirmation=(name,code)->{
                        entered.countDown();
                        try {if(!release.await(5,TimeUnit.SECONDS))throw new IOException("配对确认未放行");}
                        catch(InterruptedException e){Thread.currentThread().interrupt();throw new IOException(e);}
                        return true;
                    };
                    Future<?> pairing=worker.submit(()->{try{if(outgoing)a.node.verifyPeer(peerB);else b.node.verifyPeer(peerA);}catch(IOException expected){}});
                    try {
                        check(entered.await(5,TimeUnit.SECONDS),"未进入真实 TLS 配对确认");
                        // 服务先停止网络，完整清理仍可能在 I/O 队列中等待。
                        a.node.stopListening();release.countDown();pairing.get(5,TimeUnit.SECONDS);
                        Object gate=field(a.node,"gate");Set<?> sockets=(Set<?>)field(a.node,"connections");long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);
                        while(System.nanoTime()<until){synchronized(gate){if(sockets.isEmpty())break;}Thread.sleep(10);}
                        synchronized(gate){check(sockets.isEmpty(),"旧 TLS 处理未结束");}
                        check(!a.node.security().isTrusted(peerB.deviceId,fingerprint),"断开后的旧确认仍建立了信任");
                        check(!new SecureIdentity(profile.toFile()).isTrusted(peerB.deviceId,fingerprint),"重启后恢复了断开时失效的配对");
                    } finally {release.countDown();pairing.get(5,TimeUnit.SECONDS);}
                } finally {release.countDown();worker.shutdownNow();check(worker.awaitTermination(5,TimeUnit.SECONDS),"配对测试线程未退出");}
            }
        });
    }
    public static void main(String[] args)throws Exception {
        Path root=Paths.get(args[0]);Files.createDirectories(root);
        if(args.length>1&&args[1].equals("quota")){quotaTest(root);System.out.println("ANDROID QUOTA: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);return;}
        if(args.length>1&&args[1].equals("partial")){partialTests(root);System.out.println("ANDROID PARTIAL TLS: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);return;}
        if(args.length>1&&args[1].equals("text-cancel")){textCancelTests(root);System.out.println("ANDROID TEXT CANCEL TLS: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);return;}
        disconnectTests(root);
        if(args.length>1&&args[1].equals("disconnect")){System.out.println("ANDROID DISCONNECT TLS: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);return;}
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
        run("加密附件保存确认跨重启保留且完成报价不能取消",()->{
            File senderRoot=root.resolve("completed-restart-a").toFile(),receiverRoot=root.resolve("completed-restart-b").toFile();
            String aid="09090909090909090909090909090909",bid="10101010101010101010101010101010";File source=root.resolve("completed-restart.bin").toFile();Files.write(source.toPath(),new byte[]{1,2,3});long packet;
            try(Node sender=new Node(senderRoot,port(),aid);Node receiver=new Node(receiverRoot,port(),bid)){
                LanNode.Peer target=find(sender,receiver);sender.node.verifyPeer(target);LanNode.Message sent=sender.node.sendMessage(target,"保存确认",Collections.singletonList(source));packet=sent.number;
                LanNode.Message received=receiver.incoming.poll(5,TimeUnit.SECONDS);check(received!=null,"邀请未到达");LanNode.Peer back=find(receiver,sender);
                receiver.node.receiveFile(back,received.number,received.files.get(0),root.resolve("completed-restart-copy.bin").toFile(),new LanNode.Transfer(),null);
                File stored=new File(senderRoot,"transfer-offers.dat");long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);
                while(System.nanoTime()<deadline&&!new String(Files.readAllBytes(stored.toPath()),java.nio.charset.StandardCharsets.UTF_8).contains("对方已校验保存"))Thread.sleep(10);
                check(new String(Files.readAllBytes(stored.toPath()),java.nio.charset.StandardCharsets.UTF_8).contains("对方已校验保存"),"保存完成状态未落盘");check(!sender.node.canCancelOutgoing(target.id(),packet),"完成文件仍可取消");
            }
            try(Node restored=new Node(senderRoot,port(),aid)){
                check(!restored.node.canCancelOutgoing("device:"+bid,packet),"重启丢失文件完成状态");check(!restored.node.cancelOutgoing("device:"+bid,packet,"已送达",true),"完成报价被取消");check(restored.node.offering("device:"+bid,packet,0),"完成报价丢失");
            }
        });
        System.out.println("ANDROID SECURE: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);
    }
    private static void quotaTest(Path root){
        run("文件额度：完成释放、扩大额度及千项邀请重启",()->{
            File source=root.resolve("quota.txt").toFile();Files.write(source.toPath(),new byte[]{1,2,3});File profile=root.resolve("quota-a").toFile();
            try(Node a=new Node(profile,port(),"12121212121212121212121212121212");Node b=new Node(root.resolve("quota-b").toFile(),port(),"34343434343434343434343434343434")){
                LanNode.Peer target=find(a,b),back=find(b,a);a.node.verifyPeer(target);a.node.setPendingFileLimit(100);LanNode.Message first=null;
                for(int i=0;i<5;i++){a.node.sendMessage(target,"",Collections.nCopies(20,source));LanNode.Message got=b.incoming.poll(5,TimeUnit.SECONDS);check(got!=null,"邀请未收到");if(first==null)first=got;}
                boolean full=false;try{a.node.sendMessage(target,"",Collections.singletonList(source));}catch(IOException e){full=true;}check(full,"上限未生效");
                b.node.receiveFile(back,first.number,first.files.get(0),root.resolve("quota-received.txt").toFile(),new LanNode.Transfer(),null);
                String expected=first.number+"/0/对方已校验保存";long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);while(!a.states.contains(expected)&&System.nanoTime()<deadline)Thread.sleep(10);check(a.states.contains(expected),"未收到保存确认");
                a.node.sendMessage(target,"",Collections.singletonList(source));check(b.incoming.poll(5,TimeUnit.SECONDS)!=null,"已收完仍占额度");a.node.setPendingFileLimit(2000);
                for(int i=0;i<51;i++){a.node.sendMessage(target,"",Collections.nCopies(20,source));check(b.incoming.poll(5,TimeUnit.SECONDS)!=null,"扩大额度后未送达");}
                check(((Map<?,?>)field(a.node,"offers")).size()==1121,"邀请数量错误");
            }
            try(Node restored=new Node(profile,port(),"12121212121212121212121212121212")){check(((Map<?,?>)field(restored.node,"offers")).size()==1121,"千项邀请重启丢失");}
        });
    }
    private static Object member(Object target,String name)throws Exception {java.lang.reflect.Field value=target.getClass().getDeclaredField(name);value.setAccessible(true);return value.get(target);}
    private static void textCancelTests(Path root)throws Exception {
        run("普通 TLS 文本取消关闭正在握手的连接，异步异常不恢复重试",()->{
            try(ServerSocket server=new ServerSocket(0,1,InetAddress.getLoopbackAddress());Node sender=new Node(root.resolve("text-a").toFile(),port(),"11111111111111111111111111111111")) {
                server.setSoTimeout(5000);LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),server.getLocalPort()),"blocked","host","等待 TLS","");peer.deviceId="22222222222222222222222222222222";peer.fingerprint="a".repeat(64);peer.utf8=true;
                LanNode.Message sent=sender.node.sendMessage(peer,"0",Collections.emptyList());
                try(Socket raw=server.accept()) {
                    raw.setSoTimeout(3000);check(Arrays.equals(new byte[]{'F','Q','2','\n'},raw.getInputStream().readNBytes(4)),"尚未进入 TLS 连接");Object pending; synchronized(field(sender.node,"gate")){pending=((Map<?,?>)field(sender.node,"pending")).get(sent.number);}
                    check(pending!=null&&sender.node.cancelOutgoing(peer.id(),sent.number,sent.state,true),"握手中的文本不能取消");java.util.concurrent.atomic.AtomicBoolean sending=(java.util.concurrent.atomic.AtomicBoolean)member(pending,"sending");waitFor(()->!sending.get(),"取消后 TLS 发送线程未结束");
                    check("已取消发送".equals(((LanNode.Message)member(pending,"message")).state)&&!sender.node.canCancelOutgoing(peer.id(),sent.number),"异步异常覆盖取消状态或恢复重试");
                }
            }
        });
        run("证书失败等待状态锁期间取消，失败分支不得覆盖取消终态",()->{
            SecureIdentity remote=new SecureIdentity(root.resolve("race-remote").toFile());
            try(ServerSocket server=new ServerSocket(0,1,InetAddress.getLoopbackAddress());Node sender=new Node(root.resolve("race-a").toFile(),port(),"33333333333333333333333333333333")) {
                server.setSoTimeout(5000);LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),server.getLocalPort()),"wrong-pin","host","错误证书","");peer.deviceId="44444444444444444444444444444444";peer.fingerprint="f".repeat(64);peer.utf8=true;LanNode.Message sent=sender.node.sendMessage(peer,"123",Collections.emptyList());
                try(Socket raw=server.accept()) {
                    raw.setSoTimeout(3000);check(Arrays.equals(new byte[]{'F','Q','2','\n'},raw.getInputStream().readNBytes(4)),"尚未进入证书失败连接");Object gate=field(sender.node,"gate"),pending;
                    java.util.concurrent.atomic.AtomicBoolean rejected=new java.util.concurrent.atomic.AtomicBoolean();
                    FutureTask<Void> handshake=new FutureTask<>(()->{try(javax.net.ssl.SSLSocket tls=remote.server(raw,false)){return null;}catch(IOException expected){rejected.set(true);return null;}});
                    Thread receiver=new Thread(handshake,"text-cancel-wrong-pin");
                    synchronized(gate) {
                        pending=((Map<?,?>)field(sender.node,"pending")).get(sent.number);check(pending!=null,"发送任务已提前结束");receiver.start();
                        waitFor(()->Thread.getAllStackTraces().entrySet().stream().anyMatch(entry->entry.getKey().getState()==Thread.State.BLOCKED&&Arrays.stream(entry.getValue()).anyMatch(frame->frame.getClassName().equals(LanNode.class.getName())&&frame.getMethodName().equals("sendSecurePending"))),"未截住身份失败分支等待状态锁的窗口");
                        check(sender.node.cancelOutgoing(peer.id(),sent.number,sent.state,true),"身份失败等待期间不能取消");
                    }
                    handshake.get(8,TimeUnit.SECONDS);java.util.concurrent.atomic.AtomicBoolean sending=(java.util.concurrent.atomic.AtomicBoolean)member(pending,"sending");waitFor(()->!sending.get(),"取消后证书失败线程未结束");check(rejected.get(),"测试未实际触发证书拒绝");
                    check("已取消发送".equals(((LanNode.Message)member(pending,"message")).state)&&!sender.node.canCancelOutgoing(peer.id(),sent.number),"身份核对失败覆盖了已取消发送");
                }
            }
        });
    }
    private static void waitFor(java.util.function.BooleanSupplier condition,String message)throws Exception {
        long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);
        while(!condition.getAsBoolean()&&System.nanoTime()<deadline)Thread.sleep(10);check(condition.getAsBoolean(),message);
    }
    private static final class PausedSource implements LanNode.Source {
        final byte[] bytes=new byte[8*1024*1024];
        final java.util.concurrent.atomic.AtomicBoolean pause=new java.util.concurrent.atomic.AtomicBoolean();
        final java.util.concurrent.atomic.AtomicLong openedOffset=new java.util.concurrent.atomic.AtomicLong();
        final CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        PausedSource(){new Random(79).nextBytes(bytes);}
        public String key(){return "partial-test-source";}public String name(){return "partial.bin";}public long size(){return bytes.length;}public long modified(){return 123000;}
        public InputStream open(long offset) {
            openedOffset.set(offset);
            return new InputStream(){int position=(int)offset;
                private void waitAtBoundary()throws IOException {
                    if(pause.get()&&position>=131072){entered.countDown();try{if(!release.await(10,TimeUnit.SECONDS))throw new IOException("测试未解除源流暂停");}catch(InterruptedException e){Thread.currentThread().interrupt();throw new IOException(e);}}
                }
                public int read()throws IOException {waitAtBoundary();return position>=bytes.length?-1:bytes[position++]&255;}
                public int read(byte[] target,int start,int count)throws IOException {waitAtBoundary();if(position>=bytes.length)return -1;int size=Math.min(count,bytes.length-position);System.arraycopy(bytes,position,target,start,size);position+=size;return size;}
            };
        }
    }
    private static void sendRelease(Node sender,LanNode.Peer peer,String body)throws Exception {
        java.lang.reflect.Method method=LanNode.class.getDeclaredMethod("sendReleaseFiles",LanNode.Peer.class,String.class);method.setAccessible(true);
        try{method.invoke(sender.node,peer,body);}catch(java.lang.reflect.InvocationTargetException e){if(e.getCause() instanceof Exception)throw (Exception)e.getCause();throw e;}
    }
    private static void partialTests(Path root)throws Exception {
        run("TLS 接收暂停保留断点，继续同一邀请并校验原文件",()->{
            ExecutorService worker=Executors.newSingleThreadExecutor();PausedSource source=new PausedSource();
            try(Node sender=new Node(root.resolve("pause-a").toFile(),port(),"41414141414141414141414141414141");Node receiver=new Node(root.resolve("pause-b").toFile(),port(),"42424242424242424242424242424242")) {
                LanNode.Peer target=find(sender,receiver),back=find(receiver,sender);sender.node.verifyPeer(target);
                sender.node.sendSources(target,Collections.singletonList(source));LanNode.Message message=receiver.incoming.poll(5,TimeUnit.SECONDS);check(message!=null,"暂停测试邀请未到达");
                File destination=root.resolve("user-paused.bin").toFile(),part=LanNode.partialFile(back,message.number,message.files.get(0),destination);LanNode.Transfer task=new LanNode.Transfer();source.pause.set(true);
                Future<?> download=worker.submit(()->{try{receiver.node.receiveFile(back,message.number,message.files.get(0),destination,task,null);throw new AssertionError("暂停后仍提交文件");}catch(IOException expected){}});
                check(source.entered.await(5,TimeUnit.SECONDS),"未进入文件发送流");waitFor(()->part.length()>0,"暂停前没有非空断点");task.pause();source.release.countDown();download.get(8,TimeUnit.SECONDS);
                long offset=part.length();check(task.isPaused()&&offset>0&&offset<source.bytes.length&&!destination.exists(),"暂停清除了断点或提交不完整文件");source.pause.set(false);
                receiver.node.receiveFile(back,message.number,message.files.get(0),destination,new LanNode.Transfer(),null);
                check(source.openedOffset.get()==offset&&SecureIdentity.hashFile(destination).equals(message.files.get(0).sha256),"暂停后续传偏移或SHA-256错误");
            }finally{source.release.countDown();worker.shutdownNow();check(worker.awaitTermination(5,TimeUnit.SECONDS),"暂停测试线程未退出");}
        });
        run("TLS 活跃下载取消通过安全 M 通道通知并删除非空断点，保留同批正式文件",()->{
            ExecutorService worker=Executors.newSingleThreadExecutor();PausedSource source=new PausedSource();
            try(Node sender=new Node(root.resolve("live-a").toFile(),port(),"11111111111111111111111111111111");Node receiver=new Node(root.resolve("live-b").toFile(),port(),"22222222222222222222222222222222")) {
                LanNode.Peer target=find(sender,receiver),back=find(receiver,sender);sender.node.verifyPeer(target);
                File small=root.resolve("live-small.txt").toFile();Files.writeString(small.toPath(),"保留已保存正式文件");
                LanNode.Source completed=new LanNode.Source(){public String key(){return "completed-source";}public String name(){return small.getName();}public long size(){return small.length();}public long modified(){return small.lastModified();}public InputStream open(long offset)throws IOException{FileInputStream input=new FileInputStream(small);input.getChannel().position(offset);return input;}};
                LanNode.Message sent=sender.node.sendSources(target,Arrays.asList(completed,source));LanNode.Message message=receiver.incoming.poll(5,TimeUnit.SECONDS);check(message!=null,"邀请未到达");
                File saved=root.resolve("live-saved.txt").toFile();receiver.node.receiveFile(back,message.number,message.files.get(0),saved,new LanNode.Transfer(),null);waitFor(()->sender.states.stream().anyMatch(state->state.equals(sent.number+"/0/对方已校验保存")),"第一份文件未确认保存");
                File destination=root.resolve("live-partial.bin").toFile(),part=LanNode.partialFile(back,message.number,message.files.get(1),destination);LanNode.Transfer task=new LanNode.Transfer();
                java.util.concurrent.atomic.AtomicReference<Future<?>> cleanup=new java.util.concurrent.atomic.AtomicReference<>();
                receiver.cancellation=(peer,packet)->{if(peer.id().equals(back.id())&&packet==message.number){task.close();cleanup.set(worker.submit(()->{try{Files.deleteIfExists(part.toPath());}catch(IOException e){throw new UncheckedIOException(e);}}));}};source.pause.set(true);
                Future<?> download=worker.submit(()->{try{receiver.node.receiveFile(back,message.number,message.files.get(1),destination,task,null);throw new AssertionError("活跃取消仍提交了文件");}catch(IOException expected){ }});
                check(source.entered.await(5,TimeUnit.SECONDS),"发送源未进入可控暂停");waitFor(()->part.length()>0,"取消前未写入非空断点");check(sender.node.hasActiveOutgoingTransfer(target.id(),sent.number),"没有真实活跃发送流");
                check(sender.node.cancelOutgoing(target.id(),sent.number,"已送达",true),"取消失败");source.release.countDown();download.get(8,TimeUnit.SECONDS);waitFor(task::isCancelled,"下载中止后未收到取消通知");
                waitFor(()->cleanup.get()!=null,"取消回调没有安排释放句柄后的清理");cleanup.get().get(5,TimeUnit.SECONDS);
                check(receiver.canceled.contains(back.id()+"/"+sent.number),"未收到经过鉴权的 TLS 取消通知");check(!part.exists()&&!destination.exists(),"取消后残留断点或未完成正式文件");
                check(Arrays.equals(Files.readAllBytes(small.toPath()),Files.readAllBytes(saved.toPath())),"取消误删同批已保存文件");check(sender.node.offering(target.id(),sent.number,0)&&!sender.node.offering(target.id(),sent.number,1),"取消误撤完成报价或保留未完成报价");
            } finally {source.release.countDown();worker.shutdownNow();check(worker.awaitTermination(5,TimeUnit.SECONDS),"下载测试线程未退出");}
        });
        run("取消通知丢失与发送方重启后，续传读取 REVOKED 清断点且保留正式文件",()->{
            int senderPort=port();String aid="33333333333333333333333333333333",bid="44444444444444444444444444444444";File folder=root.resolve("revoked-a").toFile();PausedSource source=new PausedSource();
            try(Node receiver=new Node(root.resolve("revoked-b").toFile(),port(),bid)) {
                LanNode.Message message;LanNode.Peer back;
                try(Node sender=new Node(folder,senderPort,aid)) {
                    LanNode.Peer target=find(sender,receiver);back=find(receiver,sender);sender.node.verifyPeer(target);sender.node.sendSources(target,Collections.singletonList(source));message=receiver.incoming.poll(5,TimeUnit.SECONDS);check(message!=null,"邀请未到达");
                    check(sender.node.cancelOutgoing(target.id(),message.number,"已送达",false),"丢通知撤报价失败");
                }
                try(Node restored=new Node(folder,senderPort,aid)) {
                    File target=root.resolve("revoked-destination.bin").toFile(),part=LanNode.partialFile(back,message.number,message.files.get(0),target),saved=root.resolve("revoked-saved.txt").toFile();Files.writeString(saved.toPath(),"已保存");Files.write(part.toPath(),Arrays.copyOf(source.bytes,8192));
                    boolean revoked=false;try{receiver.node.receiveFile(back,message.number,message.files.get(0),target,new LanNode.Transfer(),null);}catch(LanNode.OfferRevokedException expected){revoked=true;}
                    check(revoked&&!part.exists()&&!target.exists(),"REVOKED 未识别或残留断点");check(Files.readString(saved.toPath()).equals("已保存"),"REVOKED 误删正式文件");check(receiver.canceled.isEmpty(),"无通知场景意外依赖了在线通知");
                    // 同一终态到来前若未取得文件锁，绝不删除另一任务持有的断点。
                    Files.write(part.toPath(),new byte[128]);try(java.nio.channels.FileChannel owner=java.nio.channels.FileChannel.open(part.toPath(),StandardOpenOption.READ,StandardOpenOption.WRITE);java.nio.channels.FileLock lock=owner.lock()) {
                        boolean owned=false;try{receiver.node.receiveFile(back,message.number,message.files.get(0),target,new LanNode.Transfer(),null);}catch(IOException|java.nio.channels.OverlappingFileLockException expected){owned=true;}
                        check(owned&&part.isFile()&&owner.size()==128,"没有断点所有权却删除了文件");
                    }
                }
            }
        });
        run("普通 TLS 流断开保留非空断点并从同一报价续传通过 SHA-256",()->{
            ExecutorService worker=Executors.newSingleThreadExecutor();PausedSource source=new PausedSource();
            try(Node sender=new Node(root.resolve("network-a").toFile(),port(),"55555555555555555555555555555555");Node receiver=new Node(root.resolve("network-b").toFile(),port(),"66666666666666666666666666666666")) {
                LanNode.Peer target=find(sender,receiver),back=find(receiver,sender);sender.node.verifyPeer(target);sender.node.sendSources(target,Collections.singletonList(source));LanNode.Message message=receiver.incoming.poll(5,TimeUnit.SECONDS);check(message!=null,"邀请未到达");
                File destination=root.resolve("network-resume.bin").toFile(),part=LanNode.partialFile(back,message.number,message.files.get(0),destination);LanNode.Transfer task=new LanNode.Transfer();source.pause.set(true);
                Future<?> download=worker.submit(()->{try{receiver.node.receiveFile(back,message.number,message.files.get(0),destination,task,null);throw new AssertionError("测试没有中断下载");}catch(IOException expected){check(!(expected instanceof LanNode.OfferRevokedException),"普通断网误报取消");}});
                check(source.entered.await(5,TimeUnit.SECONDS),"发送源未暂停");waitFor(()->part.length()>0,"普通断网前没有断点");
                Object gate=field(sender.node,"gate");List<Socket> active;
                synchronized(gate){Object offer=((Map<?,?>)field(sender.node,"offers")).get(message.number+":0");java.lang.reflect.Field sockets=offer.getClass().getDeclaredField("activeSockets");sockets.setAccessible(true);active=new ArrayList<>();for(Object socket:(Set<?>)sockets.get(offer))active.add((Socket)socket);}
                check(!active.isEmpty(),"没有真实发送连接");for(Socket socket:active)socket.close();source.release.countDown();download.get(8,TimeUnit.SECONDS);
                long offset=part.length();check(offset>0&&offset<source.bytes.length&&!task.isCancelled()&&receiver.canceled.isEmpty(),"普通断网误删断点或触发取消");source.pause.set(false);
                receiver.node.receiveFile(back,message.number,message.files.get(0),destination,new LanNode.Transfer(),null);
                check(source.openedOffset.get()==offset,"没有从实际断点偏移继续");check(!part.exists()&&Arrays.equals(source.bytes,Files.readAllBytes(destination.toPath()))&&SecureIdentity.hashFile(destination).equals(message.files.get(0).sha256),"续传文件 SHA-256 不一致");System.out.println("DETAIL TLS 续传偏移="+offset);
            } finally {source.release.countDown();worker.shutdownNow();check(worker.awaitTermination(5,TimeUnit.SECONDS),"断网测试线程未退出");}
        });
        run("取消消息拦截明文冒用、未配对与证书冒用；有效但未授权报价不误报 REVOKED；数字拒绝兼容",()->{
            String aid="77777777777777777777777777777777",bid="88888888888888888888888888888888",cid="99999999999999999999999999999999";PausedSource source=new PausedSource();
            try(Node sender=new Node(root.resolve("auth-a").toFile(),port(),aid);Node receiver=new Node(root.resolve("auth-b").toFile(),port(),bid);Node attacker=new Node(root.resolve("auth-c").toFile(),port(),cid)) {
                LanNode.Peer target=find(sender,receiver),back=find(receiver,sender),victim=find(attacker,receiver);sender.node.verifyPeer(target);sender.node.sendSources(target,Collections.singletonList(source));LanNode.Message message=receiver.incoming.poll(5,TimeUnit.SECONDS);check(message!=null,"邀请未到达");
                File destination=root.resolve("auth-destination.bin").toFile(),part=LanNode.partialFile(back,message.number,message.files.get(0),destination);Files.write(part.toPath(),new byte[128]);
                for(String fingerprint:new String[]{sender.node.security().fingerprint,null})raw(attacker,receiver,Protocol.encode(72001,aid,"Android",Protocol.RELEASE|Protocol.UTF8,"cancel:"+message.number,null,aid,null,fingerprint));
                raw(attacker,receiver,Protocol.encode(72002,aid,"Android",Protocol.RELEASE|Protocol.UTF8,"cancel:"+message.number,null));raw(attacker,receiver,Protocol.encode(72003,"barrier","Android",Protocol.MESSAGE|Protocol.UTF8,"cancel-barrier",null,"abababababababababababababababab"));
                LanNode.Message barrier=receiver.incoming.poll(5,TimeUnit.SECONDS);check(barrier!=null&&barrier.text.equals("cancel-barrier"),"未完成明文攻击处理");check(receiver.canceled.isEmpty()&&part.length()==128,"明文取消进入 TLS 会话");
                receiver.node.security().confirmation=(name,code)->false;boolean unpaired=false;try{sendRelease(attacker,victim,"cancel:"+message.number);}catch(IOException expected){unpaired=true;}check(unpaired&&receiver.canceled.isEmpty()&&part.length()==128,"未配对设备清理了受害者断点");
                receiver.node.security().confirmation=(name,code)->true;
                try(Node impostor=new Node(root.resolve("auth-impostor").toFile(),port(),aid)) {
                    boolean blocked=false;try{sendRelease(impostor,victim,"cancel:"+message.number);}catch(IOException expected){blocked=true;}check(blocked&&receiver.node.security().isTrusted(aid,sender.node.security().fingerprint)&&receiver.canceled.isEmpty()&&part.length()==128,"冒用设备 ID/证书修改了原会话");
                }
                LanNode.Peer unauthorized=find(attacker,sender);attacker.node.verifyPeer(unauthorized);File unowned=root.resolve("unauthorized.bin").toFile(),kept=LanNode.partialFile(unauthorized,message.number,message.files.get(0),unowned);Files.write(kept.toPath(),new byte[128]);
                boolean rejected=false;try{attacker.node.receiveFile(unauthorized,message.number,message.files.get(0),unowned,new LanNode.Transfer(),null);}catch(IOException expected){check(!(expected instanceof LanNode.OfferRevokedException),"有效但未授权报价误报 REVOKED");rejected=true;}check(rejected&&kept.length()==128,"未授权请求误删断点或泄露文件");
                attacker.node.verifyPeer(victim);sendRelease(attacker,victim,"cancel:"+message.number);check(receiver.canceled.poll(3,TimeUnit.SECONDS).equals("device:"+cid+"/"+message.number)&&part.length()==128,"另一已配对设备的同包号取消混入受害者会话");
                receiver.node.reject(back,message.number);waitFor(()->!sender.node.offering(target.id(),message.number,0),"数字拒绝未撤报价");check(sender.states.stream().anyMatch(state->state.endsWith("/对方已拒绝")),"数字拒绝没有保持对方已拒绝状态");
                try{Long.parseLong("cancel:"+message.number);throw new AssertionError("旧版数字解析误处理取消编码");}catch(NumberFormatException expected){ }
            }
        });
    }
}
