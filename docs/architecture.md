# CC Monitor Architecture

## Projects

- `CCMonitor.Core`: models, hook parsing, state machine, state storage, config, logging, and Claude settings merge logic.
- `CCMonitor.Hook`: console executable invoked by Claude Code hooks. It reads stdin JSON, updates local session state, logs, and always exits `0`.
- `CCMonitor.App`: WPF desktop widget. It watches local session JSON files, displays active sessions, plays sounds, shows notifications, and installs/uninstalls hooks.
- `CCMonitor.Core.Tests`: unit tests for parser, state machine, storage, and settings merger.
- `CCMonitor.Hook.Tests`: hook pipeline tests using mock hook JSON.

## State Files

Session states are stored in:

```text
%USERPROFILE%\.cc-monitor\sessions\<session_id>.json
```

Writes use a temp file followed by a move to avoid half-written JSON being read by the WPF watcher.

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
Notification permission_prompt
Notification idle_prompt
Stop
StopFailure
SessionEnd
```

Uninstall removes only command hooks matching the current `CCMonitor.Hook.exe` path.

## Known MVP Limits

- Real Claude Code hook payload variations should be validated with `CCMONITOR_DEBUG_HOOKS=1`.
- Notifications currently use the Windows tray balloon API rather than a packaged WinRT toast identity.
- Single-instance second launch exits instead of actively focusing the first window.
