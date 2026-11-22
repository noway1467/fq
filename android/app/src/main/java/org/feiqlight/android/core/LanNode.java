package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.nio.file.Files;
import java.security.SecureRandom;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import javax.net.ssl.SSLSocket;

/** 无 Android 依赖的通信核心，JVM 测试直接运行 APK 使用的同一份代码。 */
public final class LanNode implements AutoCloseable {
    // 不设产品级单文件上限，长度仍受协议和文件系统能力约束。
    public static final long MAX_FILE=Long.MAX_VALUE;
    public static final class Peer {
        public volatile InetSocketAddress endpoint;
        public volatile String login, host, deviceId, conversationId;
        public volatile String fingerprint;
        public volatile String name, group, avatar;
        public volatile boolean utf8, online;
        public volatile long lastSeen;
        // 仅供在线复核：最近一次单播探测及之后连续未回应次数；初值取创建时刻，nanoTime 原点为负也不会误判。
        volatile long probed=System.nanoTime();
        volatile int misses;
        public Peer(InetSocketAddress endpoint,String login,String host,String name,String group) {
            this.endpoint=endpoint; this.login=login; this.host=host; this.name=name; this.group=group;
        }
        public String id() { return conversationId!=null ? conversationId : deviceId!=null ? "device:"+deviceId : endpoint.getAddress().getHostAddress()+":"+endpoint.getPort()+"/"+login; }
    }
    public static final class Message {
        public long number, time;
        public String text, state;
        public boolean outgoing;
        public List<Protocol.Attachment> files=new ArrayList<>();
        // 仅本机发送流程填写，绝不编码进网络报文。
        public List<String> localPaths=new ArrayList<>();
        public Message copy() {
            Message m=new Message(); m.number=number; m.time=time; m.text=text; m.state=state; m.outgoing=outgoing; m.files=new ArrayList<>(files); m.localPaths=new ArrayList<>(localPaths); return m;
        }
    }
    public interface Listener {
        void peersChanged();
        void message(Peer peer, Message message, boolean update);
        void error(String text);
        default void networkStateChanged(boolean available,String reason) { }
        default void networkReset() { }
        default void offerCanceled(Peer peer,long packet) { }
        default void fileProgress(Peer peer,long packet,long file,int percent,String state) { }
        default void fileProgress(Peer peer,long packet,long file,int percent,String state,double bytesPerSecond) { fileProgress(peer,packet,file,percent,state); }
    }
    public interface SocketBinding {
        default void send(DatagramSocket socket,byte[] bytes,InetSocketAddress destination)throws IOException {socket.send(new DatagramPacket(bytes,bytes.length,destination));}
        void bind(DatagramSocket socket) throws IOException;
        void bind(Socket socket) throws IOException;
        default void bind(Socket socket,InetAddress destination) throws IOException { bind(socket); }
        default void bindAccepted(Socket socket) throws IOException { }
        default boolean allows(InetAddress address) throws IOException { return true; }
        default Collection<InetAddress> broadcasts() throws IOException { return null; }
        // null 表示没有实体局域网；键只包含路由身份和 IPv4，不包含互联网验证状态。
        default Object networkKey() throws IOException { return this; }
        default String description() throws IOException {return "物理局域网";}
        default void changed() { }
    }
    private volatile SocketBinding socketBinding;
    public void setSocketBinding(SocketBinding binding) { if(started) throw new IllegalStateException("需在启动前设置网络"); socketBinding=binding; }
    private final AtomicBoolean networkDirty=new AtomicBoolean();
    private volatile long fastRefreshUntil;
    public void networkChanged() {
        if(closed)return;
        SocketBinding binding=socketBinding;if(binding!=null)binding.changed();
        fastRefreshUntil=System.nanoTime()+TimeUnit.SECONDS.toNanos(10);
        if(networkDirty.compareAndSet(false,true))try {timer.execute(this::tick);}catch(RejectedExecutionException ignored) { }
    }
    // 静默 6 秒后逐个单播 ENTRY，连续 3 次无回应判离线；网络切换或手动刷新后 2 次无回应即离线。
    long heartbeatIdle=TimeUnit.SECONDS.toNanos(6), heartbeatRetry=TimeUnit.SECONDS.toNanos(3);
    int idleMisses=3, verifyMisses=2;
    private volatile long verifyFrom=System.nanoTime();
    /** 网络切换、用户刷新或回到前台时立即复核：仍能回应的联系人保持在线，失联者约 6 秒内离线，离线联系人立即探测恢复。 */
    public void revalidate() {
        long now=System.nanoTime();verifyFrom=now;
        synchronized(gate){
            for(Peer peer:peers.values()){
                if(!peer.online)peer.misses=0;
                if(peer.misses==0)peer.probed=now-heartbeatRetry;
            }
        }
        if(networkDirty.compareAndSet(false,true))try {timer.execute(this::tick);}catch(RejectedExecutionException ignored) { }
    }
    public interface Progress { void update(int percent); default void update(int percent,double bytesPerSecond) { update(percent); } }
    public static final class OfferRevokedException extends IOException {
        private static final long serialVersionUID=1L;
        public OfferRevokedException() { super("对方已取消发送"); }
    }
    public static final class Transfer implements AutoCloseable {
        private volatile boolean cancelled;
        private volatile Socket socket;
        public boolean isCancelled() { return cancelled; }
        private synchronized void attach(Socket value) throws IOException {
            if (cancelled) { value.close(); throw new IOException("已取消传输"); } socket=value;
        }
        @Override public synchronized void close() { cancelled=true; if (socket!=null) try { socket.close(); } catch (IOException ignored) { } }
    }
    private static final class Pending {
        final Peer peer;
        final InetSocketAddress endpoint;
        final String login,deviceId,fingerprint;
        final AtomicBoolean sending=new AtomicBoolean();
        final AtomicBoolean transmitting=new AtomicBoolean();
        volatile boolean delivered,directFailed,canceled;
        volatile long attemptStarted=System.nanoTime();
        volatile Socket connectingSocket;
        Message message; byte[] bytes; long last; int attempts=1;
        Pending(Peer peer){this.peer=peer;endpoint=peer.endpoint;login=peer.login;deviceId=peer.deviceId;fingerprint=peer.fingerprint;}
    }
    private static final class Offer {
        File file; long size, modified, expires; InetAddress address;
        Source source;
        Peer peer; Message message; long packet, id, progressVersion; int percent; String state;
        String deviceId,fingerprint,sha256,storedMessageState;
        boolean canceled;
        final Set<Socket> activeSockets=new HashSet<>();
        final AtomicBoolean pushing=new AtomicBoolean();
    }
    private static final class PendingDownload {
        final String deviceId,fingerprint;
        final long packet,fileId,offset,createdAt=System.nanoTime();
        volatile boolean directFailed;
        volatile Socket directSocket,pushedClient;
        volatile SSLSocket pushedSocket;
        final AtomicBoolean claimed=new AtomicBoolean();
        final CountDownLatch ready=new CountDownLatch(1),done=new CountDownLatch(1);
        PendingDownload(String deviceId,String fingerprint,long packet,long fileId,long offset){this.deviceId=deviceId;this.fingerprint=fingerprint;this.packet=packet;this.fileId=fileId;this.offset=offset;}
    }
    private final Object gate=new Object();
    // Android/旧 JDK 的 receive() 持有 socket 自身监视器；发送绝不能争用这把阻塞锁。
    private final Object udpSendGate=new Object();
    private final Map<String,Peer> peers=new LinkedHashMap<>();
    private final Set<InetSocketAddress> remembered=new LinkedHashSet<>();
    private final Map<Long,Pending> pending=new HashMap<>();
    private final Map<String,Long> seen=new LinkedHashMap<>();
    private final Map<String,Offer> offers=new HashMap<>();
    private final List<PendingDownload> pendingDownloads=new ArrayList<>();
    private final Set<String> activeSyncPeers=new HashSet<>();
    private static final class ReverseRequest {
        String deviceId,fingerprint;int port;Socket client,direct;
        final CountDownLatch ready=new CountDownLatch(1);
    }
    private final Map<String,ReverseRequest> reverseRequests=new HashMap<>();
    private final Set<String> activeReverseRequests=new HashSet<>();
    private int transportEpoch;
    public int transportEpoch(){synchronized(gate){return transportEpoch;}}
    interface PeerConnector {void connect(Socket socket,InetSocketAddress target)throws IOException;}
    private PeerConnector peerConnector=(socket,target)->socket.connect(target,8000);
    private static void closeSocket(Socket socket){try{socket.close();}catch(IOException ignored){}}
    private static boolean isCompletedState(String state){return "已发送（非保存确认）".equals(state)||"对方已校验保存".equals(state);}
    private static boolean isOfferCompleted(Offer offer){return offer!=null&&isCompletedState(offer.state);}
    public static boolean isUnconfirmedState(String state){String value=state==null?"":state;return "等待确认".equals(value)||value.contains("未确认")||value.contains("失败");}
    private boolean tryBeginOfferStream(String key,java.util.function.Predicate<Offer> authorize,Socket client,long[] version,Offer[] selected) {
        synchronized(gate){Offer offer=offers.get(key);if(!closed&&offer!=null&&!offer.canceled&&offer.expires>=System.nanoTime()&&authorize.test(offer)){version[0]=++offer.progressVersion;offer.activeSockets.add(client);selected[0]=offer;return true;}version[0]=0;selected[0]=null;return false;}
    }
    private void endOfferStream(Offer offer,Socket client){if(offer!=null&&client!=null)synchronized(gate){offer.activeSockets.remove(client);}}
    public boolean hasActiveOutgoingTransfer(String peerId,long packet){synchronized(gate){return offers.values().stream().anyMatch(o->o.peer.id().equals(peerId)&&o.packet==packet&&!o.canceled&&!o.activeSockets.isEmpty());}}
    public boolean hasActiveOutgoingTransfer(String peerId){synchronized(gate){return offers.values().stream().anyMatch(o->o.peer.id().equals(peerId)&&!o.canceled&&!o.activeSockets.isEmpty());}}
    public boolean canCancelOutgoing(String peerId,long packet){synchronized(gate){Pending item=pending.get(packet);return (item!=null&&!item.canceled&&!item.delivered&&item.peer.id().equals(peerId))||offers.values().stream().anyMatch(o->o.peer.id().equals(peerId)&&o.packet==packet&&!o.canceled&&o.expires>=System.nanoTime()&&!isOfferCompleted(o));}}
    public boolean hasUndeliveredOutgoing(String peerId,long packet,String state){synchronized(gate){Pending item=pending.get(packet);return item!=null&&!item.canceled&&!item.delivered&&item.peer.id().equals(peerId)||isUnconfirmedState(state)&&offers.values().stream().anyMatch(o->o.peer.id().equals(peerId)&&o.packet==packet&&!o.canceled&&!isOfferCompleted(o));}}
    public boolean cancelOutgoing(String peerId,long packet)throws IOException{return cancelOutgoing(peerId,packet,null,true);}
    public boolean cancelOutgoing(String peerId,long packet,String knownState,boolean notify)throws IOException{return cancelOutgoingInternal(peerId,packet,knownState,notify,false);}
    public boolean cancelUndeliveredOutgoing(String peerId,long packet,String state,boolean notify)throws IOException{return cancelOutgoingInternal(peerId,packet,state,notify,true);}
    private boolean cancelOutgoingInternal(String peerId,long packet,String knownState,boolean notify,boolean onlyUndelivered)throws IOException {
        Pending item=null;List<Map.Entry<String,Offer>> selected=new ArrayList<>();List<Socket> sockets=new ArrayList<>();Socket connecting=null;Message stateMessage=null;Peer notifyPeer=null;boolean wasPending=false,updateState=false;
        Peer cancelPeer=null;
        synchronized(gate){Pending candidate=pending.get(packet);if(candidate!=null&&candidate.peer.id().equals(peerId)&&!candidate.canceled&&!candidate.delivered)item=candidate;
            for(Map.Entry<String,Offer> entry:offers.entrySet()){Offer offer=entry.getValue();if(offer.peer.id().equals(peerId)&&offer.packet==packet&&!offer.canceled&&!isOfferCompleted(offer))selected.add(entry);}
            boolean active=selected.stream().anyMatch(entry->!entry.getValue().activeSockets.isEmpty());String actualState=item!=null?item.message.state:selected.stream().map(entry->entry.getValue().message==null?entry.getValue().storedMessageState:entry.getValue().message.state).filter(Objects::nonNull).findFirst().orElse(null);String state=item!=null?actualState:"已送达".equals(actualState)?actualState:(knownState!=null&&!knownState.isEmpty()?knownState:actualState);
            if(onlyUndelivered&&"已送达".equals(knownState))return false;if(onlyUndelivered&&item==null&&(!isUnconfirmedState(state)||active))return false;if(item==null&&selected.isEmpty())return false;
            if(notify&&!onlyUndelivered&&!selected.isEmpty()&&(active||"已送达".equals(state)||selected.stream().anyMatch(entry->entry.getValue().percent>0)||item!=null&&item.transmitting.get())) {
                Peer target=item!=null?item.peer:selected.get(0).getValue().peer;
                cancelPeer=new Peer(target.endpoint,target.login,target.host,target.name,target.group);cancelPeer.deviceId=target.deviceId;cancelPeer.fingerprint=target.fingerprint;cancelPeer.conversationId=target.id();
            }
            Map<String,Offer> next=new HashMap<>(offers);for(Map.Entry<String,Offer> entry:selected)if(entry.getValue().fingerprint!=null)next.remove(entry.getKey());if(selected.stream().anyMatch(entry->entry.getValue().fingerprint!=null))saveOffers(next);
            if(item!=null){item.canceled=true;pending.remove(packet);connecting=item.connectingSocket;item.connectingSocket=null;stateMessage=item.message;wasPending=true;}
            for(Map.Entry<String,Offer> entry:selected){Offer offer=entry.getValue();offer.canceled=true;offers.remove(entry.getKey());sockets.addAll(offer.activeSockets);offer.activeSockets.clear();offer.progressVersion++;offer.state="已取消发送";if(notify&&!closed)listener.fileProgress(offer.peer,offer.packet,offer.id,offer.percent,offer.state,0);if(stateMessage==null&&offer.message!=null)stateMessage=offer.message;if(notifyPeer==null)notifyPeer=offer.peer;}
            updateState=wasPending||isUnconfirmedState(state)&&!"已送达".equals(state);if(updateState&&stateMessage!=null)stateMessage.state="已取消发送";
        }
        if(connecting!=null)closeSocket(connecting);for(Socket socket:sockets)closeSocket(socket);if(notify&&updateState&&stateMessage!=null)listener.message(item!=null?item.peer:notifyPeer,stateMessage.copy(),true);
        if(cancelPeer!=null) {
            Peer target=cancelPeer;
            try {fileWorkers.execute(()->{try {sendReleaseFiles(target,"cancel:"+packet);}catch(IOException|RuntimeException ignored){/* 拉取的 REVOKED 终态兜底通知丢失。 */}});}catch(RejectedExecutionException ignored){ }
        }
        return true;
    }
    public void cancelUndeliveredOutgoingForPeer(String peerId)throws IOException { cancelUndeliveredOutgoingForPeer(peerId,Collections.emptyList()); }
    public void cancelUndeliveredOutgoingForPeer(String peerId,Collection<Long> unconfirmedPackets)throws IOException {
        List<Map.Entry<Long,String>> messages;
        synchronized(gate){Map<Long,String> unique=new LinkedHashMap<>();for(Pending item:pending.values())if(item.peer.id().equals(peerId)&&!item.canceled&&!item.delivered)unique.put(item.message.number,item.message.state);for(Offer offer:offers.values()){String state=offer.message==null?offer.storedMessageState:offer.message.state;if(offer.peer.id().equals(peerId)&&!offer.canceled&&isUnconfirmedState(state))unique.putIfAbsent(offer.packet,state);}if(unconfirmedPackets!=null)for(Long packet:unconfirmedPackets)if(packet!=null)unique.putIfAbsent(packet,"未确认送达");messages=new ArrayList<>(unique.entrySet());}
        for(Map.Entry<Long,String> entry:messages)cancelUndeliveredOutgoing(peerId,entry.getKey(),entry.getValue(),false);
    }
    private void cancelReverseRequests(){transportEpoch++;for(ReverseRequest request:reverseRequests.values())request.ready.countDown();reverseRequests.clear();}
    private final Set<Socket> connections=new HashSet<>();
    private final Set<Socket> probeSockets=new HashSet<>();
    private final java.util.function.Consumer<String> transportRevoked=fingerprint->{ synchronized(gate){ for(Socket socket:connections)closeSocket(socket); for(Socket socket:probeSockets)closeSocket(socket); cancelReverseRequests(); } };
    private final Set<InetSocketAddress> pendingTcpTargets=new LinkedHashSet<>();
    private final Set<InetSocketAddress> activeTcpProbes=new HashSet<>();
    private final AtomicBoolean tcpFallbackScheduled=new AtomicBoolean();
    private final AtomicLong sequence=new AtomicLong(new SecureRandom().nextInt(0x3fffffff));
    private final ScheduledExecutorService timer=Executors.newSingleThreadScheduledExecutor();
    private final ExecutorService fileWorkers=Executors.newFixedThreadPool(8);
    private final ExecutorService probeWorkers=Executors.newFixedThreadPool(4,r->{Thread t=new Thread(r,"feiq-probe");t.setDaemon(true);return t;});
    private final Listener listener;
    private final boolean loopback;
    private final int port;
    private final String login, host;
    private String deviceId;
    private SecureIdentity security;
    public interface Source {
        String key();String name();long size();long modified();InputStream open(long offset)throws IOException;
    }
    public interface SourceResolver { Source resolve(String key)throws IOException; }
    private SourceResolver sourceResolver;
    public void setSourceResolver(SourceResolver resolver){if(started)throw new IllegalStateException("需在启动前设置文件来源");sourceResolver=resolver;}
    private String hashSource(Source source)throws IOException {try(InputStream input=source.open(0)){java.security.MessageDigest hash=java.security.MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))>=0)hash.update(buffer,0,count);StringBuilder result=new StringBuilder();for(byte b:hash.digest())result.append(String.format(Locale.ROOT,"%02x",b&255));return result.toString();}catch(java.security.NoSuchAlgorithmException e){throw new IllegalStateException(e);}}
    private File dataRoot;
    private static final Object storedOffersGate=new Object();
    private static final int OFFER_STATES_MARKER=0x46515331;
    private static final class StoredOfferState {
        final String state,messageState;
        final int percent;
        StoredOfferState(String state,int percent,String messageState){this.state=state;this.percent=percent;this.messageState=messageState;}
    }
    private static final class StoredOfferSpan {
        final int start,length;
        final long packet,file;
        final String peer;
        StoredOfferSpan(int start,int length,long packet,long file,String peer){this.start=start;this.length=length;this.packet=packet;this.file=file;this.peer=peer;}
    }
    public boolean allowLegacyProtocol=true;
    public SecureIdentity security(){return security;}
    public void configureSecurity(File root,SecureIdentity.Confirmation confirmation)throws IOException{configureSecurity(root,new SecureIdentity(root),confirmation);}
    public void configureSecurity(File root,SecureIdentity identity,SecureIdentity.Confirmation confirmation)throws IOException{if(started)throw new IllegalStateException("安全身份必须在启动前配置");dataRoot=root;security=identity;security.confirmation=confirmation;security.observeRevocation(transportRevoked);restoreOffers();}
    public void forgetSecurity(Peer peer)throws IOException{try{security.forget(peer.deviceId);}finally{synchronized(gate){for(Pending item:new ArrayList<>(pending.values()))if(Objects.equals(item.deviceId,peer.deviceId)){pending.remove(item.message.number);item.message.state="设备信任已撤销，未确认送达";listener.message(item.peer,item.message.copy(),true);}offers.entrySet().removeIf(e->Objects.equals(e.getValue().deviceId,peer.deviceId));for(Socket socket:connections)socket.close();saveOffers();}}}
    private void saveOffers()throws IOException {saveOffers(offers);}
    private void saveOffers(Map<String,Offer> snapshot)throws IOException {
        if(dataRoot==null)return;
        synchronized(gate){ByteArrayOutputStream buffer=new ByteArrayOutputStream();try(DataOutputStream out=new DataOutputStream(buffer)) {
            if(closed)return;
            out.writeInt(2);List<Offer> saved=new ArrayList<>();for(Offer o:snapshot.values())if(o.fingerprint!=null)saved.add(o);out.writeInt(saved.size());
            for(Offer o:saved){out.writeLong(o.packet);out.writeInt((int)o.id);out.writeUTF(o.source==null?o.file.getCanonicalPath():"uri:"+o.source.key());out.writeLong(o.size);out.writeLong(o.modified);out.writeLong(System.currentTimeMillis()+TimeUnit.NANOSECONDS.toMillis(o.expires-System.nanoTime()));out.writeUTF(o.deviceId);out.writeUTF(o.fingerprint);out.writeUTF(o.sha256);out.writeUTF(o.peer.endpoint.getAddress().getHostAddress()+":"+o.peer.endpoint.getPort());out.writeUTF(o.peer.login);out.writeUTF(o.peer.host);out.writeUTF(o.peer.name);out.writeUTF(o.peer.group);out.writeUTF(o.peer.id());}
            out.writeInt(OFFER_STATES_MARKER);out.writeInt(saved.size());
            for(Offer offer:saved){out.writeLong(offer.packet);out.writeInt((int)offer.id);out.writeUTF(offer.state==null?"":offer.state);out.writeInt(offer.percent);String state=offer.message==null?offer.storedMessageState:offer.message.state;out.writeUTF(state==null?"":state);}
        }synchronized(storedOffersGate){SecureIdentity.atomic(new File(dataRoot,"transfer-offers.dat"),buffer.toByteArray());}}
    }
    private static Map<String,StoredOfferState> readOfferStates(DataInputStream input)throws IOException {
        Map<String,StoredOfferState> states=new HashMap<>();
        if(input.available()==0)return states;
        if(input.readInt()!=OFFER_STATES_MARKER)throw new IOException("文件任务状态格式无效");
        int count=input.readInt();if(count<0||count>1000)throw new IOException("文件任务状态数量无效");
        for(int index=0;index<count;index++){long packet=input.readLong();int file=input.readInt();String state=input.readUTF();int percent=input.readInt();String messageState=input.readUTF();if(percent<0||percent>100)throw new IOException("文件任务进度无效");states.put(packet+":"+file,new StoredOfferState(state,percent,messageState));}
        if(input.read()!=-1)throw new IOException("文件任务状态含有多余数据");
        return states;
    }
    public static void cancelStoredOffers(File root,String peerId,Collection<Long> packets)throws IOException {
        if(packets.isEmpty())return;
        File saved=new File(root,"transfer-offers.dat");Set<Long> selectedPackets=new HashSet<>(packets);
        synchronized(storedOffersGate){
            if(!saved.isFile())return;if(saved.length()>8*1024*1024)throw new IOException("文件任务记录过大");
            byte[] bytes=Files.readAllBytes(saved.toPath());List<StoredOfferSpan> spans=new ArrayList<>();int format,count,metadataStart;Map<String,StoredOfferState> states;
            try(DataInputStream input=new DataInputStream(new ByteArrayInputStream(bytes))){
                format=input.readInt();if(format!=1&&format!=2)throw new IOException("文件任务版本不支持");count=input.readInt();if(count<0||count>1000)throw new IOException("文件任务数量无效");
                for(int index=0;index<count;index++){
                    int start=bytes.length-input.available();long packet=input.readLong();int file=input.readInt();input.readUTF();input.readLong();input.readLong();input.readLong();
                    for(int field=0;field<8;field++)input.readUTF();String peer=input.readUTF();
                    spans.add(new StoredOfferSpan(start,bytes.length-input.available()-start,packet,file,peer));
                }
                metadataStart=bytes.length-input.available();states=readOfferStates(input);
            }
            spans.removeIf(span->{StoredOfferState state=states.get(span.packet+":"+span.file);return span.peer.equals(peerId)&&selectedPackets.contains(span.packet)&&(state==null||!isCompletedState(state.state)&&!"已送达".equals(state.messageState));});
            if(spans.size()==count)return;
            ByteArrayOutputStream buffer=new ByteArrayOutputStream();
            try(DataOutputStream output=new DataOutputStream(buffer)){
                output.writeInt(format);output.writeInt(spans.size());for(StoredOfferSpan span:spans)output.write(bytes,span.start,span.length);
                if(metadataStart<bytes.length){
                    output.writeInt(OFFER_STATES_MARKER);output.writeInt((int)spans.stream().filter(span->states.containsKey(span.packet+":"+span.file)).count());
                    for(StoredOfferSpan span:spans){
                        StoredOfferState state=states.get(span.packet+":"+span.file);if(state==null)continue;
                        output.writeLong(span.packet);output.writeInt((int)span.file);output.writeUTF(state.state);output.writeInt(state.percent);output.writeUTF(state.messageState);
                    }
                }
            }
            SecureIdentity.atomic(saved,buffer.toByteArray());
        }
    }
    public void restoreOutgoingStates(Map<String,String> states)throws IOException {
        boolean restored=false;
        synchronized(gate){for(Offer offer:offers.values()){String state=states.get(offer.peer.id()+"/"+offer.packet+"/"+offer.id+"/true");if((offer.state==null||offer.state.isEmpty())&&state!=null){offer.state=state;if(isCompletedState(state))offer.percent=100;restored=true;}}if(restored)saveOffers();}
    }
    private void restoreOffers()throws IOException {
        File saved=new File(dataRoot,"transfer-offers.dat");byte[] bytes;
        synchronized(storedOffersGate){if(!saved.isFile())return;if(saved.length()>8*1024*1024)throw new IOException("文件任务记录过大");bytes=Files.readAllBytes(saved.toPath());}
        try(DataInputStream in=new DataInputStream(new ByteArrayInputStream(bytes))){int format=in.readInt();if(format!=1&&format!=2)throw new IOException("文件任务版本不支持");int count=in.readInt();if(count<0||count>1000)throw new IOException("文件任务数量无效");
            for(int i=0;i<count;i++){Offer o=new Offer();o.packet=in.readLong();o.id=in.readInt();String origin=in.readUTF();o.size=in.readLong();o.modified=in.readLong();long until=in.readLong();o.deviceId=in.readUTF();o.fingerprint=in.readUTF();o.sha256=in.readUTF();o.peer=new Peer(endpoint(in.readUTF()),in.readUTF(),in.readUTF(),in.readUTF(),in.readUTF());o.peer.conversationId=in.readUTF();o.peer.deviceId=o.deviceId;o.peer.fingerprint=o.fingerprint;o.address=o.peer.endpoint.getAddress();
                if(until<=System.currentTimeMillis()||!security.isTrusted(o.deviceId,o.fingerprint)||!SecureIdentity.validFingerprint(o.sha256))continue;
                try {
                    if(format==2&&origin.startsWith("uri:")){if(sourceResolver==null)continue;o.source=sourceResolver.resolve(origin.substring(4));}else o.file=new File(origin);
                    if(!sourceUnchanged(o))throw new IOException("历史文件已改变或删除");
                }catch(IOException e){
                    // 历史附件失效只更新对应卡片，不能在每次启动时弹错或阻止其他任务恢复。
                    listener.fileProgress(o.peer,o.packet,o.id,0,"文件已删除或不可用",0);continue;
                }
                o.expires=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(Math.min(TimeUnit.DAYS.toMillis(7),until-System.currentTimeMillis()));offers.put(o.packet+":"+o.id,o);sequence.accumulateAndGet(o.packet,Math::max);
            }
            Map<String,StoredOfferState> states=readOfferStates(in);
            for(Map.Entry<String,StoredOfferState> entry:states.entrySet()){Offer offer=offers.get(entry.getKey());if(offer!=null){StoredOfferState state=entry.getValue();offer.state=state.state;offer.percent=state.percent;offer.storedMessageState=state.messageState;}}
        }
    }
    private volatile String avatar="default";
    public void setAvatar(String value) {String normalized=Protocol.normalizeAvatar(value);if(normalized==null)throw new IllegalArgumentException("头像数据无效");avatar=normalized;}
    public void setDeviceId(String value) {
        if(started)throw new IllegalStateException("需在启动前设置身份");
        deviceId=Protocol.normalizeDeviceId(value);
    }
    public void rememberPeers(Collection<Peer> saved) {
        synchronized(gate) { for(Peer peer:saved) if(peers.size()<1000 && (!loopback || peer.endpoint.getAddress().isLoopbackAddress())) { peer.conversationId=peer.id();peers.put(peer.id(),peer); } }
    }
    public volatile String nickname, group;
    private volatile DatagramSocket udp;
    private volatile ServerSocket tcp;
    private volatile boolean closed,started,networkAvailable;
    private boolean cleanupStarted;
    private volatile String networkStatus="网络已断开，等待 Wi-Fi、热点或以太网恢复";
    private Object networkKey;
    private volatile long refreshAt;
    private long lastMaintenance=System.currentTimeMillis();
    public boolean networkAvailable() { return !closed && networkAvailable; }
    public String networkStatus() { return networkStatus; }
    public String networkDescription(){try{return loopback?"回环测试":socketBinding==null?"未选择物理网络":socketBinding.description();}catch(IOException error){return error.getMessage();}}
    public int port() { return port; }

    public LanNode(int port,boolean loopback,String login,String host,String nickname,String group,Listener listener) {
        if (port<1024 || port>65535) throw new IllegalArgumentException("端口须为 1024–65535");
        this.port=port; this.loopback=loopback; this.login=Protocol.field(login); this.host=Protocol.field(host);
        this.nickname=nickname; this.group=group; this.listener=listener;
    }
    public void start() throws IOException {
        if (closed || started) throw new IOException("服务已启动或关闭");
        started=true;
        try {
            refresh();
            timer.scheduleWithFixedDelay(this::tick,1,1,TimeUnit.SECONDS);
        } catch (IOException | RuntimeException e) { close(); throw e; }
    }
    private void ensureListener() throws IOException {
        synchronized(gate) {
            if(closed||tcp!=null)return;
            ServerSocket next=new ServerSocket();
            tcp=next;
            try {
                if(closed){next.close();tcp=null;return;}
                // Android/Linux 会保留已关闭 TCP 的 TIME_WAIT；允许重开原端口，不启用 UDP 端口共享。
                next.setReuseAddress(true);
                next.bind(new InetSocketAddress(InetAddress.getByName(loopback?"127.0.0.1":"0.0.0.0"),port),4);
                if(closed){next.close();tcp=null;return;}
            }catch(IOException|RuntimeException error){tcp=null;next.close();throw error;}
            Thread accept=new Thread(this::acceptLoop,"feiq-files");accept.setDaemon(true);accept.start();
        }
    }
    private void networkState(boolean available,String reason) {
        if(closed || (networkAvailable==available && networkStatus.equals(reason)))return;
        networkAvailable=available;networkStatus=reason;listener.networkStateChanged(available,reason);
    }
    private void switchNetwork(Object key) throws IOException {
        synchronized(gate) {
            if(closed || (Objects.equals(key,networkKey) && (key==null || udp!=null)))return;
            if(security!=null)security.cancelConfirmations();
            synchronized(udpSendGate) {
                DatagramSocket old=udp;udp=null;networkKey=null;
                if(old!=null)old.close();
                // 旧网的在线证据、重发和文件授权不能带到新网；已落盘的接收断点保留。
                for(Peer peer:peers.values()){peer.online=false;peer.misses=0;}
                for(Pending p:pending.values()){p.message.state="网络已切换，未确认送达";listener.message(p.peer,p.message.copy(),true);}pending.clear();
                offers.entrySet().removeIf(e->{if(e.getValue().fingerprint!=null)return false;endOffer(e.getValue(),"网络已切换，请重新发送邀请");return true;});
                for(Socket socket:connections)try{socket.close();}catch(IOException ignored){ }
                for(Socket socket:probeSockets)try{socket.close();}catch(IOException ignored){ }
                for(PendingDownload pd:pendingDownloads){pd.ready.countDown();pd.done.countDown();}pendingDownloads.clear();
                cancelReverseRequests();
                pendingTcpTargets.clear();
                listener.networkReset();
                listener.peersChanged();
                networkState(false,"网络已断开，等待 Wi-Fi、热点或以太网恢复");
                if(key==null)return;
                DatagramSocket next=new DatagramSocket(null);
                // 先登记正在绑定的 socket，服务销毁时也能立即释放它；发送仍由 udpSendGate 串行。
                udp=next;
                try {
                    if(closed){next.close();udp=null;return;}
                    next.setReuseAddress(false);next.setBroadcast(!loopback);
                    next.bind(new InetSocketAddress(InetAddress.getByName(loopback?"127.0.0.1":"0.0.0.0"),port));
                    // Network.bindSocket 会访问 socket 监视器，必须在 receive 开始前完成，不能重绑阻塞中的旧 socket。
                    if(!loopback && socketBinding!=null)try{socketBinding.bind(next);}
                    catch(IOException e){udp=null;next.close();networkState(false,e.getMessage());return;}
                    if(closed){next.close();udp=null;return;}
                    networkKey=key;
                    Thread rx=new Thread(()->receiveLoop(next),"feiq-udp");rx.setDaemon(true);rx.start();
                } catch(IOException | RuntimeException e) {udp=null;next.close();throw e;}
            }
        }
    }
    public List<Peer> peers() { synchronized (gate) { return new ArrayList<>(peers.values()); } }
    public static InetSocketAddress endpoint(String text) throws IOException {
        String[] parts=text.trim().split(":",-1); if (parts.length>2) throw new IOException("请输入 IPv4 地址，可附加 :端口");
        String[] octets=parts[0].split("\\.",-1); if (octets.length!=4) throw new IOException("请输入完整 IPv4 地址");
        byte[] ip=new byte[4];
        try {
            for (int i=0;i<4;i++) { if (!octets[i].matches("[0-9]{1,3}")) throw new NumberFormatException(); int n=Integer.parseInt(octets[i]); if (n>255) throw new NumberFormatException(); ip[i]=(byte)n; }
            int p=parts.length==2?Integer.parseInt(parts[1]):2425; if (p<1024 || p>65535) throw new NumberFormatException();
            InetAddress address=InetAddress.getByAddress(ip);
            if (address.isAnyLocalAddress() || address.isMulticastAddress() || (ip[0]&255)==255) throw new NumberFormatException();
            return new InetSocketAddress(address,p);
        } catch (IllegalArgumentException e) { throw new IOException("IPv4 地址或端口无效"); }
    }
    // TCP 请求仍以首个 NUL 结束，身份扩展只用于 UDP，兼容旧端文件服务。
    private byte[] encode(int command,String body,String extra) {int mode=command&255;return Protocol.encode(sequence.incrementAndGet(),login,host,command|Protocol.UTF8,body,extra,mode==Protocol.GET_FILE?null:deviceId,mode==Protocol.ENTRY||mode==Protocol.ANSWER||mode==Protocol.ABSENCE?avatar:null,mode==Protocol.GET_FILE||security==null?null:security.fingerprint);}
    private byte[] withPort(byte[] bytes) {
        byte[] suffix=("FQPORT="+port+"\0").getBytes(java.nio.charset.StandardCharsets.US_ASCII);
        byte[] wire=Arrays.copyOf(bytes,bytes.length+suffix.length);
        System.arraycopy(suffix,0,wire,bytes.length,suffix.length);
        return wire;
    }
    private void send(byte[] bytes,InetSocketAddress to) throws IOException {
        if (!(to.getAddress() instanceof Inet4Address) || (loopback && !to.getAddress().isLoopbackAddress())) throw new IOException("无效的目标地址");
        synchronized(udpSendGate) {
            if(closed || udp==null)throw new IOException(networkStatus);
            if(!loopback && socketBinding!=null && !socketBinding.allows(to.getAddress()))throw new IOException("目标不在当前局域网内，请重新发现设备");
            byte[] wire=withPort(bytes);
            if(!loopback&&socketBinding!=null)socketBinding.send(udp,wire,to);else udp.send(new DatagramPacket(wire,wire.length,to));
        }
    }
    private boolean routeAllowed(InetAddress address) {
        if(!(address instanceof Inet4Address))return false;
        if(loopback)return address.isLoopbackAddress();
        if(socketBinding==null)return true;
        try{return socketBinding.allows(address);}catch(IOException e){return false;}
    }
    public void probe(InetSocketAddress to) throws IOException {
        probe(to,false);
    }
    private void probe(InetSocketAddress to,boolean broadcast) throws IOException {
        IOException udpFailure=null;
        try {
            send(encode(Protocol.ENTRY|Protocol.CAP_UTF8|Protocol.FILE,Protocol.field(nickname),Protocol.field(group)),to);
        } catch(IOException error) {udpFailure=error;}
        if(!broadcast && started && heartbeatRetry>=TimeUnit.SECONDS.toNanos(1) && routeAllowed(to.getAddress())) {
            synchronized(gate) {
                if(!closed && udp!=null && (pendingTcpTargets.size()<256 || pendingTcpTargets.contains(to))) {
                    pendingTcpTargets.add(to);
                    scheduleTcpFallback();
                    return;
                }
            }
        }
        if(udpFailure!=null)throw udpFailure;
    }
    public void rememberEndpoints(Collection<InetSocketAddress> endpoints) {
        synchronized(gate) {
            for(InetSocketAddress endpoint:endpoints) {
                if(remembered.size()>=2000) break;
                if(endpoint!=null && endpoint.getAddress() instanceof Inet4Address && endpoint.getPort()>=1024 && (!loopback || endpoint.getAddress().isLoopbackAddress())) remembered.add(endpoint);
            }
        }
    }
    public void refresh() throws IOException {
        revalidate();fastRefreshUntil=System.nanoTime()+TimeUnit.SECONDS.toNanos(10);refreshNow();
    }
    private synchronized void refreshNow() throws IOException {
        if(closed || !started)return;
        refreshAt=System.nanoTime();
        Object key=this;
        if(!loopback && socketBinding!=null) {
            socketBinding.changed();
            try {key=socketBinding.networkKey();}
            catch(IOException e){switchNetwork(null);networkState(false,e.getMessage());return;}
        }
        String transport="TCP";
        try { ensureListener();transport="UDP";switchNetwork(key); }
        catch(IOException e) {
            switchNetwork(null);
            fastRefreshUntil=System.nanoTime()+TimeUnit.SECONDS.toNanos(10);
            // 升级后的端口释放、切网和 VPN 初始化可能短暂失败，保留服务等待恢复。
            networkState(false,(e instanceof BindException?"本地 "+transport+" 端口 "+port+" 暂不可用，正在重试：":"局域网暂不可用，正在重试：")+e.getMessage());
            return;
        }
        if(key==null){networkState(false,"网络已断开，等待 Wi-Fi、热点或以太网恢复");return;}
        if(udp==null)return;
        Set<InetAddress> addresses=new HashSet<>();
        if (!loopback) {
            Collection<InetAddress> physical;
            try{physical=socketBinding==null?null:socketBinding.broadcasts();}
            catch(IOException e){switchNetwork(null);networkState(false,e.getMessage());return;}
            if(physical==null)throw new IOException("未配置物理局域网，已停止广播");
            addresses.addAll(physical);
        }
        boolean broadcastSent=loopback;
        for (InetAddress address:addresses) {
            try {probe(new InetSocketAddress(address,port),true);broadcastSent=true;}
            catch(IOException ignored) { }
        }
        Set<InetSocketAddress> targets;
        synchronized(gate) {
            targets=new LinkedHashSet<>(remembered);
            // 发现补包不改心跳计时；连续刷新既不能推迟判离线，也不能重复累计尚未超时的探测。
            for(Peer peer:peers.values())targets.add(peer.endpoint);
        }
        for(InetSocketAddress target:targets) {
            try {probe(target);} catch(IOException ignored) { }
        }
        networkState(true,broadcastSent?"":"广播受限，可手动添加 IP");
    }
    private boolean isSelf(Protocol.Packet packet,InetSocketAddress from) {
        return (deviceId!=null&&deviceId.equals(packet.deviceId))||(packet.login.equals(login)&&packet.host.equals(host)&&(packet.replyPort>0?packet.replyPort:from.getPort())==port);
    }
    private void receiveLoop(DatagramSocket source) {
        byte[] buffer=new byte[60001];
        while (!closed && !source.isClosed()) try {
            DatagramPacket datagram=new DatagramPacket(buffer,buffer.length); source.receive(datagram);
            if(!loopback && socketBinding!=null && !socketBinding.allows(datagram.getAddress()))continue;
            Protocol.Packet packet=Protocol.parse(Arrays.copyOf(datagram.getData(),datagram.getLength()));
            if (packet==null || !(datagram.getAddress() instanceof Inet4Address)) continue;
            if (isSelf(packet,new InetSocketAddress(datagram.getAddress(),datagram.getPort()))) continue;
            synchronized(gate) {if(source==udp)handle(packet,new InetSocketAddress(datagram.getAddress(),datagram.getPort()));}
        } catch (IOException e) { if (!closed && source==udp) { networkChanged(); try { Thread.sleep(100); } catch (InterruptedException stop) { return; } } }
          catch (RuntimeException e) { if (!closed) listener.error("消息处理失败："+e.getMessage()); }
    }
    private Peer upsertPeer(Protocol.Packet p,InetSocketAddress from) {
        synchronized (gate) {
            if(security!=null&&p.deviceId!=null){String pin=security.trustedFingerprint(p.deviceId);if(pin!=null&&!pin.equals(p.fingerprint))return null;}
            Peer peer=new Peer(from,p.login,p.host,p.login,"我的局域网");
            Peer existing=null;
            if(p.deviceId!=null) for(Peer candidate:peers.values()) if(p.deviceId.equals(candidate.deviceId)) { existing=candidate;break; }
            if(existing==null) for(Peer candidate:peers.values())
                if(candidate.endpoint.equals(from) && candidate.login.equals(p.login) && (p.deviceId==null || candidate.deviceId==null || candidate.deviceId.equals(p.deviceId))) { existing=candidate;break; }
            // 地址兼容只用于旧联系人，缺少身份的明文不能进入已配对会话。
            if(security!=null&&existing!=null){String pin=security.trustedFingerprint(existing.deviceId);if(pin!=null&&(!Objects.equals(p.deviceId,existing.deviceId)||!pin.equals(p.fingerprint)))return null;}
            peer.deviceId=p.deviceId;
            if (existing!=null) peer=existing;
            else { if (peers.size()>=1000) return null; peers.put(peer.id(),peer); }
            // 旧会话保留本地主键，历史、草稿和已下载文件路径无需破坏性迁移。
            peer.conversationId=peer.id(); if(p.deviceId!=null)peer.deviceId=p.deviceId;
            if(p.fingerprint!=null)peer.fingerprint=p.fingerprint;
            peer.endpoint=from;peer.login=p.login;peer.host=p.host;
            if (p.mode()==Protocol.ENTRY || p.mode()==Protocol.ANSWER || p.mode()==Protocol.ABSENCE) {
                if(p.avatar!=null) peer.avatar=p.avatar;
                peer.name=p.body.trim().isEmpty()?p.login:Protocol.prefix(p.body,128);
                if(!p.extra.trim().isEmpty()||existing==null)peer.group=Protocol.prefix(p.extra,128);
            }
            peer.utf8|=(p.command&(Protocol.UTF8|Protocol.CAP_UTF8))!=0; peer.online=p.mode()!=Protocol.EXIT; peer.lastSeen=System.nanoTime(); peer.misses=0;
            listener.peersChanged();
            return peer;
        }
    }
    private void handle(Protocol.Packet p,InetSocketAddress from) throws IOException {
        handle(p,p.replyPort>0?new InetSocketAddress(from.getAddress(),p.replyPort):from,false);
    }
    private void handle(Protocol.Packet p,InetSocketAddress from,boolean secure) throws IOException {
        synchronized (gate) {
            if (p.mode()==Protocol.ACK) {
                try {
                    Pending item=pending.get(Long.parseLong(p.body));
                    if (item!=null && !item.canceled && (secure||item.fingerprint==null) && item.endpoint.equals(from) && item.login.equals(p.login) && (p.deviceId==null || item.deviceId==null || item.deviceId.equals(p.deviceId))) {
                        pending.remove(item.message.number);item.delivered=true;item.message.state="已送达";for(Offer offer:offers.values())if(offer.packet==item.message.number&&offer.peer.id().equals(item.peer.id()))offer.message=item.message;listener.message(item.peer,item.message.copy(),true);if(!item.message.files.isEmpty())try{saveOffers();}catch(IOException ignored){}
                    }
                } catch (NumberFormatException ignored) { } return;
            }
            if (p.mode()==Protocol.RELEASE) {
                if(p.body.startsWith("cancel:")) {
                    long packet;
                    try {String value=p.body.substring(7);if(value.isEmpty()||!value.chars().allMatch(c->c>='0'&&c<='9'))return;packet=Long.parseLong(value);if(packet<=0)return;}
                    catch(NumberFormatException ignored){return;}
                    Peer sender=null;
                    if(secure) {
                        if(security==null||!security.isTrusted(p.deviceId,p.fingerprint))return;
                        for(Peer peer:peers.values())if(Objects.equals(peer.deviceId,p.deviceId)&&Objects.equals(peer.fingerprint,p.fingerprint)){sender=peer;break;}
                    } else {
                        if(p.fingerprint!=null||security!=null&&(!allowLegacyProtocol||security.trustedFingerprint(p.deviceId)!=null))return;
                        for(Peer peer:peers.values())if(peer.endpoint.equals(from)&&peer.login.equals(p.login)&&peer.host.equals(p.host)&&(p.deviceId==null||Objects.equals(peer.deviceId,p.deviceId))&&peer.fingerprint==null&&(security==null||security.trustedFingerprint(peer.deviceId)==null)){sender=peer;break;}
                    }
                    if(sender!=null)listener.offerCanceled(sender,packet);
                    return;
                }
                try { String prefix=Long.parseLong(p.body)+":"; offers.entrySet().removeIf(e->{Offer o=e.getValue();boolean remove=e.getKey().startsWith(prefix)&&(secure?Objects.equals(o.deviceId,p.deviceId)&&Objects.equals(o.fingerprint,p.fingerprint):o.fingerprint==null&&o.address.equals(from.getAddress()));if(remove)endOffer(o,"对方已拒绝");return remove;}); }
                catch (NumberFormatException ignored) { } saveOffers();return;
            }
            if (p.mode()!=Protocol.ENTRY && p.mode()!=Protocol.ANSWER && p.mode()!=Protocol.ABSENCE && p.mode()!=Protocol.EXIT && p.mode()!=Protocol.MESSAGE) return;
            if(!secure&&p.mode()==Protocol.MESSAGE&&security!=null&&(!allowLegacyProtocol||p.fingerprint!=null||security.trustedFingerprint(p.deviceId)!=null))return;
            Peer peer=upsertPeer(p,from);
            if(peer==null)return;
            if (p.mode()==Protocol.ENTRY) {
                try { send(encode(Protocol.ANSWER|Protocol.FILE|Protocol.CAP_UTF8|(peer.utf8?Protocol.UTF8:0),Protocol.field(nickname),Protocol.field(group)),from); } catch (IOException ignored) { }
                if (security!=null && peer.deviceId!=null && peer.fingerprint!=null && security.isTrusted(peer.deviceId,peer.fingerprint)) scheduleSync(peer);
            }
            if (p.mode()!=Protocol.MESSAGE) return;
            if (!secure&&(p.command&Protocol.CHECK)!=0) try { send(encode(Protocol.ACK,Long.toString(p.number),null),from); } catch (IOException ignored) { }
            String key=peer.id()+":"+p.number;
            if (seen.containsKey(key)) return;
            if (seen.size()>=8192) seen.remove(seen.keySet().iterator().next());
            seen.put(key,System.nanoTime());
            Message m=new Message(); m.number=p.number; m.time=System.currentTimeMillis(); m.text=p.body; m.state="已收到";
            if ((p.command&Protocol.FILE)!=0) m.files=Protocol.parseFiles(p.extra);
            listener.message(peer,m,false);
        }
    }
    public Message sendMessage(Peer peer,String text,List<File> paths) throws IOException {
        return sendMessage(peer,text,paths,null);
    }
    public Message sendFiles(Peer peer,List<File> paths,Map<String,String> relatives)throws IOException{return sendMessage(peer,"",paths,null,relatives);}
    public Message sendSources(Peer peer,List<Source> sources)throws IOException {
        if(security==null||peer.fingerprint==null||sources.isEmpty()||sources.size()>20)throw new IOException("直接读取文件需要新版加密协议，每批最多 20 项");
        Pending item=new Pending(peer);Message m=new Message();m.number=sequence.incrementAndGet();m.time=System.currentTimeMillis();m.outgoing=true;m.text="";m.state="等待确认";Map<String,Offer> additions=new HashMap<>();
        for(Source source:sources){if(!Protocol.safeName(source.name())||source.size()<0)throw new IOException("文件元数据无效");int id=m.files.size();String hash=hashSource(source);Protocol.Attachment file=new Protocol.Attachment(id,source.name(),source.size(),source.modified()/1000);file.sha256=hash;m.files.add(file);m.localPaths.add("uri:"+source.key());Offer o=new Offer();o.source=source;o.size=source.size();o.modified=source.modified();o.expires=System.nanoTime()+TimeUnit.DAYS.toNanos(7);o.peer=peer;o.message=m;o.packet=m.number;o.id=id;o.deviceId=item.deviceId;o.fingerprint=item.fingerprint;o.sha256=hash;o.address=item.endpoint.getAddress();additions.put(m.number+":"+id,o);}
        item.message=m;item.bytes=Protocol.encode(m.number,login,host,Protocol.MESSAGE|Protocol.CHECK|Protocol.UTF8|Protocol.FILE,"",Protocol.files(m.files),deviceId,null,security.fingerprint);item.last=System.nanoTime();
        synchronized(gate){if(closed||udp==null||offers.size()+sources.size()>1000||pending.size()>=256)throw new IOException("未连接或待处理任务过多");Map<String,Offer> next=new HashMap<>(offers);next.putAll(additions);saveOffers(next);offers.putAll(additions);pending.put(m.number,item);listener.message(peer,m.copy(),false);fileWorkers.execute(()->sendSecurePending(item));}return m.copy();
    }
    private boolean sourceUnchanged(Offer o)throws IOException {if(o.source==null)return o.file.isFile()&&o.file.length()==o.size&&o.file.lastModified()==o.modified;Source current=sourceResolver==null?o.source:sourceResolver.resolve(o.source.key());return current.size()==o.size&&current.modified()==o.modified;}
    public Message retryMessage(Peer peer,Message original) throws IOException {
        synchronized(gate) {
            if(!original.outgoing || !(original.state.contains("未确认") || original.state.equals("发送失败")) || pending.containsKey(original.number))throw new IOException("此消息正在发送或已送达");
            if(!original.files.isEmpty())throw new IOException("请重新选择原文件发送");
            return sendMessage(peer,original.text,Collections.emptyList(),original);
        }
    }
    private Message sendMessage(Peer peer,String text,List<File> paths,Message original) throws IOException {
        return sendMessage(peer,text,paths,original,Collections.emptyMap());
    }
    private Message sendMessage(Peer peer,String text,List<File> paths,Message original,Map<String,String> relatives) throws IOException {
        if (text==null) text="";
        if (text.indexOf('\0')>=0 || (text.trim().isEmpty() && paths.isEmpty())) throw new IOException("请输入消息或选择文件");
        if (paths.size()>20) throw new IOException("每次最多发送 20 个文件");
        Pending item;boolean utf8;
        // 发现报文可以更新联系人，但已发内容与授权必须固定在本次发送的接收地址。
        synchronized(gate){item=new Pending(peer);utf8=peer.utf8;}
        if(security!=null&&item.fingerprint==null&&(!allowLegacyProtocol||security.trustedFingerprint(item.deviceId)!=null))throw new IOException("对方使用旧版明文协议，请升级对方飞Q，或在设置中明确开启兼容模式");
        Message m=new Message(); m.number=sequence.incrementAndGet(); m.time=System.currentTimeMillis(); m.text=text; m.state="等待确认"; m.outgoing=true;
        if(original!=null) { m.number=original.number;m.time=original.time; }
        Map<String,Offer> additions=new HashMap<>();
        for (File file:paths) {
            if (!file.isFile() || !Protocol.safeName(file.getName())) throw new IOException("文件不可读、名称不安全或过长");
            int id=m.files.size(); m.files.add(new Protocol.Attachment(id,file.getName(),file.length(),file.lastModified()/1000));
            String hash=item.fingerprint==null?null:SecureIdentity.hashFile(file);m.files.get(id).sha256=hash;
            String relative=relatives.get(file.getCanonicalPath());if(relative!=null){if(hash==null||!Protocol.safeRelativePath(relative))throw new IOException("文件夹发送需要新版协议及安全路径");m.files.get(id).relativePath=relative;}
            m.localPaths.add(file.getCanonicalPath());
            Offer o=new Offer(); o.file=file; o.size=file.length(); o.modified=file.lastModified(); o.address=item.endpoint.getAddress(); o.expires=System.nanoTime()+TimeUnit.MINUTES.toNanos(30);o.message=m; additions.put(m.number+":"+id,o);
            o.peer=peer; o.packet=m.number; o.id=id;
            o.deviceId=item.deviceId;o.fingerprint=item.fingerprint;o.sha256=hash;
            if(hash!=null)o.expires=System.nanoTime()+TimeUnit.DAYS.toNanos(7);
        }
        byte[] bytes=Protocol.encode(m.number,login,host,Protocol.MESSAGE|Protocol.CHECK|(utf8?Protocol.UTF8:0)|(paths.isEmpty()?0:Protocol.FILE),text,paths.isEmpty()?null:Protocol.files(m.files),deviceId,null,security==null?null:security.fingerprint);
        synchronized (gate) {
            if (closed || udp==null) throw new IOException("尚未连接局域网");
            if (pending.size()>=256 || offers.size()+additions.size()>1000) throw new IOException("待处理任务过多，请稍后再试");
            // 先持久化候选邀请，写入失败时不发布消息，也不留下可自动重试的内容。
            Map<String,Offer> next=new HashMap<>(offers);next.putAll(additions);saveOffers(next);
            item.message=m; item.bytes=bytes; item.last=System.nanoTime(); pending.put(m.number,item); offers.putAll(additions);
            // 先发布记录再发送，避免本机 ACK 先到而被初始“等待确认”覆盖。
            listener.message(peer,m.copy(),original!=null);
            try { if(security!=null&&item.fingerprint!=null)fileWorkers.execute(()->sendSecurePending(item));else send(bytes,item.endpoint); }
            catch (IOException e) {
                pending.remove(m.number);offers.keySet().removeAll(additions.keySet());
                // 首发失败后不能留下重启仍可读取的孤立文件授权。
                saveOffers();
                m.state="发送失败";listener.message(peer,m.copy(),true);throw e;
            }
        }
        return m.copy();
    }
    private void tick() {
        if (closed) return;
        try {
            long now=System.nanoTime(); boolean changed=false; List<InetSocketAddress> probes=new ArrayList<>(); List<Peer> syncTargets=new ArrayList<>();
            long wall=System.currentTimeMillis();if(!loopback&&wall-lastMaintenance>30000){switchNetwork(null);networkDirty.set(true);}lastMaintenance=wall;
            boolean dirty=networkDirty.getAndSet(false);
            long interval=now-fastRefreshUntil<0?TimeUnit.SECONDS.toNanos(1):TimeUnit.SECONDS.toNanos(networkAvailable?30:3);
            if(dirty || now-refreshAt>=interval)refreshNow();
            synchronized (gate) {
                Iterator<Pending> it=pending.values().iterator();
                while (it.hasNext()) {
                    Pending p=it.next(); if (p.canceled||p.sending.get()||p.transmitting.get()||p.delivered||now-p.last<TimeUnit.SECONDS.toNanos(2)) continue;
                    if (p.attempts>=4) { it.remove(); p.message.state="未确认送达，请检查网络后重发";listener.message(p.peer,p.message.copy(),true); }
                    else { p.attempts++; p.last=now; try { if(security!=null&&p.fingerprint!=null)fileWorkers.execute(()->sendSecurePending(p));else if(!p.canceled&&!p.delivered)send(p.bytes,p.endpoint); } catch (IOException ignored) { } }
                }
                seen.entrySet().removeIf(e->now-e.getValue()>TimeUnit.MINUTES.toNanos(10));
                offers.entrySet().removeIf(e->{if(e.getValue().expires>=now)return false;endOffer(e.getValue(),"邀请已过期，请重新发送");return true;});
                // 切网不会发送退出通知；只按实际发出且未获回应的探测判离线，休眠或计时器延后不会误判。
                long verify=verifyFrom;
                for (Peer p:peers.values()) {
                    if (!p.online) {
                        if (networkAvailable && now-p.probed>=heartbeatRetry) { p.probed=now; probes.add(p.endpoint); }
                        continue;
                    }
                    if (security!=null && heartbeatRetry>=TimeUnit.SECONDS.toNanos(1) && p.deviceId!=null && p.fingerprint!=null && security.isTrusted(p.deviceId,p.fingerprint)) syncTargets.add(p);
                    boolean verifying=p.lastSeen-verify<0;
                    if ((!verifying && now-p.lastSeen<heartbeatIdle) || now-p.probed<heartbeatRetry) continue;
                    if (p.misses>=(verifying?verifyMisses:idleMisses)) { p.online=false; p.misses=0; changed=true; }
                    else { p.probed=now; p.misses++; probes.add(p.endpoint); }
                }
            }
            for (InetSocketAddress target:probes) try { probe(target); } catch (IOException ignored) { }
            for (Peer target:syncTargets) scheduleSync(target);
            if (changed) listener.peersChanged();
        } catch (IOException | RuntimeException e) { if (!closed) listener.error("网络维护失败："+e.getMessage()); }
    }
    private void scheduleTcpFallback() {
        if(closed||!started||!tcpFallbackScheduled.compareAndSet(false,true))return;
        try {timer.schedule(this::runTcpFallback,150,TimeUnit.MILLISECONDS);}
        catch(RejectedExecutionException e){tcpFallbackScheduled.set(false);}
    }
    private void runTcpFallback() {
        tcpFallbackScheduled.set(false);
        if(closed||!started)return;
        List<InetSocketAddress> batch=new ArrayList<>();
        synchronized(gate) {
            if(pendingTcpTargets.isEmpty())return;
            for(InetSocketAddress target:pendingTcpTargets) if(activeTcpProbes.add(target))batch.add(target);
            pendingTcpTargets.clear();
        }
        for(InetSocketAddress target:batch) {
            try {
                probeWorkers.execute(()->{
                    try {probeTcp(target);}
                    finally {synchronized(gate){activeTcpProbes.remove(target);}}
                });
            } catch(RejectedExecutionException e) {
                synchronized(gate){activeTcpProbes.remove(target);}
            }
        }
    }
    private void probeTcp(InetSocketAddress target) {
        if(closed||!started)return;
        if(loopback&&!target.getAddress().isLoopbackAddress())return;
        try {if(!loopback&&socketBinding!=null&&!socketBinding.allows(target.getAddress()))return;}
        catch(IOException ignored){return;}
        Peer known=null;
        synchronized(gate) {
            if(closed)return;
            for(Peer candidate:peers.values())if(candidate.endpoint.equals(target)){known=candidate;break;}
            long now=System.nanoTime();
            if(known!=null&&known.online&&known.misses==0&&known.lastSeen-verifyFrom>=0&&now-known.lastSeen<TimeUnit.SECONDS.toNanos(1))return;
        }
        boolean connected=false, discovered=false;
        Socket socket=new Socket(Proxy.NO_PROXY);
        synchronized(gate){if(closed){try{socket.close();}catch(IOException ignored){}return;}probeSockets.add(socket);}
        try(Socket active=socket) {
            if(!loopback&&socketBinding!=null)socketBinding.bind(active,target.getAddress());
            active.connect(target,1200);connected=true;active.setSoTimeout(1500);
            OutputStream out=active.getOutputStream();
            out.write(new byte[]{'F','Q','D','\n'});
            SecureIdentity.writeFrame(out,withPort(encode(Protocol.ENTRY|Protocol.CAP_UTF8|Protocol.FILE,Protocol.field(nickname),Protocol.field(group))));
            RequestInput input=new RequestInput(active);input.begin(1500);
            Protocol.Packet packet=Protocol.parse(SecureIdentity.readFrame(input,60000));
            if(packet!=null&&(packet.mode()==Protocol.ANSWER||packet.mode()==Protocol.ENTRY||packet.mode()==Protocol.ABSENCE)) {
                InetSocketAddress from=new InetSocketAddress(active.getInetAddress(),packet.replyPort>0?packet.replyPort:target.getPort());
                if(!isSelf(packet,from)){upsertPeer(packet,from);discovered=true;}
            }
        } catch(IOException|RuntimeException ignored) { }
        finally {synchronized(gate){probeSockets.remove(socket);}}
        if(connected&&known!=null&&security!=null&&known.fingerprint!=null&&security.isTrusted(known.deviceId,known.fingerprint)) {
            if(!discovered)try {verifyPeer(known);}catch(IOException|RuntimeException ignored){ }
            scheduleSync(known);
        }
    }
    private boolean hasPendingFor(String targetDeviceId,String targetFingerprint) {
        if(targetDeviceId==null||targetFingerprint==null)return false;
        synchronized(gate) {
            for(Pending item:pending.values())if(!item.delivered&&!item.canceled&&targetDeviceId.equals(item.deviceId)&&targetFingerprint.equals(item.fingerprint))return true;
            for(PendingDownload pd:pendingDownloads)if(!pd.claimed.get()&&targetDeviceId.equals(pd.deviceId)&&targetFingerprint.equals(pd.fingerprint))return true;
            return false;
        }
    }
    private void scheduleSync(Peer peer) {
        if(closed||!started||security==null||peer==null||peer.deviceId==null||peer.fingerprint==null)return;
        String key=peer.deviceId;
        synchronized(gate){if(!activeSyncPeers.add(key))return;}
        try {
            probeWorkers.execute(()->{
                try {syncPeer(peer);}
                finally {synchronized(gate){activeSyncPeers.remove(key);}}
            });
        } catch(RejectedExecutionException e) {
            synchronized(gate){activeSyncPeers.remove(key);}
        }
    }
    private void syncPeer(Peer peer) {
        if(closed||!started||security==null||peer.deviceId==null||peer.fingerprint==null)return;
        InetSocketAddress target=peer.endpoint;
        if(loopback&&!target.getAddress().isLoopbackAddress())return;
        try {if(!loopback&&socketBinding!=null&&!socketBinding.allows(target.getAddress()))return;}
        catch(IOException ignored){return;}
        Socket raw=new Socket(Proxy.NO_PROXY);
        synchronized(gate){if(closed){try{raw.close();}catch(IOException ignored){}return;}probeSockets.add(raw);}
        try(Socket active=raw) {
            if(!loopback&&socketBinding!=null)socketBinding.bind(active,target.getAddress());
            active.connect(target,2500);active.setSoTimeout(5000);
            active.getOutputStream().write(new byte[]{'F','Q','2','\n'});
            try(SSLSocket socket=security.client(active,peer.fingerprint)) {
                socket.setSoTimeout(15000);
                Protocol.Packet remote=authenticateSecureClient(socket,peer.deviceId,peer.fingerprint,peer.name,true);
                SecureIdentity.writeText(socket.getOutputStream(),"S\n"+port);
                SecureIdentity.writeFrame(socket.getOutputStream(),secureSyncHello());
                upsertPeer(remote,target);
                while(!closed) {
                    String cmd=SecureIdentity.readText(socket.getInputStream(),1024);
                    if("OK".equals(cmd))break;
                    if("M".equals(cmd)) {
                        Protocol.Packet msg=Protocol.parse(SecureIdentity.readFrame(socket.getInputStream(),60000));
                        if(msg==null||!peer.deviceId.equals(msg.deviceId)||!peer.fingerprint.equals(msg.fingerprint)||(msg.mode()!=Protocol.MESSAGE&&msg.mode()!=Protocol.RELEASE))throw new IOException("同步消息无效");
                        handle(msg,target,true);
                        SecureIdentity.writeText(socket.getOutputStream(),"OK");
                    } else if(cmd.startsWith("D\n")) {
                        String[] f=cmd.substring(2).split(":");
                        if(f.length>=3) {
                            long number=Long.parseLong(f[0],16),id=Long.parseLong(f[1],16),offset=Long.parseLong(f[2],16);
                            try {fileWorkers.execute(()->pushSecureFile(peer,number,id,offset));}catch(RejectedExecutionException ignored){}
                        }
                    } else if(cmd.startsWith("R\n")) {
                        String[] fields=cmd.substring(2).split(":");if(fields.length!=2||Protocol.normalizeDeviceId(fields[0])==null)throw new IOException("回连请求无效");
                        String ticket=fields[0],key=peer.deviceId+":"+ticket;int servicePort=Integer.parseInt(fields[1]),epoch;
                        synchronized(gate){if(activeReverseRequests.size()>=8||!activeReverseRequests.add(key))continue;epoch=transportEpoch;}
                        try{fileWorkers.execute(()->{try{serveReverse(target,servicePort,ticket,peer.deviceId,peer.fingerprint,epoch);}finally{synchronized(gate){activeReverseRequests.remove(key);}}});}
                        catch(RejectedExecutionException e){synchronized(gate){activeReverseRequests.remove(key);}}
                    } else throw new IOException("未知同步响应");
                }
            }
        } catch(IOException|RuntimeException ignored) { }
        finally {synchronized(gate){probeSockets.remove(raw);}}
    }
    private void pushSecureFile(Peer peer,long number,long id,long offset) {
        Offer offer;
        synchronized(gate){offer=offers.get(number+":"+id);if(offer==null||offer.canceled)return;}
        if(offer==null||offer.deviceId==null||!offer.deviceId.equals(peer.deviceId)||!Objects.equals(offer.fingerprint,peer.fingerprint)||offset<0||offset>offer.size||offer.expires<System.nanoTime())return;
        if(!offer.pushing.compareAndSet(false,true))return;
        Socket raw=null;
        try {
            raw=secureConnect(peer.endpoint);
            try(SSLSocket socket=security.client(raw,peer.fingerprint)) {
                socket.setSoTimeout(45000);
                Protocol.Packet remote=authenticateSecureClient(socket,peer.deviceId,peer.fingerprint,peer.name);
                upsertPeer(remote,peer.endpoint);
                SecureIdentity.writeText(socket.getOutputStream(),"P\n"+port);
                SecureIdentity.writeFrame(socket.getOutputStream(),Protocol.encode(sequence.incrementAndGet(),login,host,Protocol.GET_FILE|Protocol.UTF8,Long.toHexString(number)+":"+Long.toHexString(id)+":"+Long.toHexString(offset)+":",null,deviceId,null,security.fingerprint));
                streamOffer(socket,number+":"+id,candidate->candidate==offer&&Objects.equals(candidate.deviceId,peer.deviceId)&&Objects.equals(candidate.fingerprint,peer.fingerprint)&&offset>=0&&offset<=candidate.size,offset);
            }
        } catch(IOException|RuntimeException ignored) { }
        finally {
            offer.pushing.set(false);
            if(raw!=null){try{raw.close();}catch(IOException ignored){}synchronized(gate){connections.remove(raw);}}
        }
    }
    private void acceptLoop() {
        while (!closed && tcp!=null && !tcp.isClosed()) try {
            Socket client=tcp.accept();
            if(!loopback && socketBinding!=null) try { socketBinding.bindAccepted(client); } catch(IOException ignored) { }
            synchronized (gate) { if (connections.size()>=8) { client.close(); continue; } connections.add(client); }
            try { fileWorkers.execute(()->dispatchConnection(client)); } catch (RejectedExecutionException e) { synchronized (gate) { connections.remove(client); } client.close(); }
        } catch (IOException e) { if (!closed && tcp!=null && !tcp.isClosed()) listener.error("文件监听失败："+e.getMessage()); }
    }
    private void serve(Socket client,InputStream source) {
        Offer active=null; int percent=0; long version=0; boolean completed=false;
        try (Socket socket=client) {
            if(!loopback && socketBinding!=null && !socketBinding.allows(socket.getInetAddress()))return;
            RequestInput deadline=new RequestInput(socket,source);deadline.begin(5000);InputStream requestInput=new BufferedInputStream(deadline,4096);
            ByteArrayOutputStream request=new ByteArrayOutputStream();
            int c=-1; while (request.size()<4096 && (c=requestInput.read())!=-1) { request.write(c); if (c==0) break; }
            if(c!=0)return;
            Protocol.Packet packet=Protocol.parse(request.toByteArray()); if (packet==null || packet.mode()!=Protocol.GET_FILE) return;
            String[] f=packet.body.split(":"); if (f.length<3) return;
            long number=Long.parseLong(f[0],16), id=Long.parseLong(f[1],16), offset=Long.parseLong(f[2],16);Offer[] selected=new Offer[1];long[] versionOut=new long[1];
            if(!tryBeginOfferStream(number+":"+id,o->o.fingerprint==null&&offset>=0&&offset<=o.size&&o.address.equals(socket.getInetAddress()),socket,versionOut,selected))return;
            Offer offer=selected[0];version=versionOut[0];active=offer;
            if (!offer.file.isFile() || offer.file.length()!=offer.size || offer.file.lastModified()!=offer.modified) throw new IOException("原文件已改变");
            percent=offer.size==0?0:(int)(offset*100.0/offer.size); progress(offer,version,percent,"发送中");
            // 只限制无进展的连接，正常大文件不再因总时长超过五分钟而中断。
            AtomicLong lastWrite=new AtomicLong(System.nanoTime());
            ScheduledFuture<?> writeDeadline=timer.scheduleWithFixedDelay(()->{ if(System.nanoTime()-lastWrite.get()>TimeUnit.SECONDS.toNanos(30)) try { socket.close(); } catch (IOException ignored) { } },5,5,TimeUnit.SECONDS);
            try (RandomAccessFile input=new RandomAccessFile(offer.file,"r")) {
                input.seek(offset); byte[] buffer=new byte[65536]; int read; long left=offer.size-offset;
                TransferRate rate=new TransferRate(offset);long started=System.nanoTime();
                while (left>0 && (read=input.read(buffer,0,(int)Math.min(buffer.length,left)))!=-1) {
                    socket.getOutputStream().write(buffer,0,read); left-=read; lastWrite.set(System.nanoTime());
                    synchronized(gate) { offer.expires=System.nanoTime()+TimeUnit.MINUTES.toNanos(30); }
                    boolean sampled=rate.update(offer.size-left,(System.nanoTime()-started)/1000000.0);
                    int next=(int)((offer.size-left)*100.0/offer.size); if(next!=percent||sampled) {percent=next;progress(offer,version,percent,"发送中",rate.bytesPerSecond());}
                }
                if(left!=0) throw new IOException("发送中断");
                progress(offer,version,100,"已发送（非保存确认）"); completed=true;
            } finally { writeDeadline.cancel(false); }
        } catch (IOException | RuntimeException ignored) { /* 对方取消、报价失效和非法请求都关闭连接。 */ }
        finally { if(active!=null&&!completed) progress(active,version,percent,"发送中断，等待对方重试");endOfferStream(active,client);synchronized (gate) { connections.remove(client); } }
    }
    private void dispatchConnection(Socket client) {
        boolean handedOff=false;
        try {
            if(!loopback&&socketBinding!=null&&!socketBinding.allows(client.getInetAddress()))return;
            client.setSoTimeout(5000);
            InputStream source=client.getInputStream();
            int first=source.read();
            if(first<0)return;
            if(first=='F') {
                RequestInput deadline=new RequestInput(client,source);deadline.begin(5000);
                int b1=deadline.read(),b2=deadline.read(),b3=deadline.read();
                if(b1=='Q'&&b2=='2'&&b3=='\n'&&security!=null)serveSecure(client);
                else if(b1=='Q'&&b2=='D'&&b3=='\n')serveDiscovery(client,deadline);
                else if(b1=='Q'&&b2=='R'&&b3=='\n')handedOff=acceptReverse(client,deadline);
                return;
            }
            if(security==null||allowLegacyProtocol)serve(client,new SequenceInputStream(new ByteArrayInputStream(new byte[]{(byte)first}),source));
        } catch(IOException|RuntimeException ignored) { }
        finally {if(!handedOff){closeSocket(client);synchronized(gate){connections.remove(client);}}}
    }
    private boolean acceptReverse(Socket socket,InputStream input)throws IOException {
        byte[] bytes=new byte[32];new DataInputStream(input).readFully(bytes);String ticket=new String(bytes,java.nio.charset.StandardCharsets.US_ASCII);
        synchronized(gate){ReverseRequest request=reverseRequests.get(ticket);if(request==null||!security.isTrusted(request.deviceId,request.fingerprint))return false;
            reverseRequests.remove(ticket);request.client=socket;closeSocket(request.direct);request.ready.countDown();return true;}
    }
    /** 回连只改变 TCP 建连方向，TLS 配对与共享、远控权限仍走原有检查。 */
    public Socket connectPeer(Peer peer,int targetPort)throws IOException {
        Socket direct=new Socket(Proxy.NO_PROXY);ReverseRequest request=null;String ticket=UUID.randomUUID().toString().replace("-","");
        synchronized(gate){if(closed||connections.size()>=8){direct.close();throw new IOException("服务已关闭或连接任务过多");}connections.add(direct);
            if(security.isTrusted(peer.deviceId,peer.fingerprint)){request=new ReverseRequest();request.deviceId=peer.deviceId;request.fingerprint=peer.fingerprint;request.port=targetPort;request.direct=direct;reverseRequests.put(ticket,request);}}
        ScheduledFuture<?> nudge=null;
        try {
            if(loopback&&!peer.endpoint.getAddress().isLoopbackAddress())throw new IOException("测试模式不能访问局域网");
            if(!loopback&&socketBinding!=null)socketBinding.bind(direct,peer.endpoint.getAddress());
            if(request!=null)nudge=timer.schedule(()->{synchronized(gate){if(!reverseRequests.containsKey(ticket))return;}try{probe(peer.endpoint);}catch(IOException ignored){}},150,TimeUnit.MILLISECONDS);
            IOException failure=null;try{peerConnector.connect(direct,new InetSocketAddress(peer.endpoint.getAddress(),targetPort));}catch(IOException e){failure=e;}
            if(failure==null){synchronized(gate){reverseRequests.remove(ticket);if(request==null||request.client==null){direct.setSoTimeout(45000);return direct;}}}
            direct.close();releaseConnection(direct);
            if(request==null)throw new IOException("无法连接对方",failure);
            try{if(!request.ready.await(8,TimeUnit.SECONDS))throw new IOException("无法连接对方，请确认双方已升级并允许局域网连接",failure);}
            catch(InterruptedException e){Thread.currentThread().interrupt();throw new IOException("连接已取消",e);}
            synchronized(gate){if(request.client==null||closed||!security.isTrusted(request.deviceId,request.fingerprint))throw new IOException("连接已取消或设备信任已撤销");}
            request.client.setSoTimeout(45000);return request.client;
        }catch(IOException|RuntimeException e){closeSocket(direct);releaseConnection(direct);synchronized(gate){if(request!=null&&request.client!=null){closeSocket(request.client);connections.remove(request.client);}}throw e;}
        finally{if(nudge!=null)nudge.cancel(false);synchronized(gate){reverseRequests.remove(ticket);}}
    }
    public void releaseConnection(Socket socket){synchronized(gate){connections.remove(socket);}}
    private void serveReverse(InetSocketAddress endpoint,int servicePort,String ticket,String id,String fingerprint,int epoch) {
        int sharePort=port==65535?65534:port+1,phonePort=port>=65534?port-2:port+2;
        if(servicePort!=port&&servicePort!=sharePort&&servicePort!=phonePort)return;
        Socket socket=new Socket(Proxy.NO_PROXY);
        synchronized(gate){if(closed||epoch!=transportEpoch||connections.size()>=8||!security.isTrusted(id,fingerprint)){closeSocket(socket);return;}connections.add(socket);}
        try {
            if(!loopback&&socketBinding!=null)socketBinding.bind(socket,endpoint.getAddress());socket.connect(endpoint,8000);socket.setSoTimeout(5000);
            socket.getOutputStream().write(("FQR\n"+ticket).getBytes(java.nio.charset.StandardCharsets.US_ASCII));
            if(servicePort==port)dispatchConnection(socket);else security.acceptReverse(servicePort,socket);
        }catch(IOException|RuntimeException ignored){}
        finally{closeSocket(socket);releaseConnection(socket);}
    }
    private void serveDiscovery(Socket client,InputStream input)throws IOException {
        Protocol.Packet packet=Protocol.parse(SecureIdentity.readFrame(input,60000));
        if(packet==null||(packet.mode()!=Protocol.ENTRY&&packet.mode()!=Protocol.ANSWER&&packet.mode()!=Protocol.ABSENCE))return;
        InetSocketAddress from=new InetSocketAddress(client.getInetAddress(),packet.replyPort>0?packet.replyPort:port);
        if(isSelf(packet,from))return;
        Peer peer=upsertPeer(packet,from);
        if(peer==null)return;
        if(packet.mode()==Protocol.ENTRY) {
            boolean peerUtf8=(packet.command&(Protocol.UTF8|Protocol.CAP_UTF8))!=0||peer.utf8;
            SecureIdentity.writeFrame(client.getOutputStream(),withPort(encode(Protocol.ANSWER|Protocol.FILE|Protocol.CAP_UTF8|(peerUtf8?Protocol.UTF8:0),Protocol.field(nickname),Protocol.field(group))));
        }
    }
    private Socket secureConnect(InetSocketAddress endpoint)throws IOException {
        return secureConnect(endpoint,null);
    }
    private Socket secureConnect(InetSocketAddress endpoint,java.util.function.Consumer<Socket> beforeConnect)throws IOException {
        if(loopback&&!endpoint.getAddress().isLoopbackAddress())throw new IOException("测试模式不能访问局域网");
        Socket socket=new Socket(Proxy.NO_PROXY);
        synchronized(gate){if(closed||connections.size()>=8){socket.close();throw new IOException("服务已关闭或连接任务过多");}connections.add(socket);}
        try {
            if(beforeConnect!=null)beforeConnect.accept(socket);
            if(!loopback&&socketBinding!=null)socketBinding.bind(socket,endpoint.getAddress());
            socket.connect(endpoint,8000);socket.setSoTimeout(5000);socket.getOutputStream().write(new byte[]{'F','Q','2','\n'});return socket;
        } catch(IOException|RuntimeException e){socket.close();synchronized(gate){connections.remove(socket);}throw e;}
    }
    private void sendSecurePending(Pending item) {
        if(item.delivered||item.canceled||!item.sending.compareAndSet(false,true))return;
        item.attemptStarted=System.nanoTime();
        item.directFailed=false;
        Socket raw=null;
        ScheduledFuture<?> nudge=null;
        try {
            nudge=timer.schedule(()->{
                if(!item.delivered&&!item.canceled&&item.connectingSocket!=null&&!closed) {
                    try{send(encode(Protocol.ENTRY|Protocol.CAP_UTF8|Protocol.FILE,Protocol.field(nickname),Protocol.field(group)),item.endpoint);}catch(IOException ignored){}
                }
            },150,TimeUnit.MILLISECONDS);
            raw=secureConnect(item.endpoint,s->{synchronized(gate){if(item.canceled||item.delivered){closeSocket(s);throw new UncheckedIOException(new IOException("已取消发送"));}item.connectingSocket=s;}});
            if(item.delivered||item.canceled)return;
            try(SSLSocket socket=security.client(raw,item.fingerprint)) {
                socket.setSoTimeout(45000);
                Protocol.Packet remote=authenticateSecureClient(socket,item.deviceId,item.fingerprint,item.peer.name);
                if(item.delivered||item.canceled||!item.transmitting.compareAndSet(false,true))return;
                try {
                    if(item.delivered||item.canceled)return;
                    saveOffers();
                    synchronized(gate){if(item.canceled||item.delivered||pending.get(item.message.number)!=item)return;}
                    SecureIdentity.writeText(socket.getOutputStream(),"M\n"+port);SecureIdentity.writeFrame(socket.getOutputStream(),item.bytes);
                    if(!"OK".equals(SecureIdentity.readText(socket.getInputStream(),1024)))throw new IOException("对方未接受消息或未完成配对");
                    synchronized(gate){if(item.canceled||pending.get(item.message.number)!=item)return;item.delivered=true;item.message.state="已送达";for(Offer offer:offers.values())if(offer.packet==item.message.number&&offer.peer.id().equals(item.peer.id()))offer.message=item.message;pending.remove(item.message.number);}
                    upsertPeer(remote,item.endpoint);
                    listener.message(item.peer,item.message.copy(),true);
                    if(!item.message.files.isEmpty())try{saveOffers();}catch(IOException ignored){}
                    if(!item.message.files.isEmpty()&&!closed) {
                        try{timer.schedule(()->scheduleSync(item.peer),300,TimeUnit.MILLISECONDS);timer.schedule(()->scheduleSync(item.peer),1200,TimeUnit.MILLISECONDS);}catch(RejectedExecutionException ignored){}
                    }
                } finally {
                    item.transmitting.set(false);
                }
            }
        } catch(IOException|RuntimeException e) {
            if(e instanceof UncheckedIOException&&item.canceled)return;
            item.directFailed=true;
            if(item.delivered||item.canceled)return;
            if(e instanceof SecureIdentity.TrustException||e instanceof javax.net.ssl.SSLException){
                synchronized(gate){
                    if(item.canceled||item.delivered||pending.get(item.message.number)!=item)return;
                    pending.remove(item.message.number);item.message.state="身份核对未通过，未确认送达";
                }
                listener.message(item.peer,item.message.copy(),true);
            }
            else if(heartbeatRetry>=TimeUnit.SECONDS.toNanos(1)&&!closed) {
                long waitUntil=System.nanoTime()+TimeUnit.MILLISECONDS.toNanos(2200);
                while(!item.delivered&&!item.canceled&&!closed&&System.nanoTime()<waitUntil) {
                    try{Thread.sleep(50);}catch(InterruptedException ie){Thread.currentThread().interrupt();break;}
                }
                if(item.delivered||item.canceled)return;
            }
            if(!closed&&!item.delivered&&!item.canceled)listener.error("加密发送未完成："+e.getMessage());
        } finally {
            if(nudge!=null)nudge.cancel(false);
            synchronized(gate){item.connectingSocket=null;}
            if(raw!=null){try{raw.close();}catch(IOException ignored){}synchronized(gate){connections.remove(raw);}}
            item.last=System.nanoTime();
            item.sending.set(false);
        }
    }
    private void streamOffer(SSLSocket socket,String key,java.util.function.Predicate<Offer> authorize,long offset)throws IOException {
        long[] versionOut=new long[1];Offer[] selected=new Offer[1];
        if(!tryBeginOfferStream(key,authorize,socket,versionOut,selected)) {
            boolean revoked;
            synchronized(gate){Offer current=offers.get(key);revoked=!closed&&(current==null||(current.canceled||current.expires<System.nanoTime())&&authorize.test(current));}
            if(revoked)SecureIdentity.writeText(socket.getOutputStream(),"REVOKED");
            throw new IOException("文件授权无效或已取消");
        }
        Offer offer=selected[0];long version=versionOut[0];
        AtomicLong lastWrite=new AtomicLong(System.nanoTime());
        ScheduledFuture<?> deadline=timer.scheduleWithFixedDelay(()->{if(System.nanoTime()-lastWrite.get()>TimeUnit.SECONDS.toNanos(60))try{socket.close();}catch(IOException ignored){}},5,5,TimeUnit.SECONDS);
        try {
            if(!sourceUnchanged(offer))throw new IOException("源文件已改变");
            SecureIdentity.writeText(socket.getOutputStream(),"OK\n"+offer.size+"\n"+offer.sha256);
            TransferRate rate=new TransferRate(offset);long startedAt=System.nanoTime();long sent=offset;
            try(InputStream file=openOffer(offer,offset)) {
                byte[] buffer=new byte[65536];
                while(sent<offer.size){int count=file.read(buffer,0,(int)Math.min(buffer.length,offer.size-sent));if(count<0)throw new EOFException("源文件读取中断");socket.getOutputStream().write(buffer,0,count);sent+=count;lastWrite.set(System.nanoTime());synchronized(gate){offer.expires=System.nanoTime()+TimeUnit.DAYS.toNanos(7);}if(rate.update(sent,(System.nanoTime()-startedAt)/1000000.0))progress(offer,version,(int)(sent*100.0/Math.max(1,offer.size)),"发送中",rate.bytesPerSecond());}
            }
            socket.getOutputStream().flush();progress(offer,version,100,"已发送，等待校验保存");
            if(SecureIdentity.readText(socket.getInputStream(),128).equals("STORED\n"+offer.sha256)){progress(offer,version,100,"对方已校验保存");try{saveOffers();}catch(IOException error){listener.error("文件完成状态保存失败："+error.getMessage());}}
        } catch(IOException e){progress(offer,version,offer.percent,"传输中断或保存确认未送达");throw e;}
        finally {deadline.cancel(false);endOfferStream(offer,socket);}
    }
    private void serveSecure(Socket client)throws IOException {
        SSLSocket socket=security.server(client,true);try {
            socket.setSoTimeout(45000);Protocol.Packet identity=authenticateSecureServer(socket);if(identity==null)return;
            RequestInput input=new RequestInput(socket);input.begin(5000);String[] header=SecureIdentity.readText(input,128).split("\\n",-1);if(header.length!=2)throw new IOException("安全报文无效");int senderPort=Integer.parseInt(header[1]);if(senderPort<1024||senderPort>65535)throw new IOException("安全端口无效");
            Protocol.Packet packet=Protocol.parse(SecureIdentity.readFrame(input,60000));if(packet==null||!identity.deviceId.equals(packet.deviceId)||!identity.login.equals(packet.login))throw new IOException("安全报文无效");String fingerprint=SecureIdentity.remoteFingerprint(socket);if(!fingerprint.equals(packet.fingerprint))throw new IOException("报文身份与证书不一致");
            socket.setSoTimeout(45000);InetSocketAddress endpoint=new InetSocketAddress(socket.getInetAddress(),senderPort);
            if("M".equals(header[0])&&(packet.mode()==Protocol.MESSAGE||packet.mode()==Protocol.RELEASE)){handle(packet,endpoint,true);SecureIdentity.writeText(socket.getOutputStream(),"OK");return;}
            if("V".equals(header[0])&&packet.mode()==Protocol.ENTRY){upsertPeer(packet,endpoint);SecureIdentity.writeText(socket.getOutputStream(),"OK");return;}
            if("S".equals(header[0])&&(packet.mode()==Protocol.ENTRY||packet.mode()==Protocol.ABSENCE)) {
                    upsertPeer(packet,endpoint);
                    long waitNs=0;
                    synchronized(gate) {
                        long now=System.nanoTime();
                        for(Pending item:pending.values())if(!item.delivered&&!item.canceled&&packet.deviceId.equals(item.deviceId)&&fingerprint.equals(item.fingerprint)&&!item.directFailed){long rem=TimeUnit.MILLISECONDS.toNanos(120)-(now-item.attemptStarted);if(rem>waitNs)waitNs=rem;}
                        for(PendingDownload pd:pendingDownloads)if(!pd.claimed.get()&&packet.deviceId.equals(pd.deviceId)&&fingerprint.equals(pd.fingerprint)&&!pd.directFailed){long rem=TimeUnit.MILLISECONDS.toNanos(120)-(now-pd.createdAt);if(rem>waitNs)waitNs=rem;}
                    }
                    if(waitNs>0)try{Thread.sleep(TimeUnit.NANOSECONDS.toMillis(waitNs)+5);}catch(InterruptedException e){Thread.currentThread().interrupt();}
                    List<Pending> due=new ArrayList<>();
                    synchronized(gate) {
                        for(Pending item:pending.values())if(!item.delivered&&!item.canceled&&packet.deviceId.equals(item.deviceId)&&fingerprint.equals(item.fingerprint))due.add(item);
                    }
                    due.sort(Comparator.comparingLong(a->a.message.number));
                    for(Pending item:due) {
                        // 逐条占用发送权，前一条中断时后续消息仍能重试。
                        if(!item.transmitting.compareAndSet(false,true))continue;
                        try {
                            synchronized(gate){if(item.delivered||item.canceled||pending.get(item.message.number)!=item||!security.isTrusted(item.deviceId,item.fingerprint))continue;}
                            saveOffers();
                            synchronized(gate){if(item.delivered||item.canceled||pending.get(item.message.number)!=item)continue;}
                            SecureIdentity.writeText(socket.getOutputStream(),"M");
                            SecureIdentity.writeFrame(socket.getOutputStream(),item.bytes);
                            if("OK".equals(SecureIdentity.readText(socket.getInputStream(),1024))) {
                                Socket connecting;boolean delivered=false;
                                synchronized(gate){if(!item.canceled&&pending.get(item.message.number)==item){item.delivered=true;item.message.state="已送达";for(Offer offer:offers.values())if(offer.packet==item.message.number&&offer.peer.id().equals(item.peer.id()))offer.message=item.message;connecting=item.connectingSocket;item.connectingSocket=null;pending.remove(item.message.number);delivered=true;}else connecting=null;}
                                if(connecting!=null)try{connecting.close();}catch(IOException ignored){}
                                if(!delivered)continue;
                                listener.message(item.peer,item.message.copy(),true);
                                if(!item.message.files.isEmpty())try{saveOffers();}catch(IOException ignored){}
                                if(!item.message.files.isEmpty()&&!closed) {
                                    try{timer.schedule(()->scheduleSync(item.peer),300,TimeUnit.MILLISECONDS);timer.schedule(()->scheduleSync(item.peer),1200,TimeUnit.MILLISECONDS);}catch(RejectedExecutionException ignored){}
                                }
                            }
                        } finally {
                            item.last=System.nanoTime();
                            item.transmitting.set(false);
                        }
                    }
                    List<PendingDownload> pulls=new ArrayList<>();
                    synchronized(gate) {
                        for(PendingDownload pd:pendingDownloads)if(!pd.claimed.get()&&packet.deviceId.equals(pd.deviceId)&&fingerprint.equals(pd.fingerprint))pulls.add(pd);
                    }
                    for(PendingDownload pd:pulls) {
                        SecureIdentity.writeText(socket.getOutputStream(),"D\n"+Long.toHexString(pd.packet)+":"+Long.toHexString(pd.fileId)+":"+Long.toHexString(pd.offset));
                    }
                    if(packet.reverseConnections){Map<String,ReverseRequest> requests=new LinkedHashMap<>();synchronized(gate){for(Map.Entry<String,ReverseRequest> entry:reverseRequests.entrySet())if(packet.deviceId.equals(entry.getValue().deviceId)&&fingerprint.equals(entry.getValue().fingerprint))requests.put(entry.getKey(),entry.getValue());}
                        for(Map.Entry<String,ReverseRequest> entry:requests.entrySet())SecureIdentity.writeText(socket.getOutputStream(),"R\n"+entry.getKey()+":"+entry.getValue().port);}
                    SecureIdentity.writeText(socket.getOutputStream(),"OK");
                    return;
            }
            if("P".equals(header[0])&&packet.mode()==Protocol.GET_FILE) {
                upsertPeer(identity,endpoint);
                String[] fields=packet.body.split(":");if(fields.length<3)throw new IOException("文件请求无效");
                long number=Long.parseLong(fields[0],16),id=Long.parseLong(fields[1],16),offset=Long.parseLong(fields[2],16);
                PendingDownload match=null;Socket direct=null;
                synchronized(gate) {
                    for(PendingDownload pd:pendingDownloads) {
                        if(packet.deviceId.equals(pd.deviceId)&&fingerprint.equals(pd.fingerprint)&&pd.packet==number&&pd.fileId==id&&pd.offset==offset&&pd.claimed.compareAndSet(false,true)) {
                            match=pd;pendingDownloads.remove(pd);pd.pushedClient=client;pd.pushedSocket=socket;direct=pd.directSocket;pd.ready.countDown();break;
                        }
                    }
                }
                if(match==null)return;
                if(direct!=null)try{direct.close();}catch(IOException ignored){}
                try{while(!match.done.await(1,TimeUnit.SECONDS)&&!closed&&!client.isClosed()){} }catch(InterruptedException e){Thread.currentThread().interrupt();}
                return;
            }
            if(!"F".equals(header[0])||packet.mode()!=Protocol.GET_FILE)throw new IOException("未知安全操作");
            upsertPeer(identity,endpoint);
            String[] fields=packet.body.split(":");if(fields.length<3)throw new IOException("文件请求无效");long number=Long.parseLong(fields[0],16),offset=Long.parseLong(fields[2],16),id=Long.parseLong(fields[1],16);
            streamOffer(socket,number+":"+id,offer->Objects.equals(offer.deviceId,packet.deviceId)&&fingerprint.equals(offer.fingerprint)&&offset>=0&&offset<=offer.size,offset);
        }finally{socket.close();}
    }
    private void receiveSecureFile(Peer peer,long packet,Protocol.Attachment file,File destination,Transfer transfer,Progress progress)throws IOException {
        if(!Protocol.safeName(file.name)||file.size<0||!SecureIdentity.validFingerprint(file.sha256))throw new IOException("文件元数据或校验值无效");
        if(destination.exists())throw new IOException("目标已存在，不会覆盖");
        if(transfer.isCancelled())throw new IOException("已取消传输");
        File part=partialFile(peer,packet,file,destination);boolean owned=false,revoked=false;Socket raw=null;SSLSocket directTls=null;PendingDownload pd=null;ScheduledFuture<?> nudge=null;
        try {
            try(java.nio.channels.FileChannel output=java.nio.channels.FileChannel.open(part.toPath(),java.nio.file.StandardOpenOption.CREATE,java.nio.file.StandardOpenOption.WRITE,java.nio.file.StandardOpenOption.READ,java.nio.file.LinkOption.NOFOLLOW_LINKS);java.nio.channels.FileLock lock=output.tryLock()) {
                if(lock==null)throw new IOException("同一文件正在接收");owned=true;
                long offset=output.size();if(offset>file.size){output.truncate(0);offset=0;}output.position(offset);requireSpace(destination.getAbsoluteFile().getParentFile(),file.size-offset);
                pd=new PendingDownload(peer.deviceId,peer.fingerprint,packet,file.id,offset);
                synchronized(gate){pendingDownloads.add(pd);}
                PendingDownload activePd=pd;
                nudge=timer.schedule(()->{
                    if(!activePd.claimed.get()&&!closed) {
                        try{send(encode(Protocol.ENTRY|Protocol.CAP_UTF8|Protocol.FILE,Protocol.field(nickname),Protocol.field(group)),peer.endpoint);}catch(IOException ignored){}
                    }
                },150,TimeUnit.MILLISECONDS);
                long waitUntil=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);
                IOException connectError=null;
                try {
                    raw=secureConnect(peer.endpoint,s->{
                        activePd.directSocket=s;
                        try{transfer.attach(s);}catch(IOException e){throw new UncheckedIOException(e);}
                    });
                    directTls=security.client(raw,peer.fingerprint);
                    directTls.setSoTimeout(45000);
                    Protocol.Packet remote=authenticateSecureClient(directTls,peer.deviceId,peer.fingerprint,peer.name);
                    upsertPeer(remote,peer.endpoint);
                    if(pd.claimed.compareAndSet(false,true)) {
                        synchronized(gate){pendingDownloads.remove(pd);}
                        SecureIdentity.writeText(directTls.getOutputStream(),"F\n"+port);
                        SecureIdentity.writeFrame(directTls.getOutputStream(),Protocol.encode(sequence.incrementAndGet(),login,host,Protocol.GET_FILE|Protocol.UTF8,Long.toHexString(packet)+":"+Integer.toHexString(file.id)+":"+Long.toHexString(offset)+":",null,deviceId,null,security.fingerprint));
                    } else {
                        try{directTls.close();}catch(IOException ignored){}directTls=null;
                    }
                } catch(UncheckedIOException e) {
                    connectError=e.getCause();
                    if(directTls!=null){closeSocket(directTls);directTls=null;}
                } catch(IOException e) {
                    connectError=e;
                    if(directTls!=null){closeSocket(directTls);directTls=null;}
                }
                if(directTls==null) {
                    pd.directFailed=true;
                    if(raw!=null){try{raw.close();}catch(IOException ignored){}synchronized(gate){connections.remove(raw);}raw=null;}
                    if(transfer.isCancelled())throw new IOException("已取消传输");
                    String msg=connectError==null?null:connectError.getMessage();
                    if(connectError instanceof SecureIdentity.TrustException||connectError instanceof javax.net.ssl.SSLException||"服务已关闭或连接任务过多".equals(msg)||"测试模式不能访问局域网".equals(msg)||"策略拒绝连接".equals(msg))throw connectError;
                    while(pd.pushedSocket==null&&heartbeatRetry>=TimeUnit.SECONDS.toNanos(1)&&System.nanoTime()<waitUntil) {
                        if(transfer.isCancelled()||closed)throw new IOException("已取消传输");
                        try{if(pd.ready.await(100,TimeUnit.MILLISECONDS))break;}
                        catch(InterruptedException ie){Thread.currentThread().interrupt();throw new IOException("已取消传输");}
                    }
                    if(pd.pushedSocket==null)throw connectError!=null?connectError:new IOException("加密连接超时。");
                    transfer.attach(pd.pushedClient);
                }
                SSLSocket socket=directTls!=null?directTls:pd.pushedSocket;
                String[] response=SecureIdentity.readText(socket.getInputStream(),256).split("\\n",-1);
                if(response.length==1&&response[0].equals("REVOKED")){revoked=true;throw new OfferRevokedException();}
                if(response.length!=3||!response[0].equals("OK")||!response[1].equals(Long.toString(file.size))||!response[2].equals(file.sha256))throw new IOException("发送方文件信息已变化");
                byte[] buffer=new byte[65536];long received=offset;TransferRate rate=new TransferRate(offset);long startedAt=System.nanoTime();
                while(received<file.size){if(transfer.isCancelled())throw new IOException("已取消传输");int count=socket.getInputStream().read(buffer,0,(int)Math.min(buffer.length,file.size-received));if(count<0)throw new IOException("传输中断，已保留断点");java.nio.ByteBuffer bytes=java.nio.ByteBuffer.wrap(buffer,0,count);while(bytes.hasRemaining())output.write(bytes);received+=count;if(rate.update(received,(System.nanoTime()-startedAt)/1000000.0)&&progress!=null)progress.update((int)(received*100.0/Math.max(1,file.size)),rate.bytesPerSecond());}
                output.force(true);output.position(0);
                java.security.MessageDigest sha;try{sha=java.security.MessageDigest.getInstance("SHA-256");}catch(java.security.NoSuchAlgorithmException e){throw new IllegalStateException(e);}
                java.nio.ByteBuffer bufferView=java.nio.ByteBuffer.wrap(buffer);int count;
                while((count=output.read(bufferView))>0){if(transfer.isCancelled())throw new IOException("已取消传输");sha.update(buffer,0,count);bufferView.clear();}
                StringBuilder hash=new StringBuilder();for(byte value:sha.digest())hash.append(String.format(Locale.ROOT,"%02x",value&255));
                if(!hash.toString().equals(file.sha256)){output.truncate(0);throw new IOException("文件校验失败，已清除错误断点");}
            }
            if(transfer.isCancelled())throw new IOException("已取消传输");Files.move(part.toPath(),destination.toPath());
            SSLSocket socket=directTls!=null?directTls:pd.pushedSocket;
            try{SecureIdentity.writeText(socket.getOutputStream(),"STORED\n"+file.sha256);}catch(IOException e){listener.error("文件已校验保存，但保存确认未送达");}
            if(progress!=null)progress.update(100);
        } finally {
            if(nudge!=null)nudge.cancel(false);
            if(pd!=null){synchronized(gate){pendingDownloads.remove(pd);}pd.done.countDown();}
            if(directTls!=null)try{directTls.close();}catch(IOException ignored){}
            if(raw!=null){try{raw.close();}catch(IOException ignored){}synchronized(gate){connections.remove(raw);}}
            if(owned&&(revoked||transfer.isCancelled()||part.length()==0))Files.deleteIfExists(part.toPath());
        }
    }
    private InputStream openOffer(Offer offer,long offset)throws IOException {if(offer.source!=null)return offer.source.open(offset);FileInputStream input=new FileInputStream(offer.file);try{input.getChannel().position(offset);return input;}catch(IOException e){input.close();throw e;}}
    private byte[] secureHello(){return Protocol.encode(0,login,host,Protocol.ENTRY|Protocol.UTF8,Protocol.field(nickname),Protocol.field(group),deviceId,null,security.fingerprint);}
    private byte[] secureSyncHello(){byte[] hello=Protocol.encode(0,login,host,Protocol.ABSENCE|Protocol.UTF8,Protocol.field(nickname),Protocol.field(group),deviceId,null,security.fingerprint),suffix="FQREV=1\0".getBytes(java.nio.charset.StandardCharsets.US_ASCII),result=Arrays.copyOf(hello,hello.length+suffix.length);System.arraycopy(suffix,0,result,hello.length,suffix.length);return result;}
    private Socket securePeerConnect(Peer peer)throws IOException{Socket socket=connectPeer(peer,peer.endpoint.getPort());try{socket.getOutputStream().write(new byte[]{'F','Q','2','\n'});return socket;}catch(IOException e){closeSocket(socket);releaseConnection(socket);throw e;}}
    public void verifyPeer(Peer peer)throws IOException {if(security==null||peer.fingerprint==null)throw new IOException("请先升级对方飞Q并刷新联系人");Socket raw=securePeerConnect(peer);try(SSLSocket tls=security.client(raw,peer.fingerprint)){Protocol.Packet remote=authenticateSecureClient(tls,peer.deviceId,peer.fingerprint,peer.name);SecureIdentity.writeText(tls.getOutputStream(),"V\n"+port);SecureIdentity.writeFrame(tls.getOutputStream(),secureHello());if(!"OK".equals(SecureIdentity.readText(tls.getInputStream(),16)))throw new IOException("身份验证未完成");upsertPeer(remote,peer.endpoint);}finally{raw.close();synchronized(gate){connections.remove(raw);}}}
    private Protocol.Packet authenticateSecureClient(SSLSocket socket,String id,String fingerprint,String name)throws IOException {
        return authenticateSecureClient(socket,id,fingerprint,name,false);
    }
    private Protocol.Packet authenticateSecureClient(SSLSocket socket,String id,String fingerprint,String name,boolean sync)throws IOException {
        socket.setSoTimeout(45000);SecureIdentity.writeFrame(socket.getOutputStream(),sync?secureSyncHello():secureHello());
        RequestInput hello=new RequestInput(socket);hello.begin(5000);Protocol.Packet remote=Protocol.parse(SecureIdentity.readFrame(hello,4096));
        if(remote==null||!id.equals(remote.deviceId)||!fingerprint.equals(remote.fingerprint))throw new IOException("对方设备身份与所选联系人不一致");
        boolean accepted=security.ensureTrusted(id,fingerprint,name);SecureIdentity.writeText(socket.getOutputStream(),accepted?"YES":"NO");
        RequestInput response=new RequestInput(socket);response.begin(45000);if(!accepted||!"YES".equals(SecureIdentity.readText(response,16)))throw new SecureIdentity.TrustException("设备配对未通过，请在两端核对后重试");
        return remote;
    }
    private Protocol.Packet authenticateSecureServer(SSLSocket socket)throws IOException {
        RequestInput hello=new RequestInput(socket);hello.begin(5000);Protocol.Packet remote=Protocol.parse(SecureIdentity.readFrame(hello,4096));
        if(remote==null||remote.deviceId==null||!SecureIdentity.remoteFingerprint(socket).equals(remote.fingerprint))throw new IOException("加密连接身份无效");
        SecureIdentity.writeFrame(socket.getOutputStream(),secureHello());
        boolean accepted=remote.mode()==Protocol.ABSENCE
            ?(security.isTrusted(remote.deviceId,remote.fingerprint)||(hasPendingFor(remote.deviceId,remote.fingerprint)&&security.ensureTrusted(remote.deviceId,remote.fingerprint,remote.body)))
            :security.ensureTrusted(remote.deviceId,remote.fingerprint,remote.body);
        RequestInput response=new RequestInput(socket);response.begin(45000);boolean other="YES".equals(SecureIdentity.readText(response,16));SecureIdentity.writeText(socket.getOutputStream(),accepted&&other?"YES":"NO");return accepted&&other?remote:null;
    }
    private void progress(Offer offer,long version,int percent,String state) {
        progress(offer,version,percent,state,0);
    }
    private void progress(Offer offer,long version,int percent,String state,double bytesPerSecond) {
        synchronized(gate) {if(version!=offer.progressVersion)return;offer.percent=percent;offer.state=state;if(!closed)listener.fileProgress(offer.peer,offer.packet,offer.id,percent,state,bytesPerSecond);}
    }
    private void endOffer(Offer offer,String state) {if(!"已发送（非保存确认）".equals(offer.state)&&!"对方已校验保存".equals(offer.state))progress(offer,++offer.progressVersion,offer.percent,state);}
    private void sendReleaseFiles(Peer peer,String body) throws IOException {
        if(security!=null&&peer.fingerprint!=null){Socket raw=securePeerConnect(peer);try(SSLSocket tls=security.client(raw,peer.fingerprint)){Protocol.Packet remote=authenticateSecureClient(tls,peer.deviceId,peer.fingerprint,peer.name);SecureIdentity.writeText(tls.getOutputStream(),"M\n"+port);SecureIdentity.writeFrame(tls.getOutputStream(),encode(Protocol.RELEASE,body,null));if(!"OK".equals(SecureIdentity.readText(tls.getInputStream(),16)))throw new IOException("文件通知未送达");upsertPeer(remote,peer.endpoint);}finally{raw.close();synchronized(gate){connections.remove(raw);}}return;}
        if(security!=null&&(!allowLegacyProtocol||security.trustedFingerprint(peer.deviceId)!=null))throw new IOException("未允许旧版明文协议");send(Protocol.encode(sequence.incrementAndGet(),login,host,Protocol.RELEASE|Protocol.UTF8,body,null,deviceId),peer.endpoint);
    }
    public void reject(Peer peer,long packet) throws IOException { sendReleaseFiles(peer,Long.toString(packet)); }
    public boolean offering(String peer,long packet,long file) {
        synchronized(gate) {Offer offer=offers.get(packet+":"+file);return !closed&&offer!=null&&offer.peer.id().equals(peer)&&offer.expires>=System.nanoTime();}
    }
    public void revokeOffers() throws IOException {
        synchronized(gate) {
            // 已接入的文件连接可能正在读副本，不能先删文件再中断对端。
            if(!connections.isEmpty()) throw new IOException("仍有文件连接，请待传输结束后清理发送缓存");
            for(Offer offer:offers.values())endOffer(offer,"邀请已撤销");offers.clear();saveOffers();
        }
    }
    public boolean isOffered(File file) {
        synchronized(gate) { for(Offer offer:offers.values()) if(file.equals(offer.file)&&offer.expires>=System.nanoTime()) return true; return false; }
    }
    private static final Set<String> receivingPaths=new HashSet<>();
    public void receiveFile(Peer peer,long packet,Protocol.Attachment file,File destination,Transfer transfer,Progress progress) throws IOException {
        String key=destination.getCanonicalPath();
        synchronized(receivingPaths) { if(!receivingPaths.add(key)) throw new IOException("同一目标正在接收，请等待当前任务完成"); }
        // 文件锁在关闭后才能移动；目标锁必须覆盖最终提交，避免两个任务在这个间隙串用断点。
        try { if(security!=null&&peer.fingerprint!=null)receiveSecureFile(peer,packet,file,destination,transfer,progress);else {if(security!=null&&(!allowLegacyProtocol||security.trustedFingerprint(peer.deviceId)!=null))throw new IOException("未允许旧版明文文件传输");receiveFileLocked(peer,packet,file,destination,transfer,progress);} }
        finally { synchronized(receivingPaths) { receivingPaths.remove(key); } }
    }
    private void receiveFileLocked(Peer peer,long packet,Protocol.Attachment file,File destination,Transfer transfer,Progress progress) throws IOException {
        if (!Protocol.safeName(file.name) || file.size<0) throw new IOException("文件名或长度不安全");
        if (loopback && !peer.endpoint.getAddress().isLoopbackAddress()) throw new IOException("测试模式不能访问局域网");
        if (destination.exists()) throw new IOException("目标已存在，不会覆盖");
        File part=partialFile(peer,packet,file,destination);
        Socket socket=new Socket(Proxy.NO_PROXY);
        synchronized (gate) { if (closed || connections.size()>=8) { socket.close(); throw new IOException("服务已关闭或传输任务过多"); } connections.add(socket); }
        boolean owned=false,complete=false;
        try (Socket active=socket;
             java.nio.channels.FileChannel output=java.nio.channels.FileChannel.open(part.toPath(),java.nio.file.StandardOpenOption.CREATE,java.nio.file.StandardOpenOption.WRITE,java.nio.file.LinkOption.NOFOLLOW_LINKS);
             java.nio.channels.FileLock lock=output.tryLock()) {
            if(lock==null) throw new IOException("同一文件正在接收");
            owned=true; long offset=output.size();
            if(offset>file.size) { output.truncate(0); offset=0; }
            output.position(offset); requireSpace(destination.getAbsoluteFile().getParentFile(),file.size-offset);
            transfer.attach(active);
            if(!loopback && socketBinding!=null) socketBinding.bind(active,peer.endpoint.getAddress());
            active.connect(peer.endpoint,8000); active.setSoTimeout(15000);
            byte[] request=encode(Protocol.GET_FILE,Long.toHexString(packet)+":"+Integer.toHexString(file.id)+":"+Long.toHexString(offset)+":",null);
            active.getOutputStream().write(request);
            OutputStream out=new BufferedOutputStream(java.nio.channels.Channels.newOutputStream(output),65536);
            try {
                byte[] buffer=new byte[65536]; long count=offset; int last=-1;
                TransferRate rate=new TransferRate(offset);long started=System.nanoTime();
                if(progress!=null) progress.update(file.size==0?0:(int)(count*100.0/file.size));
                while (count<file.size) {
                    if (transfer.isCancelled()) throw new IOException("已取消传输");
                    int read=active.getInputStream().read(buffer,0,(int)Math.min(buffer.length,file.size-count));
                    if (read<0) throw new IOException("文件中断或邀请失效；已保留断点，请在发送方在线且邀请有效时重试");
                    out.write(buffer,0,read);
                    count+=read; int percent=(int)(count*100.0/file.size);
                    boolean sampled=rate.update(count,(System.nanoTime()-started)/1000000.0);
                    if (percent!=last||sampled) { last=percent; if (progress!=null) progress.update(percent,rate.bytesPerSecond()); }
                }
            } finally { out.flush(); }
            if (transfer.isCancelled()) throw new IOException("已取消传输");
            output.force(false); complete=true;
        } finally {
            synchronized (gate) { connections.remove(socket); }
            if(owned && (transfer.isCancelled()||(!complete&&part.length()==0))) Files.deleteIfExists(part.toPath());
        }
        if(transfer.isCancelled()) { Files.deleteIfExists(part.toPath()); throw new IOException("已取消传输"); }
        Files.move(part.toPath(),destination.toPath()); if (progress!=null) progress.update(100);
    }
    public static File partialFile(Peer peer,long packet,Protocol.Attachment file,File destination) {
        try {
            String identity=peer.id()+"\n"+peer.host+"\n"+packet+"\n"+file.id+"\n"+file.name+"\n"+file.size+"\n"+file.modified;
            byte[] hash=java.security.MessageDigest.getInstance("SHA-256").digest(identity.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            StringBuilder key=new StringBuilder(); for(byte b:hash) key.append(String.format(Locale.ROOT,"%02x",b&255));
            return new File(destination.getPath()+"."+key+".part");
        } catch(java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }
    }
    public static long availableSpace(File directory) { return Math.max(0,directory.getUsableSpace()-16L*1024*1024); }
    public static void requireSpace(File directory,long extra) throws IOException {
        if(extra<0 || extra>availableSpace(directory)) throw new IOException("存储空间不足（保留 16 MiB 安全余量），请清理文件或更换存储位置");
    }
    /** 服务销毁先释放监听端口，数据库及任务清理仍可在后台队列完成。 */
    public void stopListening() {
        closed=true;
        DatagramSocket receiver=udp;if(receiver!=null)receiver.close();
        ServerSocket listener=tcp;if(listener!=null)try{listener.close();}catch(IOException ignored){ }
    }
    @Override public void close() {
        synchronized (gate) {
            if (cleanupStarted) return;
            cleanupStarted=true;
            if(!closed)for (Peer p:peers.values()) try { send(encode(Protocol.EXIT,nickname,null),p.endpoint); } catch (IOException ignored) { }
            stopListening();
            for (Socket socket:connections) try { socket.close(); } catch (IOException ignored) { }
            for (Socket socket:probeSockets) try { socket.close(); } catch (IOException ignored) { }
            for (PendingDownload pd:pendingDownloads) { pd.ready.countDown(); pd.done.countDown(); } pendingDownloads.clear();
            cancelReverseRequests();
            connections.clear(); probeSockets.clear(); pendingTcpTargets.clear(); offers.clear();
            for (Pending p:pending.values()) { p.message.state="已断开，未确认送达"; listener.message(p.peer,p.message.copy(),true); } pending.clear();
        }
        timer.shutdownNow(); fileWorkers.shutdownNow(); probeWorkers.shutdownNow();
        if(security!=null)security.removeRevocationObserver(transportRevoked);
    }
}
