using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AuxLauncher.Core;

public static class LocalPipe
{
    public static string UserSid => WindowsIdentity.GetCurrent().User!.Value;
    public static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static NamedPipeServerStream CreateServer(string name, string ownerSid)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { ownerSid, "S-1-5-18", "S-1-5-32-544" }.Distinct())
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
    }
    public static async Task<RpcConnection> ConnectAsync(string name, string key, CancellationToken token = default)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10000, token);
            var rpc = new RpcConnection(pipe); rpc.Start();
            var ok = await rpc.CallAsync<bool>("hello", new { Key = key }, token);
            if (!ok) throw new UnauthorizedAccessException("组件身份校验失败。");
            return rpc;
        }
        catch { pipe.Dispose(); throw; }
    }
    public static async Task ServeAsync(string name, string ownerSid, string key, Func<RpcConnection,uint,Func<string,System.Text.Json.JsonElement,Task<object?>>> factory, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var pipe = CreateServer(name, ownerSid);
            try { await pipe.WaitForConnectionAsync(token); }
            catch { pipe.Dispose(); throw; }
            GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid);
            var rpc = new RpcConnection(pipe);
            var handler = factory(rpc, pid);
            var authenticated = false;
            rpc.Handler = async (method, data) =>
            {
                if (method == "hello")
                {
                    var supplied = data.GetProperty("Key").GetString() ?? "";
                    authenticated = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(supplied));
                    if (!authenticated) throw new UnauthorizedAccessException("组件身份校验失败。");
                    return true;
                }
                if (!authenticated) throw new UnauthorizedAccessException("组件尚未认证。");
                return await handler(method,data);
            };
            rpc.Disconnected += error => { _ = rpc.DisposeAsync(); };
            rpc.Start();
        }
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
}
