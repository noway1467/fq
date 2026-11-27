package org.feiqlight.android;

import android.view.View;
import android.widget.LinearLayout;
import java.util.ArrayList;
import java.util.List;

final class ChatRow {
    String signature;
    LinearLayout row,bubble;
    View portrait;
    int previews;
    final List<Runnable> progress=new ArrayList<>();
}
