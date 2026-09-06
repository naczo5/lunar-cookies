using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LunarCookies.Core;
using Microsoft.UI.Xaml;

namespace LunarCookies.ViewModels;

public sealed class AccountCardViewModel : INotifyPropertyChanged
{
    private bool _isCurrent;
    private bool _isSelected;

    public AccountCardViewModel(StoredAccount account)
    {
        Account = account;
    }

    public StoredAccount Account { get; }
    public string Id => Account.Id;
    public string Name => Account.Name;
    public string Initials
    {
        get
        {
            string[] parts = Account.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return "?";
            if (parts.Length == 1)
                return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
            return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }

    public string SourceLabel => Account.IsOffline ? "Cracked" : Account.HasRefresh ? "Localts" : "Cookie";
    public string LastUsedLabel => Account.LastUsedAt is { } used
        ? $"Used {FormatRelative(used)}"
        : $"Added {FormatRelative(Account.AddedAt)}";
    public string UuidLabel => string.IsNullOrWhiteSpace(Account.Uuid) ? "UUID unavailable" : Account.Uuid;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentVisibility)));
        }
    }
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatRelative(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.UtcNow - value;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (age.TotalDays < 1) return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        if (age.TotalDays < 7) return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        return value.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private AccountCardViewModel? _selectedAccount;
    private ServerCardViewModel? _selectedServer;
    private bool _isBusy;
    private bool _isConnected;
    private string _connectionStatus = "Not connected";
    private string _sessionStatus = "No active bridge session";
    private string _currentUsername = "—";
    private string _currentUuid = "—";
    private string _worldStatus = "Unknown";
    private string _processStatus = "Minecraft not detected";
    private string _logText = "";
    private string _directConnectAddress = "";
    private bool _minimizeToTray = true;
    private bool _exitWhenMinecraftCloses = true;
    private bool _populateLunarAccountManager;
    private bool _isCosmeticsUnlocked;
    private string _cosmeticsStatus = "Not patched";
    private string _cosmeticsDetails = "Lunar Client websocket services have not been patched yet.";
    private bool _autoUnlockCosmetics = true;

    public ObservableCollection<AccountCardViewModel> Accounts { get; } = new();
    public ObservableCollection<ServerCardViewModel> Servers { get; } = new();

    public AccountCardViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetField(ref _selectedAccount, value))
            {
                foreach (AccountCardViewModel account in Accounts)
                    account.IsSelected = ReferenceEquals(account, value);
                RaiseCommandState();
            }
        }
    }

    public ServerCardViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetField(ref _selectedServer, value))
            {
                foreach (ServerCardViewModel server in Servers)
                    server.IsSelected = ReferenceEquals(server, value);
                RaiseCommandState();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
                RaiseCommandState();
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetField(ref _isConnected, value))
                RaiseCommandState();
        }
    }

    public string ConnectionStatus { get => _connectionStatus; set => SetField(ref _connectionStatus, value); }
    public string SessionStatus { get => _sessionStatus; set => SetField(ref _sessionStatus, value); }
    public string CurrentUsername { get => _currentUsername; set => SetField(ref _currentUsername, value); }
    public string CurrentUuid { get => _currentUuid; set => SetField(ref _currentUuid, value); }
    public string WorldStatus { get => _worldStatus; set => SetField(ref _worldStatus, value); }
    public string ProcessStatus { get => _processStatus; set => SetField(ref _processStatus, value); }
    public string LogText { get => _logText; set => SetField(ref _logText, value); }
    public string DirectConnectAddress
    {
        get => _directConnectAddress;
        set
        {
            if (SetField(ref _directConnectAddress, value))
                RaiseCommandState();
        }
    }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetField(ref _minimizeToTray, value); }
    public bool ExitWhenMinecraftCloses { get => _exitWhenMinecraftCloses; set => SetField(ref _exitWhenMinecraftCloses, value); }
    public bool PopulateLunarAccountManager
    {
        get => _populateLunarAccountManager;
        set => SetField(ref _populateLunarAccountManager, value);
    }
    public bool IsCosmeticsUnlocked
    {
        get => _isCosmeticsUnlocked;
        set
        {
            if (SetField(ref _isCosmeticsUnlocked, value))
            {
                OnPropertyChanged(nameof(CosmeticsBadgeText));
            }
        }
    }
    public string CosmeticsStatus { get => _cosmeticsStatus; set => SetField(ref _cosmeticsStatus, value); }
    public string CosmeticsDetails { get => _cosmeticsDetails; set => SetField(ref _cosmeticsDetails, value); }
    public bool AutoUnlockCosmetics { get => _autoUnlockCosmetics; set => SetField(ref _autoUnlockCosmetics, value); }
    public string CosmeticsBadgeText => IsCosmeticsUnlocked ? "Unlocked & Active" : "Not Unlocked";

    public bool IsNotBusy => !IsBusy;
    public bool CanUseAccount => !IsBusy && SelectedAccount != null;
    public bool CanDeleteAccount => !IsBusy && SelectedAccount != null;
    public bool CanRestore => !IsBusy && IsConnected;
    public bool CanDisconnect => !IsBusy && IsConnected;
    public bool CanPatchCosmetics => !IsBusy;
    public bool CanJoinDirect => !IsBusy && !string.IsNullOrWhiteSpace(DirectConnectAddress);
    public bool CanJoinSelectedServer => !IsBusy && SelectedServer != null;
    public bool CanDeleteServer => !IsBusy && SelectedServer != null;
    public bool HasServers => Servers.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ReplaceAccounts(IEnumerable<StoredAccount> accounts, string? selectedId, string? currentUuid)
    {
        Accounts.Clear();
        foreach (StoredAccount account in accounts
                     .OrderByDescending(a => a.LastUsedAt ?? DateTimeOffset.MinValue)
                     .ThenByDescending(a => a.AddedAt))
        {
            Accounts.Add(new AccountCardViewModel(account)
            {
                IsCurrent = SameUuid(account.Uuid, currentUuid)
            });
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == selectedId);
        OnPropertyChanged(nameof(HasAccounts));
    }

    public void ReplaceServers(IEnumerable<SavedServer> servers, string? selectedId)
    {
        Servers.Clear();
        foreach (SavedServer server in servers
                     .OrderByDescending(s => s.LastJoinedAt ?? DateTimeOffset.MinValue)
                     .ThenByDescending(s => s.AddedAt))
        {
            Servers.Add(new ServerCardViewModel(server));
        }

        SelectedServer = Servers.FirstOrDefault(s => s.Id == selectedId);
        OnPropertyChanged(nameof(HasServers));
    }

    public bool HasAccounts => Accounts.Count > 0;

    public void MarkCurrentAccount(string? uuid)
    {
        foreach (AccountCardViewModel account in Accounts)
            account.IsCurrent = SameUuid(account.Account.Uuid, uuid);
    }

    public void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}{Environment.NewLine}{line}";
    }

    public void ClearSession()
    {
        IsConnected = false;
        ConnectionStatus = "Not connected";
        SessionStatus = "No active bridge session";
        CurrentUsername = "—";
        CurrentUuid = "—";
        WorldStatus = "Unknown";
        IsCosmeticsUnlocked = false;
        CosmeticsStatus = "Bridge disconnected";
        CosmeticsDetails = "Connect to Lunar Client to view cosmetics unlock status.";
        MarkCurrentAccount(null);
    }

    private static bool SameUuid(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Replace("-", ""), right.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanUseAccount));
        OnPropertyChanged(nameof(CanDeleteAccount));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanPatchCosmetics));
        OnPropertyChanged(nameof(CanJoinDirect));
        OnPropertyChanged(nameof(CanJoinSelectedServer));
        OnPropertyChanged(nameof(CanDeleteServer));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
