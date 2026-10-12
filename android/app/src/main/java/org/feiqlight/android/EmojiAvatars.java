package org.feiqlight.android;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

/** 和 Windows 打包同一份头像目录/图集，只在本地按稳定联系人身份选取。 */
final class EmojiAvatars {
    private static EmojiAvatars instance;
    final Bitmap atlas;
    private final List<String[]> catalog = new ArrayList<>();
    private EmojiAvatars(Context context) {
        try (InputStream image = context.getAssets().open("atlas.png");
             BufferedReader reader = new BufferedReader(new InputStreamReader(context.getAssets().open("catalog.txt"), StandardCharsets.UTF_8))) {
            atlas = BitmapFactory.decodeStream(image);
            String line;
            while ((line = reader.readLine()) != null) if (!line.isEmpty()) catalog.add(line.split("\\|"));
            if (atlas == null || catalog.isEmpty()) throw new IOException("内置头像资源不完整");
        } catch (IOException error) { throw new IllegalStateException("无法加载内置头像", error); }
    }
    static synchronized EmojiAvatars get(Context context) {
        if (instance == null) instance = new EmojiAvatars(context);
        return instance;
    }
    int count() { return catalog.size(); }
    int index(String identity) {
        // Java hashCode 按 UTF-16 计算；先转 uint32 再取模，和 C# 的 unchecked uint 一致。
        return (int)(Integer.toUnsignedLong(identity == null ? 0 : identity.hashCode()) % count());
    }
    int color(int index) { return Color.parseColor("#" + catalog.get(index)[2]); }
    String description(int index) { return catalog.get(index)[1]; }
}
