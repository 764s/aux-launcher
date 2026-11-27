using AuxLauncher.Core;
namespace AuxLauncher.Guard;
internal sealed class TcpRoute(string id,CapturedPacket first,OwnershipEvidence owner,int aliasPort,int listenerPort)
{
    public string Id {get;}=id;
    public CapturedPacket First {get;}=first;
    public OwnershipEvidence Owner {get;}=owner;
    public int AliasPort {get;}=aliasPort;
    public int ListenerPort {get;}=listenerPort;
    public volatile int State; // 0 等待代理，1 转发，2 失败或结束。
    public DateTime LastUse=DateTime.UtcNow;
    public DateTime? ClosedAt;
    public FlowKey ReverseKey=>new(6,First.Packet.Source.ToString(),ListenerPort,First.Packet.Destination.ToString(),AliasPort);
}
