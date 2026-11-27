using AuxLauncher.Core;
namespace AuxLauncher.Guard;
internal sealed record CapturedPacket(PacketView Packet,DivertAddress Address);
