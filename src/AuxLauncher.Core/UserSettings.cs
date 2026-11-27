namespace AuxLauncher.Core;

public sealed record UserSettings
{
    public int Version { get; init; } = 1;
    public string[] RecentPrograms { get; init; } = [];
    public string? SelectedProgram { get; init; }
    public ProxyEndpoint? Proxy { get; init; }
}
