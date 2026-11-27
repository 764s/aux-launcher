namespace AuxLauncher.Core;
public sealed record GuardConfiguration(string SessionId,string OwnerSid,string RelayPipe,string Key,int RelayPid,string LogDirectory,string? TestLoopbackTarget=null);
