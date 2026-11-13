package org.feiqlight.android;

import android.content.Context;
import android.net.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.*;
import java.net.*;
import java.io.IOException;
import java.util.*;
import java.util.concurrent.atomic.AtomicReference;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35)
public final class LanNetworkTest {
    private static final LanNetwork.InterfaceLookup NO_LOCAL=Collections::emptyList;
    @org.robolectric.annotation.Implements(ConnectivityManager.class)
    public static class CountingConnectivity extends ShadowConnectivityManager {
        int queries;
        Runnable duringQuery;
        @org.robolectric.annotation.Implementation protected Network[] getAllNetworks() {queries++;if(duringQuery!=null){Runnable action=duringQuery;duringQuery=null;action.run();}return super.getAllNetworks();}
    }
    @org.robolectric.annotation.Implements(Network.class)
    public static class CountingNetwork extends ShadowNetwork {
        int binds;
        @org.robolectric.annotation.Implementation protected void bindSocket(DatagramSocket socket) {binds++;super.bindSocket(socket);}
    }
    @Test @Config(shadows={CountingConnectivity.class,CountingNetwork.class})
    public void discoveryBurstDoesNotQueryAndRebindTheSameSocketForEveryPacket() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network wifi=add(cm,904,NetworkCapabilities.TRANSPORT_WIFI,"192.0.2.8",24);LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        try(DatagramSocket udp=new DatagramSocket(null)) {for(int i=0;i<200;i++)route.bind(udp);}
        CountingConnectivity manager=org.robolectric.shadow.api.Shadow.extract(cm);CountingNetwork network=org.robolectric.shadow.api.Shadow.extract(wifi);
        assertEquals("同一轮发现重复查询全部系统网络",1,manager.queries);assertEquals("同一 UDP socket 重复绑定",1,network.binds);
    }
    private Network add(ConnectivityManager cm,int id,int transport,String ip,int prefix) throws Exception {
        Network network=ShadowNetwork.newInstance(id);NetworkCapabilities caps=ShadowNetworkCapabilities.newInstance();
        shadowOf(caps).addTransportType(transport);
        if(transport==NetworkCapabilities.TRANSPORT_VPN)shadowOf(caps).removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
        else shadowOf(caps).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
        shadowOf(caps).removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET);
        shadowOf(caps).removeCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);
        shadowOf(cm).addNetwork(network,null);shadowOf(cm).setNetworkCapabilities(network,caps);
        java.lang.reflect.Constructor<LinkAddress> constructor=LinkAddress.class.getDeclaredConstructor(InetAddress.class,int.class);constructor.setAccessible(true);
        LinkProperties links=new LinkProperties();LinkProperties.class.getDeclaredMethod("addLinkAddress",LinkAddress.class).invoke(links,constructor.newInstance(InetAddress.getByName(ip),prefix));shadowOf(cm).setLinkProperties(network,links);return network;
    }
    @Test public void vpnDoesNotStealLanUdpTcpOrBroadcastAndWifiSwitchRebinds() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,901,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        Network wifi=add(cm,902,NetworkCapabilities.TRANSPORT_WIFI,"192.0.2.8",24);LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        assertEquals(wifi,route.select());assertEquals("192.0.2.255",route.broadcasts().iterator().next().getHostAddress());
        try(DatagramSocket udp=new DatagramSocket(null);Socket tcp=new Socket()) {
            route.bind(udp);route.bind(tcp);assertTrue(shadowOf(wifi).isSocketBound(udp));assertTrue(shadowOf(wifi).isSocketBound(tcp));assertEquals(0,shadowOf(vpn).boundSocketCount());
            shadowOf(cm).removeNetwork(wifi);Network next=add(cm,903,NetworkCapabilities.TRANSPORT_ETHERNET,"198.51.100.12",23);
            route.changed();
            route.bind(udp);assertTrue(shadowOf(next).isSocketBound(udp));assertEquals("198.51.101.255",route.broadcasts().iterator().next().getHostAddress());
            shadowOf(cm).removeNetwork(next);route.changed();try {route.bind(tcp);fail("VPN 单网络被当成局域网");}catch(IOException expected){assertTrue(expected.getMessage().contains("VPN"));}
        }
        assertNull("不应更改进程/系统默认路由",cm.getBoundNetworkForProcess());
    }
    @Test public void loopbackNodesNeverInvokePlatformBinding() throws Exception {
        try(java.net.ServerSocket server=new java.net.ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            int port=server.getLocalPort();server.close();
            org.feiqlight.android.core.LanNode.Listener listener=new org.feiqlight.android.core.LanNode.Listener(){public void peersChanged(){}public void message(org.feiqlight.android.core.LanNode.Peer p,org.feiqlight.android.core.LanNode.Message m,boolean update){}public void error(String text){}};
            try(org.feiqlight.android.core.LanNode node=new org.feiqlight.android.core.LanNode(port,true,"test","test","test","test",listener)) {
                node.setSocketBinding(new org.feiqlight.android.core.LanNode.SocketBinding(){public void bind(DatagramSocket s){fail("回环测试碰到真实网络");}public void bind(Socket s){fail("回环测试碰到真实网络");}});node.start();node.refresh();
            }
        }
    }
    @Test public void noLanNeverFallsBackToCellularOrDefaultBroadcast() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        add(cm,910,NetworkCapabilities.TRANSPORT_CELLULAR,"192.0.2.8",24);LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        assertNull(route.networkKey());assertTrue(route.broadcasts().isEmpty());
        try(DatagramSocket udp=new DatagramSocket(null);Socket tcp=new Socket()) {
            try{route.bind(udp);fail("没有 Wi-Fi 时仍放行 UDP");}catch(IOException expected){assertTrue(expected.getMessage().contains("网络已断开"));}
            try{route.bind(tcp);fail("没有 Wi-Fi 时仍放行 TCP");}catch(IOException expected){assertTrue(expected.getMessage().contains("网络已断开"));}
        }
    }
    @Test public void routeKeyChangesForDhcpAndWifiButNotInternetValidation() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network wifi=add(cm,911,NetworkCapabilities.TRANSPORT_WIFI,"192.0.2.8",24);LanNetwork route=new LanNetwork(cm,NO_LOCAL);Object first=route.networkKey();
        NetworkCapabilities caps=cm.getNetworkCapabilities(wifi);shadowOf(caps).addCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED);shadowOf(cm).setNetworkCapabilities(wifi,caps);
        route.changed();assertEquals("互联网状态不应触发局域网断线",first,route.networkKey());
        shadowOf(cm).setLinkProperties(wifi,new LinkProperties());route.changed();assertNull("尚未取得 IPv4 不能算可用网络",route.networkKey());
        shadowOf(cm).removeNetwork(wifi);wifi=add(cm,911,NetworkCapabilities.TRANSPORT_WIFI,"198.51.100.8",23);route.changed();
        Object changedAddress=route.networkKey();assertNotEquals(first,changedAddress);assertEquals("198.51.101.255",route.broadcasts().iterator().next().getHostAddress());
        shadowOf(cm).removeNetwork(wifi);add(cm,912,NetworkCapabilities.TRANSPORT_WIFI,"198.51.100.8",23);route.changed();
        assertNotEquals("不同 Wi-Fi 即使 IP 相同也必须更换 socket",changedAddress,route.networkKey());
    }
    @Test @Config(shadows=CountingConnectivity.class)
    public void networkChangeDuringScanIsNotLost() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        add(cm,913,NetworkCapabilities.TRANSPORT_WIFI,"192.0.2.8",24);LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        CountingConnectivity manager=org.robolectric.shadow.api.Shadow.extract(cm);manager.duringQuery=route::changed;
        route.networkKey();route.networkKey();assertEquals("扫描中的网络回调不能被已检查标记覆盖",2,manager.queries);
    }
    private static LanNetwork.LocalInterface iface(String name,boolean up,boolean loopback,boolean p2p,boolean virtual,String ip,int prefix) throws Exception {
        return new LanNetwork.LocalInterface(name,up,loopback,p2p,virtual,Collections.singletonList(new LanNetwork.LocalLink((Inet4Address)InetAddress.getByName(ip),prefix)));
    }
    @Test public void hotspotInterfaceWorksWithoutWifiAndRebindsWhenWifiJoins() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network cellular=add(cm,920,NetworkCapabilities.TRANSPORT_CELLULAR,"10.88.0.2",24);
        LinkProperties cellLinks=cm.getLinkProperties(cellular);
        LinkProperties.class.getDeclaredMethod("setInterfaceName",String.class).invoke(cellLinks,"rmnet_data0");
        shadowOf(cm).setLinkProperties(cellular,cellLinks);
        AtomicReference<List<LanNetwork.LocalInterface>> local=new AtomicReference<>(Arrays.asList(
            iface("rmnet_data0",true,false,false,false,"10.88.0.2",24),
            iface("ap0",true,false,false,false,"192.168.43.1",24)
        ));
        LanNetwork route=new LanNetwork(cm,local::get);
        Object hotspotKey=route.networkKey();
        assertNotNull("开热点时应允许作为局域网使用",hotspotKey);
        assertEquals(Collections.singletonList(InetAddress.getByName("192.168.43.255")),new ArrayList<>(route.broadcasts()));
        try(DatagramSocket udp=new DatagramSocket(null);Socket tcp=new Socket()) {
            route.bind(udp);route.bind(tcp);
            assertEquals(0,shadowOf(cellular).boundSocketCount());
        }
        // 热点网段变更必须触发新键以重建套接字。
        local.set(Collections.singletonList(iface("ap0",true,false,false,false,"10.12.34.1",24)));
        route.changed();
        Object renumberedKey=route.networkKey();
        assertNotNull(renumberedKey);
        assertNotEquals(hotspotKey,renumberedKey);
        assertEquals(Collections.singletonList(InetAddress.getByName("10.12.34.255")),new ArrayList<>(route.broadcasts()));
        // 关闭热点后回到无局域网状态。
        local.set(Collections.emptyList());
        route.changed();
        assertNull(route.networkKey());
        assertTrue(route.broadcasts().isEmpty());
        try(DatagramSocket udp=new DatagramSocket(null)) {
            try {route.bind(udp);fail("关闭热点后应断开");}catch(IOException expected){assertTrue(expected.getMessage().contains("等待 Wi-Fi"));}
        }
        // 重新打开热点后接入外部 Wi-Fi，应切换到托管 Wi-Fi 并绑定 socket。
        local.set(Collections.singletonList(iface("ap0",true,false,false,false,"192.168.43.1",24)));
        route.changed();
        Object beforeWifi=route.networkKey();
        Network wifi=add(cm,921,NetworkCapabilities.TRANSPORT_WIFI,"192.168.1.25",24);
        route.changed();
        assertNotEquals(beforeWifi,route.networkKey());
        try(DatagramSocket udp=new DatagramSocket(null)) {
            route.bind(udp);
            assertTrue(shadowOf(wifi).isSocketBound(udp));
        }
    }
    @Test public void hotspotFilterAllowsStandardHotspotAndTetherAndRejectsUnsafeInterfaces() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,930,NetworkCapabilities.TRANSPORT_VPN,"10.99.0.2",24);
        LinkProperties vpnLinks=cm.getLinkProperties(vpn);
        LinkProperties.class.getDeclaredMethod("setInterfaceName",String.class).invoke(vpnLinks,"wlan0");
        shadowOf(cm).setLinkProperties(vpn,vpnLinks);
        AtomicReference<List<LanNetwork.LocalInterface>> local=new AtomicReference<>(Arrays.asList(
            iface("wlan0",true,false,false,false,"10.99.0.2",24), // 已被 ConnectivityManager 标记为 VPN 接口
            iface("rmnet0",true,false,false,false,"10.10.0.5",24),
            iface("ccmni0",true,false,false,false,"172.16.0.5",24),
            iface("tun0",true,false,false,false,"10.8.0.2",24),
            iface("ap0",false,false,false,false,"192.168.43.1",24), // 未启用
            iface("ap1",true,false,true,false,"192.168.44.1",24), // 点对点
            iface("wlan1",true,false,false,false,"100.64.0.2",24), // CGNAT 非 RFC1918
            iface("wlan2",true,false,false,false,"169.254.12.1",16), // 链路本地
            iface("swlan0",true,false,false,false,"8.8.8.8",24), // 公网 IP
            iface("rndis0",true,false,false,false,"192.168.42.129",24),
            iface("swlan0",true,false,false,false,"172.20.10.1",28)
        ));
        LanNetwork route=new LanNetwork(cm,local::get);
        assertNotNull(route.networkKey());
        assertEquals(
            Arrays.asList(InetAddress.getByName("192.168.42.255"),InetAddress.getByName("172.20.10.15")),
            new ArrayList<>(route.broadcasts())
        );
        // 仅剩被排除的接口与 VPN 时仍应拒绝放行并提示 VPN 限制。
        local.set(Arrays.asList(
            iface("wlan0",true,false,false,false,"10.99.0.2",24),
            iface("rmnet0",true,false,false,false,"10.10.0.5",24)
        ));
        route.changed();
        try {route.networkKey();fail("仅有 VPN 与蜂窝接口不能放行");}
        catch(IOException expected){assertTrue(expected.getMessage().contains("VPN"));}
    }
    @Test public void transientPhysicalWifiWithoutIpv4DoesNotLeakToHotspotFallback() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network wifi=ShadowNetwork.newInstance(940);NetworkCapabilities caps=ShadowNetworkCapabilities.newInstance();
        shadowOf(caps).addTransportType(NetworkCapabilities.TRANSPORT_WIFI);
        shadowOf(caps).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
        LinkProperties links=new LinkProperties();
        LinkProperties.class.getDeclaredMethod("setInterfaceName",String.class).invoke(links,"wlan0");
        shadowOf(cm).addNetwork(wifi,null);shadowOf(cm).setNetworkCapabilities(wifi,caps);shadowOf(cm).setLinkProperties(wifi,links);
        AtomicReference<List<LanNetwork.LocalInterface>> local=new AtomicReference<>(Collections.singletonList(
            iface("wlan0",true,false,false,false,"192.168.1.99",24)
        ));
        LanNetwork route=new LanNetwork(cm,local::get);
        assertNull("无 IPv4 的托管 Wi-Fi 接口不应漏进热点回退",route.networkKey());
        assertTrue(route.broadcasts().isEmpty());
    }
}
