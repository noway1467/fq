package org.feiqlight.android;

import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.*;
import java.util.*;

/** 仅按单个 URI 临时授予接收/发送副本的只读权限，不暴露私有目录。 */
public final class ReceivedFileProvider extends ContentProvider {
    static Uri uri(Context context,File file,String name) {
        try {
            File canonical=file.getCanonicalFile(),received=new File(context.getFilesDir(),"received").getCanonicalFile();
            Uri.Builder result=new Uri.Builder().scheme("content").authority(context.getPackageName()+".received");
            if(canonical.getParentFile().equals(received)&&canonical.getName().matches("[a-f0-9]{64}")) return result.appendPath(canonical.getName()).appendPath(name).build();
            File offers=new File(context.getFilesDir(),"offers").getCanonicalFile(),folder=canonical.getParentFile();
            if(folder!=null&&offers.equals(folder.getParentFile())&&folder.getName().matches("[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}")&&org.feiqlight.android.core.Protocol.safeName(canonical.getName()))
                return result.appendPath("offers").appendPath(folder.getName()).appendPath(canonical.getName()).build();
        } catch(IOException e) { throw new IllegalArgumentException("附件位置不可用",e); }
        throw new IllegalArgumentException("附件不在授权副本目录内");
    }
    private File file(Uri uri) throws FileNotFoundException {
        List<String> segments=uri.getPathSegments();
        if(!"content".equals(uri.getScheme())||!(getContext().getPackageName()+".received").equals(uri.getAuthority())) throw new FileNotFoundException("无效的附件地址");
        File directory,file;
        if(segments.size()==2&&segments.get(0).matches("[a-f0-9]{64}")) {directory=new File(getContext().getFilesDir(),"received");file=new File(directory,segments.get(0));}
        else if(segments.size()==3&&segments.get(0).equals("offers")&&segments.get(1).matches("[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}")&&org.feiqlight.android.core.Protocol.safeName(segments.get(2))) {
            File offers=new File(getContext().getFilesDir(),"offers");directory=new File(offers,segments.get(1));file=new File(directory,segments.get(2));
            try {if(!directory.getCanonicalFile().getParentFile().equals(offers.getCanonicalFile())) throw new FileNotFoundException("附件目录越界");}
            catch(IOException e) {throw new FileNotFoundException("附件目录不可用");}
        } else throw new FileNotFoundException("无效的附件地址");
        try { if(!file.getCanonicalFile().getParentFile().equals(directory.getCanonicalFile())||!file.isFile()) throw new FileNotFoundException("接收文件不存在"); }
        catch(IOException e) { throw new FileNotFoundException("接收文件不可用"); } return file;
    }
    @Override public boolean onCreate() { return true; }
    @Override public ParcelFileDescriptor openFile(Uri uri,String mode) throws FileNotFoundException {
        if(!"r".equals(mode)) throw new FileNotFoundException("接收副本仅允许读取"); return ParcelFileDescriptor.open(file(uri),ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public String getType(Uri uri) { return ReceivedStorage.mime(uri.getLastPathSegment()==null?"":uri.getLastPathSegment()); }
    @Override public Cursor query(Uri uri,String[] projection,String selection,String[] args,String sort) {
        try {
            File file=file(uri); String[] columns=projection==null?new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}:projection;
            MatrixCursor cursor=new MatrixCursor(columns); Object[] row=new Object[columns.length];
            for(int i=0;i<columns.length;i++) { if(OpenableColumns.DISPLAY_NAME.equals(columns[i])) row[i]=uri.getLastPathSegment(); else if(OpenableColumns.SIZE.equals(columns[i])) row[i]=file.length(); } cursor.addRow(row); return cursor;
        } catch(FileNotFoundException e) { return null; }
    }
    @Override public Uri insert(Uri uri,ContentValues values) { throw new UnsupportedOperationException("只读"); }
    @Override public int delete(Uri uri,String selection,String[] args) { throw new UnsupportedOperationException("只读"); }
    @Override public int update(Uri uri,ContentValues values,String selection,String[] args) { throw new UnsupportedOperationException("只读"); }
}
