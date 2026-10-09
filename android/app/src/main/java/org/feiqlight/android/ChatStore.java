package org.feiqlight.android;

import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.DatabaseUtils;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.HashMap;
import java.util.Map;
import java.util.Locale;

/** 数据库只在 ChatService 的单线程队列使用，避免网络回调与界面写入乱序。 */
final class ChatStore extends SQLiteOpenHelper {
    static final class Conversation {
        LanNode.Peer peer;
        String preview;
        long time;
        int unread;
        String note="";
        boolean pinned, hidden;
        long number;
        boolean duplicateName;
        String baseDisplayName() { return displayBase(peer.name,note); }
        String nameSuffix() { return duplicateName?" · #"+number:""; }
        String displayName() { return baseDisplayName()+nameSuffix(); }
    }
    private static String displayBase(String name,String note) {return note==null||note.trim().isEmpty()?(name==null?"":name):note;}
    ChatStore(Context context) { super(context,"chat.db",null,5); }
    @Override public void onCreate(SQLiteDatabase db) {
        db.execSQL("CREATE TABLE peers (id TEXT PRIMARY KEY, endpoint TEXT, login TEXT, host TEXT, name TEXT, grp TEXT, preview TEXT DEFAULT '', time INTEGER DEFAULT 0, unread INTEGER DEFAULT 0, utf8 INTEGER DEFAULT 0, note TEXT DEFAULT '', pinned INTEGER DEFAULT 0, hidden INTEGER DEFAULT 0, conversation_number INTEGER NOT NULL DEFAULT 0)");
        db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY, peer TEXT, packet INTEGER, outgoing INTEGER, time INTEGER, text TEXT, state TEXT, files TEXT, local_files TEXT DEFAULT '[]', UNIQUE(peer,packet,outgoing))");
        db.execSQL("CREATE INDEX messages_peer_time ON messages(peer,time)");
    }
    @Override public void onUpgrade(SQLiteDatabase db,int oldVersion,int newVersion) {
        if(oldVersion<2) db.execSQL("ALTER TABLE peers ADD COLUMN utf8 INTEGER DEFAULT 0");
        if(oldVersion<3) db.execSQL("ALTER TABLE messages ADD COLUMN local_files TEXT DEFAULT '[]'");
        if(oldVersion<4) { db.execSQL("ALTER TABLE peers ADD COLUMN note TEXT DEFAULT ''"); db.execSQL("ALTER TABLE peers ADD COLUMN pinned INTEGER DEFAULT 0"); db.execSQL("ALTER TABLE peers ADD COLUMN hidden INTEGER DEFAULT 0"); }
        // 只补本地显示编号，不改联系人主键、消息归属或原有备注。
        if(oldVersion<5) { db.execSQL("ALTER TABLE peers ADD COLUMN conversation_number INTEGER NOT NULL DEFAULT 0"); db.execSQL("UPDATE peers SET conversation_number=rowid"); }
    }
    void peer(LanNode.Peer p) {
        ContentValues v=new ContentValues(); v.put("id",p.id()); v.put("endpoint",p.endpoint.getAddress().getHostAddress()+":"+p.endpoint.getPort());
        v.put("login",p.login); v.put("host",p.host); v.put("name",p.name); v.put("grp",p.group); v.put("utf8",p.utf8?1:0);
        SQLiteDatabase db=getWritableDatabase(); if (db.update("peers",v,"id=?",new String[]{p.id()})==0) {
            v.put("conversation_number",DatabaseUtils.longForQuery(db,"SELECT COALESCE(MAX(conversation_number),0)+1 FROM peers",null));db.insertOrThrow("peers",null,v);
        }
    }
    void peers(List<LanNode.Peer> peers) {
        // 一批发现只提交一次事务，避免联系人数量放大磁盘同步延迟。
        SQLiteDatabase db=getWritableDatabase();db.beginTransaction();
        try {for(LanNode.Peer peer:peers)peer(peer);db.setTransactionSuccessful();}finally{db.endTransaction();}
    }
    boolean message(LanNode.Peer p,LanNode.Message m,boolean update,boolean visible) {
        SQLiteDatabase db=getWritableDatabase(); db.beginTransaction();
        try {
            peer(p); ContentValues v=new ContentValues(); v.put("peer",p.id()); v.put("packet",m.number); v.put("outgoing",m.outgoing?1:0);
            v.put("time",m.time); v.put("text",m.text); v.put("state",m.state); v.put("files",Protocol.files(m.files));
            v.put("local_files",new org.json.JSONArray(m.localPaths).toString());
            // REPLACE 会删除旧行再分配 id，导致同毫秒消息在 ACK 后换序；原位更新保持排序稳定。
            String[] key={p.id(),Long.toString(m.number),m.outgoing?"1":"0"}; boolean inserted;
            try(Cursor existing=db.rawQuery("SELECT id FROM messages WHERE peer=? AND packet=? AND outgoing=? LIMIT 1",key)) { inserted=!existing.moveToFirst(); }
            // 已清空的消息不能被迟到的 ACK / 文件状态回调复活。
            if(inserted && update) { db.setTransactionSuccessful(); return false; }
            if(inserted) db.insertOrThrow("messages",null,v);
            else if(update) db.update("messages",v,"peer=? AND packet=? AND outgoing=?",key);
            // 网络去重表会过期；持久层也必须幂等，重复旧消息不能抬高未读或倒退最近预览。
            if (!update && inserted) {
                String preview=m.text.isEmpty() && !m.files.isEmpty()?"[文件] "+m.files.get(0).name:m.text;
                db.execSQL("UPDATE peers SET preview=?, time=?, unread=unread+?, hidden=0 WHERE id=?",new Object[]{preview,m.time,(!m.outgoing&&!visible)?1:0,p.id()});
            }
            // 本版保留每个联系人最近 500 条、全局最近 10000 条，不伪装成无限归档。
            db.execSQL("DELETE FROM messages WHERE peer=? AND id NOT IN (SELECT id FROM messages WHERE peer=? ORDER BY time DESC,id DESC LIMIT 500)",new Object[]{p.id(),p.id()});
            db.execSQL("DELETE FROM messages WHERE id NOT IN (SELECT id FROM messages ORDER BY time DESC,id DESC LIMIT 10000)");
            db.setTransactionSuccessful();
            return inserted;
        } finally { db.endTransaction(); }
    }
    void recoverPending() { getWritableDatabase().execSQL("UPDATE messages SET state='上次退出，未确认送达' WHERE state='等待确认'"); }
    void read(String id) { getWritableDatabase().execSQL("UPDATE peers SET unread=0 WHERE id=?",new Object[]{id}); }
    void edit(String id,String note,boolean pinned) {
        ContentValues v=new ContentValues();v.put("note",note.trim());v.put("pinned",pinned?1:0);getWritableDatabase().update("peers",v,"id=?",new String[]{id});
    }
    void remove(String id,boolean clear,boolean hide) {
        SQLiteDatabase db=getWritableDatabase();db.beginTransaction();
        try {
            if(clear) { db.delete("messages","peer=?",new String[]{id});db.execSQL("UPDATE peers SET preview='',time=0 WHERE id=?",new Object[]{id}); }
            db.execSQL("UPDATE peers SET hidden=?,unread=0 WHERE id=?",new Object[]{hide?1:0,id});db.setTransactionSuccessful();
        } finally {db.endTransaction();}
    }
    List<Conversation> conversations() {
        List<Conversation> result=new ArrayList<>();
        try (Cursor c=getReadableDatabase().rawQuery("SELECT endpoint,login,host,name,grp,preview,time,unread,utf8,note,pinned,hidden,conversation_number FROM peers ORDER BY pinned DESC,time DESC,name LIMIT 1000",null)) {
            while (c.moveToNext()) try {
                Conversation item=new Conversation(); item.peer=new LanNode.Peer(LanNode.endpoint(c.getString(0)),c.getString(1),c.getString(2),c.getString(3),c.getString(4));
                item.preview=c.getString(5); item.time=c.getLong(6); item.unread=c.getInt(7); item.peer.utf8=c.getInt(8)!=0; item.note=c.getString(9);item.pinned=c.getInt(10)!=0;item.hidden=c.getInt(11)!=0;item.number=c.getLong(12);result.add(item);
            } catch (IOException ignored) { }
        }
        // 重名判断不能被列表的 1000 条上限截断，否则旧会话进出窗口会让编号反复消失。
        Map<String,Integer> counts=new HashMap<>();
        try(Cursor c=getReadableDatabase().rawQuery("SELECT name,note FROM peers",null)) {
            while(c.moveToNext()) {String name=displayBase(c.getString(0),c.getString(1)).trim().toLowerCase(Locale.ROOT);counts.put(name,counts.getOrDefault(name,0)+1);}
        }
        for(Conversation item:result)item.duplicateName=counts.get(item.baseDisplayName().trim().toLowerCase(Locale.ROOT))>1;
        return result;
    }
    List<LanNode.Message> history(String peer) {
        List<LanNode.Message> result=new ArrayList<>();
        try (Cursor c=getReadableDatabase().rawQuery("SELECT packet,outgoing,time,text,state,files,local_files FROM (SELECT * FROM messages WHERE peer=? ORDER BY time DESC,id DESC LIMIT 200) ORDER BY time,id",new String[]{peer})) {
            while(c.moveToNext()) { LanNode.Message m=new LanNode.Message(); m.number=c.getLong(0); m.outgoing=c.getInt(1)!=0; m.time=c.getLong(2); m.text=c.getString(3); m.state=c.getString(4); m.files=Protocol.parseStoredFiles(c.getString(5));
                if(m.outgoing)try {org.json.JSONArray paths=new org.json.JSONArray(c.getString(6));for(int i=0;i<paths.length();i++)m.localPaths.add(paths.getString(i));}catch(org.json.JSONException ignored){m.localPaths.clear();}
                result.add(m); }
        } return result;
    }
}
