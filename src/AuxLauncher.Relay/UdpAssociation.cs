using System.Net;
using System.Net.Sockets;
using AuxLauncher.Core;

namespace AuxLauncher.Relay;
internal sealed class UdpAssociation : IDisposable
{
    private readonly TcpClient _control;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop=new();
    public string Id {get;}
    public bool Alive=>!_stop.IsCancellationRequested;
    public DateTime LastUse {get;private set;}=DateTime.UtcNow;
    private UdpAssociation(string id,TcpClient control,IPEndPoint relay)
    {
        Id=id;_control=control;_udp=new UdpClient(relay.AddressFamily);_udp.Connect(relay);
    }
    public static async Task<UdpAssociation> CreateAsync(string id,ProxyEndpoint proxy,CancellationToken token)
    {
        var (control,relay)=await Socks5Client.AssociateAsync(proxy,token);
        return new(id,control,relay);
    }
    public async Task SendAsync(UdpSendRequest request,CancellationToken token)
    {
        if(!Alive)throw new IOException("UDP 中继关联已结束。");
        var payload=Socks5Client.EncodeUdp(IPAddress.Parse(request.TargetAddress),request.TargetPort,request.Payload);
        await _udp.SendAsync(payload,token);LastUse=DateTime.UtcNow;
    }
    public void Start(Func<UdpReply,Task> onReply,Action<string> onFailure)
    {
        _=Task.Run(async()=>{
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var received=await _udp.ReceiveAsync(_stop.Token);
                    var reply=Socks5Client.DecodeUdp(received.Buffer);
                    LastUse=DateTime.UtcNow;
                    await onReply(new(Id,reply.Source.Address.ToString(),reply.Source.Port,reply.Payload));
                }
            }
            catch(Exception e) when(e is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {if(!_stop.IsCancellationRequested)onFailure(e.Message);}
            finally{_stop.Cancel();}
        });
        _=Task.Run(async()=>{
            try {var b=new byte[1];await _control.GetStream().ReadAsync(b,_stop.Token);}
            catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException){}
            finally{if(!_stop.IsCancellationRequested){onFailure("UDP 代理控制连接中断。");_stop.Cancel();}}
        });
    }
    public void Dispose(){_stop.Cancel();_udp.Dispose();_control.Dispose();}
}
