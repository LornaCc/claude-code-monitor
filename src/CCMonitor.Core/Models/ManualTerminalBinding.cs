namespace CCMonitor.Core.Models;

public sealed record ManualTerminalBinding(
    string SessionId,
    string TerminalToken,
    int? TerminalProcessId,
    string TerminalName,
    string WorkingDirectory,
    DateTimeOffset UpdatedAtUtc);
