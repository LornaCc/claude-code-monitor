namespace CCMonitor.Core.Services;

public static class HookCommandFormatter
{
    public static string ForShell(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath).Replace('\\', '/');
        if (fullPath.Length >= 3 && fullPath[1] == ':' && fullPath[2] == '/')
        {
            var drive = char.ToLowerInvariant(fullPath[0]);
            fullPath = $"/{drive}/{fullPath[3..]}";
        }

        return $"'{fullPath.Replace("'", "'\\''")}'";
    }
}
