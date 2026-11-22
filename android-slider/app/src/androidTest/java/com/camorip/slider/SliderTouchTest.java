package com.camorip.slider;

import android.content.Intent;
import android.os.SystemClock;
import android.view.InputDevice;
import android.view.MotionEvent;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class SliderTouchTest {
    @Test public void tenAndroidPointersKeepTheirSensorsWhenOneLifts() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync(() -> {
            SliderView slider = new SliderView(InstrumentationRegistry.getInstrumentation().getTargetContext(), mask -> { });
            slider.areaPercent = 100;
            slider.sensitivity = 0;
            slider.layout(0, 0, 320, 200);
            for (int count = 1; count <= 10; count++)
                deliver(slider, count == 1 ? MotionEvent.ACTION_DOWN : MotionEvent.ACTION_POINTER_DOWN | ((count - 1) << 8), count, -1, false);
            assertEquals(10, slider.fingers());
            long mask = slider.mask();
            assertEquals(10, Long.bitCount(mask));
            deliver(slider, MotionEvent.ACTION_POINTER_UP | (3 << 8), 10, -1, false);
            assertEquals(mask & ~(1L << 28), slider.mask());
            deliver(slider, MotionEvent.ACTION_MOVE, 9, 3, false);
            assertEquals(mask & ~(1L << 28), slider.mask());
            deliver(slider, MotionEvent.ACTION_CANCEL, 9, 3, false);
            assertEquals(0, slider.mask());
            assertEquals(0, slider.fingers());
        });
    }

    @Test public void overlappingAndroidPointersDoNotReleaseEachOther() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync(() -> {
            SliderView slider = new SliderView(InstrumentationRegistry.getInstrumentation().getTargetContext(), mask -> { });
            slider.areaPercent = 100;
            slider.sensitivity = 0;
            slider.layout(0, 0, 320, 200);
            deliver(slider, MotionEvent.ACTION_DOWN, 1, -1, true);
            deliver(slider, MotionEvent.ACTION_POINTER_DOWN | (1 << 8), 2, -1, true);
            long held = slider.mask();
            deliver(slider, MotionEvent.ACTION_POINTER_UP, 2, -1, true);
            assertEquals(held, slider.mask());
            assertEquals(1, slider.fingers());
            deliver(slider, MotionEvent.ACTION_UP, 1, -1, true);
            assertEquals(0, slider.mask());
        });
    }

    @Test public void activityStartsWithoutCameraOrAirPermissions() {
        Intent intent = new Intent(InstrumentationRegistry.getInstrumentation().getTargetContext(), MainActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        MainActivity activity = (MainActivity) InstrumentationRegistry.getInstrumentation().startActivitySync(intent);
        InstrumentationRegistry.getInstrumentation().runOnMainSync(() -> {
            assertNotNull(activity.slider);
            assertEquals(0, activity.slider.mask());
            activity.finish();
        });
    }

    private static void deliver(SliderView view, int action, int count, int excludedId, boolean overlap) {
        MotionEvent.PointerProperties[] ids = new MotionEvent.PointerProperties[count];
        MotionEvent.PointerCoords[] coords = new MotionEvent.PointerCoords[count];
        for (int index = 0; index < count; index++) {
            int id = excludedId >= 0 && index >= excludedId ? index + 1 : index;
            ids[index] = new MotionEvent.PointerProperties();
            ids[index].id = id;
            ids[index].toolType = MotionEvent.TOOL_TYPE_FINGER;
            coords[index] = new MotionEvent.PointerCoords();
            coords[index].x = overlap ? 5 : id * 10 + 5;
            coords[index].y = 100;
            coords[index].pressure = 1;
            coords[index].size = 0.02f;
        }
        long now = SystemClock.uptimeMillis();
        MotionEvent event = MotionEvent.obtain(now, now, action, count, ids, coords, 0, 0, 1, 1, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0);
        try { view.onTouchEvent(event); }
        finally { event.recycle(); }
    }
}
