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
        int orientation="image/jpeg".equals(o.outMimeType)?orientation(context,uri):1;
        try(InputStream in=context.getContentResolver().openInputStream(uri)) { Bitmap bitmap=BitmapFactory.decodeStream(in,null,o); if(bitmap==null) throw new IOException("无法解码图片"); return upright(bitmap,orientation); }
    }
    /** 只读文件头，得到按 EXIF 方向校正后的宽高，供后台快照预先确定预览框比例；不可读或不是图片返回 null。 */
    static int[] bounds(Context context,Uri uri) {
        BitmapFactory.Options o=new BitmapFactory.Options(); o.inJustDecodeBounds=true;
        try(InputStream in=context.getContentResolver().openInputStream(uri)) { if(in==null) return null; BitmapFactory.decodeStream(in,null,o); }
        catch(IOException|RuntimeException e) { return null; }
        if(o.outWidth<=0||o.outHeight<=0||(long)o.outWidth*o.outHeight>100000000) return null;
        int orientation="image/jpeg".equals(o.outMimeType)?orientation(context,uri):1;
        return orientation>=5&&orientation<=8?new int[]{o.outHeight,o.outWidth}:new int[]{o.outWidth,o.outHeight};
    }
    /** 只对 JPEG 读取 EXIF；相机照片的旋转和镜像同时用于尺寸与解码，避免预览框和实际图像方向不一致。 */
    private static int orientation(Context context,Uri uri) {
        try(InputStream in=context.getContentResolver().openInputStream(uri)) {
            if(in==null) return 1;
            return new android.media.ExifInterface(in).getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION,1);
        } catch(IOException|RuntimeException|OutOfMemoryError|StackOverflowError e) { return 1; }
    }
    private static Bitmap upright(Bitmap bitmap,int orientation) {
        Matrix matrix=new Matrix();
        switch(orientation) {
            case 2:matrix.setScale(-1,1);break;
            case 3:matrix.setRotate(180);break;
            case 4:matrix.setScale(1,-1);break;
            case 5:matrix.setRotate(90);matrix.postScale(-1,1);break;
            case 6:matrix.setRotate(90);break;
            case 7:matrix.setRotate(270);matrix.postScale(-1,1);break;
            case 8:matrix.setRotate(270);break;
            default:return bitmap;
        }
        Bitmap turned=Bitmap.createBitmap(bitmap,0,0,bitmap.getWidth(),bitmap.getHeight(),matrix,true);
        if(turned!=bitmap) bitmap.recycle();
        return turned;
    }
    /** 预览框外观：像素尺寸、四角圆角（左上、右上、右下、左下）、占位底色、占位文字色与发丝描边色。 */
    static final class Look {
        final int width,height,fill,ink,edge; final float[] corners;
        Look(int width,int height,float[] corners,int fill,int ink,int edge) { this.width=width;this.height=height;this.corners=corners;this.fill=fill;this.ink=ink;this.edge=edge; }
    }
    /** 圆角铺满裁切的预览图。用 BitmapShader 画圆角路径，软件画布与硬件画布都有抗锯齿边缘；
     *  视频叠加播放标记与时长，传输中叠加居中进度环。 */
    static final class Picture extends ImageView {
        private final Paint image=new Paint(Paint.ANTI_ALIAS_FLAG|Paint.FILTER_BITMAP_FLAG),fill=new Paint(Paint.ANTI_ALIAS_FLAG),rim=new Paint(Paint.ANTI_ALIAS_FLAG),scrim=new Paint(Paint.ANTI_ALIAS_FLAG),mark=new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Path shape=new Path(),glyph=new Path();
        private final Matrix matrix=new Matrix();
        private final float[] radii=new float[8];
        private Bitmap bitmap; private String play; private int progress=-1;
        Picture(Context context,Look look) {
            super(context);
            for(int i=0;i<4;i++) radii[i*2]=radii[i*2+1]=look.corners[i];
            fill.setColor(look.fill); scrim.setColor(0x73000000); mark.setColor(Color.WHITE); mark.setStrokeCap(Paint.Cap.ROUND);
            rim.setStyle(Paint.Style.STROKE); rim.setStrokeWidth(Math.max(1,getResources().getDisplayMetrics().density)); rim.setColor(look.edge);
            setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
        }
        @Override public void setImageBitmap(Bitmap value) {
            bitmap=value; image.setShader(value==null?null:new BitmapShader(value,Shader.TileMode.CLAMP,Shader.TileMode.CLAMP));
            super.setImageBitmap(value); fit(); invalidate();
        }
        /** 视频预览就绪后显示居中播放标记；时长为空串时只画标记。 */
        void play(String length) { play=length; invalidate(); }
        /** 0–100 显示居中进度环，负数隐藏。 */
        void progress(int percent) { if(progress!=percent) { progress=percent; invalidate(); } }
        int progress() { return progress; }
        @Override protected void onSizeChanged(int w,int h,int oldw,int oldh) { super.onSizeChanged(w,h,oldw,oldh); shape.reset(); shape.addRoundRect(0,0,w,h,radii,Path.Direction.CW); fit(); }
        private void fit() {
            if(bitmap==null||getWidth()==0||getHeight()==0) return;
            float scale=Math.max((float)getWidth()/bitmap.getWidth(),(float)getHeight()/bitmap.getHeight());
            matrix.setScale(scale,scale); matrix.postTranslate((getWidth()-bitmap.getWidth()*scale)/2f,(getHeight()-bitmap.getHeight()*scale)/2f); image.getShader().setLocalMatrix(matrix);
        }
        @Override protected void onDraw(Canvas canvas) {
            canvas.drawPath(shape,fill);
            boolean ready=bitmap!=null&&!bitmap.isRecycled();
            if(ready) canvas.drawPath(shape,image);
            // 半像素在外、半像素在内的发丝描边：浅色图片不会和浅色气泡糊成一片。
            canvas.drawPath(shape,rim);
            float unit=getResources().getDisplayMetrics().density,cx=getWidth()/2f,cy=getHeight()/2f,radius=Math.min(22*unit,Math.min(getWidth(),getHeight())/2f-4*unit);
            if(radius<=0) return;
            if(progress>=0) {
                canvas.drawCircle(cx,cy,radius,scrim);
                float ring=radius-6*unit; mark.setStyle(Paint.Style.STROKE); mark.setStrokeWidth(2.4f*unit);
                mark.setAlpha(80); canvas.drawCircle(cx,cy,ring,mark);
                mark.setAlpha(255); canvas.drawArc(cx-ring,cy-ring,cx+ring,cy+ring,-90,360f*Math.max(2,Math.min(100,progress))/100f,false,mark);
            } else if(play!=null&&ready) {
                canvas.drawCircle(cx,cy,radius,scrim);
                float half=radius*.4f; glyph.reset(); glyph.moveTo(cx-half*.75f,cy-half); glyph.lineTo(cx+half,cy); glyph.lineTo(cx-half*.75f,cy+half); glyph.close();
                mark.setStyle(Paint.Style.FILL); mark.setAlpha(255); canvas.drawPath(glyph,mark);
            }
            if(play!=null&&!play.isEmpty()&&ready) {
                mark.setStyle(Paint.Style.FILL); mark.setAlpha(255); mark.setTextSize(android.util.TypedValue.applyDimension(android.util.TypedValue.COMPLEX_UNIT_SP,11,getResources().getDisplayMetrics()));
                Paint.FontMetrics metrics=mark.getFontMetrics(); float height=metrics.descent-metrics.ascent+4*unit,width=mark.measureText(play)+14*unit,left=6*unit,bottom=getHeight()-6*unit;
                canvas.drawRoundRect(left,bottom-height,left+width,bottom,height/2f,height/2f,scrim);
                canvas.drawText(play,left+7*unit,bottom-height/2f-(metrics.ascent+metrics.descent)/2f,mark);
            }
        }
    }
    View thumbnail(Uri uri,String name,int width,Runnable actions) { return thumbnail(uri,name,width,()->open(uri,name),actions); }
    View thumbnail(Uri uri,String name,int width,Runnable openFile,Runnable actions) { return thumbnail(uri,name,new Look(width,width*2/3,new float[4],0xffe9edf1,0xff17212b,0),openFile,actions); }
    /** 第 0 个子视图是 {@link Picture}，第 1 个是占位说明文字；调用方可以继续往返回的容器里叠加状态角标。 */
    FrameLayout thumbnail(Uri uri,String name,Look look,Runnable openFile,Runnable actions) {
        FrameLayout frame=new FrameLayout(activity); Picture picture=new Picture(activity,look); frame.addView(picture,new FrameLayout.LayoutParams(-1,-1));
        int pad=Math.round(8*activity.getResources().getDisplayMetrics().density);
        TextView status=new TextView(activity); status.setText("正在生成预览…"); status.setTextSize(12); status.setTextColor(look.ink); status.setGravity(Gravity.CENTER); status.setPadding(pad,pad,pad,pad); frame.addView(status,new FrameLayout.LayoutParams(-1,-1));
        frame.setContentDescription(name+"，媒体预览"); frame.setLayoutParams(new LinearLayout.LayoutParams(look.width,look.height));
        frame.setOnClickListener(v->openFile.run()); frame.setOnLongClickListener(v->{actions.run();return true;});
        try { worker.execute(()->{
            Bitmap result=null; String caption="",length=null;
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
                                if(frameBitmap!=null) {float scale=Math.min(480f/frameBitmap.getWidth(),320f/frameBitmap.getHeight());result=Bitmap.createScaledBitmap(frameBitmap,Math.max(1,Math.round(frameBitmap.getWidth()*scale)),Math.max(1,Math.round(frameBitmap.getHeight()*scale)),true);if(result!=frameBitmap)frameBitmap.recycle();}
                            }
                        }
                        length=duration==null?"":String.format(java.util.Locale.ROOT,"%d:%02d",seconds/60,seconds%60);
                    }
                } else result=image(activity,uri,480);
                if(result==null) caption="预览不可用 · 点击尝试打开";
            } catch(Exception|OutOfMemoryError e) { caption="预览不可用 · 长按文件操作"; }
            Bitmap bitmap=result; String text=caption,time=length;
            // 预览框尺寸在绑定时已经确定，解码完成只换图，不改变气泡高度和阅读位置。
            main.post(()->{if(closed||!frame.isAttachedToWindow()) {if(bitmap!=null)bitmap.recycle();return;} picture.setImageBitmap(bitmap);status.setText(text);if(bitmap!=null&&time!=null)picture.play(time);});
        }); } catch(RejectedExecutionException e) {status.setText("预览队列繁忙 · 点击查看");}
        return frame;
    }
    void open(Uri uri,String name) {
        if(video(name)) {
            LinearLayout layout=new LinearLayout(activity); Dialog dialog=fullscreenViewer("关闭视频",layout);
            VideoView video=new VideoView(activity);layout.addView(video,new LinearLayout.LayoutParams(-1,0,1));
            MediaController controls=new MediaController(activity);controls.setAnchorView(video);video.setMediaController(controls);
            video.setOnErrorListener((player,what,extra)->{Toast.makeText(activity,"格式不支持或文件已失效，请使用文件菜单打开",Toast.LENGTH_LONG).show();dialog.dismiss();return true;});
            dialog.setOnDismissListener(d->video.stopPlayback());dialog.show();SystemBars.apply(dialog.getWindow(),Color.BLACK,true);video.setVideoURI(uri);video.setOnPreparedListener(player->{video.start();controls.show();});return;
        }
        LinearLayout layout=new LinearLayout(activity); Dialog dialog=fullscreenViewer("关闭图片 · 双指缩放 / 拖动",layout);
        ZoomImage image=new ZoomImage(activity);layout.addView(image,new LinearLayout.LayoutParams(-1,0,1));dialog.show();SystemBars.apply(dialog.getWindow(),Color.BLACK,true);
        try {worker.execute(()->{Bitmap bitmap=null;try{bitmap=image(activity,uri,2048);}catch(Exception|OutOfMemoryError ignored){} Bitmap result=bitmap;main.post(()->{if(closed||!dialog.isShowing()){if(result!=null)result.recycle();return;}if(result==null){Toast.makeText(activity,"图片无法预览",Toast.LENGTH_LONG).show();dialog.dismiss();}else{image.setImageBitmap(result);dialog.setOnDismissListener(d->{image.setImageDrawable(null);result.recycle();});}});});}catch(RejectedExecutionException e){dialog.dismiss();Toast.makeText(activity,"预览繁忙，请稍后重试",Toast.LENGTH_SHORT).show();}
    }
    private Dialog fullscreenViewer(String closeLabel,LinearLayout layout) {
        Dialog dialog=new Dialog(activity,android.R.style.Theme_Material_NoActionBar_Fullscreen);
        layout.setOrientation(LinearLayout.VERTICAL);layout.setBackgroundColor(Color.BLACK);
        if(Build.VERSION.SDK_INT>=30){layout.setOnApplyWindowInsetsListener((v,insets)->{Insets b=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());v.setPadding(b.left,b.top,b.right,b.bottom);return insets;});}else layout.setFitsSystemWindows(true);
        Button close=new Button(activity);close.setText(closeLabel);close.setAllCaps(false);close.setTextColor(0xffe9eef3);close.setBackgroundColor(0xff22303d);layout.addView(close);
        dialog.setContentView(layout);close.setOnClickListener(v->dialog.dismiss());SystemBars.apply(dialog.getWindow(),Color.BLACK,true);return dialog;
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
