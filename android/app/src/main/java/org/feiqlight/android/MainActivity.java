package org.feiqlight.android;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.ClipboardManager;
import android.content.pm.PackageManager;
import android.graphics.*;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.*;
import android.provider.DocumentsContract;
import android.text.*;
import android.view.*;
import android.view.inputmethod.InputMethodManager;
import android.widget.*;
import org.feiqlight.android.core.*;
import java.io.File;
import java.text.SimpleDateFormat;
import java.util.*;

/** 原生 View 界面：借鉴 Telegram 的信息层级，不加载 WebView 或第三方 UI 运行时。 */
public final class MainActivity extends Activity implements ChatService.Observer {
    private int BLUE=0xff3390ec, INK=0xff17212b, MUTED=0xff738392, PALE=0xfff1f4f7, WALL=0xffe3edf5, SURFACE=Color.WHITE, BUBBLE=0xffd9edff;
    private void applyTheme() {
        SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE); boolean dark=prefs.getBoolean("dark_mode",false);
        String accent=prefs.getString("accent","blue");BLUE=accent.equals("green")?0xff229974:accent.equals("purple")?0xff8b67d7:0xff3390ec;
        SURFACE=dark?0xff1e2731:Color.WHITE;INK=dark?0xffe6ebf1:0xff17212b;MUTED=dark?0xffa6b6c7:0xff738392;
        PALE=dark?0xff2a3644:0xfff1f4f7;WALL=dark?0xff131c26:0xffeff3f7;BUBBLE=blend(BLUE,SURFACE,dark?.23f:.12f);
        if(accent.equals("green")){SURFACE=dark?0xff172d29:0xfff4faf4;INK=dark?0xffe4f3e9:0xff20382d;MUTED=dark?0xffa3c4b6:0xff627d6a;PALE=dark?0xff28433b:0xffe4efe2;WALL=dark?0xff10221c:0xffd5e5cc;}
        else if(accent.equals("purple")){SURFACE=dark?0xff292338:0xfffcf7fc;INK=dark?0xfff0e8f7:0xff38283f;MUTED=dark?0xffbeb0d0:0xff84708d;PALE=dark?0xff3a304b:0xffefe5f1;WALL=dark?0xff1a1227:0xffe6d8ed;}
        BUBBLE=blend(BLUE,SURFACE,dark?.30f:.17f);
        getWindow().setStatusBarColor(SURFACE);getWindow().setNavigationBarColor(SURFACE);
    }
    private static final int[] AVATARS={0xff66a9e0,0xff7e9bdb,0xff65bca5,0xffdca169,0xffa38cc9};
    private ChatService service;
    private DraftStore drafts;
    private ReceivedStorage receivedStorage;
    private MediaPreview media;
    private TextView folderLabel;
    private RadioGroup wallpaperChoices;
    private String editorSavedText="";
    private boolean bound, visible, rendering, sending;
    private boolean restoreAttempted;
    private Bitmap backgroundBitmap;
    private String loadedBackground="";
    private int messageSize(){return Math.max(14,Math.min(24,getSharedPreferences("settings",MODE_PRIVATE).getInt("chat_font_size",16)));}
    private ChatWallpaper chatWallpaper(){
        SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE);String mode=prefs.getString("chat_background","orbits"),uri=prefs.getString("background_image","");
        if(!uri.equals(loadedBackground)){
            Bitmap old=backgroundBitmap;backgroundBitmap=null;loadedBackground=uri;
            if(!uri.isEmpty())try{backgroundBitmap=ChatWallpaper.load(this,Uri.parse(uri));}catch(Exception e){error("背景图片不可用，已使用内置背景");}
            if(old!=null)old.recycle();
        }
        return new ChatWallpaper(this,WALL,BLUE,mode,backgroundBitmap);
    }
    private long historyRequestVersion, historyAppliedVersion;
    private ActionMode selectionMode;
    private TextView selectionOwner;
    private String selected="", pickerPeer="", exportPath="", historySignature="";
    private final Set<Long> displayedOutgoing=new HashSet<>();
    private final List<Runnable> fileProgressViews=new ArrayList<>();
    private String progressPeer="";
    private boolean forceLatest;
    private LinearLayout root, body, toolbar, messages, connectionBar;
    private TextView title, subtitle, transferStatus, empty, chatAvatar;
    private EditText search, editor;
    private ListView list;
    private ScrollView scroll;
    private Button connect;
    private IconButton sendButton;
    private android.window.OnBackInvokedCallback backCallback;
    private ConversationAdapter adapter;
    private final ServiceConnection connection=new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name,IBinder binder) {
            service=((ChatService.LocalBinder)binder).service(); if(visible) service.observe(MainActivity.this);
            service.active(visible?selected:""); saveDraft(); renderPage(); restoreConnection();
        }
        @Override public void onServiceDisconnected(ComponentName name) { service=null; renderPage(); }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        drafts=new DraftStore(this); receivedStorage=new ReceivedStorage(this); media=new MediaPreview(this);
        if(state!=null) { selected=state.getString("peer",""); pickerPeer=state.getString("picker",""); exportPath=state.getString("export",""); }
        if(Build.VERSION.SDK_INT>=33) { backCallback=()->{ if(!selected.isEmpty()) open(""); else finish(); }; getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,backCallback); }
        renderPage(); bound=bindService(new Intent(this,ChatService.class),connection,BIND_AUTO_CREATE);
    }
    @Override protected void onStart() { super.onStart(); visible=true; if(service!=null) { service.observe(this); service.active(selected); } restoreConnection(); }
    @Override protected void onStop() { historyAppliedVersion=++historyRequestVersion; finishSelection(); saveDraft(); visible=false; if(service!=null) { service.remove(this); service.active(""); } super.onStop(); }
    @Override protected void onDestroy() { if(media!=null) media.close(); if(backgroundBitmap!=null){backgroundBitmap.recycle();backgroundBitmap=null;} if(service!=null) service.remove(this); if(bound) unbindService(connection); if(Build.VERSION.SDK_INT>=33&&backCallback!=null) getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(backCallback); super.onDestroy(); }
    @Override protected void onSaveInstanceState(Bundle state) { saveDraft(); state.putString("peer",selected); state.putString("picker",pickerPeer); state.putString("export",exportPath); super.onSaveInstanceState(state); }
    private int dp(float n) { return Math.round(n*getResources().getDisplayMetrics().density); }
    private GradientDrawable shape(int color,int radius) { GradientDrawable d=new GradientDrawable(); d.setColor(color); d.setCornerRadius(dp(radius)); return d; }
    private static final int CONTROL_RADIUS=10;
    private static int blend(int front,int back,float amount){return Color.rgb((int)(Color.red(front)*amount+Color.red(back)*(1-amount)),(int)(Color.green(front)*amount+Color.green(back)*(1-amount)),(int)(Color.blue(front)*amount+Color.blue(back)*(1-amount)));}
    private TextView text(String value,float size,int color) {
        TextView v=new TextView(this); v.setText(value); v.setTextSize(size); v.setTextColor(color); return v;
    }
    private LinearLayout column() { LinearLayout box=new LinearLayout(this); box.setOrientation(LinearLayout.VERTICAL); return box; }
    private LinearLayout row() { LinearLayout box=new LinearLayout(this); box.setOrientation(LinearLayout.HORIZONTAL); box.setGravity(Gravity.CENTER_VERTICAL); return box; }
    private LinearLayout.LayoutParams weight() { return new LinearLayout.LayoutParams(0,ViewGroup.LayoutParams.WRAP_CONTENT,1); }
    @Override public boolean dispatchTouchEvent(MotionEvent event) {
        dismissSelectionOutside(event); return super.dispatchTouchEvent(event);
    }
    private void dismissSelectionOutside(MotionEvent event) {
        if(event.getActionMasked()!=MotionEvent.ACTION_DOWN || selectionMode==null || selectionOwner==null) return;
        Rect bounds=new Rect(); int[] location=new int[2];
        selectionOwner.getLocationOnScreen(location);
        boolean shown=selectionOwner.getLocalVisibleRect(bounds); bounds.offset(location[0],location[1]);
        // 不吞掉触摸；原生工具栏与选区手柄的独立窗口不经过这里。
        if(!shown || !bounds.contains((int)event.getRawX(),(int)event.getRawY())) finishSelection();
    }
    private void finishSelection() {
        ActionMode mode=selectionMode; selectionMode=null; selectionOwner=null;
        if(mode!=null) mode.finish();
    }
    private void trackSelection(TextView owner,ActionMode mode) {
        if(selectionMode!=mode) finishSelection(); selectionOwner=owner; selectionMode=mode;
    }
    private void releaseSelection(ActionMode mode) {
        if(selectionMode==mode) { selectionMode=null; selectionOwner=null; }
    }
    private void selectable(TextView view) {
        view.setTextIsSelectable(true);
        view.setCustomSelectionActionModeCallback(new ActionMode.Callback() {
            public boolean onCreateActionMode(ActionMode mode,Menu menu) { trackSelection(view,mode); return true; }
            public boolean onPrepareActionMode(ActionMode mode,Menu menu) { return false; }
            public boolean onActionItemClicked(ActionMode mode,MenuItem item) { return false; }
            public void onDestroyActionMode(ActionMode mode) { releaseSelection(mode); }
        });
    }
    private void renderPage() {
        historyAppliedVersion=++historyRequestVersion;
        finishSelection();
        applyTheme(); historySignature=""; displayedOutgoing.clear(); forceLatest=false; editor=null; search=null; messages=null; list=null; chatAvatar=null;
        root=column(); root.setBackgroundColor(SURFACE); setContentView(root);
        root.setOnApplyWindowInsetsListener((view,insets)->{
            if(Build.VERSION.SDK_INT>=30) { Insets i=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout()|WindowInsets.Type.ime()); view.setPadding(i.left,i.top,i.right,i.bottom); }
            else view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            return insets;
        }); root.requestApplyInsets();
        toolbar=row(); toolbar.setPadding(dp(8),0,dp(8),0); toolbar.setMinimumHeight(dp(64)); root.addView(toolbar,new LinearLayout.LayoutParams(-1,-2));
        IconButton menu=new IconButton(selected.isEmpty()?"menu":"back",selected.isEmpty()?"打开菜单":"返回会话列表",MUTED);
        toolbar.addView(menu,new LinearLayout.LayoutParams(dp(48),dp(48))); menu.setOnClickListener(v->{ if(selected.isEmpty()) showMenu(v); else open(""); });
        if(!selected.isEmpty()) {
            ChatStore.Conversation current=conversation(selected); String name=current==null?"?":current.displayName();
            TextView avatar=text(name.isEmpty()?"?":name.substring(0,name.offsetByCodePoints(0,1)),18,Color.WHITE); avatar.setGravity(Gravity.CENTER); bindAvatar(avatar,selected,name,current!=null&&current.peer.online);
            chatAvatar=avatar;
            LinearLayout.LayoutParams ap=new LinearLayout.LayoutParams(dp(54),dp(54)); ap.leftMargin=-dp(6); ap.rightMargin=dp(4); toolbar.addView(avatar,ap);
        }
        LinearLayout headings=column(); title=text("飞Q",20,INK); title.setTypeface(null,Typeface.BOLD); subtitle=text("",12,MUTED);
        title.setSingleLine(); title.setEllipsize(TextUtils.TruncateAt.END); subtitle.setSingleLine(); subtitle.setEllipsize(TextUtils.TruncateAt.END);
        headings.addView(title); headings.addView(subtitle); toolbar.addView(headings,weight());
        IconButton action=new IconButton(selected.isEmpty()?"add":"more",selected.isEmpty()?"手动添加联系人":"会话菜单",MUTED);
        toolbar.addView(action,new LinearLayout.LayoutParams(dp(48),dp(48))); action.setOnClickListener(v->{ if(selected.isEmpty()) addPeer(); else {ChatStore.Conversation c=conversation(selected);if(c!=null)conversationMenu(c);} });
        View line=new View(this); line.setBackgroundColor(blend(INK,SURFACE,.09f)); root.addView(line,new LinearLayout.LayoutParams(-1,dp(1)));
        body=column(); root.addView(body,new LinearLayout.LayoutParams(-1,0,1));
        if(selected.isEmpty()) renderList(); else renderChat();
        changed();
    }
    private void renderList() {
        LinearLayout searchWrap=column(); searchWrap.setPadding(dp(16),dp(10),dp(16),dp(10)); body.addView(searchWrap);
        search=new EditText(this); configureInput(search); search.setSingleLine(); search.setTextSize(15); search.setHint("搜索"); search.setHintTextColor(MUTED); search.setTextColor(INK);
        search.setPadding(dp(16),dp(9),dp(16),dp(9)); search.setBackground(shape(PALE,CONTROL_RADIUS)); search.setContentDescription("搜索联系人或最近消息");
        searchWrap.addView(search,new LinearLayout.LayoutParams(-1,dp(46)));
        FrameLayout content=new FrameLayout(this); body.addView(content,new LinearLayout.LayoutParams(-1,0,1));
        list=new ListView(this); list.setDivider(null); list.setClipToPadding(false); list.setPadding(0,0,0,dp(12));
        adapter=new ConversationAdapter(); list.setAdapter(adapter); content.addView(list,new FrameLayout.LayoutParams(-1,-1));
        empty=text("",15,MUTED); empty.setGravity(Gravity.CENTER); empty.setPadding(dp(38),dp(24),dp(38),dp(24)); empty.setLineSpacing(dp(8),1); content.addView(empty,new FrameLayout.LayoutParams(-1,-1)); list.setEmptyView(empty);
        list.setOnItemClickListener((p,v,index,id)->open(adapter.items.get(index).peer.id()));
        list.setOnItemLongClickListener((p,v,index,id)->{conversationMenu(adapter.items.get(index));return true;});
        search.addTextChangedListener(new TextWatcher() { public void beforeTextChanged(CharSequence s,int start,int count,int after) { } public void onTextChanged(CharSequence s,int start,int before,int count) { adapter.refresh(); } public void afterTextChanged(Editable e) { } });
        LinearLayout footer=column(); connectionBar=footer; footer.setPadding(dp(20),dp(8),dp(20),dp(16)); body.addView(footer);
        connect=button("连接",true);
        LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,dp(50)); params.topMargin=dp(12); footer.addView(connect,params);
        connect.setOnClickListener(v->{ if(service!=null&&service.online()) service.refresh(); else connect(); });
    }
    private int editorGeneration;
    private void renderChat() {
        body.setBackground(chatWallpaper()); scroll=new ScrollView(this); scroll.setFillViewport(true); scroll.setClipToPadding(false);
        messages=column(); messages.setPadding(dp(12),dp(18),dp(12),dp(18)); scroll.addView(messages,new ScrollView.LayoutParams(-1,-2)); body.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        transferStatus=text("",12,BLUE); transferStatus.setMaxLines(2); transferStatus.setEllipsize(TextUtils.TruncateAt.END); transferStatus.setPadding(dp(16),dp(8),dp(16),dp(8)); transferStatus.setBackgroundColor(SURFACE); transferStatus.setOnClickListener(v->{ if(service!=null&&service.busy()) confirm("文件传输","取消当前任务？","取消任务",()->service.cancelTransfer()); }); body.addView(transferStatus);
        LinearLayout composer=new LinearLayout(this) {
            @Override protected void onMeasure(int width,int height) {
                if(editor!=null&&editor.getParent()==this) {
                    int padding=editor.getCompoundPaddingTop()+editor.getCompoundPaddingBottom();
                    int natural=editor.getLineHeight()*5+padding;
                    int budget=View.MeasureSpec.getMode(height)==View.MeasureSpec.UNSPECIFIED?natural:View.MeasureSpec.getSize(height)-getPaddingTop()-getPaddingBottom()-dp(48);
                    int limit=Math.min(natural,Math.max(editor.getLineHeight()+padding,budget));
                    if(editor.getMaxHeight()!=limit) editor.setMaxHeight(limit);
                }
                super.onMeasure(width,height);
            }
        }; composer.setOrientation(LinearLayout.HORIZONTAL);composer.setGravity(Gravity.CENTER_VERTICAL);composer.setPadding(dp(4),dp(8),dp(8),dp(8)); composer.setBackgroundColor(SURFACE); body.addView(composer);
        IconButton file=new IconButton("clip","发送文件",MUTED); composer.addView(file,new LinearLayout.LayoutParams(dp(48),dp(48))); file.setOnClickListener(v->pickFile());
        IconButton emoji=new IconButton("smile","表情",MUTED);composer.addView(emoji,new LinearLayout.LayoutParams(dp(48),dp(48)));emoji.setOnClickListener(v->showEmoji());
        final String inputPeer=selected;
        final int inputGeneration=++editorGeneration;
        editor=new ChatInput(this,(uri,mime,release)->{
            if(isDestroyed()||isFinishing()||inputGeneration!=editorGeneration||!selected.equals(inputPeer)||service==null) return false;
            ChatStore.Conversation target=conversation(inputPeer);
            return target!=null && service.sendKeyboardImage(target.peer,uri,mime,release);
        }); configureInput(editor); editor.setContentDescription("消息输入框"); editor.setHint("消息"); editor.setTextSize(messageSize()); editor.setTextColor(INK); editor.setHintTextColor(MUTED); editor.setBackground(shape(PALE,CONTROL_RADIUS)); editor.setPadding(dp(16),dp(12),dp(16),dp(12));
        editor.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE|android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES); editor.setMaxLines(5); editor.setMinHeight(dp(48));
        editor.setFilters(new InputFilter[]{new InputFilter.LengthFilter(18000)});
        editorSavedText=drafts.read(selected); editor.setText(editorSavedText); composer.addView(editor,weight());
        editor.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s,int start,int count,int after) { }
            public void onTextChanged(CharSequence s,int start,int before,int count) { saveDraft(); }
            public void afterTextChanged(Editable value) { }
        });
        sendButton=new IconButton("send","发送消息",Color.WHITE); sendButton.setBackground(shape(BLUE,28)); LinearLayout.LayoutParams sendParams=new LinearLayout.LayoutParams(dp(48),dp(48)); sendParams.leftMargin=dp(8); composer.addView(sendButton,sendParams);
        sendButton.setOnClickListener(v->send());
    }
    private static final String[] EMOJI_GROUPS={
        "😀 😃 😄 😁 😆 😅 😂 🤣 😊 😇 🙂 🙃 😉 😌 😍 🥰 😘 😋 😎 🤔 😭 😴 😡 🥺",
        "👍 👎 👏 🙌 🤝 🙏 👋 ✋ 👌 ✌️ 💪 👀 🧠 👩 👨 🧑 👧 👦 👶 👩‍💻 👨‍💻 👨‍👩‍👧‍👦 👍🏽 🫶",
        "❤️ 🧡 💛 💚 💙 💜 🖤 🤍 💕 💖 💗 💝 💔 💯 🔥 ✨ ⭐ 🌟 🎉 🎊 🎈 🎁 🎂 🥳",
        "🐶 🐱 🐭 🐹 🐰 🦊 🐻 🐼 🐨 🐯 🦁 🐸 🐷 🐵 🐔 🐧 🦄 🦋 🌸 🌹 🌻 🌈 ☀️ 🌙",
        "🍎 🍊 🍋 🍌 🍉 🍇 🍓 🍒 🥑 🍞 🥐 🍔 🍟 🍕 🍜 🍣 🍙 🍰 🍪 🍫 🍦 ☕ 🍵 🧋",
        "⚽ 🏀 🎾 🏆 🎮 🎯 🎵 🎧 🎤 🎸 🚀 ✈️ 🚗 🚲 🏠 🌍 💡 📚 ✏️ 📎 📁 ✅ ❌ 🇨🇳"
    };
    private void showEmoji() {
        if(editor==null)return;
        EditText input=editor;String peer=selected;int generation=editorGeneration;
        int start=Math.max(0,Math.min(input.getSelectionStart(),input.getSelectionEnd())),end=Math.max(start,Math.max(input.getSelectionStart(),input.getSelectionEnd()));
        LinearLayout content=column();Spinner category=new Spinner(this);category.setContentDescription("表情分类");
        ArrayAdapter<String> choices=new ArrayAdapter<String>(this,android.R.layout.simple_spinner_item,new String[]{"笑脸与心情","手势与人物","爱心与庆祝","动物与自然","食物与饮料","活动与物品"}) {
            @Override public View getView(int position,View convert,ViewGroup parent){TextView view=(TextView)super.getView(position,convert,parent);view.setTextColor(INK);return view;}
            @Override public View getDropDownView(int position,View convert,ViewGroup parent){TextView view=(TextView)super.getDropDownView(position,convert,parent);view.setTextColor(INK);view.setBackgroundColor(SURFACE);return view;}
        };choices.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);category.setAdapter(choices);content.addView(category,new LinearLayout.LayoutParams(-1,dp(48)));
        GridLayout grid=new GridLayout(this);grid.setContentDescription("表情网格");int columns=Math.max(4,Math.min(8,(getResources().getDisplayMetrics().widthPixels-dp(52))/dp(48)));grid.setColumnCount(columns);content.addView(grid,new LinearLayout.LayoutParams(-1,-2));
        Dialog dialog=sheet("表情",content,null,null);
        category.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener(){
            public void onNothingSelected(AdapterView<?> parent){}
            public void onItemSelected(AdapterView<?> parent,View view,int position,long id){
                grid.removeAllViews();int index=0;
                for(String emoji:EMOJI_GROUPS[position].split(" ")) {
                    Button pick=button(emoji,false);pick.setTextSize(24);pick.setContentDescription(emoji);pick.setMinWidth(0);pick.setMinimumWidth(0);pick.setPadding(0,dp(4),0,dp(4));
                    GridLayout.LayoutParams p=new GridLayout.LayoutParams(GridLayout.spec(index/columns),GridLayout.spec(index%columns,1f));p.width=0;p.height=dp(48);grid.addView(pick,p);index++;
                    pick.setOnClickListener(v->{dialog.dismiss();if(editor!=input||editorGeneration!=generation||!selected.equals(peer))return;if(insertEmoji(input,emoji,start,end))input.requestFocus();else error("输入已满，未插入表情");});
                }
            }
        });
    }
    static boolean insertEmoji(EditText input,String emoji,int start,int end) {
        int length=input.length();start=Math.max(0,Math.min(start,length));end=Math.max(start,Math.min(end,length));
        if(length-(end-start)+emoji.length()>18000)return false;
        input.getText().replace(start,end,emoji);input.setSelection(start+emoji.length());return true;
    }
    private ChatStore.Conversation conversation(String id) { if(service!=null) for(ChatStore.Conversation item:service.conversations()) if(item.peer.id().equals(id)) return item; return null; }
    private void open(String peer) { saveDraft(); selected=peer; if(service!=null) service.active(peer); renderPage(); }
    private void saveDraft() {
        if(isDestroyed() || editor==null || selected.isEmpty()) return;
        String text=editor.getText().toString();
        // 每次编辑即时保存；onStop 不再把已被服务清除的旧输入重新写回。
        if(!text.equals(editorSavedText)) { drafts.save(selected,text); editorSavedText=text; }
    }
    @Override public void changed() {
        if(root==null || rendering) return; rendering=true;
        try {
            if(selected.isEmpty()) {
                subtitle.setText(service==null?"":service.online()?service.conversations().stream().filter(c->c.peer.online).count()+" 人在线":service.status());
                if(adapter!=null) adapter.refresh();
                if(connect!=null) connect.setText(service!=null&&service.online()?"刷新":"连接");
                if(connectionBar!=null) connectionBar.setVisibility(service!=null&&service.online()?View.GONE:View.VISIBLE);
                if(empty!=null) empty.setText(search!=null&&!search.getText().toString().isEmpty()?"没有结果":"暂无会话");
            } else {
                String saved=drafts.read(selected);
                if(editor!=null && !editor.getText().toString().equals(saved)) { editorSavedText=saved; editor.setText(saved); }
                ChatStore.Conversation item=conversation(selected);
                title.setText(item==null?"聊天":item.displayName());
                if(chatAvatar!=null) bindAvatar(chatAvatar,selected,item==null?"?":item.displayName(),item!=null&&item.peer.online);
                subtitle.setText(item==null?"正在读取会话…":(item.peer.online?"在线":"离线")+" · "+item.peer.endpoint.getAddress().getHostAddress());
                if(service!=null) {
                    transferStatus.setText(service.transferStatus().isEmpty()?"":service.transferStatus()+ (service.busy()?" · 点击取消":""));
                    transferStatus.setVisibility(service.transferStatus().isEmpty()?View.GONE:View.VISIBLE);
                    String request=selected; LinearLayout target=messages; ChatService source=service; long version=++historyRequestVersion;
                    // 同一联系人也可能已切出再切回；同时校验页面实例和请求版本，不能只比较 ID。
                    // 只拒绝已应用结果之前的回调，不因还有更新请求排队而让当前页面一直空白。
                    source.history(request,rows->{ if(visible&&!isDestroyed()&&!isFinishing()&&service==source&&selected.equals(request)&&messages==target&&version>=historyAppliedVersion) { historyAppliedVersion=version;renderMessages(rows); } });
                }
            }
        } finally { rendering=false; }
    }
    private void renderMessages(List<LanNode.Message> rows) {
        StringBuilder fingerprint=new StringBuilder(selected);
        ChatStore.Conversation conversation=conversation(selected);
        for(LanNode.Message m:rows) { fingerprint.append(m.number).append(m.outgoing).append(m.state); if(conversation!=null) for(int i=0;i<m.files.size();i++) { File local=service.receivedFile(conversation.peer,m.number,m.files.get(i)); fingerprint.append(m.outgoing?outgoingUri(m,i):local.isFile()).append(m.outgoing?null:receivedStorage.saved(local)); } }
        boolean newOutgoing=!historySignature.isEmpty()&&rows.stream().anyMatch(m->m.outgoing&&!displayedOutgoing.contains(m.number));
        if(fingerprint.toString().equals(historySignature)) { transfersChanged(); if(forceLatest) {forceLatest=false;scrollToLatest();} return; }
        fileProgressViews.clear(); progressPeer=selected;
        displayedOutgoing.clear(); for(LanNode.Message m:rows) if(m.outgoing) displayedOutgoing.add(m.number);
        int previousY=scroll.getScrollY();
        boolean bottom=scroll.getChildAt(0).getHeight()-previousY-scroll.getHeight()<dp(100); boolean first=historySignature.isEmpty(); historySignature=fingerprint.toString(); messages.removeAllViews();
        int maxWidth=(int)(getResources().getDisplayMetrics().widthPixels*.79f);
        String lastDate=""; int previewCount=0;
        for(LanNode.Message m:rows) {
            String date=new SimpleDateFormat("M月d日",Locale.CHINA).format(new Date(m.time));
            if(!date.equals(lastDate)) { TextView day=text(date,11,MUTED); day.setGravity(Gravity.CENTER); LinearLayout.LayoutParams d=new LinearLayout.LayoutParams(-1,dp(36)); messages.addView(day,d); lastDate=date; }
            LinearLayout bubble=column(); bubble.setBackground(new BubbleDrawable(m.outgoing?BUBBLE:SURFACE,getResources().getDisplayMetrics().density,m.outgoing));
            bubble.setPadding(dp(m.outgoing?14:21),dp(8),dp(m.outgoing?21:14),dp(6));
            LinearLayout.LayoutParams bp=new LinearLayout.LayoutParams(-2,-2); bp.gravity=m.outgoing?Gravity.END:Gravity.START; bp.bottomMargin=dp(9); messages.addView(bubble,bp);
            if(!m.text.isEmpty()) { TextView content=text(m.text,messageSize(),INK); selectable(content); MessageLinks.apply(content,BLUE); content.setMaxWidth(maxWidth-dp(35)); content.setLineSpacing(dp(3),1); bubble.addView(content); }
            for(int fileIndex=0;fileIndex<m.files.size();fileIndex++) {
                Protocol.Attachment f=m.files.get(fileIndex);
                TextView attachment=text("▤  "+f.name+"\n"+size(f.size),14,BLUE); attachment.setMaxWidth(maxWidth-dp(35)); attachment.setPadding(0,dp(3),0,dp(3)); bubble.addView(attachment); attachment.setOnLongClickListener(v->{ copyText("文件名",f.name); return true; });
                if(m.outgoing) {
                    Uri sentUri=outgoingUri(m,fileIndex);
                    if(sentUri!=null&&MediaPreview.supported(f.name)&&previewCount++<24)
                        bubble.addView(media.thumbnail(sentUri,f.name,Math.min(dp(260),maxWidth-dp(35)),()->openFile(sentUri,f.name,false),()->sentFileActions(sentUri,f.name)),bubble.getChildCount()-1);
                    attachment.setContentDescription(f.name+(sentUri==null?"，发送副本已清理":"，文件操作"));
                    attachment.setOnClickListener(v->{if(sentUri!=null)sentFileActions(sentUri,f.name);else error("发送副本已清理，请重新选择原文件");});
                    attachment.setOnLongClickListener(v->{sentFileActions(sentUri,f.name);return true;});
                } else if(conversation!=null) {
                    File saved=service.receivedFile(conversation.peer,m.number,f);
                    boolean available=saved.isFile()||receivedStorage.saved(saved)!=null;
                    if(available&&MediaPreview.supported(f.name)&&previewCount++<24) {
                        Uri mediaUri=saved.isFile()?ReceivedFileProvider.uri(this,saved,f.name):receivedStorage.saved(saved);
                        bubble.addView(media.thumbnail(mediaUri,f.name,Math.min(dp(260),maxWidth-dp(35)),()->openFile(mediaUri,f.name,false),()->fileActions(conversation.peer,m.number,f)),bubble.getChildCount()-1);
                    }
                    attachment.setContentDescription(f.name+(available?"，文件操作":"，接收文件"));
                    attachment.setOnClickListener(v->{ if(available) fileActions(conversation.peer,m.number,f); else fileInvite(conversation.peer,m.number,f); });
                    attachment.setOnLongClickListener(v->{ fileActions(conversation.peer,m.number,f); return true; });
                }
                addFileProgress(bubble,attachment,m,f,conversation,maxWidth);
            }
            if(m.outgoing&&(m.state.contains("未确认")||m.state.equals("发送失败"))) {
                TextView retry=text("发送未确认 · 点击重试",13,BLUE);retry.setPadding(0,dp(8),0,dp(8));bubble.addView(retry);
                retry.setOnClickListener(v->{
                    if(!m.files.isEmpty()){error("文件邀请重发请重新选择原文件，避免发送已过期的缓存");return;}
                    confirm("重新发送","对方可能已收到但确认丢失，重发可能产生重复消息。","重新发送",()->{
                        if(service==null||conversation==null)return;retry.setEnabled(false);
                        service.retryMessage(conversation.peer,m,success->{if(isDestroyed())return;retry.setEnabled(!success);if(success&&selected.equals(conversation.peer.id())){forceLatest=true;changed();}});
                    });
                });
            }
            TextView stamp=text(new SimpleDateFormat("HH:mm",Locale.CHINA).format(new Date(m.time))+(m.outgoing?"  "+m.state:""),10,MUTED); stamp.setMaxWidth(maxWidth-dp(35)); stamp.setGravity(Gravity.END); stamp.setPadding(0,dp(6),0,0); bubble.addView(stamp,new LinearLayout.LayoutParams(-1,-2));
            final LanNode.Peer source=conversation==null?null:conversation.peer;
            bubble.setOnLongClickListener(v->{ if(source!=null) forward(m,source); return true; });
            stamp.append("  ···"); stamp.setContentDescription("消息操作：复制、转发"); stamp.setMinHeight(dp(m.files.isEmpty()?40:32));
            stamp.setOnClickListener(v->{if(source!=null) messageActions(m,source);});
        }
        boolean latest=first||bottom||newOutgoing||forceLatest; forceLatest=false;
        positionBeforeDraw(latest,previousY);
    }
    private void scrollToLatest() {
        positionBeforeDraw(true,0);
    }
    @Override public void transfersChanged() {
        // 进度只更新当前卡片，不重新查询历史/创建全部气泡，也不改变用户的阅读位置。
        if(!visible||service==null||!selected.equals(progressPeer))return;
        for(Runnable update:new ArrayList<>(fileProgressViews))update.run();
    }
    private void addFileProgress(LinearLayout bubble,TextView attachment,LanNode.Message message,Protocol.Attachment file,ChatStore.Conversation conversation,int maxWidth) {
        String peer=selected;ChatService source=service;
        TextView label=text("",12,MUTED);label.setMaxLines(2);label.setMinHeight(dp(22));label.setEllipsize(android.text.TextUtils.TruncateAt.END);
        int width=Math.min(dp(260),maxWidth-dp(35));bubble.addView(label,new LinearLayout.LayoutParams(width,-2));
        ProgressBar bar=new ProgressBar(this,null,android.R.attr.progressBarStyleHorizontal);bar.setMax(100);bar.setIndeterminate(false);
        bar.setProgressTintList(android.content.res.ColorStateList.valueOf(BLUE));bar.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(PALE));
        LinearLayout.LayoutParams bp=new LinearLayout.LayoutParams(width,dp(5));bp.topMargin=dp(2);bp.bottomMargin=dp(5);bubble.addView(bar,bp);
        Runnable update=()->{
            if(service!=source||source==null||!selected.equals(peer))return;
            ChatService.FileProgress progress=source.progress(peer,message.number,file.id,message.outgoing);
            boolean saved=false;
            if(!message.outgoing&&conversation!=null) {File local=source.receivedFile(conversation.peer,message.number,file);saved=local.isFile()||receivedStorage.saved(local)!=null;}
            int percent=progress!=null?progress.percent:saved?100:0;
            String state=progress!=null?progress.state:message.outgoing?(source.offering(peer,message.number,file.id)?"等待对方接收":"历史邀请 · 无活动传输"):saved?"已保存":"待接收 · 点击接收";
            label.setText(percent+"% · "+state);bar.setProgress(percent);
            bar.setContentDescription(file.name+"，"+percent+"%，"+state);
        };
        label.setOnClickListener(v->{if(service!=source||source==null||!selected.equals(peer))return;ChatService.FileProgress p=source.progress(peer,message.number,file.id,false);if(!message.outgoing&&p!=null&&p.task!=null)source.cancelFile(peer,message.number,file.id);else attachment.performClick();});
        fileProgressViews.add(update);update.run();
    }
    private long scrollPositionVersion;
    private void positionBeforeDraw(boolean latest,int previousY) {
        final ScrollView target=scroll; final String peer=selected;
        if(target==null) return;
        final long version=++scrollPositionVersion;
        // post 不保证在布局后执行；在首帧绘制前定位，避免先展示顶部再跳到末尾。
        target.getViewTreeObserver().addOnPreDrawListener(new ViewTreeObserver.OnPreDrawListener() {
            @Override public boolean onPreDraw() {
                if(target.getViewTreeObserver().isAlive()) target.getViewTreeObserver().removeOnPreDrawListener(this);
                if(!isDestroyed()&&scroll==target&&selected.equals(peer)&&version==scrollPositionVersion)
                    target.scrollTo(0,latest?target.getChildAt(0).getHeight():previousY);
                return true;
            }
        });
        target.invalidate();
    }
    private void forward(LanNode.Message message,LanNode.Peer source) {
        if(service==null||!service.online()) {error("请先连接局域网");return;}
        LinearLayout options=column();
        TextView hint=text("选择联系人，转发为一条新消息；不会清空草稿。",13,MUTED);options.addView(hint);
        final boolean[] submitted={false};
        final Dialog[] chooser={null};
        for(ChatStore.Conversation target:service.conversations()) {
            if(!target.peer.online)continue;
            Button pick=button(target.peer.name+" · "+target.peer.id(),false);options.addView(pick,new LinearLayout.LayoutParams(-1,-2));
            pick.setOnClickListener(v->{
                if(submitted[0]||service==null)return;
                confirm("确认转发","发送给 "+target.peer.name+"？","转发",()->{
                    if(submitted[0]||service==null)return;submitted[0]=true;
                    hint.setText("正在转发，请稍候…");for(int i=0;i<options.getChildCount();i++)if(options.getChildAt(i) instanceof Button)options.getChildAt(i).setEnabled(false);
                    service.forward(source,message,target.peer,success->{
                        if(isDestroyed()||isFinishing())return;
                        submitted[0]=success;
                        if(success){if(chooser[0]!=null)chooser[0].dismiss();error("已转发");if(selected.equals(target.peer.id())){forceLatest=true;changed();}}
                        else {hint.setText("转发未完成，请检查附件或连接后重试。");for(int i=0;i<options.getChildCount();i++)if(options.getChildAt(i) instanceof Button)options.getChildAt(i).setEnabled(true);}
                    });
                });
            });
        }
        if(options.getChildCount()==1)options.addView(text("没有在线联系人",14,MUTED));
        chooser[0]=sheet("转发到",options,"关闭",dialog->dialog.dismiss());
    }
    private void messageActions(LanNode.Message message,LanNode.Peer source){
        LinearLayout options=column();Dialog dialog=sheet("消息",options,null,null);
        if(!message.text.isEmpty())fileAction(options,dialog,"复制消息",()->copyText("消息",message.text));
        fileAction(options,dialog,"转发…",()->forward(message,source));
    }
    private void send() {
        ChatStore.Conversation current=conversation(selected); if(service==null||current==null||sending) return;
        String value=editor.getText().toString(); if(value.trim().isEmpty()) return;
        sending=true; sendButton.setEnabled(false); String peer=selected; saveDraft();
        service.send(current.peer,value,success->{
            if(isDestroyed() || isFinishing()) return;
            sending=false; if(sendButton!=null) sendButton.setEnabled(true);
            if(success && selected.equals(peer)) { forceLatest=true; changed(); }
        });
    }
    private void connect() {
        if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED) requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},10);
        try { startForegroundService(new Intent(this,ChatService.class)); } catch(RuntimeException e) { error("无法启动连接："+e.getMessage()); }
    }
    private void restoreConnection() {
        if(!visible || service==null || restoreAttempted) return;
        restoreAttempted=true;
        if(!ChatService.shouldRestoreConnection(this) || service.connectionActive()) return;
        // 只在用户打开页面后恢复，不开机自启，不重复弹通知权限。
        try { startForegroundService(new Intent(this,ChatService.class).setAction("restore")); }
        catch(RuntimeException e) { error("自动连接失败，可在菜单中重试："+e.getMessage()); }
    }
    private void showMenu(View anchor) {
        LinearLayout options=column(); Dialog dialog=sheet("飞Q",options,null,null);
        String[] labels={"刷新","添加联系人","已移除的联系人","设置","关于",service!=null&&service.canDisconnect()?"断开":"连接"};
        for(String label:labels) {
            fileAction(options,dialog,label,()->{ if(label.equals("已移除的联系人")) hiddenConversations(); else if(label.equals("设置")) settings(); else if(label.equals("添加联系人")) addPeer(); else if(label.equals("刷新")) { if(service!=null) service.refresh(); } else if(label.equals("断开")) { if(service!=null) service.disconnect(); } else if(label.equals("关于")) sheet("飞Q "+BuildConfig.VERSION_NAME,text("独立局域网客户端\n非飞秋或 Telegram 官方产品\n\n基础协议为明文，请仅用于可信局域网。",15,MUTED),"确定",Dialog::dismiss); else connect(); });
        }
    }
    private void hiddenConversations() {
        LinearLayout box=column();Dialog dialog=sheet("已移除的联系人",box,null,null);
        if(service==null)return;
        if(service.hiddenConversations().isEmpty())box.addView(text("没有已移除的联系人",14,MUTED));
        for(ChatStore.Conversation c:service.hiddenConversations())fileAction(box,dialog,"恢复 · "+c.displayName(),()->{if(service!=null)service.removeConversation(c.peer.id(),false,false,success->{});});
    }
    private void conversationMenu(ChatStore.Conversation c) {
        LinearLayout options=column();Dialog menu=sheet(c.displayName(),options,null,null);
        fileAction(options,menu,"设置备注",()->{
            LinearLayout box=column();EditText note=formField(box,"仅本机显示，留空恢复昵称",c.note==null?"":c.note);note.setFilters(new InputFilter[]{new InputFilter.LengthFilter(64)});
            sheet("联系人备注",box,"保存",dialog->{if(service!=null)service.editConversation(c.peer.id(),note.getText().toString(),c.pinned);dialog.dismiss();});
        });
        fileAction(options,menu,c.pinned?"取消置顶":"置顶会话",()->{if(service!=null)service.editConversation(c.peer.id(),c.note,!c.pinned);});
        fileAction(options,menu,"清空聊天记录",()->deleteConversation(c,true));
        fileAction(options,menu,"删除联系人",()->deleteConversation(c,false));
    }
    private void deleteConversation(ChatStore.Conversation c,boolean clearOnly) {
        LinearLayout box=column();TextView hint=text("仅影响本机，不删除已接收的文件。删除列表不是屏蔽，新消息会恢复会话入口。",14,MUTED);box.addView(hint);
        Switch clear=settingSwitch("同时删除聊天记录",clearOnly);clear.setEnabled(!clearOnly);box.addView(clear);
        sheet(clearOnly?"清空聊天记录":"删除联系人",box,"确认删除",dialog->{
            if(service==null)return;String id=c.peer.id();
            service.removeConversation(id,clear.isChecked(),!clearOnly,success->{if(!success||isDestroyed())return;dialog.dismiss();if(selected.equals(id)){if(clearOnly){historySignature="";changed();}else open("");}});
        });
    }
    private void addPeer() {
        if(service==null||!service.online()) { error("请先连接"); return; }
        LinearLayout content=column(); EditText input=formField(content,"IP 地址",""); input.setHint("192.168.1.100:2425"); input.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_VARIATION_URI);
        sheet("添加联系人",content,"添加",dialog->{ try { LanNode.endpoint(input.getText().toString()); service.probe(input.getText().toString()); dialog.dismiss(); } catch(Exception e) { input.setError(e.getMessage()); } });
    }
    private void settings() {
        SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE); LinearLayout box=column();
        settingHeading(box,"个人资料与连接");
        EditText name=formField(box,"昵称",prefs.getString("name",Build.MODEL)), group=formField(box,"部门",prefs.getString("group","我的局域网")), port=formField(box,"端口",String.format(Locale.ROOT,"%d",prefs.getInt("port",2425)));
        port.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        Switch autoConnect=settingSwitch("打开应用时恢复连接",prefs.getBoolean("auto_connect",true)); box.addView(autoConnect,new LinearLayout.LayoutParams(-1,-2));
        TextView reconnectHint=text("曾连接后，下次打开自动连接并查找历史联系人。主动断开后保持离线，需手动连接；不会开机自启。",12,MUTED); box.addView(reconnectHint);
        settingHeading(box,"文件接收");
        Switch automatic=settingSwitch("自动接收文件",receivedStorage.automatic()); automatic.setContentDescription("自动接收文件"); box.addView(automatic,new LinearLayout.LayoutParams(-1,-2));
        TextView hint=text("开启后无需逐次确认，不会自动打开文件。仅用于可信局域网。\n目录选择立即生效，其他选项点击保存。",12,MUTED);hint.setLineSpacing(dp(3),1); box.addView(hint);
        folderLabel=text(receivedStorage.folderLabel(),13,MUTED); folderLabel.setPadding(0,dp(12),0,dp(12)); folderLabel.setContentDescription("文件保存目录"); box.addView(folderLabel);
        Button choose=button("选择保存文件夹",false); box.addView(choose,new LinearLayout.LayoutParams(-1,-2)); choose.setOnClickListener(v->chooseFolder());
        Button reset=button("恢复应用内保存",false); LinearLayout.LayoutParams rp=new LinearLayout.LayoutParams(-1,-2); rp.topMargin=dp(12); box.addView(reset,rp); reset.setOnClickListener(v->{ receivedStorage.useInternal(); folderLabel.setText(receivedStorage.folderLabel()); if(service!=null) service.receiveSettingsChanged(); });
        settingHeading(box,"外观");
        Switch dark=settingSwitch("夜间模式",prefs.getBoolean("dark_mode",false));box.addView(dark,new LinearLayout.LayoutParams(-1,-2));
        RadioGroup accent=settingChoices(3);accent.setContentDescription("主题颜色");
        String selectedAccent=prefs.getString("accent","blue");String[] keys={"blue","green","purple"},names={"经典蓝","青绿色","紫罗兰"};
        for(int i=0;i<keys.length;i++){RadioButton choice=new RadioButton(this);choice.setId(View.generateViewId());choice.setTag(keys[i]);choice.setText(names[i]);choice.setTextSize(15);choice.setTextColor(INK);choice.setButtonTintList(new android.content.res.ColorStateList(new int[][]{new int[]{android.R.attr.state_checked},new int[]{}},new int[]{BLUE,MUTED}));choice.setMinHeight(dp(48));choice.setButtonDrawable(null);choice.setGravity(Gravity.CENTER);choice.setPadding(dp(4),0,dp(4),0);accent.addView(choice,choiceSpacing(i,3));if(keys[i].equals(selectedAccent))accent.check(choice.getId());}
        accent.setOnCheckedChangeListener((groupView,checked)->{for(int i=0;i<groupView.getChildCount();i++){View choice=groupView.getChildAt(i);choice.setBackground(shape(choice.getId()==checked?BUBBLE:SURFACE,CONTROL_RADIUS));}});
        for(int i=0;i<accent.getChildCount();i++){View choice=accent.getChildAt(i);choice.setBackground(shape(choice.getId()==accent.getCheckedRadioButtonId()?BUBBLE:SURFACE,CONTROL_RADIUS));}
        box.addView(accent,settingSpacing(8,20));
        LinearLayout preview=column();preview.setPadding(dp(14),dp(12),dp(14),dp(12));
        TextView sampleIn=text("今天也保持联系",messageSize(),INK),sampleOut=text("消息与输入字号预览",messageSize(),INK);
        sampleIn.setPadding(dp(12),dp(8),dp(12),dp(8));sampleOut.setPadding(dp(12),dp(8),dp(12),dp(8));
        LinearLayout.LayoutParams inParams=new LinearLayout.LayoutParams(-2,-2),outParams=new LinearLayout.LayoutParams(-2,-2);outParams.gravity=Gravity.END;outParams.topMargin=dp(8);preview.addView(sampleIn,inParams);preview.addView(sampleOut,outParams);box.addView(preview,settingSpacing(0,16));
        Runnable updatePreview=()->{View selectedColor=accent.findViewById(accent.getCheckedRadioButtonId());String key=selectedColor==null?"blue":selectedColor.getTag().toString();int blue=key.equals("green")?0xff229974:key.equals("purple")?0xff8b67d7:0xff3390ec;int surface=key.equals("green")?(dark.isChecked()?0xff172d29:0xfff4faf4):key.equals("purple")?(dark.isChecked()?0xff292338:0xfffcf7fc):dark.isChecked()?0xff1e2731:Color.WHITE,ink=dark.isChecked()?0xffe6ebf1:0xff17212b;preview.setBackground(shape(blend(blue,surface,.13f),CONTROL_RADIUS));sampleIn.setBackground(shape(surface,CONTROL_RADIUS));sampleOut.setBackground(shape(blend(blue,surface,dark.isChecked()?.30f:.17f),CONTROL_RADIUS));sampleIn.setTextColor(ink);sampleOut.setTextColor(ink);};
        dark.setOnCheckedChangeListener((buttonView,checked)->updatePreview.run());
        for(int i=0;i<accent.getChildCount();i++)accent.getChildAt(i).setOnClickListener(v->updatePreview.run());updatePreview.run();
        TextView sizeCaption=text("聊天字号 · "+messageSize()+" sp",14,INK);sizeCaption.setPadding(0,dp(14),0,0);box.addView(sizeCaption);
        SeekBar fontSize=new SeekBar(this);fontSize.setContentDescription("聊天字号");fontSize.setMax(10);fontSize.setProgress(messageSize()-14);box.addView(fontSize,new LinearLayout.LayoutParams(-1,dp(48)));
        fontSize.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){public void onStartTrackingTouch(SeekBar bar){}public void onStopTrackingTouch(SeekBar bar){}public void onProgressChanged(SeekBar bar,int value,boolean user){sizeCaption.setText("聊天字号 · "+(value+14)+" sp");sampleIn.setTextSize(value+14);sampleOut.setTextSize(value+14);}});
        TextView wallpaperHeading=text("聊天背景",14,INK);wallpaperHeading.setPadding(0,dp(12),0,dp(8));box.addView(wallpaperHeading);
        RadioGroup backgrounds=settingChoices(4);wallpaperChoices=backgrounds;String[] backgroundKeys={"none","orbits","lines","image"},backgroundNames={"纯色","星环","流线","我的图片"};
        for(int i=0;i<4;i++){RadioButton choice=new RadioButton(this);choice.setId(View.generateViewId());choice.setTag(backgroundKeys[i]);choice.setText(backgroundNames[i]);choice.setTextColor(INK);choice.setTextSize(14);choice.setMinHeight(dp(48));choice.setButtonDrawable(null);choice.setGravity(Gravity.CENTER);choice.setPadding(dp(2),0,dp(2),0);backgrounds.addView(choice,choiceSpacing(i,4));if(backgroundKeys[i].equals(prefs.getString("chat_background","orbits")))backgrounds.check(choice.getId());}box.addView(backgrounds,settingSpacing(0,12));
        backgrounds.setOnCheckedChangeListener((groupView,checked)->{for(int i=0;i<groupView.getChildCount();i++){View choice=groupView.getChildAt(i);choice.setBackground(shape(choice.getId()==checked?BUBBLE:PALE,CONTROL_RADIUS));}});
        for(int i=0;i<backgrounds.getChildCount();i++){View choice=backgrounds.getChildAt(i);choice.setBackground(shape(choice.getId()==backgrounds.getCheckedRadioButtonId()?BUBBLE:PALE,CONTROL_RADIUS));}
        String[] presetNames={"晴空 · 经典","薄荷 · 日间","深海 · 夜间","暮紫 · 夜间"};
        for(int lineIndex=0;lineIndex<2;lineIndex++){LinearLayout presetRow=row();presetRow.setBaselineAligned(false);presetRow.setGravity(Gravity.TOP);box.addView(presetRow,box.indexOfChild(preview)+1+lineIndex,new LinearLayout.LayoutParams(-1,-2));for(int columnIndex=0;columnIndex<2;columnIndex++){int preset=lineIndex*2+columnIndex;Button pick=button(presetNames[preset],false);LinearLayout.LayoutParams pp=weight();pp.setMargins(columnIndex==0?0:dp(6),0,columnIndex==0?dp(6):0,dp(12));presetRow.addView(pick,pp);pick.setOnClickListener(v->{dark.setChecked(preset>=2);accent.check(accent.getChildAt(preset==1?1:preset==3?2:0).getId());backgrounds.check(backgrounds.getChildAt(preset==1?2:1).getId());updatePreview.run();});}}
        Button chooseBackground=button("选择背景图片…",false);box.addView(chooseBackground,settingSpacing(0,4));chooseBackground.setOnClickListener(v->{try{startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("image/*").addCategory(Intent.CATEGORY_OPENABLE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),23);}catch(ActivityNotFoundException e){error("系统没有可用的图片选择器");}});
        TextView backgroundHint=text("图片授权立即保存；其他外观选项点击保存后生效。支持最长边抽样至 1920 像素，不影响原图。",12,MUTED);backgroundHint.setPadding(0,dp(8),0,0);box.addView(backgroundHint);
        settingHeading(box,"存储管理");
        Button clear=button("清理已接收文件",false); LinearLayout.LayoutParams cp=new LinearLayout.LayoutParams(-1,-2); cp.topMargin=dp(16); box.addView(clear,cp); clear.setOnClickListener(v->confirm("清理文件","删除应用内已接收文件？已导出的副本不受影响。","清理",()->{ if(service!=null) service.clearFiles(); }));
        Button clearOffers=button("清理发送缓存",false); LinearLayout.LayoutParams op=new LinearLayout.LayoutParams(-1,-2); op.topMargin=dp(10); box.addView(clearOffers,op);
        clear.setGravity(Gravity.START|Gravity.CENTER_VERTICAL);clearOffers.setGravity(Gravity.START|Gravity.CENTER_VERTICAL);
        TextView limits=text("文件不设固定大小上限，受设备可用空间限制并保留 16 MiB 余量。网络失败后重新接收可续传；主动取消会清理断点。另存到目录仍需内部暂存空间。",12,MUTED); limits.setPadding(0,dp(12),0,0); box.addView(limits);
        clearOffers.setOnClickListener(v->confirm("清理发送缓存","撤销尚未接收的文件邀请并删除暂存副本？原文件不受影响。","清理",()->{ if(service!=null) service.clearOffers(); }));
        settingHeading(box,"故障诊断");
        Switch diagnosticSwitch=settingSwitch("记录故障诊断",prefs.getBoolean("diagnostics",false));box.addView(diagnosticSwitch,new LinearLayout.LayoutParams(-1,-2));
        box.addView(text("默认关闭，保存后生效。仅记 UTC 时间和事件码，不记聊天、身份、IP、URI 和路径；最多 128 KiB。关闭后保留，可手动清除。",12,MUTED));
        Diagnostics journal=new Diagnostics(this);
        Button viewLog=button("查看诊断记录",false);box.addView(viewLog,settingSpacing(16,12));
        viewLog.setOnClickListener(v->{try{TextView log=text(journal.read(),12,INK);selectable(log);sheet("诊断记录（可选择复制）",log,"确定",Dialog::dismiss);}catch(Exception e){error("诊断读取失败："+e.getMessage());}});
        Button clearLog=button("清除诊断记录",false);box.addView(clearLog,settingSpacing(0,4));clearLog.setOnClickListener(v->{try{journal.clear();error("诊断记录已清除");}catch(Exception e){error("诊断清除失败："+e.getMessage());}});
        sheet("设置",box,"保存",dialog->{
            String n=name.getText().toString().trim(), g=group.getText().toString().trim(); int p;
            if(n.isEmpty()||n.length()>64) { name.setError("昵称须为 1–64 个字符"); return; } if(g.length()>64) { group.setError("分组最多 64 个字符"); return; }
            try { p=Integer.parseInt(port.getText().toString()); if(p<1024||p>65535) throw new NumberFormatException(); } catch(NumberFormatException e) { port.setError("端口须为 1024–65535"); return; }
            View color=accent.findViewById(accent.getCheckedRadioButtonId());
            View backdrop=backgrounds.findViewById(backgrounds.getCheckedRadioButtonId());String backdropKey=backdrop==null?"orbits":backdrop.getTag().toString();
            if(backdropKey.equals("image")&&prefs.getString("background_image","").isEmpty()){error("请先选择背景图片");return;}
            prefs.edit().putString("name",n).putString("group",g).putInt("port",p).putBoolean("auto_connect",autoConnect.isChecked()).putBoolean("diagnostics",diagnosticSwitch.isChecked()).putBoolean(ReceivedStorage.AUTO,automatic.isChecked()).putBoolean("dark_mode",dark.isChecked()).putString("accent",color==null?"blue":color.getTag().toString()).putInt("chat_font_size",fontSize.getProgress()+14).putString("chat_background",backdropKey).apply(); if(service!=null) service.receiveSettingsChanged(); dialog.dismiss(); saveDraft(); renderPage(); error("已保存；主题和接收选项立即生效，昵称和端口下次连接生效");
        });
    }
    private Button button(String label,boolean primary) {
        Button button=new Button(this); button.setText(label); button.setTextSize(15); button.setAllCaps(false); button.setTextColor(primary?Color.WHITE:INK); button.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(blend(INK,primary?BLUE:PALE,.08f)),shape(primary?BLUE:PALE,CONTROL_RADIUS),null)); button.setStateListAnimator(null); button.setMinHeight(dp(48)); button.setPadding(dp(16),dp(10),dp(16),dp(10)); return button;
    }
    private LinearLayout.LayoutParams settingSpacing(int top,int bottom) {
        LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2);p.setMargins(0,dp(top),0,dp(bottom));return p;
    }
    private RadioGroup.LayoutParams choiceSpacing(int index,int count) {
        RadioGroup.LayoutParams p=new RadioGroup.LayoutParams(0,-2,1);p.setMargins(index==0?0:dp(5),0,index==count-1?0:dp(5),0);return p;
    }
    private RadioGroup settingChoices(int count) {
        return new RadioGroup(this) {
            @Override protected void onMeasure(int width,int height) {
                // 按可用宽度和实际字体缩放决定排列，不能靠缩小触摸区或裁字塞满一行。
                float scale=getResources().getConfiguration().fontScale;
                boolean vertical=View.MeasureSpec.getSize(width)<dp(count*76*Math.max(1,scale)+(count-1)*10);
                int orientation=vertical?LinearLayout.VERTICAL:LinearLayout.HORIZONTAL;
                if(getOrientation()!=orientation)setOrientation(orientation);
                for(int i=0;i<getChildCount();i++){
                    View child=getChildAt(i);RadioGroup.LayoutParams p=(RadioGroup.LayoutParams)child.getLayoutParams();
                    int w=vertical?-1:0,bottom=vertical&&i<count-1?dp(10):0,left=!vertical&&i>0?dp(5):0,right=!vertical&&i<count-1?dp(5):0;float weight=vertical?0:1;
                    if(p.width!=w||p.weight!=weight||p.leftMargin!=left||p.rightMargin!=right||p.bottomMargin!=bottom){p.width=w;p.weight=weight;p.setMargins(left,0,right,bottom);child.setLayoutParams(p);}
                }
                super.onMeasure(width,height);
            }
        };
    }
    private void settingHeading(LinearLayout box,String title){
        if(box.getChildCount()>0){View line=new View(this);line.setBackgroundColor(blend(INK,SURFACE,.09f));LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,dp(1));p.topMargin=dp(20);p.bottomMargin=dp(16);box.addView(line,p);}
        TextView heading=text(title,14,INK);heading.setTag("section:"+title);heading.setTypeface(null,Typeface.BOLD);heading.setPadding(0,dp(6),0,dp(8));box.addView(heading,new LinearLayout.LayoutParams(-1,-2));
    }
    private Switch settingSwitch(String label,boolean checked){
        Switch toggle=new Switch(this);toggle.setText(label);toggle.setContentDescription(label);toggle.setTextSize(15);toggle.setTextColor(INK);toggle.setMinHeight(dp(48));toggle.setPadding(0,dp(8),0,dp(8));toggle.setChecked(checked);
        int[][] states={new int[]{android.R.attr.state_checked},new int[]{}};
        toggle.setThumbTintList(new android.content.res.ColorStateList(states,new int[]{BLUE,blend(INK,SURFACE,.18f)}));
        toggle.setTrackTintList(new android.content.res.ColorStateList(states,new int[]{blend(BLUE,SURFACE,.3f),blend(INK,SURFACE,.12f)}));return toggle;
    }
    private void configureInput(EditText input) {
        input.setLongClickable(true);
        ActionMode.Callback actions=new ActionMode.Callback() {
            public boolean onCreateActionMode(ActionMode mode,Menu menu) { trackSelection(input,mode); prepare(menu); return true; }
            public boolean onPrepareActionMode(ActionMode mode,Menu menu) { prepare(menu); return true; }
            private void prepare(Menu menu) {
                int[] ids={android.R.id.cut,android.R.id.copy,android.R.id.paste,android.R.id.selectAll};
                String[] names={"剪切","复制","粘贴","全选"};
                boolean selection=input.getSelectionStart()>=0&&input.getSelectionEnd()!=input.getSelectionStart();
                ClipboardManager clipboard=getSystemService(ClipboardManager.class);
                for(int i=0;i<ids.length;i++) { MenuItem item=menu.findItem(ids[i]); if(item==null) item=menu.add(Menu.NONE,ids[i],i,names[i]); item.setEnabled(i<2?selection:i==2?clipboard.hasPrimaryClip():input.length()>0); }
            }
            public boolean onActionItemClicked(ActionMode mode,MenuItem item) {
                int id=item.getItemId();
                if(id==android.R.id.cut||id==android.R.id.copy||id==android.R.id.paste||id==android.R.id.selectAll) {
                    boolean handled=input.onTextContextMenuItem(id==android.R.id.paste?android.R.id.pasteAsPlainText:id);
                    if(id!=android.R.id.selectAll) mode.finish(); return handled;
                }
                return false;
            }
            public void onDestroyActionMode(ActionMode mode) { releaseSelection(mode); }
        };
        input.setCustomSelectionActionModeCallback(actions); input.setCustomInsertionActionModeCallback(actions);
    }
    private EditText formField(LinearLayout parent,String label,String value) {
        TextView title=text(label,12,MUTED); title.setPadding(dp(4),dp(10),0,dp(7)); parent.addView(title);
        EditText field=new EditText(this); configureInput(field); field.setSingleLine(); field.setTextSize(16); field.setTextColor(INK); field.setText(value); field.setPadding(dp(15),dp(10),dp(15),dp(10)); field.setBackground(shape(PALE,CONTROL_RADIUS)); field.setMinHeight(dp(48)); field.setContentDescription(label); parent.addView(field,new LinearLayout.LayoutParams(-1,-2)); return field;
    }
    private Dialog settingsPanel(View content,java.util.function.Consumer<Dialog> handler) {
        finishSelection();Dialog dialog=new Dialog(this){@Override public boolean dispatchTouchEvent(MotionEvent event){dismissSelectionOutside(event);return super.dispatchTouchEvent(event);}};dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        dialog.setOnDismissListener(d->{if(selectionOwner!=null&&selectionOwner.getRootView()==dialog.getWindow().getDecorView())finishSelection();wallpaperChoices=null;});
        LinearLayout frame=new LinearLayout(this){@Override protected void onMeasure(int width,int height){int available=root!=null&&root.getHeight()>0?root.getHeight()-root.getPaddingTop()-root.getPaddingBottom():getResources().getDisplayMetrics().heightPixels;int cap=Math.max(1,(int)(available*.9f));if(View.MeasureSpec.getMode(height)!=View.MeasureSpec.UNSPECIFIED)cap=Math.min(cap,View.MeasureSpec.getSize(height));View navigation=findViewWithTag("settings-navigation");if(navigation!=null)navigation.setVisibility(cap<dp(280)?View.GONE:View.VISIBLE);super.onMeasure(width,View.MeasureSpec.makeMeasureSpec(cap,View.MeasureSpec.EXACTLY));}};
        frame.setOrientation(LinearLayout.VERTICAL);frame.setBackground(shape(SURFACE,18));
        LinearLayout heading=row();heading.setPadding(dp(22),dp(8),dp(10),0);TextView caption=text("设置",20,INK);caption.setContentDescription("设置");caption.setTypeface(null,Typeface.BOLD);heading.addView(caption,weight());IconButton close=new IconButton("close","关闭设置",MUTED);heading.addView(close,new LinearLayout.LayoutParams(dp(48),dp(48)));close.setOnClickListener(v->dialog.dismiss());frame.addView(heading);
        ScrollView scroller=new ScrollView(this);scroller.setFillViewport(false);LinearLayout inset=column();inset.setPadding(dp(18),dp(4),dp(18),dp(18));inset.addView(content,new LinearLayout.LayoutParams(-1,-2));scroller.addView(inset);
        LinearLayout navigation=row();navigation.setTag("settings-navigation");navigation.setPadding(dp(14),dp(4),dp(14),dp(8));String[] names={"资料","外观","文件","诊断"},sections={"个人资料与连接","外观","文件接收","故障诊断"};
        for(int i=0;i<names.length;i++){final String section=sections[i];Button jump=button(names[i],false);jump.setTextSize(14);jump.setPadding(dp(4),dp(8),dp(4),dp(8));LinearLayout.LayoutParams p=weight();p.setMargins(i==0?0:dp(4),0,i==names.length-1?0:dp(4),0);navigation.addView(jump,p);jump.setOnClickListener(v->{View target=content.findViewWithTag("section:"+section);if(target!=null){int y=target.getTop();View parent=(View)target.getParent();while(parent!=scroller){y+=parent.getTop();parent=(View)parent.getParent();}scroller.scrollTo(0,y);}});}frame.addView(navigation);
        frame.addView(scroller,new LinearLayout.LayoutParams(-1,0,1));
        LinearLayout footer=column();footer.setPadding(dp(18),dp(10),dp(18),dp(14));Button done=button("保存",true);footer.addView(done,new LinearLayout.LayoutParams(-1,-2));done.setOnClickListener(v->handler.accept(dialog));frame.addView(footer);
        dialog.setContentView(frame);Window window=dialog.getWindow();if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.setGravity(Gravity.BOTTOM);window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);window.setDimAmount(.32f);window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);}
        dialog.show();if(window!=null)window.setLayout(-1,-2);return dialog;
    }
    private Dialog sheet(String name,View content,String action,java.util.function.Consumer<Dialog> handler) {
        if(name.equals("设置"))return settingsPanel(content,handler);
        finishSelection();
        Dialog dialog=new Dialog(this) {
            @Override public boolean dispatchTouchEvent(MotionEvent event) { dismissSelectionOutside(event); return super.dispatchTouchEvent(event); }
        }; dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        dialog.setOnDismissListener(d->{ if(selectionOwner!=null&&selectionOwner.getRootView()==dialog.getWindow().getDecorView()) finishSelection(); });
        LinearLayout box=column(); box.setPadding(dp(18),dp(12),dp(18),dp(22)); box.setBackground(shape(SURFACE,18));
        LinearLayout heading=row(); heading.setPadding(dp(8),0,0,dp(10)); TextView caption=text(name,18,INK); caption.setTypeface(null,Typeface.BOLD); caption.setMaxLines(2); caption.setEllipsize(TextUtils.TruncateAt.END); caption.setContentDescription(name); heading.addView(caption,weight()); IconButton close=new IconButton("close","关闭",MUTED); heading.addView(close,new LinearLayout.LayoutParams(dp(44),dp(44))); close.setOnClickListener(v->dialog.dismiss()); box.addView(heading);
        box.addView(content,new LinearLayout.LayoutParams(-1,-2));
        if(action!=null) { Button done=button(action,true); LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2); p.topMargin=dp(20); box.addView(done,p); done.setOnClickListener(v->{ if(handler!=null) handler.accept(dialog); }); }
        // 整张操作面板可滚动；遵守父窗口实际给出的高度，避免键盘、横屏或长标题把底部按钮挤到屏外。
        ScrollView scroller=new ScrollView(this) { @Override protected void onMeasure(int width,int height) {
            int visible=root!=null&&root.getHeight()>0?root.getHeight()-root.getPaddingTop()-root.getPaddingBottom():getResources().getDisplayMetrics().heightPixels;
            int cap=Math.max(1,(int)(visible*.9f));
            if(View.MeasureSpec.getMode(height)!=View.MeasureSpec.UNSPECIFIED) cap=Math.min(cap,View.MeasureSpec.getSize(height));
            super.onMeasure(width,View.MeasureSpec.makeMeasureSpec(cap,View.MeasureSpec.AT_MOST));
        } }; scroller.setFillViewport(false); scroller.addView(box);
        dialog.setContentView(scroller); Window window=dialog.getWindow(); if(window!=null) { window.setBackgroundDrawableResource(android.R.color.transparent); window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND); window.setDimAmount(.32f); window.setGravity(Gravity.BOTTOM); window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE); }
        dialog.show(); if(window!=null) window.setLayout(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT); return dialog;
    }
    private void confirm(String title,String body,String action,Runnable confirm) { TextView content=text(body,15,INK); content.setPadding(0,dp(14),0,dp(4)); sheet(title,content,action,dialog->{ dialog.dismiss(); confirm.run(); }); }
    private void copyText(String label,String value) { getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText(label,value)); error("已复制"+label); }
    private void fileAction(LinearLayout options,Dialog dialog,String label,Runnable action) {
        boolean danger=label.equals("断开")||label.startsWith("拒绝")||label.startsWith("删除")||label.startsWith("清空");
        if(danger&&options.getChildCount()>0) { View line=new View(this);line.setBackgroundColor(blend(INK,SURFACE,.09f));LinearLayout.LayoutParams separator=new LinearLayout.LayoutParams(-1,dp(1));separator.setMargins(dp(12),dp(8),dp(12),dp(8));options.addView(line,separator); }
        int color=danger?0xffd24e54:INK;
        Button item=button(label,false); item.setTextColor(color); item.setGravity(Gravity.START|Gravity.CENTER_VERTICAL); item.setMinHeight(dp(52)); item.setPadding(dp(12),dp(12),dp(14),dp(12));
        item.setCompoundDrawablesWithIntrinsicBounds(new MenuIcon(label,danger?color:MUTED),null,null,null); item.setCompoundDrawablePadding(dp(16));
        item.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),shape(SURFACE,CONTROL_RADIUS),shape(Color.WHITE,CONTROL_RADIUS)));
        LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2); p.topMargin=dp(2); options.addView(item,p); item.setOnClickListener(v->{ dialog.dismiss(); action.run(); });
    }
    private final class MenuIcon extends android.graphics.drawable.Drawable {
        private final String label; private final Paint paint=new Paint(Paint.ANTI_ALIAS_FLAG);
        MenuIcon(String label,int color) { this.label=label;paint.setColor(color);paint.setStyle(Paint.Style.STROKE);paint.setStrokeWidth(1.7f);paint.setStrokeCap(Paint.Cap.ROUND);paint.setStrokeJoin(Paint.Join.ROUND); }
        @Override public int getIntrinsicWidth() { return dp(22); }
        @Override public int getIntrinsicHeight() { return dp(22); }
        @Override public void setAlpha(int alpha) { paint.setAlpha(alpha); }
        @Override public void setColorFilter(ColorFilter filter) { paint.setColorFilter(filter); }
        @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
        @Override public void draw(Canvas c) {
            c.save();Rect b=getBounds();c.translate(b.left,b.top);c.scale(b.width()/24f,b.height()/24f);Path p=new Path();
            if(label.startsWith("复制")) { c.drawRoundRect(8,8,20,21,2,2,paint);p.moveTo(5,16);p.lineTo(3,16);p.lineTo(3,3);p.lineTo(15,3);p.lineTo(15,5);c.drawPath(p,paint); }
            else if(label.contains("转发")||label.contains("分享")) { p.moveTo(3,4);p.lineTo(21,12);p.lineTo(3,20);p.lineTo(6,12);p.close();c.drawPath(p,paint);c.drawLine(6,12,21,12,paint); }
            else if(label.contains("目录")||label.contains("位置")) { p.moveTo(3,6);p.lineTo(9,6);p.lineTo(11,9);p.lineTo(21,9);p.lineTo(21,20);p.lineTo(3,20);p.close();c.drawPath(p,paint); }
            else if(label.contains("刷新")||label.contains("重试")) { c.drawArc(4,4,20,20,40,285,false,paint);p.moveTo(15,3);p.lineTo(20,4);p.lineTo(19,9);c.drawPath(p,paint); }
            else if(label.contains("备注")){p.moveTo(4,17);p.lineTo(16,5);p.lineTo(20,9);p.lineTo(8,21);p.lineTo(3,22);p.close();c.drawPath(p,paint);}
            else if(label.contains("置顶")){p.moveTo(8,3);p.lineTo(17,3);p.lineTo(15,10);p.lineTo(20,14);p.lineTo(5,14);p.lineTo(10,10);p.close();c.drawPath(p,paint);c.drawLine(12,14,12,22,paint);}
            else if(label.contains("设置")) { c.drawCircle(12,12,7,paint);c.drawCircle(12,12,3,paint);for(int i=0;i<8;i++){double a=i*Math.PI/4;c.drawLine(12+(float)Math.cos(a)*8,12+(float)Math.sin(a)*8,12+(float)Math.cos(a)*10,12+(float)Math.sin(a)*10,paint);} }
            else if(label.contains("添加")) { c.drawLine(12,4,12,20,paint);c.drawLine(4,12,20,12,paint); }
            else if(label.contains("连接")||label.equals("断开")) { c.drawArc(4,4,20,20,-50,280,false,paint);c.drawLine(12,2,12,12,paint); }
            else if(label.startsWith("拒绝")||label.startsWith("删除")) { c.drawLine(5,5,19,19,paint);c.drawLine(5,19,19,5,paint); }
            else if(label.equals("关于")) { c.drawCircle(12,12,9,paint);c.drawLine(12,11,12,17,paint);c.drawPoint(12,7,paint); }
            else { c.drawRoundRect(5,3,19,21,2,2,paint);c.drawLine(9,11,15,11,paint);c.drawLine(9,16,15,16,paint); }
            c.restore();
        }
    }
    private Uri outgoingUri(LanNode.Message message,int index) {
        if(index<0||index>=message.localPaths.size())return null;
        File file=new File(message.localPaths.get(index));if(!file.isFile())return null;
        try {return ReceivedFileProvider.uri(this,file,message.files.get(index).name);} catch(IllegalArgumentException e) {return null;}
    }
    private void sentFileActions(Uri uri,String name) {
        LinearLayout options=column();Dialog dialog=sheet(name,options,null,null);
        if(uri!=null){fileAction(options,dialog,"打开文件",()->openFile(uri,name,false));fileAction(options,dialog,"打开方式…",()->openFileWith(uri,name,false,true));fileAction(options,dialog,"分享文件",()->openFile(uri,name,true));}
        fileAction(options,dialog,"复制文件名",()->copyText("文件名",name));
    }
    private void fileActions(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        if(service==null) return;
        File cached=service.receivedFile(peer,packet,file); Uri exported=receivedStorage.saved(cached);
        LinearLayout options=column(); Dialog dialog=sheet(file.name,options,null,null);
        if(cached.isFile()||exported!=null) {
            // 应用内副本更可靠；清理缓存后仍可使用保存目录中的历史 URI。
            Uri uri=cached.isFile()?ReceivedFileProvider.uri(this,cached,file.name):exported;
            fileAction(options,dialog,"打开文件",()->openFile(uri,file.name,false));
            fileAction(options,dialog,"打开方式…",()->openFileWith(uri,file.name,false,true));
            fileAction(options,dialog,"分享文件",()->openFile(uri,file.name,true));
            fileAction(options,dialog,"查看保存位置",()->showFileLocation(cached));
            if(cached.isFile()) {
                fileAction(options,dialog,"另存为…",()->export(cached,file.name));
                if(receivedStorage.tree()!=null) fileAction(options,dialog,"保存到设置目录 / 重试",()->{ if(service!=null) service.saveToFolder(cached,file.name); });
            }
        } else {
            fileAction(options,dialog,"接收文件",()->{ if(service!=null) service.receive(peer,packet,file); });
            fileAction(options,dialog,"拒绝这批文件",()->{ if(service!=null) service.reject(peer,packet); });
        }
        fileAction(options,dialog,"复制文件名",()->copyText("文件名",file.name));
    }
    private void openFile(Uri uri,String name,boolean share) { openFileWith(uri,name,share,false); }
    private void openFileWith(Uri uri,String name,boolean share,boolean chooser) {
        if(!share&&name.toLowerCase(Locale.ROOT).endsWith(".apk")&&!getPackageManager().canRequestPackageInstalls()){
            confirm("允许安装此来源的应用","系统尚未允许飞Q请求安装 APK。请在下一页允许此来源，返回后再次点击打开文件；安装仍需系统确认。只安装可信来源的文件。","前往系统设置",()->{try{startActivity(new Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+getPackageName())));}catch(ActivityNotFoundException e){error("系统不提供安装授权页面，请另存 APK 后使用系统文件管理器打开");}});return;
        }
        Intent intent=new Intent(share?Intent.ACTION_SEND:Intent.ACTION_VIEW).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        if(share) { intent.setType(ReceivedStorage.mime(name)); intent.putExtra(Intent.EXTRA_STREAM,uri); }
        else intent.setDataAndType(uri,ReceivedStorage.mime(name));
        intent.setClipData(ClipData.newRawUri(name,uri));
        try { startActivity(share||chooser?Intent.createChooser(intent,share?"分享文件":"选择打开方式"):intent); }
        catch(ActivityNotFoundException e) { sheet("没有可用的打开方式",text("系统没有支持此文件格式的应用（"+ReceivedStorage.mime(name)+"）。请安装支持该格式的应用，或使用另存为/分享。",15,INK),"确定",Dialog::dismiss); }
        catch(SecurityException e) { error("文件授权已失效，请重新选择保存目录或使用应用内副本"); }
    }
    private void showFileLocation(File file) {
        Uri tree=receivedStorage.savedTree(file), saved=receivedStorage.saved(file);
        LinearLayout content=column(); String location=tree==null?"应用私有接收区，其他文件管理器不能直接访问。可使用另存为导出。":DocumentsContract.getTreeDocumentId(tree);
        TextView path=text(location,15,INK); selectable(path); content.addView(path);
        Dialog dialog=sheet("保存位置",content,null,null);
        if(tree!=null) fileAction(content,dialog,"打开保存文件夹",()->{
            Uri folder=DocumentsContract.buildDocumentUriUsingTree(tree,DocumentsContract.getTreeDocumentId(tree));
            Intent intent=new Intent(Intent.ACTION_VIEW).setDataAndType(folder,DocumentsContract.Document.MIME_TYPE_DIR).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            try { startActivity(intent); }
            catch(ActivityNotFoundException e) {
                try { startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(DocumentsContract.EXTRA_INITIAL_URI,folder),23); }
                catch(ActivityNotFoundException missing) { error("系统文件管理器不支持打开此位置"); }
            } catch(SecurityException e) { error("目录权限已失效，请在设置中重新选择目录"); }
        });
        if(saved!=null) fileAction(content,dialog,"复制文件位置",()->copyText("文件位置",saved.toString()));
    }
    private void fileInvite(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        LinearLayout content=column(); TextView label=text(file.name+"\n"+size(file.size),15,INK); label.setPadding(0,dp(14),0,dp(18)); content.addView(label);
        Button reject=button("拒绝这批文件",false); content.addView(reject,new LinearLayout.LayoutParams(-1,dp(48)));
        Dialog dialog=sheet("接收文件",content,"接收",d->{ d.dismiss(); service.receive(peer,packet,file); }); reject.setOnClickListener(v->{ dialog.dismiss(); service.reject(peer,packet); });
    }
    private void chooseFolder() {
        Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION|Intent.FLAG_GRANT_PREFIX_URI_PERMISSION);
        if(receivedStorage.tree()!=null) intent.putExtra(DocumentsContract.EXTRA_INITIAL_URI,receivedStorage.tree());
        try { startActivityForResult(intent,22); } catch(ActivityNotFoundException e) { error("系统没有可用的目录选择器"); }
    }
    private void pickFile() {
        if(service==null||!service.online()) { error("请先连接局域网"); return; } if(service.busy()) { error("已有文件任务，请等待或取消"); return; }
        pickerPeer=selected; Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_ALLOW_MULTIPLE,true);
        try { startActivityForResult(intent,20); } catch(ActivityNotFoundException e) { error("系统没有可用的文件选择器"); }
    }
    private void export(File file,String name) {
        exportPath=file.getAbsolutePath(); Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,name);
        try { startActivityForResult(intent,21); } catch(ActivityNotFoundException e) { error("系统没有可用的保存选择器"); }
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data); if(result!=RESULT_OK||data==null) return;
        if(request!=20&&data.getData()==null) return;
        if(request==23) {
            try {
                Uri uri=data.getData();try(android.os.ParcelFileDescriptor fd=getContentResolver().openFileDescriptor(uri,"r")){if(fd!=null&&fd.getStatSize()>20*1024*1024)throw new java.io.IOException("请选择不超过 20 MiB 的图片");}
                Bitmap validated=ChatWallpaper.load(this,uri);validated.recycle();
                getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);
                getSharedPreferences("settings",MODE_PRIVATE).edit().putString("background_image",uri.toString()).putString("chat_background","image").apply();
                if(wallpaperChoices!=null&&wallpaperChoices.getChildCount()==4)wallpaperChoices.check(wallpaperChoices.getChildAt(3).getId());
                error("背景图片已保存；请保存外观设置以应用");
            }catch(Exception e){error("背景图片未保存："+e.getMessage());}return;
        }
        if(request==22) {
            try { receivedStorage.choose(data.getData(),data.getFlags()); if(folderLabel!=null) folderLabel.setText(receivedStorage.folderLabel()); if(service!=null) service.receiveSettingsChanged(); error("保存目录已更新，之后接收的文件会自动保存到此目录"); }
            catch(Exception e) { error("目录授权未保存："+e.getMessage()); } return;
        }
        if(request!=20&&request!=21) return;
        if(service==null) { error("连接服务尚未就绪，请重新选择文件"); return; }
        if(request==20) {
            ChatStore.Conversation peer=conversation(pickerPeer);
            try { List<Uri> files=selectedFiles(data); if(peer!=null) service.sendFiles(peer.peer,files); else error("联系人已失效，请重新选择"); }
            catch(IllegalArgumentException e) { error(e.getMessage()); }
        }
        if(request==21) { File file=new File(exportPath); if(file.isFile()&&file.getParentFile().equals(new File(getFilesDir(),"received"))) service.exportFile(file,data.getData()); else error("应用内文件已不存在"); }
    }
    static List<Uri> selectedFiles(Intent data) {
        LinkedHashSet<Uri> files=new LinkedHashSet<>(); ClipData clip=data.getClipData();
        if(clip!=null) {
            if(clip.getItemCount()>20) throw new IllegalArgumentException("每批最多选择 20 个文件");
            for(int i=0;i<clip.getItemCount();i++) files.add(clip.getItemAt(i).getUri());
        } else if(data.getData()!=null) files.add(data.getData());
        if(files.isEmpty()||files.contains(null)) throw new IllegalArgumentException("未取得有效文件，请重新选择");
        for(Uri uri:files) if(!"content".equals(uri.getScheme())) throw new IllegalArgumentException("请选择系统文件选择器授权的文件");
        return new ArrayList<>(files);
    }
    // Android 13+ 已在 onCreate 注册原生 OnBackInvokedCallback，此覆盖仅兼容旧系统。
    @android.annotation.SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed() { if(!selected.isEmpty()) open(""); else super.onBackPressed(); }
    @Override public void error(String text) { if(!isFinishing()) Toast.makeText(this,text,Toast.LENGTH_LONG).show(); }
    private static String size(long value) { if(value>=1048576) return String.format(Locale.CHINA,"%.1f MB",value/1048576.0); if(value>=1024) return String.format(Locale.CHINA,"%.1f KB",value/1024.0); return value+" B"; }
    private void bindAvatar(TextView avatar,String id,String name,boolean online) {
        avatar.setText(name.isEmpty()?"?":name.substring(0,name.offsetByCodePoints(0,1)));
        avatar.setContentDescription(name+"，"+(online?"在线":"离线"));
        if(avatar.getBackground() instanceof AvatarDrawable) ((AvatarDrawable)avatar.getBackground()).setOnline(online);
        else avatar.setBackground(new AvatarDrawable(AVATARS[Math.floorMod(id.hashCode(),AVATARS.length)],online,dp(6)));
    }
    private final class ConversationAdapter extends BaseAdapter {
        private final List<ChatStore.Conversation> items=new ArrayList<>();
        void refresh() { items.clear(); String query=search==null?"":search.getText().toString().toLowerCase(Locale.ROOT); if(service!=null) for(ChatStore.Conversation c:service.conversations()) if((c.displayName()+" "+c.peer.name+" "+c.peer.id()+" "+c.preview).toLowerCase(Locale.ROOT).contains(query)) items.add(c); notifyDataSetChanged(); }
        @Override public int getCount() { return items.size(); }
        @Override public Object getItem(int p) { return items.get(p); }
        @Override public long getItemId(int p) { return p; }
        @Override public View getView(int position,View convert,ViewGroup parent) {
            ChatStore.Conversation c=items.get(position); LinearLayout row=row(); row.setPadding(dp(10),dp(5),dp(16),dp(5)); row.setMinimumHeight(dp(82));
            TextView avatar=text(c.displayName().isEmpty()?"?":c.displayName().substring(0,c.displayName().offsetByCodePoints(0,1)),21,Color.WHITE); avatar.setGravity(Gravity.CENTER); avatar.setTypeface(null,Typeface.BOLD);
            bindAvatar(avatar,c.peer.id(),c.displayName(),c.peer.online); row.addView(avatar,new LinearLayout.LayoutParams(dp(68),dp(68)));
            LinearLayout center=column(); LinearLayout.LayoutParams cp=weight(); cp.leftMargin=dp(8); row.addView(center,cp);
            TextView name=text((c.pinned?"↑ ":"")+c.displayName(),16,INK); name.setTypeface(null,Typeface.BOLD); name.setSingleLine(); name.setEllipsize(TextUtils.TruncateAt.END); center.addView(name);
            TextView preview=text(c.preview.isEmpty()?(c.peer.online?"在线 · ":"离线 · ")+c.peer.endpoint.getAddress().getHostAddress():c.preview.replace('\n',' '),14,MUTED); preview.setSingleLine(); preview.setEllipsize(TextUtils.TruncateAt.END); preview.setPadding(0,dp(5),0,0); center.addView(preview);
            LinearLayout right=column(); right.setGravity(Gravity.END); LinearLayout.LayoutParams rp=new LinearLayout.LayoutParams(-2,-2); rp.leftMargin=dp(8); row.addView(right,rp);
            TextView time=text(c.time==0?"":new SimpleDateFormat("HH:mm",Locale.CHINA).format(new Date(c.time)),11,MUTED); right.addView(time);
            if(c.unread>0) { TextView badge=text(c.unread>99?"99+":Integer.toString(c.unread),11,Color.WHITE); badge.setGravity(Gravity.CENTER); badge.setBackground(shape(BLUE,14)); LinearLayout.LayoutParams b=new LinearLayout.LayoutParams(dp(28),dp(23)); b.topMargin=dp(7); right.addView(badge,b); }
            return row;
        }
    }
    private final class IconButton extends View {
        private final String kind; private final Paint p=new Paint(3); private final Path path=new Path();
        IconButton(String kind,String description,int color) { super(MainActivity.this); this.kind=kind; p.setColor(color); p.setStyle(Paint.Style.STROKE); p.setStrokeWidth(1.8f); p.setStrokeCap(Paint.Cap.ROUND); p.setStrokeJoin(Paint.Join.ROUND); setContentDescription(description); setFocusable(true); setClickable(true); android.util.TypedValue value=new android.util.TypedValue(); getTheme().resolveAttribute(android.R.attr.selectableItemBackgroundBorderless,value,true); setBackgroundResource(value.resourceId); }
        @Override protected void onDraw(Canvas canvas) {
            super.onDraw(canvas); canvas.save(); canvas.translate(getWidth()/2f-dp(12),getHeight()/2f-dp(12)); canvas.scale(dp(24)/24f,dp(24)/24f);
            path.reset();
            if(kind.equals("menu")) { for(int y=6;y<=18;y+=6) canvas.drawLine(3,y,21,y,p); }
            else if(kind.equals("more")) { for(int y=5;y<=19;y+=7) canvas.drawCircle(12,y,1,p); }
            else if(kind.equals("close")) { canvas.drawLine(5,5,19,19,p); canvas.drawLine(19,5,5,19,p); }
            else if(kind.equals("add")) { canvas.drawLine(12,3,12,21,p); canvas.drawLine(3,12,21,12,p); }
            else if(kind.equals("back")) { path.moveTo(12,4); path.lineTo(4,12); path.lineTo(12,20); canvas.drawPath(path,p); canvas.drawLine(4,12,21,12,p); }
            else if(kind.equals("send")) { path.moveTo(3,3); path.lineTo(22,12); path.lineTo(3,21); path.lineTo(6,12); path.close(); canvas.drawPath(path,p); canvas.drawLine(6,12,21,12,p); }
            else if(kind.equals("smile")) {canvas.drawCircle(12,12,9,p);canvas.drawPoint(9,9,p);canvas.drawPoint(15,9,p);canvas.drawArc(7,9,17,17,15,150,false,p);}
            else { path.moveTo(17,8); path.lineTo(8,17); path.cubicTo(4,21,0,16,4,12); path.lineTo(13,3); path.cubicTo(19,-3,27,5,21,11); path.lineTo(11,21); canvas.drawPath(path,p); }
            canvas.restore();
        }
        @Override public boolean performClick() { super.performClick(); return true; }
    }
}
