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
    @org.robolectric.annotation.Implements(ConnectivityManager.class)
    public static class SnapshotConnectivity extends ShadowConnectivityManager {
        Network[] snapshot;
        @org.robolectric.annotation.Implementation protected Network[] getAllNetworks() {return snapshot==null?super.getAllNetworks():snapshot;}
    }
    @Test @Config(shadows=SnapshotConnectivity.class)
    public void trackedWifiSurvivesVpnOnlyOrEmptySnapshotAndPrunesLostNetworks()throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,990,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        Network wifi=add(cm,991,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        SnapshotConnectivity manager=org.robolectric.shadow.api.Shadow.extract(cm);manager.snapshot=new Network[]{vpn};
        LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        assertThrows(IOException.class,route::select);
        route.track(wifi);route.track(vpn);assertEquals(wifi,route.select());
        assertEquals(Collections.singletonList("192.168.10.25"),route.addresses());
        manager.snapshot=new Network[0];route.changed();assertEquals(wifi,route.select());
        try(DatagramSocket socket=new DatagramSocket(null)){route.bind(socket);assertTrue(shadowOf(wifi).isSocketBound(socket));}
        route.lost(wifi);assertThrows(IOException.class,route::select);
        route.track(wifi);assertEquals(wifi,route.select());
        NetworkCapabilities valid=cm.getNetworkCapabilities(wifi);shadowOf(cm).setNetworkCapabilities(wifi,null);route.changed();
        assertThrows(IOException.class,route::select);
        shadowOf(cm).setNetworkCapabilities(wifi,valid);route.changed();
        assertThrows("失效网络应从回调集合移除，不能在能力恢复后自动复活",IOException.class,route::select);
        route.clearTrackedNetworks();assertNull(route.networkKey());assertTrue(route.broadcasts().isEmpty());
        assertNull("回调跟踪不应改变默认路由",cm.getBoundNetworkForProcess());
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
    @Test public void ipv6VpnCallbacksKeepCurrentWifiTcpAndUdpListeners() throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        // 注入回环 /32，执行正式绑定路径但不向宿主真实局域网广播。
        Network wifi=add(cm,906,NetworkCapabilities.TRANSPORT_WIFI,"127.0.0.1",32);LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        int port;try(ServerSocket free=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){port=free.getLocalPort();}
        org.feiqlight.android.core.LanNode.Listener listener=new org.feiqlight.android.core.LanNode.Listener() {
            public void peersChanged(){}public void message(org.feiqlight.android.core.LanNode.Peer peer,org.feiqlight.android.core.LanNode.Message message,boolean update){}public void error(String text){fail(text);}
        };
        try(org.feiqlight.android.core.LanNode node=new org.feiqlight.android.core.LanNode(port,false,"ipv6-vpn","host","测试","",listener)) {
            node.setSocketBinding(route);node.start();assertTrue(node.networkAvailable());Object key=route.networkKey();
            java.lang.reflect.Field tcp=org.feiqlight.android.core.LanNode.class.getDeclaredField("tcp"),udp=org.feiqlight.android.core.LanNode.class.getDeclaredField("udp");tcp.setAccessible(true);udp.setAccessible(true);
            Object originalTcp=tcp.get(node),originalUdp=udp.get(node);
            for(int i=0;i<3;i++) {
                Network vpn=add(cm,907+i,NetworkCapabilities.TRANSPORT_VPN,"fd12:3456::2",64);
                node.networkChanged();node.refresh();assertEquals(wifi,route.select());assertEquals(key,route.networkKey());
                assertSame(originalTcp,tcp.get(node));assertSame(originalUdp,udp.get(node));assertEquals(0,shadowOf(vpn).boundSocketCount());
                shadowOf(cm).removeNetwork(vpn);node.networkChanged();node.refresh();
                assertSame(originalTcp,tcp.get(node));assertSame(originalUdp,udp.get(node));assertTrue(node.networkAvailable());
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
    private static LanNetwork.LocalInterface iface(String name,boolean up,boolean loopback,boolean p2p,boolean virtual,String ip,int prefix) throws UnknownHostException {
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
        Network cellular=add(cm,930,NetworkCapabilities.TRANSPORT_CELLULAR,"10.99.0.2",24);
        LinkProperties cellLinks=cm.getLinkProperties(cellular);
        LinkProperties.class.getDeclaredMethod("setInterfaceName",String.class).invoke(cellLinks,"wlan0");
        shadowOf(cm).setLinkProperties(cellular,cellLinks);
        AtomicReference<List<LanNetwork.LocalInterface>> local=new AtomicReference<>(Arrays.asList(
            iface("wlan0",true,false,false,false,"10.99.0.2",24), // 已被 ConnectivityManager 标记为蜂窝接口
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
        // 仅剩被排除的接口时不能当作局域网。
        local.set(Arrays.asList(
            iface("wlan0",true,false,false,false,"10.99.0.2",24),
            iface("rmnet0",true,false,false,false,"10.10.0.5",24)
        ));
        route.changed();
        assertNull(route.networkKey());assertTrue(route.broadcasts().isEmpty());
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
    @Test public void vpnReportedUnderlyingInterfaceDoesNotExcludePhysicalPrivateFallback()throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,992,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        NetworkCapabilities caps=cm.getNetworkCapabilities(vpn);shadowOf(caps).addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR);shadowOf(cm).setNetworkCapabilities(vpn,caps);
        for(String name:new String[]{"wlan0","eth0"}) {
            LinkProperties links=cm.getLinkProperties(vpn);LinkProperties.class.getDeclaredMethod("setInterfaceName",String.class).invoke(links,name);shadowOf(cm).setLinkProperties(vpn,links);
            LanNetwork route=new LanNetwork(cm,()->Collections.singletonList(iface(name,true,false,false,false,"192.168.10.25",24)));
            assertNotNull("VPN 报告底层接口不能把物理私网排除："+name,route.networkKey());
            assertTrue(route.allows(InetAddress.getByName("192.168.10.42")));
            assertTrue(route.shouldRetryWithoutSourceBind(InetAddress.getByName("192.168.10.42")));
            assertFalse(route.shouldRetryWithoutSourceBind(InetAddress.getByName("192.168.99.42")));
        }
    }
    @Test public void hotspotTrafficCannotUseHistoricalAddressesOutsideCurrentSubnets() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network cell=add(cm,950,NetworkCapabilities.TRANSPORT_CELLULAR,"10.88.0.2",24);
        AtomicReference<List<LanNetwork.LocalInterface>> local=new AtomicReference<>(Arrays.asList(
            iface("ap0",true,false,false,false,"192.168.43.1",24),
            iface("rndis0",true,false,false,false,"172.20.10.1",28)
        ));
        LanNetwork route=new LanNetwork(cm,local::get);
        for(String address:new String[]{"192.168.43.2","192.168.43.255","172.20.10.14","172.20.10.15"})
            assertTrue(address,route.allows(InetAddress.getByName(address)));
        for(String address:new String[]{"192.168.1.9","192.168.44.2","172.20.10.16","10.88.0.2","8.8.8.8","255.255.255.255","127.0.0.1","::1"})
            assertFalse("热点放行了网段外目标："+address,route.allows(InetAddress.getByName(address)));
        assertEquals(0,shadowOf(cell).boundSocketCount());
        local.set(Collections.singletonList(iface("ap0",true,false,false,false,"192.168.44.1",24)));route.changed();
        assertFalse("切网后旧热点地址仍可访问",route.allows(InetAddress.getByName("192.168.43.2")));
        assertTrue(route.allows(InetAddress.getByName("192.168.44.2")));
        local.set(Collections.emptyList());route.changed();
        assertFalse("热点关闭后仍放行流量",route.allows(InetAddress.getByName("192.168.44.2")));
    }
    private static final class RecordingSocket extends Socket {
        SocketAddress source;
        @Override public void bind(SocketAddress endpoint) { source=endpoint; }
    }
    @Test public void hotspotTcpBindsMatchingSourceAndRejectsOutsideBeforeConnect() throws Exception {
        LanNetwork route=new LanNetwork(null,()->Arrays.asList(
            iface("rndis0",true,false,false,false,"192.168.0.1",16),
            iface("ap0",true,false,false,false,"192.168.43.1",24)
        ));
        try(RecordingSocket tcp=new RecordingSocket()) {
            route.bind(tcp,InetAddress.getByName("192.168.43.42"));
            assertEquals("应使用最具体的热点接口地址",new InetSocketAddress("192.168.43.1",0),tcp.source);
        }
        try(RecordingSocket tcp=new RecordingSocket()) {
            route.bind(tcp,InetAddress.getByName("192.168.42.42"));
            assertEquals(new InetSocketAddress("192.168.0.1",0),tcp.source);
        }
        try(RecordingSocket tcp=new RecordingSocket()) {
            try {route.bind(tcp,InetAddress.getByName("203.0.113.9"));fail("网段外 TCP 请求被放行");}
            catch(IOException expected){assertTrue(expected.getMessage().contains("子网"));}
            assertNull("拒绝的目标不能先绑定或连接",tcp.source);
        }
    }
    @Test public void privateInterfaceWithOverbroadMaskCannotBroadcastOutsidePrivateRanges() throws Exception {
        LanNetwork route=new LanNetwork(null,()->Arrays.asList(
            iface("ap0",true,false,false,false,"192.168.43.1",8),
            iface("ap1",true,false,false,false,"172.20.10.1",8),
            iface("ap2",true,false,false,false,"192.168.43.1",31),
            iface("rndis0",true,false,false,false,"10.12.34.1",8)
        ));
        assertEquals(Collections.singletonList(InetAddress.getByName("10.255.255.255")),new ArrayList<>(route.broadcasts()));
        assertFalse(route.allows(InetAddress.getByName("192.168.43.2")));
        assertFalse(route.allows(InetAddress.getByName("172.20.10.2")));
    }
    @Test public void vpnWithHotspotKeepsSubnetRoutingAndNeverBindsVpn() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,951,NetworkCapabilities.TRANSPORT_VPN,"10.99.0.2",24);
        LanNetwork route=new LanNetwork(cm,()->Collections.singletonList(iface("ap0",true,false,false,false,"192.168.43.1",24)));
        Object hotspot=route.networkKey();assertNotNull(hotspot);
        shadowOf(cm).removeNetwork(vpn);route.changed();assertEquals("仅关闭 VPN 不应清除热点会话",hotspot,route.networkKey());
        vpn=add(cm,953,NetworkCapabilities.TRANSPORT_VPN,"fd12:3456::2",64);route.changed();assertEquals(hotspot,route.networkKey());
        assertTrue(route.allows(InetAddress.getByName("192.168.43.2")));
        assertFalse(route.allows(InetAddress.getByName("10.99.0.3")));
        try(RecordingSocket tcp=new RecordingSocket()){route.bind(tcp,InetAddress.getByName("192.168.43.2"));assertEquals(new InetSocketAddress("192.168.43.1",0),tcp.source);}
        try(DatagramSocket udp=new DatagramSocket(null)) {
            route.bind(udp);assertEquals(0,shadowOf(vpn).boundSocketCount());
            Network wifi=add(cm,952,NetworkCapabilities.TRANSPORT_WIFI,"192.168.1.2",24);route.changed();route.bind(udp);
            assertTrue(shadowOf(wifi).isSocketBound(udp));assertEquals(0,shadowOf(vpn).boundSocketCount());
        }
    }
    @org.robolectric.annotation.Implements(Network.class)
    public static class BlockingBindNetwork extends ShadowNetwork {
        boolean blocked=true;
        @SuppressWarnings("unchecked") private static <T extends Throwable> void raise(Throwable e) throws T {throw (T)e;}
        @org.robolectric.annotation.Implementation protected void bindSocket(DatagramSocket socket) {if(blocked)raise(new IOException("Operation not permitted"));else super.bindSocket(socket);}
        @org.robolectric.annotation.Implementation protected void bindSocket(Socket socket) {if(blocked)raise(new IOException("Operation not permitted"));else super.bindSocket(socket);}
    }
    @Test @Config(shadows=BlockingBindNetwork.class)
    public void vpnBindSocketBlockedFallsBackToLocalInterfaceSourceAddress() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        add(cm,960,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        add(cm,961,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        LanNetwork route=new LanNetwork(cm,NO_LOCAL);
        try(DatagramSocket udp=new DatagramSocket(null);RecordingSocket tcp=new RecordingSocket()) {
            route.bind(udp);
            route.bind(tcp,InetAddress.getByName("192.168.10.42"));
            assertEquals(new InetSocketAddress("192.168.10.25",0),tcp.source);
        }
        try(RecordingSocket outside=new RecordingSocket()) {
            try {route.bind(outside,InetAddress.getByName("192.168.99.42"));fail("不应允许访问当前物理子网外地址");}
            catch(IOException expected){assertTrue(expected.getMessage().contains("子网")||expected.getMessage().contains("局域网"));}
        }
    }
    @Test @Config(shadows=BlockingBindNetwork.class)
    public void unboundTcpRetryRequiresVpnBlockedBindAndCurrentPrivateUnicastSubnet()throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,993,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        Network wifi=add(cm,994,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        LanNetwork route=new LanNetwork(cm,NO_LOCAL);InetAddress target=InetAddress.getByName("192.168.10.42");
        assertFalse("绑定尚未被拒绝时不应允许默认路由重试",route.shouldRetryWithoutSourceBind(target));
        try(RecordingSocket socket=new RecordingSocket()){route.bind(socket,target);assertEquals(new InetSocketAddress("192.168.10.25",0),socket.source);}
        assertTrue(route.shouldRetryWithoutSourceBind(target));route.changed();assertTrue("同一网络的缓存刷新不能丢失绑定受限状态",route.shouldRetryWithoutSourceBind(target));
        for(String address:new String[]{"192.168.10.0","192.168.10.25","192.168.10.255","192.168.99.42","10.8.0.2","8.8.8.8","255.255.255.255","127.0.0.1","::1"})
            assertFalse("不安全 TCP 重试目标："+address,route.shouldRetryWithoutSourceBind(InetAddress.getByName(address)));
        shadowOf(cm).removeNetwork(vpn);route.changed();assertFalse("无 VPN 时不得使用默认路由回退",route.shouldRetryWithoutSourceBind(target));
        add(cm,993,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);shadowOf(cm).removeNetwork(wifi);add(cm,995,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);route.changed();
        assertFalse("新物理网络不能继承旧网络的绑定失败标记",route.shouldRetryWithoutSourceBind(target));
        LanNetwork hotspot=new LanNetwork(null,()->Collections.singletonList(iface("ap0",true,false,false,false,"192.168.10.1",24)));
        try(RecordingSocket socket=new RecordingSocket()){hotspot.bind(socket,target);}
        assertFalse("没有 VPN 的热点不能使用默认路由回退",hotspot.shouldRetryWithoutSourceBind(target));
    }
    @Test public void successfulPhysicalBindUnderVpnNeverEnablesUnboundTcpRetry()throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        add(cm,996,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);add(cm,997,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        LanNetwork route=new LanNetwork(cm,NO_LOCAL);InetAddress target=InetAddress.getByName("192.168.10.42");
        try(Socket socket=new Socket(java.net.Proxy.NO_PROXY)){route.bind(socket,target);}
        assertFalse(route.shouldRetryWithoutSourceBind(target));
    }
    @Test @Config(shadows=BlockingBindNetwork.class)
    public void vpnUnicastSkipsSubnetEndpoints()throws Exception {
        ConnectivityManager manager=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);
        for(boolean physical:new boolean[]{false,true})for(int prefix:new int[]{24,30}) {
            shadowOf(manager).clearAllNetworks();add(manager,9992,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
            if(physical)add(manager,9993,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",prefix);
            List<InetSocketAddress> sources=new ArrayList<>();List<SocketAddress> delivered=new ArrayList<>();List<DatagramSocket> created=new ArrayList<>();
            LanNetwork route=new LanNetwork(manager,physical?NO_LOCAL:()->Collections.singletonList(iface("ap0",true,false,false,false,"192.168.10.25",prefix)),source->{
                sources.add(source);DatagramSocket socket=new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet)throws IOException{if(source!=null)throw new SocketException("模拟物理源绑定被VPN拒绝");delivered.add(packet.getSocketAddress());}};created.add(socket);return socket;
            });
            try(DatagramSocket receiver=new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet){fail("未绑定的接收 socket 不能发送");}}) {
                route.bind(receiver);InetAddress peer=InetAddress.getByName(prefix==24?"192.168.10.42":"192.168.10.26");assertTrue(route.shouldRetryWithoutSourceBind(peer));
                String network=prefix==24?"192.168.10.0":"192.168.10.24",broadcast=prefix==24?"192.168.10.255":"192.168.10.27";
                for(String address:new String[]{network,"192.168.10.25",broadcast}) {
                    InetAddress target=InetAddress.getByName(address);assertFalse("子网端点不能无源TCP重试："+address,route.shouldRetryWithoutSourceBind(target));sources.clear();
                    assertThrows(IOException.class,()->route.send(receiver,new byte[]{1},new InetSocketAddress(target,2425)));assertEquals(Collections.singletonList(new InetSocketAddress("192.168.10.25",0)),sources);assertTrue("子网端点进入默认UDP路由",delivered.isEmpty());
                }
                for(String address:new String[]{"192.168.99.42","255.255.255.255"}) {sources.clear();assertFalse(route.shouldRetryWithoutSourceBind(InetAddress.getByName(address)));assertThrows(IOException.class,()->route.send(receiver,new byte[]{2},new InetSocketAddress(address,2425)));assertTrue(sources.isEmpty());}
                sources.clear();route.send(receiver,new byte[]{3},new InetSocketAddress(peer,2425));assertEquals(Arrays.asList(new InetSocketAddress("192.168.10.25",0),null),sources);assertEquals(Collections.singletonList(new InetSocketAddress(peer,2425)),delivered);
            }
            for(DatagramSocket socket:created)assertTrue("重试socket未关闭",socket.isClosed());
        }
    }
    @Test @Config(shadows=BlockingBindNetwork.class)
    public void recoveredBindOnSamePhysicalNetworkClearsUnboundRetryPermission()throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        add(cm,9990,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);Network wifi=add(cm,9991,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        LanNetwork route=new LanNetwork(cm,NO_LOCAL);InetAddress target=InetAddress.getByName("192.168.10.42");
        try(RecordingSocket socket=new RecordingSocket()){route.bind(socket,target);}assertTrue(route.shouldRetryWithoutSourceBind(target));
        BlockingBindNetwork network=org.robolectric.shadow.api.Shadow.extract(wifi);network.blocked=false;
        try(Socket socket=new Socket(java.net.Proxy.NO_PROXY)){route.bind(socket,target);}assertFalse("物理绑定恢复成功后不得继承旧的受限回退标记",route.shouldRetryWithoutSourceBind(target));
    }
    @Test @Config(shadows=BlockingBindNetwork.class)
    public void vpnUdpUnicastRetriesUnboundOnlyAfterPrivateSourceFailsAndNeverForBroadcastOrOutside()throws Exception {
        ConnectivityManager cm=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        Network vpn=add(cm,998,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);add(cm,999,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        List<InetSocketAddress> sources=new ArrayList<>();List<SocketAddress> delivered=new ArrayList<>();List<DatagramSocket> created=new ArrayList<>();
        LanNetwork route=new LanNetwork(cm,NO_LOCAL,source->{
            sources.add(source);
            DatagramSocket sender=new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet)throws IOException{if(source!=null)throw new SocketException("模拟 TUN 拒绝物理源 IP");delivered.add(packet.getSocketAddress());}};
            created.add(sender);return sender;
        });
        try(DatagramSocket receiver=new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet){fail("未绑定的监听 socket 不得走默认路由");}}) {
            route.bind(receiver);InetSocketAddress target=new InetSocketAddress("192.168.10.42",2425);route.send(receiver,new byte[]{1},target);
            assertEquals(Arrays.asList(new InetSocketAddress("192.168.10.25",0),null),sources);assertEquals(Collections.singletonList(target),delivered);
            sources.clear();assertThrows(IOException.class,()->route.send(receiver,new byte[]{2},new InetSocketAddress("192.168.10.255",2425)));
            assertEquals("广播不得尝试无源 IP socket",Collections.singletonList(new InetSocketAddress("192.168.10.25",0)),sources);
            for(String address:new String[]{"192.168.99.42","10.8.0.2","8.8.8.8","255.255.255.255"}) {
                sources.clear();assertThrows(IOException.class,()->route.send(receiver,new byte[]{3},new InetSocketAddress(address,2425)));assertTrue(address,sources.isEmpty());
            }
            sources.clear();shadowOf(cm).removeNetwork(vpn);route.changed();assertThrows(IOException.class,()->route.send(receiver,new byte[]{4},target));
            assertEquals("没有 VPN 时不能无源 IP 重试",Collections.singletonList(new InetSocketAddress("192.168.10.25",0)),sources);
            assertEquals(1,delivered.size());for(DatagramSocket sender:created)assertTrue("临时 UDP socket 未关闭",sender.isClosed());
        }
    }
    @Test @Config(shadows=BlockingBindNetwork.class)
    public void vpnUdpFallbackSurvivesNetworkCacheRefreshWithoutUsingDefaultRoute() throws Exception {
        ConnectivityManager cm=(ConnectivityManager)RuntimeEnvironment.getApplication().getSystemService(Context.CONNECTIVITY_SERVICE);shadowOf(cm).clearAllNetworks();
        add(cm,970,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        add(cm,971,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        List<InetSocketAddress> sources=new ArrayList<>();List<SocketAddress> destinations=new ArrayList<>();
        LanNetwork route=new LanNetwork(cm,NO_LOCAL,source->{
            sources.add(source);
            return new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet){destinations.add(packet.getSocketAddress());}};
        });
        try(DatagramSocket receiver=new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet){fail("未绑定的接收 socket 走了默认 VPN 路由");}}) {
            route.bind(receiver);Object key=route.networkKey();InetSocketAddress destination=new InetSocketAddress("192.168.10.255",2425);
            route.send(receiver,new byte[]{1},destination);
            route.changed();assertEquals(key,route.networkKey());
            route.send(receiver,new byte[]{2},destination);
            assertEquals(Arrays.asList(new InetSocketAddress("192.168.10.25",0),new InetSocketAddress("192.168.10.25",0)),sources);
            assertEquals(Arrays.asList(destination,destination),destinations);
        }
    }

    @Test public void boundWifiMayRejectSendAndOnlyRetryFromMatchingPrivateAddress() throws Exception {
        ConnectivityManager manager=RuntimeEnvironment.getApplication().getSystemService(ConnectivityManager.class);
        shadowOf(manager).clearAllNetworks();
        Network vpn=add(manager,980,NetworkCapabilities.TRANSPORT_VPN,"198.18.0.1",16);
        Network wifi=add(manager,981,NetworkCapabilities.TRANSPORT_WIFI,"192.168.10.25",24);
        List<SocketAddress> sources=new ArrayList<>(),destinations=new ArrayList<>();
        LanNetwork route=new LanNetwork(manager,NO_LOCAL,source->{
            sources.add(source);
            return new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet){destinations.add(packet.getSocketAddress());}};
        });
        try(DatagramSocket receiver=new DatagramSocket((SocketAddress)null){@Override public void send(DatagramPacket packet)throws IOException{throw new SocketException("UDP unavailable");}}) {
            route.bind(receiver);assertTrue(shadowOf(wifi).isSocketBound(receiver));
            InetSocketAddress broadcast=new InetSocketAddress("192.168.10.255",2425);
            route.send(receiver,new byte[]{1},broadcast);
            assertThrows(IOException.class,()->route.send(receiver,new byte[]{2},new InetSocketAddress("192.168.99.255",2425)));
            assertEquals(Collections.singletonList(new InetSocketAddress("192.168.10.25",0)),sources);
            assertEquals(Collections.singletonList(broadcast),destinations);assertEquals(0,shadowOf(vpn).boundSocketCount());
        }
    }
}
