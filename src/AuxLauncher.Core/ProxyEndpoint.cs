using System.Net;

namespace AuxLauncher.Core;

public sealed record ProxyEndpoint(string Host, int Port, string? Username = null, string? Password = null)
{
    public string Display => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
    public void Validate()
    {
        if (Port is < 1 or > 65535) throw new ArgumentException("请输入 1～65535 之间的端口。");
        if (!Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && (!IPAddress.TryParse(Host, out var ip) || !IPAddress.IsLoopback(ip)))
            throw new ArgumentException("本地代理地址必须为 localhost 或本机回环地址。");
        if (System.Text.Encoding.UTF8.GetByteCount(Username ?? "") > 255 || System.Text.Encoding.UTF8.GetByteCount(Password ?? "") > 255)
            throw new ArgumentException("代理认证信息过长。");
    }
}
