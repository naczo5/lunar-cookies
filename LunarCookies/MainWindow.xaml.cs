using System.IO;
using System.Diagnostics;
using LunarCookies.Core;
using LunarCookies.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LunarCookies;

public sealed partial class MainWindow : Window
{
    private sealed record ImportResult(bool Success, string Detail);

    private readonly AccountStore _store = new();
    private readonly ServerStore _servers = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly LunarAccountManager _lunarAccounts = new();
    private readonly InjectorService _injector = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _minecraftMonitor = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly AppSettings _settings;
    private AppWindow _appWindow = null!;
    private TrayIconService? _trayIcon;
    private string? _currentSessionUuid;
    private bool _gridReady;
    private bool _lunarWasRunning;
    private bool _isInTray;
    private bool _trayHintShown;
    private bool _isShuttingDown;
    private bool _settingsReady;
    private int _missingMinecraftChecks;
    private int? _trackedMinecraftPid;

    public MainWindowViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        InitializeComponent();
        RootLayout.DataContext = ViewModel;
        ViewModel.MinimizeToTray = _settings.MinimizeToTray;
        ViewModel.ExitWhenMinecraftCloses = _settings.ExitWhenMinecraftCloses;
        ViewModel.PopulateLunarAccountManager = _settings.PopulateLunarAccountManager;
        ConfigureWindow();
        ConfigureTrayIcon();

        MainNavigation.SelectedItem = AccountsNavigationItem;
        ReloadAccounts();
        ReloadServers();
        _lunarWasRunning = RefreshProcessStatus();
        _minecraftMonitor.Tick += MinecraftMonitor_Tick;
        _minecraftMonitor.Start();
        Log("Ready. Launch Minecraft, then select an account or open Servers.");
        _settingsReady = true;
        Closed += OnClosed;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new SizeInt32(1040, 760));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 800;
            presenter.PreferredMinimumHeight = 620;
        }

        DisplayArea area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        int x = area.WorkArea.X + Math.Max(0, (area.WorkArea.Width - 1040) / 2);
        int y = area.WorkArea.Y + Math.Max(0, (area.WorkArea.Height - 760) / 2);
        _appWindow.Move(new PointInt32(x, y));
        _appWindow.Changed += AppWindow_Changed;
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new TrayIconService(WindowNative.GetWindowHandle(this));
        _trayIcon.OpenRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
        _trayIcon.ExitRequested += () => DispatcherQueue.TryEnqueue(ShutdownApplication);
    }

    private void ReloadAccounts(string? selectId = null)
    {
        string? selectedId = selectId ?? ViewModel.SelectedAccount?.Id;
        ViewModel.ReplaceAccounts(_store.Accounts, selectedId, _currentSessionUuid);
        EmptyAccountsState.Visibility = ViewModel.HasAccounts ? Visibility.Collapsed : Visibility.Visible;
        AccountGrid.Visibility = ViewModel.HasAccounts ? Visibility.Visible : Visibility.Collapsed;
        ResizeAccountCards(AccountGrid.ActualWidth);
    }

    private void ReloadServers(string? selectId = null)
    {
        string? selectedId = selectId ?? ViewModel.SelectedServer?.Id;
        ViewModel.ReplaceServers(_servers.Servers, selectedId);
        EmptyServersState.Visibility = ViewModel.HasServers ? Visibility.Collapsed : Visibility.Visible;
        ServerList.Visibility = ViewModel.HasServers ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool RefreshProcessStatus(bool updateTrackedProcess = true)
    {
        IReadOnlyList<Process> matches = ProcessFinder.FindMinecraftProcesses();
        Process? process = matches.Count == 1 ? matches[0] : null;
        ViewModel.ProcessStatus = matches.Count switch
        {
            0 => "Supported Minecraft process not detected.",
            1 => ProcessFinder.Describe(process),
            _ => $"{matches.Count} Minecraft clients detected; close all but the target."
        };
        if (process != null && updateTrackedProcess)
            _trackedMinecraftPid = process.Id;
        return matches.Count > 0;
    }

    private void Log(string message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Log(message));
            return;
        }

        ViewModel.AppendLog(message);
        LogBox.SelectionStart = LogBox.Text?.Length ?? 0;
    }

    private void SetBusy(bool busy)
    {
        ViewModel.IsBusy = busy;
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        NoticeBar.IsOpen = true;
    }

    private void MainNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        bool accounts = tag == "accounts" || string.IsNullOrEmpty(tag);
        bool servers = tag == "servers";
        bool injection = tag == "injection";
        AccountsPage.Visibility = accounts ? Visibility.Visible : Visibility.Collapsed;
        ServersPage.Visibility = servers ? Visibility.Visible : Visibility.Collapsed;
        InjectionPage.Visibility = injection ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (injection)
            RefreshProcessStatus();
        if (servers && ViewModel.HasServers)
            _ = PingAllServersAsync();
    }

    private void AccountGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _gridReady = true;
        ResizeAccountCards(AccountGrid.ActualWidth);
    }

    private void AccountGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ResizeAccountCards(e.NewSize.Width);

    private void ResizeAccountCards(double availableWidth)
    {
        if (!_gridReady || availableWidth <= 0 || AccountGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
            return;

        const double totalMargins = 28;
        panel.ItemWidth = Math.Max(210, Math.Floor((availableWidth - totalMargins) / 3));
        panel.ItemHeight = 148;
    }

    private async void Inject_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        await RunBusyAsync(async () =>
        {
            RefreshProcessStatus();
            bool ok = await _injector.InjectAsync(Log, _lifetime.Token);
            SyncConnectionStatus();
            if (ok)
            {
                await RefreshSessionUiAsync();
                ShowNotice("Bridge connected", _injector.Status, InfoBarSeverity.Success);
            }
            else
            {
                ShowNotice("Unable to connect", _injector.Status, InfoBarSeverity.Error);
            }
        });
    }

    private async void RefreshSession_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        await RunBusyAsync(async () =>
        {
            if (!_injector.Bridge.IsConnected)
            {
                ShowNotice("Not connected", "Inject or connect the bridge first.", InfoBarSeverity.Warning);
                return;
            }
            await RefreshSessionUiAsync();
        });
    }

    private async Task RefreshSessionUiAsync()
    {
        BridgeSessionInfo? info = await _injector.Bridge.GetSessionAsync(_lifetime.Token);
        if (info == null)
        {
            ViewModel.SessionStatus = "Session unavailable";
            Log("getSession failed: no response");
            ShowNotice("Session unavailable", "The bridge did not return a session.", InfoBarSeverity.Warning);
            return;
        }

        if (!info.Ok && !string.IsNullOrEmpty(info.Error))
        {
            ViewModel.SessionStatus = "Session error";
            Log($"Session error: {info.Error}");
            ShowNotice("Session error", info.Error, InfoBarSeverity.Error);
            return;
        }

        _currentSessionUuid = info.Uuid;
        ViewModel.IsConnected = true;
        ViewModel.ConnectionStatus = _injector.Status;
        ViewModel.CurrentUsername = string.IsNullOrWhiteSpace(info.Username) ? "—" : info.Username;
        ViewModel.CurrentUuid = string.IsNullOrWhiteSpace(info.Uuid) ? "—" : info.Uuid;
        ViewModel.WorldStatus = info.InWorld ? "In a world / server" : "Main menu";
        ViewModel.SessionStatus = string.IsNullOrWhiteSpace(info.Username)
            ? "Session connected"
            : $"{info.Username} · {(info.InWorld ? "in world" : "menu")}";
        ViewModel.MarkCurrentAccount(info.Uuid);
        Log($"Current session: {info.Username} uuid={info.Uuid} inWorld={info.InWorld} ready={info.Ready}");
    }

    private async void UseAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedAccount is not { } selected)
            return;

        StoredAccount account = selected.Account;
        await RunBusyAsync(async () =>
        {
            if (!_injector.Bridge.IsConnected)
            {
                Log("Not connected — injecting before account switch.");
                if (!await _injector.InjectAsync(Log, _lifetime.Token))
                {
                    SyncConnectionStatus();
                    ShowNotice("Injection failed", _injector.Status, InfoBarSeverity.Error);
                    return;
                }
                SyncConnectionStatus();
            }

            BridgeSessionInfo? current = await _injector.Bridge.GetSessionAsync(_lifetime.Token);
            if (current is { InWorld: true })
            {
                Log("Switch blocked: leave the world or server first.");
                ShowNotice("Return to the main menu",
                    "Disconnect from the current world or server before switching accounts.",
                    InfoBarSeverity.Warning);
                return;
            }

            Log($"Authenticating {account.Name}…");
            MinecraftProfile profile;
            try
            {
                profile = account.HasRefresh
                    ? await CookieAuth.ProfileFromRefreshTokenAsync(account.RefreshToken, _lifetime.Token)
                    : await CookieAuth.ProfileFromAccessTokenAsync(account.AccessToken, _lifetime.Token);
            }
            catch (CookieAuthException ex)
            {
                Log($"Authentication failed for {account.Name}: {ex.Message}");
                ShowNotice("Authentication failed", ex.Message, InfoBarSeverity.Error);
                return;
            }

            _store.UpdateTokens(account, profile);
            BridgeSessionInfo? result = await _injector.Bridge.SetSessionAsync(
                profile.Name, profile.Uuid, profile.Token, _lifetime.Token);

            if (result == null || !result.Ok)
            {
                string error = result?.Error ?? "The bridge did not respond.";
                Log($"setSession failed: {error}");
                ShowNotice("Account switch failed", error, InfoBarSeverity.Error);
                ReloadAccounts(account.Id);
                return;
            }

            _currentSessionUuid = result.Uuid;
            ReloadAccounts(account.Id);
            await RefreshSessionUiAsync();
            Log($"Switched to {result.Username}. Join from the Servers tab, or open multiplayer in Lunar.");
            ShowNotice("Account ready", $"Minecraft is now using {result.Username}.", InfoBarSeverity.Success);
            TryPopulateLunarAccount(profile, setActive: true);
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || !_injector.Bridge.IsConnected)
            return;

        await RunBusyAsync(async () =>
        {
            BridgeSessionInfo? current = await _injector.Bridge.GetSessionAsync(_lifetime.Token);
            if (current is { InWorld: true })
            {
                ShowNotice("Return to the main menu",
                    "Leave the current world or server before restoring the launch account.",
                    InfoBarSeverity.Warning);
                return;
            }

            BridgeSessionInfo? result = await _injector.Bridge.RestoreSessionAsync(_lifetime.Token);
            if (result == null || !result.Ok)
            {
                string error = result?.Error ?? "The bridge did not respond.";
                Log($"restoreSession failed: {error}");
                ShowNotice("Restore failed", error, InfoBarSeverity.Error);
                return;
            }

            _currentSessionUuid = result.Uuid;
            await RefreshSessionUiAsync();
            Log($"Restored launch session: {result.Username}");
            ShowNotice("Launch account restored", $"Lunar is now using {result.Username}.", InfoBarSeverity.Success);
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedAccount is not { } selected)
            return;

        int oldIndex = ViewModel.Accounts.IndexOf(selected);
        ContentDialog dialog = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = $"Delete {selected.Name}?",
            Content = "This removes the saved credentials only. It does not change the account currently active in Lunar Client.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        string name = selected.Name;
        _store.Remove(selected.Id);
        ReloadAccounts();
        if (ViewModel.Accounts.Count > 0)
            ViewModel.SelectedAccount = ViewModel.Accounts[Math.Min(oldIndex, ViewModel.Accounts.Count - 1)];
        Log($"Removed saved account {name}");
        ShowNotice("Account removed", $"{name} was removed from saved accounts.", InfoBarSeverity.Success);
    }

    private void RootLayout_DragEnter(object sender, DragEventArgs e) =>
        UpdateDragState(e, showOverlay: true);

    private void RootLayout_DragOver(object sender, DragEventArgs e) =>
        UpdateDragState(e, showOverlay: true);

    private void RootLayout_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void UpdateDragState(DragEventArgs e, bool showOverlay)
    {
        bool acceptsFiles = !ViewModel.IsBusy
                            && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = acceptsFiles ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (acceptsFiles)
        {
            e.DragUIOverride.Caption = "Import account file";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        DropOverlay.Visibility = acceptsFiles && showOverlay
            ? Visibility.Visible
            : Visibility.Collapsed;
        e.Handled = true;
    }

    private async void RootLayout_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (ViewModel.IsBusy || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        catch (Exception ex)
        {
            ShowNotice("Unable to read dropped files", ex.Message, InfoBarSeverity.Error);
            return;
        }

        List<StorageFile> files = items.OfType<StorageFile>().ToList();
        if (files.Count == 0)
        {
            ShowNotice("No files found", "Drop one or more account text files.", InfoBarSeverity.Warning);
            return;
        }

        var results = new List<(string FileName, ImportResult Result)>();
        await RunBusyAsync(async () =>
        {
            for (int index = 0; index < files.Count; index++)
            {
                StorageFile file = files[index];
                string safeName = SafeFileName(file.Name);
                try
                {
                    string text = await FileIO.ReadTextAsync(file);
                    ImportResult result = await ImportAccountTextCoreAsync(text, safeName);
                    results.Add((safeName, result));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    const string detail = "The file could not be read.";
                    results.Add((safeName, new ImportResult(false, detail)));
                    Log($"{safeName}: {detail} ({ex.GetType().Name})");
                }

                // Avoid sending a burst of complete Microsoft/Xbox/Minecraft
                // authentication chains when several files are dropped.
                if (index < files.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
        });

        if (results.Count > 0)
            ShowBatchImportSummary(results);
    }

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".cookies");
        picker.FileTypeFilter.Add(".cookie");
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        try
        {
            string text = await FileIO.ReadTextAsync(file);
            await ImportAccountTextAsync(text);
        }
        catch (Exception ex)
        {
            Log($"Failed to read import file: {ex.Message}");
            ShowNotice("Unable to read file", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ImportText_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        string initialText = "";
        try
        {
            DataPackageView clipboard = Clipboard.GetContent();
            if (clipboard.Contains(StandardDataFormats.Text))
                initialText = await clipboard.GetTextAsync();
        }
        catch
        {
            // Clipboard access can fail when another process owns it.
        }

        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            MaxHeight = 300,
            PlaceholderText = "Paste a Localts token, Netscape cookies, or a cookie header…",
            Text = initialText
        };

        ContentDialog dialog = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Import account",
            Content = input,
            PrimaryButtonText = "Authenticate and save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result = await dialog.ShowAsync();
        string text = input.Text ?? "";
        input.Text = "";
        if (result == ContentDialogResult.Primary)
            await ImportAccountTextAsync(text);
    }

    private async Task<bool> ImportAccountTextAsync(string text)
    {
        ImportResult result = new(false, "The import did not run.");
        await RunBusyAsync(async () =>
        {
            result = await ImportAccountTextCoreAsync(text, "Account import");
        });

        ShowNotice(
            result.Success ? "Account imported" : "Import failed",
            result.Detail,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        return result.Success;
    }

    private async Task<ImportResult> ImportAccountTextCoreAsync(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ImportResult(false, "The file or pasted text is empty.");

        ParsedCookies parsed;
        try
        {
            parsed = CookieParser.FromText(text);
        }
        catch (CookieAuthException ex)
        {
            Log($"{label}: parsing failed — {ex.Message}");
            return new ImportResult(false, ex.Message);
        }

        string source = string.IsNullOrWhiteSpace(parsed.RefreshToken) ? "cookie" : "localts";
        Log($"{label}: parsed {source} data; authenticating.");

        try
        {
            MinecraftProfile profile = await AuthenticateWithRetryAsync(parsed, label);
            StoredAccount stored = _store.Upsert(profile, source);
            ReloadAccounts(stored.Id);
            TryPopulateLunarAccount(profile, setActive: true);
            string detail = $"{profile.Name} imported from {source}.";
            Log($"{label}: saved account {profile.Name} ({source}).");
            return new ImportResult(true, detail);
        }
        catch (CookieAuthException ex)
        {
            Log($"{label}: authentication failed — {ex.Message}");
            return new ImportResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            string status = ex.StatusCode is { } code ? $" (HTTP {(int)code})" : "";
            string detail = $"Network authentication failed{status}.";
            Log($"{label}: {detail}");
            return new ImportResult(false, detail);
        }
        catch (TaskCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            const string detail = "Authentication timed out after retries.";
            Log($"{label}: {detail}");
            return new ImportResult(false, detail);
        }
    }

    private async Task<MinecraftProfile> AuthenticateWithRetryAsync(ParsedCookies parsed, string label)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await CookieAuth.AuthenticateAsync(parsed, _lifetime.Token);
            }
            catch (CookieAuthException ex) when (ex.IsTransient && attempt < maxAttempts)
            {
                TimeSpan delay = RetryDelay(ex.RetryAfter, attempt);
                Log($"{label}: {ex.Message} Retrying in {FormatDelay(delay)} ({attempt + 1}/{maxAttempts}).");
                await Task.Delay(delay, _lifetime.Token);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                TimeSpan delay = RetryDelay(null, attempt);
                string status = ex.StatusCode is { } code ? $"HTTP {(int)code}" : "network error";
                Log($"{label}: transient {status}. Retrying in {FormatDelay(delay)} ({attempt + 1}/{maxAttempts}).");
                await Task.Delay(delay, _lifetime.Token);
            }
            catch (TaskCanceledException) when (!_lifetime.IsCancellationRequested && attempt < maxAttempts)
            {
                TimeSpan delay = RetryDelay(null, attempt);
                Log($"{label}: authentication timed out. Retrying in {FormatDelay(delay)} ({attempt + 1}/{maxAttempts}).");
                await Task.Delay(delay, _lifetime.Token);
            }
        }
    }

    private static TimeSpan RetryDelay(TimeSpan? serverDelay, int failedAttempt)
    {
        TimeSpan delay = serverDelay ?? TimeSpan.FromSeconds(5 * Math.Pow(2, failedAttempt - 1));
        return TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 120));
    }

    private static string FormatDelay(TimeSpan delay) =>
        delay.TotalSeconds < 60
            ? $"{Math.Ceiling(delay.TotalSeconds)}s"
            : $"{Math.Ceiling(delay.TotalMinutes)}m";

    private void ShowBatchImportSummary(IReadOnlyList<(string FileName, ImportResult Result)> results)
    {
        int imported = results.Count(r => r.Result.Success);
        var failures = results.Where(r => !r.Result.Success).ToList();
        string message = $"Imported {imported} of {results.Count} account files.";
        if (failures.Count > 0)
        {
            string details = string.Join(
                Environment.NewLine,
                failures.Select(f => $"{f.FileName}: {f.Result.Detail}"));
            message += Environment.NewLine + details;
        }

        InfoBarSeverity severity = imported == results.Count
            ? InfoBarSeverity.Success
            : imported > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        ShowNotice("Drop import complete", message, severity);
    }

    private static string SafeFileName(string name) =>
        name.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _injector.Disconnect();
        _currentSessionUuid = null;
        ViewModel.ClearSession();
        Log("Disconnected the control bridge. The injected DLL remains loaded.");
        ShowNotice("Bridge disconnected",
            "The control connection is closed; the injected DLL remains loaded.",
            InfoBarSeverity.Informational);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ViewModel.LogText))
            return;
        var package = new DataPackage();
        package.SetText(ViewModel.LogText);
        Clipboard.SetContent(package);
        ShowNotice("Log copied", "The diagnostic log was copied to the clipboard.", InfoBarSeverity.Success);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) =>
        ViewModel.LogText = "";

    private void DirectConnectBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.DirectConnectAddress = DirectConnectBox.Text ?? "";
    }

    private void DirectConnectBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            JoinDirect_Click(sender, e);
        }
    }

    private async void JoinDirect_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;
        await JoinAddressAsync(DirectConnectText, save: false);
    }

    private async void SaveDirect_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;
        if (!MinecraftServerAddress.TryParse(DirectConnectText, out MinecraftServerAddress parsed))
        {
            ShowNotice("Invalid address", "Enter a server as host or host:port.", InfoBarSeverity.Warning);
            return;
        }

        SavedServer saved = _servers.Add(parsed.Display, parsed.Display);
        ReloadServers(saved.Id);
        Log($"Saved server {saved.Address}");
        ShowNotice("Server saved", saved.Address, InfoBarSeverity.Success);
        await PingServerAsync(ViewModel.SelectedServer);
    }

    private async void JoinSelectedServer_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedServer is not { } selected)
            return;
        await JoinAddressAsync(selected.Address, save: false, markJoined: selected.Server);
    }

    private async void AddServer_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        var nameBox = new TextBox { PlaceholderText = "Display name (optional)" };
        var addressBox = new TextBox
        {
            PlaceholderText = "host or host:port",
            Text = ViewModel.DirectConnectAddress ?? "",
            Margin = new Thickness(0, 8, 0, 0)
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(nameBox);
        panel.Children.Add(addressBox);

        ContentDialog dialog = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Add server",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        string addressText = addressBox.Text ?? "";
        if (!MinecraftServerAddress.TryParse(addressText, out MinecraftServerAddress parsed))
        {
            ShowNotice("Invalid address", "Enter a server as host or host:port.", InfoBarSeverity.Warning);
            return;
        }

        SavedServer saved = _servers.Add(nameBox.Text ?? "", parsed.Display);
        ReloadServers(saved.Id);
        Log($"Saved server {saved.Name} ({saved.Address})");
        await PingServerAsync(ViewModel.SelectedServer);
    }

    private async void PingServers_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;
        await PingAllServersAsync();
    }

    private void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedServer is not { } selected)
            return;

        int oldIndex = ViewModel.Servers.IndexOf(selected);
        string name = selected.Name;
        _servers.Remove(selected.Id);
        ReloadServers();
        if (ViewModel.Servers.Count > 0)
            ViewModel.SelectedServer = ViewModel.Servers[Math.Min(oldIndex, ViewModel.Servers.Count - 1)];
        Log($"Removed saved server {name}");
        ShowNotice("Server removed", $"{name} was removed from the server list.", InfoBarSeverity.Success);
    }

    private string DirectConnectText =>
        string.IsNullOrWhiteSpace(DirectConnectBox.Text)
            ? ViewModel.DirectConnectAddress
            : DirectConnectBox.Text;

    private async Task JoinAddressAsync(string addressText, bool save, SavedServer? markJoined = null)
    {
        if (!MinecraftServerAddress.TryParse(addressText, out MinecraftServerAddress parsed))
        {
            ShowNotice("Invalid address", "Enter a server as host or host:port.", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (!_injector.Bridge.IsConnected)
            {
                Log("Not connected — injecting before joining a server.");
                if (!await _injector.InjectAsync(Log, _lifetime.Token))
                {
                    SyncConnectionStatus();
                    ShowNotice("Injection failed", _injector.Status, InfoBarSeverity.Error);
                    return;
                }
                SyncConnectionStatus();
            }

            if (save)
            {
                SavedServer saved = _servers.Add(parsed.Display, parsed.Display);
                markJoined ??= saved;
                ReloadServers(saved.Id);
            }

            Log($"Joining {parsed.Display}…");
            BridgeJoinResult? result = await _injector.Bridge.JoinServerAsync(
                parsed.Host, parsed.Port, _lifetime.Token);
            if (result == null || !result.Ok)
            {
                string error = result?.Error ?? "The bridge did not respond.";
                Log($"joinServer failed: {error}");
                ShowNotice("Unable to join", error, InfoBarSeverity.Error);
                return;
            }

            if (markJoined != null)
            {
                _servers.MarkJoined(markJoined);
                ReloadServers(markJoined.Id);
            }

            await RefreshSessionUiAsync();
            Log($"Minecraft is connecting to {result.Address}.");
            ShowNotice("Connecting", $"Minecraft is joining {result.Address}.", InfoBarSeverity.Success);
        });
    }

    private async Task PingAllServersAsync()
    {
        try
        {
            IReadOnlyList<ServerCardViewModel> servers = ViewModel.Servers.ToList();
            foreach (ServerCardViewModel server in servers)
                server.IsPinging = true;
            await Task.WhenAll(servers.Select(PingServerAsync));
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            Log($"Server ping failed: {ex.Message}");
        }
    }

    private async Task PingServerAsync(ServerCardViewModel? server)
    {
        if (server == null)
            return;

        try
        {
            server.IsPinging = true;
            server.StatusLabel = "Pinging…";
            ServerPingResult result = await ServerPing.QueryAsync(server.Address, _lifetime.Token);
            void Apply() => server.ApplyPing(result);
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(Apply);
                return;
            }
            Apply();
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
    }

    private void TryPopulateLunarAccount(MinecraftProfile profile, bool setActive)
    {
        if (!_settings.PopulateLunarAccountManager)
            return;

        LunarAccountWriteResult result = _lunarAccounts.Upsert(
            profile.Name, profile.Uuid, profile.Token, RefreshTokenFor(profile), setActive);
        Log(result.Detail);
        if (!result.Success)
            ShowNotice("Lunar account manager", result.Detail, InfoBarSeverity.Warning);
    }

    private void TryPopulateAllLunarAccounts()
    {
        IEnumerable<(string Name, string Uuid, string AccessToken, string RefreshToken)> accounts = _store.Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.AccessToken))
            .Select(a => (a.Name, a.Uuid, a.AccessToken, a.RefreshToken));
        LunarAccountWriteResult result = _lunarAccounts.UpsertAll(accounts, _currentSessionUuid);
        Log(result.Detail);
        ShowNotice(
            result.Success ? "Lunar account manager" : "Lunar account manager",
            result.Detail,
            result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private string RefreshTokenFor(MinecraftProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.RefreshToken))
            return profile.RefreshToken;
        return _store.Accounts.FirstOrDefault(a =>
                   string.Equals(
                       a.Uuid.Replace("-", "", StringComparison.Ordinal),
                       profile.Uuid.Replace("-", "", StringComparison.Ordinal),
                       StringComparison.OrdinalIgnoreCase))
               ?.RefreshToken
               ?? "";
    }

    private void SettingsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        bool populateNow = _settingsReady
                           && !_settings.PopulateLunarAccountManager
                           && ViewModel.PopulateLunarAccountManager;
        _settings.MinimizeToTray = ViewModel.MinimizeToTray;
        _settings.ExitWhenMinecraftCloses = ViewModel.ExitWhenMinecraftCloses;
        _settings.PopulateLunarAccountManager = ViewModel.PopulateLunarAccountManager;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log($"Unable to save settings: {ex.Message}");
            ShowNotice("Settings not saved", ex.Message, InfoBarSeverity.Warning);
            return;
        }

        if (populateNow)
            TryPopulateAllLunarAccounts();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_isShuttingDown || _isInTray || !_settings.MinimizeToTray || !args.DidPresenterChange)
            return;

        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            DispatcherQueue.TryEnqueue(MinimizeWindowToTray);
    }

    private void MinimizeWindowToTray()
    {
        if (_isShuttingDown || _isInTray || !_settings.MinimizeToTray)
            return;

        _isInTray = true;
        if (_trayIcon != null)
        {
            _trayIcon.Show();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloon(
                    "Lunar Cookies is still running",
                    "Double-click the tray icon to restore the window.");
            }
        }
        _appWindow.Hide();
    }

    private void RestoreFromTray()
    {
        if (_isShuttingDown)
            return;

        _appWindow.Show();
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();
        Activate();
        _isInTray = false;
        _trayIcon?.Hide();
    }

    private void MinecraftMonitor_Tick(object? sender, object e)
    {
        bool detected;
        if (_trackedMinecraftPid is int trackedPid && IsProcessRunning(trackedPid))
        {
            detected = true;
            RefreshProcessStatus(updateTrackedProcess: false);
        }
        else
        {
            detected = _trackedMinecraftPid == null || !_settings.ExitWhenMinecraftCloses
                ? RefreshProcessStatus()
                : false;
        }

        if (detected)
        {
            _lunarWasRunning = true;
            _missingMinecraftChecks = 0;
            return;
        }

        if (!_settings.ExitWhenMinecraftCloses || !_lunarWasRunning)
        {
            _missingMinecraftChecks = 0;
            return;
        }

        // Require two consecutive misses so a short process-query failure does
        // not close the app unexpectedly.
        if (++_missingMinecraftChecks < 2)
            return;

        Log("Minecraft closed; exiting Lunar Cookies.");
        ShutdownApplication();
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void ShutdownApplication()
    {
        if (_isShuttingDown)
            return;
        _isShuttingDown = true;
        _trayIcon?.Hide();
        Close();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (ViewModel.IsBusy)
            return;

        SetBusy(true);
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}");
            ShowNotice("Unexpected error", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                SetBusy(false);
                SyncConnectionStatus();
            }
        }
    }

    private void SyncConnectionStatus()
    {
        ViewModel.IsConnected = _injector.Bridge.IsConnected;
        ViewModel.ConnectionStatus = _injector.Status;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isShuttingDown = true;
        _minecraftMonitor.Stop();
        _lifetime.Cancel();
        _injector.Disconnect();
        if (_trayIcon != null)
        {
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _lifetime.Dispose();
    }
}
