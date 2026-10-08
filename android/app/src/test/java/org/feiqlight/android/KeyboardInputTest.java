package org.feiqlight.android;

import android.content.ClipDescription;
import android.net.Uri;
import android.text.InputFilter;
import android.view.inputmethod.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import java.util.*;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,shadows=KeyboardInputTest.PermissionShadow.class)
public final class KeyboardInputTest {
    @Implements(InputContentInfo.class)
    public static class PermissionShadow {
        static int requests,releases;
        static boolean denied;
        @Implementation protected void requestPermission() { requests++;if(denied)throw new SecurityException("denied"); }
        @Implementation protected void releasePermission() { releases++; }
    }
    @Before public void reset() {PermissionShadow.requests=PermissionShadow.releases=0;PermissionShadow.denied=false;}
    private static InputContentInfo image(String scheme,String mime) {return new InputContentInfo(Uri.parse(scheme+"://keyboard/image"),new ClipDescription("贴纸",new String[]{mime}),null);}
    @Test public void ordinaryEmojiAndLengthBoundaryRemainText() {
        ChatInput input=new ChatInput(RuntimeEnvironment.getApplication(),(uri,mime,release)->false);EditorInfo info=new EditorInfo();InputConnection connection=input.onCreateInputConnection(info);
        assertNotNull(connection);assertArrayEquals(new String[]{"image/png","image/jpeg","image/gif","image/webp"},info.contentMimeTypes);
        String text="中文𠮷 👨‍👩‍👧‍👦 👩🏽‍💻 🇨🇳 1️⃣ (๑•̀ㅂ•́)و✧\ne\u0301";assertTrue(connection.commitText(text,1));assertEquals(text,input.getText().toString());
        input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(18000)});input.setText("a".repeat(17999));input.setSelection(17999);connection.commitText("😀",1);assertEquals(17999,input.length());
    }
    @Test public void allAdvertisedImageTypesHoldPermissionUntilCopyCompletes() {
        for(String mime:new String[]{"image/png","image/jpeg","image/gif","image/webp"}) {
            List<Runnable> completion=new ArrayList<>();ChatInput input=new ChatInput(RuntimeEnvironment.getApplication(),(uri,type,release)->{assertEquals(mime,type);completion.add(release);return true;});
            assertTrue(input.onCreateInputConnection(new EditorInfo()).commitContent(image("content",mime),InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION,null));
            assertEquals(PermissionShadow.requests-1,PermissionShadow.releases);assertEquals(1,completion.size());completion.get(0).run();completion.get(0).run();assertEquals(PermissionShadow.requests,PermissionShadow.releases);
        }
    }
    @Test public void unsupportedSchemeMimeAndBusyReceiverDoNotLeakPermission() {
        ChatInput input=new ChatInput(RuntimeEnvironment.getApplication(),(uri,mime,release)->false);InputConnection connection=input.onCreateInputConnection(new EditorInfo());
        assertFalse(connection.commitContent(image("content","application/pdf"),1,null));
        // Android 在构造 InputContentInfo 时即拒绝 file://，不能伪造为合法框架对象。
        try {image("file","image/png");fail();} catch(java.security.InvalidParameterException expected) { }
        assertEquals(0,PermissionShadow.requests);
        assertFalse(connection.commitContent(image("content","image/png"),1,null));assertEquals(1,PermissionShadow.requests);assertEquals(1,PermissionShadow.releases);
        PermissionShadow.denied=true;assertFalse(connection.commitContent(image("content","image/png"),1,null));assertEquals(2,PermissionShadow.requests);assertEquals(2,PermissionShadow.releases);
    }
}
