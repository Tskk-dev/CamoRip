using CamoRip.Core.Protocol;

namespace CamoRip.Core.Transport;

public interface ITransport : IAsyncDisposable
{
    event EventHandler<ControlMessage> ControlMessageReceived;
    event EventHandler<MediaFrame> FrameReceived;
    event EventHandler<TransportState> StateChanged;
    TransportState State { get; }
    Task SendControlAsync(ControlMessage message, CancellationToken ct = default);
}

public enum TransportState { Idle, Listening, Connected, Disconnected, Error }
