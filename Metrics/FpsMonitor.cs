using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FpsOverlay.Metrics;

/// <summary>
/// Counts FPS without touching the game: subscribes to the ETW events
/// that Windows itself writes on every presented frame (Present).
/// PresentMon and Xbox Game Bar work the same way. Administrator rights are required.
/// </summary>
internal sealed class FpsMonitor : IDisposable
{
    // DirectX 10/11/12 (and Vulkan/OpenGL when the driver presents through DXGI)
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int DxgiPresentStart = 42;

    // DirectX 9
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int D3D9PresentStart = 1;

    // Graphics kernel: a fallback source for APIs that do not go through DXGI/D3D9
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int DxgKrnlPresent = 184;
    private const ulong DxgKrnlPresentKeyword = 0x8000000;

    private const string SessionName = "FpsOverlay_PresentTrace";
    private const double WindowMs = 1000;    // averaging window
    private const long StaleMs = 2500;       // no frames for longer than this: the process is not rendering

    private const long FamilyRefreshMs = 2000; // how often to rebuild the child process list

    /// <summary>Frames of one process: kept separately for the APIs (DXGI / D3D9) and for the kernel.</summary>
    private sealed class Counter
    {
        public readonly Queue<double> Api = new();
        public readonly Queue<double> Kernel = new();
        public double LastApiTime, LastKernelTime;
        public long LastApiTick, LastKernelTick;
    }

    private readonly object _lock = new();
    // The active window's process and child processes of the same app (a browser's GPU process).
    private readonly Dictionary<int, Counter> _counters = new();
    private int _targetPid;
    private long _familyTick;

    private TraceEventSession? _session;
    private Thread? _thread;

    public bool IsRunning { get; private set; }
    /// <summary>Value of <see cref="Error"/> when the ETW session cannot start without administrator rights.</summary>
    public const string AdminRequired = "admin";

    public string? Error { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        if (TraceEventSession.IsElevated() != true)
        {
            Error = AdminRequired;
            return;
        }

        try
        {
            // A leftover session with the same name after a crash will be replaced.
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
            _session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, DxgKrnlPresentKeyword);

            _session.Source.AllEvents += OnEvent;

            var session = _session;
            _thread = new Thread(() =>
            {
                try { session.Source.Process(); } // blocks until the session stops
                catch { /* session stopped */ }
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
    /// FPS of the given process, or null if the process is not presenting frames
    /// (or monitoring is unavailable). If the window is drawn not by the process itself but by its GPU process
    /// (browsers, Electron), the child process frames are used.
    /// </summary>
    public int? GetFps(int pid)
    {
        if (!IsRunning) return null;

        long now = Environment.TickCount64;
        bool retarget = pid != Volatile.Read(ref _targetPid);
        if (retarget || now - _familyTick > FamilyRefreshMs)
        {
            var family = Native.ProcessTree.RenderingChildren(pid); // process snapshot: outside the lock
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
            // The window's own process wins over its children, so a game is never replaced by a helper process.
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

        // Prefer DXGI/D3D9; use the kernel only when the APIs report nothing (so a frame is not counted twice).
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
