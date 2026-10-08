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
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35)
public final class LanNetworkTest {
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
        Network wifi=add(cm,902,NetworkCapabilities.TRANSPORT_WIFI,"192.0.2.8",24);LanNetwork route=new LanNetwork(cm);
        assertEquals(wifi,route.select());assertEquals("192.0.2.255",route.broadcasts().iterator().next().getHostAddress());
        try(DatagramSocket udp=new DatagramSocket(null);Socket tcp=new Socket()) {
            route.bind(udp);route.bind(tcp);assertTrue(shadowOf(wifi).isSocketBound(udp));assertTrue(shadowOf(wifi).isSocketBound(tcp));assertEquals(0,shadowOf(vpn).boundSocketCount());
            shadowOf(cm).removeNetwork(wifi);Network next=add(cm,903,NetworkCapabilities.TRANSPORT_ETHERNET,"198.51.100.12",23);
            route.bind(udp);assertTrue(shadowOf(next).isSocketBound(udp));assertEquals("198.51.101.255",route.broadcasts().iterator().next().getHostAddress());
            shadowOf(cm).removeNetwork(next);try {route.bind(tcp);fail("VPN 单网络被当成局域网");}catch(IOException expected){assertTrue(expected.getMessage().contains("VPN"));}
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
}
