using LunarCookies.Core;
using System.Text.Json;

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
    }),
    ("Minecraft access token (bare MCA JWT)", () =>
    {
        string jwt = FakeMcaJwt("2533274909086470", "c812a564-a9da-437f-a33c-a32760e3156f", "Sv2Void7vh");
        ParsedCookies parsed = CookieParser.FromText(jwt);
        Assert(string.IsNullOrWhiteSpace(parsed.RefreshToken));
        Assert(parsed.Cookies.Count == 0);
        Assert(parsed.AccessToken == jwt);
    }),
    ("Minecraft access token with Bearer prefix and JSON wrapper", () =>
    {
        string jwt = FakeMcaJwt("2533274909086470", "c812a564-a9da-437f-a33c-a32760e3156f", "Sv2Void7vh");
        ParsedCookies bearer = CookieParser.FromText("Bearer " + jwt);
        Assert(bearer.AccessToken == jwt);
        ParsedCookies json = CookieParser.FromText("{\"mcToken\":\"" + jwt + "\"}");
        Assert(json.AccessToken == jwt);
    }),
    ("Reject JWT without Minecraft profile", () =>
    {
        string jwt = FakeJwt(("xuid", "2535443995591896"), ("exp", "1786969129"));
        AssertThrows<CookieAuthException>(() => CookieParser.FromText(jwt));
    }),
    ("Server address host only", () =>
    {
        Assert(MinecraftServerAddress.TryParse("play.hypixel.net", out MinecraftServerAddress parsed));
        Assert(parsed.Host == "play.hypixel.net");
        Assert(parsed.Port == 25565);
        Assert(!parsed.PortSpecified);
    }),
    ("Server address with port", () =>
    {
        Assert(MinecraftServerAddress.TryParse("mc.example.com:25566", out MinecraftServerAddress parsed));
        Assert(parsed.Host == "mc.example.com" && parsed.Port == 25566 && parsed.PortSpecified);
    }),
    ("Server address IPv6", () =>
    {
        Assert(MinecraftServerAddress.TryParse("[::1]:25565", out MinecraftServerAddress parsed));
        Assert(parsed.Host == "::1" && parsed.Port == 25565);
    }),
    ("Reject invalid server port", () =>
    {
        Assert(!MinecraftServerAddress.TryParse("example.com:0", out _));
        Assert(!MinecraftServerAddress.TryParse("example.com:70000", out _));
        Assert(!MinecraftServerAddress.TryParse("", out _));
    }),
    ("Flatten MOTD extra components", () =>
    {
        using JsonDocument doc = JsonDocument.Parse(
            """{"text":"Hello ","extra":[{"text":"§aWorld"}]}""");
        Assert(ServerPing.FlattenMotd(doc.RootElement) == "Hello World");
    }),
    ("Parse server list ping status", () =>
    {
        ServerPingResult result = ServerPing.ParseStatus(
            """{"version":{"name":"1.21.4"},"players":{"max":100,"online":12},"description":{"text":"Test"}}""",
            42);
        Assert(result.Online && result.LatencyMs == 42);
        Assert(result.OnlinePlayers == 12 && result.MaxPlayers == 100);
        Assert(result.Description == "Test");
        Assert(result.PlayersLabel == "12/100");
    }),
    ("Lunar accounts.json upsert preserves other accounts", () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"lunar-cookies-test-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"accounts":{"keep-me":{"username":"Other"}},"mojangClientToken":"keep"}""");
            var manager = new LunarAccountManager(path);
            string token = FakeJwt(("xuid", "2535443995591896"), ("exp", "1786969129"));
            LunarAccountWriteResult result = manager.Upsert(
                "Player", "12345678123456781234567812345678", token, "M.Crefresh", setActive: true);
            Assert(result.Success);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert(doc.RootElement.GetProperty("mojangClientToken").GetString() == "keep");
            Assert(doc.RootElement.GetProperty("accounts").TryGetProperty("keep-me", out _));
            JsonElement written = doc.RootElement.GetProperty("accounts")
                .GetProperty("12345678123456781234567812345678");
            Assert(written.GetProperty("accessToken").GetString() == token);
            Assert(written.GetProperty("refreshToken").GetString() == "M.Crefresh");
            Assert(written.GetProperty("remoteId").GetString() == "2535443995591896");
            Assert(written.GetProperty("localId").GetString() == "12345678123456781234567812345678");
            Assert(written.GetProperty("username").GetString() == "Player");
            Assert(written.GetProperty("minecraftProfile").GetProperty("name").GetString() == "Player");
            Assert(doc.RootElement.GetProperty("activeAccountLocalId").GetString()
                == "12345678123456781234567812345678");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }),
    ("Lunar JWT claim helpers", () =>
    {
        string token = FakeJwt(("xuid", "2535443995591896"), ("exp", "1786969129"));
        Assert(LunarAccountManager.TryReadJwtString(token, "xuid") == "2535443995591896");
        Assert(LunarAccountManager.TryReadJwtExpiry(token) == DateTimeOffset.FromUnixTimeSeconds(1786969129));
    }),
    ("Offline UUID matches nameUUIDFromBytes vectors", () =>
    {
        Assert(OfflineAccount.ComputeOfflineUuid("Notch") == "b50ad385-829d-3141-a216-7e7d7539ba7f");
        Assert(OfflineAccount.ComputeOfflineUuid("Player_1") == "914e9094-dd43-376a-a052-b63bf94389aa");
    }),
    ("Offline profile creation", () =>
    {
        Assert(OfflineAccount.TryCreate(" Notch ", out string name, out string uuid, out string error));
        Assert(error == "");
        Assert(name == "Notch");
        Assert(uuid == "b50ad385829d3141a2167e7d7539ba7f");
    }),
    ("Reject invalid cracked usernames", () =>
    {
        foreach (string? bad in new[] { null, "", "  ", "ab", "thisusernameiswaytoolong", "bad name", "bad-name", "pl@yer" })
        {
            Assert(!OfflineAccount.TryCreate(bad, out _, out _, out _));
        }
        Assert(OfflineAccount.IsValidName("okname"));
        Assert(!OfflineAccount.IsValidName(null));
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

static string FakeJwt(params (string Name, string Value)[] claims)
{
    var payload = new System.Text.StringBuilder("{");
    for (int i = 0; i < claims.Length; i++)
    {
        if (i > 0)
            payload.Append(',');
        payload.Append('"').Append(claims[i].Name).Append('"').Append(':');
        if (claims[i].Name == "exp" && long.TryParse(claims[i].Value, out _))
            payload.Append(claims[i].Value);
        else
            payload.Append('"').Append(claims[i].Value).Append('"');
    }
    payload.Append('}');
    return "eyJhbGciOiJub25lIn0." + Base64Url(payload.ToString()) + ".sig";
}

static string FakeMcaJwt(string xuid, string uuid, string name)
{
    string header = Base64Url("{\"kid\":\"049181\",\"alg\":\"RS256\"}");
    string payload = Base64Url(
        "{\"xuid\":\"" + xuid + "\",\"agg\":\"Adult\",\"sub\":\"da307ebc-556f-4b3a-b1ca-864ffdede4b6\"," +
        "\"auth\":\"XBOX\",\"ns\":\"default\",\"roles\":[],\"iss\":\"authentication\"," +
        "\"flags\":[\"multiplayer\"],\"profiles\":{\"mc\":\"" + uuid + "\"}," +
        "\"platform\":\"PC_LAUNCHER\",\"tid\":\"E99B0\"," +
        "\"pfd\":[{\"type\":\"mc\",\"id\":\"" + uuid + "\",\"name\":\"" + name + "\"}]," +
        "\"xid\":\"" + xuid + "\",\"nbf\":1790831105,\"exp\":1790917505,\"iat\":1790831105," +
        "\"aid\":\"00000000-0000-0000-0000-0000402b5328\"}");
    return "eyJ" + header.Substring(3) + "." + payload + ".sig";
}

static string Base64Url(string value)
{
    string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
