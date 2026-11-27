using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AuxLauncher.Core;
public sealed class WinDivertHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public WinDivertHandle():base(true){}
    protected override bool ReleaseHandle()=>WinDivertNative.Close(handle);
}
public static class WinDivertNative
{
    public const ulong Sniff=1,RecvOnly=4;
    public const int ShutdownReceive=1,ShutdownSend=2,ShutdownBoth=3;
    [DllImport("WinDivert.dll",EntryPoint="WinDivertOpen",CharSet=CharSet.Ansi,SetLastError=true)]public static extern WinDivertHandle Open(string filter,int layer,short priority,ulong flags);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertRecv",SetLastError=true)]public static extern bool Receive(WinDivertHandle handle,byte[]? packet,uint length,out uint received,ref DivertAddress address);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertSend",SetLastError=true)]public static extern bool Send(WinDivertHandle handle,byte[] packet,uint length,out uint sent,ref DivertAddress address);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertShutdown",SetLastError=true)]public static extern bool Shutdown(WinDivertHandle handle,int how);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertSetParam",SetLastError=true)]public static extern bool SetParam(WinDivertHandle handle,int name,ulong value);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertHelperCalcChecksums",SetLastError=true)]public static extern bool CalculateChecksums(byte[] packet,uint length,ref DivertAddress address,ulong flags);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertClose",SetLastError=true)]internal static extern bool Close(IntPtr handle);
    [DllImport("WinDivert.dll",EntryPoint="WinDivertHelperFormatIPv6Address",CharSet=CharSet.Ansi)]private static extern bool FormatIPv6(IntPtr address,StringBuilder text,uint size);
    public static WinDivertHandle OpenChecked(string filter,int layer,short priority,ulong flags)
    {
        var handle=Open(filter,layer,priority,flags);
        if(!handle.IsInvalid)return handle;
        var error=Marshal.GetLastWin32Error();handle.Dispose();
        throw new Win32Exception(error,error switch{5=>"网络接管需要管理员权限。",577=>"WinDivert 驱动签名未被系统接受。",1275=>"系统策略阻止 WinDivert 驱动加载。",_=>"无法初始化 WinDivert 网络接管。"});
    }
    public static unsafe string FormatAddress(ReadOnlySpan<byte> bytes)
    {
        fixed(byte* p=bytes){var text=new StringBuilder(64);if(!FormatIPv6((IntPtr)p,text,64))throw new InvalidDataException("端点地址无效。");var ip=System.Net.IPAddress.Parse(text.ToString());return ip.IsIPv4MappedToIPv6?ip.MapToIPv4().ToString():ip.ToString();}
    }
}
