package org.feiqlight.android;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.graphics.*;
import android.hardware.display.*;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.*;
import android.os.*;
import android.util.DisplayMetrics;
import android.view.*;
import org.feiqlight.android.core.ToolsHttp;
import org.json.*;
import java.io.ByteArrayOutputStream;
import java.util.concurrent.*;
import java.util.function.BooleanSupplier;

/** 系统授权的前台录屏服务；进程重启、锁屏或系统停止录屏后不自动恢复。 */
public final class PhoneRemoteService extends Service implements PhoneRemoteAccess.Backend {
    private static volatile PhoneRemoteService instance;
    private final Handler main=new Handler(Looper.getMainLooper());
    private final Object frames=new Object(),confirmation=new Object();
    private HandlerThread captureThread;
    private Handler captureHandler;
    private MediaProjection projection;
    private VirtualDisplay display;
    private volatile ImageReader reader;
    private PhoneRemoteAccess access;
    private CompletableFuture<Integer> approval;
    private String pendingAddress="";
    private byte[] jpeg;
    private int screenWidth,screenHeight,imageWidth,imageHeight,rotation;
    private long lastFrame;
    private volatile boolean stopping;
    private boolean receiverRegistered;
    static PhoneRemoteService active(){PhoneRemoteService service=instance;return service==null||service.stopping?null:service;}
    static void stopActive(){PhoneRemoteService service=instance;if(service!=null){service.stopping=true;service.answer(false);service.main.post(service::stopSelf);}}
    String code(){return access==null?"准备中":access.code();}
    String pendingAddress(){synchronized(confirmation){return pendingAddress;}}
    String connectedAddress(){return access==null?"":access.address();}
    void answer(boolean allowed){answer(allowed,false);}
    void answer(boolean allowed,boolean always){synchronized(confirmation){if(approval!=null)approval.complete(allowed?always?2:1:0);}}
    void forget()throws java.io.IOException{if(access!=null)access.forget();answer(false);}
    private final BroadcastReceiver locked=new BroadcastReceiver(){@Override public void onReceive(Context c,Intent intent){stopActive();}};
    private final Runnable heartbeat=new Runnable(){@Override public void run(){if(stopping)return;if(access!=null)access.expire();if(getSystemService(KeyguardManager.class).isKeyguardLocked()){stopActive();return;}updateNotification();main.postDelayed(this,1000);}};
    @Override public void onCreate(){
        super.onCreate();instance=this;captureThread=new HandlerThread("phone-screen-capture");captureThread.start();captureHandler=new Handler(captureThread.getLooper());
        NotificationManager manager=getSystemService(NotificationManager.class);manager.createNotificationChannel(new NotificationChannel("phone-remote","手机远程控制",NotificationManager.IMPORTANCE_DEFAULT));
        IntentFilter filter=new IntentFilter(Intent.ACTION_SCREEN_OFF);
        if(Build.VERSION.SDK_INT>=33)registerReceiver(locked,filter,Context.RECEIVER_NOT_EXPORTED);else registerReceiver(locked,filter);receiverRegistered=true;
    }
    @Override public IBinder onBind(Intent intent){return null;}
    @Override public int onStartCommand(Intent intent,int flags,int startId){
        if(intent==null||"stop".equals(intent.getAction())){stopActive();return START_NOT_STICKY;}
        if(projection!=null)return START_NOT_STICKY;
        try{
            if(!RemoteInputService.available())throw new IllegalStateException("请先开启飞Q远控辅助服务");
            if(Build.VERSION.SDK_INT>=29)startForeground(41,notification(),ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);else startForeground(41,notification());
            Intent result=Build.VERSION.SDK_INT>=33?intent.getParcelableExtra("projection",Intent.class):intent.getParcelableExtra("projection");
            if(result==null||intent.getIntExtra("result",0)!=Activity.RESULT_OK)throw new IllegalStateException("未获得系统录屏授权");
            projection=getSystemService(MediaProjectionManager.class).getMediaProjection(Activity.RESULT_OK,result);
            projection.registerCallback(new MediaProjection.Callback(){
                @Override public void onStop(){stopActive();}
                @Override public void onCapturedContentResize(int width,int height){
                    if(width<=0||height<=0||stopping)return;
                    DisplayMetrics metrics=realMetrics();
                    // 单应用投屏坐标不等于整屏触摸坐标，拒绝继续控制，避免点到其他窗口。
                    if(width!=metrics.widthPixels||height!=metrics.heightPixels){stopActive();return;}
                    rotation=getSystemService(WindowManager.class).getDefaultDisplay().getRotation();resize(width,height);
                }
            },main);
            DisplayMetrics metrics=realMetrics();rotation=getSystemService(WindowManager.class).getDefaultDisplay().getRotation();resize(metrics.widthPixels,metrics.heightPixels);
            int port=PhoneRemoteAccess.portFor(getSharedPreferences("settings",MODE_PRIVATE).getInt("port",2425));
            access=new PhoneRemoteAccess(this,port,false,this);main.post(heartbeat);
        }catch(Exception e){android.widget.Toast.makeText(this,"手机远控未开启："+e.getMessage(),android.widget.Toast.LENGTH_LONG).show();stopActive();}
        return START_NOT_STICKY;
    }
    @SuppressWarnings("deprecation") private DisplayMetrics realMetrics(){DisplayMetrics metrics=new DisplayMetrics();getSystemService(WindowManager.class).getDefaultDisplay().getRealMetrics(metrics);return metrics;}
    private void resize(int width,int height){
        if(stopping||projection==null)return;
        float scale=Math.min(1f,1280f/Math.max(width,height));int w=Math.max(2,Math.round(width*scale)),h=Math.max(2,Math.round(height*scale));
        synchronized(frames){if(reader!=null&&screenWidth==width&&screenHeight==height)return;screenWidth=width;screenHeight=height;imageWidth=w;imageHeight=h;jpeg=null;}
        ImageReader old=reader;reader=ImageReader.newInstance(w,h,PixelFormat.RGBA_8888,2);ImageReader source=reader;
        reader.setOnImageAvailableListener(value->readFrame(value,source,w,h),captureHandler);
        if(display==null)display=projection.createVirtualDisplay("飞Q手机远控",w,h,getResources().getDisplayMetrics().densityDpi,DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,reader.getSurface(),null,main);
        else{display.resize(w,h,getResources().getDisplayMetrics().densityDpi);display.setSurface(reader.getSurface());}
        if(old!=null)old.close();
    }
    private void readFrame(ImageReader value,ImageReader source,int width,int height){
        try(Image image=value.acquireLatestImage()){
            if(image==null||stopping||SystemClock.elapsedRealtime()-lastFrame<250)return;lastFrame=SystemClock.elapsedRealtime();
            Image.Plane plane=image.getPlanes()[0];int padded=plane.getRowStride()/plane.getPixelStride();
            Bitmap raw=Bitmap.createBitmap(padded,height,Bitmap.Config.ARGB_8888),cropped=null;
            try{
                java.nio.ByteBuffer buffer=plane.getBuffer();
                if(buffer.remaining()<raw.getByteCount()){java.nio.ByteBuffer paddedBuffer=java.nio.ByteBuffer.allocate(raw.getByteCount());paddedBuffer.put(buffer);paddedBuffer.rewind();buffer=paddedBuffer;}
                raw.copyPixelsFromBuffer(buffer);cropped=Bitmap.createBitmap(raw,0,0,width,height);
                ByteArrayOutputStream output=new ByteArrayOutputStream();if(!cropped.compress(Bitmap.CompressFormat.JPEG,72,output))throw new IllegalStateException("画面压缩失败");
                synchronized(frames){if(reader==source&&!stopping){jpeg=output.toByteArray();frames.notifyAll();}}
            }finally{if(cropped!=null&&cropped!=raw)cropped.recycle();raw.recycle();}
        }catch(RuntimeException e){if(reader==source&&!stopping)stopActive();}
    }
    @Override public void onConfigurationChanged(android.content.res.Configuration config){super.onConfigurationChanged(config);if(Build.VERSION.SDK_INT<34){DisplayMetrics metrics=realMetrics();resize(metrics.widthPixels,metrics.heightPixels);}rotation=getSystemService(WindowManager.class).getDefaultDisplay().getRotation();}
    @Override public byte[] capture()throws Exception{
        synchronized(frames){long until=SystemClock.elapsedRealtime()+3000;while(jpeg==null&&!stopping&&SystemClock.elapsedRealtime()<until)frames.wait(Math.max(1,until-SystemClock.elapsedRealtime()));if(stopping||jpeg==null)throw new ToolsHttp.Failure(503,"手机画面尚未就绪");return jpeg;}
    }
    @Override public JSONObject displays()throws Exception{return new JSONObject().put("platform","android").put("imeEnter",Build.VERSION.SDK_INT>=30).put("items",new JSONArray().put(new JSONObject().put("id",0).put("name","手机")));}
    @Override public void input(JSONObject data,BooleanSupplier authorized)throws Exception{
        final int w,h,iw,ih,r;
        synchronized(frames){w=screenWidth;h=screenHeight;iw=imageWidth;ih=imageHeight;r=rotation;}
        if(data.optInt("frameWidth")!=iw||data.optInt("frameHeight")!=ih)throw new ToolsHttp.Failure(409,"画面尺寸已变化，请等待刷新");
        RemoteInputService.execute(data,w,h,()->{synchronized(frames){return !stopping&&imageWidth==iw&&imageHeight==ih&&rotation==r&&authorized.getAsBoolean()&&!getSystemService(KeyguardManager.class).isKeyguardLocked();}});
    }
    @Override public int confirm(String address)throws Exception{
        CompletableFuture<Integer> decision=new CompletableFuture<>();
        synchronized(confirmation){if(stopping)return 0;approval=decision;pendingAddress=address;}
        main.post(this::updateNotification);
        try{int result=decision.get(30,TimeUnit.SECONDS);return stopping?0:result;}catch(TimeoutException e){return 0;}
        finally{synchronized(confirmation){if(approval==decision){approval=null;pendingAddress="";}}main.post(this::updateNotification);}
    }
    @Override public void disconnected(){main.post(this::updateNotification);}
    private Notification notification(){
        PendingIntent open=PendingIntent.getActivity(this,41,new Intent(this,PhoneRemoteActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop=PendingIntent.getService(this,42,new Intent(this,PhoneRemoteService.class).setAction("stop"),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        String waiting=pendingAddress(),connected=connectedAddress();
        return new Notification.Builder(this,"phone-remote").setSmallIcon(R.drawable.ic_notify).setContentTitle(waiting.isEmpty()?connected.isEmpty()?"手机远控已开启":"手机正在被远程控制":"远控请求待确认").setContentText(waiting.isEmpty()?connected:waiting).setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true).setVisibility(Notification.VISIBILITY_PRIVATE).addAction(new Notification.Action.Builder(null,"停止远控",stop).build()).build();
    }
    private void updateNotification(){if(!stopping)getSystemService(NotificationManager.class).notify(41,notification());}
    @Override public void onDestroy(){
        stopping=true;if(instance==this)instance=null;main.removeCallbacksAndMessages(null);answer(false);
        if(access!=null){access.close();access=null;}synchronized(frames){jpeg=null;frames.notifyAll();}
        if(display!=null){display.release();display=null;}ImageReader old=reader;reader=null;if(old!=null)old.close();
        if(projection!=null){projection.stop();projection=null;}if(captureThread!=null)captureThread.quitSafely();
        if(receiverRegistered)unregisterReceiver(locked);stopForeground(STOP_FOREGROUND_REMOVE);super.onDestroy();
    }
}
