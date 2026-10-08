package org.feiqlight.android;

import android.content.Context;
import android.content.SharedPreferences;

/** 草稿写入与发送后的条件清理集中在此，旧 Activity 不能回写新页面的编辑。 */
final class DraftStore {
    private static final Object LOCK=new Object();
    private final SharedPreferences prefs;
    static final class Snapshot {
        final String text;
        final long revision;
        Snapshot(String text,long revision) { this.text=text; this.revision=revision; }
    }
    DraftStore(Context context) { prefs=context.getApplicationContext().getSharedPreferences("drafts",Context.MODE_PRIVATE); }
    private static String versionKey(String peer) { return "@revision/"+peer; }
    Snapshot snapshot(String peer) {
        synchronized(LOCK) { return new Snapshot(prefs.getString(peer,""),prefs.getLong(versionKey(peer),0)); }
    }
    String read(String peer) { return snapshot(peer).text; }
    void save(String peer,String text) {
        synchronized(LOCK) {
            Snapshot old=snapshot(peer); if(old.text.equals(text)) return;
            // 同一份 preferences 原子更新正文和版本；旧版只有正文时版本默认为 0。
            prefs.edit().putString(peer,text).putLong(versionKey(peer),old.revision+1).apply();
        }
    }
    boolean clearIfUnchanged(String peer,Snapshot sent) {
        synchronized(LOCK) {
            Snapshot now=snapshot(peer);
            if(now.revision!=sent.revision || !now.text.equals(sent.text)) return false;
            prefs.edit().remove(peer).putLong(versionKey(peer),now.revision+1).apply(); return true;
        }
    }
}
