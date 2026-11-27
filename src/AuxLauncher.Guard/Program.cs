using AuxLauncher.Core;
using AuxLauncher.Guard;

var arguments=Arguments.Parse(args);
var stop=new CancellationTokenSource();
try
{
    if(!LocalPipe.IsElevated)throw new UnauthorizedAccessException("拦截守护组件必须通过 UAC 启动。");
    await using var host=new GuardHost(arguments.Required("owner"),arguments.Required("logs"),stop,arguments.ContainsKey("self-test"));
    await LocalPipe.ServeAsync(arguments.Required("pipe"),arguments.Required("owner"),arguments.Required("key"),(rpc,pid)=>host.Control(rpc,pid,stop),stop.Token);
}
catch(OperationCanceledException){}
catch(Exception e){new AppLog(arguments.GetValueOrDefault("logs",AppContext.BaseDirectory),"guard").Write("fatal",new{e.Message});Environment.ExitCode=1;}
