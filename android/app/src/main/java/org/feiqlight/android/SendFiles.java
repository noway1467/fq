package org.feiqlight.android;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import org.feiqlight.android.core.Protocol;
import java.io.IOException;
import java.util.*;

final class SendFiles {
    static final class Entry {final Uri uri;final String relative;Entry(Uri uri,String relative){this.uri=uri;this.relative=relative;}}
    static List<Entry> folder(Context context,Uri tree)throws IOException {
        if(tree==null||!"content".equals(tree.getScheme())||!DocumentsContract.isTreeUri(tree))throw new IOException("请选择系统授权的文件夹");
        String id=DocumentsContract.getTreeDocumentId(tree),name;
        try(Cursor cursor=context.getContentResolver().query(DocumentsContract.buildDocumentUriUsingTree(tree,id),new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME},null,null)){if(cursor==null||!cursor.moveToFirst())throw new IOException("文件夹不可读");name=cursor.getString(0);}
        List<Entry> files=new ArrayList<>();walk(context.getContentResolver(),tree,id,name,files,new HashSet<>());if(files.isEmpty())throw new IOException("文件夹没有可发送的文件");return files;
    }
    private static void walk(ContentResolver resolver,Uri tree,String parent,String relative,List<Entry> files,Set<String> visited)throws IOException {
        if(!Protocol.safeRelativePath(relative)||!visited.add(parent))throw new IOException("文件夹包含循环或不安全路径");
        try(Cursor cursor=resolver.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree,parent),new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE},null,null)){
            if(cursor==null)throw new IOException("文件夹不可读");while(cursor.moveToNext()){
                String path=relative+"/"+cursor.getString(1),id=cursor.getString(0);if(!Protocol.safeRelativePath(path))throw new IOException("文件夹层级或名称不安全");
                if(DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(2)))walk(resolver,tree,id,path,files,visited);
                else {if(files.size()>=1000)throw new IOException("单批最多 1000 个文件，请分批发送");files.add(new Entry(DocumentsContract.buildDocumentUriUsingTree(tree,id),path));}
            }
        }
    }
}
