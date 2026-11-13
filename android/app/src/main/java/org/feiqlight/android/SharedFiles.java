package org.feiqlight.android;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.os.*;
import android.provider.DocumentsContract;
import org.feiqlight.android.core.ToolsHttp;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.*;

/** 仅访问用户授权的只读 SAF 目录；授权持久化由共享设置入口管理。 */
final class SharedFiles implements AutoCloseable {
    private static final class Entry {final Uri uri;final String name;final long size;final boolean directory;Entry(Uri uri,String name,long size,boolean directory){this.uri=uri;this.name=name;this.size=size;this.directory=directory;}}
    private static final class Session {final String token,address;volatile long last;Session(String token,String address){this.token=token;this.address=address;last=SystemClock.elapsedRealtime();}}
    private final Context context;
    private final Uri tree,root;
    private final Map<String,Entry> entries=new LinkedHashMap<>();
    private final List<Session> sessions=new ArrayList<>();
    private final ToolsHttp http;
    private final ToolsPairings pairings;
    private final String code=String.format(Locale.ROOT,"%06d",new SecureRandom().nextInt(1000000));
    private final long codeUntil=SystemClock.elapsedRealtime()+600000;
    private long attemptsSince;
    private int attempts;
    private volatile boolean closed;
    SharedFiles(Context context,Uri tree,int port,boolean loopback) throws IOException {
        this.context=context.getApplicationContext();this.tree=tree;
        if(tree==null||!"content".equals(tree.getScheme())||!DocumentsContract.isTreeUri(tree))throw new IOException("请选择系统授权的文件夹");
        root=DocumentsContract.buildDocumentUriUsingTree(tree,DocumentsContract.getTreeDocumentId(tree));
        try(Cursor cursor=context.getContentResolver().query(root,new String[]{DocumentsContract.Document.COLUMN_MIME_TYPE},null,null)) {
            if(cursor==null||!cursor.moveToFirst()||!DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(0)))throw new IOException("无法读取所选目录，请重新授权");
        }
        pairings=new ToolsPairings(this.context);http=new ToolsHttp(port,loopback,this::handle);
    }
    String code(){return SystemClock.elapsedRealtime()<codeUntil?code:"已过期，请停止后重新开启";}
    int port(){return http.port();}
    static String random(int size){byte[] bytes=new byte[size];new SecureRandom().nextBytes(bytes);StringBuilder text=new StringBuilder();for(byte value:bytes)text.append(String.format(Locale.ROOT,"%02x",value&255));return text.toString();}
    synchronized void forgetPairings()throws IOException{pairings.clear();sessions.clear();}
    private String pairCookie(){return "fq_pair_share_"+port();}
    private String cookie(ToolsHttp.Request request,String name){for(String part:request.header("Cookie").split(";"))if(part.trim().startsWith(name+"="))return part.trim().substring(name.length()+1);return "";}
    private JSONObject json(ToolsHttp.Request request) throws ToolsHttp.Failure {
        if(!request.header("Content-Type").startsWith("application/json"))throw new ToolsHttp.Failure(400,"请求格式错误");
        try{return new JSONObject(new String(request.body,StandardCharsets.UTF_8));}catch(JSONException e){throw new ToolsHttp.Failure(400,"请求内容无效");}
    }
    private synchronized boolean valid(Session session){return !closed&&sessions.contains(session)&&SystemClock.elapsedRealtime()-session.last<1800000;}
    private synchronized Session authorize(ToolsHttp.Request request) throws ToolsHttp.Failure {
        String token="";for(String cookie:request.header("Cookie").split(";"))if(cookie.trim().startsWith("fq_share="))token=cookie.trim().substring(9);
        for(Session session:sessions)if(session.token.equals(token)&&session.address.equals(request.address)&&valid(session)) {
            if(request.method.equals("POST")&&!request.header("X-FQ-Token").equals(token))throw new ToolsHttp.Failure(403,"请求校验失败");
            session.last=SystemClock.elapsedRealtime();return session;
        }
        throw new ToolsHttp.Failure(401,"连接已结束，请重新连接");
    }
    private synchronized void login(ToolsHttp.Request request,ToolsHttp.Response response,boolean reconnect) throws Exception {
        JSONObject data=json(request);long now=SystemClock.elapsedRealtime();
        if(!data.optString("mode").equals("share"))throw new ToolsHttp.Failure(403,"Android 仅提供文件共享，远控请连接 Windows");
        if(reconnect&&!request.header("X-FQ-Reconnect").equals("1"))throw new ToolsHttp.Failure(403,"请求校验失败");
        if(now-attemptsSince>=60000){attempts=0;attemptsSince=now;}if(++attempts>8)throw new ToolsHttp.Failure(429,"尝试过多，请一分钟后重试");
        String remembered=cookie(request,pairCookie()),scope="share:"+tree;
        if(closed)throw new ToolsHttp.Failure(403,"共享已关闭");
        if(reconnect){if(!pairings.contains(remembered,scope))throw new ToolsHttp.Failure(401,"没有可用的上次连接，请输入连接码");}
        else if(now>=codeUntil||!data.optString("code").trim().equals(code))throw new ToolsHttp.Failure(403,"连接码错误或已过期");
        sessions.removeIf(s->!valid(s));if(sessions.size()>=8)throw new ToolsHttp.Failure(409,"连接人数已满");
        remembered=pairings.remember(remembered,scope);
        Session session=new Session(random(32),request.address);sessions.add(session);
        response.json(new JSONObject().put("token",session.token).put("mode","share").toString(),"Set-Cookie: fq_share="+session.token+"; Path=/; HttpOnly; SameSite=Strict\r\nSet-Cookie: "+pairCookie()+"="+remembered+"; Path=/; HttpOnly; SameSite=Strict; Max-Age=7776000\r\n");
    }
    private void handle(ToolsHttp.Request request,ToolsHttp.Response response) throws Exception {
        if(!request.method.equals("GET")&&!request.method.equals("HEAD")&&!request.method.equals("POST"))throw new ToolsHttp.Failure(405,"不支持此操作");
        if(request.method.equals("GET")&&request.path.equals("/api/status")){
            boolean paired;synchronized(this){paired=!closed&&pairings.contains(cookie(request,pairCookie()),"share:"+tree);}
            response.json(new JSONObject().put("remote",false).put("share",!closed).put("paired",paired).put("platform","android").toString(),"");return;
        }
        if(request.method.equals("POST")) {
            if(request.path.equals("/api/login")||request.path.equals("/api/reconnect")){login(request,response,request.path.equals("/api/reconnect"));return;}
            if(request.path.equals("/api/logout")){Session session=authorize(request);synchronized(this){sessions.remove(session);}response.json("{\"ok\":true}","");return;}
            throw new ToolsHttp.Failure(404,"没有此操作");
        }
        if(request.path.equals("/api/list")){list(request,response,authorize(request));return;}
        if(request.path.equals("/file")){file(request,response,authorize(request));return;}
        if(request.path.equals("/favicon.ico")||request.path.startsWith("/native/")){response.bytes(204,"image/x-icon",new byte[0],"");return;}
        String asset=request.path.equals("/")?"index.html":request.path.substring(1);
        if(!asset.equals("index.html")&&!asset.equals("app.js")&&!asset.equals("app.css"))throw new ToolsHttp.Failure(404,"没有此页面");
        try(InputStream input=context.getAssets().open(asset);ByteArrayOutputStream output=new ByteArrayOutputStream()) {
            byte[] buffer=new byte[8192];int count;while((count=input.read(buffer))!=-1)output.write(buffer,0,count);
            response.bytes(200,asset.endsWith(".js")?"application/javascript; charset=utf-8":asset.endsWith(".css")?"text/css; charset=utf-8":"text/html; charset=utf-8",output.toByteArray(),"");
        }
    }
    private synchronized Entry entry(String id) throws ToolsHttp.Failure {
        if(id.isEmpty())return new Entry(root,"共享文件夹",0,true);
        Entry entry=entries.get(id);if(entry==null)throw new ToolsHttp.Failure(404,"文件索引已失效，请刷新目录");return entry;
    }
    private synchronized String remember(Entry entry) throws ToolsHttp.Failure {
        for(Map.Entry<String,Entry> item:entries.entrySet())if(item.getValue().uri.equals(entry.uri)){item.setValue(entry);return item.getKey();}
        if(entries.size()>=10000)throw new ToolsHttp.Failure(409,"浏览项已达上限，请重新开启共享");String id=random(16);entries.put(id,entry);return id;
    }
    private void list(ToolsHttp.Request request,ToolsHttp.Response response,Session session) throws Exception {
        Entry parent=entry(request.parameter("id"));if(!parent.directory)throw new ToolsHttp.Failure(404,"目录不存在");JSONArray items=new JSONArray();
        Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(tree,DocumentsContract.getDocumentId(parent.uri));
        try(Cursor cursor=context.getContentResolver().query(children,new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_SIZE},null,null)) {
            if(cursor==null)throw new ToolsHttp.Failure(403,"无法读取目录，请在手机上重新授权");
            while(cursor.moveToNext()) {
                if(!valid(session))throw new ToolsHttp.Failure(401,"共享已关闭");if(items.length()>=1000)throw new ToolsHttp.Failure(409,"此目录超过 1000 项，请共享更小的文件夹");
                String document=cursor.getString(0);if(document==null||document.equals(DocumentsContract.getDocumentId(parent.uri)))continue;
                boolean dir=DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(2));long size=cursor.isNull(3)?-1:cursor.getLong(3);
                Entry entry=new Entry(DocumentsContract.buildDocumentUriUsingTree(tree,document),cursor.getString(1)==null?"未命名文件":cursor.getString(1),size,dir);
                items.put(new JSONObject().put("id",remember(entry)).put("name",entry.name).put("dir",dir).put("size",size).put("seek",true));
            }
        }
        if(!valid(session))throw new ToolsHttp.Failure(401,"共享已关闭");response.json(new JSONObject().put("name",parent.name).put("items",items).toString(),"");
    }
    private void file(ToolsHttp.Request request,ToolsHttp.Response response,Session session) throws Exception {
        Entry entry=entry(request.parameter("id"));if(entry.directory)throw new ToolsHttp.Failure(404,"请选择文件");
        ParcelFileDescriptor descriptor=context.getContentResolver().openFileDescriptor(entry.uri,"r");if(descriptor==null)throw new ToolsHttp.Failure(404,"文件不可用");
        try(ParcelFileDescriptor.AutoCloseInputStream input=new ParcelFileDescriptor.AutoCloseInputStream(descriptor)) {
            long length=descriptor.getStatSize();if(length<0)length=entry.size;if(length<0)throw new ToolsHttp.Failure(409,"文件提供方未提供长度，请先保存到手机本地文件夹");
            boolean seek=true;try{input.getChannel().position(0);}catch(IOException e){seek=false;}
            long[] range=ToolsHttp.range(request.header("Range"),length);
            if(range==null){response.bytes(416,"application/octet-stream",new byte[0],"Content-Range: bytes */"+length+"\r\n");return;}
            if(range[0]>0&&!seek)throw new ToolsHttp.Failure(409,"此文件来源不支持跳转读取，请下载后播放");
            if(range[0]>0)input.getChannel().position(range[0]);
            String extra="Accept-Ranges: "+(seek?"bytes":"none")+"\r\n";
            if(!request.header("Range").isEmpty())extra+="Content-Range: bytes "+range[0]+"-"+range[1]+"/"+length+"\r\n";
            if(request.parameter("download").equals("1"))extra+="Content-Disposition: attachment; filename*=UTF-8''"+Uri.encode(entry.name)+"\r\n";
            if(!valid(session))throw new ToolsHttp.Failure(401,"共享已关闭");
            String mime=ReceivedStorage.mime(entry.name);if(!mime.startsWith("video/")&&!mime.startsWith("audio/"))mime="application/octet-stream";
            response.begin(request.header("Range").isEmpty()?200:206,mime,Math.max(0,range[1]-range[0]+1),extra);if(request.method.equals("HEAD"))return;
            long left=range[1]-range[0]+1;byte[] buffer=new byte[262144];
            while(left>0){if(!valid(session))return;session.last=SystemClock.elapsedRealtime();int count=input.read(buffer,0,(int)Math.min(left,buffer.length));if(count<0)throw new EOFException();response.write(buffer,count);left-=count;}
        }
    }
    @Override public void close(){synchronized(this){closed=true;sessions.clear();entries.clear();}http.close();}
}
