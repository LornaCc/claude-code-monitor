using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class ClaudeSessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly CcMonitorPaths _paths;

    public ClaudeSessionStateStore(CcMonitorPaths paths)
    {
        _paths = paths;
    }

    public string GetSessionPath(string sessionId) => Path.Combine(_paths.SessionsDirectory, $"{SanitizeFileName(sessionId)}.json");

    public async Task WithSessionLockAsync(string sessionId, Func<Task> action, int timeoutMs = 500)
    {
        await Task.Factory.StartNew(() =>
        {
            using var mutex = new Mutex(false, $@"Global\CCMonitor.Session.{Hash(sessionId)}");
            if (!mutex.WaitOne(timeoutMs)) return;
            try
            {
                action().GetAwaiter().GetResult();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
    }

    public async Task<ClaudeSessionState> GetOrCreateAsync(string sessionId, string? workingDirectory = null)
    {
        _paths.EnsureDirectories();
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId) ? $"unknown-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}" : sessionId;
        var path = GetSessionPath(normalizedSessionId);
        var existing = await TryReadAsync(path);
        if (existing is not null) return existing;

        var now = DateTimeOffset.Now;
        return new ClaudeSessionState
        {
            SessionId = normalizedSessionId,
            WorkingDirectory = workingDirectory ?? "",
            ProjectName = ProjectNameResolver.FromWorkingDirectory(workingDirectory),
            Status = ClaudeSessionStatus.Idle,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public async Task SaveAtomicAsync(ClaudeSessionState state)
    {
        _paths.EnsureDirectories();
        var path = GetSessionPath(state.SessionId);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
            await stream.FlushAsync();
        }

        if (File.Exists(path))
        {
            File.Move(tempPath, path, overwrite: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    public void Delete(string sessionId)
    {
        var path = GetSessionPath(sessionId);
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<IReadOnlyList<ClaudeSessionState>> LoadAllAsync()
    {
        _paths.EnsureDirectories();
        var states = new List<ClaudeSessionState>();
        foreach (var path in Directory.EnumerateFiles(_paths.SessionsDirectory, "*.json"))
        {
            var state = await TryReadWithRetryAsync(path);
            if (state is not null) states.Add(state);
        }

        return states.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public void RemoveExpiredClosedSessions(int retentionHours)
    {
        _paths.EnsureDirectories();
        var cutoff = DateTimeOffset.Now.AddHours(-Math.Max(1, retentionHours));
        foreach (var path in Directory.EnumerateFiles(_paths.SessionsDirectory, "*.json"))
        {
            ClaudeSessionState? state;
            try
            {
                state = JsonSerializer.Deserialize<ClaudeSessionState>(File.ReadAllText(path), JsonOptions);
            }
            catch
            {
                state = null;
            }

            if (state?.Status == ClaudeSessionStatus.Closed && state.UpdatedAt < cutoff)
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<ClaudeSessionState?> TryReadWithRetryAsync(string path)
    {
        for (var i = 0; i < 3; i++)
        {
            var state = await TryReadAsync(path);
            if (state is not null) return state;
            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<ClaudeSessionState?> TryReadAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer.DeserializeAsync<ClaudeSessionState>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16];
    }
}
