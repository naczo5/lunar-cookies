using LunarCookies.Core;

// Smoke tests for CookieParser (no network).
static int fails;

static void Expect(bool cond, string msg)
{
    if (!cond)
    {
        Console.WriteLine("FAIL: " + msg);
        fails++;
    }
    else
    {
        Console.WriteLine("OK:   " + msg);
    }
}

fails = 0;

var localts = CookieParser.FromText("player:M.Csynthetic-token");
Expect(!string.IsNullOrEmpty(localts.RefreshToken), "Localts username:token");
Expect(localts.RefreshToken.StartsWith("M.C"), "Localts token prefix");

var bare = CookieParser.FromText("M.CabcdefTOKEN");
Expect(bare.RefreshToken.StartsWith("M.C"), "Bare Localts token");

var header = CookieParser.FromText("__Host-MSAAUTH=abc; __Host-MSAAUTHP=def; other=1");
Expect(header.Cookies.Count >= 2, "Cookie header parse");
Expect(string.IsNullOrEmpty(header.RefreshToken), "Cookie header has no refresh");

var netscape = CookieParser.FromText(
    "# Netscape\n" +
    ".login.live.com\tTRUE\t/\tTRUE\t0\t__Host-MSAAUTH\tvalue1\n" +
    "login.live.com\tFALSE\t/\tTRUE\t0\t__Host-MSAAUTHP\tvalue2\n");
Expect(netscape.Cookies.Count == 2, "Netscape two cookies");

try
{
    CookieParser.FromText("not a cookie at all");
    Expect(false, "Should reject garbage");
}
catch (CookieAuthException)
{
    Expect(true, "Rejects garbage");
}

Console.WriteLine(fails == 0 ? "\nAll parser tests passed." : $"\n{fails} test(s) failed.");
Environment.Exit(fails == 0 ? 0 : 1);
