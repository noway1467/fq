package org.feiqlight.android.core;

import org.junit.Test;
import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;

public final class PauseTransferTest {
    @Test public void pauseKeepsBytesAndResumesAtWrittenOffset()throws Exception {
        byte[] content=new byte[4*1024*1024];new Random(41).nextBytes(content);
        File directory=Files.createTempDirectory("fq-pause-").toFile(),target=new File(directory,"pause.bin");long[] resumed={-1};
        try(ServerSocket listener=new ServerSocket(0,2,InetAddress.getLoopbackAddress())) {
            TlsFilePeer sender=new TlsFilePeer(listener);Protocol.Attachment file=new Protocol.Attachment(0,"pause.bin",content.length,1);file.sha256=SecureIdentity.hash(content);
            CompletableFuture<Void> sending=CompletableFuture.runAsync(()->{
                try{for(int attempt=0;attempt<2;attempt++)try(Socket client=listener.accept();Socket tls=sender.open(client,file)) {
                    long offset=sender.offset;if(attempt==1)resumed[0]=offset;
                    try {for(long pos=offset;pos<content.length;pos+=32768){tls.getOutputStream().write(content,(int)pos,(int)Math.min(32768,content.length-pos));Thread.sleep(5);}SecureIdentity.readText(tls.getInputStream(),128);}catch(IOException ignored){}
                }}catch(Exception e){throw new CompletionException(e);}
            });
            LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String value){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};
            try(LanNode receiver=new LanNode(listener.getLocalPort()==65535?65534:listener.getLocalPort()+1,true,"receiver","host","接收","",quiet)) {
                sender.authorize(receiver);LanNode.Peer peer=sender.peer;LanNode.Transfer transfer=new LanNode.Transfer();
                try {receiver.receiveFile(peer,1,file,target,transfer,percent->{if(percent>=10)transfer.pause();});fail("暂停后仍完成下载");}catch(IOException expected){}
                File partial=LanNode.partialFile(peer,1,file,target);long length=partial.length();assertTrue(length>0&&length<content.length);
                receiver.receiveFile(peer,1,file,target,new LanNode.Transfer(),percent->{});assertEquals(length,resumed[0]);assertArrayEquals(content,Files.readAllBytes(target.toPath()));assertFalse(partial.exists());
            }
            sending.get(10,TimeUnit.SECONDS);
        }finally{Files.deleteIfExists(target.toPath());Files.deleteIfExists(directory.toPath());}
    }
}
