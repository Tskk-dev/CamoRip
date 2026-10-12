using System.IO;

namespace CamoRip;

/// <summary>Serializes log writes and toggles so disabling returns only after all writes stop.</summary>
internal sealed class DiagnosticFileLog : IDisposable
{
    private readonly object _sync = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    internal DiagnosticFileLog(string directory) { _directory = directory; }
    internal void SetEnabled(bool enabled, bool rotate = false)
    {
        lock (_sync)
        {
            _writer?.Dispose(); _writer = null;
            if (!enabled) return;
            try
            {
                Directory.CreateDirectory(_directory);
                var latest = Path.Combine(_directory, "latest.log");
                if (rotate && File.Exists(latest)) File.Copy(latest, Path.Combine(_directory, "previous.log"), true);
                _writer = new StreamWriter(latest, !rotate) { AutoFlush = true };
                Write("INFO", "CamoRip diagnostic logging enabled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    internal void Write(string level, string message)
    {
        lock (_sync)
        {
            try { _writer?.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }
    public void Dispose() => SetEnabled(false);
}
