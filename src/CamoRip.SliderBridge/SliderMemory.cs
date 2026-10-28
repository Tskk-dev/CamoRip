using System.IO.MemoryMappedFiles;

namespace CamoRip.SliderBridge;

public sealed class SliderMemory(string mappingName = @"Local\BROKENITHM_SHARED_BUFFER") : IDisposable
{
    private readonly object sync = new();
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private readonly byte[] released = new byte[32];

    public bool Apply(byte[] packet)
    {
        if (packet.Length != 42 || packet[0] != 41 || packet[1] != 'I' || packet[2] != 'P' || packet[3] != 'T')
            throw new InvalidDataException("Expected a slider-only IPT packet.");
        lock (sync)
        {
            if (!open()) return false;
            view!.WriteArray(6, packet, 8, 32);
            return true;
        }
    }

    public byte[] LedFrame()
    {
        byte[] frame = new byte[100];
        frame[0] = 99; frame[1] = (byte)'L'; frame[2] = (byte)'E'; frame[3] = (byte)'D';
        lock (sync)
        {
            if (open()) view!.ReadArray(38, frame, 4, 96);
        }
        return frame;
    }

    public void Release()
    {
        lock (sync) { view?.WriteArray(6, released, 0, 32); }
    }

    private bool open()
    {
        if (view != null) return true;
        try
        {
            mapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
            view = mapping.CreateViewAccessor(0, 134, MemoryMappedFileAccess.ReadWrite);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            mapping?.Dispose();
            mapping = null;
            return false;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            Release();
            view?.Dispose(); view = null;
            mapping?.Dispose(); mapping = null;
        }
    }
}
