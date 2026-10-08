package org.feiqlight.android;

import android.content.*;
import android.graphics.*;
import android.os.Looper;
import android.view.*;
import android.widget.*;
import org.feiqlight.android.core.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.*;
import org.robolectric.annotation.*;
import java.io.*;
import java.lang.reflect.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35, qualifiers="w393dp-h852dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class UiTest {
    @Test public void sendClickShowsImmediatePendingWithoutClearingDraftOrDuplicatingMessage() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();CountDownLatch release=new CountDownLatch(1),blocked=new CountDownLatch(1);ActivityController<MainActivity> ac=null;
        try(java.net.DatagramSocket target=new java.net.DatagramSocket(0,java.net.InetAddress.getLoopbackAddress())) {
            settle(service);int port;try(java.net.ServerSocket socket=new java.net.ServerSocket(0,1,java.net.InetAddress.getLoopbackAddress())){port=socket.getLocalPort();}
            service.connectionFactory=(prefs,listener)->new LanNode(port,true,"pending-test","test","发送方","test",listener);service.onStartCommand(new Intent(),0,1);settle(service);
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+target.getLocalPort()),"pending-peer","test","发送反馈测试","");peer.utf8=true;
            LanNode.Message initial=new LanNode.Message();initial.number=1;initial.time=System.currentTimeMillis();initial.state="已收到";initial.text="发送消息时立即反馈，不会误认为没有点到。";service.incoming(peer,initial,false);settle(service);
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);invoke(activity,"open",String.class,peer.id());settle(service);
            ExecutorService io=(ExecutorService)field(service,"io");io.execute(()->{blocked.countDown();try{release.await(10,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}});assertTrue(blocked.await(3,TimeUnit.SECONDS));
            EditText input=(EditText)field(activity,"editor");input.setText("发送中的测试消息");View send=(View)field(activity,"sendButton");send.performClick();send.performClick();
            assertEquals(View.VISIBLE,((TextView)field(activity,"sendStatus")).getVisibility());assertEquals("发送中的测试消息",input.getText().toString());assertFalse(send.isEnabled());screenshot(activity,"android-send-pending");
            release.countDown();settle(service);assertEquals(View.GONE,((TextView)field(activity,"sendStatus")).getVisibility());assertTrue(send.isEnabled());assertEquals("",input.getText().toString());
            ChatStore store=(ChatStore)field(service,"store");assertEquals(1L,io.submit(()->store.history(peer.id()).stream().filter(m->m.outgoing).count()).get().longValue());
        } finally {release.countDown();if(ac!=null)ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void fileProgressRendersPerCardAndUpdatesWithoutRebuildingOrStealingScroll() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"progress","test","文件传输测试","");
            LanNode.Message sent=new LanNode.Message();sent.number=88;sent.outgoing=true;sent.time=System.currentTimeMillis();sent.state="已送达";sent.text="";
            sent.files.add(new Protocol.Attachment(0,"大文件.zip",1073741824,0));sent.files.add(new Protocol.Attachment(1,"报告.pdf",2048,0));
            LanNode.Message received=new LanNode.Message();received.number=88;received.time=sent.time;received.state="已收到";received.text="";received.files.add(new Protocol.Attachment(0,"接收文件.mp4",1073741824,0));
            service.incoming(peer,sent,false);service.incoming(peer,received,false);settle(service);invoke(activity,"open",String.class,peer.id());settle(service);service.remove(activity);
            Method progress=ChatService.class.getDeclaredMethod("fileProgress",String.class,long.class,long.class,boolean.class,int.class,String.class,LanNode.Transfer.class);progress.setAccessible(true);
            progress.invoke(service,peer.id(),88L,0L,true,47,"发送中",null);progress.invoke(service,peer.id(),88L,1L,true,0,"等待对方接收",null);
            LanNode.Transfer task=new LanNode.Transfer();Field active=ChatService.class.getDeclaredField("transfer");active.setAccessible(true);active.set(service,task);
            progress.invoke(service,peer.id(),88L,0L,false,26,"接收中 · 点击取消",task);
            List<LanNode.Message> rows=new ArrayList<>(Arrays.asList(sent,received));invoke(activity,"renderMessages",List.class,rows);
            View root=(View)field(activity,"root");measure(root,786,1704,true);activity.transfersChanged();
            LinearLayout messages=(LinearLayout)field(activity,"messages");List<ProgressBar> bars=new ArrayList<>();for(View v:descendants(messages))if(v instanceof ProgressBar)bars.add((ProgressBar)v);
            assertEquals(3,bars.size());assertEquals(47,bars.get(0).getProgress());assertEquals(0,bars.get(1).getProgress());assertEquals(26,bars.get(2).getProgress());
            screenshot(activity,"android-file-progress");
            List<View> bubbles=new ArrayList<>();for(View v:descendants(messages))if(v.getBackground() instanceof BubbleDrawable)bubbles.add(v);
            float density=activity.getResources().getDisplayMetrics().density;
            assertTrue("发送气泡留白过大",bubbles.get(0).getHeight()<260*density);assertTrue("接收气泡留白过大",bubbles.get(1).getHeight()<150*density);
            for(int i=0;i<16;i++){LanNode.Message m=new LanNode.Message();m.number=100+i;m.time=sent.time;m.text="用于检查阅读位置的旧消息";m.state="已收到";rows.add(m);}
            invoke(activity,"renderMessages",List.class,rows);measure(root,786,1704,true);ScrollView scroll=(ScrollView)field(activity,"scroll");scroll.scrollTo(0,150);int y=scroll.getScrollY();View first=messages.getChildAt(1);
            progress.invoke(service,peer.id(),88L,0L,true,83,"发送中",null);activity.transfersChanged();assertSame(first,messages.getChildAt(1));assertEquals(y,scroll.getScrollY());assertEquals(26,service.progress(peer.id(),88,0,false).percent);
            service.cancelFile("other-peer",88,0);assertFalse(task.isCancelled());service.cancelFile(peer.id(),88,0);assertTrue(task.isCancelled());active.set(service,null);
            TextView common=(TextView)field(activity,"transferStatus");assertEquals(View.GONE,common.getVisibility());
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void sentImagePreviewPersistsAndMissingCacheKeepsFileActions() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"preview","test","图片接收方","");
            File folder=new File(service.getFilesDir(),"offers/"+UUID.randomUUID());assertTrue(folder.mkdirs());File image=new File(folder,"发送图片.png"),document=new File(folder,"报告.txt");
            Bitmap original=Bitmap.createBitmap(120,80,Bitmap.Config.ARGB_8888);original.eraseColor(Color.rgb(240,150,35));try(OutputStream out=new FileOutputStream(image)){assertTrue(original.compress(Bitmap.CompressFormat.PNG,100,out));}finally{original.recycle();}
            java.nio.file.Files.write(document.toPath(),"普通文件保持文件卡片".getBytes(java.nio.charset.StandardCharsets.UTF_8));
            LanNode.Message message=new LanNode.Message();message.number=66;message.outgoing=true;message.time=System.currentTimeMillis();message.state="已送达";message.text="";
            message.files.add(new Protocol.Attachment(0,image.getName(),image.length(),0));message.files.add(new Protocol.Attachment(1,document.getName(),document.length(),0));message.localPaths.add(image.getPath());message.localPaths.add(document.getPath());
            ChatStore store=(ChatStore)field(service,"store");ExecutorService io=(ExecutorService)field(service,"io");io.submit(()->store.message(peer,message,false,true)).get();
            List<LanNode.Message> persisted=io.submit(()->store.history(peer.id())).get();assertEquals(message.localPaths,persisted.get(0).localPaths);
            invoke(activity,"open",String.class,peer.id());settle(service);service.remove(activity);
            ReceivedFileProvider provider=new ReceivedFileProvider();android.content.pm.ProviderInfo info=new android.content.pm.ProviderInfo();info.authority=activity.getPackageName()+".received";info.grantUriPermissions=true;provider.attachInfo(activity,info);org.robolectric.shadows.ShadowContentResolver.registerProviderInternal(info.authority,provider);
            android.net.Uri uri=ReceivedFileProvider.uri(activity,image,image.getName());Bitmap decoded=MediaPreview.image(activity,uri,480);assertEquals(120,decoded.getWidth());decoded.recycle();
            invoke(activity,"renderMessages",List.class,persisted);View root=(View)field(activity,"root");measure(root,786,1704,true);
            LinearLayout messages=(LinearLayout)field(activity,"messages");assertEquals(1,descendants(messages).stream().filter(v->v.getContentDescription()!=null&&v.getContentDescription().toString().endsWith("，媒体预览")).count());
            ImageView picture=(ImageView)descendants(messages).stream().filter(v->v instanceof ImageView).findFirst().get();for(int i=0;i<60&&picture.getDrawable()==null;i++){Thread.sleep(20);shadowOf(Looper.getMainLooper()).idle();}assertNotNull("发出的图片没有解码预览",picture.getDrawable());
            assertEquals(1,descendants(messages).stream().filter(v->v.getBackground() instanceof BubbleDrawable).count());screenshot(activity,"android-sent-image-preview");
            View card=descendants(messages).stream().filter(v->"报告.txt，文件操作".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().get();card.performClick();android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertTrue(dialog.isShowing());assertTrue(descendants(dialog.getWindow().getDecorView()).stream().anyMatch(v->v instanceof Button&&((Button)v).getText().toString().equals("打开文件")));dialog.dismiss();
            java.nio.file.Files.delete(image.toPath());invoke(activity,"renderMessages",List.class,persisted);assertEquals(0,descendants(messages).stream().filter(v->v.getContentDescription()!=null&&v.getContentDescription().toString().endsWith("，媒体预览")).count());assertTrue(descendants(messages).stream().anyMatch(v->v.getContentDescription()!=null&&v.getContentDescription().toString().contains("发送副本已清理")));
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void emojiPickerInsertsAtCursorAndProtectsLengthAndChangedConversation() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            settle(service);invoke(activity,"open",String.class,"emoji-test");EditText input=(EditText)field(activity,"editor");input.setText("AB");input.setSelection(1);
            View button=descendants((View)field(activity,"root")).stream().filter(v->"表情".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().get();button.performClick();shadowOf(Looper.getMainLooper()).idle();
            android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertTrue(dialog.isShowing());dialogScreenshot(dialog,"android-emoji-picker");
            Button smile=(Button)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("😀")).findFirst().get();smile.performClick();assertEquals("A😀B",input.getText().toString());assertEquals(3,input.getSelectionStart());
            input.setText("a".repeat(17999));assertFalse(MainActivity.insertEmoji(input,"👨‍👩‍👧‍👦",17999,17999));assertEquals(17999,input.length());
            input.setText("旧草稿");button.performClick();shadowOf(Looper.getMainLooper()).idle();dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();Button old=(Button)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("😀")).findFirst().get();invoke(activity,"open",String.class,"other-emoji");old.performClick();assertEquals("",((EditText)field(activity,"editor")).getText().toString());
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void onlineAvatarGlowRendersAndClearsWithoutChangingTheFace() throws Exception {
        for(int faceSize:new int[]{42,56,84,112}) {
            int halo=(int)Math.ceil(faceSize*.1f),size=faceSize+halo*2;
            Bitmap offline=Bitmap.createBitmap(size,size,Bitmap.Config.ARGB_8888),online=Bitmap.createBitmap(size,size,Bitmap.Config.ARGB_8888),restored=Bitmap.createBitmap(size,size,Bitmap.Config.ARGB_8888);
            try {
                AvatarDrawable avatar=new AvatarDrawable(0xff65bca5,false,halo);avatar.setBounds(0,0,size,size);avatar.draw(new Canvas(offline));
                avatar.setOnline(true);avatar.draw(new Canvas(online));assertTrue(avatar.isOnline());
                assertEquals(offline.getPixel(size/2,size/2),online.getPixel(size/2,size/2));
                assertEquals(0,Color.alpha(offline.getPixel(size/2,halo/2)));assertTrue("头像外没有光晕",Color.alpha(online.getPixel(size/2,halo/2))>0);
                assertTrue("光晕没有向外渐隐",Color.alpha(online.getPixel(size/2,halo-1))>Color.alpha(online.getPixel(size/2,1)));
                for(int y=0;y<size;y++)for(int x=0;x<size;x++)if(Color.alpha(offline.getPixel(x,y))==255)assertEquals("头像内部被染色",offline.getPixel(x,y),online.getPixel(x,y));
                assertEquals(0,Color.alpha(online.getPixel(0,0)));
                avatar.setOnline(false);avatar.draw(new Canvas(restored));assertTrue("离线后光边残留",offline.sameAs(restored));
            } finally {offline.recycle();online.recycle();restored.recycle();}
        }
    }
    @Test public void presenceRefreshesInConversationListAndOpenChatInBothThemes() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            settle(service);List<ChatStore.Conversation> rows=new ArrayList<>();
            for(int i=0;i<2;i++) { ChatStore.Conversation c=new ChatStore.Conversation();c.peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+(32425+i)),"presence"+i,"test",i==0?"在线联系人":"离线联系人","");c.peer.online=i==0;c.preview="头像状态验证";rows.add(c); }
            Field conversations=ChatService.class.getDeclaredField("conversations");conversations.setAccessible(true);
            for(boolean dark:new boolean[]{false,true}) {
                activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putString("accent","purple").apply();
                invoke(activity,"open",String.class,"");settle(service);conversations.set(service,rows);activity.changed();screenshot(activity,"android-presence-list-"+(dark?"dark":"light"));
                ListView list=(ListView)field(activity,"list");
                for(int i=0;i<2;i++) { View row=list.getAdapter().getView(i,null,list);TextView portrait=(TextView)descendants(row).stream().filter(v->v.getBackground() instanceof AvatarDrawable).findFirst().get();assertEquals(i==0,((AvatarDrawable)portrait.getBackground()).isOnline()); }
                invoke(activity,"open",String.class,rows.get(0).peer.id());settle(service);conversations.set(service,rows);activity.changed();
                TextView portrait=(TextView)field(activity,"chatAvatar");assertTrue(((AvatarDrawable)portrait.getBackground()).isOnline());screenshot(activity,"android-presence-chat-"+(dark?"dark":"light"));
                rows.get(0).peer.online=false;activity.changed();assertFalse(((AvatarDrawable)portrait.getBackground()).isOnline());assertTrue(portrait.getContentDescription().toString().endsWith("离线"));
                rows.get(0).peer.online=true;activity.changed();assertTrue(((AvatarDrawable)portrait.getBackground()).isOnline());
            }
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void bubbleTailRendersMirroredWithRoundedCorners() throws Exception {
        Bitmap out=Bitmap.createBitmap(220,100,Bitmap.Config.ARGB_8888),in=Bitmap.createBitmap(220,100,Bitmap.Config.ARGB_8888);
        try {
            BubbleDrawable right=new BubbleDrawable(Color.BLUE,1,true),left=new BubbleDrawable(Color.BLUE,1,false);
            right.setBounds(0,0,220,100);left.setBounds(0,0,220,100);right.draw(new Canvas(out));left.draw(new Canvas(in));
            assertEquals(0,Color.alpha(out.getPixel(0,0)));assertTrue(Color.alpha(out.getPixel(216,16))>0);
            File dir=new File(System.getProperty("feiq.previewDir"));dir.mkdirs();
            try(OutputStream stream=new FileOutputStream(new File(dir,"bubble-tail.png"))){out.compress(Bitmap.CompressFormat.PNG,100,stream);}
            int maxDifference=0;for(int y=0;y<100;y++)for(int x=0;x<220;x++) maxDifference=Math.max(maxDifference,Math.abs(Color.alpha(out.getPixel(x,y))-Color.alpha(in.getPixel(219-x,y))));
            // Skia 对相反绕向的曲线有子像素覆盖差异；只容许边缘抗锯齿误差，不容许错位或漏画。
            assertTrue("镜像边缘最大 alpha 差："+maxDifference,maxDifference<=32);
        } finally {out.recycle();in.recycle();}
    }
    // Robolectric 不绘制厂商原生浮动工具栏；用可观测 ActionMode 核验真实 View 事件分发与生命周期。
    private static final class SelectionMode extends ActionMode {
        final ActionMode.Callback callback; final Menu menu; final Context context; int finishes;
        SelectionMode(TextView owner,ActionMode.Callback callback) { this.callback=callback;context=owner.getContext();menu=new PopupMenu(context,owner).getMenu();assertTrue(callback.onCreateActionMode(this,menu)); }
        public void setTitle(CharSequence title) { } public void setTitle(int title) { }
        public void setSubtitle(CharSequence title) { } public void setSubtitle(int title) { }
        public void setCustomView(View view) { } public View getCustomView() { return null; }
        public void invalidate() { callback.onPrepareActionMode(this,menu); }
        public void finish() { finishes++;callback.onDestroyActionMode(this); }
        public Menu getMenu() { return menu; } public MenuInflater getMenuInflater() { return new MenuInflater(context); }
        public CharSequence getTitle() { return ""; } public CharSequence getSubtitle() { return ""; }
    }
    private static void tap(java.util.function.Consumer<MotionEvent> dispatch,float x,float y) {
        long now=android.os.SystemClock.uptimeMillis();
        for(int action:new int[]{MotionEvent.ACTION_DOWN,MotionEvent.ACTION_UP}) { MotionEvent event=MotionEvent.obtain(now,now+10,action,x,y,0);try { dispatch.accept(event); } finally { event.recycle(); } }
        shadowOf(Looper.getMainLooper()).idle();
    }
    @Test public void nativeSelectionDismissesOutsideWithoutEatingClicksAndKeepsOwnerGestures() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(sc.get(),ChatService.class),sc.get().onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).setup(); MainActivity activity=ac.get();
        try {
            View root=(View)field(activity,"root");measure(activity.getWindow().getDecorView(),786,1704,true);
            EditText input=(EditText)field(activity,"search");input.setText("原生选区与空白取消");input.requestFocus();input.setSelection(0,2);
            SelectionMode mode=new SelectionMode(input,input.getCustomSelectionActionModeCallback());
            int[] point=new int[2];input.getLocationOnScreen(point);
            tap(e->activity.dispatchTouchEvent(e),point[0]+input.getWidth()/2f,point[1]+input.getHeight()/2f);assertEquals(0,mode.finishes);
            Button target=(Button)field(activity,"connect");java.util.concurrent.atomic.AtomicBoolean clicked=new java.util.concurrent.atomic.AtomicBoolean();target.setOnClickListener(v->clicked.set(true));target.getLocationOnScreen(point);
            tap(e->activity.dispatchTouchEvent(e),point[0]+target.getWidth()/2f,point[1]+target.getHeight()/2f);
            assertEquals(1,mode.finishes);assertTrue("关闭选区不应吞掉按钮点击",clicked.get());assertEquals("原生选区与空白取消",input.getText().toString());
            TextView content=new TextView(activity);content.setText("聊天正文可以选择复制");invoke(activity,"selectable",TextView.class,content);
            ((LinearLayout)field(activity,"body")).addView(content,0);measure(root,786,1704,true);
            SelectionMode bodyMode=new SelectionMode(content,content.getCustomSelectionActionModeCallback());
            tap(e->activity.dispatchTouchEvent(e),780,850);assertEquals(1,bodyMode.finishes);
            SelectionMode old=new SelectionMode(input,input.getCustomInsertionActionModeCallback());SelectionMode next=new SelectionMode(content,content.getCustomSelectionActionModeCallback());
            old.callback.onDestroyActionMode(old);assertSame(next,field(activity,"selectionMode"));
            invoke(activity,"open",String.class,"");assertEquals(1,next.finishes);assertNull(field(activity,"selectionOwner"));
        } finally { ac.pause().stop().destroy();sc.destroy(); }
    }
    @Test public void dialogSelectionOutsideAndMenuThemesRemainUsable() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(sc.get(),ChatService.class),sc.get().onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).setup();MainActivity activity=ac.get();android.app.Dialog dialog=null;
        try {
            for(boolean dark:new boolean[]{false,true}) {
                activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putString("accent","purple").apply();
                invoke(activity,"open",String.class,"");measure((View)field(activity,"root"),786,1704,true);
                invoke(activity,"showMenu",View.class,(View)field(activity,"root"));dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();
                ScrollView scroller=findScroller(dialog.getWindow().getDecorView());measure(scroller,786,1200,false);
                for(View v:descendants(scroller)) if(v instanceof Button) { Button b=(Button)v;assertNotNull(b.getCompoundDrawables()[0]);assertTrue(b.getHeight()>=104); }
                snapshotView(scroller,"android-menu-"+(dark?"dark":"light"));dialog.dismiss();
            }
            Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();
            ScrollView scroller=findScroller(dialog.getWindow().getDecorView());measure(scroller,786,1200,false);
            EditText field=(EditText)descendants(scroller).stream().filter(v->v instanceof EditText).findFirst().get();field.requestFocus();field.setSelection(0,1);
            SelectionMode mode=new SelectionMode(field,field.getCustomSelectionActionModeCallback());
            android.app.Dialog current=dialog;View caption=descendants(dialog.getWindow().getDecorView()).stream().filter(v->"设置".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().get();
            int[] captionPoint=new int[2],origin=new int[2];caption.getLocationOnScreen(captionPoint);dialog.getWindow().getDecorView().getLocationOnScreen(origin);
            tap(e->{e.offsetLocation(-origin[0],-origin[1]);current.dispatchTouchEvent(e);},captionPoint[0]+4,captionPoint[1]+4);assertEquals(1,mode.finishes);
            assertTrue(dialog.isShowing());
        } finally { if(dialog!=null)dialog.dismiss();ac.pause().stop().destroy();sc.destroy(); }
    }
    private static int restoreStarts(android.app.Application application) {
        int count=0;Intent intent;
        // BIND_AUTO_CREATE 在 Robolectric 也记录一条无 action 的启动，不等于前台恢复请求。
        while((intent=shadowOf(application).getNextStartedService())!=null) if("restore".equals(intent.getAction())) count++;
        return count;
    }
    @Test public void connectionIntentMigrationDisconnectAndForegroundRestore() throws Exception {
        android.app.Application context=RuntimeEnvironment.getApplication();SharedPreferences prefs=context.getSharedPreferences("settings",0);prefs.edit().clear().commit();
        assertFalse(ChatService.shouldRestoreConnection(context));prefs.edit().putString("login","old-install").commit();assertTrue(ChatService.shouldRestoreConnection(context));
        prefs.edit().putBoolean("auto_connect",false).commit();assertFalse(ChatService.shouldRestoreConnection(context));prefs.edit().putBoolean("auto_connect",true).commit();
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();ActivityController<MainActivity> ac=null;
        try {
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            while(shadowOf(context).getNextStartedService()!=null) { }
            ac=Robolectric.buildActivity(MainActivity.class).setup();settle(service);
            assertEquals(1,restoreStarts(context));
            ac.pause().stop().start().resume();settle(service);assertEquals("同一页面恢复不应重复启动",0,restoreStarts(context));
            service.disconnect();settle(service);assertFalse(ChatService.shouldRestoreConnection(context));
            ac.pause().stop().destroy();ac=null;
            ac=Robolectric.buildActivity(MainActivity.class).setup();settle(service);assertEquals("主动断开后不可自动连接",0,restoreStarts(context));
        } finally { if(ac!=null)ac.pause().stop().destroy();sc.destroy(); }
        assertFalse(ChatService.shouldRestoreConnection(context));
    }
    private static Object field(Object object,String name) throws Exception { Field f=object.getClass().getDeclaredField(name); f.setAccessible(true); return f.get(object); }
    private static Object invoke(Object object,String name,Class<?> type,Object value) throws Exception { Method m=object.getClass().getDeclaredMethod(name,type); m.setAccessible(true); return m.invoke(object,value); }
    private static void settle(ChatService service) throws Exception {
        ExecutorService io=(ExecutorService)field(service,"io");
        for(int i=0;i<3;i++) { io.submit(()->{}).get(5,TimeUnit.SECONDS); shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100)); }
    }
    private static void screenshot(MainActivity activity,String name) throws Exception {
        View view=(View)field(activity,"root");
        view.measure(View.MeasureSpec.makeMeasureSpec(786,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1704,View.MeasureSpec.EXACTLY)); view.layout(0,0,786,1704);
        Bitmap bitmap=Bitmap.createBitmap(786,1704,Bitmap.Config.ARGB_8888); view.draw(new Canvas(bitmap));
        File dir=new File(System.getProperty("feiq.previewDir")); if(!dir.isDirectory()&&!dir.mkdirs()) throw new IOException("无法创建渲染输出目录");
        try(OutputStream stream=new FileOutputStream(new File(dir,name+".png"))) { assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,stream)); }
        bitmap.recycle();
    }
    private static ScrollView findScroller(View view) {
        if(view instanceof ScrollView) return (ScrollView)view;
        if(view instanceof ViewGroup) for(int i=0;i<((ViewGroup)view).getChildCount();i++) { ScrollView found=findScroller(((ViewGroup)view).getChildAt(i)); if(found!=null) return found; }
        return null;
    }
    private static java.util.List<View> descendants(View root) {
        java.util.List<View> all=new ArrayList<>();all.add(root);
        if(root instanceof ViewGroup) for(int i=0;i<((ViewGroup)root).getChildCount();i++) all.addAll(descendants(((ViewGroup)root).getChildAt(i)));
        return all;
    }
    private static void measure(View view,int width,int height,boolean exact) {
        view.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(height,exact?View.MeasureSpec.EXACTLY:View.MeasureSpec.AT_MOST));
        view.layout(0,0,view.getMeasuredWidth(),view.getMeasuredHeight());
    }
    private static void snapshotView(View view,String name)throws Exception {
        Bitmap image=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);Canvas canvas=new Canvas(image);canvas.translate(-view.getScrollX(),-view.getScrollY());view.draw(canvas);
        try(OutputStream output=new FileOutputStream(new File(System.getProperty("feiq.previewDir"),name+".png"))){assertTrue(image.compress(Bitmap.CompressFormat.PNG,100,output));}finally{image.recycle();}
    }
    @Test @Config(qualifiers="w320dp-h480dp-xhdpi") public void narrowChatLongTitleAndLargeFontStayWithinToolbar()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);
        service.remove(activity);Field serviceField=MainActivity.class.getDeclaredField("service");serviceField.setAccessible(true);serviceField.set(activity,null);
        try {
            invoke(activity,"open",String.class,"layout-peer");TextView title=(TextView)field(activity,"title"),subtitle=(TextView)field(activity,"subtitle");
            title.setText("这是非常长的联系人昵称👩🏽‍💻".repeat(12));subtitle.setText("在线 · "+"部门".repeat(50));
            // 在真实原生 View 上施加等效的大字号，避免把宿主字体配置当作真机系统设置。
            title.setTextSize(36);subtitle.setTextSize(22);
            EditText input=(EditText)field(activity,"editor");input.setText("👨‍👩‍👧‍👦 超长输入与颜文字 (๑•̀ㅂ•́)و✧");input.setTextSize(28);
            LanNode.Message message=new LanNode.Message();message.number=1;message.time=1;message.text="没有空格的很长路径".repeat(50)+"\n"+"A".repeat(300);message.state="已送达";message.outgoing=true;
            Method render=MainActivity.class.getDeclaredMethod("renderMessages",java.util.List.class);render.setAccessible(true);render.invoke(activity,Collections.singletonList(message));
            View root=(View)field(activity,"root");measure(root,640,960,true);View toolbar=(View)field(activity,"toolbar");
            assertEquals(1,title.getLineCount());assertEquals(1,subtitle.getLineCount());
            int titleTop=((View)title.getParent()).getTop()+title.getTop(),subtitleBottom=((View)subtitle.getParent()).getTop()+subtitle.getBottom();
            assertTrue(titleTop>=0);assertTrue(subtitleBottom<=toolbar.getHeight());
            for(View child:descendants(toolbar))if(child.getParent() instanceof ViewGroup){ViewGroup parent=(ViewGroup)child.getParent();assertTrue("标题栏横向越界",child.getLeft()>=0&&child.getRight()<=parent.getWidth());}
            View send=(View)field(activity,"sendButton");assertTrue(send.getRight()<=((View)send.getParent()).getWidth());assertTrue(send.getHeight()>0);
            snapshotView(root,"android-layout-narrow-large-font");
            input.setText("大字号多行输入\n".repeat(6));root.setPadding(0,0,0,400);measure(root,640,960,true);measure(root,640,960,true);
            int sendBottom=send.getBottom();View parent=(View)send.getParent();while(parent!=root){sendBottom+=parent.getTop()-parent.getScrollY();parent=(View)parent.getParent();}
            assertTrue("键盘占位后发送按钮落入屏外",sendBottom<=root.getHeight()-root.getPaddingBottom());
            View composer=(View)input.getParent();int bottom=composer.getBottom();parent=(View)composer.getParent();while(parent!=root){bottom+=parent.getTop()-parent.getScrollY();parent=(View)parent.getParent();}
            assertTrue("键盘占位后输入框容器被裁剪："+bottom,bottom<=root.getHeight()-root.getPaddingBottom());snapshotView(root,"android-layout-keyboard-reserved");
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    @Test @Config(qualifiers="w320dp-h480dp-xhdpi") public void sheetRespectsKeyboardSizedViewportAndLastActionRemainsReachable()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        android.app.Dialog dialog=null;
        try {
            View root=(View)field(activity,"root");measure(root,640,960,true);
            TextView content=new TextView(activity);content.setText("文件内容与保存路径很长\n".repeat(80));content.setTextSize(28);
            java.util.concurrent.atomic.AtomicBoolean clicked=new java.util.concurrent.atomic.AtomicBoolean();
            Method sheet=MainActivity.class.getDeclaredMethod("sheet",String.class,View.class,String.class,java.util.function.Consumer.class);sheet.setAccessible(true);
            String name="超长文件名👩🏽‍💻".repeat(24)+".webp";
            dialog=(android.app.Dialog)sheet.invoke(activity,name,content,"确认操作",(java.util.function.Consumer<android.app.Dialog>)d->{clicked.set(true);d.dismiss();});
            ScrollView scroll=findScroller(dialog.getWindow().getDecorView());assertNotNull(scroll);measure(scroll,640,440,false);assertTrue("弹窗忽略了父窗口高度",scroll.getMeasuredHeight()<=440);
            TextView caption=(TextView)descendants(scroll).stream().filter(v->v instanceof TextView&&((TextView)v).getText().toString().equals(name)).findFirst().get();assertTrue(caption.getLineCount()<=2);
            Button done=(Button)descendants(scroll).stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("确认操作")).findFirst().get();
            scroll.scrollTo(0,scroll.getChildAt(0).getHeight());int y=done.getTop();View parent=(View)done.getParent();while(parent!=scroll){y+=parent.getTop()-parent.getScrollY();parent=(View)parent.getParent();}y-=scroll.getScrollY();
            assertTrue("最终操作按钮不能滚进视口",y>=0&&y+done.getHeight()<=scroll.getHeight());snapshotView(scroll,"android-layout-small-sheet-bottom");done.performClick();assertTrue(clicked.get());
            Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();scroll=findScroller(dialog.getWindow().getDecorView());
            for(View view:descendants(scroll))if(view instanceof TextView)((TextView)view).setTextSize(28);measure(scroll,640,600,false);
            for(View view:descendants(scroll))if(view instanceof EditText){TextView field=(TextView)view;assertTrue("大字号输入被固定高度裁剪",field.getMeasuredHeight()>=field.getLineHeight()+field.getCompoundPaddingTop()+field.getCompoundPaddingBottom());}
            snapshotView(scroll,"android-layout-settings-large-font");
        }finally{if(dialog!=null)dialog.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    private static void dialogScreenshot(android.app.Dialog dialog,String name) throws Exception {
        View view=dialog.getWindow().getDecorView(); view.measure(View.MeasureSpec.makeMeasureSpec(786,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1704,View.MeasureSpec.AT_MOST));
        int height=view.getMeasuredHeight(); view.layout(0,0,786,height); Bitmap bitmap=Bitmap.createBitmap(786,height,Bitmap.Config.ARGB_8888); view.draw(new Canvas(bitmap));
        try(OutputStream stream=new FileOutputStream(new File(System.getProperty("feiq.previewDir"),name+".png"))) { assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,stream)); } bitmap.recycle();
    }
    @Test public void nativeUiHistoryDraftsAndFileActions() throws Exception {
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create(); ChatService service=serviceController.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible(); MainActivity activity=activityController.get();
        try {
            settle(service);
            screenshot(activity,"android-empty");
            LanNode.Peer a=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"test-windows","Windows","设计讨论","界面测试");
            LanNode.Peer b=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32426"),"test-android","Android","开发联调","界面测试");
            ExecutorService io=(ExecutorService)field(service,"io"); ChatStore store=(ChatStore)field(service,"store");
            io.submit(()->{
                LanNode.Message incoming=new LanNode.Message(); incoming.number=1; incoming.time=1791285000000L; incoming.text="新的界面看起来清爽多了。\n我们把资料直接发到局域网吧。"; incoming.state="已收到"; store.message(a,incoming,false,false);
                LanNode.Message outgoing=new LanNode.Message(); outgoing.number=2; outgoing.time=incoming.time+60000; outgoing.outgoing=true; outgoing.text="收到，蓝白气泡和文件收发都保留。"; outgoing.state="已送达"; store.message(a,outgoing,false,true);
                LanNode.Message file=new LanNode.Message(); file.number=3; file.time=outgoing.time+60000; file.text="这份说明给你。"; file.state="已收到"; file.files=Collections.singletonList(new Protocol.Attachment(0,"界面说明.pdf",24000,0)); store.message(a,file,false,false);
                LanNode.Message second=new LanNode.Message(); second.number=4; second.time=incoming.time-60000; second.text="联调完成后再做设备验收。"; second.state="已收到"; store.message(b,second,false,false);
            }).get(5,TimeUnit.SECONDS);
            service.active(a.id()); settle(service); service.active(""); assertEquals(2,service.conversations().size());
            screenshot(activity,"android-conversations");
            EditText search=(EditText)field(activity,"search"); search.setText("开发"); assertEquals(1,((ListView)field(activity,"list")).getCount()); search.setText("");
            invoke(activity,"open",String.class,a.id()); settle(service); screenshot(activity,"android-chat");
            assertEquals(3,store.history(a.id()).size());
            EditText editor=(EditText)field(activity,"editor"); editor.setText("这份草稿只属于设计讨论");
            invoke(activity,"open",String.class,""); settle(service); invoke(activity,"open",String.class,b.id()); settle(service);
            assertEquals("",((EditText)field(activity,"editor")).getText().toString());
            invoke(activity,"open",String.class,a.id()); settle(service); assertEquals("这份草稿只属于设计讨论",((EditText)field(activity,"editor")).getText().toString());
            assertEquals(0,service.conversations().stream().filter(c->c.peer.id().equals(a.id())).findFirst().get().unread);
            Method settings=MainActivity.class.getDeclaredMethod("settings"); settings.setAccessible(true); settings.invoke(activity);
            android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog(); assertNotNull(dialog); assertTrue(dialog.isShowing()); dialogScreenshot(dialog,"android-settings"); ScrollView settingsScroll=findScroller(dialog.getWindow().getDecorView()); assertNotNull(settingsScroll); settingsScroll.scrollTo(0,settingsScroll.getChildAt(0).getHeight()); assertTrue(settingsScroll.getScrollY()>0); dialogScreenshot(dialog,"android-settings-files"); dialog.dismiss();
            android.os.Bundle saved=new android.os.Bundle(); activityController.saveInstanceState(saved).pause().stop().destroy(); settle(service);
            activityController=Robolectric.buildActivity(MainActivity.class).create(saved).start().resume().visible(); activity=activityController.get(); settle(service);
            assertEquals("这份草稿只属于设计讨论",((EditText)field(activity,"editor")).getText().toString());
            assertEquals("设",((TextView)field(activity,"chatAvatar")).getText().toString());
        } finally { activityController.pause().stop().destroy(); serviceController.destroy(); }
    }
    @Test public void nightThemeAndOutgoingScroll() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);
        service.remove(activity);Field serviceField=MainActivity.class.getDeclaredField("service");serviceField.setAccessible(true);serviceField.set(activity,null);
        try {
            activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",true).putString("accent","purple").apply();
            invoke(activity,"open",String.class,"test-scroll");assertEquals(0xff292338,field(activity,"SURFACE"));assertEquals(0xff8b67d7,field(activity,"BLUE"));
            java.util.List<LanNode.Message> rows=new ArrayList<>();for(int i=0;i<40;i++){LanNode.Message m=new LanNode.Message();m.number=i;m.time=1;m.text="长列表消息 "+i;m.state="已收到";rows.add(m);}
            Method render=MainActivity.class.getDeclaredMethod("renderMessages",java.util.List.class);render.setAccessible(true);render.invoke(activity,rows);screenshot(activity,"android-night");shadowOf(Looper.getMainLooper()).idle();
            ScrollView scroll=(ScrollView)field(activity,"scroll");scroll.scrollTo(0,0);
            LanNode.Message outgoing=new LanNode.Message();outgoing.number=100;outgoing.time=2;outgoing.outgoing=true;outgoing.text="发送后到最新";outgoing.state="等待确认";rows.add(outgoing);render.invoke(activity,rows);screenshot(activity,"android-outgoing-scroll");shadowOf(Looper.getMainLooper()).idle();
            assertTrue(scroll.getScrollY()>0);assertTrue(scroll.getScrollY()+scroll.getHeight()>=scroll.getChildAt(0).getHeight());
            scroll.scrollTo(0,0);outgoing.state="已送达";render.invoke(activity,rows);shadowOf(Looper.getMainLooper()).idle();assertEquals(0,scroll.getScrollY());
            Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();dialogScreenshot(dialog,"android-night-settings");dialog.dismiss();
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    private static void clickDialogButton(String label) {
        android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();Button button=(Button)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals(label)).findFirst().get();button.performClick();
    }
    @Test public void conversationMenuEditsPinsRemovesAndRestoresWithoutLosingHistory() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"managed","host","对方昵称","");LanNode.Message m=new LanNode.Message();m.number=1;m.time=1;m.text="保留的会话";m.state="已收到";service.incoming(peer,m,false);settle(service);
            invoke(activity,"conversationMenu",ChatStore.Conversation.class,service.conversations().get(0));clickDialogButton("设置备注");android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();EditText note=(EditText)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof EditText).findFirst().get();note.setText("产品负责人");clickDialogButton("保存");settle(service);assertEquals("产品负责人",service.conversations().get(0).displayName());
            invoke(activity,"conversationMenu",ChatStore.Conversation.class,service.conversations().get(0));clickDialogButton("置顶会话");settle(service);assertTrue(service.conversations().get(0).pinned);
            invoke(activity,"conversationMenu",ChatStore.Conversation.class,service.conversations().get(0));clickDialogButton("删除联系人");clickDialogButton("确认删除");settle(service);assertTrue(service.conversations().isEmpty());assertEquals(1,service.hiddenConversations().size());
            ChatStore store=(ChatStore)field(service,"store");ExecutorService io=(ExecutorService)field(service,"io");assertEquals(1,(int)io.submit(()->store.history(peer.id()).size()).get());
            service.removeConversation(peer.id(),false,false,success->{});settle(service);invoke(activity,"open",String.class,peer.id());settle(service);assertEquals("产品负责人",((TextView)field(activity,"title")).getText().toString());
            invoke(activity,"conversationMenu",ChatStore.Conversation.class,service.conversations().get(0));clickDialogButton("删除联系人");dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();Switch clear=(Switch)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Switch).findFirst().get();clear.setChecked(true);clickDialogButton("确认删除");settle(service);assertEquals(0,(int)io.submit(()->store.history(peer.id()).size()).get());assertEquals("",field(activity,"selected"));
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void appearancePresetsFontAndWallpaperPersistFromSettingsUi() throws Exception {
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=controller.get();
        try {
            Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();ScrollView scroller=findScroller(dialog.getWindow().getDecorView());measure(scroller,786,1500,false);
            java.util.List<View> controls=descendants(scroller);Button mint=(Button)controls.stream().filter(v->v instanceof Button&&((Button)v).getText().toString().startsWith("薄荷")).findFirst().get();mint.performClick();
            SeekBar size=(SeekBar)controls.stream().filter(v->v instanceof SeekBar).findFirst().get();size.setProgress(8);
            TextView sample=(TextView)controls.stream().filter(v->v instanceof TextView&&((TextView)v).getText().toString().equals("消息与输入字号预览")).findFirst().get();assertEquals(22*activity.getResources().getDisplayMetrics().scaledDensity,sample.getTextSize(),.1f);
            measure(scroller,786,1500,false);Button jump=(Button)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("外观")).findFirst().get();jump.performClick();snapshotView(scroller,"android-appearance-settings");dialogScreenshot(dialog,"android-settings-fixed-footer");
            Button save=(Button)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("保存")).findFirst().get();save.performClick();SharedPreferences prefs=activity.getSharedPreferences("settings",0);assertEquals(22,prefs.getInt("chat_font_size",0));assertEquals("green",prefs.getString("accent",""));assertEquals("lines",prefs.getString("chat_background",""));assertFalse(prefs.getBoolean("dark_mode",true));
            invoke(activity,"open",String.class,"appearance-test");assertEquals(22*activity.getResources().getDisplayMetrics().scaledDensity,((EditText)field(activity,"editor")).getTextSize(),.1f);assertTrue(((View)field(activity,"body")).getBackground() instanceof ChatWallpaper);
            Bitmap original=Bitmap.createBitmap(64,32,Bitmap.Config.ARGB_8888);File image=new File(activity.getFilesDir(),"wallpaper-test.png");try(OutputStream stream=new FileOutputStream(image)){original.compress(Bitmap.CompressFormat.PNG,100,stream);}original.recycle();Bitmap loaded=ChatWallpaper.load(activity,android.net.Uri.fromFile(image));assertEquals(64,loaded.getWidth());loaded.recycle();assertTrue(image.delete());
        }finally{controller.pause().stop().destroy();serviceController.destroy();}
    }
    @Test public void settingsControlsHaveGapsAtNormalAndNarrowWidths() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();ScrollView scroll=findScroller(dialog.getWindow().getDecorView());
            for(int width:new int[]{786,640}){
                measure(scroll,width,1400,false);
                for(View view:descendants(scroll))if(view instanceof RadioGroup){RadioGroup group=(RadioGroup)view;for(int i=1;i<group.getChildCount();i++){View previous=group.getChildAt(i-1),next=group.getChildAt(i);int gap=group.getOrientation()==LinearLayout.HORIZONTAL?next.getLeft()-previous.getRight():next.getTop()-previous.getBottom();assertTrue("选项按钮贴在一起",gap>=16);}}
                java.util.List<View> views=descendants(scroll);for(View view:views)if(view instanceof Button){ViewGroup parent=(ViewGroup)view.getParent();int index=parent.indexOfChild(view);if(index>0&&parent instanceof LinearLayout&&((LinearLayout)parent).getOrientation()==LinearLayout.VERTICAL&&parent.getChildAt(index-1) instanceof Button)assertTrue("纵向按钮没有间距",view.getTop()-parent.getChildAt(index-1).getBottom()>=16);}
            }
            measure(scroll,640,1000,false);clickDialogButton("外观");snapshotView(scroll,"android-settings-spacing-narrow");
            measure(scroll,786,1400,false);clickDialogButton("外观");snapshotView(scroll,"android-settings-spacing-appearance");dialogScreenshot(dialog,"android-settings-spacing-fixed");clickDialogButton("诊断");snapshotView(scroll,"android-settings-spacing-diagnostics");dialogScreenshot(dialog,"android-settings-spacing-diagnostics-full");dialog.dismiss();
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void fileOpeningUsesMimeReadGrantChooserAndExplicitApkPermission() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            Method open=MainActivity.class.getDeclaredMethod("openFile",android.net.Uri.class,String.class,boolean.class);open.setAccessible(true);android.net.Uri uri=android.net.Uri.parse("content://org.feiqlight.android.received/test/照片.JPG");
            MediaPreview media=new MediaPreview(activity);View thumbnail=media.thumbnail(uri,"照片.JPG",200,()->{try{open.invoke(activity,uri,"照片.JPG",false);}catch(Exception e){throw new RuntimeException(e);}},()->{});thumbnail.performClick();media.close();Intent image=shadowOf(activity).getNextStartedActivity();assertEquals(Intent.ACTION_VIEW,image.getAction());assertEquals("image/jpeg",image.getType());assertEquals(uri,image.getData());assertNotNull(image.getClipData());assertTrue((image.getFlags()&Intent.FLAG_GRANT_READ_URI_PERMISSION)!=0);
            Method with=MainActivity.class.getDeclaredMethod("openFileWith",android.net.Uri.class,String.class,boolean.class,boolean.class);with.setAccessible(true);with.invoke(activity,uri,"资料.pdf",false,true);Intent chooser=shadowOf(activity).getNextStartedActivity();assertEquals(Intent.ACTION_CHOOSER,chooser.getAction());Intent nested=chooser.getParcelableExtra(Intent.EXTRA_INTENT);assertEquals("application/pdf",nested.getType());assertNotNull(nested.getClipData());
            open.invoke(activity,uri,"app.APK",false);assertNull(shadowOf(activity).getNextStartedActivity());clickDialogButton("前往系统设置");Intent permission=shadowOf(activity).getNextStartedActivity();assertEquals(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,permission.getAction());assertEquals("package:"+activity.getPackageName(),permission.getDataString());
            shadowOf(activity.getPackageManager()).setCanRequestPackageInstalls(true);open.invoke(activity,uri,"app.APK",false);Intent install=shadowOf(activity).getNextStartedActivity();assertEquals(Intent.ACTION_VIEW,install.getAction());assertEquals("application/vnd.android.package-archive",install.getType());assertNotNull(install.getClipData());
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void oldDatabaseMigratesContactMetadataWithoutLosingHistory() throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        try(android.database.sqlite.SQLiteDatabase db=context.openOrCreateDatabase("chat.db",0,null)){
            db.execSQL("CREATE TABLE peers (id TEXT PRIMARY KEY,endpoint TEXT,login TEXT,host TEXT,name TEXT,grp TEXT,preview TEXT DEFAULT '',time INTEGER DEFAULT 0,unread INTEGER DEFAULT 0,utf8 INTEGER DEFAULT 0)");
            db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY,peer TEXT,packet INTEGER,outgoing INTEGER,time INTEGER,text TEXT,state TEXT,files TEXT,local_files TEXT DEFAULT '[]',UNIQUE(peer,packet,outgoing))");
            db.execSQL("INSERT INTO peers(id,endpoint,login,host,name,grp) VALUES('127.0.0.1:32425/old','127.0.0.1:32425','old','host','旧联系人','')");db.execSQL("INSERT INTO messages(peer,packet,outgoing,time,text,state,files) VALUES('127.0.0.1:32425/old',1,0,1,'升级前消息','已收到','')");db.setVersion(3);
        }
        try(ChatStore store=new ChatStore(context)){ChatStore.Conversation c=store.conversations().get(0);assertEquals("旧联系人",c.displayName());assertEquals("升级前消息",store.history(c.peer.id()).get(0).text);assertFalse(c.pinned);assertFalse(c.hidden);assertEquals(4,store.getReadableDatabase().getVersion());store.edit(c.peer.id(),"升级后备注",true);assertEquals("升级后备注",store.conversations().get(0).displayName());}
    }
    @Test public void conversationMetadataSurvivesDiscoveryAndClearDoesNotResurrectAck() throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        try(ChatStore store=new ChatStore(context)) {
            LanNode.Peer a=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"a","host","对方昵称",""),b=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32426"),"b","host","其他人","");
            LanNode.Message m=new LanNode.Message();m.number=1;m.time=1000;m.text="保留的历史";m.state="等待确认";m.outgoing=true;store.message(a,m,false,false);
            LanNode.Message n=m.copy();n.number=2;n.time=2000;store.message(b,n,false,false);store.edit(a.id(),"我的备注",true);store.peer(a);
            ChatStore.Conversation c=store.conversations().get(0);assertEquals(a.id(),c.peer.id());assertEquals("我的备注",c.displayName());assertEquals("对方昵称",c.peer.name);assertTrue(c.pinned);
            new DraftStore(context).save(a.id(),"独立草稿");store.remove(a.id(),false,true);assertTrue(store.conversations().get(0).hidden);assertEquals(1,store.history(a.id()).size());
            store.remove(a.id(),true,true);m.state="已送达";assertFalse(store.message(a,m,true,false));assertTrue(store.history(a.id()).isEmpty());assertEquals(1,store.history(b.id()).size());assertEquals("独立草稿",new DraftStore(context).read(a.id()));
            LanNode.Message fresh=m.copy();fresh.number=3;fresh.time=3000;fresh.text="新消息";assertTrue(store.message(a,fresh,false,false));c=store.conversations().get(0);assertFalse(c.hidden);assertEquals("我的备注",c.displayName());
        }
        try(ChatStore reopened=new ChatStore(context)){assertEquals("我的备注",reopened.conversations().get(0).displayName());assertTrue(reopened.conversations().get(0).pinned);}
    }
    @Test public void webLinksOpenBrowsableIntentAndRejectNonWebSchemes() throws Exception {
        ActivityController<android.app.Activity> controller=Robolectric.buildActivity(android.app.Activity.class).setup();android.app.Activity context=controller.get();TextView view=new TextView(context);view.setText("看 https://example.com/a?q=1&x=2。和 www.example.org/path，(https://example.net/a(b))");MessageLinks.apply(view,0xff3390ec);
        android.text.Spanned text=(android.text.Spanned)view.getText();android.text.style.ClickableSpan[] spans=text.getSpans(0,text.length(),android.text.style.ClickableSpan.class);assertEquals(3,spans.length);
        spans[0].onClick(view);Intent intent=shadowOf(context).getNextStartedActivity();assertNotNull(intent);assertEquals(Intent.ACTION_VIEW,intent.getAction());assertTrue(intent.hasCategory(Intent.CATEGORY_BROWSABLE));assertEquals("https://example.com/a?q=1&x=2",intent.getDataString());
        assertNull(MessageLinks.normalize("javascript:alert(1)"));assertNull(MessageLinks.normalize("file:///data/private"));assertNull(MessageLinks.normalize("https://user:pass@example.com"));controller.pause().stop().destroy();
    }
    @Test public void firstChatFrameAlreadyAtBottomAndHistoryRefreshKeepsViewport() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();settle(service);service.remove(activity);Field source=MainActivity.class.getDeclaredField("service");source.setAccessible(true);source.set(activity,null);
        try {
            invoke(activity,"open",String.class,"scroll-regression");List<LanNode.Message> rows=new ArrayList<>();for(int i=0;i<35;i++){LanNode.Message m=new LanNode.Message();m.number=i;m.time=i+1;m.text="第 "+i+" 条历史消息 https://example.com/"+i;m.state="已收到";rows.add(m);}
            invoke(activity,"renderMessages",List.class,rows);View root=(View)field(activity,"root");measure(root,786,1704,true);ScrollView scroll=(ScrollView)field(activity,"scroll");scroll.getViewTreeObserver().dispatchOnPreDraw();assertTrue("首帧没有定位到底部",scroll.getScrollY()>0);assertEquals(scroll.getChildAt(0).getHeight()-scroll.getHeight(),scroll.getScrollY());
            scroll.scrollTo(0,170);rows.get(0).state="已送达";invoke(activity,"renderMessages",List.class,rows);measure(root,786,1704,true);scroll.getViewTreeObserver().dispatchOnPreDraw();assertEquals("ACK 抢走阅读位置",170,scroll.getScrollY());
            LanNode.Message incoming=new LanNode.Message();incoming.number=99;incoming.time=99;incoming.text="刚收到";incoming.state="已收到";rows.add(incoming);invoke(activity,"renderMessages",List.class,rows);measure(root,786,1704,true);scroll.getViewTreeObserver().dispatchOnPreDraw();assertEquals("入站消息抢走阅读位置",170,scroll.getScrollY());
            incoming.outgoing=true;incoming.number=100;invoke(activity,"renderMessages",List.class,rows);measure(root,786,1704,true);scroll.getViewTreeObserver().dispatchOnPreDraw();assertEquals(scroll.getChildAt(0).getHeight()-scroll.getHeight(),scroll.getScrollY());screenshot(activity,"android-conversation-links");
            invoke(activity,"open",String.class,"");scroll.getViewTreeObserver().dispatchOnPreDraw();assertEquals("",field(activity,"selected"));
        }finally{ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void sqliteDeliveryUpsertAndPendingRecovery() throws Exception {
        Context context=RuntimeEnvironment.getApplication(); ChatStore store=new ChatStore(context);
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"test","host","节点","部门");
            LanNode.Message m=new LanNode.Message(); m.number=7; m.time=1; m.text="同一条记录"; m.outgoing=true; m.state="等待确认"; store.message(peer,m,false,false);
            store.recoverPending(); assertEquals("上次退出，未确认送达",store.history(peer.id()).get(0).state);
            m.state="已送达"; store.message(peer,m,true,false); assertEquals(1,store.history(peer.id()).size()); assertEquals("已送达",store.history(peer.id()).get(0).state);
        } finally { store.close(); }
    }
    @Test public void ackKeepsSameMillisecondMessageOrder()throws Exception {
        try(ChatStore store=new ChatStore(RuntimeEnvironment.getApplication())) {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"order","host","排序测试","");
            LanNode.Message first=new LanNode.Message();first.number=1;first.time=1000;first.text="第一条";first.state="等待确认";first.outgoing=true;
            LanNode.Message second=first.copy();second.number=2;second.text="第二条";
            store.message(peer,first,false,true);store.message(peer,second,false,true);first.state="已送达";store.message(peer,first,true,true);
            List<LanNode.Message> history=store.history(peer.id());assertEquals(2,history.size());assertEquals("ACK 不得改变同毫秒消息的相对顺序",1,history.get(0).number);assertEquals(2,history.get(1).number);
        }
    }
    @Test public void duplicateStoredMessageDoesNotInflateUnreadOrRegressPreview()throws Exception {
        try(ChatStore store=new ChatStore(RuntimeEnvironment.getApplication())) {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"dedup","host","去重测试","");
            LanNode.Message first=new LanNode.Message();first.number=1;first.time=1000;first.text="旧消息";first.state="已收到";
            LanNode.Message second=first.copy();second.number=2;second.time=1001;second.text="新消息";
            store.message(peer,first,false,false);store.message(peer,second,false,false);LanNode.Message duplicate=first.copy();first.state="已拒绝";store.message(peer,first,true,false);store.message(peer,duplicate,false,false);
            ChatStore.Conversation conversation=store.conversations().get(0);assertEquals("重复入库增加了未读",2,conversation.unread);assertEquals("新消息",conversation.preview);assertEquals(1001,conversation.time);assertEquals(2,store.history(peer.id()).size());
            assertEquals("重复包回滚了已处理状态","已拒绝",store.history(peer.id()).get(0).state);
        }
    }
    @Test public void legacyLongUnicodeAttachmentRemainsInHistory()throws Exception {
        try(ChatStore store=new ChatStore(RuntimeEnvironment.getApplication())) {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"legacy-file","host","旧附件","");
            String name="中".repeat(100)+".png";LanNode.Message m=new LanNode.Message();m.number=1;m.time=1000;m.text="旧版附件";m.state="已收到";m.files=Arrays.asList(new Protocol.Attachment(0,name,16,1),new Protocol.Attachment(1,"../unsafe.txt",1,1));
            assertTrue("新网络邀请不应放宽名称字节限制",Protocol.parseFiles(Protocol.files(m.files)).isEmpty());store.message(peer,m,false,true);
            List<Protocol.Attachment> files=store.history(peer.id()).get(0).files;assertEquals("升级后旧历史附件被过滤丢失",1,files.size());assertEquals(name,files.get(0).name);
        }
    }
    @Test public void settingsColorSelectionPersistsWithoutSpinner()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();android.app.Dialog dialog=null;
        try {
            MainActivity activity=ac.get();Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();
            java.util.List<View> views=descendants(dialog.getWindow().getDecorView());assertFalse(views.stream().anyMatch(v->v instanceof Spinner));
            RadioGroup colors=(RadioGroup)views.stream().filter(v->v instanceof RadioGroup).findFirst().get();assertNotEquals(-1,colors.getCheckedRadioButtonId());
            RadioButton purple=(RadioButton)views.stream().filter(v->v instanceof RadioButton&&"purple".equals(v.getTag())).findFirst().get();purple.performClick();assertEquals(purple.getId(),colors.getCheckedRadioButtonId());
            for(View view:views)view.jumpDrawablesToCurrentState();dialogScreenshot(dialog,"android-settings-selection");
            Switch diagnostics=(Switch)views.stream().filter(v->v instanceof Switch&&"记录故障诊断".contentEquals(((Switch)v).getText())).findFirst().get();assertFalse(diagnostics.isChecked());diagnostics.setChecked(true);
            ScrollView diagnosticScroll=(ScrollView)views.stream().filter(v->v instanceof ScrollView).findFirst().get();diagnosticScroll.fullScroll(View.FOCUS_DOWN);shadowOf(Looper.getMainLooper()).idle();dialogScreenshot(dialog,"android-diagnostics-settings");
            Button save=(Button)views.stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("保存")).findFirst().get();save.performClick();assertEquals("purple",activity.getSharedPreferences("settings",0).getString("accent",""));
            assertTrue(activity.getSharedPreferences("settings",0).getBoolean("diagnostics",false));
            settings.invoke(activity);dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();RadioGroup restored=(RadioGroup)descendants(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof RadioGroup).findFirst().get();assertEquals("purple",restored.findViewById(restored.getCheckedRadioButtonId()).getTag());
        }finally{if(dialog!=null)dialog.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void unifiedThemeAndMessageActionsKeepCopyAndForward()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();android.app.Dialog dialog=null;
        try {
            MainActivity activity=ac.get();Method apply=MainActivity.class.getDeclaredMethod("applyTheme");apply.setAccessible(true);
            Method blend=MainActivity.class.getDeclaredMethod("blend",int.class,int.class,float.class);blend.setAccessible(true);
            for(boolean dark:new boolean[]{false,true})for(String accent:new String[]{"blue","green","purple"}){
                activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putString("accent",accent).apply();apply.invoke(activity);
                assertEquals(blend.invoke(null,(Integer)field(activity,"BLUE"),(Integer)field(activity,"SURFACE"),dark?.30f:.17f),field(activity,"BUBBLE"));
            }
            activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",false).putString("accent","blue").apply();apply.invoke(activity);
            LanNode.Message message=new LanNode.Message();message.text="原文 😊\n不要改写";LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"ui","host","来源","测试");
            Method actions=MainActivity.class.getDeclaredMethod("messageActions",LanNode.Message.class,LanNode.Peer.class);actions.setAccessible(true);actions.invoke(activity,message,peer);dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();
            java.util.List<View> views=descendants(dialog.getWindow().getDecorView());assertTrue(views.stream().anyMatch(v->v instanceof Button&&((Button)v).getText().toString().equals("转发…")));
            dialogScreenshot(dialog,"android-unified-message-actions");Button copy=(Button)views.stream().filter(v->v instanceof Button&&((Button)v).getText().toString().equals("复制消息")).findFirst().get();copy.performClick();
            assertEquals(message.text,activity.getSystemService(ClipboardManager.class).getPrimaryClip().getItemAt(0).getText().toString());assertFalse(dialog.isShowing());
        }finally{if(dialog!=null)dialog.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void migratesLegacyPeerEncodingWithoutDroppingData() throws Exception {
        Context context=RuntimeEnvironment.getApplication(); context.deleteDatabase("chat.db");
        try(android.database.sqlite.SQLiteDatabase old=context.openOrCreateDatabase("chat.db",Context.MODE_PRIVATE,null)) {
            old.execSQL("CREATE TABLE peers (id TEXT PRIMARY KEY, endpoint TEXT, login TEXT, host TEXT, name TEXT, grp TEXT, preview TEXT DEFAULT '', time INTEGER DEFAULT 0, unread INTEGER DEFAULT 0)");
            // 使用完整的旧版结构，同时验证新增本机附件列不损坏旧历史。
            old.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY, peer TEXT, packet INTEGER, outgoing INTEGER, time INTEGER, text TEXT, state TEXT, files TEXT, UNIQUE(peer,packet,outgoing))");
            old.execSQL("INSERT INTO messages(peer,packet,outgoing,time,text,state,files) VALUES('legacy',1,1,1,'旧消息','已送达','')");
            old.execSQL("INSERT INTO peers(id,endpoint,login,host,name,grp) VALUES('127.0.0.1:32425/peer','127.0.0.1:32425','peer','host','旧联系人','部门')"); old.setVersion(1);
        }
        try(ChatStore store=new ChatStore(context)) { assertEquals(1,store.conversations().size()); LanNode.Peer peer=store.conversations().get(0).peer; assertFalse(peer.utf8); peer.utf8=true; store.peer(peer); assertTrue(store.conversations().get(0).peer.utf8); assertEquals("旧消息",store.history("legacy").get(0).text);assertTrue(store.history("legacy").get(0).localPaths.isEmpty()); }
    }
}
