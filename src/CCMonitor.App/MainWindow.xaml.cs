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
    private FileSystemWatcher? _transcriptWatcher;
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
            SetupTranscriptWatcher();
            StartupLog("Transcript watcher setup");
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

    private void SetupTranscriptWatcher()
    {
        var projectsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects");
        if (!Directory.Exists(projectsDirectory))
        {
            return;
        }

        _transcriptWatcher = new FileSystemWatcher(projectsDirectory, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _transcriptWatcher.Created += Transcript_Changed;
        _transcriptWatcher.Changed += Transcript_Changed;
        _transcriptWatcher.EnableRaisingEvents = true;
    }

    private async void Transcript_Changed(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(60);
            var result = await new TranscriptInterruptStateService(_paths)
                .ApplyAsync(e.FullPath);
            if (!result.Applied)
            {
                return;
            }

            _logger.Info(
                $"transcript interrupt session={result.SessionId} " +
                $"timestamp={result.Timestamp:O} match={result.MatchKind}");
            ScheduleReload();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"transcript watcher failed path={e.FullPath}");
        }
    }

    private async Task ReloadSessionsAsync()
    {
        var hiddenIds = _visibilityStore.LoadHidden();
        var removedIds = _visibilityStore.LoadRemoved().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = (await _stateStore.LoadAllAsync())
            .Where(s => s.Status != ClaudeSessionStatus.Closed && !removedIds.Contains(s.SessionId))
            .OrderByDescending(s => StatusPriority(GetDisplayStatus(s)))
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
                target.Add(new SessionCardViewModel(
                    state,
                    GetCustomSessionName(state.SessionId),
                    TimeSpan.FromMinutes(Math.Max(1, _config.StaleSessionMinutes))));
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

    private void BindTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSession(sender, out var session)) return;

        try
        {
            _terminalBridge.PrepareManualBinding(
                session.SessionId,
                session.WorkingDirectory,
                session.ProjectName);
            HooksStatusText.Text =
                $"Binding ready for {session.ShortSessionId}; select a VS Code terminal and run the bind command.";
            System.Windows.MessageBox.Show(
                this,
                $"In VS Code, select the terminal for {session.DisplayName}, then run:\n\n" +
                "CC Monitor: Bind Active Terminal to Session",
                "Bind Terminal",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HooksStatusText.Text = $"Could not prepare terminal binding: {session.ProjectName}";
            _logger.Error(ex, "prepare terminal binding failed");
        }
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
            var foregroundGrant = VsCodeWindowActivator.TryAllowBridgeForegroundActivation();
            _logger.Info(
                $"foreground activation grant session={session.SessionId} " +
                $"granted={foregroundGrant.Granted} win32Error={foregroundGrant.Win32Error}");

            var terminalResult = await _terminalBridge.RequestFocusAsync(
                session.SessionId,
                session.TerminalToken,
                session.TerminalProcessId,
                session.WorkingDirectory,
                session.ProjectName);

            _logger.Info(
                $"terminal focus result session={session.SessionId} status={terminalResult.Status} " +
                $"bridge={terminalResult.BridgeId} liveBridges={terminalResult.LiveBridgeCount} " +
                $"terminalToken={ShortToken(session.TerminalToken)} " +
                $"match={terminalResult.MatchKind} bindingRefresh={terminalResult.BindingRefresh} " +
                $"reason={terminalResult.Reason}");

            if (terminalResult.Status == TerminalFocusStatus.Matched)
            {
                var codeWindowActuallyForeground = VsCodeWindowActivator.IsCodeWindowForeground();
                var extensionFocusedWindow = terminalResult.WindowFocused == true
                    && codeWindowActuallyForeground;
                var matchedWindowResult = extensionFocusedWindow
                    ? new VsCodeWindowActivationResult(
                        true,
                        "",
                        terminalResult.WindowFocusReason,
                        1)
                    : VsCodeWindowActivator.TryActivate(
                        session.WorkingDirectory,
                        string.IsNullOrWhiteSpace(terminalResult.WorkspaceName)
                            ? session.ProjectName
                            : terminalResult.WorkspaceName);
                HooksStatusText.Text = matchedWindowResult.Activated
                    ? $"Focused terminal: {terminalResult.TerminalName}"
                    : $"Selected terminal, but VS Code could not be brought forward: {session.ProjectName}";
                _logger.Info(
                    $"focused vscode terminal session={session.SessionId} " +
                    $"pid={terminalResult.TerminalProcessId?.ToString() ?? "n/a"} " +
                    $"bridge={terminalResult.BridgeId} workspace={terminalResult.WorkspaceName} " +
                    $"match={terminalResult.MatchKind} terminal={terminalResult.TerminalName} " +
                    $"bridgeWindowFocused={terminalResult.WindowFocused?.ToString() ?? "n/a"} " +
                    $"codeWindowActuallyForeground={codeWindowActuallyForeground} " +
                    $"bridgeWindowReason={terminalResult.WindowFocusReason} " +
                    $"windowActivated={matchedWindowResult.Activated} windowTitle={matchedWindowResult.MatchedTitle} " +
                    $"windowReason={matchedWindowResult.Reason} " +
                    $"windowInitialState={matchedWindowResult.InitialWindowState} " +
                    $"windowFinalState={matchedWindowResult.FinalWindowState} " +
                    $"windowRestoreInvoked={matchedWindowResult.RestoreInvoked}");
                return;
            }

            if (terminalResult.MatchKind is "manualTerminalNotRegistered" or "ambiguousManualBinding")
            {
                HooksStatusText.Text = terminalResult.MatchKind == "manualTerminalNotRegistered"
                    ? $"Bound terminal is no longer running. Bind terminal again: {session.ProjectName}"
                    : $"Manual terminal binding is ambiguous. Bind terminal again: {session.ProjectName}";
                return;
            }

            if (terminalResult.MatchKind is "ambiguousExactWorkingDirectory"
                or "ambiguousWorkingDirectory"
                or "ambiguousSessionWorkingDirectory")
            {
                _terminalBridge.PrepareManualBinding(
                    session.SessionId,
                    session.WorkingDirectory,
                    session.ProjectName);
            }

            var windowResult = VsCodeWindowActivator.TryActivate(session.WorkingDirectory, session.ProjectName);
            if (windowResult.Activated)
            {
                HooksStatusText.Text = terminalResult.MatchKind switch
                {
                    "ambiguousExactWorkingDirectory"
                        or "ambiguousWorkingDirectory"
                        or "ambiguousSessionWorkingDirectory"
                        => $"Multiple sessions share this cwd. Select the correct terminal and run CC Monitor: Bind Active Terminal to Session.",
                    "terminalTokenNotRegistered"
                        => $"Managed terminal is not registered. Reload VS Code or restart Claude in a managed terminal.",
                    _ when terminalResult.Status == TerminalFocusStatus.BridgeNotRunning
                        => $"Focused VS Code window (Terminal Bridge not running): {session.ProjectName}",
                    _ => $"Focused VS Code window; terminal not matched: {session.ProjectName}"
                };
                _logger.Info(
                    $"focused vscode title fallback session={session.SessionId} " +
                    $"bridgeStatus={terminalResult.Status} match={terminalResult.MatchKind} " +
                    $"title={windowResult.MatchedTitle} " +
                    $"windowInitialState={windowResult.InitialWindowState} " +
                    $"windowFinalState={windowResult.FinalWindowState} " +
                    $"windowRestoreInvoked={windowResult.RestoreInvoked}");
            }
            else
            {
                HooksStatusText.Text = terminalResult.Status switch
                {
                    TerminalFocusStatus.BridgeNotRunning => $"Terminal Bridge is not running: {session.ProjectName}",
                    _ => $"Terminal not found: {session.ProjectName}"
                };
                _logger.Info(
                    $"vscode focus unresolved session={session.SessionId} project={session.ProjectName} " +
                    $"cwd={session.WorkingDirectory} bridgeStatus={terminalResult.Status} " +
                    $"windowReason={windowResult.Reason} windowCandidates={windowResult.CandidateCount}");
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
        _transcriptWatcher?.Dispose();
        _notifyIcon?.Dispose();
    }

    private static int StatusPriority(ClaudeSessionStatus status) => status switch
    {
        ClaudeSessionStatus.Blocked => 5,
        ClaudeSessionStatus.Error => 4,
        ClaudeSessionStatus.Running => 3,
        ClaudeSessionStatus.Done => 2,
        ClaudeSessionStatus.Interrupted => 2,
        ClaudeSessionStatus.Idle => 1,
        ClaudeSessionStatus.Stale => 1,
        _ => 0
    };

    private ClaudeSessionStatus GetDisplayStatus(ClaudeSessionState state)
        => state.Status is ClaudeSessionStatus.Running or ClaudeSessionStatus.Blocked
            && DateTimeOffset.Now - state.UpdatedAt >= TimeSpan.FromMinutes(Math.Max(1, _config.StaleSessionMinutes))
                ? ClaudeSessionStatus.Stale
                : state.Status;

    private string? GetCustomSessionName(string sessionId)
        => _config.SessionNames.TryGetValue(sessionId, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    private static string ShortToken(string token)
        => string.IsNullOrWhiteSpace(token) ? "none" : token[..Math.Min(8, token.Length)];

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
