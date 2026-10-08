package org.feiqlight.android;

import android.net.*;
import org.feiqlight.android.core.LanNode;
import java.io.IOException;
import java.net.*;
import java.util.*;

/** 仅绑定飞Q自己的套接字，不更改系统代理、VPN 或其他应用的路由。 */
final class LanNetwork implements LanNode.SocketBinding {
    private final ConnectivityManager manager;
    LanNetwork(ConnectivityManager manager) { this.manager=manager; }
    static boolean physical(NetworkCapabilities capabilities) {
        return capabilities!=null && capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
            && !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            && (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)||capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET));
    }
    Network select() throws IOException {
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
    @Override public void bind(DatagramSocket socket) throws IOException {
        Network network=select(); if(network!=null) try {network.bindSocket(socket);} catch(IOException e) {throw blocked(e);}
    }
    @Override public void bind(Socket socket) throws IOException {
        Network network=select(); if(network!=null) try {network.bindSocket(socket);} catch(IOException e) {throw blocked(e);}
    }
    private IOException blocked(IOException cause) { return new IOException("局域网直连被系统限制，请检查 VPN 的允许局域网/应用排除与阻止非 VPN 连接设置。",cause); }
    @Override public Collection<InetAddress> broadcasts() throws IOException {
        Network network=select(); if(network==null) return null;
        LinkProperties links=manager.getLinkProperties(network); if(links==null) throw new IOException("局域网已切换，请稍后刷新");
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
