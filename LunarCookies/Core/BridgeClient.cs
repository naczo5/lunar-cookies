using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LunarCookies.Core;

public sealed class BridgeSessionInfo
{
    public bool Ok { get; init; }
    public string Username { get; init; } = "";
    public string Uuid { get; init; } = "";
    public bool InWorld { get; init; }
    public bool Ready { get; init; }
    public string Error { get; init; } = "";
}

public sealed class BridgeJoinResult
{
    public bool Ok { get; init; }
    public string Address { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// TCP client for switcher.dll on port 25591. Line-delimited JSON request/response.
/// The handshake is tied to a target PID so a bridge in another Minecraft
/// process can never receive account credentials by mistake.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    public const int DefaultPort = 25591;

    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly StringBuilder _readBuf = new();

    public BridgeClient(int port = DefaultPort)
    {
        _port = port;
    }

    public bool IsConnected => _client?.Connected == true;

    public async Task<bool> ConnectAsync(
        int maxAttempts = 40,
        int delayMs = 250,
        CancellationToken ct = default,
        int? expectedProcessId = null)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Disconnect();
                var client = new TcpClient();
                var connectTask = client.ConnectAsync("127.0.0.1", _port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(500, ct)).ConfigureAwait(false);
                if (completed != connectTask || !client.Connected)
                {
                    client.Dispose();
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    continue;
                }

                client.NoDelay = true;
                _client = client;
                _stream = client.GetStream();
                _readBuf.Clear();

                var pong = await SendAsync(new { op = "ping" }, ct).ConfigureAwait(false);
                if (pong != null && pong["ok"]?.GetValue<bool>() == true)
                {
                    int processId = pong["processId"]?.GetValue<int>() ?? 0;
                    int protocol = pong["protocol"]?.GetValue<int>() ?? 0;
                    if (protocol >= 2
                        && (!expectedProcessId.HasValue || processId == expectedProcessId.Value))
                    {
                        return true;
                    }
                }

                Disconnect();
            }
            catch
            {
                Disconnect();
            }

            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<BridgeSessionInfo?> GetSessionAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new { op = "getSession" }, ct).ConfigureAwait(false);
        return ParseSession(resp);
    }

    public async Task<BridgeSessionInfo?> SetSessionAsync(string name, string uuid, string token, CancellationToken ct = default)
    {
        var resp = await SendAsync(new { op = "setSession", name, uuid, token }, ct).ConfigureAwait(false);
        return ParseSession(resp);
    }

    public async Task<BridgeSessionInfo?> RestoreSessionAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new { op = "restoreSession" }, ct).ConfigureAwait(false);
        return ParseSession(resp);
    }

    public async Task<BridgeJoinResult?> JoinServerAsync(string host, int port, CancellationToken ct = default)
    {
        var resp = await SendAsync(new { op = "joinServer", host, port }, ct).ConfigureAwait(false);
        if (resp == null)
            return null;

        return new BridgeJoinResult
        {
            Ok = resp["ok"]?.GetValue<bool>() ?? false,
            Address = resp["address"]?.GetValue<string>() ?? "",
            Error = resp["error"]?.GetValue<string>() ?? ""
        };
    }

    private static BridgeSessionInfo? ParseSession(JsonNode? resp)
    {
        if (resp == null)
            return null;

        return new BridgeSessionInfo
        {
            Ok = resp["ok"]?.GetValue<bool>() ?? false,
            Username = resp["username"]?.GetValue<string>() ?? "",
            Uuid = resp["uuid"]?.GetValue<string>() ?? "",
            InWorld = resp["inWorld"]?.GetValue<bool>() ?? false,
            Ready = resp["ready"]?.GetValue<bool>() ?? false,
            Error = resp["error"]?.GetValue<string>() ?? ""
        };
    }

    private async Task<JsonNode?> SendAsync(object payload, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream == null || _client?.Connected != true)
                return null;

            string line = JsonSerializer.Serialize(payload) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);

            string? responseLine = await ReadLineAsync(ct).ConfigureAwait(false);
            if (responseLine == null)
                return null;

            return JsonNode.Parse(responseLine);
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (_stream == null)
            return null;

        byte[] buf = new byte[4096];
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            string existing = _readBuf.ToString();
            int nl = existing.IndexOf('\n');
            if (nl >= 0)
            {
                string line = existing[..nl].TrimEnd('\r');
                _readBuf.Clear();
                if (nl + 1 < existing.Length)
                    _readBuf.Append(existing[(nl + 1)..]);
                return line;
            }

            if (!_stream.DataAvailable)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
                // Still try a blocking-ish read with short timeout via async
                if (_client?.Client.Poll(0, SelectMode.SelectRead) == true
                    || _stream.DataAvailable)
                {
                    // fall through to read
                }
                else
                {
                    continue;
                }
            }

            int n = await _stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
            if (n <= 0)
                return null;
            _readBuf.Append(Encoding.UTF8.GetString(buf, 0, n));
        }

        return null;
    }

    public void Disconnect()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _client = null;
        _readBuf.Clear();
    }

    public void Dispose()
    {
        Disconnect();
        _io.Dispose();
    }
}
