package org.feiqlight.android;

import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.graphics.*;
import org.feiqlight.android.core.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class AvatarTest {
    @Test public void extensionKeepsIdentityAndValidatesPictures() throws Exception {
        String id="0123456789abcdef0123456789abcdef";
        String wire=new String(Protocol.encode(1,"user","host",Protocol.ENTRY|Protocol.UTF8,"昵称","部门",id),StandardCharsets.UTF_8)+"FQAV=builtin:23\0";
        Protocol.Packet packet=Protocol.parse(wire.getBytes(StandardCharsets.UTF_8));
        assertNotNull(packet);assertEquals(id,packet.deviceId);assertEquals("昵称",packet.body);assertEquals("部门",packet.extra);assertEquals("builtin:23",packet.avatar);
        assertNull(Protocol.parse(Protocol.encode(2,"user","host",Protocol.ENTRY|Protocol.UTF8,"旧客户端","部门",id)).avatar);
        assertNull(Protocol.normalizeAvatar("builtin:24"));assertNull(Protocol.normalizeAvatar("image:invalid"));
        Bitmap image=Bitmap.createBitmap(64,64,Bitmap.Config.ARGB_8888);image.eraseColor(Color.rgb(230,80,30));
        ByteArrayOutputStream bytes=new ByteArrayOutputStream();assertTrue(image.compress(Bitmap.CompressFormat.JPEG,80,bytes));image.recycle();
        String avatar="image:"+Base64.getEncoder().encodeToString(bytes.toByteArray());assertEquals(avatar,Protocol.normalizeAvatar(avatar));
        AvatarDrawable drawable=new AvatarDrawable(RuntimeEnvironment.getApplication(),"peer",false,0,Color.WHITE,Color.GREEN,avatar);
        Bitmap rendered=Bitmap.createBitmap(64,64,Bitmap.Config.ARGB_8888);drawable.setBounds(0,0,64,64);drawable.draw(new Canvas(rendered));assertTrue(Color.red(rendered.getPixel(32,32))>200);
        File directory=new File(System.getProperty("feiq.previewDir"));assertTrue(directory.exists()||directory.mkdirs());
        try(OutputStream output=new FileOutputStream(new File(directory,"avatar-received.png"))) {assertTrue(rendered.compress(Bitmap.CompressFormat.PNG,100,output));}rendered.recycle();
    }
    @Test public void databaseMigrationAndRestartPreserveAvatarAndOldPeer() throws Exception {
        Context context=RuntimeEnvironment.getApplication();LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:32425"),"peer","host","联系人","部门");peer.avatar="builtin:15";
        try(ChatStore store=new ChatStore(context)) {store.peer(peer);}
        try(ChatStore store=new ChatStore(context)) {assertEquals("builtin:15",store.conversations().get(0).peer.avatar);peer.avatar=null;store.peer(peer);assertEquals("builtin:15",store.conversations().get(0).peer.avatar);}
        try(SQLiteDatabase db=SQLiteDatabase.create(null);ChatStore helper=new ChatStore(context)) {
            db.execSQL("CREATE TABLE peers(id TEXT PRIMARY KEY, device_id TEXT)");db.execSQL("INSERT INTO peers VALUES('old-peer','0123456789abcdef0123456789abcdef')");helper.onUpgrade(db,6,7);
            try(Cursor cursor=db.rawQuery("SELECT id,device_id,avatar FROM peers",null)) {assertTrue(cursor.moveToFirst());assertEquals("old-peer",cursor.getString(0));assertEquals("0123456789abcdef0123456789abcdef",cursor.getString(1));assertTrue(cursor.isNull(2));}
        }
    }
}
