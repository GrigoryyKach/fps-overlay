using System.Threading;
using System.Windows;

namespace FpsOverlay;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private OverlayWindow? _overlay;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Только один экземпляр
        _mutex = new Mutex(true, @"Global\FpsOverlay.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        var settings = Settings.Load();
        _overlay = new OverlayWindow(settings);
        _overlay.Show();

        _tray = new TrayIcon(_overlay, settings, () => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _overlay?.Close();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
