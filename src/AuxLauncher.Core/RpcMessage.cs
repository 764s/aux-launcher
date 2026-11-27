using System.Text.Json;

namespace AuxLauncher.Core;
public sealed record RpcMessage(long Id, long ReplyTo, string Method, JsonElement Data, string? Error = null);
