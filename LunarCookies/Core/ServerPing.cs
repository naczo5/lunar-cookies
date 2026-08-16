using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LunarCookies.Core;

public sealed class ServerPingResult
{
    public bool Online { get; init; }
    public int LatencyMs { get; init; }
    public string Description { get; init; } = "";
    public int OnlinePlayers { get; init; }
    public int MaxPlayers { get; init; }
    public string Version { get; init; } = "";
    public string Error { get; init; } = "";

    public string StatusLabel => Online
        ? $"{LatencyMs} ms"
        : string.IsNullOrEmpty(Error) ? "Offline" : Error;

    public string PlayersLabel => Online ? $"{OnlinePlayers}/{MaxPlayers}" : "—";
}

public static class ServerPing
{
    private static readonly Regex ColorCodes = new(@"§[0-9A-FK-ORa-fk-or]", RegexOptions.Compiled);

    public static async Task<ServerPingResult> QueryAsync(string addressText, CancellationToken ct = default)
    {
        MinecraftServerAddress address;
        try
        {
            address = await MinecraftServerAddress.ResolveForPingAsync(addressText, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ServerPingResult { Error = ex.Message };
        }

        try
        {
            using var client = new TcpClient { NoDelay = true, ReceiveTimeout = 4000, SendTimeout = 4000 };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(address.Host, address.Port, connectCts.Token).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();

            await WritePacketAsync(stream, BuildHandshake(address.Host, address.Port), ct).ConfigureAwait(false);
            await WritePacketAsync(stream, new byte[] { 0x00 }, ct).ConfigureAwait(false);

            byte[] payload = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            sw.Stop();
            if (payload.Length < 1 || payload[0] != 0x00)
                return new ServerPingResult { Error = "Unexpected ping response." };

            int offset = 1;
            string json = ReadString(payload, ref offset);
            return ParseStatus(json, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return new ServerPingResult { Error = ct.IsCancellationRequested ? "Cancelled" : "Timed out" };
        }
        catch (SocketException)
        {
            return new ServerPingResult { Error = "Offline" };
        }
        catch (Exception ex)
        {
            return new ServerPingResult { Error = ex.GetType().Name };
        }
    }

    public static ServerPingResult ParseStatus(string json, int latencyMs)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string description = root.TryGetProperty("description", out JsonElement desc)
            ? FlattenMotd(desc)
            : "";
        int online = 0;
        int max = 0;
        if (root.TryGetProperty("players", out JsonElement players))
        {
            online = players.TryGetProperty("online", out JsonElement onlineEl) ? onlineEl.GetInt32() : 0;
            max = players.TryGetProperty("max", out JsonElement maxEl) ? maxEl.GetInt32() : 0;
        }

        string version = "";
        if (root.TryGetProperty("version", out JsonElement versionEl)
            && versionEl.TryGetProperty("name", out JsonElement nameEl))
        {
            version = nameEl.GetString() ?? "";
        }

        return new ServerPingResult
        {
            Online = true,
            LatencyMs = latencyMs,
            Description = description,
            OnlinePlayers = online,
            MaxPlayers = max,
            Version = version
        };
    }

    public static string FlattenMotd(JsonElement element)
    {
        var text = new StringBuilder();
        AppendMotd(element, text);
        return ColorCodes.Replace(text.ToString(), "").Trim();
    }

    private static void AppendMotd(JsonElement element, StringBuilder text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text.Append(element.GetString());
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out JsonElement inner))
                    text.Append(inner.GetString());
                if (element.TryGetProperty("extra", out JsonElement extra)
                    && extra.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement child in extra.EnumerateArray())
                        AppendMotd(child, text);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                    AppendMotd(child, text);
                break;
        }
    }

    private static byte[] BuildHandshake(string host, int port)
    {
        using var stream = new MemoryStream();
        WriteVarInt(stream, 0x00);
        WriteVarInt(stream, 767);
        WriteString(stream, host);
        Span<byte> portBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        stream.Write(portBytes);
        WriteVarInt(stream, 1);
        return stream.ToArray();
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte[] packet, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        WriteVarInt(buffer, packet.Length);
        buffer.Write(packet);
        byte[] framed = buffer.ToArray();
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        int length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length is < 1 or > 32767)
            throw new InvalidOperationException("Invalid ping packet length.");
        byte[] payload = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await stream.ReadAsync(payload.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException();
            read += n;
        }
        return payload;
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken ct)
    {
        int value = 0;
        int position = 0;
        byte[] one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException();
            byte current = one[0];
            value |= (current & 0x7F) << position;
            if ((current & 0x80) == 0)
                return value;
            position += 7;
            if (position >= 32)
                throw new InvalidOperationException("VarInt too long.");
        }
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        uint remaining = (uint)value;
        while (true)
        {
            if ((remaining & ~0x7Fu) == 0)
            {
                stream.WriteByte((byte)remaining);
                return;
            }
            stream.WriteByte((byte)((remaining & 0x7F) | 0x80));
            remaining >>= 7;
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static string ReadString(byte[] buffer, ref int offset)
    {
        int length = ReadVarInt(buffer, ref offset);
        if (length < 0 || offset + length > buffer.Length)
            throw new InvalidOperationException("Invalid ping string.");
        string value = Encoding.UTF8.GetString(buffer, offset, length);
        offset += length;
        return value;
    }

    private static int ReadVarInt(byte[] buffer, ref int offset)
    {
        int value = 0;
        int position = 0;
        while (offset < buffer.Length)
        {
            byte current = buffer[offset++];
            value |= (current & 0x7F) << position;
            if ((current & 0x80) == 0)
                return value;
            position += 7;
            if (position >= 32)
                throw new InvalidOperationException("VarInt too long.");
        }
        throw new EndOfStreamException();
    }
}
