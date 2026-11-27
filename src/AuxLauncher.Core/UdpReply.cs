namespace AuxLauncher.Core;
public sealed record UdpReply(string Id,string SourceAddress,int SourcePort,byte[] Payload);
