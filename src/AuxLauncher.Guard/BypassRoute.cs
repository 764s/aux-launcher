namespace AuxLauncher.Guard;
internal sealed class BypassRoute(uint sequence,string reason)
{
    public uint Sequence {get;}=sequence;
    public string Reason {get;}=reason;
    public DateTime LastUse=DateTime.UtcNow;
}
