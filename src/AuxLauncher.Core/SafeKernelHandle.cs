using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace AuxLauncher.Core;
public sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelHandle():base(true){}
    public SafeKernelHandle(IntPtr handle,bool owns=true):base(owns)=>SetHandle(handle);
    protected override bool ReleaseHandle()=>CloseHandle(handle);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CloseHandle(IntPtr handle);
}
