using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace AuxLauncher.Core;

public sealed class RpcConnection : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly PipeStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _next;
    public Func<string, JsonElement, Task<object?>>? Handler { get; set; }
    public event Action<Exception?>? Disconnected;
    public bool IsConnected => !_stop.IsCancellationRequested && _pipe.IsConnected;
    public RpcConnection(PipeStream pipe) => _pipe = pipe;
    public void Start() => _ = ReadLoopAsync();

    public async Task<T> CallAsync<T>(string method, object? data = null, CancellationToken token = default, int timeoutSeconds = 15)
    {
        var id = Interlocked.Increment(ref _next);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await SendAsync(new(id, 0, method, JsonSerializer.SerializeToElement(data, Json)), timeout.Token);
            var result = await waiter.Task.WaitAsync(timeout.Token);
            return result.Deserialize<T>(Json)!;
        }
        finally { _pending.TryRemove(id, out _); }
    }
    public Task NotifyAsync(string method, object? data = null) => SendAsync(new(0, 0, method, JsonSerializer.SerializeToElement(data, Json)), _stop.Token);
    private async Task SendAsync(RpcMessage message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("IPC 消息过大。");
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await _writeLock.WaitAsync(token);
        try { await _pipe.WriteAsync(length, token); await _pipe.WriteAsync(bytes, token); await _pipe.FlushAsync(token); }
        finally { _writeLock.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            var prefix = new byte[4];
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.ReadExactlyAsync(prefix, _stop.Token);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is < 1 or > 2 * 1024 * 1024) throw new InvalidDataException("IPC 帧长度无效。");
                var data = new byte[length]; await _pipe.ReadExactlyAsync(data, _stop.Token);
                var message = JsonSerializer.Deserialize<RpcMessage>(data, Json) ?? throw new InvalidDataException("IPC 消息为空。");
                if (message.ReplyTo != 0)
                {
                    if (_pending.TryRemove(message.ReplyTo, out var tcs))
                    {
                        if (message.Error is not null) tcs.TrySetException(new IOException(message.Error));
                        else tcs.TrySetResult(message.Data);
                    }
                }
                else _ = DispatchAsync(message);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException) { failure = e; }
        finally
        {
            _stop.Cancel();
            foreach (var item in _pending.Values) item.TrySetException(new IOException("组件连接已断开。", failure));
            _pending.Clear(); Disconnected?.Invoke(failure);
        }
    }
    private async Task DispatchAsync(RpcMessage message)
    {
        object? result = null; string? error = null;
        try { result = Handler is null ? null : await Handler(message.Method, message.Data); }
        catch (Exception e) { error = e.Message; }
        if (message.Id == 0) return;
        try { await SendAsync(new(0, message.Id, message.Method, JsonSerializer.SerializeToElement(result, Json), error), _stop.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _pipe.DisposeAsync(); }
}
