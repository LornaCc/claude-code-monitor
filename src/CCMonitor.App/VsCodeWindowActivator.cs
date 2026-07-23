using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CCMonitor.App;

public static class VsCodeWindowActivator
{
    private const int SwRestore = 9;
    private const uint AsfwAny = uint.MaxValue;

    public static ForegroundActivationGrantResult TryAllowBridgeForegroundActivation()
    {
        // This is intentionally called directly from the session-card click handler.
        // At that point CC Monitor owns the foreground permission granted by the user's
        // click and can briefly pass it to the VS Code process handling the bridge request.
        var granted = AllowSetForegroundWindow(AsfwAny);
        return new ForegroundActivationGrantResult(
            granted,
            granted ? 0 : Marshal.GetLastWin32Error());
    }

    public static bool IsCodeWindowForeground()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return false;

        GetWindowThreadProcessId(handle, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "Code", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static VsCodeWindowActivationResult TryActivate(string workingDirectory, string projectName)
    {
        var candidates = BuildCandidates(workingDirectory, projectName)
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return new VsCodeWindowActivationResult(false, "", "No project title candidates were available.", 0);
        }

        var windows = EnumerateCodeWindows();

        var matches = windows
            .Where(window => IsMatch(window.Title, candidates))
            .ToList();
        if (matches.Count == 0)
        {
            return new VsCodeWindowActivationResult(
                false,
                "",
                $"No VS Code window title matched project={projectName}.",
                windows.Count);
        }

        if (matches.Count > 1)
        {
            return new VsCodeWindowActivationResult(
                false,
                "",
                $"Multiple VS Code window titles matched project={projectName}.",
                matches.Count);
        }

        var match = matches[0];
        var activationPlan = CreateActivationPlan(
            IsIconic(match.Handle),
            IsZoomed(match.Handle));
        if (activationPlan.RestoreBeforeActivation)
        {
            ShowWindow(match.Handle, SwRestore);
        }

        BringWindowToTop(match.Handle);
        var activated = SetForegroundWindow(match.Handle);
        var finalWindowState = DescribeWindowState(
            IsIconic(match.Handle),
            IsZoomed(match.Handle));
        return new VsCodeWindowActivationResult(
            activated,
            match.Title,
            activated ? "Matched a unique VS Code window title." : "Windows rejected foreground activation.",
            1,
            activationPlan.InitialWindowState,
            finalWindowState,
            activationPlan.RestoreBeforeActivation);
    }

    internal static WindowActivationPlan CreateActivationPlan(bool isMinimized, bool isMaximized)
        => new(
            DescribeWindowState(isMinimized, isMaximized),
            RestoreBeforeActivation: isMinimized);

    private static string DescribeWindowState(bool isMinimized, bool isMaximized)
        => isMinimized
            ? "minimized"
            : isMaximized
                ? "maximized"
                : "normalOrArranged";

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
        var titleSegments = title
            .Split(
                [" - ", " \u2013 ", " \u2014 ", " | "],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(segment => segment.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates.Any(candidate =>
            candidate.Length >= 3
            && titleSegments.Contains(candidate));
    }

    private static IReadOnlyList<CodeWindow> EnumerateCodeWindows()
    {
        var windows = new List<CodeWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;

            var titleLength = GetWindowTextLength(handle);
            if (titleLength <= 0) return true;

            GetWindowThreadProcessId(handle, out var processId);
            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, "Code", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }

            var title = new StringBuilder(titleLength + 1);
            if (GetWindowText(handle, title, title.Capacity) > 0)
            {
                windows.Add(new CodeWindow(handle, title.ToString()));
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private readonly record struct CodeWindow(IntPtr Handle, string Title);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

public sealed record ForegroundActivationGrantResult(bool Granted, int Win32Error);

public sealed record VsCodeWindowActivationResult(
    bool Activated,
    string MatchedTitle,
    string Reason,
    int CandidateCount,
    string InitialWindowState = "",
    string FinalWindowState = "",
    bool RestoreInvoked = false);

internal readonly record struct WindowActivationPlan(
    string InitialWindowState,
    bool RestoreBeforeActivation);
