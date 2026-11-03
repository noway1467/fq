package org.feiqlight.android;

import android.content.Context;
import android.content.SharedPreferences;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.*;

final class ToolsPairings {
    private final SharedPreferences preferences;
    ToolsPairings(Context context){preferences=context.getSharedPreferences("tools_pairings",Context.MODE_PRIVATE);}
    private static String hash(String token){
        try{return Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-256").digest(token.getBytes(StandardCharsets.UTF_8)));}
        catch(NoSuchAlgorithmException e){throw new IllegalStateException(e);}
    }
    private static long until(String value){try{return Long.parseLong(value.substring(value.lastIndexOf('|')+1));}catch(NumberFormatException e){return 0;}}
    boolean contains(String token,String scope){
        if(token.length()!=64)return false;
        String value=preferences.getString(hash(token),"");
        return value.startsWith(scope+"|")&&value.substring(0,value.lastIndexOf('|')).equals(scope)&&until(value)>System.currentTimeMillis();
    }
    boolean alwaysAllows(String token,String scope){return contains(token,scope)&&preferences.getBoolean("allow:"+hash(token),false);}
    void allowAlways(String token)throws IOException{if(!preferences.edit().putBoolean("allow:"+hash(token),true).commit())throw new IOException("无法保存设备授权");}
    String remember(String token,String scope)throws IOException{
        if(contains(token,scope))return token;
        token=SharedFiles.random(32);SharedPreferences.Editor editor=preferences.edit();
        List<Map.Entry<String,?>> entries=new ArrayList<>(preferences.getAll().entrySet());entries.removeIf(e->e.getKey().startsWith("allow:"));
        entries.sort(Comparator.comparingLong(e->until(String.valueOf(e.getValue()))));
        for(int i=0;i<entries.size();i++)if(i<entries.size()-31||until(String.valueOf(entries.get(i).getValue()))<=System.currentTimeMillis()){editor.remove(entries.get(i).getKey());editor.remove("allow:"+entries.get(i).getKey());}
        if(!editor.putString(hash(token),scope+"|"+(System.currentTimeMillis()+7776000000L)).commit())throw new IOException("无法保存配对记录");
        return token;
    }
    void clear()throws IOException{if(!preferences.edit().clear().commit())throw new IOException("无法清除配对记录");}
}
