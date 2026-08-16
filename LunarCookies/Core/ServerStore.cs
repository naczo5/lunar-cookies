using System.IO;
using System.Text.Json;

namespace LunarCookies.Core;

public sealed class SavedServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastJoinedAt { get; set; }
}

public sealed class ServerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly List<SavedServer> _servers = new();

    public ServerStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LunarCookies");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "servers.json");
        Load();
    }

    public IReadOnlyList<SavedServer> Servers => _servers;

    public void Load()
    {
        _servers.Clear();
        if (!File.Exists(_path))
            return;

        try
        {
            string json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<SavedServer>>(json, JsonOptions);
            if (list != null)
                _servers.AddRange(list);
        }
        catch
        {
            // Corrupt store — start empty; user can re-add servers.
        }
    }

    public void Save()
    {
        string json = JsonSerializer.Serialize(_servers, JsonOptions);
        File.WriteAllText(_path, json);
    }

    public SavedServer Add(string name, string address)
    {
        SavedServer existing = _servers.FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase))
            ?? new SavedServer { AddedAt = DateTimeOffset.UtcNow };

        if (!_servers.Contains(existing))
            _servers.Add(existing);

        existing.Name = string.IsNullOrWhiteSpace(name) ? address : name.Trim();
        existing.Address = address.Trim();
        Save();
        return existing;
    }

    public void MarkJoined(SavedServer server)
    {
        server.LastJoinedAt = DateTimeOffset.UtcNow;
        Save();
    }

    public bool Remove(string id)
    {
        int removed = _servers.RemoveAll(s => s.Id == id);
        if (removed > 0)
            Save();
        return removed > 0;
    }
}
