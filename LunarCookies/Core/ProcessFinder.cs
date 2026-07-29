using System.Diagnostics;

namespace LunarCookies.Core;

/// <summary>
/// Finds a Minecraft JVM without ever falling back to an unrelated Java window.
/// Client and loader names deliberately do not affect compatibility: Forge,
/// Fabric, Lunar and vanilla all eventually present a Minecraft game window.
/// </summary>
public static class ProcessFinder
{
    private static readonly string[] StrongTitleKeywords =
    {
        "minecraft", "lunar client"
    };

    private static readonly string[] StrongPathKeywords =
    {
        ".lunarclient", "lunarclient", ".minecraft", "minecraft"
    };

    public static IReadOnlyList<Process> FindMinecraftProcesses()
    {
        return Process.GetProcesses()
            .Where(IsJavaProcess)
            .Select(p => new { Proc = p, Score = Score(p) })
            .Where(x => x.Score >= 100)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => SafeStartTime(x.Proc))
            .Select(x => x.Proc)
            .ToList();
    }

    public static Process? FindMinecraftProcess()
    {
        IReadOnlyList<Process> matches = FindMinecraftProcesses();
        return matches.Count == 1 ? matches[0] : null;
    }

    // Kept for source compatibility with older callers.
    public static Process? FindLunarProcess() => FindMinecraftProcess();

    private static bool IsJavaProcess(Process p)
    {
        try
        {
            return p.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase)
                || p.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int Score(Process p)
    {
        int score = 0;
        string title = "";
        try { title = p.MainWindowTitle ?? ""; } catch { /* process may exit */ }

        foreach (string keyword in StrongTitleKeywords)
        {
            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                score += 100;
        }

        try
        {
            string? path = p.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (string keyword in StrongPathKeywords)
                {
                    if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        score += 100;
                }
            }
        }
        catch
        {
            // Access to a higher-integrity process is expected to fail. A
            // Minecraft/Lunar window title remains a safe positive signal.
        }

        try
        {
            if (p.MainWindowHandle != IntPtr.Zero && score > 0)
                score += 10;
        }
        catch { /* process may exit */ }

        return score;
    }

    private static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return DateTime.MinValue; }
    }

    public static string Describe(Process? p)
    {
        if (p == null) return "none";
        string title = "";
        try { title = p.MainWindowTitle; } catch { /* process may exit */ }
        return $"PID {p.Id} ({p.ProcessName}) '{title}'";
    }
}
