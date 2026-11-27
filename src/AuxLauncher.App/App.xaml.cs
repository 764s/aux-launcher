using System.Windows;
using AuxLauncher.Core;
using Application = System.Windows.Application;

namespace AuxLauncher.App;
public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _show;
    private LauncherController? _controller;
    private bool _ownsMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var arguments=Arguments.Parse(e.Args);
        var preview=arguments.GetValueOrDefault("preview-state");
        if(LocalPipe.IsElevated&&preview is null){MessageBox.Show("请使用普通方式打开启动器。网络接管会单独请求管理员权限。","代理启动器");Shutdown();return;}
        var instance="Local\\AuxLauncher."+LocalPipe.UserSid;
        if(preview is null)
        {
            _mutex=new Mutex(false,instance);
            try{_ownsMutex=_mutex.WaitOne(0);}catch(AbandonedMutexException){_ownsMutex=true;}
            if(!_ownsMutex){try{EventWaitHandle.OpenExisting(instance+".show").Set();}catch(WaitHandleCannotBeOpenedException){}Shutdown();return;}
            _show=new(false,EventResetMode.AutoReset,instance+".show");
        }
        var directory=arguments.GetValueOrDefault("data-dir")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AuxLauncher");
        _controller=new LauncherController(directory);
        var window=new MainWindow(_controller);
        MainWindow=window;window.Show();
        if(_show is not null)_=Task.Run(()=>{while(true){_show.WaitOne();Dispatcher.Invoke(()=>window.Restore());}});
        if(preview is not null&&Enum.TryParse<LauncherState>(preview,true,out var previewState))window.Preview(previewState);
        else await _controller.InitializeAsync();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if(_ownsMutex)_mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

