package com.camorip.slider;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.ArrayAdapter;
import android.widget.TextView;

public final class MainActivity extends Activity {
    SliderView slider;
    private SliderConnection connection;
    private SharedPreferences preferences;
    private TextView status;
    private Button connect;
    private boolean requested;
    private boolean foreground;
    private int connectionGeneration;
    private String connectionStatus = "USB slider · CamoRip handles AIR";

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        preferences = getSharedPreferences("slider", MODE_PRIVATE);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON | WindowManager.LayoutParams.FLAG_FULLSCREEN);
        getWindow().setSustainedPerformanceMode(true);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        LinearLayout bar = new LinearLayout(this);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        status = new TextView(this);
        status.setPadding(16, 0, 8, 0);
        bar.addView(status, new LinearLayout.LayoutParams(0, dp(52), 1));
        connect = new Button(this);
        connect.setText("Connect USB");
        connect.setOnClickListener(view -> { requested = !requested; restart(); });
        bar.addView(connect);
        Button settings = new Button(this);
        settings.setText("Settings");
        settings.setOnClickListener(view -> settings());
        bar.addView(settings);
        root.addView(bar);
        slider = new SliderView(this, mask -> { if (connection != null) connection.update(mask); });
        root.addView(slider, new LinearLayout.LayoutParams(-1, 0, 1));
        setContentView(root);
        applyPreferences();
        status.setText(connectionStatus);
        getWindow().getDecorView().postDelayed(new Runnable() {
            @Override public void run() {
                if (isDestroyed()) return;
                SliderConnection current = connection;
                status.setText(connectionStatus + (current == null ? "" : " · TX " + current.packetsSent +
                    " · " + String.format(java.util.Locale.ROOT, "%08X", current.sentMask)));
                getWindow().getDecorView().postDelayed(this, 250);
            }
        }, 250);
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    private void restart() {
        int generation = ++connectionGeneration;
        slider.release();
        if (connection != null) connection.close();
        connection = null;
        connect.setText(requested ? "Disconnect" : "Connect USB");
        connectionStatus = "Disconnected";
        if (!requested || !foreground) return;
        SliderConnection next = new SliderConnection(preferences.getInt("port", 52468),
            preferences.getInt("frequency", 240), new SliderConnection.Observer() {
                @Override public void status(String text, boolean connected) {
                    runOnUiThread(() -> {
                        if (generation != connectionGeneration || connection == null || !requested || !foreground) return;
                        connectionStatus = text;
                        slider.release();
                    });
                }
                @Override public void leds(byte[] rgb) {
                    runOnUiThread(() -> { if (generation == connectionGeneration && connection != null) slider.setLeds(rgb); });
                }
            });
        connection = next;
        next.start();
    }

    private void applyPreferences() {
        slider.release();
        slider.columns = preferences.getInt("columns", 32);
        slider.sensitivity = preferences.getInt("sensitivity", 8) / 100f;
        slider.areaPercent = preferences.getInt("area", 80);
        slider.grid = preferences.getBoolean("grid", true);
        slider.diagnostics = preferences.getBoolean("diagnostics", true);
        slider.gameLeds = preferences.getBoolean("leds", true);
        slider.haptics = preferences.getBoolean("haptics", false);
        slider.lightTheme = preferences.getBoolean("light", false);
        slider.invalidate();
    }

    private void settings() {
        slider.release();
        LinearLayout form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20), 0, dp(20), 0);
        TextView help = new TextView(this);
        help.setText("Start CamoRip.SliderBridge.exe on PC, then run:\nadb reverse tcp:52468 tcp:52468\n\n" +
            "The tablet connects to localhost. AIR stays on CamoRip. Touch changes send immediately; the rate below controls unchanged-state heartbeats.");
        form.addView(help);
        Spinner layout = choices(form, "Sensor layout", new String[] { "32 columns", "16 columns × 2 rows" }, slider.columns == 32 ? 0 : 1);
        int[] rates = { 60, 120, 240, 500, 1000 };
        int selected = 2;
        for (int index = 0; index < rates.length; index++) if (rates[index] == preferences.getInt("frequency", 240)) selected = index;
        Spinner rate = choices(form, "Heartbeat rate", new String[] { "60 Hz", "120 Hz", "240 Hz", "500 Hz", "1000 Hz" }, selected);
        EditText port = number(form, "Local TCP port", preferences.getInt("port", 52468));
        EditText sensitivity = number(form, "Touch overlap (0–50% of one column)", preferences.getInt("sensitivity", 8));
        EditText area = number(form, "Play area height (30–100%)", slider.areaPercent);
        CheckBox grid = toggle(form, "Sensor grid", slider.grid);
        CheckBox diagnostics = toggle(form, "Finger and input diagnostics", slider.diagnostics);
        CheckBox leds = toggle(form, "Game LED feedback", slider.gameLeds);
        CheckBox haptics = toggle(form, "Touch haptics", slider.haptics);
        CheckBox light = toggle(form, "Light background", slider.lightTheme);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(form);
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle("CamoRip Slider")
            .setView(scroll).setNegativeButton("Cancel", null).setPositiveButton("Apply", null).create();
        dialog.setOnShowListener(ignored -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(view -> {
            try {
                int p = Integer.parseInt(port.getText().toString());
                int s = Integer.parseInt(sensitivity.getText().toString());
                int a = Integer.parseInt(area.getText().toString());
                if (p < 1 || p > 65535) { port.setError("Use 1–65535"); return; }
                if (s < 0 || s > 50) { sensitivity.setError("Use 0–50"); return; }
                if (a < 30 || a > 100) { area.setError("Use 30–100"); return; }
                preferences.edit().putInt("port", p).putInt("sensitivity", s).putInt("area", a)
                    .putInt("columns", layout.getSelectedItemPosition() == 0 ? 32 : 16)
                    .putInt("frequency", rates[rate.getSelectedItemPosition()])
                    .putBoolean("grid", grid.isChecked()).putBoolean("diagnostics", diagnostics.isChecked())
                    .putBoolean("leds", leds.isChecked()).putBoolean("haptics", haptics.isChecked())
                    .putBoolean("light", light.isChecked()).apply();
                applyPreferences();
                restart();
                dialog.dismiss();
            } catch (NumberFormatException error) { port.setError("Fill all numeric fields with whole numbers"); }
        }));
        dialog.show();
    }

    private Spinner choices(LinearLayout form, String label, String[] options, int selected) {
        TextView title = new TextView(this); title.setText(label); form.addView(title);
        Spinner spinner = new Spinner(this);
        spinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, options));
        spinner.setSelection(selected);
        form.addView(spinner);
        return spinner;
    }

    private EditText number(LinearLayout form, String label, int value) {
        TextView title = new TextView(this); title.setText(label); form.addView(title);
        EditText edit = new EditText(this);
        edit.setContentDescription(label);
        edit.setInputType(InputType.TYPE_CLASS_NUMBER);
        edit.setSingleLine(true);
        edit.setText(Integer.toString(value));
        form.addView(edit);
        return edit;
    }

    private CheckBox toggle(LinearLayout form, String label, boolean checked) {
        CheckBox toggle = new CheckBox(this);
        toggle.setText(label); toggle.setChecked(checked); form.addView(toggle);
        return toggle;
    }

    @Override protected void onResume() { super.onResume(); foreground = true; restart(); }
    @Override protected void onPause() { foreground = false; restart(); super.onPause(); }
    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (slider == null) return;
        if (!focused) slider.release();
        else getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN |
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }
}
