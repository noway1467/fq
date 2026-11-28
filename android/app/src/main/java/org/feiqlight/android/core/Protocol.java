package org.feiqlight.android.core;

import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Base64;

/** 与桌面端相同的 IPMSG 基础子集；不承诺飞秋私有协议兼容。 */
public final class Protocol {
    public static final int ENTRY=1, EXIT=2, ANSWER=3, ABSENCE=4, MESSAGE=32, ACK=33,
            GET_FILE=96, RELEASE=97, CHECK=0x100, FILE=0x200000, UTF8=0x800000, CAP_UTF8=0x1000000;
    static final int MAX_PACKET_BYTES = 60000;
    static final int MAX_HEADER_COLON_OFFSET = 2048;
    static final int MAX_HEADER_FIELD_CHARS = 128;
    static final int MAX_FILE_NAME_UTF8_BYTES = 240;
    static final int MAX_FILE_NAME_CHARS = 240;
    static final int MAX_RELATIVE_PATH_CHARS = 2048;
    static final int MAX_RELATIVE_PATH_SEGMENTS = 32;
    static final int MAX_PARSED_ATTACHMENTS = 100;
    static final int MAX_AVATAR_JPEG_BYTES = 16384;
    static final int MAX_ATTACHMENTS_PER_MESSAGE = 20;
    static final int HELLO_FRAME_BYTES = 4096;
    private static final Charset GBK = Charset.forName("GBK");
    private Protocol() { }
    public static final class Packet {
        public long number;
        public int command;
        public int replyPort;
        public boolean reverseConnections;
        public String login, host, body, extra, deviceId, avatar, fingerprint;
        public int mode() { return command & 255; }
    }
    public static final class Attachment {
        public final int id;
        public final String name;
        public final long size, modified;
        public String sha256;
        public String relativePath;
        public Attachment(int id, String name, long size, long modified) {
            this.id=id; this.name=name; this.size=size; this.modified=modified;
        }
    }
    public static String field(String value) {
        return value == null ? "" : value.replace(':','_').replace("\0", "").replace("\r\n"," ").replace('\n',' ').replace('\r',' ').replace('\u000b',' ').replace('\u2028',' ').replace('\u2029',' ');
    }
    public static String prefix(String value,int length) {
        int end=Math.min(value.length(),length);
        if(end>0 && end<value.length() && Character.isHighSurrogate(value.charAt(end-1)) && Character.isLowSurrogate(value.charAt(end))) end--;
        return value.substring(0,end);
    }
    public static byte[] encode(long number, String login, String host, int command, String body, String extra) {
        return encode(number,login,host,command,body,extra,null);
    }
    public static String normalizeDeviceId(String value) {
        return value!=null && value.matches("[0-9a-fA-F]{32}") && !value.equals("00000000000000000000000000000000") ? value.toLowerCase(Locale.ROOT) : null;
    }
    private static boolean asciiNumber(String text,boolean hex) {
        if(text.isEmpty())return false;
        for(int index=0;index<text.length();index++) {
            char digit=text.charAt(index);
            if(digit>='0' && digit<='9')continue;
            if(!hex || !(digit>='a' && digit<='f' || digit>='A' && digit<='F'))return false;
        }
        return true;
    }
    private static byte[] decodeBase64(String text) {
        byte[] bytes=Base64.getDecoder().decode(text);
        if(!Base64.getEncoder().encodeToString(bytes).equals(text))throw new IllegalArgumentException("Base64 不是标准带填充格式");
        return bytes;
    }
    public static String normalizeAvatar(String value) {
        if("default".equals(value)) return value;
        if(value!=null && value.matches("builtin:([0-9]|1[0-9]|2[0-3])")) return value;
        if(value==null || !value.startsWith("image:") || value.length()>21854) return null;
        try {
            byte[] bytes=decodeBase64(value.substring(6));
            if(bytes.length<12 || bytes.length>MAX_AVATAR_JPEG_BYTES || (bytes[0]&255)!=255 || (bytes[1]&255)!=216 || (bytes[bytes.length-2]&255)!=255 || (bytes[bytes.length-1]&255)!=217) return null;
            for(int offset=2;offset+8<bytes.length;) {
                if((bytes[offset++]&255)!=255) return null;
                while(offset<bytes.length && (bytes[offset]&255)==255) offset++;
                if(offset+2>=bytes.length) return null;
                int marker=bytes[offset++]&255, length=((bytes[offset]&255)<<8)|(bytes[offset+1]&255);
                if(length<2 || offset+length>bytes.length) return null;
                if(marker==192) {
                    if(length<8 || bytes[offset+2]!=8) return null;
                    int height=((bytes[offset+3]&255)<<8)|(bytes[offset+4]&255), width=((bytes[offset+5]&255)<<8)|(bytes[offset+6]&255);
                    return (width==64 || width==128) && height==width ? "image:"+Base64.getEncoder().encodeToString(bytes) : null;
                }
                if(marker==218 || marker==217) return null;
                offset+=length;
            }
        } catch(IllegalArgumentException ignored) { }
        return null;
    }
    public static byte[] encode(long number, String login, String host, int command, String body, String extra, String deviceId) {
        return encode(number,login,host,command,body,extra,deviceId,null);
    }
    public static byte[] encode(long number, String login, String host, int command, String body, String extra, String deviceId, String avatar) {
        return encode(number,login,host,command,body,extra,deviceId,avatar,null);
    }
    public static byte[] encode(long number, String login, String host, int command, String body, String extra, String deviceId, String avatar,String fingerprint) {
        if (number < 0 || field(login).length() > MAX_HEADER_FIELD_CHARS || field(host).length() > MAX_HEADER_FIELD_CHARS)
            throw new IllegalArgumentException("无效的报文头");
        if ((body!=null && body.indexOf('\0')>=0) || (extra!=null && extra.indexOf('\0')>=0))
            throw new IllegalArgumentException("消息不能包含空字符");
        String wire="1:"+number+":"+field(login)+":"+field(host)+":"+Integer.toUnsignedString(command)+":"+(body==null?"":body)+"\0";
        if (extra != null || deviceId != null || avatar!=null) wire+=(extra==null?"":extra)+"\0";
        if (normalizeDeviceId(deviceId)!=null) wire+="FQID="+normalizeDeviceId(deviceId)+"\0";
        if(avatar!=null) {String normalized=normalizeAvatar(avatar);if(normalized==null)throw new IllegalArgumentException("头像数据无效");wire+="FQAV="+normalized+"\0";}
        if(fingerprint!=null)wire+="FQSEC="+fingerprint+"\0";
        byte[] bytes;
        try {
            ByteBuffer encoded=((command & UTF8)!=0 ? StandardCharsets.UTF_8 : GBK).newEncoder().encode(CharBuffer.wrap(wire));
            bytes=new byte[encoded.remaining()]; encoded.get(bytes);
        } catch (CharacterCodingException e) {
            throw new IllegalArgumentException((command & UTF8)!=0 ? "文本包含不完整的 Unicode 字符，请重新输入" : "对方未声明 UTF-8 支持，当前文字或文件名无法无损发送，请刷新联系人或更换内容",e);
        }
        if (bytes.length > MAX_PACKET_BYTES) throw new IllegalArgumentException("消息过长，请分段发送");
        return bytes;
    }
    public static Packet parse(byte[] bytes) {
        if (bytes==null || bytes.length<12 || bytes.length>MAX_PACKET_BYTES) return null;
        try {
            int colons=0, end=-1;
            for (int i=0;i<bytes.length;i++) if (bytes[i]==58 && ++colons==5) { end=i; break; }
            if (end<0 || end>MAX_HEADER_COLON_OFFSET) return null;
            String[] head=new String(bytes,0,end,StandardCharsets.US_ASCII).split(":",-1);
            if (!head[0].equals("1") || !head[1].matches("[0-9]+") || !head[4].matches("[0-9]+")) return null;
            long number=Long.parseLong(head[1]), cmd=Long.parseLong(head[4]);
            if (cmd<0 || cmd>0xffffffffL) return null;
            String[] parts=(((int)cmd & UTF8)!=0 ? StandardCharsets.UTF_8 : GBK).newDecoder().decode(ByteBuffer.wrap(bytes)).toString().split(":",6);
            if (parts[2].length()>MAX_HEADER_FIELD_CHARS || parts[3].length()>MAX_HEADER_FIELD_CHARS) return null;
            String[] content=parts[5].split("\u0000",-1);
            Packet p=new Packet(); p.number=number; p.command=(int)cmd; p.login=parts[2]; p.host=parts[3];
            p.body=content[0]; p.extra=content.length>1 ? content[1] : "";
            p.deviceId=content.length>2 && content[2].startsWith("FQID=") ? normalizeDeviceId(content[2].substring(5)) : null;
            for(int i=2;i<content.length;i++) if(content[i].startsWith("FQAV=")) p.avatar=normalizeAvatar(content[i].substring(5));
            for(int i=2;i<content.length;i++) if(content[i].startsWith("FQSEC=")) {String value=content[i].substring(6);if(value.matches("[0-9a-f]{64}"))p.fingerprint=value;}
            for(int index=2;index<content.length;index++) {
                if(!content[index].startsWith("FQPORT="))continue;
                String digits=content[index].substring(7);
                if(!asciiNumber(digits,false))continue;
                try {
                    int port=Integer.parseInt(digits);
                    if(port>=1024 && port<=65535)p.replyPort=port;
                } catch(NumberFormatException overflow) { continue; }
            }
            for(int i=2;i<content.length;i++)if(content[i].equals("FQREV=1"))p.reverseConnections=true;
            return p;
        } catch (IllegalArgumentException | IndexOutOfBoundsException | CharacterCodingException e) { return null; }
    }
    public static boolean safeName(String name) {
        return safeName(name,false,false);
    }
    private static boolean safeName(String name,boolean stored,boolean legacyLength) {
        if (name==null || name.trim().isEmpty() || name.length()>MAX_FILE_NAME_CHARS || name.endsWith(".") || name.endsWith(" ")) return false;
        if (!stored && name.codePoints().allMatch(ch -> Character.isWhitespace(ch) || Character.isSpaceChar(ch))) return false;
        try { int bytes=StandardCharsets.UTF_8.newEncoder().encode(CharBuffer.wrap(name)).remaining(); if(!legacyLength&&bytes>MAX_FILE_NAME_UTF8_BYTES) return false; }
        catch(CharacterCodingException e) { return false; }
        for (char c:name.toCharArray()) if (Character.isISOControl(c) || "<>:\"/\\|?*".indexOf(c)>=0) return false;
        String stem=name.split("\\.",2)[0].replaceAll(" +$", "").toUpperCase(Locale.ROOT).replace('¹','1').replace('²','2').replace('³','3');
        return !stem.matches("CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]");
    }
    public static String files(List<Attachment> list) {
        StringBuilder s=new StringBuilder();
        for (Attachment f:list) s.append(f.id).append(':').append(f.name).append(':')
            .append(Long.toHexString(f.size)).append(':').append(Long.toHexString(f.modified)).append(":1:").append(f.sha256==null?"":f.sha256).append(':').append(f.relativePath==null?"":java.util.Base64.getEncoder().encodeToString(f.relativePath.getBytes(StandardCharsets.UTF_8))).append(":\u0007");
        return s.toString();
    }
    public static List<Attachment> parseFiles(String text) {
        return parseFiles(text,false);
    }
    public static boolean safeRelativePath(String value){return safeRelativePath(value,false);}
    private static boolean safeRelativePath(String value,boolean stored) {
        if(value==null||value.length()>MAX_RELATIVE_PATH_CHARS)return false;
        String[] parts=value.split("/",-1);
        return parts.length<=MAX_RELATIVE_PATH_SEGMENTS && java.util.Arrays.stream(parts).allMatch(part->safeName(part,stored,false));
    }
    // 旧版记录允许 240 个 UTF-16 单元。只恢复历史元数据，不放宽网络邀请或落盘文件名校验。
    public static List<Attachment> parseStoredFiles(String text) {
        return parseFiles(text,true);
    }
    private static List<Attachment> parseFiles(String text,boolean stored) {
        List<Attachment> files=new ArrayList<>();
        if (text==null) return files;
        String[] rows=text.split("\u0007",MAX_PARSED_ATTACHMENTS+1);
        for (int i=0;i<Math.min(rows.length,MAX_PARSED_ATTACHMENTS);i++) try {
            String[] f=rows[i].split(":",-1);
            if (f.length<5 || !safeName(f[1],stored,stored)) continue;
            if (!stored && !(asciiNumber(f[0],false) && asciiNumber(f[2],true) && asciiNumber(f[3],true) && asciiNumber(f[4],true))) continue;
            int id=Integer.parseInt(f[0]);
            long size=Long.parseLong(f[2],16);
            long time=stored && !asciiNumber(f[3],true) ? Long.parseLong(f[3],16) : Long.parseUnsignedLong(f[3],16);
            long attr=stored ? Long.parseLong(f[4],16) : Integer.toUnsignedLong(Integer.parseUnsignedInt(f[4],16));
            if (id<0 || size<0 || (attr & 255)!=1) continue;
            boolean duplicate=false; for (Attachment item:files) if (item.id==id) duplicate=true;
            String hash=f.length>5?f[5]:"";if(!hash.isEmpty()&&!hash.matches("[0-9a-f]{64}"))continue;
            String relative=null;
            if(f.length>6&&!f[6].isEmpty()) {
                byte[] bytes=stored ? Base64.getDecoder().decode(f[6]) : decodeBase64(f[6]);
                relative=StandardCharsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes)).toString();
                if(!safeRelativePath(relative,stored)||!relative.substring(relative.lastIndexOf('/')+1).equals(f[1]))continue;
            }
            if (!duplicate) {Attachment item=new Attachment(id,f[1],size,time);item.sha256=hash.isEmpty()?null:hash;item.relativePath=relative;files.add(item);}
        } catch (IllegalArgumentException | CharacterCodingException ignored) { /* 单个坏附件不影响其余合法附件。 */ }
        return files;
    }
}
