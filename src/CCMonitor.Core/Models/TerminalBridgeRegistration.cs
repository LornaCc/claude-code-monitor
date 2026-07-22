namespace CCMonitor.Core.Models;

public sealed record TerminalBridgeRegistration(
    int ProtocolVersion,
    string BridgeId,
    int ProcessId,
    DateTimeOffset UpdatedAtUtc,
    string WorkspaceName,
    IReadOnlyList<string> WorkspaceFolders,
    IReadOnlyList<TerminalBridgeTerminal> Terminals,
    int? ActiveTerminalProcessId = null,
    bool WindowFocused = false);

public sealed record TerminalBridgeTerminal(
    string TerminalId,
    string TerminalToken,
    string Name,
    int? ProcessId,
    string WorkingDirectory);

public sealed record TerminalBridgeSelection(
    TerminalBridgeRegistration? Bridge,
    string MatchKind,
    string Reason,
    int LiveBridgeCount)
{
    public bool IsMatch => Bridge is not null;
}
