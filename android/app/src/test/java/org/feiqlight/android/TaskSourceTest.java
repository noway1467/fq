package org.feiqlight.android;

import android.app.Dialog;
import android.content.*;
import android.graphics.*;
import android.os.Looper;
import android.os.SystemClock;
import android.view.*;
import android.view.inputmethod.InputMethodManager;
import android.widget.*;
import org.feiqlight.android.core.*;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.android.controller.*;
import org.robolectric.annotation.*;
import org.robolectric.shadows.ShadowDialog;
import org.robolectric.shadows.ShadowToast;
import java.io.*;
import java.lang.reflect.*;
import java.nio.file.Files;
import java.text.SimpleDateFormat;
import java.time.Duration;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=35,qualifiers="w393dp-h750dp-xhdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class TaskSourceTest {
    private static final class Fixture implements AutoCloseable {
        final ServiceController<ChatService> serviceController;
        final ActivityController<MainActivity> activityController;
        final ChatService service;
        final MainActivity activity;
        final ChatStore store;
        final ExecutorService io;
        Fixture()throws Exception {
            Context context=RuntimeEnvironment.getApplication();
            context.getSharedPreferences("settings",0).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).putBoolean(ReceivedStorage.AUTO,false).commit();
            serviceController=Robolectric.buildService(ChatService.class).create();service=serviceController.get();
            service.networkFactory=manager->new LanNetwork(manager,Collections::emptyList);
            settle(service);store=(ChatStore)field(service,"store");io=(ExecutorService)field(service,"io");
            Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.onBind(new Intent()));
            activityController=Robolectric.buildActivity(MainActivity.class).setup();activity=activityController.get();settle(service);
        }
        void contacts(LanNode.Peer... peers)throws Exception {
            List<ChatStore.Conversation> contacts=io.submit(()->{store.peers(Arrays.asList(peers));return store.conversations();}).get(5,TimeUnit.SECONDS);
            setField(service,"conversations",contacts);
        }
        void show()throws Exception {settle(service);invoke(activity,"showTasks",new Class<?>[0]);settle(service);}
        void show(String key)throws Exception {settle(service);invoke(activity,"showTasks",new Class<?>[]{String.class},key);settle(service);}
        Dialog dialog()throws Exception {return (Dialog)field(activity,"tasksDialog");}
        BaseAdapter adapter()throws Exception {return (BaseAdapter)field(activity,"taskAdapter");}
        LinearLayout sources()throws Exception {return (LinearLayout)field(adapter(),"sourceItems");}
        EditText input()throws Exception {return (EditText)field(adapter(),"input");}
        void rows(ChatStore.FileTask... rows)throws Exception {
            // 排空上一操作的真实数据库刷新，再注入仅用于筛选/布局的内存夹具。
            settle(service);
            for(ChatStore.FileTask row:rows)if(!row.message.outgoing)row.stored=new ReceivedStorage(service).exists(row.guestFile!=null?row.guestFile:service.receivedFile(row.peer,row.message.number,row.file));
            setField(adapter(),"all",new ArrayList<>(Arrays.asList(rows)));
            ((android.os.Handler)field(adapter(),"clockHandler")).removeCallbacks((Runnable)field(adapter(),"storageRefresh"));refresh(adapter());
        }
        void pick(String key)throws Exception {View pick=sources().findViewWithTag("task-source:"+key);assertNotNull("来源缺失："+key,pick);assertTrue(pick.performClick());}
        void search(String text)throws Exception {
            assertEquals(View.VISIBLE,input().getVisibility());input().requestFocus();input().setText(text);
            Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(150));
        }
        View menu()throws Exception {assertTrue(findDescription(dialog().getWindow().getDecorView(),"传输任务操作").performClick());return ShadowDialog.getLatestDialog().getWindow().getDecorView();}
        void state(ChatStore.FileTask item,int percent,String state,LanNode.Transfer task)throws Exception {progress(service).put(ChatService.fileKey(item.peer,item.message.number,item.file.id,item.message.outgoing),new ChatService.FileProgress(percent,state,task));}
        LinearLayout taskRow(int index)throws Exception {return (LinearLayout)adapter().getView(index,null,findDescription(dialog().getWindow().getDecorView(),"传输任务列表") instanceof ViewGroup?(ViewGroup)findDescription(dialog().getWindow().getDecorView(),"传输任务列表"):null);}
        @Override public void close()throws Exception {
            Dialog latest=ShadowDialog.getLatestDialog();if(latest!=null&&latest.isShowing())latest.dismiss();
            Dialog tasks=dialog();if(tasks!=null)tasks.dismiss();activityController.close();serviceController.destroy();
            for(String executor:new String[]{"io","discovery","archive","transferWorker"})assertTrue("测试队列未退出："+executor,((ExecutorService)field(service,executor)).awaitTermination(5,TimeUnit.SECONDS));
        }
    }

    @Test public void chatMenuAndNoArgumentChooseTheirOwnDefaultAndKeepToggleBehavior()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer peer=peer("a","没有文件记录的联系人");f.contacts(peer);
            invoke(f.activity,"open",new Class<?>[]{String.class},peer.id());settle(f.service);
            View transfer=findDescription((View)field(f.activity,"toolbar"),"传输任务");assertNotNull(transfer);assertTrue(transfer.performClick());settle(f.service);
            assertEquals("peer:"+peer.id(),field(f.adapter(),"selectedSource"));assertEquals(Arrays.asList("全部",peer.name),sourceNames(f.sources()));assertEquals(0,f.adapter().getCount());assertEquals(View.VISIBLE,f.input().getVisibility());
            View selected=f.sources().getChildAt(1);assertTrue(selected.isSelected());assertEquals(peer.name+"，已选中",selected.getContentDescription());
            f.pick("");assertEquals(Collections.singletonList("全部"),sourceNames(f.sources()));f.dialog().dismiss();
            f.show();assertEquals("",field(f.adapter(),"selectedSource"));assertEquals(Collections.singletonList("全部"),sourceNames(f.sources()));
            invoke(f.activity,"showTasks",new Class<?>[0]);settle(f.service);assertNull(field(f.activity,"tasksDialog"));assertNull(field(f.activity,"taskAdapter"));
            invoke(f.activity,"showMenu",new Class<?>[]{View.class},field(f.activity,"root"));
            Button tasks=findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"传输任务");assertNotNull(tasks);assertTrue(tasks.performClick());settle(f.service);
            assertEquals("",field(f.adapter(),"selectedSource"));assertEquals(View.VISIBLE,f.input().getVisibility());
        }
    }

    @Test public void guestPanelChoosesGuestAndGuestActionsHaveNoConversationLink()throws Exception {
        try(Fixture f=new Fixture();GuestTransfer guest=new GuestTransfer(f.service,Collections.emptyList(),true,true);
            LanNode node=new LanNode(31236,true,"task-panel","host","任务面板","",quiet())) {
            setField(node,"networkAvailable",true);setField(f.service,"node",node);setField(f.service,"guestTransfer",guest);
            String id="a".repeat(64);File local=new File(f.service.getFilesDir(),"received/"+id);Files.write(local.toPath(),new byte[]{1,2,3});
            f.service.getSharedPreferences("guest_received",0).edit().putString(id,"访客.txt").putString("meta:"+id,new JSONObject().put("size",3).put("time",1000).toString()).commit();
            invoke(f.activity,"showGuest",new Class<?>[0]);Button received=findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"查看已接收文件");assertNotNull(received);assertTrue(received.performClick());settle(f.service);
            assertEquals("guest",field(f.adapter(),"selectedSource"));assertEquals(Arrays.asList("全部","浏览器访客"),sourceNames(f.sources()));assertEquals(1,f.adapter().getCount());
            ChatStore.FileTask item=(ChatStore.FileTask)f.adapter().getItem(0);assertEquals(local,item.guestFile);
            invoke(f.activity,"taskActions",new Class<?>[]{ChatStore.FileTask.class},item);View menu=ShadowDialog.getLatestDialog().getWindow().getDecorView();assertNull(findButton(menu,"查看所在会话"));assertNotNull(findButton(menu,"打开文件"));ShadowDialog.getLatestDialog().dismiss();
            f.dialog().dismiss();f.show();assertEquals("",field(f.adapter(),"selectedSource"));assertEquals(1,f.adapter().getCount());
        }
    }

    @Test public void sourcesUseAllRecordsStableNewestOrderAndRemovedDevicePlaceholders()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer newest=peer("n","最近设备"),alpha=peer("a","Alpha"),zebra=peer("z","Zebra"),empty=peer("e","无记录");alpha.avatar="builtin:3";f.contacts(newest,alpha,zebra,empty);f.show();
            ChatStore.FileTask n=file(newest.id(),1,"n.bin",500),a=file(alpha.id(),2,"a.bin",300),z=file(zebra.id(),3,"z.bin",300),ghostA=file("device:ghost-a",4,"旧设备 A.bin",200),ghostB=file("device:ghost-b",5,"旧设备 B.bin",200),guest=file("browser-guests",6,"访客.bin",400);guest.guestFile=new File(f.service.getFilesDir(),"received/guest-source");
            f.rows(ghostB,z,n,guest,ghostA,a,file(alpha.id(),7,"更旧记录.bin",100));
            assertEquals(Arrays.asList("全部","最近设备","浏览器访客","Alpha","Zebra","已移除的设备","已移除的设备"),sourceNames(f.sources()));
            assertEquals("task-source:peer:device:ghost-a",f.sources().getChildAt(5).getTag());assertEquals("task-source:peer:device:ghost-b",f.sources().getChildAt(6).getTag());
            AvatarDrawable avatar=(AvatarDrawable)((ViewGroup)((ViewGroup)f.sources().getChildAt(3)).getChildAt(0)).getChildAt(0).getBackground();assertEquals("builtin:3",avatar.avatarValue());assertFalse(avatar.isOnline());
            AvatarDrawable placeholder=(AvatarDrawable)((ViewGroup)((ViewGroup)f.sources().getChildAt(5)).getChildAt(0)).getChildAt(0).getBackground();assertNull(placeholder.avatarValue());assertFalse(placeholder.isOnline());
            View stable=f.sources().getChildAt(0);f.search("没有结果");assertEquals(0,f.adapter().getCount());assertSame(stable,f.sources().getChildAt(0));findButton(f.dialog().getWindow().getDecorView(),"已完成").performClick();assertSame(stable,f.sources().getChildAt(0));
            f.dialog().dismiss();f.show("peer:"+empty.id());f.rows(n,a,z,guest,ghostB,ghostA);
            assertEquals("无记录",sourceNames(f.sources()).get(1));assertTrue(f.sources().getChildAt(1).isSelected());assertEquals(0,f.adapter().getCount());
            f.pick("peer:"+alpha.id());assertFalse(sourceNames(f.sources()).contains("无记录"));assertEquals(1,f.adapter().getCount());
        }
    }

    @Test public void unchangedSourcePresentationDoesNotRebuildAndRenameAvatarOrOrderDoes()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer a=peer("a","原名称"),b=peer("b","另一设备");a.avatar="builtin:1";f.contacts(a,b);f.show();
            ChatStore.FileTask first=file(a.id(),1,"first.bin",500),second=file(b.id(),2,"second.bin",300);f.rows(first,second);View stable=f.sources().getChildAt(1);
            for(int i=0;i<5;i++)f.activity.transfersChanged();Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(200));assertSame(stable,f.sources().getChildAt(1));
            first.message.time=600;refresh(f.adapter());assertSame("时间改变但顺序不变时无需重建来源",stable,f.sources().getChildAt(1));
            ChatStore.Conversation contact=f.service.conversations().stream().filter(item->item.peer.id().equals(a.id())).findFirst().orElseThrow();contact.note="当前备注名";contact.peer.avatar="builtin:2";f.activity.transfersChanged();Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(200));
            assertNotSame(stable,f.sources().getChildAt(1));assertEquals("当前备注名",sourceNames(f.sources()).get(1));assertTrue(((TextView)f.taskRow(0).getChildAt(1)).getText().toString().contains("当前备注名"));
            AvatarDrawable avatar=(AvatarDrawable)((ViewGroup)((ViewGroup)f.sources().getChildAt(1)).getChildAt(0)).getChildAt(0).getBackground();assertEquals("builtin:2",avatar.avatarValue());
            second.message.time=700;refresh(f.adapter());assertEquals("另一设备",sourceNames(f.sources()).get(1));
        }
    }

    @Test public void sourceStateAndSearchIntersectWithoutHidingSearchOnSourceSwitch()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer a=peer("a","设备 Alpha"),b=peer("b","设备 Beta");f.contacts(a,b);f.show();
            ChatStore.FileTask pending=file(a.id(),1,"common-pending.txt",100),active=file(a.id(),2,"common-active.txt",100),saved=file(a.id(),3,"common-saved.txt",100),other=file(b.id(),4,"common-other.txt",100),unmatched=file(a.id(),5,"another.txt",100);saved.message.state="已保存";Files.write(f.service.receivedFile(saved.peer,saved.message.number,saved.file).toPath(),new byte[]{1});f.state(active,25,"接收中",new LanNode.Transfer());
            f.rows(pending,active,saved,other,unmatched);f.pick("peer:"+a.id());f.search(" COMMON ");assertEquals(3,f.adapter().getCount());View stableSource=f.sources().getChildAt(0);
            findButton(f.dialog().getWindow().getDecorView(),"进行中").performClick();assertEquals(Collections.singletonList("common-active.txt"),fileNames(f.adapter()));assertSame(stableSource,f.sources().getChildAt(0));
            findButton(f.dialog().getWindow().getDecorView(),"待处理").performClick();assertEquals(Collections.singletonList("common-pending.txt"),fileNames(f.adapter()));
            f.pick("peer:"+b.id());assertEquals(View.VISIBLE,f.input().getVisibility());assertEquals(" COMMON ",f.input().getText().toString());assertEquals(Collections.singletonList("common-other.txt"),fileNames(f.adapter()));
            f.pick("peer:"+a.id());findButton(f.dialog().getWindow().getDecorView(),"已完成").performClick();assertEquals(Collections.singletonList("common-saved.txt"),fileNames(f.adapter()));assertTrue(((TextView)field(f.adapter(),"count")).getText().toString().startsWith("1 个文件 · 1 个已完成"));
            findButton(f.dialog().getWindow().getDecorView(),"全部").performClick();f.pick("");f.search("设备 Alpha");assertEquals(4,f.adapter().getCount());assertFalse(fileNames(f.adapter()).contains("common-other.txt"));
        }
    }

    @Test public void searchStaysVisibleDebouncesAndBackClosesPageWithoutForcedKeyboard()throws Exception {
        try(Fixture f=new Fixture()) {
            f.show();f.rows(file("unknown",1,"needle.txt",100),file("unknown",2,"other.txt",100));
            EditText input=f.input();assertNull(findDescription(f.dialog().getWindow().getDecorView(),"搜索"));assertEquals("搜索文件或设备",input.getHint());assertEquals(View.VISIBLE,input.getVisibility());
            assertFalse(input.hasFocus());assertFalse(Shadows.shadowOf(f.activity.getSystemService(InputMethodManager.class)).isSoftInputVisible());
            Changes changes=new Changes();f.adapter().registerDataSetObserver(changes);
            input.setText("n");input.setText("ne");input.setText("needle");assertEquals(0,changes.count);
            Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(150));assertEquals(1,changes.count);assertEquals(1,f.adapter().getCount());
            f.search("无匹配");assertEquals("没有匹配的文件或设备",((TextView)field(f.adapter(),"empty")).getText());
            f.search("");findButton(f.dialog().getWindow().getDecorView(),"已完成").performClick();assertEquals("当前筛选下没有传输任务",((TextView)field(f.adapter(),"empty")).getText());
            input.setText("needle");BaseAdapter adapter=f.adapter();f.activity.onBackPressed();Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(250));assertNull(field(f.activity,"tasksDialog"));assertNull(field(f.activity,"taskAdapter"));assertFalse(f.activity.isFinishing());assertFalse(((android.os.Handler)field(adapter,"clockHandler")).hasCallbacks((Runnable)field(adapter,"deferredRefresh")));
        }
    }

    @Test public void taskTimeUsesLocalPadded24HourFormatsAndOmitsMissingOrInvalidValues()throws Exception {
        TimeZone previous=TimeZone.getDefault();TimeZone.setDefault(TimeZone.getTimeZone("Asia/Shanghai"));
        try {
            long now=time("2026-11-25 14:00");assertEquals("08:05",taskTime(time("2026-11-25 08:05"),now));assertEquals("02-03 08:05",taskTime(time("2026-02-03 08:05"),now));assertEquals("2025-02-03 08:05",taskTime(time("2025-02-03 08:05"),now));
            assertEquals("",taskTime(0,now));assertEquals("",taskTime(-1,now));assertEquals("",taskTime(Long.MAX_VALUE,now));assertEquals("11-25 08:05",taskTime(time("2026-11-25 08:05"),time("2026-11-26 00:00")));assertEquals("2026-11-25 08:05",taskTime(time("2026-11-25 08:05"),time("2027-01-01 00:00")));
            TimeZone.setDefault(TimeZone.getTimeZone("UTC"));assertEquals("00:05",taskTime(time("2026-11-25 00:05"),time("2026-11-25 14:00")));
        }finally{TimeZone.setDefault(previous);}
    }

    @Test public void rowTimeParticipatesInSignatureAndMidnightRefreshWithoutSourceRebuild()throws Exception {
        TimeZone previous=TimeZone.getDefault();TimeZone.setDefault(TimeZone.getTimeZone("Asia/Shanghai"));
        try {
            AtomicLong now=new AtomicLong(time("2026-11-25 23:59"));
            try(Fixture f=new Fixture()) {
                f.show();setField(f.adapter(),"clock",(java.util.function.LongSupplier)now::get);invoke(f.adapter(),"scheduleDayRefresh",new Class<?>[0]);ChatStore.FileTask item=file("unknown",1,"文件.txt",time("2026-11-25 08:05"));f.rows(item);View source=f.sources().getChildAt(1);Changes changes=new Changes();f.adapter().registerDataSetObserver(changes);
                assertEquals("来自 已移除的设备 · 1 B · 08:05",((TextView)f.taskRow(0).getChildAt(1)).getText().toString());for(int i=0;i<5;i++)refresh(f.adapter());assertEquals(0,changes.count);assertSame(source,f.sources().getChildAt(1));
                item.message.time+=1000;refresh(f.adapter());assertEquals("秒变化未改变展示时间时不刷新",0,changes.count);assertSame(source,f.sources().getChildAt(1));
                now.set(time("2026-11-26 00:00"));Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMinutes(1));assertEquals(1,changes.count);assertSame(source,f.sources().getChildAt(1));assertEquals("来自 已移除的设备 · 1 B · 11-25 08:05",((TextView)f.taskRow(0).getChildAt(1)).getText().toString());
                item.message.outgoing=true;item.message.time=0;refresh(f.adapter());assertEquals("发送给 已移除的设备 · 1 B",((TextView)f.taskRow(0).getChildAt(1)).getText().toString());item.message.time=-1;refresh(f.adapter());int unchanged=changes.count;item.message.time=Long.MAX_VALUE;refresh(f.adapter());assertEquals(unchanged,changes.count);assertFalse(((TextView)f.taskRow(0).getChildAt(1)).getText().toString().contains("1970"));
                BaseAdapter adapter=f.adapter();android.os.Handler clock=(android.os.Handler)field(adapter,"clockHandler");Runnable callback=(Runnable)field(adapter,"dayRefresh");assertTrue(clock.hasCallbacks(callback));f.dialog().dismiss();Shadows.shadowOf(Looper.getMainLooper()).idle();assertNull(field(f.activity,"tasksDialog"));assertFalse(clock.hasCallbacks(callback));now.set(time("2026-11-28 00:00"));Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofDays(2));assertEquals(unchanged,changes.count);
            }
        }finally{TimeZone.setDefault(previous);}
    }

    @Test public void continueFromHeaderAndRowUsesDisplayedIntersectionAndLeavesOutsideActiveQueue()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer a=peer("a","设备 A"),b=peer("b","设备 B");f.contacts(a,b);f.show();
            ChatStore.FileTask inside=file(a.id(),1,"match-pending.txt",100),hiddenSearch=file(a.id(),2,"hidden.txt",100),hiddenState=file(a.id(),3,"match-saved.txt",100),outsideQueue=file(b.id(),4,"match-outside.txt",100),outsideActive=file(b.id(),5,"active.txt",100);
            Files.write(f.service.receivedFile(hiddenState.peer,hiddenState.message.number,hiddenState.file).toPath(),new byte[]{1});hiddenState.message.state="已保存";
            LanNode.Transfer active=new LanNode.Transfer();setActive(f.service,b,outsideActive,active);Object queued=enqueue(f.service,b,outsideQueue);f.state(outsideQueue,10,"排队中",null);f.state(inside,20,"已暂停",null);f.state(hiddenSearch,20,"已暂停",null);
            f.rows(inside,hiddenSearch,hiddenState,outsideQueue,outsideActive);f.pick("peer:"+a.id());f.search("match");findButton(f.dialog().getWindow().getDecorView(),"待处理").performClick();assertEquals(Collections.singletonList("match-pending.txt"),fileNames(f.adapter()));
            Button resume=findButton(f.menu(),"继续当前列表中的接收");assertNotNull(resume);assertTrue(resume.performClick());assertEquals("排队中",f.service.progress(inside.peer,inside.message.number,inside.file.id,false).state);assertEquals("已暂停",f.service.progress(hiddenSearch.peer,hiddenSearch.message.number,hiddenSearch.file.id,false).state);assertNull(f.service.progress(hiddenState.peer,hiddenState.message.number,hiddenState.file.id,false));assertSame(queued,waiting(f.service).get(f.service.receivedFile(outsideQueue.peer,outsideQueue.message.number,outsideQueue.file).getName()));assertFalse(active.isPaused());assertEquals("接收中",f.service.progress(outsideActive.peer,outsideActive.message.number,outsideActive.file.id,false).state);
            waiting(f.service).remove(f.service.receivedFile(inside.peer,inside.message.number,inside.file).getName());f.state(inside,20,"已暂停",null);refresh(f.adapter());invoke(f.activity,"taskActions",new Class<?>[]{ChatStore.FileTask.class},inside);
            resume=findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"继续当前列表中的接收");assertNotNull(resume);assertTrue(resume.performClick());assertEquals("排队中",f.service.progress(inside.peer,inside.message.number,inside.file.id,false).state);assertEquals("已暂停",f.service.progress(hiddenSearch.peer,hiddenSearch.message.number,hiddenSearch.file.id,false).state);assertSame(queued,waiting(f.service).get(f.service.receivedFile(outsideQueue.peer,outsideQueue.message.number,outsideQueue.file).getName()));assertFalse(active.isPaused());
        }
    }

    @Test public void pauseOnlyDisplayedQueueThenActiveAndPreservesOutsideTasks()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer a=peer("a","设备 A"),b=peer("b","设备 B");f.contacts(a,b);f.show();
            ChatStore.FileTask activeItem=file(a.id(),1,"match-active.txt",100),insideQueue=file(a.id(),2,"match-queued.txt",100),hiddenSearch=file(a.id(),3,"hidden-queued.txt",100),outsideQueue=file(b.id(),4,"match-outside.txt",100),hiddenState=file(a.id(),5,"match-not-running.txt",100);
            LanNode.Transfer active=new LanNode.Transfer();setActive(f.service,a,activeItem,active);enqueue(f.service,a,insideQueue);Object hidden=enqueue(f.service,a,hiddenSearch),outside=enqueue(f.service,b,outsideQueue);
            f.state(insideQueue,10,"排队中",null);f.state(hiddenSearch,10,"排队中",null);f.state(outsideQueue,10,"排队中",null);
            f.rows(activeItem,insideQueue,hiddenSearch,outsideQueue,hiddenState);f.pick("peer:"+a.id());f.search("match");findButton(f.dialog().getWindow().getDecorView(),"进行中").performClick();assertEquals(Arrays.asList("match-active.txt","match-queued.txt"),fileNames(f.adapter()));
            Button pause=findButton(f.menu(),"暂停当前列表中的接收");assertNotNull(pause);assertTrue(pause.performClick());assertTrue(active.isPaused());assertEquals("已暂停",f.service.progress(insideQueue.peer,insideQueue.message.number,insideQueue.file.id,false).state);assertFalse(waiting(f.service).containsKey(f.service.receivedFile(insideQueue.peer,insideQueue.message.number,insideQueue.file).getName()));assertSame(hidden,waiting(f.service).get(f.service.receivedFile(hiddenSearch.peer,hiddenSearch.message.number,hiddenSearch.file).getName()));assertSame(outside,waiting(f.service).get(f.service.receivedFile(outsideQueue.peer,outsideQueue.message.number,outsideQueue.file).getName()));assertEquals("排队中",f.service.progress(outsideQueue.peer,outsideQueue.message.number,outsideQueue.file.id,false).state);assertNull(f.service.progress(hiddenState.peer,hiddenState.message.number,hiddenState.file.id,false));
            LanNode.Transfer outsideActive=new LanNode.Transfer();setActive(f.service,b,outsideQueue,outsideActive);enqueue(f.service,a,insideQueue);f.state(insideQueue,10,"排队中",null);f.rows(insideQueue,outsideQueue);f.search("match");refresh(f.adapter());assertEquals(Collections.singletonList("match-queued.txt"),fileNames(f.adapter()));
            pause=findButton(f.menu(),"暂停当前列表中的接收");assertTrue(pause.performClick());assertFalse("列表外活跃任务不能暂停",outsideActive.isPaused());assertEquals("接收中",f.service.progress(outsideQueue.peer,outsideQueue.message.number,outsideQueue.file.id,false).state);assertEquals("已暂停",f.service.progress(insideQueue.peer,insideQueue.message.number,insideQueue.file.id,false).state);
        }
    }

    @Test public void selectedSourceConversationActionLocatesHistoricalMessageAndKeepsMissingConversationOpen()throws Exception {
        try(Fixture f=new Fixture()) {
            LanNode.Peer a=peer("a","文件会话"),b=peer("b","当前会话");f.contacts(a,b);ChatStore.FileTask target=file(a.id(),1,"历史文件.txt",1000);
            f.io.submit(()->{f.store.message(a,target.message,false,false);for(int i=2;i<205;i++){LanNode.Message message=new LanNode.Message();message.number=i;message.time=1000+i;message.text="后来的普通消息 "+i;message.state="已收到";f.store.message(a,message,false,false);}}).get(5,TimeUnit.SECONDS);
            invoke(f.activity,"open",new Class<?>[]{String.class},b.id());settle(f.service);f.show("peer:"+a.id());assertEquals(1,f.adapter().getCount());
            invoke(f.activity,"taskActions",new Class<?>[]{ChatStore.FileTask.class},f.adapter().getItem(0));Button locate=findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"查看所在会话");assertNotNull(locate);assertTrue(locate.performClick());settle(f.service);
            assertNull(field(f.activity,"tasksDialog"));assertNull(field(f.activity,"taskAdapter"));assertEquals(a.id(),field(f.activity,"selected"));View root=(View)field(f.activity,"root");measure(root,786,1500);((ScrollView)field(f.activity,"scroll")).getViewTreeObserver().dispatchOnPreDraw();assertEquals("1:false",field(f.activity,"locatedMessage"));
            f.io.submit(()->f.store.deleteMessages(a.id(),Collections.singletonList(target.message))).get(5,TimeUnit.SECONDS);f.show("peer:"+a.id());f.rows(target);
            invoke(f.activity,"taskActions",new Class<?>[]{ChatStore.FileTask.class},target);findButton(ShadowDialog.getLatestDialog().getWindow().getDecorView(),"查看所在会话").performClick();settle(f.service);measure((View)field(f.activity,"root"),786,1500);((ScrollView)field(f.activity,"scroll")).getViewTreeObserver().dispatchOnPreDraw();
            assertEquals(a.id(),field(f.activity,"selected"));assertNull(field(f.activity,"tasksDialog"));assertEquals("这条消息已删除",ShadowToast.getTextOfLatestToast());assertFalse(f.activity.isFinishing());
        }
    }

    @Test public void sourceSelectionAndSearchRenderLightDarkAndOverflowIsScrollable()throws Exception {
        long now=time("2026-11-25 14:20");
        for(boolean dark:new boolean[]{false,true}) {
            RuntimeEnvironment.getApplication().getSharedPreferences("settings",0).edit().putBoolean("dark_mode",dark).commit();
            try(Fixture f=new Fixture()) {
                LanNode.Peer[] contacts=new LanNode.Peer[10];List<ChatStore.FileTask> rows=new ArrayList<>();
                for(int i=0;i<contacts.length;i++){contacts[i]=peer("preview-"+i,i==1?"中文 English 很长联系人名称":"联系人 "+i);contacts[i].avatar="builtin:"+i;for(int j=0;j<(i==1?8:1);j++)rows.add(file(contacts[i].id(),i*10+j+1,j==0?"中文与 English 很长文件名-Project-delivery-2026.zip":"项目资料 "+j+".pdf",now-i*60000));}
                contacts[0].avatar=customAvatar();f.contacts(contacts);f.show("peer:"+contacts[1].id());setField(f.adapter(),"clock",(java.util.function.LongSupplier)()->now);f.rows(rows.toArray(new ChatStore.FileTask[0]));View decor=f.dialog().getWindow().getDecorView();measure(decor,786,1500);Shadows.shadowOf(Looper.getMainLooper()).idle();measure(decor,786,1500);
                int accent=(Integer)field(f.activity,"BLUE");View selected=f.sources().findViewWithTag("task-source:peer:"+contacts[1].id());assertTrue(selected.isSelected());assertEquals(accent,((TextView)((ViewGroup)selected).getChildAt(1)).getCurrentTextColor());assertEquals(dp(f.activity,68),selected.getWidth());View circle=((ViewGroup)selected).getChildAt(0);assertEquals(dp(f.activity,44),circle.getWidth());assertEquals(dp(f.activity,40),((ViewGroup)circle).getChildAt(0).getWidth());
                assertNull(findDescription(decor,"向右滚动来源"));assertNull(findDescription(decor,"向左滚动来源"));HorizontalScrollView scroller=(HorizontalScrollView)field(f.adapter(),"sourceScroll");View sourceBar=(View)field(f.adapter(),"sourceBar");assertEquals(sourceBar.getWidth()-sourceBar.getPaddingLeft()-sourceBar.getPaddingRight(),scroller.getWidth());assertTrue(f.sources().getWidth()>scroller.getWidth());preview(decor,"transfers-sources-"+(dark?"dark":"light")+".png");
                f.search("项目");Shadows.shadowOf(Looper.getMainLooper()).idle();measure(decor,786,1500);assertTrue(f.input().hasFocus());assertEquals(View.VISIBLE,f.input().getVisibility());assertEquals(7,f.adapter().getCount());preview(decor,"transfers-sources-search-"+(dark?"dark":"light")+".png");
                f.pick("peer:"+contacts[9].id());Shadows.shadowOf(Looper.getMainLooper()).idle();measure(decor,786,1500);selected=f.sources().findViewWithTag("task-source:peer:"+contacts[9].id());assertTrue(scroller.getScrollX()>0);assertTrue(selected.getLeft()>=scroller.getScrollX());assertTrue(selected.getRight()<=scroller.getScrollX()+scroller.getWidth());assertEquals(View.VISIBLE,f.input().getVisibility());assertEquals(0,f.adapter().getCount());preview(decor,"transfers-sources-scrolled-"+(dark?"dark":"light")+".png");
                int before=scroller.getScrollX();
                long downTime=SystemClock.uptimeMillis();float y=scroller.getHeight()/2f;
                MotionEvent down=MotionEvent.obtain(downTime,downTime,MotionEvent.ACTION_DOWN,dp(f.activity,60),y,0);
                MotionEvent startDrag=MotionEvent.obtain(downTime,downTime+50,MotionEvent.ACTION_MOVE,dp(f.activity,120),y,0);
                MotionEvent move=MotionEvent.obtain(downTime,downTime+100,MotionEvent.ACTION_MOVE,dp(f.activity,250),y,0);
                MotionEvent up=MotionEvent.obtain(downTime,downTime+200,MotionEvent.ACTION_UP,dp(f.activity,250),y,0);
                try {
                    assertTrue(scroller.dispatchTouchEvent(down));
                    assertTrue(scroller.dispatchTouchEvent(startDrag));
                    assertTrue(scroller.dispatchTouchEvent(move));
                    assertTrue(scroller.dispatchTouchEvent(up));
                    assertTrue("直接横滑应改变来源位置",scroller.getScrollX()<before);
                } finally {down.recycle();startDrag.recycle();move.recycle();up.recycle();}
            }
        }
    }

    @Test @Config(qualifiers="w640dp-h360dp-xhdpi") public void landscapeLargeFontAndImeInsetsLeaveScrollableListVisible()throws Exception {
        RuntimeEnvironment.setFontScale(1.8f);long now=time("2026-11-25 14:20");
        try(Fixture f=new Fixture()) {
            LanNode.Peer peer=peer("large","很长的联系人名称 Long device name");f.contacts(peer);f.show("peer:"+peer.id());setField(f.adapter(),"clock",(java.util.function.LongSupplier)()->now);ChatStore.FileTask[] rows=new ChatStore.FileTask[15];for(int i=0;i<rows.length;i++)rows[i]=file(peer.id(),i,"中文 English 项目文件 "+i+".txt",now);f.rows(rows);f.search("项目");
            View decor=f.dialog().getWindow().getDecorView();measure(decor,1280,720);ListView list=(ListView)findDescription(decor,"传输任务列表");assertTrue("横屏大字号列表没有可见高度",list.getHeight()>=dp(f.activity,80));assertEquals(15,f.adapter().getCount());assertTrue(list.canScrollVertically(1));
            LinearLayout page=(LinearLayout)((ViewGroup)f.input().getParent()).getParent().getParent();WindowInsets insets=new WindowInsets.Builder().setInsets(WindowInsets.Type.systemBars(),Insets.of(0,dp(f.activity,24),0,dp(f.activity,24))).setInsets(WindowInsets.Type.ime(),Insets.of(0,0,0,dp(f.activity,100))).build();page.dispatchApplyWindowInsets(insets);measure(decor,1280,720);
            assertEquals(dp(f.activity,100),page.getPaddingBottom());assertTrue("输入法 inset 后列表应保留空间",list.getHeight()>dp(f.activity,44));assertTrue(list.canScrollVertically(1));assertEquals(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE,f.dialog().getWindow().getAttributes().softInputMode&WindowManager.LayoutParams.SOFT_INPUT_MASK_ADJUST);
            ScrollView controls=(ScrollView)f.input().getParent().getParent();assertTrue("高度不足时顶部筛选区应可滚动",controls.canScrollVertically(1));preview(decor,"transfers-sources-landscape-large-font-ime.png");
        }finally{RuntimeEnvironment.setFontScale(1f);}
    }

    private static final class Changes extends android.database.DataSetObserver {int count;@Override public void onChanged(){count++;}}
    private static LanNode.Peer peer(String id,String name)throws Exception {LanNode.Peer peer=new LanNode.Peer(LanNode.endpoint("127.0.0.1:31234"),id,"test",name,"");peer.conversationId="device:"+id;return peer;}
    private static ChatStore.FileTask file(String peer,long packet,String name,long time) {LanNode.Message message=new LanNode.Message();message.number=packet;message.time=time;message.text="[文件] "+name;message.state="已收到";Protocol.Attachment file=new Protocol.Attachment(0,name,1,1);message.files.add(file);return new ChatStore.FileTask(peer,message,file);}
    @SuppressWarnings("unchecked") private static Map<String,ChatService.FileProgress> progress(ChatService service)throws Exception {return (Map<String,ChatService.FileProgress>)field(service,"fileProgress");}
    @SuppressWarnings("unchecked") private static Map<String,Object> waiting(ChatService service)throws Exception {return (Map<String,Object>)field(service,"waiting");}
    private static Object request(LanNode.Peer peer,ChatStore.FileTask item)throws Exception {Class<?> type=Class.forName(ChatService.class.getName()+"$ReceiveRequest");Constructor<?> constructor=type.getDeclaredConstructor(LanNode.Peer.class,long.class,Protocol.Attachment.class);constructor.setAccessible(true);return constructor.newInstance(peer,item.message.number,item.file);}
    private static Object enqueue(ChatService service,LanNode.Peer peer,ChatStore.FileTask item)throws Exception {Object request=request(peer,item);waiting(service).put(service.receivedFile(item.peer,item.message.number,item.file).getName(),request);return request;}
    private static void setActive(ChatService service,LanNode.Peer peer,ChatStore.FileTask item,LanNode.Transfer task)throws Exception {Object request=request(peer,item);setField(request,"task",task);setField(service,"activeReceive",request);setField(service,"transfer",task);((AtomicBoolean)field(service,"busy")).set(true);progress(service).put(ChatService.fileKey(item.peer,item.message.number,item.file.id,false),new ChatService.FileProgress(25,"接收中",task));}
    private static LanNode.Listener quiet(){return new LanNode.Listener(){public void peersChanged(){}public void error(String text){}public void message(LanNode.Peer peer,LanNode.Message message,boolean update){}};}
    private static Object field(Object target,String name)throws Exception {Field field=target.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(target);}
    private static void setField(Object target,String name,Object value)throws Exception {Field field=target.getClass().getDeclaredField(name);field.setAccessible(true);field.set(target,value);}
    private static Object invoke(Object target,String name,Class<?>[] types,Object... arguments)throws Exception {Method method=target.getClass().getDeclaredMethod(name,types);method.setAccessible(true);return method.invoke(target,arguments);}
    private static void refresh(BaseAdapter adapter)throws Exception {invoke(adapter,"refresh",new Class<?>[0]);}
    private static void settle(ChatService service)throws Exception {ExecutorService io=(ExecutorService)field(service,"io");for(int i=0;i<3;i++){io.submit(()->{}).get(5,TimeUnit.SECONDS);Shadows.shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(100));}}
    private static long time(String text)throws Exception {return new SimpleDateFormat("yyyy-MM-dd HH:mm",Locale.CHINA).parse(text).getTime();}
    private static String taskTime(long time,long now)throws Exception {return (String)invokeStatic("taskTime",new Class<?>[]{long.class,long.class},time,now);}
    private static Object invokeStatic(String name,Class<?>[] types,Object... arguments)throws Exception {Method method=MainActivity.class.getDeclaredMethod(name,types);method.setAccessible(true);return method.invoke(null,arguments);}
    private static List<String> sourceNames(LinearLayout sources){List<String> names=new ArrayList<>();for(int i=0;i<sources.getChildCount();i++)names.add(((TextView)((ViewGroup)sources.getChildAt(i)).getChildAt(1)).getText().toString());return names;}
    private static List<String> fileNames(BaseAdapter adapter){List<String> names=new ArrayList<>();for(int i=0;i<adapter.getCount();i++)names.add(((ChatStore.FileTask)adapter.getItem(i)).file.name);return names;}
    private static View findDescription(View view,String description){if(description.contentEquals(view.getContentDescription()==null?"":view.getContentDescription()))return view;if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){View found=findDescription(((ViewGroup)view).getChildAt(i),description);if(found!=null)return found;}return null;}
    private static Button findButton(View view,String label){if(view instanceof Button&&label.contentEquals(((Button)view).getText()))return (Button)view;if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){Button found=findButton(((ViewGroup)view).getChildAt(i),label);if(found!=null)return found;}return null;}
    private static int dp(MainActivity activity,float value){return Math.round(value*activity.getResources().getDisplayMetrics().density);}
    private static void measure(View view,int width,int height){view.measure(View.MeasureSpec.makeMeasureSpec(width,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(height,View.MeasureSpec.EXACTLY));view.layout(0,0,width,height);}
    private static String customAvatar()throws Exception {Bitmap image=Bitmap.createBitmap(128,128,Bitmap.Config.ARGB_8888);image.eraseColor(Color.rgb(228,124,60));try(ByteArrayOutputStream output=new ByteArrayOutputStream()){assertTrue(image.compress(Bitmap.CompressFormat.JPEG,85,output));return "image:"+Base64.getEncoder().encodeToString(output.toByteArray());}finally{image.recycle();}}
    private static void preview(View view,String name)throws Exception {File output=new File(System.getProperty("feiq.previewDir"));assertTrue(output.isDirectory()||output.mkdirs());Bitmap bitmap=Bitmap.createBitmap(view.getWidth(),view.getHeight(),Bitmap.Config.ARGB_8888);try{view.draw(new Canvas(bitmap));try(OutputStream stream=new FileOutputStream(new File(output,name))){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,stream));}}finally{bitmap.recycle();}}
}
