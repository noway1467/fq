package org.feiqlight.android;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.DocumentsContract;
import org.feiqlight.android.core.LanNode;
import java.io.*;

final class DocumentSource implements LanNode.Source {
    private final ContentResolver resolver;
    private final Uri uri,grant;
    private final String name;
    private final long size,modified;
    DocumentSource(Context context,Uri uri)throws IOException {
        this(context,uri,uri);
    }
    DocumentSource(Context context,Uri uri,Uri grant)throws IOException {
        resolver=context.getContentResolver();this.uri=uri;this.grant=grant;
        if(uri==null||!"content".equals(uri.getScheme())||!DocumentsContract.isDocumentUri(context,uri))throw new IOException("来源不是可恢复的系统文档");
        if(!uri.equals(grant)&&(!DocumentsContract.isTreeUri(grant)||!DocumentsContract.isTreeUri(uri)||!java.util.Objects.equals(uri.getAuthority(),grant.getAuthority())||!DocumentsContract.getTreeDocumentId(uri).equals(DocumentsContract.getTreeDocumentId(grant))))throw new IOException("文件不在授权目录内");
        boolean granted=false;for(UriPermission permission:resolver.getPersistedUriPermissions())if(permission.isReadPermission()&&permission.getUri().equals(grant))granted=true;
        if(!granted)throw new IOException("来源没有持久只读授权");
        try(Cursor cursor=resolver.query(uri,new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_SIZE,DocumentsContract.Document.COLUMN_LAST_MODIFIED},null,null)) {
            if(cursor==null||!cursor.moveToFirst()||cursor.isNull(1)||cursor.isNull(2))throw new IOException("来源未提供稳定的大小和修改时间");name=cursor.getString(0);size=cursor.getLong(1);modified=cursor.getLong(2);if(size<0||modified<=0)throw new IOException("来源元数据不稳定");
        }catch(RuntimeException e){throw new IOException("来源权限或元数据不可用",e);}
        try(InputStream ignored=open(0)){}
    }
    public String key(){return uri.toString();}public String name(){return name;}public long size(){return size;}public long modified(){return modified;}
    void check()throws IOException {
        boolean granted=false;
        for(UriPermission permission:resolver.getPersistedUriPermissions())if(permission.isReadPermission()&&permission.getUri().equals(grant))granted=true;
        if(!granted)throw new IOException("文件来源权限已失效");
        try(Cursor cursor=resolver.query(uri,new String[]{DocumentsContract.Document.COLUMN_SIZE,DocumentsContract.Document.COLUMN_LAST_MODIFIED},null,null)){
            if(cursor==null||!cursor.moveToFirst()||cursor.isNull(0)||cursor.isNull(1)||cursor.getLong(0)!=size||cursor.getLong(1)!=modified)throw new IOException("文件来源已改变，请重新选择发送");
        }catch(RuntimeException error){throw new IOException("文件来源权限已失效",error);}
    }
    public InputStream open(long offset)throws IOException {
        check();
        ParcelFileDescriptor descriptor;
        try{descriptor=resolver.openFileDescriptor(uri,"r");}
        catch(RuntimeException error){throw new IOException("文件来源权限已失效",error);}
        if(descriptor==null)throw new IOException("文件来源不可读");ParcelFileDescriptor.AutoCloseInputStream input=new ParcelFileDescriptor.AutoCloseInputStream(descriptor);
        try{if(descriptor.getStatSize()!=size)throw new IOException("文件大小已改变");input.getChannel().position(offset);return input;}catch(IOException|RuntimeException e){input.close();throw new IOException("文件来源不支持续传或已变化",e);}
    }
}
