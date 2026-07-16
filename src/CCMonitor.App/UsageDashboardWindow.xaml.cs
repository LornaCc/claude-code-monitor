using System.Collections.ObjectModel;
using System.Windows;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;

namespace CCMonitor.App;

public partial class UsageDashboardWindow : Window
{
    private readonly CcMonitorPaths _paths;
    private readonly MonitorConfig _config;
    private readonly ClaudeSessionStateStore _stateStore;
    private readonly SessionUsageMetricsStore _metricsStore;
    private readonly SessionVisibilityStore _visibilityStore;

    public ObservableCollection<UsageSessionViewModel> Sessions { get; } = new();

    public UsageDashboardWindow(CcMonitorPaths paths, MonitorConfig config)
    {
        InitializeComponent();
        _paths = paths;
        _config = config;
        _stateStore = new ClaudeSessionStateStore(_paths);
        _metricsStore = new SessionUsageMetricsStore(_paths);
        _visibilityStore = new SessionVisibilityStore(_paths);
        DataContext = this;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var removedIds = _visibilityStore.LoadRemoved().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var metrics = _metricsStore.LoadAll();
        var states = (await _stateStore.LoadAllAsync())
            .Where(s => s.Status != ClaudeSessionStatus.Closed && !removedIds.Contains(s.SessionId))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

        Sessions.Clear();
        foreach (var state in states)
        {
            _config.SessionNames.TryGetValue(state.SessionId, out var customName);
            metrics.TryGetValue(state.SessionId, out var usage);
            Sessions.Add(new UsageSessionViewModel(state, customName, usage));
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await ReloadAsync();

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}
