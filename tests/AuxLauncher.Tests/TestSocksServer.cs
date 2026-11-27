using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AuxLauncher.Core;

namespace AuxLauncher.Tests;
internal sealed class TestSocksServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly UdpClient _udp=new(new IPEndPoint(IPAddress.Loopback,0));
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentBag<TcpClient> _clients=new();
    private readonly bool _authentication;
    private readonly bool _udpSupported;
    private readonly int _dnsPort;
    private int _udpControls;
    private long _connects,_datagrams;
    public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
    public long ConnectCount=>Interlocked.Read(ref _connects);
    public long DatagramCount=>Interlocked.Read(ref _datagrams);
    public ProxyEndpoint Endpoint=>new("127.0.0.1",Port,_authentication?"test-user":null,_authentication?"test-secret":null);
    public TestSocksServer(int port=0,bool authentication=false,int dnsPort=0,bool udpSupported=true)
    {
        _authentication=authentication;_dnsPort=dnsPort;_udpSupported=udpSupported;_listener=new(IPAddress.Loopback,port);_listener.Start();_=AcceptAsync();_=UdpLoop();
    }
    private async Task AcceptAsync()
    {
        try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(client);_=HandleAsync(client);}}
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private async Task HandleAsync(TcpClient client)
    {
        using(client)try
        {
            var stream=client.GetStream();var head=new byte[2];await stream.ReadExactlyAsync(head,_stop.Token);
            var methods=new byte[head[1]];await stream.ReadExactlyAsync(methods,_stop.Token);
            var method=(byte)(_authentication?2:0);if(head[0]!=5||!methods.Contains(method)){await stream.WriteAsync(new byte[]{5,255},_stop.Token);return;}
            await stream.WriteAsync(new byte[]{5,method},_stop.Token);
            if(_authentication)
            {
                await stream.ReadExactlyAsync(head,_stop.Token);var user=new byte[head[1]];await stream.ReadExactlyAsync(user,_stop.Token);
                var length=new byte[1];await stream.ReadExactlyAsync(length,_stop.Token);var password=new byte[length[0]];await stream.ReadExactlyAsync(password,_stop.Token);
                var valid=Encoding.UTF8.GetString(user)=="test-user"&&Encoding.UTF8.GetString(password)=="test-secret";
                await stream.WriteAsync(new byte[]{1,(byte)(valid?0:1)},_stop.Token);if(!valid)return;
            }
            var command=new byte[4];await stream.ReadExactlyAsync(command,_stop.Token);
            var address=new byte[command[3]==1?4:16];await stream.ReadExactlyAsync(address,_stop.Token);
            var port=new byte[2];await stream.ReadExactlyAsync(port,_stop.Token);var targetPort=(port[0]<<8)|port[1];
            if(command[1]==3)
            {
                if(!_udpSupported){await stream.WriteAsync(new byte[]{5,7,0,1,0,0,0,0,0,0},_stop.Token);return;}
                var relayPort=((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
                Interlocked.Increment(ref _udpControls);
                try
                {
                    await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,(byte)(relayPort>>8),(byte)relayPort},_stop.Token);
                    await stream.ReadAsync(new byte[1],_stop.Token);
                }
                finally{Interlocked.Decrement(ref _udpControls);}
            }
            else if(command[1]==1)
            {
                using var upstream=new TcpClient();
                try{await upstream.ConnectAsync(IPAddress.Loopback,targetPort,_stop.Token);}
                catch(SocketException){await stream.WriteAsync(new byte[]{5,5,0,1,0,0,0,0,0,0},_stop.Token);return;}
                Interlocked.Increment(ref _connects);
                await stream.WriteAsync(new byte[]{5,0,0,1,127,0,0,1,0,1},_stop.Token);
                async Task Copy(Stream from,Stream to){try{await from.CopyToAsync(to,_stop.Token);}catch(Exception e)when(e is IOException or OperationCanceledException){}}
                await Task.WhenAny(Copy(stream,upstream.GetStream()),Copy(upstream.GetStream(),stream));
            }
            else await stream.WriteAsync(new byte[]{5,7,0,1,0,0,0,0,0,0},_stop.Token);
        }
        catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
    }
    private async Task UdpLoop()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var packet=await _udp.ReceiveAsync(_stop.Token);
                if(Volatile.Read(ref _udpControls)==0)continue;
                _=ForwardUdp(packet);
            }
        }
        catch(Exception e)when(e is OperationCanceledException or SocketException or ObjectDisposedException){}
    }
    private async Task ForwardUdp(UdpReceiveResult packet)
    {
        try
        {
            var decoded=Socks5Client.DecodeUdp(packet.Buffer);
            using var udp=new UdpClient();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);timeout.CancelAfter(2000);
            var port=decoded.Source.Port==53&&_dnsPort>0?_dnsPort:decoded.Source.Port;
            await udp.SendAsync(decoded.Payload,new IPEndPoint(IPAddress.Loopback,port),timeout.Token);
            var reply=await udp.ReceiveAsync(timeout.Token);Interlocked.Increment(ref _datagrams);
            await _udp.SendAsync(Socks5Client.EncodeUdp(decoded.Source.Address,decoded.Source.Port,reply.Buffer),packet.RemoteEndPoint,timeout.Token);
        }catch(Exception e)when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
    }
    public ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();_udp.Dispose();foreach(var client in _clients)client.Dispose();return ValueTask.CompletedTask;}
}

