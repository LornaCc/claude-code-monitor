using System.IO;
using System.Threading;
using System.Windows;
using CCMonitor.Core.Services;

namespace CCMonitor.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        LogStartup($"OnStartup args={string.Join(" ", e.Args)}");
        if (TryHandleCommandLine(e.Args))
        {
            LogStartup("Handled command line, shutting down");
            Shutdown(0);
            return;
        }

        _mutex = new Mutex(true, @"Global\CCMonitor.App", out var createdNew);
        if (!createdNew)
        {
            LogStartup("Existing instance detected, shutting down");
            Shutdown();
            return;
        }
        _ownsMutex = true;

        base.OnStartup(e);
        LogStartup("Creating MainWindow");
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        LogStartup("Showing MainWindow");
        _mainWindow.Show();
        _mainWindow.Activate();
        LogStartup($"MainWindow shown visible={_mainWindow.IsVisible} active={_mainWindow.IsActive}");
    }

    private static void LogStartup(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cc-monitor",
                "logs",
                "cc-monitor-startup.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Startup logging is best effort only.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static bool TryHandleCommandLine(string[] args)
    {
        if (args.Length == 0) return false;

        var command = args[0].Trim();
        if (!command.Equals("--install-hooks", StringComparison.OrdinalIgnoreCase)
            && !command.Equals("--uninstall-hooks", StringComparison.OrdinalIgnoreCase)
            && !command.Equals("--hooks-status", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hookCommand = HookCommandFormatter.ForShell(Path.Combine(AppContext.BaseDirectory, "CCMonitor.Hook.exe"));
        var statusLineCommand = HookCommandFormatter.ForShell(Path.Combine(AppContext.BaseDirectory, "CCMonitor.StatusLine.exe"));
        var service = new ClaudeSettingsFileService();

        if (command.Equals("--install-hooks", StringComparison.OrdinalIgnoreCase))
        {
            service.Install(hookCommand, statusLineCommand);
        }
        else if (command.Equals("--uninstall-hooks", StringComparison.OrdinalIgnoreCase))
        {
            service.Uninstall(hookCommand, statusLineCommand);
        }
        else
        {
            Console.WriteLine(service.IsInstalled(hookCommand, statusLineCommand) ? "Installed" : "Not installed");
        }

        return true;
    }
}
