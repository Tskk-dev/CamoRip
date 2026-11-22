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
            // Raise status only when it changes: this runs on every AIR update and every
            // 100 ms tick, and each event costs a UI-thread dispatch.
            bool paused = updated != 0 && Environment.TickCount64 - updated >= 500;
            if (value != statusMask || paused != statusPaused)
            {
                statusMask = value; statusPaused = paused;
                report(paused
                    ? "AIR paused: phone updates timed out; all zones released."
                    : $"AIR zones (top to bottom): {string.Concat(Enumerable.Range(0, 6).Select(zone => (value >> zone) & 1))} · Brokenithm connected");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            close();
            statusMask = -1;
            report("Waiting for Brokenithm shared buffer. Start the game and its input DLL.");
        }
    }

    private int statusMask = -1;
    private bool statusPaused;
    private string? status;
    private void report(string text)
    {
        if (text == status) return;
        status = text;
        StatusChanged?.Invoke(this, text);
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
