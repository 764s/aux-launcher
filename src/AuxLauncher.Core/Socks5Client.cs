using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AuxLauncher.Core;

public static class Socks5Client
{
    public static async Task<TcpClient> ConnectAsync(ProxyEndpoint proxy, IPAddress target, int port, CancellationToken token)
    {
        var (client,_) = await CommandAsync(proxy, 1, target, port, token);
        return client;
    }
    public static async Task<(TcpClient Control, IPEndPoint Relay)> AssociateAsync(ProxyEndpoint proxy, CancellationToken token)
    {
        var (client, endpoint) = await CommandAsync(proxy, 3, IPAddress.Any, 0, token);
        if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
            endpoint = new IPEndPoint(((IPEndPoint)client.Client.RemoteEndPoint!).Address, endpoint.Port);
        if (endpoint.Port == 0) { client.Dispose(); throw new SocksProxyException("代理返回了无效的 UDP 中继端口。", false, 7); }
        return (client,endpoint);
    }
    public static async Task<ProxyProbeResult> ProbeAsync(ProxyEndpoint proxy, CancellationToken token = default, int timeoutMs = 1500)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        try
        {
            proxy.Validate();
            using var client=await AuthenticateAsync(proxy,timeout.Token);
            await WriteRequestAsync(client.GetStream(),3,IPAddress.Any,0,timeout.Token);
            try
            {
                var relay=await ReadReplyAsync(client.GetStream(),timeout.Token);
                return new(proxy,true,false,relay.Port>0,null);
            }
            catch(SocksProxyException e) when(!e.ProxyUnavailable) { return new(proxy,true,false,false,e.Message); }
        }
        catch(ProxyAuthenticationRequiredException) { return new(proxy,false,true,false,"入口需要账号密码。"); }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or ArgumentException)
        { return new(proxy,false,false,false,e is OperationCanceledException?"连接超时。":e.Message); }
    }
    private static async Task<(TcpClient,IPEndPoint)> CommandAsync(ProxyEndpoint proxy,byte command,IPAddress target,int port,CancellationToken token)
    {
        var client=await AuthenticateAsync(proxy,token);
        try
        {
            await WriteRequestAsync(client.GetStream(),command,target,port,token);
            return (client,await ReadReplyAsync(client.GetStream(),token));
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task<TcpClient> AuthenticateAsync(ProxyEndpoint proxy,CancellationToken token)
    {
        var client=new TcpClient();
        try
        {
            try { await client.ConnectAsync(proxy.Host,proxy.Port,token); }
            catch(Exception e) when(e is SocketException or IOException) { throw new SocksProxyException("无法连接本地代理入口。",true); }
            client.NoDelay=true;
            var stream=client.GetStream();
            await stream.WriteAsync(new byte[]{5,2,0,2},token);
            var choice=new byte[2]; await stream.ReadExactlyAsync(choice,token);
            if(choice[0]!=5) throw new SocksProxyException("该端口不是 SOCKS5 入口。",true);
            if(choice[1]==2)
            {
                if(string.IsNullOrEmpty(proxy.Username)) throw new ProxyAuthenticationRequiredException();
                var user=Encoding.UTF8.GetBytes(proxy.Username);
                var pass=Encoding.UTF8.GetBytes(proxy.Password??"");
                if(user.Length>255||pass.Length>255)throw new SocksProxyException("认证信息过长。",true);
                var auth=new byte[3+user.Length+pass.Length]; auth[0]=1;auth[1]=(byte)user.Length;
                user.CopyTo(auth,2);auth[2+user.Length]=(byte)pass.Length;pass.CopyTo(auth,3+user.Length);
                await stream.WriteAsync(auth,token); await stream.ReadExactlyAsync(choice,token);
                if(choice[0]!=1||choice[1]!=0)throw new SocksProxyException("代理账号或密码不正确。",true);
            }
            else if(choice[1]!=0) throw new SocksProxyException("本地代理不支持可用的认证方式。",true);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static Task WriteRequestAsync(NetworkStream stream,byte command,IPAddress target,int port,CancellationToken token)
    {
        var address=target.GetAddressBytes();
        var request=new byte[6+address.Length];request[0]=5;request[1]=command;request[3]=(byte)(address.Length==4?1:4);
        address.CopyTo(request,4);request[^2]=(byte)(port>>8);request[^1]=(byte)port;
        return stream.WriteAsync(request,token).AsTask();
    }
    private static async Task<IPEndPoint> ReadReplyAsync(NetworkStream stream,CancellationToken token)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,token);
        if(header[0]!=5||header[2]!=0)throw new SocksProxyException("本地代理返回了无效响应。",true);
        if(header[1]!=0)throw new SocksProxyException($"代理无法建立此连接（响应 {header[1]}）。",false,header[1]);
        byte[] address;
        if(header[3] is 1 or 4)
        {
            address=new byte[header[3]==1?4:16];await stream.ReadExactlyAsync(address,token);
        }
        else if(header[3]==3)
        {
            var length=new byte[1];await stream.ReadExactlyAsync(length,token);
            var name=new byte[length[0]];await stream.ReadExactlyAsync(name,token);
            if(!IPAddress.TryParse(Encoding.ASCII.GetString(name),out var ip))throw new SocksProxyException("代理返回的中继域名无法作为本地中继地址使用。",true);
            address=ip.GetAddressBytes();
        }
        else throw new SocksProxyException("代理返回了不支持的地址格式。",true);
        var port=new byte[2];await stream.ReadExactlyAsync(port,token);
        return new(new IPAddress(address),(port[0]<<8)|port[1]);
    }

    public static byte[] EncodeUdp(IPAddress destination,int port,ReadOnlySpan<byte> payload)
    {
        var address=destination.GetAddressBytes();var data=new byte[6+address.Length+payload.Length];
        data[3]=(byte)(address.Length==4?1:4);address.CopyTo(data,4);
        data[4+address.Length]=(byte)(port>>8);data[5+address.Length]=(byte)port;
        payload.CopyTo(data.AsSpan(6+address.Length));return data;
    }
    public static (IPEndPoint Source,byte[] Payload) DecodeUdp(ReadOnlySpan<byte> packet)
    {
        if(packet.Length<10||packet[0]!=0||packet[1]!=0||packet[2]!=0)throw new InvalidDataException("无效或分片的 SOCKS5 UDP 数据报。");
        var size=packet[3]==1?4:packet[3]==4?16:throw new InvalidDataException("UDP 中继地址类型不支持。");
        if(packet.Length<6+size)throw new InvalidDataException("UDP 中继报文不完整。");
        var source=new IPEndPoint(new IPAddress(packet.Slice(4,size)),(packet[4+size]<<8)|packet[5+size]);
        return (source,packet[(6+size)..].ToArray());
    }
}
