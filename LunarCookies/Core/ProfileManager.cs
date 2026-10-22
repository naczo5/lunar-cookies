using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LunarCookies.Core;

public sealed class SkinInfo
{
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public string Url { get; set; } = "";
    public string Variant { get; set; } = "CLASSIC"; // CLASSIC or SLIM
}

public sealed class MinecraftProfileDetails
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<SkinInfo> Skins { get; set; } = new();
    public SkinInfo? ActiveSkin => Skins.FirstOrDefault(s => string.Equals(s.State, "ACTIVE", StringComparison.OrdinalIgnoreCase)) ?? Skins.FirstOrDefault();
}

public static class ProfileManager
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    /// <summary>
    /// Validates the existing token or refreshes it via Localts if a refresh token is present.
    /// Updates the stored account tokens in the account store if refreshed.
    /// </summary>
    public static async Task<string> GetValidTokenAsync(StoredAccount account, AccountStore? store = null, CancellationToken ct = default)
    {
        if (account.IsOffline)
        {
            throw new InvalidOperationException("Offline/cracked accounts do not have Mojang API access.");
        }

        if (!string.IsNullOrWhiteSpace(account.AccessToken))
        {
            if (await IsTokenValidAsync(account.AccessToken, ct).ConfigureAwait(false))
            {
                return account.AccessToken;
            }
        }

        if (account.HasRefresh)
        {
            var profile = await CookieAuth.ProfileFromRefreshTokenAsync(account.RefreshToken, ct).ConfigureAwait(false);
            if (store != null)
            {
                store.UpdateTokens(account, profile);
            }
            else
            {
                account.AccessToken = profile.Token;
                if (!string.IsNullOrWhiteSpace(profile.RefreshToken))
                    account.RefreshToken = profile.RefreshToken;
            }
            return profile.Token;
        }

        throw new InvalidOperationException("Selected account does not have a valid token or a refresh token to renew it. Please re-import the account.");
    }

    public static async Task<bool> IsTokenValidAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<MinecraftProfileDetails?> GetProfileDetailsAsync(StoredAccount account, AccountStore? store = null, CancellationToken ct = default)
    {
        if (account.IsOffline)
            return null;

        string token = await GetValidTokenAsync(account, store, ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var details = new MinecraftProfileDetails
        {
            Id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
            Name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : ""
        };

        if (root.TryGetProperty("skins", out var skinsProp) && skinsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var skinEl in skinsProp.EnumerateArray())
            {
                details.Skins.Add(new SkinInfo
                {
                    Id = skinEl.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "",
                    State = skinEl.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "",
                    Url = skinEl.TryGetProperty("url", out var su) ? su.GetString() ?? "" : "",
                    Variant = skinEl.TryGetProperty("variant", out var sv) ? sv.GetString() ?? "CLASSIC" : "CLASSIC"
                });
            }
        }

        return details;
    }

    /// <summary>
    /// Changes the Minecraft username via Mojang's API (for Microsoft accounts) or locally (for offline/cracked accounts).
    /// </summary>
    public static async Task ChangeNameAsync(StoredAccount account, string newName, AccountStore store, CancellationToken ct = default)
    {
        string trimmed = newName?.Trim() ?? "";
        if (!OfflineAccount.IsValidName(trimmed))
        {
            throw new ArgumentException("Minecraft usernames must be 3-16 characters using letters, numbers, and underscores.");
        }

        if (string.Equals(account.Name, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The new username is the same as the current username.");
        }

        if (account.IsOffline)
        {
            account.Name = trimmed;
            account.Uuid = OfflineAccount.ComputeOfflineUuid(trimmed).Replace("-", "", StringComparison.Ordinal);
            store.Save();
            return;
        }

        string token = await GetValidTokenAsync(account, store, ct).ConfigureAwait(false);
        string url = "https://api.minecraftservices.com/minecraft/profile/name/" + Uri.EscapeDataString(trimmed);

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        int code = (int)resp.StatusCode;
        if (code == 200)
        {
            account.Name = trimmed;
            store.Save();
            return;
        }

        string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (code is 400 or 403)
        {
            throw new ArgumentException("Minecraft username is unavailable or cannot be changed right now (e.g. 30-day cooldown).");
        }
        if (code == 429)
        {
            throw new InvalidOperationException("Name change rate-limited. Please wait before trying again.");
        }
        throw new HttpRequestException($"Failed to change name (HTTP {code}): {err}");
    }

    /// <summary>
    /// Uploads a new skin (.png) to Mojang via the Minecraft profile API.
    /// </summary>
    public static async Task UploadSkinAsync(
        StoredAccount account,
        byte[] skinBytes,
        string fileName,
        string variant,
        AccountStore store,
        CancellationToken ct = default)
    {
        if (account.IsOffline)
        {
            throw new InvalidOperationException("Skin changing via Mojang API requires a Microsoft account (offline/cracked accounts do not have Mojang API access).");
        }

        if (skinBytes == null || skinBytes.Length == 0)
        {
            throw new ArgumentException("Skin file is empty.");
        }

        // Verify PNG magic bytes: 89 50 4E 47 0D 0A 1A 0A
        if (skinBytes.Length < 8 ||
            skinBytes[0] != 0x89 || skinBytes[1] != 0x50 || skinBytes[2] != 0x4E || skinBytes[3] != 0x47 ||
            skinBytes[4] != 0x0D || skinBytes[5] != 0x0A || skinBytes[6] != 0x1A || skinBytes[7] != 0x0A)
        {
            throw new ArgumentException("Selected file is not a valid PNG image.");
        }

        string normalizedVariant = string.Equals(variant, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
        string token = await GetValidTokenAsync(account, store, ct).ConfigureAwait(false);

        string boundary = "===LunarCookies" + Guid.NewGuid().ToString("N") + "===";
        using var content = new MultipartFormDataContent(boundary);

        var variantContent = new StringContent(normalizedVariant);
        content.Add(variantContent, "variant");

        var fileContent = new ByteArrayContent(skinBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        string safeFileName = string.IsNullOrWhiteSpace(fileName) ? "skin.png" : Path.GetFileName(fileName);
        content.Add(fileContent, "file", safeFileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minecraftservices.com/minecraft/profile/skins")
        {
            Content = content
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        int code = (int)resp.StatusCode;
        if (code is 200 or 204)
        {
            return;
        }

        string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (code == 429)
        {
            throw new InvalidOperationException("Skin upload rate-limited. Please wait before trying again.");
        }
        throw new HttpRequestException($"Failed to upload skin (HTTP {code}): {err}");
    }

    /// <summary>
    /// Resets the player skin to the default Steve/Alex skin.
    /// </summary>
    public static async Task ResetSkinAsync(StoredAccount account, AccountStore store, CancellationToken ct = default)
    {
        if (account.IsOffline)
        {
            throw new InvalidOperationException("Skin reset via Mojang API requires a Microsoft account.");
        }

        string token = await GetValidTokenAsync(account, store, ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Delete, "https://api.minecraftservices.com/minecraft/profile/skins/active");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        int code = (int)resp.StatusCode;
        if (code is 200 or 204)
        {
            return;
        }

        string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (code == 429)
        {
            throw new InvalidOperationException("Skin reset rate-limited. Please wait before trying again.");
        }
        throw new HttpRequestException($"Failed to reset skin (HTTP {code}): {err}");
    }
}
