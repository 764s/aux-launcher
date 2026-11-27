using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AuxLauncher.Core;

namespace AuxLauncher.Tests;
internal sealed class DriverIntegration : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _id=Guid.NewGuid().ToString("N");
    private readonly string _key=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
    private EchoServers? _echo;
    private TestSocksServer? _proxy;
    private Process? _relayProcess,_guardProcess,_foreign;
    private RpcConnection? _relay,_guard;
    private SessionJob? _job;
    private SuspendedProcess? _root;
    private string _commands="";
    private string GuardPipe=>"AuxLauncher.TestGuard."+_id;
    private string RelayPipe=>"AuxLauncher.TestRelay."+_id;
    private string RelayControl=>"AuxLauncher.TestRelayControl."+_id;
    private bool _started;
    private int _proxyPort;
    private readonly string _address;
    private readonly bool _loopback;
    private DriverIntegration(string directory,bool loopback,bool ipv6){_directory=Path.Combine(directory,"driver-"+_id);_loopback=loopback;_address=loopback?(ipv6?"::1":"127.0.0.2"):"198.18.0.42";}
    public static async Task RunAsync(string directory,Func<string,Func<Task>,Task> run,bool loopback=false,bool ipv6=false)
    {
        await using var test=new DriverIntegration(directory,loopback,ipv6);
        await run("driver-initialization-and-normal-user-target",test.StartAsync);
        if(!test._started)return;
        await run("driver-tcp-through-socks",async()=>{var result=await test.Request("tcp",test._echo!.TcpPort);Check.True(result.GetProperty("Success").GetBoolean(),result.ToString());Check.True(test._proxy!.ConnectCount>0);});
        await run("driver-udp-through-socks",async()=>{var result=await test.Request("udp",test._echo!.UdpPort);Check.True(result.GetProperty("Success").GetBoolean(),result.ToString());Check.True(test._proxy!.DatagramCount>0);});
        await run("driver-autonomous-dns-through-socks",async()=>{var before=test._proxy!.DatagramCount;var port=test._loopback&&IPAddress.Parse(test._address).AddressFamily==AddressFamily.InterNetworkV6?test._echo!.DnsPort:53;var result=await test.Request("dns",port);Check.True(result.GetProperty("Success").GetBoolean(),result.ToString());Check.Equal(new IPEndPoint(IPAddress.Parse(test._address),port).ToString(),result.GetProperty("Source").GetString()!);Check.True(test._proxy.DatagramCount>before,"响应未经过测试 SOCKS5 中继，不能算代理测试通过。");var bytes=Convert.FromBase64String(result.GetProperty("Reply").GetString()!);Check.True(bytes.AsSpan(bytes.Length-4).SequenceEqual(new byte[]{203,0,113,8}),"DNS 响应不是受控测试解析器生成的结果。");});
        await run("driver-unrelated-same-exe-is-not-proxied",test.ForeignInstanceAsync);
        await run("driver-proxy-loss-preserves-target-and-new-requests-recover",test.ProxyLossAsync);
        await run("driver-relay-crash-preserves-capture",test.RelayCrashAsync);
        await run("driver-guard-exit-preserves-target-and-rebinds",test.GuardCrashAsync);
        await run("driver-parent-exit-child-stays-managed-and-cleans",test.ChildAsync);
    }
    private Process Start(string component,bool elevated,params string[] extra)
    {
        var path=component=="fixture"?UnitCases.FindFixture():ComponentPaths.Resolve(component);
        var info=new ProcessStartInfo(path){UseShellExecute=elevated,CreateNoWindow=!elevated,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(path)!};
        if(elevated)info.Verb="runas";
        foreach(var value in extra)info.ArgumentList.Add(value);
        return Process.Start(info)??throw new IOException("无法启动 "+component);
    }
    private async Task StartRelay()
    {
        _relayProcess=Start("AuxLauncher.Relay",false,"--pipe",RelayControl,"--guard-pipe",RelayPipe,"--owner",LocalPipe.UserSid,"--key",_key,"--logs",_directory);
        _relay=await LocalPipe.ConnectAsync(RelayControl,_key);
        await _relay.CallAsync<RelayReady>("configure",new RelayConfiguration(_proxy!.Endpoint));
    }
    private async Task StartGuard()
    {
        Console.WriteLine("Windows UAC: allow the test guard to load the signed WinDivert driver.");
        _guardProcess=Start("AuxLauncher.Guard",true,"--pipe",GuardPipe,"--owner",LocalPipe.UserSid,"--key",_key,"--logs",_directory,"--self-test");
        _guard=await LocalPipe.ConnectAsync(GuardPipe,_key);
        await _guard.CallAsync<GuardSnapshot>("initialize",new GuardConfiguration(_id,LocalPipe.UserSid,RelayPipe,_key,_relayProcess!.Id,_directory,_loopback?_address:null),timeoutSeconds:25);
    }
    private BindTargetRequest Binding(bool resume)=>new(Environment.ProcessId,_root!.Identity.Pid,_root.Identity.Created,_job!.Handle.DangerousGetHandle().ToInt64(),_root.Process.DangerousGetHandle().ToInt64(),_root.Thread.DangerousGetHandle().ToInt64(),UnitCases.FindFixture(),resume);
    private async Task StartAsync()
    {
        Check.True(!LocalPipe.IsElevated,"请用普通用户运行集成测试，测试会单独请求 UAC。");
        Directory.CreateDirectory(_directory);_commands=Path.Combine(_directory,"target");Directory.CreateDirectory(_commands);
        _echo=new();_proxy=new(dnsPort:_echo.DnsPort);_proxyPort=_proxy.Port;
        await StartRelay();await StartGuard();
        _job=new();_root=WindowsProcess.CreateSuspended(UnitCases.FindFixture(),"--mode client --commands \""+_commands+"\"");
        Check.True(!WindowsProcess.IsElevated(_root.Process));_job.Add(_root.Process);
        await _guard!.CallAsync<GuardSnapshot>("bind",Binding(true));
        var snapshot=await _guard.CallAsync<GuardSnapshot>("commit");Check.True(snapshot.CaptureReady);
        await UnitCases.WaitAsync(()=>File.Exists(Path.Combine(_commands,"ready.json")),5000);
        _started=true;
    }
    private async Task<JsonElement> Request(string kind,int port=0,string? commandDirectory=null,int timeout=5000,string? childDirectory=null)
    {
        var directory=commandDirectory??_commands;var id=Guid.NewGuid().ToString("N");
        var request=new{Id=id,Kind=kind,Host=_address,Port=port,Payload="guard-integration-"+id,Directory=childDirectory,TimeoutMs=timeout};
        var file=Path.Combine(directory,"request-"+id+".json");await File.WriteAllTextAsync(file+".tmp",JsonSerializer.Serialize(request));File.Move(file+".tmp",file);
        var response=Path.Combine(directory,"response-"+id+".json");
        await UnitCases.WaitAsync(()=>File.Exists(response),timeout+2500);
        using var document=JsonDocument.Parse(File.ReadAllText(response));
        if(document.RootElement.TryGetProperty("Success",out var success)&&!success.GetBoolean()&&kind is "tcp" or "udp" or "dns")
        {
            var snapshot=await _guard!.CallAsync<GuardSnapshot>("snapshot");
            var evidence=new{kind,snapshot.TcpConnections,snapshot.UdpDatagrams,snapshot.UnknownBypasses,ProxyConnects=_proxy!.ConnectCount,ProxyDatagrams=_proxy.DatagramCount};
            File.WriteAllText(Path.Combine(_directory,"failure-"+id+".json"),JsonSerializer.Serialize(evidence));
        }
        return document.RootElement.Clone();
    }
    private async Task<GuardSnapshot> WaitState(params LauncherState[] expected)
    {
        var deadline=DateTime.UtcNow.AddSeconds(12);
        while(DateTime.UtcNow<deadline)
        {
            var state=await _guard!.CallAsync<GuardSnapshot>("snapshot");
            if(expected.Contains(state.State))return state;
            await Task.Delay(250);
        }
        throw new TimeoutException("守护状态未达到："+string.Join(",",expected));
    }
    private async Task ForeignInstanceAsync()
    {
        var directory=Path.Combine(_directory,"foreign");Directory.CreateDirectory(directory);
        _foreign=Start("fixture",false,"--mode","client","--commands",directory);
        await UnitCases.WaitAsync(()=>File.Exists(Path.Combine(directory,"ready.json")),5000);
        var count=_proxy!.ConnectCount;
        var result=await Request("tcp",_echo!.TcpPort,directory,1200);
        Check.True(!result.GetProperty("Success").GetBoolean(),"非成员不应获得测试代理的数据");
        Check.Equal(count,_proxy.ConnectCount);
        var loopback=new TcpClient();await loopback.ConnectAsync(IPAddress.Loopback,_echo.TcpPort);loopback.Dispose();
        await Request("exit",commandDirectory:directory);await _foreign.WaitForExitAsync();
    }
    private async Task ProxyLossAsync()
    {
        await _proxy!.DisposeAsync();await WaitState(LauncherState.ProxyLost);
        Check.True(!WindowsProcess.HasExited(_root!.Process));
        var failed=await Request("tcp",_echo!.TcpPort,timeout:1800);Check.True(!failed.GetProperty("Success").GetBoolean());
        _proxy=new(_proxyPort,dnsPort:_echo.DnsPort);await WaitState(LauncherState.Running);
        var success=await Request("tcp",_echo.TcpPort);Check.True(success.GetProperty("Success").GetBoolean(),success.ToString());
        success=await Request("udp",_echo.UdpPort);Check.True(success.GetProperty("Success").GetBoolean(),success.ToString());
    }
    private async Task RelayCrashAsync()
    {
        _relayProcess!.Kill();await _relayProcess.WaitForExitAsync();
        var lost=await WaitState(LauncherState.ProxyLost);Check.True(lost.CaptureReady);Check.True(!WindowsProcess.HasExited(_root!.Process));
        await StartRelay();await _guard!.CallAsync<GuardSnapshot>("relay.changed");await WaitState(LauncherState.Running);
        var result=await Request("tcp",_echo!.TcpPort);Check.True(result.GetProperty("Success").GetBoolean(),result.ToString());
    }
    private async Task GuardCrashAsync()
    {
        await _guard!.CallAsync<bool>("test.crash");await _guardProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(!WindowsProcess.HasExited(_root!.Process),"守护退出不得杀死目标");
        await StartGuard();await _guard!.CallAsync<GuardSnapshot>("bind",Binding(false));
        var interrupted=await WaitState(LauncherState.ProtectionInterrupted);Check.True(interrupted.CaptureReady);
        var result=await Request("tcp",_echo!.TcpPort);Check.True(result.GetProperty("Success").GetBoolean(),result.ToString());
    }
    private async Task ChildAsync()
    {
        var childDirectory=Path.Combine(_directory,"child");Directory.CreateDirectory(childDirectory);
        var child=await Request("child",childDirectory:childDirectory);Check.True(child.GetProperty("Success").GetBoolean());
        await UnitCases.WaitAsync(()=>WindowsProcess.HasExited(_root!.Process),5000);
        await UnitCases.WaitAsync(()=>File.Exists(Path.Combine(childDirectory,"ready.json")),5000);
        _commands=childDirectory;
        var snapshot=await _guard!.CallAsync<GuardSnapshot>("snapshot");Check.True(snapshot.ProcessCount>0);
        var reply=await Request("tcp",_echo!.TcpPort);Check.True(reply.GetProperty("Success").GetBoolean(),reply.ToString());
        await Request("exit");await WaitState(LauncherState.Ready);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if(_root is not null&&_job is not null&&_job.Members().Count>0)
            {
                try{await Request("exit");await UnitCases.WaitAsync(()=>_job.Members().Count==0,5000);}catch{}
            }
            if(_guard?.IsConnected==true)try{if(!_started)await _guard.CallAsync<bool>("rollback");await _guard.CallAsync<bool>("stop",timeoutSeconds:20);}catch{}
            if(_relay?.IsConnected==true)try{await _relay.CallAsync<bool>("stop");}catch{}
        }
        finally
        {
            if(_guard is not null)await _guard.DisposeAsync();if(_relay is not null)await _relay.DisposeAsync();
            if(_proxy is not null)await _proxy.DisposeAsync();if(_echo is not null)await _echo.DisposeAsync();
            _root?.Dispose();_job?.Dispose();_guardProcess?.Dispose();_relayProcess?.Dispose();_foreign?.Dispose();
        }
    }
}

