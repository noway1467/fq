package org.feiqlight.android;

import android.content.Context;
import java.io.*;
import org.feiqlight.android.core.SecureIdentity;

final class AppIdentity {
    private static SecureIdentity identity;
    private static File location;
    static synchronized SecureIdentity get(Context context)throws IOException {
        File root=new File(context.getFilesDir(),"security");
        if(identity==null||!root.equals(location)){identity=new SecureIdentity(root);location=root;}
        return identity;
    }
}
