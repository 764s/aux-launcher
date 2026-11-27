using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace AuxLauncher.Core;
public static class WindowsProcess
{
    public const uint Query=0x1000, Synchronize=0x100000, DuplicateHandleRight=0x40;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo
    {
        public int cb; public string? reserved,desktop,title;
        public int x,y,xSize,ySize,xCount,yCount,fillAttribute,flags; public short showWindow,reserved2;
        public IntPtr reservedPointer,stdInput,stdOutput,stdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr process,thread;public int pid,tid; }
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo info);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessTimes(SafeKernelHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeKernelHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DuplicateHandle(SafeKernelHandle sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateProcess(SafeKernelHandle process,uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeKernelHandle handle,uint milliseconds);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(SafeKernelHandle process,uint desiredAccess,out SafeKernelHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(SafeKernelHandle token,int infoClass,IntPtr buffer,int size,out int returned);
    public static SuspendedProcess CreateSuspended(string path,string? arguments=null)
    {
        path=Path.GetFullPath(path);
        if(!File.Exists(path))throw new FileNotFoundException("目标程序不存在。",path);
        var startup=new StartupInfo{cb=Marshal.SizeOf<StartupInfo>()};
        var cmd=new StringBuilder("\""+path+"\""+(string.IsNullOrWhiteSpace(arguments)?"":" "+arguments));
        if(!CreateProcess(path,cmd,IntPtr.Zero,IntPtr.Zero,false,0x00000004|0x00000400,IntPtr.Zero,Path.GetDirectoryName(path)!,ref startup,out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(),"无法以普通用户权限创建目标程序。");
        var process=new SafeKernelHandle(info.process);var thread=new SafeKernelHandle(info.thread);
        try { return new(process,thread,new(info.pid,GetCreated(process))); }
        catch { TerminateProcess(process,1);thread.Dispose();process.Dispose();throw; }
    }
    public static SafeKernelHandle Open(int pid,uint access=Query|Synchronize)
    {
        var process=OpenProcess(access,false,pid);
        if(process.IsInvalid){process.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        return process;
    }
    public static long GetCreated(SafeKernelHandle process)
    {
        if(!GetProcessTimes(process,out var created,out _,out _,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        return created;
    }
    public static ProcessIdentity Identify(int pid){using var process=Open(pid);return new(pid,GetCreated(process));}
    public static bool HasExited(SafeKernelHandle process)=>process.IsClosed||WaitForSingleObject(process,0)==0;
    public static void Resume(SafeKernelHandle thread){if(ResumeThread(thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法放行目标主线程。");}
    public static void Rollback(SafeKernelHandle process){if(!HasExited(process)&&!TerminateProcess(process,1))throw new Win32Exception(Marshal.GetLastWin32Error(),"暂停目标尚未回收。");}
    public static SafeKernelHandle DuplicateFrom(int pid,long sourceHandle)
    {
        using var source=Open(pid,DuplicateHandleRight);
        if(!DuplicateHandle(source,new IntPtr(sourceHandle),GetCurrentProcess(),out var target,0,false,2))throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(target);
    }
    public static long DuplicateTo(int pid,SafeKernelHandle handle)
    {
        using var destination=Open(pid,DuplicateHandleRight);
        using var source=new SafeKernelHandle(GetCurrentProcess(),false);
        if(!DuplicateHandle(source,handle.DangerousGetHandle(),destination.DangerousGetHandle(),out var target,0,false,2))throw new Win32Exception(Marshal.GetLastWin32Error());
        return target.ToInt64();
    }
    public static string UserSid(SafeKernelHandle process)
    {
        if(!OpenProcessToken(process,8,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        using(token)
        {
            GetTokenInformation(token,1,IntPtr.Zero,0,out var size);var ptr=Marshal.AllocHGlobal(size);
            try { if(!GetTokenInformation(token,1,ptr,size,out _))throw new Win32Exception(Marshal.GetLastWin32Error());return new SecurityIdentifier(Marshal.ReadIntPtr(ptr)).Value; }
            finally {Marshal.FreeHGlobal(ptr);}
        }
    }
    public static bool IsElevated(SafeKernelHandle process)
    {
        if(!OpenProcessToken(process,8,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        using(token)
        {
            var ptr=Marshal.AllocHGlobal(4);
            try{if(!GetTokenInformation(token,20,ptr,4,out _))throw new Win32Exception(Marshal.GetLastWin32Error());return Marshal.ReadInt32(ptr)!=0;}
            finally{Marshal.FreeHGlobal(ptr);}
        }
    }
}
