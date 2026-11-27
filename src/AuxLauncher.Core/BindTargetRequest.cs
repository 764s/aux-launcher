namespace AuxLauncher.Core;
public sealed record BindTargetRequest(int CorePid,int TargetPid,long Created,long JobHandle,long ProcessHandle,long ThreadHandle,string TargetPath,bool Resume=true);
