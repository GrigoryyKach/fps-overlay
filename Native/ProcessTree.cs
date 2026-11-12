using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FpsOverlay.Native;

/// <summary>
/// Child processes that present frames on behalf of a window: browsers (Chrome, Edge, Firefox)
/// and Electron apps draw from a separate GPU process, not from the window's process.
/// </summary>
internal static class ProcessTree
{
    // WebView2 processes belong to the app but are named differently from its exe.
    private static readonly string[] SharedRenderers = { "msedgewebview2.exe" };

    /// <summary>
    /// Descendants of <paramref name="root"/> with the same exe name (or WebView2), excluding root itself.
    /// Foreign programs launched from this process (for example, games started from Explorer) are not included.
    /// </summary>
    public static HashSet<int> RenderingChildren(int root)
    {
        var result = new HashSet<int>();
        if (root <= 0) return result;

        var parent = new Dictionary<int, int>();
        var name = new Dictionary<int, string>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return result;
            do
            {
                parent[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                name[(int)e.th32ProcessID] = e.szExeFile;
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }

        if (!name.TryGetValue(root, out var rootName)) return result;

        // Walk up the parents: a process qualifies if the chain reaches root through processes of the same app.
        foreach (var (pid, exe) in name)
        {
            if (pid == root) continue;
            bool same = string.Equals(exe, rootName, StringComparison.OrdinalIgnoreCase) ||
                        Array.Exists(SharedRenderers, r => string.Equals(exe, r, StringComparison.OrdinalIgnoreCase));
            if (!same) continue;

            int cur = pid;
            for (int depth = 0; depth < 8 && parent.TryGetValue(cur, out int p) && p != 0 && p != cur; depth++)
            {
                if (p == root) { result.Add(pid); break; }
                cur = p;
            }
        }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
