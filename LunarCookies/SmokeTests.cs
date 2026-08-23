using System;
using System.IO;
using LunarCookies.Core;

namespace LunarCookies;

/// <summary>Optional CLI smoke tests: LunarCookies.exe --smoke-parser</summary>
internal static class SmokeTests
{
    public static int RunCookieAuthSmoke(string path)
    {
        try
        {
            ParsedCookies parsed = CookieParser.FromPath(path);
            _ = CookieAuth.AuthenticateAsync(parsed).GetAwaiter().GetResult();
            Console.WriteLine("OK:   Cookie account authenticated.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: Cookie account authentication: {ex.Message}");
            return 1;
        }
    }

    public static int RunParserSmoke()
    {
        int fails = 0;
        void Expect(bool cond, string msg)
        {
            Console.WriteLine((cond ? "OK:   " : "FAIL: ") + msg);
            if (!cond) fails++;
        }

        var localts = CookieParser.FromText("player:M.Csynthetic-token");
        Expect(!string.IsNullOrEmpty(localts.RefreshToken), "Localts username:token");
        Expect(localts.RefreshToken.StartsWith("M.C", StringComparison.Ordinal), "Localts token prefix");

        var bare = CookieParser.FromText("M.CabcdefTOKEN");
        Expect(bare.RefreshToken.StartsWith("M.C", StringComparison.Ordinal), "Bare Localts token");

        var header = CookieParser.FromText("__Host-MSAAUTH=abc; __Host-MSAAUTHP=def; other=1");
        Expect(header.Cookies.Count >= 2, "Cookie header parse");
        Expect(string.IsNullOrEmpty(header.RefreshToken), "Cookie header has no refresh");

        var netscape = CookieParser.FromText(
            "# Netscape\n" +
            ".login.live.com\tTRUE\t/\tTRUE\t0\t__Host-MSAAUTH\tvalue1\n" +
            "login.live.com\tFALSE\t/\tTRUE\t0\t__Host-MSAAUTHP\tvalue2\n");
        Expect(netscape.Cookies.Count == 2, "Netscape two cookies");

        var httpOnlyNetscape = CookieParser.FromText(
            "# Netscape HTTP Cookie File\n" +
            "#HttpOnly_login.live.com\tFALSE\t/\tTRUE\t0\t__Host-MSAAUTHP\tvalue\n");
        Expect(httpOnlyNetscape.Cookies.Count == 1, "Netscape #HttpOnly_ cookie");

        try
        {
            CookieParser.FromText("not a cookie at all");
            Expect(false, "Should reject garbage");
        }
        catch (CookieAuthException)
        {
            Expect(true, "Rejects garbage");
        }

        Expect(OfflineAccount.ComputeOfflineUuid("Notch") == "b50ad385-829d-3141-a216-7e7d7539ba7f",
            "Offline UUID matches nameUUIDFromBytes vector (Notch)");
        Expect(OfflineAccount.TryCreate(" Notch ", out string _, out string offlineUuid, out _)
            && offlineUuid == "b50ad385829d3141a2167e7d7539ba7f",
            "Offline profile creation");
        Expect(!OfflineAccount.TryCreate("bad name", out _, out _, out _), "Rejects invalid cracked username");

        string sampleDirectory = Path.Combine(Directory.GetCurrentDirectory(), "cookies");
        if (Directory.Exists(sampleDirectory))
        {
            int sample = 0;
            foreach (string path in Directory.EnumerateFiles(sampleDirectory, "*.txt"))
            {
                sample++;
                try
                {
                    ParsedCookies parsed = CookieParser.FromPath(path);
                    Expect(parsed.Cookies.Count > 0 || !string.IsNullOrWhiteSpace(parsed.RefreshToken),
                        $"Private cookie sample #{sample} parses");
                    if (parsed.Cookies.Count > 0)
                    {
                        CookieAuth.ValidateCookieImportForSmoke(parsed);
                        Expect(true, $"Private cookie sample #{sample} loads into the Microsoft cookie container");
                    }
                }
                catch (CookieAuthException)
                {
                    Expect(false, $"Private cookie sample #{sample} parses");
                }
            }
        }

        Console.WriteLine(fails == 0 ? "\nAll parser tests passed." : $"\n{fails} test(s) failed.");
        return fails == 0 ? 0 : 1;
    }
}
