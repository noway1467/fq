import java.net.*;
import java.net.http.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Duration;
import java.util.regex.*;

/** JVM 客户端通过真实 HTTP 读取 Windows 共享，不连接用户设备。 */
public final class SharedHttpInterop {
    private static String match(String json,String pattern){Matcher value=Pattern.compile(pattern).matcher(json);if(!value.find())throw new AssertionError("缺少字段");return value.group(1);}
    public static void main(String[] args)throws Exception{
        String metadata=Files.readString(Path.of(args[0]),StandardCharsets.UTF_8);
        String port=match(metadata,"\"port\":(\\d+)"),code=match(metadata,"\"share\":\"([0-9]{6})\"");
        String base="http://127.0.0.1:"+port;
        HttpClient client=HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();
        HttpResponse<String> login=client.send(HttpRequest.newBuilder(URI.create(base+"/api/login")).header("Content-Type","application/json").POST(HttpRequest.BodyPublishers.ofString("{\"mode\":\"share\",\"code\":\""+code+"\"}")).build(),HttpResponse.BodyHandlers.ofString());
        if(login.statusCode()!=200)throw new AssertionError("Windows 拒绝测试登录："+login.statusCode());
        String token=match(login.body(),"\"token\":\"([a-f0-9]+)\""),cookie="fq_share="+token;
        HttpResponse<String> list=client.send(HttpRequest.newBuilder(URI.create(base+"/api/list")).header("Cookie",cookie).build(),HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8));
        if(list.statusCode()!=200||!list.body().contains("视频 sample.mp4"))throw new AssertionError("共享列表或中文编码错误");
        String id=match(list.body(),"\"id\":\"([a-f0-9]+)\",\"name\":\"视频 sample.mp4\"");
        HttpResponse<byte[]> part=client.send(HttpRequest.newBuilder(URI.create(base+"/file?id="+id)).header("Cookie",cookie).header("Range","bytes=190-255").build(),HttpResponse.BodyHandlers.ofByteArray());
        if(part.statusCode()!=206||part.body().length!=66)throw new AssertionError("范围响应错误");
        for(int i=0;i<66;i++)if((part.body()[i]&255)!=190+i)throw new AssertionError("范围内容不同");
        HttpResponse<String> forbidden=client.send(HttpRequest.newBuilder(URI.create(base+"/api/screen")).header("Cookie",cookie).build(),HttpResponse.BodyHandlers.ofString());
        if(forbidden.statusCode()!=401)throw new AssertionError("共享令牌越权远控");
        client.send(HttpRequest.newBuilder(URI.create(base+"/api/logout")).header("Cookie",cookie).header("Content-Type","application/json").header("X-FQ-Token",token).POST(HttpRequest.BodyPublishers.ofString("{\"mode\":\"share\"}")).build(),HttpResponse.BodyHandlers.discarding());
        String pair=login.headers().allValues("Set-Cookie").stream().filter(value->value.startsWith("fq_pair_share_")).findFirst().orElseThrow().split(";")[0];
        HttpResponse<String> reconnect=client.send(HttpRequest.newBuilder(URI.create(base+"/api/reconnect")).header("Cookie",pair).header("Content-Type","application/json").header("X-FQ-Reconnect","1").POST(HttpRequest.BodyPublishers.ofString("{\"mode\":\"share\"}")).build(),HttpResponse.BodyHandlers.ofString());
        if(reconnect.statusCode()!=200)throw new AssertionError("Windows 拒绝免码重连");
        System.out.println("PASS JVM -> Windows shared HTTP: six-digit pairing, UTF-8 listing, range bytes, remote isolation, reconnect without code");
    }
}
