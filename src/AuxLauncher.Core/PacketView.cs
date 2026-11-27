using System.Buffers.Binary;
using System.Net;

namespace AuxLauncher.Core;
public sealed class PacketView
{
    public required byte[] Data {get;init;}
    public required IPAddress Source {get;init;}
    public required IPAddress Destination {get;init;}
    public required byte Protocol {get;init;}
    public required int TransportOffset {get;init;}
    public required int Length {get;init;}
    public int SourcePort {get;init;}
    public int DestinationPort {get;init;}
    public bool Fragment {get;init;}
    public bool FirstFragment {get;init;}
    public uint FragmentId {get;init;}
    public bool IPv6=>Data[0]>>4==6;
    public FlowKey Key=>new(Protocol,Source.ToString(),SourcePort,Destination.ToString(),DestinationPort);
    public byte TcpFlags=>Protocol==6&&Length>=TransportOffset+20?Data[TransportOffset+13]:(byte)0;
    public bool Syn=>(TcpFlags&2)!=0;
    public bool Ack=>(TcpFlags&16)!=0;
    public uint Sequence=>Protocol==6?BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(TransportOffset+4)):0;
    public int TcpHeaderLength=>Protocol==6?(Data[TransportOffset+12]>>4)*4:0;
    public byte[] UdpPayload=>Data.AsSpan(TransportOffset+8,Math.Max(0,Math.Min(Length-TransportOffset-8,BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(TransportOffset+4))-8))).ToArray();
    public string FragmentKey=>$"{Source}|{Destination}|{Protocol}|{FragmentId}";

    public static PacketView? Parse(byte[] data,bool allowTruncated=false)
    {
        if(data.Length<20)return null;
        var version=data[0]>>4;int offset,length;byte protocol;IPAddress source,destination;
        bool fragment=false,first=true;uint id=0;
        if(version==4)
        {
            offset=(data[0]&15)*4;if(offset<20||offset>data.Length)return null;
            length=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            if(length<offset||(!allowTruncated&&length>data.Length))return null;
            length=Math.Min(length,data.Length);protocol=data[9];
            source=new IPAddress(data.AsSpan(12,4));destination=new IPAddress(data.AsSpan(16,4));
            var bits=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6));
            fragment=(bits&0x3fff)!=0;first=(bits&0x1fff)==0;id=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        }
        else if(version==6)
        {
            if(data.Length<40)return null;
            length=40+BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
            if(!allowTruncated&&length>data.Length)return null;
            length=Math.Min(length,data.Length);offset=40;protocol=data[6];
            source=new IPAddress(data.AsSpan(8,16));destination=new IPAddress(data.AsSpan(24,16));
            for(var i=0;i<8&&protocol is 0 or 43 or 44 or 60 or 51;i++)
            {
                if(offset+8>length)return null;
                var next=data[offset];int size;
                if(protocol==44){var bits=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset+2));fragment=true;first=(bits&0xfff8)==0;id=BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset+4));size=8;}
                else size=protocol==51?(data[offset+1]+2)*4:(data[offset+1]+1)*8;
                offset+=size;protocol=next;if(offset>length)return null;
                if(fragment&&!first)break;
            }
        }
        else return null;
        var sourcePort=0;var destinationPort=0;
        if(first&&protocol is 6 or 17)
        {
            var minimum=allowTruncated?4:protocol==6?20:8;
            if(offset+minimum>length)return null;
            sourcePort=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            destinationPort=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset+2));
            if(!allowTruncated&&protocol==6&&((data[offset+12]>>4)*4<20||offset+(data[offset+12]>>4)*4>length))return null;
        }
        return new(){Data=data,Source=source,Destination=destination,Protocol=protocol,TransportOffset=offset,Length=length,SourcePort=sourcePort,DestinationPort=destinationPort,Fragment=fragment,FirstFragment=first,FragmentId=id};
    }
}
