package com.camorip.slider;

import android.os.Bundle;
import android.util.Log;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import java.net.Socket;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.io.Closeable;
import java.io.InputStream;
import java.io.OutputStream;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;
import static org.junit.Assume.*;

@RunWith(AndroidJUnit4.class)
public class SliderLatencyTest {
    @Test public void measureUsbRoundTripToIsolatedPcMapping() throws Exception {
        Bundle arguments = InstrumentationRegistry.getArguments();
        assumeTrue("Requires an isolated PC benchmark bridge", "true".equals(arguments.getString("benchmark")));
        long[] samples = new long[200];
        Closeable connection;
        InputStream input;
        OutputStream output;
        if ("tcp".equals(arguments.getString("transport"))) {
            Socket socket = new Socket();
            socket.setTcpNoDelay(true);
            socket.connect(new InetSocketAddress("127.0.0.1", 62468), 3000);
            socket.setSoTimeout(3000);
            connection = socket; input = socket.getInputStream(); output = socket.getOutputStream();
        } else {
            LocalSocket socket = new LocalSocket();
            socket.connect(new LocalSocketAddress("camorip-slider-62468", LocalSocketAddress.Namespace.ABSTRACT));
            socket.setSoTimeout(3000);
            connection = socket; input = socket.getInputStream(); output = socket.getOutputStream();
        }
        try (Closeable ignored = connection) {
            for (int sequence = 1; sequence <= samples.length + 20; sequence++) {
                long start = System.nanoTime();
                output.write(SliderProtocol.input(sequence % 2, sequence));
                boolean acknowledged = false;
                for (int packet = 0; packet < 100; packet++) {
                    byte[] response = SliderProtocol.readFrame(input);
                    if (response.length == 8 && response[1] == 'A' && response[2] == 'C' && response[3] == 'K' &&
                        ByteBuffer.wrap(response).getInt(4) == sequence) { acknowledged = true; break; }
                }
                assertTrue("Input was not acknowledged", acknowledged);
                if (sequence > 20) samples[sequence - 21] = System.nanoTime() - start;
                Thread.sleep(5);
            }
            output.write(SliderProtocol.input(0, 999));
        }
        Arrays.sort(samples);
        String result = String.format(java.util.Locale.ROOT,
            "USB_RTT samples=200 median_ms=%.3f p95_ms=%.3f max_ms=%.3f",
            samples[100] / 1e6, samples[189] / 1e6, samples[199] / 1e6);
        Log.i("CamoRipSliderBenchmark", result);
        Bundle report = new Bundle();
        report.putString("stream", result + "\n");
        InstrumentationRegistry.getInstrumentation().sendStatus(0, report);
    }
}
