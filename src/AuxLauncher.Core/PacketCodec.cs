using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AuxLauncher.Core;
public static class PacketCodec
{
    public static byte[] Rewrite(PacketView packet,IPAddress source,int sourcePort,IPAddress destination,int destinationPort)
    {
        if(source.AddressFamily!=destination.AddressFamily||source.AddressFamily!=packet.Source.AddressFamily)throw new ArgumentException("地址族不匹配。");
        var data=packet.Data.AsSpan(0,packet.Length).ToArray();
        source.GetAddressBytes().CopyTo(data,packet.IPv6?8:12);
        destination.GetAddressBytes().CopyTo(data,packet.IPv6?24:16);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(packet.TransportOffset),(ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(packet.TransportOffset+2),(ushort)destinationPort);
        UpdateChecksums(data);return data;
    }
    public static byte[] Udp(IPAddress source,int sourcePort,IPAddress destination,int destinationPort,ReadOnlySpan<byte> payload)
    {
        if(source.AddressFamily!=destination.AddressFamily)throw new ArgumentException("地址族不匹配。");
        var offset=source.AddressFamily==AddressFamily.InterNetwork?20:40;
        if(payload.Length>65507-(offset==40?20:0))throw new ArgumentException("数据报过长。");
        var data=new byte[offset+8+payload.Length];
        WriteIp(data,source,destination,17);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset),(ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset+2),(ushort)destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset+4),(ushort)(8+payload.Length));
        payload.CopyTo(data.AsSpan(offset+8));UpdateChecksums(data);return data;
    }
    public static byte[] Tcp(IPAddress source,int sourcePort,IPAddress destination,int destinationPort,uint sequence,uint ack,byte flags,ReadOnlySpan<byte> payload=default)
    {
        if(source.AddressFamily!=destination.AddressFamily)throw new ArgumentException("地址族不匹配。");
        var offset=source.AddressFamily==AddressFamily.InterNetwork?20:40;
        var data=new byte[offset+20+payload.Length];WriteIp(data,source,destination,6);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset),(ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset+2),(ushort)destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset+4),sequence);BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset+8),ack);
        data[offset+12]=0x50;data[offset+13]=flags;data[offset+14]=0xff;data[offset+15]=0xff;
        payload.CopyTo(data.AsSpan(offset+20));UpdateChecksums(data);return data;
    }
    public static byte[] ResetFor(PacketView packet)
    {
        var consumed=(uint)Math.Max(0,packet.Length-packet.TransportOffset-packet.TcpHeaderLength)+(packet.Syn?1u:0u)+((packet.TcpFlags&1)!=0?1u:0u);
        return Tcp(packet.Destination,packet.DestinationPort,packet.Source,packet.SourcePort,0,packet.Sequence+consumed,0x14);
    }
    private static void WriteIp(byte[] data,IPAddress source,IPAddress destination,byte protocol)
    {
        var v6=source.AddressFamily==AddressFamily.InterNetworkV6;
        if(v6){data[0]=0x60;BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4),(ushort)(data.Length-40));data[6]=protocol;data[7]=64;source.GetAddressBytes().CopyTo(data,8);destination.GetAddressBytes().CopyTo(data,24);}
        else{data[0]=0x45;BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2),(ushort)data.Length);data[6]=0x40;data[8]=64;data[9]=protocol;source.GetAddressBytes().CopyTo(data,12);destination.GetAddressBytes().CopyTo(data,16);}
    }
    public static void UpdateChecksums(byte[] data)
    {
        var p=PacketView.Parse(data)??throw new InvalidDataException();
        if(!p.IPv6){data[10]=data[11]=0;BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10),Checksum(data.AsSpan(0,p.TransportOffset)));}
        if(p.Protocol is not (6 or 17)||p.Fragment)return;
        var location=p.TransportOffset+(p.Protocol==6?16:6);data[location]=data[location+1]=0;
        var length=p.Length-p.TransportOffset;uint sum=0;
        if(p.IPv6)
        {
            sum=Sum(data.AsSpan(8,32));sum+=(uint)(length>>16)+(uint)(length&0xffff)+p.Protocol;
        }
        else {sum=Sum(data.AsSpan(12,8));sum+=(uint)length+p.Protocol;}
        var checksum=Finish(sum+Sum(data.AsSpan(p.TransportOffset,length)));
        if(p.Protocol==17&&checksum==0)checksum=0xffff;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(location),checksum);
    }
    public static ushort Checksum(ReadOnlySpan<byte> bytes)=>Finish(Sum(bytes));
    private static uint Sum(ReadOnlySpan<byte> bytes){uint sum=0;for(var i=0;i+1<bytes.Length;i+=2)sum+=BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i,2));if((bytes.Length&1)!=0)sum+=(uint)bytes[^1]<<8;return sum;}
    private static ushort Finish(uint sum){while((sum>>16)!=0)sum=(sum&0xffff)+(sum>>16);return (ushort)~sum;}
}
