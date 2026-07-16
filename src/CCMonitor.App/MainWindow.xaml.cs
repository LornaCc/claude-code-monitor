using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Forms = System.Windows.Forms;

namespace CCMonitor.App;

public partial class MainWindow : Window
{
    private readonly CcMonitorPaths _paths = new();
    private readonly MonitorConfigStore _configStore;
    private readonly ClaudeSessionStateStore _stateStore;
    private readonly SessionVisibilityStore _visibilityStore;
    private readonly RollingLogger _logger;
    private readonly VsCodeTerminalBridge _terminalBridge;
    private readonly DispatcherTimer _reloadDebounce;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastNotifications = new();
    private FileSystemWatcher? _watcher;
    private MonitorConfig _config = new();
    private Forms.NotifyIcon? _notifyIcon;

    public ObservableCollection<SessionCardViewModel> Sessions { get; } = new();
    public ObservableCollection<SessionCardViewModel> HiddenSessions { get; } = new();
    public ICollectionView SessionsView { get; }

    public MainWindow()
    {
        InitializeComponent();
        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        DataContext = this;

        _configStore = new MonitorConfigStore(_paths);
        _stateStore = new ClaudeSessionStateStore(_paths);
        _visibilityStore = new SessionVisibilityStore(_paths);
        _logger = new RollingLogger(Path.Combine(_paths.LogsDirectory, "cc-monitor-app.log"));
        _terminalBridge = new VsCodeTerminalBridge(_paths);

        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _reloadDebounce.Tick += async (_, _) =>
        {
            _reloadDebounce.Stop();
            await ReloadSessionsAsync();
        };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            foreach (var session in Sessions) session.Tick();
        };

        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task StartAsync()
    {
        try
        {
            StartupLog("StartAsync begin");
            _paths.EnsureDirectories();
            StartupLog("Directories ensured");
            _config = _configStore.LoadOrCreate();
            StartupLog("Config loaded");
            Topmost = _config.AlwaysOnTop;
            if (_config.WindowLeft is not null) Left = _config.WindowLeft.Value;
            if (_config.WindowTop is not null) Top = _config.WindowTop.Value;
            if (_config.WindowWidth is not null) Width = _config.WindowWidth.Value;
            if (_config.WindowHeight is not null) Height = _config.WindowHeight.Value;
            ApplySessionGrouping();

            _stateStore.RemoveExpiredClosedSessions(_config.SessionRetentionHours);
            StartupLog("Expired sessions removed");
            SetupNotifyIcon();
            StartupLog("Notify icon setup");
            SetupWatcher();
            StartupLog("Watcher setup");
            UpdateHookStatus();
            StartupLog("Hook status updated");
            await ReloadSessionsAsync();
            StartupLog("Sessions reloaded");
            _clock.Start();
            StartupLog("Clock started");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "app startup failed");
            StartupLog($"StartAsync failed {ex}");
        }
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(_paths.SessionsDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Deleted += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
        _watcher.EnableRaisingEvents = true;
    }

    private async Task ReloadSessionsAsync()
    {
        var hiddenIds = _visibilityStore.LoadHidden();
        var removedIds = _visibilityStore.LoadRemoved().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = (await _stateStore.LoadAllAsync())
            .Where(s => s.Status != ClaudeSessionStatus.Closed && !removedIds.Contains(s.SessionId))
            .OrderByDescending(s => StatusPriority(s.Status))
            .ThenByDescending(s => s.UpdatedAt)
            .ToList();

        var visibleStates = states.Where(s => !hiddenIds.Contains(s.SessionId)).ToList();
        var hiddenStates = states.Where(s => hiddenIds.Contains(s.SessionId)).ToList();

        SyncCollection(Sessions, visibleStates);
        SyncCollection(HiddenSessions, hiddenStates);

        foreach (var state in states)
        {
            MaybeNotify(state);
        }

        EmptyText.Visibility = Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenExpander.Visibility = HiddenSessions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HiddenExpander.Header = $"Hidden ({HiddenSessions.Count})";
    }

    private void SyncCollection(ObservableCollection<SessionCardViewModel> target, IReadOnlyList<ClaudeSessionState> states)
    {
        foreach (var state in states)
        {
            var existing = target.FirstOrDefault(s => s.SessionId == state.SessionId);
            if (existing is null)
            {
                target.Add(new SessionCardViewModel(state, GetCustomSessionName(state.SessionId)));
            }
            else
            {
                existing.Update(state, GetCustomSessionName(state.SessionId));
            }
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (states.All(s => s.SessionId != target[i].SessionId)) target.RemoveAt(i);
        }
    }

    private void MaybeNotify(ClaudeSessionState state)
    {
        if (!_config.ShowWindowsNotifications) return;
        if (state.Status is not (ClaudeSessionStatus.Blocked or ClaudeSessionStatus.Done or ClaudeSessionStatus.Error)) return;

        var key = $"{state.SessionId}:{state.Status}";
        var now = DateTimeOffset.Now;
        if (_lastNotifications.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(3)) return;
        _lastNotifications[key] = now;

        var (title, body) = state.Status switch
        {
            ClaudeSessionStatus.Blocked => ("Claude Code needs attention", $"{state.ProjectName} requires permission"),
            ClaudeSessionStatus.Done => ("Claude Code finished", $"{state.ProjectName} completed"),
            ClaudeSessionStatus.Error => ("Claude Code stopped", $"{state.ProjectName} encountered an error"),
            _ => ("CC Monitor", state.ProjectName)
        };

        try
        {
            _notifyIcon?.ShowBalloonTip(4000, title, body, Forms.ToolTipIcon.Info);
            if (state.Status == ClaudeSessionStatus.Blocked && _config.BlockedSound) SystemSounds.Exclamation.Play();
            if (state.Status == ClaudeSessionStatus.Done && _config.DoneSound) SystemSounds.Asterisk.Play();
            if (state.Status == ClaudeSessionStatus.Error && _config.ErrorSound) SystemSounds.Hand.Play();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "notification failed");
        }
    }

    private void SetupNotifyIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "CC Monitor"
        };
    }

    private void UpdateHookStatus()
    {
        var service = new ClaudeSettingsFileService();
        var installed = service.IsInstalled(GetHookCommand(), GetStatusLineCommand());
        HooksStatusText.Text = installed ? "Hooks status: Installed" : "Hooks status: Not installed";
        InstallHooksButton.Content = installed ? "Reinstall Hooks" : "Install Hooks";
    }

    private void ScheduleReload()
    {
        Dispatcher.Invoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config, GetHookCommand(), GetStatusLineCommand()) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _config = window.Config;
            _config.WindowLeft = Left;
            _config.WindowTop = Top;
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
            _configStore.Save(_config);
            Topmost = _config.AlwaysOnTop;
            UpdateHookStatus();
        }
    }

    private void Usage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dashboard = new UsageDashboardWindow(_paths, _config) { Owner = this };
            dashboard.ShowDialog();
        }
        catch (Exception ex)
        {
            HooksStatusText.Text = "Usage dashboard could not open";
            _logger.Error(ex, "usage dashboard failed");
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _reloadDebounce.Stop();
            await ReloadSessionsAsync();
            SessionsView.Refresh();
            HooksStatusText.Text = $"Refreshed {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HooksStatusText.Text = "Refresh failed";
            _logger.Error(ex, "manual refresh failed");
        }
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        _config.GroupSessionsByStatus = !_config.GroupSessionsByStatus;
        _configStore.Save(_config);
        ApplySessionGrouping();
    }

    private void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSession(sender, out var session)) return;

        var currentName = GetCustomSessionName(session.SessionId) ?? session.ProjectName;
        var window = new RenameSessionWindow(currentName) { Owner = this };
        if (window.ShowDialog() != true) return;

        var newName = window.SessionName.Trim();
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, session.ProjectName, StringComparison.OrdinalIgnoreCase))
        {
            _config.SessionNames.Remove(session.SessionId);
        }
        else
        {
            _config.SessionNames[session.SessionId] = newName;
        }

        _configStore.Save(_config);
        _ = ReloadSessionsAsync();
    }

    private void HideSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSession(sender, out var session)) return;
        _visibilityStore.Hide(session.SessionId);
        _ = ReloadSessionsAsync();
    }

    private void RestoreSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSession(sender, out var session)) return;
        _visibilityStore.Restore(session.SessionId);
        _ = ReloadSessionsAsync();
    }

    private void RestoreAllHidden_Click(object sender, RoutedEventArgs e)
    {
        _visibilityStore.RestoreAll();
        _ = ReloadSessionsAsync();
    }

    private void RemoveSession_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSession(sender, out var session)) return;

        var confirm = new ConfirmRemoveWindow(session.DisplayName) { Owner = this };
        if (confirm.ShowDialog() != true) return;

        _visibilityStore.RemovePermanently(session.SessionId);
        _stateStore.Delete(session.SessionId);
        _config.SessionNames.Remove(session.SessionId);
        _configStore.Save(_config);
        _lastNotifications.Keys.Where(k => k.StartsWith($"{session.SessionId}:", StringComparison.OrdinalIgnoreCase)).ToList()
            .ForEach(k => _lastNotifications.Remove(k));
        _ = ReloadSessionsAsync();
    }

    private void OpenActionsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;
        button.ContextMenu.DataContext = button.DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private async void SessionCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement { DataContext: SessionCardViewModel session }) return;

        try
        {
            HooksStatusText.Text = $"Finding terminal: {session.ProjectName}";
            var terminalFocusTask = _terminalBridge.RequestFocusAsync(
                session.SessionId,
                session.TerminalProcessId,
                session.WorkingDirectory,
                session.ProjectName);

            var windowFocused = VsCodeWindowActivator.TryActivate(session.WorkingDirectory, session.ProjectName, out var matchedTitle);
            var terminalResult = await terminalFocusTask;

            if (terminalResult is not null)
            {
                HooksStatusText.Text = $"Focused terminal: {terminalResult.TerminalName}";
                _logger.Info($"focused vscode terminal session={session.SessionId} pid={terminalResult.TerminalProcessId?.ToString() ?? "n/a"} match={terminalResult.MatchKind} terminal={terminalResult.TerminalName}");
            }
            else if (windowFocused)
            {
                HooksStatusText.Text = $"Focused VS Code (terminal bridge unavailable): {session.ProjectName}";
                _logger.Info($"focused vscode fallback session={session.SessionId} terminalPid={session.TerminalProcessId?.ToString() ?? "n/a"} title={matchedTitle}");
            }
            else
            {
                HooksStatusText.Text = $"VS Code window not found: {session.ProjectName}";
                _logger.Info($"vscode window not found session={session.SessionId} project={session.ProjectName} cwd={session.WorkingDirectory}");
            }
        }
        catch (Exception ex)
        {
            HooksStatusText.Text = $"Could not focus VS Code: {session.ProjectName}";
            _logger.Error(ex, "focus vscode failed");
        }
    }

    private static bool TryGetSession(object sender, out SessionCardViewModel session)
    {
        if (sender is FrameworkElement { DataContext: SessionCardViewModel direct })
        {
            session = direct;
            return true;
        }

        if (sender is MenuItem menuItem
            && menuItem.Parent is ContextMenu { DataContext: SessionCardViewModel fromMenu })
        {
            session = fromMenu;
            return true;
        }

        session = null!;
        return false;
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button or MenuItem)
            {
                return true;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void InstallHooks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new ClaudeSettingsFileService().Install(GetHookCommand(), GetStatusLineCommand());
            UpdateHookStatus();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "install hooks failed");
            System.Windows.MessageBox.Show(this, ex.Message, "Install Hooks Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string GetHookCommand()
    {
        var appDirectory = AppContext.BaseDirectory;
        return HookCommandFormatter.ForShell(Path.Combine(appDirectory, "CCMonitor.Hook.exe"));
    }

    private string GetStatusLineCommand()
    {
        var appDirectory = AppContext.BaseDirectory;
        return HookCommandFormatter.ForShell(Path.Combine(appDirectory, "CCMonitor.StatusLine.exe"));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryDragWindow(e);
    }

    private void WindowChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        TryDragWindow(e);
    }

    private void TryDragWindow(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch
        {
            // WPF can throw if mouse capture changes during drag startup.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ResizeRight_DragDelta(object sender, DragDeltaEventArgs e)
        => Width = Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);

    private void ResizeBottom_DragDelta(object sender, DragDeltaEventArgs e)
        => Height = Clamp(Height + e.VerticalChange, MinHeight, MaxHeight);

    private void ResizeLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newWidth = Clamp(Width - e.HorizontalChange, MinWidth, MaxWidth);
        Left += Width - newWidth;
        Width = newWidth;
    }

    private void ResizeTop_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newHeight = Clamp(Height - e.VerticalChange, MinHeight, MaxHeight);
        Top += Height - newHeight;
        Height = newHeight;
    }

    private void ResizeTopLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeTop_DragDelta(sender, e);
        ResizeLeft_DragDelta(sender, e);
    }

    private void ResizeTopRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeTop_DragDelta(sender, e);
        ResizeRight_DragDelta(sender, e);
    }

    private void ResizeBottomLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeBottom_DragDelta(sender, e);
        ResizeLeft_DragDelta(sender, e);
    }

    private void ResizeBottomRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeBottom_DragDelta(sender, e);
        ResizeRight_DragDelta(sender, e);
    }

    private static double Clamp(double value, double minimum, double maximum)
        => Math.Min(Math.Max(value, minimum), maximum);

    private void Cleanup()
    {
        try
        {
            _config.WindowLeft = Left;
            _config.WindowTop = Top;
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
            _configStore.Save(_config);
        }
        catch
        {
            // Best effort only.
        }

        _watcher?.Dispose();
        _notifyIcon?.Dispose();
    }

    private static int StatusPriority(ClaudeSessionStatus status) => status switch
    {
        ClaudeSessionStatus.Blocked => 5,
        ClaudeSessionStatus.Error => 4,
        ClaudeSessionStatus.Running => 3,
        ClaudeSessionStatus.Done => 2,
        ClaudeSessionStatus.Idle => 1,
        _ => 0
    };

    private string? GetCustomSessionName(string sessionId)
        => _config.SessionNames.TryGetValue(sessionId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    private void ApplySessionGrouping()
    {
        SessionsView.GroupDescriptions.Clear();
        if (_config.GroupSessionsByStatus)
        {
            SessionsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SessionCardViewModel.StatusGroup)));
            GroupButton.Content = "Flat";
        }
        else
        {
            GroupButton.Content = "Group";
        }

        SessionsView.Refresh();
    }

    private static void StartupLog(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cc-monitor",
                "logs",
                "cc-monitor-startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} MainWindow {message}{Environment.NewLine}");
        }
        catch
        {
            // Best effort only.
        }
    }
}
