package org.feiqlight.android;

import android.content.Context;
import org.feiqlight.android.core.ToolsHttp;
import org.json.JSONObject;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.Locale;
import java.util.function.BooleanSupplier;

/** 手机远控独立端口和授权，不让文件共享凭据获得屏幕或输入权限。 */
final class PhoneRemoteAccess implements AutoCloseable {
    interface Backend {
        int confirm(String address) throws Exception;
        byte[] capture() throws Exception;
        JSONObject displays() throws Exception;
        void input(JSONObject data,BooleanSupplier authorized) throws Exception;
        void disconnected();
    }
    private final Context context;
    private final ToolsPairings pairings;
    private final ToolsHttp http;
    private final Backend backend;
    private final Object gate=new Object();
    private final String code=String.format(Locale.ROOT,"%06d",new SecureRandom().nextInt(1000000));
    private final long created=System.nanoTime();
    private String token="",address="";
    private long last,attemptTime;
    private int attempts,epoch;
    private boolean closed,pending,codeUsed;
    PhoneRemoteAccess(Context context,int port,boolean loopback,Backend backend)throws IOException{
        this.context=context.getApplicationContext();this.backend=backend;pairings=new ToolsPairings(context);http=new ToolsHttp(port,loopback,this::handle);
    }
    static int portFor(int chatPort){return chatPort>=65534?chatPort-2:chatPort+2;}
    int port(){return http.port();}
    String code(){synchronized(gate){return closed?"已停止":codeUsed||System.nanoTime()-created>600000000000L?"等待已配对设备":code;}}
    String address(){synchronized(gate){return valid()?address:"";}}
    private boolean valid(){return !closed&&!token.isEmpty()&&System.nanoTime()-last<30000000000L;}
    void expire(){boolean expired; synchronized(gate){expired=!token.isEmpty()&&!valid();if(expired){token="";epoch++;}}if(expired)backend.disconnected();}
    void forget()throws IOException{synchronized(gate){pairings.clear();epoch++;token="";pending=false;codeUsed=true;}backend.disconnected();}
    private static JSONObject json(ToolsHttp.Request request)throws Exception{
        if(!request.header("Content-Type").startsWith("application/json"))throw new ToolsHttp.Failure(400,"请求格式错误");
        return new JSONObject(new String(request.body,StandardCharsets.UTF_8));
    }
    private static String cookie(ToolsHttp.Request request,String name){for(String item:request.header("Cookie").split(";"))if(item.trim().startsWith(name+"="))return item.trim().substring(name.length()+1);return "";}
    private String pairName(){return "fq_pair_phone_"+port();}
    private int authorize(ToolsHttp.Request request)throws ToolsHttp.Failure{
        synchronized(gate){
            if(!valid()||!address.equals(request.address)||!token.equals(cookie(request,"fq_phone")))throw new ToolsHttp.Failure(401,"控制已结束");
            if(request.method.equals("POST")&&!token.equals(request.header("X-FQ-Token")))throw new ToolsHttp.Failure(403,"请求校验失败");
            last=System.nanoTime();return epoch;
        }
    }
    private boolean current(int version){synchronized(gate){return epoch==version&&valid();}}
    private void login(ToolsHttp.Request request,ToolsHttp.Response response,boolean reconnect)throws Exception{
        JSONObject data=json(request);int version;String remembered=cookie(request,pairName());boolean trusted;
        synchronized(gate){
            long now=System.nanoTime();if(now-attemptTime>=60000000000L){attemptTime=now;attempts=0;}
            if(++attempts>8)throw new ToolsHttp.Failure(429,"尝试过多，请一分钟后重试");
            if(closed)throw new ToolsHttp.Failure(403,"远控已停止");
            if(!data.optString("mode").equals("remote"))throw new ToolsHttp.Failure(403,"此端口仅供手机远控");
            if(reconnect){if(!request.header("X-FQ-Reconnect").equals("1"))throw new ToolsHttp.Failure(403,"请求校验失败");if(!pairings.contains(remembered,"phone-remote"))throw new ToolsHttp.Failure(401,"请先输入连接码配对");}
            else if(codeUsed||now-created>=600000000000L||!code.equals(data.optString("code")))throw new ToolsHttp.Failure(403,"连接码错误或过期");
            if(pending||valid())throw new ToolsHttp.Failure(409,"已有控制端或请求等待确认");
            trusted=reconnect&&pairings.alwaysAllows(remembered,"phone-remote");
            // 旧会话已超时；先撤销令牌，避免定时清理误撤销正在等待本机确认的新请求。
            token="";address="";pending=true;version=++epoch;if(!reconnect)codeUsed=true;
        }
        try{
            if(!trusted)response.waitingForUser();
            int approval=trusted?1:backend.confirm(request.address);if(approval==0)throw new ToolsHttp.Failure(403,"手机未同意本次控制");
            synchronized(gate){
                if(closed||epoch!=version)throw new ToolsHttp.Failure(403,"远控已停止");
                remembered=pairings.remember(remembered,"phone-remote");if(approval==2)pairings.allowAlways(remembered);token=SharedFiles.random(32);address=request.address;last=System.nanoTime();
                response.json(new JSONObject().put("token",token).put("mode","remote").toString(),"Set-Cookie: fq_phone="+token+"; Path=/; HttpOnly; SameSite=Strict\r\nSet-Cookie: "+pairName()+"="+remembered+"; Path=/; HttpOnly; SameSite=Strict; Max-Age=7776000\r\n");
            }
        }finally{synchronized(gate){if(epoch==version)pending=false;}}
    }
    private void handle(ToolsHttp.Request request,ToolsHttp.Response response)throws Exception{
        if(!request.method.equals("GET")&&!request.method.equals("HEAD")&&!request.method.equals("POST"))throw new ToolsHttp.Failure(405,"不支持此操作");
        if(request.method.equals("GET")&&request.path.equals("/api/status")){
            synchronized(gate){
                String remembered=cookie(request,pairName());boolean paired=!closed&&pairings.contains(remembered,"phone-remote");
                response.json(new JSONObject().put("remote",!closed).put("paired",paired).put("trusted",paired&&pairings.alwaysAllows(remembered,"phone-remote")).put("platform","android").toString(),"");
            }
            return;
        }
        if(request.method.equals("POST")){
            if(request.path.equals("/api/login")||request.path.equals("/api/reconnect")){login(request,response,request.path.equals("/api/reconnect"));return;}
            int version=authorize(request);
            if(request.path.equals("/api/logout")){synchronized(gate){if(epoch==version){token="";epoch++;}}backend.disconnected();response.json("{\"ok\":true}","");return;}
            if(request.path.equals("/api/input")){backend.input(json(request),()->current(version));if(!current(version))throw new ToolsHttp.Failure(401,"控制已停止");response.json("{\"ok\":true}","");return;}
            throw new ToolsHttp.Failure(404,"没有此操作");
        }
        if(request.path.equals("/api/displays")){authorize(request);response.json(backend.displays().toString(),"");return;}
        if(request.path.equals("/api/screen")){int version=authorize(request);byte[] frame=backend.capture();if(!current(version))throw new ToolsHttp.Failure(401,"控制已停止");response.bytes(200,"image/jpeg",frame,"");return;}
        if(request.path.startsWith("/api/")||request.path.equals("/file"))throw new ToolsHttp.Failure(404,"没有此操作");
        if(request.path.equals("/favicon.ico")||request.path.startsWith("/native/")){response.bytes(204,"image/x-icon",new byte[0],"");return;}
        String asset=request.path.equals("/")?"index.html":request.path.substring(1);
        if(!asset.equals("index.html")&&!asset.equals("app.css")&&!asset.equals("app.js"))throw new ToolsHttp.Failure(404,"没有此页面");
        try(InputStream stream=context.getAssets().open(asset);ByteArrayOutputStream output=new ByteArrayOutputStream()){
            byte[] bytes=new byte[8192];int count;while((count=stream.read(bytes))!=-1)output.write(bytes,0,count);
            response.bytes(200,asset.endsWith(".js")?"application/javascript; charset=utf-8":asset.endsWith(".css")?"text/css; charset=utf-8":"text/html; charset=utf-8",output.toByteArray(),"");
        }
    }
    @Override public void close(){synchronized(gate){closed=true;token="";epoch++;pending=false;}http.close();backend.disconnected();}
}
