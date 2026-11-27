package org.feiqlight.android;

import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.DatabaseUtils;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;
import org.feiqlight.android.core.SecureIdentity;
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
    ChatStore(Context context) { super(context,"chat.db",null,9); }
    @Override public void onCreate(SQLiteDatabase db) {
        db.execSQL("CREATE TABLE peers (id TEXT PRIMARY KEY, endpoint TEXT, login TEXT, host TEXT, name TEXT, grp TEXT, preview TEXT DEFAULT '', time INTEGER DEFAULT 0, unread INTEGER DEFAULT 0, utf8 INTEGER DEFAULT 0, note TEXT DEFAULT '', pinned INTEGER DEFAULT 0, hidden INTEGER DEFAULT 0, conversation_number INTEGER NOT NULL DEFAULT 0)");
        db.execSQL("CREATE TABLE messages (id INTEGER PRIMARY KEY, peer TEXT, packet INTEGER, outgoing INTEGER, time INTEGER, text TEXT, state TEXT, files TEXT, local_files TEXT DEFAULT '[]', UNIQUE(peer,packet,outgoing))");
        db.execSQL("CREATE INDEX messages_peer_time ON messages(peer,time)");
        db.execSQL("ALTER TABLE peers ADD COLUMN device_id TEXT");
        db.execSQL("ALTER TABLE peers ADD COLUMN avatar TEXT");
        db.execSQL("ALTER TABLE peers ADD COLUMN fingerprint TEXT");
        db.execSQL("CREATE TABLE deleted_messages(peer TEXT NOT NULL, packet INTEGER NOT NULL, outgoing INTEGER NOT NULL, PRIMARY KEY(peer,packet,outgoing))");
        createTransfers(db);
    }
    @Override public void onUpgrade(SQLiteDatabase db,int oldVersion,int newVersion) {
        if(oldVersion<2) db.execSQL("ALTER TABLE peers ADD COLUMN utf8 INTEGER DEFAULT 0");
        if(oldVersion<3) db.execSQL("ALTER TABLE messages ADD COLUMN local_files TEXT DEFAULT '[]'");
        if(oldVersion<4) { db.execSQL("ALTER TABLE peers ADD COLUMN note TEXT DEFAULT ''"); db.execSQL("ALTER TABLE peers ADD COLUMN pinned INTEGER DEFAULT 0"); db.execSQL("ALTER TABLE peers ADD COLUMN hidden INTEGER DEFAULT 0"); }
        // 只补本地显示编号，不改联系人主键、消息归属或原有备注。
        if(oldVersion<5) { db.execSQL("ALTER TABLE peers ADD COLUMN conversation_number INTEGER NOT NULL DEFAULT 0"); db.execSQL("UPDATE peers SET conversation_number=rowid"); }
        if(oldVersion<6) db.execSQL("ALTER TABLE peers ADD COLUMN device_id TEXT");
        if(oldVersion<7 && newVersion>=7) db.execSQL("ALTER TABLE peers ADD COLUMN avatar TEXT");
        if(oldVersion<8 && newVersion>=8) db.execSQL("ALTER TABLE peers ADD COLUMN fingerprint TEXT");
        if(oldVersion<9 && newVersion>=9)createTransfers(db);
        if(oldVersion<7 && newVersion>=7) db.execSQL("CREATE TABLE deleted_messages(peer TEXT NOT NULL, packet INTEGER NOT NULL, outgoing INTEGER NOT NULL, PRIMARY KEY(peer,packet,outgoing))");
    }
    private static void createTransfers(SQLiteDatabase db){db.execSQL("CREATE TABLE transfer_states(task TEXT PRIMARY KEY, peer TEXT NOT NULL, packet INTEGER NOT NULL, percent INTEGER NOT NULL, state TEXT NOT NULL, time INTEGER NOT NULL)");}
    static final class TransferState {final String key,state;final int percent;TransferState(String key,int percent,String state){this.key=key;this.percent=percent;this.state=state;}}
    void transfer(String key,String peer,long packet,int percent,String state){
        // 文件终态属于历史，不能按缓存数量淘汰；迟到进度也不能重新登记已删除的消息。
        getWritableDatabase().execSQL("INSERT OR REPLACE INTO transfer_states(task,peer,packet,percent,state,time) SELECT ?,?,?,?,?,? WHERE NOT EXISTS (SELECT 1 FROM deleted_messages WHERE peer=? AND packet=? AND outgoing=?)",
            new Object[]{key,peer,packet,percent,state,System.currentTimeMillis(),peer,packet,key.endsWith("/true")?1:0});
    }
    List<TransferState> transfers(){return transfers(null,0);}
    List<TransferState> transfers(String peer,int limit){
        String sql="SELECT task,percent,state FROM transfer_states"+(peer==null?"":" WHERE peer=?")+" ORDER BY time DESC"+(limit>0?" LIMIT "+limit:"");
        List<TransferState> result=new ArrayList<>();try(Cursor cursor=getReadableDatabase().rawQuery(sql,peer==null?null:new String[]{peer})){
            while(cursor.moveToNext())result.add(restoredTransfer(cursor.getString(0),cursor.getInt(1),cursor.getString(2)));
        }java.util.Collections.reverse(result);return result;
    }
    TransferState transfer(String key){
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT percent,state FROM transfer_states WHERE task=?",new String[]{key})){
            return cursor.moveToFirst()?restoredTransfer(key,cursor.getInt(0),cursor.getString(1)):null;
        }
    }
    private static TransferState restoredTransfer(String key,int percent,String state){
        if(state.startsWith("发送中"))state="上次退出，等待对方续传";
        else if(state.startsWith("接收中")||state.startsWith("连接中")||state.startsWith("保存中")||state.equals("排队中")||state.startsWith("正在校验")||state.startsWith("正在核验"))state="上次退出 · 点击续传";
        return new TransferState(key,percent,state);
    }
    void peer(LanNode.Peer p) {
        ContentValues v=new ContentValues(); v.put("id",p.id()); v.put("endpoint",p.endpoint.getAddress().getHostAddress()+":"+p.endpoint.getPort());
        v.put("login",p.login); v.put("host",p.host); v.put("name",p.name); v.put("grp",p.group); v.put("utf8",p.utf8?1:0);
        v.put("device_id",p.deviceId);
        if(p.avatar!=null) v.put("avatar",Protocol.normalizeAvatar(p.avatar));
        if(p.fingerprint!=null) v.put("fingerprint",p.fingerprint);
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
            if(DatabaseUtils.longForQuery(db,"SELECT COUNT(*) FROM deleted_messages WHERE peer=? AND packet=? AND outgoing=?",key)>0){db.setTransactionSuccessful();return false;}
            try(Cursor existing=db.rawQuery("SELECT id,state FROM messages WHERE peer=? AND packet=? AND outgoing=? LIMIT 1",key)) {
                inserted=!existing.moveToFirst();
                // 网络线程传来的是状态快照；取消落库后，旧失败或 ACK 快照不能覆盖这个本机终态。
                if(!inserted&&m.outgoing&&"已取消发送".equals(existing.getString(1)))v.put("state","已取消发送");
            }
            // 已清空的消息不能被迟到的 ACK / 文件状态回调复活。
            if(inserted && update) { db.setTransactionSuccessful(); return false; }
            if(inserted) db.insertOrThrow("messages",null,v);
            else if(update) db.update("messages",v,"peer=? AND packet=? AND outgoing=?",key);
            // 网络去重表会过期；持久层也必须幂等，重复旧消息不能抬高未读或倒退最近预览。
            if (!update && inserted) {
                String preview=m.text.isEmpty() && !m.files.isEmpty()?"[文件] "+m.files.get(0).name:m.text;
                preview=MessageText.normalizeLineBreaks(preview).replace('\n',' ');
                db.execSQL("UPDATE peers SET preview=?, time=?, unread=unread+?, hidden=0 WHERE id=?",new Object[]{preview,m.time,(!m.outgoing&&!visible)?1:0,p.id()});
            }
            db.setTransactionSuccessful();
            return inserted;
        } finally { db.endTransaction(); }
    }
    void recoverPending() { getWritableDatabase().execSQL("UPDATE messages SET state='上次退出，未确认送达' WHERE state='等待确认'"); }
    void deleteMessages(String peer,List<LanNode.Message> selected) {
        SQLiteDatabase db=getWritableDatabase();db.beginTransaction();
        try {
            for(LanNode.Message message:selected) {
                String[] key={peer,Long.toString(message.number),message.outgoing?"1":"0"};
                if(db.delete("messages","peer=? AND packet=? AND outgoing=?",key)>0) {
                    db.delete("transfer_states","peer=? AND packet=? AND task LIKE ?",new String[]{peer,Long.toString(message.number),"%/"+message.outgoing});
                    // 删除消息号与正文删除在同一事务里，重启后的重复包不能恢复记录。
                    db.execSQL("INSERT OR IGNORE INTO deleted_messages(peer,packet,outgoing) VALUES(?,?,?)",new Object[]{peer,message.number,message.outgoing?1:0});
                }
            }
            String preview="";long time=0;
            try(Cursor last=db.rawQuery("SELECT text,files,time FROM messages WHERE peer=? ORDER BY time DESC,id DESC LIMIT 1",new String[]{peer})) {
                if(last.moveToFirst()){preview=last.getString(0);time=last.getLong(2);List<Protocol.Attachment> files=Protocol.parseStoredFiles(last.getString(1));if(preview.isEmpty()&&!files.isEmpty())preview="[文件] "+files.get(0).name;}
            }
            db.execSQL("UPDATE peers SET preview=?,time=? WHERE id=?",new Object[]{MessageText.normalizeLineBreaks(preview).replace('\n',' '),time,peer});db.setTransactionSuccessful();
        } finally {db.endTransaction();}
    }
    void read(String id) { getWritableDatabase().execSQL("UPDATE peers SET unread=0 WHERE id=?",new Object[]{id}); }
    void edit(String id,String note,boolean pinned) {
        ContentValues v=new ContentValues();v.put("note",note.trim());v.put("pinned",pinned?1:0);getWritableDatabase().update("peers",v,"id=?",new String[]{id});
    }
    void remove(String id,boolean clear,boolean hide) {
        SQLiteDatabase db=getWritableDatabase();db.beginTransaction();
        try {
            if(clear) {
                // 与正文清空在同一事务里留存消息身份，重启后旧包不能作为新消息重新出现。
                db.execSQL("INSERT OR IGNORE INTO deleted_messages(peer,packet,outgoing) SELECT peer,packet,outgoing FROM messages WHERE peer=?",new Object[]{id});
                db.delete("messages","peer=?",new String[]{id});db.delete("transfer_states","peer=?",new String[]{id});db.execSQL("UPDATE peers SET preview='',time=0 WHERE id=?",new Object[]{id});
            }
            db.execSQL("UPDATE peers SET hidden=?,unread=0 WHERE id=?",new Object[]{hide?1:0,id});db.setTransactionSuccessful();
        } finally {db.endTransaction();}
    }
    List<Conversation> conversations() {
        List<Conversation> result=new ArrayList<>();
        try (Cursor c=getReadableDatabase().rawQuery("SELECT endpoint,login,host,name,grp,preview,time,unread,utf8,note,pinned,hidden,conversation_number,id,device_id,avatar,fingerprint FROM peers ORDER BY pinned DESC,time DESC,name LIMIT 1000",null)) {
            while (c.moveToNext()) try {
                Conversation item=new Conversation(); item.peer=new LanNode.Peer(LanNode.endpoint(c.getString(0)),c.getString(1),c.getString(2),c.getString(3),c.getString(4));
                item.peer.conversationId=c.getString(13);item.peer.deviceId=Protocol.normalizeDeviceId(c.getString(14));
                item.peer.avatar=Protocol.normalizeAvatar(c.getString(15));
                item.peer.fingerprint=c.getString(16)!=null&&c.getString(16).matches("[0-9a-f]{64}")?c.getString(16):null;
                item.preview=MessageText.normalizeLineBreaks(c.getString(5)).replace('\n',' '); item.time=c.getLong(6); item.unread=c.getInt(7); item.peer.utf8=c.getInt(8)!=0; item.note=c.getString(9);item.pinned=c.getInt(10)!=0;item.hidden=c.getInt(11)!=0;item.number=c.getLong(12);result.add(item);
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
    private static final String MESSAGE_COLUMNS="packet,outgoing,time,text,state,files,local_files,id";
    static final int HISTORY_PAGE_SIZE=100;
    static final class HistoryIndex {
        final long lastId,sourceCount;
        long total;
        final List<long[]> matches=new ArrayList<>();
        HistoryIndex(long lastId,long sourceCount){this.lastId=lastId;this.sourceCount=sourceCount;}
        int pageCount(){return total==0?0:(int)((total-1)/HISTORY_PAGE_SIZE+1);}
    }
    static final class HistoryPage {
        final HistoryIndex index;
        final int number;
        final List<LanNode.Message> rows=new ArrayList<>();
        HistoryPage(HistoryIndex index,int number){this.index=index;this.number=number;}
    }
    private static LanNode.Message messageAt(Cursor cursor) {
        LanNode.Message message=new LanNode.Message();
        message.number=cursor.getLong(0);message.outgoing=cursor.getInt(1)!=0;message.time=cursor.getLong(2);
        message.text=cursor.getString(3);message.state=cursor.getString(4);message.files=Protocol.parseStoredFiles(cursor.getString(5));
        if(message.outgoing)try {
            org.json.JSONArray paths=new org.json.JSONArray(cursor.getString(6));
            for(int index=0;index<paths.length();index++)message.localPaths.add(paths.getString(index));
        } catch(org.json.JSONException ignored){message.localPaths.clear();}
        return message;
    }
    List<LanNode.Message> history(String peer) {
        List<LanNode.Message> rows=new ArrayList<>();
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT "+MESSAGE_COLUMNS+" FROM messages WHERE peer=? ORDER BY time DESC,id DESC LIMIT 200",new String[]{peer})) {
            while(cursor.moveToNext())rows.add(messageAt(cursor));
        }
        java.util.Collections.reverse(rows);return rows;
    }
    static final class FileTask {
        final String peer;final LanNode.Message message;final Protocol.Attachment file;
        java.io.File guestFile;
        boolean stored,unavailable;
        TransferState savedProgress;
        FileTask(String peer,LanNode.Message message,Protocol.Attachment file){this.peer=peer;this.message=message;this.file=file;}
    }
    List<FileTask> fileTasks() {
        List<FileTask> rows=new ArrayList<>();
        Map<String,TransferState> states=new HashMap<>();for(TransferState state:transfers())states.put(state.key,state);
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT "+MESSAGE_COLUMNS+",peer FROM messages WHERE files<>'' ORDER BY time DESC,id DESC",null)) {
            while(cursor.moveToNext()){
                LanNode.Message message=messageAt(cursor);String peer=cursor.getString(8);
                for(Protocol.Attachment file:message.files){FileTask task=new FileTask(peer,message,file);task.savedProgress=states.get(peer+"/"+message.number+"/"+file.id+"/"+message.outgoing);rows.add(task);}
            }
        }
        return rows;
    }
    LanNode.Message findMessage(String peer,long packet,boolean outgoing) {
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT "+MESSAGE_COLUMNS+" FROM messages WHERE peer=? AND packet=? AND outgoing=?",new String[]{peer,Long.toString(packet),outgoing?"1":"0"})) {
            return cursor.moveToFirst()?messageAt(cursor):null;
        }
    }
    LanNode.Peer findPeer(String id) throws IOException {
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT endpoint,login,host,name,grp,device_id,fingerprint FROM peers WHERE id=?",new String[]{id})) {
            if(!cursor.moveToFirst())return null;
            LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint(cursor.getString(0)),cursor.getString(1),cursor.getString(2),cursor.getString(3),cursor.getString(4));
            peer.conversationId=id;peer.deviceId=Protocol.normalizeDeviceId(cursor.getString(5));
            peer.fingerprint=SecureIdentity.validFingerprint(cursor.getString(6))?cursor.getString(6):null;return peer;
        }
    }
    List<LanNode.Message> incomingAttachments(String peer) {
        List<LanNode.Message> rows=new ArrayList<>();
        // 清空时读取全部入站附件，历史页的 LIMIT 200 不适用于文件清理。
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT "+MESSAGE_COLUMNS+" FROM messages WHERE peer=? AND outgoing=0 AND files<>'' ORDER BY id",new String[]{peer})) {
            while(cursor.moveToNext()){LanNode.Message message=messageAt(cursor);if(!message.files.isEmpty())rows.add(message);}
        }
        return rows;
    }
    List<Long> unconfirmedOutgoingPackets(String peer) {
        List<Long> packets=new ArrayList<>();
        try(Cursor cursor=getReadableDatabase().rawQuery("SELECT packet FROM messages WHERE peer=? AND outgoing=1 AND (state='等待确认' OR state LIKE '%未确认%' OR state LIKE '%失败%')",new String[]{peer})) {
            while(cursor.moveToNext())packets.add(cursor.getLong(0));
        }
        return packets;
    }
    List<LanNode.Message> historyAround(String peer,LanNode.Message target) {
        SQLiteDatabase database=getReadableDatabase();long time,rowId;
        try(Cursor cursor=database.rawQuery("SELECT time,id FROM messages WHERE peer=? AND packet=? AND outgoing=?",new String[]{peer,Long.toString(target.number),target.outgoing?"1":"0"})) {
            if(!cursor.moveToFirst())return new ArrayList<>();
            time=cursor.getLong(0);rowId=cursor.getLong(1);
        }
        String[] key={peer,Long.toString(time),Long.toString(time),Long.toString(rowId)};
        List<LanNode.Message> rows=new ArrayList<>();
        String select="SELECT "+MESSAGE_COLUMNS+" FROM messages WHERE peer=? AND ";
        try(Cursor cursor=database.rawQuery(select+"(time<? OR (time=? AND id<=?)) ORDER BY time DESC,id DESC LIMIT 100",key)) {
            while(cursor.moveToNext())rows.add(messageAt(cursor));
        }
        java.util.Collections.reverse(rows);
        try(Cursor cursor=database.rawQuery(select+"(time>? OR (time=? AND id>?)) ORDER BY time,id LIMIT 100",key)) {
            while(cursor.moveToNext())rows.add(messageAt(cursor));
        }
        return rows;
    }
    HistoryPage search(String peer,String query,int wanted,HistoryIndex index,java.util.function.BooleanSupplier current) {
        SQLiteDatabase database=getReadableDatabase();
        long lastId=index==null?DatabaseUtils.longForQuery(database,"SELECT coalesce(max(id),0) FROM messages",null):index.lastId;
        String[] scope={peer,Long.toString(lastId)};
        long sourceCount=DatabaseUtils.longForQuery(database,"SELECT count(*) FROM messages WHERE peer=? AND id<=?",scope);
        if(index==null || index.sourceCount!=sourceCount) {
            index=new HistoryIndex(lastId,sourceCount);
            if(query.isEmpty())index.total=sourceCount;
            else {
                String sql="SELECT id,text,files FROM messages WHERE peer=? AND id<=? ORDER BY time DESC,id DESC";
                try(Cursor cursor=database.rawQuery(sql,scope)) {
                    while(current.getAsBoolean() && cursor.moveToNext()) {
                        if(!contains(cursor.getString(1),query) && Protocol.parseStoredFiles(cursor.getString(2)).stream().noneMatch(file->contains(file.name,query)))continue;
                        int offset=(int)(index.total%HISTORY_PAGE_SIZE);
                        if(offset==0)index.matches.add(new long[HISTORY_PAGE_SIZE]);
                        index.matches.get(index.matches.size()-1)[offset]=cursor.getLong(0);
                        index.total++;
                    }
                }
            }
        }
        int number=Math.max(1,Math.min(wanted,index.pageCount()));
        HistoryPage page=new HistoryPage(index,number);
        if(!current.getAsBoolean() || index.total==0)return page;
        long start=(long)(number-1)*HISTORY_PAGE_SIZE;
        String select="SELECT "+MESSAGE_COLUMNS+" FROM messages WHERE peer=? AND ";
        String sql;
        List<String> arguments=new ArrayList<>();arguments.add(peer);
        if(query.isEmpty()) {
            sql=select+"id<=? ORDER BY time DESC,id DESC LIMIT ? OFFSET ?";
            arguments.add(scope[1]);arguments.add(Integer.toString(HISTORY_PAGE_SIZE));arguments.add(Long.toString(start));
        } else {
            long[] ids=index.matches.get(number-1);
            int length=(int)Math.min(HISTORY_PAGE_SIZE,index.total-start);
            sql=select+"id IN ("+String.join(",",java.util.Collections.nCopies(length,"?"))+") ORDER BY time DESC,id DESC";
            for(int position=0;position<length;position++)arguments.add(Long.toString(ids[position]));
        }
        try(Cursor cursor=database.rawQuery(sql,arguments.toArray(new String[0]))) {
            while(current.getAsBoolean() && cursor.moveToNext())page.rows.add(messageAt(cursor));
        }
        java.util.Collections.reverse(page.rows);return page;
    }
    static boolean contains(String value,String query) {
        for(int index=0;index<=value.length()-query.length();index++)if(value.regionMatches(true,index,query,0,query.length()))return true;
        return false;
    }
}
