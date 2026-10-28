package com.camorip.slider;

import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

final class AdbLink implements SliderConnection.Link {
    private final LocalSocket socket = new LocalSocket();
    private volatile boolean closed;

    @Override public void connect(int port) throws IOException {
        socket.setSendBufferSize(1024);
        socket.connect(new LocalSocketAddress("camorip-slider-" + port, LocalSocketAddress.Namespace.ABSTRACT));
    }
    @Override public InputStream input() throws IOException { return socket.getInputStream(); }
    @Override public OutputStream output() throws IOException { return socket.getOutputStream(); }
    @Override public boolean closed() { return closed; }
    @Override public void close() throws IOException { closed = true; socket.close(); }
}
