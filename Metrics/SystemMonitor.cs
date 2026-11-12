using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FpsOverlay.Native;

namespace FpsOverlay.Metrics;

/// <summary>Snapshot of system metrics. NaN = value unavailable.</summary>
internal sealed record SystemSnapshot(double Cpu, double Gpu, double RamUsedGb, double RamTotalGb)
{
    public static readonly SystemSnapshot Empty = new(double.NaN, double.NaN, double.NaN, double.NaN);
}

/// <summary>
/// Polls CPU, GPU (via PDH) and RAM (GlobalMemoryStatusEx) once a second on a background thread.
/// The UI only reads the ready snapshot <see cref="Current"/>.
/// </summary>
internal sealed class SystemMonitor : IDisposable
{
    private const int IntervalMs = 1000;

    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _gpuCounter;

    private volatile SystemSnapshot _current = SystemSnapshot.Empty;
    public SystemSnapshot Current => _current;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SystemMonitor", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        InitPdh();
        if (_query != IntPtr.Zero) Pdh.PdhCollectQueryData(_query); // first sample is the baseline

        var (ramUsed, ramTotal) = ReadRam(); // RAM is shown at once, CPU/GPU after the second sample
        _current = _current with { RamUsedGb = ramUsed, RamTotalGb = ramTotal };

        while (!_stop.Wait(IntervalMs))
        {
            double cpu = double.NaN, gpu = double.NaN;
            if (_query != IntPtr.Zero && Pdh.PdhCollectQueryData(_query) == Pdh.ERROR_SUCCESS)
            {
                cpu = ReadCpu();
                gpu = ReadGpu();
            }
            var (used, total) = ReadRam();
            _current = new SystemSnapshot(cpu, gpu, used, total);
        }

        if (_query != IntPtr.Zero) Pdh.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private void InitPdh()
    {
        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) != Pdh.ERROR_SUCCESS)
        {
            _query = IntPtr.Zero;
            return;
        }

        // "% Processor Utility" matches Task Manager on Win10/11 (accounts for turbo clocks).
        // Older systems may lack it; then the classic "% Processor Time".
        if (Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != Pdh.ERROR_SUCCESS &&
            Pdh.PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) != Pdh.ERROR_SUCCESS)
        {
            _cpuCounter = IntPtr.Zero;
        }

        // Load of every GPU engine for every process.
        if (Pdh.PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != Pdh.ERROR_SUCCESS)
        {
            _gpuCounter = IntPtr.Zero;
        }
    }

    private double ReadCpu()
    {
        if (_cpuCounter == IntPtr.Zero) return double.NaN;
        uint st = Pdh.PdhGetFormattedCounterValue(_cpuCounter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100, out _, out var v);
        if (st != Pdh.ERROR_SUCCESS || !Pdh.IsValid(v.CStatus)) return double.NaN;
        return Math.Clamp(v.doubleValue, 0, 100);
    }

    /// <summary>
    /// Like Task Manager: sum the load over processes for each engine
    /// (3D, Copy, VideoDecode, Compute…) and take the maximum across engines.
    /// Instance name: pid_1234_luid_0x..._0x..._phys_0_eng_0_engtype_3D
    /// </summary>
    private double ReadGpu()
    {
        if (_gpuCounter == IntPtr.Zero) return double.NaN;

        const uint fmt = Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100;
        uint size = 0;
        uint st = Pdh.PdhGetFormattedCounterArray(_gpuCounter, fmt, ref size, out uint count, IntPtr.Zero);
        if (st != Pdh.PDH_MORE_DATA || size == 0) return double.NaN;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            st = Pdh.PdhGetFormattedCounterArray(_gpuCounter, fmt, ref size, out count, buffer);
            if (st != Pdh.ERROR_SUCCESS) return double.NaN;

            var perEngine = new Dictionary<string, double>(64);
            int itemSize = Marshal.SizeOf<Pdh.PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Pdh.PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (!Pdh.IsValid(item.FmtValue.CStatus)) continue;

                string name = Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                string engine = luid >= 0 ? name.Substring(luid) : name;

                perEngine.TryGetValue(engine, out double sum);
                perEngine[engine] = sum + item.FmtValue.doubleValue;
            }

            double max = 0;
            foreach (double v in perEngine.Values) if (v > max) max = v;
            return Math.Clamp(max, 0, 100);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (double used, double total) ReadRam()
    {
        var m = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref m)) return (double.NaN, double.NaN);
        const double gb = 1024.0 * 1024 * 1024;
        return ((m.ullTotalPhys - m.ullAvailPhys) / gb, m.ullTotalPhys / gb);
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(2000);
        _stop.Dispose();
    }
}
