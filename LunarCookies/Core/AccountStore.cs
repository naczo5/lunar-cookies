using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunarCookies.Core;

public sealed class StoredAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string Source { get; set; } = "unknown"; // localts | cookie | offline
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }

    [JsonIgnore]
    public bool HasRefresh => !string.IsNullOrWhiteSpace(RefreshToken);

    [JsonIgnore]
    public bool IsOffline => string.Equals(Source, OfflineAccount.SourceName, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayLabel => IsOffline ? $"{Name}  (cracked)"
        : HasRefresh ? $"{Name}  (Localts)"
        : $"{Name}  (cookie)";
}

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly List<StoredAccount> _accounts = new();

    public AccountStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LunarCookies");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "accounts.json");
        Load();
    }

    public IReadOnlyList<StoredAccount> Accounts => _accounts;

    public void Load()
    {
        _accounts.Clear();
        if (!File.Exists(_path))
            return;

        try
        {
            string json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<StoredAccount>>(json, JsonOptions);
            if (list != null)
                _accounts.AddRange(list);
        }
        catch
        {
            // Corrupt store — start empty; user can re-import.
        }
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(_accounts, JsonOptions);
        File.WriteAllText(_path, json);
    }

    public StoredAccount Upsert(MinecraftProfile profile, string source)
    {
        // Offline entries are matched by UUID only so a cracked username can
        // never overwrite a saved Microsoft/cookie account with the same name.
        bool offline = string.Equals(source, OfflineAccount.SourceName, StringComparison.OrdinalIgnoreCase);
        var existing = _accounts.FirstOrDefault(a =>
            SameUuid(a.Uuid, profile.Uuid)
            || (!offline && string.Equals(a.Name, profile.Name, StringComparison.OrdinalIgnoreCase)));

        if (existing == null)
        {
            existing = new StoredAccount();
            _accounts.Add(existing);
        }

        existing.Name = profile.Name;
        existing.Uuid = profile.Uuid;
        existing.AccessToken = profile.Token;
        if (!string.IsNullOrWhiteSpace(profile.RefreshToken))
            existing.RefreshToken = profile.RefreshToken;
        existing.Source = source;
        Save();
        return existing;
    }

    private static bool SameUuid(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && left.Replace("-", "", StringComparison.Ordinal).Equals(
            right.Replace("-", "", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);

    public void UpdateTokens(StoredAccount account, MinecraftProfile profile)
    {
        account.Name = profile.Name;
        account.Uuid = profile.Uuid;
        account.AccessToken = profile.Token;
        if (!string.IsNullOrWhiteSpace(profile.RefreshToken))
            account.RefreshToken = profile.RefreshToken;
        account.LastUsedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public bool Remove(string id)
    {
        int removed = _accounts.RemoveAll(a => a.Id == id);
        if (removed > 0)
            Save();
        return removed > 0;
    }
}
