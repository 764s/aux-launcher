namespace AuxLauncher.Core;
public static class Arguments
{
    public static Dictionary<string,string> Parse(string[] args)
    {
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for (var i=0;i<args.Length;i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key=args[i][2..]; values[key]=i+1<args.Length&&!args[i+1].StartsWith("--")?args[++i]:"true";
        }
        return values;
    }
    public static string Required(this IReadOnlyDictionary<string,string> args,string key) => args.TryGetValue(key,out var value)?value:throw new ArgumentException("缺少参数："+key);
}
