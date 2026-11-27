namespace AuxLauncher.Guard;
internal sealed record EndpointRecord(ulong Id,int Pid,long Created,byte Protocol,string LocalAddress,int LocalPort,string RemoteAddress,int RemotePort,long Timestamp,bool Flow);
