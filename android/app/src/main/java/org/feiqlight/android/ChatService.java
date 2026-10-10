package org.feiqlight.android;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.net.*;
import android.net.wifi.WifiManager;
import android.os.*;
import android.provider.OpenableColumns;
import org.feiqlight.android.core.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

public final class ChatService extends Service {
    public interface Observer { void changed(); void error(String text); default void transfersChanged() { changed(); } }
    public final class LocalBinder extends Binder { ChatService service() { return ChatService.this; } }
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ExecutorService io=Executors.newSingleThreadExecutor();
    private final ExecutorService discovery=Executors.newSingleThreadExecutor();
    private final AtomicBoolean refreshQueued=new AtomicBoolean(),publishQueued=new AtomicBoolean();
    private final ExecutorService transferWorker=Executors.newSingleThreadExecutor();
    private final AtomicBoolean busy=new AtomicBoolean();
    private final Set<Observer> observers=new HashSet<>();
    private final LocalBinder binder=new LocalBinder();
    private ChatStore store;
    private ReceivedStorage storage;
    private final LinkedHashMap<String,ReceiveRequest> waiting=new LinkedHashMap<>();
    private volatile boolean disconnecting;
    private static final class ReceiveRequest {
        final LanNode.Peer peer; final long packet; final Protocol.Attachment file;
        ReceiveRequest(LanNode.Peer peer,long packet,Protocol.Attachment file) { this.peer=peer; this.packet=packet; this.file=file; }
    }
    private volatile LanNode node;
    private volatile boolean stopped, connecting;
    private final Object connectionLock=new Object();
    private volatile long connectionEpoch;
    // 工厂不接受外部 Intent 参数；回归测试可替换为真实回环节点，避免接触用户局域网。
    java.util.function.BiFunction<SharedPreferences,LanNode.Listener,LanNode> connectionFactory=(prefs,listener)->new LanNode(prefs.getInt("port",2425),false,prefs.getString("login",""),"Android",prefs.getString("name",Build.MODEL),prefs.getString("group","我的局域网"),listener);
    private volatile String activePeer="";
    private volatile LanNode.Transfer transfer;
    private volatile Runnable transferCleanup;
    private final java.util.concurrent.atomic.AtomicLong transferEpoch=new java.util.concurrent.atomic.AtomicLong();
    private Diagnostics diagnostics;
    private WifiManager.MulticastLock multicast;
    private ConnectivityManager connectivity;
    private ConnectivityManager.NetworkCallback callback;
    private List<ChatStore.Conversation> conversations=new ArrayList<>();
    private String status="未连接", transferStatus="";
    private volatile String transferOutcome="";
    static final class FileProgress {
        final int percent; final String state; final LanNode.Transfer task;
        FileProgress(int percent,String state,LanNode.Transfer task) {this.percent=percent;this.state=state;this.task=task;}
    }
    private final LinkedHashMap<String,FileProgress> fileProgress=new LinkedHashMap<>();
    private boolean transferNotificationQueued;
    private final Runnable notifyTransfers=()->{transferNotificationQueued=false;for(Observer observer:new ArrayList<>(observers))observer.transfersChanged();};
    static String fileKey(String peer,long packet,long file,boolean outgoing) {return peer+"/"+packet+"/"+file+"/"+outgoing;}
    FileProgress progress(String peer,long packet,long file,boolean outgoing) {return fileProgress.get(fileKey(peer,packet,file,outgoing));}
    private void fileProgress(String peer,long packet,long file,boolean outgoing,int percent,String state,LanNode.Transfer task) {
        String key=fileKey(peer,packet,file,outgoing);
        fileProgress.remove(key);fileProgress.put(key,new FileProgress(Math.max(0,Math.min(100,percent)),state,task));
        while(fileProgress.size()>2000)fileProgress.remove(fileProgress.keySet().iterator().next());
        // 限频而非反复延后：连续的大文件进度也必须在传输途中画出来。
        if(!transferNotificationQueued){transferNotificationQueued=true;main.postDelayed(notifyTransfers,60);}
    }
    void cancelFile(String peer,long packet,long file) {FileProgress p=progress(peer,packet,file,false);if(p!=null&&p.task!=null&&p.task==transfer)p.task.close();}
    boolean offering(String peer,long packet,long file) {LanNode current=node;return current!=null&&current.offering(peer,packet,file);}
    private boolean notificationQueued;
    private final Runnable notifyChanges=()->{notificationQueued=false;for (Observer observer:new ArrayList<>(observers)) observer.changed();};
    @Override public void onCreate() {
        super.onCreate(); store=new ChatStore(this); storage=new ReceivedStorage(this);diagnostics=new Diagnostics(this);
        NotificationManager manager=getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("connection","局域网连接",NotificationManager.IMPORTANCE_LOW));
        manager.createNotificationChannel(new NotificationChannel("messages","新消息",NotificationManager.IMPORTANCE_DEFAULT));
        io.execute(()->{ try { store.recoverPending(); cleanStaging(); publish(); } catch (Exception e) { fail(e); } });
    }
    @Override public IBinder onBind(Intent intent) { return binder; }
    static boolean shouldRestoreConnection(Context context) {
        SharedPreferences prefs=context.getSharedPreferences("settings",MODE_PRIVATE);
        // 旧版在首次连接时生成 login；新安装没有此标记，不擅自加入局域网。
        return prefs.getBoolean("auto_connect",true) && prefs.getBoolean("connection_enabled",prefs.contains("login"));
    }
    boolean connectionActive() { return node!=null || connecting || disconnecting; }
    boolean canDisconnect() { return node!=null || connecting; }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if (intent!=null && "stop".equals(intent.getAction())) { disconnect(); return START_NOT_STICKY; }
        if(node!=null || connecting) return START_NOT_STICKY;
        if(stopped || (intent!=null && "restore".equals(intent.getAction()) && !shouldRestoreConnection(this))) { stopSelf(startId); return START_NOT_STICKY; }
        // 必须先进入前台再异步绑定套接字；恢复仅由用户打开的 Activity 发起。
        try { startForeground(1,notification("connection","局域网连接","正在连接…",true)); connect(); }
        catch (RuntimeException e) { fail(e); stopSelf(); }
        return START_NOT_STICKY;
    }
    private Notification notification(String channel,String title,String text,boolean ongoing) {
        PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder b=new Notification.Builder(this,channel).setSmallIcon(R.drawable.ic_notify).setContentTitle(title).setContentText(text).setContentIntent(open).setOngoing(ongoing).setAutoCancel(!ongoing).setVisibility(Notification.VISIBILITY_PRIVATE);
        if (ongoing) b.addAction(new Notification.Action.Builder(null,"断开",PendingIntent.getService(this,1,new Intent(this,ChatService.class).setAction("stop"),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT)).build());
        return b.build();
    }
    private void connect() {
        diagnostics.record(Diagnostics.Event.Connecting);
        synchronized(connectionLock) {
            if(stopped || node!=null || connecting) return;
            long epoch=++connectionEpoch; disconnecting=false; connecting=true; status="正在连接…";
            io.execute(()->connectInBackground(epoch));
        }
        changed();
    }
    private boolean currentConnection(long epoch) { return !stopped && epoch==connectionEpoch; }
    private void connectInBackground(long epoch) {
            if(!currentConnection(epoch)) return;
            LanNode next=null;
            try {
                SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE);
                if(!prefs.contains("connection_enabled")) prefs.edit().putBoolean("connection_enabled",prefs.contains("login")).apply();
                String login=prefs.getString("login",null); if(login==null) { login="android-"+UUID.randomUUID().toString().substring(0,8); prefs.edit().putString("login",login).apply(); }
                next=connectionFactory.apply(prefs,new LanNode.Listener() {
                    @Override public void peersChanged() { if(currentConnection(epoch)) requestPublish(); }
                    @Override public void error(String text) { if(currentConnection(epoch)) showError(text); }
                    @Override public void message(LanNode.Peer peer,LanNode.Message message,boolean update) { if(currentConnection(epoch)||update) incoming(peer,message,update,epoch); }
                    @Override public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state) {
                        main.post(()->{if(currentConnection(epoch))ChatService.this.fileProgress(peer.id(),packet,file,true,percent,state,null);});
                    }
                });
                next.rememberEndpoints(store.conversations().stream().map(c->c.peer.endpoint).collect(java.util.stream.Collectors.toList()));
                next.setSocketBinding(new LanNetwork(getSystemService(ConnectivityManager.class)));
                if(!currentConnection(epoch)) { next.close(); return; }
                next.start();
                // 节点只在成功启动且仍属于本次请求时发布；销毁/断开不能漏掉尚未发布的节点。
                synchronized(connectionLock) { if(currentConnection(epoch)) node=next; }
                if(node!=next) { next.close(); return; }
                final LanNode connected=next;
                main.post(()->{
                    if (!currentConnection(epoch) || node!=connected) return;
                    prefs.edit().putBoolean("connection_enabled",true).apply();
                    try {
                        WifiManager wifi=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
                        if (wifi!=null) { multicast=wifi.createMulticastLock("FeiqLight"); multicast.setReferenceCounted(false); multicast.acquire(); }
                        connectivity=getSystemService(ConnectivityManager.class);
                        callback=new ConnectivityManager.NetworkCallback() {
                            private void update() {if(currentConnection(epoch)&&node==connected){connected.networkChanged();refresh();}}
                            @Override public void onAvailable(Network network) { diagnostics.record(Diagnostics.Event.NetworkChanged);update(); }
                            @Override public void onLost(Network network) { update(); }
                            @Override public void onLinkPropertiesChanged(Network network,LinkProperties properties) { update(); }
                            @Override public void onCapabilitiesChanged(Network network,NetworkCapabilities capabilities) { update(); }
                        };
                        connectivity.registerNetworkCallback(new NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),callback);
                    } catch (RuntimeException e) { showError("自动发现可能受限，可尝试手动 IP："+e.getMessage()); }
                    diagnostics.record(Diagnostics.Event.Connected);connecting=false; status="已连接 · UDP / TCP "+prefs.getInt("port",2425); changed();
                    getSystemService(NotificationManager.class).notify(1,notification("connection","飞Q正在运行","可接收局域网消息 · 点击返回",true));
                });
                try { publish(); } catch(Exception e) { fail(e); }
            } catch (Exception e) {
                if(next!=null) next.close();
                synchronized(connectionLock) { if(node==next) node=null; }
                main.post(()->{ if(!currentConnection(epoch)) return; connecting=false; status="连接失败"; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); changed(); fail(e); });
            }
    }
    void incoming(LanNode.Peer peer,LanNode.Message message,boolean update) {
        incoming(peer,message,update,connectionEpoch);
    }
    private void incoming(LanNode.Peer peer,LanNode.Message message,boolean update,long epoch) {
        queue(()->{
            boolean inserted=store.message(peer,message,update,peer.id().equals(activePeer)); publish();
            if(inserted&&!update&&!message.outgoing) main.post(()->{
                if(!currentConnection(epoch)||disconnecting) return;
                for(Protocol.Attachment file:message.files) {
                    String key=receivedFile(peer,message.number,file).getName();
                    if(waiting.size()>=100) { showError("待接收文件已达 100 个，请处理后让对方重发"); break; }
                    waiting.putIfAbsent(key,new ReceiveRequest(peer,message.number,file));
                }
                drainReceives();
                if(!peer.id().equals(activePeer)&&(Build.VERSION.SDK_INT<33||checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)==PackageManager.PERMISSION_GRANTED)) {
                    String sender=conversations.stream().filter(c->c.peer.id().equals(peer.id())).map(ChatStore.Conversation::displayName).findFirst().orElse(peer.name);
                    getSystemService(NotificationManager.class).notify(2,notification("messages",sender,message.files.isEmpty()?"收到一条局域网消息":storage.automatic()?"收到文件，正在自动接收":"收到文件邀请",false));
                }
            });
        });
    }
    void receiveSettingsChanged() { drainReceives(); changed(); }
    private void drainReceives() {
        if(stopped||disconnecting||node==null||busy()||!storage.automatic()||waiting.isEmpty()) return;
        ReceiveRequest next=waiting.values().iterator().next(); receive(next.peer,next.packet,next.file);
    }
    void observe(Observer o) { observers.add(o); o.changed(); }
    void remove(Observer o) { observers.remove(o); }
    List<ChatStore.Conversation> conversations() { return conversations.stream().filter(c->!c.hidden).collect(java.util.stream.Collectors.toList()); }
    List<ChatStore.Conversation> hiddenConversations() { return conversations.stream().filter(c->c.hidden).collect(java.util.stream.Collectors.toList()); }
    void editConversation(String id,String note,boolean pinned) {
        if(note.length()>64){showError("备注最多 64 个字符");return;}
        queue(()->{store.edit(id,note,pinned);publish();});
    }
    void removeConversation(String id,boolean clear,boolean hide,Consumer<Boolean> done) {
        if(clear && (busy() || waiting.values().stream().anyMatch(r->r.peer.id().equals(id)))) {showError("请先完成或拒绝待接收文件，再清空记录");done.accept(false);return;}
        queue(()->{try {store.remove(id,clear,hide);publish();main.post(()->done.accept(true));}catch(Exception e){fail(e);main.post(()->done.accept(false));}});
    }
    boolean online() { return node!=null && !connecting; }
    boolean busy() { return busy.get(); }
    String status() { return status; }
    // 工作线程结束到主线程终态回调之间，不能继续显示“尚未发送”的旧准备提示。
    String transferStatus() { return busy()?transferStatus:transferOutcome; }
    void active(String peer) {
        String target=peer==null?"":peer; activePeer=target;
        if(!target.isEmpty()) queue(()->{ store.read(target); publish(); });
    }
    void history(String peer,Consumer<List<LanNode.Message>> result) { queue(()->{ List<LanNode.Message> rows=store.history(peer); main.post(()->result.accept(rows)); }); }
    private void changed() { if(!notificationQueued){notificationQueued=true;main.postDelayed(notifyChanges,60);} }
    private void requestPublish() {
        if(stopped||!publishQueued.compareAndSet(false,true))return;
        queue(()->{publishQueued.set(false);publish();});
    }
    private void queue(Runnable job) {
        if (stopped) return;
        try { io.execute(()->{ try { job.run(); } catch (Exception e) { fail(e); } }); } catch (RejectedExecutionException ignored) { }
    }
    private void publish() {
        LanNode current=node; Map<String,LanNode.Peer> live=new HashMap<>();
        if (current!=null) {List<LanNode.Peer> peers=current.peers();store.peers(peers);for(LanNode.Peer p:peers)live.put(p.id(),p);}
        List<ChatStore.Conversation> list=store.conversations();
        for (ChatStore.Conversation item:list) if (live.containsKey(item.peer.id())) item.peer=live.get(item.peer.id());
        main.post(()->{ if (!stopped) { conversations=list; changed(); } });
    }
    void refresh() {
        if(stopped||!refreshQueued.compareAndSet(false,true))return;
        LanNode current=node;long epoch=connectionEpoch;
        try {discovery.execute(()->{try {if(current!=null&&node==current&&currentConnection(epoch))current.refresh();}catch(IOException e){if(currentConnection(epoch))fail(e);}finally{refreshQueued.set(false);}});}
        catch(RejectedExecutionException ignored){refreshQueued.set(false);}
    }
    void probe(String address) {
        if(stopped)return;long epoch=connectionEpoch;
        try {discovery.execute(()->{try {if(currentConnection(epoch))requireNode().probe(LanNode.endpoint(address));}catch(IOException e){if(currentConnection(epoch))fail(e);}});}catch(RejectedExecutionException ignored){}
    }
    private LanNode requireNode() throws IOException { LanNode current=node; if (current==null||stopped||disconnecting) throw new IOException("请先连接局域网"); return current; }
    void send(LanNode.Peer peer,String text,Consumer<Boolean> done) {
        DraftStore drafts=new DraftStore(this); DraftStore.Snapshot sent=drafts.snapshot(peer.id());
        if(stopped){main.post(()->done.accept(false));return;}
        try {io.execute(()->{ try { requireNode().sendMessage(peer,text,Collections.emptyList()); main.post(()->{
                if(sent.text.equals(text)) drafts.clearIfUnchanged(peer.id(),sent);
                changed(); done.accept(true);
            }); }
            catch (Exception e) { fail(e); main.post(()->done.accept(false)); } });}
        catch(RejectedExecutionException e){main.post(()->done.accept(false));}
    }
    void retryMessage(LanNode.Peer peer,LanNode.Message original,Consumer<Boolean> done) {
        queue(()->{
            try {
                LanNode.Message record=store.history(peer.id()).stream().filter(m->m.number==original.number&&m.outgoing).findFirst().orElseThrow(()->new IOException("原消息已不存在"));
                if(!(record.state.contains("未确认")||record.state.equals("发送失败"))) throw new IOException("此消息已经重发或已送达");
                if(!record.files.isEmpty()) throw new IOException("请重新选择原文件发送");
                requireNode().sendMessage(peer,record.text,Collections.emptyList());
                record.state="已重新发送（新消息）";store.message(peer,record,true,peer.id().equals(activePeer));publish();main.post(()->done.accept(true));
            }catch(Exception e){fail(e);main.post(()->done.accept(false));}
        });
    }
    void forward(LanNode.Peer source,LanNode.Message message,LanNode.Peer target,Consumer<Boolean> done) {
        if(stopped||!online()){main.post(()->done.accept(false));showError("请先连接局域网");return;}
        if(message.files.isEmpty()) {
            queue(()->{try {requireNode().sendMessage(target,message.text,Collections.emptyList());main.post(()->done.accept(true));}
                catch(Exception e){fail(e);main.post(()->done.accept(false));}});
            return;
        }
        AtomicBoolean success=new AtomicBoolean();
        transferJob(task->{
            List<File> files=new ArrayList<>();
            try {
                // 转发不走普通输入发送的草稿清理路径；为目标重新生成消息号及授权。
                if(message.outgoing&&message.localPaths.size()!=message.files.size())throw new IOException("旧发送记录没有原文件位置，请重新选择原文件发送");
                int index=0;
                for(Protocol.Attachment attachment:message.files){
                    checkTransfer(task);
                    if(!Protocol.safeName(attachment.name))throw new IOException("附件名称不安全");
                    File local=message.outgoing?new File(message.localPaths.get(index++)):receivedFile(source,message.number,attachment);
                    Uri exported=!message.outgoing&&!local.isFile()?storage.saved(local):null;
                    if(exported==null&&(!local.isFile()||local.length()!=attachment.size))throw new IOException("附件尚未接收或副本已清理，请先接收或重新选择文件");
                    // 与普通批量发送一样逐文件隔离；同名附件仍保留原名和独立授权。
                    File folder=new File(directory("offers"),UUID.randomUUID().toString());
                    if(!folder.mkdir())throw new IOException("无法暂存转发附件");
                    File copy=new File(folder,attachment.name);
                    files.add(copy);
                    try(InputStream input=exported==null?new FileInputStream(local):getContentResolver().openInputStream(exported);OutputStream output=new FileOutputStream(copy)){
                        copy(input,output,task,remaining(folder),"暂存空间不足");
                    }
                    if(copy.length()!=attachment.size)throw new IOException("附件大小已改变，请重新选择文件发送");
                }
                checkTransfer(task);
                requireNode().sendMessage(target,message.text,files);
                success.set(true);
            }finally {
                if(!success.get()) {
                    IOException cleanupFailure=null;
                    for(File file:files) try {Files.deleteIfExists(file.toPath());Files.deleteIfExists(file.getParentFile().toPath());}
                    catch(IOException e) {if(cleanupFailure==null)cleanupFailure=new IOException("转发失败，部分暂存副本未清除，可在设置中清理发送缓存");cleanupFailure.addSuppressed(e);}
                    if(cleanupFailure!=null)throw cleanupFailure;
                }
            }
        },()->main.post(()->done.accept(success.get())));
    }
    void disconnect() {
        diagnostics.record(Diagnostics.Event.Disconnected);
        getSharedPreferences("settings",MODE_PRIVATE).edit().putBoolean("connection_enabled",false).apply();
        synchronized(connectionLock) {
            if(stopped) return;
            long epoch=++connectionEpoch; LanNode old=node; node=null; disconnecting=true; connecting=false; status="正在断开…";
            // 与建连共用串行队列；旧完成回调不得停止随后发起的新连接。
            io.execute(()->{ if(old!=null) old.close(); try { publish(); } catch(Exception e) { fail(e); }
                main.post(()->{ if(!currentConnection(epoch)) return; disconnecting=false; status="已断开"; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); changed(); }); });
        }
        waiting.clear(); cancelTransfer(); releaseNetwork(); changed();
        for(Map.Entry<String,FileProgress> entry:fileProgress.entrySet()) {
            FileProgress value=entry.getValue();if(value.state.contains("中"))entry.setValue(new FileProgress(value.percent,"连接已断开",null));
        }
        main.removeCallbacks(notifyTransfers);main.post(notifyTransfers);
    }
    private void releaseNetwork() {
        if (multicast!=null && multicast.isHeld()) multicast.release(); multicast=null;
        if (callback!=null && connectivity!=null) { try { connectivity.unregisterNetworkCallback(callback); } catch (RuntimeException ignored) { } } callback=null;
    }
    void cancelTransfer() { LanNode.Transfer task=transfer; if (task!=null) task.close(); }
    private void transferJob(Throwing job) {
        transferJob(job,()->{});
    }
    private boolean transferJob(Throwing job,Runnable finished) {
        return transferJob(job,finished,"文件任务已完成","已取消，可重新操作");
    }
    private boolean transferJob(Throwing job,Runnable finished,String completion,String cancellation) {
        AtomicBoolean finishedOnce=new AtomicBoolean();
        Runnable cleanup=()->{if(finishedOnce.compareAndSet(false,true)) try {finished.run();} catch(RuntimeException e){fail(e);}};
        if (stopped || !busy.compareAndSet(false,true)) { cleanup.run(); showError("已有文件任务或服务已停止，请等待或取消"); return false; }
        transferCleanup=cleanup;
        long epoch=transferEpoch.incrementAndGet();
        LanNode.Transfer task=new LanNode.Transfer(); transfer=task; transferStatus="准备文件…";diagnostics.record(Diagnostics.Event.TaskStarted); changed();
        try { transferWorker.execute(()->{
            String result=completion;
            try { checkTransfer(task); job.run(task);diagnostics.record(Diagnostics.Event.TaskCompleted); }
            catch(Exception e) { result=completion.isEmpty()&&cancellation.isEmpty()?"":task.isCancelled()?cancellation:"文件任务失败："+e.getMessage();diagnostics.record(task.isCancelled()?Diagnostics.Event.TaskCancelled:Diagnostics.Event.TaskFailed); fail(e); }
            finally {
                cleanup.run(); if(transferCleanup==cleanup) transferCleanup=null;
                String outcome=result; transferOutcome=outcome;transfer=null;busy.set(false);
                main.post(()->{ if(!stopped&&transferEpoch.get()==epoch) { transferStatus=outcome; changed(); drainReceives(); } });
            }
        }); return true; } catch(RejectedExecutionException e) { cleanup.run(); transferCleanup=null; transfer=null; busy.set(false); if(!stopped) showError("文件服务已停止，请重新连接"); return false; }
    }
    private interface Throwing { void run(LanNode.Transfer task) throws Exception; }
    private static void checkTransfer(LanNode.Transfer task) throws IOException {
        if(task.isCancelled()||Thread.currentThread().isInterrupted()) throw new IOException("文件任务已取消");
    }
    private File directory(String name) throws IOException {
        File directory=new File(getFilesDir(),name); if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("无法创建应用文件目录"); return directory;
    }
    private long remaining(File directory) throws IOException {
        return LanNode.availableSpace(directory);
    }
    private void quota(File directory,long extra) throws IOException {
        LanNode.requireSpace(directory,extra);
    }
    private void cleanStaging() throws IOException {
        File[] folders=directory("offers").listFiles();
        if (folders!=null) for (File folder:folders) if (folder.lastModified()<System.currentTimeMillis()-TimeUnit.MINUTES.toMillis(35)) {
            File[] files=folder.listFiles(); LanNode current=node;
            if(files!=null&&current!=null&&Arrays.stream(files).anyMatch(current::isOffered)) continue;
            if(files!=null) for(File f:files) if(f.isFile()) Files.deleteIfExists(f.toPath()); Files.deleteIfExists(folder.toPath());
        }
        // 启动/选文件不能删除可恢复断点；只回收一天未写入的残片。
        File[] received=directory("received").listFiles(); if(received!=null) for(File f:received) if(f.getName().endsWith(".part") && f.lastModified()<System.currentTimeMillis()-TimeUnit.DAYS.toMillis(1)) Files.deleteIfExists(f.toPath());
    }
    void sendFile(LanNode.Peer peer,Uri uri) {
        sendFile(peer,uri,null,()->{});
    }
    boolean sendKeyboardImage(LanNode.Peer peer,Uri uri,String mime,Runnable release) {
        if(!online()||disconnecting) { release.run(); showError("请先连接局域网"); return false; }
        return sendFile(peer,uri,mime,release);
    }
    private boolean sendFile(LanNode.Peer peer,Uri uri,String keyboardMime,Runnable finished) {
        return sendFiles(peer,Collections.singletonList(uri),keyboardMime,finished);
    }
    void sendFiles(LanNode.Peer peer,List<Uri> uris) { sendFiles(peer,uris,null,()->{}); }
    private boolean sendFiles(LanNode.Peer peer,List<Uri> uris,String keyboardMime,Runnable finished) {
        if(uris==null||uris.isEmpty()||uris.size()>20) { finished.run();showError("每批请选择 1–20 个文件");return false; }
        List<Uri> batch=new ArrayList<>(new LinkedHashSet<>(uris));
        return transferJob(task->{
            LanNode current=requireNode(); List<File> staged=new ArrayList<>();boolean sent=false;
            cleanStaging();
            try {
                for(Uri uri:batch) {
                    int index=staged.size()+1;
                    main.post(()->{if(!stopped&&transfer==task&&!task.isCancelled()){transferStatus="正在准备发送文件 "+index+" / "+batch.size();changed();}});
                    if(task.isCancelled()) throw new IOException("已取消发送");
                    if(uri==null||!"content".equals(uri.getScheme())) throw new IOException("请选择系统文件选择器授权的文件");
                    String name="文件"; long declared=-1;
                    try(Cursor c=getContentResolver().query(uri,new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE},null,null)) {
                        if(c!=null&&c.moveToFirst()) {
                            int nameColumn=c.getColumnIndex(OpenableColumns.DISPLAY_NAME), sizeColumn=c.getColumnIndex(OpenableColumns.SIZE);
                            if(nameColumn>=0) name=c.getString(nameColumn);
                            if(sizeColumn>=0 && !c.isNull(sizeColumn)) declared=c.getLong(sizeColumn);
                        }
                    } catch(IllegalArgumentException | UnsupportedOperationException e) {
                        // 输入法 URI 不一定实现文件选择器的元数据查询；复制仍必须取得真实读取授权。
                        if(keyboardMime==null) throw e;
                        name="键盘表情"; declared=-1;
                    }
                    if(keyboardMime!=null) {
                        String extension;
                        switch(keyboardMime) { case "image/png": extension=".png"; break; case "image/jpeg": extension=".jpg"; break; case "image/gif": extension=".gif"; break; case "image/webp": extension=".webp"; break; default: throw new IOException("输入法图片格式不支持"); }
                        // 输入法可能只给 UUID 或临时名称；为副本补真实 MIME 对应后缀，不改变原始字节。
                        if(!Protocol.safeName(name)) name="键盘表情";
                        String lower=name.toLowerCase(Locale.ROOT);
                        if(!lower.endsWith(extension)&&!(keyboardMime.equals("image/jpeg")&&lower.endsWith(".jpeg"))) name+=extension;
                        if(!Protocol.safeName(name)) name="键盘表情"+extension;
                    }
                    if(!Protocol.safeName(name)) throw new IOException("文件名称不安全或过长，请先重命名");
                    File offers=directory("offers"); long available=remaining(offers);
                    if(declared>available) throw new IOException("暂存空间不足，请清理发送缓存或释放设备空间");
                    File folder=new File(offers,UUID.randomUUID().toString()); if(!folder.mkdir()) throw new IOException("无法暂存文件");
                    File file=new File(folder,name); staged.add(file);
                    final String displayName=name;final long declaredSize=declared;
                    Consumer<Long> copying=bytes->main.post(()->{if(!stopped&&transfer==task&&!task.isCancelled()){
                        transferStatus="本地准备 "+index+" / "+batch.size()+" · "+displayName+" · "+String.format(Locale.ROOT,"%.1f MiB",bytes/1048576.0)+(declaredSize>0?" / "+String.format(Locale.ROOT,"%.1f MiB",declaredSize/1048576.0):"")+"（尚未发送）";changed();
                    }});
                        // 不信任提供方上报大小：未知/错误大小同样在每次写入前受剩余容量限制。
                        try(InputStream input=getContentResolver().openInputStream(uri); OutputStream output=new BufferedOutputStream(new FileOutputStream(file),65536)) { copy(input,output,task,available,"暂存空间不足，请清理发送缓存或释放设备空间",copying); }
                        if(task.isCancelled()) throw new IOException("已取消发送");
                }
            // 全批暂存完成后只发一条邀请；中途失败不会留下半批已发消息。
            if(task.isCancelled()) throw new IOException("已取消发送");
            current.sendMessage(peer,"",staged); sent=true;
            } finally {
                if(!sent) {
                    IOException cleanupFailure=null;
                    for(File file:staged) try {Files.deleteIfExists(file.toPath());Files.deleteIfExists(file.getParentFile().toPath());}
                    catch(IOException e) {if(cleanupFailure==null)cleanupFailure=new IOException("发送失败，部分暂存副本未清除，可在设置中清理发送缓存");cleanupFailure.addSuppressed(e);}
                    if(cleanupFailure!=null)throw cleanupFailure;
                }
            }
        },finished,"","已取消发送；请重新选择文件");
    }
    File receivedFile(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        try {
            byte[] digest=MessageDigest.getInstance("SHA-256").digest((peer.id()+"/"+packet+"/"+file.id+"/"+file.name+"/"+file.size).getBytes(StandardCharsets.UTF_8));
            StringBuilder key=new StringBuilder(); for(byte b:digest) key.append(String.format(Locale.ROOT,"%02x",b&255));
            return new File(new File(getFilesDir(),"received"),key.toString());
        } catch(Exception e) { throw new IllegalStateException(e); }
    }
    void receive(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        if(busy()) { showError("已有文件任务，请等待或取消"); return; }
        File target=receivedFile(peer,packet,file); waiting.remove(target.getName());
        transferJob(task->{
            long epoch=transferEpoch.get();
            java.util.function.BiConsumer<Integer,String> progress=(percent,state)->main.post(()->{
                if(!stopped&&transferEpoch.get()==epoch)fileProgress(peer.id(),packet,file.id,false,percent,state,state.contains("中")?task:null);
            });
            int[] last={0};
            try {
            progress.accept(0,"连接中 · 点击取消");
            File dir=directory("received");
            if(!target.isFile()) {
                long partial=LanNode.partialFile(peer,packet,file,target).length();
                quota(dir,file.size-(partial<=file.size?partial:0));
                requireNode().receiveFile(peer,packet,file,target,task,percent->{last[0]=percent;progress.accept(percent,"接收中 · 点击取消");});
            }
            progress.accept(100,"保存中 · 点击取消");
            storage.save(target,file.name,task);
            progress.accept(100,"已保存");
            } catch(Exception e) {
                progress.accept(target.isFile()?100:task.isCancelled()?0:last[0],target.isFile()?"已接收 · 另存未完成":task.isCancelled()?"已取消 · 点击重试":"接收失败 · 点击续传");throw e;
            }
        },()->{},"","");
        // 已有关联消息的任务不再占据输入框上方；复制/导出等无消息任务仍保留公共提示。
        transferStatus=""; changed();
    }
    void saveToFolder(File file,String name) { transferJob(task->storage.save(file,name,task)); }
    void reject(LanNode.Peer peer,long packet) {
        if(busy()) { showError("请先取消当前文件任务"); return; }
        waiting.values().removeIf(r->r.peer.id().equals(peer.id())&&r.packet==packet);
        queue(()->{
            try { requireNode().reject(peer,packet); }
            catch(IOException e) { fail(e); return; }
            for(LanNode.Message m:store.history(peer.id())) if(!m.outgoing&&m.number==packet) {
                for(Protocol.Attachment f:m.files) try { Files.deleteIfExists(LanNode.partialFile(peer,packet,f,receivedFile(peer,packet,f)).toPath()); } catch(IOException e) { fail(e); }
                m.files.removeIf(f->{ File saved=receivedFile(peer,packet,f); return !saved.isFile()&&storage.saved(saved)==null; }); m.text+=(m.text.isEmpty()?"":"\n")+"[附件已拒绝]"; m.state="已拒绝"; store.message(peer,m,true,true); break;
            }
            publish();
        });
    }
    void exportFile(File file,Uri destination) {
        transferJob(task->{
            try(InputStream in=new FileInputStream(file); OutputStream out=getContentResolver().openOutputStream(destination,"wt")) {
                if(out==null) throw new IOException("无法打开保存位置"); copy(in,out,task,LanNode.MAX_FILE);
            } catch(Exception e) { throw new IOException("导出未完成，所选位置可能留下不完整副本；应用内原文件未改动。"+e.getMessage(),e); }
            main.post(()->showError("文件已另存为"));
        });
    }
    private void copy(InputStream input,OutputStream output,LanNode.Transfer task,long limit) throws IOException {
        copy(input,output,task,limit,"文件超过可用存储空间");
    }
    private void copy(InputStream input,OutputStream output,LanNode.Transfer task,long limit,String limitError) throws IOException {
        copy(input,output,task,limit,limitError,null);
    }
    private void copy(InputStream input,OutputStream output,LanNode.Transfer task,long limit,String limitError,Consumer<Long> progress) throws IOException {
        if(input==null) throw new IOException("无法读取所选文件"); byte[] buffer=new byte[65536]; long total=0; int count;
        long last=System.nanoTime();if(progress!=null)progress.accept(0L);
        while(true) {
            checkTransfer(task); count=input.read(buffer); checkTransfer(task); if(count==-1) break; if(count>limit-total) throw new IOException(limitError); total+=count; output.write(buffer,0,count);
            if(progress!=null&&System.nanoTime()-last>=TimeUnit.MILLISECONDS.toNanos(100)){progress.accept(total);last=System.nanoTime();}
        }
        if(progress!=null)progress.accept(total);
    }
    void clearFiles() {
        if(busy()) { showError("请先完成或取消文件任务"); return; }
        transferJob(task->{ File[] files=directory("received").listFiles(); if(files!=null) for(File file:files) { checkTransfer(task); if(file.isFile()) Files.deleteIfExists(file.toPath()); } });
    }
    void clearOffers() {
        transferJob(task->{
            LanNode current=node; if(current!=null) current.revokeOffers();
            File[] folders=directory("offers").listFiles(); if(folders==null) throw new IOException("无法读取发送缓存");
            for(File folder:folders) {
                checkTransfer(task);
                if(folder.isDirectory()) { File[] files=folder.listFiles(); if(files==null) throw new IOException("无法读取发送缓存"); for(File file:files) { checkTransfer(task); if(file.isFile()) Files.deleteIfExists(file.toPath()); } }
                checkTransfer(task);
                Files.deleteIfExists(folder.toPath());
            }
        });
    }
    private void fail(Exception e) { diagnostics.record(Diagnostics.Event.Error);showError(e.getMessage()==null?e.getClass().getSimpleName():e.getMessage()); }
    private void showError(String text) { main.post(()->{ for(Observer o:new ArrayList<>(observers)) o.error(text); }); }
    @Override public void onDestroy() {
        diagnostics.record(Diagnostics.Event.Stopped);
        synchronized(connectionLock) {
            stopped=true; connectionEpoch++; connecting=false; disconnecting=true;
            LanNode old=node; node=null;
            // 先使全部旧任务失效，再在队列末尾关闭节点/数据库；不能先读取 null 再让排队任务打开端口。
            io.execute(()->{ try { if(old!=null) old.close(); } finally { try { store.recoverPending(); } finally { store.close(); } } }); io.shutdown();
        }
        waiting.clear(); cancelTransfer();
        releaseNetwork(); main.removeCallbacks(notifyChanges); main.removeCallbacks(notifyTransfers); observers.clear(); transferWorker.shutdownNow();discovery.shutdownNow();
        // shutdownNow 会丢弃尚未执行的复制任务，它们不会进入 finally；销毁时也需归还 IME 授权。
        Runnable cleanup=transferCleanup; if(cleanup!=null) cleanup.run();
        super.onDestroy();
    }
}
