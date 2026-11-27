using System.Text.Json;
using System.Text.Json.Nodes;

namespace AuxLauncher.Core;
public sealed class RuntimeStore(string directory)
{
    private readonly string _path=Path.Combine(directory,"active-session.json");
    public void Save(RuntimeSession session)
    {
        Directory.CreateDirectory(directory);
        var root=JsonSerializer.SerializeToNode(session with {Key="",Proxy=session.Proxy is null?null:session.Proxy with{Password=null}})!.AsObject();
        root["ProtectedKey"]=SecretProtection.Protect(session.Key);
        if(!string.IsNullOrEmpty(session.Proxy?.Password))root["ProtectedProxyPassword"]=SecretProtection.Protect(session.Proxy.Password);
        var temp=_path+".tmp";File.WriteAllText(temp,root.ToJsonString());File.Move(temp,_path,true);
    }
    public RuntimeSession? Load()
    {
        if(!File.Exists(_path))return null;
        try
        {
            var root=JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
            var key=SecretProtection.Unprotect(root["ProtectedKey"]!.GetValue<string>());
            var password=root["ProtectedProxyPassword"]?.GetValue<string>();
            root.Remove("ProtectedKey");
            root.Remove("ProtectedProxyPassword");
            var session=root.Deserialize<RuntimeSession>()! with {Key=key};
            if(session.Proxy is not null&&!string.IsNullOrEmpty(password))session=session with {Proxy=session.Proxy with {Password=SecretProtection.Unprotect(password)}};
            return session;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or FormatException or System.ComponentModel.Win32Exception or InvalidOperationException){return null;}
    }
    public void Clear(){if(File.Exists(_path))File.Delete(_path);}
}
