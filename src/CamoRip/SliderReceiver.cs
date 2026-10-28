using System.Diagnostics;
using System.IO;
using CamoRip.Core.Slider;

namespace CamoRip;

internal sealed class SliderReceiver : IAsyncDisposable
{
    private CancellationTokenSource? shutdown;
    private Task? listener;
    private SliderMemory? memory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    public event Action<string>? Changed;
    public bool Running => listener is { IsCompleted: false };

    public async Task StartAsync()
    {
        await lifecycle.WaitAsync();
        try {
        await stop();
        shutdown = new CancellationTokenSource();
        memory = new SliderMemory();
        var server = new SliderServer(memory, App.Settings.SliderPort, App.Settings.SliderMode != "usb");
        server.Status += status => Changed?.Invoke(status);
        CancellationToken token = shutdown.Token;
        listener = Task.Run(async () =>
        {
            try
            {
                Changed?.Invoke("Waiting for tablet");
                if (App.Settings.SliderMode == "udp") await server.RunUdpAsync(token);
                else await server.RunAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { App.Log(error); Changed?.Invoke(error.Message); }
        });
        } finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await stop(); } finally { lifecycle.Release(); }
    }

    private async Task stop()
    {
        shutdown?.Cancel();
        if (listener != null) await listener;
        listener = null;
        memory?.Dispose(); memory = null;
        shutdown?.Dispose(); shutdown = null;
        Changed?.Invoke("Off");
    }

    public static async Task<string[]> DevicesAsync(string executable)
    {
        string output = await adb(executable, "devices");
        return output.Split('\n').Select(line => line.Trim()).Where(line => line.EndsWith("\tdevice", StringComparison.Ordinal))
            .Select(line => line.Split('\t')[0]).ToArray();
    }

    public static Task<string> ConnectUsbAsync(string executable, string serial, int port)
        => adb(executable, "-s", serial, "reverse", $"localabstract:camorip-slider-{port}", $"tcp:{port}");

    private static async Task<string> adb(string executable, params string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "platform-tools", "adb.exe");
            executable = File.Exists(local) ? local : "adb.exe";
        }
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ADB.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new IOException("ADB timed out. Unlock the tablet and allow USB debugging."); }
        if (process.ExitCode != 0) throw new IOException((await error).Trim());
        return await output;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
