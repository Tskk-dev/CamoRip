using UniversalCam.Core.Protocol;
using UniversalCam.Core.Video;

namespace UniversalCam.Tests;

/// <summary>
/// Integration tests for the Windows H.264 decoder. Missing native binaries or a
/// missing OS codec fail these tests; a null decoded frame never counts as success.
/// </summary>
public class H264DecoderTests
{
    private static MediaFrame Video(byte[] payload, bool keyframe = true, long timestamp = 1_000_000) =>
        new(new FrameHeader(FrameHeader.StreamTypeVideo, (uint)payload.Length, timestamp, keyframe ? (byte)1 : (byte)0), payload);

    [Fact]
    public void Feed_ValidAnnexBIdr_Decodes64By64BlackNv12FrameWithTimestamp()
    {
        using var decoder = new H264Decoder();
        var frames = new List<DecodedFrame>();
        decoder.FrameDecoded += (_, frame) => frames.Add(frame);
        var bitstream = BlackIdrFixture.Create();
        // A decoder is allowed to buffer a frame. Repeated independent IDRs flush that latency.
        for (var index = 0; index < 3; index++) decoder.Feed(Video(bitstream, timestamp: 1_000_000 + index * 33_333));
        Assert.NotEmpty(frames);
        var first = frames[0];
        Assert.Equal(64, first.Width);
        Assert.Equal(64, first.Height);
        Assert.Equal(64 * 64 * 3 / 2, first.Data.Length);
        Assert.InRange(first.PtsUs, 1_000_000, 1_066_666);
        Assert.All(first.Data.Take(64 * 64), value => Assert.InRange(value, (byte)15, (byte)17));
        Assert.All(first.Data.Skip(64 * 64), value => Assert.InRange(value, (byte)127, (byte)129));
    }

    [Fact]
    public void RequestReset_IgnoresInterFramesUntilNextKeyframe_ThenResumesDecode()
    {
        using var decoder = new H264Decoder();
        var count = 0;
        decoder.FrameDecoded += (_, _) => count++;
        var bitstream = BlackIdrFixture.Create();
        for (var index = 0; index < 3; index++) decoder.Feed(Video(bitstream));
        Assert.True(count > 0);
        decoder.RequestReset();
        var before = count;
        decoder.Feed(Video([0, 0, 0, 1, 0x41, 0x80], keyframe: false));
        Assert.Equal(before, count);
        for (var index = 0; index < 3; index++) decoder.Feed(Video(bitstream));
        Assert.True(count > before);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(50, 50)]
    public void Feed_CroppedSps_ReturnsDisplaySizeAndPackedNv12WithoutMacroblockPadding(int width, int height)
    {
        using var decoder = new H264Decoder();
        var frames = new List<DecodedFrame>();
        decoder.FrameDecoded += (_, frame) => frames.Add(frame);
        var bitstream = BlackIdrFixture.Create(width, height);
        for (var index = 0; index < 3; index++) decoder.Feed(Video(bitstream, timestamp: 1_000_000 + index * 16_667));
        Assert.NotEmpty(frames);
        Assert.All(frames, frame =>
        {
            Assert.Equal(width, frame.Width);
            Assert.Equal(height, frame.Height);
            Assert.Equal(width * height * 3 / 2, frame.Data.Length);
            Assert.All(frame.Data.Take(width * height), value => Assert.InRange(value, (byte)15, (byte)17));
            Assert.All(frame.Data.Skip(width * height), value => Assert.InRange(value, (byte)127, (byte)129));
        });
    }

    [Fact]
    public void Feed_AudioOrEmptyVideo_DoesNotInitializeNativeDecoder()
    {
        using var decoder = new H264Decoder();
        decoder.Feed(new MediaFrame(new FrameHeader(FrameHeader.StreamTypeAudio, 4, 0, 2), [0, 0, 0, 0]));
        decoder.Feed(Video([]));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndFurtherFeedIsRejected()
    {
        var decoder = new H264Decoder();
        decoder.Feed(Video(BlackIdrFixture.Create()));
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.Feed(Video([])));
    }
}

/// <summary>
/// Original synthetic H.264 Baseline fixture, generated from syntax rather than
/// copied from an encoder. A 64×64 picture contains 16 I_PCM macroblocks: Y=16,
/// Cb=Cr=128. Annex B emulation prevention is applied to SPS, PPS and the IDR.
/// The Windows decoder's documented minimum dimensions are 48×48.
/// </summary>
internal static class BlackIdrFixture
{
    public static byte[] Create(int width = 64, int height = 64)
    {
        int columns = (width + 15) / 16, rows = (height + 15) / 16;
        int cropRight = (columns * 16 - width) / 2, cropBottom = (rows * 16 - height) / 2;
        var sps = new Bits();
        sps.Fixed(66, 8); sps.Fixed(0xC0, 8); sps.Fixed(width > 64 ? 42u : 30u, 8);
        sps.Ue(0); sps.Ue(0); sps.Ue(2); // SPS0, frame_num4bits, POC type2
        sps.Ue(1); sps.Fixed(0, 1); // one reference, no gaps
        sps.Ue((uint)(columns - 1)); sps.Ue((uint)(rows - 1));
        sps.Fixed(1, 1); sps.Fixed(1, 1); sps.Fixed(0, 1); // progressive, direct8x8
        sps.Fixed(cropRight != 0 || cropBottom != 0 ? 1u : 0u, 1);
        if (cropRight != 0 || cropBottom != 0)
        {
            sps.Ue(0); sps.Ue((uint)cropRight); sps.Ue(0); sps.Ue((uint)cropBottom);
        }
        sps.Fixed(0, 1); // no VUI

        var pps = new Bits();
        pps.Ue(0); pps.Ue(0); pps.Fixed(0, 1); pps.Fixed(0, 1); // PPS/SPS0, CAVLC
        pps.Ue(0); pps.Ue(0); pps.Ue(0); // no groups, default refs
        pps.Fixed(0, 1); pps.Fixed(0, 2); // no weighting
        pps.Ue(0); pps.Ue(0); pps.Ue(0); // signed zero QP/QS/chroma offsets
        pps.Fixed(1, 1); pps.Fixed(0, 1); pps.Fixed(0, 1); // deblocking flag present

        var slice = new Bits();
        slice.Ue(0); slice.Ue(2); slice.Ue(0); // first MB, I slice, PPS0
        slice.Fixed(0, 4); slice.Ue(0); // frame_num0, idr_pic_id0
        slice.Fixed(0, 1); slice.Fixed(0, 1); // IDR reference marking
        slice.Ue(0); slice.Ue(1); // signed-zero QP delta; disable deblocking
        for (var macroblock = 0; macroblock < columns * rows; macroblock++)
        {
            slice.Ue(25); // I_PCM macroblock in an I slice
            slice.Align();
            for (var sample = 0; sample < 256; sample++) slice.Fixed(16, 8);
            for (var sample = 0; sample < 128; sample++) slice.Fixed(128, 8);
        }
        return Nal(0x67, sps.Finish()).Concat(Nal(0x68, pps.Finish())).Concat(Nal(0x65, slice.Finish())).ToArray();
    }

    private static byte[] Nal(byte header, byte[] rbsp)
    {
        var output = new List<byte> { 0, 0, 0, 1, header };
        var zeros = 0;
        foreach (var value in rbsp)
        {
            if (zeros >= 2 && value <= 3) { output.Add(3); zeros = 0; }
            output.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return output.ToArray();
    }

    private sealed class Bits
    {
        private readonly List<bool> _bits = [];
        public void Fixed(uint value, int count)
        { for (var index = count - 1; index >= 0; index--) _bits.Add(((value >> index) & 1) != 0); }
        public void Ue(uint value)
        {
            var code = value + 1;
            var count = 0;
            for (var remaining = code; remaining > 1; remaining >>= 1) count++;
            Fixed(0, count);
            Fixed(code, count + 1);
        }
        public void Align() { while (_bits.Count % 8 != 0) _bits.Add(false); }
        public byte[] Finish()
        {
            _bits.Add(true); Align();
            var bytes = new byte[_bits.Count / 8];
            for (var index = 0; index < _bits.Count; index++)
                if (_bits[index]) bytes[index / 8] |= (byte)(1 << (7 - index % 8));
            return bytes;
        }
    }
}

