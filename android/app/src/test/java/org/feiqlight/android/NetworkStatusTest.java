package org.feiqlight.android;

import android.app.Notification;
import android.app.NotificationManager;
import android.content.*;
import android.graphics.*;
import android.net.ConnectivityManager;
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
            Method switchNetwork=LanNode.class.getDeclaredMethod("switchNetwork",Object.class);switchNetwork.setAccessible(true);switchNetwork.invoke(node,new Object[]{null});settle(service);
            assertFalse(service.online());assertTrue(service.canDisconnect());assertTrue(ChatService.shouldRestoreConnection(service));
            assertTrue(((TextView)field(activity,"subtitle")).getText().toString().contains("等待 Wi-Fi"));
            assertEquals("刷新",((Button)field(activity,"connect")).getText().toString());
            Notification notification=shadowOf(service.getSystemService(NotificationManager.class)).getNotification(1);
            assertTrue(notification.extras.getString(Notification.EXTRA_TEXT).contains("等待 Wi-Fi"));
            for(boolean dark:new boolean[]{false,true}) {
                service.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).apply();
                Method render=MainActivity.class.getDeclaredMethod("renderPage");render.setAccessible(true);render.invoke(activity);settle(service);
                View root=(View)field(activity,"root");root.measure(View.MeasureSpec.makeMeasureSpec(720,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1600,View.MeasureSpec.EXACTLY));root.layout(0,0,720,1600);
                Bitmap bitmap=Bitmap.createBitmap(720,1600,Bitmap.Config.ARGB_8888);root.draw(new Canvas(bitmap));
                File output=new File(System.getProperty("feiq.previewDir"),"network-waiting-"+(dark?"dark":"light")+".png");assertTrue(output.getParentFile().isDirectory()||output.getParentFile().mkdirs());
                try(OutputStream stream=new FileOutputStream(output)){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,stream));}finally{bitmap.recycle();}
            }
            ((Button)field(activity,"connect")).performClick();settle(service);
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
}
