package org.feiqlight.android;

import android.view.MotionEvent;
import android.widget.ScrollView;
import org.feiqlight.android.core.LanNode;
import java.util.List;

final class ChatScroll extends ScrollView {
    private boolean touching;
    private long movedAt=-1000;
    private final MainActivity host;
    private final Runnable settled;
    ChatScroll(MainActivity host){
        super(host);
        this.host=host;
        settled=this::settle;
    }
    private void settle(){
        if(host.scroll!=this||touching||android.os.SystemClock.uptimeMillis()-movedAt<100)return;
        if(!host.visible||host.historyDialog!=null&&host.historyDialog.isShowing()||host.tasksDialog!=null&&
                host.tasksDialog.isShowing())return;
        if(host.deferredChatRows!=null){
            List<LanNode.Message> rows=host.deferredChatRows;
            host.deferredChatRows=null;
            host.renderMessages(rows);
        }
        host.transfersChanged();
    }
    boolean moving(){return touching||android.os.SystemClock.uptimeMillis()-movedAt<100;}
    @Override public boolean dispatchTouchEvent(MotionEvent event) {
        if(event.getActionMasked()==MotionEvent.ACTION_DOWN)touching=true;
        if(event.getActionMasked()==MotionEvent.ACTION_UP||event.getActionMasked()==MotionEvent.ACTION_CANCEL){
            touching=false;
            host.chatUpdates.removeCallbacks(settled);
            host.chatUpdates.postDelayed(settled,120);
        }
        return super.dispatchTouchEvent(event);
    }
    @Override protected void onScrollChanged(int x,int y,int oldX,int oldY){
        super.onScrollChanged(x,y,oldX,oldY);
        movedAt=android.os.SystemClock.uptimeMillis();
        host.chatUpdates.removeCallbacks(settled);
        host.chatUpdates.postDelayed(settled,120);
    }
}
