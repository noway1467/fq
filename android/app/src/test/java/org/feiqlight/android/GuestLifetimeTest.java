package org.feiqlight.android;

import android.app.Dialog;
import android.content.*;
import android.graphics.*;
import android.os.Looper;
import android.view.*;
import android.widget.*;
import org.feiqlight.android.core.*;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.android.controller.*;
import org.robolectric.shadows.ShadowDialog;
import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w393dp-h750dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class GuestLifetimeTest {
    private static Object field(Object target,String name)throws Exception {Field f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);}
    private static void set(Object target,String name,Object value)throws Exception {Field f=target.getClass().getDeclaredField(name);f.setAccessible(true);f.set(target,value);}
    private static void invoke(Object target,String name)throws Exception {Method method=target.getClass().getDeclaredMethod(name);method.setAccessible(true);method.invoke(target);}
    private static List<View> views(View parent){List<View> result=new ArrayList<>();result.add(parent);if(parent instanceof ViewGroup)for(int i=0;i<((ViewGroup)parent).getChildCount();i++)result.addAll(views(((ViewGroup)parent).getChildAt(i)));return result;}
    private static Button button(Dialog dialog,String caption){return (Button)views(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&caption.contentEquals(((Button)v).getText())).findFirst().orElseThrow();}
    private static EditText input(Dialog dialog){return (EditText)views(dialog.getWindow().getDecorView()).stream().filter(v->"链接有效期（分钟）".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();}
    private static void settle(ChatService service)throws Exception {for(int i=0;i<3;i++){((ExecutorService)field(service,"io")).submit(()->{}).get(5,TimeUnit.SECONDS);Shadows.shadowOf(Looper.getMainLooper()).idle();}}
    private static HttpURLConnection request(GuestTransfer server,String token)throws Exception {HttpURLConnection connection=(HttpURLConnection)new URL("http://127.0.0.1:"+server.port()+"/api/guest").openConnection(java.net.Proxy.NO_PROXY);connection.setConnectTimeout(3000);connection.setReadTimeout(3000);connection.setRequestProperty("X-FQ-Guest",token);return connection;}
    private static void screenshot(Dialog dialog,String name)throws Exception {
        View view=dialog.getWindow().getDecorView();int width=786,height=1400;
        view.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(height,View.MeasureSpec.AT_MOST));view.layout(0,0,width,view.getMeasuredHeight());
        Bitmap bitmap=Bitmap.createBitmap(width,Math.max(1,view.getHeight()),Bitmap.Config.ARGB_8888);view.draw(new Canvas(bitmap));
        File directory=new File(System.getProperty("feiq.previewDir"));assertTrue(directory.isDirectory()||directory.mkdirs());try(OutputStream out=new FileOutputStream(new File(directory,name+".png"))){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,out));}bitmap.recycle();
    }
    @Test public void preferencesAndActualHttpLifetime()throws Exception {
        Context context=RuntimeEnvironment.getApplication();SharedPreferences prefs=context.getSharedPreferences("settings",0);prefs.edit().clear().commit();
        assertEquals(60,GuestTransfer.lifetimeMinutes(context));
        for(int invalid:new int[]{-1,0,1441,Integer.MAX_VALUE}){prefs.edit().putInt(GuestTransfer.LIFETIME_KEY,invalid).commit();assertEquals(60,GuestTransfer.lifetimeMinutes(context));}
        prefs.edit().putString(GuestTransfer.LIFETIME_KEY,"bad").commit();assertEquals(60,GuestTransfer.lifetimeMinutes(context));prefs.edit().remove(GuestTransfer.LIFETIME_KEY).commit();
        for(String invalid:new String[]{"","0","1441","-1","1.5","abc","999999999999"}) {
            try{GuestTransfer.saveLifetime(context,invalid);fail("接受非法有效期 "+invalid);}catch(IllegalArgumentException expected){assertTrue(expected.getMessage().contains("1–1440"));}
            assertEquals(60,GuestTransfer.lifetimeMinutes(context));
        }
        String oldToken=null;
        for(int minutes:new int[]{1,60,120,1440}) {
            GuestTransfer.saveLifetime(context,Integer.toString(minutes));prefs.edit().putBoolean("notifications",false).commit();assertEquals(minutes,GuestTransfer.lifetimeMinutes(context.createPackageContext(context.getPackageName(),0)));
            try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
                assertTrue(server.active());assertEquals(minutes,server.remainingMinutes());assertTrue(server.until-System.currentTimeMillis()>minutes*60000L-3000);assertTrue(server.until-System.currentTimeMillis()<=minutes*60000L);
                HttpURLConnection connection=request(server,server.token);assertEquals(200,connection.getResponseCode());String body;try(InputStream stream=connection.getInputStream()){body=new String(stream.readAllBytes(),java.nio.charset.StandardCharsets.UTF_8);}connection.disconnect();assertEquals(server.until,java.time.Instant.parse(new JSONObject(body).getString("until")).toEpochMilli());
                connection=request(server,oldToken==null?"0".repeat(64):oldToken);assertEquals(403,connection.getResponseCode());connection.disconnect();oldToken=server.token;
                set(server,"deadline",System.nanoTime()-1);assertFalse(server.active());connection=request(server,server.token);assertEquals(410,connection.getResponseCode());connection.disconnect();
                server.close();assertFalse(server.active());
            }
        }
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();
        try {assertNull("服务重建不能恢复旧链接",controller.get().guestTransfer());}finally{controller.destroy();}
    }
    @Test public void editorAndCountdownLightAndDark()throws Exception {
        Context context=RuntimeEnvironment.getApplication();SharedPreferences prefs=context.getSharedPreferences("settings",0);
        for(boolean dark:new boolean[]{false,true}) {
            prefs.edit().clear().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).putBoolean("dark",dark).putBoolean("dark_mode",dark).commit();
            ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();service.networkFactory=manager->new LanNetwork(manager,Collections::emptyList);settle(service);
            Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
            LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String message){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
            int port;try(ServerSocket socket=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){port=socket.getLocalPort();}
            try(LanNode node=new LanNode(port,true,"guest-ui","host","测试","",quiet);ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).setup()) {
                settle(service);node.start();set(service,"node",node);MainActivity activity=ac.get();invoke(activity,"showGuest");Dialog dialog=ShadowDialog.getLatestDialog();
                EditText input=input(dialog);assertEquals("60",input.getText().toString());input.setText("1441");button(dialog,"保存有效期").performClick();assertNotNull(input.getError());assertEquals(60,GuestTransfer.lifetimeMinutes(context));
                input.setText("120");button(dialog,"保存有效期").performClick();assertNull(input.getError());assertEquals(120,GuestTransfer.lifetimeMinutes(context));
                screenshot(dialog,"android-guest-settings-"+(dark?"dark":"light"));dialog.dismiss();
                invoke(activity,"settings");Dialog settings=ShadowDialog.getLatestDialog();button(settings,"保存").performClick();settle(service);assertEquals(120,GuestTransfer.lifetimeMinutes(context));if(settings.isShowing())settings.dismiss();
                invoke(activity,"showGuest");dialog=ShadowDialog.getLatestDialog();assertEquals("120",input(dialog).getText().toString());dialog.dismiss();
                try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
                    set(service,"guestTransfer",server);invoke(activity,"showGuest");dialog=ShadowDialog.getLatestDialog();screenshot(dialog,"android-guest-active-"+(dark?"dark":"light"));
                    set(server,"deadline",System.nanoTime()+TimeUnit.MINUTES.toNanos(2));Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofSeconds(1));
                    StringBuilder captions=new StringBuilder();for(View view:views(dialog.getWindow().getDecorView()))if(view instanceof TextView)captions.append(((TextView)view).getText()).append('|');
                    assertTrue("remaining="+server.remainingMinutes()+" showing="+dialog.isShowing()+" captions="+captions,captions.toString().contains("剩余约 2 分钟"));
                    set(server,"deadline",System.nanoTime()-1);Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofSeconds(1));assertFalse("到期仍显示可用链接",dialog.isShowing());
                    assertTrue(org.robolectric.shadows.ShadowToast.getTextOfLatestToast().contains("已结束"));service.stopGuest();assertNull(service.guestTransfer());
                }
            }finally{sc.destroy();}
        }
    }
}
