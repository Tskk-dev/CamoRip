package com.camorip.slider;

import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.text.InputType;
import android.view.MotionEvent;
import android.view.View;
import android.widget.ArrayAdapter;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import com.google.android.material.button.MaterialButton;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.dialog.MaterialAlertDialogBuilder;
import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.slider.Slider;
import com.google.android.material.tabs.TabLayout;
import com.google.android.material.textfield.MaterialAutoCompleteTextView;
import com.google.android.material.textfield.TextInputEditText;
import com.google.android.material.textfield.TextInputLayout;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.Supplier;
import org.json.JSONObject;

final class SettingsSheet {
    private final MainActivity activity;
    private final Map<String, Supplier<Object>> values = new LinkedHashMap<>();
    private final Map<String, LinearLayout> pages = new LinkedHashMap<>();
    private androidx.appcompat.app.AlertDialog dialog;

    SettingsSheet(MainActivity activity) { this.activity = activity; }
    private String text(String value) { return SliderText.get(activity, value); }

    void show() {
        LinearLayout root = new LinearLayout(activity); root.setOrientation(LinearLayout.VERTICAL);
        TabLayout tabs = new TabLayout(activity); tabs.setTabMode(TabLayout.MODE_SCROLLABLE); root.addView(tabs);
        LinearLayout connection = page("Connection", root, tabs);
        label(connection, "Connect USB from CamoRip’s Android slider page. For wireless, select the same network mode and port on both devices.");
        choice(connection, "Connection", "mode", new String[] {"USB", "TCP", "UDP"}, new String[] {"usb", "tcp", "udp"}, "usb");
        input(connection, "PC address", "host", "192.168.1.2", false);
        number(connection, "Port", "port", 52468);
        range(connection, "Heartbeat rate", "frequency", 240, 50, 1000, " Hz");
        label(connection, "Touch changes send immediately. Heartbeats only repeat unchanged input.");
        toggle(connection, "Connect on launch", "autoConnect", false);
        LinearLayout slider = page("Slider", root, tabs);
        choice(slider, "Sensor layout", "columns", new String[] {"32 columns", "16 columns × 2 rows"}, new String[] {"32", "16"}, "32");
        range(slider, "Touch overlap", "sensitivity", 8, 0, 50, "%");
        range(slider, "Play area height", "area", 80, 30, 100, "%");
        toggle(slider, "Mirror input", "mirror", false); toggle(slider, "Swap sensor rows", "swapRows", false);
        MaterialSwitch haptic = toggle(slider, "Haptic feedback", "haptics", false);
        android.os.Vibrator vibrator = activity.getSystemService(android.os.Vibrator.class);
        if (vibrator == null || !vibrator.hasVibrator()) { haptic.setChecked(false); haptic.setEnabled(false); }
        toggle(slider, "Sensor grid", "grid", true); toggle(slider, "Input diagnostics", "diagnostics", false);
        action(slider, "Low visual load", () -> {
            activity.preferences.edit().putBoolean("grid", false).putBoolean("diagnostics", false).putBoolean("leds", false)
                .putBoolean("haptics", false).putString("background", "").apply();
            activity.applyPreferences(); dialog.dismiss();
        });
        LinearLayout appearance = page("Appearance", root, tabs);
        choice(appearance, "Theme", "theme", new String[] {"System", "Dark", "Light"}, new String[] {"system", "dark", "light"}, "system");
        toggle(appearance, "Material Expressive", "expressive", true); toggle(appearance, "Dynamic color", "dynamic", true);
        choice(appearance, "Accent color", "seed", new String[] {"Blue", "Violet", "Rose", "Green", "Amber", "Slate"},
            new String[] {"-12231023", "-10011977", "-5420431", "-13209517", "-6278912", "-11577248"}, "-12231023");
        toggle(appearance, "Game LED colors", "leds", true);
        choice(appearance, "Preset LED", "ledPattern", new String[] {"Single color", "Alternating", "Rainbow"}, new String[] {"0", "1", "2"}, "0");
        range(appearance, "LED brightness", "ledBrightness", 100, 0, 100, "%");
        range(appearance, "Background opacity", "backgroundOpacity", 40, 0, 100, "%");
        choice(appearance, "Language", "language", new String[] {"System", "English", "简体中文", "繁體中文", "日本語", "한국어", "Français"},
            new String[] {"", "en", "zh-CN", "zh-TW", "ja", "ko", "fr"}, "");
        action(appearance, "Import background", () -> { dialog.dismiss(); activity.pick(10, "image/*"); });
        action(appearance, "Clear background", () -> { activity.preferences.edit().remove("background").apply(); activity.loadBackground(); });
        action(appearance, "Import skin", () -> { dialog.dismiss(); activity.pick(11, "application/json"); });
        action(appearance, "Export settings", () -> { dialog.dismiss(); activity.pick(12, "application/json"); });
        LinearLayout controls = page("Controls", root, tabs);
        input(controls, "Card access code", "card", "", true);
        label(controls, "20 digits. Stored on this device and excluded from exported settings.");
        action(controls, "Insert card", activity::card); action(controls, "Insert coin", activity::coin);
        hold(controls, "Service", false); hold(controls, "Test", true);
        LinearLayout about = page("About", root, tabs);
        TextView name = label(about, "CamoRip Slider"); name.setTextSize(28);
        label(about, "0.2.0 · Tsk"); label(about, "AIR stays on CamoRip. Display refresh rate follows your tablet’s setting.");
        action(about, "Source code", () -> activity.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/Tskk-dev/CamoRip/tree/feature/android-slider"))));
        action(about, "Support on Ko-fi", () -> activity.startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://ko-fi.com/tskkkdev"))));
        action(about, "Reset settings", () -> new MaterialAlertDialogBuilder(activity).setTitle(text("Reset settings"))
            .setMessage(text("Restore defaults? Your local settings and saved card will be removed."))
            .setNegativeButton(text("Cancel"), null).setPositiveButton(text("Reset"), (ignored, which) -> {
                activity.preferences.edit().clear().apply(); activity.recreate();
            }).show());
        tabs.addOnTabSelectedListener(new TabLayout.OnTabSelectedListener() {
            @Override public void onTabSelected(TabLayout.Tab tab) {
                for (Map.Entry<String, LinearLayout> page : pages.entrySet())
                    ((View)page.getValue().getParent().getParent()).setVisibility(page.getKey().equals(tab.getTag()) ? View.VISIBLE : View.GONE);
            }
            @Override public void onTabUnselected(TabLayout.Tab tab) { }
            @Override public void onTabReselected(TabLayout.Tab tab) { }
        });
        for (Map.Entry<String, LinearLayout> page : pages.entrySet())
            ((View)page.getValue().getParent().getParent()).setVisibility(page.getKey().equals("Connection") ? View.VISIBLE : View.GONE);
        dialog = new MaterialAlertDialogBuilder(activity).setTitle(text("Settings")).setView(root)
            .setNegativeButton(text("Cancel"), null).setPositiveButton(text("Apply"), null).create();
        dialog.setOnShowListener(ignored -> {
            if (dialog.getWindow() != null) dialog.getWindow().setLayout(Math.min(activity.dp(880), (int)(activity.getResources().getDisplayMetrics().widthPixels * 0.94)),
                (int)(activity.getResources().getDisplayMetrics().heightPixels * 0.92));
            dialog.getButton(androidx.appcompat.app.AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> save());
        });
        dialog.setOnDismissListener(ignored -> { activity.holdButton(true, false); activity.holdButton(false, false); });
        dialog.show();
    }

    private LinearLayout page(String title, LinearLayout root, TabLayout tabs) {
        LinearLayout content = new LinearLayout(activity); content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(activity.dp(12), activity.dp(16), activity.dp(12), activity.dp(24));
        MaterialCardView card = new MaterialCardView(activity); card.setRadius(activity.dp(24)); card.setCardElevation(0); card.addView(content);
        ScrollView scroll = new ScrollView(activity); scroll.setFillViewport(true); scroll.addView(card);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1)); pages.put(title, content);
        tabs.addTab(tabs.newTab().setText(text(title)).setTag(title)); return content;
    }

    private TextView label(LinearLayout page, String title) {
        TextView label = new TextView(activity); label.setText(text(title)); label.setPadding(activity.dp(8), activity.dp(12), activity.dp(8), activity.dp(8));
        page.addView(label); return label;
    }

    private void choice(LinearLayout page, String title, String key, String[] labels, String[] options, String fallback) {
        TextInputLayout box = new TextInputLayout(new android.view.ContextThemeWrapper(activity, R.style.SliderDropdown)); box.setHint(text(title));
        MaterialAutoCompleteTextView dropdown = new MaterialAutoCompleteTextView(box.getContext()); dropdown.setInputType(0);
        String[] translated = java.util.Arrays.stream(labels).map(this::text).toArray(String[]::new);
        dropdown.setAdapter(new ArrayAdapter<>(activity, android.R.layout.simple_list_item_1, translated));
        Object raw = activity.preferences.getAll().get(key); String stored = raw == null ? fallback : raw.toString();
        int selected = java.util.Arrays.asList(options).indexOf(stored); dropdown.setText(translated[Math.max(0, selected)], false);
        box.addView(dropdown); page.addView(box); values.put(key, () -> {
            int index = java.util.Arrays.asList(translated).indexOf(dropdown.getText().toString());
            String value = options[Math.max(0, index)];
            return key.equals("columns") || key.equals("seed") || key.equals("ledPattern") ? Integer.parseInt(value) : value;
        });
    }

    private TextInputEditText input(LinearLayout page, String title, String key, String fallback, boolean password) {
        TextInputLayout box = new TextInputLayout(activity); box.setHint(text(title));
        TextInputEditText input = new TextInputEditText(box.getContext()); input.setSingleLine(true);
        Object stored = activity.preferences.getAll().get(key); input.setText(stored == null ? fallback : stored.toString());
        input.setInputType(password ? InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_VARIATION_PASSWORD : InputType.TYPE_CLASS_TEXT);
        if (password) box.setEndIconMode(TextInputLayout.END_ICON_PASSWORD_TOGGLE);
        box.addView(input); page.addView(box); values.put(key, () -> input.getText().toString().trim()); return input;
    }

    private void number(LinearLayout page, String title, String key, int fallback) {
        TextInputEditText input = input(page, title, key, Integer.toString(fallback), false);
        input.setText(Integer.toString(activity.preferences.getInt(key, fallback))); input.setInputType(InputType.TYPE_CLASS_NUMBER);
        values.put(key, () -> Integer.parseInt(input.getText().toString()));
    }

    private MaterialSwitch toggle(LinearLayout page, String title, String key, boolean fallback) {
        MaterialSwitch toggle = new MaterialSwitch(activity); toggle.setText(text(title)); toggle.setChecked(activity.preferences.getBoolean(key, fallback));
        toggle.setPadding(activity.dp(8), activity.dp(6), activity.dp(8), activity.dp(6)); page.addView(toggle); values.put(key, toggle::isChecked); return toggle;
    }

    private void range(LinearLayout page, String title, String key, int fallback, int min, int max, String suffix) {
        int initial = activity.preferences.getInt(key, fallback); TextView label = label(page, title + " · " + initial + suffix);
        Slider slider = new Slider(activity); slider.setValueFrom(min); slider.setValueTo(max); slider.setStepSize(1); slider.setValue(Math.max(min, Math.min(max, initial)));
        slider.addOnChangeListener((view, value, user) -> label.setText(text(title) + " · " + Math.round(value) + suffix));
        page.addView(slider); values.put(key, () -> Math.round(slider.getValue()));
    }

    private void action(LinearLayout page, String title, Runnable action) { page.addView(activity.button(title, action)); }
    private void hold(LinearLayout page, String title, boolean test) {
        MaterialButton button = activity.button(title, () -> { });
        button.setOnTouchListener((view, event) -> {
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN) activity.holdButton(test, true);
            else if (event.getActionMasked() == MotionEvent.ACTION_UP || event.getActionMasked() == MotionEvent.ACTION_CANCEL) activity.holdButton(test, false);
            return false;
        }); page.addView(button);
    }

    private void save() {
        try {
            JSONObject json = new JSONObject();
            for (Map.Entry<String, Supplier<Object>> entry : values.entrySet()) if (!entry.getKey().equals("card")) json.put(entry.getKey(), entry.getValue().get());
            String card = values.get("card").get().toString(); if (!card.isEmpty() && !card.matches("[0-9]{20}")) throw new IllegalArgumentException("Card access code must contain 20 digits");
            SliderConfig.importSkin(activity.preferences, json);
            activity.preferences.edit().putString("card", card).apply(); dialog.dismiss(); activity.recreate();
        } catch (Exception error) { activity.message(error.getMessage() == null ? "Check the settings" : error.getMessage()); }
    }
}
