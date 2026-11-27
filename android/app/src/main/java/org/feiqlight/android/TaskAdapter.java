package org.feiqlight.android;

import android.app.Dialog;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.view.*;
import android.widget.*;
import org.feiqlight.android.core.Protocol;
import java.util.*;

final class TaskAdapter extends BaseAdapter {
    List<ChatStore.FileTask> all=new ArrayList<>(),items=new ArrayList<>();
    final TextView count;
    final EditText input;
    int filter;
    private final MainActivity host;
    final LinearLayout sourceBar,sourceItems;
    final HorizontalScrollView sourceScroll;
    final Dialog dialog;
    TextView empty;
    boolean scrolling;
    private boolean refreshScheduled;
    final Map<Long,String> times=new HashMap<>();
    private String selectedSource,sourceSignature,contentSignature,summaryText;
    // 行签名、绘制与午夜刷新共用时钟；回归可注入时间而不修改系统时钟。
    private java.util.function.LongSupplier clock=System::currentTimeMillis;
    private final Handler clockHandler=new Handler(Looper.getMainLooper());
    private final Runnable dayRefresh;
    private final Runnable deferredRefresh;
    private final Runnable storageRefresh;
    TaskAdapter(MainActivity host,TextView count,EditText input,Dialog dialog,String sourceKey) {
        this.host=host;
        sourceBar=host.row();
        sourceItems=host.row();
        sourceScroll=new HorizontalScrollView(host);
        this.count=count;
        this.input=input;
        this.dialog=dialog;
        selectedSource=sourceKey;
        deferredRefresh=()->{
            refreshScheduled=false;
            if(host.resumed&&host.taskAdapter==this&&dialog.isShowing()&&!scrolling)refresh();
        };
        storageRefresh=()->{
            if(host.resumed&&host.taskAdapter==this&&dialog.isShowing()){
                if(scrolling)scheduleStorageRefresh();
                else host.loadTaskRows();
            }
        };
        dayRefresh=()->{
            if(host.resumed&&host.taskAdapter==this&&dialog.isShowing()&&!host.isDestroyed()){
                times.clear();
                if(!scrolling)refresh();
                scheduleDayRefresh();
            }
        };
        sourceBar.setPadding(host.dp(12),0,host.dp(12),0);
        sourceItems.setGravity(Gravity.TOP);
        sourceItems.setPadding(0,host.dp(4),0,host.dp(4));
        sourceScroll.setHorizontalScrollBarEnabled(false);
        sourceScroll.setContentDescription("传输来源");
        sourceScroll.addView(sourceItems);
        sourceBar.addView(sourceScroll,new LinearLayout.LayoutParams(0,-2,1));
        sourceScroll.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->{if(r-l!=or-ol)showSelectedSource();});
        sourceItems.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->{if(r-l!=or-ol)showSelectedSource();});
    }
    void requestSearch() {
        clockHandler.removeCallbacks(deferredRefresh);
        refreshScheduled=true;
        clockHandler.postDelayed(deferredRefresh,120);
    }
    void requestRefresh() {
        if(host.resumed&&!scrolling&&!refreshScheduled){
            refreshScheduled=true;
            clockHandler.postDelayed(deferredRefresh,200);
        }
    }
    void scheduleStorageRefresh() {
        clockHandler.removeCallbacks(storageRefresh);
        if(host.resumed&&dialog.isShowing())clockHandler.postDelayed(storageRefresh,3000);
    }
    private String time(ChatStore.FileTask item,long now) {
        return times.computeIfAbsent(item.message.time,value->MainActivity.taskTime(value,now));
    }
    void scheduleDayRefresh() {
        clockHandler.removeCallbacks(dayRefresh);
        long now=clock.getAsLong();
        Calendar midnight=Calendar.getInstance();
        midnight.setTimeInMillis(now);
        midnight.add(Calendar.DAY_OF_MONTH,1);
        midnight.set(Calendar.HOUR_OF_DAY,0);
        midnight.set(Calendar.MINUTE,0);
        midnight.set(Calendar.SECOND,0);
        midnight.set(Calendar.MILLISECOND,0);
        clockHandler.postDelayed(dayRefresh,Math.max(1,midnight.getTimeInMillis()-now));
    }
    void stop() {
        refreshScheduled=false;
        clockHandler.removeCallbacks(dayRefresh);
        clockHandler.removeCallbacks(deferredRefresh);
        clockHandler.removeCallbacks(storageRefresh);
    }
    private void showSelectedSource() {
        if(sourceScroll.getWidth()==0)return;
        View selected=sourceItems.findViewWithTag("task-source:"+selectedSource);
        if(selected==null)return;
        int x=sourceScroll.getScrollX();
        if(selected.getLeft()<x)x=selected.getLeft();
        else if(selected.getRight()>x+sourceScroll.getWidth())x=selected.getRight()-sourceScroll.getWidth();
        sourceScroll.scrollTo(x,0);
    }
    private void refreshSources() {
        Map<String,Long> latest=new HashMap<>();
        for(ChatStore.FileTask item:all) {
            String key=item.guestFile!=null?"guest":"peer:"+item.peer;
            latest.merge(key,Math.max(0,item.message.time),Math::max);
        }
        List<TaskSource> sources=new ArrayList<>();
        for(Map.Entry<String,Long> entry:latest.entrySet())sources.add(source(entry.getKey(),entry.getValue()));
        sources.sort(Comparator.comparingLong((TaskSource item)->item.time).reversed().thenComparing(item->item.name).thenComparing(item->item.key));
        if(!selectedSource.isEmpty()&&!latest.containsKey(selectedSource))sources.add(0,source(selectedSource,0));
        sources.add(0,new TaskSource("","全部",null,0));
        StringBuilder signature=new StringBuilder(selectedSource);
        for(TaskSource item:sources)signature.append('\0').append(item.key).append('\0').append(item.name).append('\0').append(item.avatar);
        String value=signature.toString();
        if(value.equals(sourceSignature))return;
        sourceSignature=value;
        sourceItems.removeAllViews();
        for(TaskSource item:sources) {
            boolean chosen=item.key.equals(selectedSource);
            LinearLayout pick=host.column();
            pick.setGravity(Gravity.CENTER_HORIZONTAL);
            pick.setPadding(host.dp(2),host.dp(2),host.dp(2),host.dp(4));
            pick.setTag("task-source:"+item.key);
            pick.setSelected(chosen);
            pick.setFocusable(true);
            pick.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES);
            pick.setContentDescription(item.name+"，"+(chosen?"已选中":"未选中"));
            FrameLayout circle=new FrameLayout(host);
            GradientDrawable ring=host.shape(host.PALE,99);
            ring.setStroke(host.dp(2),chosen?host.BLUE:Color.TRANSPARENT);
            circle.setBackground(ring);
            circle.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            if(item.key.isEmpty()||item.key.equals("guest")) {
                ImageView icon=new ImageView(host);
                icon.setPadding(host.dp(8),host.dp(8),host.dp(8),host.dp(8));
                icon.setImageDrawable(new MenuIcon(host,item.key.isEmpty()?"传输任务":"临时浏览器传输",chosen?host.BLUE:host.MUTED));
                circle.addView(icon,new FrameLayout.LayoutParams(host.dp(40),host.dp(40),Gravity.CENTER));
            } else {
                TextView avatar=host.text("",18,Color.WHITE);
                avatar.setBackground(new AvatarDrawable(host,item.key.substring(5),false,0,host.SURFACE,
                        host.ONLINE,item.avatar));
                circle.addView(avatar,new FrameLayout.LayoutParams(host.dp(40),host.dp(40),Gravity.CENTER));
            }
            pick.addView(circle,new LinearLayout.LayoutParams(host.dp(44),host.dp(44)));
            TextView name=host.text(item.name,12,chosen?host.BLUE:host.MUTED);
            name.setSingleLine();
            name.setEllipsize(TextUtils.TruncateAt.END);
            name.setGravity(Gravity.CENTER);
            name.setPadding(0,host.dp(4),0,0);
            name.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            pick.addView(name,new LinearLayout.LayoutParams(-1,-2));
            sourceItems.addView(pick,new LinearLayout.LayoutParams(host.dp(68),-2));
            pick.setOnClickListener(v->{if(!selectedSource.equals(item.key)){selectedSource=item.key;refresh();}});
        }
        sourceItems.post(()->{if(host.taskAdapter==this&&dialog.isShowing())showSelectedSource();});
    }
    private TaskSource source(String key,long time) {
        if(key.equals("guest"))return new TaskSource(key,"浏览器访客",null,time);
        ChatStore.Conversation contact=host.conversation(key.substring(5));
        return new TaskSource(key,contact==null?"已移除的设备":contact.displayName(),
                contact==null?null:Protocol.normalizeAvatar(contact.peer.avatar),time);
    }
    void refresh() {
        refreshScheduled=false;
        clockHandler.removeCallbacks(deferredRefresh);
        refreshSources();
        long now=clock.getAsLong();
        String query=input.getText().toString().trim().toLowerCase(Locale.ROOT);
        List<ChatStore.FileTask> next=new ArrayList<>();
        int completed=0;
        double total=0,done=0;
        StringBuilder content=new StringBuilder();
        for(ChatStore.FileTask item:all){String source=item.guestFile!=null?"guest":"peer:"+item.peer;
            if(!selectedSource.isEmpty()&&!selectedSource.equals(source))continue;
            String state=host.taskState(item);
            ChatStore.Conversation contact=host.conversation(item.peer);
            String name=item.guestFile!=null?"浏览器访客":contact==null?"已移除的设备":contact.displayName();
            if(!(item.file.name+" "+name).toLowerCase(Locale.ROOT).contains(query)||filter==1&&!MainActivity.taskActive(state)||
                    filter==2&&(MainActivity.taskActive(state)||MainActivity.taskComplete(state))||filter==3&&!MainActivity.taskComplete(state))continue;
            next.add(item);
            if(MainActivity.taskComplete(state))completed++;
            ChatService.FileProgress p=host.taskProgress(item);
            int percent=MainActivity.taskComplete(state)?100:state.equals("本机文件已删除")?0:p==null?0:p.percent;
            total+=item.file.size;
            done+=item.file.size*percent/100.0;
            content.append(item.peer).append('\0').append(item.message.number).append('\0').append(item.message.outgoing).append('\0').append(item.file.id).append('\0')
                .append(item.file.name).append('\0').append(item.file.size).append('\0').append(name).append('\0').append(time(item,
                        now)).append('\0').append(state).append('\0').append(percent).append('\0')
                .append(host.taskLabel(item,state,
                        p)).append('\0').append(item.guestFile==null?"":item.guestFile.getAbsolutePath()).append('\0')
                .append(host.taskQuickActions(item,state,p,contact)).append('\0');
        }
        String signature=content.toString();int queued=host.service==null?0:host.service.queuedTransfers();
        String summary=next.size()+" 个文件 · "+completed+" 个已完成"
                +(total>0?" · "+MainActivity.size((long)done)+" / "+MainActivity.size((long)total):"")
                +(queued>0?"\n另有 "+queued+" 项文件操作排队中":"");
        boolean rowsChanged=!signature.equals(contentSignature),summaryChanged=!summary.equals(summaryText);
        items=next;
        String emptyText=!query.isEmpty()?"没有匹配的文件或设备":!selectedSource.isEmpty()||
                filter!=0?"当前筛选下没有传输任务":"暂无传输任务\n收到或发送的文件会显示在这里";
        if(empty!=null&&!emptyText.contentEquals(empty.getText()))empty.setText(emptyText);
        if(!rowsChanged&&!summaryChanged)return;
        contentSignature=signature;summaryText=summary;
        if(summaryChanged&&!summary.contentEquals(count.getText()))count.setText(summary);
        if(rowsChanged)notifyDataSetChanged();
    }
    public int getCount(){return items.size();}
    public Object getItem(int position){return items.get(position);}
    public long getItemId(int position){return position;}
    public View getView(int position,View old,ViewGroup parent) {
        ChatStore.FileTask item=items.get(position);LinearLayout box;
        if(old==null){
            box=host.column();
            box.setPadding(host.dp(20),host.dp(12),host.dp(20),host.dp(12));
            TextView name=host.text("",15,host.INK);
            name.setTypeface(null,Typeface.BOLD);
            name.setSingleLine();
            name.setEllipsize(TextUtils.TruncateAt.END);
            box.addView(name);
            TextView meta=host.text("",12,host.MUTED);
            meta.setSingleLine();
            meta.setEllipsize(TextUtils.TruncateAt.END);
            meta.setPadding(0,host.dp(5),0,host.dp(4));
            box.addView(meta);
            TextView status=host.text("",12,host.MUTED);
            status.setMaxLines(2);
            box.addView(status);
            ProgressBar bar=new ProgressBar(host,null,android.R.attr.progressBarStyleHorizontal);
            bar.setMax(100);
            bar.setProgressTintList(android.content.res.ColorStateList.valueOf(host.BLUE));
            bar.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(host.LINE));
            LinearLayout.LayoutParams bp=new LinearLayout.LayoutParams(-1,host.dp(3));
            bp.topMargin=host.dp(8);
            box.addView(bar,bp);
            LinearLayout actions=host.row();
            LinearLayout.LayoutParams ap=new LinearLayout.LayoutParams(-1,-2);
            ap.topMargin=host.dp(8);
            box.addView(actions,ap);
        }else box=(LinearLayout)old;
        ChatStore.Conversation contact=host.conversation(item.peer);
        String state=host.taskState(item);
        ChatService.FileProgress p=host.taskProgress(item);
        String time=time(item,clock.getAsLong());
        ((TextView)box.getChildAt(0)).setText(item.file.name);
        ((TextView)box.getChildAt(1)).setText((item.message.outgoing?"发送给 ":"来自 ")
                +(item.guestFile!=null?"浏览器访客":contact==null?"已移除的设备":contact.displayName())
                +" · "+MainActivity.size(item.file.size)+(time.isEmpty()?"":" · "+time));
        String label=host.taskLabel(item,state,p);
        TextView status=(TextView)box.getChildAt(2);
        status.setText(label);
        status.setTextColor(MainActivity.taskComplete(state)?host.ONLINE:MainActivity.taskActive(state)?host.BLUE:host.MUTED);
        ProgressBar bar=(ProgressBar)box.getChildAt(3);
        bar.setProgress(MainActivity.taskComplete(state)?100:state.equals("本机文件已删除")?0:p==null?0:p.percent);
        bar.setVisibility(MainActivity.taskEnded(state)?View.GONE:View.VISIBLE);
        LinearLayout actions=(LinearLayout)box.getChildAt(4);ChatService source=host.service;
        List<String> labels=host.taskQuickActions(item,state,p,contact);
        while(actions.getChildCount()>labels.size())actions.removeViewAt(actions.getChildCount()-1);
        for(int index=0;index<labels.size();index++) {
            String action=labels.get(index);
            Button button=index<actions.getChildCount()?(Button)actions.getChildAt(index):host.taskButton(actions);
            if(!action.contentEquals(button.getText()))button.setText(action);
            button.setTextColor(action.equals("拒绝")||action.startsWith("取消")?0xffd24e54:host.BLUE);
            button.setOnClickListener(v->{
                if(host.service!=source)return;
                switch(action) {
                    case "暂停":case "暂停排队":source.pauseFile(item.peer,item.message.number,item.file.id);break;
                    case "取消":host.requestCancelFile(item);break;
                    case "接收":case "继续接收":source.receive(contact.peer,item.message.number,item.file);break;
                    case "拒绝":host.requestRejectFiles(contact.peer,item.message.number);break;
                    case "打开":
                        Uri uri=host.taskUri(item);
                        if(uri==null)host.error("本机文件已删除");
                        else host.openFile(uri,item.file.name,false);
                        break;
                    case "取消发送":host.requestCancelOutgoingTask(item);break;
                }
            });
        }
        actions.setVisibility(actions.getChildCount()==0?View.GONE:View.VISIBLE);
        box.setContentDescription(item.file.name+"，"+label);
        return box;
    }
}
