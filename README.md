# Lunar Cookie Account Switcher

A portable WinUI 3 account switcher for multiple Minecraft Java clients. It
supports Localts refresh tokens (`M.C…`) and Netscape or cookie-header
Microsoft session cookies.

## Showcase

[![Lunar Cookies account selection screen](docs/showcase.png)](https://youtu.be/t8smWCKkc6M)

Watch the [Lunar Cookies showcase video](https://youtu.be/t8smWCKkc6M).

## Client compatibility

The injected helper resolves the Minecraft singleton and session/user object by
validated runtime structure, with known names used only as fast paths.

- Minecraft 1.8.9: Forge and Lunar Client legacy session shape
- Minecraft 1.21.x: Fabric/intermediary and Lunar + Fabric session shape
- Minecraft 26.1 and 26.2: official-name, Fabric, and Lunar modern user shape

The 1.8.9 four-string constructor, the 1.21 five-argument Yarn session
constructor, and the official 26.1/26.2 `User(String, UUID, String, Optional,
Optional)` ABI are covered. Unknown or ambiguous layouts fail closed; the
bridge will not write to a guessed field.

## Interface

- **Accounts** displays saved accounts in a three-column card grid. Select a
  card and use the bottom bar to switch, restore the account Lunar launched
  with, or delete the saved credentials.
- Account files can be dragged onto any part of the window. Multiple dropped
  files are authenticated sequentially with spacing between requests.
- Transient Microsoft/Xbox/Minecraft failures are retried with backoff and
  `Retry-After` support. Batch results identify each failed filename and safe
  HTTP status without logging cookies, tokens, or response bodies.
- **Injection** contains bridge connection controls, current Lunar session
  details, process detection, and the diagnostic log.
- **Settings** controls minimize-to-tray behavior and whether the switcher exits
  automatically after the detected Minecraft process closes.
- Switching accounts automatically injects or reconnects the bridge when
  needed. Switching and restoration are blocked while Lunar is in a world or
  connected to a server.
- The interface follows the Windows light or dark theme and keeps the selected
  account and bridge state synchronized between tabs.

## Build

Prerequisites:

- .NET 8 SDK
- Internet access for the first restore of Microsoft Windows App SDK 2.3.1
- MinGW-w64 `g++` for `switcher.dll`
- JDK 8+ (`javac`) for the injected session helper

Build the native bridge and publish the unpackaged, self-contained x64 WinUI 3
application:

```bat
build_dll.bat
build_exe.bat
```

The portable output is written to `publish\`. Distribute
`LunarCookies.exe` and `switcher.dll` together. The WinUI and .NET runtime
dependencies are bundled into the executable and extracted automatically at
startup, so the Windows App SDK runtime does not need to be installed
separately.

For a Debug build:

```bat
build_dll.bat
dotnet run --project LunarCookies -c Debug
```

Run the parser smoke checks with:

```bat
LunarCookies\bin\Debug\net8.0-windows10.0.19041.0\win-x64\LunarCookies.exe --smoke-parser
```

Run the synthetic parser regression suite with:

```bat
dotnet run --project LunarCookies.Tests -c Release
```

## Usage

1. Launch a supported Minecraft client and remain on the main menu.
2. Run `LunarCookies.exe`.
3. On **Accounts**, import a Localts token or cookie file.
4. Select an account and click **Use account**. The bridge connects
   automatically when needed.
5. Join a server after the session status shows the selected account.
6. Disconnect to the main menu before switching or restoring an account.

**Restore launch account** restores the session Lunar started with; it is not a
true signed-out state. **Delete** removes only the saved credentials and does
not change the session currently active in Lunar.

Netscape-format cookie exports, including files named after the account
username, are supported. Cookie filenames do not affect parsing. Cookie
authentication follows Microsoft/Xbox redirects while preserving domain-scoped
cookies and response cookie updates.

By default, minimizing the window moves Lunar Cookies to the notification area;
double-click its tray icon to restore it. The app also exits after a previously
detected Minecraft process closes. Either behavior can be disabled from
**Settings**.

Accounts remain compatible with earlier releases and are stored at
`%AppData%\LunarCookies\accounts.json`. Tokens are currently stored in
plaintext, so treat this file as a secret.

Preferences are stored at `%AppData%\LunarCookies\settings.json`.

- Bridge log: `%TEMP%\lunar_cookies_bridge.log`
- UI crash log: `%TEMP%\lunar_cookies_ui_crash.log`
- TCP control port: `127.0.0.1:25591`

The TCP handshake includes protocol version and target PID. This prevents an
already-running bridge in another Minecraft instance from receiving commands.
Only one bridge can own the fixed port at a time; close other injected
instances before switching accounts.

The local security model and private vulnerability reporting instructions are
documented in [SECURITY.md](SECURITY.md).

## Compatibility design

- The UI selects only a positively identified Minecraft/Lunar Java window; it
  never falls back to an arbitrary Java application.
- The loader uses `LoadLibraryW`, verifies JVM/injector architecture, checks
  complete remote writes, and never frees remote path memory while a timed-out
  loader thread may still be using it.
- DLLs are staged under a content-hashed filename, avoiding stale or locked
  binaries when upgrading.
- The helper validates the current session value and constructor before every
  write, schedules mutations on the Minecraft thread when a scheduler is
  available, re-checks the in-world guard there, and verifies the write.
- Commands are size-limited and diagnostic logs record operation names only;
  access tokens are not written to the bridge log.

Constructor ABI smoke coverage is in
`JavaHelper/SessionSwitcherCompatibilityTest.java`.

## License

Lunar Cookies is licensed under the GNU General Public License version 3 or
later. See `LICENSE` and `NOTICE.md`.
