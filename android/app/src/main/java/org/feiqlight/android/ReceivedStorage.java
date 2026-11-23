package org.feiqlight.android;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import android.webkit.MimeTypeMap;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;
import java.io.*;
import java.util.*;

/** SAF 保存与历史位置映射；只创建新文档，失败时保留应用内完整副本。 */
final class ReceivedStorage {
    static final String TREE="receive_tree", AUTO="auto_receive";
    static final String CLEAN_EXPORTED="clean_exported_copy";
    private final Context context;
    ReceivedStorage(Context context) { this.context=context; }
    SharedPreferences settings() { return context.getSharedPreferences("settings",Context.MODE_PRIVATE); }
    private SharedPreferences locations() { return context.getSharedPreferences("received_locations",Context.MODE_PRIVATE); }
    boolean automatic() { return settings().getBoolean(AUTO,true); }
    Uri tree() { String value=settings().getString(TREE,""); return value.isEmpty()?null:Uri.parse(value); }
    Uri saved(File file) { String value=locations().getString(file.getName(),""); return value.isEmpty()?null:Uri.parse(value); }
    Uri savedTree(File file) { String value=locations().getString(file.getName()+".tree",""); return value.isEmpty()?null:Uri.parse(value); }
    String folderLabel() { Uri tree=tree(); return tree==null?"应用内接收文件夹（无需存储授权）":DocumentsContract.getTreeDocumentId(tree); }
    void choose(Uri uri,int flags) throws IOException {
        if(uri==null||!"content".equals(uri.getScheme())||!DocumentsContract.isTreeUri(uri)) throw new IOException("请选择系统提供的文件夹");
        int permission=Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION;
        if((flags&permission)!=permission) throw new IOException("保存文件夹需要读取和写入授权");
        // 授权失败不能替换原位置；旧目录权限保留给已保存的历史附件。
        context.getContentResolver().takePersistableUriPermission(uri,permission);
        Uri document=DocumentsContract.buildDocumentUriUsingTree(uri,DocumentsContract.getTreeDocumentId(uri));
        try(Cursor cursor=context.getContentResolver().query(document,new String[]{DocumentsContract.Document.COLUMN_MIME_TYPE,DocumentsContract.Document.COLUMN_FLAGS},null,null)) {
            if(cursor==null||!cursor.moveToFirst()||!DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(0))||(cursor.getLong(1)&DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE)==0) throw new IOException("所选目录不可写，请选择其他文件夹");
        }
        settings().edit().putString(TREE,uri.toString()).apply();
    }
    void useInternal() { settings().edit().remove(TREE).apply(); }
    void delete(File file) throws IOException {
        File received=new File(context.getFilesDir(),"received").getCanonicalFile();
        if(!received.equals(file.getCanonicalFile().getParentFile())||file.isDirectory())throw new IOException("只能删除已接收的单个文件");
        Uri document=saved(file),folder=savedTree(file);
        if(document!=null) {
            if(folder==null||!Objects.equals(folder.getAuthority(),document.getAuthority())||!DocumentsContract.isDocumentUri(context,document))throw new IOException("保存位置无效，未删除文件");
            ContentResolver resolver=context.getContentResolver();
            try(Cursor cursor=resolver.query(document,new String[]{DocumentsContract.Document.COLUMN_MIME_TYPE},null,null)) {
                if(cursor==null)throw new IOException("无法检查保存目录中的文件，请重新授权后重试");
                if(cursor.moveToFirst()) {
                    if(DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(0)))throw new IOException("不能通过附件入口删除文件夹");
                    if(!DocumentsContract.deleteDocument(resolver,document))throw new IOException("保存目录中的文件未能删除，应用内副本已保留");
                }
            }catch(SecurityException e){throw new IOException("保存目录授权已失效，应用内副本已保留，请重新授权后重试",e);}
            if(!locations().edit().remove(file.getName()).remove(file.getName()+".tree").commit())throw new IOException("保存目录中的文件已删除，但位置记录未能更新，请重试");
        }
        java.nio.file.Files.deleteIfExists(file.toPath());
    }
    static String mime(String name) {
        String lower=name.toLowerCase(Locale.ROOT);
        if(lower.endsWith(".apk"))return "application/vnd.android.package-archive";
        if(lower.endsWith(".jpg")||lower.endsWith(".jpeg"))return "image/jpeg";
        if(lower.endsWith(".png"))return "image/png";
        if(lower.endsWith(".webp"))return "image/webp";
        if(lower.endsWith(".heic"))return "image/heic";
        if(lower.endsWith(".pdf"))return "application/pdf";
        int dot=name.lastIndexOf('.'); String value=dot<0?null:MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substring(dot+1).toLowerCase(Locale.ROOT)); return value==null?"application/octet-stream":value; }
    Uri save(File file,String name,LanNode.Transfer task) throws IOException {
        return save(file,name,null,task);
    }
    Uri save(File file,String name,String relativePath,LanNode.Transfer task) throws IOException {
        Uri tree=tree(); if(tree==null) return null;
        if(!Protocol.safeName(name)) throw new IOException("不安全的文件名");
        if(!file.isFile()) throw new IOException("应用内文件不可用");
        ContentResolver resolver=context.getContentResolver(); Uri created=null;
        try {
            check(task); String parent=DocumentsContract.getTreeDocumentId(tree);
            if(relativePath!=null){if(!Protocol.safeRelativePath(relativePath))throw new IOException("目录结构无效");String[] parts=relativePath.split("/");for(int i=0;i<parts.length-1;i++)parent=directory(resolver,tree,parent,parts[i],task);}
            Set<String> names=new HashSet<>(), ids=new HashSet<>();
            Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(tree,parent);
            try(Cursor cursor=resolver.query(children,new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME},null,null)) {
                if(cursor==null) throw new IOException("无法读取保存目录，请重新选择文件夹");
                while(cursor.moveToNext()) { check(task); ids.add(cursor.getString(0)); names.add(cursor.getString(1).toLowerCase(Locale.ROOT)); if(ids.size()>10000) throw new IOException("目录文件过多，请选择其他文件夹"); }
            }
            Uri old=saved(file);
            if(tree.equals(savedTree(file))&&old!=null&&ids.contains(DocumentsContract.getDocumentId(old))) return old;
            String candidate=name; int dot=name.lastIndexOf('.'); String stem=dot>0?name.substring(0,dot):name, extension=dot>0?name.substring(dot):"";
            for(int i=1;names.contains(candidate.toLowerCase(Locale.ROOT));i++) candidate=stem+" ("+i+")"+extension;
            check(task);
            Uri result=DocumentsContract.createDocument(resolver,DocumentsContract.buildDocumentUriUsingTree(tree,parent),mime(name),candidate);
            if(result==null) throw new IOException("无法在所选目录创建文件");
            if(!tree.getAuthority().equals(result.getAuthority())||parent.equals(DocumentsContract.getDocumentId(result))||ids.contains(DocumentsContract.getDocumentId(result))) throw new IOException("文件提供方返回了已有文档，已拒绝覆盖");
            created=result;
            try(InputStream input=new FileInputStream(file); OutputStream output=resolver.openOutputStream(created,"w")) {
                if(output==null) throw new IOException("无法写入所选目录"); byte[] buffer=new byte[65536]; int count;
                while((count=input.read(buffer))!=-1) { check(task); output.write(buffer,0,count); }
            }
            check(task); if(!locations().edit().putString(file.getName(),created.toString()).putString(file.getName()+".tree",tree.toString()).commit())throw new IOException("保存位置未能记录，应用内副本保留");return created;
        } catch(Exception e) {
            boolean cleaned=created==null;
            if(created!=null) try { cleaned=DocumentsContract.deleteDocument(resolver,created); } catch(Exception ignored) { }
            throw new IOException((task.isCancelled()?"保存已取消":"保存到所选目录失败，请检查授权或重新选择目录")+"；应用内完整文件保留，可重试或另存为。"+(cleaned?"":"目标目录可能留下不完整副本。"),e);
        }
    }
    void cleanExported(File file,LanNode.Transfer task)throws IOException {
        if(!settings().getBoolean(CLEAN_EXPORTED,false)||!file.isFile())return;
        Uri destination=saved(file);if(destination==null)return;
        File received=new File(context.getFilesDir(),"received").getCanonicalFile();
        if(!received.equals(file.getCanonicalFile().getParentFile()))throw new IOException("只能清理应用内接收副本");
        try {
            java.security.MessageDigest digest=java.security.MessageDigest.getInstance("SHA-256");long size=0;
            try(InputStream input=context.getContentResolver().openInputStream(destination)) {
                if(input==null)throw new IOException("另存文件不可读");byte[] buffer=new byte[65536];int count;
                while((count=input.read(buffer))!=-1){check(task);size+=count;if(size>file.length())throw new IOException("另存文件长度不一致");digest.update(buffer,0,count);}
            }
            byte[] expected;try(InputStream input=new FileInputStream(file)) {java.security.MessageDigest original=java.security.MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))!=-1){check(task);original.update(buffer,0,count);}expected=original.digest();}
            if(size!=file.length()||!java.util.Arrays.equals(expected,digest.digest()))throw new IOException("另存文件校验不一致，已保留内部副本");
            check(task);java.nio.file.Files.delete(file.toPath());
        }catch(java.security.NoSuchAlgorithmException e){throw new IllegalStateException(e);}
    }
    @android.annotation.TargetApi(29)
    Uri gallery(File file,String name,LanNode.Transfer task)throws IOException {
        String type=mime(name);if(!type.startsWith("image/")&&!type.startsWith("video/"))throw new IOException("仅图片和视频可保存到相册");
        ContentResolver resolver=context.getContentResolver();ContentValues values=new ContentValues();values.put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME,name);values.put(android.provider.MediaStore.MediaColumns.MIME_TYPE,type);values.put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH,type.startsWith("video/")?"Movies/飞Q":"Pictures/飞Q");values.put(android.provider.MediaStore.MediaColumns.IS_PENDING,1);
        Uri entry=resolver.insert(type.startsWith("video/")?android.provider.MediaStore.Video.Media.EXTERNAL_CONTENT_URI:android.provider.MediaStore.Images.Media.EXTERNAL_CONTENT_URI,values);
        if(entry==null)throw new IOException("无法在相册创建文件");
        try {
            try(InputStream input=new FileInputStream(file);OutputStream output=resolver.openOutputStream(entry,"w")){if(output==null)throw new IOException("相册不可写");byte[] buffer=new byte[65536];int count;while((count=input.read(buffer))!=-1){check(task);output.write(buffer,0,count);}}
            check(task);ContentValues ready=new ContentValues();ready.put(android.provider.MediaStore.MediaColumns.IS_PENDING,0);if(resolver.update(entry,ready,null,null)!=1)throw new IOException("相册文件未发布");return entry;
        }catch(IOException|RuntimeException e){try{resolver.delete(entry,null,null);}catch(RuntimeException cleanup){e.addSuppressed(cleanup);}throw e;}
    }
    private static String directory(ContentResolver resolver,Uri tree,String parent,String name,LanNode.Transfer task)throws IOException {
        check(task);String found=null;
        try(Cursor children=resolver.query(DocumentsContract.buildChildDocumentsUriUsingTree(tree,parent),new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME,DocumentsContract.Document.COLUMN_MIME_TYPE},null,null)) {
            if(children==null)throw new IOException("目录不可读");int count=0;while(children.moveToNext()){if(++count>10000)throw new IOException("目录文件过多");if(name.equalsIgnoreCase(children.getString(1))){if(!DocumentsContract.Document.MIME_TYPE_DIR.equals(children.getString(2)))throw new IOException("同名文件阻止创建目录");found=children.getString(0);}}
        }
        if(found!=null)return found;Uri created=DocumentsContract.createDocument(resolver,DocumentsContract.buildDocumentUriUsingTree(tree,parent),DocumentsContract.Document.MIME_TYPE_DIR,name);
        if(created==null||!tree.getAuthority().equals(created.getAuthority())||parent.equals(DocumentsContract.getDocumentId(created)))throw new IOException("无法创建接收目录");return DocumentsContract.getDocumentId(created);
    }
    private static void check(LanNode.Transfer task) throws IOException { if(task.isCancelled()||Thread.currentThread().isInterrupted()) throw new IOException("文件任务已取消"); }
}
