using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LunarCookies.Core;

public sealed class MinecraftProfile
{
    public MinecraftProfile(string name, string uuid, string token, string? refreshToken = null)
    {
        Name = name;
        Uuid = uuid;
        Token = token;
        RefreshToken = refreshToken ?? string.Empty;
    }

    public string Name { get; }
    public string Uuid { get; }
    public string Token { get; }
    public string RefreshToken { get; }
}

/// <summary>
/// Cookie and Localts authentication. Port of IAS CookieAuth (forge-1.8).
/// Localts uses Minecraft launcher client id and XBL ticket prefix t=.
/// </summary>
public static class CookieAuth
{
    private const string SisuAuthUrl =
        "https://sisu.xboxlive.com/connect/XboxLive/?state=login&cobrandId=8058f65d-ce06-4c30-9559-473c9275a65d&tid=896928775&ru=https%3A%2F%2Fwww.minecraft.net%2Fen-us%2Flogin&aid=1142970254";
    private const string CookieUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:146.0) Gecko/20100101 Firefox/146.0";
    private const string MinecraftOauthClientId = "00000000402b5328";
    private const string MinecraftOauthScope = "service::user.auth.xboxlive.com::MBI_SSL";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CookieUserAgent);
        return client;
    }

    public static Task<MinecraftProfile> AuthenticateAsync(ParsedCookies cookies, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(cookies.RefreshToken))
            return ProfileFromRefreshTokenAsync(cookies.RefreshToken.Trim(), ct);

        if (!string.IsNullOrWhiteSpace(cookies.AccessToken))
            return ProfileFromAccessTokenAsync(cookies.AccessToken.Trim(), ct);

        return CookiesToProfileAsync(cookies, ct);
    }

    public static async Task<MinecraftProfile> ProfileFromRefreshTokenAsync(string refresh, CancellationToken ct = default)
    {
        var tokens = await LocaltsRefreshToMsaAsync(refresh, ct).ConfigureAwait(false);
        var profile = await ProfileFromMsaAsync(tokens.Access, "t=", ct).ConfigureAwait(false);
        return new MinecraftProfile(profile.Name, profile.Uuid, profile.Token, tokens.Refresh);
    }

    public static Task<MinecraftProfile> ProfileFromAccessTokenAsync(string token, CancellationToken ct = default)
    {
        ThrowIfMcaExpired(token.Trim());
        return ProfileFromMcaAsync(token.Trim(), ct);
    }

    private static async Task<MinecraftProfile> CookiesToProfileAsync(ParsedCookies cookies, CancellationToken ct)
    {
        string mca = await CookiesToMcaViaSisuAsync(cookies, ct).ConfigureAwait(false);
        return await ProfileFromMcaAsync(mca, ct).ConfigureAwait(false);
    }

    private static async Task<(string Access, string Refresh)> LocaltsRefreshToMsaAsync(string refresh, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = MinecraftOauthClientId,
            ["refresh_token"] = refresh,
            ["grant_type"] = "refresh_token",
            ["redirect_uri"] = "https://login.live.com/oauth20_desktop.srf",
            ["scope"] = MinecraftOauthScope
        };

        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.live.com/oauth20_token.srf")
        {
            Content = content
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw HttpFailure("Microsoft token exchange", resp);

        using var doc = JsonDocument.Parse(body);
        string access = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new CookieAuthException("Localts refresh response missing access_token.");
        string rotated = doc.RootElement.TryGetProperty("refresh_token", out var rt)
            ? rt.GetString() ?? refresh
            : refresh;
        return (access, rotated);
    }

    private static async Task<string> CookiesToMcaViaSisuAsync(ParsedCookies cookies, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        ImportCookies(handler.CookieContainer, cookies);

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CookieUserAgent);

        Uri current = new(SisuAuthUrl);
        string? encoded = null;
        for (int redirect = 0; redirect < 10; redirect++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation(
                "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.8");

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!IsRedirect(resp.StatusCode))
                throw HttpFailure("Microsoft/Xbox SISU authentication", resp);
            if (resp.Headers.Location == null)
                throw new CookieAuthException("SISU redirect missing Location header.");

            current = resp.Headers.Location.IsAbsoluteUri
                ? resp.Headers.Location
                : new Uri(current, resp.Headers.Location);
            encoded = ExtractSisuAccessToken(current.AbsoluteUri);
            if (!string.IsNullOrWhiteSpace(encoded))
                break;
        }

        if (string.IsNullOrWhiteSpace(encoded))
            throw new CookieAuthException("No Xbox access token was returned. The Microsoft cookies may be expired.");

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(PadBase64(encoded)));
        }
        catch (FormatException)
        {
            throw new CookieAuthException("The SISU access token response was malformed.");
        }
        int rp = decoded.IndexOf("\"rp://api.minecraftservices.com/\",", StringComparison.Ordinal);
        if (rp < 0)
            throw new CookieAuthException("Unexpected SISU token payload.");

        string slice = decoded[rp..];
        const string uhsNeedle = "{\"DisplayClaims\":{\"xui\":[{\"uhs\":\"";
        int uhsMarker = slice.IndexOf(uhsNeedle, StringComparison.Ordinal);
        int tokenMarker = slice.IndexOf("\"Token\":\"", StringComparison.Ordinal);
        if (uhsMarker < 0 || tokenMarker < 0)
            throw new CookieAuthException("Unable to parse SISU XSTS payload.");

        string hash = slice[(uhsMarker + uhsNeedle.Length)..];
        hash = hash[..hash.IndexOf('"')];
        string xsts = slice[(tokenMarker + "\"Token\":\"".Length)..];
        xsts = xsts[..xsts.IndexOf('"')];
        return await XstsToMcaAsync(xsts, hash, ct).ConfigureAwait(false);
    }

    private static void ImportCookies(CookieContainer container, ParsedCookies parsed)
    {
        int imported = 0;
        foreach (CookieEntry entry in parsed.Cookies.Values)
        {
            string domain = entry.Domain.Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(domain)
                || !domain.EndsWith("live.com", StringComparison.OrdinalIgnoreCase)
                   && !domain.EndsWith("xboxlive.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var cookie = new Cookie(entry.Name, entry.Value, string.IsNullOrWhiteSpace(entry.Path) ? "/" : entry.Path, domain)
                {
                    Secure = entry.Secure,
                    HttpOnly = entry.Name.StartsWith("__Host-", StringComparison.Ordinal)
                };
                container.Add(cookie);
                imported++;
            }
            catch (CookieException)
            {
                // Ignore unrelated malformed browser cookies; required auth
                // cookies are checked below after import.
            }
        }

        Uri login = new("https://login.live.com/");
        bool hasAuth = container.GetCookies(login).Cast<Cookie>().Any(c =>
            c.Name is "__Host-MSAAUTHP" or "__Host-MSAAUTH");
        if (imported == 0 || !hasAuth)
            throw new CookieAuthException("No usable Microsoft authentication cookies were found.");
    }

    internal static void ValidateCookieImportForSmoke(ParsedCookies parsed)
    {
        var container = new CookieContainer();
        ImportCookies(container, parsed);
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static string PadBase64(string value) =>
        value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');

    private static void ThrowIfMcaExpired(string token)
    {
        try
        {
            string[] parts = token.Split('.');
            if (parts.Length != 3)
                return;
            byte[] bytes = Convert.FromBase64String(PadBase64(parts[1].Replace('-', '+').Replace('_', '/')));
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("exp", out var exp))
                return;
            long seconds = exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt64()
                : long.Parse(exp.GetString() ?? "");
            var expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (DateTimeOffset.UtcNow > expiry.AddMinutes(1))
                throw new CookieAuthException(
                    $"Minecraft access token expired on {expiry:yyyy-MM-dd HH:mm} UTC. Re-export a fresh token file — access tokens last ~24h and cannot be refreshed.");
        }
        catch (CookieAuthException)
        {
            throw;
        }
        catch
        {
            // If expiry can't be read, let the profile lookup decide.
        }
    }

    private static string? ExtractSisuAccessToken(string url)
    {
        int idx = url.IndexOf("accessToken=", StringComparison.Ordinal);
        if (idx < 0)
            return null;

        string raw = url[(idx + "accessToken=".Length)..];
        int amp = raw.IndexOf('&');
        if (amp >= 0)
            raw = raw[..amp];
        int hash = raw.IndexOf('#');
        if (hash >= 0)
            raw = raw[..hash];

        try
        {
            return Uri.UnescapeDataString(raw.Replace('+', ' '));
        }
        catch
        {
            return raw;
        }
    }

    private static async Task<string> XstsToMcaAsync(string xsts, string hash, CancellationToken ct)
    {
        var payload = new { identityToken = $"XBL3.0 x={hash};{xsts}" };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox")
        {
            Content = content
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw HttpFailure("Minecraft token exchange", resp);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new CookieAuthException("Minecraft login_with_xbox missing access_token.");
    }

    private static async Task<MinecraftProfile> ProfileFromMcaAsync(string mca, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + mca);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string message = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Minecraft profile lookup rejected the token; the credentials may be expired or revoked."
                : "Minecraft profile lookup failed.";
            throw HttpFailure("Minecraft profile lookup", resp, message);
        }

        using var doc = JsonDocument.Parse(body);
        string name = doc.RootElement.GetProperty("name").GetString()
            ?? throw new CookieAuthException("Profile missing name.");
        string uuid = doc.RootElement.GetProperty("id").GetString()
            ?? throw new CookieAuthException("Profile missing id.");
        return new MinecraftProfile(name, uuid, mca);
    }

    private static async Task<MinecraftProfile> ProfileFromMsaAsync(string msa, string ticketPrefix, CancellationToken ct)
    {
        string mca = await MsaToMcaAsync(msa, ticketPrefix, ct).ConfigureAwait(false);
        return await ProfileFromMcaAsync(mca, ct).ConfigureAwait(false);
    }

    private static async Task<string> MsaToMcaAsync(string msa, string ticketPrefix, CancellationToken ct)
    {
        var xblPayload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = ticketPrefix + msa
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        using (var content = new StringContent(JsonSerializer.Serialize(xblPayload), Encoding.UTF8, "application/json"))
        using (var req = new HttpRequestMessage(HttpMethod.Post, "https://user.auth.xboxlive.com/user/authenticate") { Content = content })
        {
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw HttpFailure("Xbox Live authentication", resp);

            using var doc = JsonDocument.Parse(body);
            string xbl = doc.RootElement.GetProperty("Token").GetString()
                ?? throw new CookieAuthException("XBL response missing Token.");

            var xstsPayload = new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xbl }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            };

            using var content2 = new StringContent(JsonSerializer.Serialize(xstsPayload), Encoding.UTF8, "application/json");
            using var req2 = new HttpRequestMessage(HttpMethod.Post, "https://xsts.auth.xboxlive.com/xsts/authorize") { Content = content2 };
            req2.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp2 = await Http.SendAsync(req2, ct).ConfigureAwait(false);
            string body2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if ((int)resp2.StatusCode == 401)
                throw new CookieAuthException(
                    "Xbox rejected this account (HTTP 401). It may not have an Xbox/Minecraft profile linked.",
                    401);
            if (!resp2.IsSuccessStatusCode)
                throw HttpFailure("Xbox XSTS authorization", resp2);

            using var doc2 = JsonDocument.Parse(body2);
            string hash = doc2.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                ?? throw new CookieAuthException("XSTS missing uhs.");
            string xsts = doc2.RootElement.GetProperty("Token").GetString()
                ?? throw new CookieAuthException("XSTS missing Token.");
            return await XstsToMcaAsync(xsts, hash, ct).ConfigureAwait(false);
        }
    }

    private static CookieAuthException HttpFailure(
        string service,
        HttpResponseMessage response,
        string? message = null)
    {
        int status = (int)response.StatusCode;
        bool transient = status == 408 || status == 429 || status >= 500;
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter == null && response.Headers.RetryAfter?.Date is { } retryDate)
            retryAfter = retryDate - DateTimeOffset.UtcNow;
        if (retryAfter < TimeSpan.Zero)
            retryAfter = TimeSpan.Zero;

        string safeMessage = message
            ?? (status == 429
                ? $"{service} throttled the request (HTTP 429)."
                : $"{service} failed (HTTP {status} {response.StatusCode}).");
        return new CookieAuthException(safeMessage, status, transient, retryAfter);
    }
}
