package org.feiqlight.android;

import android.app.*;
import android.content.*;
import android.graphics.*;
import android.media.MediaMetadataRetriever;
import android.net.Uri;
import android.os.*;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.util.concurrent.*;

/** 有界后台解码；不读取网络地址、不自动播放，视图销毁后不回写。 */
final class MediaPreview {
    private final Activity activity;
    private final ThreadPoolExecutor worker=new ThreadPoolExecutor(1,1,0,TimeUnit.SECONDS,new ArrayBlockingQueue<>(32),r->{Thread t=new Thread(r,"media-preview"); t.setDaemon(true); return t;},new ThreadPoolExecutor.AbortPolicy());
    private final Handler main=new Handler(Looper.getMainLooper());
    private boolean closed;
    MediaPreview(Activity activity) { this.activity=activity; }
    static boolean supported(String name) { String mime=ReceivedStorage.mime(name); return mime.startsWith("image/")||mime.startsWith("video/"); }
    static boolean video(String name) { return ReceivedStorage.mime(name).startsWith("video/"); }
    static Bitmap image(Context context,Uri uri,int edge) throws IOException {
        BitmapFactory.Options o=new BitmapFactory.Options(); o.inJustDecodeBounds=true;
        try(InputStream in=context.getContentResolver().openInputStream(uri)) { BitmapFactory.decodeStream(in,null,o); }
        if(o.outWidth<=0||o.outHeight<=0||(long)o.outWidth*o.outHeight>100000000) throw new IOException("图片尺寸过大或格式不支持");
        o.inJustDecodeBounds=false; o.inSampleSize=1; while(Math.max(o.outWidth,o.outHeight)/o.inSampleSize>edge) o.inSampleSize*=2;
        try(InputStream in=context.getContentResolver().openInputStream(uri)) { Bitmap bitmap=BitmapFactory.decodeStream(in,null,o); if(bitmap==null) throw new IOException("无法解码图片"); return bitmap; }
    }
    View thumbnail(Uri uri,String name,int width,Runnable actions) { return thumbnail(uri,name,width,()->open(uri,name),actions); }
    View thumbnail(Uri uri,String name,int width,Runnable openFile,Runnable actions) {
        FrameLayout frame=new FrameLayout(activity); ImageView picture=new ImageView(activity); picture.setScaleType(ImageView.ScaleType.FIT_CENTER); frame.setBackgroundColor(0xffe9edf1); frame.addView(picture,new FrameLayout.LayoutParams(-1,-1));
        TextView status=new TextView(activity); status.setText("正在生成预览…"); status.setTextColor(0xff17212b); status.setGravity(Gravity.CENTER); frame.addView(status,new FrameLayout.LayoutParams(-1,-1));
        frame.setContentDescription(name+"，媒体预览"); frame.setLayoutParams(new LinearLayout.LayoutParams(width,width*2/3));
        frame.setOnClickListener(v->openFile.run()); frame.setOnLongClickListener(v->{actions.run();return true;});
        try { worker.execute(()->{
            Bitmap result=null; String caption="";
            try {
                if(video(name)) {
                    try(MediaMetadataRetriever retriever=new MediaMetadataRetriever()) {
                        retriever.setDataSource(activity,uri); String duration=retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION);
                        long seconds=duration==null?0:Long.parseLong(duration)/1000;
                        if(Build.VERSION.SDK_INT>=27) result=retriever.getScaledFrameAtTime(0,MediaMetadataRetriever.OPTION_CLOSEST_SYNC,480,320);
                        else {
                            String w=retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH), h=retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT);
                            if(w!=null&&h!=null&&(long)Integer.parseInt(w)*Integer.parseInt(h)<=8294400) {
                                Bitmap frameBitmap=retriever.getFrameAtTime(0,MediaMetadataRetriever.OPTION_CLOSEST_SYNC);
                                if(frameBitmap!=null) {result=Bitmap.createScaledBitmap(frameBitmap,480,320,true);if(result!=frameBitmap)frameBitmap.recycle();}
                            }
                        }
                        caption="▶  "+String.format(java.util.Locale.ROOT,"%d:%02d",seconds/60,seconds%60);
                    }
                } else result=image(activity,uri,480);
                if(result==null) caption="预览不可用 · 点击尝试打开";
            } catch(Exception|OutOfMemoryError e) { caption="预览不可用 · 长按文件操作"; }
            Bitmap bitmap=result; String text=caption;
            main.post(()->{if(closed||!frame.isAttachedToWindow()) {if(bitmap!=null)bitmap.recycle();return;} picture.setImageBitmap(bitmap);status.setText(text);});
        }); } catch(RejectedExecutionException e) {status.setText("预览队列繁忙 · 点击查看");}
        return frame;
    }
    void open(Uri uri,String name) {
        if(video(name)) {
            Dialog dialog=new Dialog(activity,android.R.style.Theme_Material_Light_NoActionBar_Fullscreen); LinearLayout layout=new LinearLayout(activity);layout.setOrientation(LinearLayout.VERTICAL);
            Button close=new Button(activity);close.setText("关闭视频");layout.addView(close); VideoView video=new VideoView(activity);layout.addView(video,new LinearLayout.LayoutParams(-1,0,1));
            MediaController controls=new MediaController(activity);controls.setAnchorView(video);video.setMediaController(controls);
            video.setOnErrorListener((player,what,extra)->{Toast.makeText(activity,"格式不支持或文件已失效，请使用文件菜单打开",Toast.LENGTH_LONG).show();dialog.dismiss();return true;});
            close.setOnClickListener(v->dialog.dismiss());dialog.setOnDismissListener(d->video.stopPlayback());dialog.setContentView(layout);dialog.show();video.setVideoURI(uri);video.setOnPreparedListener(player->{video.start();controls.show();});return;
        }
        Dialog dialog=new Dialog(activity,android.R.style.Theme_Material_Light_NoActionBar_Fullscreen); LinearLayout layout=new LinearLayout(activity);layout.setOrientation(LinearLayout.VERTICAL);
        Button close=new Button(activity);close.setText("关闭图片 · 双指缩放 / 拖动");layout.addView(close); ZoomImage image=new ZoomImage(activity);layout.addView(image,new LinearLayout.LayoutParams(-1,0,1));dialog.setContentView(layout);close.setOnClickListener(v->dialog.dismiss());dialog.show();
        try {worker.execute(()->{Bitmap bitmap=null;try{bitmap=image(activity,uri,2048);}catch(Exception|OutOfMemoryError ignored){} Bitmap result=bitmap;main.post(()->{if(closed||!dialog.isShowing()){if(result!=null)result.recycle();return;}if(result==null){Toast.makeText(activity,"图片无法预览",Toast.LENGTH_LONG).show();dialog.dismiss();}else{image.setImageBitmap(result);dialog.setOnDismissListener(d->{image.setImageDrawable(null);result.recycle();});}});});}catch(RejectedExecutionException e){dialog.dismiss();Toast.makeText(activity,"预览繁忙，请稍后重试",Toast.LENGTH_SHORT).show();}
    }
    void close() {closed=true;worker.shutdownNow();}
    static final class ZoomImage extends ImageView {
        private float zoom=1,base=1,x,y,lastX,lastY,lastSpan;
        private int pointers;
        private boolean moved;
        private final Matrix transform=new Matrix();
        ZoomImage(Context c) { super(c); setScaleType(ScaleType.MATRIX); setBackgroundColor(Color.BLACK); }
        @Override public void setImageBitmap(Bitmap bitmap) { super.setImageBitmap(bitmap); fit(); }
        @Override protected void onSizeChanged(int w,int h,int oldw,int oldh) { super.onSizeChanged(w,h,oldw,oldh); if(w!=oldw||h!=oldh) fit(); }
        private void fit() {
            if(getDrawable()==null||getWidth()==0||getHeight()==0) return;
            int w=getDrawable().getIntrinsicWidth(),h=getDrawable().getIntrinsicHeight(); if(w<=0||h<=0) return;
            base=Math.min((float)getWidth()/w,(float)getHeight()/h); zoom=1; pointers=0;
            x=(getWidth()-w*base)/2; y=(getHeight()-h*base)/2; apply();
        }
        private void apply() {
            if(getDrawable()==null) return;
            float w=getDrawable().getIntrinsicWidth()*base*zoom,h=getDrawable().getIntrinsicHeight()*base*zoom;
            x=w<=getWidth()?(getWidth()-w)/2:Math.max(getWidth()-w,Math.min(0,x));
            y=h<=getHeight()?(getHeight()-h)/2:Math.max(getHeight()-h,Math.min(0,y));
            transform.setScale(base*zoom,base*zoom); transform.postTranslate(x,y); setImageMatrix(transform);
        }
        @Override public boolean onTouchEvent(MotionEvent e) {
            int action=e.getActionMasked();
            if(action==MotionEvent.ACTION_UP||action==MotionEvent.ACTION_CANCEL) {
                pointers=0; lastSpan=0;
                if(getParent()!=null) getParent().requestDisallowInterceptTouchEvent(false);
                if(action==MotionEvent.ACTION_UP&&!moved) performClick(); return true;
            }
            if(action==MotionEvent.ACTION_DOWN) { moved=false; if(getParent()!=null) getParent().requestDisallowInterceptTouchEvent(true); }
            int skip=action==MotionEvent.ACTION_POINTER_UP?e.getActionIndex():-1;
            int count=e.getPointerCount()-(skip>=0?1:0); if(count==0) return true;
            float focusX=0,focusY=0,span=0;
            for(int i=0;i<e.getPointerCount();i++) if(i!=skip) { focusX+=e.getX(i); focusY+=e.getY(i); }
            focusX/=count; focusY/=count;
            for(int i=0;i<e.getPointerCount();i++) if(i!=skip) { float dx=e.getX(i)-focusX,dy=e.getY(i)-focusY; span+=dx*dx+dy*dy; }
            span=(float)Math.sqrt(span/count);
            // 始终在不变的 View 坐标系中变换图片。增减手指只重设基线，绝不使用上一组手指的坐标。
            if(action==MotionEvent.ACTION_MOVE&&pointers==count) {
                float next=count>=2&&lastSpan>0?Math.max(1,Math.min(5,zoom*span/lastSpan)):zoom;
                float factor=next/zoom;
                x=focusX+(x-lastX)*factor; y=focusY+(y-lastY)*factor; zoom=next; apply();
                if(Math.abs(focusX-lastX)+Math.abs(focusY-lastY)>1||Math.abs(factor-1)>0.001f) moved=true;
            }
            pointers=count; lastX=focusX; lastY=focusY; lastSpan=span; return true;
        }
        @Override public boolean performClick() { super.performClick(); return true; }
    }
}
