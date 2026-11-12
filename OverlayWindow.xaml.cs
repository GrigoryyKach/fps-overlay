using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FpsOverlay.Metrics;
using static FpsOverlay.Native.Win32;

namespace FpsOverlay;

/// <summary>
/// A tiny always-on-top window. Normally a "ghost": clicks pass through,
/// and it never takes focus. While Alt is held it can be grabbed and dragged,
/// and on release it snaps to the nearest monitor corner.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double EdgeMarginDip = 4;

    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush ValueBrush = Brushes.White;
    private static readonly Brush GoodBrush = Frozen(Color.FromRgb(0x6E, 0xE7, 0x7A));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush BadBrush = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x4F, 0xC3, 0xF7));

    private readonly Settings _settings;
    private readonly SystemMonitor _system = new();
    private readonly FpsMonitor _fps = new();

    private readonly Run _cpuRun = new(" --") { Foreground = ValueBrush };
    private readonly Run _gpuRun = new(" --") { Foreground = ValueBrush };
    private readonly Run _ramRun = new(" --/--") { Foreground = ValueBrush };
    private readonly Run _fpsRun = new(" --") { Foreground = ValueBrush };

    private readonly DispatcherTimer _altTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _topmostTimer;

    private readonly int _ownPid = Environment.ProcessId;
    private IntPtr _hwnd;
    private bool _interactive;
    private bool _dragging;
    private int _targetPid;

    internal OverlayWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;

        // C 23 G 97 R  9.1/32 F144
        Readout.Inlines.Add(Label("C"));
        Readout.Inlines.Add(_cpuRun);
        Readout.Inlines.Add(Label(" G"));
        Readout.Inlines.Add(_gpuRun);
        Readout.Inlines.Add(Label(" R"));
        Readout.Inlines.Add(_ramRun);
        Readout.Inlines.Add(Label(" F"));
        Readout.Inlines.Add(_fpsRun);

        _altTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _altTimer.Tick += (_, _) => PollAlt();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => RefreshReadout();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();

        Loaded += OnLoaded;
        SizeChanged += (_, _) => { if (!_dragging) ApplyCorner(); };
    }

    internal FpsMonitor Fps => _fps;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        long ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);

        // Start right away (synchronously inside Show()) so the tray already knows whether FPS is available
        _system.Start();
        _fps.Start();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyCorner();
        _altTimer.Start();
        _refreshTimer.Start();
        _topmostTimer.Start();
        RefreshReadout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _altTimer.Stop();
        _refreshTimer.Stop();
        _topmostTimer.Stop();
        _fps.Dispose();     // the ETW session must be stopped
        _system.Dispose();
        base.OnClosed(e);
    }

    // ---------------- Readout ----------------

    internal void RefreshReadout()
    {
        var s = _system.Current;
        _cpuRun.Text = Percent(s.Cpu);
        _gpuRun.Text = Percent(s.Gpu);
        _ramRun.Text = double.IsNaN(s.RamTotalGb)
            ? "  --/--"
            : string.Create(CultureInfo.InvariantCulture, $"{s.RamUsedGb,5:0.0}/{s.RamTotalGb:0}");

        // FPS is counted for the active window's process (the game)
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        if (pid != 0 && pid != _ownPid) _targetPid = (int)pid;

        int? fps = _fps.GetFps(_targetPid);
        _fpsRun.Text = fps is int f ? $"{f,3}" : " --";
        _fpsRun.Foreground = fps is int v && _settings.ColorFps
            ? (v >= 60 ? GoodBrush : v >= 30 ? WarnBrush : BadBrush)
            : ValueBrush;
    }

    private static string Percent(double v) => double.IsNaN(v) ? " --" : $"{Math.Round(v),3:0}";

    private static Run Label(string text) => new(text) { Foreground = LabelBrush };

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------------- Always on top ----------------

    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || _dragging) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // ---------------- Alt + drag ----------------

    private void PollAlt()
    {
        if (_hwnd == IntPtr.Zero || _dragging) return;
        bool alt = IsKeyDown(VK_MENU);
        if (alt != _interactive) SetInteractive(alt);
    }

    /// <summary>Turns the window's "clickability" on or off (clears/sets WS_EX_TRANSPARENT).</summary>
    private void SetInteractive(bool on)
    {
        _interactive = on;
        long ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = on ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)ex);

        Frame.BorderBrush = on ? AccentBrush : Brushes.Transparent;
        Cursor = on ? Cursors.SizeAll : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_interactive) return;

        _dragging = true;
        try
        {
            // System "drag by caption"; returns when the button is released.
            ReleaseCapture();
            SendMessage(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        finally
        {
            _dragging = false;
        }

        SnapToNearestCorner();
        PollAlt();
    }

    // ---------------- Corners and monitors ----------------

    private void SnapToNearestCorner()
    {
        GetWindowRect(_hwnd, out var wr);
        var center = new POINT { X = (wr.Left + wr.Right) / 2, Y = (wr.Top + wr.Bottom) / 2 };
        if (!TryGetMonitorRect(center, MONITOR_DEFAULTTONEAREST, out var mr)) return;

        int mx = (mr.Left + mr.Right) / 2;
        int my = (mr.Top + mr.Bottom) / 2;
        bool left = center.X < mx;
        bool top = center.Y < my;

        _settings.Corner = (top, left) switch
        {
            (true, true) => Corner.TopLeft,
            (true, false) => Corner.TopRight,
            (false, true) => Corner.BottomLeft,
            _ => Corner.BottomRight,
        };
        _settings.MonitorX = mx;
        _settings.MonitorY = my;
        _settings.Save();

        ApplyCorner();
    }

    internal void ResetPosition()
    {
        _settings.Corner = Corner.TopLeft;
        _settings.MonitorX = 0;
        _settings.MonitorY = 0;
        _settings.Save();
        ApplyCorner();
    }

    /// <summary>Places the window in the saved corner of the saved monitor (all in physical pixels).</summary>
    private void ApplyCorner()
    {
        if (_hwnd == IntPtr.Zero) return;

        var anchor = new POINT { X = _settings.MonitorX, Y = _settings.MonitorY };
        if (!TryGetMonitorRect(anchor, MONITOR_DEFAULTTOPRIMARY, out var mr)) return;

        GetWindowRect(_hwnd, out var wr);
        int w = wr.Right - wr.Left;
        int h = wr.Bottom - wr.Top;
        int m = (int)Math.Round(EdgeMarginDip * VisualTreeHelper.GetDpi(this).DpiScaleX);

        bool left = _settings.Corner is Corner.TopLeft or Corner.BottomLeft;
        bool top = _settings.Corner is Corner.TopLeft or Corner.TopRight;
        int x = left ? mr.Left + m : mr.Right - w - m;
        int y = top ? mr.Top + m : mr.Bottom - h - m;

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private static bool TryGetMonitorRect(POINT pt, uint flags, out RECT rect)
    {
        rect = default;
        IntPtr mon = MonitorFromPoint(pt, flags);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        rect = mi.rcMonitor; // the whole screen, not the work area: games have no taskbar
        return true;
    }
}
