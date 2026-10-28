using System.IO.MemoryMappedFiles;

namespace CamoRip.Core.Air;

public sealed class AirInputBridge : IDisposable
{
    private readonly object sync = new();
    private readonly Timer timer;
    private readonly string mappingName;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private long updated;
    private int mask;
    private bool enabled;
    private bool disposed;
    public event EventHandler<string>? StatusChanged;

    public AirInputBridge(string mappingName = @"Local\BROKENITHM_SHARED_BUFFER")
    {
        this.mappingName = mappingName;
        timer = new Timer(_ => tick(), null, 100, 100);
    }

    public void SetEnabled(bool value)
    {
        lock (sync)
        {
            if (disposed) return;
            if (enabled == value) return;
            enabled = value;
            mask = 0;
            updated = 0;
            write(0);
            if (!value) close();
        }
    }

    public void Update(int value)
    {
        if (value is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync)
        {
            if (!enabled || disposed) return;
            mask = value;
            updated = Environment.TickCount64;
            write(mask);
        }
    }

    public static int SafeMask(int mask, long elapsedMilliseconds) => elapsedMilliseconds is >= 0 and < 500 ? mask & 63 : 0;

    public static byte[] BeamBytes(int mask)
    {
        if (mask is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(mask));
        var bytes = new byte[6];
        for (int zone = 0; zone < 6; zone++)
            bytes[(5 - zone) ^ 1] = (byte)((mask >> zone) & 1);
        return bytes;
    }

    private void tick()
    {
        lock (sync)
        {
            if (enabled && !disposed)
                write(updated == 0 ? 0 : SafeMask(mask, Environment.TickCount64 - updated));
        }
    }

    private void write(int value)
    {
        try
        {
            if (view is null)
            {
                if (!enabled) return;
                mapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
                view = mapping.CreateViewAccessor(0, 0x88, MemoryMappedFileAccess.ReadWrite);
            }
            var bytes = BeamBytes(value);
            view.WriteArray(0, bytes, 0, bytes.Length);
            StatusChanged?.Invoke(this, updated != 0 && Environment.TickCount64 - updated >= 500
                ? "Input paused"
                : "Connected");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            close();
            StatusChanged?.Invoke(this, "Waiting for game");
        }
    }

    private void close() { view?.Dispose(); view = null; mapping?.Dispose(); mapping = null; }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            enabled = false;
            write(0);
            close();
            disposed = true;
        }
        timer.Dispose();
    }
}
