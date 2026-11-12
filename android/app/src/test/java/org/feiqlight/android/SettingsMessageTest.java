package org.feiqlight.android;

import android.app.*;
import android.content.*;
import android.graphics.*;
import android.net.Uri;
import android.os.Looper;
import android.view.*;
import android.widget.*;
import org.feiqlight.android.core.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.*;
import org.robolectric.annotation.*;
import org.robolectric.shadows.*;
import java.io.*;
import java.lang.reflect.*;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w360dp-h800dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class SettingsMessageTest {
    private static Object field(Object target,String name) throws Exception {Field f=target.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(target);}
    private static Object invoke(Object target,String name,Class<?>[] types,Object...args) throws Exception {Method m=target.getClass().getDeclaredMethod(name,types);m.setAccessible(true);return m.invoke(target,args);}
    private static List<View> views(View root) {List<View> result=new ArrayList<>();result.add(root);if(root instanceof ViewGroup){ViewGroup group=(ViewGroup)root;for(int i=0;i<group.getChildCount();i++)result.addAll(views(group.getChildAt(i)));}return result;}
    private static void click(Dialog dialog,String text) {views(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&text.contentEquals(((Button)v).getText())).findFirst().orElseThrow().performClick();}
    private static void messageMenu(View bubble) {views(bubble).stream().filter(v->v.getContentDescription()!=null&&v.getContentDescription().toString().startsWith("消息操作：")).findFirst().orElseThrow().performClick();}
    private static void settle(ChatService service) throws Exception {for(int i=0;i<6;i++){((ExecutorService)field(service,"io")).submit(()->{}).get(5,TimeUnit.SECONDS);shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));}}
    private static void snapshot(View view,String name) throws Exception {
        view.measure(View.MeasureSpec.makeMeasureSpec(720,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1500,View.MeasureSpec.AT_MOST));view.layout(0,0,view.getMeasuredWidth(),view.getMeasuredHeight());
        Bitmap image=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);view.draw(new Canvas(image));File output=new File(System.getProperty("feiq.previewDir"),name+".png");
        try(OutputStream stream=new FileOutputStream(output)){assertTrue(image.compress(Bitmap.CompressFormat.PNG,100,stream));}image.recycle();
    }
    @Test public void avatarsUseSystemPickerKeepDefaultAndIgnoreOldResults() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();Dialog settings=null,picker=null;
        try {
            settle(service);assertEquals("default",activity.getSharedPreferences("settings",0).getString("avatar","default"));invoke(activity,"settings",new Class<?>[0]);settings=ShadowDialog.getLatestDialog();click(settings,"更换头像");picker=ShadowDialog.getLatestDialog();
            View option=views(picker.getWindow().getDecorView()).stream().filter(v->"内置头像：熬夜猫头鹰".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();option.performClick();snapshot(picker.getWindow().getDecorView(),"android-avatar-picker-builtin");
            click(picker,"使用此头像");assertEquals("default",activity.getSharedPreferences("settings",0).getString("avatar","default"));click(settings,"外观");
            Switch follow=(Switch)views(settings.getWindow().getDecorView()).stream().filter(v->v instanceof Switch&&"跟随系统切换".contentEquals(((Switch)v).getText())).findFirst().orElseThrow();follow.setChecked(true);
            Switch night=(Switch)views(settings.getWindow().getDecorView()).stream().filter(v->v instanceof Switch&&"夜间模式".contentEquals(((Switch)v).getText())).findFirst().orElseThrow();assertFalse(night.isEnabled());
            click(settings,"保存");assertEquals("builtin:15",activity.getSharedPreferences("settings",0).getString("avatar",""));assertTrue(activity.getSharedPreferences("settings",0).getBoolean("follow_system_theme",false));
            invoke(activity,"settings",new Class<?>[0]);settings=ShadowDialog.getLatestDialog();click(settings,"更换头像");picker=ShadowDialog.getLatestDialog();click(picker,"本地图片");
            org.robolectric.shadows.ShadowActivity.IntentForResult request=shadowOf(activity).getNextStartedActivityForResult();assertEquals(25,request.requestCode);assertEquals(Intent.ACTION_OPEN_DOCUMENT,request.intent.getAction());assertEquals(0,request.intent.getFlags()&Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
            Uri uri=Uri.parse("content://avatar.test/image");Bitmap source=Bitmap.createBitmap(120,80,Bitmap.Config.ARGB_8888);source.eraseColor(Color.CYAN);ByteArrayOutputStream bytes=new ByteArrayOutputStream();source.compress(Bitmap.CompressFormat.PNG,100,bytes);source.recycle();shadowOf(activity.getContentResolver()).registerInputStream(uri,new ByteArrayInputStream(bytes.toByteArray()));
            invoke(activity,"onActivityResult",new Class<?>[]{int.class,int.class,Intent.class},25,Activity.RESULT_OK,new Intent().setData(uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));
            ((ExecutorService)field(activity,"avatarLoader")).submit(()->{}).get(5,TimeUnit.SECONDS);shadowOf(Looper.getMainLooper()).idle();String image=(String)field(field(activity,"avatarSelection"),"value");assertTrue(image.startsWith("image:"));assertNotNull(Protocol.normalizeAvatar(image));snapshot(picker.getWindow().getDecorView(),"android-avatar-picker-picture");
            byte[] avatarBytes=java.util.Base64.getDecoder().decode(image.substring(6));Bitmap avatarBitmap=BitmapFactory.decodeByteArray(avatarBytes,0,avatarBytes.length);assertEquals(128,avatarBitmap.getWidth());assertEquals(128,avatarBitmap.getHeight());avatarBitmap.recycle();
            click(picker,"使用此头像");click(settings,"保存");assertEquals(image,activity.getSharedPreferences("settings",0).getString("avatar",""));
            invoke(activity,"settings",new Class<?>[0]);settings=ShadowDialog.getLatestDialog();click(settings,"更换头像");picker=ShadowDialog.getLatestDialog();click(picker,"本地图片");shadowOf(activity).getNextStartedActivityForResult();picker.dismiss();click(settings,"更换头像");picker=ShadowDialog.getLatestDialog();
            String before=(String)field(field(activity,"avatarSelection"),"value");invoke(activity,"onActivityResult",new Class<?>[]{int.class,int.class,Intent.class},25,Activity.RESULT_OK,new Intent().setData(uri));assertEquals(before,field(field(activity,"avatarSelection"),"value"));
            picker.dismiss();settings.dismiss();assertEquals(image,activity.getSharedPreferences("settings",0).getString("avatar",""));
            try{UserAvatar.read(activity,Uri.parse("file:///ungranted.png"));fail("绕过系统 URI 授权");}catch(IOException expected){assertTrue(expected.getMessage().contains("授权"));}
        } finally {if(picker!=null)picker.dismiss();if(settings!=null)settings.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void storeDeletesPersistWithoutRemovingAttachmentsOrOtherConversations() throws Exception {
        Context context=RuntimeEnvironment.getApplication();LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"peer","host","联系人","部门"),other=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32426"),"other","host","其他","部门");
        File file=new File(context.getFilesDir(),"保留的文件.txt");try(OutputStream output=new FileOutputStream(file)){output.write(7);}new DraftStore(context).save(peer.id(),"保留草稿");
        LanNode.Message incoming=new LanNode.Message();incoming.number=1;incoming.text="入站";incoming.time=1;incoming.state="已收到";
        LanNode.Message outgoing=new LanNode.Message();outgoing.number=1;outgoing.text="出站";outgoing.time=2;outgoing.outgoing=true;outgoing.state="已送达";outgoing.localPaths.add(file.getPath());
        try(ChatStore store=new ChatStore(context)) {store.message(peer,incoming,false,false);store.message(peer,outgoing,false,false);store.message(other,incoming,false,false);store.deleteMessages(peer.id(),Collections.singletonList(incoming));assertEquals("出站",store.history(peer.id()).get(0).text);assertFalse(store.message(peer,incoming,false,false));store.deleteMessages(peer.id(),Collections.singletonList(outgoing));}
        try(ChatStore store=new ChatStore(context)) {assertFalse(store.message(peer,incoming,false,false));assertFalse(store.message(peer,outgoing,true,false));assertTrue(store.history(peer.id()).isEmpty());assertEquals(1,store.history(other.id()).size());assertTrue(file.exists());assertEquals("保留草稿",new DraftStore(context).read(peer.id()));}
    }
    @Test public void messageMenuSupportsCancelSingleDeleteAndMultiDeleteOnSmallScreen() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"delete","host","删除测试","");for(int i=0;i<3;i++){LanNode.Message message=new LanNode.Message();message.number=i+1;message.text="消息"+i;message.time=i+1;message.state="已送达";message.outgoing=true;service.incoming(peer,message,false);}settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
            View last=views((View)field(activity,"messages")).stream().filter(v->"消息：消息2".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();messageMenu(last);click(ShadowDialog.getLatestDialog(),"删除这条消息");ShadowDialog.getLatestDialog().dismiss();settle(service);assertEquals(3,((List<?>)field(activity,"messageRows")).size());
            messageMenu(last);click(ShadowDialog.getLatestDialog(),"删除这条消息");click(ShadowDialog.getLatestDialog(),"删除");settle(service);assertEquals(2,((List<?>)field(activity,"messageRows")).size());
            View first=views((View)field(activity,"messages")).stream().filter(v->"消息：消息0".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();messageMenu(first);click(ShadowDialog.getLatestDialog(),"选择多条消息");
            View second=views((View)field(activity,"messages")).stream().filter(v->"消息：消息1".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();second.performClick();assertEquals(2,((Set<?>)field(activity,"messageMarks")).size());
            snapshot(activity.getWindow().getDecorView(),"android-messages-multi-select");Button delete=(Button)field(activity,"deleteSelectedMessages");assertTrue(delete.isEnabled());delete.performClick();click(ShadowDialog.getLatestDialog(),"删除");settle(service);assertTrue(((List<?>)field(activity,"messageRows")).isEmpty());assertFalse((Boolean)field(activity,"selectingMessages"));
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void selectionDoesNotOpenAttachmentsAndPendingFilesPreventDelete() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).apply();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"files","host","文件","");LanNode.Message message=new LanNode.Message();message.number=1;message.time=1;message.text="待接收文件";message.state="已收到";message.files=Collections.singletonList(new Protocol.Attachment(0,"文件.txt",12,0));service.incoming(peer,message,false);settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
            View bubble=views((View)field(activity,"messages")).stream().filter(v->"消息：待接收文件".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();messageMenu(bubble);click(ShadowDialog.getLatestDialog(),"选择多条消息");Dialog previous=ShadowDialog.getLatestDialog();
            ac.windowFocusChanged(true);long pressed=android.os.SystemClock.uptimeMillis();MotionEvent down=MotionEvent.obtain(pressed,pressed,MotionEvent.ACTION_DOWN,10,10,0),up=MotionEvent.obtain(pressed,pressed+20,MotionEvent.ACTION_UP,10,10,0);bubble.dispatchTouchEvent(down);bubble.dispatchTouchEvent(up);down.recycle();up.recycle();shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));assertSame(previous,ShadowDialog.getLatestDialog());assertTrue("点击附件卡片未取消选中",((Set<?>)field(activity,"messageMarks")).isEmpty());
            bubble.performClick();((Button)field(activity,"deleteSelectedMessages")).performClick();click(ShadowDialog.getLatestDialog(),"删除");settle(service);assertEquals(1,((List<?>)field(activity,"messageRows")).size());assertTrue(ShadowToast.getTextOfLatestToast().contains("文件任务"));assertTrue((Boolean)field(activity,"selectingMessages"));
            invoke(activity,"open",new Class<?>[]{String.class},"other-peer");assertFalse((Boolean)field(activity,"selectingMessages"));assertTrue(((Set<?>)field(activity,"messageMarks")).isEmpty());
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void blankCardRequiresLongPressAndKeepsCornerMenu() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"hold","host","长按选择","");for(int i=0;i<2;i++){LanNode.Message message=new LanNode.Message();message.number=i+1;message.text="空白选择"+i;message.time=i+1;message.state="已收到";service.incoming(peer,message,false);}settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);snapshot(activity.getWindow().getDecorView(),"android-before-long-press");
            View first=views((View)field(activity,"messages")).stream().filter(v->"消息：空白选择0".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();ac.windowFocusChanged(true);assertTrue("长按需要窗口取得焦点",first.hasWindowFocus());
            View stamp=views(first).stream().filter(v->v.getContentDescription()!=null&&v.getContentDescription().toString().startsWith("消息操作：")).findFirst().orElseThrow();float x=stamp.getLeft()+4,y=stamp.getTop()+stamp.getHeight()/2f;
            long now=android.os.SystemClock.uptimeMillis();MotionEvent down=MotionEvent.obtain(now,now,MotionEvent.ACTION_DOWN,x,y,0),up=MotionEvent.obtain(now,now+20,MotionEvent.ACTION_UP,x,y,0);first.dispatchTouchEvent(down);first.dispatchTouchEvent(up);down.recycle();up.recycle();shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));assertFalse("普通点击不应进入多选",(Boolean)field(activity,"selectingMessages"));
            now=android.os.SystemClock.uptimeMillis();down=MotionEvent.obtain(now,now,MotionEvent.ACTION_DOWN,x,y,0);first.dispatchTouchEvent(down);MotionEvent cancel=MotionEvent.obtain(now,now+20,MotionEvent.ACTION_CANCEL,x,y,0);first.dispatchTouchEvent(cancel);down.recycle();cancel.recycle();shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(ViewConfiguration.getLongPressTimeout()+100));assertFalse("取消触摸后仍长按选中",(Boolean)field(activity,"selectingMessages"));
            now=android.os.SystemClock.uptimeMillis();down=MotionEvent.obtain(now,now,MotionEvent.ACTION_DOWN,x,y,0);first.dispatchTouchEvent(down);shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(ViewConfiguration.getLongPressTimeout()+100));up=MotionEvent.obtain(now,android.os.SystemClock.uptimeMillis(),MotionEvent.ACTION_UP,x,y,0);first.dispatchTouchEvent(up);down.recycle();up.recycle();assertTrue("时间行空白长按未选中",(Boolean)field(activity,"selectingMessages"));assertEquals(1,((Set<?>)field(activity,"messageMarks")).size());
            View second=views((View)field(activity,"messages")).stream().filter(v->"消息：空白选择1".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();second.performClick();assertEquals(2,((Set<?>)field(activity,"messageMarks")).size());messageMenu(second);assertTrue(ShadowDialog.getLatestDialog().isShowing());assertEquals(2,((Set<?>)field(activity,"messageMarks")).size());ShadowDialog.getLatestDialog().dismiss();snapshot(activity.getWindow().getDecorView(),"android-long-press-multi-select");
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void systemThemePreservesDraftSelectionAndReadingPosition() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();service.getSharedPreferences("settings",0).edit().putBoolean("follow_system_theme",true).apply();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"theme","host","系统主题","");for(int i=0;i<30;i++){LanNode.Message message=new LanNode.Message();message.number=i+1;message.text="旧消息 "+i;message.time=i+1;message.state="已收到";service.incoming(peer,message,false);}settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);snapshot(activity.getWindow().getDecorView(),"android-system-theme-before");
            ((EditText)field(activity,"editor")).setText("不能丢的草稿");ScrollView scroll=(ScrollView)field(activity,"scroll");scroll.scrollTo(0,100);View bubble=views((View)field(activity,"messages")).stream().filter(v->"消息：旧消息 0".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();bubble.performLongClick();int reading=scroll.getScrollY();
            android.content.res.Configuration config=new android.content.res.Configuration(activity.getResources().getConfiguration());config.uiMode=(config.uiMode&~android.content.res.Configuration.UI_MODE_NIGHT_MASK)|android.content.res.Configuration.UI_MODE_NIGHT_YES;activity.getResources().updateConfiguration(config,activity.getResources().getDisplayMetrics());activity.onConfigurationChanged(config);settle(service);snapshot(activity.getWindow().getDecorView(),"android-follow-system-night");((ScrollView)field(activity,"scroll")).getViewTreeObserver().dispatchOnPreDraw();
            assertTrue((Boolean)field(activity,"DARK"));assertEquals("不能丢的草稿",((EditText)field(activity,"editor")).getText().toString());assertEquals(1,((Set<?>)field(activity,"messageMarks")).size());assertEquals(reading,((ScrollView)field(activity,"scroll")).getScrollY());
            activity.getSharedPreferences("settings",0).edit().putBoolean("follow_system_theme",false).putBoolean("dark_mode",true).apply();config.uiMode=(config.uiMode&~android.content.res.Configuration.UI_MODE_NIGHT_MASK)|android.content.res.Configuration.UI_MODE_NIGHT_NO;activity.getResources().updateConfiguration(config,activity.getResources().getDisplayMetrics());activity.onConfigurationChanged(config);assertTrue((Boolean)field(activity,"DARK"));
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void transferSpeedUpdatesCardAndClearsOnCompletionWithoutScrolling() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"rate","host","传输速率","");LanNode.Message message=new LanNode.Message();message.number=1;message.time=1;message.text="速率 English";message.outgoing=true;message.state="已送达";message.files=Collections.singletonList(new Protocol.Attachment(0,"长文件名 English.zip",104857600,0));service.incoming(peer,message,false);settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
            Class<?>[] signature={String.class,long.class,long.class,boolean.class,int.class,String.class,double.class,LanNode.Transfer.class};
            invoke(service,"fileProgress",signature,peer.id(),1L,0L,true,42,"发送中",1572864.0,null);activity.transfersChanged();
            View container=(View)field(activity,"messages");assertTrue(views(container).stream().anyMatch(v->v instanceof TextView&&((TextView)v).getText().toString().contains("42% · 1.5 MB/s")));
            ScrollView scroll=(ScrollView)field(activity,"scroll");int previous=scroll.getScrollY();snapshot(activity.getWindow().getDecorView(),"android-transfer-rate");
            invoke(service,"fileProgress",signature,peer.id(),1L,0L,true,100,"已发送（非保存确认）",0.0,null);activity.transfersChanged();assertEquals(previous,scroll.getScrollY());assertFalse(views(container).stream().anyMatch(v->v instanceof TextView&&((TextView)v).getText().toString().contains("MB/s")));assertEquals(0,service.progress(peer.id(),1,0,true).bytesPerSecond,0);
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void contactAvatarOpensCenteredProfileAnd128PreviewWithLocalNickname() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();Dialog profile=null,preview=null;
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"profile","host","原昵称","部门 Engineering");Bitmap image=Bitmap.createBitmap(128,128,Bitmap.Config.ARGB_8888);image.eraseColor(Color.MAGENTA);ByteArrayOutputStream bytes=new ByteArrayOutputStream();image.compress(Bitmap.CompressFormat.JPEG,85,bytes);image.recycle();peer.avatar="image:"+java.util.Base64.getEncoder().encodeToString(bytes.toByteArray());
            LanNode.Message message=new LanNode.Message();message.number=1;message.text="资料";message.time=1;message.state="已收到";service.incoming(peer,message,false);settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
            for(boolean dark:new boolean[]{false,true}) {
                activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putBoolean("follow_system_theme",false).apply();invoke(activity,"renderPage",new Class<?>[0]);settle(service);
                ((View)field(activity,"chatAvatar")).performClick();profile=ShadowDialog.getLatestDialog();snapshot(profile.getWindow().getDecorView(),"android-contact-profile-"+(dark?"dark":"light"));
                View avatar=views(profile.getWindow().getDecorView()).stream().filter(v->"联系人资料头像，点击查看大图".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();assertEquals(((View)avatar.getParent()).getWidth()/2,avatar.getLeft()+avatar.getWidth()/2,1);
                assertTrue(views(profile.getWindow().getDecorView()).stream().anyMatch(v->v instanceof TextView&&"127.0.0.1:32425".contentEquals(((TextView)v).getText())));
                avatar.performClick();preview=ShadowDialog.getLatestDialog();ImageView big=(ImageView)views(preview.getWindow().getDecorView()).stream().filter(v->"头像大图".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();assertEquals(128,((android.graphics.drawable.BitmapDrawable)big.getDrawable()).getBitmap().getWidth());snapshot(preview.getWindow().getDecorView(),"android-avatar-full-"+(dark?"dark":"light"));click(preview,"关闭头像");
                EditText nickname=(EditText)views(profile.getWindow().getDecorView()).stream().filter(v->"联系人资料昵称".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();nickname.setText("本机昵称 English");click(profile,"保存");settle(service);assertEquals("本机昵称 English",service.conversations().get(0).note);
            }
            try(ChatStore store=new ChatStore(service)){assertEquals("本机昵称 English",store.conversations().get(0).note);assertEquals("原昵称",store.conversations().get(0).peer.name);}
            ((View)field(activity,"chatAvatar")).performClick();profile=ShadowDialog.getLatestDialog();EditText nickname=(EditText)views(profile.getWindow().getDecorView()).stream().filter(v->v instanceof EditText).findFirst().orElseThrow();nickname.setText("旧窗口修改");invoke(activity,"open",new Class<?>[]{String.class},"other-peer");click(profile,"保存");settle(service);assertEquals("本机昵称 English",service.conversations().get(0).note);
        } finally {if(preview!=null)preview.dismiss();if(profile!=null)profile.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void emojiGroupsShareOneScrollablePanelAndOldPanelCannotEditAnotherChat() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();Dialog dialog=null;
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"emoji","host","表情","");LanNode.Message message=new LanNode.Message();message.number=1;message.text="表情列表";message.time=1;message.state="已收到";service.incoming(peer,message,false);settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
            EditText input=(EditText)field(activity,"editor");input.setText("前后");input.setSelection(1);invoke(activity,"showEmoji",new Class<?>[0]);dialog=ShadowDialog.getLatestDialog();snapshot(dialog.getWindow().getDecorView(),"android-emoji-continuous-top");
            List<View> contents=views(dialog.getWindow().getDecorView());assertFalse(contents.stream().anyMatch(v->v instanceof Spinner));assertEquals(144,contents.stream().filter(v->v instanceof Button).count());
            ScrollView scroll=(ScrollView)contents.stream().filter(v->v instanceof ScrollView).findFirst().orElseThrow();scroll.setSmoothScrollingEnabled(false);scroll.fullScroll(View.FOCUS_DOWN);shadowOf(Looper.getMainLooper()).idle();assertTrue("表情列表不能滚动",scroll.getScrollY()>0);snapshot(dialog.getWindow().getDecorView(),"android-emoji-continuous-bottom");
            Button last=(Button)contents.stream().filter(v->v instanceof Button&&"🇨🇳".contentEquals(((Button)v).getText())).findFirst().orElseThrow();Rect visible=new Rect();assertTrue(last.getGlobalVisibleRect(visible));last.performClick();assertEquals("前🇨🇳后",input.getText().toString());
            invoke(activity,"showEmoji",new Class<?>[0]);dialog=ShadowDialog.getLatestDialog();Button old=(Button)views(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&"😀".contentEquals(((Button)v).getText())).findFirst().orElseThrow();invoke(activity,"open",new Class<?>[]{String.class},"other-peer");old.performClick();assertEquals("",((EditText)field(activity,"editor")).getText().toString());
        } finally {if(dialog!=null)dialog.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test @Config(qualifiers="w320dp-h480dp-xhdpi") public void largeSystemFontDoesNotClipEmojiAndLastRowRemainsReachable() throws Exception {
        RuntimeEnvironment.setFontScale(2f);
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();Dialog dialog=null;
        try {
            invoke(activity,"open",new Class<?>[]{String.class},"emoji-layout-peer");invoke(activity,"showEmoji",new Class<?>[0]);dialog=ShadowDialog.getLatestDialog();
            View decor=dialog.getWindow().getDecorView();decor.measure(View.MeasureSpec.makeMeasureSpec(640,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(560,View.MeasureSpec.AT_MOST));decor.layout(0,0,decor.getMeasuredWidth(),decor.getMeasuredHeight());
            List<View> contents=views(decor);
            for(View view:contents)if(view instanceof Button) {
                Button pick=(Button)view;assertTrue("大字号表情被固定行高裁掉",pick.getHeight()-pick.getCompoundPaddingTop()-pick.getCompoundPaddingBottom()>=pick.getLayout().getHeight());
                assertTrue("表情横向越界",pick.getLeft()>=0&&pick.getRight()<=((View)pick.getParent()).getWidth());
            }
            ScrollView scroll=(ScrollView)contents.stream().filter(v->v instanceof ScrollView).findFirst().orElseThrow();scroll.setSmoothScrollingEnabled(false);scroll.fullScroll(View.FOCUS_DOWN);shadowOf(Looper.getMainLooper()).idle();
            Button last=(Button)contents.stream().filter(v->v instanceof Button&&"🇨🇳".contentEquals(((Button)v).getText())).findFirst().orElseThrow();Rect visible=new Rect();assertTrue(last.getGlobalVisibleRect(visible));assertEquals(last.getHeight(),visible.height());
            Bitmap image=Bitmap.createBitmap(decor.getWidth(),decor.getHeight(),Bitmap.Config.ARGB_8888);decor.draw(new Canvas(image));try(OutputStream output=new FileOutputStream(new File(System.getProperty("feiq.previewDir"),"android-emoji-large-font-small-screen.png"))){assertTrue(image.compress(Bitmap.CompressFormat.PNG,100,output));}finally{image.recycle();}
        } finally {if(dialog!=null)dialog.dismiss();ac.pause().stop().destroy();sc.destroy();RuntimeEnvironment.setFontScale(1f);}
    }
}
