package org.feiqlight.android;

import android.graphics.*;
import android.graphics.drawable.Drawable;

/** 头像外单独留透明空间，让蓝色光晕向外渐隐；不描边、不改变头像内部颜色。 */
final class AvatarDrawable extends Drawable {
    private final Paint face = new Paint(Paint.ANTI_ALIAS_FLAG), glow = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final float halo;
    private boolean online;
    AvatarDrawable(int color, boolean online, float halo) { face.setColor(color); this.online=online; this.halo=halo; }
    boolean isOnline() { return online; }
    void setOnline(boolean value) { if(online!=value) { online=value; invalidateSelf(); } }
    @Override protected void onBoundsChange(Rect bounds) {
        float radius=Math.min(bounds.width(),bounds.height())/2f;
        if(radius<=halo) return;
        float edge=(radius-halo)/radius;
        glow.setShader(new RadialGradient(bounds.exactCenterX(),bounds.exactCenterY(),radius,
            new int[]{0x7d3390ec,0x7d3390ec,0x323390ec,0x003390ec},new float[]{0,edge,edge+(1-edge)*.45f,1},Shader.TileMode.CLAMP));
    }
    @Override public void draw(Canvas canvas) {
        Rect bounds=getBounds(); float radius=Math.min(bounds.width(),bounds.height())/2f, x=bounds.exactCenterX(), y=bounds.exactCenterY();
        if(radius<=halo) return;
        if(online) canvas.drawCircle(x,y,radius,glow);
        canvas.drawCircle(x,y,radius-halo,face);
    }
    @Override public void setAlpha(int alpha) { face.setAlpha(alpha);glow.setAlpha(alpha);invalidateSelf(); }
    @Override public void setColorFilter(ColorFilter filter) { face.setColorFilter(filter);glow.setColorFilter(filter);invalidateSelf(); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
}
