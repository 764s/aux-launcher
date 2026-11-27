using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AuxLauncher.Core;

namespace AuxLauncher.App;
internal sealed class LauncherController : IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly RuntimeStore _runtime;
    private readonly AppLog _log;
    private readonly string _directory;
    private readonly SemaphoreSlim _operation=new(1);
    private readonly CancellationTokenSource _stop=new();
    private LiveSession? _session;
    private bool _disposed;
    private CancellationTokenSource? _proxyValidation;
    public ProgramMemory Memory {get;}
    public LauncherState State {get;private set;}
    public string? ErrorTitle {get;private set;}
    public string? ErrorDetail {get;private set;}
    public string? PersistenceWarning {get;private set;}
    public bool CanEditProxy {get;private set;}
    public bool ProxyPanelOpen {get;private set;}
    public string ProxyPanelReason {get;private set;}="manual";
    public bool ResumeAfterSave {get;private set;}
    public IReadOnlyList<ProxyProbeResult> Candidates {get;private set;}=[];
    public GuardSnapshot? Snapshot {get;private set;}
    public string? DisplayTarget=>State.IsLocked()?(!string.IsNullOrEmpty(Snapshot?.TargetPath)?Snapshot.TargetPath:_session?.Descriptor.TargetPath??Memory.Settings.SelectedProgram):Memory.Settings.SelectedProgram;
    public bool Locked=>State.IsLocked();
    public event Action? Changed;
    public LauncherController(string directory)
    {
        _directory=directory;_store=new(directory);_runtime=new(directory);_log=new(Path.Combine(directory,"logs"),"app");
        Memory=new(_store.Load());PersistenceWarning=_store.Warning;State=StateRules.ForSelection(Memory.Settings.SelectedProgram);
        _=MonitorAsync();
    }
    private void Publish()=>Changed?.Invoke();
    private void SetState(LauncherState state,string? title=null,string? detail=null,bool editProxy=false)
    {State=state;ErrorTitle=title;ErrorDetail=detail;CanEditProxy=editProxy;Publish();}
    public async Task InitializeAsync()
    {
        var saved=_runtime.Load();
        if(saved is null)return;
        SetState(LauncherState.Preparing,"正在恢复运行会话","正在核对目标进程与接管组件。");
        try
        {
            using var guardHandle=WindowsProcess.Open(saved.GuardPid);
            if(WindowsProcess.GetCreated(guardHandle)!=saved.GuardCreated)throw new IOException();
            var guard=await LocalPipe.ConnectAsync(saved.GuardPipe,saved.Key,_stop.Token);
            var snapshot=await guard.CallAsync<GuardSnapshot>("snapshot",null,_stop.Token);
            if(snapshot.SessionId!=saved.SessionId)throw new IOException("会话身份不匹配。");
            if(snapshot.State==LauncherState.Preparing)
            {
                await guard.CallAsync<bool>("rollback",null,_stop.Token,20);
                await StopRecoveredComponentsAsync(saved,guard);
                _runtime.Clear();SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));return;
            }
            if(snapshot.State==LauncherState.Ready&&snapshot.ProcessCount==0){await StopRecoveredComponentsAsync(saved,guard);_runtime.Clear();SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));return;}
            var handles=await guard.CallAsync<SharedTargetHandles>("retain",null,_stop.Token);
            var session=new LiveSession
            {
                Descriptor=saved,Proxy=saved.Proxy??Memory.Settings.Proxy??throw new IOException("无法恢复代理配置。"),Guard=guard,
                GuardProcess=Process.GetProcessById(saved.GuardPid),RelayProcess=TryGetProcess(saved.RelayPid,saved.RelayCreated),
                Job=new(new SafeKernelHandle(new IntPtr(handles.JobHandle))),
                Target=new(new SafeKernelHandle(new IntPtr(handles.ProcessHandle)),new SafeKernelHandle(new IntPtr(handles.ThreadHandle)),new(snapshot.RootPid,snapshot.RootCreated)),
                CommitRequested=true,ProtectionInterrupted=snapshot.ProtectionInterrupted
            };
            _session=session;AttachGuard(session);ApplySnapshot(session,snapshot);
            try{session.Relay=await LocalPipe.ConnectAsync(saved.RelayControlPipe,saved.Key,_stop.Token);}catch(IOException){}
        }
        catch(Exception e)when(e is IOException or Win32Exception or OperationCanceledException or ArgumentException)
        {
            _log.Write("reattach-failed",new{e.Message});
            SessionJob? job=null;
            try
            {
                job=SessionJob.Open("Local\\AuxLauncher.Session."+saved.SessionId);
                var members=job.Members();
                if(members.Count==0){job.Dispose();_runtime.Clear();SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));return;}
                var anchor=WindowsProcess.Open(members[0]);
                var identity=new ProcessIdentity(members[0],WindowsProcess.GetCreated(anchor));
                var session=new LiveSession{Descriptor=saved,Proxy=saved.Proxy??Memory.Settings.Proxy??throw new IOException("无法恢复代理配置。"),Job=job,Target=new(anchor,new SafeKernelHandle(),identity),GuardProcess=TryGetProcess(saved.GuardPid,saved.GuardCreated),RelayProcess=TryGetProcess(saved.RelayPid,saved.RelayCreated),CommitRequested=true,ProtectionInterrupted=true};
                _session=session;job=null;
                SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","目标进程仍保留，正在尝试恢复原会话。");
            }
            catch(Win32Exception native)when(native.NativeErrorCode==2){_runtime.Clear();SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));}
            catch(Exception recoveryError){SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","无法核对原会话，请保留工作后检查目标软件。");_log.Write("job-recovery-failed",new{recoveryError.Message});}
            finally{job?.Dispose();}
        }
    }
    public async Task SelectAsync(string path,bool history=false)
    {
        if(Locked||!await _operation.WaitAsync(0))return;
        try
        {
            if(history)Memory.SelectHistory(path);else Memory.SelectNew(path);
            await SaveAsync();ProxyPanelOpen=false;ResumeAfterSave=false;
            SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException)
        {ErrorTitle="无法选择该程序";ErrorDetail=e.Message;Publish();}
        finally{_operation.Release();}
    }
    public async Task RemoveAsync(string path)
    {
        if(Locked||!await _operation.WaitAsync(0))return;
        try{Memory.Remove(path);await SaveAsync();SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));}
        finally{_operation.Release();}
    }
    private async Task SaveAsync()
    {
        try{await _store.SaveAsync(Memory.Settings);PersistenceWarning=null;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or Win32Exception){PersistenceWarning="本次更改尚未保存："+e.Message;_log.Write("settings-save-failed",new{e.Message});}
    }
    public void OpenProxy()
    {
        if(Locked)return;
        if(ProxyPanelOpen){CancelProxy();return;}
        ProxyPanelOpen=!ProxyPanelOpen;ProxyPanelReason="manual";ResumeAfterSave=false;Candidates=[];Publish();
    }
    public void CancelProxy(){if(Locked)return;_proxyValidation?.Cancel();ProxyPanelOpen=false;ResumeAfterSave=false;Candidates=[];Publish();}
    public async Task SaveProxyAsync(ProxyEndpoint proxy)
    {
        if(Locked||!await _operation.WaitAsync(0))return;
        var resume=false;
        using var validation=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _proxyValidation=validation;
        try
        {
            proxy.Validate();
            var result=await Socks5Client.ProbeAsync(proxy,validation.Token,3000);
            if(validation.IsCancellationRequested)return;
            if(!result.Available)
            {
                ProxyPanelReason=result.RequiresAuthentication?"auth":ProxyPanelReason;
                ErrorTitle="无法确认本地代理";ErrorDetail=result.Error;Publish();return;
            }
            Memory.SetProxy(proxy);await SaveAsync();resume=ResumeAfterSave;ProxyPanelOpen=false;ResumeAfterSave=false;Candidates=[];
            SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));
        }
        catch(Exception e)when(e is IOException or ArgumentException or OperationCanceledException){ErrorTitle="代理入口无效";ErrorDetail=e.Message;Publish();}
        finally{_proxyValidation=null;_operation.Release();}
        if(resume&&!_stop.IsCancellationRequested)await StartAsync();
    }
    public async Task StartAsync()
    {
        if(Locked||Memory.Settings.SelectedProgram is null||!await _operation.WaitAsync(0))return;
        LiveSession? session=null;
        try
        {
            var target=Memory.Settings.SelectedProgram;
            if(!File.Exists(target)){SetState(LauncherState.Missing);return;}
            SetState(LauncherState.Preparing);
            if(Memory.Settings.Proxy is null)
            {
                var results=await ProxyDiscovery.FindAsync(_stop.Token);
                var available=results.Where(x=>x.Available).ToArray();
                if(available.Length==1&&results.Count==1){Memory.SetProxy(available[0].Endpoint);await SaveAsync();}
                else
                {
                    Candidates=results;ProxyPanelReason=results.Count==0?"none":results.All(x=>x.RequiresAuthentication)?"auth":"multi";ResumeAfterSave=true;ProxyPanelOpen=true;
                    SetState(StateRules.ForSelection(target));return;
                }
            }
            var proxy=Memory.Settings.Proxy!;
            var probe=await Socks5Client.ProbeAsync(proxy,_stop.Token,3000);
            if(!probe.Available){SetState(LauncherState.Failed,"无法连接本地代理入口",probe.Error??"请确认代理软件已连接。",true);return;}
            if(!probe.UdpAvailable){SetState(LauncherState.Failed,"本地代理未提供 UDP 转发","请在代理软件中启用 SOCKS5 UDP 支持，或更换可用入口。",true);return;}
            var id=Guid.NewGuid().ToString("N");var key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var descriptor=new RuntimeSession(id,key,"AuxLauncher.Guard."+id,0,0,"AuxLauncher.RelayData."+id,"AuxLauncher.RelayControl."+id,0,0,target,0,0,proxy);
            session=new(){Descriptor=descriptor,Proxy=proxy};
            _session=session;ProxyPanelOpen=false;
            await StartRelayAsync(session);
            await StartGuardAsync(session);
            session.Job=new SessionJob("Local\\AuxLauncher.Session."+id);
            session.Target=WindowsProcess.CreateSuspended(target);
            session.Job.Add(session.Target.Process);
            session.Descriptor=session.Descriptor with {RootPid=session.Target.Identity.Pid,RootCreated=session.Target.Identity.Created};
            await session.Guard!.CallAsync<GuardSnapshot>("bind",Binding(session,true),_stop.Token);
            _runtime.Save(session.Descriptor);
            session.CommitRequested=true;
            var snapshot=await session.Guard.CallAsync<GuardSnapshot>("commit",null,_stop.Token);
            ApplySnapshot(session,snapshot);
        }
        catch(Exception e)
        {
            _log.Write("start-failed",new{e.Message});
            if(session?.CommitRequested==true&&session.Guard?.IsConnected==true)
            {
                try
                {
                    var snapshot=await session.Guard.CallAsync<GuardSnapshot>("snapshot",null,default,4);
                    if(snapshot.State==LauncherState.Preparing)session.CommitRequested=false;
                }
                catch(Exception statusError)when(statusError is IOException or OperationCanceledException){_log.Write("commit-outcome-unknown",new{statusError.Message});}
            }
            if(session?.CommitRequested==true)
            {
                session.ProtectionInterrupted=true;
                SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","无法确认会话提交结果，正在尝试恢复。");
            }
            else
            {
                var canceled=e is Win32Exception native&&native.NativeErrorCode==1223;
                if(session is not null){session.StartFailureTitle=canceled?"已取消管理员授权":"启动失败";session.StartFailureDetail=canceled?"目标程序未放行，可以稍后重试。":e.Message;}
                var cleanupError=await RollbackAsync(session);
                if(cleanupError is not null){session!.RollbackPending=true;SetState(LauncherState.Preparing,"启动失败，仍在回收资源",cleanupError);}
                else
                {
                    if(!File.Exists(Memory.Settings.SelectedProgram))SetState(LauncherState.Missing);
                    else SetState(LauncherState.Failed,canceled?"已取消管理员授权":"启动失败",canceled?"目标程序未放行，可以稍后重试。":e.Message);
                }
            }
        }
        finally{_operation.Release();}
    }
    private BindTargetRequest Binding(LiveSession s,bool resume)=>new(Environment.ProcessId,s.Target!.Identity.Pid,s.Target.Identity.Created,s.Job!.Handle.DangerousGetHandle().ToInt64(),s.Target.Process.DangerousGetHandle().ToInt64(),s.Target.Thread.DangerousGetHandle().ToInt64(),s.Descriptor.TargetPath,resume);
    private async Task StartRelayAsync(LiveSession session)
    {
        var d=session.Descriptor;
        session.RelayProcess=StartComponent("AuxLauncher.Relay",false,new Dictionary<string,string>{{"pipe",d.RelayControlPipe},{"guard-pipe",d.RelayPipe},{"owner",LocalPipe.UserSid},{"key",d.Key},{"logs",Path.Combine(_directory,"logs")}});
        session.Descriptor=d with {RelayPid=session.RelayProcess.Id,RelayCreated=WindowsProcess.Identify(session.RelayProcess.Id).Created};
        session.Relay=await LocalPipe.ConnectAsync(d.RelayControlPipe,d.Key,_stop.Token);
        await session.Relay.CallAsync<RelayReady>("configure",new RelayConfiguration(session.Proxy),_stop.Token);
    }
    private async Task StartGuardAsync(LiveSession session)
    {
        var d=session.Descriptor;
        session.GuardProcess=await Task.Run(()=>StartComponent("AuxLauncher.Guard",true,new Dictionary<string,string>{{"pipe",d.GuardPipe},{"owner",LocalPipe.UserSid},{"key",d.Key},{"logs",Path.Combine(_directory,"logs")}}));
        session.Descriptor=session.Descriptor with {GuardPid=session.GuardProcess.Id,GuardCreated=WindowsProcess.Identify(session.GuardProcess.Id).Created};
        session.Guard=await LocalPipe.ConnectAsync(d.GuardPipe,d.Key,_stop.Token);
        AttachGuard(session);
        await session.Guard.CallAsync<GuardSnapshot>("initialize",new GuardConfiguration(d.SessionId,LocalPipe.UserSid,d.RelayPipe,d.Key,session.Descriptor.RelayPid,Path.Combine(_directory,"logs")),_stop.Token,25);
    }
    private static Process StartComponent(string component,bool elevated,IReadOnlyDictionary<string,string> arguments)
    {
        var path=ComponentPaths.Resolve(component);
        var info=new ProcessStartInfo(path){UseShellExecute=elevated,CreateNoWindow=!elevated,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(path)!};
        if(elevated)info.Verb="runas";
        foreach(var item in arguments){info.ArgumentList.Add("--"+item.Key);info.ArgumentList.Add(item.Value);}
        return Process.Start(info)??throw new IOException("无法启动组件："+component);
    }
    private void AttachGuard(LiveSession session)
    {
        session.Guard!.Handler=(method,data)=>{
            if(method=="state")
            {
                var snapshot=data.Deserialize<GuardSnapshot>(RpcConnection.Json)!;
                ApplySnapshot(session,snapshot);
            }
            return Task.FromResult<object?>(true);
        };
    }
    private void ApplySnapshot(LiveSession session,GuardSnapshot snapshot)
    {
        if(_session!=session||snapshot.SessionId!=session.Descriptor.SessionId)return;
        session.GuardUnresponsive=false;
        Snapshot=snapshot;
        if(session.CommitRequested&&snapshot.State==LauncherState.Ready&&snapshot.ProcessCount==0)
        {if(!session.Closing)_=FinishAsync(session);return;}
        if(!session.CommitRequested&&snapshot.State!=LauncherState.Preparing)return;
        var state=session.ProtectionInterrupted&&snapshot.State is LauncherState.Running or LauncherState.ProxyLost?LauncherState.ProtectionInterrupted:snapshot.State;
        SetState(state,null,snapshot.Error);
    }
    private async Task<string?> RollbackAsync(LiveSession? session)
    {
        if(session is null)return null;
        try
        {
            if(session.Guard?.IsConnected==true)await session.Guard.CallAsync<bool>("rollback",null,default,20);
            if(session.Target is not null)WindowsProcess.Rollback(session.Target.Process);
            await StopComponentsAsync(session);
            if(_session==session)_session=null;
            session.Dispose();_runtime.Clear();Snapshot=null;return null;
        }
        catch(Exception e){_log.Write("rollback-incomplete",new{e.Message});return e.Message;}
    }
    private async Task FinishAsync(LiveSession session)
    {
        session.Closing=true;SetState(LauncherState.Recovering);
        try
        {
            await StopComponentsAsync(session);
            if(_session!=session)return;
            _session=null;session.Dispose();_runtime.Clear();Snapshot=null;await SaveAsync();
            SetState(StateRules.ForSelection(Memory.Settings.SelectedProgram));
        }
        catch(Exception e){session.Closing=false;SetState(LauncherState.Recovering,"正在重试资源回收",e.Message);}
    }
    private async Task StopComponentsAsync(LiveSession session)
    {
        var d=session.Descriptor;
        await StopComponentAsync(session.GuardProcess,session.Guard,d.GuardPipe,d.Key,"接管组件");
        await StopComponentAsync(session.RelayProcess,session.Relay,d.RelayControlPipe,d.Key,"转发组件");
    }
    private static async Task StopRecoveredComponentsAsync(RuntimeSession saved,RpcConnection guard)
    {
        using var guardProcess=TryGetProcess(saved.GuardPid,saved.GuardCreated);
        using var relayProcess=TryGetProcess(saved.RelayPid,saved.RelayCreated);
        await StopComponentAsync(guardProcess,guard,saved.GuardPipe,saved.Key,"接管组件");
        await StopComponentAsync(relayProcess,null,saved.RelayControlPipe,saved.Key,"转发组件");
    }
    private static async Task StopComponentAsync(Process? process,RpcConnection? connection,string pipe,string key,string name)
    {
        if(!IsAlive(process)){if(connection is not null)await connection.DisposeAsync();return;}
        var rpc=connection;
        try
        {
            if(rpc?.IsConnected!=true)rpc=await LocalPipe.ConnectAsync(pipe,key,CancellationToken.None);
            if(!await rpc.CallAsync<bool>("stop",null,default,20))throw new IOException(name+"仍有活动会话，尚未完成回收。");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process!.WaitForExitAsync(timeout.Token);
        }
        catch(Exception)when(!IsAlive(process)){}
        finally
        {
            if(rpc is not null&&(!IsAlive(process)||rpc!=connection))await rpc.DisposeAsync();
        }
    }
    private async Task MonitorAsync()
    {
        while(!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500,_stop.Token);
                var session=_session;
                if(session is null)
                {
                    if(State is LauncherState.Idle or LauncherState.Ready or LauncherState.Missing)
                    {
                        var next=StateRules.ForSelection(Memory.Settings.SelectedProgram);if(next!=State)SetState(next);
                    }
                    continue;
                }
                if(session.RollbackPending)
                {
                    if(await _operation.WaitAsync(0))
                    {
                        try
                        {
                            var error=await RollbackAsync(session);
                            if(error is null)SetState(File.Exists(Memory.Settings.SelectedProgram)?LauncherState.Failed:LauncherState.Missing,session.StartFailureTitle,session.StartFailureDetail);
                            else SetState(LauncherState.Preparing,"启动失败，仍在回收资源",error);
                        }
                        finally{_operation.Release();}
                    }
                    continue;
                }
                if(!session.CommitRequested||session.Closing||session.Recovery is {IsCompleted:false})continue;
                if(session.Job?.Members().Count==0){await FinishAsync(session);continue;}
                var guardAlive=IsAlive(session.GuardProcess);
                if(!guardAlive||session.Guard?.IsConnected!=true||session.GuardUnresponsive||Snapshot?.CaptureReady==false)
                {
                    session.ProtectionInterrupted=true;
                    SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","目标软件仍保留，正在尝试恢复接管。");
                    if(!session.RecoveryCancelled&&session.RecoveryAttempts<3)session.Recovery=RecoverAsync(session);
                    continue;
                }
                if(!IsAlive(session.RelayProcess))
                {
                    try{await StartRelayAsync(session);await session.Guard.CallAsync<GuardSnapshot>("relay.changed",null,_stop.Token);_runtime.Save(session.Descriptor);}
                    catch(Exception e){_log.Write("relay-recovery-failed",new{e.Message});}
                }
                try{ApplySnapshot(session,await session.Guard.CallAsync<GuardSnapshot>("snapshot",null,_stop.Token,4));}
                catch(Exception e)when(e is IOException or OperationCanceledException)
                {
                    session.GuardUnresponsive=true;session.ProtectionInterrupted=true;
                    SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","接管组件未能响应，正在尝试恢复。");
                    _log.Write("snapshot-unavailable",new{e.Message},"snapshot");
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){break;}
            catch(Exception e)
            {
                _log.Write("session-monitor-error",new{e.Message},"session-monitor-error");
                if(_session is {CommitRequested:true} active&&!active.Closing)
                {active.ProtectionInterrupted=true;SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","正在重新核对会话状态。");}
            }
        }
    }
    private async Task RecoverAsync(LiveSession session)
    {
        session.RecoveryAttempts++;
        try
        {
            await Task.Delay(Math.Min(10,session.RecoveryAttempts*2)*1000,_stop.Token);
            if(_session!=session)return;
            if(!IsAlive(session.RelayProcess))await StartRelayAsync(session);
            if(IsAlive(session.GuardProcess))
            {
                if(session.Guard?.IsConnected!=true){session.Guard=await LocalPipe.ConnectAsync(session.Descriptor.GuardPipe,session.Descriptor.Key,_stop.Token);AttachGuard(session);}
                var current=await session.Guard!.CallAsync<GuardSnapshot>("snapshot",null,_stop.Token,4);
                if(current.State==LauncherState.Preparing)
                {
                    session.CommitRequested=false;session.RollbackPending=true;session.StartFailureTitle="启动未完成";session.StartFailureDetail="目标尚未放行，正在回收暂停进程。";
                    SetState(LauncherState.Preparing,session.StartFailureTitle,session.StartFailureDetail);return;
                }
                var snapshot=await session.Guard!.CallAsync<GuardSnapshot>("recover",null,_stop.Token,25);
                ApplySnapshot(session,snapshot);
            }
            else
            {
                await StartGuardAsync(session);
                await session.Guard!.CallAsync<GuardSnapshot>("bind",Binding(session,false),_stop.Token);
                _runtime.Save(session.Descriptor);
                ApplySnapshot(session,await session.Guard.CallAsync<GuardSnapshot>("snapshot",null,_stop.Token));
            }
            SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","新连接已恢复接管；请在保存工作后自行重启目标软件。");
        }
        catch(Exception e)
        {
            if(e is Win32Exception native&&native.NativeErrorCode==1223)session.RecoveryCancelled=true;
            _log.Write("protection-recovery-failed",new{e.Message});
            SetState(LauncherState.ProtectionInterrupted,"保护中断，建议重启目标软件","恢复未完成，目标软件未被强制关闭。");
        }
    }
    private static bool IsAlive(Process? process){try{return process is not null&&!process.HasExited;}catch(InvalidOperationException){return false;}}
    private static Process? TryGetProcess(int pid,long created){try{var p=Process.GetProcessById(pid);if(WindowsProcess.Identify(pid).Created==created)return p;p.Dispose();return null;}catch(Exception e)when(e is ArgumentException or Win32Exception){return null;}}
    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;_disposed=true;
        await SaveAsync();
        if(_session is not null&&State.IsLocked())return;
        _stop.Cancel();
        if(_session is not null)await StopComponentsAsync(_session);
    }
}
