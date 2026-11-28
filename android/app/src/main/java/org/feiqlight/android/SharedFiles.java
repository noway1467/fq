package org.feiqlight.android;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.os.*;
import android.provider.DocumentsContract;
import android.system.Os;
import android.system.OsConstants;
import android.system.StructStat;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;
import org.feiqlight.android.core.SecureIdentity;
import org.feiqlight.android.core.StoreZipWriter;
import org.feiqlight.android.core.ToolsHttp;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.text.Normalizer;
import java.util.*;

/** 仅访问用户授权的只读 SAF 目录；授权持久化由共享设置入口管理。 */
final class SharedFiles implements AutoCloseable {
    static final int LIST_SCAN_LIMIT=20000,LIST_SCAN_MILLISECONDS=2000;
    static final long SNAPSHOT_LIFETIME=120000;
    private static final int SNAPSHOT_LIMIT=4,SESSION_SCAN_LIMIT=40000,BROWSE_ID_LIMIT=10000;
    private static final int ARCHIVE_DIRECTORY_LIMIT=1000,ARCHIVE_DEPTH_LIMIT=32,ARCHIVE_SCAN_LIMIT=10000;
    private static final String[] DOCUMENT_COLUMNS={DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_SIZE,DocumentsContract.Document.COLUMN_LAST_MODIFIED};
    private static final class Entry {
        final Uri tree,uri;
        final String name,normalName;
        final long size,modified;
        final boolean directory;
        Entry(Uri tree,Uri uri,String name,long size,boolean directory){this(tree,uri,name,size,directory,-1);}
        Entry(Uri tree,Uri uri,String name,long size,boolean directory,long modified){this.tree=tree;this.uri=uri;this.name=name;this.size=size;this.directory=directory;this.modified=modified>0?modified:-1;normalName=normalize(name);}
    }
    private static final class Session {
        final String token,address,fingerprint;
        volatile long last;
        boolean listing;
        CancellationSignal listSignal;
        final List<Snapshot> snapshots=new ArrayList<>();
        Session(String token,String address,String fingerprint){this.token=token;this.address=address;this.fingerprint=fingerprint;last=SystemClock.elapsedRealtime();}
    }
    private static final class Snapshot {
        Entry parent;
        String target,name,query,sort,order;
        int revision,scanned;
        long created;
        boolean truncated;
        final List<Entry> items=new ArrayList<>();
        final Map<String,Integer> cursors=new HashMap<>();
        final Map<Integer,String> offsets=new HashMap<>();
    }
    private static final class ArchiveLease {
        Entry target;
        Session session;
        ToolsHttp.Response response;
        InputStream input;
        CancellationSignal signal;
        Cursor cursor;
        android.database.ContentObserver observer;
        volatile boolean cancelled;
    }
    private static final class ArchiveFile {
        Entry entry;
        String relative;
        long size,device,inode;
        boolean identity;
    }
    private static final class ArchiveDirectory {
        Entry entry;
        String relative;
        int depth;
        ArchiveDirectory(Entry entry,String relative,int depth){this.entry=entry;this.relative=relative;this.depth=depth;}
    }
    private static final class ArchiveInput extends ParcelFileDescriptor.AutoCloseInputStream {
        final ParcelFileDescriptor descriptor;
        ArchiveInput(ParcelFileDescriptor descriptor){super(descriptor);this.descriptor=descriptor;}
    }
    private final Context context;
    private final List<Entry> roots=new ArrayList<>();
    private String scope="";
    private int revision;
    private final Map<String,Entry> entries=new LinkedHashMap<>(16,.75f,true);
    private final Map<String,String> entryIds=new HashMap<>();
    private final List<ArchiveLease> archives=new ArrayList<>();
    private final List<Session> sessions=new ArrayList<>();
    private final ToolsHttp http;
    private final ToolsPairings pairings;
    private final SecureIdentity security;
    private final Timer deadlines;
    private final java.util.function.Consumer<String> revoked=this::onTrustRevoked;
    private final String code=String.format(Locale.ROOT,"%06d",new SecureRandom().nextInt(1000000));
    private final long codeUntil=SystemClock.elapsedRealtime()+600000;
    private long attemptsSince;
    private int attempts;
    private volatile boolean closed;
    SharedFiles(Context context,Uri tree,int port,boolean loopback) throws IOException {
        this(context,Collections.singletonList(tree),port,loopback);
    }
    SharedFiles(Context context,List<Uri> folders,int port,boolean loopback) throws IOException {
        this(context,folders,port,loopback,loopback?null:new LanNetwork(context.getSystemService(android.net.ConnectivityManager.class)));
    }
    SharedFiles(Context context,List<Uri> folders,int port,boolean loopback,LanNode.SocketBinding binding) throws IOException {
        this(context,folders,port,loopback,binding,loopback||binding==null?null:AppIdentity.get(context));
    }
    SharedFiles(Context context,List<Uri> folders,int port,boolean loopback,LanNode.SocketBinding binding,SecureIdentity security) throws IOException {
        if(!loopback&&binding==null)throw new IOException("请先连接局域网");
        if(!loopback&&security==null)throw new IOException("设备安全身份不可用，不能开启共享");
        this.context=context.getApplicationContext();this.security=security;pairings=new ToolsPairings(this.context);
        updateFolders(folders);
        http=new ToolsHttp(port,loopback,this::handle,security,loopback?null:binding);
        deadlines=new Timer("shared-files-deadlines",true);
        if(security!=null)security.observeRevocation(revoked);
    }
    void updateFolders(List<Uri> folders)throws IOException {
        int before;synchronized(this){if(closed)throw new IOException("共享已关闭");before=revision;}
        List<Uri> trees=new ArrayList<>(new LinkedHashSet<>(folders));List<Entry> next=new ArrayList<>();
        if(trees.isEmpty()||trees.size()>20)throw new IOException("请选择 1–20 个共享文件夹");
        for(Uri tree:trees) {
            if(tree==null||!"content".equals(tree.getScheme())||!DocumentsContract.isTreeUri(tree))throw new IOException("请选择系统授权的文件夹");
            Uri root=DocumentsContract.buildDocumentUriUsingTree(tree,DocumentsContract.getTreeDocumentId(tree));
            try(Cursor cursor=context.getContentResolver().query(root,new String[]{DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_LAST_MODIFIED},null,null)) {
                if(cursor==null||!cursor.moveToFirst()||!DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(0)))throw new IOException("无法读取所选目录，请重新授权");
                int modified=cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED);
                next.add(new Entry(tree,root,cursor.getString(1)==null?"共享文件夹":cursor.getString(1),0,true,modified<0||cursor.isNull(modified)?-1:cursor.getLong(modified)));
            }
        }
        String nextScope="share:"+String.join("\n",trees.stream().map(Uri::toString).sorted().collect(java.util.stream.Collectors.toList()));
        synchronized(this) {
            if(closed||before!=revision)throw new IOException("共享目录已变化，请重新操作");
            if(!scope.isEmpty())pairings.changeShareScope(scope,nextScope);
            roots.clear();roots.addAll(next);scope=nextScope;revision++;
            for(String id:new ArrayList<>(entries.keySet()))if(!trees.contains(entries.get(id).tree))forgetEntry(id);
            cancelArchives(archive->!trees.contains(archive.target.tree));
            invalidateListings();
        }
    }
    String code(){return SystemClock.elapsedRealtime()<codeUntil?code:"已过期，请停止后重新开启";}
    int port(){return http.port();}
    static String random(int size){byte[] bytes=new byte[size];new SecureRandom().nextBytes(bytes);StringBuilder text=new StringBuilder();for(byte value:bytes)text.append(String.format(Locale.ROOT,"%02x",value&255));return text.toString();}
    synchronized void forgetPairings()throws IOException{pairings.clear();cancelArchives(archive->true);invalidateListings();sessions.clear();}
    private synchronized void onTrustRevoked(String fingerprint) {
        cancelArchives(archive->Objects.equals(archive.session.fingerprint,fingerprint));
        for(Session session:sessions)if(Objects.equals(session.fingerprint,fingerprint)&&session.listSignal!=null)session.listSignal.cancel();
        sessions.removeIf(session->Objects.equals(session.fingerprint,fingerprint));
        try{pairings.forgetFingerprint(fingerprint);}catch(IOException error){android.util.Log.e("SharedFiles","清除已撤信设备的配对失败",error);}
    }
    private String pairCookie(){return "fq_pair_share_"+port();}
    private String cookie(ToolsHttp.Request request,String name){for(String part:request.header("Cookie").split(";"))if(part.trim().startsWith(name+"="))return part.trim().substring(name.length()+1);return "";}
    private JSONObject json(ToolsHttp.Request request) throws ToolsHttp.Failure {
        if(!request.header("Content-Type").startsWith("application/json"))throw new ToolsHttp.Failure(400,"请求格式错误");
        try{return new JSONObject(new String(request.body,StandardCharsets.UTF_8));}catch(JSONException e){throw new ToolsHttp.Failure(400,"请求内容无效");}
    }
    private synchronized boolean valid(Session session){return !closed&&sessions.contains(session)&&SystemClock.elapsedRealtime()-session.last<1800000;}
    private synchronized Session authorize(ToolsHttp.Request request) throws ToolsHttp.Failure {
        String token="";for(String cookie:request.header("Cookie").split(";"))if(cookie.trim().startsWith("fq_share="))token=cookie.trim().substring(9);
        if(token.isEmpty())token=request.header("X-FQ-Token");
        if(token.isEmpty()&&(request.method.equals("GET")||request.method.equals("HEAD"))&&request.path.equals("/file"))try{token=request.parameter("token");}catch(IOException ignored){}
        for(Session session:sessions)if(session.token.equals(token)&&session.address.equals(request.address)&&Objects.equals(session.fingerprint,request.fingerprint)&&valid(session)) {
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
        String remembered=cookie(request,pairCookie()),scope=request.pairScope(this.scope);
        if(closed)throw new ToolsHttp.Failure(403,"共享已关闭");
        if(reconnect){if(!pairings.contains(remembered,scope))throw new ToolsHttp.Failure(401,"没有可用的上次连接，请输入连接码");}
        else if(now>=codeUntil||!data.optString("code").trim().equals(code))throw new ToolsHttp.Failure(403,"连接码错误或已过期");
        cancelArchives(archive->!valid(archive.session));sessions.removeIf(s->!valid(s));if(sessions.size()>=8)throw new ToolsHttp.Failure(409,"连接人数已满");
        remembered=pairings.remember(remembered,scope);
        Session session=new Session(random(32),request.address,request.fingerprint);sessions.add(session);
        response.json(new JSONObject().put("token",session.token).put("mode","share").toString(),"Set-Cookie: fq_share="+session.token+"; Path=/; HttpOnly; SameSite=Strict\r\nSet-Cookie: "+pairCookie()+"="+remembered+"; Path=/; HttpOnly; SameSite=Strict; Max-Age=7776000\r\n");
    }
    private void handle(ToolsHttp.Request request,ToolsHttp.Response response) throws Exception {
        if(!request.method.equals("GET")&&!request.method.equals("HEAD")&&!request.method.equals("POST"))throw new ToolsHttp.Failure(405,"不支持此操作");
        if(request.method.equals("GET")&&request.path.equals("/api/status")){
            boolean paired;synchronized(this){paired=!closed&&pairings.contains(cookie(request,pairCookie()),request.pairScope(scope));}
            response.json(new JSONObject().put("remote",false).put("share",!closed).put("paired",paired).put("platform","android").put("listPaging",true).put("directoryArchive",true).toString(),"");return;
        }
        if(request.method.equals("POST")) {
            if(request.path.equals("/api/login")||request.path.equals("/api/reconnect")){login(request,response,request.path.equals("/api/reconnect"));return;}
            if(request.path.equals("/api/logout")){Session session=authorize(request);synchronized(this){sessions.remove(session);cancelArchives(archive->archive.session==session);if(session.listSignal!=null)session.listSignal.cancel();}response.json("{\"ok\":true}","");return;}
            throw new ToolsHttp.Failure(404,"没有此操作");
        }
        if(request.path.equals("/api/list")||request.path.equals("/api/files")){list(request,response,authorize(request));return;}
        if(request.path.equals("/api/share-status")){authorize(request);synchronized(this){response.json(new JSONObject().put("revision",revision).put("listPaging",true).put("directoryArchive",true).toString(),"");}return;}
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
        if(id.isEmpty())return roots.size()==1?roots.get(0):new Entry(null,null,"共享文件夹",0,true);
        Entry entry=entries.get(id);if(entry==null||!shared(entry))throw new ToolsHttp.Failure(404,"文件索引已失效，请刷新目录");return entry;
    }
    private synchronized boolean shared(Entry entry){return !closed&&roots.stream().anyMatch(root->root.tree.equals(entry.tree));}
    private synchronized String remember(Entry entry) throws ToolsHttp.Failure {
        if(!shared(entry))throw new ToolsHttp.Failure(404,"共享目录已移除");
        String key=entryKey(entry),id=entryIds.get(key);
        if(id!=null){entries.put(id,entry);return id;}
        if(entries.size()>=BROWSE_ID_LIMIT)forgetEntry(entries.keySet().iterator().next());
        id=random(16);entries.put(id,entry);entryIds.put(key,id);return id;
    }
    private static String entryKey(Entry entry){return entry.tree+"\0"+entry.uri;}
    private void forgetEntry(String id){entryIds.remove(entryKey(entries.remove(id)));}
    private static String normalize(String name){return Normalizer.normalize(name,Normalizer.Form.NFC).toUpperCase(Locale.ROOT);}
    private void invalidateListings() {
        for(Session session:sessions) {
            if(session.listSignal!=null)session.listSignal.cancel();
            // 游标只保留短期失效记录，移根后释放整份目录元数据。
            for(Snapshot snapshot:session.snapshots){snapshot.items.clear();snapshot.scanned=0;}
        }
    }
    private synchronized void checkListing(Session session,int listedRevision)throws ToolsHttp.Failure {
        if(!valid(session))throw new ToolsHttp.Failure(401,"共享已关闭");
        if(listedRevision!=revision)throw new ToolsHttp.Failure(409,"共享目录已变化，请从首页刷新");
    }
    private static Entry readEntry(Uri tree,Cursor cursor) {
        String document=cursor.getString(0),name=cursor.getString(1);
        boolean directory=DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(2));
        int sizeColumn=cursor.getColumnIndex(DocumentsContract.Document.COLUMN_SIZE),timeColumn=cursor.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED);
        long size=sizeColumn<0||cursor.isNull(sizeColumn)?-1:cursor.getLong(sizeColumn);
        long modified=timeColumn<0||cursor.isNull(timeColumn)?-1:cursor.getLong(timeColumn);
        return new Entry(tree,DocumentsContract.buildDocumentUriUsingTree(tree,document),name==null?"未命名文件":name,directory?0:size,directory,modified);
    }
    private static int compareEntries(Entry first,Entry second,String sort,String order) {
        if(first.directory!=second.directory)return first.directory?-1:1;
        int compared;
        if(sort.equals("size")||sort.equals("mtime")) {
            long a=sort.equals("size")?first.size:first.modified,b=sort.equals("size")?second.size:second.modified;
            if((a>=0)!=(b>=0))return a>=0?-1:1;
            compared=a>=0?Long.compare(a,b):0;
        }else compared=first.normalName.compareTo(second.normalName);
        if(compared!=0)return order.equals("desc")?-compared:compared;
        compared=first.normalName.compareTo(second.normalName);
        if(compared==0)compared=entryKey(first).compareTo(entryKey(second));
        return compared;
    }
    private JSONObject listedItem(Entry entry)throws JSONException,ToolsHttp.Failure {
        return new JSONObject().put("id",remember(entry)).put("name",entry.name).put("dir",entry.directory).put("size",entry.size).put("mtime",entry.modified<0?JSONObject.NULL:entry.modified).put("seek",true);
    }
    private void list(ToolsHttp.Request request,ToolsHttp.Response response,Session session) throws Exception {
        boolean paging=Arrays.stream(request.query.split("&")).anyMatch(part->Arrays.asList("limit","cursor","q","sort","order").contains(part.split("=",2)[0]));
        String target=request.parameter("id"),cursor=request.parameter("cursor"),query=normalize(request.parameter("q"));
        String sort=request.parameter("sort"),order=request.parameter("order"),limitText=request.parameter("limit");
        if(sort.isEmpty())sort="name";if(order.isEmpty())order="asc";
        int limit=100;
        if(!limitText.isEmpty()) {
            try{if(!limitText.matches("[0-9]{1,9}")||(limit=Integer.parseInt(limitText))<1)throw new NumberFormatException();}
            catch(NumberFormatException error){throw new ToolsHttp.Failure(400,"每页数量须为正整数，最多 200 项");}
        }
        limit=Math.min(limit,200);
        if(query.length()>256||query.indexOf('\0')>=0||!Arrays.asList("name","size","mtime").contains(sort)||!Arrays.asList("asc","desc").contains(order))throw new ToolsHttp.Failure(400,"搜索或排序参数无效");
        Snapshot snapshot=null;int offset=0;
        synchronized(this) {
            session.snapshots.removeIf(item->SystemClock.elapsedRealtime()-item.created>=SNAPSHOT_LIFETIME);
            if(!cursor.isEmpty()) {
                for(Snapshot item:session.snapshots)if(item.cursors.containsKey(cursor)){snapshot=item;offset=item.cursors.get(cursor);break;}
                if(snapshot==null)throw new ToolsHttp.Failure(410,"目录分页已过期或被淘汰，请从首页刷新");
                if(snapshot.revision!=revision||!snapshot.target.equals(target)||!snapshot.query.equals(query)||!snapshot.sort.equals(sort)||!snapshot.order.equals(order))throw new ToolsHttp.Failure(409,"目录分页条件或共享目录已变化，请从首页刷新");
            }else {
                if(session.listing)throw new ToolsHttp.Failure(429,"目录正在读取，请稍后刷新");
                Entry parent=entry(target);if(!parent.directory)throw new ToolsHttp.Failure(404,"目录不存在");
                snapshot=new Snapshot();snapshot.parent=parent;snapshot.target=target;snapshot.name=parent.name;snapshot.revision=revision;snapshot.query=query;snapshot.sort=sort;snapshot.order=order;snapshot.created=SystemClock.elapsedRealtime();
                session.listing=true;
            }
        }
        if(cursor.isEmpty()) {
            try {
                scanDirectory(snapshot,session,response,paging);
                if(paging){final String selectedSort=sort,selectedOrder=order;snapshot.items.sort((a,b)->compareEntries(a,b,selectedSort,selectedOrder));}
                synchronized(this) {
                    checkListing(session,snapshot.revision);
                    if(paging) {
                        while(session.snapshots.size()>=SNAPSHOT_LIMIT||session.snapshots.stream().mapToInt(item->item.scanned).sum()+snapshot.scanned>SESSION_SCAN_LIMIT)session.snapshots.remove(0);
                        session.snapshots.add(snapshot);
                    }
                }
            }finally{synchronized(this){session.listing=false;session.listSignal=null;}}
        }
        JSONObject result;
        synchronized(this) {
            checkListing(session,snapshot.revision);JSONArray page=new JSONArray();
            int end=Math.min(snapshot.items.size(),offset+(paging?limit:1000));
            for(int index=offset;index<end;index++)page.put(listedItem(snapshot.items.get(index)));
            result=new JSONObject().put("name",snapshot.name).put("revision",snapshot.revision).put("items",page);
            if(paging) {
                String next=null;
                if(end<snapshot.items.size()) {
                    next=snapshot.offsets.get(end);
                    if(next==null){next=random(32);snapshot.offsets.put(end,next);snapshot.cursors.put(next,end);}
                }
                result.put("id",snapshot.parent.uri==null?JSONObject.NULL:remember(snapshot.parent)).put("nextCursor",next==null?JSONObject.NULL:next).put("truncated",snapshot.truncated);
            }
        }
        response.json(result.toString(),"");
    }
    private void scanDirectory(Snapshot snapshot,Session session,ToolsHttp.Response response,boolean paging)throws Exception {
        Entry parent=snapshot.parent;
        if(parent.uri==null) {
            List<Entry> selected;synchronized(this){selected=new ArrayList<>(roots);}
            for(Entry root:selected){snapshot.scanned++;if(!paging||root.normalName.contains(snapshot.query))snapshot.items.add(root);}
            return;
        }
        Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(parent.tree,DocumentsContract.getDocumentId(parent.uri));
        CancellationSignal signal=new CancellationSignal();
        synchronized(this){checkListing(session,snapshot.revision);session.listSignal=signal;}
        TimerTask deadline=new TimerTask(){@Override public void run(){signal.cancel();}};
        deadlines.schedule(deadline,LIST_SCAN_MILLISECONDS);
        long started=System.nanoTime();int characters=0;Set<String> documents=new HashSet<>();
        documents.add(DocumentsContract.getDocumentId(parent.uri));
        // 不向任意 SAF provider 传排序或分页参数；在本机有界快照中统一排序。
        try(Cursor cursor=context.getContentResolver().query(children,DOCUMENT_COLUMNS,(Bundle)null,signal)) {
            if(cursor==null)throw new ToolsHttp.Failure(403,"无法读取目录，请在手机上重新授权");
            while(cursor.moveToNext()) {
                checkListing(session,snapshot.revision);response.processing();
                if(!paging&&snapshot.scanned>=1000)throw new ToolsHttp.Failure(409,"此目录超过 1000 项，请使用支持分页的网页或共享更小的文件夹");
                if(snapshot.scanned>=LIST_SCAN_LIMIT||System.nanoTime()-started>=LIST_SCAN_MILLISECONDS*1000000L||signal.isCanceled()||characters>=4*1024*1024) {
                    if(!paging)throw new ToolsHttp.Failure(409,"目录读取超过预算，请使用支持分页的网页");
                    snapshot.truncated=true;break;
                }
                snapshot.scanned++;String document=cursor.getString(0);
                if(document==null||!documents.add(document))continue;
                Entry item=readEntry(parent.tree,cursor);characters+=document.length()+item.name.length()+item.uri.toString().length();
                if(!paging||item.normalName.contains(snapshot.query))snapshot.items.add(item);
            }
        }catch(OperationCanceledException error) {
            checkListing(session,snapshot.revision);
            if(!paging)throw new ToolsHttp.Failure(409,"目录读取超过预算，请使用支持分页的网页");
            snapshot.truncated=true;
        }finally{deadline.cancel();deadlines.purge();synchronized(this){if(session.listSignal==signal)session.listSignal=null;}}
        if(signal.isCanceled()||System.nanoTime()-started>=LIST_SCAN_MILLISECONDS*1000000L){if(!paging)throw new ToolsHttp.Failure(409,"目录读取超过预算，请使用支持分页的网页");snapshot.truncated=true;}
        checkListing(session,snapshot.revision);
    }
    private void file(ToolsHttp.Request request,ToolsHttp.Response response,Session session) throws Exception {
        Entry entry=entry(request.parameter("id"));String archive=request.parameter("archive");
        if(!archive.isEmpty()){if(!archive.equals("store"))throw new ToolsHttp.Failure(400,"不支持此目录打包格式");archive(request,response,session,entry);return;}
        if(entry.directory)throw new ToolsHttp.Failure(404,"请选择文件");
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
            if(!valid(session)||!shared(entry))throw new ToolsHttp.Failure(401,"共享已关闭或目录已移除");
            String mime=previewMime(entry.name);
            response.begin(request.header("Range").isEmpty()?200:206,mime,Math.max(0,range[1]-range[0]+1),extra);if(request.method.equals("HEAD"))return;
            long left=range[1]-range[0]+1;byte[] buffer=new byte[262144];
            while(left>0){if(!valid(session)||!shared(entry))return;session.last=SystemClock.elapsedRealtime();int count=input.read(buffer,0,(int)Math.min(left,buffer.length));if(count<0)throw new EOFException();response.write(buffer,count);left-=count;}
        }
    }
    private void cancelArchives(java.util.function.Predicate<ArchiveLease> selected) {
        for(ArchiveLease archive:new ArrayList<>(archives))if(selected.test(archive)) {
            archive.cancelled=true;
            try{archive.response.abort();}catch(IOException error){android.util.Log.w("SharedFiles","关闭共享归档连接失败",error);}
            if(archive.signal!=null)archive.signal.cancel();
            if(archive.cursor!=null)try{archive.cursor.close();}catch(RuntimeException error){android.util.Log.w("SharedFiles","关闭归档目录游标失败",error);}
            if(archive.input!=null)try{archive.input.close();}catch(IOException error){android.util.Log.w("SharedFiles","关闭归档文件句柄失败",error);}
        }
    }
    private void checkArchive(ArchiveLease archive)throws IOException {
        synchronized(this) {
            if(archive.cancelled||!valid(archive.session)||!shared(archive.target))throw new ToolsHttp.Failure(401,"共享已关闭、目录已移除或设备信任已撤销");
            archive.session.last=SystemClock.elapsedRealtime();
        }
        archive.response.checkConnection();
    }
    private CancellationSignal beginArchiveQuery(ArchiveLease archive)throws IOException {
        checkArchive(archive);CancellationSignal signal=new CancellationSignal();
        synchronized(this) {
            if(archive.cancelled||!valid(archive.session)||!shared(archive.target))throw new ToolsHttp.Failure(401,"共享目录已移除");
            archive.signal=signal;
        }
        return signal;
    }
    private synchronized void finishArchiveQuery(ArchiveLease archive,CancellationSignal signal){if(archive.signal==signal){archive.signal=null;archive.cursor=null;}}
    private InputStream openArchiveSource(ArchiveLease archive,ArchiveFile source)throws IOException {
        CancellationSignal signal=beginArchiveQuery(archive);ArchiveInput input=null;
        try {
            ParcelFileDescriptor descriptor=context.getContentResolver().openFileDescriptor(source.entry.uri,"r",signal);
            if(descriptor==null)throw new ToolsHttp.Failure(409,"目录中的文件无法读取");
            input=new ArchiveInput(descriptor);
            synchronized(this) {
                if(archive.cancelled||!valid(archive.session)||!shared(archive.target))throw new ToolsHttp.Failure(401,"共享目录已移除");
                archive.input=input;
            }
            finishArchiveQuery(archive,signal);
            validateArchiveSource(archive,source,input);return input;
        }catch(IOException|RuntimeException error){if(input!=null)input.close();throw error;}
        finally{finishArchiveQuery(archive,signal);}
    }
    private void validateArchiveSource(ArchiveLease archive,ArchiveFile source,InputStream stream)throws IOException {
        checkArchive(archive);
        if(stream==null){try(InputStream probe=openArchiveSource(archive,source)){}return;}
        CancellationSignal signal=beginArchiveQuery(archive);
        try(Cursor cursor=context.getContentResolver().query(source.entry.uri,DOCUMENT_COLUMNS,(Bundle)null,signal)) {
            synchronized(this){if(archive.cancelled)throw new ToolsHttp.Failure(401,"共享目录已移除");archive.cursor=cursor;}
            if(cursor==null||!cursor.moveToFirst()||cursor.getString(0)==null)throw new ToolsHttp.Failure(409,"原文件已不存在，请重新下载目录");
            Entry current=readEntry(source.entry.tree,cursor);
            if(!current.uri.equals(source.entry.uri)||current.directory||!current.name.equals(source.entry.name)||(source.entry.size>=0&&current.size!=source.entry.size)||(source.entry.modified>=0&&current.modified!=source.entry.modified))throw new ToolsHttp.Failure(409,"原文件已变化，请重新下载目录");
        }finally{finishArchiveQuery(archive,signal);}
        ArchiveInput input=(ArchiveInput)stream;long length=input.descriptor.getStatSize();
        if(source.size<0)source.size=length;
        if(source.size<0)throw new ToolsHttp.Failure(409,"文件提供方未提供长度，请逐件下载或先保存到手机本地文件夹");
        if(length>=0&&length!=source.size)throw new ToolsHttp.Failure(409,"原文件长度已变化，请重新下载目录");
        try {
            StructStat stat=Os.fstat(input.descriptor.getFileDescriptor());
            if(OsConstants.S_ISREG(stat.st_mode)) {
                if(source.identity&&(source.device!=stat.st_dev||source.inode!=stat.st_ino))throw new ToolsHttp.Failure(409,"原文件已被替换，请重新下载目录");
                source.device=stat.st_dev;source.inode=stat.st_ino;source.identity=true;
            }
        }catch(android.system.ErrnoException error){throw new IOException("无法复核共享文件句柄",error);}
        checkArchive(archive);
    }
    private List<ArchiveFile> scanArchive(ArchiveLease archive)throws IOException {
        List<ArchiveFile> files=new ArrayList<>();Deque<ArchiveDirectory> pending=new ArrayDeque<>();
        pending.add(new ArchiveDirectory(archive.target,"",0));
        Set<String> documents=new HashSet<>();documents.add(DocumentsContract.getDocumentId(archive.target.uri));documents.add(DocumentsContract.getTreeDocumentId(archive.target.tree));
        int directories=1,scanned=0;long started=System.nanoTime();
        while(!pending.isEmpty()) {
            ArchiveDirectory directory=pending.removeFirst();checkArchive(archive);
            CancellationSignal signal=beginArchiveQuery(archive);
            TimerTask deadline=new TimerTask(){@Override public void run(){signal.cancel();}};
            long left=5000-(System.nanoTime()-started)/1000000L;
            if(left<=0)throw new ToolsHttp.Failure(409,"目录遍历超过预算，请分目录下载");
            deadlines.schedule(deadline,left);
            Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(directory.entry.tree,DocumentsContract.getDocumentId(directory.entry.uri));
            try(Cursor cursor=context.getContentResolver().query(children,DOCUMENT_COLUMNS,(Bundle)null,signal)) {
                synchronized(this){if(archive.cancelled)throw new ToolsHttp.Failure(401,"共享目录已移除");archive.cursor=cursor;}
                if(cursor==null)throw new ToolsHttp.Failure(403,"无法读取目录，请在手机上重新授权");
                while(cursor.moveToNext()) {
                    checkArchive(archive);archive.response.processing();
                    if(++scanned>ARCHIVE_SCAN_LIMIT||System.nanoTime()-started>=5000000000L||signal.isCanceled())throw new ToolsHttp.Failure(409,"目录遍历超过预算，请分目录下载");
                    String document=cursor.getString(0);
                    if(document==null||!documents.add(document))throw new ToolsHttp.Failure(409,"文件提供方返回循环或重复文档，不能打包");
                    Entry entry=readEntry(directory.entry.tree,cursor);
                    String relative=(directory.relative.isEmpty()?"":directory.relative+"/")+entry.name;
                    if(!Protocol.safeRelativePath(relative))throw new ToolsHttp.Failure(409,"目录中的文件路径不安全或过长，不能打包");
                    if(entry.directory) {
                        if(directory.depth>=ARCHIVE_DEPTH_LIMIT||++directories>ARCHIVE_DIRECTORY_LIMIT)throw new ToolsHttp.Failure(409,"目录层数或目录数量超过上限，请分目录下载");
                        pending.addLast(new ArchiveDirectory(entry,relative,directory.depth+1));
                    }else {
                        if(files.size()>=StoreZipWriter.MAX_ENTRIES)throw new ToolsHttp.Failure(409,StoreZipWriter.LIMIT_MESSAGE);
                        ArchiveFile file=new ArchiveFile();file.entry=entry;file.relative=relative;file.size=entry.size;files.add(file);
                    }
                }
            }catch(OperationCanceledException error){checkArchive(archive);throw new ToolsHttp.Failure(409,"目录遍历超过预算，请分目录下载");}
            finally{deadline.cancel();deadlines.purge();finishArchiveQuery(archive,signal);}
        }
        return files;
    }
    private void archive(ToolsHttp.Request request,ToolsHttp.Response response,Session session,Entry target)throws IOException {
        if(request.parameter("id").isEmpty()||target.uri==null||!target.directory)throw new ToolsHttp.Failure(404,"请选择真实的共享目录");
        if(!request.header("Range").isEmpty())throw new ToolsHttp.Failure(416,"目录打包不支持续传，请重新下载");
        response.disableReuse();ArchiveLease lease=new ArchiveLease();lease.target=target;lease.session=session;lease.response=response;
        synchronized(this) {
            if(!valid(session)||!shared(target))throw new ToolsHttp.Failure(401,"共享目录已移除");
            if(archives.stream().filter(item->!item.cancelled).count()>=4||archives.stream().anyMatch(item->item.session==session&&!item.cancelled))throw new ToolsHttp.Failure(409,"已有目录打包下载正在进行，请等它完成后重试");
            archives.add(lease);
        }
        try {
            lease.observer=new android.database.ContentObserver(null){@Override public void onChange(boolean selfChange){synchronized(SharedFiles.this){cancelArchives(item->item==lease);}}};
            context.getContentResolver().registerContentObserver(target.tree,true,lease.observer);
            StoreZipWriter writer;
            try {
                List<StoreZipWriter.Entry> sources=new ArrayList<>();
                for(ArchiveFile file:scanArchive(lease)) {
                    if(file.size<0)try(InputStream probe=openArchiveSource(lease,file)){}
                    sources.add(new StoreZipWriter.Entry(file.relative,file.size,file.entry.modified<0?0:file.entry.modified,()->openArchiveSource(lease,file),input->validateArchiveSource(lease,file,input)));
                }
                try{writer=new StoreZipWriter(sources);}catch(IOException error){throw new ToolsHttp.Failure(409,error.getMessage());}
                writer.prepare(()->{checkArchive(lease);response.processing();});
            }catch(ToolsHttp.Failure error){throw error;}
            catch(IOException error){throw new ToolsHttp.Failure(409,"原文件已变化或无法读取，请重新下载目录");}
            checkArchive(lease);
            String encoded=java.net.URLEncoder.encode(target.name+".zip","UTF-8").replace("+","%20").replace("*","%2A");
            response.begin(200,"application/zip",writer.contentLength(),"Accept-Ranges: none\r\nContent-Disposition: attachment; filename*=UTF-8''"+encoded+"\r\n");
            if(!request.method.equals("HEAD"))writer.write((bytes,length)->{checkArchive(lease);response.write(bytes,length);},()->checkArchive(lease));
        }finally {
            if(lease.observer!=null)context.getContentResolver().unregisterContentObserver(lease.observer);
            synchronized(this){archives.remove(lease);if(lease.input!=null)lease.input.close();lease.input=null;}
        }
    }
    static String previewMime(String name){
        String lower=name.toLowerCase(Locale.ROOT);
        if(lower.matches(".*\\.(txt|md|json|csv|log|xml)$"))return "text/plain; charset=utf-8";
        if(lower.endsWith(".svg"))return "image/svg+xml";
        if(lower.endsWith(".pdf"))return "application/pdf";
        String mime=ReceivedStorage.mime(name);
        return mime.startsWith("video/")||mime.startsWith("audio/")||mime.startsWith("image/")?mime:"application/octet-stream";
    }
    @Override public void close(){synchronized(this){if(closed)return;closed=true;cancelArchives(archive->true);invalidateListings();sessions.clear();entries.clear();entryIds.clear();roots.clear();}if(security!=null)security.removeRevocationObserver(revoked);deadlines.cancel();http.close();}
}
