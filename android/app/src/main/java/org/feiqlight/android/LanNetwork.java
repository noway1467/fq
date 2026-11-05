package org.feiqlight.android;

import android.net.*;
import org.feiqlight.android.core.LanNode;
import java.io.IOException;
import java.net.*;
import java.util.*;

/** 仅绑定飞Q自己的套接字，不更改系统代理、VPN 或其他应用的路由。 */
final class LanNetwork implements LanNode.SocketBinding {
    private final ConnectivityManager manager;
    private final Map<DatagramSocket,Network> bound=new WeakHashMap<>();
    private Network cachedNetwork;
    private LinkProperties cachedLinks;
    private Object cachedKey;
    private final java.util.concurrent.atomic.AtomicLong revision=new java.util.concurrent.atomic.AtomicLong(1);
    private long checkedRevision;
    private long checkedAt;
    private IOException cachedFailure;
    LanNetwork(ConnectivityManager manager) { this.manager=manager; }
    @Override public void changed() {revision.incrementAndGet();}
    static boolean physical(NetworkCapabilities capabilities) {
        return capabilities!=null && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            && !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            && (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)||capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET));
    }
    synchronized Network select() throws IOException {
        long version=revision.get();
        if(version!=checkedRevision || System.nanoTime()-checkedAt>java.util.concurrent.TimeUnit.SECONDS.toNanos(5)) {
            // 网络回调主动失效，短 TTL 补漏；同一批发现/ACK 不再逐包调用系统 Binder。
            checkedRevision=version;checkedAt=System.nanoTime();cachedFailure=null;cachedNetwork=null;cachedLinks=null;cachedKey=null;
            try {
                cachedNetwork=scan();
                if(cachedNetwork!=null) {
                    cachedLinks=manager.getLinkProperties(cachedNetwork);
                    if(cachedLinks==null) {cachedNetwork=null;}
                    else {
                        List<String> addresses=new ArrayList<>();
                        for(LinkAddress link:cachedLinks.getLinkAddresses())if(link.getAddress() instanceof Inet4Address)addresses.add(link.toString());
                        Collections.sort(addresses);
                        if(addresses.isEmpty())cachedNetwork=null;
                        else cachedKey=Arrays.asList(cachedNetwork,cachedLinks.getInterfaceName(),addresses);
                    }
                }
            }
            catch(IOException e){cachedFailure=e;}
            catch(RuntimeException e){cachedFailure=new IOException("无法读取局域网状态，请检查系统网络权限",e);}
        }
        if(cachedFailure!=null)throw new IOException(cachedFailure.getMessage(),cachedFailure);
        return cachedNetwork;
    }
    @Override public synchronized Object networkKey() throws IOException {select();return cachedKey;}
    private Network scan() throws IOException {
        if(manager==null) return null;
        Network chosen=null; int best=-1; boolean vpn=false;
        for(Network network:manager.getAllNetworks()) {
            NetworkCapabilities capabilities=manager.getNetworkCapabilities(network);
            if(capabilities!=null && capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) vpn=true;
            if(!physical(capabilities)) continue;
            LinkProperties links=manager.getLinkProperties(network);
            if(links==null || links.getLinkAddresses().stream().noneMatch(a->a.getAddress() instanceof Inet4Address)) continue;
            // 无互联网的局域网也能通信，不要求 INTERNET / VALIDATED 能力。
            int score=capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)?2:1;
            if(score>best) {chosen=network;best=score;}
        }
        if(chosen==null && vpn) throw new IOException("VPN 下未找到可直连的 Wi-Fi/以太网。请连接局域网，并在 VPN 中允许局域网或将飞Q排除；强制 VPN 模式可能阻止直连。");
        return chosen;
    }
    @Override public synchronized void bind(DatagramSocket socket) throws IOException {
        Network network=select();
        if(network==null) throw new IOException("网络已断开，等待 Wi-Fi/以太网恢复");
        if(network.equals(bound.get(socket)))return;
        try {network.bindSocket(socket);bound.put(socket,network);} catch(IOException e) {changed();throw blocked(e);}
    }
    @Override public void bind(Socket socket) throws IOException {
        Network network=select(); if(network==null)throw new IOException("网络已断开，等待 Wi-Fi/以太网恢复");
        try {network.bindSocket(socket);} catch(IOException e) {changed();throw blocked(e);}
    }
    private IOException blocked(IOException cause) { return new IOException("局域网直连被系统限制，请检查 VPN 的允许局域网/应用排除与阻止非 VPN 连接设置。",cause); }
    @Override public synchronized Collection<InetAddress> broadcasts() throws IOException {
        Network network=select(); if(network==null) return Collections.emptyList();
        LinkProperties links=cachedLinks; if(links==null) throw new IOException("局域网已切换，请稍后刷新");
        List<InetAddress> result=new ArrayList<>();
        for(LinkAddress link:links.getLinkAddresses()) {
            if(!(link.getAddress() instanceof Inet4Address)||link.getPrefixLength()>30) continue;
            byte[] address=link.getAddress().getAddress(); int prefix=link.getPrefixLength();
            for(int i=0;i<4;i++) {int bits=Math.max(0,Math.min(8,prefix-i*8));address[i]|=(byte)(255>>>bits);}
            result.add(InetAddress.getByAddress(address));
        }
        return result;
    }
}
