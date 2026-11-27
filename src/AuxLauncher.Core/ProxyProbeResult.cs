namespace AuxLauncher.Core;
public sealed record ProxyProbeResult(ProxyEndpoint Endpoint, bool Available, bool RequiresAuthentication, bool UdpAvailable, string? Error);
