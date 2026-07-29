using System.Diagnostics;
using System.IO;

namespace LunarCookies.Core;

public sealed class InjectorService
{
    private readonly BridgeClient _bridge = new();

    public BridgeClient Bridge => _bridge;
    public bool IsInjected { get; private set; }
    public string Status { get; private set; } = "Not injected";

    public async Task<bool> InjectAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        IReadOnlyList<Process> candidates = ProcessFinder.FindMinecraftProcesses();
        if (candidates.Count == 0)
        {
            Status = "Minecraft not found. Launch a supported client and wait for its game window.";
            log?.Invoke(Status);
            return false;
        }
        if (candidates.Count > 1)
        {
            Status = $"Found {candidates.Count} Minecraft clients. Close all but the intended target before injecting.";
            log?.Invoke(Status);
            foreach (Process candidate in candidates)
                log?.Invoke($"Candidate: {ProcessFinder.Describe(candidate)}");
            return false;
        }
        Process proc = candidates[0];

        log?.Invoke($"Target: {ProcessFinder.Describe(proc)}");

        // A connected bridge must belong to the selected process. This check is
        // intentionally done after target discovery to prevent cross-client
        // account switching when two Minecraft instances are open.
        log?.Invoke("Checking for an existing bridge on 25591...");
        if (await _bridge.ConnectAsync(
                maxAttempts: 4,
                delayMs: 150,
                ct: ct,
                expectedProcessId: proc.Id).ConfigureAwait(false))
        {
            IsInjected = true;
            Status = "Connected to existing bridge";
            log?.Invoke(Status);
            return true;
        }

        string dllPath = ResolveDllPath();
        if (!File.Exists(dllPath))
        {
            Status = $"switcher.dll not found at {dllPath}. Run build_dll.bat first.";
            log?.Invoke(Status);
            return false;
        }

        // Stage to a short ASCII path — LoadLibraryA + long/Unicode paths are fragile.
        string staged = StageDll(dllPath, log);
        dllPath = staged;

        log?.Invoke($"Injecting {dllPath}...");
        bool ok = await Task.Run(() => NativeInjector.Inject(proc.Id, dllPath, log), ct).ConfigureAwait(false);
        if (!ok)
        {
            Status = "Injection failed (see log)";
            // Surface bridge log location for debugging crashes
            try
            {
                string bridgeLog = Path.Combine(Path.GetDirectoryName(dllPath) ?? "", "lunar_cookies_bridge.log");
                if (File.Exists(bridgeLog))
                    log?.Invoke($"Bridge log: {bridgeLog}");
                else
                    log?.Invoke($"No bridge log at {bridgeLog} (DLL may have crashed before logging).");
            }
            catch { /* ignore */ }
            return false;
        }

        log?.Invoke("Waiting for bridge TCP...");
        if (!await _bridge.ConnectAsync(
                maxAttempts: 50,
                delayMs: 200,
                ct: ct,
                expectedProcessId: proc.Id).ConfigureAwait(false))
        {
            Status = "Bridge injected but its PID-verified handshake failed on port 25591";
            log?.Invoke(Status);
            return false;
        }

        IsInjected = true;
        Status = "Injected and connected";
        log?.Invoke(Status);
        return true;
    }

    private static string StageDll(string sourceDll, Action<string>? log)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "LunarCookies");
            Directory.CreateDirectory(dir);
            string version = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourceDll)))[..12];
            string dest = Path.Combine(dir, $"switcher-{version}.dll");
            if (!File.Exists(dest))
                File.Copy(sourceDll, dest, overwrite: false);
            log?.Invoke($"Staged DLL to {dest}");
            return dest;
        }
        catch (Exception ex)
        {
            log?.Invoke($"DLL staging skipped: {ex.Message}");
            return sourceDll;
        }
    }

    private static string ResolveDllPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "switcher.dll"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Bridge", "switcher.dll")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Bridge", "switcher.dll")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "switcher.dll")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public void Disconnect()
    {
        _bridge.Disconnect();
        IsInjected = false;
        Status = "Disconnected";
    }
}
