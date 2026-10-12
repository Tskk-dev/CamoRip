using System.Buffers.Binary;
using System.Text;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Tests;

public class FrameParserTests
{

    /// Build a valid 14-byte binary frame header + payload bytes.
    private static byte[] BuildBinaryFrame(byte streamType, byte[] payload, long ptsUs = 0, byte flags = 0)
    {
        var header = new byte[FrameHeader.Size + payload.Length];
        header[0] = streamType;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), (uint)payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5), ptsUs);
        header[13] = flags;
        payload.CopyTo(header, FrameHeader.Size);
        return header;
    }

    /// Build a control frame: 0x00 + JSON bytes + 0x0A newline.
    private static byte[] BuildControlFrame(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[1 + jsonBytes.Length + 1];
        frame[0] = FrameHeader.StreamTypeControl;
        jsonBytes.CopyTo(frame, 1);
        frame[^1] = 0x0A;
        return frame;
    }


    [Fact]
    public void Feed_SingleVideoFrame_RaisesFrameParsed()
    {
        var parser = new FrameParser();
        MediaFrame? received = null;
        parser.FrameParsed += (_, f) => received = f;

        var fakeNal = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0xAB, 0xCD };
        var data    = BuildBinaryFrame(FrameHeader.StreamTypeVideo, fakeNal, ptsUs: 12345, flags: 0x01);

        parser.Feed(data);

        Assert.NotNull(received);
        Assert.True(received.IsVideo);
        Assert.Equal((long)12345, received.Header.PtsUs);
        Assert.True(received.Header.IsKeyframe);
        Assert.Equal(fakeNal, received.Payload);
    }

    [Fact]
    public void Feed_VideoFrameSplitAcrossThreeCalls_ReassemblesCorrectly()
    {
        var parser = new FrameParser();
        var frames = new List<MediaFrame>();
        parser.FrameParsed += (_, f) => frames.Add(f);

        var fakeNal = new byte[20];
        new Random(42).NextBytes(fakeNal);
        var full = BuildBinaryFrame(FrameHeader.StreamTypeVideo, fakeNal);

        int third = full.Length / 3;
        parser.Feed(full.AsSpan(0, third));
        Assert.Empty(frames); // No frame yet

        parser.Feed(full.AsSpan(third, third));
        Assert.Empty(frames); // Still incomplete

        parser.Feed(full.AsSpan(third * 2));
        Assert.Single(frames);
        Assert.Equal(fakeNal, frames[0].Payload);
    }


    [Fact]
    public void Feed_AudioFrame_IsAudioTrueAndChannelsParsed()
    {
        var parser = new FrameParser();
        MediaFrame? received = null;
        parser.FrameParsed += (_, f) => received = f;

        var aacData = new byte[128];
        var data    = BuildBinaryFrame(FrameHeader.StreamTypeAudio, aacData, flags: 2);

        parser.Feed(data);

        Assert.NotNull(received);
        Assert.True(received.IsAudio);
        Assert.Equal(2, received.Header.Channels);
    }


    [Fact]
    public void Feed_WelcomeControlMessage_RaisesControlMessageParsed()
    {
        var parser = new FrameParser();
        ControlMessage? received = null;
        parser.ControlMessageParsed += (_, m) => received = m;

        var data = BuildControlFrame("{\"type\":\"welcome\"}");
        parser.Feed(data);

        Assert.NotNull(received);
        Assert.IsType<Welcome>(received);
    }

    [Fact]
    public void Feed_HelloControlMessage_ParsesDeviceNameAndCapabilities()
    {
        var parser = new FrameParser();
        ControlMessage? received = null;
        parser.ControlMessageParsed += (_, m) => received = m;

        var json = "{\"type\":\"hello\",\"deviceName\":\"Test iPhone\",\"capabilities\":[\"h264\",\"aac_lc\"]}";
        parser.Feed(BuildControlFrame(json));

        var hello = Assert.IsType<Hello>(received);
        Assert.Equal("Test iPhone", hello.DeviceName);
        Assert.Contains("h264", hello.Capabilities);
    }


    [Fact]
    public void Feed_UnknownStreamTypeByte_SkipsAndDoesNotHang()
    {
        var parser = new FrameParser();
        var events = 0;
        parser.FrameParsed          += (_, _) => events++;
        parser.ControlMessageParsed += (_, _) => events++;

        // 0xFF lead byte followed by a valid video frame
        var validFrame = BuildBinaryFrame(FrameHeader.StreamTypeVideo, new byte[4]);
        var data = new byte[] { 0xFF }.Concat(validFrame).ToArray();

        parser.Feed(data);

        // The garbage byte is skipped, the valid frame is parsed
        Assert.Equal(1, events);
    }


    [Fact]
    public void Feed_TwoFramesInOneFeed_RaisesTwoEvents()
    {
        var parser = new FrameParser();
        var frames = new List<MediaFrame>();
        parser.FrameParsed += (_, f) => frames.Add(f);

        var f1 = BuildBinaryFrame(FrameHeader.StreamTypeVideo, new byte[8]);
        var f2 = BuildBinaryFrame(FrameHeader.StreamTypeAudio, new byte[16]);

        parser.Feed(f1.Concat(f2).ToArray());

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].IsVideo);
        Assert.True(frames[1].IsAudio);
    }

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData((uint)FrameParser.MaxMediaPayloadLength + 1)]
    public void Feed_AdvertisedOversizedPayload_IsRejectedAndParserCanReset(uint length)
    {
        var parser = new FrameParser();
        var header = new byte[FrameHeader.Size];
        header[0] = FrameHeader.StreamTypeVideo;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), length);
        Assert.Throws<InvalidDataException>(() => parser.Feed(header));
        var received = 0;
        parser.FrameParsed += (_, _) => received++;
        parser.Feed(BuildBinaryFrame(FrameHeader.StreamTypeVideo, [1, 2, 3]));
        Assert.Equal(1, received);
    }

    [Fact]
    public void Feed_UnterminatedOversizedControl_IsRejected()
    {
        var parser = new FrameParser();
        var bytes = new byte[FrameParser.MaxControlMessageLength + 2];
        Array.Fill(bytes, (byte)'a');
        bytes[0] = FrameHeader.StreamTypeControl;
        Assert.Throws<InvalidDataException>(() => parser.Feed(bytes));
    }

    [Fact]
    public void Feed_LargeMixedPacketsWithRandomFragmentation_PreservesOrder()
    {
        var parser = new FrameParser();
        var expected = Enumerable.Range(0, 12).Select(index =>
            Enumerable.Repeat((byte)index, 70_000 + index).ToArray()).ToArray();
        var wire = expected.SelectMany((payload, index) =>
            BuildBinaryFrame(FrameHeader.StreamTypeVideo, payload, index)).ToArray();
        var received = new List<MediaFrame>();
        parser.FrameParsed += (_, frame) => received.Add(frame);
        var random = new Random(0xC0FFEE);
        for (var offset = 0; offset < wire.Length;)
        {
            var count = Math.Min(random.Next(1, 40_000), wire.Length - offset);
            parser.Feed(wire.AsSpan(offset, count));
            offset += count;
        }
        Assert.Equal(expected.Length, received.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(index, received[index].Header.PtsUs);
            Assert.Equal(expected[index], received[index].Payload);
        }
    }
}
