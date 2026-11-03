package org.feiqlight.android;

import android.app.*;
import android.content.*;
import android.media.projection.*;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.widget.*;

public final class PhoneRemoteActivity extends Activity {
    private final Handler main=new Handler(Looper.getMainLooper());
    private TextView state;
    private Button enable,stop;
    private AlertDialog confirmation;
    private String shownRequest="";
    private final Runnable refresh=new Runnable(){@Override public void run(){update();main.postDelayed(this,500);}};
    @Override protected void onCreate(Bundle saved){
        super.onCreate(saved);getWindow().addFlags(WindowManager.LayoutParams.FLAG_SECURE);
        boolean dark=getSharedPreferences("settings",MODE_PRIVATE).getBoolean("dark_mode",false);int ink=dark?0xffe9eef3:0xff17212b;
        LinearLayout box=new LinearLayout(this);box.setOrientation(LinearLayout.VERTICAL);box.setPadding(dp(16),dp(16),dp(16),dp(16));box.setBackgroundColor(dark?0xff17212b:0xffffffff);
        TextView title=new TextView(this);title.setText("允许控制本机");title.setTextSize(22);title.setTextColor(ink);box.addView(title);
        state=new TextView(this);state.setTextColor(ink);state.setTextSize(20);state.setPadding(0,dp(24),0,dp(24));state.setTextIsSelectable(true);box.addView(state);
        enable=new Button(this);enable.setText("开启手机远控");enable.setOnClickListener(v->enable());box.addView(enable,new LinearLayout.LayoutParams(-1,dp(48)));
        stop=new Button(this);stop.setText("停止远控");stop.setOnClickListener(v->{PhoneRemoteService.stopActive();update();});box.addView(stop,new LinearLayout.LayoutParams(-1,dp(48)));
        Button forget=new Button(this);forget.setText("清除已配对设备");forget.setOnClickListener(v->{try{PhoneRemoteService current=PhoneRemoteService.active();if(current!=null)current.forget();else new ToolsPairings(this).clear();}catch(java.io.IOException e){Toast.makeText(this,e.getMessage(),Toast.LENGTH_LONG).show();}});box.addView(forget,new LinearLayout.LayoutParams(-1,dp(48)));
        for(Button button:new Button[]{enable,stop,forget}){
            button.setAllCaps(false);button.setTextSize(14);button.setTextColor(new android.content.res.ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{dark?0xff758594:0xff7f8d99,ink}));
            android.graphics.drawable.GradientDrawable background=new android.graphics.drawable.GradientDrawable();background.setColor(dark?0xff22303d:0xfff1f4f7);background.setCornerRadius(dp(12));button.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x223390ec),background,null));
            ((LinearLayout.LayoutParams)button.getLayoutParams()).bottomMargin=dp(8);
        }
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);scroll.addView(box);setContentView(scroll);
        if(Build.VERSION.SDK_INT>=30){getWindow().setDecorFitsSystemWindows(false);scroll.setOnApplyWindowInsetsListener((v,insets)->{android.graphics.Insets b=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());v.setPadding(b.left,b.top,b.right,b.bottom);return insets;});}else scroll.setFitsSystemWindows(true);
    }
    private int dp(int n){return Math.round(n*getResources().getDisplayMetrics().density);}
    private void enable(){
        if(PhoneRemoteService.active()!=null)return;
        if(!RemoteInputService.available()){
            new AlertDialog.Builder(this).setTitle("开启远控辅助").setMessage(R.string.remote_accessibility_description).setNegativeButton("取消",null).setPositiveButton("打开系统设置",(d,w)->startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))).show();return;
        }
        if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED){requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS},45);return;}
        MediaProjectionManager manager=getSystemService(MediaProjectionManager.class);
        Intent intent=Build.VERSION.SDK_INT>=34?manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay()):manager.createScreenCaptureIntent();
        startActivityForResult(intent,44);
    }
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results){super.onRequestPermissionsResult(request,permissions,results);if(request==45&&results.length>0&&results[0]==android.content.pm.PackageManager.PERMISSION_GRANTED)enable();}
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(request==44&&result==RESULT_OK&&data!=null)startForegroundService(new Intent(this,PhoneRemoteService.class).putExtra("result",result).putExtra("projection",data));}
    private void update(){
        PhoneRemoteService service=PhoneRemoteService.active();state.setText(service==null?"未开启":service.connectedAddress().isEmpty()?"连接码  "+service.code():"已连接  "+service.connectedAddress());enable.setEnabled(service==null);stop.setEnabled(service!=null);
        String pending=service==null?"":service.pendingAddress();
        if(pending.isEmpty()){shownRequest="";if(confirmation!=null){confirmation.dismiss();confirmation=null;}return;}
        if(pending.equals(shownRequest))return;shownRequest=pending;
        CheckBox always=new CheckBox(this);always.setText("始终允许此设备");always.setPadding(dp(24),0,dp(16),0);
        confirmation=new AlertDialog.Builder(this).setTitle("允许远程控制？").setMessage(pending).setView(always).setNegativeButton("拒绝",(d,w)->service.answer(false)).setPositiveButton("允许",(d,w)->service.answer(true,always.isChecked())).setOnCancelListener(d->service.answer(false)).create();confirmation.show();
    }
    @Override protected void onResume(){super.onResume();main.post(refresh);}
    @Override protected void onPause(){main.removeCallbacks(refresh);super.onPause();}
    @Override protected void onDestroy(){main.removeCallbacksAndMessages(null);if(confirmation!=null)confirmation.dismiss();super.onDestroy();}
}
