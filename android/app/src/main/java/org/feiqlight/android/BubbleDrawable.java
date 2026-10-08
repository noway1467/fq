package org.feiqlight.android;

import android.graphics.*;
import android.graphics.drawable.Drawable;

/** 尾角留在自身 bounds 内，避免父容器裁掉；正文另留侧边内距。 */
final class BubbleDrawable extends Drawable {
    private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path=new Path();
    private final float density;
    private final boolean outgoing;
    BubbleDrawable(int color,float density,boolean outgoing) { paint.setColor(color);this.density=density;this.outgoing=outgoing; }
    @Override protected void onBoundsChange(Rect bounds) {
        float t=7*density,r=14*density,w=bounds.width()-t,h=bounds.height();
        path.reset();path.moveTo(r,0);path.lineTo(w-r,0);path.quadTo(w,0,w,r);
        path.lineTo(w,r+2*density);path.cubicTo(w+t/2,r+2*density,w+t,r-t/2,w+t,r-t/2);
        path.cubicTo(w+t,r+t,w,r+2*t,w,r+2*t);
        path.lineTo(w,h-r);path.quadTo(w,h,w-r,h);path.lineTo(r,h);path.quadTo(0,h,0,h-r);
        path.lineTo(0,r);path.quadTo(0,0,r,0);path.close();
        Matrix matrix=new Matrix();if(!outgoing) {matrix.setScale(-1,1);matrix.postTranslate(bounds.width(),0);}
        matrix.postTranslate(bounds.left,bounds.top);path.transform(matrix);
    }
    @Override public void draw(Canvas canvas) { canvas.drawPath(path,paint); }
    @Override public void setAlpha(int alpha) { paint.setAlpha(alpha);invalidateSelf(); }
    @Override public void setColorFilter(ColorFilter filter) { paint.setColorFilter(filter);invalidateSelf(); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
}
