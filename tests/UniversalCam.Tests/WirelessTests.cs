using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniversalCam.Core.Protocol;
using UniversalCam.Core.Transport;

namespace UniversalCam.Tests;

public sealed class WirelessTests
{
    private static JsonDocument Decode(string code)
    {
        string data = code.Split("data=")[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(data.PadRight((data.Length + 3) / 4 * 4, '=')));
    }
    [Fact]
    public void PairingIsSingleUseAndCredentialSurvivesRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "camorip-wireless-" + Guid.NewGuid());
        try
        {
            string token, credential, id;
            using (var identity = WirelessIdentity.Load(directory))
            {
                using var qr = Decode(identity.CreatePairingCode(7781));
                token = qr.RootElement.GetProperty("token").GetString()!; id = identity.Id;
                Assert.Null(identity.Authenticate(new string('0', 64), true));
                credential = Assert.IsType<string>(identity.Authenticate(token, true));
                Assert.Null(identity.Authenticate(token, true));
                Assert.Null(identity.Authenticate(token, false));
                Assert.Equal("", identity.Authenticate(credential, false));
                Assert.DoesNotContain(credential, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "wireless-identity.bin"))));
            }
            using var reloaded = WirelessIdentity.Load(directory);
            Assert.Equal(id, reloaded.Id); Assert.Equal("", reloaded.Authenticate(credential, false));
            reloaded.ForgetDevices(); Assert.Null(reloaded.Authenticate(credential, false));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void RefreshAndCancelInvalidateOldPairingCodes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "camorip-wireless-" + Guid.NewGuid());
        try
        {
            using var identity = WirelessIdentity.Load(directory);
            using var first = Decode(identity.CreatePairingCode(7781));
            using var second = Decode(identity.CreatePairingCode(7781));
            Assert.Null(identity.Authenticate(first.RootElement.GetProperty("token").GetString()!, true));
            identity.CancelPairing();
            Assert.Null(identity.Authenticate(second.RootElement.GetProperty("token").GetString()!, true));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task TlsPairingReconnectAndFramedControlsWork()
    {
        string directory = Path.Combine(Path.GetTempPath(), "camorip-wireless-" + Guid.NewGuid());
        try
        {
            await using var transport = new WirelessTransport(directory, 0, advertise: false);
            using var qr = Decode(transport.BeginPairing()); var fields = qr.RootElement;
            string fingerprint = fields.GetProperty("fingerprint").GetString()!;
            string id = fields.GetProperty("id").GetString()!;
            string token = fields.GetProperty("token").GetString()!;
            var received = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.ControlMessageReceived += (_, message) => received.TrySetResult(message);
            async Task<(TcpClient, SslStream, JsonDocument)> Connect(string credential, bool pairing)
            {
                var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", transport.ListeningPort);
                var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
                    Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
                await tls.AuthenticateAsClientAsync("CamoRip");
                await tls.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = 1, pc = id, token = credential, pair = pairing }) + "\n"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var line = new List<byte>(); var b = new byte[1];
                while (await tls.ReadAsync(b, timeout.Token) > 0 && b[0] != 10) line.Add(b[0]);
                return (client, tls, JsonDocument.Parse(line.ToArray()));
            }
            var first = await Connect(token, true);
            string permanent;
            using (first.Item1) using (first.Item2) using (first.Item3)
            {
                Assert.True(first.Item3.RootElement.GetProperty("ok").GetBoolean());
                permanent = first.Item3.RootElement.GetProperty("token").GetString()!;
                byte[] json = new Hello { Capabilities = ["h264"] }.ToJsonBytes();
                await first.Item2.WriteAsync(new byte[] { 0 }.Concat(json).Concat(new byte[] { 10 }).ToArray());
                Assert.IsType<Hello>(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            var second = await Connect(permanent, false);
            using (second.Item1) using (second.Item2) using (second.Item3)
                Assert.True(second.Item3.RootElement.GetProperty("ok").GetBoolean());
            var rejected = await Connect(new string('0', 64), false);
            using (rejected.Item1) using (rejected.Item2) using (rejected.Item3)
                Assert.False(rejected.Item3.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { Directory.Delete(directory, true); }
    }
}
