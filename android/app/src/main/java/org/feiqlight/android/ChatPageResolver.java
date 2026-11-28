package org.feiqlight.android;

import android.net.Uri;
import java.io.File;
import java.util.*;
import org.feiqlight.android.ChatService.ChatPage;
import org.feiqlight.android.ChatService.FileProgress;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;

final class ChatPageResolver {
    private final ChatService service;

    ChatPageResolver(ChatService service) { this.service=service; }

    private final Map<String,int[]> mediaSizes=new LinkedHashMap<String,int[]>(64,.75f,true) {
        @Override protected boolean removeEldestEntry(Map.Entry<String,int[]> entry){return size()>512;}
    };
    private int[] mediaSize(Uri uri) {
        String key=uri.toString();
        synchronized(mediaSizes){int[] cached=mediaSizes.get(key);if(cached!=null)return cached;}
        int[] size=MediaPreview.bounds(service,uri);
        if(size!=null)synchronized(mediaSizes){mediaSizes.put(key,size);}
        return size;
    }
    ChatPage resolve(ChatStore store,ReceivedStorage storage,String peer,List<LanNode.Message> rows,Map<String,FileProgress> progress,java.util.function.BooleanSupplier current) {
        ChatPage page=new ChatPage(rows,progress);
        for(LanNode.Message message:rows)for(int index=0;index<message.files.size();index++) {
            if(!current.getAsBoolean())return page;
            Protocol.Attachment file=message.files.get(index);Uri uri;
            String key=ChatService.fileKey(peer,message.number,file.id,message.outgoing);
            // 仅补当前页被内存缓存淘汰的状态，数据库读取始终留在后台。
            if(!progress.containsKey(key)) {
                ChatStore.TransferState saved=store.transfer(key);
                if(saved!=null)progress.put(key,new FileProgress(saved.percent,saved.state,null));
            }
            if(message.outgoing)uri=storage.outgoingUri(message,index);
            else {
                File local=service.receivedFile(peer,message.number,file);
                uri=local.isFile()?ReceivedFileProvider.uri(service,local,file.name):storage.existingSaved(local);
                if(uri==null&&storage.saved(local)!=null)page.unavailable.add(key);
            }
            page.files.put(key,uri);
            // 尺寸随后台快照准备好，绑定和解码完成时均不读 SAF，也不改变气泡高度。
            if(uri!=null&&MediaPreview.supported(file.name)&&!MediaPreview.video(file.name)) {
                int[] size=mediaSize(uri);if(size!=null)page.media.put(key,size);
            }
        }
        return page;
    }
}
