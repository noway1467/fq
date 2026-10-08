package org.feiqlight.android.core;

import java.io.*;
import java.net.*;
import java.nio.file.Files;
import java.security.SecureRandom;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;

/** 无 Android 依赖的通信核心，JVM 测试直接运行 APK 使用的同一份代码。 */
public final class LanNode implements AutoCloseable {
    // 不设产品级单文件上限，长度仍受协议和文件系统能力约束。
    public static final long MAX_FILE=Long.MAX_VALUE;
    public static final class Peer {
        public final InetSocketAddress endpoint;
        public final String login, host;
        public volatile String name, group;
        public volatile boolean utf8, online;
        public volatile long lastSeen;
        public Peer(InetSocketAddress endpoint,String login,String host,String name,String group) {
            this.endpoint=endpoint; this.login=login; this.host=host; this.name=name; this.group=group;
        }
        public String id() { return endpoint.getAddress().getHostAddress()+":"+endpoint.getPort()+"/"+login; }
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
        default void fileProgress(Peer peer,long packet,long file,int percent,String state) { }
    }
    public interface SocketBinding {
        void bind(DatagramSocket socket) throws IOException;
        void bind(Socket socket) throws IOException;
        default Collection<InetAddress> broadcasts() throws IOException { return null; }
        default void changed() { }
    }
    private volatile SocketBinding socketBinding;
    public void setSocketBinding(SocketBinding binding) { if(udp!=null) throw new IllegalStateException("需在启动前设置网络"); socketBinding=binding; }
    public void networkChanged() {SocketBinding binding=socketBinding;if(binding!=null)binding.changed();}
    public interface Progress { void update(int percent); }
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
        Peer peer; Message message; byte[] bytes; long last; int attempts=1;
    }
    private static final class Offer {
        File file; long size, modified, expires; InetAddress address;
        Peer peer; long packet, id, progressVersion; int percent; String state;
    }
    private final Object gate=new Object();
    private final Map<String,Peer> peers=new LinkedHashMap<>();
    private final Set<InetSocketAddress> remembered=new LinkedHashSet<>();
    private final Map<Long,Pending> pending=new HashMap<>();
    private final Map<String,Long> seen=new LinkedHashMap<>();
    private final Map<String,Offer> offers=new HashMap<>();
    private final Set<Socket> connections=new HashSet<>();
    private final AtomicLong sequence=new AtomicLong(new SecureRandom().nextInt(0x3fffffff));
    private final ScheduledExecutorService timer=Executors.newSingleThreadScheduledExecutor();
    private final ExecutorService fileWorkers=Executors.newFixedThreadPool(4);
    private final Listener listener;
    private final boolean loopback;
    private final int port;
    private final String login, host;
    public volatile String nickname, group;
    private DatagramSocket udp;
    private ServerSocket tcp;
    private volatile boolean closed;
    private long refreshAt;

    public LanNode(int port,boolean loopback,String login,String host,String nickname,String group,Listener listener) {
        if (port<1024 || port>65535) throw new IllegalArgumentException("端口须为 1024–65535");
        this.port=port; this.loopback=loopback; this.login=Protocol.field(login); this.host=Protocol.field(host);
        this.nickname=nickname; this.group=group; this.listener=listener;
    }
    public void start() throws IOException {
        if (closed || udp!=null) throw new IOException("服务已启动或关闭");
        try {
            InetAddress address=InetAddress.getByName(loopback?"127.0.0.1":"0.0.0.0");
            udp=new DatagramSocket(null); udp.setReuseAddress(false); udp.setBroadcast(!loopback); udp.bind(new InetSocketAddress(address,port));
            if(!loopback && socketBinding!=null) socketBinding.bind(udp);
            tcp=new ServerSocket(); tcp.setReuseAddress(false); tcp.bind(new InetSocketAddress(address,port),4);
            Thread rx=new Thread(this::receiveLoop,"feiq-udp"), accept=new Thread(this::acceptLoop,"feiq-files");
            rx.setDaemon(true); accept.setDaemon(true); rx.start(); accept.start();
            timer.scheduleWithFixedDelay(this::tick,1,1,TimeUnit.SECONDS); refresh();
        } catch (IOException | RuntimeException e) { close(); throw e; }
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
    private byte[] encode(int command,String body,String extra) { return Protocol.encode(sequence.incrementAndGet(),login,host,command|Protocol.UTF8,body,extra); }
    private void send(byte[] bytes,InetSocketAddress to) throws IOException {
        if (closed || udp==null) throw new IOException("尚未连接局域网");
        if (!(to.getAddress() instanceof Inet4Address) || (loopback && !to.getAddress().isLoopbackAddress())) throw new IOException("无效的目标地址");
        synchronized(udp) {
            // 适配层只在实体网络改变后重绑，同一轮发现/ACK 复用绑定。
            if(!loopback && socketBinding!=null) socketBinding.bind(udp);
            udp.send(new DatagramPacket(bytes,bytes.length,to));
        }
    }
    public void probe(InetSocketAddress to) throws IOException {
        send(encode(Protocol.ENTRY|Protocol.CAP_UTF8|Protocol.FILE,Protocol.field(nickname),Protocol.field(group)),to);
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
        refreshAt=System.nanoTime();
        Set<InetAddress> addresses=new HashSet<>();
        if (!loopback) {
            Collection<InetAddress> physical=socketBinding==null?null:socketBinding.broadcasts();
            if(physical!=null) addresses.addAll(physical);
            else {
            addresses.add(InetAddress.getByName("255.255.255.255"));
            Enumeration<NetworkInterface> interfaces=NetworkInterface.getNetworkInterfaces();
            if (interfaces!=null) while (interfaces.hasMoreElements()) {
                NetworkInterface net=interfaces.nextElement(); if (!net.isUp() || net.isLoopback()) continue;
                for (InterfaceAddress a:net.getInterfaceAddresses()) if (a.getBroadcast()!=null) addresses.add(a.getBroadcast());
            }
            }
        }
        for (InetAddress address:addresses) try { probe(new InetSocketAddress(address,port)); } catch (IOException e) { listener.error("广播不可用，可尝试手动添加 IP"); }
        Set<InetSocketAddress> targets; synchronized(gate) { targets=new LinkedHashSet<>(remembered); }
        for(Peer peer:peers()) targets.add(peer.endpoint);
        for(InetSocketAddress target:targets) try { probe(target); } catch(IOException ignored) { }
    }
    private void receiveLoop() {
        byte[] buffer=new byte[60001];
        while (!closed) try {
            DatagramPacket datagram=new DatagramPacket(buffer,buffer.length); udp.receive(datagram);
            Protocol.Packet packet=Protocol.parse(Arrays.copyOf(datagram.getData(),datagram.getLength()));
            if (packet==null || !(datagram.getAddress() instanceof Inet4Address)) continue;
            if (packet.login.equals(login) && packet.host.equals(host) && datagram.getPort()==port) continue;
            handle(packet,new InetSocketAddress(datagram.getAddress(),datagram.getPort()));
        } catch (IOException e) { if (!closed) { listener.error("网络接收失败："+e.getMessage()); try { Thread.sleep(100); } catch (InterruptedException stop) { return; } } }
          catch (RuntimeException e) { if (!closed) listener.error("消息处理失败："+e.getMessage()); }
    }
    private void handle(Protocol.Packet p,InetSocketAddress from) throws IOException {
        synchronized (gate) {
            if (p.mode()==Protocol.ACK) {
                try {
                    Pending item=pending.get(Long.parseLong(p.body));
                    if (item!=null && item.peer.endpoint.equals(from) && item.peer.login.equals(p.login)) {
                        pending.remove(item.message.number); item.message.state="已送达"; listener.message(item.peer,item.message.copy(),true);
                    }
                } catch (NumberFormatException ignored) { } return;
            }
            if (p.mode()==Protocol.RELEASE) {
                try { String prefix=Long.parseLong(p.body)+":"; offers.entrySet().removeIf(e->{boolean remove=e.getKey().startsWith(prefix)&&e.getValue().address.equals(from.getAddress());if(remove)endOffer(e.getValue(),"对方已拒绝");return remove;}); }
                catch (NumberFormatException ignored) { } return;
            }
            if (p.mode()!=Protocol.ENTRY && p.mode()!=Protocol.ANSWER && p.mode()!=Protocol.ABSENCE && p.mode()!=Protocol.EXIT && p.mode()!=Protocol.MESSAGE) return;
            Peer peer=new Peer(from,p.login,p.host,p.login,"我的局域网");
            Peer existing=peers.get(peer.id());
            if (existing!=null) peer=existing;
            else { if (peers.size()>=1000) return; peers.put(peer.id(),peer); }
            if (p.mode()==Protocol.ENTRY || p.mode()==Protocol.ANSWER || p.mode()==Protocol.ABSENCE) {
                peer.name=p.body.trim().isEmpty()?p.login:Protocol.prefix(p.body,128);
                peer.group=Protocol.prefix(p.extra,128);
            }
            peer.utf8|=(p.command&(Protocol.UTF8|Protocol.CAP_UTF8))!=0; peer.online=p.mode()!=Protocol.EXIT; peer.lastSeen=System.nanoTime();
            listener.peersChanged();
            if (p.mode()==Protocol.ENTRY) send(encode(Protocol.ANSWER|Protocol.FILE|Protocol.CAP_UTF8|(peer.utf8?Protocol.UTF8:0),Protocol.field(nickname),Protocol.field(group)),from);
            if (p.mode()!=Protocol.MESSAGE) return;
            if ((p.command&Protocol.CHECK)!=0) send(encode(Protocol.ACK,Long.toString(p.number),null),from);
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
        if (text==null) text="";
        if (text.indexOf('\0')>=0 || (text.trim().isEmpty() && paths.isEmpty())) throw new IOException("请输入消息或选择文件");
        if (paths.size()>20) throw new IOException("每次最多发送 20 个文件");
        Message m=new Message(); m.number=sequence.incrementAndGet(); m.time=System.currentTimeMillis(); m.text=text; m.state="等待确认"; m.outgoing=true;
        Map<String,Offer> additions=new HashMap<>();
        for (File file:paths) {
            if (!file.isFile() || !Protocol.safeName(file.getName())) throw new IOException("文件不可读、名称不安全或过长");
            int id=m.files.size(); m.files.add(new Protocol.Attachment(id,file.getName(),file.length(),file.lastModified()/1000));
            m.localPaths.add(file.getCanonicalPath());
            Offer o=new Offer(); o.file=file; o.size=file.length(); o.modified=file.lastModified(); o.address=peer.endpoint.getAddress(); o.expires=System.nanoTime()+TimeUnit.MINUTES.toNanos(30); additions.put(m.number+":"+id,o);
            o.peer=peer; o.packet=m.number; o.id=id;
        }
        byte[] bytes=Protocol.encode(m.number,login,host,Protocol.MESSAGE|Protocol.CHECK|(peer.utf8?Protocol.UTF8:0)|(paths.isEmpty()?0:Protocol.FILE),text,paths.isEmpty()?null:Protocol.files(m.files));
        synchronized (gate) {
            if (closed || udp==null) throw new IOException("尚未连接局域网");
            if (pending.size()>=256 || offers.size()+additions.size()>1000) throw new IOException("待处理任务过多，请稍后再试");
            Pending item=new Pending(); item.peer=peer; item.message=m; item.bytes=bytes; item.last=System.nanoTime(); pending.put(m.number,item); offers.putAll(additions);
            // 先发布记录再发送，避免本机 ACK 先到而被初始“等待确认”覆盖。
            listener.message(peer,m.copy(),false);
            try { send(bytes,peer.endpoint); }
            catch (IOException e) { pending.remove(m.number); offers.keySet().removeAll(additions.keySet()); m.state="发送失败"; listener.message(peer,m.copy(),true); throw e; }
        }
        return m.copy();
    }
    private void tick() {
        if (closed) return;
        try {
            long now=System.nanoTime(); boolean changed=false;
            synchronized (gate) {
                Iterator<Pending> it=pending.values().iterator();
                while (it.hasNext()) {
                    Pending p=it.next(); if (now-p.last<TimeUnit.SECONDS.toNanos(2)) continue;
                    if (p.attempts>=4) { it.remove(); p.message.state="未确认送达，请检查网络后重发"; listener.message(p.peer,p.message.copy(),true); }
                    else { p.attempts++; p.last=now; try { send(p.bytes,p.peer.endpoint); } catch (IOException ignored) { } }
                }
                seen.entrySet().removeIf(e->now-e.getValue()>TimeUnit.MINUTES.toNanos(10));
                offers.entrySet().removeIf(e->{if(e.getValue().expires>=now)return false;endOffer(e.getValue(),"邀请已过期，请重新发送");return true;});
                for (Peer p:peers.values()) if (p.online && now-p.lastSeen>TimeUnit.SECONDS.toNanos(120)) { p.online=false; changed=true; }
            }
            if (changed) listener.peersChanged();
            if (now-refreshAt>TimeUnit.SECONDS.toNanos(30)) refresh();
        } catch (IOException | RuntimeException e) { if (!closed) listener.error("网络维护失败："+e.getMessage()); }
    }
    private void acceptLoop() {
        while (!closed) try {
            Socket client=tcp.accept();
            synchronized (gate) { if (connections.size()>=4) { client.close(); continue; } connections.add(client); }
            try { fileWorkers.execute(()->serve(client)); } catch (RejectedExecutionException e) { synchronized (gate) { connections.remove(client); } client.close(); }
        } catch (IOException e) { if (!closed) listener.error("文件监听失败："+e.getMessage()); }
    }
    private void serve(Socket client) {
        Offer active=null; int percent=0; long version=0;
        try (Socket socket=client) {
            socket.setSoTimeout(5000); ByteArrayOutputStream request=new ByteArrayOutputStream();
            int c; while (request.size()<4096 && (c=socket.getInputStream().read())!=-1) { request.write(c); if (c==0) break; }
            Protocol.Packet packet=Protocol.parse(request.toByteArray()); if (packet==null || packet.mode()!=Protocol.GET_FILE) return;
            String[] f=packet.body.split(":"); if (f.length<3) return;
            long number=Long.parseLong(f[0],16), id=Long.parseLong(f[1],16), offset=Long.parseLong(f[2],16); Offer offer;
            synchronized (gate) { offer=offers.get(number+":"+id); }
            if (offer==null || offset<0 || offset>offer.size || !offer.address.equals(socket.getInetAddress()) || offer.expires<System.nanoTime()) return;
            synchronized(gate) { version=++offer.progressVersion; active=offer; }
            if (!offer.file.isFile() || offer.file.length()!=offer.size || offer.file.lastModified()!=offer.modified) throw new IOException("原文件已改变");
            percent=offer.size==0?0:(int)(offset*100.0/offer.size); progress(offer,version,percent,"发送中");
            // 只限制无进展的连接，正常大文件不再因总时长超过五分钟而中断。
            AtomicLong lastWrite=new AtomicLong(System.nanoTime());
            ScheduledFuture<?> deadline=timer.scheduleWithFixedDelay(()->{ if(System.nanoTime()-lastWrite.get()>TimeUnit.SECONDS.toNanos(30)) try { socket.close(); } catch (IOException ignored) { } },5,5,TimeUnit.SECONDS);
            try (RandomAccessFile input=new RandomAccessFile(offer.file,"r")) {
                input.seek(offset); byte[] buffer=new byte[65536]; int read; long left=offer.size-offset;
                while (left>0 && (read=input.read(buffer,0,(int)Math.min(buffer.length,left)))!=-1) {
                    socket.getOutputStream().write(buffer,0,read); left-=read; lastWrite.set(System.nanoTime());
                    synchronized(gate) { offer.expires=System.nanoTime()+TimeUnit.MINUTES.toNanos(30); }
                    int next=(int)((offer.size-left)*100.0/offer.size); if(next!=percent) {percent=next;progress(offer,version,percent,"发送中");}
                }
                if(left!=0) throw new IOException("发送中断");
                progress(offer,version,100,"已发送（非保存确认）"); active=null;
            } finally { deadline.cancel(false); }
        } catch (IOException | RuntimeException ignored) { /* 对方取消、报价失效和非法请求都关闭连接。 */ }
        finally { if(active!=null) progress(active,version,percent,"发送中断，等待对方重试"); synchronized (gate) { connections.remove(client); } }
    }
    private void progress(Offer offer,long version,int percent,String state) {
        synchronized(gate) {if(version!=offer.progressVersion)return;offer.percent=percent;offer.state=state;if(!closed)listener.fileProgress(offer.peer,offer.packet,offer.id,percent,state);}
    }
    private void endOffer(Offer offer,String state) {if(!"已发送（非保存确认）".equals(offer.state))progress(offer,++offer.progressVersion,offer.percent,state);}
    public void reject(Peer peer,long packet) throws IOException { send(encode(Protocol.RELEASE,Long.toString(packet),null),peer.endpoint); }
    public boolean offering(String peer,long packet,long file) {
        synchronized(gate) {Offer offer=offers.get(packet+":"+file);return !closed&&offer!=null&&offer.peer.id().equals(peer)&&offer.expires>=System.nanoTime();}
    }
    public void revokeOffers() throws IOException {
        synchronized(gate) {
            // 已接入的文件连接可能正在读副本，不能先删文件再中断对端。
            if(!connections.isEmpty()) throw new IOException("仍有文件连接，请待传输结束后清理发送缓存");
            for(Offer offer:offers.values())endOffer(offer,"邀请已撤销");offers.clear();
        }
    }
    public boolean isOffered(File file) {
        synchronized(gate) { for(Offer offer:offers.values()) if(offer.file.equals(file)&&offer.expires>=System.nanoTime()) return true; return false; }
    }
    private static final Set<String> receivingPaths=new HashSet<>();
    public void receiveFile(Peer peer,long packet,Protocol.Attachment file,File destination,Transfer transfer,Progress progress) throws IOException {
        String key=destination.getCanonicalPath();
        synchronized(receivingPaths) { if(!receivingPaths.add(key)) throw new IOException("同一目标正在接收，请等待当前任务完成"); }
        // 文件锁在关闭后才能移动；目标锁必须覆盖最终提交，避免两个任务在这个间隙串用断点。
        try { receiveFileLocked(peer,packet,file,destination,transfer,progress); }
        finally { synchronized(receivingPaths) { receivingPaths.remove(key); } }
    }
    private void receiveFileLocked(Peer peer,long packet,Protocol.Attachment file,File destination,Transfer transfer,Progress progress) throws IOException {
        if (!Protocol.safeName(file.name) || file.size<0) throw new IOException("文件名或长度不安全");
        if (loopback && !peer.endpoint.getAddress().isLoopbackAddress()) throw new IOException("测试模式不能访问局域网");
        if (destination.exists()) throw new IOException("目标已存在，不会覆盖");
        File part=partialFile(peer,packet,file,destination);
        Socket socket=new Socket(Proxy.NO_PROXY);
        synchronized (gate) { if (closed || connections.size()>=4) { socket.close(); throw new IOException("服务已关闭或传输任务过多"); } connections.add(socket); }
        boolean owned=false,complete=false;
        try (Socket active=socket;
             java.nio.channels.FileChannel output=java.nio.channels.FileChannel.open(part.toPath(),java.nio.file.StandardOpenOption.CREATE,java.nio.file.StandardOpenOption.WRITE,java.nio.file.LinkOption.NOFOLLOW_LINKS);
             java.nio.channels.FileLock lock=output.tryLock()) {
            if(lock==null) throw new IOException("同一文件正在接收");
            owned=true; long offset=output.size();
            if(offset>file.size) { output.truncate(0); offset=0; }
            output.position(offset); requireSpace(destination.getAbsoluteFile().getParentFile(),file.size-offset);
            transfer.attach(active);
            if(!loopback && socketBinding!=null) socketBinding.bind(active);
            active.connect(peer.endpoint,8000); active.setSoTimeout(15000);
            byte[] request=encode(Protocol.GET_FILE,Long.toHexString(packet)+":"+Integer.toHexString(file.id)+":"+Long.toHexString(offset)+":",null);
            active.getOutputStream().write(request);
            OutputStream out=new BufferedOutputStream(java.nio.channels.Channels.newOutputStream(output),65536);
            try {
                byte[] buffer=new byte[65536]; long count=offset; int last=-1;
                if(progress!=null) progress.update(file.size==0?0:(int)(count*100.0/file.size));
                while (count<file.size) {
                    if (transfer.isCancelled()) throw new IOException("已取消传输");
                    int read=active.getInputStream().read(buffer,0,(int)Math.min(buffer.length,file.size-count));
                    if (read<0) throw new IOException("文件中断或邀请失效；已保留断点，请在发送方在线且邀请有效时重试");
                    out.write(buffer,0,read);
                    count+=read; int percent=(int)(count*100.0/file.size);
                    if (percent!=last) { last=percent; if (progress!=null) progress.update(percent); }
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
    @Override public void close() {
        synchronized (gate) {
            if (closed) return;
            for (Peer p:peers.values()) try { send(encode(Protocol.EXIT,nickname,null),p.endpoint); } catch (IOException ignored) { }
            closed=true; if (udp!=null) udp.close(); if (tcp!=null) try { tcp.close(); } catch (IOException ignored) { }
            for (Socket socket:connections) try { socket.close(); } catch (IOException ignored) { }
            connections.clear(); offers.clear();
            for (Pending p:pending.values()) { p.message.state="已断开，未确认送达"; listener.message(p.peer,p.message.copy(),true); } pending.clear();
        }
        timer.shutdownNow(); fileWorkers.shutdownNow();
    }
}
