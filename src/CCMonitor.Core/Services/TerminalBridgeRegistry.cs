using System.Text.Json;
using CCMonitor.Core.Models;

namespace CCMonitor.Core.Services;

public sealed class TerminalBridgeRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CcMonitorPaths _paths;
    private readonly TimeSpan _liveWindow;

    public TerminalBridgeRegistry(CcMonitorPaths paths, TimeSpan? liveWindow = null)
    {
        _paths = paths;
        _liveWindow = liveWindow ?? TimeSpan.FromSeconds(8);
    }

    public IReadOnlyList<TerminalBridgeRegistration> LoadLive(DateTimeOffset? now = null)
    {
        _paths.EnsureDirectories();
        var timestamp = now ?? DateTimeOffset.UtcNow;
        var registrations = new List<TerminalBridgeRegistration>();

        foreach (var path in Directory.EnumerateFiles(_paths.TerminalBridgesDirectory, "*.json"))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var registration = JsonSerializer.Deserialize<TerminalBridgeRegistration>(stream, JsonOptions);
                if (registration is null
                    || registration.ProtocolVersion < 2
                    || string.IsNullOrWhiteSpace(registration.BridgeId)
                    || timestamp - registration.UpdatedAtUtc > _liveWindow)
                {
                    continue;
                }

                registrations.Add(registration);
            }
            catch
            {
                // A bridge may be replacing its heartbeat file while it is being read.
            }
        }

        return registrations;
    }

    public TerminalBridgeSelection Select(
        IReadOnlyList<TerminalBridgeRegistration> liveBridges,
        string terminalToken,
        string workingDirectory,
        string projectName,
        ManualTerminalBinding? manualBinding = null,
        int? preferredTerminalProcessId = null)
    {
        if (liveBridges.Count == 0)
        {
            return new TerminalBridgeSelection(
                null,
                "bridgeNotRunning",
                "No live CC Monitor Terminal Bridge registration was found.",
                0);
        }

        if (manualBinding is not null)
        {
            var bindingMatches = liveBridges
                .Where(bridge => (bridge.Terminals ?? []).Any(
                    terminal => MatchesManualBinding(terminal, manualBinding)))
                .ToList();
            if (bindingMatches.Count == 1)
            {
                return new TerminalBridgeSelection(
                    bindingMatches[0],
                    "manualBinding",
                    $"Selected bridge {bindingMatches[0].BridgeId} by explicit session-terminal binding.",
                    liveBridges.Count);
            }

            return new TerminalBridgeSelection(
                null,
                bindingMatches.Count == 0
                    ? "manualTerminalNotRegistered"
                    : "ambiguousManualBinding",
                bindingMatches.Count == 0
                    ? "The manually bound terminal is no longer registered. Bind the session again."
                    : "Multiple live VS Code windows registered the manually bound terminal.",
                liveBridges.Count);
        }

        var requestedToken = NormalizeToken(terminalToken);
        if (requestedToken.Length > 0)
        {
            var tokenMatches = liveBridges
                .Where(bridge => (bridge.Terminals ?? []).Any(
                    terminal => NormalizeToken(terminal.TerminalToken) == requestedToken))
                .ToList();

            if (tokenMatches.Count == 1)
            {
                return new TerminalBridgeSelection(
                    tokenMatches[0],
                    "terminalToken",
                    $"Selected bridge {tokenMatches[0].BridgeId} by terminal token.",
                    liveBridges.Count);
            }

            return new TerminalBridgeSelection(
                null,
                tokenMatches.Count == 0 ? "terminalTokenNotRegistered" : "ambiguousTerminalToken",
                tokenMatches.Count == 0
                    ? "No live VS Code terminal registered the session terminal token."
                    : "Multiple live VS Code windows registered the same terminal token.",
                liveBridges.Count);
        }

        if (preferredTerminalProcessId is > 0)
        {
            var processMatches = liveBridges
                .Where(bridge => (bridge.Terminals ?? []).Any(
                    terminal => terminal.ProcessId == preferredTerminalProcessId))
                .ToList();
            if (processMatches.Count == 1)
            {
                return new TerminalBridgeSelection(
                    processMatches[0],
                    "terminalProcessId",
                    $"Selected bridge {processMatches[0].BridgeId} by terminal process ID.",
                    liveBridges.Count);
            }

            if (processMatches.Count > 1)
            {
                return new TerminalBridgeSelection(
                    null,
                    "ambiguousTerminalProcessId",
                    "Multiple live VS Code windows registered the same terminal process ID.",
                    liveBridges.Count);
            }

            // A process ID is an automatically observed hint and may become stale
            // after a terminal restart. Continue to safe cwd/workspace matching.
        }

        var requestedDirectory = NormalizePath(workingDirectory);
        var requestedProject = NormalizeName(projectName);
        var scored = liveBridges
            .Select(bridge => Score(bridge, requestedDirectory, requestedProject))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Bridge.BridgeId, StringComparer.Ordinal)
            .ToList();

        if (scored.Count == 0)
        {
            return new TerminalBridgeSelection(
                null,
                "noMatch",
                $"No registered VS Code window matched cwd={workingDirectory} project={projectName}.",
                liveBridges.Count);
        }

        var best = scored[0];
        if (scored.Count > 1 && scored[1].Score == best.Score)
        {
            return new TerminalBridgeSelection(
                null,
                "ambiguous",
                $"Multiple VS Code windows matched with equal confidence ({best.MatchKind}).",
                liveBridges.Count);
        }

        return new TerminalBridgeSelection(
            best.Bridge,
            best.MatchKind,
            $"Selected bridge {best.Bridge.BridgeId} by {best.MatchKind}.",
            liveBridges.Count);
    }

    private static ScoredBridge Score(
        TerminalBridgeRegistration bridge,
        string requestedDirectory,
        string requestedProject)
    {
        var bestScore = 0;
        var matchKind = "";

        foreach (var terminal in bridge.Terminals ?? [])
        {
            var terminalDirectory = NormalizePath(terminal.WorkingDirectory);
            if (requestedDirectory.Length > 0 && terminalDirectory == requestedDirectory)
            {
                return new ScoredBridge(bridge, 600, "exactTerminalWorkingDirectory");
            }

            var terminalAncestorScore = ScoreTerminalAncestor(
                requestedDirectory,
                terminalDirectory);
            if (terminalAncestorScore > bestScore
                && IsSafeTerminalAncestor(
                    terminalDirectory,
                    requestedDirectory,
                    bridge.WorkspaceFolders))
            {
                bestScore = terminalAncestorScore;
                matchKind = "workingDirectoryUnderTerminal";
            }

            if (requestedDirectory.Length > 0
                && IsSpecificProjectPath(requestedDirectory)
                && IsDescendant(terminalDirectory, requestedDirectory)
                && bestScore < 450)
            {
                bestScore = 450;
                matchKind = "terminalUnderWorkingDirectory";
            }
        }

        foreach (var folder in bridge.WorkspaceFolders ?? [])
        {
            var workspaceDirectory = NormalizePath(folder);
            if (requestedDirectory.Length > 0 && workspaceDirectory == requestedDirectory)
            {
                return new ScoredBridge(bridge, 550, "exactWorkspaceFolder");
            }

            if (requestedDirectory.Length > 0
                && IsDescendant(requestedDirectory, workspaceDirectory)
                && bestScore < 500)
            {
                bestScore = 500;
                matchKind = "workingDirectoryInWorkspace";
            }

            if (requestedProject.Length > 0
                && NormalizeName(Path.GetFileName(workspaceDirectory)) == requestedProject
                && bestScore < 350)
            {
                bestScore = 350;
                matchKind = "projectWorkspaceName";
            }
        }

        if (requestedProject.Length > 0
            && NormalizeName(bridge.WorkspaceName) == requestedProject
            && bestScore < 300)
        {
            bestScore = 300;
            matchKind = "workspaceName";
        }

        return new ScoredBridge(bridge, bestScore, matchKind);
    }

    internal static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Path.GetFullPath(value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToLowerInvariant();
        }
        catch
        {
            return value
                .Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToLowerInvariant();
        }
    }

    private static bool IsDescendant(string child, string parent)
        => child.Length > parent.Length
            && child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeTerminalAncestor(
        string terminalDirectory,
        string requestedDirectory,
        IReadOnlyList<string> workspaceFolders)
    {
        if (terminalDirectory.Length == 0
            || requestedDirectory.Length == 0
            || !IsDescendant(requestedDirectory, terminalDirectory))
        {
            return false;
        }

        var sameWorkspace = workspaceFolders
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Any(workspaceDirectory =>
                IsSameOrDescendant(terminalDirectory, workspaceDirectory)
                && IsSameOrDescendant(requestedDirectory, workspaceDirectory));
        if (sameWorkspace)
        {
            return true;
        }

        return IsSpecificProjectPath(terminalDirectory)
            && GetPathDistance(requestedDirectory, terminalDirectory) <= 2;
    }

    private static bool IsSameOrDescendant(string child, string parent)
        => child == parent || IsDescendant(child, parent);

    private static int ScoreTerminalAncestor(
        string requestedDirectory,
        string terminalDirectory)
    {
        if (requestedDirectory.Length == 0
            || terminalDirectory.Length == 0
            || !IsDescendant(requestedDirectory, terminalDirectory))
        {
            return 0;
        }

        var distance = GetPathDistance(requestedDirectory, terminalDirectory);
        return 541 - Math.Min(distance, 40);
    }

    private static int GetPathDistance(string child, string parent)
    {
        if (!IsDescendant(child, parent))
        {
            return int.MaxValue;
        }

        var relativePath = child[(parent.Length + 1)..];
        return relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool IsSpecificProjectPath(string path)
    {
        var root = Path.GetPathRoot(path);
        var relative = string.IsNullOrWhiteSpace(root) ? path : path[root.Length..];
        var segmentCount = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries).Length;
        return segmentCount >= 3;
    }

    private static string NormalizeName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();

    private static bool MatchesManualBinding(
        TerminalBridgeTerminal terminal,
        ManualTerminalBinding binding)
    {
        var bindingToken = NormalizeToken(binding.TerminalToken);
        if (bindingToken.Length > 0)
        {
            return NormalizeToken(terminal.TerminalToken) == bindingToken;
        }

        return binding.TerminalProcessId is not null
            && terminal.ProcessId == binding.TerminalProcessId;
    }

    private sealed record ScoredBridge(
        TerminalBridgeRegistration Bridge,
        int Score,
        string MatchKind);
}
