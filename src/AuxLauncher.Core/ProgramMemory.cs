namespace AuxLauncher.Core;

public sealed class ProgramMemory
{
    public const int Capacity = 4;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    public UserSettings Settings { get; private set; }
    public ProgramMemory(UserSettings? settings = null) => Settings = Normalize(settings ?? new());

    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("仅支持 .exe 程序文件。");
        return full;
    }
    public void SelectNew(string path)
    {
        path = NormalizePath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("目标程序已移动或不存在。", path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
        SelectRecord(path);
    }
    public void SelectHistory(string path)
    {
        var existing = Settings.RecentPrograms.FirstOrDefault(x => PathComparer.Equals(x, path)) ?? throw new ArgumentException("历史记录不存在。");
        SelectRecord(existing);
    }
    private void SelectRecord(string path) => Settings = Settings with { SelectedProgram = path, RecentPrograms = new[] { path }.Concat(Settings.RecentPrograms.Where(x => !PathComparer.Equals(x, path))).Take(Capacity).ToArray() };
    public void Remove(string path) => Settings = Settings with { SelectedProgram = PathComparer.Equals(path, Settings.SelectedProgram) ? null : Settings.SelectedProgram, RecentPrograms = Settings.RecentPrograms.Where(x => !PathComparer.Equals(x, path)).ToArray() };
    public void SetProxy(ProxyEndpoint endpoint) { endpoint.Validate(); Settings = Settings with { Proxy = endpoint }; }

    private static UserSettings Normalize(UserSettings input)
    {
        var paths = new List<string>();
        foreach (var p in input.RecentPrograms ?? [])
        {
            try { var path = NormalizePath(p); if (!paths.Contains(path, PathComparer)) paths.Add(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        var selected = input.SelectedProgram is null ? null : paths.FirstOrDefault(x => PathComparer.Equals(x, input.SelectedProgram));
        return input with { RecentPrograms = paths.Take(Capacity).ToArray(), SelectedProgram = paths.Take(Capacity).Contains(selected, PathComparer) ? selected : null };
    }
}
