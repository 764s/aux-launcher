using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace AuxLauncher.Tests;
internal static class ReleaseAudit
{
    private static readonly Regex UserPath=new(@"(?i)(?:[a-z]:[\\/]+Users[\\/]+|/(?:home|Users)/)[^\\/\s\x00]+",RegexOptions.Compiled);
    private static readonly Regex Credential=new(@"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{24,}|-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----)",RegexOptions.Compiled);
    public static int Verify(string directory)
    {
        var root=Path.GetFullPath(directory);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("发布目录不存在。");
        var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var count=0;
        foreach(var file in Directory.EnumerateFileSystemEntries(root,"*",SearchOption.AllDirectories))
        {
            var attributes=File.GetAttributes(file);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("发布目录不能包含重解析点。");
            if((attributes&FileAttributes.Directory)!=0)continue;
            var name=Path.GetFileName(file);var extension=Path.GetExtension(file).ToLowerInvariant();
            if(extension is ".pdb" or ".jsonl" or ".pfx" or ".p12" or ".pem" or ".key" || name.StartsWith("settings.json",StringComparison.OrdinalIgnoreCase) || name.StartsWith("active-session",StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布包包含调试文件或个人运行数据："+name);
            var bytes=File.ReadAllBytes(file);
            foreach(var encoding in new[]{Encoding.UTF8,Encoding.Unicode})
            {
                var text=encoding.GetString(bytes);
                if(UserPath.IsMatch(text)||(!string.IsNullOrEmpty(profile)&&text.Contains(profile,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("发布文件包含本机用户路径："+name);
                if(Credential.IsMatch(text))throw new InvalidDataException("发布文件包含疑似凭据："+name);
            }
            if(name.StartsWith("AuxLauncher",StringComparison.Ordinal)&&extension==".dll")
            {
                using var stream=File.OpenRead(file);using var pe=new PEReader(stream);
                if(pe.ReadDebugDirectory().Any(x=>x.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.EmbeddedPortablePdb))
                    throw new InvalidDataException("发布程序集仍包含调试符号引用："+name);
            }
            count++;
        }
        if(count==0)throw new InvalidDataException("发布目录为空。");
        return count;
    }
}
