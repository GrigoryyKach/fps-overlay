using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FpsOverlay.Native;

namespace FpsOverlay.Metrics;

/// <summary>Снимок системных метрик. NaN = значение недоступно.</summary>
internal sealed record SystemSnapshot(double Cpu, double Gpu, double RamUsedGb, double RamTotalGb)
{
    public static readonly SystemSnapshot Empty = new(double.NaN, double.NaN, double.NaN, double.NaN);
}

/// <summary>
/// Раз в секунду в фоновом потоке опрашивает CPU, GPU (через PDH) и RAM (GlobalMemoryStatusEx).
/// UI только читает готовый снимок <see cref="Current"/>.
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
        if (_query != IntPtr.Zero) Pdh.PdhCollectQueryData(_query); // первый замер — точка отсчёта

        var (ramUsed, ramTotal) = ReadRam(); // RAM показываем сразу, CPU/GPU — после второго замера
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

        // "% Processor Utility" совпадает с Диспетчером задач Win10/11 (учитывает турбо-частоты).
        // На старых системах его может не быть — тогда классический "% Processor Time".
        if (Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpuCounter) != Pdh.ERROR_SUCCESS &&
            Pdh.PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) != Pdh.ERROR_SUCCESS)
        {
            _cpuCounter = IntPtr.Zero;
        }

        // Загрузка каждого движка видеокарты по каждому процессу.
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
    /// Как в Диспетчере задач: суммируем загрузку по процессам для каждого движка
    /// (3D, Copy, VideoDecode, Compute…) и берём максимум среди движков.
    /// Имя экземпляра: pid_1234_luid_0x..._0x..._phys_0_eng_0_engtype_3D
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
