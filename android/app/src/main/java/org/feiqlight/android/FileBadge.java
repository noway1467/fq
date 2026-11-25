package org.feiqlight.android;

import android.content.Context;
import android.graphics.*;
import android.view.View;
import java.util.Locale;

/** 聊天文件卡片的圆形图标，与 Windows 文件卡片同一套形态：扩展名、待接收的下载箭头、传输中的进度环。
 *  只按传入状态绘制，不读取文件，也不自行查询传输进度。 */
final class FileBadge extends View {
    static final int FILE=0,DOWNLOAD=1,SENDING=2,RECEIVING=3;
    private final Paint fill=new Paint(Paint.ANTI_ALIAS_FLAG),line=new Paint(Paint.ANTI_ALIAS_FLAG),label=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path=new Path();
    private final String extension;
    private int mode=-1,progress,color;
    FileBadge(Context context,String name) {
        super(context);extension=extension(name);
        line.setStyle(Paint.Style.STROKE);line.setStrokeCap(Paint.Cap.ROUND);line.setStrokeJoin(Paint.Join.ROUND);line.setColor(Color.WHITE);
        label.setColor(Color.WHITE);label.setTypeface(Typeface.DEFAULT_BOLD);label.setTextAlign(Paint.Align.CENTER);
    }
    /** 最后一个点之后的 1–4 个字母或数字，转成大写；没有扩展名、过长或含其他字符时返回空串，改画通用文件图标。 */
    static String extension(String name) {
        int dot=name==null?-1:name.lastIndexOf('.');
        if(dot<0||dot==name.length()-1||name.length()-dot-1>4)return "";
        String value=name.substring(dot+1);
        for(int i=0;i<value.length();i++)if(!Character.isLetterOrDigit(value.charAt(i)))return "";
        return value.toUpperCase(Locale.ROOT);
    }
    /** 形态、进度或底色没有变化时不重绘，传输进度刷新不会带动整个气泡。 */
    void bind(int mode,int progress,int color) {
        if(mode==this.mode&&progress==this.progress&&color==this.color)return;
        this.mode=mode;this.progress=progress;this.color=color;invalidate();
    }
    int getProgress() {return progress;}
    int mode() {return mode;}
    String extension() {return extension;}
    @Override protected void onDraw(Canvas canvas) {
        float size=Math.min(getWidth(),getHeight()),cx=getWidth()/2f,cy=getHeight()/2f,unit=size/44f;
        if(size<=0)return;
        fill.setColor(color);canvas.drawCircle(cx,cy,size/2f,fill);
        if(mode==SENDING||mode==RECEIVING) {
            float radius=size/2f-5*unit;line.setStrokeWidth(2.2f*unit);
            line.setAlpha(70);canvas.drawCircle(cx,cy,radius,line);
            line.setAlpha(255);canvas.drawArc(cx-radius,cy-radius,cx+radius,cy+radius,-90,360f*Math.max(2,Math.min(100,progress))/100f,false,line);
            glyph(canvas,cx,cy,16*unit,mode==SENDING?"up":"close");
        } else if(mode==DOWNLOAD)glyph(canvas,cx,cy,22*unit,"download");
        else if(!extension.isEmpty()) {
            label.setTextSize((extension.length()>3?10f:11.5f)*unit);Paint.FontMetrics metrics=label.getFontMetrics();
            canvas.drawText(extension,cx,cy-(metrics.ascent+metrics.descent)/2f,label);
        } else glyph(canvas,cx,cy,20*unit,"file");
    }
    // 24 单位网格、1.8 线宽，坐标取自 Windows Theme.Glyph 的 arrow-up / close / download / file。
    private void glyph(Canvas canvas,float cx,float cy,float box,String kind) {
        canvas.save();canvas.translate(cx-box/2f,cy-box/2f);canvas.scale(box/24f,box/24f);line.setAlpha(255);line.setStrokeWidth(1.8f);path.reset();
        switch(kind) {
            case "up":path.moveTo(12,19);path.lineTo(12,5.5f);path.moveTo(6.5f,11);path.lineTo(12,5.5f);path.lineTo(17.5f,11);break;
            case "close":path.moveTo(6.5f,6.5f);path.lineTo(17.5f,17.5f);path.moveTo(17.5f,6.5f);path.lineTo(6.5f,17.5f);break;
            case "download":path.moveTo(12,4);path.lineTo(12,14.5f);path.moveTo(8,10.5f);path.lineTo(12,14.5f);path.lineTo(16,10.5f);path.moveTo(4,14.5f);path.lineTo(4,18.5f);path.lineTo(5.5f,20);path.lineTo(18.5f,20);path.lineTo(20,18.5f);path.lineTo(20,14.5f);break;
            default:path.moveTo(13.5f,3);path.lineTo(7,3);path.lineTo(5.5f,4.5f);path.lineTo(5.5f,19.5f);path.lineTo(7,21);path.lineTo(17,21);path.lineTo(18.5f,19.5f);path.lineTo(18.5f,8);path.lineTo(13.5f,3);path.lineTo(13.5f,8);path.lineTo(18.5f,8);
        }
        canvas.drawPath(path,line);canvas.restore();
    }
}
