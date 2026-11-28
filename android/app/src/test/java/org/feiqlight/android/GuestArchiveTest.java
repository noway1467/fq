package org.feiqlight.android;

import android.content.*;
import android.content.pm.ProviderInfo;
import android.net.Uri;
import android.provider.DocumentsContract;
import org.feiqlight.android.core.*;
import org.json.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.*;
import java.lang.reflect.*;
import java.net.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.zip.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35)
public final class GuestArchiveTest {
    private static final class Reply {
        final int code;final String headers;final byte[] body;
        Reply(int code,String headers,byte[] body){this.code=code;this.headers=headers;this.body=body;}
        JSONObject json()throws JSONException{return new JSONObject(new String(body,StandardCharsets.UTF_8));}
    }
    private static Reply request(GuestTransfer server,String path,String token,String extra)throws Exception {
        return request(server,path,token,extra,10000);
    }
    private static Reply request(GuestTransfer server,String path,String token,String extra,int timeout)throws Exception {
        try(Socket socket=new Socket(java.net.Proxy.NO_PROXY)) {
            socket.connect(new InetSocketAddress("127.0.0.1",server.port()),3000);socket.setSoTimeout(timeout);
            String header="GET "+path+" HTTP/1.1\r\nHost: 127.0.0.1:"+server.port()+"\r\nConnection: close\r\nContent-Length: 0\r\n"+(token==null?"":"X-FQ-Guest: "+token+"\r\n")+extra+"\r\n";
            socket.getOutputStream().write(header.getBytes(StandardCharsets.US_ASCII));byte[] bytes=socket.getInputStream().readAllBytes();
            int split=-1;for(int i=0;i+3<bytes.length;i++)if(bytes[i]==13&&bytes[i+1]==10&&bytes[i+2]==13&&bytes[i+3]==10){split=i;break;}
            assertTrue("缺少 HTTP 响应头",split>=0);String headers=new String(bytes,0,split,StandardCharsets.US_ASCII);
            return new Reply(Integer.parseInt(headers.split(" ")[1]),headers,Arrays.copyOfRange(bytes,split+4,bytes.length));
        }
    }
    private static Reply request(GuestTransfer server,String path)throws Exception{return request(server,path,server.token,"");}
    private static final class MemorySource implements LanNode.Source {
        final String name,key=UUID.randomUUID().toString();final byte[] bytes;final long length;
        volatile long modified=1;final AtomicInteger passes=new AtomicInteger(),opens=new AtomicInteger();
        java.util.function.IntConsumer reading=pass->{};
        MemorySource(String name,byte[] bytes){this(name,bytes,bytes.length);}
        MemorySource(String name,byte[] bytes,long length){this.name=name;this.bytes=bytes;this.length=length;}
        public String key(){return key;}public String name(){return name;}public long size(){return length;}public long modified(){return modified;}
        public InputStream open(long offset) {
            opens.incrementAndGet();
            return new ByteArrayInputStream(bytes,(int)offset,bytes.length-(int)offset) {
                boolean started;
                @Override public synchronized int read(byte[] data,int offset,int count){if(!started){started=true;reading.accept(passes.incrementAndGet());}return super.read(data,offset,count);}
            };
        }
    }
    private static long number(byte[] bytes,int offset){return Integer.toUnsignedLong(ByteBuffer.wrap(bytes,offset,4).order(ByteOrder.LITTLE_ENDIAN).getInt());}
    private static boolean eocd(byte[] bytes){return bytes.length>=22&&number(bytes,bytes.length-22)==0x06054b50;}
    private static Map<String,byte[]> decoded(byte[] bytes)throws Exception {
        assertTrue(eocd(bytes));Map<String,byte[]> files=new LinkedHashMap<>();
        try(ZipInputStream input=new ZipInputStream(new ByteArrayInputStream(bytes))) {
            ZipEntry entry;while((entry=input.getNextEntry())!=null){assertEquals(ZipEntry.STORED,entry.getMethod());byte[] content=input.readAllBytes();CRC32 crc=new CRC32();crc.update(content);assertEquals(crc.getValue(),entry.getCrc());assertNull(files.put(entry.getName(),content));}
        }
        return files;
    }
    private static void same(byte[] expected,byte[] actual)throws Exception {assertArrayEquals(MessageDigest.getInstance("SHA-256").digest(expected),MessageDigest.getInstance("SHA-256").digest(actual));}
    @Test public void nativeArchiveApiUsesStableDirectoryIdsAndFrozenAuthorizedList()throws Exception {
        Context context=RuntimeEnvironment.getApplication();byte[] content="目录下载中文 English +&%#😀".getBytes(StandardCharsets.UTF_8);
        MemorySource first=new MemorySource("特殊 +&%#😀.txt",content),empty=new MemorySource("空文件.txt",new byte[0]);
        List<LanNode.Source> selected=new ArrayList<>(Arrays.asList(first,empty));Map<String,String> relatives=new HashMap<>();relatives.put(first.key(),"资料 中文/子目录/"+first.name());relatives.put(empty.key(),"资料 中文/空文件.txt");
        try(GuestTransfer server=new GuestTransfer(context,selected,relatives,false,true,null)) {
            JSONObject state=request(server,"/api/guest").json();JSONArray directories=state.getJSONArray("directories");assertEquals(2,directories.length());
            assertEquals(directories.toString(),request(server,"/api/guest").json().getJSONArray("directories").toString());
            JSONObject capability=state.getJSONObject("archive");assertTrue(capability.getBoolean("enabled"));assertEquals(1000,capability.getInt("maxEntries"));assertEquals(StoreZipWriter.MAX_BYTES,capability.getLong("maxBytes"));
            selected.add(new MemorySource("会话后新增.txt",new byte[]{9}));
            Reply all=request(server,"/archive?scope=all");assertEquals(200,all.code);assertTrue(all.headers.contains("Content-Type: application/zip"));assertTrue(all.headers.contains("Content-Length: "+all.body.length));
            Map<String,byte[]> files=decoded(all.body);assertEquals(2,files.size());same(content,files.get("资料 中文/子目录/特殊 +&%#😀.txt"));assertEquals(0,files.get("资料 中文/空文件.txt").length);
            String nested=directories.getJSONObject(1).getString("id");Reply folder=request(server,"/archive?dir="+nested+"&token="+server.token,null,"");assertEquals(200,folder.code);Map<String,byte[]> nestedFiles=decoded(folder.body);assertEquals(1,nestedFiles.size());same(content,nestedFiles.get("子目录/特殊 +&%#😀.txt"));
            assertEquals(404,request(server,"/archive?dir=..%2Foutside").code);assertEquals(404,request(server,"/archive?dir=%E8%B5%84%E6%96%99").code);
            assertEquals(400,request(server,"/archive?scope=all&dir="+nested).code);assertEquals(400,request(server,"/archive?scope=unknown").code);
            assertEquals(403,request(server,"/archive?scope=all",null,"").code);assertEquals(403,request(server,"/archive?scope=all",server.token,"Origin: http://evil.invalid\r\n").code);
            assertEquals(416,request(server,"/archive?scope=all",server.token,"Range: bytes=0-5\r\n").code);
            String id=state.getJSONArray("files").getJSONObject(0).getString("id");Reply range=request(server,"/file?id="+id,server.token,"Range: bytes=2-7\r\n");assertEquals(206,range.code);assertArrayEquals(Arrays.copyOfRange(content,2,8),range.body);
        }
    }
    @Test public void emptyAndZip32SentinelsReturn409JsonBeforeZipHeadersAndOpeningSources()throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        try(GuestTransfer server=new GuestTransfer(context,Collections.emptyList(),true,true)) {
            Reply reply=request(server,"/archive?scope=all");assertEquals(409,reply.code);assertTrue(reply.json().getString("error").contains("没有"));assertFalse(reply.headers.contains("application/zip"));
        }
        for(long size:new long[]{StoreZipWriter.ZIP32_SENTINEL,StoreZipWriter.ZIP32_SENTINEL-31,StoreZipWriter.ZIP32_SENTINEL-100}) {
            MemorySource source=new MemorySource("a",new byte[0],size);
            try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
                Reply reply=request(server,"/archive?scope=all");assertEquals(409,reply.code);assertEquals(StoreZipWriter.LIMIT_MESSAGE,reply.json().getString("error"));assertFalse(reply.headers.contains("application/zip"));assertEquals(0,source.opens.get());
            }
        }
    }
    @Test public void longAuthorizedPathsKeepDirectoryMetadataBoundedAndIncludeDescendants()throws Exception {
        Context context=RuntimeEnvironment.getApplication();String nested=String.join("/",Collections.nCopies(30,"'".repeat(58)));
        List<LanNode.Source> sources=new ArrayList<>();Map<String,String> relatives=new HashMap<>();
        for(int index=0;index<1000;index++){MemorySource source=new MemorySource("a.txt",new byte[0]);sources.add(source);relatives.put(source.key(),String.format(Locale.ROOT,"r%04d/",index)+nested+"/a.txt");}
        try(GuestTransfer server=new GuestTransfer(context,sources,relatives,false,true,null)) {
            Reply reply=request(server,"/api/guest");assertEquals(200,reply.code);assertTrue(reply.body.length>2097152);JSONObject state=reply.json();assertEquals(1000,state.getJSONArray("files").length());assertEquals(2000,state.getJSONArray("directories").length());
        }
        MemorySource a=new MemorySource("a.txt",new byte[]{1}),b=new MemorySource("b.txt",new byte[]{2});Map<String,String> branch=new HashMap<>();branch.put(a.key(),"root/mid/a.txt");branch.put(b.key(),"root/mid/leaf/b.txt");
        for(boolean reverse:new boolean[]{false,true}) {
            List<LanNode.Source> order=reverse?Arrays.asList(b,a):Arrays.asList(a,b);
            try(GuestTransfer server=new GuestTransfer(context,order,branch,false,true,null)) {
                JSONArray directories=request(server,"/api/guest").json().getJSONArray("directories");JSONObject mid=null;
                for(int index=0;index<directories.length();index++)if(directories.getJSONObject(index).getString("relativePath").equals("root/mid"))mid=directories.getJSONObject(index);
                assertNotNull(mid);assertEquals(2,mid.getInt("count"));Map<String,byte[]> files=decoded(request(server,"/archive?dir="+mid.getString("id")).body);assertEquals(2,files.size());same(a.bytes,files.get("mid/a.txt"));same(b.bytes,files.get("mid/leaf/b.txt"));
            }
        }
    }
    @Test public void archiveAttachmentNamePercentEncodesApostropheAndParentheses()throws Exception {
        Context context=RuntimeEnvironment.getApplication();MemorySource source=new MemorySource("a.txt",new byte[]{1});String path="含'(+&%)目录/a.txt";
        try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),Collections.singletonMap(source.key(),path),false,true,null)) {
            String id=request(server,"/api/guest").json().getJSONArray("directories").getJSONObject(0).getString("id");Reply reply=request(server,"/archive?dir="+id);
            assertEquals(200,reply.code);assertTrue(reply.headers.contains("%27%28%2B%26%25%29"));assertTrue(eocd(reply.body));
        }
    }
    @Test public void secondArchiveIsLimitedWhileFirstUsesGlobalHttpConnectionSlot()throws Exception {
        Context context=RuntimeEnvironment.getApplication();MemorySource source=new MemorySource("a.bin",new byte[1024]);CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        source.reading=pass->{if(pass==2){entered.countDown();try{if(!release.await(10,TimeUnit.SECONDS))throw new IllegalStateException("测试未释放读取");}catch(InterruptedException error){Thread.currentThread().interrupt();throw new IllegalStateException(error);}}};
        ExecutorService worker=Executors.newSingleThreadExecutor();
        try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
            Future<Reply> first=worker.submit(()->request(server,"/archive?scope=all"));assertTrue(entered.await(5,TimeUnit.SECONDS));
            Reply second=request(server,"/archive?scope=all");assertEquals(409,second.code);assertTrue(second.json().getString("error").contains("正在进行"));
            Object http=field(server,"http");Set<?> clients=(Set<?>)field(http,"clients");synchronized(clients){assertTrue("归档未计入 HTTP 连接上限",clients.size()>=1);}
            release.countDown();Reply reply=first.get(5,TimeUnit.SECONDS);assertEquals(200,reply.code);assertEquals(1,decoded(reply.body).size());assertEquals(2,source.passes.get());
            assertEquals(200,request(server,"/archive?scope=all").code);
        }finally{release.countDown();worker.shutdownNow();assertTrue(worker.awaitTermination(5,TimeUnit.SECONDS));}
    }
    @Test public void liveSourceChangeExpiryAndStopTruncateHttpBodyWithoutEocd()throws Exception {
        Context context=RuntimeEnvironment.getApplication();
        for(String reason:new String[]{"changed","expired","stopped"}) {
            MemorySource source=new MemorySource("a.bin",new byte[524305]);
            try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
                source.reading=pass->{if(pass==2){
                    if(reason.equals("changed"))source.modified++;
                    else if(reason.equals("expired"))set(server,"deadline",System.nanoTime()-1);
                    else server.close();
                }};
                Reply reply=request(server,"/archive?scope=all");assertEquals(200,reply.code);assertTrue(reply.headers.contains("application/zip"));assertTrue(reason,reply.body.length<524305);assertFalse(reason,eocd(reply.body));assertFalse(new String(reply.body,StandardCharsets.UTF_8).contains("{\"error\""));
            }
        }
    }
    @Test public void closingClientDuringPreflightStopsBeforeSecondReadAndReleasesSlot()throws Exception {
        Context context=RuntimeEnvironment.getApplication();MemorySource source=new MemorySource("a.bin",new byte[524305]);CountDownLatch entered=new CountDownLatch(1),release=new CountDownLatch(1);
        source.reading=pass->{if(pass==1){entered.countDown();try{if(!release.await(5,TimeUnit.SECONDS))throw new IllegalStateException("测试未释放预检");}catch(InterruptedException error){Thread.currentThread().interrupt();throw new IllegalStateException(error);}}};
        try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
            try(Socket client=new Socket("127.0.0.1",server.port())) {
                String header="GET /archive?scope=all HTTP/1.1\r\nHost: 127.0.0.1:"+server.port()+"\r\nX-FQ-Guest: "+server.token+"\r\nConnection: close\r\n\r\n";client.getOutputStream().write(header.getBytes(StandardCharsets.US_ASCII));
                assertTrue(entered.await(5,TimeUnit.SECONDS));client.close();Thread.sleep(150);release.countDown();
                long deadline=System.nanoTime()+TimeUnit.SECONDS.toNanos(5);
                while(System.nanoTime()<deadline){synchronized(server){if(!(boolean)field(server,"archiveBusy"))break;}Thread.sleep(10);}
                synchronized(server){assertFalse((boolean)field(server,"archiveBusy"));}
                assertEquals("客户端断开后仍进行第二遍读取",1,source.passes.get());
            }
            assertEquals(200,request(server,"/archive?scope=all").code);
        }finally{release.countDown();}
    }
    @Test public void realCrcReadProgressKeepsLongPreflightAliveBeforeResponseHeaders()throws Exception {
        Context context=RuntimeEnvironment.getApplication();byte[] bytes=new byte[16*262144];AtomicInteger passes=new AtomicInteger();
        LanNode.Source source=new LanNode.Source() {
            public String key(){return "long-preflight";}public String name(){return "a.bin";}public long size(){return bytes.length;}public long modified(){return 1;}
            public InputStream open(long offset){return new ByteArrayInputStream(bytes,(int)offset,bytes.length-(int)offset){
                int pass;
                @Override public synchronized int read(byte[] data,int offset,int count){
                    if(pass==0)pass=passes.incrementAndGet();
                    if(pass==1&&available()>0)try{Thread.sleep(1050);}catch(InterruptedException error){Thread.currentThread().interrupt();throw new IllegalStateException(error);}
                    return super.read(data,offset,count);
                }
            };}
        };
        try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
            long started=System.nanoTime();Reply reply=request(server,"/archive?scope=all",server.token,"",25000);
            assertTrue("没有覆盖原 15 秒空闲超时边界",System.nanoTime()-started>TimeUnit.SECONDS.toNanos(15));
            assertEquals(200,reply.code);assertTrue(eocd(reply.body));same(bytes,decoded(reply.body).get("a.bin"));assertEquals(2,passes.get());
        }
    }
    private static Object field(Object object,String name)throws Exception {Field field=object.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(object);}
    private static void set(Object object,String name,long value) {
        try{Field field=object.getClass().getDeclaredField(name);field.setAccessible(true);field.setLong(object,value);}catch(ReflectiveOperationException error){throw new IllegalStateException(error);}
    }
    private static final class HookOutput extends ByteArrayOutputStream {
        java.util.function.Consumer<byte[]> written=bytes->{};
        @Override public synchronized void write(byte[] bytes,int offset,int count){super.write(bytes,offset,count);written.accept(Arrays.copyOfRange(bytes,offset,offset+count));}
    }
    private static void invokeArchive(GuestTransfer server,OutputStream output)throws Exception {
        Constructor<ToolsHttp.Response> constructor=ToolsHttp.Response.class.getDeclaredConstructor(OutputStream.class,boolean.class,AtomicLong.class,boolean.class);constructor.setAccessible(true);
        ToolsHttp.Response response=constructor.newInstance(output,false,new AtomicLong(System.nanoTime()),false);
        ToolsHttp.Request request=new ToolsHttp.Request();request.method="GET";request.path="/archive";request.query="scope=all";request.body=new byte[0];request.headers.put("X-FQ-Guest",server.token);
        Method handle=GuestTransfer.class.getDeclaredMethod("handle",ToolsHttp.Request.class,ToolsHttp.Response.class);handle.setAccessible(true);
        try{handle.invoke(server,request,response);}catch(InvocationTargetException error){if(error.getCause() instanceof Exception)throw (Exception)error.getCause();throw error;}
    }
    @Test public void documentSourceRechecksSafGrantAndModificationAfterHeaders()throws Exception {
        Context context=RuntimeEnvironment.getApplication();SecureFeaturesTest.Documents documents=new SecureFeaturesTest.Documents();ProviderInfo info=new ProviderInfo();info.authority="guest.zip.documents";info.exported=true;info.grantUriPermissions=true;info.readPermission=info.writePermission="android.permission.MANAGE_DOCUMENTS";documents.attachInfo(context,info);ShadowContentResolver.registerProviderInternal(info.authority,documents);
        android.content.pm.ResolveInfo resolved=new android.content.pm.ResolveInfo();resolved.providerInfo=info;Shadows.shadowOf(context.getPackageManager()).addResolveInfoForIntent(new Intent(DocumentsContract.PROVIDER_INTERFACE),resolved);
        documents.file=new File(context.getFilesDir(),"archive-document.bin");Files.write(documents.file.toPath(),new byte[524305]);
        Uri uri=DocumentsContract.buildDocumentUri(info.authority,"file");
        for(String reason:new String[]{"permission","modified"}) {
            context.getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);DocumentSource source=new DocumentSource(context,uri);
            try(GuestTransfer server=new GuestTransfer(context,Collections.singletonList(source),false,true)) {
                HookOutput output=new HookOutput();AtomicBoolean changed=new AtomicBoolean();
                output.written=bytes->{if(bytes.length>=4&&number(bytes,0)==0x04034b50&&changed.compareAndSet(false,true)){
                    if(reason.equals("permission"))context.getContentResolver().releasePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);else documents.modified++;
                }};
                try{invokeArchive(server,output);fail("SAF 变化后仍完成归档");}catch(IOException expected){assertNotNull(expected.getMessage());}
                assertTrue(changed.get());assertTrue(new String(output.toByteArray(),0,8,StandardCharsets.US_ASCII).startsWith("HTTP/1.1"));assertFalse(eocd(output.toByteArray()));
            }
        }
        context.getContentResolver().releasePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);
    }
}
