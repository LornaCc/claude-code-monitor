# CC Monitor Terminal Bridge

This local companion extension lets CC Monitor reveal the VS Code integrated terminal that owns a Claude Code session. It listens for short-lived requests in `%USERPROFILE%\.cc-monitor\focus-terminal.json` and matches terminals by shell process ID, with a working-directory fallback.

The extension does not read terminal contents or send telemetry.
