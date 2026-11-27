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
    private static volatile ChatService instance;
    static int phoneRemotePort() throws IOException {
        ChatService current=instance;
        if(current==null)throw new IOException("请先连接局域网，再开启手机远控");
        return PhoneRemoteAccess.portFor(current.requireNode().port());
    }
    static final int LEGACY_POLICY_VERSION=1;
    public interface Observer { void changed(); void error(String text); default void transfersChanged() { changed(); } default void sent(String peer) { } }
    public final class LocalBinder extends Binder { ChatService service() { return ChatService.this; } }
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ExecutorService io=Executors.newSingleThreadExecutor();
    private final ExecutorService discovery=Executors.newSingleThreadExecutor();
    private final ExecutorService archive=Executors.newSingleThreadExecutor();
    private final AtomicBoolean refreshQueued=new AtomicBoolean(),refreshAgain=new AtomicBoolean(),publishQueued=new AtomicBoolean();
    private final ExecutorService transferWorker=Executors.newSingleThreadExecutor();
    private final AtomicBoolean busy=new AtomicBoolean();
    // 接收、转发读取与本机删除只互斥同一文件；删除不占用整个传输通道。
    private final Set<File> receivedFileOperations=ConcurrentHashMap.newKeySet();
    private static final class QueuedTransfer {
        final Runnable start,cleanup;
        QueuedTransfer(Runnable start,Runnable cleanup){this.start=start;this.cleanup=cleanup;}
    }
    private final ArrayDeque<QueuedTransfer> queuedTransfers=new ArrayDeque<>();
    int queuedTransfers(){return queuedTransfers.size();}
    void cancelQueuedTransfers(){while(!queuedTransfers.isEmpty())queuedTransfers.removeFirst().cleanup.run();changed();}
    private final Set<Observer> observers=new HashSet<>();
    private final LocalBinder binder=new LocalBinder();
    private ChatStore store;
    private ReceivedStorage storage;
    private final LinkedHashMap<String,ReceiveRequest> waiting=new LinkedHashMap<>();
    private volatile boolean disconnecting;
    private static final class ReceiveRequest {
        final LanNode.Peer peer; final long packet; final Protocol.Attachment file;
        volatile boolean offerCanceled, rejected;
        volatile LanNode.Transfer task;
        ReceiveRequest(LanNode.Peer peer,long packet,Protocol.Attachment file) { this.peer=peer; this.packet=packet; this.file=file; }
    }
    private volatile LanNode node;
    private volatile LanNode startingNode;
    private volatile LanNetwork lanNetwork;
    private final List<ToolsGateway> gateways=new ArrayList<>();
    static Intent toolIntent(Context context,Uri url,String peerId,String mode,boolean phone) {
        if("share".equals(mode)) {
            return new Intent(Intent.ACTION_VIEW,url).addCategory(Intent.CATEGORY_BROWSABLE).putExtra("peer_id",peerId).putExtra("tool_mode",mode);
        }
        return new Intent(context,RemoteActivity.class).setData(url).putExtra("phone",phone).putExtra("peer_id",peerId).putExtra("tool_mode",mode);
    }
    void openTool(LanNode.Peer peer,int port,String mode,boolean phone,Consumer<Intent> ready) {
        LanNode current=node;LanNetwork route=lanNetwork;long epoch=connectionEpoch;int transport=current==null?0:current.transportEpoch();
        queue(()->{try {
            if(current==null||route==null)throw new IOException("请先连接局域网");current.verifyPeer(peer);
            ToolsGateway gateway=new ToolsGateway(current.security(),peer,port,route,current);
            main.post(()->{if(!currentConnection(epoch)||node!=current||transport!=current.transportEpoch()){gateway.close();return;}gateways.add(gateway);
                ready.accept(toolIntent(this,Uri.parse(gateway.url()+"#"+mode),peer.id(),mode,phone));});
        }catch(Exception error){fail(error);}});
    }
    List<String> localAddresses()throws IOException {
        LanNetwork route=lanNetwork;
        return (route!=null?route:networkFactory.apply(getSystemService(ConnectivityManager.class))).addresses();
    }
    void forgetSecurity(LanNode.Peer peer){queue(()->{try{requireNode().forgetSecurity(peer);new ToolsPairings(this).forgetFingerprint(peer.fingerprint);main.post(()->{closeGateways();closeSharing();PhoneRemoteService.stopActive();});showError("已撤销设备信任，下次连接需重新核对");}catch(Exception e){fail(e);}});}
    private void closeGateways(){for(ToolsGateway gateway:gateways)gateway.close();gateways.clear();}
    volatile SecureIdentity.Confirmation securityConfirmation;
    private SharedFiles sharedFiles;
    private GuestTransfer guestTransfer;
    private long guestEpoch;
    GuestTransfer guestTransfer(){return guestTransfer;}
    void stopGuest(){guestEpoch++;if(guestTransfer!=null){guestTransfer.close();if(!guestTransfer.cleanupError.isEmpty())showError(guestTransfer.cleanupError);guestTransfer=null;}}
    void startGuest(List<Uri> files,boolean upload,Consumer<Boolean> done) {
        if(!online()){showError("请先连接局域网");done.accept(false);return;}
        stopGuest();long epoch=guestEpoch;LanNetwork route=lanNetwork;
        queue(()->{try {
            List<LanNode.Source> sources=new ArrayList<>();Map<String,String> relatives=new HashMap<>();Set<String> selected=new HashSet<>();
            for(Uri uri:files){
                if(android.provider.DocumentsContract.isTreeUri(uri)&&!android.provider.DocumentsContract.isDocumentUri(this,uri)){
                    for(SendFiles.Entry entry:SendFiles.folder(this,uri))if(selected.add(entry.uri.toString())){sources.add(new DocumentSource(this,entry.uri,uri));relatives.put(entry.uri.toString(),entry.relative);if(sources.size()>1000)throw new IOException("单次最多 1000 个文件");}
                }else if(selected.add(uri.toString()))sources.add(new DocumentSource(this,uri));
            }
            GuestTransfer created=new GuestTransfer(this,sources,relatives,upload,false,route);created.received=()->main.post(this::changed);created.warning=this::showError;
            main.post(()->{if(stopped||epoch!=guestEpoch||!online()){created.close();done.accept(false);return;}guestTransfer=created;done.accept(true);});
        }catch(Exception e){fail(e);main.post(()->done.accept(false));}});
    }
    interface SharedFactory { SharedFiles create(List<Uri> tree,int port)throws IOException; }
    SharedFactory sharedFactory=(tree,port)->new SharedFiles(this,tree,port,false,lanNetwork);
    private long sharingEpoch;
    private int connectedPort;
    private volatile boolean stopped, connecting;
    private final Object connectionLock=new Object();
    private volatile long connectionEpoch;
    // 工厂不接受外部 Intent 参数；回归测试可替换为真实回环节点，避免接触用户局域网。
    java.util.function.BiFunction<SharedPreferences,LanNode.Listener,LanNode> connectionFactory=(prefs,listener)->new LanNode(prefs.getInt("port",2425),false,prefs.getString("login",""),"Android",prefs.getString("name",Build.MODEL),prefs.getString("group","我的局域网"),listener);
    java.util.function.Function<ConnectivityManager,LanNetwork> networkFactory=LanNetwork::new;
    private volatile String activePeer="";
    private volatile LanNode.Transfer transfer;
    private volatile ReceiveRequest activeReceive;
    private volatile Runnable transferCleanup;
    private final java.util.concurrent.atomic.AtomicLong transferEpoch=new java.util.concurrent.atomic.AtomicLong();
    private Diagnostics diagnostics;
    private WifiManager.MulticastLock multicast;
    private WifiManager.WifiLock wifiLock;
    private PowerManager.WakeLock cpuLock;
    private ConnectivityManager connectivity;
    private ConnectivityManager.NetworkCallback callback;
    private BroadcastReceiver tetherReceiver;
    private List<ChatStore.Conversation> conversations=new ArrayList<>();
    private String status="未连接", transferStatus="";
    private volatile String transferOutcome="";
    static final class FileProgress {
        final int percent; final String state; final LanNode.Transfer task;
        final double bytesPerSecond;
        final boolean live;
        FileProgress(int percent,String state,LanNode.Transfer task) {this(percent,state,0,task,false);}
        FileProgress(int percent,String state,double bytesPerSecond,LanNode.Transfer task) {this(percent,state,bytesPerSecond,task,true);}
        private FileProgress(int percent,String state,double bytesPerSecond,LanNode.Transfer task,boolean live) {this.percent=percent;this.state=state;this.bytesPerSecond=bytesPerSecond;this.task=task;this.live=live;}
    }
    private final LinkedHashMap<String,FileProgress> fileProgress=new LinkedHashMap<>();
    private boolean transferNotificationQueued;
    private final Runnable notifyTransfers=()->{transferNotificationQueued=false;for(Observer observer:new ArrayList<>(observers))observer.transfersChanged();};
    static String fileKey(String peer,long packet,long file,boolean outgoing) {return peer+"/"+packet+"/"+file+"/"+outgoing;}
    // 曾保存后又删除本机文件、正在重新接收的附件；持久化后重启、暂停续传期间邀请失效也能恢复删除态。
    private final Set<String> receivedBefore=java.util.concurrent.ConcurrentHashMap.newKeySet();
    private void rememberReceivedBefore(String key,boolean value) {
        if(!(value?receivedBefore.add(key):receivedBefore.remove(key)))return;
        SharedPreferences.Editor editor=getSharedPreferences("received_before",MODE_PRIVATE).edit();
        if(value)editor.putBoolean(key,true);else editor.remove(key);
        editor.apply();
    }
    static boolean wasStored(String state) {return "已保存".equals(state)||"本机文件已删除".equals(state)||state!=null&&state.startsWith("已接收");}
    FileProgress progress(String peer,long packet,long file,boolean outgoing) {return fileProgress.get(fileKey(peer,packet,file,outgoing));}
    private void fileProgress(String peer,long packet,long file,boolean outgoing,int percent,String state,LanNode.Transfer task) {
        fileProgress(peer,packet,file,outgoing,percent,state,0,task);
    }
    private void fileProgress(String peer,long packet,long file,boolean outgoing,int percent,String state,double bytesPerSecond,LanNode.Transfer task) {
        String key=fileKey(peer,packet,file,outgoing);
        FileProgress previous=fileProgress.get(key);
        if(!outgoing&&previous!=null&&(previous.state.equals("对方已取消发送")||previous.state.equals("已拒绝"))&&!state.equals(previous.state)&&!state.equals("已保存")&&!state.startsWith("已接收")&&!state.equals("本机文件已删除"))return;
        fileProgress.remove(key);fileProgress.put(key,new FileProgress(Math.max(0,Math.min(100,percent)),state,bytesPerSecond,task));
        if(previous==null||!previous.state.equals(state)||previous.percent/10!=percent/10)queue(()->store.transfer(key,peer,packet,percent,state));
        while(fileProgress.size()>2000)fileProgress.remove(fileProgress.keySet().iterator().next());
        // 限频而非反复延后：连续的大文件进度也必须在传输途中画出来。
        if(!transferNotificationQueued){transferNotificationQueued=true;main.postDelayed(notifyTransfers,60);}
    }
    void cancelFile(String peer,long packet,long file) {
        FileProgress p=progress(peer,packet,file,false);
        if(p!=null&&(wasStored(p.state)||p.state.equals("已拒绝")||p.state.equals("对方已取消发送")))return;
        waiting.values().removeIf(r->r.peer.id().equals(peer)&&r.packet==packet&&r.file.id==file);
        ReceiveRequest active=activeReceive;
        if(active!=null&&active.peer.id().equals(peer)&&active.packet==packet&&active.file.id==file) {
            LanNode.Transfer task=active.task==null?transfer:active.task;
            if(task!=null)task.close();
            return;
        }
        if(p!=null&&p.task!=null&&p.task==transfer){p.task.close();return;}
        queue(()->{
            try {
                LanNode.Message message=store.findMessage(peer,packet,false);
                if(message==null)return;
                Protocol.Attachment attachment=message.files.stream().filter(item->item.id==file).findFirst().orElse(null);
                if(attachment==null)return;
                File target=receivedFile(peer,packet,attachment);
                if(storage.exists(target)||storage.saved(target)!=null)return;
                LanNode.Peer source=store.findPeer(peer);
                if(source==null)return;
                Files.deleteIfExists(LanNode.partialFile(source,packet,attachment,target).toPath());
                main.post(()->{if(!stopped){fileProgress(peer,packet,file,false,0,"已取消 · 点击重试",null);changed();}});
            } catch(IOException error){fail(error);}
        });
    }
    void pauseFile(String peer,long packet,long file) {
        FileProgress p=progress(peer,packet,file,false);
        if(p!=null&&(p.state.equals("已保存")||p.state.equals("已拒绝")||p.state.equals("对方已取消发送")||p.state.equals("本机文件已删除")))return;
        waiting.values().removeIf(r->r.peer.id().equals(peer)&&r.packet==packet&&r.file.id==file);
        ReceiveRequest active=activeReceive;
        if(active!=null&&active.peer.id().equals(peer)&&active.packet==packet&&active.file.id==file) {
            LanNode.Transfer task=active.task==null?transfer:active.task;
            if(task!=null)task.pause();
        } else if(p!=null&&p.task!=null&&p.task==transfer) p.task.pause();
        else fileProgress(peer,packet,file,false,p==null?0:p.percent,"已暂停",null);
    }
    void pauseAllReceives() {
        ReceiveRequest active=activeReceive;
        for(ReceiveRequest request:new ArrayList<>(waiting.values()))pauseFile(request.peer.id(),request.packet,request.file.id);
        if(active!=null)pauseFile(active.peer.id(),active.packet,active.file.id);
        changed();notifyTransfers.run();
    }
    void pauseReceives(List<ChatStore.FileTask> items) {
        Set<String> selected=new HashSet<>();
        for(ChatStore.FileTask item:items)if(item.guestFile==null&&!item.message.outgoing)selected.add(fileKey(item.peer,item.message.number,item.file.id,false));
        ReceiveRequest active=activeReceive;
        // 先移走选中的排队项；暂停活跃接收后，队列只能继续启动列表外的任务。
        for(ReceiveRequest request:new ArrayList<>(waiting.values())) {
            if(selected.contains(fileKey(request.peer.id(),request.packet,request.file.id,false)))pauseFile(request.peer.id(),request.packet,request.file.id);
        }
        if(active!=null&&selected.contains(fileKey(active.peer.id(),active.packet,active.file.id,false)))pauseFile(active.peer.id(),active.packet,active.file.id);
        changed();notifyTransfers.run();
    }
    void fileTasks(Consumer<List<ChatStore.FileTask>> result) {
        // 主线程保存进度对象身份；后台慢查询回来时以期间新发布的保存/删除结果为准。
        Map<String,FileProgress> progressBefore=new HashMap<>(fileProgress);
        queue(()->{List<ChatStore.FileTask> rows=store.fileTasks();
            for(ChatStore.FileTask task:rows)if(!task.message.outgoing) {
                File local=receivedFile(task.peer,task.message.number,task.file);
                task.stored=storage.exists(local);task.unavailable=!task.stored&&storage.saved(local)!=null;
            }
            SharedPreferences guests=getSharedPreferences("guest_received",MODE_PRIVATE);
            for(Map.Entry<String,?> entry:guests.getAll().entrySet()) {
                if(!entry.getKey().matches("[a-f0-9]{64}")||!(entry.getValue() instanceof String))continue;
                File local=new File(new File(getFilesDir(),"received"),entry.getKey());
                long size=local.length(),time=local.lastModified();String relative=null,metadata=guests.getString("meta:"+entry.getKey(),"");
                if(!metadata.isEmpty())try {org.json.JSONObject meta=new org.json.JSONObject(metadata);size=Math.max(0,meta.optLong("size",size));time=Math.max(0,meta.optLong("time",time));String path=meta.optString("relativePath","");if(Protocol.safeRelativePath(path)&&path.endsWith("/"+entry.getValue()))relative=path;}
                catch(org.json.JSONException e){showError("访客接收记录损坏，已使用本机文件信息："+entry.getValue());}
                Protocol.Attachment file=new Protocol.Attachment(0,(String)entry.getValue(),size,0);
                file.relativePath=relative;
                LanNode.Message message=new LanNode.Message();message.number=Long.parseLong(entry.getKey().substring(0,15),16);message.outgoing=false;message.time=time;message.files=Collections.singletonList(file);message.text="[文件] "+file.name;message.state="已保存";
                ChatStore.FileTask task=new ChatStore.FileTask("browser-guests",message,file);task.guestFile=local;task.stored=storage.exists(local);task.unavailable=!task.stored&&storage.saved(local)!=null;rows.add(task);
            }
            rows.sort(Comparator.comparingLong((ChatStore.FileTask task)->task.message.time).reversed().thenComparing(Comparator.comparingLong((ChatStore.FileTask task)->task.message.number).reversed()).thenComparing(task->task.peer));
            main.post(()->{if(stopped)return;
                for(ChatStore.FileTask task:rows)if(task.guestFile==null&&!task.message.outgoing) {
                    FileProgress previous=progress(task.peer,task.message.number,task.file.id,false);
                    String key=fileKey(task.peer,task.message.number,task.file.id,false);
                    if(previous!=progressBefore.get(key)&&previous!=null&&wasStored(previous.state)) {
                        task.stored=!previous.state.equals("本机文件已删除");
                        task.unavailable=false;
                        task.savedProgress=new ChatStore.TransferState(key,previous.percent,previous.state);
                        continue;
                    }
                    if(task.unavailable)continue;
                    String previousState=previous!=null?previous.state:task.savedProgress!=null?task.savedProgress.state:task.message.state;
                    if(task.stored) {
                        if("本机文件已删除".equals(previousState)) {
                            task.savedProgress=new ChatStore.TransferState(key,100,"已保存");
                            fileProgress(task.peer,task.message.number,task.file.id,false,100,"已保存",null);
                        }
                        continue;
                    }
                    if(wasStored(previousState)&&!"本机文件已删除".equals(previousState)) {
                        task.savedProgress=new ChatStore.TransferState(key,0,"本机文件已删除");
                        fileProgress(task.peer,task.message.number,task.file.id,false,0,"本机文件已删除",null);
                    }
                }
                result.accept(rows);
            });});
    }
    boolean offering(String peer,long packet,long file) {LanNode current=node;return current!=null&&current.offering(peer,packet,file);}
    boolean canCancelOutgoing(String peer,long packet) {LanNode current=node;return current!=null&&current.canCancelOutgoing(peer,packet);}
    boolean hasUndeliveredOutgoing(String peer,LanNode.Message message) {
        if(message==null)return false;
        LanNode current=node;
        return current==null?message.outgoing&&!message.files.isEmpty()&&LanNode.isUnconfirmedState(message.state):current.hasUndeliveredOutgoing(peer,message.number,message.state);
    }
    void cancelOutgoing(String peer,LanNode.Message message,Consumer<Boolean> done) {
        queue(()->{
            try {
                LanNode current=node;
                if(current==null){showError("请先连接局域网");main.post(()->{if(!stopped)done.accept(false);});return;}
                boolean canceled=current.cancelOutgoing(peer,message.number,message.state,true);
                if(canceled) {
                    LanNode.Message stored=store.findMessage(peer,message.number,true);
                    if(stored!=null&&!("已送达".equals(stored.state)||"已取消发送".equals(stored.state))){
                        stored.state="已取消发送";
                        LanNode.Peer target=store.conversations().stream().filter(item->item.peer.id().equals(peer)).map(item->item.peer).findFirst().orElse(null);
                        if(target!=null)store.message(target,stored,true,peer.equals(activePeer));
                    }
                    publish();main.post(()->{if(!stopped)done.accept(true);});
                } else {showError("此消息已发送完成或已结束，无需取消");main.post(()->{if(!stopped)done.accept(false);});}
            } catch(Exception error){fail(error);main.post(()->{if(!stopped)done.accept(false);});}
        });
    }
    private boolean notificationQueued;
    private boolean networkReady;
    private final Runnable notifyChanges=()->{notificationQueued=false;for (Observer observer:new ArrayList<>(observers)) observer.changed();};
    @Override public void onCreate() {
        super.onCreate(); store=new ChatStore(this); storage=new ReceivedStorage(this);diagnostics=new Diagnostics(this);receivedBefore.addAll(getSharedPreferences("received_before",MODE_PRIVATE).getAll().keySet());
        instance=this;
        NotificationManager manager=getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("connection","局域网连接",NotificationManager.IMPORTANCE_LOW));
        manager.createNotificationChannel(new NotificationChannel("messages","新消息",NotificationManager.IMPORTANCE_DEFAULT));
        io.execute(()->{ try { store.recoverPending();List<ChatStore.TransferState> saved=store.transfers(null,2000);main.post(()->{for(ChatStore.TransferState item:saved)fileProgress.putIfAbsent(item.key,new FileProgress(item.percent,item.state,null));});cleanStaging(); publish(); } catch (Exception e) { fail(e); } });
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
        boolean keepBackground=getSharedPreferences("settings",MODE_PRIVATE).getBoolean("keep_background",true);
        if(node!=null || connecting) return keepBackground?START_STICKY:START_NOT_STICKY;
        if(intent==null&&(!keepBackground||!getSharedPreferences("settings",MODE_PRIVATE).getBoolean("connection_enabled",false))){stopSelf(startId);return START_NOT_STICKY;}
        if(stopped || (intent!=null && "restore".equals(intent.getAction()) && !shouldRestoreConnection(this))) { stopSelf(startId); return START_NOT_STICKY; }
        // 系统回收后可恢复用户仍开启的连接；手动断开后不能被粘性服务重新拉起。
        try { startForeground(1,notification("connection","局域网连接","正在连接…",true)); connect(); }
        catch (RuntimeException e) { fail(e); stopSelf();return START_NOT_STICKY; }
        return keepBackground?START_STICKY:START_NOT_STICKY;
    }
    private Notification notification(String channel,String title,String text,boolean ongoing) {
        PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        Notification.Builder b=new Notification.Builder(this,channel).setSmallIcon(R.drawable.ic_notify).setContentTitle(title).setContentText(text).setContentIntent(open).setOngoing(ongoing).setAutoCancel(!ongoing).setVisibility(Notification.VISIBILITY_PRIVATE);
        if (ongoing) b.addAction(new Notification.Action.Builder(null,"断开",PendingIntent.getService(this,1,new Intent(this,ChatService.class).setAction("stop"),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT)).build());
        return b.build();
    }
    private void connect() {
        synchronized(connectionLock) {
            if(stopped || node!=null || connecting) return;
            diagnostics.record(Diagnostics.Event.Connecting);networkReady=false;
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
                String deviceId=Protocol.normalizeDeviceId(prefs.getString("device_id",null));
                if(deviceId==null) { deviceId=UUID.randomUUID().toString().replace("-","");if(!prefs.edit().putString("device_id",deviceId).commit())throw new IOException("无法保存本机身份"); }
                next=connectionFactory.apply(prefs,new LanNode.Listener() {
                    @Override public void peersChanged() { if(currentConnection(epoch)) requestPublish(); }
                    @Override public void error(String text) { if(currentConnection(epoch)) showError(text); }
                    @Override public void networkStateChanged(boolean available,String reason) {
                        main.post(()->{if(currentConnection(epoch))updateNetworkStatus(available,reason);});
                    }
                    @Override public void networkReset(){main.post(()->{if(currentConnection(epoch)){cancelQueuedTransfers();stopGuest();closeGateways();closeSharing();PhoneRemoteService.stopActive();}});}
                    @Override public void message(LanNode.Peer peer,LanNode.Message message,boolean update) { if(currentConnection(epoch)||update) incoming(peer,message,update,epoch); }
                    @Override public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state,double bytesPerSecond) {
                        main.post(()->{if(currentConnection(epoch))ChatService.this.fileProgress(peer.id(),packet,file,true,percent,state,bytesPerSecond,null);});
                    }
                    @Override public void offerCanceled(LanNode.Peer peer,long packet) { if(currentConnection(epoch))ChatService.this.offerCanceled(peer,packet); }
                });
                next.setDeviceId(deviceId);
                applyProfile(next,prefs);
                next.setSourceResolver(key->new DocumentSource(this,Uri.parse(key)));
                next.configureSecurity(new File(getFilesDir(),"security"),AppIdentity.get(this),(name,code)->securityConfirmation!=null&&securityConfirmation.confirm(name,code));
                Map<String,String> savedStates=new HashMap<>();for(ChatStore.TransferState state:store.transfers())savedStates.put(state.key,state.state);next.restoreOutgoingStates(savedStates);
                next.rememberPeers(store.conversations().stream().map(c->c.peer).collect(java.util.stream.Collectors.toList()));
                next.rememberEndpoints(store.conversations().stream().map(c->c.peer.endpoint).collect(java.util.stream.Collectors.toList()));
                final LanNetwork route=networkFactory.apply(getSystemService(ConnectivityManager.class));
                next.setSocketBinding(route);
                // 先登记再复核请求，断开/销毁才能释放 start() 尚未返回的监听。
                startingNode=next;
                if(!currentConnection(epoch)) { next.close(); return; }
                next.start();
                // 节点只在成功启动且仍属于本次请求时发布；销毁/断开不能漏掉尚未发布的节点。
                synchronized(connectionLock) {
                    if(currentConnection(epoch)){node=next;lanNetwork=route;}
                    if(startingNode==next)startingNode=null;
                }
                if(node!=next) { next.close(); return; }
                final LanNode connected=next;
                main.post(()->{
                    if (!currentConnection(epoch) || node!=connected) return;
                    prefs.edit().putBoolean("connection_enabled",true).apply();
                    try {
                        WifiManager wifi=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
                        if (wifi!=null) { multicast=wifi.createMulticastLock("FeiqLight"); multicast.setReferenceCounted(false); multicast.acquire(); }
                        backgroundSettingsChanged();
                        connectivity=getSystemService(ConnectivityManager.class);
                        callback=new ConnectivityManager.NetworkCallback() {
                            private void update(Network network,boolean lost) {
                                synchronized(connectionLock) {
                                    if(!currentConnection(epoch)||node!=connected)return;
                                    if(lost)route.lost(network);else route.track(network);
                                    connected.networkChanged();
                                }
                            }
                            @Override public void onAvailable(Network network) { diagnostics.record(Diagnostics.Event.NetworkChanged);update(network,false); }
                            @Override public void onLost(Network network) { update(network,true); }
                            @Override public void onLinkPropertiesChanged(Network network,LinkProperties properties) { update(network,false); }
                            @Override public void onCapabilitiesChanged(Network network,NetworkCapabilities capabilities) { update(network,false); }
                            @Override public void onBlockedStatusChanged(Network network,boolean blocked) { update(network,false); }
                        };
                        connectivity.registerNetworkCallback(new NetworkRequest.Builder()
                            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI).addTransportType(NetworkCapabilities.TRANSPORT_ETHERNET)
                            .addTransportType(NetworkCapabilities.TRANSPORT_VPN).removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),callback);
                        IntentFilter tetherFilter=new IntentFilter();
                        tetherFilter.addAction("android.net.wifi.WIFI_AP_STATE_CHANGED");
                        tetherFilter.addAction("android.net.conn.TETHER_STATE_CHANGED");
                        tetherReceiver=new BroadcastReceiver() {
                            @Override public void onReceive(Context context,Intent intent) {
                                if(currentConnection(epoch)&&node==connected)connected.networkChanged();
                            }
                        };
                        if(Build.VERSION.SDK_INT>=33) registerReceiver(tetherReceiver,tetherFilter,Context.RECEIVER_EXPORTED);
                        else registerReceiver(tetherReceiver,tetherFilter);
                    } catch (RuntimeException e) { showError("自动发现可能受限，可尝试手动 IP："+e.getMessage()); }
                    // 建连期间设置仍可保存；发布节点时补上最新资料，端口则以实际监听值为准。
                    applyProfile(connected,prefs);
                    connecting=false;
                    connectedPort=connected.port();
                    updateNetworkStatus(connected.networkAvailable(),connected.networkStatus());
                    connected.networkChanged();
                });
                try { publish(); } catch(Exception e) { fail(e); }
            } catch (Exception e) {
                if(next!=null) next.close();
                synchronized(connectionLock) { if(node==next) node=null; }
                main.post(()->{ if(!currentConnection(epoch)) return; connecting=false; status="连接失败"; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(); changed(); fail(e); });
            } finally {
                synchronized(connectionLock) { if(startingNode==next)startingNode=null; }
            }
    }
    void incoming(LanNode.Peer peer,LanNode.Message message,boolean update) {
        incoming(peer,message,update,connectionEpoch);
    }
    private void incoming(LanNode.Peer peer,LanNode.Message message,boolean update,long epoch) {
        queue(()->{
            boolean inserted=store.message(peer,message,update,peer.id().equals(activePeer)); publish();
            if(inserted && !update && message.outgoing)main.post(()->{
                if(currentConnection(epoch))for(Observer observer:new ArrayList<>(observers))observer.sent(peer.id());
            });
            if(inserted&&!update&&!message.outgoing) main.post(()->{
                if(!currentConnection(epoch)||disconnecting) return;
                for(Protocol.Attachment file:message.files) {
                    String key=receivedFile(peer,message.number,file).getName();
                    if(waiting.size()>=SendFiles.limit(getSharedPreferences("settings",MODE_PRIVATE),"pending_file_limit",10000)) { showError("待接收文件已达设置上限，可调大上限后从传输任务继续"); break; }
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
    private static void applyProfile(LanNode target,SharedPreferences prefs) {
        String avatar=Protocol.normalizeAvatar(prefs.getString("avatar","default"));
        target.setAvatar(avatar==null?"default":avatar);
        target.nickname=prefs.getString("name",Build.MODEL);
        target.group=prefs.getString("group","我的局域网");
        target.allowLegacyProtocol=allowLegacy(prefs);
        target.setPendingFileLimit(SendFiles.limit(prefs,"pending_file_limit",10000));
    }
    static boolean allowLegacy(SharedPreferences prefs) {
        if(!prefs.contains("legacy_protocol_policy")) {
            // 0.7.0 没有记录该策略版本，升级时恢复旧版兼容；之后手动关闭会保留。
            prefs.edit().putBoolean("allow_legacy_protocol",true).putInt("legacy_protocol_policy",LEGACY_POLICY_VERSION).apply();
        }
        return prefs.getBoolean("allow_legacy_protocol",true);
    }
    void profileSettingsChanged() {
        LanNode current=node;
        if(current!=null){
            applyProfile(current,getSharedPreferences("settings",MODE_PRIVATE));
            refresh();
        }
    }
    void avatarSettingsChanged() { profileSettingsChanged(); }
    void deleteMessages(String peer,List<LanNode.Message> messages,Consumer<Boolean> done) {
        LanNode current=node;
        boolean pending=messages.stream().anyMatch(message->waiting.values().stream().anyMatch(r->!message.outgoing&&r.peer.id().equals(peer)&&r.packet==message.number)||message.outgoing&&current!=null&&current.hasActiveOutgoingTransfer(peer,message.number)||message.files.stream().anyMatch(file->{
            FileProgress progress=progress(peer,message.number,file.id,message.outgoing);
            return progress!=null&&progress.live&&progress.state.contains("中")&&!progress.state.contains("中断")&&progress.percent<100
                &&(message.outgoing?offering(peer,message.number,file.id):progress.task!=null&&progress.task==transfer&&busy());
        }));
        if(pending){showError("请先完成、拒绝或取消选中消息的文件任务，再删除记录");done.accept(false);return;}
        List<LanNode.Message> selected=new ArrayList<>(messages);
        queue(()->{
            try {
                LanNode active=node;
                if(selected.stream().anyMatch(message->message.outgoing&&active!=null&&active.hasActiveOutgoingTransfer(peer,message.number))) {showError("请先完成、拒绝或取消选中消息的文件任务，再删除记录");main.post(()->{if(!stopped)done.accept(false);});return;}
                for(LanNode.Message message:selected)if(message.outgoing&&active!=null)active.cancelUndeliveredOutgoing(peer,message.number,message.state,false);
                if(active==null){Set<Long> unconfirmed=new HashSet<>(store.unconfirmedOutgoingPackets(peer));List<Long> packets=selected.stream().filter(message->message.outgoing&&unconfirmed.contains(message.number)).map(message->message.number).collect(java.util.stream.Collectors.toList());LanNode.cancelStoredOffers(new File(getFilesDir(),"security"),peer,packets);}
                LanNode.Peer sender=store.findPeer(peer);
                if(sender!=null)for(LanNode.Message selectedMessage:selected)if(!selectedMessage.outgoing) {
                    LanNode.Message stored=store.findMessage(peer,selectedMessage.number,false);
                    if(stored!=null)deleteIncomingPartials(sender,stored);
                }
                store.deleteMessages(peer,selected);main.post(()->{fileProgress.entrySet().removeIf(entry->entry.getKey().startsWith(peer+"/")&&selected.stream().anyMatch(message->entry.getKey().startsWith(peer+"/"+message.number+"/")&&entry.getKey().endsWith("/"+message.outgoing)));notifyTransfers.run();});publish();main.post(()->{if(!stopped)done.accept(true);});
            }
            catch(Exception error){fail(error);main.post(()->{if(!stopped)done.accept(false);});}
        });
    }
    private void drainReceives() {
        if(stopped||disconnecting||!online()||busy()||waiting.isEmpty()) return;
        ReceiveRequest next=waiting.values().stream().filter(r->{if(receivedFileOperations.contains(receivedFile(r.peer,r.packet,r.file)))return false;FileProgress p=progress(r.peer.id(),r.packet,r.file.id,false);return storage.automatic()||p!=null&&p.state.equals("排队中");}).findFirst().orElse(null);
        if(next!=null)receive(next.peer,next.packet,next.file);
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
        LanNode current=node;
        if(clear && (busy() || current!=null&&current.hasActiveOutgoingTransfer(id) || waiting.values().stream().anyMatch(r->r.peer.id().equals(id)))) {showError("请先完成、拒绝或取消文件任务，再清空记录");done.accept(false);return;}
        queue(()->{try {
            LanNode active=node;
            if(clear&&active!=null&&active.hasActiveOutgoingTransfer(id)){showError("请先完成、拒绝或取消文件任务，再清空记录");main.post(()->done.accept(false));return;}
            if(clear) {
                List<Long> packets=store.unconfirmedOutgoingPackets(id);
                if(active!=null)active.cancelUndeliveredOutgoingForPeer(id,packets);else LanNode.cancelStoredOffers(new File(getFilesDir(),"security"),id,packets);
                LanNode.Peer peer=store.findPeer(id);
                if(peer!=null)for(LanNode.Message message:store.incomingAttachments(id))deleteIncomingPartials(peer,message);
            }
            store.remove(id,clear,hide);publish();main.post(()->{if(clear){fileProgress.entrySet().removeIf(entry->entry.getKey().startsWith(id+"/"));notifyTransfers.run();}done.accept(true);});
        }catch(Exception e){fail(e);main.post(()->done.accept(false));}});
    }
    boolean online() { LanNode current=node;return current!=null && !connecting && current.networkAvailable(); }
    private void updateNetworkStatus(boolean available,String reason) {
        if(node==null || connecting || stopped)return;
        boolean normal=available && reason.isEmpty();
        String next=normal?"已连接 · UDP / TCP "+connectedPort:reason;
        if(next.equals(status))return;
        if(available&&!networkReady)diagnostics.record(Diagnostics.Event.Connected);
        else if(!available)diagnostics.record(Diagnostics.Event.NetworkWaiting);
        networkReady=available;
        status=next;changed();
        getSystemService(NotificationManager.class).notify(1,notification("connection","飞Q正在运行",normal?"可接收局域网消息 · 点击返回":status,true));
        if(available && online()){if(sharedFiles==null)restoreSharing();drainReceives();}
    }
    SharedFiles sharedFiles() { return sharedFiles; }
    private void closeSharing() { sharingEpoch++; if(sharedFiles!=null){sharedFiles.close();sharedFiles=null;} }
    void stopSharing() { closeSharing();try{new SharedFolderSettings(this).stop();}catch(IOException e){showError(e.getMessage());} }
    void restoreSharing() {
        SharedFolderSettings saved=new SharedFolderSettings(this);
        try{if(saved.enabled()&&!saved.trees().isEmpty())shareFolders(saved.trees(),ignored->{});}catch(IOException e){showError(e.getMessage());}
    }
    void shareFolder(Uri tree,Consumer<Boolean> done) {
        try {
            List<Uri> trees=new SharedFolderSettings(this).trees();if(!trees.contains(tree))trees.add(tree);
            shareFolders(trees,done);
        }catch(IOException e){showError(e.getMessage());done.accept(false);}
    }
    void removeSharedFolder(Uri tree,Consumer<Boolean> done) {
        try {
            SharedFolderSettings settings=new SharedFolderSettings(this);List<Uri> trees=settings.trees();trees.remove(tree);
            if(sharedFiles!=null&&!trees.isEmpty()){shareFolders(trees,success->{if(!success)closeSharing();done.accept(success);});return;}
            closeSharing();settings.save(trees,false);done.accept(true);
        }catch(IOException e){showError(e.getMessage());done.accept(false);}
    }
    void shareFolders(List<Uri> folders,Consumer<Boolean> done) {
        if(!online()){showError("请先连接局域网");done.accept(false);return;}
        List<Uri> trees=new ArrayList<>(new LinkedHashSet<>(folders));
        if(trees.isEmpty()||trees.size()>20){showError("请选择 1–20 个共享文件夹");done.accept(false);return;}
        SharedFiles active=sharedFiles;long version=++sharingEpoch,connection=connectionEpoch;int port=ToolsHttp.portFor(connectedPort);
        io.execute(()->{
            try {
                SharedFolderSettings settings=new SharedFolderSettings(this);for(Uri tree:trees)settings.requireGrant(tree);
                if(active!=null){
                    active.updateFolders(trees);
                    main.post(()->{
                        if(stopped||version!=sharingEpoch||connection!=connectionEpoch||sharedFiles!=active){done.accept(false);return;}
                        try{settings.save(trees,true);done.accept(true);}
                        catch(IOException e){closeSharing();showError(e.getMessage());done.accept(false);}
                    });
                    return;
                }
                SharedFiles next=sharedFactory.create(trees,port);
                main.post(()->{
                    if(stopped||version!=sharingEpoch||connection!=connectionEpoch){next.close();done.accept(false);return;}
                    try{settings.save(trees,true);sharedFiles=next;done.accept(true);}
                    catch(IOException e){next.close();showError(e.getMessage());done.accept(false);}
                });
            }catch(Exception e){main.post(()->{if(!stopped&&version==sharingEpoch){showError("共享未开启："+e.getMessage());done.accept(false);}});}
        });
    }
    boolean busy() { return busy.get(); }
    String status() { return status; }
    String contactStatus() {
        LanNode current=node;
        if(current==null || !online())return status;
        long onlineCount=conversations.stream().filter(item->!item.hidden && item.peer.online).count();
        String warning=current.networkStatus();
        return onlineCount==0 && !warning.isEmpty()?warning:onlineCount+" 人在线";
    }
    String connectionDetails() {
        LanNode current=node;
        String details=status;
        if(current!=null) {
            String reason=current.networkStatus();
            String portState=current.networkAvailable()?"UDP / TCP "+current.port()+" 已监听":"端口 "+current.port();
            details=current.networkDescription()+"\n"+portState+(reason.isEmpty()?"":" · "+reason);
        }
        return details+"\n未发现设备：确认对方已连接、端口一致，检查防火墙和热点的设备隔离。VPN 需允许局域网，可在“添加联系人”中输入对方 IPv4。";
    }
    // 工作线程结束到主线程终态回调之间，不能继续显示“尚未发送”的旧准备提示。
    String transferStatus() { String current=busy()?transferStatus:transferOutcome;return queuedTransfers.isEmpty()?current:current+" · "+queuedTransfers.size()+" 项排队中"; }
    void active(String peer) {
        String target=peer==null?"":peer; activePeer=target;
        if(!target.isEmpty()) queue(()->{ store.read(target); publish(); });
    }
    static final class ChatPage {
        final List<LanNode.Message> rows;
        final Map<String,Uri> files=new HashMap<>();
        final Set<String> unavailable=new HashSet<>();
        final Map<String,int[]> media=new HashMap<>();
        final Map<String,FileProgress> progress;
        ChatPage(List<LanNode.Message> rows,Map<String,FileProgress> progress){this.rows=rows;this.progress=progress;}
    }
    private final Map<String,int[]> mediaSizes=new LinkedHashMap<String,int[]>(64,.75f,true) {
        @Override protected boolean removeEldestEntry(Map.Entry<String,int[]> entry){return size()>512;}
    };
    private int[] mediaSize(Uri uri) {
        String key=uri.toString();
        synchronized(mediaSizes){int[] cached=mediaSizes.get(key);if(cached!=null)return cached;}
        int[] size=MediaPreview.bounds(this,uri);
        if(size!=null)synchronized(mediaSizes){mediaSizes.put(key,size);}
        return size;
    }
    ChatPage resolveChatPage(String peer,List<LanNode.Message> rows,Map<String,FileProgress> progress,java.util.function.BooleanSupplier current) {
        ChatPage page=new ChatPage(rows,progress);
        for(LanNode.Message message:rows)for(int index=0;index<message.files.size();index++) {
            if(!current.getAsBoolean())return page;
            Protocol.Attachment file=message.files.get(index);Uri uri;
            String key=fileKey(peer,message.number,file.id,message.outgoing);
            // 仅补当前页被内存缓存淘汰的状态，数据库读取始终留在后台。
            if(!progress.containsKey(key)) {
                ChatStore.TransferState saved=store.transfer(key);
                if(saved!=null)progress.put(key,new FileProgress(saved.percent,saved.state,null));
            }
            if(message.outgoing)uri=storage.outgoingUri(message,index);
            else {
                File local=receivedFile(peer,message.number,file);
                uri=local.isFile()?ReceivedFileProvider.uri(this,local,file.name):storage.existingSaved(local);
                if(uri==null&&storage.saved(local)!=null)page.unavailable.add(key);
            }
            page.files.put(key,uri);
            // 尺寸随后台快照准备好，绑定和解码完成时均不读 SAF，也不改变气泡高度。
            if(uri!=null&&MediaPreview.supported(file.name)&&!MediaPreview.video(file.name)) {
                int[] size=mediaSize(uri);if(size!=null)page.media.put(key,size);
            }
        }
        return page;
    }
    void chatPage(String peer,LanNode.Message anchor,java.util.function.BooleanSupplier current,Consumer<ChatPage> result,Consumer<String> error) {
        Map<String,FileProgress> before=new HashMap<>(fileProgress);
        queue(()->{
            if(!current.getAsBoolean())return;
            try {
                List<LanNode.Message> rows=anchor==null?store.history(peer):store.historyAround(peer,anchor);
                ChatPage page=resolveChatPage(peer,rows,before,current);
                main.post(()->{if(!stopped&&current.getAsBoolean())result.accept(page);});
            }catch(RuntimeException failure){main.post(()->{if(!stopped&&current.getAsBoolean())error.accept("读取聊天记录失败："+failure.getMessage());});}
        });
    }
    void searchHistory(String peer,String query,int number,ChatStore.HistoryIndex index,java.util.function.BooleanSupplier current,Consumer<ChatStore.HistoryPage> result,Consumer<String> error) {
        if(stopped)return;
        archive.execute(()->{
            if(!current.getAsBoolean() || stopped)return;
            try(ChatStore reader=new ChatStore(this)) {
                ChatStore.HistoryPage page=reader.search(peer,query,number,index,()->!stopped && current.getAsBoolean());
                main.post(()->{if(!stopped && current.getAsBoolean())result.accept(page);});
            } catch(RuntimeException failure){main.post(()->{if(!stopped && current.getAsBoolean())error.accept("读取聊天记录失败："+failure.getMessage());});}
        });
    }
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
        // 在线状态只参与展示排序，不改写消息时间；置顶即使离线也留在普通会话之前。
        list.sort(Comparator.comparingInt((ChatStore.Conversation c)->c.pinned?0:c.peer.online?1:2)
            .thenComparing(Comparator.comparingLong((ChatStore.Conversation c)->c.time).reversed())
            .thenComparing(ChatStore.Conversation::displayName,String.CASE_INSENSITIVE_ORDER).thenComparing(c->c.peer.id()));
        main.post(()->{ if (!stopped) { conversations=list; changed(); } });
    }
    void refresh() {
        if(stopped)return;
        refreshAgain.set(true);if(!refreshQueued.compareAndSet(false,true))return;
        LanNode current=node;long epoch=connectionEpoch;
        // 扫描期间的新请求至少再执行一次，不能把 DHCP 就绪后的最后一次刷新丢掉。
        try {discovery.execute(()->{
            try {do {refreshAgain.set(false);if(current!=null&&node==current&&currentConnection(epoch))current.refresh();}while(refreshAgain.get()&&currentConnection(epoch));}
            catch(IOException e){if(currentConnection(epoch))fail(e);}
            finally{refreshQueued.set(false);if(refreshAgain.get()&&!stopped)refresh();}
        });}
        catch(RejectedExecutionException ignored){refreshQueued.set(false);}
    }
    // 回到前台只让维护线程单播复核在线联系人，不额外广播或访问系统网络接口。
    void verifyPresence() { LanNode current=node; if(current!=null&&!connecting&&!stopped) current.revalidate(); }
    void probe(String address) {
        if(stopped)return;long epoch=connectionEpoch;
        try {discovery.execute(()->{try {if(currentConnection(epoch))requireNode().probe(LanNode.endpoint(address));}catch(IOException e){if(currentConnection(epoch))fail(e);}});}catch(RejectedExecutionException ignored){}
    }
    private LanNode requireNode() throws IOException { LanNode current=node; if (current==null||stopped||disconnecting) throw new IOException("请先连接局域网");if(!current.networkAvailable())throw new IOException(current.networkStatus());return current; }
    void send(LanNode.Peer peer,String text,Consumer<Boolean> done) {
        DraftStore drafts=new DraftStore(this); DraftStore.Snapshot sent=drafts.snapshot(peer.id());
        if(stopped){main.post(()->done.accept(false));return;}
        try {io.execute(()->{ try { sendContent(peer,text,Collections.emptyList()); main.post(()->{
                if(sent.text.equals(text)) drafts.clearIfUnchanged(peer.id(),sent);
                changed(); done.accept(true);
            }); }
            catch (Exception e) { fail(e); main.post(()->done.accept(false)); } });}
        catch(RejectedExecutionException e){main.post(()->done.accept(false));}
    }
    void retryMessage(LanNode.Peer peer,LanNode.Message original,Consumer<Boolean> done) {
        queue(()->{
            try {
                LanNode.Message record=store.findMessage(peer.id(),original.number,true);
                if(record==null)throw new IOException("原消息已不存在");
                if(!(record.state.contains("未确认")||record.state.equals("发送失败"))) throw new IOException("此消息已经重发或已送达");
                if(!record.files.isEmpty()) throw new IOException("请重新选择原文件发送");
                LanNode.Message retried=requireNode().retryMessage(peer,record);
                // 同队列立即回写，第二次点击不能抢在网络状态回调之前再重发。
                store.message(peer,retried,true,peer.id().equals(activePeer));publish();main.post(()->done.accept(true));
            }catch(Exception e){fail(e);main.post(()->done.accept(false));}
        });
    }
    void forward(LanNode.Peer source,LanNode.Message message,LanNode.Peer target,Consumer<Boolean> done) {
        if(stopped||!online()){main.post(()->done.accept(false));showError("请先连接局域网");return;}
        if(message.files.isEmpty()) {
            queue(()->{try {sendContent(target,message.text,Collections.emptyList());main.post(()->done.accept(true));}
                catch(Exception e){fail(e);main.post(()->done.accept(false));}});
            return;
        }
        AtomicBoolean success=new AtomicBoolean();
        Set<File> reading=new HashSet<>();
        if(!message.outgoing)for(Protocol.Attachment file:message.files) {
            File local=receivedFile(source,message.number,file);
            if(reading.contains(local))continue;
            if(!receivedFileOperations.add(local)) {
                receivedFileOperations.removeAll(reading);showError("此文件正在接收或处理，请稍后转发");done.accept(false);return;
            }
            reading.add(local);
        }
        transferJob(task->{
            List<File> files=new ArrayList<>();
            try {
                // 转发不走普通输入发送的草稿清理路径；为目标重新生成消息号及授权。
                if(message.outgoing&&message.localPaths.size()!=message.files.size())throw new IOException("旧发送记录没有原文件位置，请重新选择原文件发送");
                int index=0;
                for(Protocol.Attachment attachment:message.files){
                    checkTransfer(task);
                    if(!Protocol.safeName(attachment.name))throw new IOException("附件名称不安全");
                    String origin=message.outgoing?message.localPaths.get(index++):null;
                    File local=message.outgoing?new File(origin):receivedFile(source,message.number,attachment);
                    Uri exported=origin!=null&&origin.startsWith("uri:")?Uri.parse(origin.substring(4)):!message.outgoing&&!local.isFile()?storage.existingSaved(local):null;
                    if(exported==null&&(!local.isFile()||local.length()!=attachment.size))throw new IOException("附件尚未接收或本机文件已删除，请先接收或重新选择文件");
                    // 与普通批量发送一样逐文件隔离；同名附件仍保留原名和独立授权。
                    File folder=new File(directory("offers"),UUID.randomUUID().toString());
                    if(!folder.mkdir())throw new IOException("无法暂存转发附件");
                    File copy=new File(folder,attachment.name);
                    files.add(copy);
                    try(InputStream input=exported==null?new FileInputStream(local):getContentResolver().openInputStream(exported);OutputStream output=new FileOutputStream(copy)){
                        copy(input,output,task,remaining(folder),"暂存空间不足");
                    }
                    if(copy.length()!=attachment.size)throw new IOException("附件大小已改变，请重新选择文件发送");
                    if(attachment.sha256!=null&&!SecureIdentity.hashFile(copy).equals(attachment.sha256))throw new IOException("附件内容已改变，请重新选择文件发送");
                }
                checkTransfer(task);
                sendContent(target,message.text,files);
                success.set(true);
            }finally {
                if(!success.get()) {
                    IOException cleanupFailure=null;
                    for(File file:files) try {Files.deleteIfExists(file.toPath());Files.deleteIfExists(file.getParentFile().toPath());}
                    catch(IOException e) {if(cleanupFailure==null)cleanupFailure=new IOException("转发失败，部分暂存副本未清除，可在设置中清理发送缓存");cleanupFailure.addSuppressed(e);}
                    if(cleanupFailure!=null)throw cleanupFailure;
                }
            }
        },()->{receivedFileOperations.removeAll(reading);main.post(()->done.accept(success.get()));});
    }
    private synchronized void sendContent(LanNode.Peer peer,String text,List<File> files) throws IOException {
        LanNode current=requireNode();
        if(!MessageText.asFile(text)){current.sendMessage(peer,text,files);return;}
        if(text.length()>MessageText.INPUT_LIMIT||text.indexOf(0)>=0)throw new IOException("正文超过 1,048,576 个字符或含有空字符，请直接选择文件发送");
        if(files.size()>=20)throw new IOException("超长正文需要一个 TXT 附件位置，请最多选择 19 个其他文件");
        java.nio.ByteBuffer encoded=StandardCharsets.UTF_8.newEncoder().encode(java.nio.CharBuffer.wrap(text));
        byte[] bytes=new byte[encoded.remaining()];encoded.get(bytes);
        cleanStaging();File offers=directory("offers");
        long used=0;File[] folders=offers.listFiles();
        if(folders==null)throw new IOException("无法读取发送缓存");
        for(File folder:folders){File[] entries=folder.listFiles();if(entries!=null)for(File entry:entries)if(entry.getName().startsWith("消息-")&&entry.getName().endsWith(".txt"))used+=entry.length();}
        if(used+bytes.length>64L*1024*1024)throw new IOException("长文本缓存已达 64 MiB，请在设置中清理发送缓存后重试");
        quota(offers,bytes.length);File folder=new File(offers,UUID.randomUUID().toString());
        if(!folder.mkdir())throw new IOException("无法暂存长文本");
        File file=new File(folder,"消息-"+System.currentTimeMillis()+".txt");boolean sent=false;
        try {
            try(OutputStream output=new FileOutputStream(file)){output.write(bytes);}
            List<File> all=new ArrayList<>(files);all.add(file);
            current.sendMessage(peer,"长文本已转为 TXT，打开附件查看完整内容。",all);sent=true;
        } finally {if(!sent){Files.deleteIfExists(file.toPath());Files.deleteIfExists(folder.toPath());}}
    }
    void disconnect() {
        stopGuest();
        cancelQueuedTransfers();
        closeGateways();
        PhoneRemoteService.stopActive();
        closeSharing();
        diagnostics.record(Diagnostics.Event.Disconnected);
        getSharedPreferences("settings",MODE_PRIVATE).edit().putBoolean("connection_enabled",false).apply();
        synchronized(connectionLock) {
            if(stopped) return;
            long epoch=++connectionEpoch; LanNode old=node; node=null; disconnecting=true; connecting=false; networkReady=false;status="正在断开…";
            if(old!=null)old.stopListening();
            LanNode starting=startingNode;startingNode=null;
            if(starting!=null)starting.stopListening();
            // 网络立即停止；记录落盘仍与建连串行，旧完成回调不得停止新连接。
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
        releaseBackgroundLocks();
        if (multicast!=null && multicast.isHeld()) multicast.release(); multicast=null;
        if (callback!=null && connectivity!=null) { try { connectivity.unregisterNetworkCallback(callback); } catch (RuntimeException ignored) { } } callback=null;
        if (tetherReceiver!=null) { try { unregisterReceiver(tetherReceiver); } catch (RuntimeException ignored) { } } tetherReceiver=null;
        LanNetwork route=lanNetwork;lanNetwork=null;if(route!=null)route.clearTrackedNetworks();
    }
    @android.annotation.SuppressLint("WakelockTimeout")
    void backgroundSettingsChanged(){
        releaseBackgroundLocks();
        if(node==null||stopped||disconnecting||!getSharedPreferences("settings",MODE_PRIVATE).getBoolean("keep_background",true))return;
        // 锁随用户开启的前台连接生存；断开、销毁和关闭此设置均立即释放。
        try {
            PowerManager power=getSystemService(PowerManager.class);
            cpuLock=power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"FeiqLight:connection");cpuLock.setReferenceCounted(false);cpuLock.acquire();
            WifiManager wifi=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
            if(wifi!=null){wifiLock=wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF,"FeiqLight:connection");wifiLock.setReferenceCounted(false);wifiLock.acquire();}
        }catch(RuntimeException e){releaseBackgroundLocks();showError("后台连接保护未开启："+e.getMessage());}
    }
    private void releaseBackgroundLocks(){
        if(cpuLock!=null&&cpuLock.isHeld())cpuLock.release();cpuLock=null;
        if(wifiLock!=null&&wifiLock.isHeld())wifiLock.release();wifiLock=null;
    }
    void cancelTransfer() { LanNode.Transfer task=transfer; if (task!=null) task.close(); }
    private void transferJob(Throwing job) {
        transferJob(job,()->{});
    }
    private boolean transferJob(Throwing job,Runnable finished) {
        return transferJob(job,finished,"文件任务已完成","已取消，可重新操作");
    }
    private boolean transferJob(Throwing job,Runnable finished,String completion,String cancellation) {
        return transferJob(job,finished,completion,cancellation,false);
    }
    private boolean transferJob(Throwing job,Runnable finished,String completion,String cancellation,boolean allowQueue) {
        AtomicBoolean finishedOnce=new AtomicBoolean();
        Runnable cleanup=()->{if(finishedOnce.compareAndSet(false,true)) try {finished.run();} catch(RuntimeException e){fail(e);}};
        if(stopped||disconnecting){cleanup.run();showError("文件服务已停止，请重新连接");return false;}
        if(!busy.compareAndSet(false,true)) {
            // 输入法授权需及时释放，删除/清理也不能延后影响后来收到的文件。
            if(!allowQueue){cleanup.run();showError("已有文件任务，请等待或取消");return false;}
            if(queuedTransfers.size()>=100){cleanup.run();showError("排队任务已达100项，请先处理已有任务");return false;}
            queuedTransfers.addLast(new QueuedTransfer(()->transferJob(job,cleanup,completion,cancellation,true),cleanup));changed();return true;
        }
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
                main.post(()->{ if(!stopped&&transferEpoch.get()==epoch) { transferStatus=outcome; changed();if(!queuedTransfers.isEmpty())queuedTransfers.removeFirst().start.run();else drainReceives(); } });
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
    private void preflightCopies(List<Uri> uris,LanNode.Transfer task)throws IOException {
        long total=0;int known=0;
        for(Uri uri:uris){checkTransfer(task);try(Cursor cursor=getContentResolver().query(uri,new String[]{OpenableColumns.SIZE},null,null)){
            if(cursor!=null&&cursor.moveToFirst()){int column=cursor.getColumnIndex(OpenableColumns.SIZE);if(column>=0&&!cursor.isNull(column)){long size=cursor.getLong(column);if(size>=0){if(size>Long.MAX_VALUE-total)throw new IOException("所选文件总大小超出支持范围");total+=size;known++;}}}
        }}
        File offers=directory("offers");quota(offers,total);String note="准备 "+uris.size()+" 个文件 · "+String.format(Locale.ROOT,"%.1f MiB",total/1048576.0)+(known<uris.size()?" 起":"")+" · 可用 "+String.format(Locale.ROOT,"%.1f GiB",remaining(offers)/1073741824.0);
        main.post(()->{if(!stopped&&transfer==task){transferStatus=note;changed();}});
    }
    private void cleanStaging() throws IOException {
        File[] folders=directory("offers").listFiles();
        if (folders!=null) for (File folder:folders) if (folder.lastModified()<System.currentTimeMillis()-TimeUnit.DAYS.toMillis(7)) {
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
        // 输入法的临时读取授权不进入长期队列，避免候选面板关闭后才使用失效 URI。
        if(busy()){release.run();showError("请等待当前文件任务完成后再发送输入法图片");return false;}
        return sendFile(peer,uri,mime,release);
    }
    private boolean sendFile(LanNode.Peer peer,Uri uri,String keyboardMime,Runnable finished) {
        return sendFiles(peer,Collections.singletonList(uri),keyboardMime,finished);
    }
    void sendFiles(LanNode.Peer peer,List<Uri> uris) { sendFiles(peer,uris,null,()->{}); }
    void sendFolder(LanNode.Peer peer,Uri tree){sendBatch(peer,()->SendFiles.folder(this,tree,SendFiles.batchLimit(this)));}
    private interface BatchSource {List<SendFiles.Entry> read()throws IOException;}
    private void sendBatch(LanNode.Peer peer,BatchSource source){
        transferJob(task->{LanNode current=requireNode();if(peer.fingerprint==null)throw new IOException("文件夹发送需要新版加密协议");current.verifyPeer(peer);List<SendFiles.Entry> entries=source.read();current.checkOfferCapacity(entries.size());preflightCopies(entries.stream().map(e->e.uri).collect(java.util.stream.Collectors.toList()),task);int sent=0;
            for(int start=0;start<entries.size();start+=20){List<File> copies=new ArrayList<>();Map<String,String> relatives=new HashMap<>();boolean published=false;
                try {for(SendFiles.Entry entry:entries.subList(start,Math.min(start+20,entries.size()))){checkTransfer(task);String name=entry.relative.substring(entry.relative.lastIndexOf('/')+1);File dir=new File(directory("offers"),UUID.randomUUID().toString());if(!dir.mkdir())throw new IOException("无法暂存文件");File copy=new File(dir,name);copies.add(copy);try(InputStream in=getContentResolver().openInputStream(entry.uri);OutputStream out=new FileOutputStream(copy)){copy(in,out,task,remaining(dir),"暂存空间不足");}relatives.put(copy.getCanonicalPath(),entry.relative);}
                    current.awaitSendSlot(task);current.sendFiles(peer,copies,relatives);published=true;sent+=copies.size();final int done=sent;main.post(()->{if(transfer==task){transferStatus="邀请已发送 "+done+" / "+entries.size();changed();}});
                }finally{if(!published)for(File copy:copies){Files.deleteIfExists(copy.toPath());Files.deleteIfExists(copy.getParentFile().toPath());}}
            }
        },()->{},"全部文件邀请已发送，接收状态见各文件","已取消，已发邀请保留",true);
    }
    private boolean sendFiles(LanNode.Peer peer,List<Uri> uris,String keyboardMime,Runnable finished) {
        int limit=SendFiles.batchLimit(this);
        if(uris==null||uris.isEmpty()||uris.size()>limit) { finished.run();showError("每批请选择 1–"+limit+" 个文件，可在设置中调整");return false; }
        List<Uri> batch=new ArrayList<>(new LinkedHashSet<>(uris));
        return transferJob(task->{
            LanNode current=requireNode();current.checkOfferCapacity(batch.size()); List<File> staged=new ArrayList<>();int published=0;if(current.security()!=null&&peer.fingerprint!=null)current.verifyPeer(peer);
            if(keyboardMime==null&&peer.fingerprint!=null){List<LanNode.Source> direct=new ArrayList<>();boolean stable=true;for(Uri uri:batch)try{direct.add(new DocumentSource(this,uri));}catch(IOException unavailable){stable=false;break;}
                if(stable){for(int start=0;start<direct.size();start+=20){checkTransfer(task);current.awaitSendSlot(task);current.sendSources(peer,direct.subList(start,Math.min(start+20,direct.size())));}return;}
            }
            cleanStaging();
            if(keyboardMime==null)preflightCopies(batch,task);
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
            // 暂存完成后分组发送；已经发布的邀请保留，失败后可从对应记录处理。
            if(task.isCancelled()) throw new IOException("已取消发送");
            for(int offset=0;offset<staged.size();offset+=20){checkTransfer(task);current.awaitSendSlot(task);int end=Math.min(offset+20,staged.size());current.sendMessage(peer,"",staged.subList(offset,end));published=end;}
            } finally {
                if(published<staged.size()) {
                    IOException cleanupFailure=null;
                    for(File file:staged.subList(published,staged.size())) try {Files.deleteIfExists(file.toPath());Files.deleteIfExists(file.getParentFile().toPath());}
                    catch(IOException e) {if(cleanupFailure==null)cleanupFailure=new IOException("发送失败，部分暂存副本未清除，可在设置中清理发送缓存");cleanupFailure.addSuppressed(e);}
                    if(cleanupFailure!=null)throw cleanupFailure;
                }
            }
        },finished,"","已取消，已发邀请保留；未发文件需重新选择",keyboardMime==null);
    }
    File receivedFile(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        return receivedFile(peer.id(),packet,file);
    }
    File receivedFile(String peer,long packet,Protocol.Attachment file) {
        try {
            byte[] digest=MessageDigest.getInstance("SHA-256").digest((peer+"/"+packet+"/"+file.id+"/"+file.name+"/"+file.size).getBytes(StandardCharsets.UTF_8));
            StringBuilder key=new StringBuilder(); for(byte b:digest) key.append(String.format(Locale.ROOT,"%02x",b&255));
            return new File(new File(getFilesDir(),"received"),key.toString());
        } catch(Exception e) { throw new IllegalStateException(e); }
    }
    void receive(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        File target=receivedFile(peer,packet,file);
        if(receivedFileOperations.contains(target)) {
            ReceiveRequest current=activeReceive;
            if(current==null||!current.peer.id().equals(peer.id())||current.packet!=packet||current.file.id!=file.id)showError("此文件正在处理，请完成后再接收");
            return;
        }
        String receiveKey=fileKey(peer.id(),packet,file.id,false);FileProgress before=progress(peer.id(),packet,file.id,false);
        if(before!=null&&wasStored(before.state))rememberReceivedBefore(receiveKey,true);
        if(busy()) {
            if(activeReceive!=null&&activeReceive.peer.id().equals(peer.id())&&activeReceive.packet==packet&&activeReceive.file.id==file.id)return;
            if(waiting.size()>=SendFiles.limit(getSharedPreferences("settings",MODE_PRIVATE),"pending_file_limit",10000)){showError("待接收文件已达设置上限，请处理已有任务或在设置中调大");return;}
            // 排队进度也会写库；先读取旧终态，避免覆盖后丢失「曾保存」标记。
            if(before==null)queue(()->{ChatStore.TransferState saved=store.transfer(receiveKey);if(saved!=null&&wasStored(saved.state))rememberReceivedBefore(receiveKey,true);});
            waiting.put(receivedFile(peer,packet,file).getName(),new ReceiveRequest(peer,packet,file));
            FileProgress previous=progress(peer.id(),packet,file.id,false);fileProgress(peer.id(),packet,file.id,false,previous==null?0:previous.percent,"排队中",null);return;
        }
        if(!receivedFileOperations.add(target)){showError("此文件正在处理，请完成后再接收");return;}
        waiting.remove(target.getName());
        ReceiveRequest request=new ReceiveRequest(peer,packet,file);activeReceive=request;
        transferJob(task->{
            request.task=task;if(request.offerCanceled||request.rejected)task.close();
            long epoch=transferEpoch.get();
            java.util.function.BiConsumer<Integer,String> progress=(percent,state)->main.post(()->{
                if(!stopped&&transferEpoch.get()==epoch&&!request.offerCanceled&&!request.rejected)fileProgress(peer.id(),packet,file.id,false,percent,state,state.contains("中")?task:null);
            });
            int[] last={0};
            try {
            if(before==null) {
                ChatStore.TransferState saved=io.submit(()->store.transfer(receiveKey)).get();
                if(saved!=null&&wasStored(saved.state))rememberReceivedBefore(receiveKey,true);
            }
            progress.accept(0,"连接中 · 点击取消");
            File dir=directory("received");
            if(!storage.exists(target)) {
                if(storage.saved(target)!=null)throw new IOException(ReceivedStorage.UNAVAILABLE);
                long partial=LanNode.partialFile(peer,packet,file,target).length();
                quota(dir,file.size-(partial<=file.size?partial:0));
                requireNode().receiveFile(peer,packet,file,target,task,new LanNode.Progress() {
                    @Override public void update(int percent) { update(percent,0); }
                    @Override public void update(int percent,double bytesPerSecond) {
                        last[0]=percent;
                        main.post(()->{if(!stopped&&transferEpoch.get()==epoch&&!request.offerCanceled&&!request.rejected)fileProgress(peer.id(),packet,file.id,false,percent,"接收中 · 点击取消",bytesPerSecond,task);});
                    }
                    @Override public void stage(String value) { progress.accept(100,value); }
                });
            }
            progress.accept(100,"保存中 · 点击取消");
            if(target.isFile())storage.save(target,file.name,file.relativePath,task);
            progress.accept(100,"已保存");rememberReceivedBefore(receiveKey,false);
            } catch(LanNode.OfferRevokedException e) {
                if(request.rejected)return;
                request.offerCanceled=true;offerCanceled(peer,packet);
            } catch(Exception e) {
                if(request.offerCanceled||request.rejected)return;
                progress.accept(target.isFile()?100:task.isPaused()?last[0]:task.isCancelled()?0:last[0],target.isFile()?"已保存":task.isPaused()?"已暂停":task.isCancelled()?"已取消 · 点击重试":"接收失败 · 点击续传："+e.getMessage());
                // 保存目录失败时内部完整文件已经是正式文件；网络中断仍只由附件卡片反馈。
                if(!target.isFile()&&e instanceof IOException){diagnostics.record(Diagnostics.Event.TaskFailed);return;}
                throw e;
            }
        },()->{
            if(activeReceive==request)activeReceive=null;
            // 排在保存进度回调之后释放，避免迟到的「已保存」覆盖随后删除的状态。
            main.post(()->receivedFileOperations.remove(target));
            if(request.offerCanceled||request.rejected)queue(()->{
                try {
                    boolean saved=storage.exists(target),wasBefore=receivedBefore.contains(receiveKey);
                    if(!saved)Files.deleteIfExists(LanNode.partialFile(peer,packet,file,target).toPath());
                    if(request.rejected)rejectIncomingOffer(peer,packet);
                    else finishIncomingOffer(peer,packet,"对方已取消发送",null);
                    rememberReceivedBefore(receiveKey,false);
                    if(saved) {
                        // 拒绝或取消晚于落盘：文件已保留，被标记拦下的「已保存」终态在此补发，卡片不能停在「保存中」。
                        main.post(()->{if(!stopped)fileProgress(peer.id(),packet,file.id,false,100,"已保存",null);});
                    } else if(!request.rejected) {
                        String ended=wasBefore?"本机文件已删除":"对方已取消发送";
                        main.post(()->{if(!stopped&&!storage.exists(target))fileProgress(peer.id(),packet,file.id,false,0,ended,null);});
                    }
                }catch(IOException e){fail(e);}
            });
        },"","");
        // 已有关联消息的任务不再占据输入框上方；复制等无消息任务仍保留公共提示。
        transferStatus=""; changed();
    }
    private boolean reserveDeletion(File local) {
        ReceiveRequest pending=waiting.get(local.getName());
        FileProgress progress=pending==null?null:progress(pending.peer.id(),pending.packet,pending.file.id,false);
        return (progress==null||!progress.state.equals("排队中"))&&receivedFileOperations.add(local);
    }
    void deleteReceivedFile(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        if(stopped||disconnecting){showError("文件服务已停止，请重新连接");return;}
        File local=receivedFile(peer,packet,file);
        if(!reserveDeletion(local)){showError("此文件正在接收或处理，请完成后再删除");return;}
        queue(()->{
            try {
                if(stopped||disconnecting)return;
                storage.delete(local);
                store.transfer(fileKey(peer.id(),packet,file.id,false),peer.id(),packet,0,"本机文件已删除");
                main.post(()->{if(!stopped){waiting.remove(local.getName());fileProgress(peer.id(),packet,file.id,false,0,"本机文件已删除",null);if(!busy())transferStatus="文件已删除，消息记录保留";changed();}});
            } catch(IOException e){fail(e);}
            finally {main.post(()->{receivedFileOperations.remove(local);drainReceives();});}
        });
    }
    static final class BatchFileDeleteResult {
        final int deleted, skippedDeleted, unreceived, skippedBusy;
        BatchFileDeleteResult(int deleted,int skippedDeleted,int unreceived,int skippedBusy) {
            this.deleted=deleted;this.skippedDeleted=skippedDeleted;this.unreceived=unreceived;this.skippedBusy=skippedBusy;
        }
    }
    void deleteReceivedFiles(String peer,List<LanNode.Message> messages,Consumer<BatchFileDeleteResult> done) {
        if(stopped||disconnecting){showError("文件服务已停止，请重新连接");return;}
        List<LanNode.Message> selected=new ArrayList<>();
        for(LanNode.Message message:messages)if(!message.outgoing&&!message.files.isEmpty())selected.add(message.copy());
        Set<File> reserved=new HashSet<>(),skipped=new HashSet<>();
        for(LanNode.Message message:selected)for(Protocol.Attachment file:message.files) {
            File local=receivedFile(peer,message.number,file);
            if(reserved.contains(local)||skipped.contains(local))continue;
            if(reserveDeletion(local))reserved.add(local);else skipped.add(local);
        }
        Map<String,FileProgress> progressSnapshot=new HashMap<>(fileProgress);
        queue(()->{
            List<ChatStore.FileTask> removed=new ArrayList<>();BatchFileDeleteResult result=null;
            try {
                if(stopped||disconnecting)return;
                List<LanNode.Message> storedMessages=new ArrayList<>();Set<String> storedTransfers=new HashSet<>();
                // 删除与历史更新在同一 I/O 队列，接收仍在 transferWorker 独立运行。
                for(LanNode.Message message:selected) {
                    LanNode.Message stored=store.findMessage(peer,message.number,false);
                    if(stored!=null)storedMessages.add(stored);
                }
                for(ChatStore.TransferState stored:store.transfers(peer,0))if(wasStored(stored.state))storedTransfers.add(stored.key);
                Set<String> seen=new HashSet<>();int deleted=0,skippedDeleted=0,unreceived=0;
                for(LanNode.Message message:storedMessages)for(Protocol.Attachment file:message.files) {
                    String key=fileKey(peer,message.number,file.id,false);
                    if(!seen.add(key))continue;
                    File target=receivedFile(peer,message.number,file);
                    if(!reserved.contains(target))continue;
                    if(stopped||disconnecting)return;
                    if(storage.exists(target)) {
                        storage.delete(target);deleted++;
                    } else {
                        if(storage.saved(target)!=null)throw new IOException("保存目录授权已失效，文件未删除，请重新授权后重试");
                        FileProgress previous=progressSnapshot.get(key);
                        if(!wasStored(message.state)&&!storedTransfers.contains(key)&&(previous==null||!wasStored(previous.state))) {
                            unreceived++;continue;
                        }
                        skippedDeleted++;
                    }
                    removed.add(new ChatStore.FileTask(peer,message,file));
                    store.transfer(key,peer,message.number,0,"本机文件已删除");
                }
                result=new BatchFileDeleteResult(deleted,skippedDeleted,unreceived,skipped.size());
            } catch(IOException e){fail(e);}
            finally {
                BatchFileDeleteResult completed=result;
                main.post(()->{
                    try {
                        if(stopped)return;
                        for(ChatStore.FileTask item:removed){waiting.remove(receivedFile(peer,item.message.number,item.file).getName());fileProgress(peer,item.message.number,item.file.id,false,0,"本机文件已删除",null);}
                        changed();notifyTransfers.run();
                        if(completed!=null)done.accept(completed);
                    } finally {
                        receivedFileOperations.removeAll(reserved);
                        drainReceives();
                    }
                });
            }
        });
    }
    void deleteGuestFile(File guestFile) {
        if(stopped||disconnecting){showError("文件服务已停止，请重新连接");return;}
        if(!reserveDeletion(guestFile)){showError("此文件正在处理，请完成后再删除");return;}
        queue(()->{
            try {
                if(stopped||disconnecting)return;
                if(!getSharedPreferences("guest_received",MODE_PRIVATE).contains(guestFile.getName()))throw new IOException("文件尚未完成保存，请完成后再删除");
                storage.delete(guestFile);
                main.post(()->{if(!stopped){if(!busy())transferStatus="文件已删除，接收记录保留";changed();notifyTransfers.run();}});
            } catch(IOException e){fail(e);}
            finally {main.post(()->receivedFileOperations.remove(guestFile));}
        });
    }
    void reject(LanNode.Peer peer,long packet) {
        waiting.values().removeIf(r->r.peer.id().equals(peer.id())&&r.packet==packet);
        ReceiveRequest active=activeReceive;
        if(active!=null&&active.peer.id().equals(peer.id())&&active.packet==packet) {
            // 接收线程退出并释放句柄后，再在完成回调中清理断点和拒收，避免删除仍在写入的文件。
            active.rejected=true;LanNode.Transfer task=active.task;if(task!=null)task.close();return;
        }
        queue(()->rejectIncomingOffer(peer,packet));
    }
    private void rejectIncomingOffer(LanNode.Peer peer,long packet) {
        finishIncomingOffer(peer,packet,"已拒绝",null);
        LanNode current=node;
        if(current==null||stopped||disconnecting||!current.networkAvailable()) {showError("已本地拒收并清理未完成文件；拒绝通知未送达对方");return;}
        try {current.reject(peer,packet);}
        catch(IOException e) {showError("已本地拒收并清理未完成文件；拒绝通知未送达对方："+e.getMessage());}
    }
    private void deleteIncomingPartials(LanNode.Peer peer,LanNode.Message message) throws IOException {
        if(message.outgoing)return;
        for(Protocol.Attachment file:message.files) {
            File target=receivedFile(peer,message.number,file);
            if(!storage.exists(target))Files.deleteIfExists(LanNode.partialFile(peer,message.number,file,target).toPath());
        }
    }
    void offerCanceled(LanNode.Peer peer,long packet) {
        ReceiveRequest active=activeReceive;
        if(active!=null&&active.peer.id().equals(peer.id())&&active.packet==packet) {
            active.offerCanceled=true;LanNode.Transfer task=active.task;if(task!=null)task.close();
        } else active=null;
        ReceiveRequest exiting=active;
        main.post(()->{if(!stopped){waiting.values().removeIf(r->r.peer.id().equals(peer.id())&&r.packet==packet);changed();}});
        queue(()->finishIncomingOffer(peer,packet,"对方已取消发送",exiting));
    }
    private void finishIncomingOffer(LanNode.Peer peer,long packet,String state,ReceiveRequest exiting) {
        LanNode.Message message=store.findMessage(peer.id(),packet,false);
        if(message==null)return;
        Set<String> stored=new HashSet<>();
        for(ChatStore.TransferState item:store.transfers(peer.id(),0))if(wasStored(item.state))stored.add(item.key);
        List<Protocol.Attachment> unfinished=new ArrayList<>();int kept=0;boolean restored=false;
        for(Protocol.Attachment file:message.files) {
            File target=receivedFile(peer,packet,file);String key=fileKey(peer.id(),packet,file.id,false);
            if(storage.exists(target)){rememberReceivedBefore(key,false);kept++;continue;}
            if(receivedBefore.contains(key)) {
                // 重新接收途中邀请失效或被拒绝：附件此前保存过，只恢复「本机文件已删除」，不从消息里移除。
                kept++;
                if(!stored.contains(key))restored=true;
                if(exiting==null||exiting.file.id!=file.id) {
                    try {Files.deleteIfExists(LanNode.partialFile(peer,packet,file,target).toPath());}
                    catch(IOException e){showError(state+"；断点清理未完成："+e.getMessage());}
                    rememberReceivedBefore(key,false);
                }
                store.transfer(key,peer.id(),packet,0,"本机文件已删除");
                main.post(()->{if(!stopped)fileProgress(peer.id(),packet,file.id,false,0,"本机文件已删除",null);});
                continue;
            }
            // 已接收后删除的是本机文件；取消剩余邀请不能再把这条附件历史当成未完成项移除。
            if(wasStored(message.state)||stored.contains(key)){kept++;continue;}
            unfinished.add(file);
            // 活跃线程的 finally 负责释放句柄；完成回调还会用原附件元数据补清一次。
            if(exiting==null||exiting.file.id!=file.id)
                try {Files.deleteIfExists(LanNode.partialFile(peer,packet,file,target).toPath());}
                catch(IOException e){showError(state+"；断点清理未完成："+e.getMessage());}
            store.transfer(fileKey(peer.id(),packet,file.id,false),peer.id(),packet,0,state);
            main.post(()->{if(!stopped)fileProgress(peer.id(),packet,file.id,false,0,state,null);});
        }
        if(restored&&unfinished.isEmpty()&&!state.equals("已拒绝"))showError("原邀请已失效，请让对方重新发送");
        // 整批附件都已保存或保存过时，拒绝不改写消息状态与正文。
        if(!unfinished.isEmpty()||state.equals("已拒绝")&&kept==0) {
            message.files.removeAll(unfinished);
            String marker=state.equals("已拒绝")?"[附件已拒绝]":"[对方已取消发送]";
            if(!message.text.endsWith(marker))message.text+=(message.text.isEmpty()?"":"\n")+marker;
            message.state=state;store.message(peer,message,true,true);
        }
        publish();
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
    /** 当前登记且真实存在的接收文件；旧版双份数据按一个文件统计，字节数包含两份。 */
    static class Usage {
        final int files, guestFiles; final long bytes;
        int skipped;
        Usage(int files,long bytes,int guestFiles) { this.files=files; this.bytes=bytes; this.guestFiles=guestFiles; }
    }
    static final class ReceivedReview extends Usage {
        final Map<File,String> locations;
        ReceivedReview(Map<File,String> locations,long bytes,int guests) {
            super(locations.size(),bytes,guests);this.locations=Collections.unmodifiableMap(locations);
        }
    }
    ReceivedReview receivedUsage() throws IOException {
        Map<File,String> locations=new LinkedHashMap<>();int guestFiles=0;long bytes=0;
        SharedPreferences guests=getSharedPreferences("guest_received",MODE_PRIVATE);
        for(File file:storage.receivedFiles())if(storage.exists(file)) {
            bytes+=storage.bytes(file);locations.put(file,storage.cleanupVersion(file));
            if(file.getName().matches("[a-f0-9]{64}")&&guests.contains(file.getName()))guestFiles++;
        }
        return new ReceivedReview(locations,bytes,guestFiles);
    }
    void reviewReceivedFiles(Consumer<ReceivedReview> done,Consumer<String> error) {
        queue(()->{
            try { ReceivedReview review=receivedUsage();main.post(()->done.accept(review)); }
            catch(IOException|RuntimeException failure){main.post(()->error.accept("无法统计已接收文件："+failure.getMessage()));}
        });
    }
    void clearFiles(ReceivedReview review,Consumer<Usage> done) {
        if(busy()) { showError("请先完成或取消文件任务"); return; }
        transferJob(task->{
            Set<File> deleted=new HashSet<>();int guestFiles=0,skipped=0;long bytes=0;Usage result=null;
            SharedPreferences guests=getSharedPreferences("guest_received",MODE_PRIVATE);
            try {
                for(Map.Entry<File,String> entry:review.locations.entrySet()) {
                    File file=entry.getKey();
                    checkTransfer(task);if(!receivedFileOperations.add(file))continue;
                    try {
                        long length=storage.bytes(file);
                        if(!storage.deleteReviewed(file,entry.getValue(),task)){skipped++;continue;}
                        deleted.add(file);bytes+=length;
                        if(file.getName().matches("[a-f0-9]{64}")&&guests.contains(file.getName()))guestFiles++;
                    } finally {receivedFileOperations.remove(file);}
                }
                checkTransfer(task);result=new Usage(deleted.size(),bytes,guestFiles);result.skipped=skipped;
            } finally {
                Usage completed=result;
                // 部分文件删除后再取消或失败，也必须刷新那些已经删除的附件；数据库仍只在 I/O 队列访问。
                queue(()->{List<ChatStore.FileTask> rows=store.fileTasks();
                    main.post(()->{if(stopped)return;
                        for(File file:deleted)waiting.remove(file.getName());
                        for(ChatStore.FileTask row:rows)if(!row.message.outgoing&&deleted.contains(receivedFile(row.peer,row.message.number,row.file)))fileProgress(row.peer,row.message.number,row.file.id,false,0,"本机文件已删除",null);
                        changed();notifyTransfers.run();if(completed!=null&&!task.isCancelled())done.accept(completed);
                    });
                });
            }
        },()->{},"","已取消，可重新操作");
    }
    void clearOffers(Consumer<Usage> done) {
        transferJob(task->{
            LanNode current=node; if(current!=null) current.revokeOffers();
            File[] folders=directory("offers").listFiles(); if(folders==null) throw new IOException("无法读取发送缓存");
            int count=0; long bytes=0;
            for(File folder:folders) {
                checkTransfer(task);
                if(folder.isDirectory()) {
                    File[] files=folder.listFiles(); if(files==null) throw new IOException("无法读取发送缓存");
                    for(File file:files) { checkTransfer(task); if(!file.isFile()) continue; long length=file.length(); if(Files.deleteIfExists(file.toPath())) { count++; bytes+=length; } }
                }
                checkTransfer(task);
                Files.deleteIfExists(folder.toPath());
            }
            Usage result=new Usage(count,bytes,0); main.post(()->{ if(!stopped) done.accept(result); });
        },()->{},"","已取消，可重新操作");
    }
    private void fail(Exception e) { diagnostics.record(Diagnostics.Event.Error);showError(e.getMessage()==null?e.getClass().getSimpleName():e.getMessage()); }
    private void showError(String text) { main.post(()->{ for(Observer o:new ArrayList<>(observers)) o.error(text); }); }
    @Override public void onDestroy() {
        if(instance==this)instance=null;
        stopGuest();
        cancelQueuedTransfers();
        closeGateways();securityConfirmation=null;
        closeSharing();
        diagnostics.record(Diagnostics.Event.Stopped);
        synchronized(connectionLock) {
            stopped=true; connectionEpoch++; connecting=false; disconnecting=true;networkReady=false;
            LanNode old=node; node=null;
            // 文档 I/O 可能仍在等待，监听端口不能跟着排队，否则新服务会撞上旧服务的 2425。
            if(old!=null)old.stopListening();
            LanNode starting=startingNode;startingNode=null;
            if(starting!=null)starting.stopListening();
            // 先使全部旧任务失效，再在队列末尾关闭节点/数据库；不能先读取 null 再让排队任务打开端口。
            io.execute(()->{ try { if(old!=null) old.close(); } finally { try { store.recoverPending(); } finally { store.close(); } } }); io.shutdown();
        }
        waiting.clear(); cancelTransfer();
        releaseNetwork(); main.removeCallbacks(notifyChanges); main.removeCallbacks(notifyTransfers); observers.clear(); transferWorker.shutdownNow();discovery.shutdownNow();archive.shutdownNow();
        // shutdownNow 会丢弃尚未执行的复制任务，它们不会进入 finally；销毁时也需归还 IME 授权。
        Runnable cleanup=transferCleanup; if(cleanup!=null) cleanup.run();
        super.onDestroy();
    }
}
