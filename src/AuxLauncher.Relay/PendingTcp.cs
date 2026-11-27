using System.Net.Sockets;
namespace AuxLauncher.Relay;
internal sealed record PendingTcp(string Id,TcpClient Upstream,DateTime Created);
