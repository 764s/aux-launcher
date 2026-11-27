namespace AuxLauncher.Tests;
internal static class Check
{
    public static void True(bool condition,string message="断言失败"){if(!condition)throw new InvalidOperationException(message);}
    public static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new InvalidOperationException($"预期 {expected}，实际 {actual}");}
    public static void Throws<T>(Action action)where T:Exception{try{action();}catch(T){return;}throw new InvalidOperationException("未抛出 "+typeof(T).Name);}
    public static async Task ThrowsAsync<T>(Func<Task> action)where T:Exception{try{await action();}catch(T){return;}throw new InvalidOperationException("未抛出 "+typeof(T).Name);}
}

