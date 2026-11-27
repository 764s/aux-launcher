using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AuxLauncher.Core;
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DivertAddress
{
    public long Timestamp;
    public uint Flags;
    public uint Reserved;
    public fixed byte Data[64];
    public byte Layer=>(byte)Flags;
    public byte Event=>(byte)(Flags>>8);
    public bool Outbound {readonly get=>(Flags&(1u<<17))!=0;set{if(value)Flags|=1u<<17;else Flags&=~(1u<<17);}}
    public readonly bool Loopback=>(Flags&(1u<<18))!=0;
    public void MarkChecksums()=>Flags|=(1u<<21)|(1u<<22)|(1u<<23);
    public byte[] Metadata(){fixed(byte* p=Data)return new ReadOnlySpan<byte>(p,64).ToArray();}
    public static DivertAddress Network(uint ifIndex=1,uint subIfIndex=0,bool outbound=false)
    {
        var address=new DivertAddress();address.Outbound=outbound;address.MarkChecksums();
        byte* p=address.Data;BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(p,4),ifIndex);BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(p+4,4),subIfIndex);
        return address;
    }
}
