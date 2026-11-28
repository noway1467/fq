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
    private static int freeChatPort()throws IOException {
        for(int attempt=0;attempt<20;attempt++) {
            try(java.net.DatagramSocket udp=new java.net.DatagramSocket(0,java.net.InetAddress.getLoopbackAddress())) {
                try(java.net.ServerSocket tcp=new java.net.ServerSocket(udp.getLocalPort(),1,java.net.InetAddress.getLoopbackAddress())) {return udp.getLocalPort();}
                catch(java.net.BindException occupied){/* TCP和UDP必须同时空闲。 */}
            }
        }
        throw new IOException("未找到同时空闲的TCP/UDP测试端口");
    }
    @Test public void settingsOrderGridAndPinnedGlyphMatchDesktop() throws Exception {
        Context context=RuntimeEnvironment.getApplication();context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).commit();
        for(boolean dark:new boolean[]{false,true}) {
            context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putBoolean("follow_system_theme",false).commit();
            ServiceController<ChatService> service=Robolectric.buildService(ChatService.class).create();
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.get().onBind(new Intent()));
            try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
                MainActivity activity=controller.get();invoke(activity,"settings",new Class<?>[0]);Dialog dialog=ShadowDialog.getLatestDialog();
                try {
                    View decor=dialog.getWindow().getDecorView();LinearLayout general=decor.findViewWithTag("settings-page:0");
                    assertTrue(general.indexOfChild(general.findViewWithTag("section:个人资料"))<general.indexOfChild(general.findViewWithTag("section:连接")));
                    click(dialog,"外观");LinearLayout appearance=decor.findViewWithTag("settings-page:1");
                    TextView sample=(TextView)views(appearance).stream().filter(v->v instanceof TextView&&"今天也保持联系".contentEquals(((TextView)v).getText())).findFirst().orElseThrow();
                    View follow=views(appearance).stream().filter(v->"跟随系统切换".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();
                    assertTrue(appearance.indexOfChild((View)sample.getParent())<appearance.indexOfChild(follow));
                    for(int width:new int[]{720,560}) {
                        appearance.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(0,View.MeasureSpec.UNSPECIFIED));appearance.layout(0,0,width,appearance.getMeasuredHeight());
                        for(View view:views(appearance))if(view instanceof RadioGroup) {
                            RadioGroup grid=(RadioGroup)view;assertEquals(6,grid.getChildCount());assertEquals(grid.getChildAt(0).getTop(),grid.getChildAt(1).getTop());
                            for(int i=0;i<6;i++){View item=grid.getChildAt(i);assertTrue(item.getLeft()>=0&&item.getRight()<=width);assertTrue(item.getHeight()>=96);}
                            ((RadioButton)grid.getChildAt(4)).performClick();assertEquals(grid.getChildAt(4).getId(),grid.getCheckedRadioButtonId());
                        }
                    }
                    snapshot(decor,"settings-order-"+(dark?"dark":"light"));
                    ScrollView scroller=(ScrollView)views(decor).stream().filter(v->v instanceof ScrollView).findFirst().orElseThrow();
                    scroller.scrollTo(0,follow.getTop());snapshot(decor,"settings-grid-"+(dark?"dark":"light"));
                    click(dialog,"文件");snapshot(decor,"settings-files-"+(dark?"dark":"light"));
                    List<EditText> limits=new ArrayList<>();for(View view:views(decor.findViewWithTag("settings-page:2")))if(view instanceof EditText)limits.add((EditText)view);
                    assertEquals("5000",limits.get(0).getText().toString());assertEquals("10000",limits.get(1).getText().toString());
                    limits.get(0).setText("99");click(dialog,"保存");assertTrue(dialog.isShowing());
                    limits.get(0).setText("50000");limits.get(1).setText("20000");click(dialog,"保存");assertFalse(dialog.isShowing());
                    assertEquals(50000,SendFiles.batchLimit(activity));assertEquals(20000,activity.getSharedPreferences("settings",0).getInt("pending_file_limit",0));
                    activity.getSharedPreferences("settings",0).edit().remove("batch_file_limit").remove("pending_file_limit").commit();
                } finally {dialog.dismiss();}
            } finally {service.destroy();}
        }
    }
    @Test public void conversationRowKeepsFullTimeAndPinBeforeName() throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).commit();
        for(boolean dark:new boolean[]{false,true}) {
            context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putBoolean("follow_system_theme",false).commit();
            ServiceController<ChatService> service=Robolectric.buildService(ChatService.class).create();
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.get().onBind(new Intent()));
            try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
                MainActivity activity=controller.get();BaseAdapter adapter=(BaseAdapter)field(activity,"adapter");
                @SuppressWarnings("unchecked") List<ChatStore.Conversation> items=(List<ChatStore.Conversation>)field(adapter,"items");items.clear();
                ChatStore.Conversation contact=new ChatStore.Conversation();contact.peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:2425"),"pinned","host","PC 中文 English 很长的联系人名称","");contact.time=System.currentTimeMillis();items.add(contact);
                for(String preview:new String[]{"[文件] 飞Q-android-0.8.14.apk 很长的文件名 Very long file name.zip",""})
                for(boolean pinned:new boolean[]{false,true}) for(int unread:new int[]{0,3,100}) for(int width:new int[]{560,720}) {
                    contact.preview=preview;contact.pinned=pinned;contact.unread=unread;
                    LinearLayout row=(LinearLayout)adapter.getView(0,null,new ListView(activity));row.setBackgroundColor((Integer)field(activity,"SURFACE"));
                    row.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(0,View.MeasureSpec.UNSPECIFIED));row.layout(0,0,width,row.getMeasuredHeight());
                    LinearLayout center=(LinearLayout)row.getChildAt(1),right=(LinearLayout)row.getChildAt(2);
                    TextView name=(TextView)center.getChildAt(0),time=(TextView)right.getChildAt(0);
                    assertEquals(pinned,name.getCompoundDrawables()[0]!=null);
                    assertTrue("时间被挤出行外",right.getRight()<=width-row.getPaddingRight());assertTrue(center.getRight()<=right.getLeft());
                    assertEquals(1,time.getLineCount());assertEquals(0,time.getLayout().getEllipsisCount(0));
                    assertTrue("时间宽度不足",time.getWidth()>=Math.ceil(time.getPaint().measureText(time.getText().toString())));
                    assertEquals(unread>0?2:1,right.getChildCount());
                    if(pinned&&unread==3&&width==720&&!preview.isEmpty()) snapshot(row,"pinned-row-"+(dark?"dark":"light"));
                }
            } finally {service.destroy();}
        }
    }
    @Test public void archiveRetainsOlderRowsAcrossRestartAndPagesEqualTimesWithoutDuplicates() throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:2425"),"archive","host","历史","");
        try(ChatStore store=new ChatStore(context)) {
            store.peer(peer);
            store.getWritableDatabase().execSQL("WITH RECURSIVE sequence(packet) AS (VALUES(1) UNION ALL SELECT packet+1 FROM sequence WHERE packet<10005) INSERT INTO messages(peer,packet,outgoing,time,text,state,files) SELECT ?,packet,0,12,'分页','已收到','' FROM sequence",new Object[]{peer.id()});
            LanNode.Message latest=new LanNode.Message();latest.number=10006;latest.time=12;latest.text="最新";latest.state="已收到";store.message(peer,latest,false,true);
        }
        try(ChatStore store=new ChatStore(context)) {
            assertEquals(10006,android.database.DatabaseUtils.queryNumEntries(store.getReadableDatabase(),"messages"));
            LanNode.Message first=store.findMessage(peer.id(),1,false);assertNotNull(first);
            assertEquals(1,store.historyAround(peer.id(),first).get(0).number);
            ChatStore.HistoryPage recent=store.search(peer.id(),"分页",1,null,()->true);
            ChatStore.HistoryPage older=store.search(peer.id(),"分页",2,recent.index,()->true);
            assertEquals(100,recent.rows.size());assertEquals(100,older.rows.size());
            assertEquals(10005,recent.rows.get(99).number);assertEquals(9905,older.rows.get(99).number);
            assertEquals(101,recent.index.pageCount());assertEquals(10005,recent.index.total);
            ChatStore.HistoryPage last=store.search(peer.id(),"分页",101,recent.index,()->true);
            assertEquals(5,last.rows.size());assertEquals(1,last.rows.get(0).number);assertSame(recent.index,last.index);
            ChatStore.HistoryPage middle=store.search(peer.id(),"",37,null,()->true);
            assertEquals(10006,middle.index.total);assertTrue(middle.index.matches.isEmpty());assertEquals(6406,middle.rows.get(99).number);
            ChatStore.HistoryPage empty=store.search(peer.id(),"没有命中的关键词",1,null,()->true);assertEquals(0,empty.index.pageCount());assertTrue(empty.rows.isEmpty());
            store.deleteMessages(peer.id(),Collections.singletonList(first));assertTrue(store.historyAround(peer.id(),first).isEmpty());
        }
    }
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
    @Test public void legacyProtocolPreferencesAreIgnoredWithoutResettingProfile() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        SharedPreferences prefs=service.getSharedPreferences("settings",0);prefs.edit().putBoolean("auto_connect",false).putString("name","协议设置").putInt("legacy_protocol_policy",1).commit();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};
        try(ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).setup();LanNode node=new LanNode(24350,true,"settings","host","设置","test",quiet)) {
            MainActivity activity=ac.get();settle(service);Method apply=ChatService.class.getDeclaredMethod("applyProfile",LanNode.class,SharedPreferences.class);apply.setAccessible(true);
            for(boolean oldChoice:new boolean[]{false,true}) {
                prefs.edit().putBoolean("allow_legacy_protocol",oldChoice).commit();apply.invoke(null,node,prefs);assertEquals("协议设置",node.nickname);
                invoke(activity,"settings",new Class<?>[0]);Dialog dialog=ShadowDialog.getLatestDialog();
                assertFalse(views(dialog.getWindow().getDecorView()).stream().anyMatch(v->v instanceof Switch&&"允许旧版明文协议".contentEquals(((Switch)v).getText())));
                click(dialog,"保存");assertEquals(oldChoice,prefs.getBoolean("allow_legacy_protocol",!oldChoice));assertEquals("协议设置",prefs.getString("name",null));
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:2425"),"old","host","旧设备","");
                java.io.IOException error=assertThrows(java.io.IOException.class,()->node.sendMessage(peer,"test",Collections.emptyList()));assertTrue(error.getMessage().contains("升级至 0.8"));
            }
        } finally {sc.destroy();}
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
    @Test public void slowAvatarReadCannotConfirmEarlyOrReplaceLaterBuiltinChoice() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        Dialog settings=null,picker=null;CountDownLatch release=new CountDownLatch(1),waiting=new CountDownLatch(1);
        try {
            settle(service);invoke(activity,"settings",new Class<?>[0]);settings=ShadowDialog.getLatestDialog();click(settings,"更换头像");picker=ShadowDialog.getLatestDialog();
            ExecutorService loader=(ExecutorService)field(activity,"avatarLoader");
            loader.submit(()->{waiting.countDown();try{release.await(5,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}});
            assertTrue(waiting.await(3,TimeUnit.SECONDS));click(picker,"本地图片");shadowOf(activity).getNextStartedActivityForResult();
            Uri uri=Uri.parse("content://avatar.test/slow");Bitmap image=Bitmap.createBitmap(64,64,Bitmap.Config.ARGB_8888);image.eraseColor(Color.GREEN);
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();image.compress(Bitmap.CompressFormat.PNG,100,bytes);image.recycle();
            shadowOf(activity.getContentResolver()).registerInputStream(uri,new ByteArrayInputStream(bytes.toByteArray()));
            invoke(activity,"onActivityResult",new Class<?>[]{int.class,int.class,Intent.class},25,Activity.RESULT_OK,new Intent().setData(uri));
            Button confirm=(Button)picker.getWindow().getDecorView().findViewWithTag("sheet-primary");
            assertFalse("图片未读完不能确认旧头像",confirm.isEnabled());
            View builtin=views(picker.getWindow().getDecorView()).stream().filter(v->"builtin:15".equals(v.getTag())).findFirst().orElseThrow();builtin.performClick();
            assertTrue("后选内置头像应立即可用",confirm.isEnabled());
            release.countDown();loader.submit(()->{}).get(5,TimeUnit.SECONDS);shadowOf(Looper.getMainLooper()).idle();
            assertEquals("较慢的本地图覆盖了后选头像","builtin:15",field(field(activity,"avatarSelection"),"value"));
            click(picker,"使用此头像");click(settings,"保存");
            assertEquals("builtin:15",activity.getSharedPreferences("settings",0).getString("avatar",""));
        } finally {release.countDown();if(picker!=null)picker.dismiss();if(settings!=null)settings.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void storeDeletesPersistWithoutRemovingAttachmentsOrOtherConversations() throws Exception {
        Context context=RuntimeEnvironment.getApplication();LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"peer","host","联系人","部门"),other=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32426"),"other","host","其他","部门");
        File file=new File(context.getFilesDir(),"保留的文件.txt");try(OutputStream output=new FileOutputStream(file)){output.write(7);}new DraftStore(context).save(peer.id(),"保留草稿");
        LanNode.Message incoming=new LanNode.Message();incoming.number=1;incoming.text="入站";incoming.time=1;incoming.state="已收到";
        LanNode.Message outgoing=new LanNode.Message();outgoing.number=1;outgoing.text="出站";outgoing.time=2;outgoing.outgoing=true;outgoing.state="已送达";outgoing.localPaths.add(file.getPath());
        try(ChatStore store=new ChatStore(context)) {
            store.message(peer,incoming,false,false);store.message(peer,outgoing,false,false);store.message(other,incoming,false,false);
            String received=ChatService.fileKey(peer.id(),1,0,false),sent=ChatService.fileKey(peer.id(),1,0,true);
            store.transfer(received,peer.id(),1,0,"本机文件已删除");store.transfer(sent,peer.id(),1,100,"对方已校验保存");
            store.deleteMessages(peer.id(),Collections.singletonList(incoming));assertEquals("出站",store.history(peer.id()).get(0).text);assertFalse(store.message(peer,incoming,false,false));
            assertNull(store.transfer(received));assertEquals("对方已校验保存",store.transfer(sent).state);
            store.transfer(received,peer.id(),1,100,"已保存");assertNull("迟到进度重新登记了已删消息",store.transfer(received));
            store.deleteMessages(peer.id(),Collections.singletonList(outgoing));
        }
        try(ChatStore store=new ChatStore(context)) {assertFalse(store.message(peer,incoming,false,false));assertFalse(store.message(peer,outgoing,true,false));assertTrue(store.history(peer.id()).isEmpty());assertEquals(1,store.history(other.id()).size());assertTrue(file.exists());assertEquals("保留草稿",new DraftStore(context).read(peer.id()));}
    }
    @Test public void clearedConversationKeepsMessageIdentitiesAcrossRestart()throws Exception {
        Context context=RuntimeEnvironment.getApplication();LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"clear","host","清空","");
        LanNode.Message old=new LanNode.Message();old.number=1;old.text="要清空的正文";old.state="已收到";
        try(ChatStore store=new ChatStore(context)){store.message(peer,old,false,false);store.remove(peer.id(),true,false);assertTrue(store.history(peer.id()).isEmpty());}
        try(ChatStore store=new ChatStore(context)){
            assertFalse(store.message(peer,old,false,false));store.remove(peer.id(),true,false);assertFalse(store.message(peer,old,false,false));
            old.number=2;assertTrue(store.message(peer,old,false,false));assertEquals(1,store.history(peer.id()).size());
        }
    }
    @Test public void sentAttachmentRecordCanBeDeletedWhileOriginalAndOfferRemain() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        try(java.net.DatagramSocket remote=new java.net.DatagramSocket(0,java.net.InetAddress.getLoopbackAddress())) {
            int number=TestPorts.chat();
            try(LanNode node=new LanNode(number,true,"delete-sender","host","发送方","",new LanNode.Listener(){public void peersChanged(){}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean u){}})) {
                settle(service);org.feiqlight.android.core.TlsFilePeer.enable(node);node.start();Field current=ChatService.class.getDeclaredField("node");current.setAccessible(true);current.set(service,node);
                File original=new File(service.getFilesDir(),"保留的发送原文件.txt");java.nio.file.Files.write(original.toPath(),new byte[]{1,2,3});
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+remote.getLocalPort()),"receiver","host","接收方","");peer.utf8=true;peer.deviceId="1".repeat(32);peer.fingerprint="b".repeat(64);
                LanNode.Message sent=node.sendMessage(peer,"发送附件",Collections.singletonList(original));sent.state="已送达";service.incoming(peer,sent,false);settle(service);
                assertTrue(node.offering(peer.id(),sent.number,0));List<Boolean> result=new ArrayList<>();service.deleteMessages(peer.id(),Collections.singletonList(sent),result::add);settle(service);
                assertEquals(Collections.singletonList(true),result);assertTrue(original.isFile());assertTrue("删除本机消息不能破坏对方接收权限",node.offering(peer.id(),sent.number,0));
                ChatStore store=(ChatStore)field(service,"store");assertTrue(store.history(peer.id()).isEmpty());service.incoming(peer,sent,true);settle(service);assertTrue(store.history(peer.id()).isEmpty());
            }
        } finally {sc.destroy();}
    }
    @Test public void unavailableHistoricalAttachmentShowsCardAndClickExplanationWithoutStartupToast() throws Exception {
        for(boolean dark:new boolean[]{false,true}) {
            Context context=RuntimeEnvironment.getApplication();context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).putBoolean("auto_connect",false).commit();
            ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).setup();MainActivity activity=ac.get();
            try {
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"missing","host","历史附件","");LanNode.Message message=new LanNode.Message();message.number=10;message.time=1;message.outgoing=true;message.state="已送达";message.text="文件已失效";message.files=Collections.singletonList(new Protocol.Attachment(0,"历史文件.txt",3,0));message.localPaths=Collections.singletonList("uri:content://expired/document/missing");
                service.incoming(peer,message,false);settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
                assertTrue(views((View)field(activity,"messages")).stream().filter(v->v instanceof TextView).anyMatch(v->((TextView)v).getText().toString().contains("文件已删除或不可用")));
                snapshot(activity.getWindow().getDecorView(),"android-unavailable-file-"+(dark?"dark":"light"));
                View attachment=views((View)field(activity,"messages")).stream().filter(v->"历史文件.txt，文件已删除或不可用".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();attachment.performClick();
                Dialog dialog=ShadowDialog.getLatestDialog();assertTrue(views(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof TextView).anyMatch(v->((TextView)v).getText().toString().contains("消息记录仍可删除")));dialog.dismiss();
            } finally {ac.pause().stop().destroy();sc.destroy();}
        }
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
    @SuppressWarnings("unchecked")
    @Test public void multiSelectionDeletesOnlyReceivedFilesAndReportsDeletedSkips() throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).putBoolean(ReceivedStorage.AUTO,false).commit();
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        try {
            settle(service);
            for(boolean dark:new boolean[]{false,true}) {
                context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"batch-files-"+dark,"host","中文 English 文件联系人","");
                Protocol.Attachment file=new Protocol.Attachment(0,"已接收的中文 English 资料.txt",3,1);
                LanNode.Message received=new LanNode.Message();received.number=1;received.time=1;received.text="已保存的接收文件";received.state="已收到";received.files.add(file);
                LanNode.Message deleted=received.copy();deleted.number=2;deleted.time=2;deleted.text="此前删除的文件";deleted.state="本机文件已删除";
                LanNode.Message outgoing=received.copy();outgoing.outgoing=true;outgoing.time=3;outgoing.text="发送方原文件必须保留";outgoing.state="已送达";
                File original=new File(service.getFilesDir(),"offers/original-"+dark+".txt");java.nio.file.Files.write(original.toPath(),new byte[]{4,5,6});outgoing.localPaths=Collections.singletonList(original.getAbsolutePath());
                LanNode.Message pending=received.copy();pending.number=4;pending.time=4;pending.text="未接收的文件邀请";pending.files=Collections.singletonList(new Protocol.Attachment(0,"未接收.bin",8192,1));
                File local=service.receivedFile(peer,received.number,file);java.nio.file.Files.write(local.toPath(),new byte[]{1,2,3});
                File partial=LanNode.partialFile(peer,pending.number,pending.files.get(0),service.receivedFile(peer,pending.number,pending.files.get(0)));java.nio.file.Files.write(partial.toPath(),new byte[128]);
                for(LanNode.Message message:Arrays.asList(received,deleted,outgoing,pending))service.incoming(peer,message,false);
                settle(service);
                try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
                    MainActivity activity=activityController.get();settle(service);invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
                    invoke(activity,"beginMessageSelection",new Class<?>[]{LanNode.Message.class},outgoing);
                    Button deleteFiles=(Button)field(activity,"deleteSelectedFiles");assertFalse("不能通过聊天多选删除发送方原文件",deleteFiles.isEnabled());
                    invoke(activity,"requestDeleteSelectedFiles",new Class<?>[0]);assertTrue(ShadowToast.getTextOfLatestToast().contains("所选消息没有接收附件"));
                    LinearLayout bar=(LinearLayout)field(activity,"messageSelectionBar");
                    views(bar).stream().filter(view->view instanceof Button&&"全选".contentEquals(((Button)view).getText())).findFirst().orElseThrow().performClick();
                    assertEquals(4,((Set<?>)field(activity,"messageMarks")).size());assertTrue(deleteFiles.isEnabled());
                    List<String> labels=new ArrayList<>();for(View view:views(bar))if(view instanceof Button)labels.add(((Button)view).getText().toString());
                    assertEquals(Arrays.asList("全选","删除文件","删除","取消"),labels);
                    View root=(View)field(activity,"root");
                    for(int width:new int[]{720,786}) {
                        root.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1500,View.MeasureSpec.EXACTLY));root.layout(0,0,width,1500);
                        TextView count=(TextView)field(activity,"messageSelectionCount");assertNotNull(count.getLayout());assertEquals("360dp–393dp 多选计数不应被按钮挤掉",0,count.getLayout().getEllipsisCount(0));
                        assertTrue(bar.getChildAt(4).getRight()<=bar.getWidth()-bar.getPaddingRight());
                    }
                    snapshot(activity.getWindow().getDecorView(),"android-multi-file-delete-"+(dark?"dark":"light"));
                    deleteFiles.performClick();click(ShadowDialog.getLatestDialog(),"删除文件");
                    ((ExecutorService)field(service,"transferWorker")).submit(()->{}).get(5,TimeUnit.SECONDS);settle(service);
                    assertFalse(local.exists());assertTrue(original.isFile());assertTrue(partial.isFile());assertEquals(4,((List<?>)field(activity,"messageRows")).size());assertFalse((Boolean)field(activity,"selectingMessages"));
                    assertEquals("已删除 1 个本机文件，1 个已删除跳过。（另有 1 个未接收附件未处理）",ShadowToast.getTextOfLatestToast());
                    assertEquals("本机文件已删除",service.progress(peer.id(),received.number,file.id,false).state);
                    assertEquals("本机文件已删除",service.progress(peer.id(),deleted.number,file.id,false).state);
                    ChatStore store=(ChatStore)field(service,"store");assertNotNull(store.findMessage(peer.id(),outgoing.number,true));assertEquals(1,store.findMessage(peer.id(),pending.number,false).files.size());assertEquals(4,store.history(peer.id()).size());
                    assertTrue(((Map<String,?>)field(service,"waiting")).containsKey(service.receivedFile(peer,pending.number,pending.files.get(0)).getName()));
                    invoke(activity,"beginMessageSelection",new Class<?>[]{LanNode.Message.class},pending);deleteFiles.performClick();click(ShadowDialog.getLatestDialog(),"删除文件");
                    ((ExecutorService)field(service,"transferWorker")).submit(()->{}).get(5,TimeUnit.SECONDS);settle(service);
                    assertEquals("所选消息没有已保存的本机文件（未接收附件请在传输任务中处理）",ShadowToast.getTextOfLatestToast());assertTrue(partial.isFile());assertTrue(original.isFile());
                }
            }
        } finally {serviceController.destroy();}
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
            invoke(service,"fileProgress",signature,peer.id(),1L,0L,true,42,"发送中",1572864.0,null);activity.transfersChanged();shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(250));
            View container=(View)field(activity,"messages");assertTrue(views(container).stream().anyMatch(v->v instanceof TextView&&((TextView)v).getText().toString().contains("42% · 1.5 MB/s")));
            ScrollView scroll=(ScrollView)field(activity,"scroll");int previous=scroll.getScrollY();snapshot(activity.getWindow().getDecorView(),"android-transfer-rate");
            invoke(service,"fileProgress",signature,peer.id(),1L,0L,true,100,"已发送（非保存确认）",0.0,null);activity.transfersChanged();shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(250));assertEquals(previous,scroll.getScrollY());assertFalse(views(container).stream().anyMatch(v->v instanceof TextView&&((TextView)v).getText().toString().contains("MB/s")));assertEquals(0,service.progress(peer.id(),1,0,true).bytesPerSecond,0);
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void contactAvatarOpensCenteredProfileAnd128PreviewWithLocalNickname() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();Dialog profile=null,preview=null,selfSettings=null;
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
            View incomingAvatar=views((View)field(activity,"messages")).stream().filter(v->v.getContentDescription()!=null&&v.getContentDescription().toString().endsWith("的资料")&&!"我的资料".contentEquals(v.getContentDescription())).findFirst().orElseThrow();
            incomingAvatar.performClick();profile=ShadowDialog.getLatestDialog();assertTrue(views(profile.getWindow().getDecorView()).stream().anyMatch(v->"联系人资料头像，点击查看大图".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())));profile.dismiss();
            LanNode.Message sent=new LanNode.Message();sent.number=2;sent.text="我的消息";sent.time=2;sent.state="已送达";sent.outgoing=true;service.incoming(peer,sent,false);settle(service);activity.changed();
            View outgoingAvatar=views((View)field(activity,"messages")).stream().filter(v->"我的资料".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();
            outgoingAvatar.performClick();selfSettings=ShadowDialog.getLatestDialog();assertTrue(views(selfSettings.getWindow().getDecorView()).stream().anyMatch(v->v instanceof Button&&"更换头像".contentEquals(((Button)v).getText())));selfSettings.dismiss();
            ((View)field(activity,"chatAvatar")).performClick();profile=ShadowDialog.getLatestDialog();EditText nickname=(EditText)views(profile.getWindow().getDecorView()).stream().filter(v->v instanceof EditText).findFirst().orElseThrow();nickname.setText("旧窗口修改");invoke(activity,"open",new Class<?>[]{String.class},"other-peer");click(profile,"保存");settle(service);assertEquals("本机昵称 English",service.conversations().get(0).note);
        } finally {if(preview!=null)preview.dismiss();if(profile!=null)profile.dismiss();if(selfSettings!=null)selfSettings.dismiss();ac.pause().stop().destroy();sc.destroy();}
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
    @Test public void messageBubblesClearOppositeAvatarColumnAndNicknameUpdatesLive() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        Dialog settings=null;
        int remotePort,localPort;
        // 节点同时绑定TCP和UDP，只检查TCP空闲可能随机撞上宿主的UDP端口。
        remotePort=freeChatPort();do {localPort=freeChatPort();}while(localPort==remotePort);
        final int selectedLocalPort=localPort;
        try(LanNode remote=new LanNode(remotePort,true,"remote","test","远端","",new LanNode.Listener(){public void peersChanged(){}public void message(LanNode.Peer p,LanNode.Message m,boolean u){}public void error(String t){}})) {
            remote.start();settle(service);
            service.getSharedPreferences("settings",0).edit().putInt("port",localPort).putString("name","旧手机昵称").commit();
            service.connectionFactory=(prefs,listener)->new LanNode(selectedLocalPort,true,"local-test","test",prefs.getString("name","Android"),prefs.getString("group","我的局域网"),listener);
            service.onStartCommand(new Intent(),0,1);settle(service);
            LanNode local=(LanNode)field(service,"node");assertNotNull(local);
            local.probe(LanNode.endpoint("127.0.0.1:"+remotePort));
            long wait=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
            while(System.nanoTime()<wait&&(remote.peers().isEmpty()||local.peers().isEmpty()))Thread.sleep(20);
            assertEquals("旧手机昵称",remote.peers().get(0).name);
            LanNode.Peer peer=local.peers().get(0);
            LanNode.Message incoming=new LanNode.Message();incoming.number=1;incoming.time=1;incoming.state="已收到";incoming.text="这是一条很长的接收消息用于检查气泡不会越过右侧对方头像所在区域并且保持合适边距";
            LanNode.Message outgoing=new LanNode.Message();outgoing.number=2;outgoing.time=2;outgoing.outgoing=true;outgoing.state="已送达";outgoing.text="这是一条很长的发送消息用于检查气泡不会越过左侧对方头像所在区域并且保持合适边距";
            service.incoming(peer,incoming,false);service.incoming(peer,outgoing,false);settle(service);
            invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
            View decor=activity.getWindow().getDecorView();decor.measure(View.MeasureSpec.makeMeasureSpec(720,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1280,View.MeasureSpec.EXACTLY));decor.layout(0,0,720,1280);
            LinearLayout messages=(LinearLayout)field(activity,"messages");
            assertTrue("两侧头像应更贴边",messages.getPaddingLeft()<=Math.round(6*activity.getResources().getDisplayMetrics().density));
            View inRow=views(messages).stream().filter(v->"消息：这是一条很长的接收消息用于检查气泡不会越过右侧对方头像所在区域并且保持合适边距".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();
            View outRow=views(messages).stream().filter(v->"消息：这是一条很长的发送消息用于检查气泡不会越过左侧对方头像所在区域并且保持合适边距".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();
            ViewGroup inParent=(ViewGroup)inRow.getParent(),outParent=(ViewGroup)outRow.getParent();
            View inAvatar=inParent.getChildAt(0),outAvatar=outParent.getChildAt(1);
            assertTrue("接收气泡右缘不应超过对方头像左缘: "+inRow.getRight()+" vs "+outAvatar.getLeft(),inRow.getRight()<=outAvatar.getLeft());
            assertTrue("发出气泡左缘不应超过对方头像右缘: "+outRow.getLeft()+" vs "+inAvatar.getRight(),outRow.getLeft()>=inAvatar.getRight());
            snapshot(decor,"android-bubble-width-and-avatar");
            invoke(activity,"settings",new Class<?>[0]);settings=ShadowDialog.getLatestDialog();
            EditText nameField=views(settings.getWindow().getDecorView()).stream().filter(v->v instanceof EditText&&"昵称".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).map(v->(EditText)v).findFirst().orElseThrow();
            nameField.setText("实时新昵称");click(settings,"保存");settle(service);
            long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);
            while(System.nanoTime()<deadline&&!"实时新昵称".equals(remote.peers().get(0).name))Thread.sleep(20);
            assertEquals("实时新昵称",remote.peers().get(0).name);
        } finally {if(settings!=null)settings.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    private static void writeLegacyOffers(File root,File original,LanNode.Peer target,LanNode.Peer other)throws Exception {
        assertTrue(root.isDirectory()||root.mkdirs());
        try(DataOutputStream output=new DataOutputStream(new FileOutputStream(new File(root,"transfer-offers.dat")))){
            output.writeInt(2);output.writeInt(3);
            for(int index=0;index<3;index++){
                LanNode.Peer peer=index==2?other:target;
                output.writeLong(10+index);output.writeInt(0);output.writeUTF(original.getCanonicalPath());output.writeLong(original.length());output.writeLong(original.lastModified());output.writeLong(System.currentTimeMillis()+86400000);
                output.writeUTF(peer.deviceId);output.writeUTF(peer.fingerprint);output.writeUTF(SecureIdentity.hashFile(original));output.writeUTF(peer.endpoint.getAddress().getHostAddress()+":"+peer.endpoint.getPort());
                output.writeUTF(peer.login);output.writeUTF(peer.host);output.writeUTF(peer.name);output.writeUTF(peer.group);output.writeUTF(peer.id());
            }
        }
    }
    private static List<Long> storedPackets(File root)throws Exception {
        List<Long> packets=new ArrayList<>();
        try(DataInputStream input=new DataInputStream(new FileInputStream(new File(root,"transfer-offers.dat")))){
            assertEquals(2,input.readInt());int count=input.readInt();
            for(int index=0;index<count;index++){packets.add(input.readLong());input.readInt();input.readUTF();input.readLong();input.readLong();input.readLong();for(int field=0;field<9;field++)input.readUTF();}
        }
        return packets;
    }
    @Test public void disconnectedDeleteRevokesOffers()throws Exception {
        for(boolean clear:new boolean[]{false,true}){
            ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
            try {
                settle(service);assertNull(field(service,"node"));
                File original=new File(service.getFilesDir(),"离线原文件.bin");java.nio.file.Files.write(original.toPath(),new byte[]{1,2,3});File security=new File(service.getFilesDir(),"security");
                LanNode.Peer target=new LanNode.Peer(LanNode.endpoint("127.0.0.1:34251"),"target","host","target","");target.deviceId=clear?"44444444444444444444444444444444":"11111111111111111111111111111111";target.fingerprint="a".repeat(64);
                LanNode.Peer other=new LanNode.Peer(LanNode.endpoint("127.0.0.1:34252"),"other","host","other","");other.deviceId="22222222222222222222222222222222";other.fingerprint="b".repeat(64);
                writeLegacyOffers(security,original,target,other);
                LanNode.Message pending=new LanNode.Message();pending.number=10;pending.time=1;pending.outgoing=true;pending.state="上次退出，未确认送达";pending.text="离线附件";pending.files.add(new Protocol.Attachment(0,original.getName(),3,0));
                LanNode.Message delivered=pending.copy();delivered.number=11;delivered.state="已送达";
                assertTrue("未连接时删除未送达附件必须提示撤销邀请",service.hasUndeliveredOutgoing(target.id(),pending));
                for(String state:new String[]{"等待确认","发送失败","网络已切换，未确认送达"}){LanNode.Message unconfirmed=pending.copy();unconfirmed.state=state;assertTrue(service.hasUndeliveredOutgoing(target.id(),unconfirmed));}
                assertFalse(service.hasUndeliveredOutgoing(target.id(),delivered));
                LanNode.Message completed=pending.copy();completed.state="对方已校验保存";assertFalse(service.hasUndeliveredOutgoing(target.id(),completed));
                LanNode.Message text=pending.copy();text.files.clear();assertFalse(service.hasUndeliveredOutgoing(target.id(),text));
                LanNode.Message incoming=pending.copy();incoming.outgoing=false;assertFalse(service.hasUndeliveredOutgoing(target.id(),incoming));
                assertFalse(service.hasUndeliveredOutgoing(target.id(),null));
                service.incoming(target,pending,false);service.incoming(target,delivered,false);settle(service);
                assertNotNull("测试用未送达消息必须已入库",((ChatStore)field(service,"store")).findMessage(target.id(),10,true));
                List<Boolean> results=new ArrayList<>();
                if(clear)service.removeConversation(target.id(),true,false,results::add);else service.deleteMessages(target.id(),Arrays.asList(pending,delivered),results::add);
                settle(service);assertEquals(Collections.singletonList(true),results);assertTrue(((ChatStore)field(service,"store")).history(target.id()).isEmpty());
                assertEquals("只撤销未送达报价，保留已送达和其他会话",Arrays.asList(11L,12L),storedPackets(security));assertTrue(original.isFile());
                SecureIdentity identity=new SecureIdentity(security);identity.confirmation=(name,code)->true;identity.ensureTrusted(target.deviceId,target.fingerprint,target.name);identity.ensureTrusted(other.deviceId,other.fingerprint,other.name);
                try(LanNode restored=new LanNode(34250,true,"restored","host","restored","",new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}})){
                    restored.configureSecurity(security,identity,(name,code)->true);assertFalse(restored.offering(target.id(),10,0));assertTrue(restored.offering(target.id(),11,0));assertTrue(restored.offering(other.id(),12,0));
                }
            } finally {controller.destroy();}
        }
    }
    @Test public void canceledOfferStatesAreFiltered()throws Exception {
        Context context=RuntimeEnvironment.getApplication();File root=new File(context.getFilesDir(),"filtered-offers");File original=new File(context.getFilesDir(),"状态尾段.bin");java.nio.file.Files.write(original.toPath(),new byte[]{1,2,3});
        LanNode.Peer target=new LanNode.Peer(LanNode.endpoint("127.0.0.1:34251"),"target","host","target","");target.deviceId="1".repeat(32);target.fingerprint="a".repeat(64);
        LanNode.Peer other=new LanNode.Peer(LanNode.endpoint("127.0.0.1:34252"),"other","host","other","");other.deviceId="2".repeat(32);other.fingerprint="b".repeat(64);
        writeLegacyOffers(root,original,target,other);
        File saved=new File(root,"transfer-offers.dat");
        try(DataOutputStream output=new DataOutputStream(new FileOutputStream(saved,true))){
            output.writeInt(0x46515331);output.writeInt(4);
            for(int index=0;index<4;index++){output.writeLong(10+index);output.writeInt(0);output.writeUTF(index==2?"对方已校验保存":"等待对方接收");output.writeInt(index==2?100:20+index);output.writeUTF(index==1?"已送达":"未确认送达");}
        }
        LanNode.cancelStoredOffers(root,target.id(),Arrays.asList(10L,11L));
        assertEquals(Arrays.asList(11L,12L),storedPackets(root));
        try(DataInputStream input=new DataInputStream(new FileInputStream(saved))){
            assertEquals(2,input.readInt());assertEquals(2,input.readInt());
            for(int index=0;index<2;index++){input.readLong();input.readInt();input.readUTF();input.readLong();input.readLong();input.readLong();for(int field=0;field<9;field++)input.readUTF();}
            assertEquals(0x46515331,input.readInt());assertEquals("只保留现存报价的状态",2,input.readInt());
            for(int index=1;index<=2;index++){assertEquals(10+index,input.readLong());assertEquals(0,input.readInt());assertEquals(index==2?"对方已校验保存":"等待对方接收",input.readUTF());assertEquals(index==2?100:21,input.readInt());assertEquals(index==1?"已送达":"未确认送达",input.readUTF());}
            assertEquals("不存在已删除报价或旧孤儿状态",-1,input.read());
        }
    }
    @Test public void legacyCompletionMigrates()throws Exception {
        Context context=RuntimeEnvironment.getApplication();File security=new File(context.getFilesDir(),"legacy-completion");File original=new File(context.getFilesDir(),"完成文件.bin");java.nio.file.Files.write(original.toPath(),new byte[]{1,2,3});
        LanNode.Peer target=new LanNode.Peer(LanNode.endpoint("127.0.0.1:34251"),"target","host","target","");target.deviceId="33333333333333333333333333333333";target.fingerprint="c".repeat(64);
        writeLegacyOffers(security,original,target,target);SecureIdentity identity=new SecureIdentity(security);identity.confirmation=(name,code)->true;identity.ensureTrusted(target.deviceId,target.fingerprint,target.name);
        LanNode.Listener events=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};
        try(LanNode migrated=new LanNode(34250,true,"migrated","host","migrated","",events)){
            migrated.configureSecurity(security,identity,(name,code)->true);assertTrue(migrated.canCancelOutgoing(target.id(),10));
            migrated.restoreOutgoingStates(Collections.singletonMap(ChatService.fileKey(target.id(),10,0,true),"对方已校验保存"));
            assertFalse(migrated.canCancelOutgoing(target.id(),10));
            Object delivered=((Map<?,?>)field(migrated,"offers")).get("11:0");Field deliveredState=delivered.getClass().getDeclaredField("storedMessageState");deliveredState.setAccessible(true);deliveredState.set(delivered,"已送达");
            migrated.restoreOutgoingStates(Collections.singletonMap(ChatService.fileKey(target.id(),11,0,true),"等待对方接收"));
        }
        LanNode.cancelStoredOffers(security,target.id(),Arrays.asList(10L,11L));
        try(LanNode restored=new LanNode(34250,true,"restored","host","restored","",events)){
            restored.configureSecurity(security,(name,code)->true);assertTrue(restored.offering(target.id(),10,0));assertFalse(restored.canCancelOutgoing(target.id(),10));assertTrue(restored.offering(target.id(),11,0));assertTrue(restored.canCancelOutgoing(target.id(),11));
        }
    }
    @Test public void offlineOutgoingFileCancelAndDelete() throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible();MainActivity activity=ac.get();
        try(java.net.DatagramSocket offlinePeer=new java.net.DatagramSocket(0,java.net.InetAddress.getLoopbackAddress())) {
            int localPort=TestPorts.chat();
            try(LanNode node=new LanNode(localPort,true,"android-cancel","host","本机","",new LanNode.Listener(){
                public void peersChanged(){}
                public void error(String text){}
                public void message(LanNode.Peer peer,LanNode.Message message,boolean update){service.incoming(peer,message,update);}
                public void fileProgress(LanNode.Peer peer,long packet,long file,int percent,String state,double bytesPerSecond){
                    try{Class<?>[] sig={String.class,long.class,long.class,boolean.class,int.class,String.class,double.class,LanNode.Transfer.class};invoke(service,"fileProgress",sig,peer.id(),packet,file,true,percent,state,bytesPerSecond,null);}catch(Exception ignored){}
                }
            })) {
                settle(service);org.feiqlight.android.core.TlsFilePeer.enable(node);node.start();Field current=ChatService.class.getDeclaredField("node");current.setAccessible(true);current.set(service,node);
                File f1=new File(service.getFilesDir(),"离线菜单取消.txt"), f2=new File(service.getFilesDir(),"离线直接删除.txt");
                java.nio.file.Files.write(f1.toPath(),new byte[]{1,2,3,4});java.nio.file.Files.write(f2.toPath(),new byte[]{5,6,7,8});
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+offlinePeer.getLocalPort()),"offline-peer","host","离线联系人","");peer.utf8=true;peer.deviceId="1".repeat(32);peer.fingerprint="b".repeat(64);
                LanNode.Message m1=node.sendMessage(peer,"离线菜单取消",Collections.singletonList(f1));settle(service);
                invoke(activity,"open",new Class<?>[]{String.class},peer.id());settle(service);
                assertTrue(service.canCancelOutgoing(peer.id(),m1.number));
                // 检查消息菜单和已发文件卡片都暴露“取消发送”
                View bubble=views((View)field(activity,"messages")).stream().filter(v->"消息：离线菜单取消".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow();
                messageMenu(bubble);Dialog menu=ShadowDialog.getLatestDialog();
                assertTrue("消息菜单缺少取消发送",views(menu.getWindow().getDecorView()).stream().anyMatch(v->v instanceof Button&&"取消发送".contentEquals(((Button)v).getText())));
                click(menu,"取消发送");click(ShadowDialog.getLatestDialog(),"取消发送");settle(service);
                assertFalse("取消后仍可取消",service.canCancelOutgoing(peer.id(),m1.number));
                assertFalse("取消后报价未撤销",node.offering(peer.id(),m1.number,0));
                assertTrue("取消不能删除本机原文件",f1.isFile());
                ChatStore store=(ChatStore)field(service,"store");
                assertEquals("已取消发送",store.findMessage(peer.id(),m1.number,true).state);
                assertEquals("已取消发送",service.progress(peer.id(),m1.number,0,true).state);

                // 测试离线未送达消息直接删除同步撤销报价，且同包号入站进度不被误清
                LanNode.Message m2=node.sendMessage(peer,"离线直删",Collections.singletonList(f2));settle(service);
                Class<?>[] sig={String.class,long.class,long.class,boolean.class,int.class,String.class,double.class,LanNode.Transfer.class};
                invoke(service,"fileProgress",sig,peer.id(),m2.number,0L,false,55,"已保存",0.0,null);
                assertTrue(node.offering(peer.id(),m2.number,0));
                List<Boolean> deleted=new ArrayList<>();service.deleteMessages(peer.id(),Collections.singletonList(m2),deleted::add);settle(service);
                assertEquals(Collections.singletonList(true),deleted);
                assertFalse("直接删除未送达离线消息未撤销报价",node.offering(peer.id(),m2.number,0));
                assertTrue("直接删除不能删除本机原文件",f2.isFile());
                assertNotNull("删除出站消息误清了同包号入站文件进度",service.progress(peer.id(),m2.number,0,false));
                assertEquals("已保存",service.progress(peer.id(),m2.number,0,false).state);
            }
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
}
