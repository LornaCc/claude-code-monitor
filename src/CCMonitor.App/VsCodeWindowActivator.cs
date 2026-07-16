using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CCMonitor.App;

public static class VsCodeWindowActivator
{
    private const int SwRestore = 9;

    public static bool TryActivate(string workingDirectory, string projectName, out string matchedTitle)
    {
        matchedTitle = "";
        var candidates = BuildCandidates(workingDirectory, projectName).ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var windows = Process.GetProcessesByName("Code")
            .Where(process => process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
            .Select(process => new CodeWindow(process.MainWindowHandle, process.MainWindowTitle))
            .ToList();

        var match = windows.FirstOrDefault(window => IsMatch(window.Title, candidates));
        if (match.Handle == IntPtr.Zero)
        {
            match = windows.FirstOrDefault(window => window.Title.Contains("Visual Studio Code", StringComparison.OrdinalIgnoreCase));
        }

        if (match.Handle == IntPtr.Zero)
        {
            return false;
        }

        matchedTitle = match.Title;
        ShowWindow(match.Handle, SwRestore);
        BringWindowToTop(match.Handle);
        return SetForegroundWindow(match.Handle);
    }

    private static IEnumerable<string> BuildCandidates(string workingDirectory, string projectName)
    {
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            yield return Normalize(projectName);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var folderName = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                yield return Normalize(folderName);
            }
        }
    }

    private static bool IsMatch(string title, IReadOnlyCollection<string> candidates)
    {
        var normalizedTitle = Normalize(title);
        return candidates.Any(candidate => candidate.Length > 0 && normalizedTitle.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private readonly record struct CodeWindow(IntPtr Handle, string Title);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
