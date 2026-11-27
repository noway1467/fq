package org.feiqlight.android;

import android.app.Dialog;
import android.widget.Button;
import android.widget.ImageButton;
import android.widget.TextView;
import java.util.ArrayList;
import java.util.List;

final class AvatarSelection {
    String value;
    Dialog dialog;
    TextView preview;
    Button confirm,local;
    int revision;
    boolean loading;
    final List<ImageButton> choices=new ArrayList<>();
}
