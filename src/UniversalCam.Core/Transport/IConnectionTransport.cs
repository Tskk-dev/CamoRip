namespace UniversalCam.Core.Transport;

public interface IConnectionTransport : ITransport
{
    event EventHandler<string>? Diagnostic;
    string StatusMessage { get; }
    int ConnectionGeneration { get; }
    TransportType Kind { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task SendControlForConnectionAsync(Protocol.ControlMessage message, int generation, CancellationToken ct = default);
}
