using System.Net;
using System.Net.Sockets;

namespace AuxLauncher.Tests;
internal sealed class EchoServers : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop=new();
    private readonly TcpListener _tcp=new(IPAddress.Loopback,0);
    private readonly UdpClient _udp=new(new IPEndPoint(IPAddress.Loopback,0));
    private readonly UdpClient _dns=new(new IPEndPoint(IPAddress.Loopback,0));
    public int TcpPort=>((IPEndPoint)_tcp.LocalEndpoint).Port;
    public int UdpPort=>((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int DnsPort=>((IPEndPoint)_dns.Client.LocalEndPoint!).Port;
    public EchoServers(){_tcp.Start();_=AcceptAsync();_=UdpLoop(_udp,false);_=UdpLoop(_dns,true);}
    private async Task AcceptAsync()
    {
        try{while(!_stop.IsCancellationRequested){var client=await _tcp.AcceptTcpClientAsync(_stop.Token);_=EchoAsync(client);}}catch(OperationCanceledException){}catch(SocketException){}
    }
    private async Task EchoAsync(TcpClient client)
    {
        using(client)try{var buffer=new byte[8192];var stream=client.GetStream();int read;while((read=await stream.ReadAsync(buffer,_stop.Token))>0)await stream.WriteAsync(buffer.AsMemory(0,read),_stop.Token);}catch(Exception e)when(e is IOException or OperationCanceledException){}
    }
    private async Task UdpLoop(UdpClient client,bool dns)
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var packet=await client.ReceiveAsync(_stop.Token);
                var data=packet.Buffer;
                if(dns&&data.Length>=12){data=data.Concat(new byte[]{0xc0,0x0c,0,1,0,1,0,0,0,60,0,4,203,0,113,8}).ToArray();data[2]=0x81;data[3]=0x80;data[7]=1;}
                await client.SendAsync(data,packet.RemoteEndPoint,_stop.Token);
            }
        }
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    public ValueTask DisposeAsync(){_stop.Cancel();_tcp.Stop();_udp.Dispose();_dns.Dispose();return ValueTask.CompletedTask;}
}

