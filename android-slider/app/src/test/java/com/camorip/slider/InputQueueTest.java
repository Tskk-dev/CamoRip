package com.camorip.slider;

import java.util.concurrent.atomic.AtomicLong;
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

    @Test public void overflowCollapsesToTheNewestStateAndReconnectStartsReleased() throws Exception {
        InputQueue queue = new InputQueue(() -> 0);
        queue.connect();
        for (int index = 0; index < 64; index++) assertTrue(queue.update(index % 2 + 1));
        // The 65th change reports the collapse but is still delivered, ahead of the history.
        assertFalse(queue.update(4));
        assertEquals(4, queue.next(0));
        assertEquals(4, queue.next(0));
        queue.disconnect(); queue.connect();
        assertEquals(0, queue.next(0));
    }

    @Test public void delayedChangesAreNotReplayedAndDoNotDropTheConnection() throws Exception {
        AtomicLong now = new AtomicLong();
        InputQueue queue = new InputQueue(now::get);
        queue.connect(); queue.update(1); queue.update(3);
        now.set(20_000_001);
        // A stalled sender skips stale history and sends only the newest state.
        assertEquals(3, queue.next(0));
        assertEquals(3, queue.next(0));
        queue.update(1);
        now.set(40_000_002);
        assertFalse(queue.update(0));
        assertEquals(0, queue.next(0));
    }
}
