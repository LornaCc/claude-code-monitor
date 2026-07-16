# CC Monitor

[简体中文](README.md) | [English](README.en.md)

CC Monitor is a lightweight always-on-top Windows widget for tracking multiple Claude Code sessions in one place. It shows whether each session is working, waiting for permission, finished, or stopped with an error.

![CC Monitor session dashboard](docs/images/cc-monitor.png)

## Features

- Monitor multiple Claude Code sessions in real time
- Highlight permission prompts and other sessions that need attention
- Flash completed sessions and optionally show Windows notifications or play sounds
- Group sessions by status or display them in a flat list
- Rename sessions and hide or remove historical sessions
- Focus the corresponding VS Code window or terminal from a session
- View locally available usage data in the Usage Dashboard
- Keep the window on top, minimize it, drag it, and resize it

## Download

Download the latest `CCMonitor-*-win-x64.zip` from [Releases](https://gitea.lan.fasteurai.com/linruyue/claude-code-monitor-desktop/releases), extract it, and run `CCMonitor.App.exe`.

The Windows x64 package is self-contained, so a separate .NET Runtime installation is not required.

## Requirements

- Windows 10 or Windows 11 (x64)
- Claude Code
- VS Code for precise terminal focusing

## Quick Start

1. Extract the release archive and run `CCMonitor.App.exe`.
2. Open **Settings** and select **Reinstall Hooks**.
3. Start or continue a Claude Code session; it will appear in CC Monitor automatically.
4. Reinstall the hooks after moving the release directory so Claude Code uses the new executable path.

If PowerShell blocks the install script, use **Reinstall Hooks** inside the app or run:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\install-hooks.ps1 `
  -AppPath "C:\Path\To\CCMonitor.App.exe"
```

To uninstall the hooks:

```powershell
PowerShell -ExecutionPolicy Bypass -File .\uninstall-hooks.ps1 `
  -AppPath "C:\Path\To\CCMonitor.App.exe"
```

The installer merges with existing fields and hooks in `%USERPROFILE%\.claude\settings.json` and avoids duplicate CC Monitor entries. It creates a backup before making changes. Invalid JSON stops the installation without overwriting the file, and an existing `statusLine` is restored when CC Monitor is uninstalled.

## VS Code Terminal Integration

The release archive includes `cc-monitor-terminal-bridge-*.vsix`. Install it in VS Code and reload the window. Clicking a session in CC Monitor can then focus its VS Code window and, when possible, the corresponding terminal.

## Statuses

| Status | Meaning |
| --- | --- |
| `IDLE` | Waiting for a task |
| `RUNNING` | Claude is working |
| `NEEDS ATTENTION` | Waiting for permission or user action |
| `DONE` | Work is complete and waiting for new input |
| `ERROR` | Claude stopped unexpectedly |

## Architecture

```text
Claude Code Hooks
      -> CCMonitor.Hook.exe
      -> Local JSON State
      -> CCMonitor.App.exe
```

The hook executable reads Claude Code hook JSON from stdin, maps it through the core state machine, and atomically writes `%USERPROFILE%\.cc-monitor\sessions\<session_id>.json`. It exits with code `0` even when an internal error occurs so it does not block Claude Code.

## Build from Source

Development requires the .NET 8 SDK.

```powershell
dotnet test
PowerShell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

The self-contained package is written to `artifacts` and includes the desktop app, hook executables, install scripts, and VS Code Terminal Bridge extension.

## Privacy

- CC Monitor may read recent Claude Code transcript metadata locally to calculate usage. Message content is not uploaded or stored by CC Monitor.
- CC Monitor does not send data to a server.
- CC Monitor does not require an Anthropic API key.
- Prompt previews are disabled by default and limited to 100 characters when enabled.
- Setting `CCMONITOR_DEBUG_HOOKS=1` saves raw hook payloads under `%USERPROFILE%\.cc-monitor\debug-hooks` and should only be enabled for local debugging.

## Troubleshooting

- Status does not change: check `/hooks` in Claude Code and `%USERPROFILE%\.cc-monitor\logs\cc-monitor-hook.log`.
- CC Monitor is missing from `/hooks`: reinstall the hooks from Settings or with the install script.
- `settings.json` is invalid: repair `%USERPROFILE%\.claude\settings.json`, then reinstall the hooks.
- The app directory moved: reinstall the hooks after moving it.
- Windows notifications do not appear: enable notifications in Settings and check Windows Focus Assist.
- Sessions have the same project name: CC Monitor derives the default name from the hook `cwd`; use the session menu to assign a custom name.

## License

[MIT](LICENSE)
