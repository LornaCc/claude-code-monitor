# CC Monitor

CC Monitor is a lightweight Windows desktop widget that shows whether Claude Code is working, waiting for permission, finished, or stopped with an error.

## Screenshot

Run `CCMonitor.App.exe` to open the always-on-top session widget.

## Requirements

- Windows 10/11
- Claude Code
- .NET 8 SDK for development
- .NET runtime only if publishing is not self-contained

## Architecture

```text
Claude Code Hooks
      -> CCMonitor.Hook.exe
      -> Local JSON State
      -> CCMonitor.App.exe
```

The hook executable reads Claude Code hook JSON from stdin, maps it through the core state machine, writes `%USERPROFILE%\.cc-monitor\sessions\<session_id>.json` atomically, and exits with code `0` even if internal errors occur.

## Status Meaning

```text
IDLE             Waiting for task
RUNNING          Claude is working
NEEDS ATTENTION  Permission required
DONE             Waiting for your input
ERROR            Claude stopped unexpectedly
```

## Build

```powershell
dotnet test
PowerShell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

The self-contained Windows package is written to `artifacts` and includes the app, hook executables, install scripts, and VS Code terminal bridge extension.

## Run

```powershell
.\CCMonitor.App.exe
```

## Install Hooks

From the app, open Settings and click `Reinstall Hooks`, or run:

```powershell
.\scripts\install-hooks.ps1 -AppPath "C:\Path\To\CCMonitor.App.exe"
```

To uninstall:

```powershell
.\scripts\uninstall-hooks.ps1 -AppPath "C:\Path\To\CCMonitor.App.exe"
```

The installer merges into `%USERPROFILE%\.claude\settings.json`, preserves existing fields and hooks, and avoids duplicate CC Monitor entries.

## Privacy

- CC Monitor may read recent Claude Code transcript metadata locally to calculate usage; message content is not uploaded or stored by CC Monitor.
- CC Monitor does not send data to a server.
- CC Monitor does not require an Anthropic API key.
- Prompt previews are disabled by default and capped at 100 characters when enabled.

## Debug Hook Mode

Set `CCMONITOR_DEBUG_HOOKS=1` to save raw hook payloads under `%USERPROFILE%\.cc-monitor\debug-hooks`.

Debug Hook Mode may store Claude Code hook metadata and must only be enabled for local debugging.

## Troubleshooting

- Monitor does not change status: verify `/hooks` in Claude Code and check `%USERPROFILE%\.cc-monitor\logs\cc-monitor-hook.log`.
- `/hooks` does not show CC Monitor: reinstall hooks from Settings or `scripts\install-hooks.ps1`.
- `settings.json` is invalid: fix `%USERPROFILE%\.claude\settings.json`, then reinstall hooks.
- Hook executable moved: reinstall hooks after moving the published folder.
- Notifications do not appear: enable Windows notifications in Settings and check Focus Assist.
- Multiple sessions show the same project name: CC Monitor derives names from hook `cwd`; sessions in same folder share a project name.
