namespace AuxLauncher.Core;
public sealed record OwnershipEvidence(int Pid,long Created,bool Managed,ulong EndpointId=0)
{
    public string ProcessKey=>$"{Pid}:{Created}";
}
