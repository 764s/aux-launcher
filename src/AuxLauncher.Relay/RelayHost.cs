using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AuxLauncher.Core;

namespace AuxLauncher.Relay;
internal sealed class RelayHost : IDisposable
{
    private readonly CancellationTokenSource _stop=new();
    private readonly AppLog _log;
    private ProxyEndpoint? _proxy;
    private TcpListener? _listener4,_listener6;
    private RpcConnection? _guard;
    private RpcConnection? _core;
    private readonly CancellationTokenSource _processStop;
    private DateTime _lastOwner=DateTime.UtcNow;
    private readonly ConcurrentDictionary<string,PendingTcp> _pending=new();
    private readonly ConcurrentDictionary<string,(TcpClient Local,TcpClient Upstream)> _active=new();
    private readonly ConcurrentDictionary<string,Lazy<Task<UdpAssociation>>> _udp=new();
    private readonly SemaphoreSlim _udpBudget=new(256);
    public RelayHost(string logs,CancellationTokenSource processStop){_log=new(logs,"relay");_processStop=processStop;_ = SweepAsync();}

    public Func<string,JsonElement,Task<object?>> Control(RpcConnection rpc,uint pid,CancellationTokenSource processStop)=>async(method,data)=>{
        _core=rpc;
        switch(method)
        {
            case "configure":
                var config=data.Deserialize<RelayConfiguration>(RpcConnection.Json)!;config.Proxy.Validate();_proxy=config.Proxy;
                if(_listener4 is null)
                {
                    _listener4=new TcpListener(IPAddress.Any,0);_listener4.Start();
                    _listener6=new TcpListener(IPAddress.IPv6Any,0);_listener6.Server.DualMode=false;_listener6.Start();
                    _=AcceptAsync(_listener4);_=AcceptAsync(_listener6);
                }
                return new RelayReady(Environment.ProcessId,((IPEndPoint)_listener4.LocalEndpoint).Port,((IPEndPoint)_listener6!.LocalEndpoint).Port);
            case "probe": return await ProbeAsync();
            case "stop": _stop.Cancel();Dispose();_=Task.Run(async()=>{await Task.Delay(150);processStop.Cancel();});return true;
            default:throw new InvalidOperationException("未知转发控制命令。");
        }
    };
    public Func<string,JsonElement,Task<object?>> Guard(RpcConnection rpc,uint pid)
    {
        return async(method,data)=>{
            _guard=rpc;
            switch(method)
            {
                case "ready":
                    if(_proxy is null||_listener4 is null)throw new InvalidOperationException("转发组件未就绪。");
                    return new RelayReady(Environment.ProcessId,((IPEndPoint)_listener4.LocalEndpoint).Port,((IPEndPoint)_listener6!.LocalEndpoint).Port);
                case "probe":return await ProbeAsync();
                case "tcp.open":return await OpenTcpAsync(data.Deserialize<TcpOpenRequest>(RpcConnection.Json)!);
                case "tcp.close":CloseTcp(data.GetString()!);return true;
                case "udp.send":await SendUdpAsync(data.Deserialize<UdpSendRequest>(RpcConnection.Json)!);return true;
                case "udp.close":CloseUdp(data.GetString()!);return true;
                case "clear":Clear();return true;
                case "session.end":Dispose();_=Task.Run(async()=>{await Task.Delay(150);_processStop.Cancel();});return true;
                default:throw new InvalidOperationException("未知转发命令。");
            }
        };
    }
    private async Task<ProxyProbeResult> ProbeAsync()=>await Socks5Client.ProbeAsync(_proxy??throw new InvalidOperationException("代理未配置。"),_stop.Token,2500);
    private static string TcpKey(string address,int port)=>$"{IPAddress.Parse(address)}:{port}";
    private async Task<TcpOpenResult> OpenTcpAsync(TcpOpenRequest request)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var upstream=await Socks5Client.ConnectAsync(_proxy!,IPAddress.Parse(request.TargetAddress),request.TargetPort,timeout.Token);
            var key=TcpKey(request.TargetAddress,request.AliasPort);
            if(!_pending.TryAdd(key,new(request.Id,upstream,DateTime.UtcNow))){upstream.Dispose();return new(false,false,"中继连接标识重复。");}
            return new(true,false,null);
        }
        catch(SocksProxyException e){return new(false,e.ProxyUnavailable,e.Message);}
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException){return new(false,false,e is OperationCanceledException?"目标连接超时。":e.Message);}
    }
    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(_stop.Token);
                var peer=(IPEndPoint)client.Client.RemoteEndPoint!;
                if(!_pending.TryRemove(TcpKey(peer.Address.ToString(),peer.Port),out var pending)){client.Dispose();continue;}
                client.NoDelay=true;
                _active[pending.Id]=(client,pending.Upstream);
                _=RelayTcpAsync(pending.Id,client,pending.Upstream);
            }
        }
        catch(Exception e) when(e is OperationCanceledException or ObjectDisposedException or SocketException){if(!_stop.IsCancellationRequested)_log.Write("listener-error",new{e.Message});}
    }
    private async Task RelayTcpAsync(string id,TcpClient local,TcpClient upstream)
    {
        async Task CopyAsync(TcpClient from,TcpClient to)
        {
            try{await from.GetStream().CopyToAsync(to.GetStream(),65536,_stop.Token);try{to.Client.Shutdown(SocketShutdown.Send);}catch(SocketException){}}
            catch(Exception e) when(e is IOException or SocketException or OperationCanceledException or ObjectDisposedException){try{local.Close();upstream.Close();}catch{}}
        }
        try{await Task.WhenAll(CopyAsync(local,upstream),CopyAsync(upstream,local));}
        finally
        {
            _active.TryRemove(id,out _);local.Dispose();upstream.Dispose();
            if(_guard?.IsConnected==true)try{await _guard.NotifyAsync("tcp.closed",id);}catch(IOException){}
        }
    }
    private async Task SendUdpAsync(UdpSendRequest request)
    {
        if(!await _udpBudget.WaitAsync(0)){_log.Write("udp-congestion",new{request.Id},"udp-congestion");return;}
        try
        {
            var lazy=_udp.GetOrAdd(request.Id,id=>new(()=>CreateUdpAsync(id)));
            var association=await lazy.Value;
            if(!association.Alive){CloseUdp(request.Id);throw new IOException("UDP 中继关联已中断。");}
            await association.SendAsync(request,_stop.Token);
        }
        catch(Exception e) when(e is IOException or SocketException or OperationCanceledException)
        {
            CloseUdp(request.Id);
            var unavailable=e is not SocksProxyException socksError||socksError.ProxyUnavailable;
            if(_guard?.IsConnected==true)try{await _guard.NotifyAsync("udp.failed",new UdpFailure(request.Id,unavailable,e.Message));}catch(IOException){}
        }
        finally{_udpBudget.Release();}
    }
    private async Task<UdpAssociation> CreateUdpAsync(string id)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);timeout.CancelAfter(6000);
        var association=await UdpAssociation.CreateAsync(id,_proxy!,timeout.Token);
        association.Start(async reply=>{if(_guard?.IsConnected==true)await _guard.NotifyAsync("udp.reply",reply);},error=>{
            _log.Write("udp-closed",new{id,error},id);
            if(_guard?.IsConnected==true)_=_guard.NotifyAsync("udp.failed",new UdpFailure(id,true,error));
        });
        return association;
    }
    private void CloseTcp(string id)
    {
        foreach(var entry in _pending.Where(x=>x.Value.Id==id).ToArray())if(_pending.TryRemove(entry.Key,out var pending))pending.Upstream.Dispose();
        if(_active.TryRemove(id,out var pair)){pair.Local.Dispose();pair.Upstream.Dispose();}
    }
    private void CloseUdp(string id)
    {
        if(_udp.TryRemove(id,out var lazy)&&lazy.IsValueCreated)
            _=lazy.Value.ContinueWith(t=>{if(t.IsCompletedSuccessfully)t.Result.Dispose();},TaskScheduler.Default);
    }
    private async Task SweepAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                await Task.Delay(5000,_stop.Token);
                if(_core?.IsConnected==true||_guard?.IsConnected==true)_lastOwner=DateTime.UtcNow;
                else if(DateTime.UtcNow-_lastOwner>TimeSpan.FromSeconds(30)){Dispose();_processStop.Cancel();break;}
                foreach(var item in _pending.Where(x=>DateTime.UtcNow-x.Value.Created>TimeSpan.FromSeconds(30)).ToArray())CloseTcp(item.Value.Id);
                foreach(var item in _udp.ToArray())if(item.Value.IsValueCreated&&item.Value.Value.IsCompletedSuccessfully&&DateTime.UtcNow-item.Value.Value.Result.LastUse>TimeSpan.FromMinutes(2))CloseUdp(item.Key);
            }
        }catch(OperationCanceledException){}
    }
    private void Clear(){foreach(var item in _pending.Values.ToArray())CloseTcp(item.Id);foreach(var id in _active.Keys.ToArray())CloseTcp(id);foreach(var id in _udp.Keys.ToArray())CloseUdp(id);}
    public void Dispose(){_stop.Cancel();_listener4?.Stop();_listener6?.Stop();Clear();}
}
