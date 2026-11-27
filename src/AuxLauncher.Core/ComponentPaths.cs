namespace AuxLauncher.Core;
public static class ComponentPaths
{
    public static string Resolve(string name)
    {
        var deployed=Path.Combine(AppContext.BaseDirectory,name+".exe");
        if(File.Exists(deployed))return deployed;
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            if(!File.Exists(Path.Combine(directory.FullName,"AuxLauncher.sln")))continue;
            foreach(var config in new[]{"Release","Debug"})
            {
                var file=Path.Combine(directory.FullName,"src",name,"bin",config,"net8.0-windows",name+".exe");
                if(File.Exists(file))return file;
            }
        }
        throw new FileNotFoundException("组件文件缺失，请先构建完整解决方案或使用完整发布目录。",name+".exe");
    }
}
