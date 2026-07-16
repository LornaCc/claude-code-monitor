using CCMonitor.Core.HookEvents;
using Xunit;

namespace CCMonitor.Core.Tests;

public sealed class HookEventParserTests
{
    private readonly HookEventParser _parser = new();

    [Theory]
    [InlineData("SessionStart", HookEventKind.SessionStart)]
    [InlineData("UserPromptSubmit", HookEventKind.UserPromptSubmit)]
    [InlineData("PermissionRequest", HookEventKind.PermissionRequest)]
    [InlineData("PreToolUse", HookEventKind.Activity)]
    [InlineData("PostToolUse", HookEventKind.Activity)]
    [InlineData("Stop", HookEventKind.Stop)]
    [InlineData("StopFailure", HookEventKind.StopFailure)]
    [InlineData("SessionEnd", HookEventKind.SessionEnd)]
    public void Parses_named_events(string name, HookEventKind expected)
    {
        var parsed = _parser.Parse($$"""{"hook_event_name":"{{name}}","session_id":"s1","cwd":"C:\\work\\app"}""");
        Assert.Equal(expected, parsed.Kind);
        Assert.Equal("s1", parsed.SessionId);
    }

    [Fact]
    public void Parses_permission_notification()
    {
        var parsed = _parser.Parse("""{"hook_event_name":"Notification","session_id":"s1","notification_type":"permission_prompt"}""");
        Assert.Equal(HookEventKind.NotificationPermissionPrompt, parsed.Kind);
    }

    [Fact]
    public void Parses_idle_notification()
    {
        var parsed = _parser.Parse("""{"hook_event_name":"Notification","session_id":"s1","notification_type":"idle_prompt"}""");
        Assert.Equal(HookEventKind.NotificationIdlePrompt, parsed.Kind);
    }

    [Theory]
    [InlineData("interrupted")]
    [InlineData("cancelled")]
    [InlineData("aborted")]
    [InlineData("stopped")]
    public void Parses_interruption_notification_as_done(string message)
    {
        var parsed = _parser.Parse($$"""{"hook_event_name":"Notification","session_id":"s1","message":"{{message}}"}""");
        Assert.Equal(HookEventKind.NotificationIdlePrompt, parsed.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not-json")]
    [InlineData("""{"hook_event_name":"Other"}""")]
    public void Unknown_inputs_do_not_throw(string input)
    {
        var parsed = _parser.Parse(input);
        Assert.Equal(HookEventKind.Unknown, parsed.Kind);
    }
}
