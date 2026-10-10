package org.feiqlight.android;

import android.content.*;
import android.content.pm.ProviderInfo;
import android.database.*;
import android.net.Uri;
import android.os.*;
import android.provider.OpenableColumns;
import android.widget.EditText;
import org.feiqlight.android.core.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.*;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.*;
import java.net.*;
import java.nio.file.Files;
import java.lang.reflect.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w393dp-h852dp-xhdpi")
public final class ReliabilityTest {
    @Test public void delayedDiscoveryQueueDoesNotDelayRealTextSend() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();CountDownLatch release=new CountDownLatch(1),entered=new CountDownLatch(1);
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode node=node()) {
            settle(service);set(service,"node",node);LanNode.Peer peer=peer(target.getLocalPort());
            block((ExecutorService)field(service,"discovery"),entered,release);assertTrue(entered.await(3,TimeUnit.SECONDS));
            for(int i=0;i<200;i++)service.refresh();
            List<Boolean> result=new ArrayList<>();new DraftStore(service).save(peer.id(),"不等发现扫描");service.send(peer,"不等发现扫描",result::add);
            io(service).submit(()->{}).get(2,TimeUnit.SECONDS);shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));
            assertEquals(Collections.singletonList(true),result);assertEquals("不等发现扫描",forwardedPacket(target).body);assertEquals(1,release.getCount());
        } finally {release.countDown();controller.destroy();}
    }
    @Test public void stagingReportsActualBytesBeforeSendingAndKeepsSpaceChecks() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        try {
            settle(service);byte[] content=new byte[1024*1024];List<Long> progress=new ArrayList<>();ByteArrayOutputStream output=new ByteArrayOutputStream();
            InputStream slow=new ByteArrayInputStream(content){@Override public synchronized int read(byte[] b,int off,int len){try{Thread.sleep(25);}catch(InterruptedException e){Thread.currentThread().interrupt();}return super.read(b,off,len);}};
            Method copy=ChatService.class.getDeclaredMethod("copy",InputStream.class,OutputStream.class,LanNode.Transfer.class,long.class,String.class,java.util.function.Consumer.class);copy.setAccessible(true);
            copy.invoke(service,slow,output,new LanNode.Transfer(),(long)content.length,"容量不足",(java.util.function.Consumer<Long>)progress::add);
            assertEquals(Long.valueOf(0),progress.get(0));assertEquals(Long.valueOf(content.length),progress.get(progress.size()-1));assertTrue(progress.stream().anyMatch(n->n>0&&n<content.length));assertEquals(content.length,output.size());
            try {copy.invoke(service,new ByteArrayInputStream(content),new ByteArrayOutputStream(),new LanNode.Transfer(),1L,"容量不足",null);fail();}catch(InvocationTargetException expected){assertTrue(expected.getCause() instanceof IOException);}
        } finally {controller.destroy();}
    }
    @Test public void continuousChangesDoNotStarveVisibleSendFeedback() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        try {
            settle(service);int[] calls={0};ChatService.Observer observer=new ChatService.Observer(){public void changed(){calls[0]++;}public void error(String text){}};
            service.observe(observer);calls[0]=0;Method changed=ChatService.class.getDeclaredMethod("changed");changed.setAccessible(true);
            for(int i=0;i<30;i++){changed.invoke(service);shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(20));}
            assertTrue("连续网络事件使界面一直等到安静后才刷新",calls[0]>=5);service.remove(observer);
        } finally {controller.destroy();}
    }
    @Test public void diagnosticLogIsOptInBoundedAndDoesNotContainUserData() throws Exception {
        Context context=RuntimeEnvironment.getApplication();Diagnostics log=new Diagnostics(context);log.clear();
        SharedPreferences prefs=context.getSharedPreferences("settings",0);prefs.edit().putBoolean("diagnostics",false).apply();
        log.record(Diagnostics.Event.Connected);File dir=new File(context.getFilesDir(),"diagnostics");assertFalse(new File(dir,"events.log").exists());
        prefs.edit().putBoolean("diagnostics",true).apply();for(int i=0;i<4000;i++)log.record(Diagnostics.Event.TaskStarted);
        assertEquals(2,Objects.requireNonNull(dir.list()).length);for(File file:Objects.requireNonNull(dir.listFiles()))assertTrue(file.length()<=Diagnostics.LIMIT);
        String text=log.read();prefs.edit().putBoolean("diagnostics",false).apply();log.record(Diagnostics.Event.Error);assertEquals(text,log.read());
        assertFalse(text.contains(context.getFilesDir().getAbsolutePath()));assertFalse(text.contains("content://"));log.clear();assertEquals(0,Objects.requireNonNull(dir.list()).length);
    }
    @Test public void olderCompletedFileTaskCannotOverwriteNewerQueuedResult() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        try {
            settle(service);Class<?> jobType=Class.forName("org.feiqlight.android.ChatService$Throwing");
            Method start=ChatService.class.getDeclaredMethod("transferJob",jobType,Runnable.class,String.class,String.class);start.setAccessible(true);
            Object noOp=java.lang.reflect.Proxy.newProxyInstance(jobType.getClassLoader(),new Class<?>[]{jobType},(proxy,method,args)->null);
            ExecutorService worker=(ExecutorService)field(service,"transferWorker");
            start.invoke(service,noOp,(Runnable)()->{},"旧任务完成","旧任务取消");worker.submit(()->{}).get(5,TimeUnit.SECONDS);
            assertFalse(service.busy());assertEquals("主线程回调未执行时也应读到完成状态","旧任务完成",service.transferStatus());
            start.invoke(service,noOp,(Runnable)()->{},"新任务完成","新任务取消");worker.submit(()->{}).get(5,TimeUnit.SECONDS);
            // 两个任务均已结束但 UI 回调尚未执行：只运行第一个，旧结果也不得覆盖当前准备状态。
            shadowOf(Looper.getMainLooper()).runOneTask();assertNotEquals("旧任务完成",field(service,"transferStatus"));
            shadowOf(Looper.getMainLooper()).idle();assertEquals("新任务完成",field(service,"transferStatus"));
        } finally {sc.destroy();}
    }
    private static Object field(Object o,String name)throws Exception { Field f=o.getClass().getDeclaredField(name); f.setAccessible(true); return f.get(o); }
    private static void set(Object o,String name,Object value)throws Exception { Field f=o.getClass().getDeclaredField(name); f.setAccessible(true); f.set(o,value); }
    private static void invoke(Object o,String name)throws Exception { Method m=o.getClass().getDeclaredMethod(name); m.setAccessible(true); m.invoke(o); }
    private static void open(MainActivity a,String id)throws Exception { Method m=MainActivity.class.getDeclaredMethod("open",String.class); m.setAccessible(true); m.invoke(a,id); }
    private static ExecutorService io(ChatService s)throws Exception { return (ExecutorService)field(s,"io"); }
    private static ChatStore store(ChatService s)throws Exception { return (ChatStore)field(s,"store"); }
    private static void settle(ChatService s)throws Exception { for(int i=0;i<4;i++) { io(s).submit(()->{}).get(5,TimeUnit.SECONDS); shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100)); } }
    private static void transferDone(ChatService s)throws Exception { ((ExecutorService)field(s,"transferWorker")).submit(()->{}).get(5,TimeUnit.SECONDS); settle(s); assertFalse(s.busy()); }
    private static LanNode.Peer peer(int port)throws Exception { LanNode.Peer p=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port),"test","host","审查联系人","测试"); p.utf8=true; return p; }
    private static LanNode.Message message() { LanNode.Message m=new LanNode.Message(); m.number=1; m.time=System.currentTimeMillis(); m.text="未读消息"; m.state="已收到"; return m; }
    private static int freePort()throws Exception {
        // Windows 的 TCP/UDP 占用区间不同；测试节点同时监听两者，不能仅检查 UDP。
        for(int i=0;i<20;i++)try(ServerSocket tcp=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            try(DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),InetAddress.getLoopbackAddress())){return tcp.getLocalPort();}catch(BindException occupied){/* 只在测试选端口阶段换候选。 */}
        }
        throw new IOException("没有空闲的 TCP/UDP 测试端口");
    }
    private static LanNode node()throws Exception {
        LanNode n=new LanNode(freePort(),true,"android-regression","host","测试","测试",new LanNode.Listener() { public void peersChanged(){} public void message(LanNode.Peer p,LanNode.Message m,boolean update){} public void error(String text){} }); n.start(); return n;
    }
    private static void block(ExecutorService io,CountDownLatch blocked,CountDownLatch release) {
        io.execute(()->{ blocked.countDown(); try { if(!release.await(20,TimeUnit.SECONDS)) throw new AssertionError("队列未释放"); } catch(InterruptedException e) { Thread.currentThread().interrupt(); } });
    }
    @Test public void destroyedServiceCannotOpenQueuedConnectionPorts() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();CountDownLatch release=new CountDownLatch(1);
        java.util.concurrent.atomic.AtomicReference<LanNode> created=new java.util.concurrent.atomic.AtomicReference<>();int port=freePort();boolean destroyed=false;
        try {
            settle(service);service.connectionFactory=(prefs,listener)->{LanNode n=new LanNode(port,true,"queued-connect","host","测试","测试",listener);created.set(n);return n;};
            CountDownLatch blocked=new CountDownLatch(1);block(io(service),blocked,release);assertTrue(blocked.await(5,TimeUnit.SECONDS));
            invoke(service,"connect");sc.destroy();destroyed=true;release.countDown();assertTrue(io(service).awaitTermination(5,TimeUnit.SECONDS));shadowOf(Looper.getMainLooper()).idle();
            try(java.net.ServerSocket tcp=new java.net.ServerSocket(port,1,InetAddress.getLoopbackAddress());DatagramSocket udp=new DatagramSocket(port,InetAddress.getLoopbackAddress())) { assertNull("销毁后仍发布了节点",field(service,"node")); }
        } finally { release.countDown();if(!destroyed)sc.destroy();LanNode n=created.get();if(n!=null)n.close(); }
    }
    @Test public void staleDisconnectCompletionCannotStopReconnectedService() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();List<LanNode> created=new CopyOnWriteArrayList<>();int port=freePort();
        try {
            settle(service);service.connectionFactory=(prefs,listener)->{LanNode n=new LanNode(port,true,"reconnect","host","测试","测试",listener);created.add(n);return n;};
            service.onStartCommand(new Intent(),0,1);settle(service);assertTrue(service.online());
            service.disconnect();io(service).submit(()->{}).get(5,TimeUnit.SECONDS);
            // 故意让旧断开完成回调还留在主队列，新连接已经完成套接字初始化。
            service.onStartCommand(new Intent(),0,2);io(service).submit(()->{}).get(5,TimeUnit.SECONDS);settle(service);
            assertTrue(service.online());assertFalse("旧回调调用 stopSelf 停止了新连接",shadowOf(service).isStoppedBySelf());
        } finally { sc.destroy();for(LanNode n:created)n.close(); }
    }
    @Test public void destroyDuringConnectionFactoryClosesUnpublishedNode() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        java.util.concurrent.atomic.AtomicReference<LanNode> created=new java.util.concurrent.atomic.AtomicReference<>();int port=freePort();boolean destroyed=false;
        try {
            settle(service);service.connectionFactory=(prefs,listener)->{LanNode n=new LanNode(port,true,"creating","host","测试","测试",listener);created.set(n);entered.countDown();try{if(!release.await(5,TimeUnit.SECONDS))throw new AssertionError("工厂未释放");}catch(InterruptedException e){Thread.currentThread().interrupt();throw new AssertionError(e);}return n;};
            invoke(service,"connect");assertTrue(entered.await(5,TimeUnit.SECONDS));assertTrue("连接过程中应允许断开",service.canDisconnect());sc.destroy();destroyed=true;release.countDown();
            assertTrue(io(service).awaitTermination(5,TimeUnit.SECONDS));assertNull(field(service,"node"));
            try(ServerSocket tcp=new ServerSocket(port,1,InetAddress.getLoopbackAddress());DatagramSocket udp=new DatagramSocket(port,InetAddress.getLoopbackAddress())) { assertNotNull(created.get()); }
            try { created.get().start();fail("已失效节点没有关闭"); } catch(IOException expected) { }
        } finally {release.countDown();if(!destroyed)sc.destroy();LanNode n=created.get();if(n!=null)n.close();}
    }
    @Test public void staleConnectionFailureCannotStopNewAttempt() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();java.util.concurrent.atomic.AtomicInteger attempts=new java.util.concurrent.atomic.AtomicInteger();int port=freePort();
        try {
            settle(service);service.connectionFactory=(prefs,listener)->{if(attempts.incrementAndGet()==1)throw new IllegalStateException("首次连接失败");return new LanNode(port,true,"retry","host","测试","测试",listener);};
            invoke(service,"connect");io(service).submit(()->{}).get(5,TimeUnit.SECONDS);
            service.disconnect();invoke(service,"connect");settle(service);
            assertTrue(service.online());assertFalse(shadowOf(service).isStoppedBySelf());assertTrue(ChatService.shouldRestoreConnection(service));
        } finally {sc.destroy();assertTrue(io(service).awaitTermination(5,TimeUnit.SECONDS));}
    }
    @Test public void rapidConversationSwitchMarksOriginalTargetsRead()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get(); CountDownLatch release=new CountDownLatch(1);
        try {
            settle(s); ChatStore store=store(s); LanNode.Peer a=peer(32425),b=peer(32426);
            io(s).submit(()->{ store.message(a,message(),false,false); store.message(b,message(),false,false); }).get(5,TimeUnit.SECONDS);
            CountDownLatch blocked=new CountDownLatch(1); block(io(s),blocked,release); assertTrue(blocked.await(5,TimeUnit.SECONDS));
            s.active(a.id()); s.active(b.id()); s.active(""); release.countDown(); settle(s);
            assertTrue(io(s).submit(()->store.conversations().stream().allMatch(c->c.unread==0)).get(5,TimeUnit.SECONDS));
        } finally { release.countDown(); sc.destroy(); }
    }
    @Test public void oldHistoryReplyCannotRenderIntoReopenedConversation() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();ActivityController<MainActivity> ac=null;CountDownLatch release=new CountDownLatch(1);
        try {
            settle(service);LanNode.Peer peer=peer(32425);service.incoming(peer,message(),false);settle(service);
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);open(activity,peer.id());settle(service);
            activity.changed();io(service).submit(()->{}).get(5,TimeUnit.SECONDS);
            CountDownLatch blocked=new CountDownLatch(1);block(io(service),blocked,release);assertTrue(blocked.await(5,TimeUnit.SECONDS));
            open(activity,peer(32426).id());open(activity,peer.id());
            shadowOf(Looper.getMainLooper()).idle();
            assertEquals("旧 A 页回调写进了重新打开的 A 页",0,((android.widget.LinearLayout)field(activity,"messages")).getChildCount());
            release.countDown();settle(service);assertTrue(((android.widget.LinearLayout)field(activity,"messages")).getChildCount()>0);
        } finally { release.countDown();if(ac!=null)ac.pause().stop().destroy();sc.destroy(); }
    }
    @Test public void queuedNewerHistoryRequestDoesNotStarveCurrentPageResult() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();ActivityController<MainActivity> ac=null;CountDownLatch release=new CountDownLatch(1);
        try {
            settle(service);LanNode.Peer peer=peer(32425);service.incoming(peer,message(),false);settle(service);
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);open(activity,peer.id());settle(service);
            LanNode.Message second=message();second.number=2;second.text="当前结果应立即显示";
            io(service).submit(()->store(service).message(peer,second,false,true)).get(5,TimeUnit.SECONDS);
            activity.changed();io(service).submit(()->{}).get(5,TimeUnit.SECONDS);
            CountDownLatch blocked=new CountDownLatch(1);block(io(service),blocked,release);assertTrue(blocked.await(5,TimeUnit.SECONDS));
            activity.changed();shadowOf(Looper.getMainLooper()).idle();
            assertEquals("更新请求仍排队，不应丢弃本页已完成的有效结果",3,((android.widget.LinearLayout)field(activity,"messages")).getChildCount());
            release.countDown();settle(service);
        } finally {release.countDown();if(ac!=null)ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void destroyedActivityCannotOverwriteNewerDraft()throws Exception {
        rotateWhileSending(true);
    }
    @Test public void sentDraftClearsAfterRotationWithoutRestoringOldInput()throws Exception {
        rotateWhileSending(false);
    }
    private void rotateWhileSending(boolean editNext)throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get(); ActivityController<MainActivity> ac=null; CountDownLatch release=new CountDownLatch(1);
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress()); LanNode n=node()) {
            settle(s); set(s,"node",n); LanNode.Peer p=peer(target.getLocalPort()); ChatStore store=store(s);
            io(s).submit(()->store.message(p,message(),false,true)).get(5,TimeUnit.SECONDS); s.active(p.id()); settle(s);
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(s,ChatService.class),s.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible(); MainActivity old=ac.get(); settle(s); open(old,p.id()); settle(s);
            CountDownLatch blocked=new CountDownLatch(1); block(io(s),blocked,release); assertTrue(blocked.await(5,TimeUnit.SECONDS));
            EditText oldEditor=(EditText)field(old,"editor"); oldEditor.setText("A：点击发送的消息"); invoke(old,"send");
            assertEquals(android.view.View.VISIBLE,((android.view.View)field(old,"sendStatus")).getVisibility());assertFalse(((android.view.View)field(old,"sendButton")).isEnabled());invoke(old,"send");
            if(editNext) oldEditor.setText("B：旋转前草稿");
            Bundle state=new Bundle(); ac.saveInstanceState(state).pause().stop().destroy();
            ac=Robolectric.buildActivity(MainActivity.class).create(state).start().resume().visible(); MainActivity fresh=ac.get();
            EditText freshEditor=(EditText)field(fresh,"editor"); if(editNext) freshEditor.setText("C：旋转后新草稿");
            ac.pause().stop(); release.countDown(); settle(s);
            DraftStore drafts=new DraftStore(fresh); assertEquals(editNext?"C：旋转后新草稿":"",drafts.read(p.id()));
            ac.restart().start().resume().visible(); settle(s);
            assertEquals(editNext?"C：旋转后新草稿":"",((EditText)field(ac.get(),"editor")).getText().toString());
            assertEquals(android.view.View.GONE,((android.view.View)field(ac.get(),"sendStatus")).getVisibility());
        } finally { release.countDown(); if(ac!=null) ac.pause().stop().destroy(); sc.destroy(); }
    }
    @Test public void draftVersionProtectsEditBackToSameTextAndSupportsOldData() {
        Context context=RuntimeEnvironment.getApplication(); context.getSharedPreferences("drafts",Context.MODE_PRIVATE).edit().putString("peer","旧版本草稿").commit();
        DraftStore drafts=new DraftStore(context); DraftStore.Snapshot old=drafts.snapshot("peer"); assertEquals(0,old.revision);
        drafts.save("peer","新内容"); drafts.save("peer","旧版本草稿"); assertFalse(drafts.clearIfUnchanged("peer",old));
        assertEquals("旧版本草稿",drafts.read("peer")); assertTrue(drafts.clearIfUnchanged("peer",drafts.snapshot("peer"))); assertEquals("",drafts.read("peer"));
    }
    @Test public void failedSendKeepsDraft()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get();
        try { settle(s); LanNode.Peer p=peer(32425); DraftStore drafts=new DraftStore(s); drafts.save(p.id(),"未连接时保留"); List<Boolean> result=new ArrayList<>(); s.send(p,"未连接时保留",result::add); settle(s); assertEquals(Collections.singletonList(false),result); assertEquals("未连接时保留",drafts.read(p.id())); }
        finally { sc.destroy(); }
    }
    private static Protocol.Packet forwardedPacket(DatagramSocket socket)throws Exception {
        socket.setSoTimeout(5000);
        for(int i=0;i<10;i++){byte[] data=new byte[65536];DatagramPacket packet=new DatagramPacket(data,data.length);socket.receive(packet);Protocol.Packet parsed=Protocol.parse(Arrays.copyOf(data,packet.getLength()));if(parsed!=null&&parsed.mode()==Protocol.MESSAGE)return parsed;}
        throw new AssertionError("没有收到转发消息");
    }
    @Test public void forwardTextUsesNewPacketAndPreservesDraft()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();
        try(DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()){
            settle(s);set(s,"node",n);LanNode.Peer target=peer(socket.getLocalPort());DraftStore drafts=new DraftStore(s);drafts.save(target.id(),"不能清空的草稿");
            LanNode.Message original=message();original.text="转发中文 😊\n第二行";List<Boolean> result=new ArrayList<>();
            s.forward(peer(32425),original,target,result::add);settle(s);Protocol.Packet packet=forwardedPacket(socket);
            assertEquals(original.text,packet.body);assertNotEquals(original.number,packet.number);assertEquals(Collections.singletonList(true),result);assertEquals("不能清空的草稿",drafts.read(target.id()));assertEquals("已收到",original.state);
        }finally{sc.destroy();}
    }
    @Test public void forwardReceivedAttachmentPreservesNameBytesAndRejectsMissing()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();
        try(DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()){
            settle(s);set(s,"node",n);LanNode.Peer source=peer(32425),target=peer(socket.getLocalPort());LanNode.Message original=message();
            byte[] content="转发文件内容 😊".getBytes(java.nio.charset.StandardCharsets.UTF_8);Protocol.Attachment file=new Protocol.Attachment(0,"原文件名.txt",content.length,0);original.files.add(file);
            File local=s.receivedFile(source,original.number,file);assertTrue(local.getParentFile().isDirectory()||local.getParentFile().mkdirs());Files.write(local.toPath(),content);
            List<Boolean> result=new ArrayList<>();s.forward(source,original,target,result::add);transferDone(s);assertEquals(Collections.singletonList(true),result);
            Protocol.Packet packet=forwardedPacket(socket);Protocol.Attachment offered=Protocol.parseFiles(packet.extra).get(0);assertEquals("原文件名.txt",offered.name);assertEquals(original.text,packet.body);
            File downloaded=new File(s.getCacheDir(),"forward-download.txt");n.receiveFile(peer((Integer)field(n,"port")),packet.number,offered,downloaded,new LanNode.Transfer(),percent->{});assertArrayEquals(content,Files.readAllBytes(downloaded.toPath()));
            Files.delete(local.toPath());result.clear();s.forward(source,original,target,result::add);transferDone(s);assertEquals(Collections.singletonList(false),result);
        }finally{sc.destroy();}
    }
    @Test public void forwardSameNameReceivedAndOutgoingAttachmentsKeepsNamesBytesAndSeparateOffers()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();
        try(DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()) {
            settle(s);set(s,"node",n);LanNode.Peer source=peer(32425),target=peer(socket.getLocalPort());
            for(boolean outgoing:new boolean[]{false,true}) {
                LanNode.Message original=message();original.outgoing=outgoing;original.number=outgoing?44:42;
                List<byte[]> contents=Arrays.asList(new byte[]{1,2,3},new byte[]{4,5});
                for(int i=0;i<2;i++) {
                    Protocol.Attachment a=new Protocol.Attachment(i,"same.txt",contents.get(i).length,1);original.files.add(a);
                    File local=s.receivedFile(source,original.number,a);assertTrue(local.getParentFile().isDirectory()||local.getParentFile().mkdirs());Files.write(local.toPath(),contents.get(i));original.localPaths.add(local.getPath());
                }
                DraftStore drafts=new DraftStore(s);drafts.save(target.id(),"保留目标草稿");List<Boolean> result=new ArrayList<>();
                s.forward(source,original,target,result::add);transferDone(s);assertEquals(Collections.singletonList(true),result);
                Protocol.Packet packet=forwardedPacket(socket);List<Protocol.Attachment> files=Protocol.parseFiles(packet.extra);assertEquals(2,files.size());assertNotEquals(files.get(0).id,files.get(1).id);assertNotEquals(original.number,packet.number);
                for(int i=0;i<2;i++) {
                    assertEquals("same.txt",files.get(i).name);File download=new File(s.getCacheDir(),"forward-"+outgoing+"-"+i);
                    n.receiveFile(peer((Integer)field(n,"port")),packet.number,files.get(i),download,new LanNode.Transfer(),percent->{});
                    assertArrayEquals(contents.get(i),Files.readAllBytes(download.toPath()));assertArrayEquals(contents.get(i),Files.readAllBytes(new File(original.localPaths.get(i)).toPath()));
                }
                assertEquals("保留目标草稿",drafts.read(target.id()));
            }
        }finally {sc.destroy();}
    }
    @Test public void failedMultiAttachmentForwardRemovesOnlyItsStagedCopies()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();
        try(DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()) {
            settle(s);set(s,"node",n);File keep=cache(s,3);LanNode.Peer source=peer(32425);LanNode.Message original=message();
            original.files.add(new Protocol.Attachment(0,"same.txt",1,1));original.files.add(new Protocol.Attachment(1,"same.txt",1,1));
            File local=s.receivedFile(source,original.number,original.files.get(0));assertTrue(local.getParentFile().isDirectory()||local.getParentFile().mkdirs());Files.write(local.toPath(),new byte[]{7});
            List<Boolean> result=new ArrayList<>();s.forward(source,original,peer(socket.getLocalPort()),result::add);transferDone(s);
            assertEquals(Collections.singletonList(false),result);assertEquals(1,new File(s.getFilesDir(),"offers").list().length);assertTrue(keep.isFile());assertArrayEquals(new byte[]{7},Files.readAllBytes(local.toPath()));assertTrue(((Map<?,?>)field(n,"offers")).isEmpty());
        }finally {sc.destroy();}
    }
    @Test public void cancellingForwardDuringAuthorizedReadCleansItsFolder()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();CountDownLatch release=new CountDownLatch(1);
        try(DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()) {
            settle(s);set(s,"node",n);TestProvider provider=provider(s,1L,1);provider.openedGate=new CountDownLatch(1);provider.releaseGate=release;
            LanNode.Peer source=peer(32425);LanNode.Message original=message();Protocol.Attachment a=new Protocol.Attachment(0,"saved.txt",1,1);original.files.add(a);
            s.getSharedPreferences("received_locations",0).edit().putString(s.receivedFile(source,original.number,a).getName(),"content://feiq.test/file").commit();
            List<Boolean> result=new ArrayList<>();s.forward(source,original,peer(socket.getLocalPort()),result::add);assertTrue(provider.openedGate.await(5,TimeUnit.SECONDS));
            s.cancelTransfer();release.countDown();transferDone(s);assertEquals(Collections.singletonList(false),result);
            assertEquals(0,new File(s.getFilesDir(),"offers").list().length);assertTrue(provider.source.isFile());assertTrue(((Map<?,?>)field(n,"offers")).isEmpty());
        }finally {release.countDown();sc.destroy();}
    }
    @Test public void outgoingAttachmentPathsSurviveStoreAndAckCopy()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();
        try(DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()){
            settle(s);set(s,"node",n);LanNode.Peer target=peer(socket.getLocalPort());File local=new File(s.getCacheDir(),"发送原文件.txt");Files.write(local.toPath(),new byte[]{1,2,3});
            LanNode.Message sent=n.sendMessage(target,"附件说明",Collections.singletonList(local));forwardedPacket(socket);
            io(s).submit(()->store(s).message(target,sent,false,true)).get(5,TimeUnit.SECONDS);
            LanNode.Message restored=io(s).submit(()->store(s).history(target.id()).get(0)).get(5,TimeUnit.SECONDS);assertEquals(Collections.singletonList(local.getCanonicalPath()),restored.localPaths);assertEquals(restored.localPaths,restored.copy().localPaths);
            List<Boolean> result=new ArrayList<>();s.forward(target,restored,target,result::add);transferDone(s);assertEquals(Collections.singletonList(true),result);assertEquals("发送原文件.txt",Protocol.parseFiles(forwardedPacket(socket).extra).get(0).name);
        }finally{sc.destroy();}
    }
    public static final class TestProvider extends ContentProvider {
        File source; Long declared; int opened; boolean metadataUnsupported; CountDownLatch openedGate,releaseGate;
        @Override public boolean onCreate() { return true; }
        @Override public Cursor query(Uri uri,String[] projection,String selection,String[] args,String order) { if(metadataUnsupported)throw new UnsupportedOperationException("metadata unsupported");MatrixCursor c=new MatrixCursor(new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}); c.addRow(new Object[]{"测试文件.bin",declared}); return c; }
        @Override public ParcelFileDescriptor openFile(Uri uri,String mode)throws FileNotFoundException {
            opened++;
            if(openedGate!=null) {openedGate.countDown();try {if(!releaseGate.await(5,TimeUnit.SECONDS))throw new FileNotFoundException("测试读取未释放");}catch(InterruptedException e){Thread.currentThread().interrupt();throw new FileNotFoundException("测试读取被中断");}}
            return ParcelFileDescriptor.open(source,ParcelFileDescriptor.MODE_READ_ONLY);
        }
        @Override public String getType(Uri uri) { return "application/octet-stream"; }
        @Override public Uri insert(Uri uri,ContentValues values) { throw new UnsupportedOperationException(); }
        @Override public int delete(Uri uri,String selection,String[] args) { throw new UnsupportedOperationException(); }
        @Override public int update(Uri uri,ContentValues values,String selection,String[] args) { throw new UnsupportedOperationException(); }
    }
    private static TestProvider provider(ChatService s,Long declared,int bytes)throws Exception {
        TestProvider p=new TestProvider(); p.source=new File(s.getCacheDir(),"source.bin"); p.declared=declared;
        try(OutputStream out=new FileOutputStream(p.source)) { out.write(new byte[bytes]); }
        ProviderInfo info=new ProviderInfo(); info.authority="feiq.test"; p.attachInfo(s,info); ShadowContentResolver.registerProviderInternal(info.authority,p); return p;
    }
    private static File cache(ChatService s,long bytes)throws Exception {
        File folder=new File(s.getFilesDir(),"offers/existing"); assertTrue(folder.mkdir()); File file=new File(folder,"cached.bin");
        try(RandomAccessFile out=new RandomAccessFile(file,"rw")) { out.setLength(bytes); } return file;
    }
    @Test public void knownAndUnknownSmallFilesFitAbove256MiBCache()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get(); List<String> errors=new ArrayList<>();
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress()); LanNode n=node()) {
            settle(s); set(s,"node",n); s.observe(new ChatService.Observer(){public void changed(){}public void error(String e){errors.add(e);}});
            cache(s,257L*1024*1024); TestProvider p=provider(s,1L,1); LanNode.Peer peer=peer(target.getLocalPort());
            for(Long declared:new Long[]{1L,null}) { p.declared=declared; s.sendFile(peer,Uri.parse("content://feiq.test/file")); transferDone(s); }
            assertTrue(errors.toString(),errors.isEmpty()); assertEquals(2,p.opened); assertEquals(2,((Map<?,?>)field(n,"offers")).size());
            s.clearOffers(); transferDone(s); assertTrue(errors.toString(),errors.isEmpty()); assertEquals(0,new File(s.getFilesDir(),"offers").list().length); assertTrue(p.source.isFile()); assertTrue(((Map<?,?>)field(n,"offers")).isEmpty());
            s.sendFile(peer,Uri.parse("content://feiq.test/file")); transferDone(s); assertEquals(1,((Map<?,?>)field(n,"offers")).size());
        } finally { sc.destroy(); }
    }
    @Test public void old512MiBCacheDoesNotRejectUnknownOrMisreportedFiles()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get(); List<String> errors=new ArrayList<>();
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress()); LanNode n=node()) {
            settle(s); set(s,"node",n); s.observe(new ChatService.Observer(){public void changed(){}public void error(String e){errors.add(e);}});
            cache(s,512L*1024*1024-1); TestProvider p=provider(s,null,2);
            for(Long declared:new Long[]{null,1L,2L}) { errors.clear(); p.declared=declared; s.sendFile(peer(target.getLocalPort()),Uri.parse("content://feiq.test/file")); transferDone(s);
                assertTrue(errors.toString(),errors.isEmpty()); }
            assertEquals(3,p.opened); assertEquals(3,((Map<?,?>)field(n,"offers")).size());
            p.declared=Long.MAX_VALUE; s.sendFile(peer(target.getLocalPort()),Uri.parse("content://feiq.test/file")); transferDone(s);
            assertEquals("超出实际可用空间时不应打开",3,p.opened); assertTrue(errors.toString(),errors.stream().anyMatch(e->e.contains("空间不足")));
        } finally { sc.destroy(); }
    }
    @Test public void exactLimitAndZeroByteAtFullCacheAreAllowed()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get(); List<String> errors=new ArrayList<>();
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress()); LanNode n=node()) {
            settle(s); set(s,"node",n); s.observe(new ChatService.Observer(){public void changed(){}public void error(String e){errors.add(e);}});
            cache(s,512L*1024*1024-2); provider(s,2L,2); s.sendFile(peer(target.getLocalPort()),Uri.parse("content://feiq.test/file")); transferDone(s);
            assertTrue(errors.toString(),errors.isEmpty()); provider(s,0L,0); s.sendFile(peer(target.getLocalPort()),Uri.parse("content://feiq.test/file")); transferDone(s);
            assertTrue(errors.toString(),errors.isEmpty()); assertEquals(2,((Map<?,?>)field(n,"offers")).size());
        } finally { sc.destroy(); }
    }
    @Test public void dynamicSpaceAndStreamingGuardStillProtectStorage()throws Exception {
        File nearlyFull=new File("unused") { @Override public long getUsableSpace() { return 16L*1024*1024+1; } };
        assertEquals(1,LanNode.availableSpace(nearlyFull)); LanNode.requireSpace(nearlyFull,1);
        try { LanNode.requireSpace(nearlyFull,2); fail(); } catch(IOException expected) { }
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();
        try {
            Method copy=ChatService.class.getDeclaredMethod("copy",InputStream.class,OutputStream.class,LanNode.Transfer.class,long.class); copy.setAccessible(true);
            ByteArrayOutputStream output=new ByteArrayOutputStream();
            try { copy.invoke(sc.get(),new ByteArrayInputStream(new byte[2]),output,new LanNode.Transfer(),1L); fail(); }
            catch(InvocationTargetException e) { assertTrue(e.getCause() instanceof IOException); }
            assertEquals(0,output.size());
            copy.invoke(sc.get(),new ByteArrayInputStream(new byte[0]),output,new LanNode.Transfer(),0L);
        } finally { sc.destroy(); }
    }
    @Test public void keyboardImageCopyKeepsBytesAndReleasesOnSuccessFailureAndBusy()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();java.util.concurrent.atomic.AtomicInteger released=new java.util.concurrent.atomic.AtomicInteger();
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()) {
            settle(s);set(s,"node",n);LanNode.Peer p=peer(target.getLocalPort());TestProvider provider=provider(s,null,73);
            byte[] bytes=Base64.getDecoder().decode("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");Files.write(provider.source.toPath(),bytes);
            assertTrue(s.sendKeyboardImage(p,Uri.parse("content://feiq.test/file"),"image/gif",released::incrementAndGet));transferDone(s);assertEquals(1,released.get());
            File[] folders=new File(s.getFilesDir(),"offers").listFiles();assertEquals(1,folders.length);File[] copies=folders[0].listFiles();assertEquals(1,copies.length);assertTrue(copies[0].getName().endsWith(".gif"));assertArrayEquals(bytes,Files.readAllBytes(copies[0].toPath()));
            provider.declared=Long.MAX_VALUE;assertTrue(s.sendKeyboardImage(p,Uri.parse("content://feiq.test/file"),"image/png",released::incrementAndGet));transferDone(s);assertEquals(2,released.get());
            ((java.util.concurrent.atomic.AtomicBoolean)field(s,"busy")).set(true);
            try {assertFalse(s.sendKeyboardImage(p,Uri.parse("content://feiq.test/file"),"image/png",released::incrementAndGet));assertEquals(3,released.get());}
            finally {((java.util.concurrent.atomic.AtomicBoolean)field(s,"busy")).set(false);}
            provider.metadataUnsupported=true;
            assertTrue(s.sendKeyboardImage(p,Uri.parse("content://feiq.test/file"),"image/gif",released::incrementAndGet));transferDone(s);assertEquals(4,released.get());
            assertEquals(2,((Map<?,?>)field(n,"offers")).size());
        } finally {sc.destroy();}
    }
    @Test public void keyboardGrantReleasedWhenDestroyedBeforeCopyStarts()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();CountDownLatch release=new CountDownLatch(1),blocked=new CountDownLatch(1);java.util.concurrent.atomic.AtomicInteger released=new java.util.concurrent.atomic.AtomicInteger();
        boolean destroyed=false;
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()) {
            settle(s);set(s,"node",n);provider(s,null,1);block((ExecutorService)field(s,"transferWorker"),blocked,release);assertTrue(blocked.await(5,TimeUnit.SECONDS));
            assertTrue(s.sendKeyboardImage(peer(target.getLocalPort()),Uri.parse("content://feiq.test/file"),"image/png",released::incrementAndGet));assertEquals(0,released.get());
            sc.destroy();destroyed=true;assertEquals(1,released.get());
        } finally {release.countDown();if(!destroyed)sc.destroy();}
    }
    @Test public void cancellingQueuedCleanupDoesNotDeleteReceivedFiles()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();CountDownLatch blocked=new CountDownLatch(1),release=new CountDownLatch(1);
        try {
            settle(s);File file=new File(s.getFilesDir(),"received/keep-after-cancel");Files.write(file.toPath(),new byte[]{1,2,3});
            block((ExecutorService)field(s,"transferWorker"),blocked,release);assertTrue(blocked.await(5,TimeUnit.SECONDS));
            s.clearFiles();assertTrue(s.busy());s.cancelTransfer();release.countDown();transferDone(s);
            assertTrue("已经取消的排队清理仍删除了文件",file.isFile());assertArrayEquals(new byte[]{1,2,3},Files.readAllBytes(file.toPath()));assertTrue(s.transferStatus(),s.transferStatus().contains("取消"));
        }finally{release.countDown();sc.destroy();}
    }
    @Test public void duplicateRejectedInvitationDoesNotReturnToReceiveQueue()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService s=sc.get();
        try(DatagramSocket target=new DatagramSocket(0,InetAddress.getLoopbackAddress());LanNode n=node()) {
            settle(s);set(s,"node",n);s.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();LanNode.Peer p=peer(target.getLocalPort());
            LanNode.Message invitation=message();invitation.files=Collections.singletonList(new org.feiqlight.android.core.Protocol.Attachment(0,"rejected.bin",12,1));
            s.incoming(p,invitation,false);settle(s);assertEquals(1,((Map<?,?>)field(s,"waiting")).size());
            s.reject(p,invitation.number);settle(s);assertTrue(((Map<?,?>)field(s,"waiting")).isEmpty());
            s.incoming(p,invitation,false);settle(s);assertTrue("已拒绝邀请被重复包重新排队",((Map<?,?>)field(s,"waiting")).isEmpty());
            assertEquals("已拒绝",io(s).submit(()->store(s).history(p.id()).get(0).state).get(5,TimeUnit.SECONDS));
        }finally{sc.destroy();}
    }
    @Test public void clearingOffersRefusesActiveFileConnection()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService s=sc.get(); List<String> errors=new ArrayList<>();
        try(LanNode n=node(); Socket connection=new Socket()) {
            settle(s); set(s,"node",n); s.observe(new ChatService.Observer(){public void changed(){}public void error(String e){errors.add(e);}}); File existing=cache(s,1);
            connection.connect(new InetSocketAddress(InetAddress.getLoopbackAddress(),(Integer)field(n,"port")));
            long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3); while(((Set<?>)field(n,"connections")).isEmpty()&&System.nanoTime()<until) Thread.sleep(10);
            s.clearOffers(); transferDone(s); assertTrue(existing.isFile()); assertTrue(errors.toString(),errors.stream().anyMatch(e->e.contains("文件连接")));
        } finally { sc.destroy(); }
    }
}
