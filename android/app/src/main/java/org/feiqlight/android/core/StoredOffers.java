package org.feiqlight.android.core;

import java.io.*;
import java.nio.file.Files;
import java.util.*;

final class StoredOffers {
    private StoredOffers() { }
    private static final Object storedOffersGate=new Object();
    static final int OFFER_STATES_MARKER=0x46515331;
    static final class StoredOfferState {
        final String state,messageState;
        final int percent;
        StoredOfferState(String state,int percent,String messageState){this.state=state;this.percent=percent;this.messageState=messageState;}
    }
    private static final class StoredOfferSpan {
        final int start,length;
        final long packet,file;
        final String peer,sha256,deviceId,fingerprint;
        StoredOfferSpan(int start,int length,long packet,long file,String peer,String sha256,String deviceId,String fingerprint){this.start=start;this.length=length;this.packet=packet;this.file=file;this.peer=peer;this.sha256=sha256;this.deviceId=deviceId;this.fingerprint=fingerprint;}
    }

    static final int COMPLETION_JOURNAL_LIMIT=256*1024;
    static final class Completion {
        final String sha256,deviceId,fingerprint;
        final long expires;
        Completion(String sha256,long expires,String deviceId,String fingerprint){this.sha256=sha256;this.expires=expires;this.deviceId=deviceId;this.fingerprint=fingerprint;}
    }
    private static File completionFile(File root){return new File(root,"transfer-offer-completions.log");}
    static void appendCompletion(File root,long packet,int file,String sha256,long expires,String deviceId,String fingerprint)throws IOException {
        synchronized(storedOffersGate) {
            File journal=completionFile(root);
            byte[] entry=(packet+":"+file+":"+expires+":"+sha256+":"+deviceId+":"+fingerprint+"\n").getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            if(journal.length()+entry.length>COMPLETION_JOURNAL_LIMIT)throw new IOException("文件完成确认日志已达上限，请先保存任务快照");
            // STORED 必须先同步写入小日志，合并快照期间崩溃也不会把已完成文件重新计入待处理额度。
            try(RandomAccessFile output=new RandomAccessFile(journal,"rw")) {
                long end=output.length();
                if(end>0) {
                    output.seek(end-1);
                    if(output.read()!='\n') {
                        while(end>0){output.seek(--end);if(output.read()=='\n'){end++;break;}}
                        output.setLength(end);
                    }
                }
                output.seek(output.length());output.write(entry);output.getFD().sync();
            }
        }
    }
    static Map<String,Completion> readCompletions(File root)throws IOException {
        synchronized(storedOffersGate) {
            File journal=completionFile(root);Map<String,Completion> completed=new HashMap<>();
            if(!journal.isFile()||journal.length()==0)return completed;
            if(journal.length()>COMPLETION_JOURNAL_LIMIT)throw new IOException("文件完成确认日志过大");
            byte[] bytes=Files.readAllBytes(journal.toPath());int complete=bytes.length;
            while(complete>0&&bytes[complete-1]!='\n')complete--;
            String text=new String(bytes,0,complete,java.nio.charset.StandardCharsets.US_ASCII);
            String[] lines=text.split("\n",-1);
            for(int index=0;index<lines.length-1;index++) {
                String line=lines[index];
                String[] fields=line.split(":",-1);
                if(fields.length!=6||!fields[0].matches("[0-9]+")||!fields[1].matches("[0-9]+")||!fields[2].matches("[0-9]+")||!SecureIdentity.validFingerprint(fields[3])||
                    Protocol.normalizeDeviceId(fields[4])==null||!SecureIdentity.validFingerprint(fields[5]))throw new IOException("文件完成确认日志损坏");
                try {
                    long packet=Long.parseLong(fields[0]),expires=Long.parseLong(fields[2]);int file=Integer.parseInt(fields[1]);
                    completed.put(packet+":"+file,new Completion(fields[3],expires,Protocol.normalizeDeviceId(fields[4]),fields[5]));
                }catch(NumberFormatException error){throw new IOException("文件完成确认日志编号无效",error);}
            }
            return completed;
        }
    }

    static final int COMPLETED_OFFER_CACHE=1000,MAX_STORED_OFFERS=LanNode.MAX_FILE_LIMIT+COMPLETED_OFFER_CACHE+8;
    static boolean isCompletedState(String state){return "已发送（非保存确认）".equals(state)||"对方已校验保存".equals(state);}

    static byte[] read(File saved) throws IOException {
        synchronized (storedOffersGate) {
            if (!saved.isFile()) return null;
            if (saved.length() > 64*1024*1024) throw new IOException("文件任务记录过大");
            return Files.readAllBytes(saved.toPath());
        }
    }

    static void write(File saved, byte[] contents) throws IOException {
        synchronized (storedOffersGate) {
            SecureIdentity.atomic(saved, contents);
            File journal=completionFile(saved.getParentFile());
            // 快照原子提交后才清日志；在这两步之间崩溃，恢复时重放相同记录仍是安全的。
            if(journal.isFile()&&journal.length()>0)try(FileOutputStream output=new FileOutputStream(journal)){output.getFD().sync();}
        }
    }

    static Map<String,StoredOfferState> readOfferStates(DataInputStream input)throws IOException {
        Map<String,StoredOfferState> states=new HashMap<>();
        if(input.available()==0)return states;
        if(input.readInt()!=OFFER_STATES_MARKER)throw new IOException("文件任务状态格式无效");
        int count=input.readInt();if(count<0||count>MAX_STORED_OFFERS)throw new IOException("文件任务状态数量无效");
        for(int index=0;index<count;index++){
            long packet=input.readLong();
            int file=input.readInt();
            String state=input.readUTF();
            int percent=input.readInt();
            String messageState=input.readUTF();
            if(percent<0||percent>100)throw new IOException("文件任务进度无效");
            states.put(packet+":"+file,new StoredOfferState(state,percent,messageState));
        }
        if(input.read()!=-1)throw new IOException("文件任务状态含有多余数据");
        return states;
    }
    static void cancel(File root,String peerId,Collection<Long> packets)throws IOException {
        if(packets.isEmpty())return;
        File saved=new File(root,"transfer-offers.dat");Set<Long> selectedPackets=new HashSet<>(packets);
        synchronized(storedOffersGate){
            if(!saved.isFile())return;if(saved.length()>64*1024*1024)throw new IOException("文件任务记录过大");
            byte[] bytes=Files.readAllBytes(saved.toPath());List<StoredOfferSpan> spans=new ArrayList<>();int format,count,metadataStart;Map<String,StoredOfferState> states;
            Map<String,Completion> completed=readCompletions(root);
            try(DataInputStream input=new DataInputStream(new ByteArrayInputStream(bytes))){
                format=input.readInt();if(format!=1&&format!=2)throw new IOException("文件任务版本不支持");count=input.readInt();if(count<0||count>MAX_STORED_OFFERS)throw new IOException("文件任务数量无效");
                for(int index=0;index<count;index++){
                    int start=bytes.length-input.available();long packet=input.readLong();int file=input.readInt();input.readUTF();input.readLong();input.readLong();input.readLong();
                    String deviceId=input.readUTF(),fingerprint=input.readUTF(),sha256=input.readUTF();
                    for(int field=0;field<5;field++)input.readUTF();String peer=input.readUTF();
                    spans.add(new StoredOfferSpan(start,bytes.length-input.available()-start,packet,file,peer,sha256,deviceId,fingerprint));
                }
                metadataStart=bytes.length-input.available();states=readOfferStates(input);
            }
            spans.removeIf(span->{
                StoredOfferState state=states.get(span.packet+":"+span.file);
                Completion completion=completed.get(span.packet+":"+span.file);
                return span.peer.equals(peerId)&&selectedPackets.contains(span.packet)&&(completion==null||!span.sha256.equals(completion.sha256)||
                    !span.deviceId.equals(completion.deviceId)||!span.fingerprint.equals(completion.fingerprint))
                    &&(state==null||!isCompletedState(state.state)&&!"已送达".equals(state.messageState));
            });
            if(spans.size()==count)return;
            ByteArrayOutputStream buffer=new ByteArrayOutputStream();
            try(DataOutputStream output=new DataOutputStream(buffer)){
                output.writeInt(format);output.writeInt(spans.size());for(StoredOfferSpan span:spans)output.write(bytes,span.start,span.length);
                if(metadataStart<bytes.length){
                    output.writeInt(OFFER_STATES_MARKER);output.writeInt((int)spans.stream().filter(span->states.containsKey(span.packet+":"+span.file)).count());
                    for(StoredOfferSpan span:spans){
                        StoredOfferState state=states.get(span.packet+":"+span.file);if(state==null)continue;
                        output.writeLong(span.packet);output.writeInt((int)span.file);output.writeUTF(state.state);output.writeInt(state.percent);output.writeUTF(state.messageState);
                    }
                }
            }
            SecureIdentity.atomic(saved,buffer.toByteArray());
        }
    }
}
