namespace AuxLauncher.Core;
public sealed class SuspendedProcess(SafeKernelHandle process,SafeKernelHandle thread,ProcessIdentity identity) : IDisposable
{
    public SafeKernelHandle Process { get; }=process;
    public SafeKernelHandle Thread { get; }=thread;
    public ProcessIdentity Identity { get; }=identity;
    public void Dispose(){Thread.Dispose();Process.Dispose();}
}
