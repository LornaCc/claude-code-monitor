namespace CCMonitor.Core.Models;

public sealed class MonitorConfig
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowWindowsNotifications { get; set; } = false;
    public bool BlockedSound { get; set; } = true;
    public bool DoneSound { get; set; } = true;
    public bool ErrorSound { get; set; } = true;
    public bool SavePromptPreview { get; set; } = false;
    public int SessionRetentionHours { get; set; } = 24;
    public int StaleSessionMinutes { get; set; } = 30;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool GroupSessionsByStatus { get; set; } = true;
    public Dictionary<string, string> SessionNames { get; set; } = new();
}
