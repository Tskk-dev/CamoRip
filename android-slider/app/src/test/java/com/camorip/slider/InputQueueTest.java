package com.camorip.slider;

import org.junit.Test;
import static org.junit.Assert.*;

public class InputQueueTest {
    @Test public void quickPressReleaseAndPressAreNotOverwritten() throws Exception {
        InputQueue queue = new InputQueue();
        queue.connect();
        queue.update(1); queue.update(0); queue.update(2);
        assertEquals(1, queue.next(0));
        assertEquals(0, queue.next(0));
        assertEquals(2, queue.next(0));
        assertEquals(2, queue.next(0));
    }

    @Test public void reconnectStartsReleasedAndOverflowIsReported() throws Exception {
        InputQueue queue = new InputQueue();
        queue.connect();
        for (int index = 0; index < 256; index++) assertTrue(queue.update(index % 2 + 1));
        assertFalse(queue.update(4));
        queue.disconnect(); queue.connect();
        assertEquals(0, queue.next(0));
    }
}
