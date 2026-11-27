using System.Text.Json;

namespace AuxLauncher.Core;

public sealed class AppLog(string directory, string component)
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(directory, component + ".jsonl");
    private readonly Dictionary<string, DateTime> _last = new();
    public void Write(string name, object? data = null, string? rateKey = null)
    {
        lock (_gate)
        {
            if (rateKey is not null && _last.TryGetValue(rateKey, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(5)) return;
            if (_last.Count >= 4096) _last.Clear();
            if (rateKey is not null) _last[rateKey] = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > 4 * 1024 * 1024) File.Move(_path, _path + ".previous", true);
                File.AppendAllText(_path, JsonSerializer.Serialize(new { time = DateTimeOffset.Now, component, name, data }) + "\n");
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
