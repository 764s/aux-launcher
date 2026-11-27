using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AuxLauncher.Core;

var options=Arguments.Parse(args);
var mode=options.GetValueOrDefault("mode","hold");
if(mode=="hold"){if(options.TryGetValue("output",out var ready))File.WriteAllText(ready,JsonSerializer.Serialize(new{Pid=Environment.ProcessId,Elevated=LocalPipe.IsElevated}));await Task.Delay(int.Parse(options.GetValueOrDefault("ms","1500")));return;}
if(mode=="spawn")
{
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
    start.ArgumentList.Add("--mode");start.ArgumentList.Add("hold");start.ArgumentList.Add("--ms");start.ArgumentList.Add("1800");
    using var child=Process.Start(start)!;File.WriteAllText(options.Required("output"),JsonSerializer.Serialize(new{Parent=Environment.ProcessId,Child=child.Id}));return;
}
var directory=options.Required("commands");Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory,"ready.json"),JsonSerializer.Serialize(new{Pid=Environment.ProcessId,Elevated=LocalPipe.IsElevated,Sid=LocalPipe.UserSid}));
var seen=new HashSet<string>();
while(true)
{
    foreach(var file in Directory.EnumerateFiles(directory,"request-*.json"))
    {
        if(!seen.Add(file))continue;
        FixtureRequest request;
        try{request=JsonSerializer.Deserialize<FixtureRequest>(File.ReadAllText(file))!;}catch{seen.Remove(file);continue;}
        var response=Path.Combine(directory,"response-"+request.Id+".json");
        var exit=false;object result;
        try
        {
            using var timeout=new CancellationTokenSource(request.TimeoutMs);
            if(request.Kind=="exit"){result=new{Success=true};exit=true;}
            else if(request.Kind=="child")
            {
                var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                foreach(var item in new[]{"--mode","client","--commands",request.Directory!})info.ArgumentList.Add(item);
                using var child=Process.Start(info)!;result=new{Success=true,Child=child.Id};exit=true;
            }
            else if(request.Kind=="tcp")
            {
                using var client=new TcpClient();await client.ConnectAsync(IPAddress.Parse(request.Host),request.Port,timeout.Token);
                var bytes=Encoding.UTF8.GetBytes(request.Payload);await client.GetStream().WriteAsync(bytes,timeout.Token);
                var reply=new byte[bytes.Length];await client.GetStream().ReadExactlyAsync(reply,timeout.Token);
                result=new{Success=reply.SequenceEqual(bytes),Reply=Encoding.UTF8.GetString(reply),Elevated=LocalPipe.IsElevated};
            }
            else
            {
                var ip=IPAddress.Parse(request.Host);using var udp=new UdpClient(ip.AddressFamily);
                var bytes=request.Kind=="dns"?DnsQuestion():Encoding.UTF8.GetBytes(request.Payload);
                await udp.SendAsync(bytes,new IPEndPoint(ip,request.Port),timeout.Token);
                var reply=await udp.ReceiveAsync(timeout.Token);
                var success=request.Kind=="dns"?reply.Buffer.Length>=bytes.Length+16&&(reply.Buffer[2]&0x80)!=0:reply.Buffer.SequenceEqual(bytes);
                result=new{Success=success,Source=reply.RemoteEndPoint.ToString(),Reply=Convert.ToBase64String(reply.Buffer),Elevated=LocalPipe.IsElevated};
            }
        }
        catch(Exception e){result=new{Success=false,Error=e.Message,Elevated=LocalPipe.IsElevated};}
        var tmp=response+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(result));File.Move(tmp,response,true);
        if(exit)return;
    }
    await Task.Delay(30);
}
static byte[] DnsQuestion()=>[0x12,0x34,1,0,0,1,0,0,0,0,0,0,4,(byte)'t',(byte)'e',(byte)'s',(byte)'t',7,(byte)'e',(byte)'x',(byte)'a',(byte)'m',(byte)'p',(byte)'l',(byte)'e',0,0,1,0,1];

