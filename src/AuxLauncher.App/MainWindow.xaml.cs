using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AuxLauncher.Core;
using Forms = System.Windows.Forms;
using DrawingIcons = System.Drawing.SystemIcons;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AuxLauncher.App;
public partial class MainWindow : Window
{
    private readonly LauncherController _controller;
    private readonly Forms.NotifyIcon _tray;
    private bool _allowClose,_panelWasOpen,_authOpen;
    private string? _recentKey,_candidateKey;
    private LauncherState? _preview;
    private bool _warned;
    private LauncherState? _animatedState;
    private string? _lastPanelReason;
    private readonly Dictionary<FrameworkElement,long> _transitions=new();
    internal MainWindow(LauncherController controller)
    {
        InitializeComponent();_controller=controller;
        _tray=new Forms.NotifyIcon {Icon=DrawingIcons.Application,Text="代理启动器",Visible=false};
        _tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(Restore);
        var menu=new Forms.ContextMenuStrip();menu.Items.Add("打开启动器",null,(_,_)=>Dispatcher.Invoke(Restore));_tray.ContextMenuStrip=menu;
        _controller.Changed+=()=>Dispatcher.BeginInvoke(Render);
        Closing+=OnClosing;DragOver+=OnDragOver;Drop+=OnDrop;
        Render();
    }
    public void Restore(){Show();WindowState=WindowState.Normal;Activate();_tray.Visible=false;}
    public void Preview(LauncherState state){_preview=state;Render();}
    private static SolidColorBrush Brush(string hex)=>(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private void Render()
    {
        var state=_preview??_controller.State;var locked=state.IsLocked();
        var path=_preview is not null&&state!=LauncherState.Idle?@"C:\Program Files\ExampleApp\ExampleApp.exe":_controller.DisplayTarget;
        var missing=state==LauncherState.Missing;
        FileTitle.Text=path is null?"选择目标程序":Path.GetFileName(path);
        FileSubtitle.Text=path??"点击选择或拖入 .exe 文件";
        FileSubtitle.FontFamily=new FontFamily(path is null?"Segoe UI":"Cascadia Mono, Consolas");
        FileSubtitle.ToolTip=path;
        FileSubtitle.Foreground=Brush(missing?"#BE123C":path is null?"#94A3B8":"#64748B");
        MissingBadge.Visibility=missing?Visibility.Visible:Visibility.Collapsed;
        ChooseButton.Content=path is null?"选择":missing?"重新定位":"更换";
        ChooseButton.Background=Brush(path is null||missing?"#EFF6FF":"#FFFFFF");ChooseButton.Foreground=Brush(path is null||missing?"#2563EB":"#64748B");ChooseButton.BorderBrush=Brush(path is null||missing?"#BFDBFE":"#E2E8F0");
        ChooseButton.Visibility=locked?Visibility.Collapsed:Visibility.Visible;
        LiveDot.Visibility=locked?Visibility.Visible:Visibility.Collapsed;
        LiveDot.Fill=Brush(state==LauncherState.ProtectionInterrupted?"#F43F5E":state==LauncherState.ProxyLost?"#F59E0B":state==LauncherState.Running?"#10B981":"#3B82F6");
        if(_animatedState!=state)
        {
            _animatedState=state;LiveDot.BeginAnimation(OpacityProperty,null);
            if(locked)LiveDot.BeginAnimation(OpacityProperty,new DoubleAnimation(1,.55,TimeSpan.FromMilliseconds(state==LauncherState.ProtectionInterrupted?575:1100)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
        }
        FileSlot.Background=Brush(missing||state==LauncherState.ProtectionInterrupted?"#FFF1F2":path is null||locked?"#F8FAFC":"#FFFFFF");
        FileSlot.BorderBrush=Brush(missing||state==LauncherState.ProtectionInterrupted?"#FECDD3":"#E2E8F0");
        FileSlot.BorderThickness=new Thickness(path is null?0:1);EmptySlotOutline.Visibility=path is null?Visibility.Visible:Visibility.Collapsed;
        FileIconBox.Background=Brush(missing||state==LauncherState.ProtectionInterrupted?"#FFE4E6":state==LauncherState.Running?"#ECFDF5":path is null?"#F1F5F9":"#EFF6FF");
        FileIcon.Stroke=Brush(missing||state==LauncherState.ProtectionInterrupted?"#BE123C":state==LauncherState.Running?"#047857":path is null?"#94A3B8":"#2563EB");
        FileIcon.Data=Geometry.Parse(path is null?"M3,4 L19,4 L19,18 L3,18 Z M3,8 L19,8 M11,11 L11,15 M9,13 L13,13":"M3,4 L19,4 L19,18 L3,18 Z M3,8 L19,8 M7.5,11.5 L9.8,13 L7.5,14.5 M11.5,14.5 L14.5,14.5");
        FileSlot.ContextMenu!.IsEnabled=!locked&&path is not null;
        var endpoint=_controller.Memory.Settings.Proxy;
        ProxyTrigger.Content=endpoint is null?"未配置":":"+endpoint.Port;
        ProxyTrigger.ToolTip=endpoint is null?"本地 SOCKS5 入口（点击配置）":endpoint.Display+"（点击修改）";
        ProxyTrigger.IsEnabled=!locked;
        RenderRecent(locked,path);RenderProxy(locked);
        var label="经代理启动";var icon="Play";var color="#FFFFFF";var background="#2563EB";var border="#1E40AF";
        switch(state)
        {
            case LauncherState.Idle:case LauncherState.Missing:background="#F1F5F9";color="#94A3B8";border="#E2E8F0";break;
            case LauncherState.Preparing:label="正在准备启动…";icon="Spin";background="#EFF6FF";color="#2563EB";border="#BFDBFE";break;
            case LauncherState.Running:label="正在代理运行中";icon="Check";background="#ECFDF5";color="#047857";border="#A7F3D0";break;
            case LauncherState.ProxyLost:label="等待代理恢复…";icon="Warn";background="#F8FAFC";color="#64748B";border="#E2E8F0";break;
            case LauncherState.ProtectionInterrupted:label="保护异常中断";icon="ShieldOff";background="#FFF1F2";color="#BE123C";border="#FECDD3";break;
            case LauncherState.Recovering:label="正在回收会话…";icon="Spin";background="#EFF6FF";color="#2563EB";border="#BFDBFE";break;
            case LauncherState.Failed:label="重试";icon="Retry";break;
        }
        MainLabel.Text=label;MainLabel.Foreground=Brush(color);
        MainGlyph.Data=(Geometry)FindResource(icon);MainGlyph.Fill=icon=="Play"?Brush(color):null;MainGlyph.Stroke=icon=="Play"?null:Brush(color);MainGlyph.StrokeThickness=1.6;
        LaunchButton.Background=Brush(background);LaunchButton.BorderBrush=Brush(border);
        if(state is LauncherState.Ready or LauncherState.Failed)
        {
            LaunchButton.Background=new LinearGradientBrush(Color.FromRgb(43,110,245),Color.FromRgb(29,78,216),90);
            LaunchButton.Effect=new DropShadowEffect{Color=Color.FromRgb(29,78,216),Opacity=.26,BlurRadius=12,ShadowDepth=4};
        }
        else LaunchButton.Effect=null;
        LaunchButton.IsEnabled=(state is LauncherState.Ready or LauncherState.Failed)&&!_controller.ProxyPanelOpen;
        var rotate=(RotateTransform)MainGlyph.RenderTransform;
        if(icon=="Spin"){if(!rotate.HasAnimatedProperties)rotate.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(850)){RepeatBehavior=RepeatBehavior.Forever});}
        else rotate.BeginAnimation(RotateTransform.AngleProperty,null);
        string? title=null,detail=null;var tone="info";
        switch(state)
        {
            case LauncherState.Missing:title="原路径文件已移动或不存在";detail="请重新定位，或从最近记录切换其他程序";tone="error";break;
            case LauncherState.Preparing:detail="正在核验本地代理并建立进程接管…";break;
            case LauncherState.Running:title="程序已启动，代理接管生效中";detail="非 TUN 运行 · 关闭软件后自动释放";tone="success";break;
            case LauncherState.ProxyLost:title="本地代理连接中断";detail="已确认受管连接已暂停联网，代理恢复后新请求自动续接";tone="warn";break;
            case LauncherState.ProtectionInterrupted:title="保护中断，建议重启目标软件";detail=_controller.Snapshot?.CaptureReady==true?"新连接已恢复接管，请在保存工作后自行重启目标软件":"目标软件仍保留，正在尝试恢复接管";tone="error";break;
            case LauncherState.Recovering:detail="受管进程已全部退出，正在释放本会话资源…";break;
            case LauncherState.Failed:title="启动失败";detail="请检查本地代理入口后重试";tone="error";break;
        }
        if(_preview is null&&!string.IsNullOrEmpty(_controller.ErrorTitle)){title=_controller.ErrorTitle;detail=_controller.ErrorDetail;tone=state==LauncherState.Preparing?"warn":"error";}
        if(title is null&&detail is null&&!string.IsNullOrEmpty(_controller.PersistenceWarning)){title="更改尚未保存";detail=_controller.PersistenceWarning;tone="warn";}
        SetFeedback(title,detail,tone,_preview is null&&_controller.CanEditProxy&&!locked);
        if(state==LauncherState.ProtectionInterrupted)FeedbackGlyph.Data=(Geometry)FindResource("ShieldOff");
        if(_preview is null&&state==LauncherState.ProtectionInterrupted&&!_warned){_warned=true;_tray.Visible=true;_tray.ShowBalloonTip(6000,"代理启动器","保护中断，建议保存工作后重启目标软件。",Forms.ToolTipIcon.Warning);}
        if(!locked)_warned=false;
    }
    private void RenderRecent(bool locked,string? current)
    {
        var recent=_controller.Memory.Settings.RecentPrograms.Where(p=>!string.Equals(p,current,StringComparison.OrdinalIgnoreCase)).Take(3).ToArray();
        var key=string.Join("|",recent);
        if(key!=_recentKey)
        {
            _recentKey=key;RecentChips.Children.Clear();
            foreach(var path in recent)
            {
                var chip=new Border {CornerRadius=new CornerRadius(6),Background=Brush("#F1F5F9"),BorderBrush=Brush("#E2E8F0"),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,5,4),MaxWidth=152,Height=24,ToolTip=path};
                var panel=new DockPanel();
                var remove=new Button{Content="×",Style=(Style)FindResource("FlatButton"),FontSize=12,Padding=new Thickness(2,0,3,0),Width=17,Opacity=0,ToolTip="从记忆中移除",Tag=path};
                DockPanel.SetDock(remove,Dock.Right);remove.Click+=async(_,_)=>await _controller.RemoveAsync(path);
                var pick=new Button{Style=(Style)FindResource("FlatButton"),Padding=new Thickness(7,0,2,0),HorizontalContentAlignment=HorizontalAlignment.Left,Content=new TextBlock{Text=Path.GetFileName(path),FontSize=11.5,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=118}};
                pick.Click+=async(_,_)=>await _controller.SelectAsync(path,true);
                panel.Children.Add(remove);panel.Children.Add(pick);chip.Child=panel;
                chip.MouseEnter+=(_,_)=>remove.Opacity=1;chip.MouseLeave+=(_,_)=>remove.Opacity=0;chip.GotKeyboardFocus+=(_,_)=>remove.Opacity=1;
                RecentChips.Children.Add(chip);
            }
        }
        RecentPanel.IsEnabled=!locked;RecentPanel.Opacity=locked ? 0.45 : 1;
        Reveal(RecentPanel,recent.Length>0);
    }
    private void RenderProxy(bool locked)
    {
        var open=_controller.ProxyPanelOpen&&!locked;
        if(open&&!_panelWasOpen)
        {
            var saved=_controller.Memory.Settings.Proxy;
            ProxyHost.Text=saved?.Host??"127.0.0.1";ProxyPort.Text=saved?.Port.ToString()??"";
            ProxyUsername.Text=saved?.Username??"";ProxyPassword.Password=saved?.Password??"";
            if(saved is null&&_controller.Candidates.Count==1){ProxyHost.Text=_controller.Candidates[0].Endpoint.Host;ProxyPort.Text=_controller.Candidates[0].Endpoint.Port.ToString();}
            _authOpen=_controller.ProxyPanelReason=="auth"||!string.IsNullOrEmpty(saved?.Username);
        }
        _panelWasOpen=open;
        ProxyTitle.Text=_controller.ProxyPanelReason switch{"multi"=>"选择本地 SOCKS5 代理","none"=>"未检测到本地代理","auth"=>"需要代理认证",_=>"本地 SOCKS5 代理"};
        ProxyHint.Text=_controller.ProxyPanelReason=="none"?"请填写本地 SOCKS5 地址与端口":"";
        ProxyHint.Visibility=ProxyHint.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        if(_lastPanelReason!=_controller.ProxyPanelReason&&_controller.ProxyPanelReason=="auth")_authOpen=true;
        _lastPanelReason=_controller.ProxyPanelReason;
        var key=string.Join("|",_controller.Candidates.Select(x=>x.Endpoint.Display));
        if(key!=_candidateKey)
        {
            _candidateKey=key;CandidatesPanel.Children.Clear();
            foreach(var candidate in _controller.Candidates)
            {
                var button=new Button{Style=(Style)FindResource("FlatButton"),Content=candidate.Endpoint.Display,FontSize=10.5,FontFamily=new FontFamily("Consolas"),Background=Brush("#EFF6FF"),Foreground=Brush("#2563EB"),Margin=new Thickness(0,0,5,4),Padding=new Thickness(6,4,6,4)};
                button.Click+=(_,_)=>{ProxyHost.Text=candidate.Endpoint.Host;ProxyPort.Text=candidate.Endpoint.Port.ToString();if(candidate.RequiresAuthentication){_authOpen=true;Render();}};
                CandidatesPanel.Children.Add(button);
            }
        }
        CandidatesPanel.Visibility=CandidatesPanel.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
        AuthFields.Visibility=_authOpen?Visibility.Visible:Visibility.Collapsed;AuthToggle.Content=_authOpen?"− 收起账号密码":"+ 需要账号密码";
        SaveProxyButton.Content=_controller.ResumeAfterSave?"保存并启动":"保存";
        Reveal(ProxyPanel,open);
    }
    private void SetFeedback(string? title,string? detail,string tone,bool edit)
    {
        FeedbackTitle.Text=title??"";FeedbackTitle.Visibility=string.IsNullOrEmpty(title)?Visibility.Collapsed:Visibility.Visible;
        FeedbackDetail.Text=detail??"";FeedbackDetail.Visibility=string.IsNullOrEmpty(detail)?Visibility.Collapsed:Visibility.Visible;
        var colors=tone switch{"success"=>("#047857","#ECFDF5","#A7F3D0","Check"),"warn"=>("#B45309","#FFFBEB","#FDE68A","Warn"),"error"=>("#BE123C","#FFF1F2","#FECDD3","Error"),_=>("#2563EB","#EFF6FF","#BFDBFE","Spin")};
        Feedback.Background=Brush(colors.Item2);Feedback.BorderBrush=Brush(colors.Item3);
        FeedbackTitle.Foreground=FeedbackDetail.Foreground=FeedbackGlyph.Stroke=Brush(colors.Item1);FeedbackGlyph.Data=(Geometry)FindResource(colors.Item4);
        EditProxyLink.Visibility=edit?Visibility.Visible:Visibility.Collapsed;
        Reveal(Feedback,!string.IsNullOrEmpty(title)||!string.IsNullOrEmpty(detail));
    }
    private void Reveal(FrameworkElement element,bool visible)
    {
        if(visible==(element.Visibility==Visibility.Visible))return;
        var version=_transitions.GetValueOrDefault(element)+1;_transitions[element]=version;
        element.BeginAnimation(HeightProperty,null);
        if(!visible){element.Visibility=Visibility.Collapsed;element.Height=double.NaN;return;}
        element.Visibility=Visibility.Visible;element.Height=double.NaN;
        element.Measure(new Size(348,double.PositiveInfinity));
        var height=element.DesiredSize.Height;
        var animation=new DoubleAnimation(0,Math.Max(1,height),TimeSpan.FromMilliseconds(220)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
        animation.Completed+=(_,_)=>{if(_transitions.GetValueOrDefault(element)==version){element.BeginAnimation(HeightProperty,null);element.Height=double.NaN;}};
        element.BeginAnimation(HeightProperty,animation);
    }
    private void TitleDrag(object sender,MouseButtonEventArgs e)
    {
        for(var element=e.OriginalSource as DependencyObject;element is not null;element=VisualTreeHelper.GetParent(element))
            if(element is ButtonBase)return;
        if(e.LeftButton==MouseButtonState.Pressed)try{DragMove();}catch(InvalidOperationException){}
    }
    private void Minimize(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    private void CloseClick(object sender,RoutedEventArgs e)=>Close();
    private void OpenProxy(object sender,RoutedEventArgs e)=>_controller.OpenProxy();
    private void CancelProxy(object sender,RoutedEventArgs e)=>_controller.CancelProxy();
    private void ToggleAuth(object sender,RoutedEventArgs e){_authOpen=!_authOpen;Render();}
    private async void SaveProxy(object sender,RoutedEventArgs e)
    {
        if(!int.TryParse(ProxyPort.Text,out var port)){SetFeedback("端口格式不正确","请输入 1～65535 之间的端口","error",false);return;}
        SaveProxyButton.IsEnabled=false;
        try{await _controller.SaveProxyAsync(new(ProxyHost.Text.Trim(),port,_authOpen?ProxyUsername.Text:null,_authOpen?ProxyPassword.Password:null));}
        finally{SaveProxyButton.IsEnabled=true;}
    }
    private async void Choose(object sender,RoutedEventArgs e)=>await PickAsync();
    private async void FileSlotClick(object sender,MouseButtonEventArgs e){if(_controller.Memory.Settings.SelectedProgram is null&&!_controller.Locked)await PickAsync();}
    private async Task PickAsync()
    {
        if(_controller.Locked||_preview is not null)return;
        var dialog=new OpenFileDialog{Filter="Windows 应用程序 (*.exe)|*.exe",CheckFileExists=true,Multiselect=false,Title="选择目标程序"};
        if(dialog.ShowDialog(this)==true)await _controller.SelectAsync(dialog.FileName);
    }
    private async void RemoveCurrent(object sender,RoutedEventArgs e){if(_controller.Memory.Settings.SelectedProgram is string path)await _controller.RemoveAsync(path);}
    private async void Launch(object sender,RoutedEventArgs e){if(_preview is null)await _controller.StartAsync();}
    private void OnDragOver(object sender,DragEventArgs e){e.Effects=!_controller.Locked&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private async void OnDrop(object sender,DragEventArgs e)
    {
        if(_controller.Locked||_preview is not null)return;
        if(e.Data.GetData(DataFormats.FileDrop) is string[] paths&&paths.Length==1)await _controller.SelectAsync(paths[0]);
        e.Handled=true;
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_allowClose)return;e.Cancel=true;
        if(_controller.Locked&&_preview is null){Hide();_tray.Visible=true;return;}
        await _controller.DisposeAsync();_allowClose=true;_tray.Dispose();Close();Application.Current.Shutdown();
    }
}

