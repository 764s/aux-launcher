using System.Diagnostics;
using System.Text.Json;
using AuxLauncher.Core;
using AuxLauncher.Tests;

var options=Arguments.Parse(args);
if(options.TryGetValue("verify-release",out var releaseDirectory))
{
    try{var count=ReleaseAudit.Verify(releaseDirectory);Console.WriteLine($"发布隐私检查通过：{count} 个文件。");}
    catch(Exception e){Console.Error.WriteLine(e.Message);Environment.ExitCode=1;}
    return;
}
var directory=Path.GetFullPath(options.GetValueOrDefault("artifacts",Path.Combine(AppContext.BaseDirectory,"test-artifacts")));
Directory.CreateDirectory(directory);
var results=new List<TestResult>();
async Task Run(string name,Func<Task> test)
{
    var clock=Stopwatch.StartNew();
    try{await test();results.Add(new(name,true,clock.ElapsedMilliseconds));Console.WriteLine("PASS "+name);}
    catch(Exception e){results.Add(new(name,false,clock.ElapsedMilliseconds,e.ToString()));Console.WriteLine("FAIL "+name+": "+e.Message);}
}
await UnitCases.RunAsync(directory,Run);
if(options.ContainsKey("integration"))await DriverIntegration.RunAsync(directory,Run,options.ContainsKey("loopback"),options.ContainsKey("ipv6"));
await ControllerCases.RunAsync(directory,Run,options.ContainsKey("integration"));
var report=Path.Combine(directory,"report.json");
await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{IntegrationMode=options.ContainsKey("integration")?(options.ContainsKey("loopback")?(options.ContainsKey("ipv6")?"ControlledLoopbackIPv6":"ControlledLoopbackIPv4"):"ExternalTestAddress"):"None",Passed=results.Count(x=>x.Passed),Failed=results.Count(x=>!x.Passed),Tests=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Tests: {results.Count(x=>x.Passed)} passed, {results.Count(x=>!x.Passed)} failed");
Console.WriteLine("Report: "+report);
Environment.ExitCode=results.All(x=>x.Passed)?0:1;

