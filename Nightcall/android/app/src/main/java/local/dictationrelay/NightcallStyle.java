package local.dictationrelay;
import android.app.Activity;
import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.*;
import android.graphics.drawable.*;
import android.view.*;
import android.widget.*;

final class NightcallStyle {
    final boolean night;
    final int panel, ink, muted, accent, secondary, line, actionInk;
    NightcallStyle() {
        night = ThemeClock.night(); panel = color(night ? "#211B36" : "#FFFBF7"); ink = color(night ? "#FFF3E8" : "#173E48");
        muted = color(night ? "#BDAFC7" : "#526772"); accent = color(night ? "#FF53B4" : "#BD2467");
        secondary = color(night ? "#49DFDF" : "#087F85"); line = color(night ? "#593854" : "#DFC6D0"); actionInk = color(night ? "#180C24" : "#FFFFFF");
    }
    static int color(String value) { return Color.parseColor(value); }
    static int dp(Context context, float value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }
    GradientDrawable round(Context c, int fill, int stroke, int radius) {
        GradientDrawable shape = new GradientDrawable(); shape.setColor(fill); shape.setCornerRadius(dp(c, radius));
        if (stroke != 0) shape.setStroke(dp(c, 1), stroke); return shape;
    }
    Drawable backdrop() { return new GradientDrawable(GradientDrawable.Orientation.TL_BR,
        new int[] { color(night ? "#102D38" : "#DBF5EE"), color(night ? "#100F24" : "#FFF2EB"), color(night ? "#30132D" : "#FFDAE8") }); }
    TextView text(Context c, String value, int size, boolean quiet) {
        TextView view = new TextView(c); view.setText(value); view.setTextSize(size); view.setTextColor(quiet ? muted : ink); view.setLineSpacing(dp(c, 3), 1); return view;
    }
    LinearLayout card(Context c) {
        LinearLayout card = new LinearLayout(c); card.setOrientation(LinearLayout.VERTICAL); int pad = dp(c, 18); card.setPadding(pad, pad, pad, pad);
        card.setBackground(round(c, panel, line, 22)); LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(-1, -2); layout.bottomMargin = dp(c, 14); card.setLayoutParams(layout); return card;
    }
    Button button(Context c, String label, boolean primary, Runnable action) {
        Button button = new Button(c); button.setText(label); button.setAllCaps(false); button.setTextSize(15); button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setTextColor(primary ? actionInk : ink); button.setMinHeight(dp(c, 52)); button.setPadding(dp(c, 16), dp(c, 10), dp(c, 16), dp(c, 10));
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), round(c, primary ? accent : panel, primary ? 0 : line, 15), null));
        LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(-1, -2); layout.bottomMargin = dp(c, 10); button.setLayoutParams(layout); button.setOnClickListener(v -> action.run()); return button;
    }
    View header(Context c) {
        LinearLayout row = new LinearLayout(c); row.setGravity(Gravity.CENTER_VERTICAL);
        ImageView image = new ImageView(c); image.setImageResource(night ? R.drawable.nightcall_night : R.drawable.nightcall_day); image.setScaleType(ImageView.ScaleType.CENTER_CROP);
        image.setBackground(round(c, panel, 0, 22)); image.setClipToOutline(true); LinearLayout.LayoutParams tile = new LinearLayout.LayoutParams(dp(c, 92), dp(c, 92)); tile.rightMargin = dp(c, 18); row.addView(image, tile);
        LinearLayout words = new LinearLayout(c); words.setOrientation(LinearLayout.VERTICAL);
        TextView title = text(c, "Nightcall", 31, false); title.setTypeface(Typeface.create("sans-serif-medium", Typeface.BOLD)); words.addView(title);
        words.addView(text(c, "Across the airwaves.", 13, true)); row.addView(words, new LinearLayout.LayoutParams(0, -2, 1));
        LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(-1, -2); layout.bottomMargin = dp(c, 24); row.setLayoutParams(layout); return row;
    }
    void window(Activity activity) {
        activity.getWindow().setStatusBarColor(color(night ? "#102D38" : "#DBF5EE"));
        activity.getWindow().setNavigationBarColor(color(night ? "#30132D" : "#FFDAE8"));
        activity.getWindow().getDecorView().setSystemUiVisibility(night ? 0 : View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
    }
}
