package com.camorip.slider;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramSocket;
import java.net.DatagramPacket;
import java.net.InetSocketAddress;
import java.net.Socket;

final class NetworkLink implements SliderConnection.Link {
    private final String host;
    private final boolean datagrams;
    private final Socket tcp = new Socket();
    private DatagramSocket udp;
    private volatile boolean closed;

    NetworkLink(String host, boolean datagrams) { this.host = host; this.datagrams = datagrams; }

    @Override public void connect(int port) throws IOException {
        if (!datagrams) {
            tcp.setTcpNoDelay(true); tcp.setSendBufferSize(1024);
            tcp.connect(new InetSocketAddress(host, port), 2000);
        } else { udp = new DatagramSocket(); udp.connect(new InetSocketAddress(host, port)); }
    }

    @Override public InputStream input() throws IOException {
        if (!datagrams) return tcp.getInputStream();
        return new InputStream() {
            private final byte[] buffer = new byte[128];
            private int offset, length;
            private void receive() throws IOException {
                while (offset == length) {
                    DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
                    udp.receive(packet); length = packet.getLength(); offset = 0;
                    if (length < 4 || (buffer[0] & 255) + 1 != length) { length = 0; continue; }
                }
            }
            @Override public int read() throws IOException { receive(); return buffer[offset++] & 255; }
            @Override public int read(byte[] target, int start, int count) throws IOException {
                if (count == 0) return 0;
                receive(); int size = Math.min(count, length - offset);
                System.arraycopy(buffer, offset, target, start, size); offset += size; return size;
            }
        };
    }

    @Override public OutputStream output() throws IOException {
        if (!datagrams) return tcp.getOutputStream();
        return new OutputStream() {
            @Override public void write(int value) throws IOException { write(new byte[] {(byte)value}); }
            @Override public void write(byte[] packet) throws IOException { udp.send(new DatagramPacket(packet, packet.length)); }
        };
    }

    @Override public boolean closed() { return closed; }
    @Override public void close() throws IOException { closed = true; if (udp != null) udp.close(); tcp.close(); }
}
