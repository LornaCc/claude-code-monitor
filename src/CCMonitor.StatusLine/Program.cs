using System.Text.Json;
using CCMonitor.Core.Models;
using CCMonitor.Core.Services;

var paths = new CcMonitorPaths();
var logger = new RollingLogger(Path.Combine(paths.LogsDirectory, "cc-monitor-statusline.log"));

try
{
    paths.EnsureDirectories();
    var input = await Console.In.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(input))
    {
        Console.Write("CC Monitor");
        return 0;
    }

    using var document = ParseFirstJsonDocument(input);
    var root = document.RootElement;
    var transcriptPath = GetString(root, "transcript_path") ?? GetString(root, "transcriptPath");
    var sessionId = GetString(root, "session_id") ?? GetString(root, "sessionId") ?? InferSessionIdFromTranscriptPath(transcriptPath) ?? "";
    if (string.IsNullOrWhiteSpace(sessionId))
    {
        Console.Write("CC Monitor");
        return 0;
    }

    var hasDirectContextSnapshot = GetNestedLong(root, "context_window", "total_input_tokens") is not null
        || GetContextUsedPercent(root) is not null;
    var transcriptMetrics = hasDirectContextSnapshot
        ? new SessionUsageMetrics()
        : ReadTranscriptUsage(transcriptPath);

    var contextWindowSize = GetNestedLong(root, "context_window", "context_window_size")
        ?? transcriptMetrics.ContextWindowSizeTokens;
    var currentUsage = GetNestedObject(root, "context_window", "current_usage");
    var uncachedInputTokens = GetLong(currentUsage, "input_tokens") ?? transcriptMetrics.UncachedInputTokens;
    var cacheCreationInputTokens = GetLong(currentUsage, "cache_creation_input_tokens") ?? transcriptMetrics.CacheCreationInputTokens;
    var cacheReadInputTokens = GetLong(currentUsage, "cache_read_input_tokens") ?? transcriptMetrics.CacheReadInputTokens;
    var contextInputTokens = GetNestedLong(root, "context_window", "total_input_tokens")
        ?? SumInputTokens(uncachedInputTokens, cacheCreationInputTokens, cacheReadInputTokens)
        ?? transcriptMetrics.InputTokens;
    var contextOutputTokens = GetNestedLong(root, "context_window", "total_output_tokens")
        ?? GetLong(currentUsage, "output_tokens")
        ?? transcriptMetrics.OutputTokens;

    var metrics = new SessionUsageMetrics
    {
        SessionId = sessionId,
        ContextUsedPercent = GetContextUsedPercent(root) ?? transcriptMetrics.ContextUsedPercent,
        ContextRemainingPercent = GetContextRemainingPercent(root) ?? transcriptMetrics.ContextRemainingPercent,
        ContextWindowSizeTokens = contextWindowSize,
        InputTokens = contextInputTokens,
        UncachedInputTokens = uncachedInputTokens,
        CacheCreationInputTokens = cacheCreationInputTokens,
        CacheReadInputTokens = cacheReadInputTokens,
        OutputTokens = contextOutputTokens,
        TotalTokens = AddNullable(contextInputTokens, contextOutputTokens),
        TotalCostUsd = GetNestedDecimal(root, "cost", "total_cost_usd"),
        ModelName = GetNestedString(root, "model", "display_name")
            ?? GetNestedString(root, "model", "id")
            ?? transcriptMetrics.ModelName,
        UpdatedAt = DateTimeOffset.Now
    };

    if (metrics.ContextUsedPercent is null
        && metrics.InputTokens is not null
        && metrics.ContextWindowSizeTokens is > 0)
    {
        metrics.ContextUsedPercent = Math.Clamp(
            metrics.InputTokens.Value * 100d / metrics.ContextWindowSizeTokens.Value,
            0,
            100);
    }

    if (metrics.ContextRemainingPercent is null && metrics.ContextUsedPercent is not null)
    {
        metrics.ContextRemainingPercent = Math.Max(0, 100 - metrics.ContextUsedPercent.Value);
    }

    await new SessionUsageMetricsStore(paths).SaveAtomicAsync(metrics);
    var interruptResult = await new TranscriptInterruptStateService(paths)
        .ApplyAsync(transcriptPath);
    logger.Info(
        $"usage saved session={sessionId} transcript={File.Exists(transcriptPath)} " +
        $"contextTokens={metrics.InputTokens?.ToString() ?? "n/a"}/{metrics.ContextWindowSizeTokens?.ToString() ?? "n/a"} " +
        $"context={metrics.ContextUsedPercent?.ToString("0.#") ?? "n/a"} " +
        $"cost={(metrics.TotalCostUsd is null ? "n/a" : "available")} " +
        $"interruptApplied={interruptResult.Applied}");
    Console.Write("CC Monitor");
}
catch (Exception ex)
{
    logger.Error(ex, "statusline failed");
    Console.Write("CC Monitor");
}

return 0;

static string? GetString(JsonElement root, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;
}

static double? GetNumber(JsonElement root, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(propertyName, out var value)
        && value.TryGetDouble(out var number)
        ? number
        : null;
}

static long? GetLong(JsonElement root, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(propertyName, out var value)
        && value.TryGetInt64(out var number)
        ? number
        : null;
}

static decimal? GetDecimal(JsonElement root, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(propertyName, out var value)
        && value.TryGetDecimal(out var number)
        ? number
        : null;
}

static double? GetNestedNumber(JsonElement root, string parent, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parent, out var node) ? GetNumber(node, propertyName) : null;
}

static long? GetNestedLong(JsonElement root, string parent, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parent, out var node) ? GetLong(node, propertyName) : null;
}

static decimal? GetNestedDecimal(JsonElement root, string parent, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parent, out var node) ? GetDecimal(node, propertyName) : null;
}

static JsonElement GetNestedObject(JsonElement root, string parent, string propertyName)
{
    if (root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(parent, out var node)
        && node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Object)
    {
        return value;
    }

    return default;
}

static string? GetNestedString(JsonElement root, string parent, string propertyName)
{
    return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(parent, out var node) ? GetString(node, propertyName) : null;
}

static JsonDocument ParseFirstJsonDocument(string input)
{
    input = input.Trim('\uFEFF', '\u200B', '\r', '\n', '\t', ' ');
    if (string.IsNullOrWhiteSpace(input))
    {
        throw new JsonException("statusline input was empty after trimming.");
    }

    JsonException? lastException = null;
    var searchIndex = 0;
    while (searchIndex < input.Length)
    {
        var objectStart = input.IndexOf('{', searchIndex);
        if (objectStart < 0) break;

        var candidate = ExtractFirstJsonObject(input[objectStart..]);
        try
        {
            var document = JsonDocument.Parse(candidate, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("session_id", out _)
                    || root.TryGetProperty("sessionId", out _)
                    || root.TryGetProperty("transcript_path", out _)
                    || root.TryGetProperty("transcriptPath", out _)))
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException ex)
        {
            lastException = ex;
        }

        searchIndex = objectStart + 1;
    }

    throw new JsonException("statusline input did not contain a valid Claude Code JSON object.", lastException);
}

static string ExtractFirstJsonObject(string input)
{
    var depth = 0;
    var inString = false;
    var escaped = false;

    for (var index = 0; index < input.Length; index++)
    {
        var character = input[index];
        if (escaped)
        {
            escaped = false;
            continue;
        }

        if (character == '\\' && inString)
        {
            escaped = true;
            continue;
        }

        if (character == '"')
        {
            inString = !inString;
            continue;
        }

        if (inString)
        {
            continue;
        }

        if (character == '{')
        {
            depth++;
        }
        else if (character == '}')
        {
            depth--;
            if (depth == 0)
            {
                return input[..(index + 1)];
            }
        }
    }

    return input;
}

static double? GetContextUsedPercent(JsonElement root)
{
    return GetNumber(root, "context_used_percent")
        ?? GetNumber(root, "contextUsedPercent")
        ?? GetNestedNumber(root, "context", "used_percent")
        ?? GetNestedNumber(root, "context", "usedPercent")
        ?? GetNestedNumber(root, "context_usage", "used_percent")
        ?? GetNestedNumber(root, "contextUsage", "usedPercent")
        ?? GetNestedNumber(root, "context_window", "used_percentage");
}

static double? GetContextRemainingPercent(JsonElement root)
{
    return GetNumber(root, "context_remaining_percent")
        ?? GetNumber(root, "contextRemainingPercent")
        ?? GetNestedNumber(root, "context", "remaining_percent")
        ?? GetNestedNumber(root, "context", "remainingPercent")
        ?? GetNestedNumber(root, "context_usage", "remaining_percent")
        ?? GetNestedNumber(root, "contextUsage", "remainingPercent")
        ?? GetNestedNumber(root, "context_window", "remaining_percentage");
}

static long? SumInputTokens(long? inputTokens, long? cacheCreationTokens, long? cacheReadTokens)
{
    if (inputTokens is null && cacheCreationTokens is null && cacheReadTokens is null) return null;
    return (inputTokens ?? 0) + (cacheCreationTokens ?? 0) + (cacheReadTokens ?? 0);
}

static long? AddNullable(long? left, long? right)
{
    if (left is null && right is null) return null;
    return (left ?? 0) + (right ?? 0);
}

static string? InferSessionIdFromTranscriptPath(string? transcriptPath)
{
    if (string.IsNullOrWhiteSpace(transcriptPath))
    {
        return null;
    }

    try
    {
        return Path.GetFileNameWithoutExtension(transcriptPath);
    }
    catch
    {
        return null;
    }
}

static SessionUsageMetrics ReadTranscriptUsage(string? transcriptPath)
{
    var metrics = new SessionUsageMetrics();
    if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
    {
        return metrics;
    }

    foreach (var line in ReadRecentLines(transcriptPath, 2 * 1024 * 1024))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        try
        {
            using var lineDocument = JsonDocument.Parse(line);
            var root = lineDocument.RootElement;

            metrics.ContextUsedPercent ??= GetContextUsedPercent(root);
            metrics.ContextRemainingPercent ??= GetContextRemainingPercent(root);

            if (!TryGetUsage(root, out var usage))
            {
                continue;
            }

            metrics.UncachedInputTokens = GetLong(usage, "input_tokens");
            metrics.CacheCreationInputTokens = GetLong(usage, "cache_creation_input_tokens");
            metrics.CacheReadInputTokens = GetLong(usage, "cache_read_input_tokens");
            metrics.InputTokens = SumInputTokens(
                metrics.UncachedInputTokens,
                metrics.CacheCreationInputTokens,
                metrics.CacheReadInputTokens);
            metrics.OutputTokens = GetLong(usage, "output_tokens");
            metrics.TotalTokens = AddNullable(metrics.InputTokens, metrics.OutputTokens);
            metrics.ModelName = GetNestedString(root, "message", "model") ?? GetString(root, "model") ?? metrics.ModelName;
        }
        catch (JsonException)
        {
            // Tailing can start in the middle of a JSONL record; skip partial lines.
        }
        catch (IOException)
        {
            break;
        }
    }

    return metrics;
}

static bool TryGetUsage(JsonElement root, out JsonElement usage)
{
    if (root.TryGetProperty("message", out var message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("usage", out usage)
        && usage.ValueKind == JsonValueKind.Object)
    {
        return true;
    }

    if (root.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
    {
        return true;
    }

    usage = default;
    return false;
}

static IEnumerable<string> ReadRecentLines(string path, int maxBytes)
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
