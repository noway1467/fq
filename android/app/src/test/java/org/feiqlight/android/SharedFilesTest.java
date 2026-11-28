package org.feiqlight.android;

import android.content.*;
import android.content.pm.ProviderInfo;
import android.database.*;
import android.net.Uri;
import android.os.*;
import android.provider.DocumentsContract;
import android.provider.DocumentsProvider;
import org.feiqlight.android.core.ToolsHttp;
import org.feiqlight.android.core.SecureIdentity;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.ConscryptMode;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.*;
import java.util.concurrent.*;
import java.util.zip.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35)
public final class SharedFilesTest {
    private Context context;
    private Docs docs;
    private static final Uri TREE=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","root");
    @Before public void setup() throws Exception {
        context=RuntimeEnvironment.getApplication();new ToolsPairings(context).clear();docs=new Docs();ProviderInfo info=new ProviderInfo();info.authority="feiq.shared.test";info.exported=true;info.grantUriPermissions=true;info.readPermission=info.writePermission="android.permission.MANAGE_DOCUMENTS";docs.attachInfo(context,info);ShadowContentResolver.registerProviderInternal(info.authority,docs);
        docs.file=new File(context.getCacheDir(),"shared-sample.mp4");byte[] bytes=new byte[256];for(int i=0;i<bytes.length;i++)bytes[i]=(byte)i;Files.write(docs.file.toPath(),bytes);
    }
    public static final class Docs extends DocumentsProvider {
        File file;volatile boolean denied;volatile String openedMode="";
        final Map<String,Doc> documents=new HashMap<>();
        final Map<String,List<String>> children=new HashMap<>();
        final Map<String,Integer> opens=new ConcurrentHashMap<>();
        volatile String sortOrder="unset",slowParent="";
        volatile int delay;
        volatile Runnable afterOpen;
        volatile String triggerDocument="";
        volatile int triggerOpen;
        @Override public boolean onCreate(){return true;}
        private Cursor row(String id,String[] columns){
            if(denied)throw new SecurityException("权限已撤销");
            String[] names=columns==null?new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID}:columns;MatrixCursor cursor=new MatrixCursor(names);Object[] values=new Object[names.length];
            Doc doc=documents.get(id);
            for(int i=0;i<names.length;i++){
                if(names[i].equals(DocumentsContract.Document.COLUMN_DOCUMENT_ID))values[i]=id;
                if(names[i].equals(DocumentsContract.Document.COLUMN_DISPLAY_NAME))values[i]=doc!=null?doc.name:id.equals("root")?"共享根目录":id.equals("folder")?"空目录":"视频 English 😀.mp4";
                if(names[i].equals(DocumentsContract.Document.COLUMN_MIME_TYPE))values[i]=doc!=null?(doc.directory?DocumentsContract.Document.MIME_TYPE_DIR:"application/octet-stream"):id.equals("video")?"video/mp4":DocumentsContract.Document.MIME_TYPE_DIR;
                if(names[i].equals(DocumentsContract.Document.COLUMN_SIZE)){if(doc!=null)values[i]=doc.size;else values[i]=id.equals("video")?file.length():0;}
                if(names[i].equals(DocumentsContract.Document.COLUMN_LAST_MODIFIED)){if(doc!=null)values[i]=doc.modified;else values[i]=id.equals("video")?file.lastModified():null;}
            }
            cursor.addRow(values);return cursor;
        }
        @Override public Cursor queryRoots(String[] columns){return new MatrixCursor(new String[]{DocumentsContract.Root.COLUMN_ROOT_ID});}
        @Override public Cursor queryDocument(String id,String[] columns){return row(id,columns);}
        @Override public Cursor queryChildDocuments(String parent,String[] columns,String sort){
            if(denied)throw new SecurityException("权限已撤销");sortOrder=sort;
            if(parent.equals(slowParent))try{Thread.sleep(delay);}catch(InterruptedException error){Thread.currentThread().interrupt();throw new OperationCanceledException();}
            MatrixCursor result=new MatrixCursor(columns);
            List<String> selected=children.getOrDefault(parent,parent.equals("root")?Arrays.asList("video","folder"):Collections.emptyList());
            for(String id:selected)try(Cursor row=row(id,columns)){row.moveToFirst();Object[] values=new Object[columns.length];for(int i=0;i<values.length;i++)values[i]=row.getType(i)==Cursor.FIELD_TYPE_INTEGER?row.getLong(i):row.getString(i);result.addRow(values);}return result;
        }
        @Override public boolean isChildDocument(String parent,String child) {
            if(parent.equals("root")&&(child.equals("video")||child.equals("folder")))return true;
            Set<String> visited=new HashSet<>();Deque<String> pending=new ArrayDeque<>();pending.add(parent);
            while(!pending.isEmpty()){String current=pending.removeFirst();if(!visited.add(current))continue;for(String id:children.getOrDefault(current,Collections.emptyList())){if(id.equals(child))return true;if(documents.containsKey(id)&&documents.get(id).directory)pending.addLast(id);}}
            return false;
        }
        @Override public ParcelFileDescriptor openDocument(String id,String mode,CancellationSignal signal)throws FileNotFoundException {
            if(denied)throw new SecurityException("权限已撤销");Doc doc=documents.get(id);if(!id.equals("video")&&(doc==null||doc.directory))throw new FileNotFoundException();openedMode=mode;
            int count=opens.merge(id,1,Integer::sum);if(id.equals(triggerDocument)&&count==triggerOpen&&afterOpen!=null)afterOpen.run();
            return ParcelFileDescriptor.open(doc!=null&&doc.file!=null?doc.file:file,ParcelFileDescriptor.MODE_READ_ONLY);
        }
    }
    private static final class Doc {
        final String id,name;
        final boolean directory;
        final Long size;
        volatile Long modified;
        File file;
        Doc(String id,String name,boolean directory,Long size,Long modified){this.id=id;this.name=name;this.directory=directory;this.size=size;this.modified=modified;}
    }
    private static final class Reply {
        int code;String headers;byte[] body;
        JSONObject json()throws JSONException{return new JSONObject(new String(body,StandardCharsets.UTF_8));}
    }
    private static Reply request(int port,String path,String cookie,String data,String extra,String method)throws Exception {
        try(Socket socket=new Socket("127.0.0.1",port)){
            socket.setSoTimeout(10000);byte[] body=data==null?new byte[0]:data.getBytes(StandardCharsets.UTF_8);
            String header=method+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nCookie: "+cookie+"\r\n"+extra;
            if(data!=null)header+="Content-Type: application/json\r\nContent-Length: "+body.length+"\r\n";
            socket.getOutputStream().write((header+"Connection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));socket.getOutputStream().write(body);
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int count;while((count=socket.getInputStream().read(buffer))!=-1)bytes.write(buffer,0,count);
            byte[] result=bytes.toByteArray();int split=-1;for(int i=0;i+3<result.length;i++)if(result[i]==13&&result[i+1]==10&&result[i+2]==13&&result[i+3]==10){split=i;break;}
            assertTrue("缺少 HTTP 响应",split>=0);Reply reply=new Reply();reply.headers=new String(result,0,split,StandardCharsets.US_ASCII);reply.code=Integer.parseInt(reply.headers.split(" ")[1]);reply.body=Arrays.copyOfRange(result,split+4,result.length);return reply;
        }
    }
    private static Reply get(SharedFiles server,String path,String cookie)throws Exception{return request(server.port(),path,cookie,null,"","GET");}
    private static String login(SharedFiles server)throws Exception {Reply reply=request(server.port(),"/api/login","",new JSONObject().put("mode","share").put("code",server.code()).toString(),"","POST");assertEquals(new String(reply.body,StandardCharsets.UTF_8),200,reply.code);return "fq_share="+reply.json().getString("token");}
    @Test public void rangesAre64BitAndRejectMalformedOrMultipleRanges(){
        assertArrayEquals(new long[]{2147483650L,2999999999L},ToolsHttp.range("bytes=2147483650-",3000000000L));assertArrayEquals(new long[]{90,99},ToolsHttp.range("bytes=-10",100));
        for(String range:new String[]{"bytes=100-","bytes=-0","bytes=0-1,4-5","items=0-1","bytes=4-2","bytes=+1-2"})assertNull(range,ToolsHttp.range(range,100));assertNull(ToolsHttp.range("bytes=0-",0));
    }
    @Test public void realLoopbackListsAuthorizedSafDirectoryAndOnlyReadsFiles()throws Exception{
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            assertEquals(401,get(server,"/api/list","").code);String cookie=login(server);JSONArray items=get(server,"/api/list",cookie).json().getJSONArray("items");assertEquals(2,items.length());
            JSONObject video=items.getJSONObject(0);assertEquals("视频 English 😀.mp4",video.getString("name"));String path="/file?id="+video.getString("id");
            Reply all=get(server,path,cookie);assertEquals(200,all.code);assertArrayEquals(Files.readAllBytes(docs.file.toPath()),all.body);assertEquals("r",docs.openedMode);
            Reply head=request(server.port(),path,cookie,null,"","HEAD");assertEquals(200,head.code);assertEquals(0,head.body.length);assertTrue(head.headers.contains("Content-Length: 256"));
            Reply part=request(server.port(),path,cookie,null,"Range: bytes=40-59\r\n","GET");assertEquals(new String(part.body,StandardCharsets.UTF_8),206,part.code);assertArrayEquals(Arrays.copyOfRange(all.body,40,60),part.body);
            assertEquals(416,request(server.port(),path,cookie,null,"Range: bytes=256-\r\n","GET").code);
            assertEquals(0,get(server,"/api/list?id="+items.getJSONObject(1).getString("id"),cookie).json().getJSONArray("items").length());
        }
    }
    @Test public void rejectsTraversalWriteCrossOriginAndRemoteElevation()throws Exception{
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            String cookie=login(server);assertEquals(404,get(server,"/file?id=..%2foutside",cookie).code);
            assertEquals(405,request(server.port(),"/file",cookie,null,"","DELETE").code);
            assertEquals(403,request(server.port(),"/api/list",cookie,null,"Origin: https://evil.invalid\r\n","GET").code);
            assertEquals(403,request(server.port(),"/api/login","",new JSONObject().put("mode","remote").put("code",server.code()).toString(),"","POST").code);
            assertEquals(403,request(server.port(),"/api/logout",cookie,"{\"mode\":\"share\"}","","POST").code);
            assertEquals(200,request(server.port(),"/api/logout",cookie,"{\"mode\":\"share\"}","X-FQ-Token: "+cookie.substring(9)+"\r\n","POST").code);assertEquals(401,get(server,"/api/list",cookie).code);
        }
    }
    @Test public void permissionRevocationRefusesFurtherReadsAndAssetsAreBundled()throws Exception{
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            for(String path:new String[]{"/","/app.css","/app.js"})assertEquals(200,get(server,path,"").code);
            String cookie=login(server);String id=get(server,"/api/list",cookie).json().getJSONArray("items").getJSONObject(0).getString("id");docs.denied=true;
            assertEquals(500,get(server,"/api/list",cookie).code);assertEquals(500,get(server,"/file?id="+id,cookie).code);assertEquals(256,docs.file.length());
        }
    }
    @Test public void closesListenerAndDoesNotPersistGrantOrCode()throws Exception{
        SharedFiles server=new SharedFiles(context,TREE,0,true);int port=server.port();login(server);server.close();
        assertTrue(context.getContentResolver().getPersistedUriPermissions().isEmpty());
        try(Socket ignored=new Socket("127.0.0.1",port)){fail("关闭后仍接受连接");}catch(ConnectException expected){}
    }
    @Test public void sixDigitCodeAndRememberedDeviceSurviveServerRestartButNotRevocation()throws Exception{
        String pair;int port;
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            assertTrue(server.code().matches("[0-9]{6}"));port=server.port();
            Reply login=request(port,"/api/login","",new JSONObject().put("mode","share").put("code",server.code()).toString(),"","POST");assertEquals(200,login.code);
            pair=Arrays.stream(login.headers.split("\r\n")).filter(s->s.startsWith("Set-Cookie: fq_pair_")).findFirst().get().substring(12).split(";")[0];
            assertTrue(login.headers.contains("HttpOnly; SameSite=Strict; Max-Age=7776000"));
            assertFalse(context.getSharedPreferences("tools_pairings",Context.MODE_PRIVATE).getAll().toString().contains(pair.split("=")[1]));
        }
        try(SharedFiles server=new SharedFiles(context,TREE,port,true)){
            assertEquals(403,request(port,"/api/reconnect",pair,"{\"mode\":\"share\"}","","POST").code);
            Reply reconnect=request(port,"/api/reconnect",pair,"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n","POST");assertEquals(200,reconnect.code);
            String session="fq_share="+reconnect.json().getString("token");assertEquals(200,get(server,"/api/list",session).code);
            server.forgetPairings();assertEquals(401,get(server,"/api/list",session).code);
            assertEquals(401,request(port,"/api/reconnect",pair,"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n","POST").code);
        }
        assertTrue(context.getContentResolver().getPersistedUriPermissions().isEmpty());
    }
    @Test public void pairingsAreScopedAndExpire()throws Exception{
        ToolsPairings pairings=new ToolsPairings(context);String token=pairings.remember("","share:one");
        assertTrue(pairings.contains(token,"share:one"));assertFalse(pairings.contains(token,"share:two"));assertFalse(pairings.contains(token,"remote"));
        android.content.SharedPreferences prefs=context.getSharedPreferences("tools_pairings",Context.MODE_PRIVATE);String key=prefs.getAll().keySet().iterator().next();prefs.edit().putString(key,"share:one|1").commit();assertFalse(pairings.contains(token,"share:one"));
    }
    @Test public void videoRangesReuseSocketAndRecheckRevokedSession()throws Exception{
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            String cookie=login(server),id=get(server,"/api/list",cookie).json().getJSONArray("items").getJSONObject(0).getString("id");
            try(Socket socket=new Socket("127.0.0.1",server.port())){
                socket.setSoTimeout(4000);
                for(int request=0;request<4;request++){
                    if(request==3)server.forgetPairings();
                    String method=request==1?"HEAD":"GET";
                    String header=method+" /file?id="+id+" HTTP/1.1\r\nHost: 127.0.0.1:"+server.port()+"\r\nCookie: "+cookie+"\r\nRange: bytes=40-59\r\nConnection: keep-alive\r\n\r\n";socket.getOutputStream().write(header.getBytes(StandardCharsets.US_ASCII));
                    ByteArrayOutputStream bytes=new ByteArrayOutputStream();int last=0;
                    while(last!=0x0d0a0d0a){int b=socket.getInputStream().read();assertTrue("复用连接提前结束",b>=0);bytes.write(b);last=(last<<8)|b;}
                    String response=bytes.toString("US-ASCII");assertTrue(response.startsWith("HTTP/1.1 "+(request==3?401:206)));
                    assertTrue(response.contains(request==3?"Connection: close":"Connection: keep-alive"));
                    int length=method.equals("HEAD")?0:Integer.parseInt(Arrays.stream(response.split("\r\n")).filter(s->s.startsWith("Content-Length:")).findFirst().get().substring(15).trim());
                    byte[] body=socket.getInputStream().readNBytes(length);assertEquals(length,body.length);
                    if(request!=1&&request!=3)assertArrayEquals(Arrays.copyOfRange(Files.readAllBytes(docs.file.toPath()),40,60),body);
                    if(request==3)assertEquals(-1,socket.getInputStream().read());
                }
            }
        }
    }
    @Test public void sharedDirectoryAndReadOnlyGrantSurviveServiceReplacement()throws Exception{
        SharedFolderSettings saved=new SharedFolderSettings(context);
        assertNull(saved.tree());assertFalse(saved.enabled());
        saved.acquire(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);saved.save(TREE);
        SharedFolderSettings next=new SharedFolderSettings(context);assertEquals(TREE,next.tree());assertTrue(next.enabled());next.requireGrant(TREE);
        android.content.UriPermission permission=context.getContentResolver().getPersistedUriPermissions().get(0);assertTrue(permission.isReadPermission());assertFalse(permission.isWritePermission());
        try(SharedFiles server=new SharedFiles(context,next.tree(),0,true)){String cookie=login(server);assertEquals(200,get(server,"/api/list",cookie).code);}
        assertTrue(new SharedFolderSettings(context).enabled());
        next.stop();SharedFolderSettings stopped=new SharedFolderSettings(context);assertEquals(TREE,stopped.tree());assertFalse(stopped.enabled());stopped.requireGrant(TREE);
        context.getContentResolver().releasePersistableUriPermission(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try{stopped.requireGrant(TREE);fail("已撤销目录权限仍可恢复");}catch(IOException expected){}
        assertEquals(TREE,new SharedFolderSettings(context).tree());
    }
    @Test public void legacySettingsAndPairingFormatRemainUsableWithoutReset()throws Exception{
        // 固定旧版键名和存储格式，避免读写一起改名后新建数据测试仍通过。
        context.getContentResolver().takePersistableUriPermission(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION);
        SharedPreferences folders=context.getSharedPreferences("shared_folder",Context.MODE_PRIVATE);
        assertTrue(folders.edit().putString("tree",TREE.toString()).putBoolean("enabled",true).commit());
        String token=String.join("",Collections.nCopies(64,"a"));
        String hash=Base64.getEncoder().encodeToString(java.security.MessageDigest.getInstance("SHA-256").digest(token.getBytes(StandardCharsets.UTF_8)));
        SharedPreferences pairings=context.getSharedPreferences("tools_pairings",Context.MODE_PRIVATE);
        assertTrue(pairings.edit().putString(hash,"share:"+TREE+"|"+(System.currentTimeMillis()+86400000L)).commit());
        Map<String,?> folderBefore=new HashMap<>(folders.getAll()),pairBefore=new HashMap<>(pairings.getAll());
        SharedFolderSettings restored=new SharedFolderSettings(context);assertTrue(restored.enabled());assertEquals(TREE,restored.tree());restored.requireGrant(TREE);
        try(SharedFiles server=new SharedFiles(context,restored.tree(),0,true)){
            Reply reconnect=request(server.port(),"/api/reconnect","fq_pair_share_"+server.port()+"="+token,"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n","POST");
            assertEquals(200,reconnect.code);assertEquals(200,get(server,"/api/list","fq_share="+reconnect.json().getString("token")).code);
        }
        assertEquals(folderBefore,folders.getAll());assertEquals(pairBefore,pairings.getAll());
        String remoteToken=String.join("",Collections.nCopies(64,"b"));
        String remoteHash=Base64.getEncoder().encodeToString(java.security.MessageDigest.getInstance("SHA-256").digest(remoteToken.getBytes(StandardCharsets.UTF_8)));
        assertTrue(pairings.edit().putString(remoteHash,"phone-remote|"+(System.currentTimeMillis()+86400000L)).putBoolean("allow:"+remoteHash,true).commit());
        assertTrue(new ToolsPairings(context).alwaysAllows(remoteToken,"phone-remote"));
        SharedPreferences settings=context.getSharedPreferences("settings",Context.MODE_PRIVATE);
        assertTrue(settings.edit().putString("last_remote_target","http://127.0.0.1:2426/#remote").putBoolean("last_remote_phone",false).commit());
        Intent last=RemoteActivity.lastConnection(context);assertNotNull(last);assertEquals("http://127.0.0.1:2426/#remote",last.getData().toString());
    }
    @Test public void unsupportedTemporaryGrantIsNotSilentlySaved()throws Exception{
        SharedFolderSettings settings=new SharedFolderSettings(context);
        try{settings.acquire(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION);fail("临时授权被当成可恢复");}catch(IOException expected){}
        assertNull(settings.tree());assertFalse(settings.enabled());
    }
    @Test public void chatServiceRestoresDirectoryButManualStopStaysStopped()throws Exception{
        SharedFolderSettings settings=new SharedFolderSettings(context);settings.acquire(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);settings.save(TREE);
        for(int generation=0;generation<3;generation++){
            org.robolectric.android.controller.ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
            final int chatPort=TestPorts.withTools();
            service.connectionFactory=(prefs,listener)->new org.feiqlight.android.core.LanNode(chatPort,true,"restore-test","Android","恢复测试","test",listener);
            service.sharedFactory=(tree,port)->new SharedFiles(context,tree,0,true);
            try{
                service.onStartCommand(new Intent(context,ChatService.class),0,1);
                long until=System.currentTimeMillis()+10000;
                while((!service.online()||(generation<2&&service.sharedFiles()==null))&&System.currentTimeMillis()<until){org.robolectric.Shadows.shadowOf(Looper.getMainLooper()).idle();Thread.sleep(20);}
                assertTrue("聊天服务未启动："+service.status(),service.online());
                if(generation==0){
                    SharedFiles before=service.sharedFiles();assertNotNull(before);
                    Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");settings.acquire(second,Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
                    String code=before.code(),cookie=login(before);List<Boolean> updated=new ArrayList<>();service.shareFolder(second,updated::add);
                    long updateDeadline=System.currentTimeMillis()+5000;
                    while(updated.isEmpty()&&System.currentTimeMillis()<updateDeadline){org.robolectric.Shadows.shadowOf(Looper.getMainLooper()).idle();Thread.sleep(20);}
                    assertEquals(Collections.singletonList(true),updated);assertSame(before,service.sharedFiles());assertEquals(code,before.code());assertEquals(2,get(before,"/api/list",cookie).json().getJSONArray("items").length());
                    updated.clear();service.removeSharedFolder(second,updated::add);updateDeadline=System.currentTimeMillis()+5000;
                    while(updated.isEmpty()&&System.currentTimeMillis()<updateDeadline){org.robolectric.Shadows.shadowOf(Looper.getMainLooper()).idle();Thread.sleep(20);}
                    assertEquals(Collections.singletonList(true),updated);assertSame(before,service.sharedFiles());assertEquals(200,get(before,"/api/list",cookie).code);
                    java.lang.reflect.Field field=ChatService.class.getDeclaredField("node");field.setAccessible(true);
                    org.feiqlight.android.core.LanNode node=(org.feiqlight.android.core.LanNode)field.get(service);
                    java.lang.reflect.Method change=node.getClass().getDeclaredMethod("switchNetwork",Object.class);change.setAccessible(true);
                    change.invoke(node,new Object[]{null});node.refresh();assertTrue(node.networkAvailable());
                    long deadline=System.currentTimeMillis()+5000;
                    while((service.sharedFiles()==null || service.sharedFiles()==before) && System.currentTimeMillis()<deadline){org.robolectric.Shadows.shadowOf(Looper.getMainLooper()).idle();Thread.sleep(20);}
                    assertNotSame("切网后不能保留旧共享会话",before,service.sharedFiles());
                }
                if(generation<2){assertNotNull("重连未恢复共享",service.sharedFiles());String cookie=login(service.sharedFiles());assertEquals(200,get(service.sharedFiles(),"/api/list",cookie).code);if(generation==1)service.stopSharing();}
                else{assertNull(service.sharedFiles());assertFalse(new SharedFolderSettings(context).enabled());assertEquals(TREE,new SharedFolderSettings(context).tree());}
            }finally{controller.destroy();java.lang.reflect.Field executor=ChatService.class.getDeclaredField("io");executor.setAccessible(true);assertTrue(((java.util.concurrent.ExecutorService)executor.get(service)).awaitTermination(5,java.util.concurrent.TimeUnit.SECONDS));}
        }
    }
    @Test public void statusReportsPairingAndNativeEndpointsReturnNoContent()throws Exception{
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            Reply anonymous=get(server,"/api/status","");
            assertEquals(200,anonymous.code);assertTrue(anonymous.json().getBoolean("share"));assertFalse(anonymous.json().getBoolean("paired"));
            Reply loginReply=request(server.port(),"/api/login","",new JSONObject().put("mode","share").put("code",server.code()).toString(),"","POST");
            assertEquals(200,loginReply.code);
            String pair=Arrays.stream(loginReply.headers.split("\r\n")).filter(s->s.startsWith("Set-Cookie: fq_pair_")).findFirst().get().substring(12).split(";")[0];
            Reply paired=get(server,"/api/status",pair);
            assertEquals(200,paired.code);assertTrue(paired.json().getBoolean("paired"));
            assertEquals(204,get(server,"/native/paired","").code);
            assertEquals(204,get(server,"/native/unavailable","").code);
        }
    }
    @Test public void multipleRootsKeepTheirOwnTreeAndRevokeOldScope()throws Exception {
        Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");String pair;
        try(SharedFiles server=new SharedFiles(context,Arrays.asList(TREE,second),0,true)) {
            Reply reply=request(server.port(),"/api/login","",new JSONObject().put("mode","share").put("code",server.code()).toString(),"","POST");
            pair=Arrays.stream(reply.headers.split("\r\n")).filter(s->s.startsWith("Set-Cookie: fq_pair_")).findFirst().get().substring(12).split(";")[0];
            String cookie="fq_share="+reply.json().getString("token");JSONArray roots=get(server,"/api/list",cookie).json().getJSONArray("items");assertEquals(2,roots.length());
            assertEquals(2,get(server,"/api/list?id="+roots.getJSONObject(0).getString("id"),cookie).json().getJSONArray("items").length());
            assertEquals(0,get(server,"/api/list?id="+roots.getJSONObject(1).getString("id"),cookie).json().getJSONArray("items").length());
        }
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            assertEquals(401,request(server.port(),"/api/reconnect",pair,"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n","POST").code);
        }
    }
    @Test public void updatingFoldersKeepsSessionCodeAndPairingButRevokesRemovedIds()throws Exception {
        Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)){
            String code=server.code();Reply reply=request(server.port(),"/api/login","",new JSONObject().put("mode","share").put("code",code).toString(),"","POST");
            String pair=Arrays.stream(reply.headers.split("\r\n")).filter(s->s.startsWith("Set-Cookie: fq_pair_")).findFirst().get().substring(12).split(";")[0];
            String cookie="fq_share="+reply.json().getString("token");JSONArray before=get(server,"/api/list",cookie).json().getJSONArray("items");String file=before.getJSONObject(0).getString("id");
            int revision=get(server,"/api/share-status",cookie).json().getInt("revision");
            server.updateFolders(Arrays.asList(TREE,second));assertEquals(code,server.code());assertEquals(2,get(server,"/api/list",cookie).json().getJSONArray("items").length());
            assertTrue(get(server,"/api/share-status",cookie).json().getInt("revision")>revision);assertEquals(200,get(server,"/file?id="+file,cookie).code);
            assertEquals(200,request(server.port(),"/api/reconnect",pair,"{\"mode\":\"share\"}","X-FQ-Reconnect: 1\r\n","POST").code);
            server.updateFolders(Collections.singletonList(second));assertEquals(404,get(server,"/file?id="+file,cookie).code);assertEquals(200,get(server,"/api/list",cookie).code);
        }
        assertEquals("image/png",SharedFiles.previewMime("图片.png"));assertEquals("application/pdf",SharedFiles.previewMime("doc.pdf"));assertEquals("text/plain; charset=utf-8",SharedFiles.previewMime("data.json"));
    }
    @Test public void legacyFolderMigratesWithoutLosingGrants()throws Exception {
        Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");SharedFolderSettings settings=new SharedFolderSettings(context);
        int flags=Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION;
        settings.acquire(TREE,flags);settings.acquire(second,flags);
        context.getSharedPreferences("shared_folder",0).edit().clear().putString("tree",TREE.toString()).putBoolean("enabled",true).commit();
        assertEquals(Collections.singletonList(TREE),settings.trees());settings.save(Arrays.asList(TREE,second,TREE),true);
        assertEquals(Arrays.asList(TREE,second),new SharedFolderSettings(context).trees());settings.stop();assertFalse(settings.enabled());assertEquals(2,settings.trees().size());
        settings.save(Collections.singletonList(second),false);assertEquals(Collections.singletonList(second),settings.trees());assertEquals(2,context.getContentResolver().getPersistedUriPermissions().size());
    }
    private Doc add(String parent,String id,String name,boolean directory,Long size,Long modified) {
        Doc doc=new Doc(id,name,directory,size,modified);docs.documents.put(id,doc);docs.children.computeIfAbsent(parent,key->new ArrayList<>()).add(id);return doc;
    }
    private static Object field(Object value,String name)throws Exception{java.lang.reflect.Field field=value.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(value);}
    private static void setField(Object value,String name,Object data)throws Exception{java.lang.reflect.Field field=value.getClass().getDeclaredField(name);field.setAccessible(true);field.set(value,data);}
    private static List<String> names(JSONArray items)throws JSONException{List<String> names=new ArrayList<>();for(int index=0;index<items.length();index++)names.add(items.getJSONObject(index).getString("name"));return names;}
    private static boolean eocd(byte[] bytes){return bytes.length>=22&&(bytes[bytes.length-22]&255)==0x50&&(bytes[bytes.length-21]&255)==0x4b&&(bytes[bytes.length-20]&255)==5&&(bytes[bytes.length-19]&255)==6;}
    private static long contentLength(Reply reply){return Long.parseLong(Arrays.stream(reply.headers.split("\r\n")).filter(line->line.startsWith("Content-Length:")).findFirst().orElseThrow().substring(15).trim());}
    private static final class ArchiveConnection implements AutoCloseable {
        final Socket socket=new Socket(),transport;
        final Reply reply=new Reply();
        ArchiveConnection(SharedFiles server,String id,String cookie)throws Exception{this(server,id,cookie,null,null);}
        ArchiveConnection(SharedFiles server,String id,String cookie,SecureIdentity identity,String fingerprint)throws Exception {
            socket.setReceiveBufferSize(65536);socket.connect(new InetSocketAddress("127.0.0.1",server.port()));socket.setSoTimeout(15000);
            transport=identity==null?socket:identity.client(socket,fingerprint);transport.setSoTimeout(15000);
            String header="GET /file?id="+id+"&archive=store HTTP/1.1\r\nHost: 127.0.0.1:"+server.port()+"\r\nCookie: "+cookie+"\r\nConnection: close\r\n\r\n";transport.getOutputStream().write(header.getBytes(StandardCharsets.US_ASCII));
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();int last=0;
            while(last!=0x0d0a0d0a){int value=transport.getInputStream().read();if(value<0)throw new EOFException("归档响应头提前关闭");bytes.write(value);last=(last<<8)|value;}
            reply.headers=bytes.toString("US-ASCII");reply.code=Integer.parseInt(reply.headers.split(" ")[1]);
        }
        byte[] readBody()throws IOException {
            ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[262144];
            try{int count;while((count=transport.getInputStream().read(buffer))>=0)bytes.write(buffer,0,count);}catch(SocketException expected){/* 保留实际断流字节，下面仍校验长度和 EOCD。 */}
            return bytes.toByteArray();
        }
        @Override public void close()throws IOException{transport.close();socket.close();}
    }
    private static InputStream archiveInput(SharedFiles server,String cookie)throws Exception {
        synchronized(server){for(Object archive:(List<?>)field(server,"archives"))if(field(field(archive,"session"),"token").equals(cookie.substring(9)))return (InputStream)field(archive,"input");}
        return null;
    }
    private static boolean open(InputStream input){return input instanceof FileInputStream&&((FileInputStream)input).getChannel().isOpen();}
    private static InputStream awaitArchive(SharedFiles server,String cookie)throws Exception {
        long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(3);InputStream input;
        do{input=archiveInput(server,cookie);if(open(input))return input;Thread.sleep(5);}while(System.nanoTime()<until);
        fail("没有进入活跃归档读取");return null;
    }
    private Doc large(String parent,String id)throws Exception {
        File file=new File(context.getCacheDir(),id+".bin");try(RandomAccessFile output=new RandomAccessFile(file,"rw")){output.setLength(16*1024*1024);}
        Doc doc=add(parent,id,id+".bin",false,file.length(),file.lastModified());doc.file=file;return doc;
    }
    @Test public void largeDirectoryKeepsLegacy409AndPagesWithoutDuplicatesOrMissingSearchHits()throws Exception {
        for(int index=0;index<1123;index++)add("root","file-"+index,String.format(Locale.ROOT,"item-%04d.txt",index),false,(long)(index%5),1000L+index);
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            assertTrue(get(server,"/api/status","").json().getBoolean("listPaging"));String cookie=login(server);
            assertEquals(409,get(server,"/api/list",cookie).code);assertEquals(409,get(server,"/api/files",cookie).code);
            Reply page=get(server,"/api/files?limit=200&sort=name&order=asc",cookie);assertEquals(201,((Map<?,?>)field(server,"entries")).size());
            add("root","new-file","added-after-snapshot.txt",false,0L,null);
            Set<String> ids=new HashSet<>(),names=new HashSet<>();int pages=0;
            while(true) {
                assertEquals(200,page.code);JSONObject result=page.json();assertFalse(result.getBoolean("truncated"));JSONArray items=result.getJSONArray("items");
                for(int index=0;index<items.length();index++){JSONObject item=items.getJSONObject(index);assertTrue(ids.add(item.getString("id")));assertTrue(names.add(item.getString("name")));assertTrue(item.has("mtime"));}
                String cursor=result.isNull("nextCursor")?"":result.getString("nextCursor");if(cursor.isEmpty())break;assertTrue(++pages<10);page=get(server,"/api/files?limit=200&sort=name&order=asc&cursor="+cursor,cookie);
            }
            assertEquals(1123,ids.size());assertEquals(1123,names.size());assertFalse(names.contains("added-after-snapshot.txt"));
            JSONArray found=get(server,"/api/list?limit=100&q=ITEM-1122",cookie).json().getJSONArray("items");assertEquals(1,found.length());assertEquals("item-1122.txt",found.getJSONObject(0).getString("name"));
            assertEquals(100,get(server,"/api/list?sort=name",cookie).json().getJSONArray("items").length());assertEquals(200,get(server,"/api/list?limit=999",cookie).json().getJSONArray("items").length());assertNull(docs.sortOrder);
        }
    }
    @Test public void serverSortsAllFieldsWithDirectoriesFirstUnknownLastAndStableSourceTies()throws Exception {
        add("root","dir","z-dir",true,0L,null);add("root","a","a-known",false,1L,1000L);add("root","z","z-known",false,9L,3000L);add("root","unknown","unknown",false,null,null);
        add("root","dup-1","dup",false,1L,1000L);add("root","dup-2","dup",false,1L,1000L);add("root","e-1","é",false,1L,1000L);add("root","e-2","e\u0301",false,1L,1000L);
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            String cookie=login(server);List<String> ties=null;
            for(String sort:Arrays.asList("name","size","mtime"))for(String order:Arrays.asList("asc","desc")) {
                JSONArray items=get(server,"/api/list?limit=100&sort="+sort+"&order="+order,cookie).json().getJSONArray("items");assertEquals("z-dir",items.getJSONObject(0).getString("name"));
                if(!sort.equals("name")) {
                    assertEquals("unknown",items.getJSONObject(items.length()-1).getString("name"));assertTrue(items.getJSONObject(items.length()-1).isNull("mtime"));
                    List<String> current=new ArrayList<>();for(int index=0;index<items.length();index++)if(items.getJSONObject(index).getString("name").equals("dup"))current.add(items.getJSONObject(index).getString("id"));
                    if(ties==null)ties=current;else assertEquals(ties,current);
                }
                if(order.equals("desc")&&!sort.equals("name"))assertEquals("z-known",items.getJSONObject(1).getString("name"));
            }
            assertEquals(Arrays.asList("z-dir","é","e\u0301","z-known","unknown","dup","dup","a-known"),names(get(server,"/api/list?sort=name&order=desc",cookie).json().getJSONArray("items")));
            Set<String> legacyFields=new HashSet<>();get(server,"/api/files",cookie).json().keys().forEachRemaining(legacyFields::add);assertEquals(new HashSet<>(Arrays.asList("name","revision","items")),legacyFields);
        }
    }
    @Test public void opaqueCursorsBindSessionDirectoryConditionsRevisionTtlAndSnapshotBudget()throws Exception {
        Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            String cookie=login(server),other=login(server);JSONObject page=get(server,"/api/list?limit=1",cookie).json();String cursor=page.getString("nextCursor");assertTrue(cursor.matches("[0-9a-f]{64}"));
            assertEquals(410,get(server,"/api/list?limit=1&cursor="+cursor,other).code);
            for(String extra:Arrays.asList("&q=video","&sort=size","&order=desc","&id="+page.getString("id")))assertEquals(409,get(server,"/api/list?limit=1&cursor="+cursor+extra,cookie).code);
            for(String value:Arrays.asList("0","-1","abc"))assertEquals(400,get(server,"/api/list?limit="+value,cookie).code);
            Object session=((List<?>)field(server,"sessions")).get(0);List<?> snapshots=(List<?>)field(session,"snapshots");setField(snapshots.get(0),"created",-SharedFiles.SNAPSHOT_LIFETIME);assertEquals(410,get(server,"/api/list?limit=1&cursor="+cursor,cookie).code);
            String first=get(server,"/api/list?limit=1",cookie).json().getString("nextCursor");for(int index=0;index<4;index++)get(server,"/api/list?limit=1",cookie);assertEquals(4,snapshots.size());assertEquals(410,get(server,"/api/list?limit=1&cursor="+first,cookie).code);
            cursor=get(server,"/api/list?limit=1",cookie).json().getString("nextCursor");server.updateFolders(Arrays.asList(TREE,second));assertEquals(409,get(server,"/api/list?limit=1&cursor="+cursor,cookie).code);
            JSONArray roots=get(server,"/api/list?limit=100",cookie).json().getJSONArray("items");String removed=roots.getJSONObject(0).getString("id");server.updateFolders(Collections.singletonList(second));assertEquals(404,get(server,"/api/list?limit=100&id="+removed,cookie).code);
            assertEquals(((Map<?,?>)field(server,"entries")).size(),((Map<?,?>)field(server,"entryIds")).size());
        }
    }
    @Test public void scanAndIdBudgetsBoundLargeDirectoriesAndReportIncompleteResults()throws Exception {
        for(int index=0;index<=SharedFiles.LIST_SCAN_LIMIT;index++)add("root","f"+index,String.format(Locale.ROOT,"file-%05d",index),false,0L,null);
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            String cookie=login(server);Reply page=get(server,"/api/list?limit=200",cookie);assertTrue(page.json().getBoolean("truncated"));Set<String> names=new HashSet<>();
            while(true) {
                JSONArray items=page.json().getJSONArray("items");for(int index=0;index<items.length();index++)assertTrue(names.add(items.getJSONObject(index).getString("name")));
                assertTrue(((Map<?,?>)field(server,"entries")).size()<=10000);assertEquals(((Map<?,?>)field(server,"entries")).size(),((Map<?,?>)field(server,"entryIds")).size());
                if(page.json().isNull("nextCursor"))break;page=get(server,"/api/list?limit=200&cursor="+page.json().getString("nextCursor"),cookie);
            }
            assertTrue(names.size()>1000);assertTrue(names.size()<=SharedFiles.LIST_SCAN_LIMIT);assertEquals(10000,((Map<?,?>)field(server,"entries")).size());
        }
    }
    @Test public void slowSafProviderHasTimeBudgetAndDoesNotHoldAuthorizationLock()throws Exception {
        docs.slowParent="root";docs.delay=2500;Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            String cookie=login(server);long start=System.nanoTime();Reply limited=get(server,"/api/list?limit=100",cookie);assertEquals(200,limited.code);assertTrue(limited.json().getBoolean("truncated"));assertTrue(System.nanoTime()-start<TimeUnit.SECONDS.toNanos(4));
            ExecutorService worker=Executors.newSingleThreadExecutor();
            try {
                Future<Reply> listing=worker.submit(()->get(server,"/api/list?limit=100",cookie));long until=System.nanoTime()+TimeUnit.SECONDS.toNanos(2);
                while(!(boolean)field(((List<?>)field(server,"sessions")).get(0),"listing")&&System.nanoTime()<until)Thread.sleep(5);
                start=System.nanoTime();server.updateFolders(Collections.singletonList(second));assertTrue("目录读取占用了授权锁",System.nanoTime()-start<TimeUnit.MILLISECONDS.toNanos(500));assertEquals(409,listing.get(5,TimeUnit.SECONDS).code);
            }finally{worker.shutdownNow();assertTrue(worker.awaitTermination(5,TimeUnit.SECONDS));}
        }
    }
    @Test public void sharedZipUsesRealIdsAndExistingAuthorizationAndStoreUtf8Contracts()throws Exception {
        Doc content=add("root","text","中文 +%#😀.txt",false,(long)docs.file.length(),docs.file.lastModified());content.file=docs.file;
        Doc zero=add("root","zero","empty.txt",false,0L,null);zero.file=new File(context.getCacheDir(),"zip-empty");Files.write(zero.file.toPath(),new byte[0]);
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            String cookie=login(server),id=get(server,"/api/list?limit=100",cookie).json().getString("id"),path="/file?id="+id+"&archive=store";
            assertEquals(401,get(server,path,"").code);assertEquals(401,request(server.port(),path,"",null,"X-FQ-Guest: fake\r\n","GET").code);assertEquals(404,get(server,"/file?archive=store",cookie).code);assertEquals(404,get(server,"/file?id="+id,cookie).code);
            assertEquals(403,request(server.port(),path,cookie,null,"Origin: https://evil.invalid\r\n","GET").code);assertEquals(416,request(server.port(),path,cookie,null,"Range: bytes=0-5\r\n","GET").code);
            Reply reply=get(server,path,cookie);assertEquals(new String(reply.body,StandardCharsets.UTF_8),200,reply.code);assertTrue(eocd(reply.body));assertEquals(reply.body.length,contentLength(reply));assertTrue(reply.headers.contains("application/zip"));assertTrue(reply.headers.contains("Content-Disposition: attachment"));
            try(ZipInputStream zip=new ZipInputStream(new ByteArrayInputStream(reply.body))) {
                ZipEntry entry=zip.getNextEntry();assertEquals("中文 +%#😀.txt",entry.getName());assertEquals(ZipEntry.STORED,entry.getMethod());assertArrayEquals(Files.readAllBytes(docs.file.toPath()),zip.readAllBytes());assertEquals("empty.txt",zip.getNextEntry().getName());assertEquals(0,zip.readAllBytes().length);assertNull(zip.getNextEntry());
            }
            Reply head=request(server.port(),path,cookie,null,"","HEAD");assertEquals(200,head.code);assertEquals(0,head.body.length);assertEquals(reply.body.length,contentLength(head));
        }
    }
    @Test public void zipBoundsRejectFileCountDirectoryCountDepthCyclesAndUnsafeNames()throws Exception {
        for(String mode:Arrays.asList("files","directories","depth","cycle","unsafe")) {
            docs.documents.clear();docs.children.clear();
            if(mode.equals("files")||mode.equals("directories"))for(int index=0;index<1001;index++)add("root","n"+index,"n"+index,mode.equals("directories"),0L,null);
            else if(mode.equals("depth")){String parent="root";for(int index=0;index<33;index++){add(parent,"d"+index,"d",true,0L,null);parent="d"+index;}}
            else if(mode.equals("cycle"))docs.children.put("root",Collections.singletonList("root"));else add("root","unsafe","../outside.txt",false,0L,null);
            try(SharedFiles server=new SharedFiles(context,TREE,0,true)){String cookie=login(server),id=get(server,"/api/list?limit=1",cookie).json().getString("id");assertEquals(mode,409,get(server,"/file?id="+id+"&archive=store",cookie).code);}
        }
    }
    @Test public void removingRootClosesItsSocketAndFileHandleButOtherArchiveCompletes()throws Exception {
        Uri second=DocumentsContract.buildTreeDocumentUri("feiq.shared.test","folder");large("root","first");large("folder","second");
        try(SharedFiles server=new SharedFiles(context,Arrays.asList(TREE,second),0,true)) {
            String a=login(server),b=login(server);JSONArray roots=get(server,"/api/list?limit=100",a).json().getJSONArray("items");String first=roots.getJSONObject(0).getString("id"),other=roots.getJSONObject(1).getString("id");
            try(ArchiveConnection downloadA=new ArchiveConnection(server,first,a);ArchiveConnection downloadB=new ArchiveConnection(server,other,b)) {
                assertEquals(200,downloadA.reply.code);assertEquals(200,downloadB.reply.code);InputStream inputA=awaitArchive(server,a),inputB=awaitArchive(server,b);
                long start=System.nanoTime();server.updateFolders(Collections.singletonList(second));assertTrue(System.nanoTime()-start<TimeUnit.MILLISECONDS.toNanos(500));assertFalse(open(inputA));assertTrue(open(inputB));
                byte[] partial=downloadA.readBody();assertTrue(partial.length<contentLength(downloadA.reply));assertFalse(eocd(partial));byte[] complete=downloadB.readBody();assertEquals(contentLength(downloadB.reply),complete.length);assertTrue(eocd(complete));assertEquals(200,get(server,"/api/list?limit=100",b).code);
            }
        }
    }
    @Test public void sourceNotificationMetadataChangeStopAndLogoutInterruptArchivesWithoutEocd()throws Exception {
        for(String mode:Arrays.asList("notification","metadata","close","logout")) {
            docs.documents.clear();docs.children.clear();Doc large=large("root",mode);
            try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
                String cookie=login(server),id=get(server,"/api/list?limit=1",cookie).json().getString("id");
                try(ArchiveConnection download=new ArchiveConnection(server,id,cookie)) {
                    assertEquals(200,download.reply.code);InputStream input=awaitArchive(server,cookie);long start=System.nanoTime();
                    if(mode.equals("notification")){large.modified++;context.getContentResolver().notifyChange(TREE,null);}
                    else if(mode.equals("metadata"))large.modified++;else if(mode.equals("close"))server.close();else assertEquals(200,request(server.port(),"/api/logout",cookie,"{\"mode\":\"share\"}","X-FQ-Token: "+cookie.substring(9)+"\r\n","POST").code);
                    byte[] partial=download.readBody();assertTrue(partial.length<contentLength(download.reply));assertFalse(eocd(partial));assertFalse(open(input));assertTrue(mode,System.nanoTime()-start<TimeUnit.SECONDS.toNanos(2));
                }
            }
        }
    }
    @Test public void crcDetectsSameLengthSourceContentChangeEvenWithUnknownMtime()throws Exception {
        Doc source=add("root","crc","crc.bin",false,256L,null);source.file=docs.file;docs.triggerDocument="crc";docs.triggerOpen=4;
        docs.afterOpen=()->{try{byte[] bytes=Files.readAllBytes(docs.file.toPath());bytes[0]^=1;Files.write(docs.file.toPath(),bytes);}catch(IOException error){throw new UncheckedIOException(error);}};
        try(SharedFiles server=new SharedFiles(context,TREE,0,true)) {
            String cookie=login(server),id=get(server,"/api/list?limit=1",cookie).json().getString("id");Reply zip=get(server,"/file?id="+id+"&archive=store",cookie);assertEquals(200,zip.code);assertFalse(eocd(zip.body));assertTrue(zip.body.length<contentLength(zip));
        }
    }
    private static Reply secureRequest(SharedFiles server,SecureIdentity client,String fingerprint,String path,String cookie,String data)throws Exception {
        try(Socket socket=new Socket("127.0.0.1",server.port());Socket tls=client.client(socket,fingerprint)) {
            tls.setSoTimeout(15000);byte[] body=data==null?new byte[0]:data.getBytes(StandardCharsets.UTF_8);String header=(data==null?"GET":"POST")+" "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+server.port()+"\r\nCookie: "+cookie+"\r\n";
            if(data!=null)header+="Content-Type: application/json\r\nContent-Length: "+body.length+"\r\n";
            tls.getOutputStream().write((header+"Connection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));tls.getOutputStream().write(body);byte[] bytes=tls.getInputStream().readAllBytes();int split=-1;
            for(int index=0;index+3<bytes.length;index++)if(bytes[index]==13&&bytes[index+1]==10&&bytes[index+2]==13&&bytes[index+3]==10){split=index;break;}assertTrue(split>=0);Reply reply=new Reply();reply.headers=new String(bytes,0,split,StandardCharsets.US_ASCII);reply.code=Integer.parseInt(reply.headers.split(" ")[1]);reply.body=Arrays.copyOfRange(bytes,split+4,bytes.length);return reply;
        }
    }
    @Test @ConscryptMode(ConscryptMode.Mode.OFF) public void tlsTrustRevocationClosesOnlyThatClientsArchiveAndOpenDescriptor()throws Exception {
        large("root","tls-large");String hostId="dddddddddddddddddddddddddddddddd",idA="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",idB="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        SecureIdentity host=new SecureIdentity(new File(context.getFilesDir(),"wp3-host")),a=new SecureIdentity(new File(context.getFilesDir(),"wp3-a")),b=new SecureIdentity(new File(context.getFilesDir(),"wp3-b"));host.confirmation=a.confirmation=b.confirmation=(name,code)->true;
        assertTrue(host.ensureTrusted(idA,a.fingerprint,"A"));assertTrue(host.ensureTrusted(idB,b.fingerprint,"B"));assertTrue(a.ensureTrusted(hostId,host.fingerprint,"host"));assertTrue(b.ensureTrusted(hostId,host.fingerprint,"host"));
        try(SharedFiles server=new SharedFiles(context,Collections.singletonList(TREE),0,true,null,host)) {
            String data=new JSONObject().put("mode","share").put("code",server.code()).toString();Reply loginA=secureRequest(server,a,host.fingerprint,"/api/login","",data),loginB=secureRequest(server,b,host.fingerprint,"/api/login","",data);
            String cookieA="fq_share="+loginA.json().getString("token"),cookieB="fq_share="+loginB.json().getString("token"),id=secureRequest(server,a,host.fingerprint,"/api/list?limit=1",cookieA,null).json().getString("id");
            try(ArchiveConnection first=new ArchiveConnection(server,id,cookieA,a,host.fingerprint);ArchiveConnection second=new ArchiveConnection(server,id,cookieB,b,host.fingerprint)) {
                assertEquals(200,first.reply.code);assertEquals(200,second.reply.code);InputStream inputA=awaitArchive(server,cookieA),inputB=awaitArchive(server,cookieB);long start=System.nanoTime();host.forget(idA);assertTrue(System.nanoTime()-start<TimeUnit.MILLISECONDS.toNanos(500));assertFalse(open(inputA));assertTrue(open(inputB));
                byte[] partial=first.readBody();assertTrue(partial.length<contentLength(first.reply));assertFalse(eocd(partial));byte[] complete=second.readBody();assertEquals(contentLength(second.reply),complete.length);assertTrue(eocd(complete));assertEquals(200,secureRequest(server,b,host.fingerprint,"/api/list?limit=1",cookieB,null).code);
            }
        }
    }
}
