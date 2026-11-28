package org.feiqlight.android.core;

import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipInputStream;
import static org.junit.Assert.*;

/** 受控慢来源通过真实认证 TLS，覆盖回环入口 15 秒与上游 45 秒超时。 */
public final class ArchiveGatewayProgressTest {
    @Rule public final TemporaryFolder temporary=new TemporaryFolder(new File(System.getProperty("java.io.tmpdir")));
    private static final class SlowInput extends ByteArrayInputStream {
        SlowInput(byte[] bytes){super(bytes);}
        @Override public synchronized int read(byte[] bytes,int offset,int length){
            try{Thread.sleep(1000);}catch(InterruptedException error){Thread.currentThread().interrupt();throw new IllegalStateException(error);}
            return super.read(bytes,offset,Math.min(length,1024));
        }
    }
    private void run(int seconds)throws Exception{
        File root=temporary.newFolder();SecureIdentity serverIdentity=new SecureIdentity(new File(root,"server")),clientIdentity=new SecureIdentity(new File(root,"client"));
        String serverId="a".repeat(32),clientId="b".repeat(32);
        serverIdentity.confirmation=clientIdentity.confirmation=(name,code)->true;
        assertTrue(serverIdentity.ensureTrusted(clientId,clientIdentity.fingerprint,"client"));assertTrue(clientIdentity.ensureTrusted(serverId,serverIdentity.fingerprint,"server"));
        byte[] content=new byte[Math.max(seconds,20)*1024];for(int index=0;index<content.length;index++)content[index]=(byte)index;
        CountDownLatch entered=new CountDownLatch(1),ended=new CountDownLatch(1);AtomicBoolean completed=new AtomicBoolean();
        try(ToolsHttp server=new ToolsHttp(0,true,(request,response)->{
            try{
                if(request.header("X-FQ-Archive-Progress").equals("1"))response.enableArchiveProgress();
                AtomicBoolean preparing=new AtomicBoolean(true);
                StoreZipWriter writer=new StoreZipWriter(Collections.singletonList(new StoreZipWriter.Entry("目录/data.bin",content.length,System.currentTimeMillis(),()->preparing.get()?new SlowInput(content):new ByteArrayInputStream(content),input->{})));
                writer.prepare(()->{response.processing();entered.countDown();});preparing.set(false);
                response.begin(200,"application/zip",writer.contentLength(),"");writer.write(response::write,response::checkConnection);completed.set(true);
            }finally{ended.countDown();}
        },serverIdentity)){
            LanNode.Peer peer=new LanNode.Peer(new InetSocketAddress("127.0.0.1",server.port()),"server","server","server","");peer.deviceId=serverId;peer.fingerprint=serverIdentity.fingerprint;
            try(ToolsGateway gateway=new ToolsGateway(clientIdentity,peer,server.port(),null);Socket browser=new Socket(Proxy.NO_PROXY)){
                URI url=URI.create(gateway.url());browser.connect(new InetSocketAddress("127.0.0.1",url.getPort()));browser.setSoTimeout(90000);
                String request="GET /file?id=controlled&archive=store HTTP/1.1\r\nHost: 127.0.0.1:"+url.getPort()+"\r\nX-FQ-Gateway: "+url.getQuery().substring(7)+"\r\nConnection: close\r\n\r\n";
                browser.getOutputStream().write(request.getBytes(StandardCharsets.US_ASCII));
                if(seconds==0){assertTrue(entered.await(3,TimeUnit.SECONDS));long started=System.nanoTime();browser.close();assertTrue(ended.await(2500,TimeUnit.MILLISECONDS));assertFalse(completed.get());System.out.println("PASS 预检取消 "+TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started)+"ms");}
                else{
                    byte[] all=browser.getInputStream().readAllBytes();String text=new String(all,StandardCharsets.ISO_8859_1);int start=text.indexOf("\r\n\r\n")+4;assertTrue(text.startsWith("HTTP/1.1 200 "));assertTrue(start>=4);
                    try(ZipInputStream zip=new ZipInputStream(new ByteArrayInputStream(all,start,all.length-start))){assertNotNull(zip.getNextEntry());assertArrayEquals(content,zip.readAllBytes());assertNull(zip.getNextEntry());}
                    assertTrue(completed.get());System.out.println("PASS 持续进展 "+seconds+"s 预检经网关下载解压一致");
                }
            }
        }
    }
    @Test public void progressSurvivesTwentySeconds()throws Exception{run(20);}
    @Test public void progressSurvivesSixtySeconds()throws Exception{run(60);}
    @Test public void browserCancelStopsPreflight()throws Exception{run(0);}
}
