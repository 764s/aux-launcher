using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AuxLauncher.Core;

namespace AuxLauncher.Guard;
internal sealed class OwnerResolver(Func<SessionJob?> job) : IDisposable
{
    private readonly ConcurrentDictionary<string,EndpointRecord> _endpoints=new();
    private readonly Dictionary<int,ProcessReference> _processes=new();
    private readonly object _processGate=new();
    public event Action<ulong,int,byte,string,int>? EndpointClosed;
    public void Observe(DivertAddress address)
    {
        var data=address.Metadata();var id=BinaryPrimitives.ReadUInt64LittleEndian(data);
        var pid=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        var local=WinDivertNative.FormatAddress(data.AsSpan(20,16));var remote=WinDivertNative.FormatAddress(data.AsSpan(36,16));
        var localPort=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(52));var remotePort=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(54));var protocol=data[56];
        if(protocol is not (6 or 17))return;
        if(address.Event==7)
        {
            foreach(var item in _endpoints.Where(x=>x.Value.Id==id&&x.Value.Pid==pid&&x.Value.Timestamp<=address.Timestamp).ToArray())_endpoints.TryRemove(item.Key,out _);
            EndpointClosed?.Invoke(id,pid,protocol,local,localPort);return;
        }
        var key=$"{address.Layer}:{id}:{remote}:{remotePort}";
        if(address.Event==2){_endpoints.TryRemove(key,out _);return;}
        long created=0;
        try {using var process=WindowsProcess.Open(pid);created=WindowsProcess.GetCreated(process);}catch(System.ComponentModel.Win32Exception){}
        var entry=new EndpointRecord(id,pid,created,protocol,local,localPort,remote,remotePort,address.Timestamp,address.Layer==2);
        _endpoints.AddOrUpdate(key,entry,(_,old)=>entry.Timestamp>=old.Timestamp?entry:old);
        if(_endpoints.Count>30000)
        {
            var cut=Stopwatch.GetTimestamp()-Stopwatch.Frequency*120;
            foreach(var item in _endpoints.Where(x=>x.Value.Timestamp<cut).Take(5000))_endpoints.TryRemove(item.Key,out _);
        }
    }
    private static bool Wildcard(string address)=>address is "0.0.0.0" or "::";
    public OwnershipEvidence? Resolve(PacketView packet,bool tableFallback=true)
    {
        if(job() is null)return new(0,0,false);
        var candidates=_endpoints.Values.Where(x=>x.Protocol==packet.Protocol&&x.LocalPort==packet.SourcePort&&(x.LocalAddress==packet.Source.ToString()||Wildcard(x.LocalAddress))&&(x.RemotePort==0||(x.RemotePort==packet.DestinationPort&&x.RemoteAddress==packet.Destination.ToString()))).ToArray();
        var exact=candidates.Where(x=>x.RemotePort!=0).ToArray();
        if(exact.Length>0)candidates=exact;
        var identities=new List<OwnershipEvidence>();
        foreach(var candidate in candidates)
        {
            var identity=ResolveProcess(candidate.Pid);
            if(identity is not null&&candidate.Created!=0&&candidate.Created==identity.Created)identities.Add(identity with {EndpointId=candidate.Id});
        }
        var owners=identities.DistinctBy(x=>x.ProcessKey).ToArray();
        if(owners.Length==1)return owners[0];
        if(owners.Length>1)return null;
        if(!tableFallback)return null;
        var pids=IpOwnerTable.Find(packet);
        return pids.Count==1?ResolveProcess(pids[0]):null;
    }
    public OwnershipEvidence? ResolveProcess(int pid)
    {
        if(pid<=0)return null;
        lock(_processGate)
        {
            if(_processes.TryGetValue(pid,out var cached))
            {
                if(!WindowsProcess.HasExited(cached.Handle))
                {
                    var managed=job()?.Contains(cached.Handle)??false;
                    return new(pid,cached.Identity.Created,managed);
                }
                cached.Handle.Dispose();_processes.Remove(pid);
            }
            try
            {
                var process=WindowsProcess.Open(pid);
                var identity=new ProcessIdentity(pid,WindowsProcess.GetCreated(process));
                var managed=job()?.Contains(process)??false;
                _processes[pid]=new(identity,process,managed);
                return new(pid,identity.Created,managed);
            }
            catch(System.ComponentModel.Win32Exception){return null;}
        }
    }
    public bool IsAlive(OwnershipEvidence owner)
    {
        var current=ResolveProcess(owner.Pid);
        return current is {Managed:true}&&current.Created==owner.Created;
    }
    public void SweepExited()
    {
        lock(_processGate)
        {
            foreach(var item in _processes.Where(x=>WindowsProcess.HasExited(x.Value.Handle)).ToArray()){item.Value.Handle.Dispose();_processes.Remove(item.Key);}
        }
    }
    public void Dispose(){lock(_processGate){foreach(var process in _processes.Values)process.Handle.Dispose();_processes.Clear();}}
}
