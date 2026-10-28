package com.camorip.slider;

import java.io.IOException;
import java.io.OutputStream;
import java.io.InputStream;
import java.io.Closeable;
import java.util.Arrays;
import java.util.Timer;
import java.util.TimerTask;
import java.util.function.Supplier;

final class SliderConnection implements AutoCloseable {
    interface Link extends Closeable {
        void connect(int port) throws IOException;
        InputStream input() throws IOException;
        OutputStream output() throws IOException;
        boolean closed();
    }
    interface Observer {
        void status(String text, boolean connected);
        void leds(byte[] rgb);
    }

    private final int port;
    private final int frequency;
    private final Observer observer;
    private final InputQueue queue;
    private final Supplier<Link> linkFactory;
    private final Timer watchdog = new Timer("slider-watchdog", true);
    private volatile boolean running = true;
    private volatile Link socket;
    private volatile long writeStarted;
    private final Object ackLock = new Object();
    private final int[] sequences = new int[64];
    private final long[] sentAt = new long[64];
    volatile long roundTripMicros = -1;
    volatile long packetsSent;
    volatile long sentMask;
    private final Thread worker;
    private final java.util.concurrent.ArrayBlockingQueue<byte[]> commands = new java.util.concurrent.ArrayBlockingQueue<>(16);
    private volatile boolean testButton, serviceButton;

    SliderConnection(int port, int frequency, Observer observer) {
        this(port, frequency, observer, AdbLink::new, new InputQueue());
    }

    SliderConnection(int port, int frequency, Observer observer, Supplier<Link> linkFactory, InputQueue queue) {
        if (port < 1 || port > 65535 || frequency < 50 || frequency > 1000)
            throw new IllegalArgumentException("Invalid connection settings");
        this.port = port;
        this.frequency = frequency;
        this.observer = observer;
        this.linkFactory = linkFactory;
        this.queue = queue;
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

    void buttons(boolean test, boolean service) { testButton = test; serviceButton = service; queue.wake(); }
    void command(byte[] packet) { if (commands.offer(packet.clone())) queue.wake(); }

    private void run() {
        int sequence = 1;
        while (running) {
            Link current = linkFactory.get();
            socket = current;
            try {
                current.connect(port);
                roundTripMicros = -1;
                OutputStream output = current.output();
                write(output, SliderProtocol.input(0, sequence++));
                queue.connect();
                observer.status("Waiting for PC", false);
                Thread reader = new Thread(() -> receive(current), "slider-leds");
                reader.setDaemon(true);
                reader.start();
                while (running && !current.closed()) {
                    long mask = queue.next(1_000_000_000L / frequency);
                    byte[] command;
                    while ((command = commands.poll()) != null) {
                        writeStarted = System.nanoTime();
                        try { output.write(command); } finally { writeStarted = 0; }
                    }
                    byte[] packet = SliderProtocol.input(mask, sequence++);
                    packet[40] = testButton ? (byte)1 : 0; packet[41] = serviceButton ? (byte)1 : 0;
                    write(output, packet);
                    sentMask = mask;
                    packetsSent++;
                }
            } catch (IOException | IllegalArgumentException error) {
                if (running) observer.status("Waiting for PC / adb reverse: " + error.getMessage(), false);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                break;
            } finally {
                queue.disconnect();
                commands.clear(); testButton = serviceButton = false;
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

    private void receive(Link current) {
        try {
            InputStream input = current.input();
            boolean announced = false;
            while (running && socket == current) {
                byte[] frame = SliderProtocol.readFrame(input);
                boolean feedback = frame.length == 100 && frame[1] == 'L' && frame[2] == 'E' && frame[3] == 'D' ||
                    frame.length == 8 && frame[1] == 'A' && frame[2] == 'C' && frame[3] == 'K';
                if (feedback && !announced) { announced = true; observer.status("Connected", true); }
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

    private static void closeSocket(Link socket) {
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
