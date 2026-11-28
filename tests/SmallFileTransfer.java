import org.feiqlight.android.core.*;
import org.feiqlight.android.TestPorts;
import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import javax.net.ssl.SSLSocket;

public final class SmallFileTransfer {
    private static final String A_ID="11111111111111111111111111111111",B_ID="22222222222222222222222222222222";
    private static int passed,failed;
    private interface Test {void run()throws Exception;}
    private interface Condition {boolean get()throws Exception;}
    private static void check(boolean value,String reason){if(!value)throw new AssertionError(reason);}
    private static Object field(Object object,String name)throws Exception {Field field=object.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(object);}
    private static void waitFor(Condition condition,String reason)throws Exception {
        long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(10);
        while(!condition.get()){if(System.nanoTime()>=until)throw new AssertionError(reason);Thread.sleep(5);}
    }
    private static final LanNode.Listener QUIET=new LanNode.Listener(){public void peersChanged(){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}public void error(String text){}};
    private static LanNode node(Path root,String id,boolean start,LanNode.Listener listener)throws Exception {
        LanNode node=new LanNode(TestPorts.chat(),true,"wp5-"+id.substring(0,2),"test","WP5","",listener);
        try{node.setDeviceId(id);node.configureSecurity(root.toFile(),(name,code)->true);if(start)node.start();return node;}catch(Exception error){node.close();throw error;}
    }
    private static LanNode.Peer find(LanNode from,LanNode to,String id)throws Exception {
        from.probe(LanNode.endpoint("127.0.0.1:"+to.port()));
        waitFor(()->from.peers().stream().anyMatch(peer->id.equals(peer.deviceId)),"发现超时");
        return from.peers().stream().filter(peer->id.equals(peer.deviceId)).findFirst().orElseThrow();
    }
    private static final class Item {final long packet;final Protocol.Attachment file;Item(long packet,Protocol.Attachment file){this.packet=packet;this.file=file;}}
    private static final class Pair implements AutoCloseable {
        final LanNode a,b;
        final LanNode.Peer target,back;
        final BlockingQueue<LanNode.Message> incoming=new LinkedBlockingQueue<>();
        final Set<Long> delivered=ConcurrentHashMap.newKeySet();
        final Path root,source;
        volatile Runnable completionHook;
        Pair(Path root)throws Exception {
            this.root=Files.createDirectories(root);source=root.resolve("source.bin");Files.write(source,new byte[]{3,5,7,11});
            LanNode.Listener sender=new LanNode.Listener() {
                public void peersChanged(){}public void error(String text){}
                public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(message.outgoing&&update&&"已送达".equals(message.state))delivered.add(message.number);}
                public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state,double rate){if("对方已校验保存".equals(state)&&completionHook!=null)completionHook.run();}
            };
            a=node(root.resolve("a"),A_ID,true,sender);
            b=node(root.resolve("b"),B_ID,true,new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(!message.outgoing)incoming.add(message);}});
            target=find(a,b,B_ID);a.verifyPeer(target);back=find(b,a,A_ID);
        }
        List<Item> send(int count)throws Exception {
            List<Long> packets=new ArrayList<>();List<Item> files=new ArrayList<>();
            for(int offset=0;offset<count;offset+=20)packets.add(a.sendMessage(target,"",Collections.nCopies(Math.min(20,count-offset),source.toFile())).number);
            for(int index=0;index<packets.size();index++) {
                LanNode.Message message=incoming.poll(10,TimeUnit.SECONDS);check(message!=null,"邀请没有到达");
                for(Protocol.Attachment file:message.files)files.add(new Item(message.number,file));
            }
            waitFor(()->delivered.containsAll(packets),"邀请未确认送达");return files;
        }
        @Override public void close(){b.close();a.close();}
    }
    private static int offers(LanNode node)throws Exception {synchronized(field(node,"gate")){return ((Map<?,?>)field(node,"offers")).size();}}
    @SuppressWarnings("unchecked")
    private static void holdReverseSync(LanNode node,String deviceId)throws Exception {
        waitFor(()->{synchronized(field(node,"gate")){return ((Set<String>)field(node,"activeSyncPeers")).add(deviceId);}},"已有同步连接未结束");
    }
    private static int completed(LanNode node)throws Exception {
        synchronized(field(node,"gate")) {
            int count=0;for(Object offer:((Map<?,?>)field(node,"offers")).values())if((boolean)field(offer,"stored"))count++;return count;
        }
    }
    private static void run(String name,Test test) {
        try{test.run();passed++;System.out.println("PASS "+name);}catch(Exception|AssertionError error){failed++;System.out.println("FAIL "+name+"："+error);error.printStackTrace();}
    }
    public static void main(String[] args)throws Exception {
        Path root=Files.createDirectories(Paths.get(args[0]));
        if(args.length>1&&"crash-writer".equals(args[1])){crashWriter(root);System.exit(1);return;}
        String selected=args.length>1?args[1]:"all";
        if("all".equals(selected)||"batch".equals(selected))run("独立能力、64件连接上限、逐件授权与撤信立即断连",()->batch(root.resolve("batch")));
        if("all".equals(selected)||"boundary".equals(selected))run("批量连接拒绝非F下一帧",()->boundary(root.resolve("boundary")));
        if("all".equals(selected)||"recovery".equals(selected))run("真实进程崩溃、截断确认日志恢复与损坏日志拒绝",()->recovery(root.resolve("recovery")));
        if("all".equals(selected)||"eviction".equals(selected))run("1105完成项淘汰与100待处理额度跨重启",()->eviction(root.resolve("eviction")));
        if("all".equals(selected)||"phase2".equals(selected)) {
            run("快照提交后日志清理失败仍保持发送撤销状态一致",()->journalCleanup(root.resolve("cleanup")));
            run("确认日志失败不发送NEXT且保留本机已保存文件",()->journalFailure(root.resolve("journal-failure")));
            run("关闭快照失败后迟到补刷不清空邀请及确认日志",()->closeFailure(root.resolve("close-failure")));
            run("取消单邀请保留同联系人正反向批量连接",()->cancelIsolation(root.resolve("cancel-isolation")));
            run("拨号及回连等待取消立即归还连接和票据",()->connectCancellation(root.resolve("connect-cancel")));
            run("旧批量连接断开后单次操作经反向P完成",()->staleReverse(root.resolve("stale-reverse")));
        }
        System.out.println("ANDROID SMALL FILE: "+passed+" passed, "+failed+" failed");System.exit(failed==0?0:1);
    }
    private static byte[] capability(byte[] hello,String value) {
        byte[] suffix=(value+'\0').getBytes(StandardCharsets.US_ASCII),result=Arrays.copyOf(hello,hello.length+suffix.length);System.arraycopy(suffix,0,result,hello.length,suffix.length);return result;
    }
    private static void batch(Path root)throws Exception {
        byte[] hello=Protocol.encode(0,"test","host",Protocol.ENTRY|Protocol.UTF8,"","",A_ID,null,"a".repeat(64));
        Protocol.Packet reverse=Protocol.parse(capability(hello,"FQREV=1")),batch=Protocol.parse(capability(hello,"FQBATCH=1"));
        check(reverse.reverseConnections&&!reverse.batchFiles&&batch.batchFiles&&!batch.reverseConnections,"FQBATCH 与 FQREV 未隔离");
        try(Pair pair=new Pair(root)) {
            List<Item> files=pair.send(80);Set<Object> clients=new HashSet<>();Path received=Files.createDirectories(root.resolve("received"));
            for(int index=0;index<files.size();index++) {
                Item item=files.get(index);pair.b.receiveFile(pair.back,item.packet,item.file,received.resolve(index+".bin").toFile(),new LanNode.Transfer(),null);
                Object channel=field(pair.b,"idleFileConnection");if(channel!=null)clients.add(field(channel,"raw"));
            }
            check(clients.size()==2,"80件没有按64件上限复用为两条连接");
            Item legacy=pair.send(1).get(0);Object legacyOffer;
            synchronized(field(pair.a,"gate")) {
                legacyOffer=((Map<?,?>)field(pair.a,"offers")).get(legacy.packet+":"+legacy.file.id);
                Field state=legacyOffer.getClass().getDeclaredField("state");state.setAccessible(true);state.set(legacyOffer,"已发送（非保存确认）");
                Field percent=legacyOffer.getClass().getDeclaredField("percent");percent.setAccessible(true);percent.set(legacyOffer,100);
            }
            legacy.file.sha256="0".repeat(64);
            try{pair.b.receiveFile(pair.back,legacy.packet,legacy.file,root.resolve("legacy-failed.bin").toFile(),new LanNode.Transfer(),null);throw new AssertionError("错误摘要未被拒绝");}catch(IOException expected){}
            waitFor(()->!pair.a.hasActiveOutgoingTransfer(pair.target.id(),legacy.packet),"错误摘要连接没有释放");
            check("已发送（非保存确认）".equals(field(legacyOffer,"state")),"历史完成项在失败重收后退回待处理");pair.a.setPendingFileLimit(100);pair.a.checkOfferCapacity(100);
            try(LanNode other=node(root.resolve("c"),"33333333333333333333333333333333",true,QUIET)) {
                LanNode.Peer sender=find(other,pair.a,A_ID);other.verifyPeer(sender);boolean denied=false;
                try{other.receiveFile(sender,files.get(0).packet,files.get(0).file,root.resolve("unauthorized.bin").toFile(),new LanNode.Transfer(),null);}catch(IOException expected){denied=true;}
                check(denied&&!Files.exists(root.resolve("unauthorized.bin")),"其他已信任设备取得未授权附件");
            }
            Item fresh=pair.send(1).get(0);pair.b.receiveFile(pair.back,fresh.packet,fresh.file,root.resolve("fresh.bin").toFile(),new LanNode.Transfer(),null);
            Socket idle=(Socket)field(field(pair.b,"idleFileConnection"),"raw");pair.b.discardFileBatch(pair.back);check(idle.isClosed(),"SAF 导出阶段取消未关闭闲置批量连接");
            fresh=pair.send(1).get(0);pair.b.receiveFile(pair.back,fresh.packet,fresh.file,root.resolve("fresh-after-cancel.bin").toFile(),new LanNode.Transfer(),null);
            Socket active;
            synchronized(field(pair.a,"gate")){active=(Socket)((Map<?,?>)field(pair.a,"fileBatchPeers")).keySet().stream().filter(key->!((Socket)key).isClosed()).findFirst().orElseThrow();}
            check(!active.isClosed(),"撤信前批量连接已经关闭");
            pair.a.security().confirmation=(name,code)->false;long started=System.nanoTime();pair.a.security().forget(B_ID);
            check(active.isClosed()&&System.nanoTime()-started<TimeUnit.SECONDS.toNanos(1),"撤信未立即关闭批量连接");
            try(var entries=Files.list(received)){check(entries.count()==80&&completed(pair.a)==82,"完成文件被改写或终态退回待处理");}
        }
    }
    private static void boundary(Path root)throws Exception {
        try(Pair pair=new Pair(root)) {
            Item item=pair.send(1).get(0);
            try(Socket raw=new Socket("127.0.0.1",pair.a.port())) {
                raw.setSoTimeout(2000);raw.getOutputStream().write(new byte[]{'F','Q','2','\n'});
                try(SSLSocket tls=pair.b.security().client(raw,pair.a.security().fingerprint)) {
                    tls.setSoTimeout(2000);
                    byte[] hello=Protocol.encode(0,"wp5-22","test",Protocol.ENTRY|Protocol.UTF8,"","",B_ID,null,pair.b.security().fingerprint);
                    SecureIdentity.writeFrame(tls.getOutputStream(),capability(hello,"FQBATCH=1"));SecureIdentity.readFrame(tls.getInputStream(),4096);
                    SecureIdentity.writeText(tls.getOutputStream(),"YES");check("YES".equals(SecureIdentity.readText(tls.getInputStream(),16)),"认证失败");
                    SecureIdentity.writeText(tls.getOutputStream(),"F\n"+pair.b.port());
                    SecureIdentity.writeFrame(tls.getOutputStream(),Protocol.encode(1,"wp5-22","test",Protocol.GET_FILE|Protocol.UTF8,Long.toHexString(item.packet)+":"+Integer.toHexString(item.file.id)+":0:",null,B_ID,null,pair.b.security().fingerprint));
                    check(SecureIdentity.readText(tls.getInputStream(),256).startsWith("OK\n"),"授权请求失败");
                    byte[] bytes=tls.getInputStream().readNBytes((int)item.file.size);check(Arrays.equals(bytes,new byte[]{3,5,7,11}),"正文损坏");
                    SecureIdentity.writeText(tls.getOutputStream(),"STORED\n"+item.file.sha256);check("NEXT".equals(SecureIdentity.readText(tls.getInputStream(),16)),"未确认批量边界");
                    SecureIdentity.writeText(tls.getOutputStream(),"M\n"+pair.b.port());
                    SecureIdentity.writeFrame(tls.getOutputStream(),Protocol.encode(2,"wp5-22","test",Protocol.MESSAGE|Protocol.UTF8,"invalid-batch-message",null,B_ID,null,pair.b.security().fingerprint));
                    boolean rejected=false;try{SecureIdentity.readText(tls.getInputStream(),16);}catch(IOException expected){rejected=true;}
                    check(rejected,"批量通道接受非F帧或保持异常连接");
                }
            }
        }
    }
    private static void crashWriter(Path root)throws Exception {
        Pair pair=new Pair(root);List<Item> files=pair.send(100);
        pair.completionHook=()->{
            System.out.println("CRASH_READY");System.out.flush();
            try{new CountDownLatch(1).await();}catch(InterruptedException error){Thread.currentThread().interrupt();throw new IllegalStateException(error);}
        };
        Item first=files.get(0);pair.b.receiveFile(pair.back,first.packet,first.file,root.resolve("saved.bin").toFile(),new LanNode.Transfer(),null);
        throw new AssertionError("崩溃写入夹具不应正常返回");
    }
    private static void recovery(Path root)throws Exception {
        Files.createDirectories(root);String javaExecutable=Paths.get(System.getProperty("java.home"),"bin","java.exe").toString();
        Process child=new ProcessBuilder(javaExecutable,"-Dfile.encoding=UTF-8","-cp",System.getProperty("java.class.path"),"SmallFileTransfer",root.toString(),"crash-writer").redirectError(root.resolve("child-errors.log").toFile()).start();
        ExecutorService reader=Executors.newSingleThreadExecutor();
        try {
            Future<String> ready=reader.submit(()->new BufferedReader(new InputStreamReader(child.getInputStream(),StandardCharsets.UTF_8)).readLine());
            check("CRASH_READY".equals(ready.get(30,TimeUnit.SECONDS))&&child.isAlive(),"崩溃夹具未到确认日志边界");
            child.destroyForcibly();check(child.waitFor(10,TimeUnit.SECONDS),"崩溃进程没有退出");
        } finally {if(child.isAlive())child.destroyForcibly().waitFor(10,TimeUnit.SECONDS);reader.shutdownNow();}
        Path journal=root.resolve("a/transfer-offer-completions.log");check(Files.size(journal)>0,"崩溃前没有同步确认日志");
        String[] confirmation=Files.readAllLines(journal).get(0).split(":");
        Path snapshot=root.resolve("a/transfer-offers.dat");byte[] bytes=Files.readAllBytes(snapshot);
        try(DataInputStream input=new DataInputStream(new ByteArrayInputStream(bytes))) {
            input.readInt();int count=input.readInt();
            for(int index=0;index<count;index++) {
                long packet=input.readLong();int file=input.readInt();input.readUTF();input.readLong();input.readLong();int expiry=bytes.length-input.available();input.readLong();
                if(Long.toString(packet).equals(confirmation[0])&&Integer.toString(file).equals(confirmation[1]))java.nio.ByteBuffer.wrap(bytes).putLong(expiry,System.currentTimeMillis()-5000);
                for(int field=0;field<9;field++)input.readUTF();
            }
        }
        SecureIdentity.atomic(snapshot.toFile(),bytes);
        Files.writeString(journal,"999:0:partial",StandardOpenOption.APPEND);
        try(LanNode restored=node(root.resolve("a"),A_ID,false,QUIET)) {
            check(offers(restored)==100&&completed(restored)==1,"确认日志、续期或末尾半条恢复错误");
            restored.setPendingFileLimit(100);restored.checkOfferCapacity(1);boolean full=false;try{restored.checkOfferCapacity(2);}catch(IOException expected){full=true;}
            check(full,"崩溃恢复后完成项重占额度或待处理额度失效");
        }
        check(Arrays.equals(Files.readAllBytes(root.resolve("saved.bin")),new byte[]{3,5,7,11}),"崩溃恢复损坏单份正式文件");
        Files.writeString(journal,"broken-record\n");boolean corrupt=false;
        try(LanNode ignored=node(root.resolve("a"),A_ID,false,QUIET)){}catch(IOException expected){corrupt=true;}
        check(corrupt&&Files.readString(journal).equals("broken-record\n"),"完整损坏记录被静默忽略或清空");
    }
    private static void eviction(Path root)throws Exception {
        try(Pair pair=new Pair(root)) {
            List<Item> files=pair.send(1105);Path received=Files.createDirectories(root.resolve("r"));
            for(int index=0;index<files.size();index++){Item item=files.get(index);pair.b.receiveFile(pair.back,item.packet,item.file,received.resolve(index+".bin").toFile(),new LanNode.Transfer(),null);}
            check(completed(pair.a)==1105,"1105件没有全部确认保存");
            pair.a.setPendingFileLimit(100);pair.a.checkOfferCapacity(100);check(offers(pair.a)==1000,"完成缓存未淘汰到1000项");
            pair.send(100);boolean full=false;try{pair.a.checkOfferCapacity(1);}catch(IOException expected){full=true;}
            try(var entries=Files.list(received)){check(full&&offers(pair.a)==1100&&entries.count()==1105,"完成缓存占额度或文件出现副本");}
        }
        try(LanNode restored=node(root.resolve("a"),A_ID,false,QUIET)) {
            check(offers(restored)==1100&&completed(restored)==1000,"淘汰后的完成/待处理状态未跨重启保留");
            restored.setPendingFileLimit(100);boolean full=false;try{restored.checkOfferCapacity(1);}catch(IOException expected){full=true;}check(full,"重启后待处理额度失效");
        }
    }
    private static void journalCleanup(Path root)throws Exception {
        Path journal=root.resolve("a/transfer-offer-completions.log");
        try(Pair pair=new Pair(root)) {
            pair.send(1);Files.writeString(journal,"unfinished");Files.setAttribute(journal,"dos:readonly",true);
            try {
                Item second=pair.send(1).get(0);check(offers(pair.a)==2,"已提交的新邀请未进入内存");
                check(pair.a.cancelOutgoing(pair.target.id(),second.packet,null,false),"日志清理失败误报撤销失败");
                check(offers(pair.a)==1,"撤销未同步内存");
            } finally {Files.setAttribute(journal,"dos:readonly",false);}
        }
        try(LanNode restored=node(root.resolve("a"),A_ID,false,QUIET)){check(offers(restored)==1,"重启授权与内存分叉");}
    }
    private static void journalFailure(Path root)throws Exception {
        try(Pair pair=new Pair(root)) {
            List<Item> files=pair.send(2);Path journal=root.resolve("a/transfer-offer-completions.log");Files.createDirectory(journal);
            Path saved=root.resolve("saved.bin");Item first=files.get(0);
            try {
                pair.b.receiveFile(pair.back,first.packet,first.file,saved.toFile(),new LanNode.Transfer(),null);
                check(Arrays.equals(Files.readAllBytes(saved),new byte[]{3,5,7,11}),"远端回执失败丢失正式文件");
                check(field(pair.b,"idleFileConnection")==null,"日志失败仍发送NEXT并复用连接");check(completed(pair.a)==0,"日志失败却进入完成态");
            }finally{Files.delete(journal);}
            Item second=files.get(1);pair.b.receiveFile(pair.back,second.packet,second.file,root.resolve("second.bin").toFile(),new LanNode.Transfer(),null);
            check(completed(pair.a)==1,"故障恢复后不能完成下一件");
        }
    }
    private static void closeFailure(Path root)throws Exception {
        Path saved=root.resolve("a/transfer-offers.dat"),blocker=root.resolve("a/transfer-offers.dat.new"),journal=root.resolve("a/transfer-offer-completions.log");
        try(Pair pair=new Pair(root)) {
            Item first=pair.send(3).get(0);byte[] before;
            synchronized(field(pair.a,"gate")) {
                before=Files.readAllBytes(saved);
                Object offer=((Map<?,?>)field(pair.a,"offers")).get(first.packet+":"+first.file.id);
                Method store=LanNode.class.getDeclaredMethod("storeOfferCompletion",offer.getClass());store.setAccessible(true);store.invoke(pair.a,offer);
                Files.createDirectory(blocker);pair.a.close();
            }
            check(Arrays.equals(before,Files.readAllBytes(saved))&&Files.size(journal)>0,"失败的最终快照破坏原快照或日志");
            Files.delete(blocker);
            Method flush=LanNode.class.getDeclaredMethod("flushOfferCompletions");flush.setAccessible(true);flush.invoke(pair.a);
            check(Arrays.equals(before,Files.readAllBytes(saved))&&Files.size(journal)>0,"清空后迟到补刷覆盖原快照");
        }
        java.util.concurrent.atomic.AtomicInteger replayed=new java.util.concurrent.atomic.AtomicInteger();
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}
            public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state){if(percent==100&&"对方已校验保存".equals(state))replayed.incrementAndGet();}};
        try(LanNode restored=node(root.resolve("a"),A_ID,false,listener)) {
            check(offers(restored)==3&&completed(restored)==1,"重启未恢复原有全部邀请与完成态");
            restored.replayCompletedOutgoingStates();check(replayed.get()==1,"恢复完成态未通知历史同步入口");
        }
    }
    private static void cancelIsolation(Path root)throws Exception {
        try(Pair pair=new Pair(root)) {
            List<Item> forward=pair.send(2);Item canceled=pair.send(1).get(0);
            LanNode.Message reverse=pair.b.sendMessage(pair.back,"",Arrays.asList(pair.source.toFile(),pair.source.toFile()));
            holdReverseSync(pair.a,B_ID);holdReverseSync(pair.b,A_ID);
            Item first=forward.get(0);pair.b.receiveFile(pair.back,first.packet,first.file,root.resolve("forward1.bin").toFile(),new LanNode.Transfer(),null);
            pair.a.receiveFile(pair.target,reverse.number,reverse.files.get(0),root.resolve("back1.bin").toFile(),new LanNode.Transfer(),null);
            Object forwardRaw=field(field(pair.b,"idleFileConnection"),"raw"),backRaw=field(field(pair.a,"idleFileConnection"),"raw");
            check(pair.a.cancelOutgoing(pair.target.id(),canceled.packet,null,false),"指定邀请未取消");
            Method release=LanNode.class.getDeclaredMethod("sendReleaseFiles",LanNode.Peer.class,String.class);release.setAccessible(true);release.invoke(pair.a,pair.target,"cancel:"+canceled.packet);
            Item second=forward.get(1);pair.b.receiveFile(pair.back,second.packet,second.file,root.resolve("forward2.bin").toFile(),new LanNode.Transfer(),null);
            check(forwardRaw==field(field(pair.b,"idleFileConnection"),"raw"),"取消甲中断了无关乙连接");
            pair.a.receiveFile(pair.target,reverse.number,reverse.files.get(1),root.resolve("back2.bin").toFile(),new LanNode.Transfer(),null);
            check(backRaw==field(field(pair.a,"idleFileConnection"),"raw"),"取消出站误关反向接收");
            check(Arrays.equals(Files.readAllBytes(root.resolve("forward2.bin")),Files.readAllBytes(pair.source))&&Arrays.equals(Files.readAllBytes(root.resolve("back2.bin")),Files.readAllBytes(pair.source)),"双向内容不符");
        }
    }
    private static void connectCancellation(Path root)throws Exception {
        for(boolean blockDial:new boolean[]{true,false})try(LanNode sender=node(root.resolve("a"+blockDial),A_ID,false,QUIET);LanNode target=node(root.resolve("b"+blockDial),B_ID,false,QUIET)) {
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress("127.0.0.1",target.port()),"test","host","target","");peer.deviceId=B_ID;peer.fingerprint=target.security().fingerprint;
            sender.security().ensureTrusted(B_ID,peer.fingerprint,"target");
            Field connector=LanNode.class.getDeclaredField("peerConnector");connector.setAccessible(true);CountDownLatch entered=new CountDownLatch(1);
            Object hook=java.lang.reflect.Proxy.newProxyInstance(LanNode.class.getClassLoader(),new Class<?>[]{connector.getType()},(proxy,method,args)->{
                entered.countDown();Socket socket=(Socket)args[0];
                while(blockDial&&!socket.isClosed())Thread.sleep(1);
                throw new SocketException("测试拨号被取消");
            });connector.set(sender,hook);
            ExecutorService worker=Executors.newSingleThreadExecutor();LanNode.ConnectCancellation cancellation=new LanNode.ConnectCancellation();
            try {
                Future<?> request=worker.submit(()->{try{sender.connectPeer(peer,target.port(),cancellation);throw new AssertionError("取消后仍返回socket");}catch(IOException expected){}});
                check(entered.await(3,TimeUnit.SECONDS),"没有进入受控拨号");
                if(!blockDial)waitFor(()->((Set<?>)field(sender,"connections")).isEmpty(),"没有进入回连等待");
                long start=System.nanoTime();cancellation.close();request.get(500,TimeUnit.MILLISECONDS);
                check(System.nanoTime()-start<TimeUnit.MILLISECONDS.toNanos(500),"取消超过500ms");
                check(((Set<?>)field(sender,"connections")).isEmpty()&&((Map<?,?>)field(sender,"reverseRequests")).isEmpty(),"取消后连接额度或票据未归还");
            }finally{cancellation.close();worker.shutdownNow();}
        }
    }
    private static void staleReverse(Path root)throws Exception {
        try(Pair pair=new Pair(root)) {
            List<Item> files=pair.send(2);Item first=files.get(0),second=files.get(1);
            holdReverseSync(pair.a,B_ID);
            pair.b.receiveFile(pair.back,first.packet,first.file,root.resolve("first.bin").toFile(),new LanNode.Transfer(),null);
            Object channel=field(pair.b,"idleFileConnection");check(channel!=null,"未预热批量连接");
            ((ScheduledFuture<?>)field(channel,"idleTimeout")).cancel(false);
            ((ServerSocket)field(pair.a,"tcp")).close();
            synchronized(field(pair.a,"gate")){for(Object socket:((Map<?,?>)field(pair.a,"fileBatchPeers")).keySet())((Socket)socket).close();}
            synchronized(field(pair.a,"gate")){((Set<?>)field(pair.a,"activeSyncPeers")).remove(B_ID);}
            pair.b.receiveFile(pair.back,second.packet,second.file,root.resolve("reverse.bin").toFile(),new LanNode.Transfer(),null);
            check(Arrays.equals(Files.readAllBytes(root.resolve("reverse.bin")),Files.readAllBytes(pair.source)),"反向重试内容不符");
            check(field(pair.b,"idleFileConnection")==null,"反向P被误回收为批量连接");
        }
    }
}
