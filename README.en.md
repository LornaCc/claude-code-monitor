# CC Monitor

[简体中文](README.md) | [English](README.en.md)

CC Monitor is a Windows desktop monitor for Claude Code. It collects sessions from multiple terminals into one floating window, shows working, attention, completion, interruption, error, and stale states, and can return you to the corresponding VS Code window and terminal.

![CC Monitor session dashboard](docs/images/cc-monitor.png)

## Features

### Multi-session status monitoring

- Monitor multiple Claude Code sessions in real time and display them grouped by status or in a flat list.
- Distinguish `RUNNING`, `NEEDS ATTENTION`, `DONE`, `INTERRUPTED`, `ERROR`, and `STALE` instead of treating every stop as an error.
- Detect local transcript interruption markers after `Ctrl+C` or `Esc`, even when a Stop Hook does not arrive promptly.
- Keep `/clear`, new sessions, and a resumed older conversation in another terminal separate by combining session and terminal identity.
- Flash completed sessions and optionally play a sound or show Windows notifications. Sessions can be renamed, hidden, or removed.
- Show locally available Claude Code usage, cost, and context data in the Usage Dashboard.

### Precise VS Code window and terminal focusing

- Every VS Code window publishes its workspace, terminal PID, cwd, stable token, and active terminal through Terminal Bridge. Every ordinary terminal receives a unique Bridge token, so CC Monitor no longer guesses a terminal PID from the Hook process tree.
- Run `claude` directly in any ordinary terminal. The first `SessionStart` or `UserPromptSubmit` automatically claims the active terminal in the focused window and saves its token and PID; Managed Terminal and manual Bind are not prerequisites.
- Terminal selection and native window activation are separate stages. The extension selects the terminal first; the desktop app then brings the selected VS Code window forward.
- When a workspace or cwd contains several terminals or sessions, explicit bindings and stable tokens take priority. Ambiguous requests report no match instead of selecting an arbitrary first VS Code window.
- If VS Code does not expose a native-window focus command, the app uses a Win32 fallback. Focusing preserves geometry: maximized windows remain maximized, normal and snapped windows keep their size and position, and only genuinely minimized windows are restored.
- Bridge results distinguish `matched`, `noMatch`, and `bridgeNotRunning`. Logs include the request, match strategy, target bridge, and native activation result.

### Terminal association strategies

Matching proceeds from most to least reliable:

1. **Automatic Bridge token**: the extension assigns every terminal a unique token, and the Hook automatically claims the active terminal when Claude starts or receives a prompt.
2. **Managed environment token**: optionally run **CC Monitor: Create Managed Claude Terminal**. Its environment token is inherited at Claude startup, but this is not required for normal use.
3. **Manual binding fallback**: use **Bind terminal…** and **CC Monitor: Bind Active Terminal to Session** only when automatic claiming cannot identify one terminal safely.
4. **Safe cwd/workspace bootstrap**: terminal cwd or workspace boundaries constrain the initial claim; subsequent focusing uses the saved token/PID.

To migrate an existing terminal, run **CC Monitor: Migrate Active Terminal**. The extension creates a tokenized replacement at the same cwd and starts Claude while leaving the old terminal open.

### Hook and local-data safety

- Hook payloads use a tolerant parser, so one malformed payload does not leave Claude Code reporting persistent Hook errors.
- Session state uses file locks and atomic writes to reduce races between concurrent Hook processes.
- Hook failures are non-blocking by default and exit with code `0` so they do not interrupt Claude Code.
- Raw Hook payloads are not stored by default. Logs contain structural diagnostics and hashes; payload capture requires the explicit `CCMONITOR_DEBUG_HOOKS=1` setting.
- Session, bridge, binding, usage, and log data stays under `%USERPROFILE%\.cc-monitor` and is not uploaded.

## Download and installation

Download `CCMonitor-v0.5.2-win-x64.zip` from [Gitea Releases](https://gitea.lan.fasteurai.com/linruyue/claude-code-monitor-desktop/releases) or [GitHub Releases](https://github.com/LornaCc/claude-code-monitor/releases).

The Windows x64 package is self-contained; .NET Runtime and Node.js are not required.

1. Extract the ZIP completely. Do not run it from the archive preview.
2. Double-click `Install-CCMonitor.cmd`.
3. Wait for the installer to report success.
4. Run **Developer: Reload Window** in every open VS Code window.
5. Run `claude` normally in any VS Code integrated terminal. Managed Terminal and manual Bind are optional.

The installer stops every older CC Monitor instance, installs v0.5.2 under `%LOCALAPPDATA%\Programs\CCMonitor\0.5.2`, repoints Claude Code Hooks and StatusLine, force-installs and verifies Terminal Bridge 0.5.2, updates Start menu and Desktop shortcuts, and starts only the new build. Older directories may remain for Claude Code processes that have not reloaded yet, but installer-managed Hooks, shortcuts, and running processes point to the new build.

To reinstall Hooks only, use **Reinstall Hooks** in Settings or run:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\install-hooks.ps1 `
  -AppPath "C:\Path\To\CCMonitor.App.exe"
```

## Statuses

| Status | Meaning |
| --- | --- |
| `IDLE` | The session is registered and waiting for a task |
| `RUNNING` | Claude is working |
| `NEEDS ATTENTION` | Waiting for permission, tool confirmation, or user action |
| `DONE` | The current turn finished and is waiting for new input |
| `INTERRUPTED` | The user stopped the turn through `Ctrl+C`, `Esc`, or an equivalent action |
| `ERROR` | Claude or the Hook pipeline failed unexpectedly |
| `STALE` | No recent event arrived; the process may have ended or a Hook may be missing |

`STALE` is a UI inference and does not overwrite the last real Hook state on disk. Its threshold is configurable in Settings.

## Local architecture

```text
Claude Code Hooks / StatusLine
        -> CCMonitor.Hook.exe / CCMonitor.StatusLine.exe
        -> %USERPROFILE%\.cc-monitor\sessions
        -> CCMonitor.App.exe

VS Code Terminal Bridge (one registration per window)
        -> %USERPROFILE%\.cc-monitor\terminal-bridges
        <-> focus request / result
        -> target terminal + target VS Code window
```

Useful diagnostics:

- App log: `%USERPROFILE%\.cc-monitor\logs\cc-monitor-app.log`
- Hook log: `%USERPROFILE%\.cc-monitor\logs\cc-monitor-hook.log`
- StatusLine log: `%USERPROFILE%\.cc-monitor\logs\cc-monitor-statusline.log`
- VS Code: **Output → CC Monitor Terminal Bridge**

## Troubleshooting

- **Terminal Bridge is not running**: verify the latest extension included in the release package and run **Developer: Reload Window** in every relevant VS Code window.
- **Terminal not found**: activate the target terminal and submit one prompt so the session can claim it automatically. Use **Bind terminal…** only if it remains ambiguous.
- **Several terminals share one cwd**: `SessionStart`/`UserPromptSubmit` claims the active terminal in the focused window. Manual binding is required only when no unique claim is safe.
- **Status does not change**: inspect `/hooks` in Claude Code and the Hook and StatusLine logs.
- **Hook reports `InvalidJson`**: reinstall the latest Hooks. Enable `CCMONITOR_DEBUG_HOOKS=1` only when local raw payload capture is acceptable.
- **A window does not come forward**: inspect `bridgeWindowFocused`, `windowActivated`, `windowInitialState`, and `windowRestoreInvoked` in the App log.
- **The installation directory moved**: reinstall Hooks, preferably by running `Install-CCMonitor.cmd` again.

## Build from source

.NET 8 SDK, Node.js, and PowerShell are required:

```powershell
dotnet test .\CCMonitor.sln -c Release
PowerShell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

The script runs the .NET and Terminal Bridge tests, then writes the self-contained app, Hook, StatusLine, VSIX, one-click installer, and ZIP to `artifacts`.

## License

[MIT](LICENSE)
