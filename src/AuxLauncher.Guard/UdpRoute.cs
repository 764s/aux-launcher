using AuxLauncher.Core;
namespace AuxLauncher.Guard;
internal sealed class UdpRoute(string id,CapturedPacket first,OwnershipEvidence owner)
{
    public string Id {get;}=id;
    public OwnershipEvidence Owner {get;}=owner;
    public CapturedPacket Last=first;
    public DateTime LastUse=DateTime.UtcNow;
}
