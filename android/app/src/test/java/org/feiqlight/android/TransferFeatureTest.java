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
    private static <T> T find(View view,Class<T> type){if(type.isInstance(view))return type.cast(view);if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){T result=find(((ViewGroup)view).getChildAt(i),type);if(result!=null)return result;}return null;}
}
