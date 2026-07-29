# Security

## Supported version

Security fixes are applied to the latest release on the default branch.

## Reporting

Please report vulnerabilities privately through GitHub Security Advisories
instead of opening a public issue. Include reproduction steps, affected
versions, and the expected impact.

## Local threat model

Lunar Cookies is intended for accounts whose owners accept the risks of local
session switching. Account tokens are stored in plaintext under
`%AppData%\LunarCookies\accounts.json`, and the injected bridge accepts commands
over a loopback-only TCP port. Software already running as the same Windows user
may be able to read or interfere with those credentials.

Do not use the application on an untrusted or shared Windows account. Never
attach cookie files, tokens, account stores, or authentication logs to public
issues.
