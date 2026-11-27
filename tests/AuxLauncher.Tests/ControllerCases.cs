using System.Collections.Concurrent;
using AuxLauncher.App;
using AuxLauncher.Core;

namespace AuxLauncher.Tests;
internal static class ControllerCases
{
    public static async Task RunAsync(string directory,Func<string,Func<Task>,Task> run,bool integration)
    {
        await run("controller-selection-persistence-and-missing-history",async()=>
        {
            var folder=Path.Combine(directory,"controller-memory-"+Guid.NewGuid().ToString("N"));
            var missing=Path.Combine(folder,"gone.exe");
            await using(var controller=new LauncherController(folder))
            {
                Check.Equal(LauncherState.Idle,controller.State);
                await controller.SelectAsync(UnitCases.FindFixture());Check.Equal(LauncherState.Ready,controller.State);
                await controller.SelectAsync(missing);Check.Equal(UnitCases.FindFixture(),controller.Memory.Settings.SelectedProgram!);
            }
            await using(var restored=new LauncherController(folder))
            {
                Check.Equal(LauncherState.Ready,restored.State);
                File.WriteAllBytes(missing,[]);await restored.SelectAsync(missing);await restored.SelectAsync(UnitCases.FindFixture());File.Delete(missing);
                await restored.SelectAsync(missing,true);Check.Equal(LauncherState.Missing,restored.State);
                await restored.RemoveAsync(missing);Check.Equal(LauncherState.Idle,restored.State);
            }
        });
        await run("controller-proxy-confirmation-auth-and-cancel",async()=>
        {
            await using var proxy=new TestSocksServer(authentication:true);
            var folder=Path.Combine(directory,"controller-proxy-"+Guid.NewGuid().ToString("N"));
            await using var controller=new LauncherController(folder);
            controller.OpenProxy();Check.True(controller.ProxyPanelOpen);
            await controller.SaveProxyAsync(proxy.Endpoint with{Username=null,Password=null});
            Check.True(controller.ProxyPanelOpen);Check.Equal("auth",controller.ProxyPanelReason);
            await controller.SaveProxyAsync(proxy.Endpoint);Check.True(!controller.ProxyPanelOpen);Check.Equal(proxy.Port,controller.Memory.Settings.Proxy!.Port);
            controller.OpenProxy();controller.CancelProxy();Check.True(!controller.ProxyPanelOpen);
            Check.Equal(proxy.Endpoint,new SettingsStore(folder).Load().Proxy!);
        });
        await run("controller-rejects-proxy-without-udp-before-uac",async()=>
        {
            await using var proxy=new TestSocksServer(udpSupported:false);
            await using var controller=new LauncherController(Path.Combine(directory,"controller-no-udp-"+Guid.NewGuid().ToString("N")));
            await controller.SelectAsync(UnitCases.FindFixture());controller.Memory.SetProxy(proxy.Endpoint);
            await controller.StartAsync();Check.Equal(LauncherState.Failed,controller.State);Check.True(!controller.Locked);
            Check.True(controller.ErrorTitle!.Contains("UDP"));Check.True(controller.Snapshot is null);
        });
        await run("runtime-session-keeps-encrypted-proxy-snapshot",()=>
        {
            var folder=Path.Combine(directory,"runtime-"+Guid.NewGuid().ToString("N"));var store=new RuntimeStore(folder);
            var session=new RuntimeSession("test","session-secret","guard",1,2,"relay", "control",3,4,"target.exe",5,6,new("127.0.0.1",1080,"user","proxy-secret"));
            store.Save(session);var text=File.ReadAllText(Path.Combine(folder,"active-session.json"));
            Check.True(!text.Contains("session-secret")&&!text.Contains("proxy-secret"));Check.Equal(session,store.Load()!);store.Clear();Check.True(store.Load() is null);return Task.CompletedTask;
        });
        if(integration)await run("controller-real-start-lock-and-complete-cleanup",async()=>
        {
            var folder=Path.Combine(directory,"controller-live-"+Guid.NewGuid().ToString("N"));
            await using var proxy=new TestSocksServer();await using var controller=new LauncherController(folder);
            var states=new ConcurrentQueue<LauncherState>();controller.Changed+=()=>states.Enqueue(controller.State);
            await controller.SelectAsync(UnitCases.FindFixture());controller.Memory.SetProxy(proxy.Endpoint);
            await controller.StartAsync();
            try
            {
                Check.True(controller.State is LauncherState.Running or LauncherState.Recovering,controller.ErrorDetail??controller.State.ToString());
                if(controller.State==LauncherState.Running)
                {
                    Check.True(controller.Locked);var selected=controller.Memory.Settings.SelectedProgram;
                    await controller.RemoveAsync(selected!);Check.Equal(selected!,controller.Memory.Settings.SelectedProgram!);
                }
            }
            finally{await UnitCases.WaitAsync(()=>!controller.Locked,25000);}
            Check.Equal(LauncherState.Ready,controller.State);
            Check.True(states.Contains(LauncherState.Preparing)&&states.Contains(LauncherState.Running)&&states.Contains(LauncherState.Recovering));
            Check.True(!File.Exists(Path.Combine(folder,"active-session.json")),"回收完成后不得残留活动会话。");
        });
    }
}
