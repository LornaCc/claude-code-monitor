# CC Monitor Terminal Bridge

This local companion extension lets CC Monitor reveal the VS Code integrated terminal that owns a Claude Code session.

Each VS Code window publishes a short-lived local registration under `%USERPROFILE%\.cc-monitor\terminal-bridges`. CC Monitor selects one registered window and sends a focus request only to that window.

For reliable focusing when several terminals use the same working directory, run:

- **CC Monitor: Create Managed Claude Terminal** to create a terminal with a stable terminal token and start Claude.
- **CC Monitor: Migrate Active Terminal** to create a tokenized replacement using the active terminal's working directory. The old terminal is left open.
- **CC Monitor: Bind Active Terminal to Session** to explicitly bind the selected terminal when cwd matching is not sufficient. The session can first be prepared from its `Bind terminal…` menu in the desktop app, or selected from a list in VS Code.

The token belongs to the terminal rather than the Claude session, so `/clear`, resume, or a new Claude session ID can still map back to the same terminal. Existing terminals remain supported through working-directory matching when that match is unique.

The extension does not read terminal contents or send telemetry.
