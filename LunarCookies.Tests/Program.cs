using LunarCookies.Core;

var tests = new (string Name, Action Run)[]
{
    ("Localts labeled token", () =>
    {
        ParsedCookies parsed = CookieParser.FromText(
            "0123456789abcdef0123456789ab\nexample:M.Csynthetic-token");
        Assert(parsed.RefreshToken == "M.Csynthetic-token");
        Assert(parsed.Cookies.Count == 0);
    }),
    ("Localts bare token", () =>
    {
        ParsedCookies parsed = CookieParser.FromText("M.Csynthetic-token");
        Assert(parsed.RefreshToken == "M.Csynthetic-token");
    }),
    ("Seven-field Netscape export", () =>
    {
        ParsedCookies parsed = CookieParser.FromText(
            ".login.live.com\tTRUE\t/\tTRUE\t1999999999\tJSH\tvalue\n" +
            "login.live.com\tFALSE\t/\tTRUE\t1999999999\t__Host-MSAAUTHP\tauth-value\n");
        Assert(parsed.Cookies.Count == 2);
        Assert(parsed.ToSisuCookieHeader().Contains("__Host-MSAAUTHP=auth-value"));
    }),
    ("Netscape HTTP-only prefix", () =>
    {
        ParsedCookies parsed = CookieParser.FromText(
            "# Netscape HTTP Cookie File\n" +
            "#HttpOnly_login.live.com\tFALSE\t/\tTRUE\t1999999999\t__Host-MSAAUTHP\tauth-value\n");
        Assert(parsed.Cookies.Count == 1);
        Assert(parsed.Cookies.Values.Single().Domain == "login.live.com");
    }),
    ("Space-separated Netscape export", () =>
    {
        ParsedCookies parsed = CookieParser.FromText(
            "login.live.com FALSE / TRUE 1999999999 __Host-MSAAUTH auth-value");
        Assert(parsed.Cookies.Count == 1);
    }),
    ("Cookie header", () =>
    {
        ParsedCookies parsed = CookieParser.FromText(
            "__Host-MSAAUTH=one; __Host-MSAAUTHP=two; other=three");
        Assert(parsed.Cookies.Count == 3);
    }),
    ("Reject missing authentication cookie", () =>
    {
        AssertThrows<CookieAuthException>(() =>
            CookieParser.FromText("ordinary=value; second=value"));
    }),
    ("Reject garbage", () =>
    {
        AssertThrows<CookieAuthException>(() =>
            CookieParser.FromText("not a supported account format"));
    })
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void Assert(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Assertion failed.");
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
