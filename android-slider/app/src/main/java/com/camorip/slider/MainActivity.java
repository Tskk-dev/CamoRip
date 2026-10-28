package com.camorip.slider;

import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.net.Uri;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.app.AppCompatDelegate;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;
import com.google.android.material.button.MaterialButton;
import com.google.android.material.card.MaterialCardView;
import com.google.android.material.color.DynamicColors;
import com.google.android.material.color.DynamicColorsOptions;
import com.google.android.material.color.MaterialColors;
import com.google.android.material.snackbar.Snackbar;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends AppCompatActivity {
    SliderView slider;
    SharedPreferences preferences;
    private SliderConnection connection;
    private TextView status;
    private MaterialButton connect;
    private MaterialCardView chrome;
    private MaterialButton restore;
    private boolean requested, foreground, connected;
    private boolean testHeld, serviceHeld;
    private int connectionGeneration;
    private String connectionStatus = "Ready";
    private final ExecutorService files = Executors.newSingleThreadExecutor();
    private long backgroundGeneration;

    @Override public void onCreate(Bundle state) {
        preferences = getSharedPreferences("slider", MODE_PRIVATE);
        String theme = preferences.getString("theme", preferences.getBoolean("light", false) ? "light" : "system");
        AppCompatDelegate.setDefaultNightMode(theme.equals("light") ? AppCompatDelegate.MODE_NIGHT_NO :
            theme.equals("dark") ? AppCompatDelegate.MODE_NIGHT_YES : AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM);
        setTheme(preferences.getBoolean("expressive", true) ? R.style.Theme_CamoRipSlider : R.style.Theme_CamoRipSlider_Classic);
        super.onCreate(state);
        if (preferences.getBoolean("dynamic", true)) DynamicColors.applyToActivityIfAvailable(this);
        else DynamicColors.applyToActivityIfAvailable(this, new DynamicColorsOptions.Builder()
            .setContentBasedSource(preferences.getInt("seed", 0xff455e91)).build());
        requested = state == null ? preferences.getBoolean("autoConnect", false) : state.getBoolean("connect");
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(MaterialColors.getColor(root, com.google.android.material.R.attr.colorSurface));
        slider = new SliderView(this, mask -> { if (connection != null) connection.update(mask); });
        root.addView(slider, new FrameLayout.LayoutParams(-1, -1));
        chrome = new MaterialCardView(this); chrome.setRadius(dp(28)); chrome.setCardElevation(0);
        chrome.setCardBackgroundColor(MaterialColors.getColor(root, com.google.android.material.R.attr.colorSurfaceContainer));
        LinearLayout bar = new LinearLayout(this); bar.setGravity(Gravity.CENTER_VERTICAL); bar.setPadding(dp(20), dp(8), dp(12), dp(8));
        LinearLayout title = new LinearLayout(this); title.setOrientation(LinearLayout.VERTICAL);
        TextView name = new TextView(this); name.setText("CamoRip Slider"); name.setTextSize(24);
        name.setTypeface(null, android.graphics.Typeface.BOLD); title.addView(name);
        status = new TextView(this); status.setTextSize(12); title.addView(status);
        bar.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        connect = button("Connect", () -> { requested = !requested; restart(); }); bar.addView(connect);
        bar.addView(button("Settings", () -> { slider.release(); new SettingsSheet(this).show(); }));
        bar.addView(button("Hide", () -> { chrome.setVisibility(View.GONE); restore.setVisibility(slider.areaPercent < 100 ? View.VISIBLE : View.GONE); }));
        chrome.addView(bar);
        FrameLayout.LayoutParams header = new FrameLayout.LayoutParams(-1, -2, Gravity.TOP);
        header.setMargins(dp(16), dp(12), dp(16), 0); root.addView(chrome, header);
        restore = button("Controls", () -> { chrome.setVisibility(View.VISIBLE); restore.setVisibility(View.GONE); });
        FrameLayout.LayoutParams handle = new FrameLayout.LayoutParams(-2, -2, Gravity.TOP | Gravity.END);
        handle.setMargins(0, dp(8), dp(16), 0); root.addView(restore, handle); restore.setVisibility(View.GONE);
        setContentView(root);
        getOnBackPressedDispatcher().addCallback(this, new androidx.activity.OnBackPressedCallback(true) {
            @Override public void handleOnBackPressed() {
                if (chrome.getVisibility() == View.GONE) { chrome.setVisibility(View.VISIBLE); restore.setVisibility(View.GONE); }
                else finish();
            }
        });
        ViewCompat.setOnApplyWindowInsetsListener(root, (view, insets) -> {
            androidx.core.graphics.Insets bars = insets.getInsets(WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom); return insets;
        });
        applyPreferences();
        root.postDelayed(new Runnable() {
            @Override public void run() {
                if (isDestroyed()) return;
                SliderConnection current = connection;
                String ack = current == null || current.roundTripMicros < 0 ? "" : String.format(java.util.Locale.ROOT, " · %.1f ms RTT", current.roundTripMicros / 1000.0);
                status.setText(SliderText.get(MainActivity.this, connectionStatus) + ack); root.postDelayed(this, 500);
            }
        }, 500);
    }

    int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    MaterialButton button(String label, Runnable action) {
        MaterialButton button = new MaterialButton(this);
        button.setText(SliderText.get(this, label)); button.setMinHeight(dp(48)); button.setOnClickListener(view -> action.run());
        return button;
    }
    void message(String text) { if (!isDestroyed() && !isFinishing()) Snackbar.make(slider, text, Snackbar.LENGTH_LONG).show(); }

    void restart() {
        int generation = ++connectionGeneration;
        slider.release(); if (connection != null) connection.close(); connection = null; connected = false;
        connect.setText(SliderText.get(this, requested ? "Disconnect" : "Connect")); connectionStatus = "Disconnected";
        if (!requested || !foreground) return;
        String mode = preferences.getString("mode", "usb"), host = preferences.getString("host", "192.168.1.2");
        SliderConnection next = new SliderConnection(preferences.getInt("port", 52468), preferences.getInt("frequency", 240),
            new SliderConnection.Observer() {
                @Override public void status(String text, boolean ready) {
                    runOnUiThread(() -> {
                        if (generation != connectionGeneration || !requested || !foreground) return;
                        connected = ready; connectionStatus = ready ? "Connected" : "Waiting for PC"; slider.release();
                    });
                }
                @Override public void leds(byte[] rgb) {
                    if (!preferences.getBoolean("leds", true)) return;
                    runOnUiThread(() -> { if (generation == connectionGeneration && connection != null) slider.setLeds(rgb); });
                }
            }, () -> mode.equals("usb") ? new AdbLink() : new NetworkLink(host, mode.equals("udp")), new InputQueue());
        connection = next; next.start();
    }

    void applyPreferences() {
        slider.release(); slider.columns = preferences.getInt("columns", 32); slider.sensitivity = preferences.getInt("sensitivity", 8) / 100f;
        slider.areaPercent = preferences.getInt("area", 80); slider.grid = preferences.getBoolean("grid", true);
        slider.diagnostics = preferences.getBoolean("diagnostics", false); slider.gameLeds = preferences.getBoolean("leds", true);
        slider.haptics = preferences.getBoolean("haptics", false); slider.mirror = preferences.getBoolean("mirror", false);
        slider.swapRows = preferences.getBoolean("swapRows", false); slider.ledBrightness = preferences.getInt("ledBrightness", 100);
        slider.backgroundOpacity = preferences.getInt("backgroundOpacity", 40);
        int pattern = preferences.getInt("ledPattern", 0), seed = preferences.getInt("seed", 0xff455e91);
        for (int sensor = 0; sensor < 32; sensor++) {
            int color = pattern == 2 ? android.graphics.Color.HSVToColor(new float[] {sensor * 360f / 32, 0.6f, 0.7f}) :
                pattern == 1 && sensor % 2 == 0 ? 0xff303030 : seed;
            slider.presetColors[sensor] = android.graphics.Color.rgb(android.graphics.Color.red(color) * slider.ledBrightness / 100,
                android.graphics.Color.green(color) * slider.ledBrightness / 100, android.graphics.Color.blue(color) * slider.ledBrightness / 100);
        }
        slider.lightTheme = (getResources().getConfiguration().uiMode & android.content.res.Configuration.UI_MODE_NIGHT_MASK) != android.content.res.Configuration.UI_MODE_NIGHT_YES;
        slider.accent = MaterialColors.getColor(slider, com.google.android.material.R.attr.colorPrimaryContainer); slider.invalidate(); loadBackground();
    }

    void holdButton(boolean test, boolean held) {
        if (test) testHeld = held; else serviceHeld = held;
        if (connected && connection != null) connection.buttons(testHeld, serviceHeld);
    }
    void coin() { if (ready()) connection.command(new byte[] {4, 'F', 'N', 'C', 1}); }
    private boolean ready() { if (connected && connection != null) return true; message("Connect to your PC first"); return false; }
    void card() {
        if (!ready()) return;
        String digits = preferences.getString("card", "");
        if (!digits.matches("[0-9]{20}")) { message("Save a 20-digit card access code first"); return; }
        byte[] packet = new byte[16]; packet[0] = 15; packet[1] = 'C'; packet[2] = 'R'; packet[3] = 'D'; packet[4] = packet[5] = 1;
        for (int index = 0; index < 10; index++) packet[index + 6] = (byte)(((digits.charAt(index * 2) - '0') << 4) | (digits.charAt(index * 2 + 1) - '0'));
        connection.command(packet);
    }

    void pick(int request, String type) {
        Intent intent = new Intent(request == 12 ? Intent.ACTION_CREATE_DOCUMENT : Intent.ACTION_OPEN_DOCUMENT);
        intent.addCategory(Intent.CATEGORY_OPENABLE); intent.setType(type);
        if (request == 12) intent.putExtra(Intent.EXTRA_TITLE, "camorip-slider.json"); startActivityForResult(intent, request);
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        Uri uri = data.getData(); files.execute(() -> {
            try {
                if (request == 10) {
                    getContentResolver().takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
                    preferences.edit().putString("background", uri.toString()).apply(); runOnUiThread(this::loadBackground);
                } else if (request == 11) {
                    try (InputStream input = getContentResolver().openInputStream(uri)) {
                        if (input == null) throw new java.io.IOException("Could not open skin");
                        java.io.ByteArrayOutputStream bytes = new java.io.ByteArrayOutputStream(); byte[] buffer = new byte[4096]; int count;
                        while ((count = input.read(buffer)) != -1) { bytes.write(buffer, 0, count); if (bytes.size() > 65536) throw new java.io.IOException("Skin is too large"); }
                        SliderConfig.importSkin(preferences, new org.json.JSONObject(bytes.toString("UTF-8")));
                    }
                    runOnUiThread(this::recreate);
                } else if (request == 12) {
                    try (OutputStream output = getContentResolver().openOutputStream(uri)) {
                        if (output == null) throw new java.io.IOException("Could not save settings");
                        output.write(SliderConfig.export(preferences).toString(2).getBytes(java.nio.charset.StandardCharsets.UTF_8));
                    }
                    runOnUiThread(() -> message("Settings exported"));
                }
            } catch (Exception error) { runOnUiThread(() -> message(error.getMessage() == null ? "Could not open file" : error.getMessage())); }
        });
    }

    void loadBackground() {
        long generation = ++backgroundGeneration; String path = preferences.getString("background", "");
        if (path.isEmpty()) { slider.setBackgroundImage(null); return; }
        files.execute(() -> {
            try {
                Uri uri = Uri.parse(path); BitmapFactory.Options options = new BitmapFactory.Options(); options.inJustDecodeBounds = true;
                try (InputStream input = getContentResolver().openInputStream(uri)) { BitmapFactory.decodeStream(input, null, options); }
                if (options.outWidth <= 0 || options.outHeight <= 0) throw new java.io.IOException("Unsupported image");
                options.inSampleSize = 1;
                while (options.outWidth / options.inSampleSize > 2048 || options.outHeight / options.inSampleSize > 2048) options.inSampleSize *= 2;
                options.inJustDecodeBounds = false; Bitmap image;
                try (InputStream input = getContentResolver().openInputStream(uri)) { image = BitmapFactory.decodeStream(input, null, options); }
                runOnUiThread(() -> { if (!isDestroyed() && generation == backgroundGeneration) slider.setBackgroundImage(image); });
            } catch (Exception error) { runOnUiThread(() -> message("Background image is unavailable")); }
        });
    }

    @Override protected void onSaveInstanceState(Bundle state) { state.putBoolean("connect", requested); super.onSaveInstanceState(state); }
    @Override protected void onResume() { super.onResume(); foreground = true; restart(); }
    @Override protected void onPause() { foreground = false; restart(); super.onPause(); }
    @Override protected void onDestroy() { files.shutdownNow(); super.onDestroy(); }
    @Override public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused); if (slider == null) return;
        if (!focused) { slider.release(); testHeld = serviceHeld = false; if (connection != null) connection.buttons(false, false); }
        else androidx.core.view.WindowCompat.getInsetsController(getWindow(), getWindow().getDecorView()).hide(WindowInsetsCompat.Type.systemBars());
    }
}
