using System;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Nowring;

/// <summary>Минимальный клиент Discord IPC (named pipe), без сторонних библиотек.</summary>
sealed class DiscordIpc
{
    NamedPipeClientStream? _pipe;
    readonly SemaphoreSlim _write = new(1, 1);
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public event Action? Disconnected;

    /// <summary>Discord отклонил статус (например, из-за кнопки или ссылки).</summary>
    public event Action<string>? ActivityError;
    public bool Connected => _pipe is { IsConnected: true };

    public async Task<bool> ConnectAsync(string clientId)
    {
        Close();
        for (int i = 0; i < 10; i++)
        {
            var p = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await p.ConnectAsync(250);
                _pipe = p;
                await SendAsync(p, 0, JsonSerializer.Serialize(new { v = 1, client_id = clientId }));
                var (op, _) = await ReadAsync(p);
                if (op == 2) { Close(); return false; }   // неверный Application ID
                _ = Task.Run(() => ReadLoop(p));
                return true;
            }
            catch
            {
                p.Dispose();
                if (ReferenceEquals(_pipe, p)) _pipe = null;
            }
        }
        return false;
    }

    public Task SetActivityAsync(object? activity)
    {
        var p = _pipe ?? throw new InvalidOperationException("not connected");
        var json = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString()
        }, Json);
        return SendAsync(p, 1, json);
    }

    async Task ReadLoop(NamedPipeClientStream p)
    {
        try
        {
            while (true)
            {
                var (op, payload) = await ReadAsync(p);
                if (op == 3) await SendAsync(p, 4, payload);   // PING -> PONG
                else if (op == 2) break;                       // CLOSE
                else if (op == 1 && IsError(payload)) ActivityError?.Invoke(payload);
            }
        }
        catch { }
        if (ReferenceEquals(_pipe, p))
        {
            Close();
            Disconnected?.Invoke();
        }
    }

    static bool IsError(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("evt", out var e)
                   && e.ValueKind == JsonValueKind.String && e.GetString() == "ERROR";
        }
        catch { return false; }
    }

    async Task SendAsync(NamedPipeClientStream p, int op, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + payload.Length];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 4), payload.Length);
        payload.CopyTo(buf, 8);
        await _write.WaitAsync();
        try
        {
            await p.WriteAsync(buf);
            await p.FlushAsync();
        }
        finally { _write.Release(); }
    }

    static async Task<(int, string)> ReadAsync(NamedPipeClientStream p)
    {
        var head = new byte[8];
        await p.ReadExactlyAsync(head);
        int op = BitConverter.ToInt32(head, 0);
        int len = BitConverter.ToInt32(head, 4);
        if (len < 0 || len > 1 << 20) throw new InvalidOperationException("bad frame");
        var body = new byte[len];
        await p.ReadExactlyAsync(body);
        return (op, Encoding.UTF8.GetString(body));
    }

    public void Close()
    {
        var p = _pipe;
        _pipe = null;
        try { p?.Dispose(); } catch { }
    }
}
