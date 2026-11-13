package org.feiqlight.android;

import android.net.*;
import org.feiqlight.android.core.LanNode;
import java.io.IOException;
import java.net.*;
import java.util.*;

/** 仅绑定飞Q自己的套接字，不更改系统代理、VPN 或其他应用的路由。 */
final class LanNetwork implements LanNode.SocketBinding {
    static final String DISCONNECTED_STATUS="网络已断开，等待 Wi-Fi、热点或以太网恢复";
    static final class LocalLink {
        final Inet4Address address;
        final int prefix;
        LocalLink(Inet4Address address,int prefix) { this.address=address; this.prefix=prefix; }
    }
    static final class LocalInterface {
        final String name;
        final boolean up, loopback, pointToPoint, virtual;
        final List<LocalLink> links;
        LocalInterface(String name,boolean up,boolean loopback,boolean pointToPoint,boolean virtual,List<LocalLink> links) {
            this.name=name; this.up=up; this.loopback=loopback; this.pointToPoint=pointToPoint; this.virtual=virtual;
            this.links=Collections.unmodifiableList(new ArrayList<>(links));
        }
    }
    interface InterfaceLookup { List<LocalInterface> list() throws IOException; }
    private static final String[] HOTSPOT_PREFIXES={"ap","swlan","softap","wlan","wifi","hotspot","tether","rndis","usb","ncm","p2p","eth"};
    private static final String[] BLOCKED_PREFIXES={"rmnet","ccmni","pdp","wwan","clat","v4-","tun","tap","ppp","ipsec","wg","sit","ip6tnl","gre","dummy","lo","svnet","seth","qmi"};
    private final ConnectivityManager manager;
    private final InterfaceLookup interfaceLookup;
    private final Map<DatagramSocket,Network> bound=new WeakHashMap<>();
    private Network cachedNetwork;
    private LinkProperties cachedLinks;
    private boolean cachedLocal;
    private List<InetAddress> cachedLocalBroadcasts=Collections.emptyList();
    private Object cachedKey;
    private final java.util.concurrent.atomic.AtomicLong revision=new java.util.concurrent.atomic.AtomicLong(1);
    private long checkedRevision;
    private long checkedAt;
    private IOException cachedFailure;
    LanNetwork(ConnectivityManager manager) { this(manager,LanNetwork::systemInterfaces); }
    LanNetwork(ConnectivityManager manager,InterfaceLookup interfaceLookup) { this.manager=manager; this.interfaceLookup=interfaceLookup; }
    @Override public void changed() {revision.incrementAndGet();}
    static boolean physical(NetworkCapabilities capabilities) {
        return capabilities!=null && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            && !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            && (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)||capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET));
    }
    private static List<LocalInterface> systemInterfaces() throws IOException {
        Enumeration<NetworkInterface> interfaces=NetworkInterface.getNetworkInterfaces();
        if(interfaces==null) return Collections.emptyList();
        List<LocalInterface> result=new ArrayList<>();
        while(interfaces.hasMoreElements()) {
            NetworkInterface net=interfaces.nextElement();
            try {
                List<LocalLink> links=new ArrayList<>();
                for(InterfaceAddress item:net.getInterfaceAddresses())
                    if(item.getAddress() instanceof Inet4Address) links.add(new LocalLink((Inet4Address)item.getAddress(),item.getNetworkPrefixLength()));
                result.add(new LocalInterface(net.getName(),net.isUp(),net.isLoopback(),net.isPointToPoint(),net.isVirtual(),links));
            } catch(SocketException ignored) {
                // 跳过瞬态不可读网卡，避免单个接口波动影响其他可用热点或网卡。
            }
        }
        return result;
    }
    private static boolean hasPrefix(String name,String[] prefixes) {
        for(String prefix:prefixes) if(name.startsWith(prefix)) return true;
        return false;
    }
    private static boolean isHotspotInterface(LocalInterface item,Set<String> excluded) {
        if(!item.up || item.loopback || item.pointToPoint || item.virtual || item.name==null) return false;
        String name=item.name.trim().toLowerCase(Locale.ROOT);
        if(name.isEmpty() || excluded.contains(name) || hasPrefix(name,BLOCKED_PREFIXES)) return false;
        return hasPrefix(name,HOTSPOT_PREFIXES);
    }
    private static InetAddress broadcastAddress(Inet4Address address,int prefix,int minPrefix) throws UnknownHostException {
        if(prefix<minPrefix || prefix>30) return null;
        byte[] bytes=address.getAddress();
        for(int i=0;i<4;i++) {int bits=Math.max(0,Math.min(8,prefix-i*8));bytes[i]|=(byte)(255>>>bits);}
        return InetAddress.getByAddress(bytes);
    }
    synchronized Network select() throws IOException {
        long version=revision.get();
        if(version!=checkedRevision || System.nanoTime()-checkedAt>java.util.concurrent.TimeUnit.SECONDS.toNanos(5)) {
            // 网络回调主动失效，短 TTL 补漏；同一批发现/ACK 不再逐包调用系统 Binder。
            checkedRevision=version;checkedAt=System.nanoTime();cachedFailure=null;cachedNetwork=null;cachedLinks=null;cachedLocal=false;cachedLocalBroadcasts=Collections.emptyList();cachedKey=null;
            try {
                scan();
            }
            catch(IOException e){cachedFailure=e;}
            catch(RuntimeException e){cachedFailure=new IOException("无法读取局域网状态，请检查系统网络权限",e);}
        }
        if(cachedFailure!=null)throw new IOException(cachedFailure.getMessage(),cachedFailure);
        return cachedNetwork;
    }
    @Override public synchronized Object networkKey() throws IOException {select();return cachedKey;}
    private void scan() throws IOException {
        Network chosen=null; LinkProperties chosenLinks=null; int best=-1; boolean vpn=false;
        Set<String> nonLanInterfaces=new HashSet<>();
        if(manager!=null) {
            for(Network network:manager.getAllNetworks()) {
                NetworkCapabilities capabilities=manager.getNetworkCapabilities(network);
                if(capabilities!=null && capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) vpn=true;
                LinkProperties links=manager.getLinkProperties(network);
                if(!physical(capabilities)) {
                    if(links!=null && links.getInterfaceName()!=null) nonLanInterfaces.add(links.getInterfaceName().trim().toLowerCase(Locale.ROOT));
                    continue;
                }
                if(links==null || links.getLinkAddresses().stream().noneMatch(a->a.getAddress() instanceof Inet4Address)) {
                    if(links!=null && links.getInterfaceName()!=null) nonLanInterfaces.add(links.getInterfaceName().trim().toLowerCase(Locale.ROOT));
                    continue;
                }
                // 无互联网的局域网也能通信，不要求 INTERNET / VALIDATED 能力。
                int score=capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)?2:1;
                if(score>best) {chosen=network;chosenLinks=links;best=score;}
            }
        }
        if(chosen!=null) {
            List<String> addresses=new ArrayList<>();
            for(LinkAddress link:chosenLinks.getLinkAddresses())if(link.getAddress() instanceof Inet4Address)addresses.add(link.toString());
            Collections.sort(addresses);
            cachedNetwork=chosen;cachedLinks=chosenLinks;
            cachedKey=Arrays.asList(chosen,chosenLinks.getInterfaceName(),addresses);
            return;
        }
        // Android 个人热点/USB 共享不作为客户端 Network 出现在 ConnectivityManager 中，回退识别本地共享私网接口。
        List<String> localKeys=new ArrayList<>();
        Set<InetAddress> localBroadcasts=new LinkedHashSet<>();
        for(LocalInterface item:interfaceLookup.list()) {
            if(!isHotspotInterface(item,nonLanInterfaces)) continue;
            String normalizedName=item.name.trim().toLowerCase(Locale.ROOT);
            for(LocalLink link:item.links) {
                if(link.address==null || !link.address.isSiteLocalAddress()) continue;
                InetAddress broadcast=broadcastAddress(link.address,link.prefix,8);
                if(broadcast==null) continue;
                localKeys.add(normalizedName+":"+link.address.getHostAddress()+"/"+link.prefix);
                localBroadcasts.add(broadcast);
            }
        }
        if(!localKeys.isEmpty()) {
            Collections.sort(localKeys);
            cachedLocal=true;
            cachedLocalBroadcasts=Collections.unmodifiableList(new ArrayList<>(localBroadcasts));
            cachedKey=Arrays.asList("local",localKeys);
            return;
        }
        if(vpn) throw new IOException("VPN 下未找到可直连的 Wi-Fi/以太网或热点。请连接局域网或开启热点，并在 VPN 中允许局域网或将飞Q排除；强制 VPN 模式可能阻止直连。");
    }
    @Override public synchronized void bind(DatagramSocket socket) throws IOException {
        Network network=select();
        if(network==null) {
            if(cachedLocal) return;
            throw new IOException(DISCONNECTED_STATUS);
        }
        if(network.equals(bound.get(socket)))return;
        try {network.bindSocket(socket);bound.put(socket,network);} catch(IOException e) {changed();throw blocked(e);}
    }
    @Override public synchronized void bind(Socket socket) throws IOException {
        Network network=select();
        if(network==null) {
            if(cachedLocal) return;
            throw new IOException(DISCONNECTED_STATUS);
        }
        try {network.bindSocket(socket);} catch(IOException e) {changed();throw blocked(e);}
    }
    private IOException blocked(IOException cause) { return new IOException("局域网直连被系统限制，请检查 VPN 的允许局域网/应用排除与阻止非 VPN 连接设置。",cause); }
    @Override public synchronized Collection<InetAddress> broadcasts() throws IOException {
        Network network=select();
        if(network==null) return cachedLocal?cachedLocalBroadcasts:Collections.emptyList();
        LinkProperties links=cachedLinks; if(links==null) throw new IOException("局域网已切换，请稍后刷新");
        Set<InetAddress> result=new LinkedHashSet<>();
        for(LinkAddress link:links.getLinkAddresses()) {
            if(!(link.getAddress() instanceof Inet4Address)) continue;
            InetAddress broadcast=broadcastAddress((Inet4Address)link.getAddress(),link.getPrefixLength(),1);
            if(broadcast!=null) result.add(broadcast);
        }
        return new ArrayList<>(result);
    }
}
