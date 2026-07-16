using CCMonitor.Core.Models;

namespace CCMonitor.App;

public sealed class UsageSessionViewModel
{
    public UsageSessionViewModel(ClaudeSessionState state, string? customName, SessionUsageMetrics? metrics)
    {
        SessionId = state.SessionId;
        DisplayName = string.IsNullOrWhiteSpace(customName) ? state.ProjectName : customName;
        ProjectName = state.ProjectName;
        Status = state.Status.ToString();
        ModelText = string.IsNullOrWhiteSpace(metrics?.ModelName) ? "" : metrics.ModelName;
        TokenText = metrics?.InputTokens is null && metrics?.OutputTokens is null
            ? "Token snapshot unavailable"
            : $"{metrics?.InputTokens ?? 0:N0} input / {metrics?.OutputTokens ?? 0:N0} output tokens";
        CostText = metrics?.TotalCostUsd is null ? "" : $"Estimated session cost: ${metrics.TotalCostUsd:0.0000}";
        UpdatedText = metrics is null ? "No usage snapshot yet" : $"Updated {FormatAgo(DateTimeOffset.Now - metrics.UpdatedAt)} ago";
    }

    public string SessionId { get; }
    public string DisplayName { get; }
    public string ProjectName { get; }
    public string Status { get; }
    public string ModelText { get; }
    public string TokenText { get; }
    public string CostText { get; }
    public string UpdatedText { get; }

    private static string FormatAgo(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1) return $"{Math.Max(0, (int)duration.TotalSeconds)} sec";
        if (duration.TotalHours < 1) return $"{(int)duration.TotalMinutes} min";
        return $"{(int)duration.TotalHours} hr";
    }
}
