package com.camorip.slider;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;

public final class SliderProtocol {
    public static byte[] input(long mask, int sequence) {
        byte[] packet = new byte[42];
        fillInput(packet, mask, sequence);
        return packet;
    }

    /** Fills a reusable 42-byte input packet with test/service cleared; the sender sets them. */
    public static void fillInput(byte[] packet, long mask, int sequence) {
        packet[0] = 41;
        packet[1] = 'I'; packet[2] = 'P'; packet[3] = 'T';
        packet[4] = (byte) (sequence >>> 24); packet[5] = (byte) (sequence >>> 16);
        packet[6] = (byte) (sequence >>> 8); packet[7] = (byte) sequence;
        for (int sensor = 0; sensor < 32; sensor++)
            packet[8 + sensor] = (mask & (1L << sensor)) == 0 ? 0 : (byte) 0x80;
        packet[40] = 0; packet[41] = 0;
    }

    public static byte[] readFrame(InputStream input) throws IOException {
        int length = input.read();
        if (length < 0) throw new EOFException("PC disconnected");
        if (length < 3 || length > 127) throw new IOException("Invalid PC packet length");
        byte[] frame = new byte[length + 1];
        frame[0] = (byte) length;
        int offset = 1;
        while (offset < frame.length) {
            int count = input.read(frame, offset, frame.length - offset);
            if (count < 0) throw new EOFException("Incomplete PC packet");
            offset += count;
        }
        return frame;
    }
}
