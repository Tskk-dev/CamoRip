using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace UniversalCam.Core.Transport;

/// <summary>Per-user certificate and device credentials protected by Windows DPAPI.</summary>
public sealed class WirelessIdentity : IDisposable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public byte[] CertificatePfx { get; set; } = [];
    public List<string> DeviceTokenHashes { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public X509Certificate2 Certificate { get; private set; } = null!;
    [System.Text.Json.Serialization.JsonIgnore] private string _path = "";
    private string? _pairingHash;
    private DateTimeOffset _expires;
    private readonly object _gate = new();
    public string ServiceName => "CamoRip-" + Id;
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

    public static WirelessIdentity Load(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wireless-identity.bin");
        WirelessIdentity identity;
        if (File.Exists(path))
            identity = JsonSerializer.Deserialize<WirelessIdentity>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser))
                ?? throw new InvalidDataException("Wireless pairing identity is unreadable.");
        else
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=CamoRip Local Camera", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            identity = new() { CertificatePfx = certificate.Export(X509ContentType.Pfx) };
        }
        identity._path = path;
        // Schannel needs a user key container for the server's private key.
        // The container is removed on Dispose; the saved PFX stays DPAPI-protected.
        identity.Certificate = new(identity.CertificatePfx, (string?)null, X509KeyStorageFlags.UserKeySet);
        identity.Save();
        return identity;
    }

    public string CreatePairingCode(int port)
    {
        lock (_gate)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _pairingHash = Hash(token); _expires = DateTimeOffset.UtcNow.AddMinutes(5);
            var hosts = LocalAddresses().Select(a => a.Address.ToString()).Distinct().Take(16).ToArray();
            if (hosts.Length == 0) throw new IOException("Connect this PC to your local network before pairing.");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, id = Id, name = Environment.MachineName,
                service = ServiceName, hosts, port, fingerprint = Fingerprint, token, expires = _expires.ToUnixTimeSeconds() });
            return "camorip://pair?data=" + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    public string? Authenticate(string token, bool pairing)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        lock (_gate)
        {
            var hash = Hash(token);
            if (!pairing) return DeviceTokenHashes.Any(h => FixedEquals(h, hash)) ? "" : null;
            if (_pairingHash is null || DateTimeOffset.UtcNow > _expires || !FixedEquals(_pairingHash, hash)) return null;
            if (DeviceTokenHashes.Count >= 16) throw new InvalidOperationException("Forget paired phones before adding another.");
            var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            DeviceTokenHashes.Add(Hash(credential));
            try { Save(); } catch { DeviceTokenHashes.RemoveAt(DeviceTokenHashes.Count - 1); throw; }
            _pairingHash = null; // single-use only after persistence succeeds
            return credential;
        }
    }
    public void CancelPairing() { lock (_gate) _pairingHash = null; }
    public void ForgetDevices() { lock (_gate) { DeviceTokenHashes.Clear(); _pairingHash = null; Save(); } }
    private void Save()
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path + ".tmp", bytes); File.Move(_path + ".tmp", _path, true);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static bool FixedEquals(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
    internal static IEnumerable<UnicastIPAddressInformation> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && IsPrivateAddress(a.Address));
    private static bool IsPrivateAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }
    internal static bool IsLocalPeer(IPAddress remote)
    {
        if (IPAddress.IsLoopback(remote)) return true; // loopback integration tests
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var r = remote.GetAddressBytes();
        return LocalAddresses().Any(a => { var l = a.Address.GetAddressBytes(); var m = a.IPv4Mask.GetAddressBytes();
            return Enumerable.Range(0, 4).All(i => (l[i] & m[i]) == (r[i] & m[i])); });
    }
    public void Dispose() => Certificate.Dispose();
}
