package org.feiqlight.android;

import android.content.*;
import android.content.pm.ActivityInfo;
import android.net.Uri;
import android.view.*;
import android.webkit.WebView;
import android.webkit.WebResourceRequest;
import android.widget.Button;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.android.controller.ActivityController;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w800dp-h360dp-land")
public final class RemoteActivityTest {
    @Test public void onlyAcceptsExplicitIpv4RemoteTargets(){
        assertTrue(RemoteActivity.validTarget(Uri.parse("http://192.168.1.5:2426/#remote")));
        for(String value:new String[]{"file:///data/secret","http://example.org:2426/#remote","http://127.0.0.1:80/#remote","http://user@127.0.0.1:2426/#remote","http://127.0.0.1:2426/file#remote","http://999.0.0.1:2426/#remote","http://127.0.0.1:2426/?next=bad#remote"})assertFalse(value,RemoteActivity.validTarget(Uri.parse(value)));
    }
    @Test public void defaultsLandscapeRotatesWithoutReplacingWebViewAndRestrictsCapabilities(){
        Intent intent=new Intent(RuntimeEnvironment.getApplication(),RemoteActivity.class).setData(Uri.parse("http://127.0.0.1:2426/#remote"));
        try(ActivityController<RemoteActivity> controller=Robolectric.buildActivity(RemoteActivity.class,intent).setup()){
            RemoteActivity activity=controller.get();assertEquals(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE,activity.getRequestedOrientation());
            WebView web=find(activity.getWindow().getDecorView(),WebView.class);assertNotNull(web);assertTrue(web.getSettings().getJavaScriptEnabled());assertFalse(web.getSettings().getAllowFileAccess());assertFalse(web.getSettings().getAllowContentAccess());assertFalse(web.getSettings().supportMultipleWindows());
            assertEquals("http://127.0.0.1:2426/#remote",shadowOf(web).getLastLoadedUrl());
            assertTrue(web.getSettings().getDomStorageEnabled());assertNull(find(activity.getWindow().getDecorView(),Button.class));
            assertTrue(web.getWebViewClient().shouldOverrideUrlLoading(web,request("http://evil.invalid:2426/native/rotate")));assertEquals(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE,activity.getRequestedOrientation());
            assertTrue(web.getWebViewClient().shouldOverrideUrlLoading(web,request("http://127.0.0.1:2426/native/rotate")));assertEquals(ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT,activity.getRequestedOrientation());assertSame(web,find(activity.getWindow().getDecorView(),WebView.class));
            assertFalse(activity.getSharedPreferences("settings",Context.MODE_PRIVATE).getBoolean("remote_landscape",true));
            controller.pause();assertTrue(shadowOf(web).getLastEvaluatedJavascript().contains("feiqStop"));controller.stop();
        }
    }
    @Test public void unavailablePageExitsInsteadOfLeavingWhiteWebView(){
        Intent intent=new Intent(RuntimeEnvironment.getApplication(),RemoteActivity.class).setData(Uri.parse("http://127.0.0.1:2426/#remote"));
        try(ActivityController<RemoteActivity> controller=Robolectric.buildActivity(RemoteActivity.class,intent).setup()){
            WebView web=find(controller.get().getWindow().getDecorView(),WebView.class);
            web.getWebViewClient().onReceivedError(web,request("http://127.0.0.1:2426/#remote"),null);assertTrue(controller.get().isFinishing());
        }
    }
    @Test public void slowConnectionExitsAndPhoneViewerDefaultsPortrait(){
        Intent intent=new Intent(RuntimeEnvironment.getApplication(),RemoteActivity.class).setData(Uri.parse("http://127.0.0.1:2427/#remote")).putExtra("phone",true);
        try(ActivityController<RemoteActivity> controller=Robolectric.buildActivity(RemoteActivity.class,intent).setup()){
            assertEquals(ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT,controller.get().getRequestedOrientation());
            shadowOf(android.os.Looper.getMainLooper()).idleFor(java.time.Duration.ofSeconds(7));assertTrue(controller.get().isFinishing());
        }
    }
    @Test public void lastRemoteTargetSurvivesActivityRecreationAndForeignPageCannotReplaceIt(){
        android.content.Context context=RuntimeEnvironment.getApplication();assertNull(RemoteActivity.lastConnection(context));
        Intent intent=new Intent(context,RemoteActivity.class).setData(Uri.parse("http://127.0.0.1:2427/#remote")).putExtra("phone",true);
        try(ActivityController<RemoteActivity> controller=Robolectric.buildActivity(RemoteActivity.class,intent).setup()){
            WebView web=find(controller.get().getWindow().getDecorView(),WebView.class);
            web.getWebViewClient().shouldOverrideUrlLoading(web,request("http://192.168.0.10:2427/native/paired"));assertNull(RemoteActivity.lastConnection(context));
            assertTrue(web.getWebViewClient().shouldOverrideUrlLoading(web,request("http://127.0.0.1:2427/native/paired")));
        }
        Intent restored=RemoteActivity.lastConnection(context);assertNotNull(restored);assertEquals(intent.getData(),restored.getData());assertTrue(restored.getBooleanExtra("phone",false));
        try(ActivityController<RemoteActivity> controller=Robolectric.buildActivity(RemoteActivity.class,restored).setup()){
            assertEquals(intent.getData().toString(),shadowOf(find(controller.get().getWindow().getDecorView(),WebView.class)).getLastLoadedUrl());
        }
    }
    @Test public void samePageAcceptsRootEmptyPathAndResumeChecksStatus() {
        android.content.Context context=RuntimeEnvironment.getApplication();
        Intent intent=new Intent(context,RemoteActivity.class).setData(Uri.parse("http://127.0.0.1:2427/#remote"));
        try(ActivityController<RemoteActivity> controller=Robolectric.buildActivity(RemoteActivity.class,intent).setup()){
            RemoteActivity activity=controller.get();
            WebView web=find(activity.getWindow().getDecorView(),WebView.class);
            assertFalse(web.getWebViewClient().shouldOverrideUrlLoading(web,request("http://127.0.0.1:2427")));
            assertTrue(web.getWebViewClient().shouldOverrideUrlLoading(web,request("http://127.0.0.1:2427/native/paired")));
            assertEquals("http://127.0.0.1:2427/#remote",RemoteActivity.lastConnection(context).getDataString());
            controller.pause();controller.resume();
            assertTrue(shadowOf(web).getLastEvaluatedJavascript().contains("feiqCheck"));
        }
    }
    private static WebResourceRequest request(String address){return new WebResourceRequest(){public Uri getUrl(){return Uri.parse(address);}public boolean isForMainFrame(){return true;}public boolean isRedirect(){return false;}public boolean hasGesture(){return true;}public String getMethod(){return "GET";}public java.util.Map<String,String> getRequestHeaders(){return java.util.Collections.emptyMap();}};}
    private static <T extends View> T find(View view,Class<T> type){if(type.isInstance(view))return type.cast(view);if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){T match=find(((ViewGroup)view).getChildAt(i),type);if(match!=null)return match;}return null;}
}
