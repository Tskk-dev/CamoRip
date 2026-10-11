using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UniversalCam.Core.Transport;

public sealed record UsbMuxDevice(uint DeviceId, string SerialNumber);
public enum UsbPairingState { Paired, Missing, Unknown }

/// <summary>
/// Original managed implementation of the usbmux plist wire protocol.
/// Only connects to Apple's service on IPv4 loopback; network devices are excluded.
/// Protocol references: libimobiledevice/libusbmuxd include/usbmuxd-proto.h and
/// src/libusbmuxd.c (message layouts, Windows service port, port byte order).
/// No libusbmuxd code or executable is linked or redistributed.
/// </summary>
public sealed class UsbMuxClient
{
    public const int DefaultServicePort = 27015;
    private const int HeaderLength = 16;
    private const uint PlistMessage = 8;
    private const int MaxReplyLength = 4 * 1024 * 1024;
    private readonly int _servicePort;

    public UsbMuxClient(int servicePort = DefaultServicePort)
    {
        if (servicePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(servicePort));
        _servicePort = servicePort;
    }

    public async Task<IReadOnlyList<UsbMuxDevice>> ListUsbDevicesAsync(CancellationToken ct = default)
    {
        using var timeout = CreateTimeout(ct);
        using var client = await OpenServiceAsync(timeout.Token);
        var reply = await RequestAsync(client.GetStream(), "ListDevices", [], timeout.Token);
        var array = Value(reply, "DeviceList");
        if (array?.Name != "array")
            throw new InvalidDataException("Apple USB service did not return a device list.");
        var result = new List<UsbMuxDevice>();
        foreach (var item in array.Elements("dict"))
        {
            var properties = Value(item, "Properties");
            if (properties?.Name != "dict" || Value(properties, "ConnectionType")?.Value != "USB") continue;
            var idText = Value(item, "DeviceID")?.Value ?? Value(properties, "DeviceID")?.Value;
            if (!uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var serial = Value(properties, "SerialNumber")?.Value;
            if (!string.IsNullOrWhiteSpace(serial)) result.Add(new UsbMuxDevice(id, serial));
        }
        return result;
    }

    public async Task<UsbPairingState> CheckPairingAsync(UsbMuxDevice device, CancellationToken ct = default)
    {
        using var timeout = CreateTimeout(ct);
        using var client = await OpenServiceAsync(timeout.Token);
        var reply = await RequestAsync(client.GetStream(), "ReadPairRecord",
            [Entry("PairRecordID", "string", device.SerialNumber)], timeout.Token);
        // Record data contains credentials. Inspect presence only; never decode or log it.
        if (Value(reply, "PairRecordData") is { Name.LocalName: "data" } record &&
            !string.IsNullOrWhiteSpace(record.Value)) return UsbPairingState.Paired;
        var code = Value(reply, "Number")?.Value;
        return code == "2" ? UsbPairingState.Missing : UsbPairingState.Unknown;
    }

    public async Task<TcpClient> OpenTunnelAsync(UsbMuxDevice device, ushort devicePort, CancellationToken ct = default)
    {
        using var timeout = CreateTimeout(ct);
        var client = await OpenServiceAsync(timeout.Token);
        try
        {
            // PortNumber is a host integer containing the network-byte-order ushort.
            var networkPort = BinaryPrimitives.ReverseEndianness(devicePort);
            var reply = await RequestAsync(client.GetStream(), "Connect",
                [Entry("DeviceID", "integer", device.DeviceId.ToString(CultureInfo.InvariantCulture)),
                 Entry("PortNumber", "integer", networkPort.ToString(CultureInfo.InvariantCulture))], timeout.Token);
            var number = Value(reply, "Number")?.Value;
            if (Value(reply, "MessageType")?.Value != "Result" || number != "0")
                throw new UsbMuxException(int.TryParse(number, out var code) ? code : -1);
            // Exactly one reply was consumed. The same stream is now raw app data.
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return timeout;
    }

    private async Task<TcpClient> OpenServiceAsync(CancellationToken ct)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, _servicePort, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static XElement[] Entry(string key, string type, string value) =>
        [new XElement("key", key), new XElement(type, value)];

    private static async Task<XElement> RequestAsync(NetworkStream stream, string message,
        XElement[][] extraEntries, CancellationToken ct)
    {
        const uint tag = 1; // A new service connection owns every request.
        var dictionary = new XElement("dict",
            Entry("MessageType", "string", message),
            Entry("ClientVersionString", "string", "PocketCam 0.1"),
            Entry("ProgName", "string", "PocketCam"),
            Entry("kLibUSBMuxVersion", "integer", "3"), extraEntries);
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary));
        var payload = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        var packet = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), PlistMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), tag);
        payload.CopyTo(packet, HeaderLength);
        await stream.WriteAsync(packet, ct);

        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var replyTag = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var validPlist = version == 1 && type == PlistMessage;
        var validBinaryResult = version <= 1 && type == 1 && length == HeaderLength + 4;
        if (length < HeaderLength || length > MaxReplyLength || (!validPlist && !validBinaryResult) || replyTag != tag)
            throw new InvalidDataException("Invalid response from the Apple USB service.");
        var body = new byte[(int)length - HeaderLength];
        await stream.ReadExactlyAsync(body, ct);
        if (validBinaryResult)
            return new XElement("dict", Entry("MessageType", "string", "Result"),
                Entry("Number", "integer", BinaryPrimitives.ReadUInt32LittleEndian(body).ToString(CultureInfo.InvariantCulture)));
        using var reader = XmlReader.Create(new MemoryStream(body), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
            MaxCharactersInDocument = MaxReplyLength
        });
        var parsed = XDocument.Load(reader);
        return parsed.Root?.Element("dict") ?? throw new InvalidDataException("Invalid Apple USB service plist.");
    }

    private static XElement? Value(XElement dictionary, string key)
    {
        var element = dictionary.Elements("key").FirstOrDefault(candidate => candidate.Value == key);
        return element?.ElementsAfterSelf().FirstOrDefault();
    }
}

public sealed class UsbMuxException : IOException
{
    public int ResultCode { get; }
    public UsbMuxException(int code) : base(code switch
    {
        2 => "The iPhone disconnected from USB. Reconnect the cable and unlock the phone.",
        3 => "The iPhone app is not accepting connections. Unlock the phone and open PocketCam; check Trust This Computer in Apple Devices.",
        6 => "Apple USB service does not support the expected protocol. Update Apple Devices or iTunes.",
        _ => $"Apple USB service rejected the connection (code {code}). Unlock the phone and check Apple Devices."
    }) => ResultCode = code;
}
