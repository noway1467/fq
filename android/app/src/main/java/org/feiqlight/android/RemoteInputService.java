package org.feiqlight.android;

import android.accessibilityservice.*;
import android.graphics.Path;
import android.os.*;
import android.view.accessibility.*;
import org.feiqlight.android.core.ToolsHttp;
import org.json.JSONObject;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BooleanSupplier;

/** 只响应当前已确认远控会话，不监听、保存或上传界面文字。 */
public final class RemoteInputService extends AccessibilityService {
    private static volatile RemoteInputService instance;
    private final Handler main=new Handler(Looper.getMainLooper());
    static boolean available(){return instance!=null;}
    @Override protected void onServiceConnected(){instance=this;}
    @Override public void onAccessibilityEvent(AccessibilityEvent event){}
    @Override public void onInterrupt(){PhoneRemoteService.stopActive();}
    @Override public void onDestroy(){if(instance==this)instance=null;PhoneRemoteService.stopActive();super.onDestroy();}
    static String replaceSelection(String original,int start,int end,String text){
        if(start<0||end<0||start>original.length()||end>original.length())start=end=original.length();
        return original.substring(0,Math.min(start,end))+text+original.substring(Math.max(start,end));
    }
    static boolean protectedPackage(String name){return name==null||name.equals("org.feiqlight.android")||name.contains("settings")||name.contains("permissioncontroller")||name.contains("packageinstaller")||name.equals("com.android.systemui");}
    static void execute(JSONObject data,int width,int height,BooleanSupplier authorized)throws Exception{
        RemoteInputService service=instance;if(service==null)throw new ToolsHttp.Failure(503,"请在手机开启飞Q远控辅助服务");
        CompletableFuture<Boolean> result=new CompletableFuture<>();AtomicBoolean waiting=new AtomicBoolean(true);
        service.main.post(()->{
            if(!waiting.get()||!authorized.getAsBoolean()){result.complete(false);return;}
            try{service.apply(data,width,height,authorized,result);}catch(Exception e){result.completeExceptionally(e);}
        });
        try{if(!result.get(4,TimeUnit.SECONDS))throw new ToolsHttp.Failure(409,"手机未执行此操作，请检查当前界面");}
        catch(ExecutionException e){if(e.getCause() instanceof ToolsHttp.Failure)throw (ToolsHttp.Failure)e.getCause();throw e;}
        catch(TimeoutException e){throw new ToolsHttp.Failure(408,"手机操作超时");}
        finally{waiting.set(false);}
    }
    private void apply(JSONObject data,int width,int height,BooleanSupplier authorized,CompletableFuture<Boolean> result)throws Exception{
        String type=data.optString("type");
        if(type.equals("phone-key")){
            String key=data.optString("key");int action=key.equals("back")?GLOBAL_ACTION_BACK:key.equals("home")?GLOBAL_ACTION_HOME:key.equals("recents")?GLOBAL_ACTION_RECENTS:0;
            if(action==0)throw new ToolsHttp.Failure(400,"不支持的手机按键");result.complete(authorized.getAsBoolean()&&performGlobalAction(action));return;
        }
        AccessibilityNodeInfo root=getRootInActiveWindow();
        if(root==null)throw new ToolsHttp.Failure(409,"当前界面不可控制");
        try{
            if(protectedPackage(root.getPackageName()==null?null:root.getPackageName().toString()))throw new ToolsHttp.Failure(403,"系统授权和飞Q设置需在手机本机操作");
            if(type.equals("gesture")){
                float x=coordinate(data,"x",width),y=coordinate(data,"y",height),endX=coordinate(data,"endX",width),endY=coordinate(data,"endY",height);
                int duration=data.getInt("duration");if(duration<1||duration>1500)throw new ToolsHttp.Failure(400,"手势时长无效");
                Path path=new Path();path.moveTo(x,y);if(x!=endX||y!=endY)path.lineTo(endX,endY);
                GestureDescription gesture=new GestureDescription.Builder().addStroke(new GestureDescription.StrokeDescription(path,0,duration)).build();
                if(!authorized.getAsBoolean()||!dispatchGesture(gesture,new GestureResultCallback(){@Override public void onCompleted(GestureDescription value){result.complete(true);}@Override public void onCancelled(GestureDescription value){result.complete(false);}},main))result.complete(false);
                return;
            }
            AccessibilityNodeInfo focus=root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if(focus==null)throw new ToolsHttp.Failure(409,"请先选中手机上的输入框");
            try{
                if(focus.isPassword()||!focus.isEditable())throw new ToolsHttp.Failure(403,"此输入框不支持远程输入");
                if(type.equals("phone-enter")){
                    if(Build.VERSION.SDK_INT<30)throw new ToolsHttp.Failure(409,"此系统不支持远程输入法确认");
                    result.complete(authorized.getAsBoolean()&&focus.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.getId()));return;
                }
                if(!type.equals("text"))throw new ToolsHttp.Failure(400,"不支持的手机操作");
                String text=data.getString("text");if(text.length()>4000)throw new ToolsHttp.Failure(400,"输入文字过长");
                String original=focus.getText()==null?"":focus.getText().toString();if(original.length()>64000)throw new ToolsHttp.Failure(409,"输入框内容过长，请在手机处理");
                int start=focus.getTextSelectionStart(),end=focus.getTextSelectionEnd();String next=replaceSelection(original,start,end,text);
                Bundle args=new Bundle();args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,next);
                boolean done=authorized.getAsBoolean()&&focus.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,args);
                if(done){int caret=(start<0||end<0?original.length():Math.min(start,end))+text.length();Bundle selection=new Bundle();selection.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT,caret);selection.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT,caret);focus.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION,selection);}
                result.complete(done);
            }finally{focus.recycle();}
        }finally{root.recycle();}
    }
    private static float coordinate(JSONObject data,String key,int size)throws Exception{double value=data.getDouble(key);if(Double.isNaN(value)||Double.isInfinite(value)||value<0||value>10000)throw new ToolsHttp.Failure(400,"手势坐标无效");return (float)(value/10000*Math.max(0,size-1));}
}
