package org.feiqlight.android;

import android.content.*;
import android.net.Uri;
import android.provider.DocumentsContract;
import java.io.IOException;
import java.util.*;
import org.json.*;

/** 使用稳定的应用私有设置和 SAF 只读授权，不把短期会话当作可恢复设置。 */
final class SharedFolderSettings {
    private final Context context;
    private final SharedPreferences preferences;
    SharedFolderSettings(Context context){this.context=context.getApplicationContext();preferences=this.context.getSharedPreferences("shared_folder",Context.MODE_PRIVATE);}
    Uri tree(){String value=preferences.getString("tree","");return value.isEmpty()?null:Uri.parse(value);}
    List<Uri> trees() throws IOException {
        if(!preferences.contains("trees"))return tree()==null?new ArrayList<>():new ArrayList<>(Collections.singletonList(tree()));
        try {
            JSONArray stored=new JSONArray(preferences.getString("trees","[]"));List<Uri> result=new ArrayList<>();
            for(int i=0;i<stored.length();i++){Uri uri=Uri.parse(stored.getString(i));if(!result.contains(uri))result.add(uri);}
            if(result.size()>20)throw new IOException("共享文件夹不能超过 20 个");return result;
        }catch(JSONException e){throw new IOException("共享目录设置损坏，未重置原设置",e);}
    }
    boolean enabled(){return preferences.getBoolean("enabled",false);}
    void acquire(Uri uri,int flags)throws IOException{
        if(uri==null||!DocumentsContract.isTreeUri(uri)||(flags&Intent.FLAG_GRANT_READ_URI_PERMISSION)==0)throw new IOException("请选择有读取授权的文件夹");
        if((flags&Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)==0)throw new IOException("此文件来源不支持保存授权，请选择本地文件夹");
        try{context.getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);}
        catch(SecurityException e){throw new IOException("文件夹授权未保存，请重新选择",e);}
        requireGrant(uri);
    }
    void requireGrant(Uri uri)throws IOException{
        for(UriPermission permission:context.getContentResolver().getPersistedUriPermissions())if(permission.getUri().equals(uri)&&permission.isReadPermission())return;
        throw new IOException("共享目录授权已失效，请重新选择原文件夹");
    }
    void save(Uri uri)throws IOException{
        save(Collections.singletonList(uri),true);
    }
    void save(List<Uri> folders,boolean enabled)throws IOException {
        List<Uri> unique=new ArrayList<>(new LinkedHashSet<>(folders));
        if(unique.size()>20)throw new IOException("最多共享 20 个文件夹");
        JSONArray values=new JSONArray();for(Uri uri:unique){requireGrant(uri);values.put(uri.toString());}
        if(!preferences.edit().putString("trees",values.toString()).putString("tree",unique.isEmpty()?"":unique.get(0).toString()).putBoolean("enabled",enabled&&!unique.isEmpty()).commit())throw new IOException("共享目录设置未保存");
    }
    void stop()throws IOException{if(!preferences.edit().putBoolean("enabled",false).commit())throw new IOException("停止共享设置未保存");}
}
