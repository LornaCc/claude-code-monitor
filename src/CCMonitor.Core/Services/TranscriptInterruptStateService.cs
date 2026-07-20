using System.Text.Json;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class TranscriptInterruptStateService
{
    private const int MaxTailBytes = 512 * 1024;
    private readonly ClaudeSessionStateStore _store;

    public TranscriptInterruptStateService(CcMonitorPaths paths)
    {
        _store = new ClaudeSessionStateStore(paths);
    }

    public async Task<TranscriptInterruptApplyResult> ApplyAsync(string? transcriptPath)
    {
        var marker = FindLatestMarker(transcriptPath);
        if (marker is null || string.IsNullOrWhiteSpace(transcriptPath))
        {
            return TranscriptInterruptApplyResult.NotApplied;
        }

        var sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return TranscriptInterruptApplyResult.NotApplied;
        }

        var applied = false;
        await _store.WithSessionLockAsync(sessionId, async () =>
        {
            var state = await _store.TryLoadAsync(sessionId);
            if (state is null
                || state.Status is not (ClaudeSessionStatus.Running or ClaudeSessionStatus.Blocked)
                || state.StartedAt is null
                || marker.Timestamp < state.StartedAt
                || state.InterruptedAt >= marker.Timestamp)
            {
                return;
            }

            state.Status = ClaudeSessionStatus.Interrupted;
            state.UpdatedAt = marker.Timestamp;
            state.FinishedAt = marker.Timestamp;
            state.FailedAt = null;
            state.InterruptedAt = marker.Timestamp;
            state.BlockedAt = null;
            state.BlockedReason = null;
            state.LastHookEvent = "TranscriptInterrupt";
            await _store.SaveAtomicAsync(state);
            applied = true;
        });

        return applied
            ? new TranscriptInterruptApplyResult(true, sessionId, marker.Timestamp, marker.MatchKind)
            : TranscriptInterruptApplyResult.NotApplied;
    }

    public static TranscriptInterruptMarker? FindLatestMarker(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
        {
            return null;
        }

        TranscriptInterruptMarker? latest = null;
        try
        {
            foreach (var line in ReadRecentLines(transcriptPath, MaxTailBytes))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var matchKind = GetInterruptMatchKind(root);
                    if (matchKind is null
                        || !TryGetTimestamp(root, out var timestamp)
                        || latest is not null && latest.Timestamp >= timestamp)
                    {
                        continue;
                    }

                    latest = new TranscriptInterruptMarker(timestamp, matchKind);
                }
                catch (JsonException)
                {
                    // The transcript may be observed while Claude is appending a line.
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return latest;
    }

    private static string? GetInterruptMatchKind(JsonElement root)
    {
        if (root.TryGetProperty("interruptedMessageId", out var interruptedMessageId)
            && interruptedMessageId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(interruptedMessageId.GetString()))
        {
            return "interruptedMessageId";
        }

        if (root.TryGetProperty("toolUseResult", out var toolUseResult)
            && toolUseResult.ValueKind == JsonValueKind.Object
            && toolUseResult.TryGetProperty("interrupted", out var interrupted)
            && interrupted.ValueKind == JsonValueKind.True)
        {
            return "interruptedToolResult";
        }

        if (!root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String
            && IsInterruptText(content.GetString()))
        {
            return "interruptText";
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && IsInterruptText(text.GetString()))
            {
                return "interruptText";
            }
        }

        return null;
    }

    private static bool IsInterruptText(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.TrimStart().StartsWith(
                "[Request interrupted by user",
                StringComparison.OrdinalIgnoreCase);

    private static bool TryGetTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty("timestamp", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out timestamp);
    }

    private static IEnumerable<string> ReadRecentLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var start = Math.Max(0, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        if (start > 0)
        {
            _ = reader.ReadLine();
        }

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

public sealed record TranscriptInterruptMarker(
    DateTimeOffset Timestamp,
    string MatchKind);

public sealed record TranscriptInterruptApplyResult(
    bool Applied,
    string SessionId,
    DateTimeOffset? Timestamp,
    string MatchKind)
{
    public static TranscriptInterruptApplyResult NotApplied { get; }
        = new(false, "", null, "");
}
