using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FpsOverlay.Native;

/// <summary>
/// Дочерние процессы, которые выводят кадры за окно: браузеры (Chrome, Edge, Firefox)
/// и Electron-приложения рисуют из отдельного GPU-процесса, а не из процесса окна.
/// </summary>
internal static class ProcessTree
{
    // Процессы WebView2 принадлежат приложению, но называются иначе, чем его exe.
    private static readonly string[] SharedRenderers = { "msedgewebview2.exe" };

    /// <summary>
    /// Потомки <paramref name="root"/> с тем же именем exe (или WebView2), без самого root.
    /// Чужие программы, запущенные из этого процесса (например, игры из Проводника), не попадают.
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

        // Обход по родителям: процесс подходит, если цепочка доходит до root через процессы того же приложения.
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
