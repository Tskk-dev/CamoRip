package com.camorip.slider;

import org.junit.Test;
import static org.junit.Assert.*;

public class TouchStateTest {
    @Test public void removingAnotherFingerNeverReassignsHeldSensors() {
        TouchState state = new TouchState();
        state.set(12, 5, 20, 320, 100, 32, 0);
        state.set(50, 5, 20, 320, 100, 32, 0);
        assertEquals(1L << 31, state.mask());
        state.remove(12);
        assertEquals(1L << 31, state.mask());
        assertEquals(1, state.count());
        state.remove(50);
        assertEquals(0, state.mask());
    }

    @Test public void tenFingersCanMoveAndLiftInAnyOrder() {
        TouchState state = new TouchState();
        long expected = 0;
        for (int finger = 0; finger < 10; finger++) {
            state.set(100 - finger, finger * 30 + 5, 50, 320, 100, 32, 0);
            expected |= 1L << (31 - finger * 3);
        }
        assertEquals(10, state.count());
        assertEquals(expected, state.mask());
        state.remove(95);
        expected &= ~(1L << 16);
        assertEquals(expected, state.mask());
        state.set(100, 315, 50, 320, 100, 32, 0);
        assertEquals((expected & ~(1L << 31)) | 1, state.mask());
        state.clear();
        assertEquals(0, state.mask());
    }

    @Test public void twoRowLayoutUsesPositionRatherThanFingerArrivalOrder() {
        TouchState state = new TouchState();
        state.set(20, 5, 75, 160, 100, 16, 0);
        state.set(7, 5, 25, 160, 100, 16, 0);
        assertEquals((1L << 31) | (1L << 30), state.mask());
        state.remove(7);
        assertEquals(1L << 30, state.mask());
    }

    @Test public void overlapAddsNeighborsButOutsideContactsCannotStick() {
        TouchState state = new TouchState();
        state.set(3, 9.5f, 50, 320, 100, 32, 0.1f);
        assertEquals((1L << 31) | (1L << 30), state.mask());
        state.set(3, -1, 50, 320, 100, 32, 0.1f);
        assertEquals(0, state.mask());
        state.set(3, Float.NaN, 50, 320, 100, 32, 0.1f);
        assertEquals(0, state.mask());
    }
}
