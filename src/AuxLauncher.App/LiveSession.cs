using System.Diagnostics;
using AuxLauncher.Core;

namespace AuxLauncher.App;
internal sealed class LiveSession : IDisposable
{
    public required RuntimeSession Descriptor;
    public required ProxyEndpoint Proxy;
    public Process? GuardProcess;
    public Process? RelayProcess;
    public RpcConnection? Guard;
    public RpcConnection? Relay;
    public SessionJob? Job;
    public SuspendedProcess? Target;
    public bool CommitRequested;
    public bool ProtectionInterrupted;
    public bool GuardUnresponsive;
    public bool RecoveryCancelled;
    public int RecoveryAttempts;
    public Task? Recovery;
    public bool Closing;
    public bool RollbackPending;
    public string? StartFailureTitle;
    public string? StartFailureDetail;
    public void Dispose(){Target?.Dispose();Job?.Dispose();GuardProcess?.Dispose();RelayProcess?.Dispose();}
}
