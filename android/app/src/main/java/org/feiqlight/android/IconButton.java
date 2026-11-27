package org.feiqlight.android;

import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.view.View;

final class IconButton extends View {
    private final String kind; private final Paint p=new Paint(3); private final Path path=new Path();
    private final MainActivity host;
    IconButton(MainActivity host,String kind,String description,int color) {
        super(host);
        this.host=host;
        this.kind=kind;
        p.setColor(color);
        p.setStyle(Paint.Style.STROKE);
        p.setStrokeWidth(1.8f);
        p.setStrokeCap(Paint.Cap.ROUND);
        p.setStrokeJoin(Paint.Join.ROUND);
        setContentDescription(description);
        setFocusable(true);
        setClickable(true);
        android.util.TypedValue value=new android.util.TypedValue();
        host.getTheme().resolveAttribute(android.R.attr.selectableItemBackgroundBorderless,value,true);
        setBackgroundResource(value.resourceId);
    }
    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        canvas.save();
        canvas.translate(getWidth()/2f-host.dp(12),getHeight()/2f-host.dp(12));
        canvas.scale(host.dp(24)/24f,host.dp(24)/24f);
        path.reset();
        if(kind.equals("menu")) { for(int y=6;y<=18;y+=6) canvas.drawLine(3,y,21,y,p); }
        else if(kind.equals("more")) { for(int y=5;y<=19;y+=7) canvas.drawCircle(12,y,1,p); }
        else if(kind.equals("search")) { canvas.drawCircle(10,10,6,p);canvas.drawLine(14.5f,14.5f,21,21,p); }
        else if(kind.equals("close")) { canvas.drawLine(5,5,19,19,p); canvas.drawLine(19,5,5,19,p); }
        else if(kind.equals("add")) { canvas.drawLine(12,3,12,21,p); canvas.drawLine(3,12,21,12,p); }
        else if(kind.equals("back")) {
            path.moveTo(12,4);
            path.lineTo(4,12);
            path.lineTo(12,20);
            canvas.drawPath(path,p);
            canvas.drawLine(4,12,21,12,p);
        }
        else if(kind.equals("transfer")) {
            canvas.drawLine(4,7,20,7,p);path.moveTo(16,3);path.lineTo(20,7);path.lineTo(16,11);
            canvas.drawLine(20,17,4,17,p);
            path.moveTo(8,13);
            path.lineTo(4,17);
            path.lineTo(8,21);
            canvas.drawPath(path,p);
        }
        else if(kind.equals("send")) {
            path.moveTo(3,3);
            path.lineTo(22,12);
            path.lineTo(3,21);
            path.lineTo(6,12);
            path.close();
            canvas.drawPath(path,p);
            canvas.drawLine(6,12,21,12,p);
        }
        else if(kind.equals("smile")) {
            canvas.drawCircle(12,12,9,p);
            canvas.drawPoint(9,9,p);
            canvas.drawPoint(15,9,p);
            canvas.drawArc(7,9,17,17,15,150,false,p);
        }
        else if(kind.equals("code")) {
            path.moveTo(7,6);
            path.lineTo(2,12);
            path.lineTo(7,18);
            path.moveTo(17,6);
            path.lineTo(22,12);
            path.lineTo(17,18);
            canvas.drawPath(path,p);
            canvas.drawLine(14,4,10,20,p);
        }
        else {
            path.moveTo(17,8);
            path.lineTo(8,17);
            path.cubicTo(4,21,0,16,4,12);
            path.lineTo(13,3);
            path.cubicTo(19,-3,27,5,21,11);
            path.lineTo(11,21);
            canvas.drawPath(path,p);
        }
        canvas.restore();
    }
    @Override public boolean performClick() { super.performClick(); return true; }
}
