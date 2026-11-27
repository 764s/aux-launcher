using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AuxLauncher.Core;
public sealed class SessionJob : IDisposable
{
    public SafeKernelHandle Handle {get;}
    public SessionJob(string? name=null)=>Handle=CreateJobObject(IntPtr.Zero,name);
    public SessionJob(SafeKernelHandle handle)=>Handle=handle;
    public static SessionJob Open(string name)
    {
        var handle=OpenJobObject(4,false,name);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        return new(handle);
    }
    public void Add(SafeKernelHandle process)
    {
        if(Handle.IsInvalid||!AssignProcessToJobObject(Handle,process))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法建立进程树范围。");
    }
    public bool Contains(SafeKernelHandle process)
    {
        if(!IsProcessInJob(process,Handle,out var contained))throw new Win32Exception(Marshal.GetLastWin32Error());
        return contained;
    }
    public IReadOnlyList<int> Members()
    {
        for(var capacity=64;capacity<=65536;capacity*=2)
        {
            var size=8+capacity*IntPtr.Size;var buffer=Marshal.AllocHGlobal(size);
            try
            {
                if(!QueryInformationJobObject(Handle,3,buffer,(uint)size,out _))
                {if(Marshal.GetLastWin32Error()==234)continue;throw new Win32Exception(Marshal.GetLastWin32Error());}
                var count=Marshal.ReadInt32(buffer,4);
                if(count<0||count>capacity)throw new InvalidDataException("Job 进程列表无效。");
                var result=new int[count];
                for(var i=0;i<count;i++)result[i]=checked((int)Marshal.ReadIntPtr(buffer,8+i*IntPtr.Size).ToInt64());
                return result;
            }finally{Marshal.FreeHGlobal(buffer);}
        }
        throw new IOException("Job 进程数超出支持范围。");
    }
    public void Dispose()=>Handle.Dispose();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeKernelHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeKernelHandle OpenJobObject(uint access,bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeKernelHandle job,SafeKernelHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool IsProcessInJob(SafeKernelHandle process,SafeKernelHandle job,out bool result);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryInformationJobObject(SafeKernelHandle job,int kind,IntPtr buffer,uint size,out uint returned);
}
