package org.feiqlight.android;

import android.app.Dialog;
import android.content.*;
import android.graphics.*;
import android.view.*;
import android.widget.*;
import org.feiqlight.android.core.*;
import org.json.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.android.controller.*;
import org.robolectric.shadows.ShadowDialog;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w393dp-h852dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class TransferFeatureTest {
    public static final class GalleryProvider extends ContentProvider {
        File file;ContentValues values;boolean published,deleted;
        public boolean onCreate(){return true;}
        public android.net.Uri insert(android.net.Uri uri,ContentValues values){this.values=new ContentValues(values);return android.net.Uri.withAppendedPath(uri,"1");}
        public int update(android.net.Uri uri,ContentValues values,String selection,String[] args){published=values.getAsInteger(android.provider.MediaStore.MediaColumns.IS_PENDING)==0;return 1;}
        public int delete(android.net.Uri uri,String selection,String[] args){deleted=true;file.delete();return 1;}
        public android.os.ParcelFileDescriptor openFile(android.net.Uri uri,String mode)throws FileNotFoundException{return android.os.ParcelFileDescriptor.open(file,android.os.ParcelFileDescriptor.MODE_CREATE|android.os.ParcelFileDescriptor.MODE_TRUNCATE|android.os.ParcelFileDescriptor.MODE_WRITE_ONLY);}
        public android.database.Cursor query(android.net.Uri uri,String[] projection,String selection,String[] args,String sort){return null;}
        public String getType(android.net.Uri uri){return "image/png";}
    }
    @Test public void galleryPublishesCompleteBytesAndCleansCanceledCopy()throws Exception {
        Context context=RuntimeEnvironment.getApplication();GalleryProvider provider=new GalleryProvider();android.content.pm.ProviderInfo info=new android.content.pm.ProviderInfo();info.authority="media";provider.attachInfo(context,info);org.robolectric.shadows.ShadowContentResolver.registerProviderInternal("media",provider);
        File source=new File(context.getFilesDir(),"gallery-source.png");byte[] bytes={1,2,3,4};Files.write(source.toPath(),bytes);provider.file=new File(context.getFilesDir(),"gallery-copy.png");ReceivedStorage storage=new ReceivedStorage(context);
        storage.gallery(source,"照片.png",new LanNode.Transfer());assertTrue(provider.published);assertEquals("Pictures/飞Q",provider.values.getAsString(android.provider.MediaStore.MediaColumns.RELATIVE_PATH));assertArrayEquals(bytes,Files.readAllBytes(provider.file.toPath()));assertTrue(source.exists());
        provider.published=false;LanNode.Transfer canceled=new LanNode.Transfer();canceled.close();try{storage.gallery(source,"取消.png",canceled);fail("取消任务仍保存");}catch(IOException expected){}
        assertTrue(provider.deleted);assertFalse(provider.published);assertFalse(provider.file.exists());assertArrayEquals(bytes,Files.readAllBytes(source.toPath()));
    }
    private static final class Reply {
        final int code;final byte[] body;
        Reply(int code,byte[] body){this.code=code;this.body=body;}
        JSONObject json()throws JSONException{return new JSONObject(new String(body,StandardCharsets.UTF_8));}
    }
    private static Reply request(GuestTransfer server,String path,String token,String method,byte[] body,String origin)throws Exception {
        HttpURLConnection connection=(HttpURLConnection)new URL("http://127.0.0.1:"+server.port()+path).openConnection(Proxy.NO_PROXY);
        connection.setConnectTimeout(5000);connection.setReadTimeout(10000);connection.setInstanceFollowRedirects(false);connection.setRequestMethod(method);
        if(token!=null)connection.setRequestProperty("X-FQ-Guest",token);if(origin!=null)connection.setRequestProperty("Origin",origin);
        try {
            if(body!=null){connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");connection.setFixedLengthStreamingMode(body.length);try(OutputStream output=connection.getOutputStream()){output.write(body);}}
            int code=connection.getResponseCode();try(InputStream input=code>=400?connection.getErrorStream():connection.getInputStream()){return new Reply(code,input==null?new byte[0]:input.readAllBytes());}
        }finally{connection.disconnect();}
    }
    private static Reply post(GuestTransfer server,String path,JSONObject data)throws Exception {return request(server,path,server.token,"POST",data.toString().getBytes(StandardCharsets.UTF_8),null);}
    @Test public void guestUploadsResumeAndNeverExposeUnselectedFiles()throws Exception {
        Context context=RuntimeEnvironment.getApplication();byte[] original="浏览器发送的中文 😀".getBytes(StandardCharsets.UTF_8);
        try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
            assertEquals(403,request(server,"/api/guest",null,"GET",null,null).code);
            assertEquals(400,post(server,"/api/prepare",new JSONObject().put("name","../outside").put("size",1)).code);
            String id=post(server,"/api/prepare",new JSONObject().put("name","测试.txt").put("size",original.length)).json().getString("id");
            assertEquals(200,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",Arrays.copyOfRange(original,0,5),null).code);
            assertEquals(409,post(server,"/api/finish?id="+id,new JSONObject()).code);
            assertEquals(5,request(server,"/api/upload-status?id="+id,server.token,"GET",null,null).json().getLong("offset"));
            assertEquals(409,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",original,null).code);
            assertEquals(200,request(server,"/api/chunk?id="+id+"&offset=5",server.token,"POST",Arrays.copyOfRange(original,5,original.length),null).code);
            assertEquals(200,post(server,"/api/finish?id="+id,new JSONObject()).code);
            File saved=new File(context.getFilesDir(),"received/"+id);assertArrayEquals(original,Files.readAllBytes(saved.toPath()));assertEquals("测试.txt",context.getSharedPreferences("guest_received",0).getString(id,""));
            assertEquals(404,request(server,"/file?id="+id,server.token,"GET",null,null).code);
            String canceled=post(server,"/api/prepare",new JSONObject().put("name","cancel.bin").put("size",8)).json().getString("id");assertEquals(200,post(server,"/api/cancel?id="+canceled,new JSONObject()).code);
            post(server,"/api/prepare",new JSONObject().put("name","unfinished.bin").put("size",8));server.close();
            assertEquals(0,Objects.requireNonNull(new File(context.getFilesDir(),"received").listFiles((dir,name)->name.endsWith(".part"))).length);assertTrue(saved.isFile());
        }
    }
    @Test public void guestDownloadsAreSelectedReadOnlyAndServeAssets()throws Exception {
        Context context=RuntimeEnvironment.getApplication();byte[] bytes={1,2,3,4,5};
        LanNode.Source source=new LanNode.Source(){public String key(){return "test";}public String name(){return "中文.txt";}public long size(){return bytes.length;}public long modified(){return 1;}public InputStream open(long offset){return new ByteArrayInputStream(Arrays.copyOfRange(bytes,(int)offset,bytes.length));}};
        try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
            String id=request(server,"/api/guest",server.token,"GET",null,null).json().getJSONArray("files").getJSONObject(0).getString("id");
            assertArrayEquals(bytes,request(server,"/file?id="+id,server.token,"GET",null,null).body);
            assertEquals(0,request(server,"/file?id="+id,server.token,"HEAD",null,null).body.length);
            assertEquals(403,post(server,"/api/prepare",new JSONObject().put("name","x").put("size",1)).code);
            for(String path:new String[]{"/","/guest.js","/guest.css","/qrcodegen.js"})assertEquals(path,200,request(server,path,null,"GET",null,null).code);
        }
    }
    @Test public void allAttachmentPagesAndDeletedMessagesStayConsistent()throws Exception {
        Context context=RuntimeEnvironment.getApplication();try(ChatStore store=new ChatStore(context)) {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"test","host","设备","");LanNode.Message message=new LanNode.Message();message.number=1;message.text="附件";message.state="已收到";message.files.add(new Protocol.Attachment(0,"文件.txt",9,1));store.message(peer,message,false,false);
            for(int i=2;i<302;i++){LanNode.Message text=new LanNode.Message();text.number=i;text.text="普通文字";text.state="已收到";store.message(peer,text,false,false);}
            assertEquals(1,store.fileTasks().size());assertEquals(peer.id(),store.fileTasks().get(0).peer);store.deleteMessages(peer.id(),Collections.singletonList(message));assertTrue(store.fileTasks().isEmpty());
        }
    }
    @Test public void chatToolbarOpensTasks()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try {
            settle(service);ChatStore store=(ChatStore)field(service,"store");ExecutorService io=(ExecutorService)field(service,"io");
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"toolbar","host","中文 English 很长的聊天联系人名称","");
            io.submit(()->{LanNode.Message message=new LanNode.Message();message.number=1;message.text="文件消息";message.state="已收到";message.files.add(new Protocol.Attachment(0,"资料.txt",20,1));store.message(peer,message,false,false);}).get(5,TimeUnit.SECONDS);
            for(boolean dark:new boolean[]{false,true}) {
                context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
                try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
                    MainActivity activity=controller.get();settle(service);
                    java.lang.reflect.Method open=MainActivity.class.getDeclaredMethod("open",String.class);open.setAccessible(true);open.invoke(activity,peer.id());settle(service);
                    View root=(View)field(activity,"root");measure(root,786,1500);LinearLayout toolbar=(LinearLayout)field(activity,"toolbar");
                    View transfer=findDescription(toolbar,"传输任务"),more=findDescription(toolbar,"会话菜单");assertNotNull(transfer);assertNotNull(more);
                    assertEquals("transfer",field(transfer,"kind"));assertEquals(toolbar.indexOfChild(more)-1,toolbar.indexOfChild(transfer));
                    int expected=Math.round(48*activity.getResources().getDisplayMetrics().density);assertEquals(expected,transfer.getWidth());assertEquals(expected,transfer.getHeight());assertTrue(transfer.getRight()<=more.getLeft());
                    preview(toolbar,"chat-header-android-"+(dark?"dark":"light")+".png",(Integer)field(activity,"SURFACE"));assertTrue(transfer.performClick());settle(service);
                    Dialog dialog=(Dialog)field(activity,"tasksDialog");assertNotNull(dialog);assertTrue(dialog.isShowing());assertEquals(ViewGroup.LayoutParams.MATCH_PARENT,dialog.getWindow().getAttributes().width);assertEquals(ViewGroup.LayoutParams.MATCH_PARENT,dialog.getWindow().getAttributes().height);
                    BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");assertEquals(1,adapter.getCount());dialog.dismiss();settle(service);assertNull(field(activity,"taskAdapter"));
                }
            }
        }finally{serviceController.destroy();}
    }
    @SuppressWarnings("unchecked")
    @Test public void taskRefreshSkipsEqualContent()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=controller.get();settle(service);showTasks(activity);settle(service);Dialog dialog=(Dialog)field(activity,"tasksDialog");
            try {
                BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");List<ChatStore.FileTask> all=(List<ChatStore.FileTask>)field(adapter,"all");
                ChatStore.FileTask first=task("test",1,"中文 English.bin",10485760),second=task("test",2,"waiting.txt",1024);all.clear();all.add(first);all.add(second);
                Map<String,ChatService.FileProgress> progress=(Map<String,ChatService.FileProgress>)field(service,"fileProgress");String key=ChatService.fileKey(first.peer,first.message.number,first.file.id,false);
                progress.put(key,new ChatService.FileProgress(20,"接收中",1048576,null));refresh(adapter);
                Changes changes=new Changes();adapter.registerDataSetObserver(changes);TextView count=(TextView)field(adapter,"count");final int[] textChanges={0};
                count.addTextChangedListener(new android.text.TextWatcher(){public void beforeTextChanged(CharSequence s,int start,int n,int after){}public void onTextChanged(CharSequence s,int start,int before,int n){textChanges[0]++;}public void afterTextChanged(android.text.Editable value){}});
                for(int i=0;i<5;i++){refresh(adapter);activity.transfersChanged();}assertEquals(0,changes.count);assertEquals(0,textChanges[0]);
                ChatStore.FileTask copy=task("test",1,"中文 English.bin",10485760);all.set(0,copy);refresh(adapter);assertEquals(0,changes.count);assertSame("等值刷新仍应保留新的操作数据",copy,adapter.getItem(0));
                progress.put(key,new ChatService.FileProgress(35,"接收中",1048576,null));activity.transfersChanged();assertEquals(1,changes.count);assertTrue(count.getText().toString().contains("3.5 MB"));assertEquals(1,textChanges[0]);
                ListView parent=find(dialog.getWindow().getDecorView(),ListView.class);LinearLayout view=(LinearLayout)adapter.getView(0,null,parent);assertEquals(35,((ProgressBar)view.getChildAt(3)).getProgress());assertTrue(((TextView)view.getChildAt(2)).getText().toString().contains("1.0 MB/s"));
                progress.put(key,new ChatService.FileProgress(35,"接收中",1048577,null));activity.transfersChanged();assertEquals("显示速度与剩余时间相同应跳过刷新",1,changes.count);
                progress.put(key,new ChatService.FileProgress(35,"已暂停",0,null));activity.transfersChanged();assertEquals(2,changes.count);assertEquals("已暂停",((TextView)((LinearLayout)adapter.getView(0,view,parent)).getChildAt(2)).getText().toString());
                EditText input=(EditText)field(adapter,"input");input.setText("waiting");assertEquals(1,adapter.getCount());int notifications=changes.count,summaryChanges=textChanges[0];
                progress.put(key,new ChatService.FileProgress(60,"接收中",2097152,null));activity.transfersChanged();assertEquals("筛选外任务变化不应重绑当前列表",notifications,changes.count);assertEquals(summaryChanges,textChanges[0]);
                input.setText(" WAITING ");assertEquals("相同搜索结果不应重复刷新",notifications,changes.count);
                all.clear();refresh(adapter);assertEquals(0,adapter.getCount());notifications=changes.count;refresh(adapter);activity.transfersChanged();assertEquals("空列表不应反复刷新",notifications,changes.count);
            }finally{dialog.dismiss();}
        }finally{serviceController.destroy();}
    }
    @SuppressWarnings("unchecked")
    @Test public void taskQueueSummaryDoesNotRebindRows()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=controller.get();settle(service);showTasks(activity);settle(service);Dialog dialog=(Dialog)field(activity,"tasksDialog");
            try {
                BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");Changes changes=new Changes();adapter.registerDataSetObserver(changes);TextView count=(TextView)field(adapter,"count");
                ArrayDeque<Object> queue=(ArrayDeque<Object>)field(service,"queuedTransfers");Class<?> type=Class.forName(ChatService.class.getName()+"$QueuedTransfer");java.lang.reflect.Constructor<?> constructor=type.getDeclaredConstructor(Runnable.class,Runnable.class);constructor.setAccessible(true);
                queue.add(constructor.newInstance((Runnable)()->{},(Runnable)()->{}));activity.transfersChanged();assertTrue(count.getText().toString().contains("另有 1 项"));assertEquals(0,changes.count);
                CharSequence summary=count.getText();activity.transfersChanged();assertSame("等值摘要不应重复设置文本",summary,count.getText());assertEquals(0,changes.count);
                queue.clear();activity.transfersChanged();assertFalse(count.getText().toString().contains("排队中"));assertEquals(0,changes.count);
            }finally{dialog.dismiss();}
        }finally{serviceController.destroy();}
    }
    @Test public void concurrentTaskReloadCoalescesAndKeepsLatestRows()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=controller.get();settle(service);
            ChatStore store=(ChatStore)field(service,"store");ExecutorService io=(ExecutorService)field(service,"io");
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"reload","host","测试设备","");
            ChatStore.FileTask first=task(peer.id(),1,"旧任务.txt",20),latest=task(peer.id(),2,"最新任务.txt",30);
            first.message.text="旧附件";first.message.time=1;latest.message.text="最新附件";latest.message.time=2;
            io.submit(()->store.message(peer,first.message,false,false)).get(5,TimeUnit.SECONDS);
            showTasks(activity);settle(service);BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");assertEquals(1,adapter.getCount());
            java.lang.reflect.Method load=MainActivity.class.getDeclaredMethod("loadTaskRows");load.setAccessible(true);load.invoke(activity);
            // 第一份旧快照已读完，主线程回调尚未运行；此时产生新任务并再次请求刷新。
            io.submit(()->{}).get(5,TimeUnit.SECONDS);assertEquals(true,field(activity,"loadingTasks"));
            io.submit(()->store.message(peer,latest.message,false,false)).get(5,TimeUnit.SECONDS);
            for(int index=0;index<5;index++)load.invoke(activity);
            assertEquals(true,field(activity,"reloadTasksPending"));settle(service);
            assertEquals("并发刷新丢失最新任务",2,adapter.getCount());assertEquals(2,((ChatStore.FileTask)adapter.getItem(0)).message.number);
            assertEquals(false,field(activity,"loadingTasks"));assertEquals(false,field(activity,"reloadTasksPending"));
            load.invoke(activity);io.submit(()->{}).get(5,TimeUnit.SECONDS);load.invoke(activity);
            ((Dialog)field(activity,"tasksDialog")).dismiss();settle(service);
            assertNull(field(activity,"tasksDialog"));assertNull(field(activity,"taskAdapter"));assertEquals(false,field(activity,"reloadTasksPending"));
            showTasks(activity);settle(service);assertEquals(2,((BaseAdapter)field(activity,"taskAdapter")).getCount());((Dialog)field(activity,"tasksDialog")).dismiss();
        }finally{serviceController.destroy();}
    }
    @Test public void tasksRenderWithNativeMainWindowStyle()throws Exception {
        Context context=RuntimeEnvironment.getApplication();ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try {
            for(boolean dark:new boolean[]{false,true}) {
                context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).putBoolean("dark_mode",dark).commit();
                try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
                    MainActivity activity=controller.get();java.lang.reflect.Method show=MainActivity.class.getDeclaredMethod("showTasks");show.setAccessible(true);show.invoke(activity);Dialog dialog=ShadowDialog.getLatestDialog();
                    java.lang.reflect.Field field=MainActivity.class.getDeclaredField("taskAdapter");field.setAccessible(true);Object adapter=field.get(activity);
                    List<ChatStore.FileTask> rows=new ArrayList<>();for(int i=0;i<8;i++){LanNode.Message message=new LanNode.Message();message.number=i;message.outgoing=i%2==0;message.files.add(new Protocol.Attachment(0,i==0?"中文与 English 很长文件名 Project-delivery-2026.zip":"项目文件 "+i+".pdf",67108864,1));rows.add(new ChatStore.FileTask("test",message,message.files.get(0)));}
                    java.lang.reflect.Field all=adapter.getClass().getDeclaredField("all");all.setAccessible(true);all.set(adapter,rows);java.lang.reflect.Method refresh=adapter.getClass().getDeclaredMethod("refresh");refresh.setAccessible(true);refresh.invoke(adapter);
                    View view=dialog.getWindow().getDecorView();view.measure(View.MeasureSpec.makeMeasureSpec(786,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1500,View.MeasureSpec.EXACTLY));view.layout(0,0,786,1500);
                    File output=new File(System.getProperty("feiq.previewDir"));assertTrue(output.isDirectory()||output.mkdirs());Bitmap bitmap=Bitmap.createBitmap(786,1500,Bitmap.Config.ARGB_8888);view.draw(new Canvas(bitmap));try(OutputStream stream=new FileOutputStream(new File(output,"transfers-"+(dark?"dark":"light")+".png"))){bitmap.compress(Bitmap.CompressFormat.PNG,100,stream);}bitmap.recycle();
                    EditText input=find(view,EditText.class);assertNotNull(input);input.setText("不存在");assertEquals(0,((BaseAdapter)adapter).getCount());dialog.dismiss();
                }
            }
        }finally{serviceController.destroy();}
    }
    private static final class Changes extends android.database.DataSetObserver {int count;@Override public void onChanged(){count++;}}
    private static void disableConnection(Context context){context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).commit();}
    private static Object field(Object target,String name)throws Exception {java.lang.reflect.Field field=target.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(target);}
    private static void refresh(Object adapter)throws Exception {java.lang.reflect.Method refresh=adapter.getClass().getDeclaredMethod("refresh");refresh.setAccessible(true);refresh.invoke(adapter);}
    private static void showTasks(MainActivity activity)throws Exception {java.lang.reflect.Method show=MainActivity.class.getDeclaredMethod("showTasks");show.setAccessible(true);show.invoke(activity);}
    private static ChatStore.FileTask task(String peer,long packet,String name,long size){LanNode.Message message=new LanNode.Message();message.number=packet;message.state="已收到";Protocol.Attachment file=new Protocol.Attachment(0,name,size,1);message.files.add(file);return new ChatStore.FileTask(peer,message,file);}
    private static void settle(ChatService service)throws Exception {
        ExecutorService io=(ExecutorService)field(service,"io");
        for(int i=0;i<3;i++){io.submit(()->{}).get(5,TimeUnit.SECONDS);Shadows.shadowOf(android.os.Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(100));}
    }
    private static void measure(View view,int width,int height){view.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(height,View.MeasureSpec.EXACTLY));view.layout(0,0,width,height);}
    private static void preview(View view,String name,int background)throws Exception {
        File output=new File(System.getProperty("feiq.previewDir"));assertTrue(output.isDirectory()||output.mkdirs());Bitmap bitmap=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);
        try{Canvas canvas=new Canvas(bitmap);canvas.drawColor(background);view.draw(canvas);try(OutputStream stream=new FileOutputStream(new File(output,name))){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,stream));}}finally{bitmap.recycle();}
    }
    private static View findDescription(View view,String description){if(description.contentEquals(view.getContentDescription()==null?"":view.getContentDescription()))return view;if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){View result=findDescription(((ViewGroup)view).getChildAt(i),description);if(result!=null)return result;}return null;}
    private static <T> T find(View view,Class<T> type){if(type.isInstance(view))return type.cast(view);if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){T result=find(((ViewGroup)view).getChildAt(i),type);if(result!=null)return result;}return null;}
}
