namespace ServiceLib.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessTree
{
    internal static IReadOnlyList<ProcessIdentity> Snapshot(IEnumerable<ProcessIdentity> roots)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), File = "" };
            if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { parents[(int)entry.Pid] = (int)entry.Parent; } while (Process32NextW(snapshot, ref entry));
            if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(snapshot); }
        var owned = new Dictionary<int, ProcessIdentity>();
        foreach (var root in roots)
        {
            var current = Read(root.Pid);
            if (current == null) continue;
            if (current.StartTime != root.StartTime) throw new InvalidOperationException("Process identity changed: " + root.Pid);
            owned[root.Pid] = current;
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var (pid, parent) in parents)
                if (owned.ContainsKey(parent) && !owned.ContainsKey(pid) && Read(pid) is { } identity)
                { owned[pid] = identity; changed = true; }
        } while (changed);
        return owned.Values.ToArray();
    }

    internal static ProcessIdentity? Read(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new(pid, process.StartTime.ToUniversalTime().Ticks.ToString(), process.ProcessName);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; } // Exited between lookup and StartTime.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string File;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
