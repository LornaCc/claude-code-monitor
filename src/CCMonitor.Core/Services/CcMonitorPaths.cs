namespace CCMonitor.Core.Services;

public sealed class CcMonitorPaths
{
    public CcMonitorPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? Environment.GetEnvironmentVariable("CCMONITOR_ROOT")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cc-monitor");
    }

    public string RootDirectory { get; }
    public string SessionsDirectory => Path.Combine(RootDirectory, "sessions");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string DebugHooksDirectory => Path.Combine(RootDirectory, "debug-hooks");
    public string TerminalBridgesDirectory => Path.Combine(RootDirectory, "terminal-bridges");
    public string TerminalBindingsDirectory => Path.Combine(RootDirectory, "terminal-bindings");
    public string UsageMetricsDirectory => Path.Combine(RootDirectory, "usage");
    public string ConfigPath => Path.Combine(RootDirectory, "config.json");
    public string HiddenSessionsPath => Path.Combine(RootDirectory, "hidden-sessions.json");
    public string RemovedSessionsPath => Path.Combine(RootDirectory, "removed-sessions.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TerminalBridgesDirectory);
        Directory.CreateDirectory(TerminalBindingsDirectory);
        Directory.CreateDirectory(UsageMetricsDirectory);
    }
}
