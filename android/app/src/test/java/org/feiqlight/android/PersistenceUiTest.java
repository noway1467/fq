package org.feiqlight.android;

import android.app.Dialog;
import android.content.*;
import android.graphics.*;
import android.view.*;
import android.widget.TextView;
import java.io.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.shadows.ShadowDialog;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class) @Config(sdk=35) @GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class PersistenceUiTest {
    @Test public void savedRemoteEntryRendersAndReopensSameTarget()throws Exception{
        Context context=RuntimeEnvironment.getApplication();
        org.robolectric.android.controller.ServiceController<ChatService> service=Robolectric.buildService(ChatService.class).create();
        Shadows.shadowOf(RuntimeEnvironment.getApplication()).setComponentNameAndServiceForBindService(new ComponentName(context,ChatService.class),service.get().onBind(new Intent()));
        try{
        for(boolean dark:new boolean[]{false,true}){
            context.getSharedPreferences("settings",Context.MODE_PRIVATE).edit().putBoolean("auto_connect",false).putBoolean("connection_enabled",false).putBoolean("dark_mode",dark).putString("last_remote_target","http://127.0.0.1:2426/#remote").putBoolean("last_remote_phone",false).commit();
            try(ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).setup()){
                MainActivity activity=controller.get();java.lang.reflect.Method menu=MainActivity.class.getDeclaredMethod("showMenu",View.class);menu.setAccessible(true);menu.invoke(activity,activity.findViewById(android.R.id.content));
                Dialog dialog=ShadowDialog.getLatestDialog();assertNotNull(dialog);View view=dialog.getWindow().getDecorView();
                view.measure(View.MeasureSpec.makeMeasureSpec(720,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(1200,View.MeasureSpec.EXACTLY));view.layout(0,0,720,1200);
                TextView last=find(view,"上次远程连接");assertNotNull(last);
                Bitmap image=Bitmap.createBitmap(720,1200,Bitmap.Config.ARGB_8888);view.draw(new Canvas(image));File directory=new File(System.getProperty("feiq.previewDir"));assertTrue(directory.isDirectory()||directory.mkdirs());
                try(OutputStream output=new FileOutputStream(new File(directory,"persist-menu-"+(dark?"dark":"light")+".png"))){assertTrue(image.compress(Bitmap.CompressFormat.PNG,100,output));}finally{image.recycle();}
                last.performClick();Intent started=Shadows.shadowOf(activity).getNextStartedActivity();assertNotNull(started);assertEquals("http://127.0.0.1:2426/#remote",started.getDataString());dialog.dismiss();
            }
        }
        }finally{service.destroy();}
    }
    private static TextView find(View view,String text){if(view instanceof TextView&&text.contentEquals(((TextView)view).getText()))return (TextView)view;if(view instanceof ViewGroup)for(int i=0;i<((ViewGroup)view).getChildCount();i++){TextView result=find(((ViewGroup)view).getChildAt(i),text);if(result!=null)return result;}return null;}
}
