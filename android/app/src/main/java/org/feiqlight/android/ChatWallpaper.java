package org.feiqlight.android;

import android.content.Context;
import android.graphics.*;
import android.graphics.drawable.Drawable;
import android.net.Uri;
import java.io.*;
import java.util.Random;

/** 背景是静态装饰，不跟随消息滚动；画法与 Windows ChatWallpaper 一致：四色自由渐变 + 线稿涂鸦（星环）或波纹（流线）。抽样解码避免大图片挤占聊天内存。 */
final class ChatWallpaper extends Drawable {
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG|Paint.FILTER_BITMAP_FLAG);
    private final int[] colors;private final int wall,accent;private final boolean dark;private final float density;private final String pattern;
    private Bitmap image,gradient,tile;
    private Path[] waves=new Path[0];private int[] waveColors=new int[0];private float[] waveWidths=new float[0];
    ChatWallpaper(Context context,int[] colors,int wall,int accent,boolean dark,String pattern,Bitmap image) {this.colors=colors;this.wall=wall;this.accent=accent;this.dark=dark;this.pattern=pattern;this.image=image;density=context.getResources().getDisplayMetrics().density;}
    static Bitmap load(Context context,Uri uri) throws IOException {
        BitmapFactory.Options options=new BitmapFactory.Options();options.inJustDecodeBounds=true;
        try(InputStream input=context.getContentResolver().openInputStream(uri)){BitmapFactory.decodeStream(input,null,options);}
        if(options.outWidth<=0||options.outHeight<=0||(long)options.outWidth*options.outHeight>40000000)throw new IOException("请选择有效且不超过 4000 万像素的图片");
        options.inJustDecodeBounds=false;options.inSampleSize=1;
        while(Math.max(options.outWidth,options.outHeight)/options.inSampleSize>1920)options.inSampleSize*=2;
        try(InputStream input=context.getContentResolver().openInputStream(uri)){Bitmap bitmap=BitmapFactory.decodeStream(input,null,options);if(bitmap==null)throw new IOException("无法读取背景图片");return bitmap;}
    }
    // 渐变取决于宽高比、波纹取决于尺寸：只在尺寸变化时重算，绘制时直接复用。
    @Override protected void onBoundsChange(Rect bounds) {
        if(gradient!=null){gradient.recycle();gradient=null;}
        if(bounds.isEmpty()||pattern.equals("none")||pattern.equals("image"))return;
        gradient=gradient(bounds.width(),bounds.height());
        if(pattern.equals("lines"))buildWaves(bounds);
    }
    @Override public void draw(Canvas canvas) {
        Rect area=getBounds();canvas.drawColor(wall);
        if(image!=null&&!image.isRecycled()&&pattern.equals("image")) {
            float scale=Math.max(area.width()/(float)image.getWidth(),area.height()/(float)image.getHeight());float w=image.getWidth()*scale,h=image.getHeight()*scale;
            paint.setColor(Color.WHITE);paint.setAlpha(255);canvas.drawBitmap(image,null,new RectF((area.width()-w)/2,(area.height()-h)/2,(area.width()+w)/2,(area.height()+h)/2),paint);
            canvas.drawColor((wall&0x00ffffff)|((dark?90:55)<<24));return;
        }
        if(pattern.equals("none")||gradient==null)return;
        canvas.drawBitmap(gradient,null,area,paint);
        if(pattern.equals("lines")) {
            Paint line=new Paint(Paint.ANTI_ALIAS_FLAG);line.setStyle(Paint.Style.STROKE);
            for(int i=0;i<waves.length;i++){line.setColor(waveColors[i]);line.setStrokeWidth(waveWidths[i]);canvas.drawPath(waves[i],line);}
            return;
        }
        if(tile==null)tile=doodles();
        paint.setShader(new BitmapShader(tile,Shader.TileMode.REPEAT,Shader.TileMode.REPEAT));canvas.drawRect(area,paint);paint.setShader(null);
    }
    // 涂鸦块只取决于密度与配色：各会话页共用一份，切换会话不重复分配位图。
    private static Bitmap sharedTile;private static String sharedKey="";
    // 四色自由渐变：低分辨率逐点按距离混色后平滑放大，与窗口尺寸无关。
    private static final float[][] ANCHORS={{.80f,.10f},{.30f,.28f},{.18f,.92f},{.70f,.74f}};
    private Bitmap gradient(int width,int height) {
        int w=40,h=52;Bitmap small=Bitmap.createBitmap(w,h,Bitmap.Config.ARGB_8888);float aspect=width/(float)Math.max(1,height);
        for(int y=0;y<h;y++)for(int x=0;x<w;x++) {
            float u=(x+.5f)/w,v=(y+.5f)/h;double r=0,g=0,b=0,total=0;
            for(int i=0;i<4;i++) {
                double dx=(u-ANCHORS[i][0])*aspect,dy=v-ANCHORS[i][1],weight=Math.pow(Math.max(0,1.05-Math.sqrt(dx*dx+dy*dy)),3.2)+1e-6;
                r+=Color.red(colors[i])*weight;g+=Color.green(colors[i])*weight;b+=Color.blue(colors[i])*weight;total+=weight;
            }
            small.setPixel(x,y,Color.rgb((int)(r/total),(int)(g/total),(int)(b/total)));
        }
        return small;
    }
    private int ink(int alpha) { return dark?Color.argb(alpha*2/3,255,255,255):(alpha<<24)|(blend(Color.BLACK,accent,.55f)&0xffffff); }
    // 涂鸦：固定种子的抖动网格平铺，聊天、文件、纸飞机等局域网主题线稿，每块可无缝拼接。
    private static final String[] SHAPES={"chat","send","heart","file","smile","star","clip","cloud","image","music","bell","coffee","folder","sparkle","pin","monitor"};
    private Bitmap doodles() {
        String key=density+"|"+ink(30);
        synchronized(ChatWallpaper.class){if(sharedTile!=null&&key.equals(sharedKey))return sharedTile;}
        int cell=Math.round(78*density),columns=4,rows=4,size=cell*columns;
        Bitmap result=Bitmap.createBitmap(size,size,Bitmap.Config.ARGB_8888);Canvas canvas=new Canvas(result);Random random=new Random(20261027);
        Paint stroke=new Paint(Paint.ANTI_ALIAS_FLAG);stroke.setStyle(Paint.Style.STROKE);stroke.setStrokeWidth(1.8f);stroke.setStrokeCap(Paint.Cap.ROUND);stroke.setStrokeJoin(Paint.Join.ROUND);stroke.setColor(ink(30));
        Paint fill=new Paint(Paint.ANTI_ALIAS_FLAG);fill.setColor(ink(30));Paint dot=new Paint(Paint.ANTI_ALIAS_FLAG);dot.setColor(ink(24));
        for(int row=0;row<rows;row++)for(int column=0;column<columns;column++) {
            int index=(row*columns+column+row*3)%SHAPES.length;float s=(float)((26+random.nextDouble()*10)*density);
            float x=column*cell+(cell-s)/2f+(float)((random.nextDouble()-.5)*cell*.34),y=row*cell+(cell-s)/2f+(float)((random.nextDouble()-.5)*cell*.34);
            if(row%2==1)x+=cell/2f;if(x+s>size)x-=size;
            float angle=(float)((random.nextDouble()-.5)*50);
            for(int wrap=0;wrap<2;wrap++) {
                float left=wrap==0?x:x+size;if(wrap==1&&x>=0)break;
                canvas.save();canvas.translate(left+s/2,y+s/2);canvas.rotate(angle);canvas.translate(-s/2,-s/2);canvas.scale(s/24f,s/24f);glyph(canvas,SHAPES[index],stroke,fill);canvas.restore();
            }
            // 小点缀填补空隙，避免图案呈规则方阵。
            float d=3.2f*density,dx=column*cell+(float)(random.nextDouble()*cell*.8),dy=row*cell+(float)(random.nextDouble()*cell*.8);canvas.drawOval(dx,dy,dx+d,dy+d,dot);
        }
        // 旧块可能仍被其他已建页面的绘制引用，只替换引用，交给 GC 回收，不主动 recycle。
        synchronized(ChatWallpaper.class){sharedTile=result;sharedKey=key;}
        return result;
    }
    // 24 单位网格线稿，几何与 Windows Theme.Glyph 同名图标一致。
    private static void glyph(Canvas c,String name,Paint p,Paint fill) {
        Path path=new Path();
        switch(name) {
            case "chat": path.arcTo(3,4,9,10,180,90,false);path.arcTo(15,4,21,10,270,90,false);path.arcTo(15,11.5f,21,17.5f,0,90,false);path.lineTo(10,17.5f);path.lineTo(6,21);path.lineTo(6,17.4f);path.arcTo(3,11.5f,9,17.5f,90,90,false);path.close();c.drawPath(path,p);break;
            case "send": path.moveTo(3.2f,11.4f);path.lineTo(20.8f,3.6f);path.lineTo(14,20.8f);path.lineTo(11.1f,13.6f);path.close();c.drawPath(path,fill);c.drawPath(path,p);break;
            case "heart": path.moveTo(12,20);path.cubicTo(5,15.5f,3,12,3,8.8f);path.cubicTo(3,6,5.2f,4,7.8f,4);path.cubicTo(9.6f,4,11.2f,5.1f,12,6.8f);path.cubicTo(12.8f,5.1f,14.4f,4,16.2f,4);path.cubicTo(18.8f,4,21,6,21,8.8f);path.cubicTo(21,12,19,15.5f,12,20);c.drawPath(path,p);break;
            case "file": lines(path,13.5f,3,7,3,5.5f,4.5f,5.5f,19.5f,7,21,17,21,18.5f,19.5f,18.5f,8,13.5f,3,13.5f,8,18.5f,8);c.drawPath(path,p);break;
            case "smile": c.drawCircle(12,12,9,p);c.drawOval(8.1f,8.6f,10.3f,11.2f,fill);c.drawOval(13.7f,8.6f,15.9f,11.2f,fill);c.drawArc(7.6f,8.4f,16.4f,16.6f,25,130,false,p);break;
            case "star": for(int k=0;k<10;k++){double a=Math.toRadians(k*36-90),r=k%2==0?9.5:4.2;float x=(float)(12+Math.cos(a)*r),y=(float)(12+Math.sin(a)*r);if(k==0)path.moveTo(x,y);else path.lineTo(x,y);}path.close();c.drawPath(path,p);break;
            case "clip": c.save();c.rotate(45,12,12);path.moveTo(15,8);path.lineTo(15,16.5f);path.arcTo(9,13.5f,15,19.5f,0,180,false);path.lineTo(9,6.5f);path.arcTo(9,4.5f,13,8.5f,180,180,false);path.lineTo(13,15.5f);c.drawPath(path,p);c.restore();break;
            case "cloud": path.arcTo(3,10.5f,10,17.5f,90,180,false);path.arcTo(6.5f,5,15.5f,14,190,150,false);path.arcTo(13,8.5f,21,16.5f,260,190,false);path.close();c.drawPath(path,p);break;
            case "image": c.drawRoundRect(3.5f,4.5f,20.5f,19.5f,2.5f,2.5f,p);c.drawOval(7,8,10.2f,11.2f,p);lines(path,4.5f,17.5f,9.5f,12.8f,12.8f,15.6f,15.3f,13.2f,19.6f,17);c.drawPath(path,p);break;
            case "music": c.drawOval(4.5f,15,9.5f,19.2f,p);c.drawOval(14.5f,13,19.5f,17.2f,p);lines(path,9.5f,17,9.5f,5.5f,19.5f,3.5f,19.5f,15);c.drawPath(path,p);c.drawLine(9.5f,9,19.5f,7,p);break;
            case "bell": path.moveTo(5.5f,17);path.cubicTo(7,15,6.5f,13,6.5f,10.5f);path.cubicTo(6.5f,7,9,4.5f,12,4.5f);path.cubicTo(15,4.5f,17.5f,7,17.5f,10.5f);path.cubicTo(17.5f,13,17,15,18.5f,17);path.close();c.drawPath(path,p);c.drawArc(10,17.5f,14,21,0,180,false,p);break;
            case "coffee": lines(path,4.5f,9,5.5f,17.5f,7.5f,19.5f,13,19.5f,15,17.5f,16,9,4.5f,9);c.drawPath(path,p);c.drawArc(14.5f,10.5f,20,15.5f,-90,180,false,p);path.reset();path.moveTo(8,3.5f);path.cubicTo(7,5,9,5.5f,8,7);path.moveTo(12,3.5f);path.cubicTo(11,5,13,5.5f,12,7);c.drawPath(path,p);break;
            case "folder": lines(path,3.5f,18,3.5f,6.5f,5,5,9.2f,5,11.2f,7.5f,19,7.5f,20.5f,9,20.5f,18,19,19.5f,5,19.5f);path.close();c.drawPath(path,p);break;
            case "sparkle": path.moveTo(12,3);path.cubicTo(12.8f,9,15,11.2f,21,12);path.cubicTo(15,12.8f,12.8f,15,12,21);path.cubicTo(11.2f,15,9,12.8f,3,12);path.cubicTo(9,11.2f,11.2f,9,12,3);c.drawPath(path,p);break;
            case "pin": lines(path,9,3.5f,15,3.5f,14.2f,9.6f,17.5f,13.6f,6.5f,13.6f,9.8f,9.6f,9,3.5f);c.drawPath(path,p);c.drawLine(12,13.6f,12,20.5f,p);break;
            default: c.drawRoundRect(3,4,21,16.5f,2,2,p);c.drawLine(12,16.5f,12,20,p);c.drawLine(8,20,16,20,p);break;
        }
    }
    private static void lines(Path path,float... points) { path.moveTo(points[0],points[1]);for(int i=2;i+1<points.length;i+=2)path.lineTo(points[i],points[i+1]); }
    private void buildWaves(Rect area) {
        int gap=Math.max(18,(int)(34*density)),count=0;for(int band=-2;band*gap<area.height()+gap*2;band++)count++;
        waves=new Path[count];waveColors=new int[count];waveWidths=new float[count];
        for(int band=-2,line=0;line<count;band++,line++) {
            waveColors[line]=ink(line%3==0?34:20);waveWidths[line]=Math.max(1f,(line%3==0?1.6f:1.1f)*density);
            Path path=new Path();double phase=band*.55,amplitude=(10+(line%4)*3)*density,length=(260+(line%3)*60)*density;
            for(float x=-20;x<=area.width()+20;x+=6*density){float y=(float)(band*gap+Math.sin(x/length*Math.PI*2+phase)*amplitude+Math.sin(x/(length*.43)+phase*1.7)*amplitude*.35);if(x==-20)path.moveTo(x,y);else path.lineTo(x,y);}
            waves[line]=path;
        }
    }
    private static int blend(int a,int b,float t){return Color.rgb((int)(Color.red(a)*t+Color.red(b)*(1-t)),(int)(Color.green(a)*t+Color.green(b)*(1-t)),(int)(Color.blue(a)*t+Color.blue(b)*(1-t)));}
    @Override public void setAlpha(int alpha) { }
    @Override public void setColorFilter(ColorFilter filter) { }
    @Override public int getOpacity(){return PixelFormat.OPAQUE;}
}
