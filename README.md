# Claude usage widget

A small always-on-top Windows overlay that shows the Claude Code usage limits (5-hour session and weekly windows) of
every account you run through a `CLAUDE_CONFIG_DIR` alias, built on the FluentGpu engine.

- Accounts are discovered from `%USERPROFILE%\.claude-*` folders that hold a `.credentials.json` (one per alias, e.g.
  `claude-work` → `~/.claude-work`).
- Usage comes from the endpoint behind the CLI's `/usage`. An expired access token is refreshed and the rotated tokens
  are written back to the alias's `.credentials.json` (a one-time `.credentials.json.widget-bak` is kept), so the CLI
  stays signed in. Failed refreshes (429 included) back off per account.
- Four layouts (Compact, Rings, Strip, Detailed), picked from the "..." menu or a right-click; the window sizes itself
  to the layout. Mica or acrylic backdrop, always-on-top toggle, remembered position, a tray icon whose badge shows the
  best account's headroom. Settings live in `HKCU\Software\ClaudeUsageWidget\Widget\Settings`.

## Build

Needs the .NET 10 SDK and a FluentGpu engine checkout. `Directory.Build.props` points `EngineRoot` at
`..\lane-a\fluent-gpu` (the `feat/overlay-window` branch, christosk92/fluent-gpu#58); override it in a git-ignored
`EngineRoot.local.props`:

```xml
<Project><PropertyGroup><EngineRoot>C:\wavee\fgpu\fluent-gpu\</EngineRoot></PropertyGroup></Project>
```

```powershell
dotnet build                      # bin\Debug\net10.0\ClaudeUsageWidget.exe
dotnet publish -c Release -o publish   # NativeAOT single exe
```

Screenshot of the composed window: `ClaudeUsageWidget.exe --fg shot-window=shot.png --fg shot-exit`.
