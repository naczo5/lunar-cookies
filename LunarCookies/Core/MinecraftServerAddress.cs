using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LunarCookies.Core;

public readonly record struct MinecraftServerAddress(string Host, int Port, bool PortSpecified)
{
    public const int DefaultPort = 25565;

    public string Display => PortSpecified || Port != DefaultPort ? $"{FormatHost(Host)}:{Port}" : Host;

    public static bool TryParse(string? text, out MinecraftServerAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string value = text.Trim();
        if (value.Length > 256)
            return false;

        string host;
        int port = DefaultPort;
        bool portSpecified = false;

        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close <= 1)
                return false;
            host = value[1..close];
            string rest = value[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':') || !TryParsePort(rest[1..], out port))
                    return false;
                portSpecified = true;
            }
        }
        else
        {
            int colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(':') == colon && colon < value.Length - 1
                && !value.Contains('/'))
            {
                host = value[..colon];
                if (!TryParsePort(value[(colon + 1)..], out port))
                    return false;
                portSpecified = true;
            }
            else
            {
                host = value;
            }
        }

        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(' '))
            return false;

        address = new MinecraftServerAddress(host, port, portSpecified);
        return true;
    }

    public static async Task<MinecraftServerAddress> ResolveForPingAsync(
        string text, CancellationToken ct = default)
    {
        if (!TryParse(text, out MinecraftServerAddress parsed))
            throw new ArgumentException("Enter a server address as host or host:port.", nameof(text));
        if (parsed.PortSpecified)
            return parsed;

        MinecraftServerAddress? srv = await TrySrvAsync(parsed.Host, ct).ConfigureAwait(false);
        return srv ?? parsed;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65535;

    private static string FormatHost(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static async Task<MinecraftServerAddress?> TrySrvAsync(string host, CancellationToken ct)
    {
        try
        {
            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 2000;
            client.Client.SendTimeout = 2000;
            byte[] query = BuildSrvQuery($"_minecraft._tcp.{host.TrimEnd('.')}");
            await client.SendAsync(query, query.Length, "8.8.8.8", 53)
                .WaitAsync(ct).ConfigureAwait(false);

            UdpReceiveResult result = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2), ct)
                .ConfigureAwait(false);
            if (TryReadSrv(result.Buffer, out string target, out int port)
                && !string.IsNullOrWhiteSpace(target) && target != ".")
            {
                return new MinecraftServerAddress(target.TrimEnd('.'), port, true);
            }
        }
        catch
        {
            // SRV is optional; ping and join still use the explicit host.
        }

        return null;
    }

    private static byte[] BuildSrvQuery(string name)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)0x1C0D));
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)0x0100));
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
        writer.Write((byte)0);
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)33));
        writer.Write((ushort)IPAddress.HostToNetworkOrder((short)1));
        return stream.ToArray();
    }

    private static bool TryReadSrv(byte[] buffer, out string target, out int port)
    {
        target = "";
        port = DefaultPort;
        if (buffer.Length < 12)
            return false;

        int questionCount = (buffer[4] << 8) | buffer[5];
        int answerCount = (buffer[6] << 8) | buffer[7];
        int offset = 12;
        for (int i = 0; i < questionCount; i++)
        {
            if (!SkipName(buffer, ref offset))
                return false;
            offset += 4;
        }

        for (int i = 0; i < answerCount; i++)
        {
            if (!SkipName(buffer, ref offset) || offset + 10 > buffer.Length)
                return false;
            int type = (buffer[offset] << 8) | buffer[offset + 1];
            int length = (buffer[offset + 8] << 8) | buffer[offset + 9];
            offset += 10;
            if (offset + length > buffer.Length)
                return false;
            if (type == 33 && length >= 6)
            {
                port = (buffer[offset + 4] << 8) | buffer[offset + 5];
                int nameOffset = offset + 6;
                if (TryReadName(buffer, ref nameOffset, out target) && port is >= 1 and <= 65535)
                    return true;
            }
            offset += length;
        }

        return false;
    }

    private static bool SkipName(byte[] buffer, ref int offset)
    {
        int hops = 0;
        while (offset < buffer.Length && hops++ < 32)
        {
            byte len = buffer[offset];
            if (len == 0)
            {
                offset++;
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                offset += 2;
                return true;
            }
            offset += 1 + len;
        }
        return false;
    }

    private static bool TryReadName(byte[] buffer, ref int offset, out string name)
    {
        var labels = new List<string>();
        int hops = 0;
        int end = offset;
        bool jumped = false;
        while (offset < buffer.Length && hops++ < 32)
        {
            byte len = buffer[offset];
            if (len == 0)
            {
                if (!jumped)
                    end = offset + 1;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= buffer.Length)
                    break;
                if (!jumped)
                    end = offset + 2;
                offset = ((len & 0x3F) << 8) | buffer[offset + 1];
                jumped = true;
                continue;
            }
            offset++;
            if (offset + len > buffer.Length)
                break;
            labels.Add(Encoding.ASCII.GetString(buffer, offset, len));
            offset += len;
        }

        offset = end;
        name = string.Join('.', labels);
        return labels.Count > 0;
    }
}
