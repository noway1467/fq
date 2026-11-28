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
        final String peer;
        StoredOfferSpan(int start,int length,long packet,long file,String peer){this.start=start;this.length=length;this.packet=packet;this.file=file;this.peer=peer;}
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
        synchronized (storedOffersGate) { SecureIdentity.atomic(saved, contents); }
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
            try(DataInputStream input=new DataInputStream(new ByteArrayInputStream(bytes))){
                format=input.readInt();if(format!=1&&format!=2)throw new IOException("文件任务版本不支持");count=input.readInt();if(count<0||count>MAX_STORED_OFFERS)throw new IOException("文件任务数量无效");
                for(int index=0;index<count;index++){
                    int start=bytes.length-input.available();long packet=input.readLong();int file=input.readInt();input.readUTF();input.readLong();input.readLong();input.readLong();
                    for(int field=0;field<8;field++)input.readUTF();String peer=input.readUTF();
                    spans.add(new StoredOfferSpan(start,bytes.length-input.available()-start,packet,file,peer));
                }
                metadataStart=bytes.length-input.available();states=readOfferStates(input);
            }
            spans.removeIf(span->{
                StoredOfferState state=states.get(span.packet+":"+span.file);
                return span.peer.equals(peerId)&&selectedPackets.contains(span.packet)&&(state==null||!isCompletedState(state.state)&&!"已送达".equals(state.messageState));
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
