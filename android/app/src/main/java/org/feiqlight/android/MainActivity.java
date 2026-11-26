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
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicBoolean;

/** 原生 View 界面：借鉴 Telegram 的信息层级，不加载 WebView 或第三方 UI 运行时。 */
public final class MainActivity extends Activity implements ChatService.Observer {
    private static final String GITHUB_REPOSITORY="https://github.com/noway1467/fq";
    private int BLUE=0xff3390ec, INK=0xff17212b, MUTED=0xff64727f, PALE=0xfff1f4f7, LINE=0xffe7ecf0, WALL=0xffd1dff5, SURFACE=Color.WHITE, BUBBLE=0xffd6e9fb, OUTGOING=0xffe0eefc, ONLINE=0xff3cb34f;
    private int[] WALLS={0xffd6e9fa,0xffc6d6f6,0xffcbe5ee,0xffdbd6f5};
    private boolean DARK;
    // 十二套配色与 Windows Theme.Configure 同表：强调、表面、正文、次要文字、输入底、分隔线、壁纸四色；派生色按同一公式换算。
    private static final int[][] PALETTES={
        {0xff3390ec,0xffffffff,0xff17212b,0xff64727f,0xfff1f4f7,0xffe7ecf0,0xffd6e9fa,0xffc6d6f6,0xffcbe5ee,0xffdbd6f5},
        {0xff3390ec,0xff17212b,0xffe9eef3,0xff8a9aab,0xff22303d,0xff2b3a48,0xff0e1a28,0xff16294a,0xff0c1f2b,0xff1c2141},
        {0xff229974,0xfff7fbf8,0xff1d3328,0xff5e7368,0xffeaf2ec,0xffdce8df,0xffdaecce,0xffbedebe,0xffe2eac8,0xffc8e2cd},
        {0xff2cb385,0xff14221d,0xffe2f1e8,0xff8fae9f,0xff1e3129,0xff29423a,0xff0f241c,0xff16382b,0xff0e2a21,0xff1b3a31},
        {0xff8b67d7,0xfffcf9fd,0xff33263d,0xff6e6478,0xfff2ebf6,0xffe7ddec,0xffecdef6,0xffd7ccf2,0xfff2e0ec,0xffded6f4},
        {0xff9a78e6,0xff211b2e,0xffeee7f6,0xffa897ba,0xff2d253d,0xff3a3050,0xff1c1330,0xff2d1e47,0xff25162f,0xff2a2045},
        {0xffd97706,0xfffffdf9,0xff2d2216,0xff786958,0xfff8f1e6,0xffede1cf,0xfffcecd2,0xfff6dbb8,0xfffbe8dc,0xfff3dfc1},
        {0xffdd8014,0xff221b15,0xfff5ede3,0xffb3a18e,0xff2e241c,0xff3d3025,0xff24180f,0xff362313,0xff2b1b15,0xff322418},
        {0xffe0537c,0xfffff9fb,0xff341f27,0xff7d616b,0xfffaecf1,0xfff0dce3,0xfffce0ea,0xfff3d3e6,0xfffde4df,0xffeed5eb},
        {0xffe25b84,0xff24181e,0xfff6eaef,0xffb393a0,0xff312129,0xff422c37,0xff24111b,0xff38192a,0xff2b1522,0xff351e30},
        {0xff4b647d,0xfffafbfd,0xff1e252d,0xff64707d,0xffedf1f5,0xffdfe5ec,0xffdfe6ee,0xffd2dce7,0xffe5eaef,0xffd8dfe8},
        {0xff5f7e9e,0xff181c21,0xffeaeef2,0xff939fab,0xff222830,0xff2f3742,0xff12161b,0xff1b222c,0xff161b22,0xff1f2530}};
    private void applyTheme() {
        SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE); DARK=SystemBars.isDark(this);
        int[] p=palette(prefs.getString("accent","blue"),DARK);
        BLUE=p[0];SURFACE=p[1];INK=p[2];MUTED=p[3];PALE=p[4];LINE=p[5];WALLS=new int[]{p[6],p[7],p[8],p[9]};WALL=wall(p);
        // BUBBLE 用于选中与按压底色（Windows Sky），OUTGOING 为发出气泡（Windows Outgoing）。
        BUBBLE=blend(BLUE,SURFACE,DARK?.30f:.17f);OUTGOING=blend(BLUE,SURFACE,DARK?.42f:.15f);ONLINE=DARK?0xff58c468:0xff3cb34f;
        SystemBars.apply(getWindow(),SURFACE,DARK);
    }
    private boolean systemDark() {return SystemBars.systemDark(this);}
    private static int[] palette(String accent,boolean dark) { int group="green".equals(accent)?2:"purple".equals(accent)?4:"amber".equals(accent)?6:"rose".equals(accent)?8:"slate".equals(accent)?10:0; return PALETTES[group+(dark?1:0)]; }
    // 纯色背景与日期胶囊取壁纸四色的平均值。
    private static int wall(int[] p) { return Color.rgb((Color.red(p[6])+Color.red(p[7])+Color.red(p[8])+Color.red(p[9]))/4,(Color.green(p[6])+Color.green(p[7])+Color.green(p[8])+Color.green(p[9]))/4,(Color.blue(p[6])+Color.blue(p[7])+Color.blue(p[8])+Color.blue(p[9]))/4); }
    // 气泡元信息、日期胶囊与气泡投影：与 Windows OutgoingMeta、ServiceFill 及气泡阴影同一换算。
    private int outgoingMeta(){return DARK?blend(BLUE,INK,.38f):blend(BLUE,INK,.62f);}
    private CharSequence messageMeta(String prefix,LanNode.Message message,boolean includeDelivery) {
        return messageMeta(prefix,message,includeDelivery,false);
    }
    private CharSequence messageMeta(String prefix,LanNode.Message message,boolean includeDelivery,boolean onScrim) {
        boolean canceled=message.outgoing&&message.state.contains("取消");
        if(!message.outgoing||(!includeDelivery&&!canceled))return prefix;
        if(!canceled)return prefix+"  "+message.state;
        SpannableStringBuilder value=new SpannableStringBuilder(prefix).append("  ");
        int start=value.length();value.append("● ").append(message.state);
        value.setSpan(new android.text.style.ForegroundColorSpan(DARK||onScrim?0xffff6e6e:0xffd24e54),start,value.length(),Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
        return value;
    }
    private int serviceFill(){return (blend(Color.BLACK,WALL,DARK?.45f:.42f)&0xffffff)|((DARK?150:165)<<24);}
    private int bubbleShadow(){return DARK?0x46000000:(blend(Color.BLACK,BLUE,.7f)&0xffffff)|(26<<24);}
    private ChatService service;
    private DraftStore drafts;
    private ReceivedStorage receivedStorage;
    private MediaPreview media;
    private TextView folderLabel;
    private RadioGroup wallpaperChoices;
    private String editorSavedText="";
    private boolean bound, visible, rendering, sending;
    private String sendingPeer="";
    private TextView sendStatus;
    private boolean restoreAttempted;
    private boolean resumed;
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
        return new ChatWallpaper(this,WALLS,WALL,BLUE,DARK,mode,backgroundBitmap);
    }
    private long historyRequestVersion, historyAppliedVersion;
    private ActionMode selectionMode;
    private TextView selectionOwner;
    private String selected="", pickerPeer="", historySignature="";
    private String pendingMessageLocation, locatedMessage;
    private LanNode.Message archiveAnchor;
    private Button returnToLatest;
    private boolean shareHandled;
    private final SecureIdentity.Confirmation securityPrompt=this::confirmSecurity;
    private final Set<Long> displayedOutgoing=new HashSet<>();
    private final List<Runnable> fileProgressViews=new ArrayList<>();
    private final Map<String,Uri> chatFiles=new HashMap<>();
    private final Map<String,int[]> chatMedia=new HashMap<>();
    private final Map<String,ChatService.FileProgress> chatFileProgress=new HashMap<>();
    private final Map<String,ChatRow> chatRows=new HashMap<>();
    private static final class ChatRow {
        String signature;
        LinearLayout row,bubble;
        View portrait;
        int previews;
        final List<Runnable> progress=new ArrayList<>();
    }
    private final Handler chatUpdates=new Handler(Looper.getMainLooper());
    private boolean chatLoading,chatReloadPending;
    private volatile long chatLoadGeneration;
    private List<LanNode.Message> deferredChatRows;
    private boolean chatProgressPending;
    private final Runnable refreshChatProgress=()->{
        chatProgressPending=false;
        if(!visible||service==null||!selected.equals(this.progressPeer)||this.historyDialog!=null&&this.historyDialog.isShowing()||this.tasksDialog!=null&&this.tasksDialog.isShowing()||this.scroll instanceof ChatScroll&&((ChatScroll)this.scroll).moving())return;
        for(Runnable update:new ArrayList<>(fileProgressViews))update.run();
    };
    private String progressPeer="";
    private boolean forceLatest;
    private LinearLayout root, body, toolbar, messages, connectionBar;
    private TextView title, subtitle, transferStatus, empty, chatAvatar;
    private AvatarDrawable incomingMessageAvatar, outgoingMessageAvatar;
    private String messageAvatarSignature="";
    private final List<View> messageAvatarViews=new ArrayList<>();
    private EditText search, editor;
    private ListView list;
    private ScrollView scroll;
    private Button connect;
    private IconButton sendButton;
    private Dialog historyDialog;
    private Dialog tasksDialog;
    private TaskAdapter taskAdapter;
    private boolean loadingTasks,reloadTasksPending;
    private final List<Uri> guestFiles=new ArrayList<>();
    private boolean guestUpload;
    private android.window.OnBackInvokedCallback backCallback;
    private ConversationAdapter adapter;
    private final java.util.concurrent.ExecutorService avatarLoader=java.util.concurrent.Executors.newSingleThreadExecutor();
    private AvatarSelection avatarSelection, avatarImageRequest;
    private int avatarImageRevision;
    private static final class AvatarSelection {
        String value; Dialog dialog; TextView preview; Button confirm,local; int revision; boolean loading;
        final List<ImageButton> choices=new ArrayList<>();
    }
    private boolean selectingMessages;
    private final Set<String> messageMarks=new LinkedHashSet<>();
    private final Map<String,View> messageViews=new HashMap<>();
    private List<LanNode.Message> messageRows=new ArrayList<>();
    private LinearLayout messageSelectionBar;
    private TextView messageSelectionCount;
    private Button deleteSelectedMessages;
    private Button deleteSelectedFiles;
    private Integer themeScrollPosition;
    private final ServiceConnection connection=new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name,IBinder binder) {
            service=((ChatService.LocalBinder)binder).service(); if(visible) service.observe(MainActivity.this);
            service.securityConfirmation=securityPrompt;
            service.active(visible?selected:""); saveDraft(); renderPage(); restoreConnection();
        }
        @Override public void onServiceDisconnected(ComponentName name) { service=null; restoreAttempted=false; renderPage(); }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        drafts=new DraftStore(this); receivedStorage=new ReceivedStorage(this); media=new MediaPreview(this);
        if(state!=null) { selected=state.getString("peer",""); pickerPeer=state.getString("picker","");shareHandled=state.getBoolean("share_handled",false); }
        if(Build.VERSION.SDK_INT>=33) { backCallback=()->{ if(tasksDialog!=null)tasksDialog.onBackPressed();else if(selectingMessages)endMessageSelection();else if(!selected.isEmpty()) open(""); else finish(); }; getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,backCallback); }
        renderPage(); bound=bindService(new Intent(this,ChatService.class),connection,BIND_AUTO_CREATE);
    }
    // 熄屏期间对端可能已切网离开，亮屏回到页面时立即复核头像在线光晕。
    @Override protected void onStart() { super.onStart(); visible=true; if(SystemBars.isDark(this)!=DARK)rerenderPreservingState(); if(service!=null) { service.observe(this); service.active(selected); service.verifyPresence(); changed(); } restoreConnection(); }
    @Override protected void onResume() { super.onResume();resumed=true;restoreConnection();if(taskAdapter!=null){taskAdapter.times.clear();taskAdapter.scheduleDayRefresh();loadTaskRows();} }
    @Override protected void onPause() { resumed=false;if(taskAdapter!=null)taskAdapter.stop();super.onPause(); }
    @Override public void onWindowFocusChanged(boolean hasFocus) { super.onWindowFocusChanged(hasFocus); if(hasFocus) SystemBars.appearance(getWindow(),DARK); }
    @Override protected void onStop() { pendingMessageLocation=null;scrollPositionVersion++;historyAppliedVersion=++historyRequestVersion;visible=false;closeHistory(); finishSelection(); saveDraft();chatUpdates.removeCallbacksAndMessages(null);deferredChatRows=null;chatProgressPending=false; if(service!=null) { service.remove(this); service.active(""); } super.onStop(); }
    @Override protected void onDestroy() { if(tasksDialog!=null)tasksDialog.dismiss();avatarLoader.shutdownNow();avatarImageRequest=null;if(avatarSelection!=null&&avatarSelection.dialog!=null)avatarSelection.dialog.dismiss();if(media!=null) media.close(); if(backgroundBitmap!=null){backgroundBitmap.recycle();backgroundBitmap=null;} if(service!=null){service.remove(this);if(service.securityConfirmation==securityPrompt)service.securityConfirmation=null;} if(bound) unbindService(connection); if(Build.VERSION.SDK_INT>=33&&backCallback!=null) getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(backCallback); super.onDestroy(); }
    @Override protected void onSaveInstanceState(Bundle state) { saveDraft(); state.putString("peer",selected); state.putString("picker",pickerPeer);state.putBoolean("share_handled",shareHandled); super.onSaveInstanceState(state); }
    @Override public void onConfigurationChanged(android.content.res.Configuration configuration) {
        super.onConfigurationChanged(configuration);
        if(!getSharedPreferences("settings",MODE_PRIVATE).getBoolean("follow_system_theme",false))return;
        rerenderPreservingState();
    }
    private void rerenderPreservingState() {
        saveDraft();themeScrollPosition=scroll==null?null:scroll.getScrollY();boolean choosing=selectingMessages;Set<String> marks=new LinkedHashSet<>(messageMarks);
        renderPage();selectingMessages=choosing;messageMarks.addAll(marks);updateMessageSelection();
    }
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
        pendingMessageLocation=null;locatedMessage=null;archiveAnchor=null;
        selectingMessages=false;messageMarks.clear();messageViews.clear();messageRows.clear();messageAvatarViews.clear();messageAvatarSignature="";messageSelectionBar=null;
        closeHistory();
        chatRows.clear();chatFiles.clear();chatMedia.clear();chatFileProgress.clear();fileProgressViews.clear();deferredChatRows=null;chatLoading=false;chatReloadPending=false;chatLoadGeneration++;
        chatUpdates.removeCallbacksAndMessages(null);chatProgressPending=false;
        historyAppliedVersion=++historyRequestVersion;
        finishSelection();
        applyTheme(); historySignature=""; displayedOutgoing.clear(); forceLatest=false; editor=null; search=null; messages=null; list=null; chatAvatar=null;sendStatus=null;sendButton=null;
        root=column(); root.setBackgroundColor(SURFACE); setContentView(root);
        SystemBars.apply(getWindow(),SURFACE,DARK);
        root.setOnApplyWindowInsetsListener((view,insets)->{
            if(Build.VERSION.SDK_INT>=30) { Insets i=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout()|WindowInsets.Type.ime()); view.setPadding(i.left,i.top,i.right,i.bottom); }
            else view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            return insets;
        }); root.requestApplyInsets();
        toolbar=row(); toolbar.setPadding(dp(8),0,dp(8),0); toolbar.setMinimumHeight(dp(64)); root.addView(toolbar,new LinearLayout.LayoutParams(-1,-2));
        IconButton menu=new IconButton(selected.isEmpty()?"menu":"back",selected.isEmpty()?"打开菜单":"返回会话列表",MUTED);
        toolbar.addView(menu,new LinearLayout.LayoutParams(dp(48),dp(48))); menu.setOnClickListener(v->{ if(selectingMessages)endMessageSelection();else if(selected.isEmpty()) showMenu(v); else open(""); });
        if(!selected.isEmpty()) {
            ChatStore.Conversation current=conversation(selected); String name=current==null?"?":current.displayName();
            TextView avatar=text("",18,Color.WHITE); avatar.setGravity(Gravity.CENTER); bindAvatar(avatar,selected,name,current!=null&&current.peer.online,current==null?null:current.peer.avatar);
            chatAvatar=avatar;
            avatar.setOnClickListener(v->{ChatStore.Conversation contact=conversation(selected);if(contact!=null)showContactProfile(contact);});
            LinearLayout.LayoutParams ap=new LinearLayout.LayoutParams(dp(54),dp(54)); ap.leftMargin=-dp(6); ap.rightMargin=dp(4); toolbar.addView(avatar,ap);
        }
        LinearLayout headings=column(); title=text("飞Q",20,INK); title.setTypeface(null,Typeface.BOLD); subtitle=text("",12,MUTED);
        title.setSingleLine(); title.setEllipsize(TextUtils.TruncateAt.END); subtitle.setSingleLine(); subtitle.setEllipsize(TextUtils.TruncateAt.END);
        headings.addView(title); headings.addView(subtitle); toolbar.addView(headings,weight());
        // 主页与聊天页顶栏都有传输任务入口：主页默认「全部」，聊天页默认当前联系人。
        IconButton transfers=new IconButton("transfer","传输任务",MUTED);
        toolbar.addView(transfers,new LinearLayout.LayoutParams(dp(48),dp(48)));transfers.setOnClickListener(v->{if(selected.isEmpty())showTasks();else showTasks("peer:"+selected);});
        IconButton action=new IconButton(selected.isEmpty()?"add":"more",selected.isEmpty()?"手动添加联系人":"会话菜单",MUTED);
        toolbar.addView(action,new LinearLayout.LayoutParams(dp(48),dp(48))); action.setOnClickListener(v->{ if(selected.isEmpty()) addPeer(); else {ChatStore.Conversation c=conversation(selected);if(c!=null)conversationMenu(c);} });
        View line=new View(this); line.setBackgroundColor(LINE); root.addView(line,new LinearLayout.LayoutParams(-1,dp(1)));
        if(!selected.isEmpty()) {
            messageSelectionBar=row();messageSelectionBar.setContentDescription("消息多选操作栏");messageSelectionBar.setPadding(dp(12),dp(4),dp(12),dp(4));messageSelectionBar.setVisibility(View.GONE);
            messageSelectionCount=text("已选 0 条",14,INK);messageSelectionCount.setSingleLine();messageSelectionCount.setEllipsize(TextUtils.TruncateAt.END);messageSelectionBar.addView(messageSelectionCount,weight());
            Button all=button("全选",false);deleteSelectedFiles=button("删除文件",false);deleteSelectedFiles.setTextColor(0xffd24e54);deleteSelectedMessages=button("删除",false);deleteSelectedMessages.setTextColor(0xffd24e54);Button cancel=button("取消",false);
            for(Button button:new Button[]{all,deleteSelectedFiles,deleteSelectedMessages,cancel}){button.setPadding(dp(10),dp(8),dp(10),dp(8));button.setMinWidth(0);button.setMinimumWidth(0);LinearLayout.LayoutParams gap=new LinearLayout.LayoutParams(-2,-2);gap.leftMargin=dp(4);messageSelectionBar.addView(button,gap);}
            all.setOnClickListener(v->{messageMarks.clear();for(LanNode.Message message:messageRows)messageMarks.add(messageKey(message));updateMessageSelection();});cancel.setOnClickListener(v->endMessageSelection());
            deleteSelectedMessages.setOnClickListener(v->{List<LanNode.Message> selectedRows=new ArrayList<>();for(LanNode.Message message:messageRows)if(messageMarks.contains(messageKey(message)))selectedRows.add(message);requestDeleteMessages(selectedRows);});
            deleteSelectedFiles.setOnClickListener(v->requestDeleteSelectedFiles());
            root.addView(messageSelectionBar,new LinearLayout.LayoutParams(-1,-2));
        }
        body=column(); root.addView(body,new LinearLayout.LayoutParams(-1,0,1));
        if(selected.isEmpty()) renderList(); else renderChat();
        changed();
    }
    private void renderList() {
        LinearLayout searchWrap=column(); searchWrap.setPadding(dp(16),dp(10),dp(16),dp(10)); body.addView(searchWrap);
        search=new EditText(this); configureInput(search); search.setSingleLine(); search.setTextSize(15); search.setHint("搜索"); search.setHintTextColor(MUTED); search.setTextColor(INK);
        search.setPadding(dp(16),dp(9),dp(16),dp(9)); search.setBackground(shape(PALE,CONTROL_RADIUS)); search.setContentDescription("搜索联系人或最近消息");
        searchWrap.addView(search,new LinearLayout.LayoutParams(-1,dp(46)));
        PullRefreshLayout content=new PullRefreshLayout(this,SURFACE,LINE,BLUE); body.addView(content,new LinearLayout.LayoutParams(-1,0,1));
        list=new ListView(this); list.setDivider(null); list.setClipToPadding(false); list.setPadding(0,0,0,dp(12));
        adapter=new ConversationAdapter(); list.setAdapter(adapter); content.addView(list,new FrameLayout.LayoutParams(-1,-1));
        empty=text("",15,MUTED); empty.setGravity(Gravity.CENTER); empty.setPadding(dp(38),dp(24),dp(38),dp(24)); empty.setLineSpacing(dp(8),1); content.addView(empty,new FrameLayout.LayoutParams(-1,-1)); list.setEmptyView(empty);
        ListView target=list;
        content.setTarget(()->!target.canScrollVertically(-1));content.setOnRefresh(this::pullRefresh);
        content.setContentDescription("会话列表，下拉刷新");
        content.setAccessibilityDelegate(new View.AccessibilityDelegate(){
            @Override public void onInitializeAccessibilityNodeInfo(View host,android.view.accessibility.AccessibilityNodeInfo info){super.onInitializeAccessibilityNodeInfo(host,info);info.addAction(new android.view.accessibility.AccessibilityNodeInfo.AccessibilityAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD,"刷新联系人"));}
            @Override public boolean performAccessibilityAction(View host,int action,Bundle args){if(action==android.view.accessibility.AccessibilityNodeInfo.ACTION_SCROLL_BACKWARD){pullRefresh();return true;}return super.performAccessibilityAction(host,action,args);}
        });
        list.setOnItemClickListener((p,v,index,id)->open(adapter.items.get(index).peer.id()));
        list.setOnItemLongClickListener((p,v,index,id)->{conversationMenu(adapter.items.get(index));return true;});
        search.addTextChangedListener(new TextWatcher() { public void beforeTextChanged(CharSequence s,int start,int count,int after) { } public void onTextChanged(CharSequence s,int start,int before,int count) { adapter.refresh(); } public void afterTextChanged(Editable e) { } });
        LinearLayout footer=column(); connectionBar=footer; footer.setPadding(dp(20),dp(8),dp(20),dp(16)); body.addView(footer);
        connect=button("连接",true);
        LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,dp(50)); params.topMargin=dp(12); footer.addView(connect,params);
        connect.setOnClickListener(v->{ if(service!=null&&service.connectionActive()) service.refresh(); else connect(); });
    }
    private boolean pullRefresh() {
        if(service==null||!service.connectionActive()){error("请先点击“连接”，再下拉刷新联系人");return false;}
        service.refresh();return true;
    }
    private int editorGeneration;
    private void renderChat() {
        body.setBackground(chatWallpaper()); scroll=new ChatScroll(); scroll.setFillViewport(true); scroll.setClipToPadding(false);
        messages=column(); messages.setPadding(dp(4),dp(18),dp(4),dp(18)); scroll.addView(messages,new ScrollView.LayoutParams(-1,-2)); body.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        returnToLatest=button("回到最新消息",false);returnToLatest.setVisibility(View.GONE);body.addView(returnToLatest);
        returnToLatest.setOnClickListener(view->{forceLatest=true;changed();});
        transferStatus=text("",12,BLUE); transferStatus.setMaxLines(2); transferStatus.setEllipsize(TextUtils.TruncateAt.END); transferStatus.setPadding(dp(16),dp(8),dp(16),dp(8)); transferStatus.setBackgroundColor(SURFACE); transferStatus.setOnClickListener(v->{ if(service!=null&&service.busy()) confirm("文件传输","取消当前任务？","取消任务",()->service.cancelTransfer()); }); body.addView(transferStatus);
        sendStatus=text("正在发送…",12,MUTED);sendStatus.setPadding(dp(16),dp(4),dp(16),dp(4));sendStatus.setBackgroundColor(SURFACE);sendStatus.setVisibility(View.GONE);sendStatus.setContentDescription("消息发送状态");body.addView(sendStatus);
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
        editor.setFilters(new InputFilter[]{(source,start,end,dest,dstart,dend)->{
            String value=source.subSequence(start,end).toString();
            if(value.equals(MessageText.normalizeLineBreaks(value)))return null;
            // 保留输入法和剪贴板的 Span；仅改换行字符，再交给 LengthFilter 保护长度和代理对。
            SpannableStringBuilder normalized=new SpannableStringBuilder(source,start,end);
            for(int i=normalized.length()-1;i>=0;i--){
                char ch=normalized.charAt(i);
                if(ch=='\r'&&i+1<normalized.length()&&normalized.charAt(i+1)=='\n')normalized.delete(i,i+1);
                else if(ch=='\r'||ch=='\u000b'||ch=='\u2028'||ch=='\u2029')normalized.replace(i,i+1,"\n");
            }
            return normalized;
        },new InputFilter.LengthFilter(MessageText.INPUT_LIMIT)});
        String rawDraft=drafts.read(selected),normalizedDraft=MessageText.normalizeLineBreaks(rawDraft);
        if(!normalizedDraft.equals(rawDraft))drafts.save(selected,normalizedDraft);
        editorSavedText=normalizedDraft; editor.setText(editorSavedText); composer.addView(editor,weight());
        editor.addTextChangedListener(new TextWatcher() {
            public void beforeTextChanged(CharSequence s,int start,int count,int after) { }
            public void onTextChanged(CharSequence s,int start,int before,int count) { saveDraft(); updateSendFeedback(); }
            public void afterTextChanged(Editable value) { }
        });
        sendButton=new IconButton("send","发送消息",Color.WHITE); sendButton.setBackground(shape(BLUE,28)); LinearLayout.LayoutParams sendParams=new LinearLayout.LayoutParams(dp(48),dp(48)); sendParams.leftMargin=dp(8); composer.addView(sendButton,sendParams);
        sendButton.setOnClickListener(v->send());
        updateSendFeedback();
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
        LinearLayout content=column();content.setContentDescription("表情网格");List<Button> picks=new ArrayList<>();
        String[] categories={"笑脸与心情","手势与人物","爱心与庆祝","动物与自然","食物与饮料","活动与物品"};
        int cell=Math.max(dp(48),(int)Math.ceil(android.util.TypedValue.applyDimension(android.util.TypedValue.COMPLEX_UNIT_SP,24,getResources().getDisplayMetrics()))+dp(16));
        int columns=Math.max(1,Math.min(8,(getResources().getDisplayMetrics().widthPixels-dp(52))/cell));
        for(int group=0;group<EMOJI_GROUPS.length;group++) {
            TextView heading=text(categories[group],12,MUTED);heading.setPadding(dp(4),dp(group==0?0:12),0,dp(8));content.addView(heading);
            GridLayout grid=new GridLayout(this);grid.setColumnCount(columns);content.addView(grid,new LinearLayout.LayoutParams(-1,-2));int index=0;
            for(String emoji:EMOJI_GROUPS[group].split(" ")) {
                Button pick=button(emoji,false);pick.setTextSize(24);pick.setContentDescription(emoji);pick.setMinWidth(0);pick.setMinimumWidth(0);pick.setPadding(0,dp(4),0,dp(4));
                pick.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),null,shape(Color.WHITE,CONTROL_RADIUS)));
                // 系统大字号时按文字实际高度展开，保留最小触摸高度。
                pick.setMinHeight(dp(48));pick.setMinimumHeight(dp(48));
                GridLayout.LayoutParams p=new GridLayout.LayoutParams(GridLayout.spec(index/columns),GridLayout.spec(index%columns,1f));p.width=0;p.height=ViewGroup.LayoutParams.WRAP_CONTENT;grid.addView(pick,p);index++;picks.add(pick);
            }
        }
        Dialog dialog=sheet("表情",content,null,null);
        for(Button pick:picks) pick.setOnClickListener(v->{String emoji=pick.getText().toString();dialog.dismiss();if(editor!=input||editorGeneration!=generation||!selected.equals(peer))return;if(insertEmoji(input,emoji,start,end))input.requestFocus();else error("输入已满，未插入表情");});
    }
    static boolean insertEmoji(EditText input,String emoji,int start,int end) {
        int length=input.length();start=Math.max(0,Math.min(start,length));end=Math.max(start,Math.min(end,length));
        if(length-(end-start)+emoji.length()>MessageText.INPUT_LIMIT)return false;
        input.getText().replace(start,end,emoji);input.setSelection(start+emoji.length());return true;
    }
    private ChatStore.Conversation conversation(String id) { if(service!=null) for(ChatStore.Conversation item:service.conversations()) if(item.peer.id().equals(id)) return item; return null; }
    private void open(String peer) { themeScrollPosition=null;saveDraft(); selected=peer; if(service!=null) service.active(peer); renderPage(); }
    private void acceptSystemShare(){
        if(shareHandled||service==null||!service.online())return;Intent request=getIntent();String action=request.getAction();if(!Intent.ACTION_SEND.equals(action)&&!Intent.ACTION_SEND_MULTIPLE.equals(action))return;
        List<ChatStore.Conversation> choices=new ArrayList<>();for(ChatStore.Conversation c:service.conversations())if(c.peer.online)choices.add(c);if(choices.isEmpty())return;
        shareHandled=true;LinearLayout box=column();Dialog dialog=sheet("分享到飞Q · 选择联系人",box,null,null);
        for(ChatStore.Conversation contact:choices)fileAction(box,dialog,contact.displayName(),()->{
            try{ArrayList<Uri> uris=Intent.ACTION_SEND_MULTIPLE.equals(action)?request.getParcelableArrayListExtra(Intent.EXTRA_STREAM):new ArrayList<>();if(uris==null)uris=new ArrayList<>();if(Intent.ACTION_SEND.equals(action)){Uri uri=request.getParcelableExtra(Intent.EXTRA_STREAM);if(uri!=null)uris.add(uri);}
                if(uris.isEmpty()&&request.getClipData()!=null)for(int i=0;i<request.getClipData().getItemCount();i++){Uri uri=request.getClipData().getItemAt(i).getUri();if(uri!=null)uris.add(uri);}
                CharSequence text=request.getCharSequenceExtra(Intent.EXTRA_TEXT);if(!uris.isEmpty())service.sendFiles(contact.peer,uris);if(text!=null&&!text.toString().trim().isEmpty())service.send(contact.peer,MessageText.normalizeLineBreaks(text.toString()),success->{});open(contact.peer.id());
            }catch(RuntimeException error){error("分享内容无效："+error.getMessage());}
        });
    }
    private void saveDraft() {
        if(isDestroyed() || editor==null || selected.isEmpty()) return;
        String text=editor.getText().toString();
        // 每次编辑即时保存；onStop 不再把已被服务清除的旧输入重新写回。
        if(!text.equals(editorSavedText)) { drafts.save(selected,text); editorSavedText=text; }
    }
    @Override public void sent(String peer) {
        if(archiveAnchor!=null && selected.equals(peer)){forceLatest=true;changed();}
    }
    @Override public void changed() {
        acceptSystemShare();
        loadTaskRows();
        if(tasksDialog!=null&&tasksDialog.isShowing()||historyDialog!=null&&historyDialog.isShowing())return;
        if(root==null || rendering) return; rendering=true;
        try {
            if(selected.isEmpty()) {
                subtitle.setText(service==null?"":service.contactStatus());
                if(adapter!=null) adapter.refresh();
                if(connect!=null) connect.setText(service!=null&&service.connectionActive()?"重试":"连接");
                if(connectionBar!=null) connectionBar.setVisibility(service!=null&&service.online()?View.GONE:View.VISIBLE);
                if(empty!=null) empty.setText(search!=null&&!search.getText().toString().isEmpty()?"没有结果":"暂无会话");
            } else {
                String rawSaved=drafts.read(selected),saved=MessageText.normalizeLineBreaks(rawSaved);
                if(!saved.equals(rawSaved))drafts.save(selected,saved);
                if(editor!=null && !editor.getText().toString().equals(saved)) { editorSavedText=saved; editor.setText(saved); }
                ChatStore.Conversation item=conversation(selected);
                title.setText(item==null?"聊天":item.displayName());
                if(chatAvatar!=null) bindAvatar(chatAvatar,selected,item==null?"?":item.displayName(),item!=null&&item.peer.online,item==null?null:item.peer.avatar);
                subtitle.setText(item==null?"正在读取会话…":(item.peer.online?"在线":"离线")+item.nameSuffix()+" · "+item.peer.endpoint.getAddress().getHostAddress()+":"+item.peer.endpoint.getPort());
                if(item!=null)title.setContentDescription(item.displayName()+" · "+item.peer.id());
                if(service!=null) {
                    transferStatus.setText(service.transferStatus().isEmpty()?"":service.transferStatus()+ (service.busy()?" · 点击取消":""));
                    transferStatus.setVisibility(service.transferStatus().isEmpty()?View.GONE:View.VISIBLE);
                    if(chatLoading){chatReloadPending=true;return;}
                    String request=selected; LinearLayout target=messages; ChatService source=service; long version=++historyRequestVersion,load=++chatLoadGeneration;
                    chatLoading=true;
                    // 同一联系人也可能已切出再切回；同时校验页面实例和请求版本，不能只比较 ID。
                    // 只拒绝已应用结果之前的回调，不因还有更新请求排队而让当前页面一直空白。
                    if(forceLatest){archiveAnchor=null;pendingMessageLocation=null;}
                    LanNode.Message anchor=archiveAnchor;
                    returnToLatest.setVisibility(anchor==null?View.GONE:View.VISIBLE);
                    source.chatPage(request,anchor,()->load==chatLoadGeneration&&!isDestroyed(),found->{
                        chatLoading=false;
                        if(!visible||isDestroyed()||isFinishing()||tasksDialog!=null&&tasksDialog.isShowing()||historyDialog!=null&&historyDialog.isShowing()||service!=source||!selected.equals(request)||messages!=target||anchor!=archiveAnchor||version<historyAppliedVersion){if(chatReloadPending){chatReloadPending=false;changed();}return;}
                        historyAppliedVersion=version;
                        if(anchor!=null && found.rows.isEmpty()) {
                            archiveAnchor=null;pendingMessageLocation=null;error("这条消息已删除");changed();return;
                        }
                        renderChatPage(found);
                        if(chatReloadPending){chatReloadPending=false;changed();}
                    },failure->{chatLoading=false;chatReloadPending=false;error(failure);});
                }
            }
        } finally { rendering=false; }
    }
    private void renderChatPage(ChatService.ChatPage page) {
        chatFiles.clear();chatFiles.putAll(page.files);chatMedia.clear();chatMedia.putAll(page.media);chatFileProgress.clear();chatFileProgress.putAll(page.progress);
        renderMessages(page.rows);
    }
    private ChatService.FileProgress chatProgress(LanNode.Message message,Protocol.Attachment file) {
        ChatService.FileProgress live=service==null?null:service.progress(selected,message.number,file.id,message.outgoing);
        return live!=null?live:chatFileProgress.get(ChatService.fileKey(selected,message.number,file.id,message.outgoing));
    }
    private boolean chatSaved(LanNode.Message message,Protocol.Attachment file) {
        String key=ChatService.fileKey(selected,message.number,file.id,message.outgoing);
        ChatService.FileProgress progress=service==null?null:service.progress(selected,message.number,file.id,message.outgoing);
        // 后台快照之后的新保存/删除结果优先，旧进度不能覆盖重新检查的文件存在性。
        if(progress!=null&&progress!=chatFileProgress.get(key)&&ChatService.wasStored(progress.state))return !progress.state.equals("本机文件已删除");
        return chatFiles.get(key)!=null;
    }
    private final class ChatScroll extends ScrollView {
        private boolean touching;
        private long movedAt=-1000;
        private final Runnable settled=()->{
            if(scroll!=this||touching||android.os.SystemClock.uptimeMillis()-movedAt<100)return;
            if(!visible||historyDialog!=null&&historyDialog.isShowing()||tasksDialog!=null&&tasksDialog.isShowing())return;
            if(deferredChatRows!=null){List<LanNode.Message> rows=deferredChatRows;deferredChatRows=null;renderMessages(rows);}
            transfersChanged();
        };
        ChatScroll(){super(MainActivity.this);}
        boolean moving(){return touching||android.os.SystemClock.uptimeMillis()-movedAt<100;}
        @Override public boolean dispatchTouchEvent(MotionEvent event) {
            if(event.getActionMasked()==MotionEvent.ACTION_DOWN)touching=true;
            if(event.getActionMasked()==MotionEvent.ACTION_UP||event.getActionMasked()==MotionEvent.ACTION_CANCEL){touching=false;chatUpdates.removeCallbacks(settled);chatUpdates.postDelayed(settled,120);}
            return super.dispatchTouchEvent(event);
        }
        @Override protected void onScrollChanged(int x,int y,int oldX,int oldY){super.onScrollChanged(x,y,oldX,oldY);movedAt=android.os.SystemClock.uptimeMillis();chatUpdates.removeCallbacks(settled);chatUpdates.postDelayed(settled,120);}
    }
    private void renderMessages(List<LanNode.Message> rows) {
        boolean sent=rows.stream().anyMatch(message->message.outgoing&&!displayedOutgoing.contains(message.number));
        if(!historySignature.isEmpty()&&!sent&&!forceLatest&&pendingMessageLocation==null&&scroll instanceof ChatScroll&&((ChatScroll)scroll).moving()){deferredChatRows=rows;return;}
        deferredChatRows=null;
        messageRows=new ArrayList<>(rows);Set<String> availableKeys=new HashSet<>();for(LanNode.Message message:rows)availableKeys.add(messageKey(message));messageMarks.retainAll(availableKeys);updateMessageSelection();
        StringBuilder fingerprint=new StringBuilder(selected);
        ChatStore.Conversation conversation=conversation(selected);
        SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE);
        String ownIdentity="device:"+prefs.getString("device_id",""), ownAvatar=prefs.getString("avatar","default"), peerAvatar=conversation==null?null:conversation.peer.avatar;
        String avatarSignature=selected+"\u0001"+peerAvatar+"\u0001"+ownIdentity+"\u0001"+ownAvatar;
        if(!avatarSignature.equals(messageAvatarSignature)) {
            // 每个会话只复用两张头像；资料变化不重建气泡，也不改变滚动位置。
            incomingMessageAvatar=new AvatarDrawable(this,selected,false,dp(2),SURFACE,ONLINE,peerAvatar);
            outgoingMessageAvatar=new AvatarDrawable(this,ownIdentity,false,dp(2),SURFACE,ONLINE,ownAvatar);
            messageAvatarSignature=avatarSignature;for(View avatar:messageAvatarViews)avatar.invalidate();
        }
        Map<String,String> rowSignatures=new HashMap<>();
        fingerprint.append(dayKey(System.currentTimeMillis()));
        for(LanNode.Message message:rows) {
            StringBuilder value=new StringBuilder(messageKey(message)).append('\0').append(message.state).append('\0').append(message.text).append('\0').append(message.time);
            for(Protocol.Attachment file:message.files) {
                String fileKey=ChatService.fileKey(selected,message.number,file.id,message.outgoing);int[] box=chatMedia.get(fileKey);
                value.append('\0').append(file.id).append('\0').append(file.name).append('\0').append(file.size).append('\0').append(chatFiles.get(fileKey)).append('\0').append(box==null?"":box[0]+"x"+box[1]);
            }
            String signature=value.toString();rowSignatures.put(messageKey(message),signature);fingerprint.append('\0').append(signature);
        }
        boolean newOutgoing=!historySignature.isEmpty()&&rows.stream().anyMatch(m->m.outgoing&&!displayedOutgoing.contains(m.number));
        if(fingerprint.toString().equals(historySignature)) { transfersChanged(); if(pendingMessageLocation!=null)positionBeforeDraw(false,scroll.getScrollY());else if(forceLatest) {forceLatest=false;scrollToLatest();} return; }
        fileProgressViews.clear(); progressPeer=selected;
        displayedOutgoing.clear(); for(LanNode.Message m:rows) if(m.outgoing) displayedOutgoing.add(m.number);
        int previousY=scroll.getScrollY();
        boolean bottom=scroll.getChildAt(0).getHeight()-previousY-scroll.getHeight()<dp(100); boolean first=historySignature.isEmpty(); historySignature=fingerprint.toString();messageViews.clear();messageAvatarViews.clear();
        List<View> children=new ArrayList<>();Map<String,ChatRow> nextRows=new HashMap<>();Map<String,TextView> dates=new HashMap<>();
        for(int i=0;i<messages.getChildCount();i++){View child=messages.getChildAt(i);if(child instanceof TextView&&child.getTag() instanceof String)dates.put((String)child.getTag(),(TextView)child);}
        int viewport=scroll.getWidth()>0?scroll.getWidth():getResources().getDisplayMetrics().widthPixels;
        int avatarGap=dp(6), avatarSlot=dp(48)+avatarGap;
        int rowWidth=viewport-messages.getPaddingLeft()-messages.getPaddingRight();
        int maxWidth=Math.max(dp(120),Math.min(dp(540),rowWidth-avatarSlot*2));
        String lastDate=""; int previewCount=0; float density=getResources().getDisplayMetrics().density;
        final String cardPeer=selected;final int cardGeneration=editorGeneration;
        for(int index=0;index<rows.size();index++) {
            final LanNode.Message m=rows.get(index); LanNode.Message next=index+1<rows.size()?rows.get(index+1):null;
            String date=dayKey(m.time); boolean newDay=!date.equals(lastDate);
            if(newDay) {
                TextView day=dates.get(date);String label=dayLabel(m.time);
                if(day==null){day=text(label,12,Color.WHITE);day.setTag(date);day.setGravity(Gravity.CENTER);day.setPadding(dp(10),dp(3),dp(10),dp(3));day.setBackground(shape(serviceFill(),99));}
                else if(!label.contentEquals(day.getText()))day.setText(label);
                int topMargin=dp(lastDate.isEmpty()?0:6);
                if(day.getLayoutParams()==null||((LinearLayout.LayoutParams)day.getLayoutParams()).topMargin!=topMargin){LinearLayout.LayoutParams d=new LinearLayout.LayoutParams(-2,-2);d.gravity=Gravity.CENTER_HORIZONTAL;d.topMargin=topMargin;d.bottomMargin=dp(10);day.setLayoutParams(d);}
                children.add(day);lastDate=date;
            }
            // 与 Windows 一致：同侧且相隔 5 分钟内的消息成组，组内间距收紧，只有组尾带尾巴。
            boolean groupFirst=newDay||!continues(rows.get(index-1),m), groupLast=next==null||!continues(m,next)||!dayKey(next.time).equals(date);
            String key=messageKey(m),rowSignature=rowSignatures.get(key)+"\0"+maxWidth+"/"+groupFirst+"/"+groupLast+"/"+Math.max(0,24-previewCount);
            ChatRow cached=chatRows.get(key);
            if(cached!=null&&rowSignature.equals(cached.signature)) {
                children.add(cached.row);nextRows.put(key,cached);messageViews.put(key,cached.bubble);messageAvatarViews.add(cached.portrait);fileProgressViews.addAll(cached.progress);previewCount+=cached.previews;continue;
            }
            ChatRow built=new ChatRow();built.signature=rowSignature;int firstProgress=fileProgressViews.size(),firstPreview=previewCount;
            LinearLayout messageRow=row();messageRow.setGravity((m.outgoing?Gravity.END:Gravity.START)|Gravity.BOTTOM);
            LinearLayout.LayoutParams rp=new LinearLayout.LayoutParams(-1,-2);rp.bottomMargin=dp(groupLast?10:3);messageRow.setLayoutParams(rp);children.add(messageRow);
            View portrait=new View(this) {
                @Override protected void onDraw(Canvas canvas) {
                    super.onDraw(canvas);AvatarDrawable image=m.outgoing?outgoingMessageAvatar:incomingMessageAvatar;
                    image.setBounds(0,0,getWidth(),getHeight());image.draw(canvas);
                }
            };
            portrait.setContentDescription(m.outgoing?"我的资料":(conversation==null?"联系人":conversation.displayName())+"的资料");portrait.setFocusable(true);
            portrait.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),null,shape(Color.WHITE,24)));
            portrait.setOnClickListener(v->{
                if(!cardPeer.equals(selected)||cardGeneration!=editorGeneration)return;
                if(m.outgoing)settings();else {ChatStore.Conversation contact=conversation(cardPeer);if(contact!=null)showContactProfile(contact);}
            });messageAvatarViews.add(portrait);
            LinearLayout.LayoutParams ap=new LinearLayout.LayoutParams(dp(48),dp(48));ap.bottomMargin=-dp(2);
            if(!m.outgoing){ap.rightMargin=avatarGap;messageRow.addView(portrait,ap);}else ap.leftMargin=avatarGap;
            final TextView[] menuStamp={null};
            LinearLayout bubble=new LinearLayout(this) {
                private boolean cardTouch;
                @Override public boolean dispatchTouchEvent(MotionEvent event) {
                    // 整个手势固定路由，空白时间行交给卡片处理长按；滚动取消仍走原生触摸语义。
                    Rect menu=new Rect();TextView target=menuStamp[0];boolean row=target!=null&&target.getParent()==this;
                    if(row){target.getHitRect(menu);menu.left=Math.max(menu.left,menu.right-dp(48));}
                    // 图片上的时间胶囊整块可点；坐标转换后仍可在多选模式打开消息菜单。
                    else if(target!=null&&target.getWidth()>0){menu.set(0,0,target.getWidth(),target.getHeight());offsetDescendantRectToMyCoords(target,menu);}
                    if(event.getActionMasked()==MotionEvent.ACTION_DOWN) {
                        boolean stampBlank=row&&event.getY()>=target.getTop()&&!menu.contains((int)event.getX(),(int)event.getY());
                        cardTouch=(selectingMessages||stampBlank)&&!menu.contains((int)event.getX(),(int)event.getY());
                    }
                    if(cardTouch) {boolean handled=onTouchEvent(event);if(event.getActionMasked()==MotionEvent.ACTION_UP||event.getActionMasked()==MotionEvent.ACTION_CANCEL)cardTouch=false;return handled;}
                    return super.dispatchTouchEvent(event);
                }
                @Override public boolean performClick() {boolean handled=super.performClick();if(!cardPeer.equals(selected)||cardGeneration!=editorGeneration)return handled;if(selectingMessages){toggleMessage(m);return true;}return handled;}
            };bubble.setOrientation(LinearLayout.VERTICAL);bubble.setClickable(true);bubble.setContentDescription("消息："+m.text);messageViews.put(messageKey(m),bubble);bubble.setBackground(new BubbleDrawable(m.outgoing?OUTGOING:SURFACE,bubbleShadow(),density,m.outgoing,groupFirst,groupLast));
            boolean retryRow=m.outgoing&&(m.state.contains("未确认")||m.state.equals("发送失败"));
            boolean solo=m.text.isEmpty()&&m.files.size()==1&&!retryRow&&(m.outgoing||conversation!=null)&&previewCount<24;
            if(solo){Protocol.Attachment file=m.files.get(0);solo=MediaPreview.supported(file.name)&&chatFiles.get(ChatService.fileKey(cardPeer,m.number,file.id,m.outgoing))!=null;}
            int mediaInset=dp(3),tail=dp(7);
            if(solo)bubble.setPadding(mediaInset+(m.outgoing?0:tail),mediaInset,mediaInset+(m.outgoing?tail:0),mediaInset+dp(1));
            else bubble.setPadding(dp(m.outgoing?14:21),dp(8),dp(m.outgoing?21:14),0);
            FrameLayout soloFrame=null;
            messageRow.addView(bubble,new LinearLayout.LayoutParams(-2,-2));if(m.outgoing)messageRow.addView(portrait,ap);
            if(!m.text.isEmpty()) {
                String normalized=MessageText.normalizeLineBreaks(m.text),preview=MessageText.preview(normalized);TextView content=text(preview,messageSize(),INK);selectable(content);MessageLinks.apply(content,BLUE);content.setMaxWidth(maxWidth-dp(35));content.setLineSpacing(dp(3),1);bubble.addView(content);
                if(!preview.equals(normalized)){TextView more=text("查看全文",14,BLUE);more.setMinHeight(dp(44));more.setGravity(Gravity.CENTER_VERTICAL);more.setFocusable(true);more.setOnClickListener(v->showFullText(m.text));bubble.addView(more);}
            }
            for(int fileIndex=0;fileIndex<m.files.size();fileIndex++) {
                Protocol.Attachment f=m.files.get(fileIndex);
                String fileKey=ChatService.fileKey(cardPeer,m.number,f.id,m.outgoing);Uri uri=chatFiles.get(fileKey);
                Runnable actions=m.outgoing?()->sentFileActions(uri,f.name,m,conversation==null?null:conversation.peer):conversation==null?()->copyText("文件名",f.name):()->fileActions(conversation.peer,m.number,f);
                Runnable tap=!m.outgoing&&conversation!=null&&uri==null?()->fileInvite(conversation.peer,m.number,f):actions;
                int gap=bubble.getChildCount()==0?0:dp(fileIndex==0?6:4);
                if(uri!=null&&(m.outgoing||conversation!=null)&&MediaPreview.supported(f.name)&&previewCount++<24) {
                    int[] original=chatMedia.get(fileKey);if(original==null)original=new int[]{1600,900};
                    int width=Math.min(dp(solo?280:260),maxWidth-(solo?mediaInset*2+tail:dp(35)));
                    int[] box=mediaBox(original[0],original[1],width,dp(solo?320:240),dp(solo?148:120),dp(solo?100:76),density);
                    float round=dp(solo?12:10),sharp=dp(4);float[] corners={round,round,round,round};
                    if(solo)corners=m.outgoing?new float[]{round,groupFirst?round:sharp,sharp,round}:new float[]{groupFirst?round:sharp,round,round,sharp};
                    int back=m.outgoing?OUTGOING:SURFACE;
                    FrameLayout frame=media.thumbnail(uri,f.name,new MediaPreview.Look(box[0],box[1],corners,blend(INK,back,DARK?.14f:.07f),MUTED,DARK?0x24ffffff:0x14000000),()->openFile(uri,f.name,false),actions);
                    LinearLayout.LayoutParams layout=new LinearLayout.LayoutParams(box[0],box[1]);layout.topMargin=gap;bubble.addView(frame,layout);
                    addMediaStatus(frame,m,f,conversation);
                    if(solo)soloFrame=frame;
                    else {
                        TextView caption=text(f.name,12,m.outgoing?outgoingMeta():MUTED);caption.setSingleLine();caption.setEllipsize(TextUtils.TruncateAt.MIDDLE);caption.setMinHeight(dp(26));caption.setGravity(Gravity.CENTER_VERTICAL);caption.setContentDescription(f.name+"，文件操作");
                        bubble.addView(caption,new LinearLayout.LayoutParams(box[0],-2));caption.setOnClickListener(v->actions.run());caption.setOnLongClickListener(v->{actions.run();return true;});
                    }
                } else addFileCard(bubble,m,f,conversation,uri,Math.min(dp(250),maxWidth-dp(35)),gap,tap,actions);
            }
            if(m.files.size()>1){
                TextView total=text("",12,MUTED);total.setMaxWidth(maxWidth-dp(35));bubble.addView(total);ChatService sourceService=service;ChatStore.Conversation sourceConversation=conversation;
                Runnable update=()->{
                    if(service!=sourceService||!cardPeer.equals(selected))return;
                    double bytes=0,done=0;int savedCount=0;
                    for(Protocol.Attachment file:m.files) {
                        ChatService.FileProgress p=chatProgress(m,file);
                        boolean saved=p!=null&&taskComplete(p.state);int percent=p==null?0:p.percent;
                        if(!m.outgoing&&sourceConversation!=null) {
                            saved=chatSaved(m,file);
                            if(!saved&&(p==null?ChatService.wasStored(m.state):ChatService.wasStored(p.state)))percent=0;
                        }
                        double weight=Math.max(1,file.size);bytes+=weight;done+=weight*(saved?100:percent)/100.0;if(saved)savedCount++;
                    }
                    String label="总进度 "+(int)(done*100/bytes)+"% · 已保存 "+savedCount+" / "+m.files.size();
                    if(!label.contentEquals(total.getText()))total.setText(label);
                };fileProgressViews.add(update);update.run();
            }
            if(retryRow) {
                TextView retry=text("发送未确认 · 点击重试",13,BLUE);retry.setPadding(0,dp(8),0,dp(8));bubble.addView(retry);
                retry.setOnClickListener(v->{
                    if(!m.files.isEmpty()){error("文件邀请重发请重新选择原文件，避免发送已过期的缓存");return;}
                    final LanNode.Peer retryPeer=conversation.peer; final int retryGeneration=editorGeneration;
                    confirm("重新发送","将更新原消息，不新增气泡；对方旧版客户端可能无法长期去重。","重新发送",()->{
                        if(service==null||retryGeneration!=editorGeneration||!selected.equals(retryPeer.id()))return;retry.setEnabled(false);
                        service.retryMessage(retryPeer,m,success->{if(isDestroyed())return;retry.setEnabled(!success);if(success&&retryGeneration==editorGeneration&&selected.equals(retryPeer.id())){forceLatest=true;changed();}});
                    });
                });
            }
            // 时间行保留整行点击高度并延伸到气泡底边，文字在行内垂直居中，气泡底部不再空出一截。
            TextView stamp=text("",10,solo?Color.WHITE:m.outgoing?outgoingMeta():MUTED);stamp.setText(messageMeta(new SimpleDateFormat("HH:mm",Locale.CHINA).format(new Date(m.time)),m,true,solo));stamp.setMaxWidth(maxWidth-dp(35));
            if(soloFrame!=null) {
                int near=dp(6),reach=dp(10);stamp.setSingleLine();stamp.setEllipsize(TextUtils.TruncateAt.START);stamp.setGravity(Gravity.CENTER);
                stamp.setBackground(new android.graphics.drawable.InsetDrawable(shape(0x73000000,10),reach,reach,near,near));
                stamp.setPadding(reach+dp(7),reach+dp(2),near+dp(7),near+dp(2));stamp.setMaxWidth(soloFrame.getLayoutParams().width);
                soloFrame.addView(stamp,new FrameLayout.LayoutParams(-2,-2,Gravity.BOTTOM|Gravity.END));
            } else {stamp.setGravity(Gravity.END|Gravity.CENTER_VERTICAL);bubble.addView(stamp,new LinearLayout.LayoutParams(-1,-2));}
            menuStamp[0]=stamp;
            final LanNode.Peer source=conversation==null?null:conversation.peer;
            bubble.setOnLongClickListener(v->{if(cardPeer.equals(selected)&&cardGeneration==editorGeneration){if(selectingMessages)toggleMessage(m);else beginMessageSelection(m);}return true;});
            stamp.append("  ···"); stamp.setContentDescription("消息操作：复制、转发、删除、多选"); stamp.setMinHeight(dp(solo?36:m.files.isEmpty()?40:32));
            stamp.setOnClickListener(v->{if(source!=null) messageActions(m,source);});
            built.row=messageRow;built.bubble=bubble;built.portrait=portrait;built.previews=previewCount-firstPreview;
            built.progress.addAll(fileProgressViews.subList(firstProgress,fileProgressViews.size()));nextRows.put(key,built);
        }
        // 只移除或插入实际变化的行，保留其余文本布局、选区和已解码媒体。
        Set<View> retained=new HashSet<>(children);
        for(int i=messages.getChildCount()-1;i>=0;i--)if(!retained.contains(messages.getChildAt(i)))messages.removeViewAt(i);
        for(int i=0;i<children.size();i++){View child=children.get(i);if(messages.getChildAt(i)==child)continue;if(child.getParent()==messages)messages.removeView(child);messages.addView(child,i);}
        chatRows.clear();chatRows.putAll(nextRows);
        updateMessageSelection();
        boolean latest=first||bottom||newOutgoing||forceLatest; forceLatest=false;
        if(themeScrollPosition!=null){previousY=themeScrollPosition;latest=false;themeScrollPosition=null;}positionBeforeDraw(latest,previousY);
    }
    private void scrollToLatest() {
        positionBeforeDraw(true,0);
    }
    private static String dayKey(long time) { return new SimpleDateFormat("yyyy-MM-dd",Locale.CHINA).format(new Date(time)); }
    // 日期胶囊文案与 Windows 相同：今天、昨天，其余显示月日，跨年时带年份。
    private static String dayLabel(long time) {
        Calendar now=Calendar.getInstance(),day=Calendar.getInstance();day.setTimeInMillis(time);String key=dayKey(time);
        if(key.equals(dayKey(now.getTimeInMillis()))) return "今天";
        int year=now.get(Calendar.YEAR);now.add(Calendar.DAY_OF_MONTH,-1);if(key.equals(dayKey(now.getTimeInMillis()))) return "昨天";
        return new SimpleDateFormat(day.get(Calendar.YEAR)==year?"M月d日":"yyyy年M月d日",Locale.CHINA).format(new Date(time));
    }
    private static boolean continues(LanNode.Message previous,LanNode.Message next) { long gap=next.time-previous.time; return previous.outgoing==next.outgoing&&gap>-60000&&gap<300000; }
    @Override public void transfersChanged() {
        if(taskAdapter!=null&&tasksDialog!=null&&tasksDialog.isShowing()){taskAdapter.requestRefresh();return;}
        if(historyDialog!=null&&historyDialog.isShowing())return;
        // 进度只更新当前卡片，不重新查询历史/创建全部气泡，也不改变用户的阅读位置。
        if(!visible||service==null||!selected.equals(progressPeer))return;
        if(scroll instanceof ChatScroll&&((ChatScroll)scroll).moving())return;
        if(!chatProgressPending){chatProgressPending=true;chatUpdates.postDelayed(refreshChatProgress,120);}
    }
    private static final class CardState {
        int percent;boolean active,receivable,ended;String label;
    }
    private CardState cardState(LanNode.Message message,Protocol.Attachment file,ChatStore.Conversation conversation) {
        CardState card=new CardState();ChatService.FileProgress progress=chatProgress(message,file);
        int fileIndex=message.files.indexOf(file);
        boolean outgoingUnavailable=message.outgoing&&fileIndex<message.localPaths.size()&&chatFiles.get(ChatService.fileKey(selected,message.number,file.id,true))==null;
        boolean saved=!message.outgoing&&conversation!=null&&chatSaved(message,file);
        int percent=progress!=null?progress.percent:saved?100:0;
        String state=progress!=null?progress.state:message.outgoing?(service.offering(selected,message.number,file.id)?"等待对方接收":"历史邀请 · 无活动传输"):saved?"已保存":"待接收 · 点击接收";
        if(!message.outgoing&&(progress==null||!taskActive(progress.state))) {
            if(saved){state="已保存";percent=100;}
            else if(ChatService.wasStored(message.state)||progress!=null&&ChatService.wasStored(progress.state)){state="本机文件已删除";percent=0;}
        }
        if(outgoingUnavailable)state="文件已删除或不可用";
        card.percent=percent;card.active=progress!=null&&!outgoingUnavailable&&taskActive(state)&&percent<100;
        card.receivable=!message.outgoing&&!saved&&!card.active&&(state.startsWith("待接收")||state.contains("重试")||state.contains("续传")||state.contains("点击接收"));
        card.ended=!card.active&&!card.receivable&&(message.outgoing?outgoingUnavailable:!saved);
        String rate=progress!=null&&(state.equals("发送中")||state.startsWith("接收中"))?" · "+size((long)progress.bytesPerSecond)+"/s":"";
        card.label=card.active?percent+"%"+rate+" · "+state:size(file.size)+" · "+state;
        return card;
    }
    private void addFileCard(LinearLayout bubble,LanNode.Message message,Protocol.Attachment file,ChatStore.Conversation conversation,Uri uri,int width,int gap,Runnable tap,Runnable actions) {
        String peer=selected;ChatService source=service;
        LinearLayout card=row();card.setMinimumHeight(dp(52));LinearLayout.LayoutParams layout=new LinearLayout.LayoutParams(width,-2);layout.topMargin=gap;bubble.addView(card,layout);
        FileBadge badge=new FileBadge(this,file.name);badge.bind(FileBadge.FILE,0,BLUE);card.addView(badge,new LinearLayout.LayoutParams(dp(44),dp(44)));
        LinearLayout words=column();LinearLayout.LayoutParams textLayout=weight();textLayout.leftMargin=dp(10);card.addView(words,textLayout);
        TextView name=text(file.name,15,INK);name.setTypeface(null,Typeface.BOLD);name.setSingleLine();name.setEllipsize(TextUtils.TruncateAt.MIDDLE);words.addView(name);
        TextView label=text("",12,message.outgoing?outgoingMeta():MUTED);label.setMaxLines(2);label.setEllipsize(TextUtils.TruncateAt.END);label.setPadding(0,dp(3),0,0);words.addView(label);
        card.setContentDescription(file.name+(message.outgoing&&uri==null?"，文件已删除或不可用":uri!=null?"，文件操作":"，接收文件"));
        card.setOnClickListener(v->tap.run());card.setOnLongClickListener(v->{actions.run();return true;});
        // 取消接收只放在叉号和状态文字上，点击文件名仍打开操作面板，避免误触清除断点。
        View.OnClickListener control=v->{if(service!=source||source==null||!selected.equals(peer))return;ChatService.FileProgress p=source.progress(peer,message.number,file.id,false);if(!message.outgoing&&p!=null&&p.task!=null)source.cancelFile(peer,message.number,file.id);else card.performClick();};
        badge.setOnClickListener(control);label.setOnClickListener(control);badge.setOnLongClickListener(v->card.performLongClick());label.setOnLongClickListener(v->card.performLongClick());
        Runnable update=()->{
            if(service!=source||source==null||!selected.equals(peer))return;
            CardState state=cardState(message,file,conversation);int back=message.outgoing?OUTGOING:SURFACE;
            badge.bind(state.active?(message.outgoing?FileBadge.SENDING:FileBadge.RECEIVING):state.receivable?FileBadge.DOWNLOAD:FileBadge.FILE,state.percent,state.ended?blend(MUTED,back,.55f):BLUE);
            if(!state.label.contentEquals(label.getText())){label.setText(state.label);badge.setContentDescription(file.name+"，"+state.label);}
        };
        fileProgressViews.add(update);update.run();
    }
    private static int[] mediaBox(int width,int height,int maxWidth,int maxHeight,int minWidth,int minHeight,float density) {
        // 普通照片保留比例；极长、极宽和小图才限制最小边，用居中裁切填满圆角框。
        float scale=Math.min(density,Math.min((float)maxWidth/width,(float)maxHeight/height));
        return new int[]{Math.max(Math.min(minWidth,maxWidth),Math.round(width*scale)),Math.max(Math.min(minHeight,maxHeight),Math.round(height*scale))};
    }
    private void addMediaStatus(FrameLayout frame,LanNode.Message message,Protocol.Attachment file,ChatStore.Conversation conversation) {
        String peer=selected;ChatService source=service;MediaPreview.Picture picture=(MediaPreview.Picture)frame.getChildAt(0);
        TextView label=text("",11,Color.WHITE);label.setSingleLine();label.setEllipsize(TextUtils.TruncateAt.END);label.setMaxWidth(frame.getLayoutParams().width-dp(12));label.setPadding(dp(7),dp(2),dp(7),dp(2));label.setBackground(shape(0x73000000,10));
        FrameLayout.LayoutParams layout=new FrameLayout.LayoutParams(-2,-2,Gravity.TOP|Gravity.START);layout.leftMargin=dp(6);layout.topMargin=dp(6);frame.addView(label,layout);
        Runnable update=()->{if(service!=source||source==null||!selected.equals(peer))return;CardState state=cardState(message,file,conversation);if(!state.label.contentEquals(label.getText())){label.setText(state.label);label.setContentDescription(file.name+"，"+state.label);}picture.progress(state.active?state.percent:-1);};
        fileProgressViews.add(update);update.run();
    }
    private long scrollPositionVersion;
    private void locateHistoryMessage(String peer,LanNode.Message message) {
        if(!selected.equals(peer))open(peer);
        finishSelection();endMessageSelection();forceLatest=false;
        pendingMessageLocation=messageKey(message);
        if(archiveAnchor!=null || !messageViews.containsKey(pendingMessageLocation))archiveAnchor=message;
        historyAppliedVersion=++historyRequestVersion;
        changed();
    }
    private void positionBeforeDraw(boolean latest,int previousY) {
        final ScrollView target=scroll; final String peer=selected;
        if(target==null) return;
        final long version=++scrollPositionVersion;
        // post 不保证在布局后执行；在首帧绘制前定位，避免先展示顶部再跳到末尾。
        target.getViewTreeObserver().addOnPreDrawListener(new ViewTreeObserver.OnPreDrawListener() {
            @Override public boolean onPreDraw() {
                if(target.getViewTreeObserver().isAlive()) target.getViewTreeObserver().removeOnPreDrawListener(this);
                if(!isDestroyed()&&scroll==target&&selected.equals(peer)&&version==scrollPositionVersion) {
                    if(pendingMessageLocation!=null) {
                        String key=pendingMessageLocation;pendingMessageLocation=null;View message=messageViews.get(key);
                        if(message==null){error("该消息已删除或不在当前可加载的聊天记录中。");return true;}
                        Rect bounds=new Rect(0,0,message.getWidth(),message.getHeight());messages.offsetDescendantRectToMyCoords(message,bounds);
                        int available=target.getHeight()-target.getPaddingTop()-target.getPaddingBottom();
                        target.scrollTo(0,messages.getTop()+bounds.top-Math.max(dp(12),(available-Math.min(bounds.height(),available-dp(24)))/2));
                        locatedMessage=key;updateMessageSelection();
                    } else target.scrollTo(0,latest?target.getChildAt(0).getHeight():previousY);
                }
                return true;
            }
        });
        target.invalidate();
    }
    private void forward(LanNode.Message message,LanNode.Peer source) {
        if(service==null||!service.online()) {error("请先连接局域网");return;}
        LinearLayout options=column();
        TextView hint=text(message.files.isEmpty()?"转发文本 · 选择联系人后确认发送":"转发 "+message.files.size()+" 个附件及正文 · 选择联系人",13,MUTED);
        hint.setPadding(dp(6),dp(4),dp(6),dp(14));options.addView(hint);
        LinearLayout contacts=column();ScrollView list=new ScrollView(this);list.addView(contacts);
        options.addView(list,new LinearLayout.LayoutParams(-1,dp(280)));
        final ChatStore.Conversation[] target={null};final boolean[] submitted={false};
        List<View> rows=new ArrayList<>();List<TextView> marks=new ArrayList<>();
        Button send=button("确认转发",true);send.setEnabled(false);send.setAlpha(.45f);
        LinearLayout footer=row();footer.setGravity(Gravity.END);footer.setPadding(0,dp(16),0,0);footer.addView(send,new LinearLayout.LayoutParams(-2,-2));options.addView(footer);
        for(ChatStore.Conversation c:service.conversations()) {
            if(!c.peer.online)continue;
            LinearLayout item=row();item.setMinimumHeight(dp(72));item.setPadding(dp(4),dp(6),dp(10),dp(6));item.setBackground(shape(SURFACE,CONTROL_RADIUS));
            TextView avatar=text("",19,Color.WHITE);avatar.setGravity(Gravity.CENTER);bindAvatar(avatar,c.peer.id(),c.displayName(),true,c.peer.avatar);item.addView(avatar,new LinearLayout.LayoutParams(dp(56),dp(56)));
            LinearLayout identity=column();LinearLayout.LayoutParams ip=weight();ip.leftMargin=dp(10);item.addView(identity,ip);
            TextView name=text(c.displayName(),16,INK);name.setSingleLine();name.setEllipsize(c.duplicateName?TextUtils.TruncateAt.MIDDLE:TextUtils.TruncateAt.END);identity.addView(name);
            TextView address=text(c.peer.endpoint.getAddress().getHostAddress()+":"+c.peer.endpoint.getPort(),12,MUTED);address.setSingleLine();address.setEllipsize(TextUtils.TruncateAt.END);address.setPadding(0,dp(4),0,0);identity.addView(address);
            TextView mark=text("",18,BLUE);mark.setGravity(Gravity.CENTER);item.addView(mark,new LinearLayout.LayoutParams(dp(28),dp(40)));marks.add(mark);
            item.setContentDescription("转发给 "+c.displayName());item.setFocusable(true);rows.add(item);contacts.addView(item,new LinearLayout.LayoutParams(-1,-2));
            item.setOnClickListener(v->{
                if(submitted[0])return;target[0]=c;
                for(int i=0;i<rows.size();i++){boolean chosen=rows.get(i)==v;rows.get(i).setSelected(chosen);rows.get(i).setBackground(shape(chosen?BUBBLE:SURFACE,CONTROL_RADIUS));marks.get(i).setText(chosen?"✓":"");}
                send.setEnabled(true);send.setAlpha(1f);
            });
        }
        if(rows.isEmpty()){contacts.addView(text("没有在线联系人，请先连接或添加联系人。",14,MUTED));list.getLayoutParams().height=dp(100);}
        Dialog chooser=sheet("转发到",options,null,null);
        send.setOnClickListener(v->{
            if(submitted[0]||target[0]==null||service==null)return;
            ChatStore.Conversation chosen=target[0];int page=editorGeneration;submitted[0]=true;send.setEnabled(false);send.setText("正在转发…");
            for(View item:rows)item.setEnabled(false);
            service.forward(source,message,chosen.peer,success->{
                if(isDestroyed()||isFinishing())return;
                submitted[0]=success;
                if(success){chooser.dismiss();error("已转发");if(page==editorGeneration&&selected.equals(chosen.peer.id())){forceLatest=true;changed();}}
                else if(chooser.isShowing()){hint.setText("转发未完成，请检查附件或连接后重试。");send.setText("确认转发");send.setEnabled(true);for(View item:rows)item.setEnabled(true);}
            });
        });
    }
    private void showFullText(String value) {
        TextView content=text(MessageText.normalizeLineBreaks(value),messageSize(),INK);selectable(content);content.setPadding(dp(4),dp(10),dp(4),dp(12));
        sheet("消息全文（长按选择复制）",content,"关闭",Dialog::dismiss);
    }
    private void messageActions(LanNode.Message message,LanNode.Peer source){
        int generation=editorGeneration;
        LinearLayout options=column();Dialog dialog=sheet("消息",options,null,null);
        if(message.outgoing&&service!=null&&service.canCancelOutgoing(source.id(),message.number))fileAction(options,dialog,"取消发送",()->{if(selected.equals(source.id())&&generation==editorGeneration)requestCancelOutgoing(message,source);});
        if(!message.text.isEmpty())fileAction(options,dialog,"复制消息",()->copyText("消息",MessageText.normalizeLineBreaks(message.text)));
        if(!message.text.isEmpty())fileAction(options,dialog,"查看全文 / 选段复制",()->showFullText(message.text));
        fileAction(options,dialog,"转发…",()->forward(message,source));
        fileAction(options,dialog,"选择多条消息",()->{if(selected.equals(source.id())&&generation==editorGeneration)beginMessageSelection(message);});
        if(selectingMessages)fileAction(options,dialog,"删除所选本机文件",()->{if(selected.equals(source.id())&&generation==editorGeneration)requestDeleteSelectedFiles();});
        fileAction(options,dialog,"删除这条消息",()->{if(selected.equals(source.id())&&generation==editorGeneration)requestDeleteMessages(Collections.singletonList(message));});
    }
    private static String messageKey(LanNode.Message message) {return message.number+":"+message.outgoing;}
    private void beginMessageSelection(LanNode.Message message) {finishSelection();selectingMessages=true;messageMarks.clear();messageMarks.add(messageKey(message));updateMessageSelection();}
    private void toggleMessage(LanNode.Message message) {String key=messageKey(message);if(!messageMarks.add(key))messageMarks.remove(key);updateMessageSelection();}
    private void endMessageSelection() {selectingMessages=false;messageMarks.clear();updateMessageSelection();}
    private void updateMessageSelection() {
        if(messageSelectionBar!=null){messageSelectionBar.setVisibility(selectingMessages?View.VISIBLE:View.GONE);messageSelectionCount.setText("已选 "+messageMarks.size()+" 条");deleteSelectedMessages.setEnabled(!messageMarks.isEmpty());deleteSelectedFiles.setEnabled(messageRows.stream().anyMatch(message->messageMarks.contains(messageKey(message))&&!message.outgoing&&!message.files.isEmpty()));}
        for(Map.Entry<String,View> entry:messageViews.entrySet()) {
            if(selectingMessages?messageMarks.contains(entry.getKey()):entry.getKey().equals(locatedMessage)){GradientDrawable border=shape(Color.TRANSPARENT,CONTROL_RADIUS);border.setStroke(dp(2),BLUE);entry.getValue().setForeground(border);}
            else entry.getValue().setForeground(null);
        }
    }
    private void requestCancelOutgoing(LanNode.Message message,LanNode.Peer target) {
        if(message==null||target==null||service==null)return;
        ChatService source=service;String peer=target.id();int generation=editorGeneration;
        confirm("取消发送？",message.files.isEmpty()?"将停止继续发送这条尚未确认送达的消息。":"将停止发送并撤销对方尚未接收的文件邀请；本机原文件会保留。","取消发送",()->{
            if(service!=source||!peer.equals(selected)||generation!=editorGeneration)return;
            source.cancelOutgoing(peer,message,success->{if(success&&!isDestroyed()&&!isFinishing()&&service==source&&peer.equals(selected)&&generation==editorGeneration)changed();});
        });
    }
    private void requestDeleteMessages(List<LanNode.Message> messages) {
        if(messages.isEmpty()||service==null)return;ChatService source=service;String peer=selected;int generation=editorGeneration;
        boolean undelivered=messages.stream().anyMatch(message->message.outgoing&&source.hasUndeliveredOutgoing(peer,message));
        String body=undelivered?"将停止发送尚未送达的消息并撤销对应文件邀请；只删除本机的消息记录，对方已收到的记录和本机附件文件会保留。":"仅删除这台设备上的记录，对方记录和附件文件会保留。";
        confirm(messages.size()==1?"删除这条消息？":"删除选中的 "+messages.size()+" 条消息？",body,"删除",()->{
            if(service!=source||!peer.equals(selected)||generation!=editorGeneration)return;
            source.deleteMessages(peer,messages,success->{if(success&&!isDestroyed()&&!isFinishing()&&service==source&&peer.equals(selected)&&generation==editorGeneration){endMessageSelection();changed();}});
        });
    }
    private void requestDeleteSelectedFiles() {
        List<LanNode.Message> selectedRows=new ArrayList<>();
        for(LanNode.Message message:messageRows)if(messageMarks.contains(messageKey(message)))selectedRows.add(message);
        if(selectedRows.stream().noneMatch(message->!message.outgoing&&!message.files.isEmpty())) {
            error("所选消息没有接收附件，发送的原文件不能通过聊天删除");return;
        }
        if(service==null){error("连接服务尚未就绪，请稍后重试");return;}
        ChatService source=service;String peer=selected;int generation=editorGeneration;
        confirm("删除选中的本机文件？","将删除所选消息中已保存的本机接收文件；聊天记录保留，正在接收或处理的文件及已删除文件自动跳过。此操作无法撤销。","删除文件",()->{
            if(service!=source||!peer.equals(selected)||generation!=editorGeneration)return;
            source.deleteReceivedFiles(peer,selectedRows,result->{
                if(isDestroyed()||isFinishing()||service!=source||!peer.equals(selected)||generation!=editorGeneration)return;
                if(result.skippedBusy==0)endMessageSelection();changed();
                if(result.deleted==0&&result.skippedDeleted==0&&result.skippedBusy==0)error("所选消息没有已保存的本机文件（未接收附件请在传输任务中处理）");
                else error("已删除 "+result.deleted+" 个本机文件，"+result.skippedDeleted+" 个已删除跳过。"+(result.skippedBusy>0?result.skippedBusy+" 个正在接收或处理的文件已跳过。":"")+(result.unreceived>0?"（另有 "+result.unreceived+" 个未接收附件未处理）":""));
            });
        });
    }
    private void send() {
        ChatStore.Conversation current=conversation(selected); if(sending)return;
        if(service==null||!service.online()){error("请先连接局域网");return;}
        if(current==null){error("会话尚未就绪，请稍后重试");return;}
        String value=MessageText.normalizeLineBreaks(editor.getText().toString()); if(value.trim().isEmpty()) return;
        sending=true;sendingPeer=selected;updateSendFeedback();String peer=selected;saveDraft();
        service.send(current.peer,value,success->{
            if(isDestroyed() || isFinishing()) return;
            sending=false;sendingPeer="";updateSendFeedback();
            if(success && selected.equals(peer)) { forceLatest=true; changed(); }
        });
    }
    private void updateSendFeedback() {
        if(sendButton!=null){sendButton.setEnabled(!sending);sendButton.setAlpha(sending?.5f:1f);sendButton.setContentDescription(sending?"正在发送，请勿重复点击":"发送消息");}
        if(sendStatus!=null){boolean pending=sending&&selected.equals(sendingPeer),longText=editor!=null&&MessageText.asFile(editor.getText().toString());sendStatus.setText(pending?"正在发送…":"长文本将作为 TXT 附件发送");sendStatus.setVisibility(pending||longText?View.VISIBLE:View.GONE);}
    }
    private void connect() {
        if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED) requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},10);
        try { startForegroundService(new Intent(this,ChatService.class)); } catch(RuntimeException e) { error("无法启动连接："+e.getMessage()); }
    }
    private void restoreConnection() {
        if(!resumed || !visible || service==null || restoreAttempted) return;
        if(!ChatService.shouldRestoreConnection(this) || service.connectionActive()) return;
        // 安装器返回和服务绑定可能早于 onResume；页面真正回到前台后再启动前台服务。
        try { startForegroundService(new Intent(this,ChatService.class).setAction("restore"));restoreAttempted=true; }
        catch(RuntimeException e) { error("自动连接失败，可在菜单中重试："+e.getMessage()); }
    }
    private void showMenu(View anchor) {
        LinearLayout options=column(); Dialog dialog=sheet("飞Q",options,null,null);
        Intent lastRemote=RemoteActivity.lastConnection(this);
        if(lastRemote!=null)fileAction(options,dialog,"上次远程连接",()->{
            ChatStore.Conversation remembered=conversation(getSharedPreferences("settings",MODE_PRIVATE).getString("last_remote_peer",""));
            if(remembered==null){error("请从联系人重新打开一次远控，以核对设备身份");return;}
            if(lastRemote.getBooleanExtra("phone",false))openPhone(remembered);else openTools(remembered,true);
        });
        // 传输任务已移到主页顶栏；刷新改为会话列表下拉，菜单不再重复提供。
        String[] labels={"临时浏览器传输","添加联系人","共享本机文件夹","允许控制本机","已移除的联系人","设置","关于",service!=null&&service.canDisconnect()?"断开":"连接"};
        for(String label:labels) {
            fileAction(options,dialog,label,()->{ if(label.equals("临时浏览器传输"))showGuest();else if(label.equals("允许控制本机"))startActivity(new Intent(this,PhoneRemoteActivity.class));else if(label.equals("共享本机文件夹")) sharing(); else if(label.equals("已移除的联系人")) hiddenConversations(); else if(label.equals("设置")) settings(); else if(label.equals("添加联系人")) addPeer(); else if(label.equals("断开")) { if(service!=null) service.disconnect(); } else if(label.equals("关于")) showAbout(); else connect(); });
        }
    }
    private void showAbout() {
        LinearLayout content=column(),brand=row();brand.setPadding(0,dp(4),0,dp(10));
        ImageView logo=new ImageView(this);logo.setImageDrawable(getApplicationInfo().loadIcon(getPackageManager()));logo.setContentDescription("飞Q图标");brand.addView(logo,new LinearLayout.LayoutParams(dp(48),dp(48)));
        LinearLayout identity=column();identity.setPadding(dp(14),0,0,0);TextView name=text("飞Q",20,INK);name.setTypeface(null,Typeface.BOLD);identity.addView(name);identity.addView(text("Android  "+BuildConfig.VERSION_NAME+" · 局域网聊天与文件传输",12,MUTED));brand.addView(identity,weight());content.addView(brand);
        LinearLayout repository=row();repository.setPadding(dp(12),dp(10),dp(12),dp(10));
        GradientDrawable repoBg=shape(PALE,CONTROL_RADIUS);repoBg.setStroke(dp(1),LINE);
        repository.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),repoBg,null));repository.setFocusable(true);repository.setContentDescription("GitHub 仓库：github.com/noway1467/fq");
        IconButton source=new IconButton("code","源码",BLUE);source.setClickable(false);source.setFocusable(false);source.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);repository.addView(source,new LinearLayout.LayoutParams(dp(38),dp(40)));
        LinearLayout labels=column();labels.setPadding(dp(8),0,0,0);labels.addView(text("GitHub 仓库",14,BLUE));labels.addView(text("github.com/noway1467/fq",12,MUTED));repository.addView(labels,weight());repository.setOnClickListener(v->{try{startActivity(new Intent(Intent.ACTION_VIEW,Uri.parse(GITHUB_REPOSITORY)).addCategory(Intent.CATEGORY_BROWSABLE));}catch(ActivityNotFoundException e){error("没有可用的浏览器");}});content.addView(repository,new LinearLayout.LayoutParams(-1,-2));
        TextView note=text("默认 TLS 加密 · 首次核对配对码\n内置头像：Twemoji · CC BY 4.0（© Twitter, Inc. 及贡献者）",12,MUTED);note.setPadding(dp(2),dp(12),dp(2),0);note.setLineSpacing(dp(3),1);content.addView(note);
        sheet("关于飞Q",content,"确定",Dialog::dismiss);
    }
    private void hiddenConversations() {
        LinearLayout box=column();Dialog dialog=sheet("已移除的联系人",box,null,null);
        if(service==null)return;
        if(service.hiddenConversations().isEmpty())box.addView(text("没有已移除的联系人",14,MUTED));
        for(ChatStore.Conversation c:service.hiddenConversations())fileAction(box,dialog,"恢复 · "+c.displayName(),()->{if(service!=null)service.removeConversation(c.peer.id(),false,false,success->{});});
    }
    private void showContactProfile(ChatStore.Conversation c) {
        String peer=selected;int generation=editorGeneration;ChatService source=service;
        LinearLayout content=column();TextView avatar=text("",18,INK);bindAvatar(avatar,c.peer.id(),c.displayName(),c.peer.online,c.peer.avatar);avatar.setContentDescription("联系人资料头像，点击查看大图");
        LinearLayout.LayoutParams portrait=new LinearLayout.LayoutParams(dp(76),dp(76));portrait.gravity=Gravity.CENTER_HORIZONTAL;portrait.bottomMargin=dp(6);content.addView(avatar,portrait);avatar.setOnClickListener(v->showAvatarPreview(c.peer));
        EditText nickname=formField(content,"昵称",c.note==null||c.note.isEmpty()?c.peer.name:c.note);nickname.setContentDescription("联系人资料昵称");nickname.setHint("留空恢复对方昵称");nickname.setHintTextColor(MUTED);nickname.setFilters(new InputFilter[]{new InputFilter.LengthFilter(64)});
        LinearLayout infoCard=column();GradientDrawable cardBg=shape(PALE,CONTROL_RADIUS);cardBg.setStroke(dp(1),LINE);infoCard.setBackground(cardBg);infoCard.setPadding(dp(14),dp(4),dp(14),dp(10));
        if(c.peer.group!=null&&!c.peer.group.trim().isEmpty())profileField(infoCard,"部门",c.peer.group,"联系人资料部门");
        profileField(infoCard,"IP 地址",c.peer.endpoint.getAddress().getHostAddress()+":"+c.peer.endpoint.getPort(),"联系人资料地址");
        profileField(infoCard,"传输安全",c.peer.fingerprint==null?(ChatService.allowLegacy(getSharedPreferences("settings",MODE_PRIVATE))?"旧版设备 · 明文兼容":"旧版设备 · 明文已关"):"TLS 加密","联系人安全状态");
        LinearLayout.LayoutParams cardParams=new LinearLayout.LayoutParams(-1,-2);cardParams.topMargin=dp(12);content.addView(infoCard,cardParams);
        if(c.peer.fingerprint!=null){Button forget=button("撤销设备信任",false);LinearLayout.LayoutParams fp=new LinearLayout.LayoutParams(-1,-2);fp.topMargin=dp(10);content.addView(forget,fp);forget.setOnClickListener(v->confirm("撤销设备信任","下次连接将重新核对两端配对码。","撤销",()->{if(service!=null)service.forgetSecurity(c.peer);}));}
        Dialog dialog=sheet("联系人资料",content,"保存",d->{
            if(service!=source||source==null||!selected.equals(peer)||editorGeneration!=generation){d.dismiss();return;}
            String value=nickname.getText().toString().trim();source.editConversation(c.peer.id(),value.equals(c.peer.name)?"":value,c.pinned);d.dismiss();
        });
        dialog.getWindow().setGravity(Gravity.CENTER);dialog.getWindow().setLayout(Math.min(getResources().getDisplayMetrics().widthPixels-dp(24),dp(420)),ViewGroup.LayoutParams.WRAP_CONTENT);
    }
    private void profileField(LinearLayout content,String name,String value,String description) {
        if(content.getChildCount()>0){View divider=new View(this);divider.setBackgroundColor(LINE);LinearLayout.LayoutParams dpLine=new LinearLayout.LayoutParams(-1,dp(1));dpLine.topMargin=dp(8);content.addView(divider,dpLine);}
        TextView label=text(name,11,MUTED);label.setPadding(0,dp(8),0,dp(2));content.addView(label);
        TextView field=text(value,14,INK);field.setContentDescription(description);selectable(field);content.addView(field);
    }
    private void showAvatarPreview(LanNode.Peer peer) {
        String value=Protocol.normalizeAvatar(peer.avatar);Bitmap bitmap;
        if(value!=null&&value.startsWith("image:")) {byte[] data=java.util.Base64.getDecoder().decode(value.substring(6));bitmap=BitmapFactory.decodeByteArray(data,0,data.length);}
        else {bitmap=Bitmap.createBitmap(320,320,Bitmap.Config.ARGB_8888);AvatarDrawable face=new AvatarDrawable(this,peer.id(),false,0,SURFACE,ONLINE,value);face.setBounds(0,0,320,320);face.draw(new Canvas(bitmap));}
        if(bitmap==null){error("头像图片无法解码");return;}
        Dialog dialog=new Dialog(this,DARK?android.R.style.Theme_Material_NoActionBar:android.R.style.Theme_Material_Light_NoActionBar);dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);LinearLayout content=column();content.setBackgroundColor(SURFACE);
        if(Build.VERSION.SDK_INT>=30){content.setOnApplyWindowInsetsListener((v,insets)->{Insets b=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout());v.setPadding(b.left,b.top,b.right,b.bottom);return insets;});}else content.setFitsSystemWindows(true);
        Button close=button("关闭头像",false);content.addView(close,new LinearLayout.LayoutParams(-1,-2));
        MediaPreview.ZoomImage image=new MediaPreview.ZoomImage(this);image.setContentDescription("头像大图");image.setBackgroundColor(SURFACE);image.setImageBitmap(bitmap);content.addView(image,new LinearLayout.LayoutParams(-1,0,1));
        dialog.setContentView(content);close.setOnClickListener(v->dialog.dismiss());dialog.setOnDismissListener(d->{image.setImageDrawable(null);bitmap.recycle();});SystemBars.apply(dialog.getWindow(),SURFACE,DARK);dialog.show();if(dialog.getWindow()!=null){dialog.getWindow().setLayout(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.MATCH_PARENT);SystemBars.apply(dialog.getWindow(),SURFACE,DARK);}
    }
    private void conversationMenu(ChatStore.Conversation c) {
        LinearLayout options=column();Dialog menu=sheet(c.displayName(),options,null,null);
        fileAction(options,menu,"聊天记录",()->showHistory(c));
        fileAction(options,menu,"浏览共享文件",()->openTools(c,false));
        fileAction(options,menu,"控制 Windows 桌面",()->openTools(c,true));
        fileAction(options,menu,"控制 Android 手机",()->openPhone(c));
        fileAction(options,menu,"设置备注",()->{
            LinearLayout box=column();EditText note=formField(box,"仅本机显示，留空恢复昵称",c.note==null?"":c.note);note.setFilters(new InputFilter[]{new InputFilter.LengthFilter(64)});
            sheet("联系人备注",box,"保存",dialog->{if(service!=null)service.editConversation(c.peer.id(),note.getText().toString(),c.pinned);dialog.dismiss();});
        });
        fileAction(options,menu,c.pinned?"取消置顶":"置顶会话",()->{if(service!=null)service.editConversation(c.peer.id(),c.note,!c.pinned);});
        fileAction(options,menu,"清空聊天记录",()->deleteConversation(c,true));
        fileAction(options,menu,"删除联系人",()->deleteConversation(c,false));
    }
    private void closeHistory() { if(historyDialog!=null){historyDialog.dismiss();historyDialog=null;} }
    private void openTools(ChatStore.Conversation conversation,boolean remote) {
        if(service==null){error("请先连接局域网");return;}
        service.openTool(conversation.peer,ToolsHttp.portFor(conversation.peer.endpoint.getPort()),remote?"remote":"share",false,intent->{
            if(isFinishing()||isDestroyed())return;
            try {startActivity(intent);}
            catch(ActivityNotFoundException ex) {
                if(!remote&&intent.getData()!=null)startActivity(new Intent(this,RemoteActivity.class).setData(intent.getData()).putExtra("peer_id",conversation.peer.id()).putExtra("tool_mode","share").putExtra("phone",true));
                else error("设备没有可用的浏览器");
            }
        });
    }
    private void openPhone(ChatStore.Conversation conversation){
        if(service==null){error("请先连接局域网");return;}
        service.openTool(conversation.peer,PhoneRemoteAccess.portFor(conversation.peer.endpoint.getPort()),"remote",true,intent->{if(!isFinishing()&&!isDestroyed())startActivity(intent);});
    }
    private String folderDisplayName(Uri tree) {
        if(tree==null)return "";
        try {
            String docId=DocumentsContract.getTreeDocumentId(tree);
            Uri docUri=DocumentsContract.buildDocumentUriUsingTree(tree,docId);
            try(android.database.Cursor cursor=getContentResolver().query(docUri,new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME},null,null,null)) {
                if(cursor!=null&&cursor.moveToFirst()){String name=cursor.getString(0);if(name!=null&&!name.isEmpty())return name;}
            }
            int colon=docId.indexOf(':');
            String path=colon>=0?docId.substring(colon+1):docId;
            if(path.isEmpty())return "内部存储根目录";
            int slash=path.lastIndexOf('/');
            return slash>=0&&slash+1<path.length()?path.substring(slash+1):path;
        } catch(RuntimeException ignored) { return "已授权文件夹"; }
    }
    private void showTasks() { showTasks(""); }
    private void showTasks(String sourceKey) {
        if(tasksDialog!=null){tasksDialog.dismiss();return;}
        Dialog dialog=new Dialog(this);dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);tasksDialog=dialog;
        LinearLayout page=column();page.setBackgroundColor(SURFACE);page.setFocusableInTouchMode(true);
        LinearLayout header=row();header.setPadding(dp(8),dp(6),dp(12),dp(6));
        IconButton back=new IconButton("back","返回",MUTED);header.addView(back,new LinearLayout.LayoutParams(dp(48),dp(48)));back.setOnClickListener(v->dialog.dismiss());
        TextView caption=text("传输任务",20,INK);caption.setTypeface(null,Typeface.BOLD);caption.setSingleLine();caption.setEllipsize(TextUtils.TruncateAt.END);header.addView(caption,weight());page.addView(header);
        IconButton queueMenu=new IconButton("more","传输任务操作",MUTED);header.addView(queueMenu,new LinearLayout.LayoutParams(dp(48),dp(48)));queueMenu.setOnClickListener(v->{
            if(service==null)return;ChatService source=service;LinearLayout options=column();Dialog menu=sheet("传输任务操作",options,null,null);
            fileAction(options,menu,"继续当前列表中的接收",this::continueCurrentReceives);
            fileAction(options,menu,"暂停当前列表中的接收",()->{if(service==source&&taskAdapter!=null){taskAdapter.refresh();source.pauseReceives(new ArrayList<>(taskAdapter.items));}});
            int queued=source.queuedTransfers();
            if(queued>0)fileAction(options,menu,"取消排队发送任务",()->confirm("取消排队发送任务？","取消尚未开始的 "+queued+" 项发送任务？正在传输和已保存的文件不受影响。","取消排队",()->{if(service==source)source.cancelQueuedTransfers();}));
        });
        LinearLayout controls=column();ScrollView controlScroll=new ScrollView(this) {
            @Override protected void onMeasure(int width,int height) {
                // 横屏、键盘和大字号下让顶部可滚动，列表始终保留剩余空间。
                int cap=Math.max(1,(int)(View.MeasureSpec.getSize(height)*.55f));
                super.onMeasure(width,View.MeasureSpec.makeMeasureSpec(cap,View.MeasureSpec.AT_MOST));
            }
        };controlScroll.addView(controls);page.addView(controlScroll,new LinearLayout.LayoutParams(-1,-2));
        EditText input=new EditText(this);configureInput(input);input.setSingleLine();input.setTextSize(15);input.setTextColor(INK);input.setHintTextColor(MUTED);input.setHint("搜索文件或设备");input.setPadding(dp(14),dp(8),dp(14),dp(8));input.setBackground(shape(PALE,CONTROL_RADIUS));input.setMinHeight(dp(46));
        LinearLayout.LayoutParams ip=new LinearLayout.LayoutParams(-1,-2);ip.setMargins(dp(16),dp(4),dp(16),dp(8));controls.addView(input,ip);
        TextView count=text("正在读取传输记录…",12,MUTED);count.setPadding(dp(20),dp(8),dp(20),dp(10));
        TaskAdapter tasks=new TaskAdapter(count,input,dialog,sourceKey);taskAdapter=tasks;controls.addView(tasks.sourceBar,0);
        LinearLayout filters=row();filters.setPadding(dp(12),0,dp(12),0);String[] names={"全部","进行中","待处理","已完成"};List<Button> picks=new ArrayList<>();
        for(int i=0;i<names.length;i++){final int index=i;Button pick=button(names[i],false);pick.setMinWidth(0);pick.setMinimumWidth(0);pick.setMinHeight(dp(44));pick.setSingleLine();pick.setEllipsize(TextUtils.TruncateAt.END);pick.setPadding(0,dp(8),0,dp(8));filters.addView(pick,new LinearLayout.LayoutParams(0,-2,1));picks.add(pick);pick.setOnClickListener(v->{tasks.filter=index;for(int j=0;j<picks.size();j++){picks.get(j).setTextColor(j==index?BLUE:MUTED);picks.get(j).setBackground(shape(j==index?BUBBLE:SURFACE,CONTROL_RADIUS));}tasks.refresh();});}
        controls.addView(filters);controls.addView(count);picks.get(0).performClick();
        FrameLayout area=new FrameLayout(this);ListView rows=new ListView(this);rows.setDivider(null);rows.setAdapter(tasks);rows.setContentDescription("传输任务列表");area.addView(rows,new FrameLayout.LayoutParams(-1,-1));
        TextView emptyTasks=text("暂无传输任务\n收到或发送的文件会显示在这里",15,MUTED);tasks.empty=emptyTasks;emptyTasks.setGravity(Gravity.CENTER);emptyTasks.setLineSpacing(dp(8),1);area.addView(emptyTasks,new FrameLayout.LayoutParams(-1,-1));rows.setEmptyView(emptyTasks);page.addView(area,new LinearLayout.LayoutParams(-1,0,1));
        rows.setOnScrollListener(new AbsListView.OnScrollListener(){public void onScroll(AbsListView view,int first,int visible,int total){}public void onScrollStateChanged(AbsListView view,int state){tasks.scrolling=state!=SCROLL_STATE_IDLE;if(!tasks.scrolling)tasks.refresh();}});
        rows.setOnItemClickListener((parent,view,position,id)->taskActions(tasks.items.get(position)));
        input.addTextChangedListener(new TextWatcher(){public void beforeTextChanged(CharSequence s,int start,int n,int after){}public void onTextChanged(CharSequence s,int start,int before,int n){tasks.requestSearch();}public void afterTextChanged(Editable e){}});
        dialog.setContentView(page);page.requestFocus();dialog.setOnDismissListener(d->{tasks.stop();if(selectionOwner==input)finishSelection();if(tasksDialog==dialog){tasksDialog=null;taskAdapter=null;loadingTasks=false;reloadTasksPending=false;if(visible&&!isDestroyed())changed();}});dialog.show();tasks.scheduleDayRefresh();
        Window window=dialog.getWindow();window.setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(SURFACE));window.setLayout(-1,-1);window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE|WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN);SystemBars.apply(window,SURFACE,DARK);
        page.setOnApplyWindowInsetsListener((v,insets)->{if(Build.VERSION.SDK_INT>=30){Insets pad=insets.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout()|WindowInsets.Type.ime());v.setPadding(pad.left,pad.top,pad.right,pad.bottom);}else v.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());return insets;});page.requestApplyInsets();loadTaskRows();
    }
    private void loadTaskRows() {
        if(tasksDialog==null||taskAdapter==null||service==null)return;
        if(loadingTasks){reloadTasksPending=true;return;}
        TaskAdapter expected=taskAdapter;ChatService source=service;loadingTasks=true;
        source.fileTasks(rows->{
            if(taskAdapter!=expected)return;
            loadingTasks=false;
            if(isDestroyed())return;
            if(source==service){expected.all=rows;expected.times.clear();if(!expected.scrolling&&resumed)expected.refresh();expected.scheduleStorageRefresh();}
            if(reloadTasksPending&&tasksDialog!=null&&tasksDialog.isShowing()){reloadTasksPending=false;loadTaskRows();}
        });
    }
    private ChatService.FileProgress taskProgress(ChatStore.FileTask item) {
        ChatService.FileProgress live=service==null?null:service.progress(item.peer,item.message.number,item.file.id,item.message.outgoing);
        if(live!=null)return live;
        ChatStore.TransferState saved=item.savedProgress;
        return saved==null?null:new ChatService.FileProgress(saved.percent,saved.state,null);
    }
    private String taskState(ChatStore.FileTask item) {
        if(item.guestFile!=null)return item.stored?"已保存":"本机文件已删除";
        ChatService.FileProgress progress=taskProgress(item);
        if(!item.message.outgoing) {
            if(progress!=null&&taskActive(progress.state))return progress.state;
            if(progress!=null&&ChatService.wasStored(progress.state))return progress.state.equals("本机文件已删除")?"本机文件已删除":"已保存";
            if(item.stored)return "已保存";
            if(ChatService.wasStored(item.message.state))return "本机文件已删除";
        }
        if(progress!=null)return progress.state;
        if(item.message.state!=null&&(item.message.state.equals("对方已取消发送")||item.message.state.equals("已拒绝")||item.message.state.equals("已取消发送")))return item.message.state;
        return item.message.outgoing?"等待对方接收":"待接收";
    }
    private static boolean taskComplete(String state) { return state.equals("已保存")||state.equals("对方已校验保存"); }
    private static boolean taskEnded(String state) { return taskComplete(state)||state.contains("已拒绝")||state.contains("已取消发送")||state.equals("本机文件已删除"); }
    private static boolean taskActive(String state) { return state.contains("中")&&!state.contains("中断")&&!taskEnded(state); }
    private String taskLabel(ChatStore.FileTask item,String state,ChatService.FileProgress progress) {
        String label=state;
        if(progress!=null&&progress.bytesPerSecond>0&&taskActive(state)) {
            double seconds=item.file.size*(1-progress.percent/100.0)/progress.bytesPerSecond;
            label+=" · "+size((long)progress.bytesPerSecond)+"/s · 剩余约 "+(seconds>=60?(long)Math.ceil(seconds/60)+" 分钟":Math.max(1,(long)Math.ceil(seconds))+" 秒");
        }
        return label;
    }
    private Uri taskUri(ChatStore.FileTask item) {
        if(item.message.outgoing||item.guestFile==null&&service==null)return null;
        File local=item.guestFile==null?service.receivedFile(item.peer,item.message.number,item.file):item.guestFile;
        return local.isFile()?ReceivedFileProvider.uri(this,local,item.file.name):receivedStorage.existingSaved(local);
    }
    private List<String> taskQuickActions(ChatStore.FileTask item,String state,ChatService.FileProgress progress,ChatStore.Conversation contact) {
        List<String> actions=new ArrayList<>();
        if(item.guestFile==null&&!item.message.outgoing&&service!=null&&contact!=null&&!taskEnded(state)) {
            if(progress!=null&&progress.task!=null) {actions.add("暂停");actions.add("取消");}
            else if(state.equals("排队中")) {actions.add("暂停排队");actions.add("拒绝");}
            else {actions.add((progress==null||progress.percent==0)&&state.startsWith("待接收")?"接收":"继续接收");actions.add("拒绝");}
        }
        if(!item.message.outgoing&&taskComplete(state)&&(item.guestFile!=null||service!=null))actions.add("打开");
        if(item.message.outgoing&&service!=null&&service.canCancelOutgoing(item.peer,item.message.number))actions.add("取消发送");
        return actions;
    }
    private Button taskButton(LinearLayout row) {
        Button button=button("",false);button.setTextSize(14);
        button.setPadding(dp(12),dp(6),dp(12),dp(6));button.setMinWidth(0);button.setMinimumWidth(0);button.setFocusable(false);button.setFocusableInTouchMode(false);
        LinearLayout.LayoutParams spacing=new LinearLayout.LayoutParams(-2,-2);spacing.rightMargin=dp(8);row.addView(button,spacing);return button;
    }
    private void requestCancelFile(ChatStore.FileTask item) {
        ChatService source=service;if(source==null)return;
        confirm("取消接收","已下载但尚未完成的数据将被清理，之后需要重新接收。","取消接收",()->{if(service==source)source.cancelFile(item.peer,item.message.number,item.file.id);});
    }
    private void requestRejectFiles(LanNode.Peer peer,long packet) {
        ChatService source=service;if(source==null)return;
        confirm("拒绝这批文件？","将拒绝该消息中未接收的文件并清理断点；已保存的文件保留。","拒绝这批文件",()->{if(service==source)source.reject(peer,packet);});
    }
    private void requestCancelOutgoingTask(ChatStore.FileTask item) {
        ChatService source=service;if(source==null)return;
        confirm("取消发送？","撤销这批未完成的文件邀请；原文件和对方已保存的文件保留。","取消发送",()->{if(service==source)source.cancelOutgoing(item.peer,item.message,success->{if(success)loadTaskRows();});});
    }
    private void continueCurrentReceives() {
        ChatService source=service;
        if(source==null||taskAdapter==null)return;
        taskAdapter.refresh();
        for(ChatStore.FileTask item:new ArrayList<>(taskAdapter.items)) {
            ChatStore.Conversation contact=conversation(item.peer);
            String state=taskState(item);
            if(item.guestFile==null&&contact!=null&&!item.message.outgoing&&!taskEnded(state)&&!taskActive(state))source.receive(contact.peer,item.message.number,item.file);
        }
    }
    private static String taskTime(long time,long now) {
        if(time<=0)return "";
        Calendar day=Calendar.getInstance(),current=Calendar.getInstance();
        day.setTimeInMillis(time);
        current.setTimeInMillis(now);
        if(day.get(Calendar.ERA)!=GregorianCalendar.AD||day.get(Calendar.YEAR)>9999)return "";
        boolean sameYear=day.get(Calendar.YEAR)==current.get(Calendar.YEAR);
        boolean today=sameYear&&day.get(Calendar.DAY_OF_YEAR)==current.get(Calendar.DAY_OF_YEAR);
        return new SimpleDateFormat(today?"HH:mm":sameYear?"MM-dd HH:mm":"yyyy-MM-dd HH:mm",Locale.CHINA).format(new Date(time));
    }
    private static final class TaskSource {
        final String key,name,avatar;
        final long time;
        TaskSource(String key,String name,String avatar,long time) {
            this.key=key;
            this.name=name;
            this.avatar=avatar;
            this.time=time;
        }
    }
    private final class TaskAdapter extends BaseAdapter {
        List<ChatStore.FileTask> all=new ArrayList<>(),items=new ArrayList<>();final TextView count;final EditText input;int filter;
        final LinearLayout sourceBar=row(),sourceItems=row();
        final HorizontalScrollView sourceScroll=new HorizontalScrollView(MainActivity.this);
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
        TaskAdapter(TextView count,EditText input,Dialog dialog,String sourceKey) {
            this.count=count;
            this.input=input;
            this.dialog=dialog;
            selectedSource=sourceKey;
            deferredRefresh=()->{refreshScheduled=false;if(resumed&&taskAdapter==this&&dialog.isShowing()&&!scrolling)refresh();};
            storageRefresh=()->{if(resumed&&taskAdapter==this&&dialog.isShowing()){if(scrolling)scheduleStorageRefresh();else loadTaskRows();}};
            dayRefresh=()->{if(resumed&&taskAdapter==this&&dialog.isShowing()&&!isDestroyed()){times.clear();if(!scrolling)refresh();scheduleDayRefresh();}};
            sourceBar.setPadding(dp(12),0,dp(12),0);
            sourceItems.setGravity(Gravity.TOP);
            sourceItems.setPadding(0,dp(4),0,dp(4));
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
            if(resumed&&!scrolling&&!refreshScheduled){refreshScheduled=true;clockHandler.postDelayed(deferredRefresh,200);}
        }
        void scheduleStorageRefresh() {
            clockHandler.removeCallbacks(storageRefresh);
            if(resumed&&dialog.isShowing())clockHandler.postDelayed(storageRefresh,3000);
        }
        private String time(ChatStore.FileTask item,long now) {
            return times.computeIfAbsent(item.message.time,value->taskTime(value,now));
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
                LinearLayout pick=column();
                pick.setGravity(Gravity.CENTER_HORIZONTAL);
                pick.setPadding(dp(2),dp(2),dp(2),dp(4));
                pick.setTag("task-source:"+item.key);
                pick.setSelected(chosen);
                pick.setFocusable(true);
                pick.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES);
                pick.setContentDescription(item.name+"，"+(chosen?"已选中":"未选中"));
                FrameLayout circle=new FrameLayout(MainActivity.this);
                GradientDrawable ring=shape(PALE,99);
                ring.setStroke(dp(2),chosen?BLUE:Color.TRANSPARENT);
                circle.setBackground(ring);
                circle.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
                if(item.key.isEmpty()||item.key.equals("guest")) {
                    ImageView icon=new ImageView(MainActivity.this);
                    icon.setPadding(dp(8),dp(8),dp(8),dp(8));
                    icon.setImageDrawable(new MenuIcon(item.key.isEmpty()?"传输任务":"临时浏览器传输",chosen?BLUE:MUTED));
                    circle.addView(icon,new FrameLayout.LayoutParams(dp(40),dp(40),Gravity.CENTER));
                } else {
                    TextView avatar=text("",18,Color.WHITE);
                    avatar.setBackground(new AvatarDrawable(MainActivity.this,item.key.substring(5),false,0,SURFACE,ONLINE,item.avatar));
                    circle.addView(avatar,new FrameLayout.LayoutParams(dp(40),dp(40),Gravity.CENTER));
                }
                pick.addView(circle,new LinearLayout.LayoutParams(dp(44),dp(44)));
                TextView name=text(item.name,12,chosen?BLUE:MUTED);
                name.setSingleLine();
                name.setEllipsize(TextUtils.TruncateAt.END);
                name.setGravity(Gravity.CENTER);
                name.setPadding(0,dp(4),0,0);
                name.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
                pick.addView(name,new LinearLayout.LayoutParams(-1,-2));
                sourceItems.addView(pick,new LinearLayout.LayoutParams(dp(68),-2));
                pick.setOnClickListener(v->{if(!selectedSource.equals(item.key)){selectedSource=item.key;refresh();}});
            }
            sourceItems.post(()->{if(taskAdapter==this&&dialog.isShowing())showSelectedSource();});
        }
        private TaskSource source(String key,long time) {
            if(key.equals("guest"))return new TaskSource(key,"浏览器访客",null,time);
            ChatStore.Conversation contact=conversation(key.substring(5));
            return new TaskSource(key,contact==null?"已移除的设备":contact.displayName(),contact==null?null:Protocol.normalizeAvatar(contact.peer.avatar),time);
        }
        void refresh() {
            refreshScheduled=false;
            clockHandler.removeCallbacks(deferredRefresh);
            refreshSources();
            long now=clock.getAsLong();
            String query=input.getText().toString().trim().toLowerCase(Locale.ROOT);List<ChatStore.FileTask> next=new ArrayList<>();int completed=0;double total=0,done=0;
            StringBuilder content=new StringBuilder();
            for(ChatStore.FileTask item:all){String source=item.guestFile!=null?"guest":"peer:"+item.peer;
                if(!selectedSource.isEmpty()&&!selectedSource.equals(source))continue;
                String state=taskState(item);ChatStore.Conversation contact=conversation(item.peer);String name=item.guestFile!=null?"浏览器访客":contact==null?"已移除的设备":contact.displayName();
                if(!(item.file.name+" "+name).toLowerCase(Locale.ROOT).contains(query)||filter==1&&!taskActive(state)||filter==2&&(taskActive(state)||taskComplete(state))||filter==3&&!taskComplete(state))continue;
                next.add(item);if(taskComplete(state))completed++;ChatService.FileProgress p=taskProgress(item);
                int percent=taskComplete(state)?100:state.equals("本机文件已删除")?0:p==null?0:p.percent;total+=item.file.size;done+=item.file.size*percent/100.0;
                content.append(item.peer).append('\0').append(item.message.number).append('\0').append(item.message.outgoing).append('\0').append(item.file.id).append('\0')
                    .append(item.file.name).append('\0').append(item.file.size).append('\0').append(name).append('\0').append(time(item,now)).append('\0').append(state).append('\0').append(percent).append('\0')
                    .append(taskLabel(item,state,p)).append('\0').append(item.guestFile==null?"":item.guestFile.getAbsolutePath()).append('\0')
                    .append(taskQuickActions(item,state,p,contact)).append('\0');
            }
            String signature=content.toString();int queued=service==null?0:service.queuedTransfers();
            String summary=next.size()+" 个文件 · "+completed+" 个已完成"+(total>0?" · "+size((long)done)+" / "+size((long)total):"")+(queued>0?"\n另有 "+queued+" 项文件操作排队中":"");
            boolean rowsChanged=!signature.equals(contentSignature),summaryChanged=!summary.equals(summaryText);
            items=next;
            String emptyText=!query.isEmpty()?"没有匹配的文件或设备":!selectedSource.isEmpty()||filter!=0?"当前筛选下没有传输任务":"暂无传输任务\n收到或发送的文件会显示在这里";
            if(empty!=null&&!emptyText.contentEquals(empty.getText()))empty.setText(emptyText);
            if(!rowsChanged&&!summaryChanged)return;
            contentSignature=signature;summaryText=summary;
            if(summaryChanged&&!summary.contentEquals(count.getText()))count.setText(summary);
            if(rowsChanged)notifyDataSetChanged();
        }
        public int getCount(){return items.size();}public Object getItem(int position){return items.get(position);}public long getItemId(int position){return position;}
        public View getView(int position,View old,ViewGroup parent) {
            ChatStore.FileTask item=items.get(position);LinearLayout box;
            if(old==null){box=column();box.setPadding(dp(20),dp(12),dp(20),dp(12));TextView name=text("",15,INK);name.setTypeface(null,Typeface.BOLD);name.setSingleLine();name.setEllipsize(TextUtils.TruncateAt.END);box.addView(name);TextView meta=text("",12,MUTED);meta.setSingleLine();meta.setEllipsize(TextUtils.TruncateAt.END);meta.setPadding(0,dp(5),0,dp(4));box.addView(meta);TextView status=text("",12,MUTED);status.setMaxLines(2);box.addView(status);ProgressBar bar=new ProgressBar(MainActivity.this,null,android.R.attr.progressBarStyleHorizontal);bar.setMax(100);bar.setProgressTintList(android.content.res.ColorStateList.valueOf(BLUE));bar.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(LINE));LinearLayout.LayoutParams bp=new LinearLayout.LayoutParams(-1,dp(3));bp.topMargin=dp(8);box.addView(bar,bp);LinearLayout actions=row();LinearLayout.LayoutParams ap=new LinearLayout.LayoutParams(-1,-2);ap.topMargin=dp(8);box.addView(actions,ap);}else box=(LinearLayout)old;
            ChatStore.Conversation contact=conversation(item.peer);String state=taskState(item);ChatService.FileProgress p=taskProgress(item);
            String time=time(item,clock.getAsLong());
            ((TextView)box.getChildAt(0)).setText(item.file.name);((TextView)box.getChildAt(1)).setText((item.message.outgoing?"发送给 ":"来自 ")+(item.guestFile!=null?"浏览器访客":contact==null?"已移除的设备":contact.displayName())+" · "+size(item.file.size)+(time.isEmpty()?"":" · "+time));
            String label=taskLabel(item,state,p);
            TextView status=(TextView)box.getChildAt(2);status.setText(label);status.setTextColor(taskComplete(state)?ONLINE:taskActive(state)?BLUE:MUTED);ProgressBar bar=(ProgressBar)box.getChildAt(3);bar.setProgress(taskComplete(state)?100:state.equals("本机文件已删除")?0:p==null?0:p.percent);bar.setVisibility(taskEnded(state)?View.GONE:View.VISIBLE);
            LinearLayout actions=(LinearLayout)box.getChildAt(4);ChatService source=service;
            List<String> labels=taskQuickActions(item,state,p,contact);
            while(actions.getChildCount()>labels.size())actions.removeViewAt(actions.getChildCount()-1);
            for(int index=0;index<labels.size();index++) {
                String action=labels.get(index);
                Button button=index<actions.getChildCount()?(Button)actions.getChildAt(index):taskButton(actions);
                if(!action.contentEquals(button.getText()))button.setText(action);
                button.setTextColor(action.equals("拒绝")||action.startsWith("取消")?0xffd24e54:BLUE);
                button.setOnClickListener(v->{
                if(service!=source)return;
                switch(action) {
                    case "暂停":case "暂停排队":source.pauseFile(item.peer,item.message.number,item.file.id);break;
                    case "取消":requestCancelFile(item);break;
                    case "接收":case "继续接收":source.receive(contact.peer,item.message.number,item.file);break;
                    case "拒绝":requestRejectFiles(contact.peer,item.message.number);break;
                    case "打开":Uri uri=taskUri(item);if(uri==null)error("本机文件已删除");else openFile(uri,item.file.name,false);break;
                    case "取消发送":requestCancelOutgoingTask(item);break;
                }
                });
            }
            actions.setVisibility(actions.getChildCount()==0?View.GONE:View.VISIBLE);box.setContentDescription(item.file.name+"，"+label);return box;
        }
    }
    private void taskActions(ChatStore.FileTask item) {
        if(item.guestFile!=null) {
            LinearLayout box=column();box.addView(text("浏览器访客 · "+taskState(item),13,MUTED));Dialog dialog=sheet(item.file.name,box,null,null);
            Uri uri=item.guestFile.isFile()?ReceivedFileProvider.uri(this,item.guestFile,item.file.name):receivedStorage.existingSaved(item.guestFile);
            if(uri!=null){
                fileAction(box,dialog,"打开文件",()->openFile(uri,item.file.name,false));
                fileAction(box,dialog,"打开方式…",()->openFileWith(uri,item.file.name,false,true));
                fileAction(box,dialog,"分享文件",()->openFile(uri,item.file.name,true));
                ChatService source=service;
                fileAction(box,dialog,"删除本机文件",()->confirm("删除这个文件？",item.file.name+"\n将删除本机保存的文件，接收记录保留，无法重新接收。此操作无法撤销。","删除文件",()->{if(source!=null&&service==source)source.deleteGuestFile(item.guestFile);}));
            }
            fileAction(box,dialog,"查看保存位置",()->showFileLocation(item.guestFile));
            fileAction(box,dialog,"复制文件名",()->copyText("文件名",item.file.name));return;
        }
        if(service==null)return;ChatStore.Conversation contact=conversation(item.peer);if(contact==null){error("联系人已移除，请重新添加后操作");return;}
        ChatService source=service;String state=taskState(item);Uri uri=taskUri(item);
        LinearLayout content=column();content.addView(text(state+" · "+size(item.file.size),13,MUTED));Dialog actions=sheet(item.file.name,content,null,null);
        if(!item.message.outgoing&&uri!=null) {
            File local=source.receivedFile(item.peer,item.message.number,item.file);
            fileAction(content,actions,"打开文件",()->openFile(uri,item.file.name,false));
            fileAction(content,actions,"打开方式…",()->openFileWith(uri,item.file.name,false,true));
            fileAction(content,actions,"分享文件",()->openFile(uri,item.file.name,true));
            fileAction(content,actions,"查看保存位置",()->showFileLocation(local));
            fileAction(content,actions,"删除本机文件",()->confirm("删除这个文件？",item.file.name+"\n将删除本机保存的文件，聊天记录与对方文件保留。此操作无法撤销。","删除文件",()->{if(service==source)source.deleteReceivedFile(contact.peer,item.message.number,item.file);}));
        } else if(!item.message.outgoing&&state.equals("本机文件已删除")) {
            fileAction(content,actions,"重新接收",()->{if(service==source)source.receive(contact.peer,item.message.number,item.file);});
        } else if(!item.message.outgoing&&!taskEnded(state)) {
            ChatService.FileProgress progress=taskProgress(item);
            if(progress!=null&&progress.task!=null) {
                fileAction(content,actions,"暂停并保留断点",()->{if(service==source)source.pauseFile(item.peer,item.message.number,item.file.id);});
                fileAction(content,actions,"取消接收并清理断点",()->requestCancelFile(item));
            } else {
                String receive=(progress==null||progress.percent==0)&&state.startsWith("待接收")?"接收文件":"继续接收";
                fileAction(content,actions,receive,()->{if(service==source)source.receive(contact.peer,item.message.number,item.file);});
                if(state.equals("排队中"))fileAction(content,actions,"暂停排队",()->{if(service==source)source.pauseFile(item.peer,item.message.number,item.file.id);});
                if(progress!=null&&progress.percent>0||state.equals("已暂停"))fileAction(content,actions,"取消接收并清理断点",()->requestCancelFile(item));
            }
            fileAction(content,actions,"拒绝这批文件",()->requestRejectFiles(contact.peer,item.message.number));
            fileAction(content,actions,"继续当前列表中的接收",this::continueCurrentReceives);
        }
        if(item.message.outgoing&&service.canCancelOutgoing(item.peer,item.message.number)) {
            fileAction(content,actions,"取消发送",()->requestCancelOutgoingTask(item));
        }
        fileAction(content,actions,"查看所在会话",()->{if(tasksDialog!=null)tasksDialog.dismiss();locateHistoryMessage(item.peer,item.message);});
        fileAction(content,actions,"复制文件名",()->copyText("文件名",item.file.name));
    }
    private String guestFileName(Uri uri) {
        try(android.database.Cursor cursor=getContentResolver().query(uri,new String[]{android.provider.OpenableColumns.DISPLAY_NAME},null,null,null)) {
            if(cursor!=null&&cursor.moveToFirst()){String name=cursor.getString(0);if(name!=null&&!name.isEmpty())return name;}
        }catch(RuntimeException ignored){}
        String segment=uri==null?null:uri.getLastPathSegment();
        return segment==null||segment.isEmpty()?"已选文件":segment;
    }
    private void showGuest() {
        if(service==null||!service.online()){error("请先连接局域网");return;}
        ChatService source=service;GuestTransfer active=source.guestTransfer();boolean running=active!=null&&active.active();
        LinearLayout box=column();
        LinearLayout card=column();
        GradientDrawable cardBg=shape(PALE,CONTROL_RADIUS);cardBg.setStroke(dp(1),LINE);
        card.setBackground(cardBg);card.setPadding(dp(14),dp(12),dp(14),dp(12));
        LinearLayout topRow=row();
        int leftMinutes=running?Math.max(0,(int)Math.ceil((active.until-System.currentTimeMillis())/60000.0)):0;
        TextView badge=text(running?"开放中 · 剩余约 "+leftMinutes+" 分钟":"未开启",12,running?ONLINE:MUTED);
        badge.setTypeface(null,Typeface.BOLD);
        badge.setPadding(dp(10),dp(4),dp(10),dp(4));
        badge.setBackground(shape(blend(running?ONLINE:MUTED,SURFACE,DARK?.28f:.14f),99));
        topRow.addView(badge);
        TextView safeNote=text("HTTP 明文 · 15 分钟有效",11,MUTED);safeNote.setGravity(Gravity.END|Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams safeParams=weight();safeParams.leftMargin=dp(8);topRow.addView(safeNote,safeParams);
        card.addView(topRow);
        if(running) {
            List<String> addresses=new ArrayList<>();
            try{addresses.addAll(service!=null?service.localAddresses():new LanNetwork(getSystemService(android.net.ConnectivityManager.class)).addresses());}catch(java.io.IOException e){error(e.getMessage());}
            for(String address:addresses) {
                String link=active.url(address);
                LinearLayout linkCard=column();
                GradientDrawable linkBg=shape(SURFACE,CONTROL_RADIUS);linkBg.setStroke(dp(1),LINE);
                linkCard.setBackground(linkBg);linkCard.setPadding(dp(12),dp(10),dp(12),dp(10));
                LinearLayout.LayoutParams lcp=new LinearLayout.LayoutParams(-1,-2);lcp.topMargin=dp(10);
                TextView ipLabel=text(address,12,BLUE);ipLabel.setTypeface(null,Typeface.BOLD);linkCard.addView(ipLabel);
                TextView urlView=text(link,12,MUTED);urlView.setTypeface(Typeface.MONOSPACE);urlView.setSingleLine();urlView.setEllipsize(TextUtils.TruncateAt.MIDDLE);urlView.setPadding(0,dp(3),0,dp(8));linkCard.addView(urlView);
                LinearLayout btnRow=row();
                Button copyBtn=button("复制链接",false);
                copyBtn.setTextColor(BLUE);copyBtn.setMinHeight(dp(38));copyBtn.setPadding(dp(10),dp(6),dp(10),dp(6));
                copyBtn.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),shape(OUTGOING,CONTROL_RADIUS),null));
                copyBtn.setOnClickListener(v->copyText("临时传输链接",link));
                Button qrBtn=button("显示二维码",false);
                qrBtn.setMinHeight(dp(38));qrBtn.setPadding(dp(10),dp(6),dp(10),dp(6));
                GradientDrawable qrBg=shape(PALE,CONTROL_RADIUS);qrBg.setStroke(dp(1),LINE);
                qrBtn.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),qrBg,null));
                qrBtn.setOnClickListener(v->{try{startActivity(new Intent(Intent.ACTION_VIEW,Uri.parse(link+"&qr")));}catch(ActivityNotFoundException e){error("没有可用浏览器，请复制链接");}});
                LinearLayout.LayoutParams leftBtn=weight();leftBtn.rightMargin=dp(8);btnRow.addView(copyBtn,leftBtn);btnRow.addView(qrBtn,weight());
                linkCard.addView(btnRow);card.addView(linkCard,lcp);
            }
            LinearLayout.LayoutParams cp=new LinearLayout.LayoutParams(-1,-2);cp.bottomMargin=dp(8);box.addView(card,cp);
            Dialog dialog=sheet("临时浏览器传输",box,null,null);
            fileAction(box,dialog,"查看已接收文件",()->showTasks("guest"));
            fileAction(box,dialog,"结束临时传输",()->{source.stopGuest();error("临时传输已结束，完整文件保留");});
            return;
        }
        String summaryText=guestFiles.isEmpty()?"未选择分享文件（可仅开启接收）":"已选 "+guestFiles.size()+" 个文件";
        TextView fileSummary=text(summaryText,14,INK);fileSummary.setTypeface(null,Typeface.BOLD);fileSummary.setPadding(0,dp(10),0,dp(4));card.addView(fileSummary);
        if(!guestFiles.isEmpty()) {
            StringBuilder preview=new StringBuilder();
            int show=Math.min(3,guestFiles.size());
            for(int i=0;i<show;i++){if(i>0)preview.append('\n');preview.append("· ").append(guestFileName(guestFiles.get(i)));}
            if(guestFiles.size()>show)preview.append("\n等共 ").append(guestFiles.size()).append(" 个文件");
            TextView listPreview=text(preview.toString(),12,MUTED);listPreview.setLineSpacing(dp(2),1);listPreview.setPadding(0,0,0,dp(8));card.addView(listPreview);
        }
        LinearLayout pickRow=row();pickRow.setPadding(0,dp(4),0,0);
        Button pickBtn=button(guestFiles.isEmpty()?"选择分享文件":"重新选择文件",false);
        pickBtn.setTextColor(BLUE);pickBtn.setMinHeight(dp(40));pickBtn.setPadding(dp(12),dp(6),dp(12),dp(6));
        pickBtn.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),shape(OUTGOING,CONTROL_RADIUS),null));
        LinearLayout.LayoutParams pickParams=weight();
        if(!guestFiles.isEmpty())pickParams.rightMargin=dp(8);
        pickRow.addView(pickBtn,pickParams);
        Button clearBtn=null;
        if(!guestFiles.isEmpty()) {
            clearBtn=button("清空",false);clearBtn.setMinHeight(dp(40));clearBtn.setPadding(dp(14),dp(6),dp(14),dp(6));
            GradientDrawable clearBg=shape(SURFACE,CONTROL_RADIUS);clearBg.setStroke(dp(1),LINE);
            clearBtn.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),clearBg,null));
            pickRow.addView(clearBtn,new LinearLayout.LayoutParams(-2,-2));
        }
        card.addView(pickRow);
        LinearLayout.LayoutParams cp=new LinearLayout.LayoutParams(-1,-2);cp.bottomMargin=dp(10);box.addView(card,cp);

        LinearLayout uploadCard=column();
        GradientDrawable uploadBg=shape(PALE,CONTROL_RADIUS);uploadBg.setStroke(dp(1),LINE);
        uploadCard.setBackground(uploadBg);uploadCard.setPadding(dp(14),dp(4),dp(14),dp(10));
        Switch receive=settingSwitch("允许对方向我发送文件",guestUpload);uploadCard.addView(receive);receive.setOnCheckedChangeListener((button,checked)->guestUpload=checked);
        uploadCard.addView(text("保存至应用接收区 · 限 100 个文件 / 10 GiB",12,MUTED));
        box.addView(uploadCard,new LinearLayout.LayoutParams(-1,-2));

        Dialog dialog=sheet("临时浏览器传输",box,"开启临时传输",d->{source.startGuest(new ArrayList<>(guestFiles),receive.isChecked(),success->{if(success&&!isDestroyed()&&service==source){d.dismiss();showGuest();}});});
        pickBtn.setOnClickListener(v->{dialog.dismiss();try{startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_ALLOW_MULTIPLE,true).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),27);}catch(ActivityNotFoundException e){error("系统文件选择器不可用");}});
        if(clearBtn!=null)clearBtn.setOnClickListener(v->{dialog.dismiss();guestFiles.clear();showGuest();});
    }
    private void sharing() {
        if(service==null||!service.online()){error("请先连接局域网");return;}
        SharedFiles current=service.sharedFiles();List<Uri> savedTrees;
        try{savedTrees=new SharedFolderSettings(this).trees();}catch(java.io.IOException e){error(e.getMessage());return;}
        LinearLayout box=column();
        LinearLayout card=column();
        GradientDrawable cardBg=shape(PALE,CONTROL_RADIUS);cardBg.setStroke(dp(1),LINE);
        card.setBackground(cardBg);card.setPadding(dp(14),dp(14),dp(14),dp(14));
        LinearLayout topRow=row();
        boolean active=current!=null;
        TextView badge=text(active?"正在共享 · 只读":"未开启共享",12,active?ONLINE:MUTED);
        badge.setTypeface(null,Typeface.BOLD);
        badge.setPadding(dp(10),dp(4),dp(10),dp(4));
        badge.setBackground(shape(blend(active?ONLINE:MUTED,SURFACE,DARK?.28f:.14f),99));
        topRow.addView(badge);
        TextView safeNote=text("仅允许只读浏览与下载",12,MUTED);safeNote.setGravity(Gravity.END|Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams safeParams=weight();safeParams.leftMargin=dp(8);topRow.addView(safeNote,safeParams);
        card.addView(topRow);
        String folderText=savedTrees.isEmpty()?"添加需要共享的文件夹":"已选择 "+savedTrees.size()+" / 20 个文件夹";
        TextView folderTextView=text(folderText,14,INK);folderTextView.setPadding(0,dp(10),0,active?dp(12):0);folderTextView.setMaxLines(2);folderTextView.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        card.addView(folderTextView);
        if(active) {
            String codeValue=current.code();
            boolean validCode=codeValue.length()==6;
            LinearLayout codeCard=row();
            GradientDrawable codeBg=shape(SURFACE,CONTROL_RADIUS);codeBg.setStroke(dp(1),LINE);
            codeCard.setBackground(codeBg);codeCard.setPadding(dp(14),dp(10),dp(10),dp(10));
            LinearLayout codeLeft=column();
            codeLeft.addView(text("6 位连接码（10 分钟内有效）",11,MUTED));
            TextView codeView=text(codeValue,validCode?24:14,validCode?INK:0xffd24e54);
            if(validCode){codeView.setTypeface(Typeface.MONOSPACE,Typeface.BOLD);codeView.setLetterSpacing(0.18f);}
            codeView.setTextIsSelectable(true);codeView.setPadding(0,dp(2),0,0);
            codeLeft.addView(codeView);codeCard.addView(codeLeft,weight());
            if(validCode) {
                Button copy=button("复制",false);
                copy.setTextColor(BLUE);copy.setMinHeight(dp(38));copy.setPadding(dp(14),dp(6),dp(14),dp(6));
                copy.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),shape(OUTGOING,CONTROL_RADIUS),null));
                copy.setOnClickListener(v->copyText("连接码",codeValue));
                LinearLayout.LayoutParams copyParams=new LinearLayout.LayoutParams(-2,-2);copyParams.leftMargin=dp(8);
                codeCard.addView(copy,copyParams);
            }
            card.addView(codeCard);
        }
        LinearLayout.LayoutParams cardParams=new LinearLayout.LayoutParams(-1,-2);cardParams.bottomMargin=dp(10);
        box.addView(card,cardParams);
        Dialog dialog=sheet("共享本机文件夹",box,null,null);
        for(Uri folder:savedTrees) {
            LinearLayout item=row();item.setPadding(dp(10),dp(6),0,dp(6));
            TextView title=text(folderDisplayName(folder),14,INK);title.setMaxLines(2);title.setEllipsize(TextUtils.TruncateAt.MIDDLE);item.addView(title,weight());
            IconButton remove=new IconButton("close","移除共享文件夹",MUTED);item.addView(remove,new LinearLayout.LayoutParams(dp(48),dp(48)));
            remove.setOnClickListener(v->confirm("移除共享文件夹","仅取消这个目录的共享，不删除文件；当前共享连接将结束。","移除",()->{ChatService expected=service;if(expected!=null)expected.removeSharedFolder(folder,success->{if(success&&!isDestroyed()){dialog.dismiss();sharing();}});}));box.addView(item);
        }
        if(current==null&&!savedTrees.isEmpty())fileAction(box,dialog,"开启已选文件夹",()->{ChatService expected=service;if(expected!=null)expected.shareFolders(savedTrees,success->{if(success&&!isFinishing()&&!isDestroyed()&&service==expected)sharing();});});
        fileAction(box,dialog,"添加文件夹并开启",()->{
            try {Intent pick=new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION|Intent.FLAG_GRANT_PREFIX_URI_PERMISSION);if(!savedTrees.isEmpty())pick.putExtra(android.provider.DocumentsContract.EXTRA_INITIAL_URI,savedTrees.get(0));startActivityForResult(pick,24);}
            catch(ActivityNotFoundException e){error("系统没有可用的文件夹选择器");}
        });
        if(current!=null)fileAction(box,dialog,"停止共享",()->{if(service!=null)service.stopSharing();});
        fileAction(box,dialog,"清除已配对设备",()->{try{PhoneRemoteService.stopActive();SharedFiles activeShared=service==null?null:service.sharedFiles();if(activeShared!=null)activeShared.forgetPairings();else new ToolsPairings(this).clear();}catch(java.io.IOException e){error(e.getMessage());}});
    }
    private CharSequence searchHighlight(String value,String query) {
        SpannableString spans=new SpannableString(value);
        if(query.isEmpty())return spans;
        for(int i=0;i+query.length()<=value.length();) {
            if(value.regionMatches(true,i,query,0,query.length())) {
                spans.setSpan(new android.text.style.BackgroundColorSpan(blend(0xfff4b92a,SURFACE,.48f)),i,i+query.length(),Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
                i+=query.length();
            } else i++;
        }
        return spans;
    }
    private static String searchPreview(String value,String query) {
        value=MessageText.normalizeLineBreaks(value);
        query=MessageText.normalizeLineBreaks(query);
        String preview=MessageText.preview(value);
        if(ChatStore.contains(preview,query))return preview;
        for(int i=0;i+query.length()<=value.length();i++)if(value.regionMatches(true,i,query,0,query.length())) {
            int start=Math.max(0,i-60),end=Math.min(value.length(),i+Math.max(query.length(),180));
            if(start>0&&Character.isLowSurrogate(value.charAt(start)))start--;
            if(end<value.length()&&Character.isHighSurrogate(value.charAt(end-1)))end--;
            return (start>0?"… ":"")+value.substring(start,end)+(end<value.length()?" …":"");
        }
        return preview;
    }
    private void showHistory(ChatStore.Conversation conversation) {
        if(service==null)return;
        closeHistory();finishSelection();
        ChatService source=service;String page=selected;int generation=editorGeneration;
        Dialog dialog=new Dialog(this) {
            @Override public boolean dispatchTouchEvent(MotionEvent event){dismissSelectionOutside(event);return super.dispatchTouchEvent(event);}
        };dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);historyDialog=dialog;
        LinearLayout frame=column();frame.setBackground(shape(SURFACE,18));frame.setPadding(dp(16),dp(12),dp(16),dp(12));
        LinearLayout heading=row();TextView caption=text("聊天记录 · "+conversation.displayName(),18,INK);caption.setTypeface(null,Typeface.BOLD);caption.setMaxLines(2);caption.setEllipsize(TextUtils.TruncateAt.END);heading.addView(caption,weight());
        IconButton close=new IconButton("close","关闭聊天记录",MUTED);heading.addView(close,new LinearLayout.LayoutParams(dp(44),dp(44)));close.setOnClickListener(v->dialog.dismiss());frame.addView(heading);
        EditText input=new EditText(this);configureInput(input);input.setSingleLine();input.setTextSize(15);input.setTextColor(INK);input.setHintTextColor(MUTED);input.setHint("搜索消息或文件名");input.setContentDescription("搜索聊天记录");input.setBackground(shape(PALE,CONTROL_RADIUS));input.setPadding(dp(14),dp(10),dp(14),dp(10));input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(200)});frame.addView(input,new LinearLayout.LayoutParams(-1,-2));
        TextView count=text("正在读取聊天记录…",12,MUTED);count.setContentDescription("聊天记录搜索结果");count.setPadding(0,dp(10),0,dp(10));frame.addView(count);
        ListView results=new ListView(this);results.setContentDescription("聊天记录搜索列表");results.setDivider(null);results.setBackgroundColor(WALL);frame.addView(results,new LinearLayout.LayoutParams(-1,0,1));
        LinearLayout navigation=row();navigation.setGravity(Gravity.CENTER_VERTICAL);frame.addView(navigation);navigation.setVisibility(View.GONE);
        LinearLayout numbers=row();navigation.addView(numbers,new LinearLayout.LayoutParams(0,-2,1));
        EditText destination=new EditText(this);configureInput(destination);destination.setSingleLine();destination.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        destination.setContentDescription("跳转页码");destination.setHint("页码");destination.setTextColor(INK);destination.setHintTextColor(MUTED);
        destination.setSelectAllOnFocus(true);destination.setTextSize(14);destination.setGravity(Gravity.CENTER);destination.setPadding(dp(4),0,dp(4),0);
        destination.setImeOptions(android.view.inputmethod.EditorInfo.IME_ACTION_GO);navigation.addView(destination,new LinearLayout.LayoutParams(dp(60),dp(48)));
        Button jump=button("跳转",false);jump.setMinWidth(0);jump.setPadding(dp(8),0,dp(8),0);navigation.addView(jump,new LinearLayout.LayoutParams(dp(56),dp(48)));
        List<LanNode.Message> matches=new ArrayList<>();
        class Presentation {
            final CharSequence stamp,content;
            final List<CharSequence> files=new ArrayList<>();
            Presentation(LanNode.Message message,String query,SimpleDateFormat date) {
                stamp=messageMeta(date.format(new Date(message.time))+" · "+(message.outgoing?"我":conversation.displayName()),message,false);
                content=searchHighlight(searchPreview(message.text,query),query);
                for(Protocol.Attachment file:message.files)files.add(searchHighlight(file.name,query));
            }
        }
        List<Presentation> presentations=new ArrayList<>();
        java.util.concurrent.atomic.AtomicLong revision=new java.util.concurrent.atomic.AtomicLong();
        class ResultRow extends LinearLayout {
            final LinearLayout bubble=column();
            final TextView stamp=text("",12,MUTED),content=text("",messageSize(),INK);
            final List<TextView> files=new ArrayList<>();
            Presentation bound;
            Boolean outgoing;
            ResultRow(){
                super(MainActivity.this);setOrientation(LinearLayout.VERTICAL);setPadding(dp(10),dp(8),dp(10),dp(8));setFocusable(true);setContentDescription("定位到聊天消息");
                bubble.setPadding(dp(14),dp(10),dp(14),dp(10));addView(bubble);bubble.addView(stamp);
                content.setLineSpacing(dp(3),1);content.setPadding(0,dp(6),0,dp(4));bubble.addView(content);
            }
        }
        BaseAdapter adapter=new BaseAdapter() {
            public int getCount(){return matches.size();} public Object getItem(int position){return matches.get(position);} public long getItemId(int position){return position;}
            public View getView(int position,View recycled,ViewGroup parent) {
                LanNode.Message message=matches.get(position);Presentation display=presentations.get(position);
                ResultRow item=recycled instanceof ResultRow?(ResultRow)recycled:new ResultRow();
                long shown=revision.get();View.OnClickListener locate=v->{
                    if(shown!=revision.get()||historyDialog!=dialog||!dialog.isShowing()||!visible||isDestroyed()||service!=source||generation!=editorGeneration||!page.equals(selected))return;
                    dialog.dismiss();locateHistoryMessage(conversation.peer.id(),message);
                };
                item.setOnClickListener(locate);
                if(item.bound!=display) {
                    item.bound=display;item.stamp.setText(display.stamp);item.content.setText(display.content);item.content.setVisibility(message.text.isEmpty()?View.GONE:View.VISIBLE);
                    if(item.outgoing==null||item.outgoing!=message.outgoing){item.outgoing=message.outgoing;item.bubble.setBackground(shape(message.outgoing?OUTGOING:SURFACE,CONTROL_RADIUS));}
                    while(item.files.size()<display.files.size()) {TextView name=text("",14,INK);name.setPadding(0,dp(6),0,dp(4));item.files.add(name);item.bubble.addView(name);}
                    for(int i=0;i<item.files.size();i++){TextView name=item.files.get(i);boolean used=i<display.files.size();name.setVisibility(used?View.VISIBLE:View.GONE);if(used)name.setText(display.files.get(i));}
                }
                return item;
            }
        };results.setAdapter(adapter);
        class Pages {
            ChatStore.HistoryIndex index;
            final Handler handler=new Handler(Looper.getMainLooper());
            final Runnable search=()->load(1);
            void searchChanged(){revision.incrementAndGet();index=null;handler.removeCallbacks(search);handler.postDelayed(search,150);}
            void load(int wanted) {
                handler.removeCallbacks(search);
                long request=revision.incrementAndGet();String query=input.getText().toString();
                matches.clear();presentations.clear();adapter.notifyDataSetChanged();count.setText("正在读取聊天记录…");navigation.setVisibility(View.GONE);
                source.searchHistory(conversation.peer.id(),query,wanted,index,()->revision.get()==request,found->{
                    if(historyDialog!=dialog||!dialog.isShowing()||!visible||service!=source||generation!=editorGeneration||!page.equals(selected))return;
                    matches.addAll(found.rows);SimpleDateFormat date=new SimpleDateFormat("M月d日 HH:mm",Locale.CHINA);
                    for(LanNode.Message message:found.rows)presentations.add(new Presentation(message,query,date));
                    index=found.index;adapter.notifyDataSetChanged();results.setSelection(0);
                    int total=index.pageCount();count.setText(index.total+(query.isEmpty()?" 条记录":" 条匹配记录")+(total==0?"":" · 第 "+found.number+" / "+total+" 页"));
                    numbers.removeAllViews();destination.setText("");destination.setError(null);
                    int first=Math.max(1,Math.min(found.number-1,total-2));
                    for(int number=first;number<=Math.min(total,first+2);number++) {
                        final int chosen=number;boolean active=number==found.number;
                        Button pick=button(Integer.toString(number),active);pick.setMinWidth(0);pick.setMinimumWidth(0);pick.setSingleLine();pick.setTextSize(14);pick.setPadding(dp(2),0,dp(2),0);
                        pick.setContentDescription("第 "+number+" 页");pick.setSelected(active);pick.setOnClickListener(view->{if(!active)load(chosen);});
                        LinearLayout.LayoutParams slot=new LinearLayout.LayoutParams(0,dp(48),1);slot.rightMargin=dp(4);numbers.addView(pick,slot);
                    }
                    navigation.setVisibility(total>1?View.VISIBLE:View.GONE);
                },count::setText);
            }
            void jump() {
                if(index==null)return;
                int wanted;
                try {wanted=Integer.parseInt(destination.getText().toString());}catch(NumberFormatException invalid){wanted=0;}
                if(wanted<1 || wanted>index.pageCount()){destination.setError("请输入 1 至 "+index.pageCount()+" 的页码");destination.requestFocus();return;}
                getSystemService(InputMethodManager.class).hideSoftInputFromWindow(destination.getWindowToken(),0);
                load(wanted);
            }
        }
        Pages pages=new Pages();jump.setOnClickListener(view->pages.jump());
        destination.setOnEditorActionListener((view,action,event)->{if(action!=android.view.inputmethod.EditorInfo.IME_ACTION_GO)return false;pages.jump();return true;});
        input.addTextChangedListener(new TextWatcher(){public void beforeTextChanged(CharSequence s,int start,int count,int after){}public void onTextChanged(CharSequence s,int start,int before,int count){pages.searchChanged();}public void afterTextChanged(Editable value){}});
        dialog.setOnDismissListener(d->{revision.incrementAndGet();pages.handler.removeCallbacks(pages.search);finishSelection();if(historyDialog==dialog){historyDialog=null;if(visible&&!isDestroyed())changed();}});
        dialog.setContentView(frame);Window window=dialog.getWindow();if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.setGravity(Gravity.BOTTOM);window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);SystemBars.applyOverlay(window,DARK);}
        dialog.show();if(window!=null){window.setLayout(-1,Math.max(dp(240),(int)(getResources().getDisplayMetrics().heightPixels*.85f)));SystemBars.applyOverlay(window,DARK);}
        pages.load(1);
    }
    private void deleteConversation(ChatStore.Conversation c,boolean clearOnly) {
        LinearLayout box=column();TextView hint=text(clearOnly?"仅清空本机聊天记录，已接收文件保留。":"仅从列表移除，已接收文件保留，新消息会自动恢复。",13,MUTED);hint.setPadding(dp(2),0,dp(2),dp(10));box.addView(hint);
        LinearLayout toggleCard=column();GradientDrawable cardBg=shape(PALE,CONTROL_RADIUS);cardBg.setStroke(dp(1),LINE);toggleCard.setBackground(cardBg);toggleCard.setPadding(dp(14),dp(2),dp(14),dp(2));
        Switch clear=settingSwitch("同时删除聊天记录",clearOnly);clear.setEnabled(!clearOnly);toggleCard.addView(clear);box.addView(toggleCard);
        sheet(clearOnly?"清空聊天记录":"删除联系人",box,"确认删除",dialog->{
            if(service==null)return;String id=c.peer.id();
            service.removeConversation(id,clear.isChecked(),!clearOnly,success->{if(!success||isDestroyed())return;dialog.dismiss();if(selected.equals(id)){if(clearOnly){historySignature="";changed();}else open("");}});
        });
    }
    private void addPeer() {
        if(service==null||!service.online()) { error("请先连接"); return; }
        LinearLayout content=column(); EditText input=formField(content,"IP 地址",""); input.setHint("192.168.1.100:2425"); input.setHintTextColor(MUTED); input.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_VARIATION_URI);
        sheet("添加联系人",content,"添加",dialog->{ try { LanNode.endpoint(input.getText().toString()); service.probe(input.getText().toString()); dialog.dismiss(); } catch(Exception e) { input.setError(e.getMessage()); } });
    }
    private void settings() {
        SharedPreferences prefs=getSharedPreferences("settings",MODE_PRIVATE); LinearLayout box=column();
        settingHeading(box,"个人资料");
        String normalizedAvatar=Protocol.normalizeAvatar(prefs.getString("avatar","default"));final String[] avatarValue={normalizedAvatar==null?"default":normalizedAvatar};
        String self="device:"+prefs.getString("device_id","");LinearLayout avatarRow=row();TextView portrait=text("",18,INK);portrait.setContentDescription("个人头像");bindAvatar(portrait,self,"个人头像",false,avatarValue[0]);avatarRow.addView(portrait,new LinearLayout.LayoutParams(dp(64),dp(64)));
        Button changeAvatar=button("更换头像",false);LinearLayout.LayoutParams avatarButton=weight();avatarButton.leftMargin=dp(12);avatarRow.addView(changeAvatar,avatarButton);box.addView(avatarRow,settingSpacing(0,10));
        changeAvatar.setOnClickListener(v->showAvatarPicker(avatarValue[0],value->{avatarValue[0]=value;bindAvatar(portrait,self,"个人头像",false,value);}));
        EditText name=formField(box,"昵称",prefs.getString("name",Build.MODEL)), group=formField(box,"部门",prefs.getString("group","我的局域网"));
        settingHeading(box,"连接");
        EditText port=formField(box,"端口",String.format(Locale.ROOT,"%d",prefs.getInt("port",2425)));
        port.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        Switch autoConnect=settingSwitch("打开应用时自动恢复连接",prefs.getBoolean("auto_connect",true)); box.addView(autoConnect,settingSpacing(6,0));
        Switch legacy=settingSwitch("允许旧版明文协议",ChatService.allowLegacy(prefs));box.addView(legacy,new LinearLayout.LayoutParams(-1,-2));box.addView(text("兼容旧设备明文；已配对设备与共享远控始终加密",12,MUTED));
        settingHeading(box,"文件接收");
        Switch automatic=settingSwitch("自动接收文件",receivedStorage.automatic()); automatic.setContentDescription("自动接收文件"); box.addView(automatic,new LinearLayout.LayoutParams(-1,-2));
        TextView hint=text("仅用于可信局域网，不自动打开文件",12,MUTED); box.addView(hint);
        folderLabel=text(receivedStorage.folderLabel(),13,MUTED); folderLabel.setPadding(0,dp(10),0,dp(10)); folderLabel.setContentDescription("文件保存目录"); box.addView(folderLabel);
        LinearLayout folderBtns=row();
        Button choose=button("选择保存文件夹",false); Button reset=button("恢复应用内",false);
        LinearLayout.LayoutParams chp=weight();chp.rightMargin=dp(8);folderBtns.addView(choose,chp);folderBtns.addView(reset,weight());box.addView(folderBtns);
        choose.setOnClickListener(v->chooseFolder());
        reset.setOnClickListener(v->{ receivedStorage.useInternal(); folderLabel.setText(receivedStorage.folderLabel()); if(service!=null) service.receiveSettingsChanged(); });
        settingHeading(box,"外观");
        Switch follow=settingSwitch("跟随系统切换",prefs.getBoolean("follow_system_theme",false));box.addView(follow,settingSpacing(0,8));
        Switch dark=settingSwitch("夜间模式",follow.isChecked()?systemDark():prefs.getBoolean("dark_mode",false));dark.setEnabled(!follow.isChecked());box.addView(dark,new LinearLayout.LayoutParams(-1,-2));
        String initialAccent=prefs.getString("accent","blue"), initialBackground=prefs.getString("chat_background","orbits"), initialBackgroundImage=prefs.getString("background_image","");
        int initialFontSize=messageSize();
        RadioGroup accent=settingChoices(6);accent.setContentDescription("主题颜色");
        String selectedAccent=initialAccent;String[] keys={"blue","green","purple","amber","rose","slate"},names={"经典蓝","青绿色","紫罗兰","琥珀金","绯樱粉","墨韵灰"};
        for(int i=0;i<keys.length;i++){RadioButton choice=new RadioButton(this);choice.setId(View.generateViewId());choice.setTag(keys[i]);choice.setText(names[i]);choice.setTextSize(15);choice.setTextColor(INK);choice.setButtonTintList(new android.content.res.ColorStateList(new int[][]{new int[]{android.R.attr.state_checked},new int[]{}},new int[]{BLUE,MUTED}));choice.setMinHeight(dp(48));choice.setButtonDrawable(null);choice.setGravity(Gravity.CENTER);choice.setPadding(dp(4),0,dp(4),0);accent.addView(choice,choiceSpacing(i,keys.length));if(keys[i].equals(selectedAccent))accent.check(choice.getId());}
        accent.setOnCheckedChangeListener((groupView,checked)->{for(int i=0;i<groupView.getChildCount();i++){View choice=groupView.getChildAt(i);choice.setBackground(shape(choice.getId()==checked?BUBBLE:SURFACE,CONTROL_RADIUS));}});
        for(int i=0;i<accent.getChildCount();i++){View choice=accent.getChildAt(i);choice.setBackground(shape(choice.getId()==accent.getCheckedRadioButtonId()?BUBBLE:SURFACE,CONTROL_RADIUS));}
        TextView colorHeading=text("主题颜色",14,INK);box.addView(colorHeading,settingSpacing(12,0));
        box.addView(accent,settingSpacing(8,20));
        LinearLayout preview=column();preview.setPadding(dp(14),dp(12),dp(14),dp(12));
        TextView sampleIn=text("今天也保持联系",messageSize(),INK),sampleOut=text("消息与输入字号预览",messageSize(),INK);
        sampleIn.setPadding(dp(12),dp(8),dp(12),dp(8));sampleOut.setPadding(dp(12),dp(8),dp(12),dp(8));
        LinearLayout.LayoutParams inParams=new LinearLayout.LayoutParams(-2,-2),outParams=new LinearLayout.LayoutParams(-2,-2);outParams.gravity=Gravity.END;outParams.topMargin=dp(8);preview.addView(sampleIn,inParams);preview.addView(sampleOut,outParams);box.addView(preview,box.indexOfChild(follow),settingSpacing(0,16));
        // 预览与 applyTheme 同表取色：底色为聊天背景，收到/发出样例分别取表面色与发出气泡色。
        Runnable updatePreview=()->{View selectedColor=accent.findViewById(accent.getCheckedRadioButtonId());int[] p=palette(selectedColor==null?"blue":selectedColor.getTag().toString(),dark.isChecked());preview.setBackground(shape(wall(p),CONTROL_RADIUS));sampleIn.setBackground(shape(p[1],CONTROL_RADIUS));sampleOut.setBackground(shape(blend(p[0],p[1],dark.isChecked()?.42f:.15f),CONTROL_RADIUS));sampleIn.setTextColor(p[2]);sampleOut.setTextColor(p[2]);};
        dark.setOnCheckedChangeListener((buttonView,checked)->updatePreview.run());
        follow.setOnCheckedChangeListener((buttonView,checked)->{dark.setEnabled(!checked);if(checked)dark.setChecked(systemDark());updatePreview.run();});
        for(int i=0;i<accent.getChildCount();i++)accent.getChildAt(i).setOnClickListener(v->updatePreview.run());updatePreview.run();
        TextView sizeCaption=text("聊天字号 · "+messageSize()+" sp",14,INK);sizeCaption.setPadding(0,dp(14),0,0);box.addView(sizeCaption);
        SeekBar fontSize=new SeekBar(this);fontSize.setContentDescription("聊天字号");fontSize.setThumbTintList(android.content.res.ColorStateList.valueOf(BLUE));fontSize.setProgressTintList(android.content.res.ColorStateList.valueOf(BLUE));fontSize.setProgressBackgroundTintList(android.content.res.ColorStateList.valueOf(blend(INK,SURFACE,.18f)));fontSize.setMax(10);fontSize.setProgress(messageSize()-14);box.addView(fontSize,new LinearLayout.LayoutParams(-1,dp(48)));
        fontSize.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener(){public void onStartTrackingTouch(SeekBar bar){}public void onStopTrackingTouch(SeekBar bar){}public void onProgressChanged(SeekBar bar,int value,boolean user){sizeCaption.setText("聊天字号 · "+(value+14)+" sp");sampleIn.setTextSize(value+14);sampleOut.setTextSize(value+14);}});
        TextView wallpaperHeading=text("聊天背景",14,INK);wallpaperHeading.setPadding(0,dp(12),0,dp(8));box.addView(wallpaperHeading);
        RadioGroup backgrounds=settingChoices(6);wallpaperChoices=backgrounds;String[] backgroundKeys={"none","orbits","lines","grid","aurora","image"},backgroundNames={"纯色","星环","流线","网格","极光","我的图片"};
        for(int i=0;i<backgroundKeys.length;i++){RadioButton choice=new RadioButton(this);choice.setId(View.generateViewId());choice.setTag(backgroundKeys[i]);choice.setText(backgroundNames[i]);choice.setTextColor(INK);choice.setTextSize(14);choice.setMinHeight(dp(48));choice.setButtonDrawable(null);choice.setGravity(Gravity.CENTER);choice.setPadding(dp(2),0,dp(2),0);backgrounds.addView(choice,choiceSpacing(i,backgroundKeys.length));if(backgroundKeys[i].equals(initialBackground))backgrounds.check(choice.getId());}box.addView(backgrounds,settingSpacing(0,12));
        backgrounds.setOnCheckedChangeListener((groupView,checked)->{for(int i=0;i<groupView.getChildCount();i++){View choice=groupView.getChildAt(i);choice.setBackground(shape(choice.getId()==checked?BUBBLE:PALE,CONTROL_RADIUS));}});
        for(int i=0;i<backgrounds.getChildCount();i++){View choice=backgrounds.getChildAt(i);choice.setBackground(shape(choice.getId()==backgrounds.getCheckedRadioButtonId()?BUBBLE:PALE,CONTROL_RADIUS));}
        String[] presetNames={"晴空 · 经典","薄荷 · 日间","暖阳 · 日间","绯樱 · 日间","深海 · 夜间","森幽 · 夜间","暮紫 · 夜间","石墨 · 夜间"};
        int[] presetAccentIndex={0,1,3,4,0,1,2,5}, presetBgIndex={1,2,4,1,1,2,4,3};
        boolean[] presetDark={false,false,false,false,true,true,true,true};
        for(int lineIndex=0;lineIndex<4;lineIndex++){LinearLayout presetRow=row();presetRow.setBaselineAligned(false);presetRow.setGravity(Gravity.TOP);box.addView(presetRow,box.indexOfChild(preview)+1+lineIndex,new LinearLayout.LayoutParams(-1,-2));for(int columnIndex=0;columnIndex<2;columnIndex++){int preset=lineIndex*2+columnIndex;Button pick=button(presetNames[preset],false);LinearLayout.LayoutParams pp=weight();pp.setMargins(columnIndex==0?0:dp(6),0,columnIndex==0?dp(6):0,dp(12));presetRow.addView(pick,pp);pick.setOnClickListener(v->{follow.setChecked(false);dark.setChecked(presetDark[preset]);accent.check(accent.getChildAt(presetAccentIndex[preset]).getId());backgrounds.check(backgrounds.getChildAt(presetBgIndex[preset]).getId());updatePreview.run();});}}
        Button chooseBackground=button("选择背景图片…",false);box.addView(chooseBackground,settingSpacing(0,4));chooseBackground.setOnClickListener(v->{try{startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("image/*").addCategory(Intent.CATEGORY_OPENABLE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),23);}catch(ActivityNotFoundException e){error("系统没有可用的图片选择器");}});
        settingHeading(box,"存储管理");
        LinearLayout clearRow=row();
        Button clear=button("清理已接收文件",false); Button clearOffers=button("清理发送缓存",false);
        LinearLayout.LayoutParams clrP=weight();clrP.rightMargin=dp(8);clearRow.addView(clear,clrP);clearRow.addView(clearOffers,weight());box.addView(clearRow,settingSpacing(6,8));
        clear.setOnClickListener(v->clearReceivedFiles(clear));
        box.addView(text("仅清理飞Q接收的文件，包含以前保存目录中的文件；其他文件保留。",12,MUTED),settingSpacing(0,8));
        clearOffers.setOnClickListener(v->confirm("清理发送缓存","撤销尚未接收的文件邀请并删除暂存副本？原文件不受影响。","清理",()->{
            if(service==null) { error("连接服务尚未就绪，请稍后重试"); return; }
            service.clearOffers(result->error(result.files==0?"没有可清理的发送缓存":"已撤销未接收的文件邀请，删除 "+result.files+" 个暂存副本，释放 "+size(result.bytes)));
        }));
        settingHeading(box,"故障诊断");
        TextView connectionDetails=text(service==null?"尚未连接局域网":service.connectionDetails(),12,MUTED);selectable(connectionDetails);box.addView(connectionDetails,settingSpacing(0,10));
        Switch diagnosticSwitch=settingSwitch("记录故障诊断",prefs.getBoolean("diagnostics",false));box.addView(diagnosticSwitch,new LinearLayout.LayoutParams(-1,-2));
        box.addView(text("仅记时间与事件码（限 128 KiB），不含聊天与路径",12,MUTED));
        Diagnostics journal=new Diagnostics(this);
        LinearLayout logRow=row();
        Button viewLog=button("查看诊断记录",false); Button clearLog=button("清除诊断记录",false);
        LinearLayout.LayoutParams vlP=weight();vlP.rightMargin=dp(8);logRow.addView(viewLog,vlP);logRow.addView(clearLog,weight());box.addView(logRow,settingSpacing(12,4));
        viewLog.setOnClickListener(v->{try{TextView log=text(journal.read(),12,INK);selectable(log);sheet("诊断记录",log,"确定",Dialog::dismiss);}catch(Exception e){error("诊断读取失败："+e.getMessage());}});
        clearLog.setOnClickListener(v->{try{journal.clear();error("诊断记录已清除");}catch(Exception e){error("诊断清除失败："+e.getMessage());}});
        sheet("设置",box,"保存",dialog->{
            String n=name.getText().toString().trim(), g=group.getText().toString().trim(); int p;
            if(n.isEmpty()||n.length()>64) { settingsError(dialog,name,"昵称须为 1–64 个字符"); return; } if(g.length()>64) { settingsError(dialog,group,"分组最多 64 个字符"); return; }
            try { p=Integer.parseInt(port.getText().toString()); if(p<1024||p>65535) throw new NumberFormatException(); } catch(NumberFormatException e) { settingsError(dialog,port,"端口须为 1024–65535"); return; }
            View color=accent.findViewById(accent.getCheckedRadioButtonId());String accentKey=color==null?"blue":color.getTag().toString();
            View backdrop=backgrounds.findViewById(backgrounds.getCheckedRadioButtonId());String backdropKey=backdrop==null?"orbits":backdrop.getTag().toString();
            if(backdropKey.equals("image")&&prefs.getString("background_image","").isEmpty()){dialog.getWindow().getDecorView().findViewWithTag("settings-tab:1").performClick();error("请先选择背景图片");return;}
            int nextSize=fontSize.getProgress()+14;boolean nextDark=follow.isChecked()?systemDark():dark.isChecked();String nextBgImage=prefs.getString("background_image","");
            boolean saved=prefs.edit().putBoolean("allow_legacy_protocol",legacy.isChecked()).putInt("legacy_protocol_policy",ChatService.LEGACY_POLICY_VERSION).putString("avatar",avatarValue[0]).putString("name",n).putString("group",g).putInt("port",p).putBoolean("auto_connect",autoConnect.isChecked()).putBoolean("diagnostics",diagnosticSwitch.isChecked()).putBoolean(ReceivedStorage.AUTO,automatic.isChecked()).putBoolean("follow_system_theme",follow.isChecked()).putBoolean("dark_mode",dark.isChecked()).putString("accent",accentKey).putInt("chat_font_size",nextSize).putString("chat_background",backdropKey).commit();
            if(!saved){error("设置未保存，请检查设备存储空间");return;}if(service!=null){service.receiveSettingsChanged();service.profileSettingsChanged();}dialog.dismiss();
            boolean visualChanged=nextDark!=DARK||!accentKey.equals(initialAccent)||nextSize!=initialFontSize||!backdropKey.equals(initialBackground)||!nextBgImage.equals(initialBackgroundImage);
            if(visualChanged) {
                rerenderPreservingState();
            } else {
                if(!selected.isEmpty()&&messages!=null&&!messageRows.isEmpty()) renderMessages(messageRows);
                changed();
            }
            error("已保存；昵称、头像、主题和接收选项立即生效，端口下次连接生效");
        });
    }
    private void showAvatarPicker(String value,java.util.function.Consumer<String> use) {
        AvatarSelection state=new AvatarSelection();state.value=Protocol.normalizeAvatar(value);if(state.value==null)state.value="default";avatarSelection=state;
        LinearLayout box=column();String identity="device:"+getSharedPreferences("settings",MODE_PRIVATE).getString("device_id","");
        LinearLayout topCard=row();GradientDrawable topBg=shape(PALE,CONTROL_RADIUS);topBg.setStroke(dp(1),LINE);topCard.setBackground(topBg);topCard.setPadding(dp(12),dp(10),dp(12),dp(10));
        state.preview=text("",18,INK);state.preview.setContentDescription("所选头像预览");topCard.addView(state.preview,new LinearLayout.LayoutParams(dp(56),dp(56)));
        Button local=button("本地图片",false),reset=button("默认头像",false);
        GradientDrawable btnBg1=shape(SURFACE,CONTROL_RADIUS);btnBg1.setStroke(dp(1),LINE);local.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),btnBg1,null));
        GradientDrawable btnBg2=shape(SURFACE,CONTROL_RADIUS);btnBg2.setStroke(dp(1),LINE);reset.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),btnBg2,null));
        local.setMinHeight(dp(42));reset.setMinHeight(dp(42));local.setPadding(dp(8),dp(6),dp(8),dp(6));reset.setPadding(dp(8),dp(6),dp(8),dp(6));
        LinearLayout.LayoutParams lp=weight();lp.leftMargin=dp(12);topCard.addView(local,lp);
        LinearLayout.LayoutParams rp=weight();rp.leftMargin=dp(8);topCard.addView(reset,rp);
        box.addView(topCard,settingSpacing(0,10));
        GridLayout grid=new GridLayout(this);grid.setColumnCount(6);grid.setContentDescription("内置头像列表");box.addView(grid,new LinearLayout.LayoutParams(-1,-2));EmojiAvatars avatars=EmojiAvatars.get(this);
        for(int i=0;i<avatars.count();i++) {
            String selected="builtin:"+i;ImageButton choice=new ImageButton(this);choice.setTag(selected);choice.setContentDescription("内置头像："+avatars.description(i));choice.setPadding(dp(4),dp(4),dp(4),dp(4));choice.setImageDrawable(new AvatarDrawable(this,"",false,dp(3),SURFACE,ONLINE,selected));
            GridLayout.LayoutParams spacing=new GridLayout.LayoutParams(GridLayout.spec(i/6),GridLayout.spec(i%6,1f));spacing.width=0;spacing.height=dp(52);spacing.setMargins(dp(2),dp(2),dp(2),dp(2));grid.addView(choice,spacing);state.choices.add(choice);
            choice.setOnClickListener(v->{state.revision++;state.loading=false;state.value=selected;updateAvatarPicker(state,identity);});
        }
        state.local=local;
        reset.setOnClickListener(v->{state.revision++;state.loading=false;state.value="default";updateAvatarPicker(state,identity);});
        local.setOnClickListener(v->{
            avatarImageRequest=state;avatarImageRevision=++state.revision;state.loading=false;updateAvatarPicker(state,identity);
            try{startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("image/*").addCategory(Intent.CATEGORY_OPENABLE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),25);}
            catch(ActivityNotFoundException e){avatarImageRequest=null;error("系统没有可用的图片选择器");}
        });
        state.dialog=sheet("更换头像",box,"使用此头像",dialog->{if(!state.loading){use.accept(state.value);dialog.dismiss();}});
        state.confirm=(Button)state.dialog.getWindow().getDecorView().findViewWithTag("sheet-primary");
        state.dialog.setOnDismissListener(d->{if(avatarSelection==state)avatarSelection=null;});updateAvatarPicker(state,identity);
    }
    private void updateAvatarPicker(AvatarSelection state,String identity) {
        state.confirm.setEnabled(!state.loading);state.local.setText(state.loading?"读取图片中…":"本地图片");
        bindAvatar(state.preview,identity,"所选头像",false,state.value);
        for(ImageButton choice:state.choices){boolean checked=choice.getTag().equals(state.value);choice.setSelected(checked);choice.setBackground(shape(checked?BUBBLE:PALE,CONTROL_RADIUS));}
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
            private int columns,cellHeight;
            @Override protected void onMeasure(int width,int height) {
                // 小屏用网格保留阅读顺序；大字号再减少列数，不压缩触摸区。
                float scale=getResources().getConfiguration().fontScale;
                int available=View.MeasureSpec.getSize(width),gap=dp(10);
                columns=Math.max(1,Math.min(3,(available+gap)/(dp(88*Math.max(1,scale))+gap)));
                int cellWidth=Math.max(1,(available-gap*(columns-1))/columns);cellHeight=dp(48);
                for(int i=0;i<getChildCount();i++){
                    View child=getChildAt(i);child.measure(View.MeasureSpec.makeMeasureSpec(cellWidth,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(0,View.MeasureSpec.UNSPECIFIED));cellHeight=Math.max(cellHeight,child.getMeasuredHeight());
                }
                for(int i=0;i<getChildCount();i++)getChildAt(i).measure(View.MeasureSpec.makeMeasureSpec(cellWidth,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(cellHeight,View.MeasureSpec.EXACTLY));
                int rows=(getChildCount()+columns-1)/columns;setMeasuredDimension(available,rows*cellHeight+Math.max(0,rows-1)*gap);
            }
            @Override protected void onLayout(boolean changed,int left,int top,int right,int bottom) {
                int gap=dp(10);
                for(int i=0;i<getChildCount();i++){View child=getChildAt(i);int x=(i%columns)*(child.getMeasuredWidth()+gap),y=(i/columns)*(cellHeight+gap);child.layout(x,y,x+child.getMeasuredWidth(),y+cellHeight);}
            }
        };
    }
    private void settingHeading(LinearLayout box,String title){
        if(box.getChildCount()>0){View line=new View(this);line.setTag("settings-divider");line.setBackgroundColor(blend(INK,SURFACE,.09f));LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,dp(1));p.topMargin=dp(20);p.bottomMargin=dp(16);box.addView(line,p);}
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
    private void settingsError(Dialog dialog,EditText field,String message) {
        View tab=dialog.getWindow().getDecorView().findViewWithTag("settings-tab:0");if(tab!=null)tab.performClick();field.setError(message);field.requestFocus();
    }
    private Dialog settingsPanel(View content,java.util.function.Consumer<Dialog> handler) {
        finishSelection();Dialog dialog=new Dialog(this){@Override public boolean dispatchTouchEvent(MotionEvent event){dismissSelectionOutside(event);return super.dispatchTouchEvent(event);}};dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        dialog.setOnDismissListener(d->{if(selectionOwner!=null&&selectionOwner.getRootView()==dialog.getWindow().getDecorView())finishSelection();wallpaperChoices=null;});
        LinearLayout frame=new LinearLayout(this){@Override protected void onMeasure(int width,int height){int available=root!=null&&root.getHeight()>0?root.getHeight()-root.getPaddingTop()-root.getPaddingBottom():getResources().getDisplayMetrics().heightPixels;int cap=Math.max(1,(int)(available*.9f));if(View.MeasureSpec.getMode(height)!=View.MeasureSpec.UNSPECIFIED)cap=Math.min(cap,View.MeasureSpec.getSize(height));super.onMeasure(width,View.MeasureSpec.makeMeasureSpec(cap,View.MeasureSpec.EXACTLY));}};
        frame.setOrientation(LinearLayout.VERTICAL);frame.setBackground(shape(SURFACE,18));
        LinearLayout heading=row();heading.setPadding(dp(22),dp(8),dp(10),0);TextView caption=text("设置",20,INK);caption.setContentDescription("设置");caption.setTypeface(null,Typeface.BOLD);heading.addView(caption,weight());IconButton close=new IconButton("close","关闭设置",MUTED);heading.addView(close,new LinearLayout.LayoutParams(dp(48),dp(48)));close.setOnClickListener(v->dialog.dismiss());frame.addView(heading);
        String[] names={"通用","外观","文件","诊断"};LinearLayout[] pages=new LinearLayout[4];Button[] tabs=new Button[4];
        LinearLayout original=(LinearLayout)content;List<View> fields=new ArrayList<>();for(int i=0;i<original.getChildCount();i++)fields.add(original.getChildAt(i));original.removeAllViews();
        for(int i=0;i<pages.length;i++){pages[i]=column();pages[i].setTag("settings-page:"+i);pages[i].setVisibility(i==0?View.VISIBLE:View.GONE);original.addView(pages[i],new LinearLayout.LayoutParams(-1,-2));}
        int page=0;
        for(View field:fields){
            Object tag=field.getTag();if("settings-divider".equals(tag))continue;
            if("section:外观".equals(tag))page=1;else if("section:文件接收".equals(tag)||"section:存储管理".equals(tag))page=2;else if("section:故障诊断".equals(tag))page=3;
            if(tag instanceof String&&((String)tag).startsWith("section:")&&pages[page].getChildCount()>0)((LinearLayout.LayoutParams)field.getLayoutParams()).topMargin=dp(24);
            pages[page].addView(field);
        }
        ScrollView scroller=new ScrollView(this);scroller.setFillViewport(false);LinearLayout inset=column();inset.setPadding(dp(22),dp(16),dp(22),dp(24));inset.addView(content,new LinearLayout.LayoutParams(-1,-2));scroller.addView(inset);
        LinearLayout navigation=row();navigation.setTag("settings-navigation");navigation.setPadding(dp(14),0,dp(14),0);
        java.util.function.IntConsumer select=index->{
            finishSelection();for(int i=0;i<pages.length;i++){pages[i].setVisibility(i==index?View.VISIBLE:View.GONE);tabs[i].setSelected(i==index);tabs[i].setTextColor(i==index?BLUE:MUTED);tabs[i].setContentDescription("设置标签："+names[i]+(i==index?"，已选择":""));}
            scroller.scrollTo(0,0);scroller.requestLayout();
            android.view.inputmethod.InputMethodManager keyboard=getSystemService(android.view.inputmethod.InputMethodManager.class);if(keyboard!=null)keyboard.hideSoftInputFromWindow(frame.getWindowToken(),0);
            View focused=dialog.getCurrentFocus();if(focused instanceof EditText)focused.clearFocus();
        };
        for(int i=0;i<names.length;i++){
            final int index=i;Button tab=new Button(this){private final Paint ink=new Paint(Paint.ANTI_ALIAS_FLAG);@Override protected void onDraw(Canvas canvas){super.onDraw(canvas);ink.setColor(blend(INK,SURFACE,.09f));canvas.drawRect(0,getHeight()-dp(1),getWidth(),getHeight(),ink);if(isSelected()){ink.setColor(BLUE);canvas.drawRoundRect(getWidth()/2f-dp(16),getHeight()-dp(3),getWidth()/2f+dp(16),getHeight(),dp(1),dp(1),ink);}}};
            tab.setText(names[i]);tab.setTextSize(15);tab.setAllCaps(false);tab.setGravity(Gravity.CENTER);tab.setMinHeight(dp(48));tab.setMinimumWidth(0);tab.setPadding(dp(2),dp(10),dp(2),dp(10));tab.setStateListAnimator(null);tab.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),shape(SURFACE,0),null));tab.setTag("settings-tab:"+i);tabs[i]=tab;navigation.addView(tab,new LinearLayout.LayoutParams(0,-2,1));tab.setOnClickListener(v->select.accept(index));
        }
        frame.addView(navigation);frame.addView(scroller,new LinearLayout.LayoutParams(-1,0,1));
        LinearLayout footer=row();footer.setPadding(dp(20),dp(10),dp(20),dp(14));footer.setGravity(Gravity.END|Gravity.CENTER_VERTICAL);Button done=button("保存",true);done.setMinimumWidth(dp(112));footer.addView(done,new LinearLayout.LayoutParams(-2,-2));done.setOnClickListener(v->handler.accept(dialog));frame.addView(footer);
        dialog.setContentView(frame);Window window=dialog.getWindow();if(window!=null){window.setBackgroundDrawableResource(android.R.color.transparent);window.setGravity(Gravity.BOTTOM);window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND);window.setDimAmount(.32f);window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);SystemBars.applyOverlay(window,DARK);}
        select.accept(0);dialog.show();if(window!=null){window.setLayout(-1,-2);SystemBars.applyOverlay(window,DARK);}return dialog;
    }
    private Dialog sheet(String name,View content,String action,java.util.function.Consumer<Dialog> handler) {
        if(name.equals("设置"))return settingsPanel(content,handler);
        finishSelection();
        Dialog dialog=new Dialog(this) {
            @Override public boolean dispatchTouchEvent(MotionEvent event) { dismissSelectionOutside(event); return super.dispatchTouchEvent(event); }
        }; dialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
        dialog.setOnDismissListener(d->{ if(selectionOwner!=null&&selectionOwner.getRootView()==dialog.getWindow().getDecorView()) finishSelection(); });
        LinearLayout box=column(); box.setPadding(dp(18),dp(8),dp(18),dp(18)); box.setBackground(shape(SURFACE,20));
        View handle=new View(this); handle.setBackground(shape(blend(INK,SURFACE,DARK?.22f:.14f),99));
        LinearLayout.LayoutParams handleParams=new LinearLayout.LayoutParams(dp(36),dp(4)); handleParams.gravity=Gravity.CENTER_HORIZONTAL; handleParams.bottomMargin=dp(6); box.addView(handle,handleParams);
        LinearLayout heading=row(); heading.setPadding(dp(2),0,0,dp(8)); TextView caption=text(name,18,INK); caption.setTypeface(null,Typeface.BOLD); caption.setMaxLines(2); caption.setEllipsize(TextUtils.TruncateAt.END); caption.setContentDescription(name); heading.addView(caption,weight()); IconButton close=new IconButton("close","关闭",MUTED); heading.addView(close,new LinearLayout.LayoutParams(dp(42),dp(42))); close.setOnClickListener(v->dialog.dismiss()); box.addView(heading);
        box.addView(content,new LinearLayout.LayoutParams(-1,-2));
        if(action!=null) {
            boolean danger=action.contains("删除")||action.contains("清空")||action.contains("取消发送")||action.contains("清除")||action.contains("撤销")||action.contains("拒绝")||action.contains("结束");
            Button done=button(action,true); done.setTag("sheet-primary");
            if(danger){int tone=DARK?0xffe06066:0xffd24e54;done.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x33ffffff),shape(tone,CONTROL_RADIUS),null));done.setTextColor(Color.WHITE);}
            LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2); p.topMargin=dp(14); box.addView(done,p); done.setOnClickListener(v->{ if(handler!=null) handler.accept(dialog); });
        }
        // 整张操作面板可滚动；遵守父窗口实际给出的高度，避免键盘、横屏或长标题把底部按钮挤到屏外。
        ScrollView scroller=new ScrollView(this) { @Override protected void onMeasure(int width,int height) {
            int visible=root!=null&&root.getHeight()>0?root.getHeight()-root.getPaddingTop()-root.getPaddingBottom():getResources().getDisplayMetrics().heightPixels;
            int cap=Math.max(1,(int)(visible*.9f));
            if(View.MeasureSpec.getMode(height)!=View.MeasureSpec.UNSPECIFIED) cap=Math.min(cap,View.MeasureSpec.getSize(height));
            super.onMeasure(width,View.MeasureSpec.makeMeasureSpec(cap,View.MeasureSpec.AT_MOST));
        } }; scroller.setFillViewport(false); scroller.addView(box);
        dialog.setContentView(scroller); Window window=dialog.getWindow(); if(window!=null) { window.setBackgroundDrawableResource(android.R.color.transparent); window.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND); window.setDimAmount(.32f); window.setGravity(Gravity.BOTTOM); window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE); SystemBars.applyOverlay(window,DARK); }
        dialog.show(); if(window!=null) { window.setLayout(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT); SystemBars.applyOverlay(window,DARK); } return dialog;
    }
    private void confirm(String title,String body,String action,Runnable confirm) {
        LinearLayout panel=column();
        TextView content=text(body,15,blend(INK,MUTED,.82f)); content.setLineSpacing(dp(5),1f); content.setPadding(dp(2),dp(2),dp(2),dp(16)); panel.addView(content);
        LinearLayout actions=row();
        Button cancel=button("取消",false);
        GradientDrawable cancelBg=shape(PALE,CONTROL_RADIUS); cancelBg.setStroke(dp(1),LINE);
        cancel.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(BUBBLE),cancelBg,null));
        Button done=button(action,true); done.setTag("sheet-primary");
        boolean danger=action.contains("删除")||action.contains("清空")||action.contains("取消发送")||action.contains("清除")||action.contains("撤销")||action.contains("拒绝");
        if(danger){int tone=DARK?0xffe06066:0xffd24e54;done.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x33ffffff),shape(tone,CONTROL_RADIUS),null));done.setTextColor(Color.WHITE);}
        LinearLayout.LayoutParams left=weight(); left.rightMargin=dp(10); actions.addView(cancel,left);
        actions.addView(done,weight());
        panel.addView(actions);
        Dialog dialog=sheet(title,panel,null,null);
        cancel.setOnClickListener(v->dialog.dismiss());
        done.setOnClickListener(v->{ dialog.dismiss(); confirm.run(); });
    }
    private void copyText(String label,String value) { getSystemService(ClipboardManager.class).setPrimaryClip(ClipData.newPlainText(label,value)); error("已复制"+label); }
    private void fileAction(LinearLayout options,Dialog dialog,String label,Runnable action) {
        boolean danger=label.equals("断开")||label.equals("取消发送")||label.startsWith("拒绝")||label.startsWith("删除")||label.startsWith("清空")||label.startsWith("结束");
        if(danger&&options.getChildCount()>0) { View line=new View(this);line.setBackgroundColor(blend(INK,SURFACE,.09f));LinearLayout.LayoutParams separator=new LinearLayout.LayoutParams(-1,dp(1));separator.setMargins(dp(10),dp(6),dp(10),dp(6));options.addView(line,separator); }
        int color=danger?0xffd24e54:INK;
        Button item=button(label,false); item.setTextColor(color); item.setGravity(Gravity.START|Gravity.CENTER_VERTICAL); item.setMinHeight(dp(52)); item.setPadding(dp(12),dp(10),dp(12),dp(10));
        item.setCompoundDrawablesWithIntrinsicBounds(new MenuIcon(label,danger?color:MUTED),null,null,null); item.setCompoundDrawablePadding(dp(14));
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
            if(label.contains("传输任务")) { c.drawLine(4,7,20,7,paint);p.moveTo(16,3);p.lineTo(20,7);p.lineTo(16,11);c.drawLine(20,17,4,17,paint);p.moveTo(8,13);p.lineTo(4,17);p.lineTo(8,21);c.drawPath(p,paint); }
            else if(label.contains("临时浏览器")) { c.drawCircle(12,12,9,paint);c.drawLine(3,12,21,12,paint);c.drawOval(7.5f,3,16.5f,21,paint); }
            else if(label.startsWith("复制")) { c.drawRoundRect(8,8,20,21,2,2,paint);p.moveTo(5,16);p.lineTo(3,16);p.lineTo(3,3);p.lineTo(15,3);p.lineTo(15,5);c.drawPath(p,paint); }
            else if(label.contains("转发")||label.contains("分享")) { p.moveTo(3,4);p.lineTo(21,12);p.lineTo(3,20);p.lineTo(6,12);p.close();c.drawPath(p,paint);c.drawLine(6,12,21,12,paint); }
            else if(label.contains("目录")||label.contains("位置")||label.contains("共享")||label.contains("文件夹")) { p.moveTo(3,6);p.lineTo(9,6);p.lineTo(11,9);p.lineTo(21,9);p.lineTo(21,20);p.lineTo(3,20);p.close();c.drawPath(p,paint); }
            else if(label.contains("Windows")){c.drawRoundRect(3,4,21,17,2,2,paint);c.drawLine(12,17,12,21,paint);c.drawLine(8,21,16,21,paint);}
            else if(label.contains("刷新")||label.contains("重试")) { c.drawArc(4,4,20,20,40,285,false,paint);p.moveTo(15,3);p.lineTo(20,4);p.lineTo(19,9);c.drawPath(p,paint); }
            else if(label.contains("备注")){p.moveTo(4,17);p.lineTo(16,5);p.lineTo(20,9);p.lineTo(8,21);p.lineTo(3,22);p.close();c.drawPath(p,paint);}
            else if(label.contains("置顶")){p.moveTo(9,3.5f);p.lineTo(15,3.5f);p.lineTo(14.2f,9.6f);p.lineTo(17.5f,13.6f);p.lineTo(6.5f,13.6f);p.lineTo(9.8f,9.6f);p.close();c.drawPath(p,paint);c.drawLine(12,13.6f,12,20.5f,paint);}
            else if(label.contains("设置")) { c.drawCircle(12,12,7,paint);c.drawCircle(12,12,3,paint);for(int i=0;i<8;i++){double a=i*Math.PI/4;c.drawLine(12+(float)Math.cos(a)*8,12+(float)Math.sin(a)*8,12+(float)Math.cos(a)*10,12+(float)Math.sin(a)*10,paint);} }
            else if(label.contains("添加")) { c.drawLine(12,4,12,20,paint);c.drawLine(4,12,20,12,paint); }
            else if(label.contains("连接")||label.equals("断开")) { c.drawArc(4,4,20,20,-50,280,false,paint);c.drawLine(12,2,12,12,paint); }
            else if(label.equals("取消发送")||label.startsWith("拒绝")||label.startsWith("删除")) { c.drawLine(5,5,19,19,paint);c.drawLine(5,19,19,5,paint); }
            else if(label.equals("关于")) { c.drawCircle(12,12,9,paint);c.drawLine(12,11,12,17,paint);c.drawPoint(12,7,paint); }
            else { c.drawRoundRect(5,3,19,21,2,2,paint);c.drawLine(9,11,15,11,paint);c.drawLine(9,16,15,16,paint); }
            c.restore();
        }
    }
    private void sentFileActions(Uri uri,String name,LanNode.Message message,LanNode.Peer peer) {
        int generation=editorGeneration;
        LinearLayout options=column();Dialog dialog=sheet(name,options,null,null);
        if(message!=null&&peer!=null&&message.outgoing&&service!=null&&service.canCancelOutgoing(peer.id(),message.number))fileAction(options,dialog,"取消发送",()->{if(selected.equals(peer.id())&&generation==editorGeneration)requestCancelOutgoing(message,peer);});
        if(uri!=null){fileAction(options,dialog,"打开文件",()->openFile(uri,name,false));fileAction(options,dialog,"打开方式…",()->openFileWith(uri,name,false,true));fileAction(options,dialog,"分享文件",()->openFile(uri,name,true));}
        else options.addView(text("文件已删除或读取授权已失效，消息记录仍可删除。",14,MUTED));
        fileAction(options,dialog,"复制文件名",()->copyText("文件名",name));
    }
    // 先说明删什么、留什么再确认；没有可清理的文件直接提示，完成后报告实际结果，不让按钮“点了没反应”。
    private void clearReceivedFiles(Button button) {
        ChatService source=service;
        if(source==null) { error("连接服务尚未就绪，请稍后重试"); return; }
        if(!button.isEnabled())return;
        button.setEnabled(false);button.setText("正在统计…");
        Runnable restore=()->{button.setEnabled(true);button.setText("清理已接收文件");};
        source.reviewReceivedFiles(review->{
            restore.run();if(service!=source||isFinishing()||isDestroyed()||!button.isShown())return;
            if(review.files==0) { error("没有可清理的已接收文件"); return; }
            String detail="删除本机已接收的 "+review.files+" 个文件，共 "+size(review.bytes)+"。聊天与传输记录保留，删除后文件不再保留；新收到的文件不删除。聊天附件在对方在线且邀请有效时可重新接收。";
            if(review.guestFiles>0)detail+="\n注意：其中 "+review.guestFiles+" 个来自临时浏览器传输，清理后无法重新接收。";
            confirm("清理已接收文件",detail,"清理",()->{
                if(service!=source) { error("连接服务已变化，请重新清理"); return; }
                source.clearFiles(review,result->{if(!isFinishing()&&!isDestroyed()&&service==source)error("已清理 "+result.files+" 个已接收文件，释放 "+size(result.bytes));});
            });
        },message->{
            restore.run();if(service==source&&!isFinishing()&&!isDestroyed()&&button.isShown())error(message);
        });
    }
    private void fileActions(LanNode.Peer peer,long packet,Protocol.Attachment file) {
        if(service==null) return;
        File cached=service.receivedFile(peer,packet,file);Uri uri=cached.isFile()?ReceivedFileProvider.uri(this,cached,file.name):receivedStorage.existingSaved(cached);
        LinearLayout options=column(); Dialog dialog=sheet(file.name,options,null,null);
        if(uri!=null) {
            fileAction(options,dialog,"打开文件",()->openFile(uri,file.name,false));
            fileAction(options,dialog,"打开方式…",()->openFileWith(uri,file.name,false,true));
            fileAction(options,dialog,"分享文件",()->openFile(uri,file.name,true));
            fileAction(options,dialog,"查看保存位置",()->showFileLocation(cached));
            ChatService source=service;
            fileAction(options,dialog,"删除本机文件",()->confirm("删除这个文件？",file.name+"\n将删除本机保存的文件，聊天记录与对方文件保留。此操作无法撤销。","删除文件",()->{if(service==source)source.deleteReceivedFile(peer,packet,file);}));
        } else {
            fileAction(options,dialog,"接收文件",()->{ if(service!=null) service.receive(peer,packet,file); });
            fileAction(options,dialog,"拒绝这批文件",()->{ if(service!=null) service.reject(peer,packet); });
        }
        fileAction(options,dialog,"复制文件名",()->copyText("文件名",file.name));
    }
    private void openFile(Uri uri,String name,boolean share) { openFileWith(uri,name,share,false); }
    private void openFileWith(Uri uri,String name,boolean share,boolean chooser) {
        if(!share&&name.toLowerCase(Locale.ROOT).endsWith(".apk")&&!getPackageManager().canRequestPackageInstalls()){
            confirm("允许安装此来源的应用","系统尚未允许飞Q请求安装 APK。请在下一页允许此来源，返回后再次点击打开文件；安装仍需系统确认。只安装可信来源的文件。","前往系统设置",()->{try{startActivity(new Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,Uri.parse("package:"+getPackageName())));}catch(ActivityNotFoundException e){error("系统不提供安装授权页面，可使用分享文件交给支持安装的应用");}});return;
        }
        Intent intent=new Intent(share?Intent.ACTION_SEND:Intent.ACTION_VIEW).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        if(share) { intent.setType(ReceivedStorage.mime(name)); intent.putExtra(Intent.EXTRA_STREAM,uri); }
        else intent.setDataAndType(uri,ReceivedStorage.mime(name));
        intent.setClipData(ClipData.newRawUri(name,uri));
        try { startActivity(share||chooser?Intent.createChooser(intent,share?"分享文件":"选择打开方式"):intent); }
        catch(ActivityNotFoundException e) { sheet("没有可用的打开方式",text("系统没有支持此文件格式的应用（"+ReceivedStorage.mime(name)+"）。请安装支持该格式的应用，或使用分享文件交给其他应用。",15,INK),"确定",Dialog::dismiss); }
        catch(SecurityException e) { error("文件授权已失效，请在设置中重新选择保存文件夹后重试"); }
    }
    private void showFileLocation(File file) {
        Uri saved=file.isFile()?null:receivedStorage.existingSaved(file),tree=saved==null?null:receivedStorage.savedTree(file);
        LinearLayout content=column();String location=file.isFile()?"保存在应用内接收区；可在设置中选择保存文件夹供后续接收使用。":saved==null?"本机文件已删除":DocumentsContract.getTreeDocumentId(tree);
        TextView path=text(location,15,INK); selectable(path); content.addView(path);
        Dialog dialog=sheet("保存位置",content,null,null);
        if(tree!=null) fileAction(content,dialog,"打开保存文件夹",()->{
            Uri folder=DocumentsContract.buildDocumentUriUsingTree(tree,DocumentsContract.getTreeDocumentId(tree));
            Intent intent=new Intent(Intent.ACTION_VIEW).setDataAndType(folder,DocumentsContract.Document.MIME_TYPE_DIR).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            try { startActivity(intent); }
            catch(ActivityNotFoundException e) {
                try { startActivity(new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(DocumentsContract.EXTRA_INITIAL_URI,folder)); }
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
        LinearLayout box=column();Dialog dialog=sheet("发送附件",box,null,null);fileAction(box,dialog,"选择文件（可多选）",this::pickDocuments);fileAction(box,dialog,"选择文件夹（保留目录）",()->{
            if(service==null||!service.online()){error("请先连接局域网");return;}pickerPeer=selected;try{startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION|Intent.FLAG_GRANT_PREFIX_URI_PERMISSION),26);}catch(ActivityNotFoundException e){error("系统没有目录选择器");}
        });
    }
    private void pickDocuments() {
        if(service==null||!service.online()) { error("请先连接局域网"); return; } if(service.busy()) { error("已有文件任务，请等待或取消"); return; }
        pickerPeer=selected; Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_ALLOW_MULTIPLE,true).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        try { startActivityForResult(intent,20); } catch(ActivityNotFoundException e) { error("系统没有可用的文件选择器"); }
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data);
        if(request==25&&(result!=RESULT_OK||data==null||data.getData()==null)){avatarImageRequest=null;return;}
        if(result!=RESULT_OK||data==null) return;
        if(request!=20&&request!=27&&data.getData()==null) return;
        if(request==27) {
            try {List<Uri> chosen=selectedFiles(data);for(Uri uri:chosen)getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);guestFiles.clear();guestFiles.addAll(chosen);showGuest();}
            catch(RuntimeException e){error("文件授权失败："+e.getMessage());}return;
        }
        if(request==25) {
            AvatarSelection expected=avatarImageRequest;int revision=avatarImageRevision;avatarImageRequest=null;if(expected==null||avatarSelection!=expected||expected.revision!=revision||!expected.dialog.isShowing())return;
            Uri uri=data.getData();String identity="device:"+getSharedPreferences("settings",MODE_PRIVATE).getString("device_id","");
            expected.loading=true;updateAvatarPicker(expected,identity);
            avatarLoader.execute(()->{
                // 窗口实例和选择版本都要匹配；较慢的本地图不能覆盖后来选择的默认/内置头像。
                try{String value=UserAvatar.read(this,uri);runOnUiThread(()->{if(currentAvatarRequest(expected,revision)){expected.loading=false;expected.value=value;updateAvatarPicker(expected,identity);}});}
                catch(Exception e){runOnUiThread(()->{if(currentAvatarRequest(expected,revision)){expected.loading=false;updateAvatarPicker(expected,identity);error("头像未更改："+e.getMessage());}});}
            });return;
        }
        if(request==24) {
            ChatService expected=service;
            if(expected==null){error("连接服务未就绪，请重新选择");return;}
            try{new SharedFolderSettings(this).acquire(data.getData(),data.getFlags());}catch(java.io.IOException e){error(e.getMessage());return;}
            expected.shareFolder(data.getData(),success->{if(success&&!isFinishing()&&!isDestroyed()&&service==expected)sharing();});return;
        }
        if(request==26){ChatStore.Conversation peer=conversation(pickerPeer);if(service==null||peer==null){error("联系人已失效");return;}try{getContentResolver().takePersistableUriPermission(data.getData(),Intent.FLAG_GRANT_READ_URI_PERMISSION);service.sendFolder(peer.peer,data.getData());}catch(RuntimeException e){error("目录授权失败："+e.getMessage());}return;}
        if(request==23) {
            try {
                Uri uri=data.getData();try(android.os.ParcelFileDescriptor fd=getContentResolver().openFileDescriptor(uri,"r")){if(fd!=null&&fd.getStatSize()>20*1024*1024)throw new java.io.IOException("请选择不超过 20 MiB 的图片");}
                Bitmap validated=ChatWallpaper.load(this,uri);validated.recycle();
                getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);
                getSharedPreferences("settings",MODE_PRIVATE).edit().putString("background_image",uri.toString()).putString("chat_background","image").apply();
                if(wallpaperChoices!=null&&wallpaperChoices.getChildCount()>0)wallpaperChoices.check(wallpaperChoices.getChildAt(wallpaperChoices.getChildCount()-1).getId());
                error("背景图片已保存；请保存外观设置以应用");
            }catch(Exception e){error("背景图片未保存："+e.getMessage());}return;
        }
        if(request==22) {
            try { receivedStorage.choose(data.getData(),data.getFlags()); if(folderLabel!=null) folderLabel.setText(receivedStorage.folderLabel()); if(service!=null) service.receiveSettingsChanged(); error("保存目录已更新，之后接收的文件会自动保存到此目录"); }
            catch(Exception e) { error("目录授权未保存："+e.getMessage()); } return;
        }
        if(request!=20) return;
        if(service==null) { error("连接服务尚未就绪，请重新选择文件"); return; }
        if(request==20) {
            ChatStore.Conversation peer=conversation(pickerPeer);
            try { List<Uri> files=selectedFiles(data);if((data.getFlags()&Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)!=0)for(Uri uri:files)try{getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);}catch(SecurityException ignored){/* 无持久权限的来源仍使用受限副本。 */}if(peer!=null) service.sendFiles(peer.peer,files); else error("联系人已失效，请重新选择"); }
            catch(IllegalArgumentException e) { error(e.getMessage()); }
        }
    }
    private boolean currentAvatarRequest(AvatarSelection state,int revision) {
        return !isDestroyed()&&!isFinishing()&&avatarSelection==state&&state.revision==revision&&state.dialog.isShowing();
    }
    static List<Uri> selectedFiles(Intent data) {
        LinkedHashSet<Uri> files=new LinkedHashSet<>(); ClipData clip=data.getClipData();
        if(clip!=null) {
            if(clip.getItemCount()>1000) throw new IllegalArgumentException("每批最多选择 1000 个文件");
            for(int i=0;i<clip.getItemCount();i++) files.add(clip.getItemAt(i).getUri());
        } else if(data.getData()!=null) files.add(data.getData());
        if(files.isEmpty()||files.contains(null)) throw new IllegalArgumentException("未取得有效文件，请重新选择");
        for(Uri uri:files) if(!"content".equals(uri.getScheme())) throw new IllegalArgumentException("请选择系统文件选择器授权的文件");
        return new ArrayList<>(files);
    }
    // Android 13+ 已在 onCreate 注册原生 OnBackInvokedCallback，此覆盖仅兼容旧系统。
    @android.annotation.SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed() { if(tasksDialog!=null)tasksDialog.onBackPressed();else if(selectingMessages)endMessageSelection();else if(!selected.isEmpty()) open(""); else super.onBackPressed(); }
    @Override public void error(String text) { if(!isFinishing()) Toast.makeText(this,text,Toast.LENGTH_LONG).show(); }
    private boolean confirmSecurity(String name,String code) {
        if(isFinishing()||isDestroyed()||!visible)return false;
        CountDownLatch done=new CountDownLatch(1);AtomicBoolean accepted=new AtomicBoolean();
        java.util.concurrent.atomic.AtomicReference<AlertDialog> prompt=new java.util.concurrent.atomic.AtomicReference<>();AtomicBoolean expired=new AtomicBoolean();
        mainHandler().post(()->{if(isFinishing()||isDestroyed()||!visible||expired.get()){done.countDown();return;}AlertDialog dialog=new AlertDialog.Builder(this).setTitle("核对设备安全身份").setMessage("正在与「"+name+"」建立加密连接。\n\n请核对两台设备显示的配对码：\n"+code+"\n\n只有配对码一致才继续。").setNegativeButton("取消",(d,w)->done.countDown()).setPositiveButton("信任并继续",(d,w)->{if(!expired.get())accepted.set(true);done.countDown();}).setOnDismissListener(d->done.countDown()).create();prompt.set(dialog);dialog.show();});
        try {if(!done.await(30,java.util.concurrent.TimeUnit.SECONDS))expired.set(true);}catch(InterruptedException e){expired.set(true);Thread.currentThread().interrupt();}
        finally{mainHandler().post(()->{AlertDialog dialog=prompt.get();if(dialog!=null)dialog.dismiss();});}
        return !expired.get()&&accepted.get()&&!isFinishing()&&!isDestroyed();
    }
    private Handler mainHandler(){return new Handler(Looper.getMainLooper());}
    private static String size(long value) { if(value>=1048576) return String.format(Locale.CHINA,"%.1f MB",value/1048576.0); if(value>=1024) return String.format(Locale.CHINA,"%.1f KB",value/1024.0); return value+" B"; }
    private void bindAvatar(TextView avatar,String id,String name,boolean online,String value) {
        String selectedAvatar=Protocol.normalizeAvatar(value);
        EmojiAvatars avatars=EmojiAvatars.get(this);int index=selectedAvatar!=null && selectedAvatar.startsWith("builtin:")?Integer.parseInt(selectedAvatar.substring(8)):avatars.index(id);
        avatar.setText("");
        avatar.setContentDescription(name+"，"+(selectedAvatar!=null && selectedAvatar.startsWith("image:")?"自选图片":avatars.description(index))+"，"+(online?"在线":"离线"));
        if(avatar.getBackground() instanceof AvatarDrawable && ((AvatarDrawable)avatar.getBackground()).avatarIndex()==index && java.util.Objects.equals(((AvatarDrawable)avatar.getBackground()).avatarValue(),selectedAvatar)) ((AvatarDrawable)avatar.getBackground()).setOnline(online);
        else avatar.setBackground(new AvatarDrawable(this,id,online,dp(6),SURFACE,ONLINE,selectedAvatar));
    }
    private final class ConversationAdapter extends BaseAdapter {
        private final List<ChatStore.Conversation> items=new ArrayList<>();
        void refresh() { items.clear(); String query=search==null?"":search.getText().toString().toLowerCase(Locale.ROOT); if(service!=null) for(ChatStore.Conversation c:service.conversations()) if((c.displayName()+" "+c.peer.name+" "+c.peer.id()+" "+c.preview).toLowerCase(Locale.ROOT).contains(query)) items.add(c); notifyDataSetChanged(); }
        @Override public int getCount() { return items.size(); }
        @Override public Object getItem(int p) { return items.get(p); }
        @Override public long getItemId(int p) { return p; }
        @Override public View getView(int position,View convert,ViewGroup parent) {
            ChatStore.Conversation c=items.get(position); LinearLayout row=row(); row.setPadding(dp(10),dp(5),dp(16),dp(5)); row.setMinimumHeight(dp(82));
            // 禁止按文字基线预先测量加权中栏，先给右侧完整时间留出宽度。
            row.setBaselineAligned(false);
            TextView avatar=text("",21,Color.WHITE); avatar.setGravity(Gravity.CENTER);
            bindAvatar(avatar,c.peer.id(),c.displayName(),c.peer.online,c.peer.avatar); row.addView(avatar,new LinearLayout.LayoutParams(dp(68),dp(68)));
            LinearLayout center=column(); LinearLayout.LayoutParams cp=weight(); cp.leftMargin=dp(8); row.addView(center,cp);
            TextView name=text(c.displayName(),16,INK); name.setTypeface(null,Typeface.BOLD); name.setSingleLine();
            // 中间省略保留末尾编号，短名称时编号紧跟昵称，不挤到消息时间旁。
            name.setEllipsize(c.duplicateName?TextUtils.TruncateAt.MIDDLE:TextUtils.TruncateAt.END);name.setContentDescription(c.displayName());
            if(c.duplicateName) {SpannableString caption=new SpannableString(name.getText());int start=caption.length()-c.nameSuffix().length();caption.setSpan(new android.text.style.ForegroundColorSpan(MUTED),start,caption.length(),Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);caption.setSpan(new android.text.style.RelativeSizeSpan(.8f),start,caption.length(),Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);name.setText(caption);}
            if(c.pinned) {MenuIcon pin=new MenuIcon("置顶会话",MUTED);pin.setBounds(0,0,dp(17),dp(17));name.setCompoundDrawables(pin,null,null,null);name.setCompoundDrawablePadding(dp(4));name.setContentDescription("已置顶，"+c.displayName());}
            center.addView(name);
            TextView preview=text(c.preview.isEmpty()?(c.peer.online?"在线 · ":"离线 · ")+c.peer.endpoint.getAddress().getHostAddress():MessageText.normalizeLineBreaks(c.preview).replace('\n',' '),14,MUTED); preview.setSingleLine(); preview.setEllipsize(TextUtils.TruncateAt.END); preview.setPadding(0,dp(5),0,0); center.addView(preview);
            LinearLayout right=column(); right.setGravity(Gravity.END); LinearLayout.LayoutParams rp=new LinearLayout.LayoutParams(-2,-2); rp.leftMargin=dp(8); row.addView(right,rp);
            TextView time=text(c.time==0?"":new SimpleDateFormat("HH:mm",Locale.CHINA).format(new Date(c.time)),11,MUTED); time.setSingleLine(); right.addView(time);
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
            else if(kind.equals("search")) { canvas.drawCircle(10,10,6,p);canvas.drawLine(14.5f,14.5f,21,21,p); }
            else if(kind.equals("close")) { canvas.drawLine(5,5,19,19,p); canvas.drawLine(19,5,5,19,p); }
            else if(kind.equals("add")) { canvas.drawLine(12,3,12,21,p); canvas.drawLine(3,12,21,12,p); }
            else if(kind.equals("back")) { path.moveTo(12,4); path.lineTo(4,12); path.lineTo(12,20); canvas.drawPath(path,p); canvas.drawLine(4,12,21,12,p); }
            else if(kind.equals("transfer")) {
                canvas.drawLine(4,7,20,7,p);path.moveTo(16,3);path.lineTo(20,7);path.lineTo(16,11);
                canvas.drawLine(20,17,4,17,p);path.moveTo(8,13);path.lineTo(4,17);path.lineTo(8,21);canvas.drawPath(path,p);
            }
            else if(kind.equals("send")) { path.moveTo(3,3); path.lineTo(22,12); path.lineTo(3,21); path.lineTo(6,12); path.close(); canvas.drawPath(path,p); canvas.drawLine(6,12,21,12,p); }
            else if(kind.equals("smile")) {canvas.drawCircle(12,12,9,p);canvas.drawPoint(9,9,p);canvas.drawPoint(15,9,p);canvas.drawArc(7,9,17,17,15,150,false,p);}
            else if(kind.equals("code")) {path.moveTo(7,6);path.lineTo(2,12);path.lineTo(7,18);path.moveTo(17,6);path.lineTo(22,12);path.lineTo(17,18);canvas.drawPath(path,p);canvas.drawLine(14,4,10,20,p);}
            else { path.moveTo(17,8); path.lineTo(8,17); path.cubicTo(4,21,0,16,4,12); path.lineTo(13,3); path.cubicTo(19,-3,27,5,21,11); path.lineTo(11,21); canvas.drawPath(path,p); }
            canvas.restore();
        }
        @Override public boolean performClick() { super.performClick(); return true; }
    }
}
