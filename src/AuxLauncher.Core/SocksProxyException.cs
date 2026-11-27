namespace AuxLauncher.Core;
public sealed class SocksProxyException(string message, bool unavailable, byte reply = 0) : IOException(message)
{
    public bool ProxyUnavailable { get; } = unavailable;
    public byte Reply { get; } = reply;
}
