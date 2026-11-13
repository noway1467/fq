package org.feiqlight.android;

import android.content.*;
import android.content.pm.ProviderInfo;
import android.database.*;
import android.net.Uri;
import android.os.*;
import android.provider.DocumentsContract;
import android.provider.DocumentsProvider;
import org.feiqlight.android.core.ToolsHttp;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.*;
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
        @Override public boolean onCreate(){return true;}
        private Cursor row(String id,String[] columns){
            if(denied)throw new SecurityException("权限已撤销");
            String[] names=columns==null?new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID}:columns;MatrixCursor cursor=new MatrixCursor(names);Object[] values=new Object[names.length];
            for(int i=0;i<names.length;i++){
                if(names[i].equals(DocumentsContract.Document.COLUMN_DOCUMENT_ID))values[i]=id;
                if(names[i].equals(DocumentsContract.Document.COLUMN_DISPLAY_NAME))values[i]=id.equals("root")?"共享根目录":id.equals("folder")?"空目录":"视频 English 😀.mp4";
                if(names[i].equals(DocumentsContract.Document.COLUMN_MIME_TYPE))values[i]=id.equals("video")?"video/mp4":DocumentsContract.Document.MIME_TYPE_DIR;
                if(names[i].equals(DocumentsContract.Document.COLUMN_SIZE))values[i]=id.equals("video")?file.length():0;
            }
            cursor.addRow(values);return cursor;
        }
        @Override public Cursor queryRoots(String[] columns){return new MatrixCursor(new String[]{DocumentsContract.Root.COLUMN_ROOT_ID});}
        @Override public Cursor queryDocument(String id,String[] columns){return row(id,columns);}
        @Override public Cursor queryChildDocuments(String parent,String[] columns,String sort){
            if(denied)throw new SecurityException("权限已撤销");MatrixCursor result=new MatrixCursor(columns);
            if(parent.equals("root"))for(String id:new String[]{"video","folder"})try(Cursor row=row(id,columns)){row.moveToFirst();Object[] values=new Object[columns.length];for(int i=0;i<values.length;i++)values[i]=row.getType(i)==Cursor.FIELD_TYPE_INTEGER?row.getLong(i):row.getString(i);result.addRow(values);}return result;
        }
        @Override public boolean isChildDocument(String parent,String child){return parent.equals("root")&&(child.equals("video")||child.equals("folder"));}
        @Override public ParcelFileDescriptor openDocument(String id,String mode,CancellationSignal signal)throws FileNotFoundException{if(denied)throw new SecurityException("权限已撤销");if(!id.equals("video"))throw new FileNotFoundException();openedMode=mode;return ParcelFileDescriptor.open(file,ParcelFileDescriptor.MODE_READ_ONLY);}
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
                    int length=method.equals("HEAD")?0:Integer.parseInt(Arrays.stream(response.split("\r\n")).filter(s->s.startsWith("Content-Length:")).findFirst().get().substring(15).trim());
                    byte[] body=socket.getInputStream().readNBytes(length);assertEquals(length,body.length);
                    if(request!=1&&request!=3)assertArrayEquals(Arrays.copyOfRange(Files.readAllBytes(docs.file.toPath()),40,60),body);
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
            final int chatPort;try(ServerSocket probe=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){chatPort=probe.getLocalPort();}
            service.connectionFactory=(prefs,listener)->new org.feiqlight.android.core.LanNode(chatPort,true,"restore-test","Android","恢复测试","test",listener);
            service.sharedFactory=(tree,port)->new SharedFiles(context,tree,0,true);
            try{
                service.onStartCommand(new Intent(context,ChatService.class),0,1);
                long until=System.currentTimeMillis()+10000;
                while((!service.online()||(generation<2&&service.sharedFiles()==null))&&System.currentTimeMillis()<until){org.robolectric.Shadows.shadowOf(Looper.getMainLooper()).idle();Thread.sleep(20);}
                assertTrue("聊天服务未启动："+service.status(),service.online());
                if(generation<2){assertNotNull("重启未恢复共享",service.sharedFiles());String cookie=login(service.sharedFiles());assertEquals(200,get(service.sharedFiles(),"/api/list",cookie).code);if(generation==1)service.stopSharing();}
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
}
