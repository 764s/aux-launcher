using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AuxLauncher.Core;

namespace AuxLauncher.Guard;
internal sealed class PacketEngine : IAsyncDisposable
{
    private readonly OwnerResolver _owners;
    private readonly AppLog _log;
    private readonly ConcurrentDictionary<FlowKey,TcpRoute> _tcp=new();
    private readonly ConcurrentDictionary<FlowKey,TcpRoute> _reverse=new();
    private readonly ConcurrentDictionary<int,byte> _aliases=new();
    private readonly ConcurrentDictionary<string,UdpRoute> _udp=new();
    private readonly ConcurrentDictionary<FlowKey,DateTime> _udpPeers=new();
    private readonly ConcurrentDictionary<FlowKey,BypassRoute> _bypass=new();
    private readonly SemaphoreSlim[] _locks=Enumerable.Range(0,128).Select(_=>new SemaphoreSlim(1)).ToArray();
    private readonly ConcurrentDictionary<string,(bool Drop,DateTime Seen)> _fragments=new();
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _opening=new(128);
    private NativeCapture? _capture;
    private RpcConnection? _relay;
    private RelayReady? _relayInfo;
    private int _alias=19000;
    private volatile bool _active,_proxyHealthy=true,_stopping;
    private bool _disposed;
    private readonly string? _testLoopbackTarget;
    private long _tcpCount,_udpCount,_unknown;
    public bool CaptureReady=>_capture?.Healthy==true&&!_stopping;
    public bool ProxyHealthy=>_proxyHealthy&&_relay?.IsConnected==true;
    public long TcpCount=>Interlocked.Read(ref _tcpCount);
    public long UdpCount=>Interlocked.Read(ref _udpCount);
    public long UnknownCount=>Interlocked.Read(ref _unknown);
    public event Action<string>? CaptureFailed;
    public PacketEngine(Func<SessionJob?> job,AppLog log,string? testLoopbackTarget=null)
    {
        _log=log;_owners=new(job);_testLoopbackTarget=testLoopbackTarget;
        _owners.EndpointClosed+=(id,pid,protocol,address,port)=>
        {
            if(protocol!=17)return;
            foreach(var item in _udp.Where(x=>x.Value.Owner.Pid==pid&&x.Value.Last.Packet.SourcePort==port&&(x.Value.Owner.EndpointId==id||x.Value.Owner.EndpointId==0)).ToArray())
                if(_udp.TryRemove(item.Key,out _)&&_relay?.IsConnected==true)_=CloseUdpRelayAsync(item.Key);
        };
    }
    private async Task CloseUdpRelayAsync(string id){try{if(_relay?.IsConnected==true)await _relay.NotifyAsync("udp.close",id);}catch(Exception e)when(e is IOException or OperationCanceledException){}}
    public void Start()
    {
        _capture=new NativeCapture(_log);
        _capture.Fault+=error=>CaptureFailed?.Invoke(error);
        _capture.Run(_owners,HandleAsync,HandleOverload);
        _=SweepAsync();
    }
    public async Task AttachRelayAsync(string pipe,string key)
    {
        if(_relay is not null)await _relay.DisposeAsync();
        _relay=await LocalPipe.ConnectAsync(pipe,key,_stop.Token);
        _relay.Handler=OnRelayEventAsync;
        _relay.Disconnected+=_=>_proxyHealthy=false;
        _relayInfo=await _relay.CallAsync<RelayReady>("ready",null,_stop.Token);
        await _relay.CallAsync<bool>("clear",null,_stop.Token);
        foreach(var route in _tcp.Values){route.State=2;route.ClosedAt=DateTime.UtcNow;}
        _udp.Clear();await ProbeAsync();
    }
    public void Activate()=>_active=true;
    public async Task<bool> ProbeAsync()
    {
        try{if(_relay is null)return _proxyHealthy=false;var result=await _relay.CallAsync<ProxyProbeResult>("probe",null,_stop.Token,5);return _proxyHealthy=result.Available&&result.UdpAvailable;}
        catch(Exception e) when(e is IOException or OperationCanceledException or ObjectDisposedException){return _proxyHealthy=false;}
    }
    private Task<object?> OnRelayEventAsync(string method,JsonElement data)
    {
        if(method=="tcp.closed")
        {
            var id=data.GetString();
            foreach(var route in _tcp.Values.Where(x=>x.Id==id)){route.ClosedAt=DateTime.UtcNow;route.LastUse=DateTime.UtcNow;}
        }
        else if(method=="udp.failed"){var failure=data.Deserialize<UdpFailure>(RpcConnection.Json)!;if(failure.ProxyUnavailable)_proxyHealthy=false;_log.Write("udp-error",new{failure.Id,failure.Error},failure.Id);}
        else if(method=="udp.reply")
        {
            var reply=data.Deserialize<UdpReply>(RpcConnection.Json)!;
            if(_udp.TryGetValue(reply.Id,out var route)&&!_stopping&&_owners.IsAlive(route.Owner))
            {
                var last=route.Last;var source=IPAddress.Parse(reply.SourceAddress);
                var currentOwners=IpOwnerTable.Find(last.Packet);
                if(currentOwners.Count!=1||currentOwners[0]!=route.Owner.Pid)return Task.FromResult<object?>(true);
                if(source.AddressFamily==last.Packet.Source.AddressFamily)
                {
                    var bytes=PacketCodec.Udp(source,reply.SourcePort,last.Packet.Source,last.Packet.SourcePort,reply.Payload);
                    var address=last.Address;address.Outbound=last.Address.Loopback;address.MarkChecksums();_capture?.Send(bytes,address);
                }
            }
        }
        return Task.FromResult<object?>(true);
    }
    private bool IsRelayReturn(PacketView p)=>_relayInfo is not null&&p.Protocol==6&&p.SourcePort==(p.IPv6?_relayInfo.Tcp6Port:_relayInfo.Tcp4Port);
    private void Direct(CapturedPacket p)=>_capture?.Send(p.Packet.Data,p.Address);
    private void Unknown(CapturedPacket p,string reason)
    {
        Interlocked.Increment(ref _unknown);
        if(p.Packet.Protocol==6)_bypass[p.Packet.Key]=new(p.Packet.Sequence,reason);
        _log.Write("unknown-bypass",new{flow=p.Packet.Key,reason},p.Packet.Key.ToString());
        Direct(p);
    }
    private void HandleOverload(CapturedPacket p)
    {
        if(IsRelayReturn(p.Packet))return;
        if(p.Address.Loopback||!_active){Direct(p);return;}
        if(_tcp.ContainsKey(p.Packet.Key)||_owners.Resolve(p.Packet,false)?.Managed==true){_log.Write("owned-congestion",null,"owned-congestion");return;}
        Unknown(p,"QueueFull");
    }
    private async Task HandleAsync(CapturedPacket captured)
    {
        var p=captured.Packet;
        if(_stopping)
        {
            if(_tcp.ContainsKey(p.Key)||_reverse.ContainsKey(p.Key)||_udpPeers.ContainsKey(p.Key)||_udpPeers.ContainsKey(p.Key.Reverse())||IsRelayReturn(p))return;
            Direct(captured);return;
        }
        if(_reverse.TryGetValue(p.Key,out var reverse))
        {
            var original=reverse.First.Packet;
            var bytes=PacketCodec.Rewrite(p,original.Destination,original.DestinationPort,original.Source,original.SourcePort);
            var address=captured.Address;address.Outbound=captured.Address.Loopback;address.MarkChecksums();_capture?.Send(bytes,address);
            reverse.LastUse=DateTime.UtcNow;return;
        }
        // 中继返回只能经逆映射发送，不能落入未知包直通分支。
        if(IsRelayReturn(p))return;
        if(!_active||((captured.Address.Loopback||IPAddress.IsLoopback(p.Destination))&&p.Destination.ToString()!=_testLoopbackTarget)){Direct(captured);return;}
        var destination=p.Destination.GetAddressBytes();
        if(p.Destination.IsIPv6Multicast||p.Destination.Equals(IPAddress.Broadcast)||(!p.IPv6&&destination[0] is >=224 and <=239)){Direct(captured);return;}
        if(p.Protocol is 1 or 58){HandleIcmp(captured);return;}
        if(p.Protocol is not (6 or 17)){Direct(captured);return;}
        if(p.Fragment&&!p.FirstFragment)
        {
            if(_fragments.TryGetValue(p.FragmentKey,out var decision)){if(!decision.Drop)Direct(captured);}else Unknown(captured,"UnattributedFragment");
            return;
        }
        if(_tcp.TryGetValue(p.Key,out var known))
        {
            if(p.Syn&&!p.Ack&&p.Sequence!=known.First.Packet.Sequence){RemoveTcp(p.Key,known);}
            else{ForwardTcp(captured,known);return;}
        }
        if(_bypass.TryGetValue(p.Key,out var bypass))
        {
            if(p.Protocol==6&&p.Syn&&!p.Ack&&p.Sequence!=bypass.Sequence)_bypass.TryRemove(p.Key,out _);
            else{bypass.LastUse=DateTime.UtcNow;Direct(captured);if(p.Protocol==6&&(p.TcpFlags&4)!=0)_bypass.TryRemove(p.Key,out _);return;}
        }
        var gate=_locks[(p.Key.GetHashCode()&int.MaxValue)%_locks.Length];
        await gate.WaitAsync();
        try
        {
            if(_tcp.TryGetValue(p.Key,out known)){ForwardTcp(captured,known);return;}
            if(_bypass.ContainsKey(p.Key)){Direct(captured);return;}
            OwnershipEvidence? owner=null;
            var deadline=Stopwatch.GetTimestamp()+Stopwatch.Frequency/20;
            do
            {
                owner=_owners.Resolve(p);
                if(owner is not null)break;
                await Task.Delay(5);
            }while(Stopwatch.GetTimestamp()<deadline&&!_stopping);
            if(owner is null){Unknown(captured,"AttributionTimeout");return;}
            if(!owner.Managed){if(p.Protocol==6)_bypass[p.Key]=new(p.Sequence,"NonMember");Direct(captured);return;}
            if(p.Fragment){_fragments[p.FragmentKey]=(true,DateTime.UtcNow);_log.Write("owned-fragment-not-supported",new{flow=p.Key},p.FragmentKey);return;}
            if(_stopping)return;
            if(p.Protocol==6)
            {
                if(!p.Syn||p.Ack){Unknown(captured,"ExistingConnection");return;}
                if(!ProxyHealthy){Reset(captured);return;}
                var alias=AllocateAlias();
                if(alias==0){Reset(captured);_log.Write("relay-capacity-exhausted");return;}
                var route=new TcpRoute(Guid.NewGuid().ToString("N"),captured,owner,alias,p.IPv6?_relayInfo!.Tcp6Port:_relayInfo!.Tcp4Port);
                _tcp[p.Key]=route;_reverse[route.ReverseKey]=route;
                _=OpenTcpAsync(route);
            }
            else
            {
                if(!ProxyHealthy)return;
                var id=$"{owner.Pid}-{owner.Created}-{owner.EndpointId}-{p.Source}-{p.SourcePort}";
                var route=_udp.GetOrAdd(id,_=>new(id,captured,owner));route.Last=captured;route.LastUse=DateTime.UtcNow;
                _udpPeers[p.Key]=DateTime.UtcNow;
                try{await _relay!.NotifyAsync("udp.send",new UdpSendRequest(id,p.Destination.ToString(),p.DestinationPort,p.UdpPayload));Interlocked.Increment(ref _udpCount);}
                catch(IOException){_proxyHealthy=false;}
            }
        }
        finally{gate.Release();}
    }
    private async Task OpenTcpAsync(TcpRoute route)
    {
        if(!await _opening.WaitAsync(0)){route.State=2;Reset(route.First);return;}
        try
        {
            var result=await _relay!.CallAsync<TcpOpenResult>("tcp.open",new TcpOpenRequest(route.Id,route.First.Packet.Destination.ToString(),route.First.Packet.DestinationPort,route.AliasPort),_stop.Token,15);
            if(!result.Success){if(result.ProxyUnavailable)_proxyHealthy=false;route.State=2;route.ClosedAt=DateTime.UtcNow;Reset(route.First);_log.Write("tcp-open-failed",new{flow=route.First.Packet.Key,result.Error});return;}
            if(_stopping||!_owners.IsAlive(route.Owner)||!_tcp.TryGetValue(route.First.Packet.Key,out var current)||current!=route){await _relay.NotifyAsync("tcp.close",route.Id);return;}
            route.State=1;Interlocked.Increment(ref _tcpCount);ForwardTcp(route.First,route);
        }
        catch(Exception e) when(e is IOException or OperationCanceledException){_proxyHealthy=false;route.State=2;route.ClosedAt=DateTime.UtcNow;if(!_stopping)Reset(route.First);}
        finally{_opening.Release();}
    }
    private void ForwardTcp(CapturedPacket captured,TcpRoute route)
    {
        route.LastUse=DateTime.UtcNow;
        if(route.State==0)return;
        if(route.State==2||_stopping){if((captured.Packet.TcpFlags&4)==0)Reset(captured);return;}
        var p=captured.Packet;
        var bytes=PacketCodec.Rewrite(p,p.Destination,route.AliasPort,p.Source,route.ListenerPort);
        var address=captured.Address;address.Outbound=captured.Address.Loopback;address.MarkChecksums();_capture?.Send(bytes,address);
        if((p.TcpFlags&4)!=0)route.ClosedAt=DateTime.UtcNow;
    }
    private void Reset(CapturedPacket captured)
    {
        if(captured.Packet.Protocol!=6||captured.Packet.Fragment)return;
        var bytes=PacketCodec.ResetFor(captured.Packet);var address=captured.Address;address.Outbound=captured.Address.Loopback;address.MarkChecksums();_capture?.Send(bytes,address);
    }
    private void HandleIcmp(CapturedPacket captured)
    {
        var p=captured.Packet;
        if(p.Length>p.TransportOffset+8)
        {
            var type=p.Data[p.TransportOffset];
            var error=p.Protocol==1?type is 3 or 11 or 12:type is >=1 and <=4;
            if(error)
            {
                var quote=PacketView.Parse(p.Data.AsSpan(p.TransportOffset+8,p.Length-p.TransportOffset-8).ToArray(),true);
                if(quote is not null&&_udpPeers.TryGetValue(quote.Key.Reverse(),out var time)&&DateTime.UtcNow-time<TimeSpan.FromMinutes(2))return;
            }
        }
        Direct(captured);
    }
    private int AllocateAlias()
    {
        for(var attempt=0;attempt<46000;attempt++)
        {
            var port=19000+(int)((uint)Interlocked.Increment(ref _alias)%46000);
            if(_aliases.TryAdd(port,0))return port;
        }
        return 0;
    }
    private void RemoveTcp(FlowKey key,TcpRoute route){_tcp.TryRemove(key,out _);_reverse.TryRemove(route.ReverseKey,out _);_aliases.TryRemove(route.AliasPort,out _);if(_relay?.IsConnected==true)_=_relay.NotifyAsync("tcp.close",route.Id);}
    private async Task SweepAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                await Task.Delay(3000,_stop.Token);
                _owners.SweepExited();
                foreach(var item in _tcp.ToArray())if(item.Value.ClosedAt is DateTime closed&&DateTime.UtcNow-closed>TimeSpan.FromSeconds(30))RemoveTcp(item.Key,item.Value);
                foreach(var item in _bypass.ToArray())if(DateTime.UtcNow-item.Value.LastUse>TimeSpan.FromMinutes(item.Key.Protocol==6?5:1))_bypass.TryRemove(item.Key,out _);
                foreach(var item in _fragments.ToArray())if(DateTime.UtcNow-item.Value.Seen>TimeSpan.FromSeconds(30))_fragments.TryRemove(item.Key,out _);
                foreach(var item in _udpPeers.ToArray())if(DateTime.UtcNow-item.Value>TimeSpan.FromMinutes(2))_udpPeers.TryRemove(item.Key,out _);
                foreach(var item in _udp.ToArray())if(!_owners.IsAlive(item.Value.Owner)||DateTime.UtcNow-item.Value.LastUse>TimeSpan.FromMinutes(2))
                {if(_udp.TryRemove(item.Key,out _)&&_relay?.IsConnected==true)await _relay.NotifyAsync("udp.close",item.Key);}
            }
        }catch(Exception e) when(e is OperationCanceledException or IOException){}
    }
    public ValueTask DisposeAsync()=>DisposeCoreAsync(false);
    public ValueTask DisposeSessionAsync()=>DisposeCoreAsync(true);
    private async ValueTask DisposeCoreAsync(bool endSession)
    {
        if(_disposed)return;_stopping=true;_stop.Cancel();
        if(_relay?.IsConnected==true)try{await _relay.CallAsync<bool>("clear",null,default,5);}catch(Exception e) when(e is IOException or OperationCanceledException){}
        if(_capture is not null)await _capture.DisposeAsync();
        if(endSession&&_relay?.IsConnected==true)try{await _relay.CallAsync<bool>("session.end",null,default,5);}catch(Exception e)when(e is IOException or OperationCanceledException){}
        if(_relay is not null)await _relay.DisposeAsync();
        _owners.Dispose();_disposed=true;
    }
}
