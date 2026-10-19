# Claude usage widget
<img width="529" height="500" alt="image" src="https://github.com/user-attachments/assets/14cf054c-49f0-4811-bf4f-c8743d5701ca" />

A small always-on-top Windows overlay that shows the Claude Code usage limits (5-hour session and weekly windows) of
every account you run through a `CLAUDE_CONFIG_DIR` alias, built on the FluentGpu engine.

- Accounts are discovered from `%USERPROFILE%\.claude-*` folders that hold a `.credentials.json`, plus every
  `function claude-<name>` alias in the PowerShell profile (e.g. `claude-ohsnap` → `~/.claude-ohsnap`).
- "Manage accounts…" in the "..." menu adds, renames and deletes aliases: the profile function and its folder change
  together (plugin install paths are re-pointed on rename; a deleted folder goes to the Recycle Bin), and a new account
  signs in from a terminal the widget opens. Rename and delete refuse while a Claude Code session or daemon still runs
  on the account. The profile gets a one-time `.widget-bak` copy before the first edit.
- Usage comes from the endpoint behind the CLI's `/usage`. An expired access token is refreshed and the rotated tokens
  are written back to the alias's `.credentials.json` (a one-time `.credentials.json.widget-bak` is kept), so the CLI
  stays signed in. Failed refreshes (429 included) back off per account.
- The usage endpoint allows very few calls per account, so the widget only calls it when it has nothing under 2 minutes
  old: its own last results (saved in `%LOCALAPPDATA%\ClaudeUsageWidget\usage-cache.json`, along with any running
  backoff, so a restart doesn't refetch) or Claude Code's cached copy (`cachedUsageUtilization` in the alias's
  `.claude.json`). Only one widget runs at a time; launching it again shows the running one.
- A failed fetch shows its error in red; click it (or "Errors…" in the "..." menu) for the request, status, rate-limit
  headers, body and stack trace. "Copy all for LLM" copies one Markdown report with every account's state, every failure
  and the recent HTTP calls. Failures go to `errors.log` and every call to `requests.log`, next to the saved cache.
- Four layouts (Compact, Rings, Strip, Detailed), picked from the "..." menu or a right-click; the window sizes itself
  to the layout. Mica or acrylic backdrop, always-on-top toggle, remembered position, a tray icon whose badge shows the
  best account's headroom. Settings live in `HKCU\Software\ClaudeUsageWidget\Widget\Settings`.

## Build

Needs the .NET 10 SDK and a checkout of [fluent-gpu](https://github.com/christosk92/fluent-gpu) `main` (the overlay
APIs landed in christosk92/fluent-gpu#58). `Directory.Build.props` looks for it at `..\fgpu\fluent-gpu`; point it
elsewhere with a git-ignored `EngineRoot.local.props` next to the csproj:

```xml
<Project><PropertyGroup><EngineRoot>C:\wavee\fgpu\fluent-gpu\</EngineRoot></PropertyGroup></Project>
```

```powershell
dotnet build                      # bin\Debug\net10.0\ClaudeUsageWidget.exe
dotnet publish -c Release -o publish   # NativeAOT single exe
```

Screenshot of the composed window: `ClaudeUsageWidget.exe --fg shot-window=shot.png --fg shot-exit`.
