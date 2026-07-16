namespace CCMonitor.Core.Models;

public sealed class SessionUsageMetrics
{
    public string SessionId { get; set; } = "";
    public double? ContextUsedPercent { get; set; }
    public double? ContextRemainingPercent { get; set; }
    public long? ContextWindowSizeTokens { get; set; }
    public long? InputTokens { get; set; }
    public long? UncachedInputTokens { get; set; }
    public long? CacheCreationInputTokens { get; set; }
    public long? CacheReadInputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public decimal? TotalCostUsd { get; set; }
    public string? ModelName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
