# Changelog

## 0.4.2 - 2026-07-23

- Preserved and migrated terminal identity across Claude Code `/clear`.
- Automatically refreshed a session's window binding after its terminal was moved to another VS Code window and successfully focused there.
- Hardened the one-click upgrade path: release-derived versioning, App/Hook/StatusLine version checks, exclusive Hook path verification, exact VSIX verification, and Start menu/Desktop shortcut replacement.

## 0.4.1 - 2026-07-23

- Reworked VS Code terminal targeting around per-window bridge registrations, explicit terminal bindings, stable terminal tokens, and safe cwd/workspace matching.
- Added deterministic handling for multiple sessions and terminals that share one working directory.
- Added managed-terminal creation, active-terminal migration, and manual session-to-terminal binding commands.
- Added transcript-based `Ctrl+C`/`Esc` interruption reconciliation and separate `INTERRUPTED` and `STALE` states.
- Hardened Hook parsing, locking, atomic state writes, diagnostics, and non-blocking failure behavior.
- Removed arbitrary first-window fallback behavior and added explicit `matched`, `noMatch`, and `bridgeNotRunning` results.
- Preserved maximized, normal, and snapped VS Code window geometry during native foreground activation.
- Added a self-contained one-click installer that stops older instances, repoints Hooks and StatusLine, verifies the VS Code extension, updates the shortcut, and launches the installed build.

## 0.1.0 - 2026-07-16

- Initial Windows desktop monitor, Claude Code Hook integration, session dashboard, notifications, and usage view.
