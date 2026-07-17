using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    var hookEvent = parser.Parse(input) with
    {
        TerminalToken = NormalizeTerminalToken(
            Environment.GetEnvironmentVariable("CCMONITOR_TERMINAL_TOKEN"))
    };

    if (hookEvent.Kind == HookEventKind.Unknown)
    {
        logger.Info(
            $"event={hookEvent.RawEventName} ignored inputLength={input.Length} inputSha256={ShortHash(input)} " +
            $"shape={DescribeInput(input)} parseError={hookEvent.ParseError ?? "n/a"} duration={stopwatch.ElapsedMilliseconds}ms");
        if (Environment.GetEnvironmentVariable("CCMONITOR_DEBUG_HOOKS") == "1")
        {
            SaveDebugHook(paths, hookEvent.RawEventName, input);
        }
        return 0;
    }

    if (hookEvent.WasRecovered)
    {
        logger.Info(
            $"session={Short(hookEvent.SessionId)} event={hookEvent.RawEventName} recovered " +
            $"inputLength={input.Length} inputSha256={ShortHash(input)} parseError={hookEvent.ParseError ?? "n/a"}");
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

    await store.WithSessionLockAsync(hookEvent.SessionId, async () =>
    {
        var state = await store.GetOrCreateAsync(hookEvent.SessionId, hookEvent.WorkingDirectory);
        var oldStatus = state.Status;
        stateMachine.Apply(state, hookEvent, config);
        await store.SaveAtomicAsync(state);
        logger.Info(
            $"session={Short(state.SessionId)} terminalToken={ShortToken(state.TerminalToken)} " +
            $"event={hookEvent.RawEventName} {oldStatus}->{state.Status} duration={stopwatch.ElapsedMilliseconds}ms");
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

static string ShortToken(string token)
    => string.IsNullOrWhiteSpace(token) ? "none" : token[..Math.Min(8, token.Length)];

static string? NormalizeTerminalToken(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var normalized = value.Trim();
    return normalized.Length is >= 16 and <= 128
        && normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            ? normalized
            : null;
}

static string ShortHash(string input)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexString(hash)[..16];
}

static string DescribeInput(string input)
{
    if (string.IsNullOrEmpty(input)) return "<empty>";
    var firstObject = input.IndexOf('{');
    var lastObject = input.LastIndexOf('}');
    var leadingLength = firstObject < 0 ? input.Length : firstObject;
    var leading = new string(input
        .Take(Math.Min(leadingLength, 48))
        .Select(ch => char.IsControl(ch) ? ' ' : ch)
        .ToArray())
        .Trim();
    return $"first=U+{(int)input[0]:X4},last=U+{(int)input[^1]:X4}," +
        $"firstObject={firstObject},lastObject={lastObject}," +
        $"newlines={input.Count(ch => ch == '\n')},nuls={input.Count(ch => ch == '\0')}," +
        $"leading={(string.IsNullOrEmpty(leading) ? "<none>" : leading)}";
}
