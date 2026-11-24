package local.dictationrelay;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.Build;
import java.util.*;

/** Cosmetic clock changes never start the microphone or require exact-alarm privileges. */
public final class ThemeClock extends BroadcastReceiver {
    static boolean night() { return nightAt(Calendar.getInstance()); }
    static boolean nightAt(Calendar clock) { int hour = clock.get(Calendar.HOUR_OF_DAY); return hour < 7 || hour >= 19; }
    static void apply(Context context) {
        PackageManager pm = context.getPackageManager();
        ComponentName selected = new ComponentName(context, context.getPackageName() + (night() ? ".NightLauncher" : ".DayLauncher"));
        ComponentName other = new ComponentName(context, context.getPackageName() + (night() ? ".DayLauncher" : ".NightLauncher"));
        if (pm.getComponentEnabledSetting(selected) == PackageManager.COMPONENT_ENABLED_STATE_ENABLED && pm.getComponentEnabledSetting(other) == PackageManager.COMPONENT_ENABLED_STATE_DISABLED) return;
        if (Build.VERSION.SDK_INT >= 33) {
            pm.setComponentEnabledSettings(Arrays.asList(
                new PackageManager.ComponentEnabledSetting(selected, PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP),
                new PackageManager.ComponentEnabledSetting(other, PackageManager.COMPONENT_ENABLED_STATE_DISABLED, PackageManager.DONT_KILL_APP)));
        } else {
            pm.setComponentEnabledSetting(selected, PackageManager.COMPONENT_ENABLED_STATE_ENABLED, PackageManager.DONT_KILL_APP);
            pm.setComponentEnabledSetting(other, PackageManager.COMPONENT_ENABLED_STATE_DISABLED, PackageManager.DONT_KILL_APP);
        }
    }
    static long nextBoundary(Calendar current) {
        Calendar next = (Calendar)current.clone(); int hour = next.get(Calendar.HOUR_OF_DAY);
        if (hour >= 19) next.add(Calendar.DAY_OF_YEAR, 1);
        next.set(Calendar.HOUR_OF_DAY, hour >= 7 && hour < 19 ? 19 : 7);
        next.set(Calendar.MINUTE, 0); next.set(Calendar.SECOND, 0); next.set(Calendar.MILLISECOND, 0);
        return next.getTimeInMillis();
    }
    static void schedule(Context context) {
        PendingIntent pending = PendingIntent.getBroadcast(context, 7, new Intent(context, ThemeClock.class).setAction("local.dictationrelay.APPEARANCE"), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        ((AlarmManager)context.getSystemService(Context.ALARM_SERVICE)).set(AlarmManager.RTC, nextBoundary(Calendar.getInstance()), pending);
    }
    @Override public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        if ("local.dictationrelay.APPEARANCE".equals(action) || Intent.ACTION_BOOT_COMPLETED.equals(action) || Intent.ACTION_MY_PACKAGE_REPLACED.equals(action) || Intent.ACTION_TIME_CHANGED.equals(action) || Intent.ACTION_TIMEZONE_CHANGED.equals(action)) { apply(context); schedule(context); }
    }
}
