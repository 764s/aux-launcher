using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AuxLauncher.Core;

namespace AuxLauncher.Guard;
internal sealed class NativeCapture : IAsyncDisposable
{
    private readonly WinDivertHandle _network,_socket,_flow;
    private readonly Channel<CapturedPacket> _packets=Channel.CreateBounded<CapturedPacket>(new BoundedChannelOptions(4096){SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly List<Task> _tasks=new();
    private readonly AppLog _log;
    private volatile bool _stopping;
    private bool _closed;
    public bool Healthy {get;private set;}=true;
    public event Action<string>? Fault;
    public NativeCapture(AppLog log)
    {
        _log=log;
        _socket=WinDivertNative.OpenChecked("true",3,0,WinDivertNative.Sniff|WinDivertNative.RecvOnly);
        try {_flow=WinDivertNative.OpenChecked("true",2,0,WinDivertNative.Sniff|WinDivertNative.RecvOnly);}
        catch{_socket.Dispose();throw;}
        try {_network=WinDivertNative.OpenChecked("outbound and (tcp or udp or icmp or icmpv6)",0,100,0);}
        catch{_socket.Dispose();_flow.Dispose();throw;}
        WinDivertNative.SetParam(_network,0,8192);
        WinDivertNative.SetParam(_network,1,2000);
        WinDivertNative.SetParam(_network,2,8*1024*1024);
    }
    public void Run(OwnerResolver owners,Func<CapturedPacket,Task> handle,Action<CapturedPacket> overload)
    {
        _tasks.Add(Task.Factory.StartNew(()=>MetadataLoop(_socket,owners),TaskCreationOptions.LongRunning));
        _tasks.Add(Task.Factory.StartNew(()=>MetadataLoop(_flow,owners),TaskCreationOptions.LongRunning));
        _tasks.Add(Task.Factory.StartNew(()=>NetworkLoop(overload),TaskCreationOptions.LongRunning));
        for(var i=0;i<4;i++)_tasks.Add(Task.Run(async()=>{
            await foreach(var packet in _packets.Reader.ReadAllAsync())
            {
                try{await handle(packet);}
                catch(Exception e){_log.Write("packet-handler-error",new{e.Message},"packet-handler");Fail(e.Message);}
            }
        }));
    }
    private void MetadataLoop(WinDivertHandle handle,OwnerResolver owners)
    {
        try
        {
            while(true)
            {
                var address=new DivertAddress();
                if(!WinDivertNative.Receive(handle,null,0,out _,ref address))
                {
                    var error=Marshal.GetLastWin32Error();if(_stopping||error==232)break;
                    throw new Win32Exception(error);
                }
                owners.Observe(address);
            }
        }
        catch(Exception e){if(!_stopping)Fail(e.Message);}
    }
    private void NetworkLoop(Action<CapturedPacket> overload)
    {
        try
        {
            var buffer=new byte[65575];
            while(true)
            {
                var address=new DivertAddress();
                if(!WinDivertNative.Receive(_network,buffer,(uint)buffer.Length,out var length,ref address))
                {
                    var error=Marshal.GetLastWin32Error();if(_stopping||error==232)break;
                    throw new Win32Exception(error);
                }
                var bytes=buffer.AsSpan(0,(int)length).ToArray();
                var packet=PacketView.Parse(bytes);
                if(packet is null){Send(bytes,address);continue;}
                var captured=new CapturedPacket(packet,address);
                if(!_packets.Writer.TryWrite(captured))overload(captured);
            }
        }
        catch(Exception e){if(!_stopping)Fail(e.Message);}
        finally{_packets.Writer.TryComplete();}
    }
    public void Send(byte[] bytes,DivertAddress address)
    {
        if(_network.IsClosed)return;
        // 网卡卸载可能让捕获内容带有尚未最终填写的校验和。
        WinDivertNative.CalculateChecksums(bytes,(uint)bytes.Length,ref address,0);
        if(!WinDivertNative.Send(_network,bytes,(uint)bytes.Length,out _,ref address))
        {
            var error=Marshal.GetLastWin32Error();
            if(!_stopping)
            {
                if(error is 6 or 5 or 995 or 1167)Fail("数据包重注入失败："+error);
                else _log.Write("packet-send-failed",new{error},"send-error:"+error);
            }
        }
    }
    private void Fail(string error){if(!Healthy)return;Healthy=false;_log.Write("capture-failed",new{error});Fault?.Invoke(error);}
    public async ValueTask DisposeAsync()
    {
        if(_closed)return;
        if(!_stopping){_stopping=true;WinDivertNative.Shutdown(_network,WinDivertNative.ShutdownReceive);WinDivertNative.Shutdown(_socket,WinDivertNative.ShutdownReceive);WinDivertNative.Shutdown(_flow,WinDivertNative.ShutdownReceive);}
        await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(15));
        _network.Dispose();_socket.Dispose();_flow.Dispose();_closed=true;
    }
}
