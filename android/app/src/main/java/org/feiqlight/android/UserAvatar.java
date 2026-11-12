package org.feiqlight.android;

import android.content.Context;
import android.graphics.*;
import android.net.Uri;
import org.feiqlight.android.core.Protocol;
import java.io.*;
import java.util.Base64;

final class UserAvatar {
    private UserAvatar() { }
    static String read(Context context,Uri uri) throws IOException {
        if(uri==null || !"content".equals(uri.getScheme()))throw new IOException("请选择系统图片选择器授权的图片");
        ByteArrayOutputStream original=new ByteArrayOutputStream();byte[] buffer=new byte[8192];
        // 提供方可能不给文件大小，实际读取也要限额；不保留 URI 权限或原图副本。
        try(InputStream input=context.getContentResolver().openInputStream(uri)) {
            if(input==null)throw new IOException("无法读取头像图片");
            int count;while((count=input.read(buffer))!=-1){if(original.size()+count>20*1024*1024)throw new IOException("请选择不超过 20 MiB 的图片");original.write(buffer,0,count);}
        }
        byte[] data=original.toByteArray();BitmapFactory.Options options=new BitmapFactory.Options();options.inJustDecodeBounds=true;BitmapFactory.decodeByteArray(data,0,data.length,options);
        if(options.outWidth<=0||options.outHeight<=0||(long)options.outWidth*options.outHeight>40000000)throw new IOException("请选择有效且不超过 4000 万像素的图片");
        options.inJustDecodeBounds=false;options.inSampleSize=1;while(Math.max(options.outWidth,options.outHeight)/options.inSampleSize>512)options.inSampleSize*=2;
        Bitmap source=BitmapFactory.decodeByteArray(data,0,data.length,options);if(source==null)throw new IOException("无法解码头像图片");
        Bitmap target=Bitmap.createBitmap(128,128,Bitmap.Config.ARGB_8888);
        try {
            Canvas canvas=new Canvas(target);canvas.drawColor(Color.WHITE);int side=Math.min(source.getWidth(),source.getHeight());
            Rect crop=new Rect((source.getWidth()-side)/2,(source.getHeight()-side)/2,(source.getWidth()+side)/2,(source.getHeight()+side)/2);
            canvas.drawBitmap(source,crop,new Rect(0,0,128,128),new Paint(Paint.ANTI_ALIAS_FLAG|Paint.FILTER_BITMAP_FLAG));
            for(int quality:new int[]{85,70,55,40}) {
                ByteArrayOutputStream bytes=new ByteArrayOutputStream();if(!target.compress(Bitmap.CompressFormat.JPEG,quality,bytes))throw new IOException("无法生成头像副本");
                if(bytes.size()<=16384){String value="image:"+Base64.getEncoder().encodeToString(bytes.toByteArray());if(Protocol.normalizeAvatar(value)!=null)return value;}
            }
            throw new IOException("图片无法缩成头像，请换一张图片");
        } finally {source.recycle();target.recycle();}
    }
}
