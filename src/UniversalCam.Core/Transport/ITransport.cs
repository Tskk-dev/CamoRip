using UniversalCam.Core.Protocol;

namespace UniversalCam.Core.Transport;

/// <summary>
/// Common interface for both QUIC (Wi-Fi) and TCP/USB transport implementations.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// Fires when a control message is received from the iPhone.
    event EventHandler<ControlMessage> ControlMessageReceived;

    /// Fires when a media frame (video or audio) is received.
    event EventHandler<MediaFrame> FrameReceived;

    /// Fires when connection state changes.
    event EventHandler<TransportState> StateChanged;

    TransportState State { get; }

    /// Send a control message to the connected iPhone.
    Task SendControlAsync(ControlMessage message, CancellationToken ct = default);
}

public enum TransportState
{
    Idle,
    Listening,
    Connected,
    Disconnected,
    Error,
}
