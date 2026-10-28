package com.camorip.slider;

import java.util.ArrayDeque;

final class InputQueue {
    private final ArrayDeque<Long> changes = new ArrayDeque<>();
    private long current;
    private boolean connected;

    synchronized void connect() { changes.clear(); current = 0; connected = true; notifyAll(); }
    synchronized void disconnect() { changes.clear(); current = 0; connected = false; notifyAll(); }

    synchronized boolean update(long mask) {
        mask &= 0xffffffffL;
        if (!connected || mask == current) return true;
        if (changes.size() >= 256) return false;
        current = mask;
        changes.addLast(mask);
        notifyAll();
        return true;
    }

    synchronized long next(long intervalNanos) throws InterruptedException {
        long deadline = System.nanoTime() + intervalNanos;
        while (connected && changes.isEmpty()) {
            long remaining = deadline - System.nanoTime();
            if (remaining <= 0) break;
            wait(remaining / 1_000_000, (int) (remaining % 1_000_000));
        }
        return changes.isEmpty() ? current : changes.removeFirst();
    }
}
