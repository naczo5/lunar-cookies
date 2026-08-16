using System.ComponentModel;
using System.Runtime.CompilerServices;
using LunarCookies.Core;
using Microsoft.UI.Xaml;

namespace LunarCookies.ViewModels;

public sealed class ServerCardViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isPinging;
    private string _statusLabel = "Not pinged";
    private string _playersLabel = "—";
    private string _motd = "";
    private bool _isOnline;

    public ServerCardViewModel(SavedServer server)
    {
        Server = server;
    }

    public SavedServer Server { get; }
    public string Id => Server.Id;
    public string Name => string.IsNullOrWhiteSpace(Server.Name) ? Server.Address : Server.Name;
    public string Address => Server.Address;
    public string LastJoinedLabel => Server.LastJoinedAt is { } joined
        ? $"Joined {FormatRelative(joined)}"
        : $"Added {FormatRelative(Server.AddedAt)}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionVisibility)));
        }
    }
    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public bool IsPinging
    {
        get => _isPinging;
        set => SetField(ref _isPinging, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetField(ref _isOnline, value);
    }

    public string StatusLabel
    {
        get => _statusLabel;
        set => SetField(ref _statusLabel, value);
    }

    public string PlayersLabel
    {
        get => _playersLabel;
        set => SetField(ref _playersLabel, value);
    }

    public string Motd
    {
        get => _motd;
        set => SetField(ref _motd, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyPing(ServerPingResult result)
    {
        IsOnline = result.Online;
        StatusLabel = result.StatusLabel;
        PlayersLabel = result.PlayersLabel;
        Motd = result.Description;
        IsPinging = false;
    }

    private static string FormatRelative(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.UtcNow - value;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (age.TotalDays < 1) return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        if (age.TotalDays < 7) return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        return value.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
