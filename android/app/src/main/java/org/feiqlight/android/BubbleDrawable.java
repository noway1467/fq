package org.feiqlight.android;

import android.graphics.*;
import android.graphics.drawable.Drawable;

/** 与 Windows 气泡同一轮廓：远离尾巴一侧固定大圆角；尾巴一侧按组内位置用小圆角，组尾画出向外弯的底部尾巴。
 *  尾巴与底部投影都留在自身 bounds 内，避免父容器裁掉；正文另留侧边内距。 */
final class BubbleDrawable extends Drawable {
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG),shade=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path=new Path();
    private final float density;
    private final boolean outgoing,first,last;
    BubbleDrawable(int color,float density,boolean outgoing) { this(color,0,density,outgoing,true,true); }
    BubbleDrawable(int color,int shadow,float density,boolean outgoing,boolean first,boolean last) { paint.setColor(color);shade.setColor(shadow);this.density=density;this.outgoing=outgoing;this.first=first;this.last=last; }
    @Override protected void onBoundsChange(Rect bounds) {
        float t=7*density,big=15*density,small=6*density,w=bounds.width()-t,h=bounds.height()-(Color.alpha(shade.getColor())>0?density:0);
        float radius=Math.min(big,h/2),top=Math.min(first?big:small,h/2);
        path.reset();
        path.arcTo(0,0,radius*2,radius*2,180,90,false);
        path.arcTo(w-top*2,0,w,top*2,270,90,false);
        if(last) {
            float rise=Math.min(h-top,t*2.4f);
            path.lineTo(w,h-rise);path.cubicTo(w,h-rise*.32f,w+t*.42f,h-t*.06f,w+t,h);path.lineTo(w-1,h);
        } else { float bottom=Math.min(small,h/2);path.arcTo(w-bottom*2,h-bottom*2,w,h,0,90,false); }
        path.arcTo(0,h-radius*2,radius*2,h,90,90,false);path.close();
        Matrix matrix=new Matrix();if(!outgoing) {matrix.setScale(-1,1);matrix.postTranslate(bounds.width(),0);}
        matrix.postTranslate(bounds.left,bounds.top);path.transform(matrix);
    }
    @Override public void draw(Canvas canvas) {
        if(Color.alpha(shade.getColor())>0) { canvas.save();canvas.translate(0,density);canvas.drawPath(path,shade);canvas.restore(); }
        canvas.drawPath(path,paint);
    }
    @Override public void setAlpha(int alpha) { paint.setAlpha(alpha);invalidateSelf(); }
    @Override public void setColorFilter(ColorFilter filter) { paint.setColorFilter(filter);shade.setColorFilter(filter);invalidateSelf(); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
}
