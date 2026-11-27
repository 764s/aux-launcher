namespace AuxLauncher.Core;
public sealed record TcpOpenRequest(string Id,string TargetAddress,int TargetPort,int AliasPort);
