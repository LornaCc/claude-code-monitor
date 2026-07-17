namespace CCMonitor.Core.Models;

public sealed class ClaudeSessionState
{
    public string SessionId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string TerminalToken { get; set; } = "";
    public int? TerminalProcessId { get; set; }
    public ClaudeSessionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? BlockedReason { get; set; }
    public string? PromptPreview { get; set; }
    public string? LastHookEvent { get; set; }
}
