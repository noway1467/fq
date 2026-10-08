package org.feiqlight.android;

import android.content.Intent;
import android.net.Uri;
import android.text.SpannableString;
import android.text.Spanned;
import android.text.method.LinkMovementMethod;
import android.text.style.ClickableSpan;
import android.view.View;
import android.widget.TextView;
import android.widget.Toast;
import java.util.regex.*;

/** 只允许网页链接；文件路径和应用私有协议不自动执行。 */
final class MessageLinks {
    private static final Pattern URL=Pattern.compile("(?:https?://|www\\.)[^\\s<>\"，。！？；：、]+",Pattern.CASE_INSENSITIVE);
    static String normalize(String value) {
        if(value.regionMatches(true,0,"www.",0,4))value="https://"+value;
        Uri uri=Uri.parse(value);String scheme=uri.getScheme();
        return ("http".equalsIgnoreCase(scheme)||"https".equalsIgnoreCase(scheme))&&uri.getHost()!=null&&!uri.getHost().isEmpty()&&uri.getUserInfo()==null?value:null;
    }
    static void apply(TextView view,int color) {
        String text=view.getText().toString();SpannableString spans=new SpannableString(text);Matcher matcher=URL.matcher(text);boolean found=false;
        while(matcher.find()) {
            String value=matcher.group().replaceAll("[.,!?;:'\"]+$","");
            while(value.endsWith(")")&&value.chars().filter(c->c==')').count()>value.chars().filter(c->c=='(').count())value=value.substring(0,value.length()-1);
            final String url=normalize(value);if(url==null)continue;
            spans.setSpan(new ClickableSpan(){@Override public void onClick(View widget){
                try {widget.getContext().startActivity(new Intent(Intent.ACTION_VIEW,Uri.parse(url)).addCategory(Intent.CATEGORY_BROWSABLE));}
                catch(RuntimeException e){Toast.makeText(widget.getContext(),"没有可用浏览器，或系统拒绝打开链接",Toast.LENGTH_LONG).show();}
            }},matcher.start(),matcher.start()+value.length(),Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);found=true;
        }
        if(found){view.setText(spans);view.setLinkTextColor(color);view.setMovementMethod(LinkMovementMethod.getInstance());}
    }
}
