package com.camorip.slider;

import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;

final class AdbLink implements SliderConnection.Link {
    private final LocalSocket socket = new LocalSocket();
    private final NetworkLink tcp = new NetworkLink("127.0.0.1", false);
    private boolean fallback;
    private volatile boolean closed;

    @Override public void connect(int port) throws IOException {
        try {
            socket.setSendBufferSize(1024);
            socket.connect(new LocalSocketAddress("camorip-slider-" + port, LocalSocketAddress.Namespace.ABSTRACT));
        } catch (IOException error) {
            socket.close();
            if (closed) throw error;
            // Some Android builds deny app access to adbd's abstract sockets.
            android.util.Log.w("CamoRipSlider", "Abstract USB tunnel unavailable; using TCP USB tunnel", error);
            fallback = true;
            tcp.connect(port);
        }
    }
    @Override public InputStream input() throws IOException { return fallback ? tcp.input() : socket.getInputStream(); }
    @Override public OutputStream output() throws IOException { return fallback ? tcp.output() : socket.getOutputStream(); }
    @Override public boolean closed() { return closed; }
    @Override public void close() throws IOException {
        closed = true;
        try { socket.close(); } finally { tcp.close(); }
    }
}
