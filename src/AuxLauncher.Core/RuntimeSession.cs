namespace AuxLauncher.Core;
public sealed record RuntimeSession(string SessionId,string Key,string GuardPipe,int GuardPid,long GuardCreated,string RelayPipe,string RelayControlPipe,int RelayPid,long RelayCreated,string TargetPath,int RootPid,long RootCreated,ProxyEndpoint? Proxy=null);
