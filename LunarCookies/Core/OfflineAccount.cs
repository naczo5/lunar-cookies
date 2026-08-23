using System.Security.Cryptography;
using System.Text;

namespace LunarCookies.Core;

/// <summary>
/// Creates "cracked" (offline-mode) Minecraft profiles from a username alone.
/// The UUID follows the standard offline convention — a v3 (MD5) UUID over
/// the bytes of <c>"OfflinePlayer:" + name</c> — so offline servers and
/// plugins that derive UUIDs the same way recognize the account. The access
/// token is the conventional <c>"0"</c> placeholder; offline-mode servers
/// never validate it.
/// </summary>
public static class OfflineAccount
{
    public const string SourceName = "offline";
    public const string OfflineToken = "0";

    /// <summary>Minecraft usernames are 3-16 chars of [A-Za-z0-9_].</summary>
    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new("^[A-Za-z0-9_]{3,16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && NamePattern.IsMatch(name.Trim());

    public static bool TryCreate(string? rawName, out string name, out string uuid, out string error)
    {
        name = "";
        uuid = "";
        string trimmed = rawName?.Trim() ?? "";

        if (!IsValidName(trimmed))
        {
            error = "Usernames must be 3-16 characters using only letters, numbers, and underscores.";
            return false;
        }

        name = trimmed;
        uuid = ComputeOfflineUuid(trimmed).Replace("-", "", StringComparison.Ordinal);
        error = "";
        return true;
    }

    public static string ComputeOfflineUuid(string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        return FormatDashed(hash);
    }

    private static string FormatDashed(ReadOnlySpan<byte> b) =>
        b[0].ToString("x2") + b[1].ToString("x2") + b[2].ToString("x2") + b[3].ToString("x2") + "-" +
        b[4].ToString("x2") + b[5].ToString("x2") + "-" +
        b[6].ToString("x2") + b[7].ToString("x2") + "-" +
        b[8].ToString("x2") + b[9].ToString("x2") + "-" +
        b[10].ToString("x2") + b[11].ToString("x2") + b[12].ToString("x2") + b[13].ToString("x2") +
        b[14].ToString("x2") + b[15].ToString("x2");
}
