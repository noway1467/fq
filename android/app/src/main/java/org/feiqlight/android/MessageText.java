package org.feiqlight.android;

import java.nio.charset.StandardCharsets;

final class MessageText {
    static final int FILE_THRESHOLD=16*1024;
    static final int INPUT_LIMIT=1024*1024;
    static boolean asFile(String text) { return text.getBytes(StandardCharsets.UTF_8).length>FILE_THRESHOLD; }
    static String normalizeLineBreaks(String text) {
        return (text==null?"":text).replace("\r\n","\n").replace('\r','\n').replace('\u000b','\n').replace('\u2028','\n').replace('\u2029','\n');
    }
    static String preview(String text) {
        text=normalizeLineBreaks(text);
        int end=Math.min(text.length(),600),lines=0;
        for(int i=0;i<end;i++)if(text.charAt(i)=='\n'&&++lines==8){end=i;break;}
        if(end==text.length())return text;
        if(end>0&&Character.isHighSurrogate(text.charAt(end-1)))end--;
        return text.substring(0,end)+"\n…";
    }
}
