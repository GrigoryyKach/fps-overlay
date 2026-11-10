using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FpsOverlay.Metrics;

/// <summary>
/// Считает FPS без вмешательства в игру: подписывается на ETW-события,
/// которые Windows сама пишет при каждом выводе кадра (Present).
/// Так же работают PresentMon и Xbox Game Bar. Нужны права администратора.
/// </summary>
internal sealed class FpsMonitor : IDisposable
{
    // DirectX 10/11/12 (а также Vulkan/OpenGL, если драйвер выводит кадры через DXGI)
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int DxgiPresentStart = 42;

    // DirectX 9
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int D3D9PresentStart = 1;

    // Ядро графической подсистемы — запасной источник для API, не идущих через DXGI/D3D9
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int DxgKrnlPresent = 184;
    private const ulong DxgKrnlPresentKeyword = 0x8000000;

    private const string SessionName = "FpsOverlay_PresentTrace";
    private const double WindowMs = 1000;    // окно усреднения
    private const long StaleMs = 2500;       // нет кадров дольше — считаем, что процесс не рисует

    private const long FamilyRefreshMs = 2000; // как часто пересобирать список дочерних процессов

    /// <summary>Кадры одного процесса: отдельно от API (DXGI / D3D9) и от ядра.</summary>
    private sealed class Counter
    {
        public readonly Queue<double> Api = new();
        public readonly Queue<double> Kernel = new();
        public double LastApiTime, LastKernelTime;
        public long LastApiTick, LastKernelTick;
    }

    private readonly object _lock = new();
    // Процесс активного окна и его дочерние процессы того же приложения (GPU-процесс браузера).
    private readonly Dictionary<int, Counter> _counters = new();
    private int _targetPid;
    private long _familyTick;

    private TraceEventSession? _session;
    private Thread? _thread;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        if (TraceEventSession.IsElevated() != true)
        {
            Error = "Нужны права администратора";
            return;
        }

        try
        {
            // Если после аварийного завершения осталась сессия с тем же именем — она будет заменена.
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
            _session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, DxgKrnlPresentKeyword);

            _session.Source.AllEvents += OnEvent;

            var session = _session;
            _thread = new Thread(() =>
            {
                try { session.Source.Process(); } // блокирует до остановки сессии
                catch { /* сессия остановлена */ }
                IsRunning = false;
            })
            { IsBackground = true, Name = "FpsMonitor" };

            IsRunning = true;
            _thread.Start();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            IsRunning = false;
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnEvent(TraceEvent data)
    {
        Guid provider = data.ProviderGuid;
        int id = (int)data.ID;

        bool api = (provider == DxgiProvider && id == DxgiPresentStart) ||
                   (provider == D3D9Provider && id == D3D9PresentStart);
        bool kernel = !api && provider == DxgKrnlProvider && id == DxgKrnlPresent;
        if (!api && !kernel) return;

        double t = data.TimeStampRelativeMSec;
        long now = Environment.TickCount64;

        lock (_lock)
        {
            if (!_counters.TryGetValue(data.ProcessID, out var c)) return;
            if (api)
            {
                Push(c.Api, t);
                c.LastApiTime = t;
                c.LastApiTick = now;
            }
            else
            {
                Push(c.Kernel, t);
                c.LastKernelTime = t;
                c.LastKernelTick = now;
            }
        }
    }

    private static void Push(Queue<double> q, double t)
    {
        q.Enqueue(t);
        while (q.Count > 0 && t - q.Peek() > WindowMs) q.Dequeue();
    }

    /// <summary>
    /// FPS указанного процесса или null, если процесс сейчас не выводит кадры
    /// (или мониторинг недоступен). Если окно рисует не сам процесс, а его GPU-процесс
    /// (браузеры, Electron), берутся кадры дочернего процесса.
    /// </summary>
    public int? GetFps(int pid)
    {
        if (!IsRunning) return null;

        long now = Environment.TickCount64;
        bool retarget = pid != Volatile.Read(ref _targetPid);
        if (retarget || now - _familyTick > FamilyRefreshMs)
        {
            var family = Native.ProcessTree.RenderingChildren(pid); // снимок процессов — вне блокировки
            family.Add(pid);
            lock (_lock)
            {
                if (retarget) _counters.Clear();
                foreach (int old in new List<int>(_counters.Keys))
                    if (!family.Contains(old)) _counters.Remove(old);
                foreach (int p in family)
                    if (!_counters.ContainsKey(p)) _counters[p] = new Counter();
                _targetPid = pid;
            }
            _familyTick = now;
            if (retarget) return null;
        }

        lock (_lock)
        {
            // Сам процесс окна важнее дочерних: так игра никогда не подменяется вспомогательным процессом.
            if (_counters.TryGetValue(pid, out var own) && Fps(own, now) is int f) return f;

            int? best = null;
            foreach (var (p, c) in _counters)
                if (p != pid && Fps(c, now) is int v && (best is null || v > best)) best = v;
            return best;
        }
    }

    private static int? Fps(Counter c, long now)
    {
        bool apiAlive = c.LastApiTick != 0 && now - c.LastApiTick < StaleMs;
        bool kernelAlive = c.LastKernelTick != 0 && now - c.LastKernelTick < StaleMs;

        // Предпочитаем DXGI/D3D9; ядро — только если от API событий нет (чтобы не считать кадр дважды).
        Queue<double> q;
        double last;
        if (apiAlive) { q = c.Api; last = c.LastApiTime; }
        else if (kernelAlive) { q = c.Kernel; last = c.LastKernelTime; }
        else return null;

        if (q.Count < 2) return q.Count;

        double span = last - q.Peek();
        if (span < 100) return q.Count;
        return (int)Math.Round((q.Count - 1) * 1000.0 / span);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
        _thread?.Join(2000);
        IsRunning = false;
    }
}
