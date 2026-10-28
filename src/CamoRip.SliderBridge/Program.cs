using CamoRip.SliderBridge;

if (args.Length > 1 || (args.Length == 1 && (!int.TryParse(args[0], out int value) || value is < 1 or > 65535)))
{
    Console.Error.WriteLine("Usage: CamoRip.SliderBridge.exe [port: 52468]");
    return 1;
}
int port = args.Length == 1 ? int.Parse(args[0]) : 52468;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine($"CamoRip Slider · localhost:{port}");
Console.WriteLine($"Run: adb reverse tcp:{port} tcp:{port}");
Console.WriteLine("Start the game with Brokenithm-Evolved. AIR remains controlled by CamoRip.");
try
{
    using var memory = new SliderMemory();
    var server = new SliderServer(memory, port);
    server.Status += Console.WriteLine;
    await server.RunAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
