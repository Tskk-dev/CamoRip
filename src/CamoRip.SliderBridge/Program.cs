using CamoRip.SliderBridge;
using System.IO.MemoryMappedFiles;

bool benchmark = args.Length == 1 && args[0] == "--benchmark";
if (args.Length > 1 || (args.Length == 1 && !benchmark && (!int.TryParse(args[0], out int value) || value is < 1 or > 65535)))
{
    Console.Error.WriteLine("Usage: CamoRip.SliderBridge.exe [port: 52468 | --benchmark]");
    return 1;
}
int port = benchmark ? 62468 : args.Length == 1 ? int.Parse(args[0]) : 52468;
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.WriteLine($"CamoRip Slider · localhost:{port}");
Console.WriteLine($"Run: adb reverse localabstract:camorip-slider-{port} tcp:{port}");
Console.WriteLine("Start the game with Brokenithm-Evolved. AIR remains controlled by CamoRip.");
try
{
    string name = benchmark ? @"Local\CamoRip_Slider_Benchmark_" + Guid.NewGuid() : @"Local\BROKENITHM_SHARED_BUFFER";
    using var isolated = benchmark ? MemoryMappedFile.CreateNew(name, 150) : null;
    if (benchmark) Console.WriteLine("Isolated benchmark mapping; packets cannot reach the game.");
    using var memory = new SliderMemory(name);
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
