package com.camorip.slider;

import android.content.SharedPreferences;
import org.json.JSONObject;
import org.json.JSONException;
import java.util.Map;

final class SliderConfig {
    static final Map<String, int[]> RANGES = Map.of(
        "port", new int[] {1, 65535}, "frequency", new int[] {50, 1000},
        "sensitivity", new int[] {0, 50}, "area", new int[] {30, 100},
        "ledBrightness", new int[] {0, 100}, "backgroundOpacity", new int[] {0, 100}, "ledPattern", new int[] {0, 2});
    static final String[] FLAGS = {"grid", "diagnostics", "leds", "haptics", "dynamic", "expressive", "autoConnect", "mirror", "swapRows"};

    static JSONObject export(SharedPreferences preferences) throws JSONException {
        JSONObject json = new JSONObject();
        json.put("version", 1);
        for (String key : RANGES.keySet()) if (preferences.contains(key)) json.put(key, preferences.getInt(key, 0));
        for (String key : FLAGS) if (preferences.contains(key)) json.put(key, preferences.getBoolean(key, false));
        for (String key : new String[] {"mode", "host", "theme", "language", "columns", "seed"})
            if (preferences.contains(key)) json.put(key, preferences.getAll().get(key));
        return json;
    }

    static void importSkin(SharedPreferences preferences, JSONObject json) throws JSONException {
        SharedPreferences.Editor edit = preferences.edit();
        if (json.optInt("version", 1) != 1) throw new JSONException("Unsupported skin version");
        for (Map.Entry<String, int[]> range : RANGES.entrySet()) {
            String key = range.getKey();
            if (!json.has(key)) continue;
            double raw = json.getDouble(key);
            if (!Double.isFinite(raw) || raw != Math.rint(raw) || raw < range.getValue()[0] || raw > range.getValue()[1])
                throw new JSONException("Invalid " + key);
            edit.putInt(key, (int)raw);
        }
        for (String key : FLAGS) if (json.has(key)) edit.putBoolean(key, json.getBoolean(key));
        if (json.has("columns")) {
            int columns = json.getInt("columns");
            if (columns != 16 && columns != 32) throw new JSONException("Invalid layout");
            edit.putInt("columns", columns);
        }
        if (json.has("seed")) edit.putInt("seed", json.getInt("seed"));
        for (String key : new String[] {"mode", "host", "theme", "language"}) {
            if (!json.has(key)) continue;
            String value = json.getString(key);
            if (key.equals("mode") && !java.util.Set.of("usb", "tcp", "udp").contains(value)) throw new JSONException("Invalid connection");
            if (key.equals("theme") && !java.util.Set.of("system", "dark", "light").contains(value)) throw new JSONException("Invalid theme");
            if (key.equals("language") && !java.util.Set.of("", "en", "zh-CN", "zh-TW", "ja", "ko", "fr").contains(value)) throw new JSONException("Invalid language");
            if (key.equals("host") && (value.isEmpty() || value.length() > 253 || !value.matches("[a-zA-Z0-9.:-]+"))) throw new JSONException("Invalid PC address");
            edit.putString(key, value);
        }
        edit.apply();
    }
}
