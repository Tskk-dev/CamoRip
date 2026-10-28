package com.camorip.slider;

import org.junit.Test;
import static org.junit.Assert.*;

public class InputQueueTest {
    @Test public void quickPressReleaseAndPressAreNotOverwritten() throws Exception {
        InputQueue queue = new InputQueue(() -> 0);
        queue.connect();
        queue.update(1); queue.update(0); queue.update(2);
        assertEquals(1, queue.next(0));
        assertEquals(0, queue.next(0));
        assertEquals(2, queue.next(0));
        assertEquals(2, queue.next(0));
    }

    @Test public void reconnectStartsReleasedAndOverflowIsReported() throws Exception {
        InputQueue queue = new InputQueue(() -> 0);
        queue.connect();
        for (int index = 0; index < 64; index++) assertTrue(queue.update(index % 2 + 1));
        assertFalse(queue.update(4));
        queue.disconnect(); queue.connect();
        assertEquals(0, queue.next(0));
    }

    @Test public void delayedChangesCannotBeReplayedAsFreshInput() throws Exception {
        java.util.concurrent.atomic.AtomicLong now = new java.util.concurrent.atomic.AtomicLong();
        InputQueue queue = new InputQueue(now::get);
        queue.connect(); queue.update(1);
        now.set(20_000_001);
        assertFalse(queue.update(0));
        assertThrows(java.io.IOException.class, () -> queue.next(0));
    }
}
