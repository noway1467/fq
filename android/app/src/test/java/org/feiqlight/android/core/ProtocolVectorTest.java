package org.feiqlight.android.core;

import org.json.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import static org.junit.Assert.*;

/** 共用 JSON 是唯一预期来源；差异向量锁定现状，不放宽生产校验。 */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=35)
public final class ProtocolVectorTest {
    private static String text(JSONObject value,String key) {return value.isNull(key)?null:value.optString(key,null);}
    private static void equal(Object expected,Object actual,String context)throws Exception {
        if(expected==JSONObject.NULL) {assertTrue(context,actual==null||actual==JSONObject.NULL);return;}
        if(expected instanceof JSONObject) {
            assertTrue(context,actual instanceof JSONObject);JSONObject a=(JSONObject)expected,b=(JSONObject)actual;
            assertEquals(context,a.length(),b.length());Iterator<String> keys=a.keys();while(keys.hasNext()){String key=keys.next();equal(a.get(key),b.get(key),context+"."+key);}
        } else if(expected instanceof JSONArray) {
            assertTrue(context,actual instanceof JSONArray);JSONArray a=(JSONArray)expected,b=(JSONArray)actual;
            assertEquals(context,a.length(),b.length());for(int i=0;i<a.length();i++)equal(a.get(i),b.get(i),context+"["+i+"]");
        } else if(expected instanceof Number) assertEquals(context,((Number)expected).longValue(),((Number)actual).longValue());
        else assertEquals(context,expected,actual);
    }
    private static JSONObject packet(byte[] bytes)throws Exception {
        Protocol.Packet p=Protocol.parse(bytes);if(p==null)return null;
        JSONObject value=new JSONObject();value.put("number",p.number).put("command",Integer.toUnsignedLong(p.command)).put("login",p.login).put("host",p.host).put("body",p.body).put("extra",p.extra).put("replyPort",p.replyPort).put("reverseConnections",p.reverseConnections);
        value.put("deviceId",p.deviceId==null?JSONObject.NULL:p.deviceId).put("avatar",p.avatar==null?JSONObject.NULL:p.avatar).put("fingerprint",p.fingerprint==null?JSONObject.NULL:p.fingerprint);return value;
    }
    private static JSONArray files(List<Protocol.Attachment> files)throws Exception {
        JSONArray values=new JSONArray();for(Protocol.Attachment f:files)values.put(new JSONObject().put("id",f.id).put("name",f.name).put("size",f.size).put("modified",f.modified).put("sha256",f.sha256==null?JSONObject.NULL:f.sha256).put("relativePath",f.relativePath==null?JSONObject.NULL:f.relativePath));return values;
    }
    private static Object execute(JSONObject test)throws Exception {
        String input=text(test,"input");
        switch(test.getString("op")) {
            case "field":return Protocol.field(input);
            case "prefix":return Protocol.prefix(input,test.getInt("length"));
            case "device":return Protocol.normalizeDeviceId(input);
            case "pair":return SecureIdentity.pairCode(input,test.getString("second"));
            case "avatar":return Protocol.normalizeAvatar(input);
            case "name":return Protocol.safeName(input);
            case "path":return Protocol.safeRelativePath(input);
            case "packet":return packet(Base64.getDecoder().decode(input));
            case "encode":
                JSONObject p=test.getJSONObject("input");
                String body=text(p,"body");
                if(p.has("bodyUtf16")){JSONArray units=p.getJSONArray("bodyUtf16");char[] chars=new char[units.length()];for(int i=0;i<chars.length;i++)chars[i]=(char)units.getInt(i);body=new String(chars);}
                byte[] encoded=Protocol.encode(p.getLong("number"),text(p,"login"),text(p,"host"),(int)p.getLong("command"),body,text(p,"extra"),text(p,"deviceId"),text(p,"avatar"),text(p,"fingerprint"));
                if(test.has("encodedLength"))assertEquals("编码字节数",test.getInt("encodedLength"),encoded.length);
                return packet(encoded);
            case "storedFiles":
                assertTrue("历史兼容不得放宽网络入口",Protocol.parseFiles(input).isEmpty());
                return files(Protocol.parseStoredFiles(input));
            case "files":return files(Protocol.parseFiles(input));
            case "filesRoundTrip":
                List<Protocol.Attachment> list=new ArrayList<>();JSONArray items=test.getJSONArray("input");
                for(int i=0;i<items.length();i++){JSONObject f=items.getJSONObject(i);Protocol.Attachment a=new Protocol.Attachment(f.getInt("id"),f.getString("name"),f.getLong("size"),f.getLong("modified"));a.sha256=text(f,"sha256");a.relativePath=text(f,"relativePath");list.add(a);}
                String wire=Protocol.files(list);return new JSONObject().put("wire",wire).put("files",files(Protocol.parseFiles(wire)));
            case "frame":
                ByteArrayOutputStream output=new ByteArrayOutputStream();SecureIdentity.writeFrame(output,Base64.getDecoder().decode(input));
                return new JSONObject().put("wire",Base64.getEncoder().encodeToString(output.toByteArray())).put("payload",Base64.getEncoder().encodeToString(SecureIdentity.readFrame(new ByteArrayInputStream(output.toByteArray()),test.getInt("maximum"))));
            case "readFrame":return Base64.getEncoder().encodeToString(SecureIdentity.readFrame(new ByteArrayInputStream(Base64.getDecoder().decode(input)),test.getInt("maximum")));
            default:throw new AssertionError("未知向量操作："+test.getString("op"));
        }
    }
    private static void runCase(JSONObject test,Object expected)throws Exception {
        if(expected instanceof JSONObject&&((JSONObject)expected).optBoolean("error",false)) {
            try{execute(test);}catch(IllegalArgumentException|IOException e){return;}
            fail(test.getString("name")+" 未拒绝非法输入");
        }
        equal(expected,execute(test),test.getString("name"));
    }
    private static void constants(JSONObject c,Path root)throws Exception {
        JSONObject commands=new JSONObject();String[] names={"entry","exit","answer","absence","sendMessage","receiveMessage","getFile","releaseFiles"};int[] values={Protocol.ENTRY,Protocol.EXIT,Protocol.ANSWER,Protocol.ABSENCE,Protocol.MESSAGE,Protocol.ACK,Protocol.GET_FILE,Protocol.RELEASE};
        for(int i=0;i<names.length;i++)commands.put(names[i],values[i]);equal(c.get("commands"),commands,"命令常量");
        equal(c.get("flags"),new JSONObject().put("sendCheck",Protocol.CHECK).put("fileAttach",Protocol.FILE).put("utf8",Protocol.UTF8).put("capUtf8",Protocol.CAP_UTF8),"标志常量");
        String protocol=new String(Files.readAllBytes(root.resolve("android/app/src/main/java/org/feiqlight/android/core/Protocol.java")),StandardCharsets.UTF_8);
        String[][] checks={{"maxPacketBytes","bytes.length>"},{"maxHeaderColonOffset","end>"},{"maxHeaderFieldChars","parts[2].length()>"},{"maxFileNameUtf8Bytes","bytes>"},{"maxFileNameChars","name.length()>"},{"maxRelativePathChars","value.length()>"},{"maxRelativePathSegments","parts.length<="},{"maxParsedAttachments","Math.min(rows.length,"},{"maxAvatarJpegBytes","bytes.length>"}};
        for(String[] check:checks)assertTrue("生产边界与常量不一致："+check[0],protocol.contains(check[1]+c.getInt(check[0])));
        String lan=new String(Files.readAllBytes(root.resolve("android/app/src/main/java/org/feiqlight/android/core/LanNode.java")),StandardCharsets.UTF_8);
        assertTrue("默认端口",lan.contains(":"+c.getInt("defaultChatPort")));
        assertTrue("邀请上限",lan.contains("paths.size()>"+c.getInt("maxAttachmentsPerMessage")));
        assertTrue("Hello 上限",lan.contains(","+c.getInt("helloFrameBytes")+")"));
        Class<?> messageText=Class.forName("org.feiqlight.android.MessageText");java.lang.reflect.Field threshold=messageText.getDeclaredField("FILE_THRESHOLD");threshold.setAccessible(true);
        assertEquals("文本阈值",c.getInt("textAsFileUtf8Bytes"),threshold.getInt(null));
        ByteArrayOutputStream stream=new ByteArrayOutputStream();int maximum=c.getInt("maxFrameBytes");SecureIdentity.writeFrame(stream,new byte[maximum]);
        try{SecureIdentity.writeFrame(stream,new byte[maximum+1]);fail("最大帧未拒绝超限");}catch(IOException expected){}
    }
    @Test public void sharedProtocolVectors()throws Exception {
        Path vector=null;
        for(String candidate:new String[]{"../../tests/protocol-vectors.json","../tests/protocol-vectors.json","tests/protocol-vectors.json"})if(Files.isRegularFile(Paths.get(candidate))){vector=Paths.get(candidate).toAbsolutePath().normalize();break;}
        assertNotNull("未找到共用协议向量文件 tests/protocol-vectors.json",vector);
        JSONObject data=new JSONObject(new String(Files.readAllBytes(vector),StandardCharsets.UTF_8));assertEquals(1,data.getInt("version"));constants(data.getJSONObject("constants"),vector.getParent().getParent());int count=0;
        for(String group:new String[]{"fieldCases","prefixCases","deviceIdAndPairCodeCases","avatarCases","fileNameAndRelativePathCases","packetCases","attachmentListCases","frameCases","knownPlatformDivergences"}) {
            JSONArray cases=data.getJSONArray(group);assertTrue("向量组不能为空："+group,cases.length()>0);
            for(int i=0;i<cases.length();i++){JSONObject test=cases.getJSONObject(i);runCase(test,test.get(group.equals("knownPlatformDivergences")?"android":"expected"));count++;}
            System.out.println("PASS protocol "+group+" "+cases.length());
        }
        System.out.println("PASS 共用协议向量 "+count+" 项 + 常量核对");
    }
}
