package org.feiqlight.android;

import android.content.Context;
import android.net.Uri;
import org.feiqlight.android.core.*;
import org.json.*;
import java.io.*;
import java.nio.file.Files;
import java.util.*;
import java.util.concurrent.*;

/** 临时访客只访问本次文件；共享、远控的证书授权不参与此端口。 */
final class GuestTransfer implements AutoCloseable {
    private static final class Download {
        final LanNode.Source source;final String id;
        Download(LanNode.Source source){this.source=source;id=SharedFiles.random(16);}
    }
    private static final class Upload {
        final String id,name;final long size;final File partial,target;long offset,time;boolean canceled,moved,placed,saved;String warning="";
        Upload(String name,long size,File directory){this.name=name;this.size=size;id=SharedFiles.random(32);target=new File(directory,id);partial=new File(directory,".fq-guest-"+id+".part");}
    }
    static final long UPLOAD_QUOTA=10L*1024*1024*1024;
    private final Context context;
    private final Map<String,Download> downloads=new LinkedHashMap<>();
    private final Map<String,Upload> uploads=new LinkedHashMap<>();
    private final File directory;
    private final boolean allowUpload;
    private final ToolsHttp http;
    private final ScheduledExecutorService expiry=Executors.newSingleThreadScheduledExecutor(r->{Thread t=new Thread(r,"guest-expiry");t.setDaemon(true);return t;});
    private volatile boolean closed;
    private long reserved;
    private int activeUploads;
    final String token=SharedFiles.random(32);
    final long until=System.currentTimeMillis()+TimeUnit.MINUTES.toMillis(15);
    private final long deadline=System.nanoTime()+TimeUnit.MINUTES.toNanos(15);
    volatile String cleanupError="";
    Runnable received=()->{};
    java.util.function.Consumer<String> warning=text->{};
    GuestTransfer(Context context,List<LanNode.Source> sources,boolean allowUpload,boolean loopback)throws IOException {
        this(context,sources,allowUpload,loopback,loopback?null:new LanNetwork(context.getSystemService(android.net.ConnectivityManager.class)));
    }
    GuestTransfer(Context context,List<LanNode.Source> sources,boolean allowUpload,boolean loopback,LanNode.SocketBinding binding)throws IOException {
        if(!loopback&&binding==null)throw new IOException("请先连接局域网");
        this.context=context.getApplicationContext();this.allowUpload=allowUpload;directory=new File(context.getFilesDir(),"received");Files.createDirectories(directory.toPath());
        if(sources.size()>1000)throw new IOException("单次最多 1000 个文件");
        for(LanNode.Source source:sources){if(!Protocol.safeName(source.name())||source.size()<0)throw new IOException("文件名或大小无效");Download file=new Download(source);downloads.put(file.id,file);}
        if(downloads.isEmpty()&&!allowUpload)throw new IOException("请先选择文件或开启接收");
        http=new ToolsHttp(0,loopback,this::handle,null,loopback?null:binding,1048576);
        expiry.schedule(this::close,15,TimeUnit.MINUTES);
    }
    int port(){return http.port();}
    boolean active(){return !closed&&System.nanoTime()<deadline;}
    String url(String address){return "http://"+address+":"+port()+"/#"+token;}
    private void check()throws ToolsHttp.Failure{if(!active())throw new ToolsHttp.Failure(410,"临时传输已结束，请让对方重新开启");}
    private void authorize(ToolsHttp.Request request)throws IOException {
        check();String supplied=request.header("X-FQ-Guest");if(supplied.isEmpty()&&(request.method.equals("GET")||request.method.equals("HEAD")))supplied=request.parameter("token");
        if(!token.equals(supplied))throw new ToolsHttp.Failure(403,"链接无效，请使用对方分享的完整链接");
    }
    private void handle(ToolsHttp.Request request,ToolsHttp.Response response)throws Exception {
        check();
        if(request.method.equals("GET")&&(request.path.equals("/")||request.path.equals("/guest.css")||request.path.equals("/guest.js")||request.path.equals("/qrcodegen.js"))) {
            String name=request.path.equals("/")?"guest.html":request.path.substring(1);
            try(InputStream input=context.getAssets().open(name);ByteArrayOutputStream data=new ByteArrayOutputStream()){byte[] buffer=new byte[8192];int count;while((count=input.read(buffer))!=-1)data.write(buffer,0,count);response.bytes(200,name.endsWith(".js")?"application/javascript; charset=utf-8":name.endsWith(".css")?"text/css; charset=utf-8":"text/html; charset=utf-8",data.toByteArray(),"");}return;
        }
        if(request.path.equals("/favicon.ico")){response.bytes(204,"image/x-icon",new byte[0],"");return;}
        authorize(request);
        if(request.method.equals("GET")&&request.path.equals("/api/guest")) {
            JSONArray list=new JSONArray();for(Download file:downloads.values())list.put(new JSONObject().put("id",file.id).put("name",file.source.name()).put("size",file.source.size()));
            response.json(new JSONObject().put("files",list).put("upload",allowUpload).put("until",java.time.Instant.ofEpochMilli(until).toString()).put("limit",UPLOAD_QUOTA).toString(),"");return;
        }
        if((request.method.equals("GET")||request.method.equals("HEAD"))&&request.path.equals("/file")){send(request,response);return;}
        if(!allowUpload)throw new ToolsHttp.Failure(403,"对方未开启浏览器接收");
        if(request.method.equals("POST")&&request.path.equals("/api/prepare")) {
            if(!request.header("Content-Type").startsWith("application/json"))throw new ToolsHttp.Failure(400,"请求格式错误");
            JSONObject data=new JSONObject(new String(request.body,java.nio.charset.StandardCharsets.UTF_8));String name=data.optString("name");long size=data.optLong("size",-1);Upload upload;
            if(!Protocol.safeName(name)||size<0||size>UPLOAD_QUOTA)throw new ToolsHttp.Failure(400,"文件名或大小无效");
            synchronized(this){check();if(uploads.size()>=1024)throw new ToolsHttp.Failure(409,"本次上传任务过多，请重新开启临时传输");
                if(activeUploads>=100||size>UPLOAD_QUOTA-reserved)throw new ToolsHttp.Failure(409,"本次接收已达 100 个文件或 10 GiB 上限，请重新开启");
                LanNode.requireSpace(directory,size);upload=new Upload(name,size,directory);Files.createFile(upload.partial.toPath());uploads.put(upload.id,upload);reserved+=size;activeUploads++;}
            response.json(new JSONObject().put("id",upload.id).put("offset",0).toString(),"");return;
        }
        Upload upload;synchronized(this){upload=uploads.get(request.parameter("id"));}if(upload==null)throw new ToolsHttp.Failure(404,"上传任务不存在");
        synchronized(upload) {
            check();if(upload.canceled)throw new ToolsHttp.Failure(410,"上传已取消");
            if(request.method.equals("GET")&&request.path.equals("/api/upload-status")){response.json(new JSONObject().put("offset",upload.offset).put("complete",upload.saved).toString(),"");return;}
            if(request.method.equals("POST")&&request.path.equals("/api/cancel")){
                if(upload.saved||upload.moved)throw new ToolsHttp.Failure(409,"文件已保存");
                Files.deleteIfExists(upload.partial.toPath());
                synchronized(this){upload.canceled=true;reserved=Math.max(0,reserved-upload.size);activeUploads--;}
                response.json("{\"ok\":true}","");return;
            }
            if(upload.saved){response.json(new JSONObject().put("offset",upload.offset).put("complete",true).toString(),"");return;}
            if(request.method.equals("POST")&&request.path.equals("/api/chunk")) {
                if(upload.moved)throw new ToolsHttp.Failure(409,"文件已完整上传，请重试完成保存");
                long offset;try{offset=Long.parseLong(request.parameter("offset"));}catch(NumberFormatException e){throw new ToolsHttp.Failure(400,"上传位置无效");}
                if(offset!=upload.offset||request.body.length==0||request.body.length>upload.size-upload.offset)throw new ToolsHttp.Failure(409,"上传位置已变化，请重试");
                LanNode.requireSpace(directory,request.body.length);
                try(RandomAccessFile output=new RandomAccessFile(upload.partial,"rw")){output.seek(upload.offset);output.write(request.body);output.getFD().sync();upload.offset=output.length();}
                response.json(new JSONObject().put("offset",upload.offset).toString(),"");return;
            }
            if(request.method.equals("POST")&&request.path.equals("/api/finish")) {
                if(upload.offset!=upload.size)throw new ToolsHttp.Failure(409,"文件尚未完整上传");
                synchronized(this){check();if(!upload.moved){Files.move(upload.partial.toPath(),upload.target.toPath());upload.moved=true;upload.time=System.currentTimeMillis();}}
                ReceivedStorage storage=new ReceivedStorage(context);
                if(!upload.placed) {
                    if(storage.tree()!=null)try(LanNode.Transfer task=new LanNode.Transfer()){storage.save(upload.target,upload.name,task);}
                    catch(IOException e){upload.warning=e.getMessage();}
                    upload.placed=true;
                }
                if(!storage.exists(upload.target))throw new IOException("已接收的本机文件已删除，请重新选择文件上传");
                // 文件已移动后记录仍可能写入失败；重试只补写记录，完成时间和原文件保持不变。
                String meta=new JSONObject().put("size",upload.size).put("time",upload.time).toString();
                if(!context.getSharedPreferences("guest_received",Context.MODE_PRIVATE).edit().putString(upload.id,upload.name).putString("meta:"+upload.id,meta).commit())throw new IOException("文件已保存，接收记录未能写入");
                upload.saved=true;
                if(!upload.warning.isEmpty())warning.accept(upload.warning);
                received.run();
                response.json(new JSONObject().put("complete",true).put("name",upload.name).put("warning",upload.warning).toString(),"");return;
            }
            throw new ToolsHttp.Failure(404,"没有此操作");
        }
    }
    private void send(ToolsHttp.Request request,ToolsHttp.Response response)throws Exception {
        Download file=downloads.get(request.parameter("id"));if(file==null)throw new ToolsHttp.Failure(404,"文件不在本次分享中");
        long size=file.source.size();long[] range=ToolsHttp.range(request.header("Range"),size);
        if(range==null){response.bytes(416,"application/octet-stream",new byte[0],"Content-Range: bytes */"+size+"\r\n");return;}
        try(InputStream input=file.source.open(range[0])) {
            long left=Math.max(0,range[1]-range[0]+1);String headers="Content-Disposition: attachment; filename*=UTF-8''"+Uri.encode(file.source.name())+"\r\nAccept-Ranges: bytes\r\n";
            if(!request.header("Range").isEmpty())headers+="Content-Range: bytes "+range[0]+"-"+range[1]+"/"+size+"\r\n";
            response.begin(request.header("Range").isEmpty()?200:206,"application/octet-stream",left,headers);if(request.method.equals("HEAD"))return;
            byte[] buffer=new byte[262144];while(left>0){check();int count=input.read(buffer,0,(int)Math.min(left,buffer.length));if(count<0)throw new EOFException();response.write(buffer,count);left-=count;}
        }
    }
    @Override public void close() {
        List<Upload> pending;synchronized(this){if(closed)return;closed=true;pending=new ArrayList<>(uploads.values());}
        http.close();expiry.shutdownNow();
        for(Upload upload:pending)synchronized(upload){if(!upload.saved)try{Files.deleteIfExists(upload.partial.toPath());}catch(IOException e){cleanupError="未完成文件清理失败："+e.getMessage();}}
    }
}
