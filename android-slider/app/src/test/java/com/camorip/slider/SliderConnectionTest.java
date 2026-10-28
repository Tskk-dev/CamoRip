package com.camorip.slider;

import java.io.DataInputStream;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.junit.Test;
import static org.junit.Assert.*;

public class SliderConnectionTest {
    @Test public void usbTcpPathPreservesRapidTransitionsAndReadsFragmentedLeds() throws Exception {
        try (ServerSocket server = new ServerSocket(0)) {
            CountDownLatch connected = new CountDownLatch(1), leds = new CountDownLatch(1);
            AtomicReference<byte[]> receivedLeds = new AtomicReference<>();
            SliderConnection client = new SliderConnection(server.getLocalPort(), 240, new SliderConnection.Observer() {
                @Override public void status(String text, boolean ready) { if (ready) connected.countDown(); }
                @Override public void leds(byte[] rgb) { receivedLeds.set(rgb); leds.countDown(); }
            }, () -> new SliderConnection.Link() {
                private final Socket socket = new Socket();
                @Override public void connect(int port) throws java.io.IOException {
                    socket.setTcpNoDelay(true);
                    socket.connect(new java.net.InetSocketAddress("127.0.0.1", port), 3000);
                }
                @Override public java.io.InputStream input() throws java.io.IOException { return socket.getInputStream(); }
                @Override public java.io.OutputStream output() throws java.io.IOException { return socket.getOutputStream(); }
                @Override public boolean closed() { return socket.isClosed(); }
                @Override public void close() throws java.io.IOException { socket.close(); }
            }, new InputQueue(() -> 0));
            try {
                client.start();
                server.setSoTimeout(3000);
                try (Socket peer = server.accept()) {
                    peer.setSoTimeout(3000);
                    DataInputStream input = new DataInputStream(peer.getInputStream());
                    byte[] packet = new byte[42];
                    input.readFully(packet);
                    assertEquals(0, packet[8]);
                    assertTrue(connected.await(3, TimeUnit.SECONDS));
                    for (int index = 0; index < 20; index++) { client.update(1); client.update(0); }
                    int edges = 0;
                    boolean pressed = false;
                    while (edges < 40) {
                        input.readFully(packet);
                        boolean next = packet[8] != 0;
                        if (pressed != next) { edges++; pressed = next; }
                    }
                    assertFalse(pressed);
                    byte[] feedback = new byte[100];
                    feedback[0] = 99; feedback[1] = 'L'; feedback[2] = 'E'; feedback[3] = 'D'; feedback[4] = 42;
                    for (byte value : feedback) peer.getOutputStream().write(value);
                    assertTrue(leds.await(3, TimeUnit.SECONDS));
                    assertEquals(42, receivedLeds.get()[0]);
                }
            } finally { client.close(); }
        }
    }
}
