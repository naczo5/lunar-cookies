using System.IO;

namespace LunarCookies.Core;

public sealed class CookieEntry
{
    public CookieEntry(string domain, string path, bool secure, string name, string value)
    {
        Domain = domain;
        Path = path;
        Secure = secure;
        Name = name;
        Value = value;
    }

    public string Domain { get; }
    public string Path { get; }
    public bool Secure { get; }
    public string Name { get; }
    public string Value { get; }

    public string Pair() => $"{Name}={Value}";
}

public sealed class ParsedCookies
{
    public ParsedCookies(IReadOnlyDictionary<string, CookieEntry> cookies, string? refreshToken = null, string? accessToken = null)
    {
        Cookies = cookies;
        RefreshToken = refreshToken ?? string.Empty;
        AccessToken = accessToken ?? string.Empty;
    }

    public IReadOnlyDictionary<string, CookieEntry> Cookies { get; }
    public string RefreshToken { get; }
    public string AccessToken { get; }

    public string ToSisuCookieHeader()
    {
        var byName = new Dictionary<string, CookieEntry>(StringComparer.Ordinal);
        foreach (var entry in Cookies.Values)
        {
            if (!entry.Domain.EndsWith("login.live.com", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!byName.ContainsKey(entry.Name))
                byName[entry.Name] = entry;
        }

        if (byName.Count == 0)
            throw new CookieAuthException("Cookie file has no login.live.com cookies for SISU auth.");

        return string.Join("; ", byName.Values.Select(e => e.Pair()));
    }
}

public sealed class CookieAuthException : Exception
{
    public CookieAuthException(
        string message,
        int? httpStatusCode = null,
        bool isTransient = false,
        TimeSpan? retryAfter = null) : base(message)
    {
        HttpStatusCode = httpStatusCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public int? HttpStatusCode { get; }
    public bool IsTransient { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Parser for Microsoft cookie alt files (Netscape export and Localts token format).
/// Port of IAS CookieParser (forge-1.8).
/// </summary>
public static class CookieParser
{
    private const string MsaTokenPrefix = "M.C";
    private const string HttpOnlyPrefix = "#HttpOnly_";
    private static readonly System.Text.RegularExpressions.Regex SpaceNetscapeLine =
        new(@"^(\S+)\s+(TRUE|FALSE)\s+(\S+)\s+(TRUE|FALSE)\s+(\d+)\s+(\S+)(?:\s+(.*))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static ParsedCookies FromPath(string path)
    {
        string normalized = NormalizePath(path);
        try
        {
            string text = File.ReadAllText(normalized);
            return FromText(text);
        }
        catch (IOException ex)
        {
            throw new CookieAuthException($"Unable to read cookie file: {normalized}") { Data = { ["inner"] = ex } };
        }
    }

    public static ParsedCookies FromText(string text)
    {
        string trimmed = NormalizeInput(text).Trim();
        if (trimmed.Contains('\t') && LooksLikeNetscape(trimmed))
            return FromNetscape(trimmed);
        if (LooksLikeCookieHeader(trimmed))
            return FromCookieHeader(trimmed);
        if (LooksLikeLocalts(trimmed))
            return FromLocalts(trimmed);
        if (TryExtractMcaToken(trimmed, out string? mca) && !string.IsNullOrWhiteSpace(mca))
            return FromMca(mca);
        if (trimmed.Contains('\t'))
            return FromNetscape(trimmed);

        throw new CookieAuthException(
            "Unrecognized cookie format. Use a Netscape cookie file, semicolon-separated cookie header, Localts token file, or Minecraft access-token (eyJ...) file.");
    }

    private static string NormalizePath(string path)
    {
        path = path.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            return path[1..^1].Trim();
        return path;
    }

    private static string NormalizeInput(string text)
    {
        text = text.Replace("\uFEFF", "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Contains('\t') || !LooksLikeSpaceSeparatedNetscape(text))
            return text;
        return ConvertSpaceSeparatedNetscape(text);
    }

    private static bool LooksLikeSpaceSeparatedNetscape(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t) || IsCommentLine(t))
                continue;
            t = StripHttpOnlyPrefix(t);
            return SpaceNetscapeLine.IsMatch(t);
        }
        return false;
    }

    private static string ConvertSpaceSeparatedNetscape(string text)
    {
        var outSb = new System.Text.StringBuilder(text.Length);
        foreach (string line in text.Split('\n'))
        {
            string stripped = line.Trim();
            if (string.IsNullOrEmpty(stripped) || IsCommentLine(stripped))
            {
                outSb.Append(line).Append('\n');
                continue;
            }
            stripped = StripHttpOnlyPrefix(stripped);

            var m = SpaceNetscapeLine.Match(stripped);
            if (!m.Success)
            {
                outSb.Append(line).Append('\n');
                continue;
            }

            outSb.Append(m.Groups[1].Value).Append('\t')
                .Append(m.Groups[2].Value).Append('\t')
                .Append(m.Groups[3].Value).Append('\t')
                .Append(m.Groups[4].Value).Append('\t')
                .Append(m.Groups[5].Value).Append('\t')
                .Append(m.Groups[6].Value);
            if (m.Groups[7].Success && !string.IsNullOrEmpty(m.Groups[7].Value))
                outSb.Append('\t').Append(m.Groups[7].Value);
            outSb.Append('\n');
        }
        return outSb.ToString();
    }

    private static bool LooksLikeNetscape(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t) || IsCommentLine(t))
                continue;
            t = StripHttpOnlyPrefix(t);
            return t.Split('\t').Length >= 6;
        }
        return false;
    }

    private static bool LooksLikeCookieHeader(string text)
    {
        if (text.Contains('\t') || !text.Contains('='))
            return false;
        if (text.Contains(';'))
            return true;

        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (!string.IsNullOrEmpty(t) && !t.StartsWith('#') && t.Contains('='))
                return true;
        }
        return false;
    }

    private static ParsedCookies FromCookieHeader(string text)
    {
        var cookies = new Dictionary<string, CookieEntry>(StringComparer.Ordinal);
        const string domain = "login.live.com";
        foreach (string segment in text.Split(';'))
        {
            string s = segment.Trim();
            if (string.IsNullOrEmpty(s))
                continue;
            int eq = s.IndexOf('=');
            if (eq <= 0)
                continue;
            string name = s[..eq].Trim();
            string value = s[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            cookies[domain + '\0' + name] = new CookieEntry(domain, "/", true, name, value);
        }
        return ValidateAuthCookies(cookies);
    }

    private static ParsedCookies ValidateAuthCookies(Dictionary<string, CookieEntry> cookies)
    {
        if (cookies.Count == 0)
            throw new CookieAuthException("Cookie file is empty.");

        bool hasAuth = cookies.Values.Any(e =>
            e.Name is "__Host-MSAAUTHP" or "__Host-MSAAUTH");
        if (!hasAuth)
            throw new CookieAuthException("Cookie file is missing __Host-MSAAUTHP or __Host-MSAAUTH.");

        return new ParsedCookies(cookies);
    }

    private static bool LooksLikeLocalts(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t) || t.StartsWith('#'))
                continue;
            if (t.Contains("Localts", StringComparison.OrdinalIgnoreCase))
                return true;
            if (LooksLikeLocaltsRefreshToken(t))
                return true;
            if (t.Contains('\t') || t.Contains('='))
                continue;
            int sep = t.IndexOf(':');
            if (sep > 0 && LooksLikeLocaltsRefreshToken(t[(sep + 1)..].Trim()))
                return true;
        }
        return false;
    }

    private static ParsedCookies FromLocalts(string text)
    {
        string? token = ExtractLocaltsToken(text);
        if (string.IsNullOrWhiteSpace(token))
            throw new CookieAuthException("Localts input is missing a valid MSA session token (M.C... or username:M.C...).");
        return new ParsedCookies(new Dictionary<string, CookieEntry>(), token);
    }

    private static string? ExtractLocaltsToken(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t) || t.StartsWith('#') || t.Contains("Localts", StringComparison.OrdinalIgnoreCase))
                continue;

            int sep = t.IndexOf(':');
            if (sep > 0)
            {
                string value = t[(sep + 1)..].Trim();
                if (LooksLikeLocaltsRefreshToken(value))
                    return value;
            }

            int eq = t.IndexOf('=');
            if (eq > 0)
            {
                string value = t[(eq + 1)..].Trim();
                if (LooksLikeLocaltsRefreshToken(value))
                    return value;
            }
        }

        int mc = text.IndexOf(MsaTokenPrefix, StringComparison.Ordinal);
        if (mc < 0)
            return null;

        int end = mc;
        while (end < text.Length)
        {
            char c = text[end];
            if (c is '\n' or '\r' or ' ' or '\t')
                break;
            end++;
        }
        return text[mc..end];
    }

    private static bool LooksLikeLocaltsRefreshToken(string value) =>
        value.StartsWith(MsaTokenPrefix, StringComparison.Ordinal);

    private static ParsedCookies FromMca(string token) =>
        new(new Dictionary<string, CookieEntry>(), null, token.Trim().Trim('"', '\''));

    private static bool TryExtractMcaToken(string text, out string? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\t'))
            return false;

        // Find JWT-shaped candidates (header starts with eyJ). This also handles
        // JSON wrappers like {"mcToken":"eyJ..."} or a "Bearer eyJ..." prefix.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, @"eyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string candidate = m.Value.Trim().Trim('"', '\'');
            if (IsMinecraftAccessToken(candidate))
            {
                token = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool IsMinecraftAccessToken(string jwt)
    {
        string[] parts = jwt.Split('.');
        if (parts.Length != 3)
            return false;

        System.Text.Json.JsonElement header;
        System.Text.Json.JsonElement payload;
        try
        {
            using var hdoc = System.Text.Json.JsonDocument.Parse(DecodeBase64Url(parts[0]));
            header = hdoc.RootElement.Clone();
            using var doc = System.Text.Json.JsonDocument.Parse(DecodeBase64Url(parts[1]));
            payload = doc.RootElement.Clone();
        }
        catch
        {
            return false;
        }

        if (!header.TryGetProperty("alg", out _))
            return false;

        // MCA (login_with_xbox) tokens embed the Minecraft profile plus Xbox ids:
        // profiles.mc uuid and pfd[type=mc].name/id, with xuid/xid and the
        // Minecraft client id (aid ...402b5328) / iss=authentication / auth=XBOX.
        bool hasMcProfile = false;
        if (payload.TryGetProperty("profiles", out var profiles)
            && profiles.ValueKind == System.Text.Json.JsonValueKind.Object
            && profiles.TryGetProperty("mc", out var mc)
            && mc.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(mc.GetString()))
        {
            hasMcProfile = true;
        }
        else if (payload.TryGetProperty("pfd", out var pfd)
            && pfd.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var entry in pfd.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                bool isMc = entry.TryGetProperty("type", out var t)
                    && string.Equals(t.GetString(), "mc", StringComparison.OrdinalIgnoreCase);
                bool hasId = entry.TryGetProperty("id", out var id)
                    && !string.IsNullOrWhiteSpace(id.GetString());
                bool hasName = entry.TryGetProperty("name", out var name)
                    && !string.IsNullOrWhiteSpace(name.GetString());
                if (isMc && hasId && hasName)
                {
                    hasMcProfile = true;
                    break;
                }
            }
        }
        if (!hasMcProfile)
            return false;

        bool hasXuid = (payload.TryGetProperty("xuid", out var xuid)
                && !string.IsNullOrWhiteSpace(xuid.GetString()))
            || (payload.TryGetProperty("xid", out var xid)
                && !string.IsNullOrWhiteSpace(xid.GetString()));
        if (!hasXuid)
            return false;

        return true;
    }

    private static string DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static ParsedCookies FromNetscape(string text)
    {
        var cookies = new Dictionary<string, CookieEntry>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t) || IsCommentLine(t))
                continue;
            t = StripHttpOnlyPrefix(t);

            string[] parts = t.Split('\t');
            if (parts.Length < 6)
                throw new CookieAuthException($"Invalid cookie line (expected at least 6 tab-separated fields): {t}");

            string domain = parts[0];
            string cookiePath = parts[2];
            bool secure = parts[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            string name = parts[5];
            string value = parts.Length == 6 ? "" : string.Join("\t", parts.Skip(6));
            cookies[domain + '\0' + name] = new CookieEntry(domain, cookiePath, secure, name, value);
        }
        return ValidateAuthCookies(cookies);
    }

    private static bool IsCommentLine(string line) =>
        line.StartsWith('#') && !line.StartsWith(HttpOnlyPrefix, StringComparison.OrdinalIgnoreCase);

    private static string StripHttpOnlyPrefix(string line) =>
        line.StartsWith(HttpOnlyPrefix, StringComparison.OrdinalIgnoreCase)
            ? line[HttpOnlyPrefix.Length..]
            : line;
}
