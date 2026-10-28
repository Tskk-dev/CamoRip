package com.camorip.slider;

import java.util.ArrayDeque;
import java.io.IOException;
import java.util.function.LongSupplier;

final class InputQueue {
    private final ArrayDeque<long[]> changes = new ArrayDeque<>();
    private final LongSupplier clock;
    private long current;
    private boolean connected;

    InputQueue() { this(System::nanoTime); }
    InputQueue(LongSupplier clock) { this.clock = clock; }

    synchronized void connect() { changes.clear(); current = 0; connected = true; notifyAll(); }
    synchronized void disconnect() { changes.clear(); current = 0; connected = false; notifyAll(); }
    synchronized void wake() { if (connected && changes.size() < 64) changes.addLast(new long[] { current, clock.getAsLong() }); notifyAll(); }

    synchronized boolean update(long mask) {
        mask &= 0xffffffffL;
        if (!connected || mask == current) return true;
        if (changes.size() >= 64 || stale()) return false;
        current = mask;
        changes.addLast(new long[] { mask, clock.getAsLong() });
        notifyAll();
        return true;
    }

    synchronized long next(long intervalNanos) throws InterruptedException, IOException {
        long deadline = System.nanoTime() + intervalNanos;
        while (connected && changes.isEmpty()) {
            long remaining = deadline - System.nanoTime();
            if (remaining <= 0) break;
            wait(remaining / 1_000_000, (int) (remaining % 1_000_000));
        }
        if (stale()) throw new IOException("Input queue exceeded 20 ms; reconnecting");
        return changes.isEmpty() ? current : changes.removeFirst()[0];
    }

    private boolean stale() { return !changes.isEmpty() && clock.getAsLong() - changes.peekFirst()[1] > 20_000_000L; }
}
