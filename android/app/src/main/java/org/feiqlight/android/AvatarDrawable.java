package org.feiqlight.android;

import android.content.Context;
import android.graphics.*;
import android.graphics.drawable.Drawable;

/** 头像外单独留透明边；在线时与 Windows 一致在右下角画绿点，外圈取界面底色与头像分隔。离线不加任何标记，头像主体像素不变。 */
final class AvatarDrawable extends Drawable {
    private final Paint face = new Paint(Paint.ANTI_ALIAS_FLAG), ring = new Paint(Paint.ANTI_ALIAS_FLAG), dot = new Paint(Paint.ANTI_ALIAS_FLAG), emoji = new Paint(Paint.ANTI_ALIAS_FLAG | Paint.FILTER_BITMAP_FLAG);
    private final EmojiAvatars avatars;
    private final int index;
    private final Rect source;
    private final RectF target = new RectF();
    private final float halo, density;
    private boolean online;
    private final String avatar;
    private final Bitmap picture;
    AvatarDrawable(Context context, String identity, boolean online, float halo) { this(context, identity, online, halo, Color.WHITE, 0xff3cb34f); }
    AvatarDrawable(Context context, String identity, boolean online, float halo, int surface, int presence) {
        this(context,identity,online,halo,surface,presence,null);
    }
    AvatarDrawable(Context context, String identity, boolean online, float halo, int surface, int presence, String value) {
        avatar=org.feiqlight.android.core.Protocol.normalizeAvatar(value);
        avatars=EmojiAvatars.get(context);index=avatar!=null && avatar.startsWith("builtin:")?Integer.parseInt(avatar.substring(8)):avatars.index(identity);face.setColor(avatars.color(index));
        Bitmap decoded=null;
        if(avatar!=null && avatar.startsWith("image:")) { byte[] data=java.util.Base64.getDecoder().decode(avatar.substring(6)); decoded=BitmapFactory.decodeByteArray(data,0,data.length); }
        picture=decoded;
        source=new Rect(index%6*80+4,index/6*80+4,index%6*80+76,index/6*80+76);
        ring.setColor(surface);dot.setColor(presence);density=context.getResources().getDisplayMetrics().density;
        this.online=online;this.halo=halo;
    }
    int avatarIndex() { return index; }
    String avatarValue() { return avatar; }
    boolean isOnline() { return online; }
    void setOnline(boolean value) { if(online!=value) { online=value; invalidateSelf(); } }
    @Override protected void onBoundsChange(Rect bounds) {
        float radius=Math.min(bounds.width(),bounds.height())/2f;
        if(radius<=halo) return;
        float half=(radius-halo)*.72f;
        target.set(bounds.exactCenterX()-half,bounds.exactCenterY()-half,bounds.exactCenterX()+half,bounds.exactCenterY()+half);
    }
    @Override public void draw(Canvas canvas) {
        Rect bounds=getBounds(); float radius=Math.min(bounds.width(),bounds.height())/2f, x=bounds.exactCenterX(), y=bounds.exactCenterY();
        if(radius<=halo) return;
        canvas.drawCircle(x,y,radius-halo,face);
        if(picture==null) canvas.drawBitmap(avatars.atlas,source,target,emoji);
        else {
            int saved=canvas.save(); Path circle=new Path();circle.addCircle(x,y,radius-halo,Path.Direction.CW);canvas.clipPath(circle);
            canvas.drawBitmap(picture,null,new RectF(x-radius+halo,y-radius+halo,x+radius-halo,y+radius-halo),emoji);canvas.restoreToCount(saved);
        }
        if(online) {
            // 与 Windows ContactAvatar 同一比例：点径为头像直径的 30%，向右下探出 14%，底色描边约 5.5%。
            float size=(radius-halo)*2, outer=Math.max(9*density,size*.3f), edge=Math.max(1.5f*density,size*.055f);
            float cx=x-(radius-halo)+size-outer*.86f+outer/2, cy=y-(radius-halo)+size-outer*.86f+outer/2;
            canvas.drawCircle(cx,cy,outer/2,ring);canvas.drawCircle(cx,cy,outer/2-edge,dot);
        }
    }
    @Override public void setAlpha(int alpha) { face.setAlpha(alpha);ring.setAlpha(alpha);dot.setAlpha(alpha);emoji.setAlpha(alpha);invalidateSelf(); }
    @Override public void setColorFilter(ColorFilter filter) { face.setColorFilter(filter);ring.setColorFilter(filter);dot.setColorFilter(filter);emoji.setColorFilter(filter);invalidateSelf(); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
}
