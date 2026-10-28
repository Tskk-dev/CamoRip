package com.camorip.slider;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.view.HapticFeedbackConstants;
import android.view.MotionEvent;
import android.view.View;
import java.util.HashMap;
import java.util.Map;
import java.util.function.LongConsumer;

final class SliderView extends View {
    private final TouchState touches = new TouchState();
    private final Map<Integer, float[]> points = new HashMap<>();
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final LongConsumer changed;
    int columns = 32;
    float sensitivity = 0.08f;
    int areaPercent = 80;
    boolean haptics;
    boolean grid = true;
    boolean gameLeds = true;
    boolean diagnostics = true;
    boolean lightTheme;
    boolean mirror;
    boolean swapRows;
    int accent = Color.WHITE;
    int ledBrightness = 100;
    int backgroundOpacity = 40;
    final int[] presetColors = new int[32];
    private android.graphics.Bitmap background;
    private final android.graphics.RectF imageBounds = new android.graphics.RectF();
    private byte[] leds = new byte[96];
    private long mask;
    private int cancellations;

    SliderView(Context context, LongConsumer changed) {
        super(context);
        this.changed = changed;
        setContentDescription("32 sensor slider touch surface");
        setFocusable(true);
    }

    long mask() { return mask; }
    int fingers() { return touches.count(); }
    void setLeds(byte[] rgb) { leds = rgb.clone(); postInvalidateOnAnimation(); }
    void setBackgroundImage(android.graphics.Bitmap image) { background = image; invalidate(); }
    private float top() { return getHeight() * (1 - areaPercent / 100f); }

    void release() {
        touches.clear();
        points.clear();
        publish();
    }

    private void publish() {
        long next = touches.mask();
        if (next != mask) {
            if (haptics && (next & ~mask) != 0) performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK);
            mask = next;
            changed.accept(mask);
        }
        postInvalidateOnAnimation();
    }

    @Override public boolean onTouchEvent(MotionEvent event) {
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_CANCEL || action == MotionEvent.ACTION_UP) {
            if (action == MotionEvent.ACTION_CANCEL) cancellations++;
            release();
            if (action == MotionEvent.ACTION_UP) performClick();
            return true;
        }
        if (action != MotionEvent.ACTION_DOWN && action != MotionEvent.ACTION_POINTER_DOWN &&
            action != MotionEvent.ACTION_MOVE && action != MotionEvent.ACTION_POINTER_UP) return true;
        for (int history = 0; history < event.getHistorySize(); history++) sample(event, history, -1);
        sample(event, -1, action == MotionEvent.ACTION_POINTER_UP ? event.getActionIndex() : -1);
        return true;
    }

    private void sample(MotionEvent event, int history, int excluded) {
        touches.clear();
        points.clear();
        for (int pointer = 0; pointer < event.getPointerCount(); pointer++) {
            if (pointer == excluded) continue;
            float x = history < 0 ? event.getX(pointer) : event.getHistoricalX(pointer, history);
            float y = history < 0 ? event.getY(pointer) : event.getHistoricalY(pointer, history);
            float sensorX = mirror ? getWidth() - x - 0.001f : x;
            float sensorY = swapRows && columns == 16 ? getHeight() - y - 0.001f : y - top();
            int id = event.getPointerId(pointer);
            touches.set(id, sensorX, sensorY, getWidth(), getHeight() - top(), columns, sensitivity);
            points.put(id, new float[] { x, y });
        }
        publish();
    }

    @Override public boolean performClick() { super.performClick(); return true; }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        canvas.drawColor(lightTheme ? Color.WHITE : Color.rgb(15, 15, 15));
        if (background != null) {
            paint.setAlpha(backgroundOpacity * 255 / 100);
            imageBounds.set(0, 0, getWidth(), getHeight());
            canvas.drawBitmap(background, null, imageBounds, paint);
            paint.setAlpha(255);
        }
        float top = top(), width = getWidth() / (float) columns;
        int rows = 32 / columns;
        float height = (getHeight() - top) / rows;
        for (int column = 0; column < columns; column++) {
            for (int row = 0; row < rows; row++) {
                int mappedColumn = mirror ? columns - column - 1 : column;
                int mappedRow = swapRows && rows == 2 ? 1 - row : row;
                int sensor = 31 - mappedColumn * rows - mappedRow;
                boolean active = (mask & (1L << sensor)) != 0;
                int offset = sensor * 3;
                int color = gameLeds ? Color.rgb((leds[offset] & 255) * ledBrightness / 100,
                    (leds[offset + 1] & 255) * ledBrightness / 100, (leds[offset + 2] & 255) * ledBrightness / 100) : presetColors[sensor];
                paint.setColor(active ? accent : color);
                if (!active && background != null && !gameLeds) paint.setAlpha(160);
                paint.setStyle(Paint.Style.FILL);
                float x = column * width, y = top + row * height;
                canvas.drawRect(x + 1, y + 1, x + width - 1, y + height - 1, paint);
                paint.setAlpha(255);
                if (grid) {
                    paint.setColor(active ? Color.BLACK : Color.GRAY);
                    paint.setStyle(Paint.Style.STROKE);
                    paint.setStrokeWidth(1);
                    canvas.drawRect(x + 1, y + 1, x + width - 1, y + height - 1, paint);
                    paint.setStyle(Paint.Style.FILL);
                    paint.setTextSize(12 * getResources().getDisplayMetrics().scaledDensity);
                    canvas.drawText(Integer.toString(sensor + 1), x + 4, y + 22, paint);
                }
            }
        }
        if (diagnostics) {
            paint.setColor(lightTheme ? Color.BLACK : Color.WHITE);
            paint.setTextSize(14 * getResources().getDisplayMetrics().scaledDensity);
            canvas.drawText("Fingers: " + touches.count() + "    Sensors: " + Long.bitCount(mask) +
                "    Mask: " + String.format(java.util.Locale.ROOT, "%08X", mask) + "    Cancels: " + cancellations, 12, 26, paint);
            paint.setColor(Color.GRAY);
            paint.setStyle(Paint.Style.STROKE);
            paint.setStrokeWidth(3);
            for (float[] point : points.values()) canvas.drawCircle(point[0], point[1], 16, paint);
            paint.setStyle(Paint.Style.FILL);
        }
    }
}
