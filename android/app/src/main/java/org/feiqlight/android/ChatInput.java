package org.feiqlight.android;

import android.content.Context;
import android.net.Uri;
import android.os.Bundle;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputConnection;
import android.view.inputmethod.InputConnectionWrapper;
import android.view.inputmethod.InputContentInfo;
import android.widget.EditText;
import java.util.concurrent.atomic.AtomicBoolean;

/** 使用系统 IME 富内容接口；授权保留到后台复制结束，不依赖特定输入法或素材库。 */
final class ChatInput extends EditText {
    interface ContentReceiver { boolean receive(Uri uri,String mime,Runnable release); }
    private static final String[] MIME_TYPES={"image/png","image/jpeg","image/gif","image/webp"};
    private final ContentReceiver receiver;
    public ChatInput(Context context) { this(context,(uri,mime,release)->false); }
    ChatInput(Context context,ContentReceiver receiver) { super(context); this.receiver=receiver; }
    @Override public InputConnection onCreateInputConnection(EditorInfo info) {
        InputConnection base=super.onCreateInputConnection(info);
        if(base==null) return null;
        info.contentMimeTypes=MIME_TYPES.clone();
        return new InputConnectionWrapper(base,false) {
            @Override public boolean commitContent(InputContentInfo content,int flags,Bundle opts) {
                if(content==null || !"content".equals(content.getContentUri().getScheme())) return false;
                String mime=null;
                for(String supported:MIME_TYPES) if(content.getDescription().hasMimeType(supported)) { mime=supported; break; }
                if(mime==null) return false;
                AtomicBoolean released=new AtomicBoolean();
                boolean grant=(flags&InputConnection.INPUT_CONTENT_GRANT_READ_URI_PERMISSION)!=0;
                Runnable release=()->{ if(released.compareAndSet(false,true)&&grant) { try { content.releasePermission(); } catch(SecurityException ignored) { /* 输入法可能已撤回授权。 */ } } };
                boolean accepted=false;
                try {
                    if(grant) content.requestPermission();
                    accepted=receiver.receive(content.getContentUri(),mime,release);
                    return accepted;
                } catch(RuntimeException e) { return false; }
                finally { if(!accepted) release.run(); }
            }
        };
    }
}
