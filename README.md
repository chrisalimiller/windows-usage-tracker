# Windows Usage Tracker

Shows your **Claude** and **Codex** plan limits inside the Windows taskbar, just left of the
clock and tray icons. It is not a tray icon and not a floating window.

```
Claude  5h 23%  7d 41%
Codex   5h 12%  7d  8%
```

- **5h** is the rolling 5-hour session limit, **7d** the weekly limit (percent used).
- Numbers turn amber at 70% and red at 90%.
- Hover for exact numbers, reset times and plan; double-click to refresh; right-click for
  **Refresh now**, **Start with Windows**, **Open log folder** and **Exit**.
- Follows light and dark taskbars, display scaling, taskbar size changes and Explorer restarts.

## Requirements

- Windows 11 (Windows 10 should work too).
- **Claude:** Claude Code signed in with your Claude subscription, on Windows and/or in WSL.
- **Codex:** the Codex CLI signed in with your ChatGPT plan (`codex login`).
  An API-key sign-in has no plan limits, so it isn't shown.

A provider that isn't signed in anywhere is hidden; if neither is, both rows show `--`.

## Install and run

1. Open the latest successful run of the **build** workflow in this repo's **Actions** tab
   (or the **Checks** tab of a pull request) and download the artifact
   **WindowsUsageTracker-win-x64** (or **-win-arm64** for an ARM PC). It's a zip with one file.
2. Put `WindowsUsageTracker.exe` somewhere permanent, for example
   `%LOCALAPPDATA%\Programs\WindowsUsageTracker\`.
3. Double-click it. The exe isn't code-signed yet, so SmartScreen may warn you the first
   time: choose **More info → Run anyway**.
4. Right-click the widget and tick **Start with Windows** if you want it at sign-in.

It's a single self-contained exe (about 35 MB); no .NET install is needed. Only one copy runs at a time.

## Where the numbers come from

| | Credentials (read only, never modified) | Endpoint |
|---|---|---|
| Claude | `%CLAUDE_CONFIG_DIR%\.credentials.json` if set, then `%USERPROFILE%\.claude\.credentials.json`, then `~/.claude/.credentials.json` in each WSL distro | `GET https://api.anthropic.com/api/oauth/usage` |
| Codex | `%CODEX_HOME%\auth.json` if set, otherwise `%USERPROFILE%\.codex\auth.json`, then `~/.codex/auth.json` in each WSL distro | `GET https://chatgpt.com/backend-api/wham/usage` |

- The first sign-in that hasn't expired wins. WSL is only checked when the Windows file is
  missing or expired, because opening `\\wsl.localhost\…` can start a distro.
- The app never refreshes or writes tokens. Claude Code and Codex refresh their own sign-ins
  when you use them; if a token has expired the label turns amber and the tooltip says to run
  the CLI once.
- It polls every 90 seconds, backs off up to 15 minutes on errors, and honors `Retry-After`.
- No telemetry and no server of its own. Tokens go only to the two endpoints above.
- **Both endpoints are undocumented** and can change without notice. Each lives in one place
  (`src/UsageTracker.Core/Providers.cs` and the two parsers) so it's easy to fix if it breaks;
  when a response can't be understood the tooltip says so.

## Troubleshooting

**Check the sign-ins without the taskbar part:**

```
WindowsUsageTracker.exe --check
```

shows what each provider returned in a message box and exits.

**Debug the taskbar embedding:** exit the running copy (right-click → Exit), then start it with

```
WindowsUsageTracker.exe --diagnose
```

and open `%LOCALAPPDATA%\WindowsUsageTracker\usage-tracker.log` (right-click → **Open log folder**).
With `--diagnose` the log lists every child window of the taskbar (class, rectangle, visibility),
the computed position on each layout change, the DPI, z-order corrections and every poll.
Useful lines:

- `Embedded in taskbar 0x… as child window 0x…` means the widget is a child of `Shell_TrayWnd`.
- `Layout: taskbar … tray … -> widget WxH at screen (x,y)` is where it was placed.
- `Z-order: sibling … was above the widget; raising` means the taskbar's own content had
  covered it and it was moved back on top.
- `Couldn't place the widget in the taskbar: …` means embedding failed. The app then shows a
  warning icon in the notification area with the reason (it never silently floats) and keeps
  retrying every 15 seconds.

Tokens and response bodies are never written to the log.

## How it works

- `src/UsageTracker.Core` (cross-platform): credential parsing, the two usage providers,
  response parsers, polling and backoff, text formatting. Covered by unit tests.
- `src/UsageTracker` (Windows): plain Win32 through P/Invoke, no WinForms/WPF.
  - A hidden controller window receives Explorer's `TaskbarCreated` broadcast, display, DPI and
    theme changes, and poll results.
  - The widget is a layered (`WS_EX_LAYERED`) child window of `Shell_TrayWnd`, positioned just
    left of `TrayNotifyWnd` and sized to the taskbar height. It's drawn with GDI+ into a
    premultiplied bitmap and shown with `UpdateLayeredWindow`, so the taskbar shows through.
  - A one-second layout check re-attaches after Explorer restarts and follows tray width, taskbar
    size, auto-hide and DPI changes. It also keeps the widget above the Windows 11 taskbar's own
    XAML content.
  - Per-monitor DPI aware (v2) and declares Windows 8+ compatibility in `app.manifest`, which
    layered child windows require.

Only the primary taskbar is used for now.

## Build from source

Needs the .NET 8 SDK.

```
dotnet test tests/UsageTracker.Core.Tests
dotnet publish src/UsageTracker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out/win-x64
```

The solution also builds on Linux and macOS (`EnableWindowsTargeting`), but the app only runs on Windows.

## License

GPL-3.0, see [LICENSE](LICENSE).
