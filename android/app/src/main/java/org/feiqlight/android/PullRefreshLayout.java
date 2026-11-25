package org.feiqlight.android;

import android.animation.Animator;
import android.animation.AnimatorListenerAdapter;
import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.*;
import android.view.MotionEvent;
import android.view.ViewConfiguration;
import android.widget.FrameLayout;
import java.util.function.BooleanSupplier;

/** 用系统 View 手写的下拉刷新容器（项目不引入 AndroidX）。只有手势从内容顶部开始、并明显向下拖动时才接管，
 *  点击、长按与列表滚动照常交给子视图；内容不位移，只在顶部落下一个指示圈。 */
final class PullRefreshLayout extends FrameLayout {
    private final Paint disc=new Paint(Paint.ANTI_ALIAS_FLAG),rim=new Paint(Paint.ANTI_ALIAS_FLAG),shade=new Paint(Paint.ANTI_ALIAS_FLAG),arc=new Paint(Paint.ANTI_ALIAS_FLAG),head=new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path tip=new Path();
    private final float density;
    private final int slop,trigger,size;
    private BooleanSupplier atTop=()->true,refresh=()->false;
    private float downX,downY,pull,offset,spin;
    private boolean armed,dragging,refreshing,tap;
    private ValueAnimator animator;
    PullRefreshLayout(Context context,int surface,int line,int accent) {
        super(context);density=getResources().getDisplayMetrics().density;
        slop=ViewConfiguration.get(context).getScaledTouchSlop();trigger=Math.round(64*density);size=Math.round(40*density);
        disc.setColor(surface);shade.setColor(Color.BLACK);
        rim.setStyle(Paint.Style.STROKE);rim.setStrokeWidth(Math.max(1,density*.75f));rim.setColor(line);
        arc.setStyle(Paint.Style.STROKE);arc.setStrokeCap(Paint.Cap.ROUND);arc.setStrokeWidth(2.4f*density);arc.setColor(accent);head.setColor(accent);
    }
    /** 内容是否已停在顶部；列表为空或被空态替换时也算在顶部。 */
    void setTarget(BooleanSupplier atTop) {this.atTop=atTop;}
    /** 松手且拉过阈值时调用；返回 true 表示确实开始刷新，指示圈会转动片刻，否则立即收回。 */
    void setOnRefresh(BooleanSupplier refresh) {this.refresh=refresh;}
    boolean refreshing() {return refreshing;}
    int triggerDistance() {return trigger;}
    @Override public boolean onInterceptTouchEvent(MotionEvent event) {return track(event);}
    // 没有子视图接手时（例如空态文字）由容器自己接住手势，空列表同样可以下拉。
    @Override public boolean onTouchEvent(MotionEvent event) {
        track(event);
        if(event.getActionMasked()==MotionEvent.ACTION_UP&&tap)performClick();
        return true;
    }
    @Override public boolean performClick(){super.performClick();return true;}
    // 列表一开始滚动就会要求父容器停止拦截；这里不采纳，否则斜着起手的下拉永远触发不了刷新，只把请求转给上层。
    @Override public void requestDisallowInterceptTouchEvent(boolean disallow) {if(getParent()!=null)getParent().requestDisallowInterceptTouchEvent(disallow);}
    private boolean track(MotionEvent event) {
        switch(event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                downX=event.getX();downY=event.getY();dragging=false;pull=0;tap=true;
                // 手势必须从顶部开始：先向上翻再拉回来的滚动不触发刷新。
                armed=isEnabled()&&!refreshing&&atTop.getAsBoolean();break;
            case MotionEvent.ACTION_MOVE:
                if(Math.abs(event.getX()-downX)>slop||Math.abs(event.getY()-downY)>slop)tap=false;
                if(!dragging&&armed&&!refreshing) {
                    float dx=event.getX()-downX,dy=event.getY()-downY;
                    if(dy>slop&&dy>Math.abs(dx)&&atTop.getAsBoolean()) {dragging=true;downY=event.getY();cancelAnimation();}
                }
                if(dragging) {pull=Math.max(0,(event.getY()-downY)*.5f);offset=Math.min(pull,trigger*1.5f);spin=0;invalidate();}
                break;
            case MotionEvent.ACTION_POINTER_DOWN:
                tap=false;
                // 多指手势不是下拉刷新，直接收回。
                if(dragging) {dragging=false;armed=false;pull=0;settle();}
                armed=false;break;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                if(dragging) {
                    boolean fire=event.getActionMasked()==MotionEvent.ACTION_UP&&pull>=trigger;
                    dragging=false;pull=0;
                    if(fire&&refresh.getAsBoolean())spinThenSettle();else settle();
                }
                armed=false;break;
            default:break;
        }
        return dragging;
    }
    private void settle() {animate(offset,0,180,false,null);}
    private void spinThenSettle() {refreshing=true;animate(offset,trigger*.75f,1000,true,()->{refreshing=false;settle();});}
    private void animate(float from,float to,long duration,boolean turning,Runnable end) {
        cancelAnimation();
        ValueAnimator next=ValueAnimator.ofFloat(0,1);next.setDuration(duration);
        next.addUpdateListener(animation->{
            float fraction=(Float)animation.getAnimatedValue();
            // 转动期间只用前一小段把圆圈收到停靠位置，其余时间原地旋转。
            offset=from+(to-from)*(turning?Math.min(1,fraction*6):fraction);spin=turning?fraction*900:0;invalidate();
        });
        next.addListener(new AnimatorListenerAdapter() {
            private boolean canceled;
            @Override public void onAnimationCancel(Animator animation) {canceled=true;}
            @Override public void onAnimationEnd(Animator animation) {if(animator==animation)animator=null;if(!canceled&&end!=null)end.run();}
        });
        animator=next;next.start();
    }
    private void cancelAnimation() {if(animator!=null) {ValueAnimator running=animator;animator=null;running.cancel();}}
    @Override protected void onDetachedFromWindow() {cancelAnimation();dragging=false;refreshing=false;offset=0;super.onDetachedFromWindow();}
    @Override protected void dispatchDraw(Canvas canvas) {
        super.dispatchDraw(canvas);
        if(offset<=0)return;
        float cx=getWidth()/2f,radius=size/2f,cy=offset-radius,progress=Math.min(1,offset/trigger);
        int alpha=Math.round(255*Math.min(1,offset/(trigger*.5f)));
        shade.setAlpha(alpha*18/255);canvas.drawCircle(cx,cy+density*1.5f,radius+density,shade);canvas.drawCircle(cx,cy+density*.5f,radius+density*.5f,shade);
        disc.setAlpha(alpha);canvas.drawCircle(cx,cy,radius,disc);
        rim.setAlpha(alpha);canvas.drawCircle(cx,cy,radius-rim.getStrokeWidth()/2f,rim);
        float ring=radius*.46f,start=refreshing?spin-90:-90+progress*150,sweep=refreshing?270:Math.max(8,300*progress);
        int ink=refreshing?255:Math.round(alpha*(.45f+.55f*progress));
        arc.setAlpha(ink);canvas.drawArc(cx-ring,cy-ring,cx+ring,cy+ring,start,sweep,false,arc);
        if(!refreshing) {
            // 拉动时弧线末端带箭头，拉满后提示松手即可刷新。
            double end=Math.toRadians(start+sweep);float px=cx+ring*(float)Math.cos(end),py=cy+ring*(float)Math.sin(end),tx=-(float)Math.sin(end),ty=(float)Math.cos(end),nx=(float)Math.cos(end),ny=(float)Math.sin(end),length=ring*.62f;
            tip.reset();tip.moveTo(px+tx*length,py+ty*length);tip.lineTo(px+nx*length*.7f,py+ny*length*.7f);tip.lineTo(px-nx*length*.7f,py-ny*length*.7f);tip.close();
            head.setAlpha(ink);canvas.drawPath(tip,head);
        }
    }
}
