using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CCMonitor.Core.Models;
using MediaColor = System.Windows.Media.Color;

namespace CCMonitor.App;

public sealed class SessionCardViewModel : INotifyPropertyChanged
{
    private ClaudeSessionState _state;
    private DateTimeOffset _now = DateTimeOffset.Now;
    private string? _customName;

    public SessionCardViewModel(ClaudeSessionState state, string? customName = null)
    {
        _state = state;
        _customName = customName;
    }

    public string SessionId => _state.SessionId;
    public ClaudeSessionStatus Status => _state.Status;
    public string WorkingDirectory => _state.WorkingDirectory;
    public int? TerminalProcessId => _state.TerminalProcessId;
    public string ProjectName => string.IsNullOrWhiteSpace(_state.ProjectName) ? "Unknown Project" : _state.ProjectName;
    public string DisplayName => string.IsNullOrWhiteSpace(_customName)
        ? $"{ProjectName} - {ShortSessionId}"
        : $"{_customName} - {ShortSessionId}";
    public string ShortSessionId => string.IsNullOrWhiteSpace(SessionId)
        ? "session unknown"
        : $"session {SessionId[..Math.Min(8, SessionId.Length)]}";
    public string PrimaryText => Status == ClaudeSessionStatus.Blocked ? "NEEDS ATTENTION" : Status.ToString().ToUpperInvariant();
    public string StatusGroup => Status switch
    {
        ClaudeSessionStatus.Blocked => "Needs attention",
        ClaudeSessionStatus.Running => "Working",
        ClaudeSessionStatus.Done => "Done",
        ClaudeSessionStatus.Error => "Errors",
        ClaudeSessionStatus.Idle => "Waiting",
        _ => "Other"
    };
    public bool IsNeedsAttention => Status == ClaudeSessionStatus.Blocked;
    public bool IsDoneFlashing => Status == ClaudeSessionStatus.Done
        && _state.FinishedAt is not null
        && _now - _state.FinishedAt.Value < TimeSpan.FromSeconds(6);
    public string DetailText => Status switch
    {
        ClaudeSessionStatus.Idle => "Waiting for task",
        ClaudeSessionStatus.Running => _state.StartedAt is null ? "Claude is working" : $"Working for {FormatDuration(_now - _state.StartedAt.Value)}",
        ClaudeSessionStatus.Blocked => BlockedDetailText,
        ClaudeSessionStatus.Done => _state.FinishedAt is null ? "Waiting for your input" : $"Finished {FormatAgo(_now - _state.FinishedAt.Value)} ago",
        ClaudeSessionStatus.Error => "Claude stopped unexpectedly",
        ClaudeSessionStatus.Closed => "Session closed",
        _ => ""
    };

    public System.Windows.Media.Brush AccentBrush => Status switch
    {
        ClaudeSessionStatus.Running => new SolidColorBrush(MediaColor.FromRgb(37, 99, 235)),
        ClaudeSessionStatus.Blocked => new SolidColorBrush(MediaColor.FromRgb(220, 38, 38)),
        ClaudeSessionStatus.Done => new SolidColorBrush(MediaColor.FromRgb(22, 163, 74)),
        ClaudeSessionStatus.Error => new SolidColorBrush(MediaColor.FromRgb(190, 18, 60)),
        _ => new SolidColorBrush(MediaColor.FromRgb(100, 116, 139))
    };

    private string BlockedDetailText
    {
        get
        {
            var reason = string.IsNullOrWhiteSpace(_state.BlockedReason) ? "Permission required" : _state.BlockedReason!;
            if (_state.BlockedAt is not null && _now - _state.BlockedAt.Value > TimeSpan.FromSeconds(8))
            {
                return $"{reason}. Waiting for Claude to continue";
            }

            return reason;
        }
    }

    public string Dot => Status switch
    {
        ClaudeSessionStatus.Running => "*",
        ClaudeSessionStatus.Blocked => "*",
        ClaudeSessionStatus.Done => "*",
        ClaudeSessionStatus.Error => "*",
        _ => "-"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ClaudeSessionState state, string? customName = null)
    {
        _state = state;
        _customName = customName;
        RaiseAll();
    }

    public void Tick()
    {
        _now = DateTimeOffset.Now;
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(IsDoneFlashing));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(TerminalProcessId));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ShortSessionId));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(StatusGroup));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(IsNeedsAttention));
        OnPropertyChanged(nameof(IsDoneFlashing));
        OnPropertyChanged(nameof(Dot));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatAgo(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1) return $"{Math.Max(0, (int)duration.TotalSeconds)} sec";
        if (duration.TotalHours < 1) return $"{(int)duration.TotalMinutes} min";
        return $"{(int)duration.TotalHours} hr";
    }
}
