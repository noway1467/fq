package org.feiqlight.android;

import android.content.*;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.BooleanSupplier;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=35)
public final class PhoneRemoteTest {
    private Context context;
    private static final class Backend implements PhoneRemoteAccess.Backend {
        int allows=1,confirmations,inputs,writes;long delay;BooleanSupplier lastAuthorization;String clipboard;
        CountDownLatch entered,release;
        public int confirm(String address)throws Exception{confirmations++;if(entered!=null){entered.countDown();release.await(5,TimeUnit.SECONDS);}if(delay>0)Thread.sleep(delay);return allows;}
        public byte[] capture(){return new byte[]{1,2,3};}
        public byte[] captureDelta(long since,BooleanSupplier authorized){return new byte[]{70,81,70,49};}
        public void clipboard(String text,BooleanSupplier authorized){lastAuthorization=authorized;if(authorized.getAsBoolean()){clipboard=text;writes++;}}
        public JSONObject displays()throws Exception{return new JSONObject().put("platform","android").put("items",new JSONArray().put(new JSONObject().put("id",0)));}
        public void input(JSONObject data,BooleanSupplier authorized){lastAuthorization=authorized;if(authorized.getAsBoolean())inputs++;}
        public void disconnected(){}
    }
    private static final class Reply {int code;String text,session,pair;byte[] bytes;JSONObject json()throws Exception{return new JSONObject(text);}}
    private static Reply call(int port,String path,String cookie,String data,boolean csrf)throws Exception{
        HttpURLConnection connection=(HttpURLConnection)new URL("http://127.0.0.1:"+port+path).openConnection(Proxy.NO_PROXY);connection.setConnectTimeout(3000);connection.setReadTimeout(40000);connection.setRequestProperty("Cookie",cookie);
        if(data!=null){connection.setRequestMethod("POST");connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");if(csrf){connection.setRequestProperty("X-FQ-Reconnect","1");if(cookie.startsWith("fq_phone="))connection.setRequestProperty("X-FQ-Token",cookie.substring(9));}try(OutputStream output=connection.getOutputStream()){output.write(data.getBytes(StandardCharsets.UTF_8));}}
        Reply reply=new Reply();reply.code=connection.getResponseCode();
        try(InputStream input=reply.code>=400?connection.getErrorStream():connection.getInputStream()){reply.bytes=input.readAllBytes();reply.text=new String(reply.bytes,StandardCharsets.UTF_8);}
        for(Map.Entry<String,List<String>> header:connection.getHeaderFields().entrySet())if("Set-Cookie".equalsIgnoreCase(header.getKey()))for(String value:header.getValue()){if(value.startsWith("fq_phone="))reply.session=value.split(";")[0];if(value.startsWith("fq_pair_phone_"))reply.pair=value.split(";")[0];}
        connection.disconnect();return reply;
    }
    private static Reply login(PhoneRemoteAccess access)throws Exception{return call(access.port(),"/api/login","",new JSONObject().put("mode","remote").put("code",access.code()).toString(),true);}
    @Before public void setup()throws Exception{context=RuntimeEnvironment.getApplication();new ToolsPairings(context).clear();}
    @Test public void clipboardRoutesRequireAuthorizationRejectReadAndValidateText()throws Exception{
        Backend backend=new Backend();try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){
            assertEquals(401,call(server.port(),"/api/clipboard","","{\"text\":\"test\"}",true).code);
            Reply session=login(server);JSONObject displays=call(server.port(),"/api/displays",session.session,null,false).json();
            assertTrue(displays.getBoolean("clipboardWrite"));assertFalse(displays.getBoolean("clipboardRead"));
            assertEquals(403,call(server.port(),"/api/clipboard",session.session,null,false).code);
            assertEquals(403,call(server.port(),"/api/clipboard",session.session,"{\"text\":\"test\"}",false).code);
            for(String data:new String[]{"{\"text\":\"\"}","{\"text\":1}","{\"text\":\"a\\u0000b\"}","{\"text\":\"\\ud800\"}",new JSONObject().put("text","x".repeat(4001)).toString()})assertEquals(data.substring(0,Math.min(40,data.length())),400,call(server.port(),"/api/clipboard",session.session,data,true).code);
            assertEquals(0,backend.writes);
            assertEquals(200,call(server.port(),"/api/clipboard",session.session,new JSONObject().put("text","中文 😀\r\n下一行\t").toString(),true).code);
            assertEquals("中文 😀\r\n下一行\t",backend.clipboard);assertEquals(1,backend.writes);
            assertArrayEquals(new byte[]{70,81,70,49},call(server.port(),"/api/screen?delta=1&since=0",session.session,null,false).bytes);
            assertArrayEquals(new byte[]{1,2,3},call(server.port(),"/api/screen",session.session,null,false).bytes);
            assertEquals(400,call(server.port(),"/api/screen?delta=1&since=4294967296",session.session,null,false).code);
            call(server.port(),"/api/logout",session.session,"{}",true);assertFalse(backend.lastAuthorization.getAsBoolean());
            assertEquals(401,call(server.port(),"/api/clipboard",session.session,"{\"text\":\"again\"}",true).code);assertEquals(1,backend.writes);
        }
    }
    private static void field(Object target,String name,Object value)throws Exception{java.lang.reflect.Field f=target.getClass().getDeclaredField(name);f.setAccessible(true);f.set(target,value);}
    @Test public void deltaFrameWaitsWithoutPixelsWakesOnChangeAndHonorsRevocation()throws Exception{
        org.robolectric.android.controller.ServiceController<PhoneRemoteService> controller=Robolectric.buildService(PhoneRemoteService.class).create();
        try{
            PhoneRemoteService service=controller.get();field(service,"jpeg",new byte[]{1,2,3});field(service,"frameSeq",7L);field(service,"imageWidth",720);field(service,"imageHeight",1280);
            byte[] full=service.captureDelta(0,()->true);java.nio.ByteBuffer header=java.nio.ByteBuffer.wrap(full);
            assertEquals(0x46514631,header.getInt());assertEquals(1,full[4]);assertEquals(7,header.getInt(8));assertEquals(720,header.getShort(12));assertEquals(1280,header.getShort(14));assertEquals(33,full.length);
            long start=System.nanoTime();byte[] idle=service.captureDelta(7,()->true);
            assertEquals(18,idle.length);assertEquals(0,idle[4]);assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-start)>=275);
            CompletableFuture<byte[]> waiting=CompletableFuture.supplyAsync(()->{try{return service.captureDelta(7,()->true);}catch(Exception e){throw new CompletionException(e);}});
            java.lang.reflect.Field framesField=PhoneRemoteService.class.getDeclaredField("frames");framesField.setAccessible(true);Object frames=framesField.get(service);
            synchronized(frames){field(service,"frameSeq",8L);field(service,"jpeg",new byte[]{4,5,6});frames.notifyAll();}
            byte[] next=waiting.get(2,TimeUnit.SECONDS);assertEquals(1,next[4]);assertEquals(8,java.nio.ByteBuffer.wrap(next).getInt(8));
            try{service.captureDelta(8,()->false);fail("撤销后仍交付画面");}catch(org.feiqlight.android.core.ToolsHttp.Failure e){assertEquals(401,e.code);}
            assertArrayEquals(new byte[]{4,5,6},service.capture());
        }finally{controller.destroy();}
    }
    @Test public void clipboardWriteRunsOnMainThreadAndCancelledAuthorizationCannotWrite()throws Exception{
        org.robolectric.android.controller.ServiceController<PhoneRemoteService> controller=Robolectric.buildService(PhoneRemoteService.class).create();
        try{
            PhoneRemoteService service=controller.get();assertFalse(service.getSystemService(android.app.KeyguardManager.class).isKeyguardLocked());
            for(boolean authorized:new boolean[]{true,false}){
                CompletableFuture<Throwable> result=CompletableFuture.supplyAsync(()->{try{service.clipboard(authorized?"远控写入 😀\n测试":"不应写入",()->authorized);return null;}catch(Exception e){return e;}});
                long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
                while(!result.isDone()&&System.nanoTime()<until){Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();Thread.sleep(10);}
                Throwable failure=result.get(2,TimeUnit.SECONDS);
                if(authorized)assertNull(failure);else {assertTrue(failure instanceof org.feiqlight.android.core.ToolsHttp.Failure);assertEquals(403,((org.feiqlight.android.core.ToolsHttp.Failure)failure).code);}
                assertEquals("远控写入 😀\n测试",service.getSystemService(ClipboardManager.class).getPrimaryClip().getItemAt(0).getText().toString());
            }
            assertEquals("远控已写入剪贴板",org.robolectric.shadows.ShadowToast.getTextOfLatestToast());
        }finally{controller.destroy();}
    }
    @Test public void authenticatedPhoneRoutesAndScopeIsolation()throws Exception{
        Backend backend=new Backend();try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){
            assertTrue(server.code().matches("[0-9]{6}"));assertTrue(call(server.port(),"/api/status","",null,false).json().getBoolean("remote"));
            assertEquals(401,call(server.port(),"/api/screen","",null,false).code);
            Reply login=login(server);assertEquals(200,login.code);assertEquals(1,backend.confirmations);
            assertEquals("android",call(server.port(),"/api/displays",login.session,null,false).json().getString("platform"));
            assertEquals(200,call(server.port(),"/api/screen",login.session,null,false).code);
            assertEquals(403,call(server.port(),"/api/input",login.session,"{\"type\":\"text\",\"text\":\"测试\"}",false).code);assertEquals(0,backend.inputs);
            assertEquals(200,call(server.port(),"/api/input",login.session,"{\"type\":\"text\",\"text\":\"测试\"}",true).code);assertTrue(backend.lastAuthorization.getAsBoolean());
            assertEquals(404,call(server.port(),"/file",login.session,null,false).code);
            assertEquals(200,call(server.port(),"/api/logout",login.session,"{\"mode\":\"remote\"}",true).code);assertFalse(backend.lastAuthorization.getAsBoolean());
            assertEquals(200,call(server.port(),"/api/reconnect",login.pair,"{\"mode\":\"remote\"}",true).code);assertEquals(2,backend.confirmations);
        }
    }
    @Test public void alwaysAllowIsDeviceScopedPersistsAndCanBeRevoked()throws Exception{
        Backend backend=new Backend();backend.allows=2;String pair;int port;
        try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){
            Reply first=login(server);assertEquals(200,first.code);pair=first.pair;port=server.port();
            call(port,"/api/logout",first.session,"{}",true);assertEquals(200,call(port,"/api/reconnect",pair,"{\"mode\":\"remote\"}",true).code);assertEquals(1,backend.confirmations);
        }
        backend.allows=0;
        try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,port,true,backend)){
            assertEquals(200,call(port,"/api/reconnect",pair,"{\"mode\":\"remote\"}",true).code);assertEquals(1,backend.confirmations);
            server.forget();assertEquals(401,call(port,"/api/reconnect",pair,"{\"mode\":\"remote\"}",true).code);
            assertFalse(new ToolsPairings(context).contains(pair.substring(pair.indexOf('=')+1),"share:any"));
        }
    }
    @Test public void pendingApprovalCannotReviveClearedAuthorization()throws Exception{
        Backend backend=new Backend();backend.entered=new CountDownLatch(1);backend.release=new CountDownLatch(1);
        try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){
            CompletableFuture<Reply> future=CompletableFuture.supplyAsync(()->{try{return login(server);}catch(Exception e){throw new CompletionException(e);}});
            assertTrue(backend.entered.await(5,TimeUnit.SECONDS));server.forget();backend.release.countDown();assertEquals(403,future.get(5,TimeUnit.SECONDS).code);assertEquals("",server.address());
        }
    }
    @Test public void localConfirmationCanWaitBeyondNormalTransferTimeout()throws Exception{
        Backend backend=new Backend();backend.delay=17000;
        try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){assertEquals(200,login(server).code);assertEquals(1,backend.confirmations);}
    }
    @Test public void expiredSessionCleanupDoesNotCancelPendingReconnect()throws Exception{
        Backend backend=new Backend();
        try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){
            Reply first=login(server);assertEquals(200,first.code);
            java.lang.reflect.Field last=PhoneRemoteAccess.class.getDeclaredField("last");last.setAccessible(true);last.setLong(server,System.nanoTime()-TimeUnit.SECONDS.toNanos(31));
            backend.entered=new CountDownLatch(1);backend.release=new CountDownLatch(1);
            CompletableFuture<Reply> reconnect=CompletableFuture.supplyAsync(()->{try{return call(server.port(),"/api/reconnect",first.pair,"{\"mode\":\"remote\"}",true);}catch(Exception e){throw new CompletionException(e);}});
            try{assertTrue(backend.entered.await(5,TimeUnit.SECONDS));server.expire();}
            finally{backend.release.countDown();}
            Reply second=reconnect.get(5,TimeUnit.SECONDS);assertEquals(second.text,200,second.code);
            assertEquals(200,call(server.port(),"/api/logout",second.session,"{}",true).code);
            assertEquals(200,call(server.port(),"/api/reconnect",first.pair,"{\"mode\":\"remote\"}",true).code);
        }
    }
    @Test public void protectedWindowInputKeepsActionableHttpError()throws Exception{
        org.robolectric.android.controller.ServiceController<RemoteInputService> controller=Robolectric.buildService(RemoteInputService.class).create();
        RemoteInputService service=controller.get();service.onServiceConnected();
        android.view.accessibility.AccessibilityNodeInfo root=android.view.accessibility.AccessibilityNodeInfo.obtain();root.setPackageName("com.android.settings");
        org.robolectric.Shadows.shadowOf(service).setRootInActiveWindow(root);
        try{
            CompletableFuture<Throwable> result=CompletableFuture.supplyAsync(()->{
                try{RemoteInputService.execute(new JSONObject().put("type","text").put("text","test"),720,1280,()->true);return null;}
                catch(Exception error){return error;}
            });
            long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
            while(!result.isDone()&&System.nanoTime()<until){org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();Thread.sleep(10);}
            Throwable error=result.get(2,TimeUnit.SECONDS);
            assertTrue("输入拒绝被包装为通用错误："+error,error instanceof org.feiqlight.android.core.ToolsHttp.Failure);
            assertEquals(403,((org.feiqlight.android.core.ToolsHttp.Failure)error).code);
            assertTrue(error.getMessage().contains("本机操作"));
        }finally{controller.destroy();root.recycle();}
    }
    @Test public void portBoundariesAndTextSelectionDoNotTruncateUnicode(){
        assertEquals(2427,PhoneRemoteAccess.portFor(2425));assertEquals(65532,PhoneRemoteAccess.portFor(65534));assertEquals(65533,PhoneRemoteAccess.portFor(65535));
        assertEquals("a中文😀b",RemoteInputService.replaceSelection("ab",1,1,"中文😀"));assertEquals("a替换d",RemoteInputService.replaceSelection("abcd",3,1,"替换"));assertEquals("abc\n下一行",RemoteInputService.replaceSelection("abc",-1,-1,"\n下一行"));
        assertTrue(RemoteInputService.protectedPackage("com.android.settings"));assertTrue(RemoteInputService.protectedPackage("com.android.permissioncontroller"));assertTrue(RemoteInputService.protectedPackage("org.feiqlight.android"));assertFalse(RemoteInputService.protectedPackage("org.example.notes"));
    }
    @Test public void phoneScreenHasStopAndDoesNotStartCaptureWithoutUserAction(){
        try(org.robolectric.android.controller.ActivityController<PhoneRemoteActivity> controller=Robolectric.buildActivity(PhoneRemoteActivity.class).setup()){
            assertNull(PhoneRemoteService.active());assertNull(org.robolectric.Shadows.shadowOf(RuntimeEnvironment.getApplication()).getNextStartedService());assertTrue((controller.get().getWindow().getAttributes().flags&android.view.WindowManager.LayoutParams.FLAG_SECURE)!=0);
        }
    }
    @Test @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    public void renderPhoneControlSettingsLightDark()throws Exception{
        for(boolean dark:new boolean[]{false,true}){
            context.getSharedPreferences("settings",Context.MODE_PRIVATE).edit().putBoolean("dark_mode",dark).commit();
            try(org.robolectric.android.controller.ActivityController<PhoneRemoteActivity> controller=Robolectric.buildActivity(PhoneRemoteActivity.class).setup()){
                android.view.View view=controller.get().findViewById(android.R.id.content);view.measure(android.view.View.MeasureSpec.makeMeasureSpec(720,android.view.View.MeasureSpec.EXACTLY),android.view.View.MeasureSpec.makeMeasureSpec(1280,android.view.View.MeasureSpec.EXACTLY));view.layout(0,0,720,1280);
                android.graphics.Bitmap bitmap=android.graphics.Bitmap.createBitmap(720,1280,android.graphics.Bitmap.Config.ARGB_8888);view.draw(new android.graphics.Canvas(bitmap));
                File directory=new File(System.getProperty("feiq.previewDir"));assertTrue(directory.isDirectory()||directory.mkdirs());
                try(OutputStream output=new FileOutputStream(new File(directory,"phone-remote-"+(dark?"dark":"light")+".png"))){assertTrue(bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG,100,output));}finally{bitmap.recycle();}
            }
        }
    }
    @Test public void statusReportsPairingAndTrustedAndNativeEndpointsReturnNoContent()throws Exception{
        Backend backend=new Backend();backend.allows=2;
        try(PhoneRemoteAccess server=new PhoneRemoteAccess(context,0,true,backend)){
            Reply anonymous=call(server.port(),"/api/status","",null,false);
            assertEquals(200,anonymous.code);assertTrue(anonymous.json().getBoolean("remote"));assertFalse(anonymous.json().getBoolean("paired"));assertFalse(anonymous.json().getBoolean("trusted"));
            Reply first=login(server);assertEquals(200,first.code);
            Reply paired=call(server.port(),"/api/status",first.pair,null,false);
            assertEquals(200,paired.code);assertTrue(paired.json().getBoolean("paired"));assertTrue(paired.json().getBoolean("trusted"));
            assertEquals(204,call(server.port(),"/native/paired","",null,false).code);
            assertEquals(204,call(server.port(),"/native/unavailable","",null,false).code);
        }
    }
    @Test public void systemBarsFollowDarkModeAndSystemNight(){
        int lightMask=android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
        String qualifiers=RuntimeEnvironment.getQualifiers();
        try{
            context.getSharedPreferences("settings",Context.MODE_PRIVATE).edit().putBoolean("follow_system_theme",false).putBoolean("dark_mode",false).commit();
            try(org.robolectric.android.controller.ActivityController<PhoneRemoteActivity> controller=Robolectric.buildActivity(PhoneRemoteActivity.class).setup()){
                PhoneRemoteActivity activity=controller.get();
                assertEquals(0xffffffff,((android.graphics.drawable.ColorDrawable)activity.getWindow().getDecorView().getBackground()).getColor());
                assertEquals(lightMask,activity.getWindow().getInsetsController().getSystemBarsAppearance()&lightMask);
                if(android.os.Build.VERSION.SDK_INT<35)assertEquals(0xffffffff,activity.getWindow().getStatusBarColor());
            }
            context.getSharedPreferences("settings",Context.MODE_PRIVATE).edit().putBoolean("follow_system_theme",false).putBoolean("dark_mode",true).commit();
            try(org.robolectric.android.controller.ActivityController<PhoneRemoteActivity> controller=Robolectric.buildActivity(PhoneRemoteActivity.class).setup()){
                PhoneRemoteActivity activity=controller.get();
                assertEquals(0xff17212b,((android.graphics.drawable.ColorDrawable)activity.getWindow().getDecorView().getBackground()).getColor());
                assertEquals(0,activity.getWindow().getInsetsController().getSystemBarsAppearance()&lightMask);
                if(android.os.Build.VERSION.SDK_INT<35){assertEquals(0xff17212b,activity.getWindow().getStatusBarColor());assertEquals(0xff17212b,activity.getWindow().getNavigationBarColor());}
                android.view.View root=((android.view.ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0);
                assertEquals(0xff17212b,((android.graphics.drawable.ColorDrawable)root.getBackground()).getColor());
            }
            context.getSharedPreferences("settings",Context.MODE_PRIVATE).edit().putBoolean("follow_system_theme",true).putBoolean("dark_mode",false).commit();
            RuntimeEnvironment.setQualifiers("+night");
            try(org.robolectric.android.controller.ActivityController<PhoneRemoteActivity> controller=Robolectric.buildActivity(PhoneRemoteActivity.class).setup()){
                PhoneRemoteActivity activity=controller.get();
                assertEquals(0xff17212b,((android.graphics.drawable.ColorDrawable)activity.getWindow().getDecorView().getBackground()).getColor());
                assertEquals(0,activity.getWindow().getInsetsController().getSystemBarsAppearance()&lightMask);
            }
        }finally{RuntimeEnvironment.setQualifiers(qualifiers);}
    }
}
