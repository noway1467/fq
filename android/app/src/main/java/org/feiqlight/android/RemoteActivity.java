package org.feiqlight.android;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.graphics.Color;
import android.net.Uri;
import android.os.*;
import android.view.*;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.webkit.*;
import android.widget.*;

/** 系统 WebView 只承载远控页面，不向网页暴露文件、剪贴板或 Java 桥。 */
public final class RemoteActivity extends Activity {
    private WebView web;
    private Uri target;
    private boolean landscape=true;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final Runnable loadTimeout=this::unavailable;
    private void unavailable(){if(!isFinishing()){Toast.makeText(this,"设备未连接或尚未开启远控",Toast.LENGTH_SHORT).show();finish();}}
    static boolean validTarget(Uri uri) {
        if(uri==null||!"http".equals(uri.getScheme())||uri.getHost()==null||uri.getUserInfo()!=null||uri.getPort()<1024||uri.getPort()>65535||!"/".equals(uri.getPath())||uri.getQuery()!=null||!"remote".equals(uri.getFragment()))return false;
        String[] parts=uri.getHost().split("\\.",-1);if(parts.length!=4)return false;
        for(String part:parts){if(!part.matches("[0-9]{1,3}")||Integer.parseInt(part)>255)return false;}return true;
    }
    private boolean samePage(Uri uri){return uri!=null&&target.getScheme().equals(uri.getScheme())&&target.getHost().equals(uri.getHost())&&target.getPort()==uri.getPort()&&uri.getUserInfo()==null&&("/".equals(uri.getPath())||"/index.html".equals(uri.getPath()));}
    static Intent lastConnection(Context context){
        android.content.SharedPreferences prefs=context.getSharedPreferences("settings",MODE_PRIVATE);
        Uri uri=Uri.parse(prefs.getString("last_remote_target",""));
        return validTarget(uri)?new Intent(context,RemoteActivity.class).setData(uri).putExtra("phone",prefs.getBoolean("last_remote_phone",false)):null;
    }
    @SuppressLint("SetJavaScriptEnabled")
    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);target=getIntent().getData();if(!validTarget(target)){Toast.makeText(this,"远控地址无效，请从联系人菜单打开",Toast.LENGTH_LONG).show();finish();return;}
        landscape=state==null?getSharedPreferences("settings",MODE_PRIVATE).getBoolean(getIntent().getBooleanExtra("phone",false)?"phone_remote_landscape":"remote_landscape",!getIntent().getBooleanExtra("phone",false)):state.getBoolean("landscape",true);setRequestedOrientation(landscape?ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE:ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        boolean dark=getSharedPreferences("settings",MODE_PRIVATE).getBoolean("dark_mode",false);int surface=dark?0xff17212b:Color.WHITE;
        LinearLayout root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setBackgroundColor(surface);
        web=new WebView(this){@Override public InputConnection onCreateInputConnection(EditorInfo attributes){InputConnection connection=super.onCreateInputConnection(attributes);attributes.imeOptions|=EditorInfo.IME_FLAG_NO_EXTRACT_UI|EditorInfo.IME_FLAG_NO_FULLSCREEN;return connection;}};web.setBackgroundColor(surface);WebSettings settings=web.getSettings();settings.setJavaScriptEnabled(true);settings.setAllowFileAccess(false);settings.setAllowContentAccess(false);settings.setDomStorageEnabled(true);settings.setSupportMultipleWindows(false);settings.setJavaScriptCanOpenWindowsAutomatically(false);settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);settings.setMediaPlaybackRequiresUserGesture(true);
        CookieManager.getInstance().setAcceptThirdPartyCookies(web,false);
        web.setWebChromeClient(new WebChromeClient(){@Override public void onPermissionRequest(PermissionRequest request){request.deny();}});
        web.setWebViewClient(new WebViewClient(){
            @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request){
                Uri uri=request.getUrl();
                if(request.isForMainFrame()&&samePage(Uri.parse(view.getUrl()==null?"":view.getUrl()))&&uri.toString().equals(target.buildUpon().path("/native/paired").fragment(null).build().toString())){
                    // 配对成功后立即刷盘，覆盖安装可能直接结束进程，不一定再触发 onPause。
                    CookieManager.getInstance().flush();
                    if(!getSharedPreferences("settings",MODE_PRIVATE).edit().putString("last_remote_target",target.toString()).putBoolean("last_remote_phone",getIntent().getBooleanExtra("phone",false)).commit())Toast.makeText(RemoteActivity.this,"上次远控地址未保存",Toast.LENGTH_SHORT).show();
                    return true;
                }
                if(request.isForMainFrame()&&uri.toString().equals(target.buildUpon().path("/native/unavailable").fragment(null).build().toString())){unavailable();return true;}
                if(request.isForMainFrame()&&samePage(Uri.parse(view.getUrl()==null?"":view.getUrl()))&&uri.toString().equals(target.buildUpon().path("/native/rotate").fragment(null).build().toString())){rotate();return true;}
                return !samePage(uri);
            }
            @Override public void onReceivedError(WebView view,WebResourceRequest request,WebResourceError problem){if(request.isForMainFrame())unavailable();}
            @Override public void onReceivedHttpError(WebView view,WebResourceRequest request,WebResourceResponse response){if(request.isForMainFrame())unavailable();}
            @Override public void onPageFinished(WebView view,String url){if(samePage(Uri.parse(url))){main.removeCallbacks(loadTimeout);view.evaluateJavascript("document.documentElement.dataset.native='true';document.documentElement.dataset.nativePairing='true';document.documentElement.dataset.theme='"+(dark?"dark":"light")+"';window.feiqCheck && window.feiqCheck();",null);updateRotation();}}
        });
        root.addView(web,new LinearLayout.LayoutParams(-1,0,1));setContentView(root);
        if(Build.VERSION.SDK_INT>=30){getWindow().setDecorFitsSystemWindows(false);root.setOnApplyWindowInsetsListener((v,insets)->{android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());android.graphics.Insets ime=insets.getInsets(WindowInsets.Type.ime());v.setPadding(bars.left,bars.top,bars.right,Math.max(bars.bottom,ime.bottom));return insets;});}
        else root.setFitsSystemWindows(true);
        hideSystemBars();
        main.postDelayed(loadTimeout,6000);web.loadUrl(target.toString());
    }
    private void rotate(){landscape=!landscape;getSharedPreferences("settings",MODE_PRIVATE).edit().putBoolean(getIntent().getBooleanExtra("phone",false)?"phone_remote_landscape":"remote_landscape",landscape).apply();setRequestedOrientation(landscape?ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE:ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT);updateRotation();}
    private void updateRotation(){web.evaluateJavascript("document.getElementById('rotate').textContent='"+(landscape?"竖屏":"横屏")+"';",null);}
    @SuppressWarnings("deprecation") private void hideSystemBars(){
        if(Build.VERSION.SDK_INT>=30){WindowInsetsController controller=getWindow().getInsetsController();if(controller!=null){controller.hide(WindowInsets.Type.systemBars());controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);}}
        else getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }
    @Override protected void onSaveInstanceState(Bundle state){state.putBoolean("landscape",landscape);super.onSaveInstanceState(state);}
    @Override protected void onPause(){if(web!=null){web.evaluateJavascript("window.feiqStop && window.feiqStop();",null);CookieManager.getInstance().flush();web.onPause();}super.onPause();}
    @Override protected void onResume(){super.onResume();if(web!=null){web.onResume();hideSystemBars();}}
    @Override protected void onDestroy(){main.removeCallbacksAndMessages(null);if(web!=null){web.stopLoading();((android.view.ViewGroup)web.getParent()).removeView(web);web.destroy();web=null;}super.onDestroy();}
}
