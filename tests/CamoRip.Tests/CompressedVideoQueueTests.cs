using System.Diagnostics;
using CamoRip.Core.Protocol;
using CamoRip.Core.Video;

namespace CamoRip.Tests;

public class CompressedVideoQueueTests
{
    private static MediaFrame Video(bool keyframe = false, int bytes = 1) =>
        new(new FrameHeader(FrameHeader.StreamTypeVideo, (uint)bytes, 0, keyframe ? (byte)1 : (byte)0), new byte[bytes]);

    [Fact]
    public async Task Overflow_DiscardsBrokenGopOnce_WaitsForIdr_ThenPreservesOrder()
    {
        var queue = new CompressedVideoQueue(capacity: 2, maxAge: TimeSpan.FromDays(1));
        Assert.True(queue.Enqueue(Video(true)));
        Assert.True(queue.Enqueue(Video()));
        Assert.False(queue.Enqueue(Video()));
        var generation = queue.Generation;
        for (var index = 0; index < 100; index++) Assert.False(queue.Enqueue(Video()));
        Assert.Equal(generation, queue.Generation);
        Assert.Equal(1, queue.Snapshot().Recoveries);
        Assert.Equal(0, queue.Snapshot().Depth);
        Assert.True(queue.Snapshot().WaitingForKeyframe);
        var keyframe = Video(true);
        var delta = Video();
        Assert.True(queue.Enqueue(keyframe));
        Assert.True(queue.Enqueue(delta));
        queue.Complete();
        var result = new List<QueuedVideoFrame>();
        await foreach (var frame in queue.ReadAllAsync()) result.Add(frame);
        Assert.Equal(2, result.Count);
        Assert.Same(keyframe, result[0].Frame);
        Assert.Same(delta, result[1].Frame);
        Assert.All(result, frame => Assert.Equal(generation, frame.Generation));
    }

    [Fact]
    public async Task OverflowOnKeyframe_ResumesImmediatelyWithThatKeyframe()
    {
        var queue = new CompressedVideoQueue(capacity: 1, maxAge: TimeSpan.FromDays(1));
        queue.Enqueue(Video(true));
        var newest = Video(true);
        Assert.True(queue.Enqueue(newest));
        Assert.Equal(1, queue.Snapshot().Drops);
        queue.Complete();
        await foreach (var frame in queue.ReadAllAsync()) Assert.Same(newest, frame.Frame);
    }

    [Fact]
    public void ByteLimit_IsBounded_AndRejectsOversizeKeyframe()
    {
        var queue = new CompressedVideoQueue(maxBytes: 4);
        Assert.True(queue.Enqueue(Video(true, 3)));
        Assert.False(queue.Enqueue(Video(false, 2)));
        Assert.Equal(0, queue.Snapshot().Bytes);
        Assert.False(queue.Enqueue(Video(true, 5)));
        Assert.True(queue.Snapshot().WaitingForKeyframe);
        Assert.True(queue.Enqueue(Video(true, 4)));
        Assert.Equal(4, queue.Snapshot().Bytes);
    }

    [Fact]
    public async Task StaleBacklog_IsDiscardedBeforeDecode()
    {
        long now = 1;
        var queue = new CompressedVideoQueue(clock: () => now);
        queue.Enqueue(Video(true));
        now += Stopwatch.Frequency;
        queue.Complete();
        var count = 0;
        await foreach (var frame in queue.ReadAllAsync()) count++;
        Assert.Equal(0, count);
        Assert.Equal(1, queue.Snapshot().Recoveries);
        Assert.True(queue.Snapshot().WaitingForKeyframe);
    }

    [Fact]
    public async Task Reset_InvalidatesInFlightGenerationAndQueuedFrames()
    {
        var queue = new CompressedVideoQueue();
        queue.Enqueue(Video(true));
        var oldGeneration = queue.Generation;
        queue.Reset("unplugged");
        Assert.NotEqual(oldGeneration, queue.Generation);
        Assert.False(queue.Enqueue(Video()));
        queue.Complete();
        Assert.False(queue.Enqueue(Video(true)));
        var count = 0;
        await foreach (var frame in queue.ReadAllAsync()) count++;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ProducerAndConsumer_DoNotLoseWakeups()
    {
        var queue = new CompressedVideoQueue(capacity: 1024, maxAge: TimeSpan.FromDays(1));
        var consume = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var frame in queue.ReadAllAsync()) count++;
            return count;
        });
        for (var index = 0; index < 1000; index++)
        {
            Assert.True(queue.Enqueue(Video(index == 0)));
            if (index % 13 == 0) await Task.Yield();
        }
        queue.Complete();
        Assert.Equal(1000, await consume.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
