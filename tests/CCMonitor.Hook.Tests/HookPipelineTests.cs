using CCMonitor.Core.HookEvents;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CCMonitor.Hook.Tests;

public sealed class HookPipelineTests
{
    [Fact]
    public async Task Mock_stdin_event_updates_state_file()
    {
        using var temp = new TempDirectory();
        var paths = new CcMonitorPaths(temp.Path);
        var config = new MonitorConfig();
        var store = new ClaudeSessionStateStore(paths);
        var parser = new HookEventParser();
        var machine = new ClaudeSessionStateMachine();

        var hookEvent = parser.Parse("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:\\work\\demo"}""");
        await store.WithSessionLockAsync(hookEvent.SessionId, async () =>
        {
            var state = await store.GetOrCreateAsync(hookEvent.SessionId, hookEvent.WorkingDirectory);
            machine.Apply(state, hookEvent, config);
            await store.SaveAtomicAsync(state);
        });

        var loaded = await store.LoadAllAsync();
        Assert.Single(loaded);
        Assert.Equal(ClaudeSessionStatus.Running, loaded[0].Status);
        Assert.Equal("demo", loaded[0].ProjectName);
    }

    [Fact]
    public async Task Terminal_token_survives_session_state_updates()
    {
        using var temp = new TempDirectory();
        var paths = new CcMonitorPaths(temp.Path);
        var store = new ClaudeSessionStateStore(paths);
        var machine = new ClaudeSessionStateMachine();
        var hookEvent = new HookEvent
        {
            Kind = HookEventKind.UserPromptSubmit,
            RawEventName = "UserPromptSubmit",
            SessionId = "new-session-id",
            TerminalToken = "0123456789abcdef0123456789abcdef",
            WorkingDirectory = @"C:\work\demo"
        };

        var state = await store.GetOrCreateAsync(hookEvent.SessionId, hookEvent.WorkingDirectory);
        machine.Apply(state, hookEvent, new MonitorConfig());
        await store.SaveAtomicAsync(state);

        var loaded = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("new-session-id", loaded.SessionId);
        Assert.Equal("0123456789abcdef0123456789abcdef", loaded.TerminalToken);
    }

    [Fact]
    public async Task Utf8_standard_input_preserves_a_chinese_working_directory()
    {
        const string input =
            """{"hook_event_name":"SessionStart","session_id":"s1","cwd":"C:\\Users\\admin\\Desktop\\新建文件夹"}""";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));

        var decoded = await Utf8StandardInputReader.ReadAsync(stream);
        var hookEvent = new HookEventParser().Parse(decoded);

        Assert.Equal(@"C:\Users\admin\Desktop\新建文件夹", hookEvent.WorkingDirectory);
        Assert.False(hookEvent.WasRecovered);
    }

    [Fact]
    public async Task Hook_process_claims_an_ordinary_terminal_token_with_a_utf8_cwd()
    {
        using var temp = new TempDirectory();
        var paths = new CcMonitorPaths(temp.Path);
        paths.EnsureDirectories();
        const string sessionId = "utf8-session";
        const string workingDirectory = @"C:\Users\admin\Desktop\新建文件夹";
        const string terminalToken = "ordinary-terminal-token";
        const int terminalProcessId = 26101;
        var registration = new TerminalBridgeRegistration(
            3,
            "bridge-one",
            123,
            DateTimeOffset.UtcNow,
            "新建文件夹",
            [workingDirectory],
            [
                new TerminalBridgeTerminal(
                    $"token:{terminalToken}",
                    terminalToken,
                    "bash",
                    terminalProcessId,
                    workingDirectory)
            ],
            terminalProcessId,
            true);
        await File.WriteAllTextAsync(
            Path.Combine(paths.TerminalBridgesDirectory, "bridge-one.json"),
            JsonSerializer.Serialize(registration));

        var hookAssembly = FindHookAssembly();
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            Arguments = $"\"{hookAssembly}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["CCMONITOR_ROOT"] = temp.Path;
        startInfo.Environment.Remove("CCMONITOR_TERMINAL_TOKEN");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start CCMonitor.Hook.");
        var input = JsonSerializer.Serialize(new
        {
            hook_event_name = "SessionStart",
            session_id = sessionId,
            cwd = workingDirectory
        });
        await process.StandardInput.BaseStream.WriteAsync(Encoding.UTF8.GetBytes(input));
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        var standardError = await process.StandardError.ReadToEndAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Hook exited with {process.ExitCode}: {standardError}");
        var state = Assert.Single(await new ClaudeSessionStateStore(paths).LoadAllAsync());
        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(workingDirectory, state.WorkingDirectory);
        Assert.Equal(terminalToken, state.TerminalToken);
        Assert.Equal(terminalProcessId, state.TerminalProcessId);
    }

    [Fact]
    public async Task Session_lock_timeout_is_reported_instead_of_silently_dropping_event()
    {
        using var temp = new TempDirectory();
        var store = new ClaudeSessionStateStore(new CcMonitorPaths(temp.Path));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = store.WithSessionLockAsync("same-session", async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            store.WithSessionLockAsync("same-session", () => Task.CompletedTask, timeoutMs: 100));

        release.SetResult();
        await holder;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cc-monitor-hook-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private static string FindHookAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "CCMonitor.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Debug";
        var assembly = Path.Combine(
            root,
            "src",
            "CCMonitor.Hook",
            "bin",
            configuration,
            "net8.0",
            "CCMonitor.Hook.dll");
        return File.Exists(assembly)
            ? assembly
            : throw new FileNotFoundException("Could not locate the built Hook assembly.", assembly);
    }
}
