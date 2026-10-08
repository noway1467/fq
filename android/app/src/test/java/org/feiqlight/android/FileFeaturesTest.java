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
    @Test public void multiPickerAcceptsClipOnlyDeduplicatesAndRejectsInvalidItems() {
        Uri a=Uri.parse("content://feiq.documents/a"),b=Uri.parse("content://feiq.documents/b");
        ClipData clip=ClipData.newRawUri("文件",a);clip.addItem(new ClipData.Item(b));clip.addItem(new ClipData.Item(a));
        Intent selection=new Intent();selection.setClipData(clip);
        assertEquals(Arrays.asList(a,b),MainActivity.selectedFiles(selection));
        assertEquals(Collections.singletonList(a),MainActivity.selectedFiles(new Intent().setData(a)));
        try {MainActivity.selectedFiles(new Intent().setData(Uri.parse("file:///private")));fail();}catch(IllegalArgumentException expected){}
        for(int i=3;i<21;i++)clip.addItem(new ClipData.Item(a));
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
    private static int port()throws Exception { try(ServerSocket tcp=new ServerSocket(0,1,InetAddress.getLoopbackAddress()); DatagramSocket udp=new DatagramSocket(tcp.getLocalPort(),InetAddress.getLoopbackAddress())) { return tcp.getLocalPort(); } }
    private static File source(Context c,String text)throws Exception { File f=File.createTempFile("received-",".txt",c.getCacheDir()); Files.write(f.toPath(),text.getBytes(StandardCharsets.UTF_8)); return f; }
    private static void fails(Throwing action)throws Exception { try { action.run(); fail("操作应当被拒绝"); } catch(IOException|SecurityException expected) { } }
    private interface Throwing { void run()throws Exception; }
    public static final class Docs extends DocumentsProvider {
        final Map<String,File> files=new LinkedHashMap<>(); final Map<String,String> names=new LinkedHashMap<>(); int sequence; boolean deny, failOpen, reuse, refuseDelete;
        @Override public boolean onCreate() { return true; }
        private void check() { if(deny) throw new SecurityException("授权已撤销"); }
        private String[] columns(String[] p) { return p==null?new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_FLAGS}:p; }
        private void row(MatrixCursor c,String id) { Object[] data=new Object[c.getColumnCount()]; String[] cols=c.getColumnNames(); for(int i=0;i<cols.length;i++) {
            if(cols[i].equals(DocumentsContract.Document.COLUMN_DOCUMENT_ID)) data[i]=id;
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_DISPLAY_NAME)) data[i]=id.equals("root")?"测试接收目录":names.get(id);
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_MIME_TYPE)) data[i]=id.equals("root")?DocumentsContract.Document.MIME_TYPE_DIR:"text/plain";
            else if(cols[i].equals(DocumentsContract.Document.COLUMN_FLAGS)) data[i]=id.equals("root")?DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE:DocumentsContract.Document.FLAG_SUPPORTS_WRITE;
        } c.addRow(data); }
        @Override public Cursor queryRoots(String[] p) { return new MatrixCursor(new String[]{DocumentsContract.Root.COLUMN_ROOT_ID}); }
        @Override public Cursor queryDocument(String id,String[] p)throws FileNotFoundException { check(); if(!id.equals("root")&&!files.containsKey(id)) throw new FileNotFoundException(); MatrixCursor c=new MatrixCursor(columns(p)); row(c,id); return c; }
        @Override public Cursor queryChildDocuments(String parent,String[] p,String sort) { check(); MatrixCursor c=new MatrixCursor(columns(p)); for(String id:files.keySet()) row(c,id); return c; }
        @Override public boolean isChildDocument(String parent,String child) { return parent.equals("root")&&files.containsKey(child); }
        @Override public String createDocument(String parent,String mime,String name)throws FileNotFoundException { check(); if(reuse) return files.keySet().iterator().next(); try { String id="file"+(++sequence); File f=File.createTempFile("doc-",".tmp",getContext().getCacheDir()); files.put(id,f); names.put(id,name); return id; } catch(IOException e) { throw new FileNotFoundException(); } }
        @Override public void deleteDocument(String id)throws FileNotFoundException { if(refuseDelete) throw new FileNotFoundException(); File file=files.remove(id); names.remove(id); if(file!=null) file.delete(); }
        @Override public ParcelFileDescriptor openDocument(String id,String mode,CancellationSignal signal)throws FileNotFoundException { check(); if(failOpen) throw new FileNotFoundException("写入失败"); return ParcelFileDescriptor.open(files.get(id),ParcelFileDescriptor.parseMode(mode)); }
    }
    private static Docs provider(Context context) {
        Docs docs=new Docs(); ProviderInfo info=new ProviderInfo(); info.authority="feiq.documents"; info.exported=true; info.grantUriPermissions=true; info.readPermission=info.writePermission="android.permission.MANAGE_DOCUMENTS"; docs.attachInfo(context,info); ShadowContentResolver.registerProviderInternal(info.authority,docs); return docs;
    }
    @Test public void directoryGrantPersistsAndSameNamesNeverOverwrite()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); Docs docs=provider(c); ReceivedStorage storage=new ReceivedStorage(c);
        assertTrue(storage.automatic()); assertNull(storage.tree()); storage.choose(TREE,FLAGS); assertEquals(TREE,new ReceivedStorage(c).tree());
        String old=docs.createDocument("root","text/plain","报告.txt"); Files.write(docs.files.get(old).toPath(),"原文件".getBytes(StandardCharsets.UTF_8));
        File file=source(c,"新文件"); Uri saved=storage.save(file,"报告.txt",new LanNode.Transfer());
        assertEquals("报告 (1).txt",docs.names.get(DocumentsContract.getDocumentId(saved))); assertEquals("原文件",new String(Files.readAllBytes(docs.files.get(old).toPath()),StandardCharsets.UTF_8));
        assertEquals("新文件",new String(Files.readAllBytes(docs.files.get(DocumentsContract.getDocumentId(saved)).toPath()),StandardCharsets.UTF_8));
        assertEquals(saved,new ReceivedStorage(c).saved(file)); assertEquals(saved,storage.save(file,"报告.txt",new LanNode.Transfer())); assertEquals(2,docs.files.size());
        storage.useInternal(); assertNull(storage.tree()); assertEquals(saved,storage.saved(file)); assertNull(storage.save(file,"报告.txt",new LanNode.Transfer()));
    }
    @Test public void revokedPermissionAndFailedWriteKeepPrivateCopy()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); Docs docs=provider(c); ReceivedStorage storage=new ReceivedStorage(c); storage.choose(TREE,FLAGS); File file=source(c,"保留完整副本");
        fails(()->storage.choose(TREE,Intent.FLAG_GRANT_READ_URI_PERMISSION)); assertEquals(TREE,storage.tree());
        docs.deny=true; fails(()->storage.save(file,"资料.txt",new LanNode.Transfer())); assertTrue(file.isFile()); assertNull(storage.saved(file));
        docs.deny=false; docs.failOpen=true; fails(()->storage.save(file,"资料.txt",new LanNode.Transfer())); assertTrue(docs.files.isEmpty()); assertTrue(file.isFile());
        docs.failOpen=false; assertNotNull(storage.save(file,"资料.txt",new LanNode.Transfer()));
    }
    @Test public void cancellationAndProviderReturningExistingDocumentAreSafe()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); Docs docs=provider(c); ReceivedStorage storage=new ReceivedStorage(c); storage.choose(TREE,FLAGS); File file=source(c,"新内容");
        LanNode.Transfer cancelled=new LanNode.Transfer(); cancelled.close(); fails(()->storage.save(file,"文件.txt",cancelled)); assertTrue(docs.files.isEmpty());
        String old=docs.createDocument("root","text/plain","旧文件.txt"); Files.write(docs.files.get(old).toPath(),"不可覆盖".getBytes(StandardCharsets.UTF_8)); docs.reuse=true;
        fails(()->storage.save(file,"新名字.txt",new LanNode.Transfer())); assertEquals(1,docs.files.size()); assertEquals("不可覆盖",new String(Files.readAllBytes(docs.files.get(old).toPath()),StandardCharsets.UTF_8));
    }
    @Test public void incompleteExternalDocumentIsExplicitWhenProviderRefusesCleanup()throws Exception {
        Context c=RuntimeEnvironment.getApplication(); Docs docs=provider(c); ReceivedStorage storage=new ReceivedStorage(c); storage.choose(TREE,FLAGS); docs.failOpen=docs.refuseDelete=true; File file=source(c,"完整内容");
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
            File local=service.receivedFile(source,message.number,file);assertTrue(local.getParentFile().isDirectory()||local.getParentFile().mkdirs());Files.write(local.toPath(),bytes);storage.save(local,file.name,new LanNode.Transfer());Files.delete(local.toPath());
            List<Boolean> result=new ArrayList<>();service.forward(source,message,target,result::add);waitFor(service,()->!result.isEmpty());assertEquals(Collections.singletonList(true),result);
            assertEquals(1,docs.files.size());assertArrayEquals(bytes,Files.readAllBytes(docs.files.values().iterator().next().toPath()));
            File offers=new File(service.getFilesDir(),"offers");File[] folders=offers.listFiles();assertNotNull(folders);assertEquals(1,folders.length);assertArrayEquals(bytes,Files.readAllBytes(new File(folders[0],file.name).toPath()));
        }finally{controller.destroy();}
    }
    @Test public void backgroundBatchReceiveSwitchAndDirectoryFailureRetry()throws Exception {
        ServiceController<ChatService> controller=Robolectric.buildService(ChatService.class).create(); ChatService service=controller.get();
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
            docs.deny=false; service.saveToFolder(cached,one.getName()); waitFor(service,()->storage.saved(cached)!=null&&!service.busy()); assertTrue(cached.isFile());
            storage.settings().edit().putBoolean(ReceivedStorage.AUTO,false).apply();
            LanNode.Message partial=sender.sendMessage(sender.peers().get(0),"部分接收后拒绝剩余",Arrays.asList(one,empty));
            waitFor(service,()->((Map<?,?>)uncheckedField(service,"waiting")).size()==2);
            service.receive(remote,partial.number,partial.files.get(0)); File kept=service.receivedFile(remote,partial.number,partial.files.get(0)); waitFor(service,()->kept.isFile()&&!service.busy());
            service.reject(remote,partial.number); settle(service);
            LanNode.Message record=((ChatStore)field(service,"store")).history(remote.id()).stream().filter(m->m.number==partial.number).findFirst().get();
            assertEquals(1,record.files.size()); assertTrue(kept.isFile()); assertTrue(((Map<?,?>)field(service,"waiting")).isEmpty());
            LanNode.Message failed=new LanNode.Message();failed.number=98765;failed.time=System.currentTimeMillis();failed.outgoing=true;failed.text="失败重发测试";failed.state="未确认送达，请检查网络后重发";
            ((ExecutorService)field(service,"io")).submit(()->((ChatStore)uncheckedField(service,"store")).message(remote,failed,false,true)).get(5,TimeUnit.SECONDS);
            java.util.List<Boolean> outcomes=new ArrayList<>();service.retryMessage(remote,failed,outcomes::add);waitFor(service,()->outcomes.size()==1);assertTrue(outcomes.get(0));
            service.retryMessage(remote,failed,outcomes::add);waitFor(service,()->outcomes.size()==2);assertFalse(outcomes.get(1));
            assertEquals("已重新发送（新消息）",((ChatStore)field(service,"store")).history(remote.id()).stream().filter(m->m.number==98765).findFirst().get().state);
        } finally { controller.destroy(); }
    }
    private static Object uncheckedField(Object o,String name) { try { return field(o,name); } catch(Exception e) { throw new AssertionError(e); } }
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
            assertTrue(attachment.performLongClick()); Dialog dialog=org.robolectric.shadows.ShadowDialog.getLatestDialog(); assertNotNull(label(dialog,"查看保存位置")); assertNotNull(label(dialog,"另存为…")); label(dialog,"打开文件").performClick();
            Intent view=shadowOf(activity).getNextStartedActivity(); assertEquals(Intent.ACTION_VIEW,view.getAction()); assertEquals("content",view.getData().getScheme()); assertEquals(FLAGS&Intent.FLAG_GRANT_READ_URI_PERMISSION,view.getFlags()&FLAGS); assertNotNull(view.getClipData());
            attachment.performLongClick(); label(org.robolectric.shadows.ShadowDialog.getLatestDialog(),"分享文件").performClick(); Intent chooser=shadowOf(activity).getNextStartedActivity(); assertEquals(Intent.ACTION_CHOOSER,chooser.getAction()); Intent share=chooser.getParcelableExtra(Intent.EXTRA_INTENT); assertEquals(Intent.ACTION_SEND,share.getAction()); assertNotNull(share.getClipData());
            EditText editor=(EditText)field(activity,"editor"); assertNotNull(editor.getCustomSelectionActionModeCallback()); assertNotNull(editor.getCustomInsertionActionModeCallback());
            editor.requestFocus(); assertTrue(editor.isFocused()); editor.setText("复制文本"); editor.setSelection(0,2); assertTrue(editor.onTextContextMenuItem(android.R.id.copy)); assertEquals("复制",activity.getSystemService(ClipboardManager.class).getPrimaryClip().getItemAt(0).getText().toString());
            editor.requestFocus(); editor.setSelection(0,2); assertTrue(editor.onTextContextMenuItem(android.R.id.cut)); assertEquals("文本",editor.getText().toString()); editor.setSelection(0); assertTrue(editor.onTextContextMenuItem(android.R.id.pasteAsPlainText)); assertEquals("复制文本",editor.getText().toString()); editor.onTextContextMenuItem(android.R.id.selectAll); assertEquals(editor.length(),editor.getSelectionEnd()-editor.getSelectionStart());
            Method settings=MainActivity.class.getDeclaredMethod("settings"); settings.setAccessible(true); settings.invoke(activity); Dialog config=org.robolectric.shadows.ShadowDialog.getLatestDialog();
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
