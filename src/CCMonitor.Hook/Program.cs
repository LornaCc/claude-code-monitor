using System.Diagnostics;
using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Services;

var stopwatch = Stopwatch.StartNew();
var paths = new CcMonitorPaths();
var logger = new RollingLogger(Path.Combine(paths.LogsDirectory, "cc-monitor-hook.log"));

try
{
    paths.EnsureDirectories();

    var input = await Console.In.ReadToEndAsync();
    var parser = new HookEventParser();
    var hookEvent = parser.Parse(input);

    if (hookEvent.Kind == HookEventKind.Unknown)
    {
        logger.Info($"event={hookEvent.RawEventName} ignored duration={stopwatch.ElapsedMilliseconds}ms");
        return 0;
    }

    if (Environment.GetEnvironmentVariable("CCMONITOR_DEBUG_HOOKS") == "1")
    {
        SaveDebugHook(paths, hookEvent.RawEventName, input);
    }

    var visibilityStore = new SessionVisibilityStore(paths);
    if (visibilityStore.IsRemoved(hookEvent.SessionId))
    {
        logger.Info($"session={Short(hookEvent.SessionId)} event={hookEvent.RawEventName} ignored removed duration={stopwatch.ElapsedMilliseconds}ms");
        return 0;
    }

    var config = new MonitorConfigStore(paths).LoadOrCreate();
    var store = new ClaudeSessionStateStore(paths);
    var stateMachine = new ClaudeSessionStateMachine();
    var terminalProcessId = TerminalProcessLocator.FindTerminalShellProcessId();

    await store.WithSessionLockAsync(hookEvent.SessionId, async () =>
    {
        var state = await store.GetOrCreateAsync(hookEvent.SessionId, hookEvent.WorkingDirectory);
        var oldStatus = state.Status;
        state.TerminalProcessId = terminalProcessId ?? state.TerminalProcessId;
        stateMachine.Apply(state, hookEvent, config);
        await store.SaveAtomicAsync(state);
        logger.Info($"session={Short(state.SessionId)} event={hookEvent.RawEventName} {oldStatus}->{state.Status} terminalPid={state.TerminalProcessId?.ToString() ?? "n/a"} duration={stopwatch.ElapsedMilliseconds}ms");
    });
}
catch (Exception ex)
{
    logger.Error(ex, $"hook failed duration={stopwatch.ElapsedMilliseconds}ms");
}

return 0;

static void SaveDebugHook(CcMonitorPaths paths, string eventName, string input)
{
    Directory.CreateDirectory(paths.DebugHooksDirectory);
    var safeEventName = string.Join("_", eventName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    var fileName = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{safeEventName}.json";
    File.WriteAllText(Path.Combine(paths.DebugHooksDirectory, fileName), input);
}

static string Short(string sessionId) => sessionId.Length <= 8 ? sessionId : sessionId[..8];
