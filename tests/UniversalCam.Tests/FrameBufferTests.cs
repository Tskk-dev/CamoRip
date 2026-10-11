using UniversalCam.VirtualCamera;

namespace UniversalCam.Tests;

public class FrameBufferTests
{
    [Fact]
    public void FrameBuffer_Enqueue_Dequeue_RoundTrip()
    {
        // Arrange
        var buffer = new FrameBuffer(capacity: 4);
        var frame = new NV12Frame
        {
            Width = 1920,
            Height = 1080,
            PtsUs = 12345,
            Data = new byte[1920 * 1080 * 3 / 2]
        };

        // Act
        buffer.Enqueue(frame);
        bool dequeued = buffer.TryDequeue(out var result);

        // Assert
        Assert.True(dequeued);
        Assert.Equal(frame.Width, result.Width);
        Assert.Equal(frame.Height, result.Height);
        Assert.Equal(frame.PtsUs, result.PtsUs);
    }

    [Fact]
    public void FrameBuffer_EmptyBuffer_TryDequeueReturnsFalse()
    {
        // Arrange
        var buffer = new FrameBuffer(capacity: 4);

        // Act
        bool dequeued = buffer.TryDequeue(out var result);

        // Assert
        Assert.False(dequeued);
    }

    [Fact]
    public void FrameBuffer_OverCapacity_DropsOldest()
    {
        // Arrange
        var buffer = new FrameBuffer(capacity: 2);
        var frame1 = CreateFrame(pts: 100);
        var frame2 = CreateFrame(pts: 200);
        var frame3 = CreateFrame(pts: 300);

        // Act
        buffer.Enqueue(frame1);
        buffer.Enqueue(frame2);
        buffer.Enqueue(frame3);  // Should drop frame1

        // Assert: Should have frame2 and frame3
        Assert.True(buffer.TryDequeue(out var result1));
        Assert.Equal(200, result1.PtsUs);

        Assert.True(buffer.TryDequeue(out var result2));
        Assert.Equal(300, result2.PtsUs);

        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public void FrameBuffer_FillAndEmpty_Fifo()
    {
        // Arrange
        var buffer = new FrameBuffer(capacity: 3);
        var frames = new[] { CreateFrame(100), CreateFrame(200), CreateFrame(300) };

        // Act & Assert
        foreach (var f in frames)
            buffer.Enqueue(f);

        Assert.Equal(3, buffer.Count);

        for (int i = 0; i < frames.Length; i++)
        {
            Assert.True(buffer.TryDequeue(out var result));
            Assert.Equal((i + 1) * 100, result.PtsUs);
        }

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void FrameBuffer_Clear_RemovesAllFrames()
    {
        // Arrange
        var buffer = new FrameBuffer(capacity: 4);
        buffer.Enqueue(CreateFrame(100));
        buffer.Enqueue(CreateFrame(200));

        // Act
        buffer.Clear();

        // Assert
        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public async Task FrameBuffer_ConcurrentProducerConsumer_IsThreadSafe()
    {
        // Arrange
        var buffer = new FrameBuffer(capacity: 10);
        var frames = new List<long>();
        var errors = new List<string>();

        // Act: Produce frames on one task, consume on another
        var producer = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    buffer.Enqueue(CreateFrame(i * 1000));
                    Thread.Sleep(1);  // Small delay
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                    errors.Add($"Producer error: {ex.Message}");
            }
        });

        var consumer = Task.Run(() =>
        {
            try
            {
                int consumed = 0;
                while (consumed < 100)
                {
                    if (buffer.TryDequeue(out var frame))
                    {
                        lock (frames)
                            frames.Add(frame.PtsUs);
                        consumed++;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                    errors.Add($"Consumer error: {ex.Message}");
            }
        });

        // Use await instead of blocking Task.WaitAll to satisfy xUnit1031
        await Task.WhenAll(producer, consumer);

        // Assert
        Assert.Empty(errors);
        Assert.Equal(100, frames.Count);
    }

    [Fact]
    public void FrameBuffer_InvalidCapacity_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new FrameBuffer(capacity: 0));
    }

    private static NV12Frame CreateFrame(long pts)
    {
        return new NV12Frame
        {
            Width = 1920,
            Height = 1080,
            PtsUs = pts,
            Data = new byte[1920 * 1080 * 3 / 2]
        };
    }
}
