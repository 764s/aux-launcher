using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuxLauncher.Core;

public sealed class SettingsStore(string directory)
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _path = Path.Combine(directory, "settings.json");
    public bool ReadOnly { get; private set; }
    public string? Warning { get; private set; }

    public UserSettings Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path))?.AsObject() ?? throw new JsonException();
            if (root["Version"]?.GetValue<int>() != 1) { ReadOnly = true; Warning = "设置版本不受支持，原文件已保留。"; return new(); }
            var secret = root["ProtectedPassword"]?.GetValue<string>();
            root.Remove("ProtectedPassword");
            var settings = root.Deserialize<UserSettings>() ?? throw new JsonException();
            if (settings.Proxy is not null && !string.IsNullOrEmpty(secret))
                settings = settings with { Proxy = settings.Proxy with { Password = SecretProtection.Unprotect(secret) } };
            return settings;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Warning = "无法恢复设置，请重新选择程序或确认代理入口。";
            try { File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { ReadOnly = true; }
            return new();
        }
    }
    public async Task SaveAsync(UserSettings settings)
    {
        if (ReadOnly) throw new IOException("原设置文件暂不可覆盖，本次更改尚未保存。");
        await _writer.WaitAsync();
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var root = JsonSerializer.SerializeToNode(settings with { Proxy = settings.Proxy is null ? null : settings.Proxy with { Password = null } })!.AsObject();
            if (!string.IsNullOrEmpty(settings.Proxy?.Password)) root["ProtectedPassword"] = SecretProtection.Protect(settings.Proxy.Password);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, root, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(true);
            }
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); _writer.Release(); }
    }
}
