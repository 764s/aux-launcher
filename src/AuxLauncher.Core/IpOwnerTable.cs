using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace AuxLauncher.Core;

public static class IpOwnerTable
{
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool sorted,int family,int kind,uint reserved);
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint GetExtendedUdpTable(IntPtr table,ref int size,bool sorted,int family,int kind,uint reserved);
    public static IReadOnlyList<int> Find(PacketView packet)
    {
        var v6=packet.IPv6;var tcp=packet.Protocol==6;var family=v6?23:2;int size=0;
        if(tcp)GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,5,0);else GetExtendedUdpTable(IntPtr.Zero,ref size,false,family,1,0);
        if(size<=4||size>16*1024*1024)return [];
        var ptr=Marshal.AllocHGlobal(size);
        try
        {
            var status=tcp?GetExtendedTcpTable(ptr,ref size,false,family,5,0):GetExtendedUdpTable(ptr,ref size,false,family,1,0);
            if(status!=0)return [];
            var data=new byte[size];Marshal.Copy(ptr,data,0,size);
            var count=BinaryPrimitives.ReadInt32LittleEndian(data);var rowSize=tcp?(v6?56:24):(v6?28:12);
            var result=new HashSet<int>();
            for(var i=0;i<count&&4+(i+1)*rowSize<=size;i++)
            {
                var row=data.AsSpan(4+i*rowSize,rowSize);
                var localOffset=tcp&&!v6?4:0;var localPortOffset=tcp?(v6?20:8):(v6?20:4);
                var localPort=BinaryPrimitives.ReadUInt16BigEndian(row.Slice(localPortOffset,2));
                if(localPort!=packet.SourcePort)continue;
                var localAddress=new IPAddress(row.Slice(localOffset,v6?16:4));
                if(!localAddress.Equals(packet.Source)&&!localAddress.Equals(v6?IPAddress.IPv6Any:IPAddress.Any))continue;
                if(tcp)
                {
                    var remoteAddress=new IPAddress(row.Slice(v6?24:12,v6?16:4));
                    var remotePort=BinaryPrimitives.ReadUInt16BigEndian(row.Slice(v6?44:16,2));
                    if(!remoteAddress.Equals(packet.Destination)||remotePort!=packet.DestinationPort)continue;
                }
                var pid=BinaryPrimitives.ReadInt32LittleEndian(row.Slice(rowSize-4,4));
                if(pid>0)result.Add(pid);
            }
            return result.ToArray();
        }
        finally{Marshal.FreeHGlobal(ptr);}
    }
}
