using System.Text.Json;
using AuxLauncher.Core;

namespace AuxLauncher.Guard;
internal sealed class GuardHost : IAsyncDisposable
{
    private readonly string _ownerSid;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _commands=new(1);
    private readonly CancellationTokenSource _stop=new();
    private readonly List<RpcConnection> _clients=new();
    private GuardConfiguration? _config;
    private PacketEngine? _engine;
    private SessionJob? _job;
    private SuspendedProcess? _target;
    private BindTargetRequest? _binding;
    private volatile bool _committed,_protectionInterrupted,_recovering,_ended;
    private string? _error;
    private DateTime _lastCoreSeen=DateTime.UtcNow;
    private bool _disposing;
    private readonly bool _allowTestFault;
    private readonly CancellationTokenSource _processStop;
    public GuardHost(string ownerSid,string logs,CancellationTokenSource processStop,bool allowTestFault=false){_ownerSid=ownerSid;_log=new(logs,"guard");_processStop=processStop;_allowTestFault=allowTestFault;_=MonitorAsync();}

    public Func<string,JsonElement,Task<object?>> Control(RpcConnection rpc,uint clientPid,CancellationTokenSource processStop)
    {
        return async(method,data)=>{
            using(var peer=WindowsProcess.Open((int)clientPid))
                if(WindowsProcess.UserSid(peer)!=_ownerSid)throw new UnauthorizedAccessException("运行核心用户身份不匹配。");
            _lastCoreSeen=DateTime.UtcNow;
            lock(_clients){if(!_clients.Contains(rpc))_clients.Add(rpc);}
            if(method=="snapshot")return Snapshot();
            if(!await _commands.WaitAsync(0))throw new InvalidOperationException("组件正在处理会话命令。");
            try
            {
                switch(method)
                {
                    case "test.crash":
                        if(!_allowTestFault)throw new InvalidOperationException("当前运行不接受故障注入。");
                        _=Task.Run(async()=>{await Task.Delay(150);Environment.Exit(91);});return true;
                    case "initialize":
                        if(_engine is not null)return Snapshot();
                        _config=data.Deserialize<GuardConfiguration>(RpcConnection.Json)!;
                        if(_config.OwnerSid!=_ownerSid)throw new UnauthorizedAccessException("会话用户身份不匹配。");
                        if(_config.TestLoopbackTarget is not null&&(!_allowTestFault||_config.TestLoopbackTarget is not ("127.0.0.2" or "::1")))throw new InvalidOperationException("本机测试目标仅用于显式自测模式。");
                        _engine=NewEngine();_engine.Start();
                        try{await _engine.AttachRelayAsync(_config.RelayPipe,_config.Key);}
                        catch{await _engine.DisposeAsync();_engine=null;throw;}
                        _log.Write("capture-ready",new{_config.SessionId});
                        return Snapshot();
                    case "bind":
                        if(_engine is null||!_engine.CaptureReady)throw new InvalidOperationException("网络接管尚未就绪。");
                        if(_binding is not null)throw new InvalidOperationException("本组件已有运行会话。");
                        var binding=data.Deserialize<BindTargetRequest>(RpcConnection.Json)!;
                        if(binding.CorePid!=(int)clientPid)throw new UnauthorizedAccessException("句柄来源不匹配。");
                        SessionJob? job=null;SuspendedProcess? target=null;
                        try
                        {
                            job=new(WindowsProcess.DuplicateFrom(binding.CorePid,binding.JobHandle));
                            var process=WindowsProcess.DuplicateFrom(binding.CorePid,binding.ProcessHandle);
                            var thread=binding.ThreadHandle==0&&!binding.Resume?new SafeKernelHandle():WindowsProcess.DuplicateFrom(binding.CorePid,binding.ThreadHandle);
                            target=new(process,thread,new(binding.TargetPid,binding.Created));
                            if(WindowsProcess.GetCreated(process)!=binding.Created)throw new InvalidOperationException("进程创建身份已变化。");
                            if(!WindowsProcess.HasExited(process))
                            {
                                if(WindowsProcess.UserSid(process)!=_ownerSid||WindowsProcess.IsElevated(process))throw new UnauthorizedAccessException("目标必须由原普通用户启动。");
                                if(!job.Contains(process))throw new InvalidOperationException("目标不在本次 Job 中。");
                            }
                            else if(binding.Resume)throw new InvalidOperationException("暂停目标已退出。");
                            _job=job;_target=target;_binding=binding;_engine.Activate();
                            if(!binding.Resume){_committed=true;_protectionInterrupted=true;}
                            _log.Write("target-bound",new{binding.TargetPid,binding.Created,binding.Resume});
                            return Snapshot();
                        }
                        catch{target?.Dispose();job?.Dispose();throw;}
                    case "commit":
                        if(_committed)return Snapshot();
                        if(_target is null||_engine?.CaptureReady!=true||!_engine.ProxyHealthy)throw new InvalidOperationException("接管与代理尚未就绪。");
                        WindowsProcess.Resume(_target.Thread);_committed=true;
                        _log.Write("target-resumed",_target.Identity);await BroadcastAsync();return Snapshot();
                    case "retain":
                        if(_job is null||_target is null)throw new InvalidOperationException("没有可恢复会话。");
                        return new SharedTargetHandles(WindowsProcess.DuplicateTo((int)clientPid,_job.Handle),WindowsProcess.DuplicateTo((int)clientPid,_target.Process),_target.Thread.IsInvalid?0:WindowsProcess.DuplicateTo((int)clientPid,_target.Thread));
                    case "relay.changed":
                        if(_engine is null||_config is null)throw new InvalidOperationException("没有运行会话。");
                        await _engine.AttachRelayAsync(_config.RelayPipe,_config.Key);return Snapshot();
                    case "recover":
                        if(_config is null)throw new InvalidOperationException("没有会话。");
                        _protectionInterrupted=true;
                        if(_engine is not null)await _engine.DisposeAsync();
                        _engine=NewEngine();_engine.Start();await _engine.AttachRelayAsync(_config.RelayPipe,_config.Key);
                        if(_binding is not null)_engine.Activate();
                        _error=null;await BroadcastAsync();return Snapshot();
                    case "rollback":
                        if(_committed)throw new InvalidOperationException("运行中的目标不能执行启动回滚。");
                        if(_target is not null)WindowsProcess.Rollback(_target.Process);
                        await CleanupAsync();return true;
                    case "stop":
                        if(_committed&&_job?.Members().Count>0)return false;
                        if(!_committed&&_target is not null)WindowsProcess.Rollback(_target.Process);
                        await CleanupAsync();_=Task.Run(async()=>{await Task.Delay(150);processStop.Cancel();});return true;
                    default:throw new InvalidOperationException("未知接管命令。");
                }
            }
            finally{_commands.Release();}
        };
    }
    private PacketEngine NewEngine()
    {
        var engine=new PacketEngine(()=>_job,_log,_config?.TestLoopbackTarget);
        engine.CaptureFailed+=error=>{_protectionInterrupted=true;_error=error;_ = BroadcastAsync();};
        return engine;
    }
    private GuardSnapshot Snapshot()
    {
        int count=0;
        try{count=_job?.Members().Count??0;}catch(Exception e){_protectionInterrupted=true;_error=e.Message;}
        var state=_ended?LauncherState.Ready:_recovering?LauncherState.Recovering:!_committed?LauncherState.Preparing:_protectionInterrupted?LauncherState.ProtectionInterrupted:_engine?.ProxyHealthy==true?LauncherState.Running:LauncherState.ProxyLost;
        return new(_config?.SessionId??"",state,_binding?.TargetPath??"",_binding?.TargetPid??0,_binding?.Created??0,count,_protectionInterrupted,_engine?.CaptureReady==true,_engine?.TcpCount??0,_engine?.UdpCount??0,_engine?.UnknownCount??0,_error);
    }
    private async Task MonitorAsync()
    {
        var nextProbe=DateTime.MinValue;
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(400,_stop.Token);
                if(_engine is not null&&DateTime.UtcNow>=nextProbe){nextProbe=DateTime.UtcNow.AddSeconds(2);await _engine.ProbeAsync();}
                if(_committed&&!_ended&&_job is not null&&_job.Members().Count==0)
                {
                    if(await _commands.WaitAsync(0))
                    {
                        try{if(_job.Members().Count==0){_recovering=true;await BroadcastAsync();await CleanupAsync();}}
                        finally{_commands.Release();}
                    }
                }
                if(!_committed&&_target is not null&&DateTime.UtcNow-_lastCoreSeen>TimeSpan.FromSeconds(30))
                {
                    if(await _commands.WaitAsync(0))
                    {try{WindowsProcess.Rollback(_target.Process);await CleanupAsync();}finally{_commands.Release();}}
                }
                await BroadcastAsync();
                if((_ended||_binding is null)&&DateTime.UtcNow-_lastCoreSeen>TimeSpan.FromSeconds(30))
                {
                    bool connected;lock(_clients){connected=_clients.Any(x=>x.IsConnected);}
                    if(!connected){await CleanupAsync();_processStop.Cancel();break;}
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){break;}
            catch(Exception e)
            {
                // 一次查询或回收失败不能终止整个会话的后续监测。
                _error=e.Message;_protectionInterrupted=true;_log.Write("monitor-error",new{e.Message},"monitor-error");
                try{await BroadcastAsync();}catch(IOException){}
            }
        }
    }
    private async Task BroadcastAsync()
    {
        RpcConnection[] clients;lock(_clients){_clients.RemoveAll(x=>!x.IsConnected);clients=_clients.ToArray();}
        var snapshot=Snapshot();
        foreach(var rpc in clients)try{await rpc.NotifyAsync("state",snapshot);}catch(Exception e)when(e is IOException or OperationCanceledException){}
    }
    private async Task CleanupAsync()
    {
        if(_ended)return;
        if(_engine is not null)_log.Write("traffic-summary",new{_engine.TcpCount,_engine.UdpCount,_engine.UnknownCount});
        if(_engine is not null){await _engine.DisposeSessionAsync();_engine=null;}
        _target?.Dispose();_target=null;_job?.Dispose();_job=null;_ended=true;_recovering=false;
        _log.Write("session-cleaned");await BroadcastAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if(_disposing)return;_disposing=true;_stop.Cancel();
        if(_engine is not null)await _engine.DisposeAsync();
        // 不使用 KILL_ON_JOB_CLOSE，异常退出也不强杀已经运行的目标。
        _target?.Dispose();_job?.Dispose();
    }
}
