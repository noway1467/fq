package org.feiqlight.android;

import android.app.Dialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.ComponentName;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.os.Looper;
import android.text.SpannableString;
import android.text.SpannableStringBuilder;
import android.text.Spanned;
import android.text.style.ForegroundColorSpan;
import android.text.style.UnderlineSpan;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ListView;
import android.widget.TextView;
import org.feiqlight.android.core.LanNode;
import org.feiqlight.android.core.Protocol;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.android.controller.ServiceController;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import org.robolectric.shadows.ShadowDialog;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.TimeUnit;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w360dp-h800dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class LineCancelTest {
    private static final String LEGACY="首行\u000bhttps://example.com/a\r\n定位😀\r第四行\u2028www.example.org/b\u2029末行";
    private static final String NORMAL="首行\nhttps://example.com/a\n定位😀\n第四行\nwww.example.org/b\n末行";
    private static Object field(Object target,String name)throws Exception {Field value=target.getClass().getDeclaredField(name);value.setAccessible(true);return value.get(target);}
    private static void set(Object target,String name,Object value)throws Exception {Field slot=target.getClass().getDeclaredField(name);slot.setAccessible(true);slot.set(target,value);}
    private static Object invoke(Object target,String name,Class<?>[] types,Object...args)throws Exception {Method method=target.getClass().getDeclaredMethod(name,types);method.setAccessible(true);return method.invoke(target,args);}
    private static List<View> views(View root) {List<View> all=new ArrayList<>();all.add(root);if(root instanceof ViewGroup){ViewGroup group=(ViewGroup)root;for(int i=0;i<group.getChildCount();i++)all.addAll(views(group.getChildAt(i)));}return all;}
    private static TextView text(View root,String value) {return (TextView)views(root).stream().filter(v->v instanceof TextView&&value.contentEquals(((TextView)v).getText())).findFirst().orElse(null);}
    private static void click(Dialog dialog,String label) {View button=views(dialog.getWindow().getDecorView()).stream().filter(v->v instanceof Button&&label.contentEquals(((Button)v).getText())).findFirst().orElseThrow();button.performClick();}
    private static Bitmap bitmap(View view,int width) {
        view.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1800,View.MeasureSpec.AT_MOST));view.layout(0,0,view.getMeasuredWidth(),view.getMeasuredHeight());
        Bitmap image=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);view.draw(new Canvas(image));return image;
    }
    private static void save(Bitmap image,String name)throws Exception {
        File directory=new File(System.getProperty("feiq.previewDir"));assertTrue(directory.isDirectory()||directory.mkdirs());
        try(FileOutputStream stream=new FileOutputStream(new File(directory,name+".png"))){assertTrue(image.compress(Bitmap.CompressFormat.PNG,100,stream));}
    }
    private static void samePixels(Bitmap actual,Bitmap expected) {
        assertEquals(expected.getWidth(),actual.getWidth());assertEquals(expected.getHeight(),actual.getHeight());
        int[] a=new int[actual.getWidth()*actual.getHeight()],b=new int[a.length];actual.getPixels(a,0,actual.getWidth(),0,0,actual.getWidth(),actual.getHeight());expected.getPixels(b,0,expected.getWidth(),0,0,expected.getWidth(),expected.getHeight());assertArrayEquals("旧换行绘制与标准 LF 不一致",b,a);
    }
    private static LanNode.Peer peer()throws Exception {return new LanNode.Peer(LanNode.endpoint("127.0.0.1:32481"),"line-peer","host","换行与取消回归","");}
    private static LanNode.Message message(String value) {LanNode.Message message=new LanNode.Message();message.number=71;message.time=1700000000000L;message.text=value;message.state="已收到";return message;}
    private static final class Session implements AutoCloseable {
        final ServiceController<ChatService> sc;final ChatService service;final ActivityController<MainActivity> ac;final MainActivity activity;
        Session()throws Exception {
            sc=Robolectric.buildService(ChatService.class).create();service=sc.get();service.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("follow_system_theme",false).commit();
            shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(service,ChatService.class),service.onBind(new Intent()));
            ac=Robolectric.buildActivity(MainActivity.class).setup();activity=ac.get();settle();
        }
        void settle()throws Exception {for(int i=0;i<6;i++){((ExecutorService)field(service,"io")).submit(()->{}).get(5,TimeUnit.SECONDS);((ExecutorService)field(service,"archive")).submit(()->{}).get(5,TimeUnit.SECONDS);shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));}}
        void open(String peer)throws Exception {invoke(activity,"open",new Class<?>[]{String.class},peer);settle();}
        View messages()throws Exception {return (View)field(activity,"messages");}
        ChatStore store()throws Exception {return (ChatStore)field(service,"store");}
        public void close() {Dialog dialog=ShadowDialog.getLatestDialog();if(dialog!=null&&dialog.isShowing())dialog.dismiss();ac.pause().stop().destroy();sc.destroy();}
    }
    @Test public void lineHelpers() {
        assertEquals("",MessageText.normalizeLineBreaks(null));
        for(String separator:new String[]{"\n","\r\n","\r","\u000b","\u2028","\u2029"}) {
            String raw=String.join(separator,Arrays.asList("中文","https://example.com/","😀","末行"));assertEquals("中文\nhttps://example.com/\n😀\n末行",MessageText.preview(raw));
            List<String> lines=new ArrayList<>();for(int i=1;i<=12;i++)lines.add("第"+i+"行");assertEquals(String.join("\n",lines.subList(0,8))+"\n…",MessageText.preview(String.join(separator,lines)));
            assertEquals("中文 https_//example.com/ 😀 末行",Protocol.field(raw));
        }
        assertEquals("a".repeat(599)+"\n…",MessageText.preview("a".repeat(599)+"😀tail"));
    }
    @Test public void legacyViews()throws Exception {
        try(Session s=new Session()) {
            LanNode.Peer peer=peer();LanNode.Message legacy=message(LEGACY);s.service.incoming(peer,legacy,false);s.settle();s.open(peer.id());
            TextView content=text(s.messages(),NORMAL);assertNotNull(content);
            try {
                Bitmap actual=bitmap(content,500);save(actual,"android-legacy-lines");assertTrue(content.getLineCount()>=6);
                assertTrue(content.getText() instanceof Spanned);Spanned linked=(Spanned)content.getText();android.text.style.ClickableSpan[] links=linked.getSpans(0,content.length(),android.text.style.ClickableSpan.class);assertEquals(2,links.length);
                List<String> opened=new ArrayList<>();for(android.text.style.ClickableSpan link:links){link.onClick(content);opened.add(shadowOf(s.activity).getNextStartedActivity().getDataString());}
                assertEquals(Arrays.asList("https://example.com/a","https://www.example.org/b"),opened);assertEquals(NORMAL.indexOf("https://example.com/a"),linked.getSpanStart(links[0]));assertEquals(NORMAL.indexOf("www.example.org/b"),linked.getSpanStart(links[1]));
                assertNull("仅规范化换行不应误报全文截断",text(s.messages(),"查看全文"));
                LanNode.Message normalized=legacy.copy();normalized.text=NORMAL;invoke(s.activity,"renderMessages",new Class<?>[]{List.class},Collections.singletonList(normalized));
                TextView expected=text(s.messages(),NORMAL);Bitmap reference=bitmap(expected,500);samePixels(actual,reference);reference.recycle();actual.recycle();
            } finally {assertEquals(LEGACY,legacy.text);assertEquals(LEGACY,s.store().findMessage(peer.id(),legacy.number,false).text);}
            invoke(s.activity,"showFullText",new Class<?>[]{String.class},LEGACY);Dialog full=ShadowDialog.getLatestDialog();TextView value=text(full.getWindow().getDecorView(),NORMAL);assertNotNull(value);
            Bitmap fullImage=bitmap(value,500);assertTrue(value.getLineCount()>=6);save(fullImage,"android-legacy-full-text");fullImage.recycle();full.dismiss();
            invoke(s.activity,"messageActions",new Class<?>[]{LanNode.Message.class,LanNode.Peer.class},legacy,peer);click(ShadowDialog.getLatestDialog(),"复制消息");
            assertEquals(NORMAL,s.activity.getSystemService(ClipboardManager.class).getPrimaryClip().getItemAt(0).getText().toString());
            Method preview=MainActivity.class.getDeclaredMethod("searchPreview",String.class,String.class);preview.setAccessible(true);
            assertEquals(NORMAL,preview.invoke(null,LEGACY,"定位"));String late="a".repeat(800)+"\u000b尾部命中\r下一行";String found=(String)preview.invoke(null,late,"尾部命中");assertTrue(found.contains("尾部命中\n下一行"));assertFalse(found.contains("\u000b"));
            ChatStore.Conversation conversation=s.service.conversations().stream().filter(c->c.peer.id().equals(peer.id())).findFirst().orElseThrow();assertEquals(NORMAL.replace('\n',' '),conversation.preview);
            s.store().getWritableDatabase().execSQL("UPDATE peers SET preview=? WHERE id=?",new Object[]{LEGACY,peer.id()});assertEquals(NORMAL.replace('\n',' '),s.store().conversations().get(0).preview);
            invoke(s.activity,"showHistory",new Class<?>[]{ChatStore.Conversation.class},conversation);s.settle();Dialog archive=ShadowDialog.getLatestDialog();ListView list=(ListView)views(archive.getWindow().getDecorView()).stream().filter(v->v instanceof ListView).findFirst().orElseThrow();assertEquals(1,list.getAdapter().getCount());View row=list.getAdapter().getView(0,null,list);TextView body=text(row,NORMAL);assertNotNull(body);Bitmap history=bitmap(row,600);assertTrue(body.getLineCount()>=6);save(history,"android-legacy-history");history.recycle();archive.dismiss();
        }
    }
    @Test public void inputAndDraft()throws Exception {
        try(Session s=new Session()) {
            LanNode.Peer peer=peer();
            s.service.incoming(peer,message("输入回归"),false);s.settle();
            s.activity.getSharedPreferences("drafts",0).edit().putString(peer.id(),LEGACY).commit();
            s.open(peer.id());
            EditText input=(EditText)field(s.activity,"editor");
            assertEquals(NORMAL,input.getText().toString());

            ClipboardManager clipboard=s.activity.getSystemService(ClipboardManager.class);
            input.setText("pre😀post");input.setSelection(3,5);
            clipboard.setPrimaryClip(ClipData.newPlainText("旧文本",LEGACY));
            assertTrue(input.onTextContextMenuItem(android.R.id.pasteAsPlainText));
            assertEquals("pre"+NORMAL+"post",input.getText().toString());
            assertEquals(3+NORMAL.length(),input.getSelectionStart());

            SpannableString source=new SpannableString(LEGACY);
            source.setSpan(new UnderlineSpan(),0,source.length(),Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            input.getText().replace(0,input.length(),source);
            assertEquals(NORMAL,input.getText().toString());
            UnderlineSpan[] spans=input.getText().getSpans(0,input.length(),UnderlineSpan.class);
            assertEquals(1,spans.length);
            assertEquals(input.length(),input.getText().getSpanEnd(spans[0]));

            // 长度测试执行输入框原有的过滤器，避免 Native LineBreaker 为百万字单段反复排版。
            SpannableStringBuilder limited=new SpannableStringBuilder("a".repeat(MessageText.INPUT_LIMIT-1));
            limited.setFilters(input.getFilters());limited.append("😀");
            assertEquals(MessageText.INPUT_LIMIT-1,limited.length());
            assertFalse(Character.isSurrogate(limited.charAt(limited.length()-1)));
            limited.append("\r\n");
            assertEquals(MessageText.INPUT_LIMIT,limited.length());
            assertEquals('\n',limited.charAt(limited.length()-1));

            DraftStore drafts=new DraftStore(s.activity);
            assertEquals(NORMAL,drafts.read(peer.id()));
            String crlf="首行\r\n第二行";
            drafts.save("crlf-draft",crlf);
            DraftStore.Snapshot sent=drafts.snapshot("crlf-draft");
            assertEquals("底层草稿存储应无损保留 CRLF",crlf,sent.text);
            assertTrue("按原版本条件清理草稿",drafts.clearIfUnchanged("crlf-draft",sent));
            assertEquals("",drafts.read("crlf-draft"));
        }
    }
    private static TextView canceledStamp(View root) {return (TextView)views(root).stream().filter(v->v instanceof TextView&&((TextView)v).getText().toString().contains("● 已取消发送")).findFirst().orElseThrow();}
    private static void dangerSpan(TextView stamp,boolean dark) {
        assertTrue(stamp.getText() instanceof Spanned);Spanned value=(Spanned)stamp.getText();ForegroundColorSpan[] spans=value.getSpans(0,value.length(),ForegroundColorSpan.class);assertEquals(1,spans.length);assertEquals(dark?0xffff6e6e:0xffd24e54,spans[0].getForegroundColor());assertEquals("● 已取消发送",value.subSequence(value.getSpanStart(spans[0]),value.getSpanEnd(spans[0])).toString());
    }
    @Test public void cancelText()throws Exception {
        LanNode.Peer peer;long packet;
        try(Session s=new Session();DatagramSocket offline=new DatagramSocket(0,InetAddress.getLoopbackAddress())) {
            int local;try(ServerSocket port=new ServerSocket(0,1,InetAddress.getLoopbackAddress())){local=port.getLocalPort();}
            peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:"+offline.getLocalPort()),"offline-text","host","离线文本","");peer.utf8=true;
            try(LanNode node=new LanNode(local,true,"text-cancel","host","本机","",new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer p,LanNode.Message m,boolean update){s.service.incoming(p,m,update);}})) {
                node.start();set(s.service,"node",node);
                List<Boolean> sent=new ArrayList<>();s.service.send(peer,"0",sent::add);s.settle();
                assertEquals(Collections.singletonList(true),sent);
                LanNode.Message message=s.store().history(peer.id()).get(0);packet=message.number;
                s.open(peer.id());assertTrue(s.service.canCancelOutgoing(peer.id(),packet));
                TextView menu=(TextView)views(s.messages()).stream().filter(v->v.getContentDescription()!=null&&v.getContentDescription().toString().startsWith("消息操作：")).findFirst().orElseThrow();
                menu.performClick();click(ShadowDialog.getLatestDialog(),"取消发送");
                click(ShadowDialog.getLatestDialog(),"取消发送");s.settle();
                assertFalse(s.service.canCancelOutgoing(peer.id(),packet));
                assertEquals("已取消发送",s.store().findMessage(peer.id(),packet,true).state);
                invoke(node,"tick",new Class<?>[0]);assertFalse(node.canCancelOutgoing(peer.id(),packet));

                LanNode.Message stale=message.copy();stale.state="身份核对未通过，未确认送达";
                s.service.incoming(peer,stale,true);s.settle();
                assertEquals("已取消发送",s.store().findMessage(peer.id(),packet,true).state);
                stale.state="已送达";s.service.incoming(peer,stale,true);s.settle();
                assertEquals("已取消发送",s.store().findMessage(peer.id(),packet,true).state);

                for(boolean dark:new boolean[]{false,true}) {
                    s.activity.getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
                    invoke(s.activity,"applyTheme",new Class<?>[0]);invoke(s.activity,"renderPage",new Class<?>[0]);s.settle();
                    TextView stamp=canceledStamp(s.messages());dangerSpan(stamp,dark);
                    Bitmap image=bitmap(stamp,450);int red=0;
                    for(int y=0;y<image.getHeight();y++)for(int x=0;x<image.getWidth();x++)if(image.getPixel(x,y)==(dark?0xffff6e6e:0xffd24e54))red++;
                    assertTrue("取消红点和标签未绘制",red>4);
                    save(image,"android-canceled-text-"+(dark?"dark":"light"));image.recycle();
                    assertTrue(stamp.getText().toString().contains("···"));
                    assertNull(text(s.messages(),"发送未确认 · 点击重试"));
                    stamp.performClick();Dialog actions=ShadowDialog.getLatestDialog();
                    assertNull(text(actions.getWindow().getDecorView(),"取消发送"));actions.dismiss();
                }

                ChatStore.Conversation conversation=s.service.conversations().stream().filter(c->c.peer.id().equals(peer.id())).findFirst().orElseThrow();
                invoke(s.activity,"showHistory",new Class<?>[]{ChatStore.Conversation.class},conversation);s.settle();
                Dialog archive=ShadowDialog.getLatestDialog();
                ListView list=(ListView)views(archive.getWindow().getDecorView()).stream().filter(v->v instanceof ListView).findFirst().orElseThrow();
                View row=list.getAdapter().getView(0,null,list);dangerSpan(canceledStamp(row),true);
                Bitmap image=bitmap(row,600);save(image,"android-canceled-history");image.recycle();archive.dismiss();
                s.open("");s.open(peer.id());dangerSpan(canceledStamp(s.messages()),true);
            }
        }
        try(Session restarted=new Session()) {assertEquals("已取消发送",restarted.store().findMessage(peer.id(),packet,true).state);restarted.open(peer.id());dangerSpan(canceledStamp(restarted.messages()),true);assertFalse(restarted.service.canCancelOutgoing(peer.id(),packet));}
    }
}
