package org.feiqlight.android;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.drawable.ColorDrawable;
import android.os.Build;
import android.view.View;
import android.view.Window;
import android.view.WindowInsetsController;
import android.view.WindowManager;

final class SystemBars {
    private static final int LEGACY_DARK_NAV = 0xff17212b;
    @SuppressWarnings("deprecation")
    private static final int ALL_LEGACY_LIGHT_FLAGS =
            View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;

    private SystemBars() {}

    static boolean systemDark(Context context) {
        return (context.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK)
                == Configuration.UI_MODE_NIGHT_YES;
    }

    static boolean isDark(Context context) {
        SharedPreferences prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        if (prefs.getBoolean("follow_system_theme", false)) return systemDark(context);
        return prefs.getBoolean("dark_mode", false);
    }

    static int navigationBarColor(boolean dark, int surface, int sdkInt) {
        return (dark || sdkInt >= 27) ? surface : LEGACY_DARK_NAV;
    }

    @SuppressWarnings("deprecation")
    static int lightBarFlags(boolean dark, int sdkInt) {
        if (dark) return 0;
        int flags = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
        if (sdkInt >= 27) flags |= View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        return flags;
    }

    static int lightBarFlags(boolean dark) {
        return lightBarFlags(dark, Build.VERSION.SDK_INT);
    }

    @SuppressWarnings("deprecation")
    static int legacySystemUiVisibility(int current, boolean dark, int sdkInt) {
        return dark ? (current & ~ALL_LEGACY_LIGHT_FLAGS) : (current | lightBarFlags(false, sdkInt));
    }

    @SuppressWarnings("deprecation")
    static void apply(Window window, int surface, boolean dark) {
        if (window == null) return;
        window.getDecorView();
        window.setBackgroundDrawable(new ColorDrawable(surface));
        window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        window.clearFlags(WindowManager.LayoutParams.FLAG_TRANSLUCENT_STATUS
                | WindowManager.LayoutParams.FLAG_TRANSLUCENT_NAVIGATION);
        window.setStatusBarColor(surface);
        window.setNavigationBarColor(navigationBarColor(dark, surface, Build.VERSION.SDK_INT));
        disableContrastEnforcement(window);
        appearance(window, dark);
    }

    static void applyOverlay(Window window, boolean dark) {
        if (window == null) return;
        disableContrastEnforcement(window);
        appearance(window, dark);
    }

    private static void disableContrastEnforcement(Window window) {
        if (Build.VERSION.SDK_INT >= 29) {
            window.setStatusBarContrastEnforced(false);
            window.setNavigationBarContrastEnforced(false);
        }
    }

    @SuppressWarnings("deprecation")
    static void appearance(Window window, boolean dark) {
        if (window == null) return;
        View decor = window.getDecorView();
        if (Build.VERSION.SDK_INT >= 30) {
            // 先清掉主题 XML 可能带入的旧 SYSTEM_UI_FLAG_LIGHT_*，再由 InsetsController 覆盖最终外观。
            int cleared = decor.getSystemUiVisibility() & ~ALL_LEGACY_LIGHT_FLAGS;
            if (decor.getSystemUiVisibility() != cleared) decor.setSystemUiVisibility(cleared);
            WindowInsetsController controller = window.getInsetsController();
            if (controller != null) {
                int mask = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS
                        | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS;
                controller.setSystemBarsAppearance(dark ? 0 : mask, mask);
            }
            return;
        }
        decor.setSystemUiVisibility(legacySystemUiVisibility(decor.getSystemUiVisibility(), dark, Build.VERSION.SDK_INT));
    }
}
