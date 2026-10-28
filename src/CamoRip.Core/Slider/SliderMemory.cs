using System.IO.MemoryMappedFiles;

namespace CamoRip.Core.Slider;

public sealed class SliderMemory(string mappingName = @"Local\BROKENITHM_SHARED_BUFFER") : IDisposable
{
    private readonly object sync = new();
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private readonly byte[] released = new byte[32];

    public bool Apply(byte[] packet)
    {
        bool slider = packet.Length == 42 && packet[0] == 41 && packet[1] == 'I' && packet[2] == 'P' && packet[3] == 'T';
        bool function = packet.Length == 5 && packet[0] == 4 && packet[1] == 'F' && packet[2] == 'N' && packet[3] == 'C' && packet[4] is 1 or 2;
        bool card = packet.Length == 16 && packet[0] == 15 && packet[1] == 'C' && packet[2] == 'R' && packet[3] == 'D' && packet[4] == 1 && packet[5] == 1;
        if (!slider && !function && !card) throw new InvalidDataException("Unsupported controller packet.");
        if (card && packet.Skip(6).Any(value => (value & 15) > 9 || (value >> 4) > 9)) throw new InvalidDataException("Invalid card digits.");
        lock (sync)
        {
            if (!open()) return false;
            if (slider)
            {
                view!.WriteArray(6, packet, 8, 32);
                view.Write(134, (byte)(packet[40] == 0 ? 0 : 1));
                view.Write(135, (byte)(packet[41] == 0 ? 0 : 1));
            }
            else if (function) view!.Write(packet[4] == 1 ? 136 : 137, (byte)1);
            else
            {
                view!.WriteArray(140, packet, 6, 10);
                view.Write(139, (byte)1);
                view.Write(138, (byte)1);
            }
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
        lock (sync)
        {
            view?.WriteArray(6, released, 0, 32);
            view?.Write(134, (byte)0); view?.Write(135, (byte)0);
        }
    }

    private bool open()
    {
        if (view != null) return true;
        try
        {
            mapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
            view = mapping.CreateViewAccessor(0, 150, MemoryMappedFileAccess.ReadWrite);
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
