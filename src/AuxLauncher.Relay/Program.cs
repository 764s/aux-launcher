using AuxLauncher.Core;
using AuxLauncher.Relay;

var argsMap=Arguments.Parse(args);
var stop=new CancellationTokenSource();
try
{
    using var host=new RelayHost(argsMap.Required("logs"),stop);
    await Task.WhenAll(
        LocalPipe.ServeAsync(argsMap.Required("pipe"),argsMap.Required("owner"),argsMap.Required("key"),(rpc,pid)=>host.Control(rpc,pid,stop),stop.Token),
        LocalPipe.ServeAsync(argsMap.Required("guard-pipe"),argsMap.Required("owner"),argsMap.Required("key"),(rpc,pid)=>host.Guard(rpc,pid),stop.Token));
}
catch(OperationCanceledException){}
catch(Exception e){Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
