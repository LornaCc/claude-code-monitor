# CC Monitor Terminal Bridge

This local companion extension lets CC Monitor reveal the VS Code integrated terminal that owns a Claude Code session.

Each VS Code window publishes a short-lived local registration under `%USERPROFILE%\.cc-monitor\terminal-bridges`. CC Monitor selects one registered window and sends a focus request only to that window.

Every integrated terminal automatically receives a stable Bridge token. When Claude starts or a prompt is submitted, CC Monitor claims the active terminal in the focused VS Code window and saves that token with the session. Starting Claude in an ordinary terminal requires no setup.

- **CC Monitor: Create Managed Claude Terminal** optionally creates a terminal with an environment token and starts Claude.
- **CC Monitor: Migrate Active Terminal** to create a tokenized replacement using the active terminal's working directory. The old terminal is left open.
- **CC Monitor: Bind Active Terminal to Session** is a fallback when automatic claiming cannot identify one terminal safely.

The token belongs to the terminal rather than the Claude session, so `/clear`, resume, or a new Claude session ID can still map back to the same terminal.

The extension does not read terminal contents or send telemetry.
