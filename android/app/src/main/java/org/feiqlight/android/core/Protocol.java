package org.feiqlight.android.core;

import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** 与桌面端相同的 IPMSG 基础子集；不承诺飞秋私有协议兼容。 */
public final class Protocol {
    public static final int ENTRY=1, EXIT=2, ANSWER=3, ABSENCE=4, MESSAGE=32, ACK=33,
            GET_FILE=96, RELEASE=97, CHECK=0x100, FILE=0x200000, UTF8=0x800000, CAP_UTF8=0x1000000;
    private static final Charset GBK = Charset.forName("GBK");
    private Protocol() { }
    public static final class Packet {
        public long number;
        public int command;
        public String login, host, body, extra;
        public int mode() { return command & 255; }
    }
    public static final class Attachment {
        public final int id;
        public final String name;
        public final long size, modified;
        public Attachment(int id, String name, long size, long modified) {
            this.id=id; this.name=name; this.size=size; this.modified=modified;
        }
    }
    public static String field(String value) {
        return value == null ? "" : value.replace(':','_').replace("\0", "").replace('\n',' ').replace('\r',' ');
    }
    public static String prefix(String value,int length) {
        int end=Math.min(value.length(),length);
        if(end>0 && end<value.length() && Character.isHighSurrogate(value.charAt(end-1)) && Character.isLowSurrogate(value.charAt(end))) end--;
        return value.substring(0,end);
    }
    public static byte[] encode(long number, String login, String host, int command, String body, String extra) {
        if (number < 0 || field(login).length() > 128 || field(host).length() > 128)
            throw new IllegalArgumentException("无效的报文头");
        if ((body!=null && body.indexOf('\0')>=0) || (extra!=null && extra.indexOf('\0')>=0))
            throw new IllegalArgumentException("消息不能包含空字符");
        String wire="1:"+number+":"+field(login)+":"+field(host)+":"+Integer.toUnsignedString(command)+":"+(body==null?"":body)+"\0";
        if (extra != null) wire+=extra+"\0";
        byte[] bytes;
        try {
            ByteBuffer encoded=((command & UTF8)!=0 ? StandardCharsets.UTF_8 : GBK).newEncoder().encode(CharBuffer.wrap(wire));
            bytes=new byte[encoded.remaining()]; encoded.get(bytes);
        } catch (CharacterCodingException e) {
            throw new IllegalArgumentException((command & UTF8)!=0 ? "文本包含不完整的 Unicode 字符，请重新输入" : "对方未声明 UTF-8 支持，当前文字或文件名无法无损发送，请刷新联系人或更换内容",e);
        }
        if (bytes.length > 60000) throw new IllegalArgumentException("消息过长，请分段发送");
        return bytes;
    }
    public static Packet parse(byte[] bytes) {
        if (bytes==null || bytes.length<12 || bytes.length>60000) return null;
        try {
            int colons=0, end=-1;
            for (int i=0;i<bytes.length;i++) if (bytes[i]==58 && ++colons==5) { end=i; break; }
            if (end<0 || end>2048) return null;
            String[] head=new String(bytes,0,end,StandardCharsets.US_ASCII).split(":",-1);
            if (!head[0].equals("1") || !head[1].matches("[0-9]+") || !head[4].matches("[0-9]+")) return null;
            long number=Long.parseLong(head[1]), cmd=Long.parseLong(head[4]);
            if (cmd<0 || cmd>0xffffffffL) return null;
            String[] parts=(((int)cmd & UTF8)!=0 ? StandardCharsets.UTF_8 : GBK).newDecoder().decode(ByteBuffer.wrap(bytes)).toString().split(":",6);
            if (parts[2].length()>128 || parts[3].length()>128) return null;
            String[] content=parts[5].split("\u0000",-1);
            Packet p=new Packet(); p.number=number; p.command=(int)cmd; p.login=parts[2]; p.host=parts[3];
            p.body=content[0]; p.extra=content.length>1 ? content[1] : ""; return p;
        } catch (IllegalArgumentException | IndexOutOfBoundsException | CharacterCodingException e) { return null; }
    }
    public static boolean safeName(String name) {
        return safeName(name,false);
    }
    private static boolean safeName(String name,boolean stored) {
        if (name==null || name.trim().isEmpty() || name.length()>240 || name.endsWith(".") || name.endsWith(" ")) return false;
        try { int bytes=StandardCharsets.UTF_8.newEncoder().encode(CharBuffer.wrap(name)).remaining(); if(!stored&&bytes>240) return false; }
        catch(CharacterCodingException e) { return false; }
        for (char c:name.toCharArray()) if (Character.isISOControl(c) || "<>:\"/\\|?*".indexOf(c)>=0) return false;
        String stem=name.split("\\.",2)[0].replaceAll(" +$", "").toUpperCase(Locale.ROOT).replace('¹','1').replace('²','2').replace('³','3');
        return !stem.matches("CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]");
    }
    public static String files(List<Attachment> list) {
        StringBuilder s=new StringBuilder();
        for (Attachment f:list) s.append(f.id).append(':').append(f.name).append(':')
            .append(Long.toHexString(f.size)).append(':').append(Long.toHexString(f.modified)).append(":1:\u0007");
        return s.toString();
    }
    public static List<Attachment> parseFiles(String text) {
        return parseFiles(text,false);
    }
    // 旧版记录允许 240 个 UTF-16 单元。只恢复历史元数据，不放宽网络邀请或落盘文件名校验。
    public static List<Attachment> parseStoredFiles(String text) {
        return parseFiles(text,true);
    }
    private static List<Attachment> parseFiles(String text,boolean stored) {
        List<Attachment> files=new ArrayList<>();
        if (text==null) return files;
        String[] rows=text.split("\u0007",101);
        for (int i=0;i<Math.min(rows.length,100);i++) try {
            String[] f=rows[i].split(":",-1);
            if (f.length<5 || !safeName(f[1],stored)) continue;
            int id=Integer.parseInt(f[0]); long size=Long.parseLong(f[2],16), time=Long.parseLong(f[3],16), attr=Long.parseLong(f[4],16);
            if (id<0 || size<0 || (attr & 255)!=1) continue;
            boolean duplicate=false; for (Attachment item:files) if (item.id==id) duplicate=true;
            if (!duplicate) files.add(new Attachment(id,f[1],size,time));
        } catch (IllegalArgumentException ignored) { /* 单个坏附件不影响其余合法附件。 */ }
        return files;
    }
}
