package com.camorip.slider;

import java.util.ArrayDeque;
import java.io.IOException;
import java.util.function.LongSupplier;

/**
 * Orders slider state changes so quick press/release pairs reach the PC. Each packet carries
 * the absolute sensor state, so when the sender falls behind (queue full, or the oldest change
 * waited more than 20 ms) the stale history is dropped and only the newest state is sent.
 * Old changes are never replayed as fresh input, and the connection is not torn down: a
 * reconnect released every held input on the PC and paused input for over half a second.
 */
final class InputQueue {
    private static final int CAPACITY = 64;
    private static final long STALE_NANOS = 20_000_000L;
    private final ArrayDeque<long[]> changes = new ArrayDeque<>();
    private final LongSupplier clock;
    private long current;
    private boolean connected;

    InputQueue() { this(System::nanoTime); }
    InputQueue(LongSupplier clock) { this.clock = clock; }

    synchronized void connect() { changes.clear(); current = 0; connected = true; notifyAll(); }
    synchronized void disconnect() { changes.clear(); current = 0; connected = false; notifyAll(); }
    synchronized void wake() { if (connected && changes.size() < CAPACITY) changes.addLast(new long[] { current, clock.getAsLong() }); notifyAll(); }

    /** Returns false when queued history was collapsed because the sender fell behind. */
    synchronized boolean update(long mask) {
        mask &= 0xffffffffL;
        if (!connected || mask == current) return true;
        boolean collapsed = changes.size() >= CAPACITY || stale();
        if (collapsed) changes.clear();
        current = mask;
        changes.addLast(new long[] { mask, clock.getAsLong() });
        notifyAll();
        return !collapsed;
    }

    synchronized long next(long intervalNanos) throws InterruptedException, IOException {
        long deadline = System.nanoTime() + intervalNanos;
        while (connected && changes.isEmpty()) {
            long remaining = deadline - System.nanoTime();
            if (remaining <= 0) break;
            wait(remaining / 1_000_000, (int) (remaining % 1_000_000));
        }
        if (stale()) {
            // Skip the stale history and send the newest state immediately.
            changes.clear();
            return current;
        }
        return changes.isEmpty() ? current : changes.removeFirst()[0];
    }

    private boolean stale() { return !changes.isEmpty() && clock.getAsLong() - changes.peekFirst()[1] > STALE_NANOS; }
}
