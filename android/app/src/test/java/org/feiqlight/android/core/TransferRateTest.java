package org.feiqlight.android.core;

import org.junit.Test;
import static org.junit.Assert.*;
import java.io.*;
import java.net.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;

public final class TransferRateTest {
    @Test public void resumedRateUsesRecentBytesAndMonotonicIntervals() {
        long offset=5L*1024*1024*1024;TransferRate rate=new TransferRate(offset);
        assertFalse(rate.update(offset+1000,100));assertTrue(rate.update(offset+262144,250));assertEquals(1048576,rate.bytesPerSecond(),0);
        assertTrue(rate.update(offset+393216,500));assertEquals(524288,rate.bytesPerSecond(),0);
        assertTrue(rate.update(offset+393216,1000));assertEquals(0,rate.bytesPerSecond(),0);
    }
    @Test public void slowResumedSocketReportsRateEvenWithinSamePercent() throws Exception {
        Path directory=Files.createTempDirectory("rate-socket");ExecutorService worker=Executors.newSingleThreadExecutor();
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}public void error(String text){}};
        try(ServerSocket server=new ServerSocket(0,1,InetAddress.getLoopbackAddress());LanNode receiver=new LanNode(32429,true,"rate","host","rate","",quiet)) {
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress(InetAddress.getLoopbackAddress(),server.getLocalPort()),"remote","host","remote","");
            int total=8*1024*1024,offset=total-65536;Protocol.Attachment file=new Protocol.Attachment(0,"rate.bin",total,0);File target=directory.resolve("received.bin").toFile();File partial=LanNode.partialFile(peer,1,file,target);
            try(RandomAccessFile existing=new RandomAccessFile(partial,"rw")){existing.setLength(offset);}
            Future<?> sent=worker.submit(()->{try(Socket socket=server.accept()) {ByteArrayOutputStream request=new ByteArrayOutputStream();int b;while((b=socket.getInputStream().read())>0)request.write(b);String[] fields=Protocol.parse(request.toByteArray()).body.split(":");assertEquals(offset,Long.parseLong(fields[2],16));byte[] chunk=new byte[4096];Arrays.fill(chunk,(byte)7);for(int i=0;i<16;i++){socket.getOutputStream().write(chunk);Thread.sleep(40);}}catch(Exception error){throw new RuntimeException(error);}});
            List<Integer> percents=new ArrayList<>();List<Double> rates=new ArrayList<>();
            receiver.receiveFile(peer,1,file,target,new LanNode.Transfer(),new LanNode.Progress(){public void update(int p){update(p,0);}public void update(int p,double rate){percents.add(p);rates.add(rate);}});sent.get(5,TimeUnit.SECONDS);
            assertTrue("百分比未变化时缺少速率刷新",Collections.frequency(percents,99)>=3);assertTrue(rates.stream().anyMatch(rate->rate>0&&rate<1048576));assertEquals(total,target.length());assertFalse(partial.exists());
            try(RandomAccessFile output=new RandomAccessFile(target,"r")){output.seek(offset);for(int i=0;i<65536;i++)assertEquals(7,output.read());}
        } finally {worker.shutdownNow();assertTrue(worker.awaitTermination(5,TimeUnit.SECONDS));try(java.util.stream.Stream<Path> files=Files.list(directory)){for(Path path:(Iterable<Path>)files::iterator)Files.delete(path);}Files.delete(directory);}
    }
}
