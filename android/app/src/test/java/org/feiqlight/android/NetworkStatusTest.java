package org.feiqlight.android;

import android.app.Notification;
import android.app.NotificationManager;
import android.content.*;
import android.graphics.*;
import android.net.*;
import android.os.Looper;
import android.view.View;
import android.widget.*;
import org.feiqlight.android.core.LanNode;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.*;
import org.robolectric.annotation.*;
import org.robolectric.shadows.ShadowNetwork;
import org.robolectric.shadows.ShadowNetworkCapabilities;
import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.time.Duration;
import java.util.concurrent.*;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w360dp-h800dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class NetworkStatusTest {
    private static Object field(Object target,String name)throws Exception {Field f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);}
    private static void settle(ChatService service)throws Exception {
        for(int i=0;i<4;i++) {
            ((ExecutorService)field(service,"io")).submit(()->{}).get(5,TimeUnit.SECONDS);
            ((ExecutorService)field(service,"discovery")).submit(()->{}).get(5,TimeUnit.SECONDS);
            shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));
        }
    }
    @Test public void lostNetworkUpdatesListNotificationAndRefreshWithoutChangingConnectionIntent()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();ActivityController<MainActivity> ac=null;
        try(ServerSocket probe=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            int port=probe.getLocalPort();probe.close();settle(service);
            service.connectionFactory=(prefs,listener)->new LanNode(port,true,"network-ui","host","测试","组",listener);
            service.onStartCommand(new Intent(),0,1);settle(service);
            LanNode node=(LanNode)field(service,"node");((ScheduledExecutorService)field(node,"timer")).shutdownNow();assertTrue(service.online());
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);
            Method state=LanNode.class.getDeclaredMethod("networkState",boolean.class,String.class);state.setAccessible(true);
            state.invoke(node,true,"广播受限，可手动添加 IP");settle(service);
            assertTrue(service.online());assertTrue(service.connectionDetails().contains("已监听 · 广播受限"));
            assertTrue(((TextView)field(activity,"subtitle")).getText().toString().contains("添加 IP"));
            Notification limited=shadowOf(service.getSystemService(NotificationManager.class)).getNotification(1);
            assertTrue(limited.extras.getString(Notification.EXTRA_TEXT).contains("广播受限"));
            Method addPeer=MainActivity.class.getDeclaredMethod("addPeer");addPeer.setAccessible(true);addPeer.invoke(activity);
            android.app.Dialog manual=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertTrue(manual.isShowing());manual.dismiss();
            for(boolean waiting:new boolean[]{false,true}) {
                if(waiting) {
                    Method switchNetwork=LanNode.class.getDeclaredMethod("switchNetwork",Object.class);switchNetwork.setAccessible(true);switchNetwork.invoke(node,new Object[]{null});settle(service);
                    assertFalse(service.online());assertTrue(service.canDisconnect());assertTrue(ChatService.shouldRestoreConnection(service));
                    assertTrue(((TextView)field(activity,"subtitle")).getText().toString().contains("等待 Wi-Fi"));
                    assertEquals("重试",((Button)field(activity,"connect")).getText().toString());
                    Notification notification=shadowOf(service.getSystemService(NotificationManager.class)).getNotification(1);
                    assertTrue(notification.extras.getString(Notification.EXTRA_TEXT).contains("等待 Wi-Fi"));
                }
                for(boolean dark:new boolean[]{false,true}) {
                    service.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).apply();
                    Method render=MainActivity.class.getDeclaredMethod("renderPage");render.setAccessible(true);render.invoke(activity);settle(service);
                    View root=(View)field(activity,"root");root.measure(View.MeasureSpec.makeMeasureSpec(720,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1600,View.MeasureSpec.EXACTLY));root.layout(0,0,720,1600);
                    Bitmap bitmap=Bitmap.createBitmap(720,1600,Bitmap.Config.ARGB_8888);root.draw(new Canvas(bitmap));
                    File output=new File(System.getProperty("feiq.previewDir"),(waiting?"network-waiting-":"network-limited-")+(dark?"dark":"light")+".png");assertTrue(output.getParentFile().isDirectory()||output.getParentFile().mkdirs());
                    try(OutputStream stream=new FileOutputStream(output)){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,stream));}finally{bitmap.recycle();}
                }
            }
            Method pull=MainActivity.class.getDeclaredMethod("pullRefresh");pull.setAccessible(true);assertEquals(true,pull.invoke(activity));settle(service);
            assertTrue("等待网络时刷新必须触发已有节点恢复",service.online());assertSame(node,field(service,"node"));
            assertFalse(service.status().contains("等待"));assertEquals(View.GONE,((View)field(activity,"connectionBar")).getVisibility());
            ConnectivityManager.NetworkCallback old=(ConnectivityManager.NetworkCallback)field(service,"callback");
            service.disconnect();settle(service);old.onAvailable(ShadowNetwork.newInstance(921));settle(service);
            assertFalse(service.online());assertFalse(service.canDisconnect());assertFalse(ChatService.shouldRestoreConnection(service));assertEquals("已断开",service.status());
        } finally {if(ac!=null)ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void hotspotBroadcastTriggersRecoveryAndDisconnectUnregisters()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        try(ServerSocket probe=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            int port=probe.getLocalPort();probe.close();settle(service);
            service.connectionFactory=(prefs,listener)->new LanNode(port,true,"hotspot-ui","host","测试","组",listener);
            service.onStartCommand(new Intent(),0,1);settle(service);
            LanNode node=(LanNode)field(service,"node");assertTrue(service.online());
            Method switchNetwork=LanNode.class.getDeclaredMethod("switchNetwork",Object.class);switchNetwork.setAccessible(true);switchNetwork.invoke(node,new Object[]{null});settle(service);
            assertFalse(service.online());
            BroadcastReceiver receiver=(BroadcastReceiver)field(service,"tetherReceiver");
            assertNotNull(receiver);
            receiver.onReceive(service,new Intent("android.net.wifi.WIFI_AP_STATE_CHANGED"));
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
            while(!service.online() && System.nanoTime()<deadline) { settle(service); Thread.sleep(10); }
            assertTrue("收到热点广播应触发节点重检与恢复",service.online());
            service.disconnect();settle(service);
            assertNull(field(service,"tetherReceiver"));
            receiver.onReceive(service,new Intent("android.net.conn.TETHER_STATE_CHANGED"));settle(service);
            assertFalse(service.online());assertFalse(service.canDisconnect());assertEquals("已断开",service.status());
        } finally {sc.destroy();}
    }
    @Test @Config(shadows=LanNetworkTest.SnapshotConnectivity.class)
    public void coldVpnStartTracksCallbackWifiUpdatesStatusAndSharesRouteWithGuest()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        ConnectivityManager cm=service.getSystemService(ConnectivityManager.class);shadowOf(cm).clearAllNetworks();
        LanNetworkTest.SnapshotConnectivity manager=org.robolectric.shadow.api.Shadow.extract(cm);
        Network vpn=ShadowNetwork.newInstance(9931),wifi=ShadowNetwork.newInstance(9932);
        NetworkCapabilities vpnCaps=ShadowNetworkCapabilities.newInstance(),wifiCaps=ShadowNetworkCapabilities.newInstance();
        shadowOf(vpnCaps).addTransportType(NetworkCapabilities.TRANSPORT_VPN);shadowOf(vpnCaps).removeCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
        shadowOf(wifiCaps).addTransportType(NetworkCapabilities.TRANSPORT_WIFI);shadowOf(wifiCaps).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
        shadowOf(cm).addNetwork(vpn,null);shadowOf(cm).setNetworkCapabilities(vpn,vpnCaps);
        shadowOf(cm).addNetwork(wifi,null);shadowOf(cm).setNetworkCapabilities(wifi,wifiCaps);
        Constructor<LinkAddress> constructor=LinkAddress.class.getDeclaredConstructor(InetAddress.class,int.class);constructor.setAccessible(true);
        LinkProperties links=new LinkProperties();LinkProperties.class.getDeclaredMethod("addLinkAddress",LinkAddress.class).invoke(links,constructor.newInstance(InetAddress.getLoopbackAddress(),32));shadowOf(cm).setLinkProperties(wifi,links);
        manager.snapshot=new Network[]{vpn};
        try(ServerSocket free=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            int port=free.getLocalPort();free.close();settle(service);
            service.networkFactory=connectivity->new LanNetwork(connectivity,java.util.Collections::emptyList);
            service.connectionFactory=(prefs,listener)->new LanNode(port,false,"callback-wifi","host","测试","",listener);
            service.onStartCommand(new Intent(),0,1);settle(service);assertFalse(service.online());assertTrue(service.status(),service.status().contains("VPN"));
            LanNode node=(LanNode)field(service,"node");LanNetwork route=(LanNetwork)field(service,"lanNetwork");assertSame(route,field(node,"socketBinding"));
            ConnectivityManager.NetworkCallback callback=(ConnectivityManager.NetworkCallback)field(service,"callback");
            callback.onAvailable(wifi);callback.onCapabilitiesChanged(wifi,wifiCaps);callback.onLinkPropertiesChanged(wifi,links);
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(4);
            while(!service.online()&&System.nanoTime()<deadline){settle(service);Thread.sleep(10);}
            settle(service);assertTrue(service.status(),service.online());assertEquals(wifi,route.select());assertFalse(service.status(),service.status().contains("VPN"));
            Notification notification=shadowOf(service.getSystemService(NotificationManager.class)).getNotification(1);assertFalse(notification.extras.getString(Notification.EXTRA_TEXT).contains("VPN"));
            final boolean[] started={false};service.startGuest(java.util.Collections.emptyList(),true,success->started[0]=success);settle(service);
            assertTrue(started[0]);GuestTransfer guest=service.guestTransfer();assertSame("访客服务必须复用回调网络视图",route,field(field(guest,"http"),"binding"));
            manager.snapshot=new Network[0];route.changed();assertEquals(wifi,route.select());
            shadowOf(cm).removeNetwork(wifi);callback.onLost(wifi);settle(service);
            deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(4);while(service.online()&&System.nanoTime()<deadline){settle(service);Thread.sleep(10);}
            assertFalse(service.online());service.disconnect();settle(service);callback.onAvailable(wifi);settle(service);
            assertNull(field(service,"lanNetwork"));assertTrue(((java.util.Set<?>)field(route,"trackedNetworks")).isEmpty());assertNull(cm.getBoundNetworkForProcess());
        } finally {sc.destroy();}
    }
    @Test public void profileSavedWhileConnectingIsPublishedWithoutChangingBoundPort()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        CountDownLatch created=new CountDownLatch(1),release=new CountDownLatch(1);
        java.util.concurrent.atomic.AtomicReference<LanNode> candidate=new java.util.concurrent.atomic.AtomicReference<>();
        try(ServerSocket probe=new ServerSocket(0,1,InetAddress.getLoopbackAddress())) {
            int port=probe.getLocalPort();probe.close();settle(service);
            SharedPreferences prefs=service.getSharedPreferences("settings",0);
            prefs.edit().putInt("port",port).putString("name","连接前昵称").putString("group","旧部门").commit();
            service.connectionFactory=(settings,listener)->{
                LanNode next=new LanNode(port,true,"profile-race","host",settings.getString("name",""),settings.getString("group",""),listener);
                candidate.set(next);created.countDown();
                try {if(!release.await(5,TimeUnit.SECONDS))throw new IllegalStateException("等待测试放行超时");}
                catch(InterruptedException e){Thread.currentThread().interrupt();throw new IllegalStateException(e);}
                return next;
            };
            service.onStartCommand(new Intent(),0,1);assertTrue(created.await(5,TimeUnit.SECONDS));
            synchronized(field(service,"connectionLock")) {
                release.countDown();long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
                while(!candidate.get().networkAvailable()&&System.nanoTime()<deadline)Thread.sleep(10);
                assertTrue(candidate.get().networkAvailable());assertNull("节点应仍在等待发布",field(service,"node"));
                int laterPort=port==65535?65534:port+1;
                assertTrue(prefs.edit().putString("name","建连中保存的新昵称").putString("group","新部门").putString("avatar","builtin:3").putInt("port",laterPort).commit());
                service.profileSettingsChanged();
            }
            settle(service);LanNode connected=(LanNode)field(service,"node");
            assertSame(candidate.get(),connected);assertEquals(port,connected.port());assertEquals(port,field(service,"connectedPort"));
            assertEquals("建连中保存的新昵称",connected.nickname);assertEquals("新部门",connected.group);
            assertEquals("builtin:3",field(connected,"avatar"));assertTrue(service.status().contains(Integer.toString(port)));
            assertNotEquals("下次连接的端口配置不能被本次监听覆盖",port,prefs.getInt("port",0));
            assertTrue(ChatService.shouldRestoreConnection(service));
        } finally {release.countDown();sc.destroy();}
    }
}
