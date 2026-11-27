namespace AuxLauncher.Core;
public readonly record struct FlowKey(byte Protocol,string LocalAddress,int LocalPort,string RemoteAddress,int RemotePort)
{
    public FlowKey Reverse()=>new(Protocol,RemoteAddress,RemotePort,LocalAddress,LocalPort);
}
