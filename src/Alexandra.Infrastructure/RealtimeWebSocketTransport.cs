using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Alexandra.Core;

namespace Alexandra.Infrastructure;

public sealed class RealtimeWebSocketTransport : IRealtimeTransport
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1);
    public bool IsConnected => _socket.State == WebSocketState.Open;
    public async Task ConnectAsync(string model, string key, CancellationToken cancellationToken)
    {
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model)), cancellationToken);
    }
    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken);
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, cancellationToken); }
        finally { _send.Release(); }
    }
    public async IAsyncEnumerable<string> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[32768];
        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            using var data = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                if (data.Length + result.Count > 8 * 1024 * 1024) throw new InvalidOperationException("Voice service returned an oversized event.");
                data.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Text) yield return Encoding.UTF8.GetString(data.ToArray());
        }
    }
    public ValueTask DisposeAsync()
    {
        _socket.Abort();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

