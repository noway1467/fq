package org.feiqlight.android;

import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import android.webkit.MimeTypeMap;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;
import java.io.*;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.security.MessageDigest;
import java.util.*;

/** 每次接收只保留一份正式文件；SAF 提交失败时保留应用内完整文件。 */
final class ReceivedStorage {
    static final String TREE="receive_tree", AUTO="auto_receive";
    private final Context context;
    ReceivedStorage(Context context) { this.context=context; }
    SharedPreferences settings() { return context.getSharedPreferences("settings",Context.MODE_PRIVATE); }
    private SharedPreferences locations() { return context.getSharedPreferences("received_locations",Context.MODE_PRIVATE); }
    boolean automatic() { return settings().getBoolean(AUTO,true); }
    Uri tree() { String value=settings().getString(TREE,""); return value.isEmpty()?null:Uri.parse(value); }
    Uri saved(File file) { String value=locations().getString(file.getName(),""); return value.isEmpty()?null:Uri.parse(value); }
    Uri savedTree(File file) { String value=locations().getString(file.getName()+".tree",""); return value.isEmpty()?null:Uri.parse(value); }
    Uri existingSaved(File file) {
        Uri document=saved(file);if(document==null)return null;
        try {
            Uri folder=savedTree(file);
            if(folder==null||!"content".equals(document.getScheme())||!Objects.equals(folder.getAuthority(),document.getAuthority())||!DocumentsContract.isDocumentUri(context,document))throw new IllegalArgumentException("保存位置无效");
            try(Cursor cursor=context.getContentResolver().query(document,new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_MIME_TYPE},null,null)) {
                if(cursor!=null&&cursor.moveToFirst()&&!DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(1)))return document;
            }
        } catch(SecurityException e) {
            // 暂时失去授权不等于文件已删除，保留位置以便重新授权后恢复访问。
            return null;
        } catch(Exception e) {
            if(!(e instanceof IllegalArgumentException)&&!(e instanceof FileNotFoundException)&&!(e.getCause() instanceof FileNotFoundException))throw new IllegalStateException("无法检查已接收文件",e);
        }
        forget(file);return null;
    }
    boolean exists(File file) { return file.isFile()||existingSaved(file)!=null; }
    private void forget(File file) {
        String key=file.getName();locations().edit().remove(key).remove(key+".tree").remove(key+".size").remove(key+".name").apply();
    }
    List<File> receivedFiles() throws IOException {
        File directory=new File(context.getFilesDir(),"received");Map<String,File> files=new LinkedHashMap<>();
        File[] internal=directory.listFiles();
        if(internal==null&&directory.exists())throw new IOException("无法读取应用内接收文件");
        if(internal!=null)for(File file:internal)if(!file.getName().endsWith(".part")&&Files.isRegularFile(file.toPath(),LinkOption.NOFOLLOW_LINKS))files.put(file.getName(),file);
        // 只使用飞Q登记的单文件位置，不遍历用户选择的保存目录。
        for(Map.Entry<String,?> entry:locations().getAll().entrySet()) {
            String key=entry.getKey();
            if(!Protocol.safeName(key)||key.endsWith(".part")||key.endsWith(".tree")||key.endsWith(".size")||key.endsWith(".name")||!(entry.getValue() instanceof String)||!((String)entry.getValue()).startsWith("content://"))continue;
            File file=new File(directory,key);if(!file.isDirectory())files.putIfAbsent(key,file);
        }
        return new ArrayList<>(files.values());
    }
    long bytes(File file) throws IOException {
        long internal=file.isFile()?file.length():0;Uri document=existingSaved(file);if(document==null)return internal;
        try(Cursor cursor=context.getContentResolver().query(document,new String[]{DocumentsContract.Document.COLUMN_SIZE},null,null)) {
            if(cursor==null||!cursor.moveToFirst()){forget(file);return internal;}
            int column=cursor.getColumnIndex(DocumentsContract.Document.COLUMN_SIZE);
            long external=column>=0&&!cursor.isNull(column)?cursor.getLong(column):locations().getLong(file.getName()+".size",0);
            return internal+Math.max(0,external);
        } catch(SecurityException e) {return internal;}
        catch(Exception e) {
            if(e instanceof IllegalArgumentException||e instanceof FileNotFoundException||e.getCause() instanceof FileNotFoundException){forget(file);return internal;}
            throw new IOException("无法读取已接收文件大小",e);
        }
    }
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
    private void checkReceivedFile(File file) throws IOException {
        File received=new File(context.getFilesDir(),"received").getCanonicalFile();
        if(!received.equals(file.getCanonicalFile().getParentFile())||file.isDirectory()||file.getName().endsWith(".part"))throw new IOException("只能操作应用内接收区的正式文件");
    }
    void delete(File file) throws IOException {
        checkReceivedFile(file);
        Uri document=saved(file),folder=savedTree(file);
        if(document!=null) {
            if(folder==null||!Objects.equals(folder.getAuthority(),document.getAuthority())||!DocumentsContract.isDocumentUri(context,document))throw new IOException("保存位置无效，未删除文件");
            ContentResolver resolver=context.getContentResolver();boolean found=false;
            try(Cursor cursor=resolver.query(document,new String[]{DocumentsContract.Document.COLUMN_MIME_TYPE},null,null)) {
                if(cursor!=null&&cursor.moveToFirst()) {
                    found=true;
                    if(DocumentsContract.Document.MIME_TYPE_DIR.equals(cursor.getString(0)))throw new IOException("不能通过附件入口删除文件夹");
                    if(!DocumentsContract.deleteDocument(resolver,document))throw new IOException("保存目录中的文件未能删除，请重试");
                }
            } catch(SecurityException e){throw new IOException("保存目录授权已失效，文件未删除，请重新授权后重试",e);}
            catch(Exception e) {
                if(found||(!(e instanceof IllegalArgumentException)&&!(e instanceof FileNotFoundException)&&!(e.getCause() instanceof FileNotFoundException)))throw new IOException("保存目录中的文件未能删除："+e.getMessage(),e);
            }
            String key=file.getName();
            if(!locations().edit().remove(key).remove(key+".tree").remove(key+".size").remove(key+".name").commit())throw new IOException("保存目录中的文件已删除，但位置记录未能更新，请重试");
        }
        Files.deleteIfExists(file.toPath());
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
        checkReceivedFile(file);
        if(!Protocol.safeName(name)) throw new IOException("不安全的文件名");
        ContentResolver resolver=context.getContentResolver(); Uri created=null;
        try {
            check(task);Uri old=existingSaved(file);
            if(old!=null) {
                if(file.isFile()){verify(file,old,task);check(task);Files.delete(file.toPath());}
                return old;
            }
            if(saved(file)!=null)throw new IOException("原保存位置暂时不可读，请恢复目录授权");
            if(!file.isFile())throw new IOException("应用内文件不可用");
            check(task); String parent=DocumentsContract.getTreeDocumentId(tree);
            if(relativePath!=null){if(!Protocol.safeRelativePath(relativePath))throw new IOException("目录结构无效");String[] parts=relativePath.split("/");for(int i=0;i<parts.length-1;i++)parent=directory(resolver,tree,parent,parts[i],task);}
            Set<String> names=new HashSet<>(), ids=new HashSet<>();
            Uri children=DocumentsContract.buildChildDocumentsUriUsingTree(tree,parent);
            try(Cursor cursor=resolver.query(children,new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,DocumentsContract.Document.COLUMN_DISPLAY_NAME},null,null)) {
                if(cursor==null) throw new IOException("无法读取保存目录，请重新选择文件夹");
                while(cursor.moveToNext()) { check(task); ids.add(cursor.getString(0)); names.add(cursor.getString(1).toLowerCase(Locale.ROOT)); if(ids.size()>10000) throw new IOException("目录文件过多，请选择其他文件夹"); }
            }
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
            verify(file,created,task);check(task);
            String key=file.getName();
            if(!locations().edit().putString(key,created.toString()).putString(key+".tree",tree.toString()).putLong(key+".size",file.length()).putString(key+".name",candidate).commit())throw new IOException("保存位置未能记录");
            check(task);Files.delete(file.toPath());return created;
        } catch(Exception e) {
            boolean cleaned=created==null;
            if(created!=null) {
                try { cleaned=DocumentsContract.deleteDocument(resolver,created); } catch(Exception cleanup) {e.addSuppressed(cleanup);}
                if(created.equals(saved(file)))forget(file);
            }
            throw new IOException((task.isCancelled()?"保存已取消":"保存目录不可用")+"，文件已保存在应用内接收区。"+(cleaned?"":"目标目录可能留下不完整副本。")+" "+e.getMessage(),e);
        }
    }
    private void verify(File file,Uri destination,LanNode.Transfer task)throws IOException {
        try {
            MessageDigest original=MessageDigest.getInstance("SHA-256"),saved=MessageDigest.getInstance("SHA-256");byte[] buffer=new byte[65536];int count;long size=0;
            try(InputStream input=new FileInputStream(file)) {
                while((count=input.read(buffer))!=-1){check(task);original.update(buffer,0,count);}
            }
            try(InputStream input=context.getContentResolver().openInputStream(destination)) {
                if(input==null)throw new IOException("保存文件不可读");
                while((count=input.read(buffer))!=-1){check(task);size+=count;if(size>file.length())throw new IOException("保存文件长度不一致");saved.update(buffer,0,count);}
            }
            if(size!=file.length()||!Arrays.equals(original.digest(),saved.digest()))throw new IOException("保存文件校验不一致");
        }catch(java.security.NoSuchAlgorithmException e){throw new IllegalStateException(e);}
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
