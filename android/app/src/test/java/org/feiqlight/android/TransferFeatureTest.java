package org.feiqlight.android;

import android.app.Dialog;
import android.content.*;
import android.graphics.*;
import android.os.Looper;
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
    @Test public void guestUsesSelectedFolder()throws Exception {
        Context context=RuntimeEnvironment.getApplication();ReceivedStorage storage=new ReceivedStorage(context);FileFeaturesTest.Docs docs=documents(context);byte[] bytes="访客唯一文件 😀".getBytes(StandardCharsets.UTF_8);
        String unrelated=docs.createDocument("root","text/plain","访客.txt");Files.write(docs.files.get(unrelated).toPath(),new byte[]{9});
        for(boolean external:new boolean[]{false,true}) {
            if(external)storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);else storage.useInternal();
            try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
                String id=post(server,"/api/prepare",new JSONObject().put("name","访客.txt").put("size",bytes.length)).json().getString("id");
                assertEquals(200,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",bytes,null).code);Reply finish=post(server,"/api/finish?id="+id,new JSONObject());assertEquals(200,finish.code);assertEquals("",finish.json().getString("warning"));
                File local=new File(context.getFilesDir(),"received/"+id);assertEquals(!external,local.isFile());
                if(external){android.net.Uri saved=storage.existingSaved(local);assertNotNull(saved);String document=android.provider.DocumentsContract.getDocumentId(saved);assertEquals("访客 (1).txt",docs.names.get(document));assertArrayEquals(bytes,Files.readAllBytes(docs.files.get(document).toPath()));}
                else {assertNull(storage.saved(local));assertArrayEquals(bytes,Files.readAllBytes(local.toPath()));}
                assertEquals(bytes.length,new JSONObject(context.getSharedPreferences("guest_received",0).getString("meta:"+id,"")).getLong("size"));assertEquals(bytes.length,storage.bytes(local));
                assertArrayEquals(new byte[]{9},Files.readAllBytes(docs.files.get(unrelated).toPath()));
            }
        }
    }
    @Test public void guestFolderFailureKeepsFile()throws Exception {
        Context context=RuntimeEnvironment.getApplication();ReceivedStorage storage=new ReceivedStorage(context);FileFeaturesTest.Docs docs=documents(context);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);byte[] bytes={1,2,3,4};
        for(boolean denied:new boolean[]{false,true}) {
            docs.deny=denied;docs.failOpen=!denied;List<String> warnings=new ArrayList<>();
            try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
                server.warning=warnings::add;String id=post(server,"/api/prepare",new JSONObject().put("name","失败回退.txt").put("size",bytes.length)).json().getString("id");
                assertEquals(200,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",bytes,null).code);Reply finish=post(server,"/api/finish?id="+id,new JSONObject());assertEquals(200,finish.code);assertTrue(finish.json().getString("warning").contains("文件已保存在应用内接收区"));assertEquals(1,warnings.size());
                File local=new File(context.getFilesDir(),"received/"+id);assertArrayEquals(bytes,Files.readAllBytes(local.toPath()));assertNull(storage.saved(local));assertTrue(docs.files.isEmpty());assertEquals("失败回退.txt",context.getSharedPreferences("guest_received",0).getString(id,""));
                assertTrue(request(server,"/api/upload-status?id="+id,server.token,"GET",null,null).json().getBoolean("complete"));assertEquals(200,post(server,"/api/finish?id="+id,new JSONObject()).code);assertEquals(1,warnings.size());
            }
        }
    }
    @Test public void guestCancelQuotaAndSlots()throws Exception {
        Context context=RuntimeEnvironment.getApplication();long large=6L*1024*1024*1024;
        try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
            Reply prepared=post(server,"/api/prepare",new JSONObject().put("name","large.bin").put("size",large));assertEquals(200,prepared.code);String id=prepared.json().getString("id");
            assertEquals(409,post(server,"/api/prepare",new JSONObject().put("name","overflow.bin").put("size",large)).code);
            assertEquals(200,post(server,"/api/cancel?id="+id,new JSONObject()).code);
            Reply replacement=post(server,"/api/prepare",new JSONObject().put("name","replacement.bin").put("size",large));assertEquals("取消后未归还配额",200,replacement.code);
            assertEquals(410,post(server,"/api/cancel?id="+id,new JSONObject()).code);
            assertEquals(409,post(server,"/api/prepare",new JSONObject().put("name","double-refund.bin").put("size",5L*1024*1024*1024)).code);
            assertEquals(200,post(server,"/api/cancel?id="+replacement.json().getString("id"),new JSONObject()).code);
            List<String> ids=new ArrayList<>();for(int i=0;i<100;i++){Reply ready=post(server,"/api/prepare",new JSONObject().put("name","empty.txt").put("size",0));assertEquals(200,ready.code);ids.add(ready.json().getString("id"));}
            assertEquals(409,post(server,"/api/prepare",new JSONObject().put("name","extra.txt").put("size",0)).code);
            assertEquals(200,post(server,"/api/finish?id="+ids.get(0),new JSONObject()).code);assertEquals(409,post(server,"/api/cancel?id="+ids.get(0),new JSONObject()).code);
            assertEquals(200,post(server,"/api/cancel?id="+ids.get(1),new JSONObject()).code);assertEquals("取消后未释放有效文件名额",200,post(server,"/api/prepare",new JSONObject().put("name","new.txt").put("size",0)).code);
        }
    }
    @Test public void guestCancelHistoryBound()throws Exception {
        try(GuestTransfer server=new GuestTransfer(RuntimeEnvironment.getApplication(),Collections.emptyList(),true,true)) {
            for(int i=0;i<1024;i++){Reply ready=post(server,"/api/prepare",new JSONObject().put("name","empty.txt").put("size",0));assertEquals(200,ready.code);assertEquals(200,post(server,"/api/cancel?id="+ready.json().getString("id"),new JSONObject()).code);}
            assertEquals("取消历史没有内存上限",409,post(server,"/api/prepare",new JSONObject().put("name","extra.txt").put("size",0)).code);
        }
    }
    @Test public void guestFinishRetriesRecordCommit()throws Exception {
        Context application=RuntimeEnvironment.getApplication();java.util.concurrent.atomic.AtomicBoolean failCommit=new java.util.concurrent.atomic.AtomicBoolean(true);
        FileFeaturesTest.Docs docs=documents(application);ReceivedStorage storage=new ReceivedStorage(application);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        Context flaky=new ContextWrapper(application) {
            @Override public Context getApplicationContext(){return this;}
            @Override public SharedPreferences getSharedPreferences(String name,int mode) {
                SharedPreferences preferences=super.getSharedPreferences(name,mode);if(!name.equals("guest_received"))return preferences;
                return (SharedPreferences)java.lang.reflect.Proxy.newProxyInstance(SharedPreferences.class.getClassLoader(),new Class<?>[]{SharedPreferences.class},(proxy,method,args)->{
                    if(!method.getName().equals("edit"))return method.invoke(preferences,args);
                    SharedPreferences.Editor editor=preferences.edit();
                    return java.lang.reflect.Proxy.newProxyInstance(SharedPreferences.Editor.class.getClassLoader(),new Class<?>[]{SharedPreferences.Editor.class},(editorProxy,operation,arguments)->{
                        if(operation.getName().equals("commit")&&failCommit.getAndSet(false))return false;
                        Object result=operation.invoke(editor,arguments);return result==editor?editorProxy:result;
                    });
                });
            }
        };
        byte[] bytes={1,2,3,4};java.util.concurrent.atomic.AtomicInteger notifications=new java.util.concurrent.atomic.AtomicInteger();
        try(GuestTransfer server=new GuestTransfer(flaky,Collections.emptyList(),true,true)) {
            server.received=notifications::incrementAndGet;String id=post(server,"/api/prepare",new JSONObject().put("name","record.txt").put("size",bytes.length)).json().getString("id");
            assertEquals(200,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",bytes,null).code);
            assertEquals(500,post(server,"/api/finish?id="+id,new JSONObject()).code);long firstFinishAt=System.currentTimeMillis();
            File target=new File(application.getFilesDir(),"received/"+id);assertFalse(target.exists());android.net.Uri saved=storage.existingSaved(target);assertNotNull(saved);File document=docs.files.get(android.provider.DocumentsContract.getDocumentId(saved));assertArrayEquals(bytes,Files.readAllBytes(document.toPath()));
            SharedPreferences records=application.getSharedPreferences("guest_received",0);assertFalse(records.contains(id));assertFalse(records.contains("meta:"+id));
            assertFalse("记录未写入时不应报告完成",request(server,"/api/upload-status?id="+id,server.token,"GET",null,null).json().getBoolean("complete"));
            assertEquals(409,post(server,"/api/cancel?id="+id,new JSONObject()).code);assertEquals(409,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",new byte[]{9},null).code);
            assertFalse(new File(target.getParentFile(),".fq-guest-"+id+".part").exists());storage.useInternal();Thread.sleep(20);
            assertEquals("完成重试没有补写接收记录",200,post(server,"/api/finish?id="+id,new JSONObject()).code);
            JSONObject metadata=new JSONObject(records.getString("meta:"+id,""));assertEquals("record.txt",records.getString(id,""));assertEquals(bytes.length,metadata.getLong("size"));assertTrue(metadata.getLong("time")>0&&metadata.getLong("time")<=firstFinishAt);
            assertEquals(200,post(server,"/api/finish?id="+id,new JSONObject()).code);assertEquals(1,notifications.get());assertFalse(target.exists());assertArrayEquals(bytes,Files.readAllBytes(document.toPath()));assertEquals("补写记录不应重新保存第二份文件",1,docs.files.size());assertEquals(saved,storage.existingSaved(target));
        }
    }
    @Test public void guestMetadataSurvivesCleanup()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);byte[] bytes="访客大小与时间".getBytes(StandardCharsets.UTF_8);String id;
        FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
            id=post(server,"/api/prepare",new JSONObject().put("name","访客.txt").put("size",bytes.length)).json().getString("id");
            assertEquals(200,request(server,"/api/chunk?id="+id+"&offset=0",server.token,"POST",bytes,null).code);assertEquals(200,post(server,"/api/finish?id="+id,new JSONObject()).code);
        }
        JSONObject metadata=new JSONObject(context.getSharedPreferences("guest_received",0).getString("meta:"+id,""));long time=metadata.getLong("time");
        ServiceController<ChatService> first=Robolectric.buildService(ChatService.class).create();File local=new File(context.getFilesDir(),"received/"+id);
        try {
            ChatService service=first.get();settle(service);android.net.Uri saved=storage.existingSaved(local);assertFalse(local.exists());assertNotNull(saved);assertArrayEquals(bytes,Files.readAllBytes(docs.files.get(android.provider.DocumentsContract.getDocumentId(saved)).toPath()));
            ChatStore.FileTask task=fileTasks(service).get(0);assertEquals(bytes.length,task.file.size);assertEquals(time,task.message.time);assertEquals("访客.txt",task.file.name);assertEquals(1,service.receivedUsage().guestFiles);
            List<ChatService.Usage> results=new ArrayList<>();service.clearFiles(service.receivedUsage(),results::add);transferDone(service);assertEquals(1,results.size());assertEquals(1,results.get(0).files);assertFalse(storage.exists(local));assertTrue(docs.files.isEmpty());assertEquals("访客.txt",context.getSharedPreferences("guest_received",0).getString(id,""));
        }finally{first.destroy();assertTrue(((ExecutorService)field(first.get(),"io")).awaitTermination(5,TimeUnit.SECONDS));}
        ServiceController<ChatService> restarted=Robolectric.buildService(ChatService.class).create();
        try {ChatStore.FileTask task=fileTasks(restarted.get()).get(0);assertEquals("访客.txt",context.getSharedPreferences("guest_received",0).getString(id,""));assertEquals(bytes.length,task.file.size);assertEquals(time,task.message.time);}
        finally{restarted.destroy();}
    }
    @Test public void cleanupAfterDirectoryChangesAndRestartKeepsUnregisteredFiles()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);
        String a=docs.createDocument("root",android.provider.DocumentsContract.Document.MIME_TYPE_DIR,"目录 A");
        String b=docs.createDocument("root",android.provider.DocumentsContract.Document.MIME_TYPE_DIR,"目录 B");
        android.net.Uri treeA=android.provider.DocumentsContract.buildTreeDocumentUri("feiq.documents",a),treeB=android.provider.DocumentsContract.buildTreeDocumentUri("feiq.documents",b);
        String unrelatedA=docs.createDocument(a,"text/plain","用户原件.txt"),unrelatedB=docs.createDocument(b,"text/plain","用户原件.txt");
        Files.write(docs.files.get(unrelatedA).toPath(),new byte[]{8});Files.write(docs.files.get(unrelatedB).toPath(),new byte[]{9});
        int flags=Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
        ServiceController<ChatService> first=Robolectric.buildService(ChatService.class).create();
        File localA,localB;android.net.Uri savedA,savedB;
        try {
            ChatService service=first.get();settle(service);
            localA=guestFile(context,"a".repeat(64),"来自 A.txt",new byte[]{1,2},100);
            storage.choose(treeA,flags);savedA=storage.save(localA,"来自 A.txt",new LanNode.Transfer());
            localB=guestFile(context,"b".repeat(64),"来自 B.txt",new byte[]{3,4,5},200);
            storage.choose(treeB,flags);savedB=storage.save(localB,"来自 B.txt",new LanNode.Transfer());
            assertEquals(treeA,storage.savedTree(localA));assertEquals(treeB,storage.savedTree(localB));assertEquals(treeB,storage.tree());
            assertEquals(a,docs.parents.get(android.provider.DocumentsContract.getDocumentId(savedA)));assertEquals(b,docs.parents.get(android.provider.DocumentsContract.getDocumentId(savedB)));
        }finally{first.destroy();assertTrue(((ExecutorService)field(first.get(),"io")).awaitTermination(5,TimeUnit.SECONDS));}
        ServiceController<ChatService> restarted=Robolectric.buildService(ChatService.class).create();
        try {
            ChatService service=restarted.get();settle(service);ReceivedStorage restored=new ReceivedStorage(service);
            assertEquals(treeB,restored.tree());assertEquals(savedA,restored.existingSaved(localA));assertEquals(savedB,restored.existingSaved(localB));
            assertEquals(2,service.receivedUsage().files);assertEquals(5,service.receivedUsage().bytes);
            List<ChatService.Usage> results=new ArrayList<>();service.clearFiles(service.receivedUsage(),results::add);transferDone(service);
            assertEquals(1,results.size());assertEquals(2,results.get(0).files);assertEquals(5,results.get(0).bytes);
            assertFalse(restored.exists(localA));assertFalse(restored.exists(localB));assertEquals(treeB,restored.tree());
            assertArrayEquals(new byte[]{8},Files.readAllBytes(docs.files.get(unrelatedA).toPath()));assertArrayEquals(new byte[]{9},Files.readAllBytes(docs.files.get(unrelatedB).toPath()));
            assertEquals(2,docs.files.size());assertTrue(docs.directories.contains(a)&&docs.directories.contains(b));
        }finally{restarted.destroy();}
    }
    @Test public void guestTaskStableOrder()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);String low="1".repeat(64),high="2".repeat(64),legacy="3".repeat(64);File directory=new File(context.getFilesDir(),"received");assertTrue(directory.isDirectory()||directory.mkdirs());
        Files.write(new File(directory,low).toPath(),new byte[1]);Files.write(new File(directory,high).toPath(),new byte[2]);File old=new File(directory,legacy);Files.write(old.toPath(),new byte[5]);assertTrue(old.setLastModified(500));
        context.getSharedPreferences("guest_received",0).edit().putString(high,"guest-b").putString("meta:"+high,new JSONObject().put("size",22).put("time",1000).toString()).putString(low,"guest-a").putString("meta:"+low,new JSONObject().put("size",11).put("time",1000).toString()).putString(legacy,"legacy").commit();
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();
        try {
            ChatService service=controller.get();settle(service);ChatStore store=(ChatStore)field(service,"store");LanNode.Peer a=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"a","host","A","");a.conversationId="a-device";
            LanNode.Peer z=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31235"),"z","host","Z","");z.conversationId="z-device";long packet=Long.parseLong(high.substring(0,15),16);
            ChatStore.FileTask older=task(z.id(),packet,"chat-z",3),same=task(a.id(),packet,"chat-a",4),newest=task(a.id(),1,"newest",6);older.message.time=same.message.time=1000;newest.message.time=2000;
            older.message.text=older.file.name;same.message.text=same.file.name;newest.message.text=newest.file.name;
            ((ExecutorService)field(service,"io")).submit(()->{store.message(z,older.message,false,false);store.message(a,same.message,false,false);store.message(a,newest.message,false,false);}).get(5,TimeUnit.SECONDS);
            List<String> expected=Arrays.asList("newest","chat-a","guest-b","chat-z","guest-a","legacy");
            for(int attempt=0;attempt<3;attempt++){List<ChatStore.FileTask> rows=fileTasks(service);assertEquals(expected,rows.stream().map(row->row.file.name).collect(java.util.stream.Collectors.toList()));assertEquals(5,rows.get(5).file.size);assertEquals(500,rows.get(5).message.time);}
        }finally{controller.destroy();}
    }
    @Test public void guestDeleteKeepsMetadata()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=activityController.get();settle(service);FileFeaturesTest.Docs docs=documents(service);ReceivedStorage storage=new ReceivedStorage(service);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            java.util.concurrent.atomic.AtomicInteger changed=new java.util.concurrent.atomic.AtomicInteger();service.observe(new ChatService.Observer(){public void changed(){}public void error(String text){fail(text);}public void transfersChanged(){changed.incrementAndGet();}});
            for(boolean legacyCopy:new boolean[]{false,true}) {
                String id=(legacyCopy?"5":"4").repeat(64);File local=guestFile(context,id,"guest.txt",new byte[]{1,2,3},900);
                android.net.Uri exported=storage.save(local,"guest.txt",new LanNode.Transfer());assertFalse(local.exists());if(legacyCopy)Files.write(local.toPath(),new byte[]{1,2,3});ChatStore.FileTask task=fileTasks(service).stream().filter(row->local.equals(row.guestFile)).findFirst().get();
                taskActions(activity,task);Button delete=findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除本机文件");assertNotNull(delete);delete.performClick();Dialog confirm=ShadowDialog.getLatestDialog();assertTrue(dialogText(confirm).contains("将删除本机保存的文件"));confirm.dismiss();
                assertEquals(exported,storage.existingSaved(local));assertTrue(docs.files.containsKey(android.provider.DocumentsContract.getDocumentId(exported)));assertEquals(legacyCopy,local.isFile());
                taskActions(activity,task);findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除本机文件").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除文件").performClick();transferDone(service);
                assertFalse(local.exists());assertNull(storage.saved(local));assertFalse(docs.files.containsKey(android.provider.DocumentsContract.getDocumentId(exported)));assertEquals("guest.txt",context.getSharedPreferences("guest_received",0).getString(id,""));
                ChatStore.FileTask deleted=fileTasks(service).stream().filter(row->local.equals(row.guestFile)).findFirst().get();assertEquals(3,deleted.file.size);assertEquals(900,deleted.message.time);assertEquals("本机文件已删除",taskState(activity,deleted));
                taskActions(activity,deleted);assertNull(findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除本机文件"));ShadowDialog.getLatestDialog().dismiss();
            }
            assertTrue("删除后没有刷新传输任务",changed.get()>=2);File outside=new File(context.getFilesDir(),"outside.txt");Files.write(outside.toPath(),new byte[]{9});assertThrows(IOException.class,()->storage.delete(outside));assertTrue(outside.isFile());
        }finally{controller.destroy();}
    }
    @Test public void guestSingleFileActions()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try {
            settle(service);
            for(boolean dark:new boolean[]{false,true}) {
                context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
                try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
                    MainActivity activity=activityController.get();settle(service);
                    for(String name:new String[]{"访客.png","访客.mp4","访客.txt"}) {
                        String id=SharedFiles.random(32);File local=guestFile(context,id,name,new byte[]{1,2,3,4},800);ChatStore.FileTask task=fileTasks(service).stream().filter(row->local.equals(row.guestFile)).findFirst().get();taskActions(activity,task);Dialog menu=ShadowDialog.getLatestDialog();View decor=menu.getWindow().getDecorView();measure(decor,786,1500);
                        for(String label:new String[]{"打开文件","打开方式…","分享文件","查看保存位置","删除本机文件","复制文件名"}){Button action=findButton(decor,label);assertNotNull(label,action);assertTrue(label,action.getHeight()>=104);}
                        for(String removed:new String[]{"另存为…","保存到设置目录","保存到相册"})assertNull(findButton(decor,removed));
                        preview(decor,"guest-actions-"+(dark?"dark":"light")+"-"+name.substring(name.lastIndexOf('.')+1)+".png",(Integer)field(activity,"SURFACE"));
                        findButton(decor,"分享文件").performClick();Intent chooser=Shadows.shadowOf(activity).getNextStartedActivity();assertEquals(Intent.ACTION_CHOOSER,chooser.getAction());Intent share=chooser.getParcelableExtra(Intent.EXTRA_INTENT);assertEquals(Intent.ACTION_SEND,share.getAction());assertNotNull(share.getClipData());assertTrue(local.isFile());
                    }
                }
            }
        }finally{controller.destroy();}
    }
    @Test public void guestClearWarnsEveryGuest()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=activityController.get();settle(service);File unsaved=guestFile(context,"6".repeat(64),"内部.txt",new byte[4],600),exported=guestFile(context,"7".repeat(64),"目录.txt",new byte[5],700);File chat=new File(unsaved.getParentFile(),"chat.bin");Files.write(chat.toPath(),new byte[2]);
            FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);storage.save(exported,"目录.txt",new LanNode.Transfer());String unrelated=docs.createDocument("root","text/plain","用户文件.txt");Files.write(docs.files.get(unrelated).toPath(),new byte[]{9});
            ChatService.Usage usage=service.receivedUsage();assertEquals(3,usage.files);assertEquals(11,usage.bytes);assertEquals(2,usage.guestFiles);assertEquals(0,new ChatService.Usage(1,2,0).guestFiles);
            java.lang.reflect.Method settings=MainActivity.class.getDeclaredMethod("settings");settings.setAccessible(true);settings.invoke(activity);
            Dialog panel=ShadowDialog.getLatestDialog();View content=panel.getWindow().getDecorView();findButton(content,"文件").performClick();Button clear=findButton(content,"清理已接收文件");
            clear.performClick();assertFalse("统计时不应能重复点击",clear.isEnabled());settle(service);Dialog confirm=ShadowDialog.getLatestDialog();String text=dialogText(confirm);
            assertTrue(text,text.contains("其中 2 个来自临时浏览器传输，清理后无法重新接收。"));assertTrue(text.contains("删除后文件不再保留"));assertFalse(text.contains("另存"));View decor=confirm.getWindow().getDecorView();measure(decor,786,1500);preview(decor,"guest-clear-warning.png",(Integer)field(activity,"SURFACE"));confirm.dismiss();assertTrue(unsaved.isFile());assertTrue(storage.exists(exported));
            clear.performClick();settle(service);File late=guestFile(context,"8".repeat(64),"确认后到达.txt",new byte[]{42},900);
            findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"清理").performClick();transferDone(service);assertFalse(unsaved.exists());assertFalse(exported.exists());assertFalse(chat.exists());
            assertTrue("清理删除了确认后才收到的访客文件",late.isFile());assertArrayEquals(new byte[]{42},Files.readAllBytes(late.toPath()));
            assertNull(storage.saved(exported));assertArrayEquals(new byte[]{9},Files.readAllBytes(docs.files.get(unrelated).toPath()));assertTrue(org.robolectric.shadows.ShadowToast.getTextOfLatestToast().contains("已清理 3 个已接收文件"));assertEquals(1,service.receivedUsage().guestFiles);assertEquals("",service.transferStatus());
            ChatStore.FileTask deleted=fileTasks(service).stream().filter(row->unsaved.equals(row.guestFile)).findFirst().get();assertEquals(4,deleted.file.size);assertEquals(600,deleted.message.time);assertEquals("本机文件已删除",taskState(activity,deleted));
        }finally{controller.destroy();}
    }
    @Test public void guestExternalDeletion()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        try {
            for(boolean dark:new boolean[]{false,true}) {
                context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
                try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
                    MainActivity activity=activityController.get();settle(service);File local=guestFile(context,SharedFiles.random(32),"中文 English guest.txt",new byte[]{1,2,3},900);android.net.Uri saved=storage.save(local,"中文 English guest.txt",new LanNode.Transfer());
                    ChatStore.FileTask item=fileTasks(service).stream().filter(row->local.equals(row.guestFile)).findFirst().get();assertEquals("已保存",taskState(activity,item));
                    Files.delete(docs.files.get(android.provider.DocumentsContract.getDocumentId(saved)).toPath());item=fileTasks(service).stream().filter(row->local.equals(row.guestFile)).findFirst().get();assertEquals("本机文件已删除",taskState(activity,item));assertNull(storage.saved(local));assertFalse(storage.exists(local));
                    taskActions(activity,item);Dialog dialog=ShadowDialog.getLatestDialog();View decor=dialog.getWindow().getDecorView();
                    for(String label:new String[]{"打开文件","打开方式…","分享文件","删除本机文件"})assertNull(label,findButton(decor,label));assertNotNull(findButton(decor,"复制文件名"));
                    measure(decor,786,1500);preview(decor,"guest-deleted-actions-"+(dark?"dark":"light")+".png",(Integer)field(activity,"SURFACE"));dialog.dismiss();
                }
            }
        }finally{controller.destroy();}
    }
    @SuppressWarnings("unchecked")
    @Test public void chatExternalDeletion()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);context.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"deleted","host","中文 English 文件联系人","");
        Bitmap bitmap=Bitmap.createBitmap(12,8,Bitmap.Config.ARGB_8888);bitmap.eraseColor(Color.BLUE);byte[] png;
        try(ByteArrayOutputStream output=new ByteArrayOutputStream()){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,output));png=output.toByteArray();}finally{bitmap.recycle();}
        try {
            settle(service);
            for(boolean dark:new boolean[]{false,true}) {
                context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
                LanNode.Message message=new LanNode.Message();message.number=dark?101:100;message.time=System.currentTimeMillis();message.text="保留聊天与文件记录";message.state="已保存";
                Protocol.Attachment image=new Protocol.Attachment(0,"中文 English 长文件名称 "+message.number+".png",png.length,1),text=new Protocol.Attachment(1,"说明 "+message.number+".txt",4,1);message.files=Arrays.asList(image,text);
                File imageFile=service.receivedFile(peer,message.number,image),textFile=service.receivedFile(peer,message.number,text);Files.write(imageFile.toPath(),png);Files.write(textFile.toPath(),new byte[]{1,2,3,4});
                android.net.Uri imageUri=storage.save(imageFile,image.name,new LanNode.Transfer()),textUri=storage.save(textFile,text.name,new LanNode.Transfer());service.incoming(peer,message,false);settle(service);
                Map<String,ChatService.FileProgress> progress=(Map<String,ChatService.FileProgress>)field(service,"fileProgress");ChatStore store=(ChatStore)field(service,"store");
                for(Protocol.Attachment file:message.files){String key=ChatService.fileKey(peer.id(),message.number,file.id,false);progress.put(key,new ChatService.FileProgress(100,"已保存",null));((ExecutorService)field(service,"io")).submit(()->store.transfer(key,peer.id(),message.number,100,"已保存")).get(5,TimeUnit.SECONDS);}
                try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
                    MainActivity activity=activityController.get();settle(service);java.lang.reflect.Method open=MainActivity.class.getDeclaredMethod("open",String.class);open.setAccessible(true);open.invoke(activity,peer.id());settle(service);
                    ((ExecutorService)field(field(activity,"media"),"worker")).submit(()->{}).get(5,TimeUnit.SECONDS);settle(service);
                    View root=(View)field(activity,"root");assertNotNull(findDescription(root,image.name+"，媒体预览"));StringBuilder before=new StringBuilder();dialogText(root,before);assertTrue(before.toString().contains("已保存 2 / 2"));
                    ChatStore.FileTask item=fileTasks(service).stream().filter(row->row.message.number==message.number&&row.file.id==0).findFirst().get();assertEquals("已保存",taskState(activity,item));
                    docs.deny=true;fileTasks(service);assertEquals("本机文件已删除",service.progress(peer.id(),message.number,image.id,false).state);assertEquals(imageUri,storage.saved(imageFile));
                    docs.deny=false;item=fileTasks(service).stream().filter(row->row.message.number==message.number&&row.file.id==0).findFirst().get();assertEquals("授权恢复后应订正旧删除态","已保存",taskState(activity,item));assertEquals("已保存",service.progress(peer.id(),message.number,image.id,false).state);
                    Files.delete(docs.files.get(android.provider.DocumentsContract.getDocumentId(imageUri)).toPath());item=fileTasks(service).stream().filter(row->row.message.number==message.number&&row.file.id==0).findFirst().get();assertEquals("本机文件已删除",taskState(activity,item));activity.changed();settle(service);
                    assertNull(findDescription(root,image.name+"，媒体预览"));StringBuilder after=new StringBuilder();dialogText(root,after);assertTrue(after.toString(),after.toString().contains(" · 本机文件已删除"));assertEquals(0,service.progress(peer.id(),message.number,image.id,false).percent);assertTrue(after.toString().contains("已保存 1 / 2"));
                    java.lang.reflect.Method actions=MainActivity.class.getDeclaredMethod("fileActions",LanNode.Peer.class,long.class,Protocol.Attachment.class);actions.setAccessible(true);actions.invoke(activity,peer,message.number,image);Dialog dialog=ShadowDialog.getLatestDialog();View menu=dialog.getWindow().getDecorView();
                    assertNotNull(findButton(menu,"接收文件"));for(String label:new String[]{"打开文件","分享文件","删除本机文件","另存为…","保存到设置目录 / 重试","保存到相册"})assertNull(label,findButton(menu,label));dialog.dismiss();
                    activityController.pause().stop();Files.delete(docs.files.get(android.provider.DocumentsContract.getDocumentId(textUri)).toPath());activityController.start().resume().visible();settle(service);
                    fileTasks(service);settle(service);StringBuilder returned=new StringBuilder();dialogText(root,returned);assertTrue(returned.toString(),returned.toString().contains("总进度 0% · 已保存 0 / 2"));
                    for(Protocol.Attachment file:message.files)assertEquals("本机文件已删除",service.progress(peer.id(),message.number,file.id,false).state);
                    measure(root,786,1500);preview(root,"chat-deleted-"+(dark?"dark":"light")+".png",(Integer)field(activity,"SURFACE"));assertFalse(storage.exists(imageFile));assertFalse(storage.exists(textFile));
                }
            }
        }finally{controller.destroy();assertTrue(((ExecutorService)field(service,"io")).awaitTermination(5,TimeUnit.SECONDS));}
        ServiceController<ChatService> restarted=Robolectric.buildService(ChatService.class).create();
        try {for(ChatStore.FileTask item:fileTasks(restarted.get())){assertFalse(item.stored);assertEquals("本机文件已删除",restarted.get().progress(item.peer,item.message.number,item.file.id,false).state);}}
        finally{restarted.destroy();}
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
    @Test public void oldDeletedFileStateSurvivesCacheEvictionAndRestart()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"long-file-history","host","旧文件状态","");
        ChatStore.FileTask old=task(peer.id(),1,"已删除.txt",3);
        try(ChatStore store=new ChatStore(context)) {
            store.message(peer,old.message,false,false);
            store.transfer(ChatService.fileKey(peer.id(),1,0,false),peer.id(),1,0,"本机文件已删除");
            for(int i=2;i<=2002;i++)store.transfer(ChatService.fileKey(peer.id(),i,0,true),peer.id(),i,100,"对方已校验保存");
            assertEquals(2002,store.transfers().size());
        }
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> activity=Robolectric.buildActivity(MainActivity.class).setup()) {
            settle(service);assertTrue(((Map<?,?>)field(service,"fileProgress")).size()<=2000);
            ChatStore.FileTask restored=fileTasks(service).get(0);assertEquals("本机文件已删除",restored.savedProgress.state);
            assertEquals("本机文件已删除",taskState(activity.get(),restored));
            ChatService.ChatPage page=((ExecutorService)field(service,"io")).submit(()->service.resolveChatPage(peer.id(),Collections.singletonList(old.message),new HashMap<>(),()->true)).get(5,TimeUnit.SECONDS);
            assertEquals("本机文件已删除",page.progress.get(ChatService.fileKey(peer.id(),1,0,false)).state);
        } finally {controller.destroy();}
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
                progress.put(key,new ChatService.FileProgress(35,"接收中",1048576,null));progressRefresh(activity);assertEquals(1,changes.count);assertTrue(count.getText().toString().contains("3.5 MB"));assertEquals(1,textChanges[0]);
                ListView parent=find(dialog.getWindow().getDecorView(),ListView.class);LinearLayout view=(LinearLayout)adapter.getView(0,null,parent);assertEquals(35,((ProgressBar)view.getChildAt(3)).getProgress());assertTrue(((TextView)view.getChildAt(2)).getText().toString().contains("1.0 MB/s"));
                progress.put(key,new ChatService.FileProgress(35,"接收中",1048577,null));progressRefresh(activity);assertEquals("显示速度与剩余时间相同应跳过刷新",1,changes.count);
                progress.put(key,new ChatService.FileProgress(35,"已暂停",0,null));progressRefresh(activity);assertEquals(2,changes.count);assertEquals("已暂停",((TextView)((LinearLayout)adapter.getView(0,view,parent)).getChildAt(2)).getText().toString());
                EditText input=(EditText)field(adapter,"input");input.setText("waiting");Shadows.shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(150));assertEquals(1,adapter.getCount());int notifications=changes.count,summaryChanges=textChanges[0];
                progress.put(key,new ChatService.FileProgress(60,"接收中",2097152,null));progressRefresh(activity);assertEquals("筛选外任务变化不应重绑当前列表",notifications,changes.count);assertEquals(summaryChanges,textChanges[0]);
                input.setText(" WAITING ");Shadows.shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(150));assertEquals("相同搜索结果不应重复刷新",notifications,changes.count);
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
                queue.add(constructor.newInstance((Runnable)()->{},(Runnable)()->{}));progressRefresh(activity);assertTrue(count.getText().toString().contains("另有 1 项"));assertEquals(0,changes.count);
                CharSequence summary=count.getText();progressRefresh(activity);assertSame("等值摘要不应重复设置文本",summary,count.getText());assertEquals(0,changes.count);
                queue.clear();progressRefresh(activity);assertFalse(count.getText().toString().contains("排队中"));assertEquals(0,changes.count);
                queue.add(constructor.newInstance((Runnable)()->{},(Runnable)()->{}));
                findDescription(dialog.getWindow().getDecorView(),"传输任务操作").performClick();View menu=ShadowDialog.getLatestDialog().getWindow().getDecorView();
                assertNotNull(findButton(menu,"继续当前列表中的接收"));assertNotNull(findButton(menu,"暂停当前列表中的接收"));assertNotNull(findButton(menu,"取消排队发送任务"));
                findButton(menu,"取消排队发送任务").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"取消排队").performClick();assertTrue(queue.isEmpty());activity.transfersChanged();assertEquals(0,changes.count);
            }finally{dialog.dismiss();}
        }finally{serviceController.destroy();}
    }
    @SuppressWarnings("unchecked")
    @Test public void taskQuickActionChangesRefreshRowsWithoutChangingStatusOrProgress()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=controller.get();settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"action-signature","host","任务操作","");
            ChatStore.FileTask item=task(peer.id(),1,"状态不变.txt",4096);service.incoming(peer,item.message,false);settle(service);showTasks(activity);settle(service);
            BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");Map<String,ChatService.FileProgress> progress=(Map<String,ChatService.FileProgress>)field(service,"fileProgress");String key=ChatService.fileKey(peer.id(),1,0,false);
            progress.put(key,new ChatService.FileProgress(10,"接收中",null));refresh(adapter);Changes changes=new Changes();adapter.registerDataSetObserver(changes);
            LinearLayout row=taskRow(activity,item);assertNotNull(findButton(row,"继续接收"));assertNull(findButton(row,"暂停"));
            progress.put(key,new ChatService.FileProgress(10,"接收中",new LanNode.Transfer()));refresh(adapter);assertEquals(1,changes.count);
            row=taskRow(activity,item);assertNotNull(findButton(row,"暂停"));assertNotNull(findButton(row,"取消"));assertTrue(row.getChildAt(2) instanceof TextView);assertTrue(row.getChildAt(3) instanceof ProgressBar);assertTrue(row.getChildAt(4) instanceof LinearLayout);
            for(int index=0;index<((LinearLayout)row.getChildAt(4)).getChildCount();index++){View button=((LinearLayout)row.getChildAt(4)).getChildAt(index);assertFalse(button.isFocusable());assertFalse(button.isFocusableInTouchMode());}
            for(int index=0;index<5;index++)refresh(adapter);assertEquals("快捷动作相同不能额外重绑行",1,changes.count);
            ((Dialog)field(activity,"tasksDialog")).dismiss();
        }finally{serviceController.destroy();}
    }
    // 此回归验证任务操作；Robolectric 的模拟 Conscrypt 证书链不能替代真实设备的 TLS 验收。
    @Test @ConscryptMode(ConscryptMode.Mode.OFF) public void taskButtonsReceivePauseCancelRejectOpenAndReReceive()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);context.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
        ServiceController<ChatService> serviceController=Robolectric.buildService(ChatService.class).create();ChatService service=serviceController.get();List<String> errors=new CopyOnWriteArrayList<>();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String text){errors.add(text);}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};
        LanNode.Listener incoming=new LanNode.Listener(){public void peersChanged(){}public void error(String text){errors.add(text);}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){service.incoming(peer,message,update);}public void offerCanceled(LanNode.Peer peer,long packet){service.offerCanceled(peer,packet);}};
        int senderPort=chatPort(),receiverPort=chatPort();while(receiverPort==senderPort)receiverPort=chatPort();
        java.util.concurrent.atomic.AtomicBoolean throttle=new java.util.concurrent.atomic.AtomicBoolean();
        try(LanNode sender=new LanNode(senderPort,true,"task-sender","host","发送方", "",quiet);LanNode receiver=new LanNode(receiverPort,true,"task-receiver","host","接收方","",incoming)) {
            settle(service);setField(service,"node",receiver);service.observe(new ChatService.Observer(){public void changed(){}public void error(String text){errors.add(text);}});
            sender.setDeviceId("5".repeat(32));receiver.setDeviceId("6".repeat(32));
            File senderSecurity=new File(service.getFilesDir(),"task-sender-security"),receiverSecurity=new File(service.getFilesDir(),"task-receiver-security");
            SecureIdentity senderIdentity=new SecureIdentity(senderSecurity),receiverIdentity=new SecureIdentity(receiverSecurity);
            senderIdentity.confirmation=(name,code)->true;receiverIdentity.confirmation=(name,code)->true;
            sender.configureSecurity(senderSecurity,senderIdentity,(name,code)->true);receiver.configureSecurity(receiverSecurity,receiverIdentity,(name,code)->true);
            sender.start();receiver.start();sender.probe(LanNode.endpoint("127.0.0.1:"+receiver.port()));waitUntil(service,()->sender.peers().size()==1&&receiver.peers().size()==1&&sender.peers().get(0).fingerprint!=null&&receiver.peers().get(0).fingerprint!=null);
            LanNode.Peer target=sender.peers().get(0),peer=receiver.peers().get(0);
            senderIdentity.ensureTrusted(target.deviceId,target.fingerprint,target.name);receiverIdentity.ensureTrusted(peer.deviceId,peer.fingerprint,peer.name);
            File original=new File(service.getFilesDir(),"保留的中文 English 原文件.txt"),deletedOriginal=new File(service.getFilesDir(),"已删除的接收副本.txt"),slowOriginal=new File(service.getFilesDir(),"中文 English 很长的暂停拒收文件名-2026-delivery.bin");
            Files.write(original.toPath(),new byte[]{1,2,3});Files.write(deletedOriginal.toPath(),new byte[]{4,5,6});byte[] bytes=new byte[2*1024*1024];new Random(84).nextBytes(bytes);Files.write(slowOriginal.toPath(),bytes);
            sender.sendSources(target,Arrays.asList(taskSource(original,null),taskSource(deletedOriginal,null),taskSource(slowOriginal,throttle)));
            try {waitUntil(service,()->service.conversations().size()==1&&((Map<?,?>)uncheckedField(service,"waiting")).size()==3);}
            catch(AssertionError failure){throw new AssertionError("邀请入库状态：会话 "+service.conversations().size()+"，待接收 "+((Map<?,?>)field(service,"waiting")).size()+"，节点错误 "+errors,failure);}
            List<ChatStore.FileTask> tasks=fileTasks(service);ChatStore.FileTask saved=tasks.stream().filter(item->item.file.name.equals(original.getName())).findFirst().orElseThrow(),deleted=tasks.stream().filter(item->item.file.name.equals(deletedOriginal.getName())).findFirst().orElseThrow(),slow=tasks.stream().filter(item->item.file.name.equals(slowOriginal.getName())).findFirst().orElseThrow();
            File savedLocal=service.receivedFile(peer,saved.message.number,saved.file),deletedLocal=service.receivedFile(peer,deleted.message.number,deleted.file),slowLocal=service.receivedFile(peer,slow.message.number,slow.file),part=LanNode.partialFile(peer,slow.message.number,slow.file,slowLocal);
            try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()) {
                MainActivity activity=controller.get();settle(service);showTasks(activity);settle(service);
                LinearLayout row=taskRow(activity,saved);Button receive=findButton(row,"接收");assertNotNull(receive);assertNotNull(findButton(row,"拒绝"));assertFalse(receive.isFocusable());
                ListView list=find(((Dialog)field(activity,"tasksDialog")).getWindow().getDecorView(),ListView.class);BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");
                assertTrue(list.performItemClick(row,taskIndex(adapter,saved),0));assertNotNull(findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"接收文件"));assertNotNull(findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"拒绝这批文件"));ShadowDialog.getLatestDialog().dismiss();
                receive.performClick();waitUntil(service,()->savedLocal.isFile()&&!service.busy()&&"已保存".equals(service.progress(peer.id(),saved.message.number,saved.file.id,false).state));assertArrayEquals(Files.readAllBytes(original.toPath()),Files.readAllBytes(savedLocal.toPath()));
                findButton(taskRow(activity,saved),"打开").performClick();Intent opened=Shadows.shadowOf(activity).getNextStartedActivity();assertNotNull(opened);assertEquals(Intent.ACTION_VIEW,opened.getAction());assertNotNull(opened.getData());assertTrue((opened.getFlags()&Intent.FLAG_GRANT_READ_URI_PERMISSION)!=0);
                findButton(taskRow(activity,deleted),"接收").performClick();waitUntil(service,()->deletedLocal.isFile()&&!service.busy()&&"已保存".equals(service.progress(peer.id(),deleted.message.number,deleted.file.id,false).state));
                taskActions(activity,deleted);View menu=ShadowDialog.getLatestDialog().getWindow().getDecorView();
                for(String label:new String[]{"打开文件","打开方式…","分享文件","查看保存位置","删除本机文件","查看所在会话","复制文件名"})assertNotNull(label,findButton(menu,label));assertNull(findButton(menu,"文件与保存位置"));
                findButton(menu,"删除本机文件").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除文件").performClick();transferDone(service);assertFalse(deletedLocal.exists());assertTrue(deletedOriginal.isFile());
                taskActions(activity,deleted);menu=ShadowDialog.getLatestDialog().getWindow().getDecorView();assertNotNull(findButton(menu,"重新接收"));assertNull(findButton(menu,"删除本机文件"));findButton(menu,"重新接收").performClick();waitUntil(service,()->deletedLocal.isFile()&&!service.busy()&&"已保存".equals(service.progress(peer.id(),deleted.message.number,deleted.file.id,false).state));
                for(boolean dark:new boolean[]{false,true}) {
                    ((Dialog)field(activity,"tasksDialog")).dismiss();settle(service);context.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();invokeNoArgs(activity,"rerenderPreservingState");showTasks(activity);settle(service);
                    View view=((Dialog)field(activity,"tasksDialog")).getWindow().getDecorView();measure(view,786,1500);preview(view,"transfers-direct-actions-"+(dark?"dark":"light")+".png",(Integer)field(activity,"SURFACE"));
                }
                throttle.set(true);findButton(taskRow(activity,slow),"接收").performClick();waitUntil(service,()->part.length()>0&&service.busy());
                LanNode.Transfer running=(LanNode.Transfer)field(service,"transfer");
                service.deleteReceivedFile(peer,saved.message.number,saved.file);settle(service);
                assertFalse("接收另一个文件时未能单独删除已保存文件",savedLocal.exists());assertTrue(original.isFile());assertSame(running,field(service,"transfer"));assertFalse(running.isCancelled());
                List<ChatService.BatchFileDeleteResult> deletion=new ArrayList<>();
                service.deleteReceivedFiles(peer.id(),Arrays.asList(deleted.message,slow.message),deletion::add);settle(service);
                assertEquals(1,deletion.size());assertEquals(1,deletion.get(0).deleted);assertEquals(1,deletion.get(0).skippedBusy);assertFalse(deletedLocal.exists());assertTrue(part.isFile());assertSame(running,field(service,"transfer"));
                File guest=new File(savedLocal.getParentFile(),"a".repeat(64));Files.write(guest.toPath(),new byte[]{1,2});context.getSharedPreferences("guest_received",0).edit().putString(guest.getName(),"访客文件.txt").commit();
                service.deleteGuestFile(guest);settle(service);assertFalse("传输期间访客文件未删除",guest.exists());assertTrue(context.getSharedPreferences("guest_received",0).contains(guest.getName()));assertSame(running,field(service,"transfer"));
                ((Dialog)field(activity,"tasksDialog")).dismiss();settle(service);invokeOpen(activity,peer.id());settle(service);invokeMessageSelection(activity,slow.message);((Button)field(activity,"deleteSelectedFiles")).performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除文件").performClick();
                settle(service);assertTrue(org.robolectric.shadows.ShadowToast.getTextOfLatestToast().contains("1 个正在接收或处理的文件已跳过"));assertEquals(true,field(activity,"selectingMessages"));assertFalse(savedLocal.isFile());assertFalse(running.isCancelled());
                invokeNoArgs(activity,"endMessageSelection");showTasks(activity);settle(service);row=taskRow(activity,slow);assertNotNull(findButton(row,"暂停"));assertNotNull(findButton(row,"取消"));findButton(row,"暂停").performClick();waitUntil(service,()->!service.busy()&&"已暂停".equals(service.progress(peer.id(),slow.message.number,slow.file.id,false).state));assertTrue("暂停必须保留断点",part.length()>0);
                taskActions(activity,slow);assertNotNull(findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"继续接收"));findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"取消接收并清理断点").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"取消接收").performClick();settle(service);assertFalse(part.exists());
                assertNotNull(findButton(taskRow(activity,slow),"继续接收"));findButton(taskRow(activity,slow),"继续接收").performClick();waitUntil(service,()->part.length()>0&&service.busy());
                findButton(taskRow(activity,slow),"取消").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"取消接收").performClick();
                // 文件已清理、busy 已释放时，主线程终态回调仍可能排队；同时等到对应卡片的取消状态。
                waitUntil(service,()->!service.busy()&&!part.exists()&&service.progress(peer.id(),slow.message.number,slow.file.id,false).state.startsWith("已取消"));
                findButton(taskRow(activity,slow),"继续接收").performClick();waitUntil(service,()->part.length()>0&&service.busy());
                LanNode.Message queuedMessage=sender.sendMessage(target,"排队文件",Collections.singletonList(original));waitUntil(service,()->((ChatStore)uncheckedField(service,"store")).findMessage(peer.id(),queuedMessage.number,false)!=null);
                ChatStore.FileTask queued=fileTasks(service).stream().filter(item->item.message.number==queuedMessage.number).findFirst().orElseThrow();findButton(taskRow(activity,queued),"接收").performClick();assertEquals("排队中",service.progress(peer.id(),queued.message.number,queued.file.id,false).state);assertNotNull(findButton(taskRow(activity,queued),"暂停排队"));
                findDescription(((Dialog)field(activity,"tasksDialog")).getWindow().getDecorView(),"传输任务操作").performClick();menu=ShadowDialog.getLatestDialog().getWindow().getDecorView();assertNotNull(findButton(menu,"继续当前列表中的接收"));assertNotNull(findButton(menu,"暂停当前列表中的接收"));assertNull(findButton(menu,"取消排队发送任务"));findButton(menu,"暂停当前列表中的接收").performClick();
                waitUntil(service,()->!service.busy()&&"已暂停".equals(service.progress(peer.id(),slow.message.number,slow.file.id,false).state));assertEquals("已暂停",service.progress(peer.id(),queued.message.number,queued.file.id,false).state);assertTrue(((Map<?,?>)field(service,"waiting")).isEmpty());assertTrue(part.isFile());
                findDescription(((Dialog)field(activity,"tasksDialog")).getWindow().getDecorView(),"传输任务操作").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"继续当前列表中的接收").performClick();
                File queuedLocal=service.receivedFile(peer,queued.message.number,queued.file);waitUntil(service,()->{ChatService.FileProgress p=service.progress(peer.id(),slow.message.number,slow.file.id,false);return queuedLocal.isFile()&&part.length()>0&&service.busy()&&p!=null&&p.task!=null;});
                taskActions(activity,slow);menu=ShadowDialog.getLatestDialog().getWindow().getDecorView();assertNotNull(findButton(menu,"暂停并保留断点"));assertNotNull(findButton(menu,"取消接收并清理断点"));assertNotNull(findButton(menu,"拒绝这批文件"));assertNull(findButton(menu,"文件与保存位置"));findButton(menu,"拒绝这批文件").performClick();
                assertTrue(dialogText(ShadowDialog.getLatestDialog()).contains("将拒绝该消息中未接收的文件并清理断点；已保存的文件保留。"));findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"拒绝这批文件").performClick();
                waitUntil(service,()->!service.busy()&&!part.exists()&&"已拒绝".equals(service.progress(peer.id(),slow.message.number,slow.file.id,false).state));
                LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),slow.message.number,false);assertEquals("已拒绝",stored.state);assertEquals(2,stored.files.size());assertEquals("本机文件已删除",service.progress(peer.id(),deleted.message.number,deleted.file.id,false).state);assertFalse(savedLocal.isFile());assertFalse(deletedLocal.exists());assertTrue(original.isFile());assertTrue(deletedOriginal.isFile());assertTrue(slowOriginal.isFile());
                LanNode.Message rejected=sender.sendMessage(target,"直接拒收",Collections.singletonList(original));waitUntil(service,()->((ChatStore)uncheckedField(service,"store")).findMessage(peer.id(),rejected.number,false)!=null);ChatStore.FileTask pending=fileTasks(service).stream().filter(item->item.message.number==rejected.number).findFirst().orElseThrow();
                findButton(taskRow(activity,pending),"拒绝").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"拒绝这批文件").performClick();settle(service);assertEquals("已拒绝",service.progress(peer.id(),pending.message.number,pending.file.id,false).state);assertFalse(service.receivedFile(peer,pending.message.number,pending.file).exists());
                LanNode.Message outbound=receiver.sendMessage(peer,"取消发送",Collections.singletonList(original));waitUntil(service,()->((ChatStore)uncheckedField(service,"store")).findMessage(peer.id(),outbound.number,true)!=null);ChatStore.FileTask outgoing=fileTasks(service).stream().filter(item->item.message.number==outbound.number&&item.message.outgoing).findFirst().orElseThrow();
                findButton(taskRow(activity,outgoing),"取消发送").performClick();findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"取消发送").performClick();settle(service);assertFalse(receiver.offering(peer.id(),outbound.number,outgoing.file.id));assertTrue(original.isFile());assertEquals(View.GONE,taskRow(activity,outgoing).getChildAt(4).getVisibility());
                assertFalse(errors.toString(),errors.stream().anyMatch(text->text.contains("请先取消当前文件任务")||text.contains("接收失败")||text.contains("文件任务已取消")));
                ((Dialog)field(activity,"tasksDialog")).dismiss();
            }
        }finally{throttle.set(false);serviceController.destroy();}
    }
    @Test public void rejectUnrelatedOfferWhileBusyClearsOnlyItsOwnPartial()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        try {
            settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"unrelated-reject","host","独立邀请","");ChatStore.FileTask item=task(peer.id(),84,"未完成.bin",8192);
            service.incoming(peer,item.message,false);settle(service);File local=service.receivedFile(peer,84,item.file),part=LanNode.partialFile(peer,84,item.file,local);Files.write(part.toPath(),new byte[128]);File unrelated=new File(part.getParentFile(),"unrelated-task.part");Files.write(unrelated.toPath(),new byte[]{9});
            java.util.concurrent.atomic.AtomicBoolean busy=(java.util.concurrent.atomic.AtomicBoolean)field(service,"busy");LanNode.Transfer task=new LanNode.Transfer();setField(service,"transfer",task);busy.set(true);service.reject(peer,84);settle(service);
            assertTrue(busy.get());assertFalse(task.isCancelled());assertFalse(part.exists());assertTrue(unrelated.isFile());assertEquals("已拒绝",service.progress(peer.id(),84,item.file.id,false).state);assertEquals("已拒绝",((ChatStore)field(service,"store")).findMessage(peer.id(),84,false).state);
            busy.set(false);setField(service,"transfer",null);
        }finally{controller.destroy();}
    }
    @Test public void batchFileDeleteUsesStoredHistoryDeduplicatesAndRespectsSafPermission()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        try {
            settle(service);ReceivedStorage storage=new ReceivedStorage(context);FileFeaturesTest.Docs docs=documents(context);
            ChatStore store=(ChatStore)field(service,"store");ExecutorService io=(ExecutorService)field(service,"io");
            for(boolean external:new boolean[]{false,true}) {
                if(external)storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);else storage.useInternal();
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"batch-history-"+external,"host","历史接收文件","");
                ChatStore.FileTask saved=task(peer.id(),83,"已保存.txt",3),missing=task(peer.id(),84,"外部已删除.txt",3);
                io.submit(()->{store.message(peer,saved.message,false,false);store.message(peer,missing.message,false,false);store.transfer(ChatService.fileKey(peer.id(),84,missing.file.id,false),peer.id(),84,100,"已接收");}).get(5,TimeUnit.SECONDS);
                File local=service.receivedFile(peer,83,saved.file);Files.write(local.toPath(),new byte[]{1,2,3});storage.save(local,saved.file.name,new LanNode.Transfer());
                List<ChatService.BatchFileDeleteResult> result=new ArrayList<>();
                if(external) {
                    docs.deny=true;service.deleteReceivedFiles(peer.id(),Collections.singletonList(saved.message),result::add);transferDone(service);
                    assertTrue(result.isEmpty());assertNotNull("授权失效不能抹掉保存位置",storage.saved(local));
                    docs.deny=false;assertTrue("授权恢复后原文件必须仍在",storage.exists(local));
                }
                service.deleteReceivedFiles(peer.id(),Arrays.asList(saved.message,missing.message,saved.message),result::add);transferDone(service);
                assertEquals(1,result.size());assertEquals(1,result.get(0).deleted);assertEquals(1,result.get(0).skippedDeleted);assertEquals(0,result.get(0).unreceived);
                assertFalse(storage.exists(local));assertEquals("本机文件已删除",service.progress(peer.id(),84,missing.file.id,false).state);
                assertEquals(1,store.findMessage(peer.id(),83,false).files.size());assertEquals(1,store.findMessage(peer.id(),84,false).files.size());
                result.clear();service.deleteReceivedFiles(peer.id(),Arrays.asList(saved.message,missing.message,saved.message),result::add);transferDone(service);
                assertEquals(1,result.size());assertEquals(0,result.get(0).deleted);assertEquals(2,result.get(0).skippedDeleted);
            }
        }finally{controller.destroy();}
    }
    @Test public void deletionReservesOnlyItsFileAndSurvivesServiceRestart()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        CountDownLatch blocked=new CountDownLatch(1),release=new CountDownLatch(1);
        try {
            settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"delete-race","host","删除与接收互斥","");
            ChatStore.FileTask item=task(peer.id(),98,"删除中的中文 English.txt",3);service.incoming(peer,item.message,false);settle(service);
            File local=service.receivedFile(peer,item.message.number,item.file);Files.write(local.toPath(),new byte[]{1,2,3});
            ExecutorService io=(ExecutorService)field(service,"io");
            io.submit(()->{blocked.countDown();try{release.await(5,TimeUnit.SECONDS);}catch(InterruptedException e){Thread.currentThread().interrupt();}});assertTrue(blocked.await(5,TimeUnit.SECONDS));
            service.deleteReceivedFile(peer,item.message.number,item.file);
            service.receive(peer,item.message.number,item.file);
            Shadows.shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(100));
            assertTrue("I/O 队列释放前不应删除文件",local.isFile());
            assertFalse("重新接收越过了同一文件的删除保护",service.busy());assertNull(field(service,"activeReceive"));assertEquals("重新接收改动了待接收队列",1,((Map<?,?>)field(service,"waiting")).size());
            service.deleteReceivedFile(peer,item.message.number,item.file);
            release.countDown();io.submit(()->{}).get(5,TimeUnit.SECONDS);
            assertFalse(local.exists());
            assertEquals("本机文件已删除",((ChatStore)field(service,"store")).transfers().stream().filter(row->row.key.equals(ChatService.fileKey(peer.id(),98,0,false))).findFirst().orElseThrow().state);
            // 销毁发生在主线程完成回调之前，磁盘删除态仍必须保留。
            controller.destroy();io.awaitTermination(5,TimeUnit.SECONDS);
            ServiceController<ChatService> restarted=Robolectric.buildService(ChatService.class).create();
            try {settle(restarted.get());assertEquals("本机文件已删除",restarted.get().progress(peer.id(),98,0,false).state);assertFalse(local.exists());}
            finally {restarted.destroy();}
        } finally {release.countDown();if(!(Boolean)field(service,"stopped"))controller.destroy();}
    }
    @Test public void taskSnapshotKeepsNewSave()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();CountDownLatch release=new CountDownLatch(1);
        try {
            settle(service);FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),"snapshot","host","时序测试","");LanNode.Message message=new LanNode.Message();message.number=7;message.text="任务查询途中接收完成";message.state="已收到";
            Protocol.Attachment completed=new Protocol.Attachment(0,"刚保存.txt",1,1),slow=new Protocol.Attachment(1,"慢查询.txt",1,1);message.files=Arrays.asList(completed,slow);
            File received=service.receivedFile(peer,message.number,completed),external=service.receivedFile(peer,message.number,slow);Files.write(external.toPath(),new byte[]{9});android.net.Uri saved=storage.save(external,slow.name,new LanNode.Transfer());
            ChatStore store=(ChatStore)field(service,"store");((ExecutorService)field(service,"io")).submit(()->store.message(peer,message,false,false)).get(5,TimeUnit.SECONDS);
            docs.forbidMainQueries=true;docs.blockedQuery=android.provider.DocumentsContract.getDocumentId(saved);docs.queryGate=new CountDownLatch(1);docs.queryRelease=release;java.util.concurrent.atomic.AtomicReference<List<ChatStore.FileTask>> result=new java.util.concurrent.atomic.AtomicReference<>();service.fileTasks(result::set);assertTrue(docs.queryGate.await(5,TimeUnit.SECONDS));
            // 第一项已查出不存在，第二项仍在等待 SAF；这时真实接收完成回调先更新文件和进度。
            Files.write(received.toPath(),new byte[]{1});java.lang.reflect.Method progress=ChatService.class.getDeclaredMethod("fileProgress",String.class,long.class,long.class,boolean.class,int.class,String.class,LanNode.Transfer.class);progress.setAccessible(true);progress.invoke(service,peer.id(),message.number,completed.id,false,100,"已保存",null);
            release.countDown();settle(service);assertNotNull(result.get());assertEquals("较旧的查询结果抹掉了新保存状态","已保存",service.progress(peer.id(),message.number,completed.id,false).state);assertTrue(result.get().stream().filter(item->item.file.id==0).findFirst().get().stored);assertTrue(received.isFile());
        }finally{release.countDown();controller.destroy();}
    }
    @SuppressWarnings("unchecked")
    @Test public void taskSearchAndScrollUseSnapshotsReuseButtonsAndPauseRefresh()throws Exception {
        Context context=RuntimeEnvironment.getApplication();disableConnection(context);
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
        try(ActivityController<MainActivity> activityController=Robolectric.buildActivity(MainActivity.class).setup()) {
            MainActivity activity=activityController.get();settle(service);FileFeaturesTest.Docs docs=documents(context);ReceivedStorage storage=new ReceivedStorage(context);
            storage.choose(documentTree(),Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            File guest=guestFile(context,SharedFiles.random(32),"慢目录中的文件.txt",new byte[]{1,2,3},100);
            android.net.Uri document=storage.save(guest,"慢目录中的文件.txt",new LanNode.Transfer());docs.forbidMainQueries=true;
            showTasks(activity);settle(service);BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");Dialog dialog=(Dialog)field(activity,"tasksDialog");
            ListView list=find(dialog.getWindow().getDecorView(),ListView.class);int queries=docs.queries.get();LinearLayout row=(LinearLayout)adapter.getView(0,null,list);Button open=findButton(row,"打开");assertNotNull(open);
            for(int i=0;i<100;i++){row=(LinearLayout)adapter.getView(0,row,list);refresh(adapter);assertSame("滚动重复绑定重建了按钮",open,findButton(row,"打开"));}
            EditText input=(EditText)field(adapter,"input");input.setText("慢");input.setText("慢目录");Shadows.shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(150));assertEquals(1,adapter.getCount());assertEquals("搜索和滚动重复访问了文档提供方",queries,docs.queries.get());
            ChatStore.FileTask active=task("test",2,"慢目录 active.bin",100);List<ChatStore.FileTask> all=(List<ChatStore.FileTask>)field(adapter,"all");all.add(active);refresh(adapter);
            Changes changes=new Changes();adapter.registerDataSetObserver(changes);Map<String,ChatService.FileProgress> progress=(Map<String,ChatService.FileProgress>)field(service,"fileProgress");
            java.lang.reflect.Field listenerField=AbsListView.class.getDeclaredField("mOnScrollListener");listenerField.setAccessible(true);AbsListView.OnScrollListener listener=(AbsListView.OnScrollListener)listenerField.get(list);
            listener.onScrollStateChanged(list,AbsListView.OnScrollListener.SCROLL_STATE_FLING);
            for(int i=1;i<=5;i++){progress.put(ChatService.fileKey(active.peer,2,0,false),new ChatService.FileProgress(i*10,"接收中",null));progressRefresh(activity);}
            assertEquals("滚动中进度更新重绑了列表",0,changes.count);listener.onScrollStateChanged(list,AbsListView.OnScrollListener.SCROLL_STATE_IDLE);assertEquals(1,changes.count);
            Files.delete(docs.files.get(android.provider.DocumentsContract.getDocumentId(document)).toPath());
            invokeNoArgs(activity,"loadTaskRows");settle(service);assertEquals("本机文件已删除",taskState(activity,(ChatStore.FileTask)adapter.getItem(0)));
            activityController.pause();android.os.Handler handler=(android.os.Handler)field(adapter,"clockHandler");assertFalse(handler.hasCallbacks((Runnable)field(adapter,"storageRefresh")));assertFalse(handler.hasCallbacks((Runnable)field(adapter,"deferredRefresh")));
            activityController.resume();settle(service);assertTrue(handler.hasCallbacks((Runnable)field(adapter,"storageRefresh")));dialog.dismiss();Shadows.shadowOf(Looper.getMainLooper()).idle();assertFalse(handler.hasCallbacks((Runnable)field(adapter,"storageRefresh")));
        }finally{controller.destroy();}
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
                    EditText input=find(view,EditText.class);assertNotNull(input);assertEquals(View.VISIBLE,input.getVisibility());assertNull(findDescription(view,"搜索"));input.setText("不存在");Shadows.shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(150));assertEquals(0,((BaseAdapter)adapter).getCount());dialog.dismiss();
                }
            }
        }finally{serviceController.destroy();}
    }
    private static final class Changes extends android.database.DataSetObserver {int count;@Override public void onChanged(){count++;}}
    private static void disableConnection(Context context){context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).commit();}
    private static Object field(Object target,String name)throws Exception {java.lang.reflect.Field field=target.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(target);}
    private static Object uncheckedField(Object target,String name) {try{return field(target,name);}catch(Exception error){throw new AssertionError(error);}}
    private static void setField(Object target,String name,Object value)throws Exception {java.lang.reflect.Field field=target.getClass().getDeclaredField(name);field.setAccessible(true);field.set(target,value);}
    private static void invokeNoArgs(MainActivity activity,String name)throws Exception {java.lang.reflect.Method method=MainActivity.class.getDeclaredMethod(name);method.setAccessible(true);method.invoke(activity);}
    private static void invokeOpen(MainActivity activity,String peer)throws Exception {java.lang.reflect.Method method=MainActivity.class.getDeclaredMethod("open",String.class);method.setAccessible(true);method.invoke(activity,peer);}
    private static void invokeMessageSelection(MainActivity activity,LanNode.Message message)throws Exception {java.lang.reflect.Method method=MainActivity.class.getDeclaredMethod("beginMessageSelection",LanNode.Message.class);method.setAccessible(true);method.invoke(activity,message);}
    private static int chatPort()throws IOException {
        for(int attempt=0;attempt<20;attempt++)try(DatagramSocket udp=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            try(ServerSocket tcp=new ServerSocket(udp.getLocalPort(),1,InetAddress.getLoopbackAddress())){return udp.getLocalPort();}
            catch(BindException occupied){/* 节点同时需要空闲的 TCP 和 UDP 端口。 */}
        }
        throw new IOException("未找到回环测试端口");
    }
    private static LanNode.Source taskSource(File file,java.util.concurrent.atomic.AtomicBoolean throttle) {
        return new LanNode.Source() {
            public String key(){return file.getAbsolutePath();}public String name(){return file.getName();}public long size(){return file.length();}public long modified(){return file.lastModified();}
            public InputStream open(long offset)throws IOException {
                FileInputStream input=new FileInputStream(file);input.skipNBytes(offset);
                return new FilterInputStream(input) {
                    @Override public int read(byte[] bytes,int start,int length)throws IOException {
                        // 只减慢真实回环发送流，给 UI 操作留下稳定窗口；不创建大文件或模拟接收结果。
                        if(throttle!=null&&throttle.get())try{Thread.sleep(8);}catch(InterruptedException error){Thread.currentThread().interrupt();throw new InterruptedIOException("回环发送已停止");}
                        return super.read(bytes,start,Math.min(length,4096));
                    }
                };
            }
        };
    }
    private static void waitUntil(ChatService service,java.util.function.BooleanSupplier condition)throws Exception {
        long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(8);
        while(!condition.getAsBoolean()&&System.nanoTime()<deadline) {
            ((ExecutorService)field(service,"io")).submit(()->{}).get(5,TimeUnit.SECONDS);
            Shadows.shadowOf(android.os.Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(20));Thread.sleep(10);
        }
        assertTrue("等待传输状态超时："+service.transferStatus(),condition.getAsBoolean());
    }
    private static int taskIndex(BaseAdapter adapter,ChatStore.FileTask expected) {
        for(int index=0;index<adapter.getCount();index++) {
            ChatStore.FileTask item=(ChatStore.FileTask)adapter.getItem(index);
            if(item.peer.equals(expected.peer)&&item.message.number==expected.message.number&&item.message.outgoing==expected.message.outgoing&&item.file.id==expected.file.id)return index;
        }
        throw new AssertionError("任务未显示："+expected.file.name);
    }
    private static LinearLayout taskRow(MainActivity activity,ChatStore.FileTask item)throws Exception {
        BaseAdapter adapter=(BaseAdapter)field(activity,"taskAdapter");refresh(adapter);
        ListView parent=find(((Dialog)field(activity,"tasksDialog")).getWindow().getDecorView(),ListView.class);
        return (LinearLayout)adapter.getView(taskIndex(adapter,item),null,parent);
    }
    private static android.net.Uri documentTree(){return android.net.Uri.parse("content://feiq.documents/tree/root");}
    private static FileFeaturesTest.Docs documents(Context context) {
        return FileFeaturesTest.provider(context);
    }
    private static File guestFile(Context context,String id,String name,byte[] bytes,long time)throws Exception {
        File directory=new File(context.getFilesDir(),"received");assertTrue(directory.isDirectory()||directory.mkdirs());File local=new File(directory,id);Files.write(local.toPath(),bytes);
        assertTrue(context.getSharedPreferences("guest_received",0).edit().putString(id,name).putString("meta:"+id,new JSONObject().put("size",bytes.length).put("time",time).toString()).commit());return local;
    }
    private static List<ChatStore.FileTask> fileTasks(ChatService service)throws Exception {
        java.util.concurrent.atomic.AtomicReference<List<ChatStore.FileTask>> result=new java.util.concurrent.atomic.AtomicReference<>();service.fileTasks(result::set);settle(service);assertNotNull("任务列表回调未返回",result.get());return result.get();
    }
    private static void taskActions(MainActivity activity,ChatStore.FileTask task)throws Exception {java.lang.reflect.Method method=MainActivity.class.getDeclaredMethod("taskActions",ChatStore.FileTask.class);method.setAccessible(true);method.invoke(activity,task);}
    private static String taskState(MainActivity activity,ChatStore.FileTask task)throws Exception {java.lang.reflect.Method method=MainActivity.class.getDeclaredMethod("taskState",ChatStore.FileTask.class);method.setAccessible(true);return (String)method.invoke(activity,task);}
    private static void transferDone(ChatService service)throws Exception {((ExecutorService)field(service,"transferWorker")).submit(()->{}).get(5,TimeUnit.SECONDS);settle(service);}
    private static Button findButton(View view,String label) {if(view instanceof Button&&label.contentEquals(((Button)view).getText()))return (Button)view;if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){Button found=findButton(((ViewGroup)view).getChildAt(i),label);if(found!=null)return found;}return null;}
    private static String dialogText(Dialog dialog){StringBuilder text=new StringBuilder();dialogText(dialog.getWindow().getDecorView(),text);return text.toString();}
    private static void dialogText(View view,StringBuilder text) {if(view instanceof TextView&&!(view instanceof Button))text.append(((TextView)view).getText()).append('\n');if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++)dialogText(((ViewGroup)view).getChildAt(i),text);}
    private static void refresh(Object adapter)throws Exception {java.lang.reflect.Method refresh=adapter.getClass().getDeclaredMethod("refresh");refresh.setAccessible(true);refresh.invoke(adapter);}
    private static void progressRefresh(MainActivity activity){activity.transfersChanged();Shadows.shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(200));}
    private static void showTasks(MainActivity activity)throws Exception {java.lang.reflect.Method show=MainActivity.class.getDeclaredMethod("showTasks");show.setAccessible(true);show.invoke(activity);}
    private static ChatStore.FileTask task(String peer,long packet,String name,long size){LanNode.Message message=new LanNode.Message();message.number=packet;message.text=name;message.state="已收到";Protocol.Attachment file=new Protocol.Attachment(0,name,size,1);message.files.add(file);return new ChatStore.FileTask(peer,message,file);}
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
