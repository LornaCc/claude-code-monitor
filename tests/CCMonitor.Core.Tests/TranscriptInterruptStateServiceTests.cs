using System.Text.Json;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class TranscriptInterruptStateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"cc-monitor-transcript-interrupt-{Guid.NewGuid():N}");
    private readonly CcMonitorPaths _paths;

    public TranscriptInterruptStateServiceTests()
    {
        _paths = new CcMonitorPaths(_root);
        _paths.EnsureDirectories();
    }

    [Fact]
    public async Task Applies_recent_transcript_interrupt_to_running_session()
    {
        var sessionId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var interruptedAt = DateTimeOffset.UtcNow;
        var store = new ClaudeSessionStateStore(_paths);
        await store.SaveAtomicAsync(State(sessionId, startedAt));
        var transcriptPath = WriteTranscript(sessionId, interruptedAt);

        var result = await new TranscriptInterruptStateService(_paths)
            .ApplyAsync(transcriptPath);
        var state = await store.TryLoadAsync(sessionId);

        Assert.True(result.Applied);
        Assert.Equal("interruptedMessageId", result.MatchKind);
        Assert.NotNull(state);
        Assert.Equal(ClaudeSessionStatus.Interrupted, state!.Status);
        Assert.Equal(interruptedAt, state.InterruptedAt);
        Assert.Equal("TranscriptInterrupt", state.LastHookEvent);
    }

    [Fact]
    public async Task Ignores_interrupt_from_before_the_current_prompt()
    {
        var sessionId = Guid.NewGuid().ToString();
        var interruptedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var startedAt = DateTimeOffset.UtcNow;
        var store = new ClaudeSessionStateStore(_paths);
        await store.SaveAtomicAsync(State(sessionId, startedAt));
        var transcriptPath = WriteTranscript(sessionId, interruptedAt);

        var result = await new TranscriptInterruptStateService(_paths)
            .ApplyAsync(transcriptPath);
        var state = await store.TryLoadAsync(sessionId);

        Assert.False(result.Applied);
        Assert.Equal(ClaudeSessionStatus.Running, state!.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteTranscript(string sessionId, DateTimeOffset timestamp)
    {
        var path = Path.Combine(_root, $"{sessionId}.jsonl");
        var line = JsonSerializer.Serialize(new
        {
            type = "user",
            timestamp,
            interruptedMessageId = "msg_interrupted",
            message = new
            {
                role = "user",
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = "[Request interrupted by user for tool use]"
                    }
                }
            }
        });
        File.WriteAllText(path, line + Environment.NewLine);
        return path;
    }

    private static ClaudeSessionState State(
        string sessionId,
        DateTimeOffset startedAt)
        => new()
        {
            SessionId = sessionId,
            ProjectName = "project",
            WorkingDirectory = @"C:\work\project",
            Status = ClaudeSessionStatus.Running,
            CreatedAt = startedAt.AddSeconds(-1),
            UpdatedAt = startedAt,
            StartedAt = startedAt
        };
}
