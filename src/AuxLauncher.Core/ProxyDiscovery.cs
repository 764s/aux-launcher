namespace AuxLauncher.Core;
public static class ProxyDiscovery
{
    public static readonly int[] Ports = [10808,7897,7890,7891,1080,2080,10810];
    public static async Task<IReadOnlyList<ProxyProbeResult>> FindAsync(CancellationToken token=default)
    {
        var results=await Task.WhenAll(Ports.Select(port=>Socks5Client.ProbeAsync(new("127.0.0.1",port),token,650)));
        return results.Where(x=>x.Available||x.RequiresAuthentication).ToArray();
    }
}
