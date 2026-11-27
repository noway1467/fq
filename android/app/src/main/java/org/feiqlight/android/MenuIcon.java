package org.feiqlight.android;

import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Rect;

final class MenuIcon extends android.graphics.drawable.Drawable {
    private final String label; private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final MainActivity host;
    MenuIcon(MainActivity host,String label,int color) {
        this.host=host;
        this.label=label;
        paint.setColor(color);
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(1.7f);
        paint.setStrokeCap(Paint.Cap.ROUND);
        paint.setStrokeJoin(Paint.Join.ROUND);
    }
    @Override public int getIntrinsicWidth() { return host.dp(22); }
    @Override public int getIntrinsicHeight() { return host.dp(22); }
    @Override public void setAlpha(int alpha) { paint.setAlpha(alpha); }
    @Override public void setColorFilter(ColorFilter filter) { paint.setColorFilter(filter); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
    @Override public void draw(Canvas c) {
        c.save();
        Rect b=getBounds();
        c.translate(b.left,b.top);
        c.scale(b.width()/24f,b.height()/24f);
        Path p=new Path();
        if(label.contains("传输任务")) {
            c.drawLine(4,7,20,7,paint);
            p.moveTo(16,3);
            p.lineTo(20,7);
            p.lineTo(16,11);
            c.drawLine(20,17,4,17,paint);
            p.moveTo(8,13);
            p.lineTo(4,17);
            p.lineTo(8,21);
            c.drawPath(p,paint);
        }
        else if(label.contains("临时浏览器")) {
            c.drawCircle(12,12,9,paint);
            c.drawLine(3,12,21,12,paint);
            c.drawOval(7.5f,3,16.5f,21,paint);
        }
        else if(label.startsWith("复制")) {
            c.drawRoundRect(8,8,20,21,2,2,paint);
            p.moveTo(5,16);
            p.lineTo(3,16);
            p.lineTo(3,3);
            p.lineTo(15,3);
            p.lineTo(15,5);
            c.drawPath(p,paint);
        }
        else if(label.contains("转发")||label.contains("分享")) {
            p.moveTo(3,4);
            p.lineTo(21,12);
            p.lineTo(3,20);
            p.lineTo(6,12);
            p.close();
            c.drawPath(p,paint);
            c.drawLine(6,12,21,12,paint);
        }
        else if(label.contains("目录")||label.contains("位置")||label.contains("共享")||label.contains("文件夹")) {
            p.moveTo(3,6);
            p.lineTo(9,6);
            p.lineTo(11,9);
            p.lineTo(21,9);
            p.lineTo(21,20);
            p.lineTo(3,20);
            p.close();
            c.drawPath(p,paint);
        }
        else if(label.contains("Windows")){
            c.drawRoundRect(3,4,21,17,2,2,paint);
            c.drawLine(12,17,12,21,paint);
            c.drawLine(8,21,16,21,paint);
        }
        else if(label.contains("刷新")||label.contains("重试")) {
            c.drawArc(4,4,20,20,40,285,false,paint);
            p.moveTo(15,3);
            p.lineTo(20,4);
            p.lineTo(19,9);
            c.drawPath(p,paint);
        }
        else if(label.contains("备注")){
            p.moveTo(4,17);
            p.lineTo(16,5);
            p.lineTo(20,9);
            p.lineTo(8,21);
            p.lineTo(3,22);
            p.close();
            c.drawPath(p,paint);
        }
        else if(label.contains("置顶")){
            p.moveTo(9,3.5f);
            p.lineTo(15,3.5f);
            p.lineTo(14.2f,9.6f);
            p.lineTo(17.5f,13.6f);
            p.lineTo(6.5f,13.6f);
            p.lineTo(9.8f,9.6f);
            p.close();
            c.drawPath(p,paint);
            c.drawLine(12,13.6f,12,20.5f,paint);
        }
        else if(label.contains("设置")) {
            c.drawCircle(12,12,7,paint);
            c.drawCircle(12,12,3,paint);
            for(int i=0;i<8;i++){
                double a=i*Math.PI/4;
                c.drawLine(12+(float)Math.cos(a)*8,12+(float)Math.sin(a)*8,12+(float)Math.cos(a)*10,
                        12+(float)Math.sin(a)*10,paint);
            }
        }
        else if(label.contains("添加")) { c.drawLine(12,4,12,20,paint);c.drawLine(4,12,20,12,paint); }
        else if(label.contains("连接")||label.equals("断开")) {
            c.drawArc(4,4,20,20,-50,280,false,paint);
            c.drawLine(12,2,12,12,paint);
        }
        else if(label.equals("取消发送")||label.startsWith("拒绝")||label.startsWith("删除")) {
            c.drawLine(5,5,19,19,paint);
            c.drawLine(5,19,19,5,paint);
        }
        else if(label.equals("关于")) {
            c.drawCircle(12,12,9,paint);
            c.drawLine(12,11,12,17,paint);
            c.drawPoint(12,7,paint);
        }
        else { c.drawRoundRect(5,3,19,21,2,2,paint);c.drawLine(9,11,15,11,paint);c.drawLine(9,16,15,16,paint); }
        c.restore();
    }
}
