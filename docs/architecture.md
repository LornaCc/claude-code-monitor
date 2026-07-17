# CC Monitor Architecture

## Projects

- `CCMonitor.Core`: models, hook parsing, state machine, state storage, config, logging, and Claude settings merge logic.
- `CCMonitor.Hook`: console executable invoked by Claude Code hooks. It reads stdin JSON, updates local session state, logs, and always exits `0`.
- `CCMonitor.App`: WPF desktop widget. It watches local session JSON files, displays active sessions, plays sounds, shows notifications, and installs/uninstalls hooks.
- `vscode-extension`: publishes one live registration per VS Code window and handles focus requests targeted to that window.
- `CCMonitor.Core.Tests`: unit tests for parser, state machine, storage, and settings merger.
- `CCMonitor.Hook.Tests`: hook pipeline tests using mock hook JSON.

## State Files

Session states are stored in:

```text
%USERPROFILE%\.cc-monitor\sessions\<session_id>.json
```

Writes use a temp file followed by a move to avoid half-written JSON being read by the WPF watcher.

Hook parsing first attempts strict JSON, then a single-value recovery parse, then recovery of essential event fields. Invalid payloads are logged with length, hash, and structural diagnostics without logging prompt content. Setting `CCMONITOR_DEBUG_HOOKS=1` additionally saves raw payloads.

## Terminal Bridge

Each VS Code window writes a heartbeat to:

```text
%USERPROFILE%\.cc-monitor\terminal-bridges\<bridge_id>.json
```

Protocol v3 registrations contain workspace folders and terminal PID/cwd metadata plus a terminal token for managed terminals. The extension injects `CCMONITOR_TERMINAL_TOKEN` when it creates a managed Claude terminal. Claude Hooks inherit the token and persist it in the session state, so a changed Claude session ID still points to the same terminal.

The desktop app selects by terminal token first, then writes a request containing `targetBridgeId` and the token. Only that bridge answers. Legacy terminals without a token continue using cwd/project matching when the match is unique. Ambiguous legacy cwd matches return `noMatch` with migration guidance. Results explicitly report `matched`, `noMatch`, or, from the app when no heartbeat exists, `bridgeNotRunning`.

If the bridge cannot identify a terminal, the app activates a VS Code window only when exactly one window title contains the project name. It never selects an arbitrary first VS Code window.

## State Machine

```text
SessionStart       -> Idle
UserPromptSubmit   -> Running
PermissionRequest  -> Blocked
Notification(permission_prompt) -> Blocked
Notification(idle_prompt)       -> Done
Stop               -> Done
StopFailure        -> Error
SessionEnd         -> Closed
```

`UserPromptSubmit` starts a new turn from `Idle`, `Done`, `Error`, or `Blocked`.

## Hook Installation

`ClaudeSettingsMerger` modifies `%USERPROFILE%\.claude\settings.json` as JSON, preserving existing settings and existing hooks. It adds CC Monitor command hooks for:

```text
SessionStart
UserPromptSubmit
PermissionRequest
Notification (single catch-all hook)
Stop
StopFailure
SessionEnd
```

Uninstall removes only command hooks matching the current `CCMonitor.Hook.exe` path.

## Known MVP Limits

- Sessions with no Hook activity for the configured stale interval are displayed as possibly stale rather than indefinitely working.
- Notifications currently use the Windows tray balloon API rather than a packaged WinRT toast identity.
- Single-instance second launch exits instead of actively focusing the first window.
