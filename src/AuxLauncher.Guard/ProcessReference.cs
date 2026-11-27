using AuxLauncher.Core;
namespace AuxLauncher.Guard;
internal sealed record ProcessReference(ProcessIdentity Identity,SafeKernelHandle Handle,bool Managed);
