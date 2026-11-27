namespace AuxLauncher.Core;
public sealed record GuardSnapshot(string SessionId,LauncherState State,string TargetPath,int RootPid,long RootCreated,int ProcessCount,bool ProtectionInterrupted,bool CaptureReady,long TcpConnections,long UdpDatagrams,long UnknownBypasses,string? Error=null);
