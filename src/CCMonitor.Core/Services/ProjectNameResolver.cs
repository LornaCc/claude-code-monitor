namespace CCMonitor.Core.Services;

public static class ProjectNameResolver
{
    public static string FromWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory)) return "Unknown Project";

        var trimmed = workingDirectory.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return "Unknown Project";

        var lastSlash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
