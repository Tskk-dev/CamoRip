using UniversalCam.Core.Protocol;
using UniversalCam.Core.Video;
using UniversalCam.VirtualCamera;

namespace UniversalCam.Tests;

public class VideoSchedulingTests
{
    [Fact]
    public async Task SynchronousCompressedReader_WakesAndDrainsWithoutLosingFrames()
    {
        var queue = new CompressedVideoQueue(capacity: 1024, maxAge: TimeSpan.FromDays(1));
        var consumer = Task.Factory.StartNew(() => queue.ReadAll().Count(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        for (var index = 0; index < 1000; index++)
        {
            Assert.True(queue.Enqueue(new MediaFrame(new FrameHeader(FrameHeader.StreamTypeVideo, 1, index,
                index == 0 ? (byte)1 : (byte)0), [0])));
            if (index % 13 == 0) await Task.Yield();
        }
        queue.Complete();
        Assert.Equal(1000, await consumer.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void LatestMailbox_ReleasesSupersededAndRejectedFramesAndDrainsOnCompletion()
    {
        var discarded = new List<int>();
        var mailbox = new LatestFrameMailbox<int>(discarded.Add);
        mailbox.Publish(1);
        mailbox.Publish(2);
        mailbox.Complete();
        mailbox.Publish(3);
        Assert.Equal(new[] { 1, 3 }, discarded);
        Assert.Equal(new[] { 2 }, mailbox.ReadAll());
        Assert.Empty(mailbox.ReadAll());
    }

    [Fact]
    public async Task LatestMailbox_WakesWaitingDedicatedConsumerOnComplete()
    {
        var mailbox = new LatestFrameMailbox<int>(_ => { });
        var consumer = Task.Factory.StartNew(() => mailbox.ReadAll().Count(), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        mailbox.Complete();
        Assert.Equal(0, await consumer.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void PooledDecoder_HoldsRetainedFrameUntilBothConsumersReleaseIt()
    {
        using var decoder = new H264Decoder(preferHardware: false, reuseFrameBuffers: true);
        DecodedFrame? retained = null;
        byte[]? releasedBuffer = null;
        var count = 0;
        decoder.FrameDecoded += (_, frame) =>
        {
            count++;
            if (retained is null)
            {
                retained = frame.Retain();
                frame.Retain(); // independent camera and preview references
                retained.Release();
            }
            else
            {
                Assert.NotSame(retained.Data, frame.Data);
                if (releasedBuffer is not null) Assert.Same(releasedBuffer, frame.Data);
                releasedBuffer = frame.Data;
            }
        };
        var payload = BlackIdrFixture.Create();
        for (var index = 0; index < 12; index++)
            decoder.Feed(new MediaFrame(new FrameHeader(FrameHeader.StreamTypeVideo, (uint)payload.Length, index, 1), payload));
        Assert.True(count > 2);
        Assert.NotNull(retained);
        Assert.All(retained.Data.Take(64 * 64), value => Assert.InRange(value, (byte)15, (byte)17));
        retained.Release();
        Assert.Throws<ObjectDisposedException>(() => retained.Retain());
        Assert.Throws<InvalidOperationException>(() => retained.Release());
    }

    [Fact]
    public void ReusedConversionBuffers_MatchAllocatingPathsAndClearLetterboxing()
    {
        var source = new byte[64 * 48 * 3 / 2];
        Array.Fill(source, (byte)128);
        var preview = Enumerable.Repeat((byte)255, 128 * 72 * 4).ToArray();
        Assert.Same(preview, NV12Converter.CreateBgraPreview(source, 64, 48, 128, 72, preview));
        Assert.Equal(NV12Converter.CreateBgraPreview(source, 64, 48, 128, 72), preview);
        var resized = new byte[128 * 72 * 3 / 2];
        Assert.Same(resized, NV12Converter.FitNv12(source, 64, 48, 128, 72, resized));
        Assert.Equal(NV12Converter.FitNv12(source, 64, 48, 128, 72), resized);
        Assert.Throws<ArgumentException>(() => NV12Converter.CreateBgraPreview(source, 64, 48, 128, 72, new byte[1]));
        Assert.Throws<ArgumentException>(() => NV12Converter.FitNv12(source, 64, 48, 128, 72, new byte[1]));
    }
}
