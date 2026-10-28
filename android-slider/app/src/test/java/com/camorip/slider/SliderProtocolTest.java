package com.camorip.slider;

import java.io.ByteArrayInputStream;
import java.io.EOFException;
import org.junit.Test;
import static org.junit.Assert.*;

public class SliderProtocolTest {
    @Test public void packetUsesSliderOnlyHeaderAndAll32Sensors() {
        byte[] packet = SliderProtocol.input(0x80000001L, 0x01020304);
        assertEquals(42, packet.length);
        assertArrayEquals(new byte[] {41, 'I', 'P', 'T', 1, 2, 3, 4}, java.util.Arrays.copyOf(packet, 8));
        assertEquals((byte) 0x80, packet[8]);
        assertEquals((byte) 0x80, packet[39]);
        for (int index = 9; index < 39; index++) assertEquals(0, packet[index]);
        assertEquals(0, packet[40]); assertEquals(0, packet[41]);
    }

    @Test public void fragmentedLedFramesAreReassembledWithoutLosingTheNextFrame() throws Exception {
        byte[] packets = new byte[200];
        packets[0] = packets[100] = 99;
        packets[1] = packets[101] = 'L';
        packets[2] = packets[102] = 'E';
        packets[3] = packets[103] = 'D';
        packets[4] = 42; packets[104] = 99;
        ByteArrayInputStream stream = new ByteArrayInputStream(packets) {
            @Override public synchronized int read(byte[] buffer, int offset, int length) {
                return super.read(buffer, offset, Math.min(1, length));
            }
        };
        assertEquals(42, SliderProtocol.readFrame(stream)[4]);
        assertEquals(99, SliderProtocol.readFrame(stream)[4]);
        assertThrows(EOFException.class, () -> SliderProtocol.readFrame(stream));
        assertThrows(EOFException.class, () -> SliderProtocol.readFrame(new ByteArrayInputStream(new byte[] {99, 'L'})));
    }
}
