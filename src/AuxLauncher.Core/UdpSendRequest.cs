namespace AuxLauncher.Core;
public sealed record UdpSendRequest(string Id,string TargetAddress,int TargetPort,byte[] Payload);
