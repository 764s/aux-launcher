namespace AuxLauncher.Core;

public enum LauncherState { Idle, Ready, Missing, Preparing, Running, ProxyLost, ProtectionInterrupted, Recovering, Failed }

public static class StateRules
{
    public static bool IsLocked(this LauncherState state) => state is LauncherState.Preparing or LauncherState.Running or LauncherState.ProxyLost or LauncherState.ProtectionInterrupted or LauncherState.Recovering;
    public static LauncherState ForSelection(string? path) => path is null ? LauncherState.Idle : File.Exists(path) ? LauncherState.Ready : LauncherState.Missing;
}
