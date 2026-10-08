package org.feiqlight.android;

import android.content.Context;
import android.graphics.*;
import android.graphics.drawable.Drawable;
import android.net.Uri;
import java.io.*;

/** 背景是静态装饰，不跟随消息滚动；抽样解码避免大图片挤占聊天内存。 */
final class ChatWallpaper extends Drawable {
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG|Paint.FILTER_BITMAP_FLAG);
    private final int wall,accent;private final float density;private final String pattern;
    private Bitmap image;
    ChatWallpaper(Context context,int wall,int accent,String pattern,Bitmap image) {this.wall=wall;this.accent=accent;this.pattern=pattern;this.image=image;density=context.getResources().getDisplayMetrics().density;}
    static Bitmap load(Context context,Uri uri) throws IOException {
        BitmapFactory.Options options=new BitmapFactory.Options();options.inJustDecodeBounds=true;
        try(InputStream input=context.getContentResolver().openInputStream(uri)){BitmapFactory.decodeStream(input,null,options);}
        if(options.outWidth<=0||options.outHeight<=0||(long)options.outWidth*options.outHeight>40000000)throw new IOException("请选择有效且不超过 4000 万像素的图片");
        options.inJustDecodeBounds=false;options.inSampleSize=1;
        while(Math.max(options.outWidth,options.outHeight)/options.inSampleSize>1920)options.inSampleSize*=2;
        try(InputStream input=context.getContentResolver().openInputStream(uri)){Bitmap bitmap=BitmapFactory.decodeStream(input,null,options);if(bitmap==null)throw new IOException("无法读取背景图片");return bitmap;}
    }
    @Override public void draw(Canvas canvas) {
        Rect area=getBounds();canvas.drawColor(wall);
        if(image!=null&&!image.isRecycled()&&pattern.equals("image")) {
            float scale=Math.max(area.width()/(float)image.getWidth(),area.height()/(float)image.getHeight());float w=image.getWidth()*scale,h=image.getHeight()*scale;
            paint.setColor(Color.WHITE);paint.setAlpha(255);canvas.drawBitmap(image,null,new RectF((area.width()-w)/2,(area.height()-h)/2,(area.width()+w)/2,(area.height()+h)/2),paint);
            canvas.drawColor((wall&0x00ffffff)|0x41000000);return;
        }
        if(pattern.equals("none"))return;
        paint.setShader(new LinearGradient(0,0,area.width(),area.height(),wall,blend(accent,wall,.1f),Shader.TileMode.CLAMP));canvas.drawRect(area,paint);paint.setShader(null);
        paint.setColor(blend(accent,wall,.14f));paint.setStrokeWidth(density);paint.setStyle(Paint.Style.STROKE);
        float step=96*density,r=18*density;
        for(float y=-step;y<area.height()+step;y+=step)for(float x=-step;x<area.width()+step;x+=step){float xx=x+(((int)(y/step))%2)*step/2;
            if(pattern.equals("lines")){canvas.drawArc(xx,y,xx+step,y+step,10,120,false,paint);canvas.drawArc(xx+step/2,y+step/2,xx+step*1.5f,y+step*1.5f,190,120,false,paint);}
            else {canvas.drawCircle(xx+r/2,y+r/2,r/2,paint);canvas.drawLine(xx+2*r,y+2*r,xx+2*r+7*density,y+2*r,paint);canvas.drawLine(xx+2*r,y+2*r,xx+2*r,y+2*r+7*density,paint);}
        }
        paint.setStyle(Paint.Style.FILL);
    }
    private static int blend(int a,int b,float t){return Color.rgb((int)(Color.red(a)*t+Color.red(b)*(1-t)),(int)(Color.green(a)*t+Color.green(b)*(1-t)),(int)(Color.blue(a)*t+Color.blue(b)*(1-t)));}
    @Override public void setAlpha(int alpha) { }
    @Override public void setColorFilter(ColorFilter filter) { }
    @Override public int getOpacity(){return PixelFormat.OPAQUE;}
}
