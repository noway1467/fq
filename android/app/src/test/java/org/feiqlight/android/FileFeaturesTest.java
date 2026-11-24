package org.feiqlight.android;

import android.app.*;
import android.content.*;
import android.content.pm.ProviderInfo;
import android.database.*;
import android.net.Uri;
import android.os.*;
import android.provider.*;
import android.view.*;
import android.widget.*;
import org.feiqlight.android.core.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.android.controller.*;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.*;
import java.net.*;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import java.util.function.BooleanSupplier;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w393dp-h852dp-xhdpi")
public final class FileFeaturesTest {
    @Test public void onlineConversationsMoveAboveRecentOfflineAndPinsSurviveOffline() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        Method publish=ChatService.class.getDeclaredMethod("requestPublish");publish.setAccessible(true);
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){try{publish.invoke(service);}catch(Exception e){throw new RuntimeException(e);}}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
        int a=port(),b=port();while(a==b)b=port();
        try(LanNode sender=new LanNode(a,true,"sort-local","host","本机","test",listener);LanNode remote=new LanNode(b,true,"sort-online","host","较早在线","test",quiet)) {
            settle(service);set(service,"node",sender);sender.start();
            LanNode.Peer pinned=new LanNode.Peer(LanNode.endpoint("127.0.0.1:33421"),"sort-pin","host","置顶离线",""),recent=new LanNode.Peer(LanNode.endpoint("127.0.0.1:33422"),"sort-recent","host","最近离线",""),old=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+b),"sort-online","host","较早在线","");
            LanNode.Peer[] peers={pinned,recent,old};long[] times={100,500,200};
            for(int i=0;i<peers.length;i++){LanNode.Message m=new LanNode.Message();m.number=i+1;m.time=times[i];m.text="排序历史";m.state="已送达";m.outgoing=true;service.incoming(peers[i],m,false);}
            service.editConversation(pinned.id(),"",true);settle(service);new DraftStore(service).save(recent.id(),"保留草稿");service.active(recent.id());
            remote.start();sender.probe(old.endpoint);waitFor(service,()->service.conversations().size()==3&&service.conversations().get(1).peer.online);
            assertEquals(Arrays.asList(pinned.id(),old.id(),recent.id()),service.conversations().stream().map(c->c.peer.id()).collect(java.util.stream.Collectors.toList()));
            service.editConversation(pinned.id(),"",false);waitFor(service,()->!service.conversations().get(0).pinned);assertEquals(old.id(),service.conversations().get(0).peer.id());
            service.editConversation(old.id(),"",true);waitFor(service,()->service.conversations().get(0).pinned);remote.close();waitFor(service,()->!service.conversations().get(0).peer.online);
            assertEquals(old.id(),service.conversations().get(0).peer.id());assertTrue(service.conversations().get(0).pinned);assertEquals(200,service.conversations().get(0).time);
            assertEquals(recent.id(),field(service,"activePeer"));assertEquals("保留草稿",new DraftStore(service).read(recent.id()));
            ChatStore store=(ChatStore)field(service,"store");assertTrue(store.conversations().stream().anyMatch(c->c.peer.id().equals(old.id())&&c.pinned));
        } finally {controller.destroy();}
    }
    @Test public void longTextUsesLosslessTxtAndKeepsDraftOnFailure() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        List<LanNode.Message> received=new CopyOnWriteArrayList<>();
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){if(!update&&!m.outgoing)received.add(m);}};
        int a=port(),b=port();while(a==b)b=port();
        try(LanNode sender=new LanNode(a,true,"text-sender","host","发送方","test",quiet);LanNode receiver=new LanNode(b,true,"text-receiver","host","接收方","test",listener)) {
            settle(service);set(service,"node",sender);sender.start();receiver.start();sender.probe(LanNode.endpoint("127.0.0.1:"+b));waitFor(service,()->sender.peers().size()==1&&receiver.peers().size()==1);
            String text="中文 👩🏽‍💻\r\n 保留空格  \t".repeat(5000);LanNode.Peer peer=sender.peers().get(0);DraftStore drafts=new DraftStore(service);drafts.save(peer.id(),text);
            assertFalse(MessageText.asFile("a".repeat(16384)));assertTrue(MessageText.asFile("a".repeat(16385)));assertTrue(MessageText.asFile("中".repeat(6000)));
            List<Boolean> result=new ArrayList<>();service.send(peer,text,result::add);waitFor(service,()->!result.isEmpty()&&!received.isEmpty());assertEquals(Collections.singletonList(true),result);assertEquals("",drafts.read(peer.id()));
            LanNode.Message message=received.get(0);assertEquals(1,message.files.size());assertTrue(message.files.get(0).name.endsWith(".txt"));
            File target=new File(service.getCacheDir(),"long-text-received.txt");receiver.receiveFile(receiver.peers().get(0),message.number,message.files.get(0),target,new LanNode.Transfer(),percent->{});assertEquals(text,new String(Files.readAllBytes(target.toPath()),StandardCharsets.UTF_8));
            File offers=new File(service.getFilesDir(),"offers");File[] folders=offers.listFiles();assertNotNull(folders);File original=Arrays.stream(folders).flatMap(f->Arrays.stream(Objects.requireNonNull(f.listFiles()))).filter(f->f.getName().equals(message.files.get(0).name)).findFirst().orElseThrow();
            Uri uri=ReceivedFileProvider.uri(service,original,original.getName());assertNotNull(uri);
            int before=folders.length;result.clear();String invalid=text+"\ud800";drafts.save(peer.id(),invalid);service.send(peer,invalid,result::add);waitFor(service,()->!result.isEmpty());assertEquals(Collections.singletonList(false),result);assertEquals(invalid,drafts.read(peer.id()));assertEquals(before,Objects.requireNonNull(offers.list()).length);
        } finally {controller.destroy();}
    }
    @Test public void multiPickerAcceptsClipOnlyDeduplicatesAndRejectsInvalidItems() {
        Uri a=Uri.parse("content://feiq.documents/a"),b=Uri.parse("content://feiq.documents/b");
        ClipData clip=ClipData.newRawUri("文件",a);clip.addItem(new ClipData.Item(b));clip.addItem(new ClipData.Item(a));
        Intent selection=new Intent();selection.setClipData(clip);
        assertEquals(Arrays.asList(a,b),MainActivity.selectedFiles(selection));
        assertEquals(Collections.singletonList(a),MainActivity.selectedFiles(new Intent().setData(a)));
        try {MainActivity.selectedFiles(new Intent().setData(Uri.parse("file:///private")));fail();}catch(IllegalArgumentException expected){}
        for(int i=3;i<1001;i++)clip.addItem(new ClipData.Item(a));
        try {MainActivity.selectedFiles(selection);fail();}catch(IllegalArgumentException expected){}
    }
    @Test public void batchSendUsesOneInvitationAndCleansFailedBatch() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        List<LanNode.Message> received=new CopyOnWriteArrayList<>();
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
        LanNode.Listener listener=new LanNode.Listener(){public void peersChanged(){}public void error(String t){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){if(!update&&!m.outgoing)received.add(m);}};
        int a=port(),b=port();while(a==b)b=port();
        try(LanNode sender=new LanNode(a,true,"batch-sender","host","发送方","test",quiet);LanNode receiver=new LanNode(b,true,"batch-receiver","host","接收方","test",listener)) {
            settle(service);set(service,"node",sender);sender.start();receiver.start();sender.probe(LanNode.endpoint("127.0.0.1:"+b));
            waitFor(service,()->sender.peers().size()==1&&receiver.peers().size()==1);
            Docs docs=provider(service);List<Uri> uris=new ArrayList<>();
            for(String content:Arrays.asList("第一份中文 😀","第二份同名文件")) {
                String id=docs.createDocument("root","text/plain","同名.txt");Files.write(docs.files.get(id).toPath(),content.getBytes(StandardCharsets.UTF_8));
                uris.add(DocumentsContract.buildDocumentUri("feiq.documents",id));
            }
            service.sendFiles(sender.peers().get(0),uris);waitFor(service,()->!service.busy());
            // 发送线程结束不代表对端回调已执行；高负载下仍要等待真实收到邀请。
            waitFor(service,()->!received.isEmpty());
            assertEquals("发送完成仍显示常驻说明","",service.transferStatus());
            assertFalse(String.valueOf(field(service,"transferStatus")),received.isEmpty());
            assertEquals(1,received.size());LanNode.Message message=received.get(0);assertEquals(2,message.files.size());
            for(int i=0;i<2;i++) {
                File target=new File(service.getCacheDir(),"batch-received-"+i);
                receiver.receiveFile(receiver.peers().get(0),message.number,message.files.get(i),target,new LanNode.Transfer(),percent->{});
                assertArrayEquals(Files.readAllBytes(docs.files.get(DocumentsContract.getDocumentId(uris.get(i))).toPath()),Files.readAllBytes(target.toPath()));
            }
            File offers=new File(service.getFilesDir(),"offers");int before=Objects.requireNonNull(offers.list()).length;
            service.sendFiles(sender.peers().get(0),Arrays.asList(uris.get(0),Uri.parse("content://feiq.documents/document/missing")));
            waitFor(service,()->!service.busy());assertEquals(before,Objects.requireNonNull(offers.list()).length);assertEquals(1,received.size());
            assertTrue(docs.files.values().stream().allMatch(File::isFile));
        } finally {controller.destroy();}
    }
    @Test public void cancellingAfterFirstBatchPreservesPublishedFilesAndCleansUnsentCopies() throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        java.util.concurrent.atomic.AtomicInteger published=new java.util.concurrent.atomic.AtomicInteger();List<LanNode.Message> received=new CopyOnWriteArrayList<>();
        LanNode.Listener sending=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(message.outgoing&&!update&&published.incrementAndGet()==1)service.cancelTransfer();}};
        LanNode.Listener receiving=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){if(!message.outgoing&&!update)received.add(message);}};
        int a=port(),b=port();while(a==b)b=port();
        try(LanNode sender=new LanNode(a,true,"cancel-sender","host","发送方","test",sending);LanNode receiver=new LanNode(b,true,"cancel-receiver","host","接收方","test",receiving)) {
            settle(service);set(service,"node",sender);sender.start();receiver.start();sender.probe(LanNode.endpoint("127.0.0.1:"+b));waitFor(service,()->sender.peers().size()==1&&receiver.peers().size()==1);
            Docs docs=provider(service);List<Uri> uris=new ArrayList<>();
            for(int i=0;i<21;i++){String id=docs.createDocument("root","text/plain","文件"+i+".txt");Files.write(docs.files.get(id).toPath(),("原始文件"+i).getBytes(StandardCharsets.UTF_8));uris.add(DocumentsContract.buildDocumentUri("feiq.documents",id));}
            service.sendFiles(sender.peers().get(0),uris);waitFor(service,()->!service.busy()&&!received.isEmpty());
            assertEquals("取消后仍发出了下一批邀请",1,published.get());assertEquals(20,received.get(0).files.size());
            File offers=new File(service.getFilesDir(),"offers");assertEquals("未发文件的缓存没有清理",20,Objects.requireNonNull(offers.list()).length);
            assertTrue("清理误删了来源",docs.files.values().stream().allMatch(File::isFile));
            File target=new File(service.getCacheDir(),"cancel-published-file.txt");LanNode.Message message=received.get(0);receiver.receiveFile(receiver.peers().get(0),message.number,message.files.get(0),target,new LanNode.Transfer(),null);
            assertTrue(target.isFile());assertTrue(service.transferStatus().contains("已发邀请保留"));
        } finally {controller.destroy();}
    }
    @Test public void offeredFileProviderIsSingleFileReadOnlyAndRejectsTraversal() throws Exception {
        Context context=RuntimeEnvironment.getApplication();ReceivedFileProvider provider=new ReceivedFileProvider();ProviderInfo info=new ProviderInfo();info.authority=context.getPackageName()+".received";info.grantUriPermissions=true;provider.attachInfo(context,info);
        File folder=new File(context.getFilesDir(),"offers/"+UUID.randomUUID());assertTrue(folder.mkdirs());File file=new File(folder,"图片.png");Files.write(file.toPath(),new byte[]{1,2,3});
        Uri uri=ReceivedFileProvider.uri(context,file,"图片.png");assertEquals(Arrays.asList("offers",folder.getName(),"图片.png"),uri.getPathSegments());
        try(ParcelFileDescriptor fd=provider.openFile(uri,"r")){assertEquals(3,fd.getStatSize());}
        fails(()->provider.openFile(uri,"rw"));fails(()->provider.openFile(uri.buildUpon().path("offers/"+folder.getName()+"/../secret").build(),"r"));
        Uri escaped=new Uri.Builder().scheme("content").authority(info.authority).appendPath("offers").appendPath(folder.getName()).appendPath("../secret").build();fails(()->provider.openFile(escaped,"r"));
        File outside=source(context,"不可分享");try{ReceivedFileProvider.uri(context,outside,"secret");fail("授权范围扩大到整个私有目录");}catch(IllegalArgumentException expected){}
        try(Cursor row=provider.query(uri,null,null,null,null)){assertTrue(row.moveToFirst());assertEquals("图片.png",row.getString(row.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME)));}
        Files.delete(file.toPath());fails(()->provider.openFile(uri,"r"));assertNull(provider.query(uri,null,null,null,null));Files.delete(folder.toPath());
    }
    private static final Uri TREE=DocumentsContract.buildTreeDocumentUri("feiq.documents","root");
    private static final int FLAGS=Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
    private static Object field(Object o,String name)throws Exception { Field f=o.getClass().getDeclaredField(name); f.setAccessible(true); return f.get(o); }
    private static void set(Object o,String name,Object value)throws Exception { Field f=o.getClass().getDeclaredField(name); f.setAccessible(true); f.set(o,value); }
    private static void settle(ChatService s)throws Exception { for(int i=0;i<3;i++) { ((ExecutorService)field(s,"io")).submit(()->{}).get(5,TimeUnit.SECONDS); shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100)); } }
    private static void waitFor(ChatService s,BooleanSupplier done)throws Exception { long end=System.nanoTime()+TimeUnit.SECONDS.toNanos(8); do { settle(s); if(done.getAsBoolean()) return; Thread.sleep(15); } while(System.nanoTime()<end); assertTrue("异步接收超时",done.getAsBoolean()); }
    private static int port()throws Exception {
        for(int attempt=0;attempt<20;attempt++) {
            try(DatagramSocket udp=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
                try(ServerSocket tcp=new ServerSocket(udp.getLocalPort(),1,InetAddress.getLoopbackAddress())) {return udp.getLocalPort();}
                catch(BindException occupied){/* TCP和UDP的动态端口池独立，需要同时空闲。 */}
            }
        }
        throw new IOException("未找到同时空闲的TCP/UDP测试端口");
    }
    private static File source(Context c,String text)throws Exception { File f=File.createTempFile("received-",".txt",c.getCacheDir()); Files.write(f.toPath(),text.getBytes(StandardCharsets.UTF_8)); return f; }
    private static File receivedSource(Context context,String text)throws Exception {
        File directory=new File(context.getFilesDir(),"received");assertTrue(directory.isDirectory()||directory.mkdirs());
        File file=new File(directory,SharedFiles.random(32));Files.write(file.toPath(),text.getBytes(StandardCharsets.UTF_8));return file;
    }
    private static void fails(Throwing action)throws Exception { try { action.run(); fail("操作应当被拒绝"); } catch(IOException|SecurityException expected) { } }
    private interface Throwing { void run()throws Exception; }
    public static final class Docs extends DocumentsProvider {
        final Map<String,File> files=new LinkedHashMap<>();final Map<String,String> names=new LinkedHashMap<>(),parents=new LinkedHashMap<>();final Set<String> directories=new HashSet<>();
        int sequence,missingResponse;boolean deny,failOpen,reuse,refuseDelete,corruptRead;CountDownLatch openedGate,releaseGate,queryGate,queryRelease;String blockedQuery;
        @Override public boolean onCreate() { return true; }
        private void check() { if(deny) throw new SecurityException("授权已撤销"); }
        private String[] columns(String[] p) { return p==null?new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_FLAGS}:p; }
        private void row(MatrixCursor c,String id) { Object[] data=new Object[c.getColumnCount()]; String[] cols=c.getColumnNames(); for(int i=0;i<cols.length;i++) {
            if(cols[i].equals(DocumentsContract.Document.COLUMN_DOCUMENT_ID)) data[i]=id;
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_DISPLAY_NAME)) data[i]=id.equals("root")?"测试接收目录":names.get(id);
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_MIME_TYPE)) data[i]=id.equals("root")||directories.contains(id)?DocumentsContract.Document.MIME_TYPE_DIR:"text/plain";
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_FLAGS)) data[i]=id.equals("root")||directories.contains(id)?DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE:DocumentsContract.Document.FLAG_SUPPORTS_WRITE;
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_SIZE)) data[i]=files.containsKey(id)?files.get(id).length():0L;
        } c.addRow(data); }
        @Override public Cursor queryRoots(String[] p) { return new MatrixCursor(new String[]{DocumentsContract.Root.COLUMN_ROOT_ID}); }
        @Override public Cursor queryDocument(String id,String[] p)throws FileNotFoundException {
            check();
            if(id.equals(blockedQuery)){queryGate.countDown();try{if(!queryRelease.await(5,TimeUnit.SECONDS))throw new FileNotFoundException("查询超时");}catch(InterruptedException e){Thread.currentThread().interrupt();throw new FileNotFoundException("已取消查询");}}
            MatrixCursor cursor=new MatrixCursor(columns(p));
            if(!id.equals("root")&&!directories.contains(id)&&(!files.containsKey(id)||!files.get(id).isFile())) {
                if(missingResponse==1)return cursor;if(missingResponse==2)return null;if(missingResponse==3)throw new IllegalArgumentException("文件已删除");throw new FileNotFoundException();
            }
            row(cursor,id);return cursor;
        }
        @Override public Cursor queryChildDocuments(String parent,String[] p,String sort) {check();MatrixCursor cursor=new MatrixCursor(columns(p));for(String id:names.keySet())if(parent.equals(parents.get(id)))row(cursor,id);return cursor;}
        @Override public boolean isChildDocument(String parent,String child) {for(String ancestor=parents.get(child);ancestor!=null;ancestor=parents.get(ancestor))if(parent.equals(ancestor))return true;return false;}
        @Override public String createDocument(String parent,String mime,String name)throws FileNotFoundException {
            check();if(reuse)return files.keySet().iterator().next();
            try {
                String id="file"+(++sequence);
                if(DocumentsContract.Document.MIME_TYPE_DIR.equals(mime))directories.add(id);
                else files.put(id,File.createTempFile("doc-",".tmp",getContext().getCacheDir()));
                names.put(id,name);parents.put(id,parent);return id;
            }catch(IOException e){throw new FileNotFoundException();}
        }
        @Override public void deleteDocument(String id)throws FileNotFoundException {check();if(refuseDelete)throw new FileNotFoundException("提供方拒绝删除");File file=files.remove(id);names.remove(id);directories.remove(id);if(file!=null)file.delete();}
        @Override public ParcelFileDescriptor openDocument(String id,String mode,CancellationSignal signal)throws FileNotFoundException {
            check();if(failOpen||!files.containsKey(id))throw new FileNotFoundException("写入失败");
            if(mode.equals("r")&&openedGate!=null){openedGate.countDown();try{if(!releaseGate.await(5,TimeUnit.SECONDS))throw new FileNotFoundException("读取超时");}catch(InterruptedException e){Thread.currentThread().interrupt();throw new FileNotFoundException("已取消读取");}}
            if(corruptRead&&mode.equals("r"))try {byte[] bytes=Files.readAllBytes(files.get(id).toPath());if(bytes.length>0)bytes[0]^=1;Files.write(files.get(id).toPath(),bytes);}catch(IOException e){throw new FileNotFoundException();}
            return ParcelFileDescriptor.open(files.get(id),ParcelFileDescriptor.parseMode(mode));
        }
    }
    static Docs provider(Context context) {
        Docs docs=new Docs();ProviderInfo info=new ProviderInfo();info.authority="feiq.documents";info.exported=true;info.grantUriPermissions=true;info.readPermission=info.writePermission="android.permission.MANAGE_DOCUMENTS";docs.attachInfo(context,info);ShadowContentResolver.registerProviderInternal(info.authority,docs);
        android.content.pm.ResolveInfo resolved=new android.content.pm.ResolveInfo();resolved.providerInfo=info;shadowOf(context.getPackageManager()).addResolveInfoForIntent(new Intent(DocumentsContract.PROVIDER_INTERFACE),resolved);return docs;
    }
    @Test public void directoryGrantPersistsAndSameNamesNeverOverwrite()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); Docs docs=provider(c); ReceivedStorage storage=new ReceivedStorage(c);
        assertTrue(storage.automatic()); assertNull(storage.tree()); storage.choose(TREE,FLAGS); assertEquals(TREE,new ReceivedStorage(c).tree());
        String old=docs.createDocument("root","text/plain","报告.txt"); Files.write(docs.files.get(old).toPath(),"原文件".getBytes(StandardCharsets.UTF_8));
        File file=receivedSource(c,"新文件");Uri saved=storage.save(file,"报告.txt",new LanNode.Transfer());assertFalse("保存目录提交后仍留下第二份文件",file.exists());
        assertEquals("报告 (1).txt",docs.names.get(DocumentsContract.getDocumentId(saved))); assertEquals("原文件",new String(Files.readAllBytes(docs.files.get(old).toPath()),StandardCharsets.UTF_8));
        assertEquals("新文件",new String(Files.readAllBytes(docs.files.get(DocumentsContract.getDocumentId(saved)).toPath()),StandardCharsets.UTF_8));
        assertEquals(saved,new ReceivedStorage(c).existingSaved(file));assertEquals(saved,storage.save(file,"报告.txt",new LanNode.Transfer()));assertEquals(2,docs.files.size());
        storage.useInternal();assertNull(storage.tree());assertEquals(saved,storage.existingSaved(file));assertNull(storage.save(file,"报告.txt",new LanNode.Transfer()));
    }
    @Test public void revokedPermissionAndFailedWriteKeepPrivateCopy()throws Exception {
        Context c=RuntimeEnvironment.getApplication();Docs docs=provider(c);ReceivedStorage storage=new ReceivedStorage(c);storage.choose(TREE,FLAGS);File file=receivedSource(c,"保留完整文件");
        fails(()->storage.choose(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION)); assertEquals(TREE,storage.tree());
        docs.deny=true; fails(()->storage.save(file,"资料.txt",new LanNode.Transfer())); assertTrue(file.isFile()); assertNull(storage.saved(file));
        docs.deny=false; docs.failOpen=true; fails(()->storage.save(file,"资料.txt",new LanNode.Transfer())); assertTrue(docs.files.isEmpty()); assertTrue(file.isFile());
        docs.failOpen=false;assertNotNull(storage.save(file,"资料.txt",new LanNode.Transfer()));assertFalse(file.exists());assertTrue(storage.exists(file));
    }
    @Test public void deletingReceivedFileRemovesOnlyItsPrivateAndSavedCopies()throws Exception {
        Context c=RuntimeEnvironment.getApplication();Docs docs=provider(c);ReceivedStorage storage=new ReceivedStorage(c);storage.choose(TREE,FLAGS);
        android.content.pm.ResolveInfo resolved=new android.content.pm.ResolveInfo();resolved.providerInfo=new ProviderInfo();resolved.providerInfo.authority="feiq.documents";shadowOf(c.getPackageManager()).addResolveInfoForIntent(new Intent(DocumentsContract.PROVIDER_INTERFACE),resolved);
        File directory=new File(c.getFilesDir(),"received");assertTrue(directory.isDirectory()||directory.mkdirs());File file=new File(directory,"selected");Files.write(file.toPath(),new byte[]{1,2,3});
        String other=docs.createDocument("root","text/plain","另一个文件.txt");Files.write(docs.files.get(other).toPath(),new byte[]{9});
        Uri saved=storage.save(file,"已接收.txt",new LanNode.Transfer());storage.useInternal();
        Files.write(file.toPath(),new byte[]{1,2,3}); // 兼容旧版遗留双份数据，删除仍必须清理两处。
        storage.delete(file);assertFalse(file.exists());assertNull(new ReceivedStorage(c).saved(file));assertFalse(docs.files.containsKey(DocumentsContract.getDocumentId(saved)));assertTrue(docs.files.containsKey(other));
        File outside=source(c,"原文件保留");fails(()->storage.delete(outside));assertTrue(outside.isFile());
    }
    @Test public void deniedSavedFileDeletionPreservesPrivateCopyAndLocation()throws Exception {
        Context c=RuntimeEnvironment.getApplication();Docs docs=provider(c);ReceivedStorage storage=new ReceivedStorage(c);storage.choose(TREE,FLAGS);
        android.content.pm.ResolveInfo resolved=new android.content.pm.ResolveInfo();resolved.providerInfo=new ProviderInfo();resolved.providerInfo.authority="feiq.documents";shadowOf(c.getPackageManager()).addResolveInfoForIntent(new Intent(DocumentsContract.PROVIDER_INTERFACE),resolved);
        File directory=new File(c.getFilesDir(),"received");assertTrue(directory.isDirectory()||directory.mkdirs());File file=new File(directory,"preserved");Files.write(file.toPath(),new byte[]{4,5,6});Uri saved=storage.save(file,"保留.txt",new LanNode.Transfer());
        Files.write(file.toPath(),new byte[]{4,5,6});
        docs.deny=true;fails(()->storage.delete(file));assertTrue(file.isFile());assertEquals(saved,storage.saved(file));
        docs.deny=false;docs.refuseDelete=true;fails(()->storage.delete(file));assertTrue(file.isFile());assertEquals(saved,storage.saved(file));
        docs.refuseDelete=false;storage.delete(file);assertFalse(file.exists());assertNull(storage.saved(file));
    }
    @Test public void receivedFileMenuConfirmsDeletionAndKeepsMessage()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create();ChatService service=sc.get();service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
        shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
        ActivityController<MainActivity> ac=Robolectric.buildActivity(MainActivity.class).setup();MainActivity activity=ac.get();
        try {
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"saved","host","文件","");Protocol.Attachment attachment=new Protocol.Attachment(0,"已接收.txt",3,0);
            File file=service.receivedFile(peer,1,attachment);assertTrue(file.getParentFile().isDirectory()||file.getParentFile().mkdirs());Files.write(file.toPath(),new byte[]{1,2,3});
            LanNode.Message message=new LanNode.Message();message.number=1;message.text="保留消息";message.time=1;message.files=Collections.singletonList(attachment);service.incoming(peer,message,false);settle(service);
            Method menu=MainActivity.class.getDeclaredMethod("fileActions",LanNode.Peer.class,long.class,Protocol.Attachment.class);menu.setAccessible(true);menu.invoke(activity,peer,1L,attachment);
            android.app.Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();findButton(dialog.getWindow().getDecorView(),"删除本机文件").performClick();dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();dialog.dismiss();assertTrue(file.isFile());
            menu.invoke(activity,peer,1L,attachment);findButton(org.robolectric.shadows.ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除本机文件").performClick();findButton(org.robolectric.shadows.ShadowDialog.getLatestDialog().getWindow().getDecorView(),"删除文件").performClick();
            ((ExecutorService)field(service,"transferWorker")).submit(()->{}).get(5,TimeUnit.SECONDS);settle(service);
            assertFalse(file.exists());assertEquals(1,((ChatStore)field(service,"store")).history(peer.id()).size());assertEquals("本机文件已删除",service.progress(peer.id(),1,0,false).state);
        } finally {ac.pause().stop().destroy();sc.destroy();}
    }
    private static Button findButton(View view,String text) {
        if(view instanceof Button&&text.contentEquals(((Button)view).getText()))return (Button)view;
        if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){Button found=findButton(((ViewGroup)view).getChildAt(i),text);if(found!=null)return found;}return null;
    }
    @Test public void cancellationAndProviderReturningExistingDocumentAreSafe()throws Exception {
        Context c=RuntimeEnvironment.getApplication();Docs docs=provider(c);ReceivedStorage storage=new ReceivedStorage(c);storage.choose(TREE,FLAGS);File file=receivedSource(c,"新内容");
        LanNode.Transfer cancelled=new LanNode.Transfer(); cancelled.close(); fails(()->storage.save(file,"文件.txt",cancelled)); assertTrue(docs.files.isEmpty());
        String old=docs.createDocument("root","text/plain","旧文件.txt"); Files.write(docs.files.get(old).toPath(),"不可覆盖".getBytes(StandardCharsets.UTF_8)); docs.reuse=true;
        fails(()->storage.save(file,"新名字.txt",new LanNode.Transfer())); assertEquals(1,docs.files.size()); assertEquals("不可覆盖",new String(Files.readAllBytes(docs.files.get(old).toPath()),StandardCharsets.UTF_8));
    }
    @Test public void singleSaveChecksBytesBeforeDeleting()throws Exception {
        Context c=RuntimeEnvironment.getApplication();Docs docs=provider(c);ReceivedStorage storage=new ReceivedStorage(c);storage.choose(TREE,FLAGS);
        File file=receivedSource(c,"保留完整文件 😀");byte[] original=Files.readAllBytes(file.toPath());docs.corruptRead=true;
        fails(()->storage.save(file,"报告.txt",new LanNode.Transfer()));assertArrayEquals(original,Files.readAllBytes(file.toPath()));assertTrue(docs.files.isEmpty());assertNull(storage.saved(file));
        docs.corruptRead=false;Uri saved=storage.save(file,"报告.txt",new LanNode.Transfer());assertFalse(file.exists());assertArrayEquals(original,Files.readAllBytes(docs.files.get(DocumentsContract.getDocumentId(saved)).toPath()));assertEquals(saved,storage.existingSaved(file));
        SharedPreferences records=c.getSharedPreferences("received_locations",0);assertEquals(original.length,records.getLong(file.getName()+".size",-1));assertEquals("报告.txt",records.getString(file.getName()+".name",""));
    }
    @Test public void singleSaveKeepsRelativeFolders()throws Exception {
        Context context=RuntimeEnvironment.getApplication();Docs docs=provider(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(TREE,FLAGS);
        String unrelated=docs.createDocument("root","text/plain","用户文件.txt");Files.write(docs.files.get(unrelated).toPath(),new byte[]{9});
        File file=receivedSource(context,"目录中的文件");Uri saved=storage.save(file,"报告.txt","项目/子目录/报告.txt",new LanNode.Transfer());
        String document=DocumentsContract.getDocumentId(saved),parent=docs.parents.get(document);assertEquals("子目录",docs.names.get(parent));assertEquals("项目",docs.names.get(docs.parents.get(parent)));assertEquals(2,docs.directories.size());assertFalse(file.exists());
        storage.delete(file);assertFalse(storage.exists(file));assertTrue(docs.files.containsKey(unrelated));assertEquals(2,docs.directories.size());
    }
    @Test public void missingSavedFilesDiscardOnlyStaleLocations()throws Exception {
        Context context=RuntimeEnvironment.getApplication();Docs docs=provider(context);ReceivedStorage storage=new ReceivedStorage(context);storage.choose(TREE,FLAGS);
        for(int response=0;response<4;response++) {
            docs.missingResponse=response;File file=receivedSource(context,"唯一正式文件");Uri saved=storage.save(file,"已接收.txt",new LanNode.Transfer());assertTrue(storage.exists(file));
            docs.deny=true;assertNull(storage.existingSaved(file));assertFalse(storage.exists(file));assertEquals("权限失效不能丢掉位置",saved,storage.saved(file));docs.deny=false;assertEquals(saved,storage.existingSaved(file));
            docs.deleteDocument(DocumentsContract.getDocumentId(saved));assertNull(storage.existingSaved(file));assertFalse(storage.exists(file));assertNull(storage.saved(file));assertNull(storage.savedTree(file));assertFalse(context.getSharedPreferences("received_locations",0).contains(file.getName()+".size"));
            File direct=receivedSource(context,"直接清理失效位置");Uri document=storage.save(direct,"失效.txt",new LanNode.Transfer());docs.deleteDocument(DocumentsContract.getDocumentId(document));storage.delete(direct);assertNull(storage.saved(direct));assertFalse(storage.exists(direct));
        }
    }
    @Test public void incompleteExternalDocumentIsExplicitWhenProviderRefusesCleanup()throws Exception {
        Context c=RuntimeEnvironment.getApplication();Docs docs=provider(c);ReceivedStorage storage=new ReceivedStorage(c);storage.choose(TREE,FLAGS);docs.failOpen=docs.refuseDelete=true;File file=receivedSource(c,"完整内容");
        try { storage.save(file,"文件.txt",new LanNode.Transfer()); fail(); } catch(IOException e) { assertTrue(e.getMessage().contains("不完整副本")); }
        assertTrue(file.isFile()); assertNull(storage.saved(file)); assertEquals(1,docs.files.size());
    }
    @Test public void forwardCanReadAuthorizedExportAfterInternalCopyIsGone()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){}};
        try(LanNode node=new LanNode(port(),true,"forward-saf","host","测试","测试",quiet);DatagramSocket socket=new DatagramSocket(0,InetAddress.getLoopbackAddress())){
            settle(service);node.start();set(service,"node",node);Docs docs=provider(service);ReceivedStorage storage=new ReceivedStorage(service);storage.choose(TREE,FLAGS);
            LanNode.Peer source=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"source","host","来源","测试"),target=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+socket.getLocalPort()),"target","host","目标","测试");target.utf8=true;
            LanNode.Message message=new LanNode.Message();message.number=123;message.text="SAF转发";byte[] bytes="授权副本".getBytes(StandardCharsets.UTF_8);Protocol.Attachment file=new Protocol.Attachment(0,"导出文件.txt",bytes.length,0);message.files.add(file);
            File local=service.receivedFile(source,message.number,file);assertTrue(local.getParentFile().isDirectory()||local.getParentFile().mkdirs());Files.write(local.toPath(),bytes);Uri saved=storage.save(local,file.name,new LanNode.Transfer());assertFalse(local.exists());
            List<Boolean> result=new ArrayList<>();service.forward(source,message,target,result::add);waitFor(service,()->!result.isEmpty());assertEquals(Collections.singletonList(true),result);
            assertEquals(1,docs.files.size());assertArrayEquals(bytes,Files.readAllBytes(docs.files.values().iterator().next().toPath()));
            File offers=new File(service.getFilesDir(),"offers");File[] folders=offers.listFiles();assertNotNull(folders);assertEquals(1,folders.length);assertArrayEquals(bytes,Files.readAllBytes(new File(folders[0],file.name).toPath()));
            docs.deleteDocument(DocumentsContract.getDocumentId(saved));result.clear();service.forward(source,message,target,result::add);waitFor(service,()->!result.isEmpty());assertEquals(Collections.singletonList(false),result);assertFalse(storage.exists(local));assertNull(storage.saved(local));assertEquals(1,Objects.requireNonNull(offers.listFiles()).length);
        }finally{controller.destroy();}
    }
    @Test public void backgroundBatchReceiveSwitchAndDirectoryFailureRetry()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create(); ChatService service=controller.get();
        List<String> errors=new ArrayList<>();service.observe(new ChatService.Observer(){public void changed(){}public void error(String text){errors.add(text);}});
        int aPort=port(), bPort=port(); while(aPort==bPort) bPort=port();
        LanNode.Listener quiet=new LanNode.Listener(){ public void peersChanged(){} public void error(String text){} public void message(LanNode.Peer p,LanNode.Message m,boolean update){} };
        LanNode.Listener incoming=new LanNode.Listener(){ public void peersChanged(){} public void error(String text){} public void message(LanNode.Peer p,LanNode.Message m,boolean update){service.incoming(p,m,update);} };
        try(LanNode sender=new LanNode(aPort,true,"file-sender","host","发送方","test",quiet); LanNode receiver=new LanNode(bPort,true,"file-receiver","host","接收方","test",incoming)) {
            settle(service); set(service,"node",receiver); sender.start(); receiver.start(); sender.probe(LanNode.endpoint("127.0.0.1:"+bPort));
            waitFor(service,()->sender.peers().size()==1&&receiver.peers().size()==1);
            LanNode.Peer remote=receiver.peers().get(0); File one=source(service,"自动接收内容"), empty=source(service,"");
            LanNode.Message batch=sender.sendMessage(sender.peers().get(0),"后台批量",Arrays.asList(one,empty));
            File saved=service.receivedFile(remote,batch.number,batch.files.get(0)), zero=service.receivedFile(remote,batch.number,batch.files.get(1));
            waitFor(service,()->saved.isFile()&&zero.isFile()&&!service.busy()); assertArrayEquals(Files.readAllBytes(one.toPath()),Files.readAllBytes(saved.toPath())); assertEquals(0,zero.length());
            service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).apply();
            LanNode.Message manual=sender.sendMessage(sender.peers().get(0),"等待确认",Collections.singletonList(one)); File pending=service.receivedFile(remote,manual.number,manual.files.get(0));
            waitFor(service,()->!((Map<?,?>)uncheckedField(service,"waiting")).isEmpty()); assertFalse(pending.exists());
            service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,true).apply(); service.receiveSettingsChanged(); waitFor(service,()->pending.isFile()&&!service.busy());
            Docs docs=provider(service); ReceivedStorage storage=new ReceivedStorage(service); storage.choose(TREE,FLAGS); docs.deny=true;
            LanNode.Message failure=sender.sendMessage(sender.peers().get(0),"目录权限失效",Collections.singletonList(one)); File cached=service.receivedFile(remote,failure.number,failure.files.get(0));
            waitFor(service,()->cached.isFile()&&!service.busy()); assertNull(storage.saved(cached)); assertTrue(((Map<?,?>)field(service,"waiting")).isEmpty());
            assertEquals("已保存",service.progress(remote.id(),failure.number,failure.files.get(0).id,false).state);
            assertTrue(errors.toString(),errors.stream().anyMatch(text->text.contains("保存目录不可用")&&text.contains("文件已保存在应用内接收区")));
            docs.deny=false;LanNode.Message restored=sender.sendMessage(sender.peers().get(0),"目录恢复后的新接收",Collections.singletonList(one));File external=service.receivedFile(remote,restored.number,restored.files.get(0));
            waitFor(service,()->storage.exists(external)&&!service.busy());assertFalse(external.exists());assertTrue("恢复目录不能另行搬走之前的内部正式文件",cached.isFile());assertArrayEquals(Files.readAllBytes(one.toPath()),Files.readAllBytes(docs.files.get(DocumentsContract.getDocumentId(storage.existingSaved(external))).toPath()));
            docs.deleteDocument(DocumentsContract.getDocumentId(storage.existingSaved(external)));assertFalse(storage.exists(external));service.receive(remote,restored.number,restored.files.get(0));waitFor(service,()->storage.exists(external)&&!service.busy());assertFalse(external.exists());
            storage.settings().edit().putBoolean(ReceivedStorage.AUTO,false).apply();
            LanNode.Message partial=sender.sendMessage(sender.peers().get(0),"部分接收后拒绝剩余",Arrays.asList(one,empty));
            waitFor(service,()->((Map<?,?>)uncheckedField(service,"waiting")).size()==2);
            service.receive(remote,partial.number,partial.files.get(0));File kept=service.receivedFile(remote,partial.number,partial.files.get(0));waitFor(service,()->storage.exists(kept)&&!service.busy());
            service.reject(remote,partial.number); settle(service);
            LanNode.Message record=((ChatStore)field(service,"store")).history(remote.id()).stream().filter(m->m.number==partial.number).findFirst().get();
            assertEquals(1,record.files.size());assertTrue(storage.exists(kept));assertFalse(kept.isFile());assertTrue(((Map<?,?>)field(service,"waiting")).isEmpty());
            LanNode.Message failed=new LanNode.Message();failed.number=98765;failed.time=System.currentTimeMillis();failed.outgoing=true;failed.text="失败重发测试";failed.state="未确认送达，请检查网络后重发";
            ((ExecutorService)field(service,"io")).submit(()->((ChatStore)uncheckedField(service,"store")).message(remote,failed,false,true)).get(5,TimeUnit.SECONDS);
            java.util.List<Boolean> outcomes=new ArrayList<>();service.retryMessage(remote,failed,outcomes::add);waitFor(service,()->outcomes.size()==1);assertTrue(outcomes.get(0));
            service.retryMessage(remote,failed,outcomes::add);waitFor(service,()->outcomes.size()==2);assertFalse(outcomes.get(1));
            waitFor(service,()->((ChatStore)uncheckedField(service,"store")).history(remote.id()).stream().anyMatch(m->m.number==98765&&m.state.equals("已送达")));
            assertEquals(1,((ChatStore)field(service,"store")).history(remote.id()).stream().filter(m->m.text.equals(failed.text)).count());
        } finally { controller.destroy(); }
    }
    private static Object uncheckedField(Object o,String name) { try { return field(o,name); } catch(Exception e) { throw new AssertionError(e); } }
    private static LanNode.Message interrupted(ChatService service,LanNode.Peer peer,long packet)throws Exception {
        LanNode.Message message=new LanNode.Message();message.number=packet;message.time=1;message.text="中断入站附件";message.state="已收到";
        message.files.add(new Protocol.Attachment(0,"saved.bin",3,123));message.files.add(new Protocol.Attachment(1,"partial.bin",4096,123));
        File saved=service.receivedFile(peer,packet,message.files.get(0)),part=LanNode.partialFile(peer,packet,message.files.get(1),service.receivedFile(peer,packet,message.files.get(1)));
        assertTrue(saved.getParentFile().isDirectory()||saved.getParentFile().mkdirs());Files.write(saved.toPath(),new byte[]{1,2,3});Files.write(part.toPath(),new byte[128]);
        ((ExecutorService)field(service,"io")).submit(()->((ChatStore)uncheckedField(service,"store")).message(peer,message,false,true)).get(5,TimeUnit.SECONDS);
        Method progress=ChatService.class.getDeclaredMethod("fileProgress",String.class,long.class,long.class,boolean.class,int.class,String.class,LanNode.Transfer.class);progress.setAccessible(true);
        progress.invoke(service,peer.id(),packet,0L,false,100,"已保存",null);progress.invoke(service,peer.id(),packet,1L,false,25,"接收失败 · 点击续传",null);settle(service);return message;
    }
    @Test public void offlineRejectClears()throws Exception {
        for(boolean detached:new boolean[]{false,true}) {
            ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();List<String> errors=new ArrayList<>();service.observe(new ChatService.Observer(){public void changed(){}public void error(String text){errors.add(text);}});
            LanNode local=null;
            try {
                settle(service);service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"offline","host","离线发送方","");peer.deviceId=detached?"1".repeat(32):"2".repeat(32);
                if(!detached) {
                    local=new LanNode(port(),true,"reject-local","host","本机","",new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}});
                    local.start();set(service,"node",local);
                    // 收包线程/发通知刚失去套接字时，通知 IOException 也不能回滚拒收。
                    ((DatagramSocket)field(local,"udp")).close();
                }
                LanNode.Message message=interrupted(service,peer,901);
                File saved=service.receivedFile(peer,message.number,message.files.get(0)),part=LanNode.partialFile(peer,message.number,message.files.get(1),service.receivedFile(peer,message.number,message.files.get(1)));
                service.reject(peer,message.number);settle(service);
                LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),message.number,false);
                assertEquals("已拒绝",stored.state);assertTrue(stored.text.endsWith("[附件已拒绝]"));assertEquals(1,stored.files.size());assertEquals(0,stored.files.get(0).id);
                assertFalse(part.exists());assertArrayEquals(new byte[]{1,2,3},Files.readAllBytes(saved.toPath()));assertEquals("已保存",service.progress(peer.id(),message.number,0,false).state);
                assertEquals("已拒绝",service.progress(peer.id(),message.number,1,false).state);assertEquals(0,service.progress(peer.id(),message.number,1,false).percent);
                assertTrue(errors.toString(),errors.stream().anyMatch(text->text.contains("已本地拒收")&&text.contains("拒绝通知未送达")));
                assertTrue(((Map<?,?>)field(service,"waiting")).isEmpty());
            } finally {if(local!=null)local.close();controller.destroy();}
        }
    }
    @Test public void deleteThenCancelKeepsRecord()throws Exception {
        for(boolean revoked:new boolean[]{false,true}) {
            ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
            try {
                settle(service);service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
                LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"deleted-saved","host","已保存后删除","");long packet=revoked?911:910;LanNode.Message message=interrupted(service,peer,packet);
                File file=service.receivedFile(peer,packet,message.files.get(0)),part=LanNode.partialFile(peer,packet,message.files.get(1),service.receivedFile(peer,packet,message.files.get(1)));
                Docs docs=provider(service);ReceivedStorage storage=new ReceivedStorage(service);storage.choose(TREE,FLAGS);Uri saved=storage.save(file,"saved.bin",new LanNode.Transfer());docs.deleteDocument(DocumentsContract.getDocumentId(saved));
                service.fileTasks(rows->{});settle(service);assertEquals("本机文件已删除",service.progress(peer.id(),packet,0,false).state);
                if(revoked)service.offerCanceled(peer,packet);else service.reject(peer,packet);settle(service);
                LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),packet,false);assertNotNull(stored);assertEquals(1,stored.files.size());assertEquals(0,stored.files.get(0).id);assertEquals("本机文件已删除",service.progress(peer.id(),packet,0,false).state);assertFalse(storage.exists(file));assertFalse(part.exists());
            }finally{controller.destroy();}
        }
    }
    @Test public void reReceiveExpiredOfferRestoresDeletedState()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();List<String> errors=new java.util.concurrent.CopyOnWriteArrayList<>();
        try {
            settle(service);service.observe(new ChatService.Observer(){public void changed(){}public void error(String text){errors.add(text);}});service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"expired-offer","host","失效邀请","");long packet=912;LanNode.Message message=interrupted(service,peer,packet);
            File file=service.receivedFile(peer,packet,message.files.get(0));
            Docs docs=provider(service);ReceivedStorage storage=new ReceivedStorage(service);storage.choose(TREE,FLAGS);Uri saved=storage.save(file,"saved.bin",new LanNode.Transfer());docs.deleteDocument(DocumentsContract.getDocumentId(saved));
            service.fileTasks(rows->{});settle(service);assertEquals("本机文件已删除",service.progress(peer.id(),packet,0,false).state);
            // 点「重新接收」后尚未收回文件，邀请随即失效。
            service.receive(peer,packet,message.files.get(0));waitFor(service,()->!service.busy());settle(service);
            assertNotEquals("本机文件已删除",service.progress(peer.id(),packet,0,false).state);
            assertTrue(service.getSharedPreferences("received_before",0).contains(ChatService.fileKey(peer.id(),packet,0,false)));
            service.offerCanceled(peer,packet);settle(service);
            LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),packet,false);assertNotNull(stored);
            assertEquals("重新接收失效后附件不能从消息中移除",1,stored.files.size());assertEquals(0,stored.files.get(0).id);
            assertEquals("本机文件已删除",service.progress(peer.id(),packet,0,false).state);assertEquals("对方已取消发送",service.progress(peer.id(),packet,1,false).state);
            assertEquals("本机文件已删除",((ChatStore)field(service,"store")).transfers().stream().filter(state->state.key.equals(ChatService.fileKey(peer.id(),packet,0,false))).findFirst().orElseThrow().state);
            assertFalse(service.getSharedPreferences("received_before",0).contains(ChatService.fileKey(peer.id(),packet,0,false)));
            // 单附件且消息状态已为「已保存」时，重新接收遇到失效邀请也必须恢复删除态并弹出失效提示。
            long singlePacket=913;LanNode.Message single=new LanNode.Message();single.number=singlePacket;single.time=System.currentTimeMillis();single.state="已保存";single.text="[文件] one.bin";single.files.add(new Protocol.Attachment(0,"one.bin",5,0));
            service.incoming(peer,single,false);settle(service);File singleFile=service.receivedFile(peer,singlePacket,single.files.get(0));Files.write(singleFile.toPath(),new byte[]{1,2,3,4,5});
            Uri singleSaved=storage.save(singleFile,"one.bin",new LanNode.Transfer());docs.deleteDocument(DocumentsContract.getDocumentId(singleSaved));
            service.fileTasks(rows->{});settle(service);assertEquals("本机文件已删除",service.progress(peer.id(),singlePacket,0,false).state);
            service.receive(peer,singlePacket,single.files.get(0));waitFor(service,()->!service.busy());settle(service);
            assertNotEquals("本机文件已删除",service.progress(peer.id(),singlePacket,0,false).state);
            errors.clear();service.offerCanceled(peer,singlePacket);settle(service);
            assertEquals("本机文件已删除",service.progress(peer.id(),singlePacket,0,false).state);
            assertTrue("整批已保存消息重收失效缺少提示："+errors,errors.stream().anyMatch(text->text.contains("原邀请已失效")));
            assertFalse(service.getSharedPreferences("received_before",0).contains(ChatService.fileKey(peer.id(),singlePacket,0,false)));
        }finally{controller.destroy();}
    }
    @Test public void rejectDuringSavingStillPublishesSavedState()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        try {
            settle(service);service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"reject-saving","host","保存中拒绝","");long packet=914;
            LanNode.Message message=new LanNode.Message();message.number=packet;message.time=System.currentTimeMillis();message.state="已收到";message.text="[文件] saved.bin";message.files.add(new Protocol.Attachment(0,"saved.bin",6,0));
            service.incoming(peer,message,false);settle(service);
            File target=service.receivedFile(peer,packet,message.files.get(0));target.getParentFile().mkdirs();Files.write(target.toPath(),new byte[]{1,2,3,4,5,6});
            Docs docs=provider(service);ReceivedStorage storage=new ReceivedStorage(service);storage.choose(TREE,FLAGS);
            docs.openedGate=new CountDownLatch(1);docs.releaseGate=new CountDownLatch(1);
            service.receive(peer,packet,message.files.get(0));assertTrue(docs.openedGate.await(5,TimeUnit.SECONDS));
            settle(service);assertEquals("保存中 · 点击取消",service.progress(peer.id(),packet,0,false).state);
            service.reject(peer,packet);docs.releaseGate.countDown();waitFor(service,()->!service.busy());settle(service);
            ChatService.FileProgress p=service.progress(peer.id(),packet,0,false);assertNotNull(p);
            assertEquals("保存中拒绝但已落盘的文件不能停在保存中","已保存",p.state);assertEquals(100,p.percent);
            assertTrue(storage.exists(target));
            LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),packet,false);assertNotNull(stored);assertEquals(1,stored.files.size());
        }finally{controller.destroy();}
    }
    @Test public void deletePartialsAllHistory()throws Exception {
        for(boolean clear:new boolean[]{false,true}) {
            ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
            try {
                settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"history","host","历史联系人","");peer.deviceId=clear?"3".repeat(32):"4".repeat(32);
                LanNode.Message message=interrupted(service,peer,902);ChatStore store=(ChatStore)field(service,"store");
                ((ExecutorService)field(service,"io")).submit(()->{for(int i=0;i<250;i++){LanNode.Message newer=new LanNode.Message();newer.number=1000+i;newer.time=100+i;newer.text="较新历史";newer.state="已收到";store.message(peer,newer,false,true);}}).get(5,TimeUnit.SECONDS);
                assertFalse("测试应超过最近 200 条历史页",store.history(peer.id()).stream().anyMatch(row->row.number==message.number));
                File saved=service.receivedFile(peer,message.number,message.files.get(0)),part=LanNode.partialFile(peer,message.number,message.files.get(1),service.receivedFile(peer,message.number,message.files.get(1)));
                File other=new File(part.getParentFile(),"other-conversation.part");Files.write(other.toPath(),new byte[]{9});List<Boolean> result=new ArrayList<>();
                if(clear)service.removeConversation(peer.id(),true,false,result::add);else service.deleteMessages(peer.id(),Collections.singletonList(message),result::add);
                settle(service);assertEquals(Collections.singletonList(true),result);assertNull(store.findMessage(peer.id(),message.number,false));assertFalse(part.exists());assertTrue(other.isFile());assertArrayEquals(new byte[]{1,2,3},Files.readAllBytes(saved.toPath()));
                if(clear)assertTrue(store.history(peer.id()).isEmpty());else assertEquals(200,store.history(peer.id()).size());
            } finally {controller.destroy();}
        }
    }
    @Test public void liveCancelCleans()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();List<String> errors=new ArrayList<>();
        LanNode.Listener incoming=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){service.incoming(peer,message,update);}public void offerCanceled(LanNode.Peer peer,long packet){service.offerCanceled(peer,packet);}};
        LanNode.Listener quiet=new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};
        try(LanNode sender=new LanNode(port(),true,"live-sender","host","发送方","",quiet);LanNode receiver=new LanNode(port(),true,"live-receiver","host","接收方","",incoming)) {
            settle(service);service.observe(new ChatService.Observer(){public void changed(){}public void error(String text){errors.add(text);}});service.getSharedPreferences("settings",0).edit().putBoolean(ReceivedStorage.AUTO,false).commit();set(service,"node",receiver);
            sender.setDeviceId("5".repeat(32));receiver.setDeviceId("6".repeat(32));sender.start();receiver.start();sender.probe(LanNode.endpoint("127.0.0.1:"+receiver.port()));waitFor(service,()->sender.peers().size()==1&&receiver.peers().size()==1);
            File small=source(service,"完成附件"),large=new File(service.getFilesDir(),"large-cancel.bin");byte[] block=new byte[1024*1024];new Random(73).nextBytes(block);
            try(OutputStream output=new FileOutputStream(large)){for(int i=0;i<64;i++)output.write(block);}
            LanNode.Peer target=sender.peers().get(0),peer=receiver.peers().get(0);LanNode.Message sent=sender.sendMessage(target,"同批取消",Arrays.asList(small,large));
            waitFor(service,()->((Map<?,?>)uncheckedField(service,"waiting")).size()==2);service.receive(peer,sent.number,sent.files.get(0));File saved=service.receivedFile(peer,sent.number,sent.files.get(0));waitFor(service,()->saved.isFile()&&!service.busy());
            service.receive(peer,sent.number,sent.files.get(1));File part=LanNode.partialFile(peer,sent.number,sent.files.get(1),service.receivedFile(peer,sent.number,sent.files.get(1)));
            waitFor(service,()->part.length()>0&&service.busy());assertTrue("必须在真实流仍活跃时取消",sender.hasActiveOutgoingTransfer(target.id(),sent.number));assertTrue(sender.cancelOutgoing(target.id(),sent.number,"已送达",true));
            waitFor(service,()->!service.busy()&&!part.exists()&&service.progress(peer.id(),sent.number,1,false)!=null&&service.progress(peer.id(),sent.number,1,false).state.equals("对方已取消发送"));
            LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),sent.number,false);assertEquals("对方已取消发送",stored.state);assertEquals(1,stored.files.size());assertEquals(0,stored.files.get(0).id);
            assertArrayEquals(Files.readAllBytes(small.toPath()),Files.readAllBytes(saved.toPath()));assertEquals("已保存",service.progress(peer.id(),sent.number,0,false).state);assertTrue(((Map<?,?>)field(service,"waiting")).isEmpty());
            assertFalse("取消不应弹接收失败提示："+errors,errors.stream().anyMatch(text->text.contains("传输")||text.contains("中断")));
            settle(service);assertEquals("对方已取消发送",service.progress(peer.id(),sent.number,1,false).state);
        } finally {controller.destroy();}
    }
    @Test public void cancelKeepsSavedHistory()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create();ChatService service=controller.get();
        try {
            settle(service);LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+port()),"canceled","host","取消发送方","");peer.deviceId="7".repeat(32);
            LanNode.Message message=interrupted(service,peer,903);File saved=service.receivedFile(peer,message.number,message.files.get(0)),part=LanNode.partialFile(peer,message.number,message.files.get(1),service.receivedFile(peer,message.number,message.files.get(1)));
            service.offerCanceled(peer,message.number);settle(service);service.offerCanceled(peer,message.number);settle(service);
            LanNode.Message stored=((ChatStore)field(service,"store")).findMessage(peer.id(),message.number,false);assertEquals("对方已取消发送",stored.state);assertEquals(1,stored.files.size());assertFalse(part.exists());assertArrayEquals(new byte[]{1,2,3},Files.readAllBytes(saved.toPath()));assertEquals("已保存",service.progress(peer.id(),message.number,0,false).state);
            assertEquals(1,stored.text.split("\\[对方已取消发送\\]",-1).length-1);assertEquals("对方已取消发送",((ChatStore)field(service,"store")).transfers().stream().filter(state->state.key.equals(ChatService.fileKey(peer.id(),message.number,1,false))).findFirst().orElseThrow().state);
        } finally {controller.destroy();}
    }
    private static List<View> views(View parent) { List<View> result=new ArrayList<>(); result.add(parent); if(parent instanceof ViewGroup) for(int i=0;i<((ViewGroup)parent).getChildCount();i++) result.addAll(views(((ViewGroup)parent).getChildAt(i))); return result; }
    private static TextView label(Dialog dialog,String text) { return (TextView)views(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof TextView&&((TextView)v).getText().toString().equals(text)).findFirst().orElseThrow(AssertionError::new); }
    @Test public void fileLongPressMenusReadGrantsInputActionsAndFolderPicker()throws Exception {
        ServiceController<ChatService> sc=Robolectric.buildService(ChatService.class).create(); ChatService service=sc.get(); ActivityController<MainActivity> ac=null;
        try {
            settle(service); LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"ui-file","host","文件联系人","测试");
            LanNode.Message message=new LanNode.Message(); message.number=17; message.time=System.currentTimeMillis(); message.text="附件"; message.state="已收到"; message.files=Collections.singletonList(new Protocol.Attachment(0,"资料.txt",3,0));
            File cached=service.receivedFile(peer,message.number,message.files.get(0)); Files.write(cached.toPath(),new byte[]{1,2,3}); service.incoming(peer,message,false); settle(service);
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).create().start().resume().visible(); MainActivity activity=ac.get(); settle(service);
            assertEquals("飞Q",((TextView)field(activity,"title")).getText().toString());
            Method open=MainActivity.class.getDeclaredMethod("open",String.class); open.setAccessible(true); open.invoke(activity,peer.id()); settle(service);
            View attachment=views(activity.getWindow().getDecorView()).stream().filter(v->"资料.txt，文件操作".contentEquals(v.getContentDescription()==null?"":v.getContentDescription())).findFirst().orElseThrow(AssertionError::new);
            assertTrue(attachment.performLongClick());Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog();assertNotNull(label(dialog,"查看保存位置"));assertFalse(views(dialog.getWindow().getDecorView()).stream().anyMatch(v->v instanceof TextView&&Arrays.asList("另存为…","保存到相册","保存到设置目录 / 重试").contains(((TextView)v).getText().toString())));label(dialog,"打开文件").performClick();
            Intent view=shadowOf(activity).getNextStartedActivity(); assertEquals(Intent.ACTION_VIEW,view.getAction()); assertEquals("content",view.getData().getScheme()); assertEquals(FLAGS&Intent.FLAG_GRANT_READ_URI_PERMISSION,view.getFlags()&FLAGS); assertNotNull(view.getClipData());
            attachment.performLongClick(); label(org.robolectric.shadows.ShadowDialog.getLatestDialog(),"分享文件").performClick(); Intent chooser=shadowOf(activity).getNextStartedActivity(); assertEquals(Intent.ACTION_CHOOSER,chooser.getAction()); Intent share=chooser.getParcelableExtra(Intent.EXTRA_INTENT); assertEquals(Intent.ACTION_SEND,share.getAction()); assertNotNull(share.getClipData());
            EditText editor=(EditText)field(activity,"editor"); assertNotNull(editor.getCustomSelectionActionModeCallback()); assertNotNull(editor.getCustomInsertionActionModeCallback());
            editor.requestFocus(); assertTrue(editor.isFocused()); editor.setText("复制文本"); editor.setSelection(0,2); assertTrue(editor.onTextContextMenuItem(android.R.id.copy)); assertEquals("复制",activity.getSystemService(ClipboardManager.class).getPrimaryClip().getItemAt(0).getText().toString());
            editor.requestFocus(); editor.setSelection(0,2); assertTrue(editor.onTextContextMenuItem(android.R.id.cut)); assertEquals("文本",editor.getText().toString()); editor.setSelection(0); assertTrue(editor.onTextContextMenuItem(android.R.id.pasteAsPlainText)); assertEquals("复制文本",editor.getText().toString()); editor.onTextContextMenuItem(android.R.id.selectAll); assertEquals(editor.length(),editor.getSelectionEnd()-editor.getSelectionStart());
            Method settings=MainActivity.class.getDeclaredMethod("settings"); settings.setAccessible(true); settings.invoke(activity); Dialog config=org.robolectric.shadows.ShadowDialog.getLatestDialog();
            assertFalse(views(config.getWindow().getDecorView()).stream().anyMatch(v->v instanceof Switch&&"另存后清理应用内副本".contentEquals(((Switch)v).getText())));
            Switch automatic=(Switch)views(config.getWindow().getDecorView()).stream().filter(v->v instanceof Switch && "自动接收文件".contentEquals(v.getContentDescription())).findFirst().get(); assertTrue(automatic.isChecked()); automatic.setChecked(false);
            label(config,"选择保存文件夹").performClick(); var requested=shadowOf(activity).getNextStartedActivityForResult(); assertEquals(22,requested.requestCode); assertEquals(Intent.ACTION_OPEN_DOCUMENT_TREE,requested.intent.getAction()); assertEquals(FLAGS,requested.intent.getFlags()&FLAGS);
            provider(activity); activity.onActivityResult(22,Activity.RESULT_OK,new Intent().setData(TREE).addFlags(FLAGS)); assertEquals(TREE,new ReceivedStorage(activity).tree());
            label(config,"保存").performClick(); assertFalse(new ReceivedStorage(activity).automatic());
        } finally { if(ac!=null) ac.pause().stop().destroy(); sc.destroy(); }
    }
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Test public void mediaImageSamplingAndCorruptFallback()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); File file=File.createTempFile("preview-",".png",c.getCacheDir());
        android.graphics.Bitmap source=android.graphics.Bitmap.createBitmap(1600,1200,android.graphics.Bitmap.Config.ARGB_8888);
        try(OutputStream output=new FileOutputStream(file)){assertTrue(source.compress(android.graphics.Bitmap.CompressFormat.PNG,100,output));}source.recycle();
        android.graphics.Bitmap image=MediaPreview.image(c,Uri.fromFile(file),480);assertTrue(image.getWidth()<=480&&image.getHeight()<=480);image.recycle();
        Files.write(file.toPath(),new byte[]{1,2,3});fails(()->MediaPreview.image(c,Uri.fromFile(file),480));
        assertTrue(MediaPreview.supported("图片.png"));assertTrue(MediaPreview.video("视频.mp4"));assertFalse(MediaPreview.supported("程序.exe"));
    }
    private static void touch(MediaPreview.ZoomImage image,int action,int[] ids,float... xy) {
        MotionEvent.PointerProperties[] props=new MotionEvent.PointerProperties[ids.length]; MotionEvent.PointerCoords[] coords=new MotionEvent.PointerCoords[ids.length];
        for(int i=0;i<ids.length;i++) { props[i]=new MotionEvent.PointerProperties(); props[i].id=ids[i]; props[i].toolType=MotionEvent.TOOL_TYPE_FINGER; coords[i]=new MotionEvent.PointerCoords(); coords[i].x=xy[i*2]; coords[i].y=xy[i*2+1]; coords[i].pressure=1; coords[i].size=1; }
        MotionEvent event=MotionEvent.obtain(0,SystemClock.uptimeMillis(),action,ids.length,props,coords,0,0,1,1,0,0,InputDevice.SOURCE_TOUCHSCREEN,0);
        try { assertTrue(image.onTouchEvent(event)); } finally { event.recycle(); }
    }
    private static float[] matrix(MediaPreview.ZoomImage image) { float[] values=new float[9]; image.getImageMatrix().getValues(values); return values; }
    @org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
    @Test public void pinchPanAndPointerChangesKeepStableImageMatrix()throws Exception {
        MediaPreview.ZoomImage image=new MediaPreview.ZoomImage(RuntimeEnvironment.getApplication()); image.layout(0,0,400,400);
        android.graphics.Bitmap bitmap=android.graphics.Bitmap.createBitmap(400,400,android.graphics.Bitmap.Config.ARGB_8888);
        android.graphics.Canvas canvas=new android.graphics.Canvas(bitmap); android.graphics.Paint paint=new android.graphics.Paint();
        for(int y=0;y<8;y++) for(int x=0;x<8;x++) { paint.setColor((x+y)%2==0?0xff3390ec:0xffe3edf5); canvas.drawRect(x*50,y*50,x*50+50,y*50+50,paint); }
        image.setImageBitmap(bitmap);
        touch(image,MotionEvent.ACTION_DOWN,new int[]{7},100,200);
        touch(image,MotionEvent.ACTION_POINTER_DOWN|(1<<MotionEvent.ACTION_POINTER_INDEX_SHIFT),new int[]{7,9},100,200,300,200);
        touch(image,MotionEvent.ACTION_MOVE,new int[]{7,9},50,200,350,200);
        assertEquals(1.5f,matrix(image)[0],0.001f);
        for(int i=0;i<50;i++) { float[] before=matrix(image); touch(image,MotionEvent.ACTION_MOVE,new int[]{7,9},50,200,350,200); assertArrayEquals(before,matrix(image),0.001f); }
        touch(image,MotionEvent.ACTION_MOVE,new int[]{7,9},70,210,370,210);
        assertEquals(-80,matrix(image)[2],0.01f); assertEquals(-90,matrix(image)[5],0.01f);
        float[] before=matrix(image);
        touch(image,MotionEvent.ACTION_POINTER_UP,new int[]{7,9},70,210,370,210);
        touch(image,MotionEvent.ACTION_MOVE,new int[]{9},370,210); assertArrayEquals(before,matrix(image),0.001f);
        touch(image,MotionEvent.ACTION_MOVE,new int[]{9},360,200); assertEquals(before[2]-10,matrix(image)[2],0.01f);
        touch(image,MotionEvent.ACTION_POINTER_DOWN|(1<<MotionEvent.ACTION_POINTER_INDEX_SHIFT),new int[]{9,11},360,200,200,100);
        before=matrix(image); touch(image,MotionEvent.ACTION_MOVE,new int[]{9,11},360,200,200,100); assertArrayEquals(before,matrix(image),0.001f);
        touch(image,MotionEvent.ACTION_CANCEL,new int[]{9,11},360,200,200,100);
        touch(image,MotionEvent.ACTION_DOWN,new int[]{25},50,50); before=matrix(image); touch(image,MotionEvent.ACTION_MOVE,new int[]{25},50,50); assertArrayEquals(before,matrix(image),0.001f);
        assertEquals(1,image.getScaleX(),0); assertEquals(1,image.getScaleY(),0); assertEquals(0,image.getTranslationX(),0); assertEquals(0,image.getTranslationY(),0);
        android.graphics.Bitmap preview=android.graphics.Bitmap.createBitmap(400,400,android.graphics.Bitmap.Config.ARGB_8888); image.draw(new android.graphics.Canvas(preview));
        File folder=new File(System.getProperty("feiq.previewDir")); folder.mkdirs(); try(OutputStream out=new FileOutputStream(new File(folder,"android-zoom-gesture.png"))) { assertTrue(preview.compress(android.graphics.Bitmap.CompressFormat.PNG,100,out)); }
        image.setImageDrawable(null); bitmap.recycle(); preview.recycle();
    }
    @Test public void sharingProviderIsReadOnlyAndRejectsTraversal()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); ReceivedFileProvider provider=new ReceivedFileProvider(); ProviderInfo info=new ProviderInfo(); info.authority=c.getPackageName()+".received"; provider.attachInfo(c,info);
        File directory=new File(c.getFilesDir(),"received"); directory.mkdirs(); File file=new File(directory,String.join("",Collections.nCopies(64,"a"))); Files.write(file.toPath(),"分享内容".getBytes(StandardCharsets.UTF_8));
        Uri uri=ReceivedFileProvider.uri(c,file,"资料.txt"); try(Cursor cursor=provider.query(uri,null,null,null,null)) { assertTrue(cursor.moveToFirst()); assertEquals("资料.txt",cursor.getString(0)); }
        try(ParcelFileDescriptor fd=provider.openFile(uri,"r")) { assertEquals(file.length(),fd.getStatSize()); }
        fails(()->provider.openFile(uri,"w")); fails(()->provider.openFile(Uri.parse("content://"+info.authority+"/../settings.xml"),"r"));
        fails(()->provider.openFile(Uri.parse("content://other/"+file.getName()+"/资料.txt"),"r")); assertTrue(file.isFile());
    }
}
