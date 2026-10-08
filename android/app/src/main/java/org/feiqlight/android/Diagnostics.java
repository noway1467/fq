package org.feiqlight.android;

import android.content.Context;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.time.Instant;

/** 白名单事件码：禁止传入异常原文、消息、URI、IP、联系人及文件名。 */
final class Diagnostics {
    enum Event { Connecting, Connected, Disconnected, NetworkChanged, Error, TaskStarted, TaskCompleted, TaskFailed, TaskCancelled, Stopped }
    static final int LIMIT=65536;
    private static final Object GATE=new Object();
    private final Context context;
    private final File current,previous;
    Diagnostics(Context context) {this.context=context.getApplicationContext();File dir=new File(context.getFilesDir(),"diagnostics");current=new File(dir,"events.log");previous=new File(dir,"previous.log");}
    void record(Event event) {
        if(!context.getSharedPreferences("settings",0).getBoolean("diagnostics",false))return;
        synchronized(GATE) {try {
            byte[] line=(Instant.now()+" "+event.name()+"\n").getBytes(StandardCharsets.UTF_8);
            Files.createDirectories(current.getParentFile().toPath());
            if(current.length()+line.length>LIMIT) {Files.deleteIfExists(previous.toPath());Files.move(current.toPath(),previous.toPath());}
            try(OutputStream out=new FileOutputStream(current,true)){out.write(line);}
        }catch(IOException|SecurityException ignored){ /* 诊断不可用不能中断通信。 */ }}
    }
    String read() throws IOException {
        synchronized(GATE) {
            StringBuilder text=new StringBuilder("飞Q "+BuildConfig.VERSION_NAME+" / Android "+android.os.Build.VERSION.SDK_INT+" / UTC\n");
            for(File file:new File[]{previous,current}) if(file.isFile()) {if(file.length()>LIMIT)throw new IOException("诊断文件超过读取限额");text.append(new String(Files.readAllBytes(file.toPath()),StandardCharsets.UTF_8));}
            return text.toString();
        }
    }
    void clear() throws IOException {synchronized(GATE){Files.deleteIfExists(current.toPath());Files.deleteIfExists(previous.toPath());}}
}
