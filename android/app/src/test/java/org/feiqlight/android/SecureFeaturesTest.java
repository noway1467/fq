package org.feiqlight.android;

import android.content.*;
import android.content.pm.ProviderInfo;
import android.database.*;
import android.net.Uri;
import android.os.*;
import android.provider.DocumentsContract;
import android.provider.DocumentsProvider;
import java.io.*;
import java.nio.file.Files;
import java.util.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import org.robolectric.shadows.ShadowContentResolver;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=35)
public final class SecureFeaturesTest {
    public static final class Documents extends DocumentsProvider {
        File file;long modified=1000;
        @Override public boolean onCreate(){return true;}
        @Override public Cursor queryRoots(String[] projection){return new MatrixCursor(new String[]{DocumentsContract.Root.COLUMN_ROOT_ID});}
        @Override public Cursor queryDocument(String id,String[] projection){
            MatrixCursor result=new MatrixCursor(projection);Object[] row=new Object[projection.length];
            for(int i=0;i<projection.length;i++)switch(projection[i]){
                case DocumentsContract.Document.COLUMN_DOCUMENT_ID:row[i]=id;break;
                case DocumentsContract.Document.COLUMN_DISPLAY_NAME:row[i]="文档.txt";break;
                case DocumentsContract.Document.COLUMN_SIZE:row[i]=file.length();break;
                case DocumentsContract.Document.COLUMN_LAST_MODIFIED:row[i]=modified;break;
                case DocumentsContract.Document.COLUMN_MIME_TYPE:row[i]="text/plain";break;
                default:row[i]=0;
            }
            result.addRow(row);return result;
        }
        @Override public Cursor queryChildDocuments(String parent,String[] projection,String sort){return queryDocument("file",projection);}
        @Override public boolean isChildDocument(String parent,String child){return parent.equals("root")&&child.equals("file");}
        @Override public ParcelFileDescriptor openDocument(String id,String mode,CancellationSignal signal)throws FileNotFoundException{return ParcelFileDescriptor.open(file,ParcelFileDescriptor.MODE_READ_ONLY);}
    }
    @Test public void stableDocumentRequiresPersistentPermissionAndChecksChangedSource()throws Exception{
        Context context=RuntimeEnvironment.getApplication();Documents documents=new Documents();ProviderInfo info=new ProviderInfo();info.authority="secure.documents";info.exported=true;info.grantUriPermissions=true;info.readPermission=info.writePermission="android.permission.MANAGE_DOCUMENTS";documents.attachInfo(context,info);ShadowContentResolver.registerProviderInternal(info.authority,documents);
        android.content.pm.ResolveInfo resolved=new android.content.pm.ResolveInfo();resolved.providerInfo=info;Shadows.shadowOf(context.getPackageManager()).addResolveInfoForIntent(new Intent(DocumentsContract.PROVIDER_INTERFACE),resolved);
        documents.file=new File(context.getFilesDir(),"stable.txt");Files.write(documents.file.toPath(),new byte[]{1,2,3,4});Uri uri=DocumentsContract.buildDocumentUri(info.authority,"file");
        try{new DocumentSource(context,uri);fail("无持久权限也接受直接读取");}catch(IOException expected){}
        context.getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);
        DocumentSource source=new DocumentSource(context,uri);try(InputStream input=source.open(2)){assertEquals(3,input.read());assertEquals(4,input.read());assertEquals(-1,input.read());}
        Uri tree=DocumentsContract.buildTreeDocumentUri(info.authority,"root"),child=DocumentsContract.buildDocumentUriUsingTree(tree,"file");
        try{new DocumentSource(context,child,tree);fail("无目录权限也接受读取");}catch(IOException expected){}
        context.getContentResolver().takePersistableUriPermission(tree,Intent.FLAG_GRANT_READ_URI_PERMISSION);
        java.util.List<SendFiles.Entry> entries=SendFiles.folder(context,tree);assertEquals(1,entries.size());
        DocumentSource nested=new DocumentSource(context,entries.get(0).uri,tree);
        try(GuestTransfer guest=new GuestTransfer(context,java.util.Collections.singletonList(nested),java.util.Collections.singletonMap(nested.key(),entries.get(0).relative),false,true,null)){
            java.net.HttpURLConnection connection=(java.net.HttpURLConnection)new java.net.URL(guest.url("127.0.0.1").split("/#")[0]+"/api/guest").openConnection(java.net.Proxy.NO_PROXY);connection.setRequestProperty("X-FQ-Guest",guest.token);
            org.json.JSONObject entry;
            try(InputStream input=connection.getInputStream()){entry=new org.json.JSONObject(new String(input.readAllBytes(),java.nio.charset.StandardCharsets.UTF_8)).getJSONArray("files").getJSONObject(0);assertEquals("文档.txt/文档.txt",entry.getString("relativePath"));}finally{connection.disconnect();}
            connection=(java.net.HttpURLConnection)new java.net.URL("http://127.0.0.1:"+guest.port()+"/file?id="+entry.getString("id")).openConnection(java.net.Proxy.NO_PROXY);connection.setRequestProperty("X-FQ-Guest",guest.token);
            try(InputStream input=connection.getInputStream()){assertArrayEquals(new byte[]{1,2,3,4},input.readAllBytes());}finally{connection.disconnect();}
        }
        context.getContentResolver().releasePersistableUriPermission(tree,Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try{new DocumentSource(context,child,tree);fail("撤销目录授权仍允许新分享");}catch(IOException expected){}
        documents.modified++;try{source.open(0);fail("同大小、已改变的来源仍可读取");}catch(IOException expected){assertTrue(expected.getMessage().contains("改变"));}
    }
    @Test public void versionEightUpgradeAddsTransfersWithoutReplacingMessages()throws Exception{
        Context context=RuntimeEnvironment.getApplication();
        try(ChatStore store=new ChatStore(context)){android.database.sqlite.SQLiteDatabase db=store.getWritableDatabase();db.execSQL("DROP TABLE transfer_states");db.setVersion(8);db.execSQL("INSERT INTO messages(peer,packet,outgoing,time,text,state,files) VALUES('saved',1,0,1,'保留历史','已收到','')");}
        try(ChatStore store=new ChatStore(context)){assertEquals(9,store.getReadableDatabase().getVersion());assertEquals("保留历史",store.history("saved").get(0).text);store.transfer("saved/1/0/false","saved",1,50,"接收中");assertEquals(1,store.transfers().size());}
    }
    @Test public void rememberedToolsPairingCannotCrossCertificatesAndRevocationPersists()throws Exception{
        Context context=RuntimeEnvironment.getApplication();ToolsPairings pairings=new ToolsPairings(context);String fingerprint="a".repeat(64),scope="phone-remote|tls="+fingerprint;
        String token=pairings.remember("",scope);pairings.allowAlways(token);assertTrue(new ToolsPairings(context).alwaysAllows(token,scope));assertFalse(pairings.contains(token,"phone-remote|tls="+"b".repeat(64)));pairings.forgetFingerprint(fingerprint);assertFalse(new ToolsPairings(context).contains(token,scope));
    }
}
