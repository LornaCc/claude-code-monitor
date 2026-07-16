using System.Runtime.InteropServices;

namespace CCMonitor.Core.Services;

public static class TerminalProcessLocator
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static int? FindTerminalShellProcessId()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var processes = SnapshotProcesses();
        if (processes.Count == 0) return null;

        var visited = new HashSet<int>();
        var processId = Environment.ProcessId;
        int? outermostShellProcessId = null;

        while (processes.TryGetValue(processId, out var process) && visited.Add(processId))
        {
            if (IsTerminalShell(process.ExecutableName))
            {
                outermostShellProcessId = process.ProcessId;
            }

            if (process.ExecutableName.Equals("Code.exe", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            processId = process.ParentProcessId;
        }

        return outermostShellProcessId;
    }

    private static bool IsTerminalShell(string executableName)
        => executableName.Equals("bash.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("zsh.exe", StringComparison.OrdinalIgnoreCase)
            || executableName.Equals("fish.exe", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<int, ProcessEntry> SnapshotProcesses()
    {
        var result = new Dictionary<int, ProcessEntry>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;

            do
            {
                result[(int)entry.ProcessId] = new ProcessEntry(
                    (int)entry.ProcessId,
                    (int)entry.ParentProcessId,
                    entry.ExecutableFile ?? "");
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
