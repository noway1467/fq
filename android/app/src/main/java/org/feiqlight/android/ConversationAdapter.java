package org.feiqlight.android;

import android.graphics.Color;
import android.graphics.Typeface;
import android.text.SpannableString;
import android.text.Spanned;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.BaseAdapter;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

final class ConversationAdapter extends BaseAdapter {
    private final MainActivity host;
    final List<ChatStore.Conversation> items=new ArrayList<>();
    ConversationAdapter(MainActivity host) { this.host=host; }
    void refresh() {
        items.clear();
        String query=host.search==null?"":host.search.getText().toString().toLowerCase(Locale.ROOT);
        if(host.service!=null) {
            for(ChatStore.Conversation c:host.service.conversations()) {
                String searchable=c.displayName()+" "+c.peer.name+" "+c.peer.id()+" "+c.preview;
                if(searchable.toLowerCase(Locale.ROOT).contains(query)) items.add(c);
            }
        }
        notifyDataSetChanged();
    }
    @Override public int getCount() { return items.size(); }
    @Override public Object getItem(int p) { return items.get(p); }
    @Override public long getItemId(int p) { return p; }
    @Override public View getView(int position,View convert,ViewGroup parent) {
        ChatStore.Conversation c=items.get(position);
        LinearLayout row=host.row();
        row.setPadding(host.dp(10),host.dp(5),host.dp(16),host.dp(5));
        row.setMinimumHeight(host.dp(82));
        // 禁止按文字基线预先测量加权中栏，先给右侧完整时间留出宽度。
        row.setBaselineAligned(false);
        TextView avatar=host.text("",21,Color.WHITE); avatar.setGravity(Gravity.CENTER);
        host.bindAvatar(avatar,c.peer.id(),c.displayName(),c.peer.online,c.peer.avatar);
        row.addView(avatar,new LinearLayout.LayoutParams(host.dp(68),host.dp(68)));
        LinearLayout center=host.column();
        LinearLayout.LayoutParams cp=host.weight();
        cp.leftMargin=host.dp(8);
        row.addView(center,cp);
        TextView name=host.text(c.displayName(),16,host.INK); name.setTypeface(null,Typeface.BOLD); name.setSingleLine();
        // 中间省略保留末尾编号，短名称时编号紧跟昵称，不挤到消息时间旁。
        name.setEllipsize(c.duplicateName?TextUtils.TruncateAt.MIDDLE:TextUtils.TruncateAt.END);
        name.setContentDescription(c.displayName());
        if(c.duplicateName) {
            SpannableString caption=new SpannableString(name.getText());
            int start=caption.length()-c.nameSuffix().length();
            caption.setSpan(new android.text.style.ForegroundColorSpan(host.MUTED),start,caption.length(),
                    Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            caption.setSpan(new android.text.style.RelativeSizeSpan(.8f),start,caption.length(),
                    Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            name.setText(caption);
        }
        if(c.pinned) {
            MenuIcon pin=new MenuIcon(host,"置顶会话",host.MUTED);
            pin.setBounds(0,0,host.dp(17),host.dp(17));
            name.setCompoundDrawables(pin,null,null,null);
            name.setCompoundDrawablePadding(host.dp(4));
            name.setContentDescription("已置顶，"+c.displayName());
        }
        center.addView(name);
        String previewText=c.preview.isEmpty()?(c.peer.online?"在线 · ":"离线 · ")+c.peer.endpoint.getAddress().getHostAddress()
                :MessageText.normalizeLineBreaks(c.preview).replace('\n',' ');
        TextView preview=host.text(previewText,14,host.MUTED);
        preview.setSingleLine();
        preview.setEllipsize(TextUtils.TruncateAt.END);
        preview.setPadding(0,host.dp(5),0,0);
        center.addView(preview);
        LinearLayout right=host.column();
        right.setGravity(Gravity.END);
        LinearLayout.LayoutParams rp=new LinearLayout.LayoutParams(-2,-2);
        rp.leftMargin=host.dp(8);
        row.addView(right,rp);
        TextView time=host.text(c.time==0?"":new SimpleDateFormat("HH:mm",Locale.CHINA).format(new Date(c.time)),11,
                host.MUTED);
        time.setSingleLine();
        right.addView(time);
        if(c.unread>0) {
            TextView badge=host.text(c.unread>99?"99+":Integer.toString(c.unread),11,Color.WHITE);
            badge.setGravity(Gravity.CENTER);
            badge.setBackground(host.shape(host.BLUE,14));
            LinearLayout.LayoutParams b=new LinearLayout.LayoutParams(host.dp(28),host.dp(23));
            b.topMargin=host.dp(7);
            right.addView(badge,b);
        }
        return row;
    }
}
