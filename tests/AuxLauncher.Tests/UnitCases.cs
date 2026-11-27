using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AuxLauncher.Core;

namespace AuxLauncher.Tests;
internal static class UnitCases
{
    public static async Task RunAsync(string directory,Func<string,Func<Task>,Task> run)
    {
        await run("history-mru-capacity-and-independent-selection",()=>{
            var dir=Path.Combine(directory,"history");Directory.CreateDirectory(dir);
            var files=Enumerable.Range(0,5).Select(i=>Path.Combine(dir,$"App{i}.exe")).ToArray();foreach(var f in files)File.WriteAllText(f,"fixture");
            var memory=new ProgramMemory();foreach(var f in files)memory.SelectNew(f);
            Check.Equal(4,memory.Settings.RecentPrograms.Length);Check.Equal(files[4],memory.Settings.SelectedProgram);
            memory.SelectHistory(files[1]);Check.Equal(files[1],memory.Settings.RecentPrograms[0]);Check.Equal(4,memory.Settings.RecentPrograms.Length);
            memory.Remove(files[1]);Check.True(memory.Settings.SelectedProgram is null);Check.Equal(3,memory.Settings.RecentPrograms.Length);
            var restored=new ProgramMemory(memory.Settings);Check.True(restored.Settings.SelectedProgram is null);
            return Task.CompletedTask;
        });
        await run("missing-history-and-invalid-new-selection",()=>{
            var dir=Path.Combine(directory,"paths");Directory.CreateDirectory(dir);var a=Path.Combine(dir,"A.exe");var b=Path.Combine(dir,"B.exe");
            File.WriteAllText(a,"fixture");File.WriteAllText(b,"fixture");var memory=new ProgramMemory();memory.SelectNew(a);memory.SelectNew(b);File.Delete(a);
            memory.SelectHistory(a);Check.Equal(LauncherState.Missing,StateRules.ForSelection(memory.Settings.SelectedProgram));
            Check.Throws<ArgumentException>(()=>memory.SelectNew(Path.Combine(dir,"bad.txt")));Check.Equal(a,memory.Settings.SelectedProgram);
            memory.Remove(a);memory.Remove(b);Check.Equal(LauncherState.Idle,StateRules.ForSelection(memory.Settings.SelectedProgram));
            return Task.CompletedTask;
        });
        await run("settings-atomic-save-and-user-secret-protection",async()=>{
            var dir=Path.Combine(directory,"settings");var store=new SettingsStore(dir);
            var state=new UserSettings{Proxy=new("127.0.0.1",10808,"user","not-plaintext-secret"),RecentPrograms=[@"C:\apps\A.exe"],SelectedProgram=@"C:\apps\A.exe"};
            await store.SaveAsync(state);var raw=File.ReadAllText(Path.Combine(dir,"settings.json"));
            Check.True(!raw.Contains("not-plaintext-secret"));var restored=store.Load();Check.Equal(state.Proxy,restored.Proxy);Check.Equal(state.SelectedProgram,restored.SelectedProgram);
            Check.Equal(0,Directory.GetFiles(dir,"*.tmp-*").Length);
        });
        await run("unknown-settings-version-preserved",async()=>{
            var dir=Path.Combine(directory,"future");Directory.CreateDirectory(dir);var file=Path.Combine(dir,"settings.json");File.WriteAllText(file,"{\"Version\":999,\"Keep\":\"yes\"}");
            var store=new SettingsStore(dir);store.Load();Check.True(store.ReadOnly);
            await Check.ThrowsAsync<IOException>(()=>store.SaveAsync(new()));Check.True(File.ReadAllText(file).Contains("999"));
        });
        await run("corrupt-settings-quarantined",()=>{
            var dir=Path.Combine(directory,"corrupt");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"settings.json"),"{invalid");
            var store=new SettingsStore(dir);Check.True(store.Load().SelectedProgram is null);Check.True(Directory.GetFiles(dir,"*.corrupt-*").Length>0);return Task.CompletedTask;
        });
        await run("all-nine-states-lock-rules",()=>{
            var locked=new[]{LauncherState.Preparing,LauncherState.Running,LauncherState.ProxyLost,LauncherState.ProtectionInterrupted,LauncherState.Recovering};
            Check.Equal(9,Enum.GetValues<LauncherState>().Length);
            foreach(var state in Enum.GetValues<LauncherState>())Check.Equal(locked.Contains(state),state.IsLocked());
            return Task.CompletedTask;
        });
        await run("local-proxy-address-validation",()=>{
            new ProxyEndpoint("localhost",7890).Validate();new ProxyEndpoint("::1",1080).Validate();
            Check.Throws<ArgumentException>(()=>new ProxyEndpoint("8.8.8.8",1080).Validate());Check.Throws<ArgumentException>(()=>new ProxyEndpoint("127.0.0.1",0).Validate());return Task.CompletedTask;
        });
        await run("socks-udp-ipv4-ipv6-and-empty-datagrams",()=>{
            foreach(var ip in new[]{IPAddress.Parse("198.18.0.42"),IPAddress.Parse("2001:db8::42")})
            foreach(var payload in new[]{Array.Empty<byte>(),Encoding.UTF8.GetBytes("hello")})
            {
                var bytes=Socks5Client.EncodeUdp(ip,5353,payload);var result=Socks5Client.DecodeUdp(bytes);
                Check.Equal(ip,result.Source.Address);Check.Equal(5353,result.Source.Port);Check.True(result.Payload.SequenceEqual(payload));
                bytes[2]=1;Check.Throws<InvalidDataException>(()=>Socks5Client.DecodeUdp(bytes));
            }
            return Task.CompletedTask;
        });
        await run("packet-nat-roundtrip-and-tcp-reset",()=>{
            foreach(var pair in new[]{(IPAddress.Parse("192.0.2.10"),IPAddress.Parse("198.18.0.42")),(IPAddress.Parse("2001:db8::10"),IPAddress.Parse("2001:db8::42"))})
            {
                var raw=PacketCodec.Tcp(pair.Item1,42000,pair.Item2,443,1234,0,2);
                var packet=PacketView.Parse(raw)!;var nat=PacketCodec.Rewrite(packet,pair.Item2,19001,pair.Item1,35000);
                var restored=PacketCodec.Rewrite(PacketView.Parse(nat)!,pair.Item1,42000,pair.Item2,443);
                Check.True(raw.SequenceEqual(restored));var rst=PacketView.Parse(PacketCodec.ResetFor(packet))!;
                Check.Equal(0x14,(int)rst.TcpFlags);Check.Equal((uint)1235,BinaryPrimitives.ReadUInt32BigEndian(rst.Data.AsSpan(rst.TransportOffset+8)));
                if(!packet.IPv6)Check.Equal((ushort)0,PacketCodec.Checksum(raw.AsSpan(0,20)));
            }
            return Task.CompletedTask;
        });
        await run("udp-packet-payload-and-checksum",()=>{
            var bytes=PacketCodec.Udp(IPAddress.Parse("2001:db8::1"),4000,IPAddress.Parse("2001:db8::2"),53,Encoding.UTF8.GetBytes("dns"));
            var packet=PacketView.Parse(bytes)!;Check.Equal(53,packet.DestinationPort);Check.Equal("dns",Encoding.UTF8.GetString(packet.UdpPayload));
            var copy=bytes.ToArray();PacketCodec.UpdateChecksums(copy);Check.True(copy.SequenceEqual(bytes));return Task.CompletedTask;
        });
        await run("windivert-address-abi",()=>{Check.Equal(80,Marshal.SizeOf<DivertAddress>());var address=DivertAddress.Network(7,9,true);Check.True(address.Outbound);address.Outbound=false;Check.True(!address.Outbound);return Task.CompletedTask;});
        await run("native-endpoint-owner-tcp-and-udp",async()=>{
            await using var echo=new EchoServers();using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,echo.TcpPort);
            var local=(IPEndPoint)client.Client.LocalEndPoint!;
            var localAddress=local.Address.IsIPv4MappedToIPv6?local.Address.MapToIPv4():local.Address;
            var packet=PacketView.Parse(PacketCodec.Tcp(localAddress,local.Port,IPAddress.Loopback,echo.TcpPort,1,0,2))!;
            Check.True(IpOwnerTable.Find(packet).Contains(Environment.ProcessId),"TCP 所有者未匹配");
            using var udp=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));local=(IPEndPoint)udp.Client.LocalEndPoint!;
            packet=PacketView.Parse(PacketCodec.Udp(local.Address,local.Port,IPAddress.Parse("198.18.0.42"),53,[1,2]))!;
            Check.True(IpOwnerTable.Find(packet).Contains(Environment.ProcessId),"UDP 所有者未匹配");
        });
        await run("socks5-tcp-udp-through-real-loopback-servers",async()=>{
            await using var echo=new EchoServers();await using var proxy=new TestSocksServer(dnsPort:echo.DnsPort);
            using var timeout=new CancellationTokenSource(5000);
            var probe=await Socks5Client.ProbeAsync(proxy.Endpoint,timeout.Token);Check.True(probe.Available&&probe.UdpAvailable);
            using var client=await Socks5Client.ConnectAsync(proxy.Endpoint,IPAddress.Parse("198.18.0.42"),echo.TcpPort,timeout.Token);
            var payload=Encoding.UTF8.GetBytes("actual tcp payload");await client.GetStream().WriteAsync(payload,timeout.Token);var reply=new byte[payload.Length];await client.GetStream().ReadExactlyAsync(reply,timeout.Token);Check.True(reply.SequenceEqual(payload));
            var association=await Socks5Client.AssociateAsync(proxy.Endpoint,timeout.Token);using var control=association.Control;using var udp=new UdpClient();
            await udp.SendAsync(Socks5Client.EncodeUdp(IPAddress.Parse("198.18.0.42"),echo.UdpPort,payload),association.Relay,timeout.Token);
            var received=await udp.ReceiveAsync(timeout.Token);Check.True(Socks5Client.DecodeUdp(received.Buffer).Payload.SequenceEqual(payload));
        });
        await run("native-endpoint-owner-ipv6-udp",async()=>{
            using var udp=new UdpClient(new IPEndPoint(IPAddress.IPv6Any,0));
            var port=((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            await udp.SendAsync(new byte[]{1,2},new IPEndPoint(IPAddress.IPv6Loopback,49100));
            var packet=PacketView.Parse(PacketCodec.Udp(IPAddress.IPv6Loopback,port,IPAddress.IPv6Loopback,49100,new byte[]{1,2}))!;
            var owners=IpOwnerTable.Find(packet);Check.True(owners.Contains(Environment.ProcessId),"IPv6 UDP owner="+string.Join(",",owners)+", expected="+Environment.ProcessId);
        });
        await run("socks-auth-required-success-and-rejection",async()=>{
            await using var proxy=new TestSocksServer(authentication:true);
            Check.True((await Socks5Client.ProbeAsync(new("127.0.0.1",proxy.Port))).RequiresAuthentication);
            Check.True((await Socks5Client.ProbeAsync(proxy.Endpoint)).Available);
            Check.True(!(await Socks5Client.ProbeAsync(proxy.Endpoint with{Password="wrong"})).Available);
        });
        await run("named-pipe-authentication-and-concurrent-rpc",async()=>{
            var name="AuxLauncher.Tests."+Guid.NewGuid().ToString("N");using var stop=new CancellationTokenSource();
            var serving=LocalPipe.ServeAsync(name,LocalPipe.UserSid,"key",(rpc,pid)=>async(method,data)=>{await Task.Yield();return method=="double"?data.GetInt32()*2:throw new InvalidOperationException();},stop.Token);
            await using var rpc=await LocalPipe.ConnectAsync(name,"key");
            var values=await Task.WhenAll(Enumerable.Range(0,40).Select(x=>rpc.CallAsync<int>("double",x)));
            for(var i=0;i<values.Length;i++)Check.Equal(i*2,values[i]);
            await Check.ThrowsAsync<IOException>(async()=>{await using var wrong=await LocalPipe.ConnectAsync(name,"wrong");});
            stop.Cancel();try{await serving;}catch(OperationCanceledException){}
        });
        await run("ordinary-process-suspended-start-and-parent-exit",async()=>{
            var fixture=FindFixture();var output=Path.Combine(directory,"child.json");
            using var job=new SessionJob();using var process=WindowsProcess.CreateSuspended(fixture,"--mode spawn --output \""+output+"\"");
            Check.True(!WindowsProcess.IsElevated(process.Process));job.Add(process.Process);Check.Equal(1,job.Members().Count);
            WindowsProcess.Resume(process.Thread);
            await WaitAsync(()=>File.Exists(output),5000);
            await WaitAsync(()=>WindowsProcess.HasExited(process.Process),5000);
            Check.True(job.Members().Count>0,"父进程退出后子进程必须仍在 Job 内");
            await WaitAsync(()=>job.Members().Count==0,5000);
        });
    }
    public static string FindFixture()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"AuxLauncher.sln")))return Path.Combine(d.FullName,"tests","AuxLauncher.Fixture","bin","Release","net8.0-windows","AuxLauncher.Fixture.exe");
        throw new FileNotFoundException("测试程序未构建。");
    }
    public static async Task WaitAsync(Func<bool> predicate,int timeout)
    {
        var end=DateTime.UtcNow.AddMilliseconds(timeout);
        while(DateTime.UtcNow<end){if(predicate())return;await Task.Delay(30);}
        throw new TimeoutException("等待测试条件超时。");
    }
}

