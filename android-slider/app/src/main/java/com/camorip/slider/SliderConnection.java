package com.camorip.slider;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.Arrays;
import java.util.Timer;
import java.util.TimerTask;

final class SliderConnection implements AutoCloseable {
    interface Observer {
        void status(String text, boolean connected);
        void leds(byte[] rgb);
    }

    private final int port;
    private final int frequency;
    private final Observer observer;
    private final InputQueue queue = new InputQueue();
    private final Timer watchdog = new Timer("slider-watchdog", true);
    private volatile boolean running = true;
    private volatile Socket socket;
    private volatile long writeStarted;
    private final Object ackLock = new Object();
    private final int[] sequences = new int[64];
    private final long[] sentAt = new long[64];
    volatile long roundTripMicros = -1;
    volatile long packetsSent;
    volatile long sentMask;
    private final Thread worker;

    SliderConnection(int port, int frequency, Observer observer) {
        if (port < 1 || port > 65535 || frequency < 50 || frequency > 1000)
            throw new IllegalArgumentException("Invalid connection settings");
        this.port = port;
        this.frequency = frequency;
        this.observer = observer;
        worker = new Thread(this::run, "slider-send");
        worker.setDaemon(true);
    }

    void start() {
        watchdog.scheduleAtFixedRate(new TimerTask() {
            @Override public void run() {
                long started = writeStarted;
                if (started != 0 && System.nanoTime() - started > 50_000_000L) closeSocket(socket);
            }
        }, 10, 10);
        worker.start();
    }

    void update(long mask) {
        if (!queue.update(mask)) {
            observer.status("PC is falling behind; reconnecting with released inputs", false);
            closeSocket(socket);
        }
    }

    private void run() {
        int sequence = 1;
        while (running) {
            Socket current = new Socket();
            socket = current;
            try {
                current.setTcpNoDelay(true);
                current.setKeepAlive(true);
                current.setSendBufferSize(1024);
                current.connect(new InetSocketAddress("127.0.0.1", port), 1500);
                roundTripMicros = -1;
                OutputStream output = current.getOutputStream();
                write(output, SliderProtocol.input(0, sequence++));
                queue.connect();
                observer.status("USB connected", true);
                Thread reader = new Thread(() -> receive(current), "slider-leds");
                reader.setDaemon(true);
                reader.start();
                while (running && !current.isClosed()) {
                    long mask = queue.next(1_000_000_000L / frequency);
                    write(output, SliderProtocol.input(mask, sequence++));
                    sentMask = mask;
                    packetsSent++;
                }
            } catch (IOException error) {
                if (running) observer.status("Waiting for PC / adb reverse: " + error.getMessage(), false);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                break;
            } finally {
                queue.disconnect();
                sentMask = 0;
                closeSocket(current);
            }
            try { Thread.sleep(500); }
            catch (InterruptedException error) { Thread.currentThread().interrupt(); break; }
        }
    }

    private void write(OutputStream output, byte[] packet) throws IOException {
        int sequence = java.nio.ByteBuffer.wrap(packet).getInt(4);
        synchronized (ackLock) {
            sequences[sequence & 63] = sequence;
            sentAt[sequence & 63] = System.nanoTime();
        }
        writeStarted = System.nanoTime();
        try { output.write(packet); }
        finally { writeStarted = 0; }
    }

    private void receive(Socket current) {
        try {
            while (running && socket == current) {
                byte[] frame = SliderProtocol.readFrame(current.getInputStream());
                if (frame.length == 100 && frame[1] == 'L' && frame[2] == 'E' && frame[3] == 'D' && socket == current)
                    observer.leds(Arrays.copyOfRange(frame, 4, 100));
                else if (frame.length == 8 && frame[1] == 'A' && frame[2] == 'C' && frame[3] == 'K') {
                    int sequence = java.nio.ByteBuffer.wrap(frame).getInt(4);
                    synchronized (ackLock) {
                        if (sequences[sequence & 63] == sequence)
                            roundTripMicros = (System.nanoTime() - sentAt[sequence & 63]) / 1000;
                    }
                }
            }
        } catch (IOException error) { closeSocket(current); }
    }

    private static void closeSocket(Socket socket) {
        if (socket != null) try { socket.close(); } catch (IOException ignored) { }
    }

    @Override public void close() {
        running = false;
        queue.disconnect();
        watchdog.cancel();
        closeSocket(socket);
        worker.interrupt();
    }
}
