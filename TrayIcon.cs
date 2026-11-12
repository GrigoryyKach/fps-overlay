using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FpsOverlay;

/// <summary>Tray icon: the only way to control the overlay (it has no window or taskbar button).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Settings _settings;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _checkNowItem;
    private readonly ToolStripMenuItem _colorItem, _autostartItem, _resetItem, _exitItem;
    private readonly ToolStripMenuItem _updatesMenu, _autoUpdateItem, _languageMenu, _themeMenu;
    private readonly ToolStripMenuItem _fpsErrorItem;
    private readonly Dictionary<Lang, ToolStripMenuItem> _langItems = new();
    private readonly Dictionary<ThemePreference, ToolStripMenuItem> _themeItems = new();
    private readonly string? _fpsError;
    private readonly Action _exit;
    private Updater.Release? _release;

    public TrayIcon(OverlayWindow overlay, Settings settings, Action exit)
    {
        // The exe icon comes from assets/icon.ico.
        _icon = (Environment.ProcessPath is string exePath ? Icon.ExtractAssociatedIcon(exePath) : null)
                ?? (Icon)SystemIcons.Application.Clone();
        _exit = exit;
        _settings = settings;
        _fpsError = overlay.Fps.Error;

        _colorItem = new ToolStripMenuItem { Checked = settings.ColorFps, CheckOnClick = true };
        _colorItem.CheckedChanged += (_, _) =>
        {
            settings.ColorFps = _colorItem.Checked;
            settings.Save();
            overlay.RefreshReadout();
        };

        _autostartItem = new ToolStripMenuItem { Checked = Autostart.IsEnabled() };
        _autostartItem.Click += (_, _) =>
        {
            bool want = !_autostartItem.Checked;
            if (Autostart.Set(want, out string? error))
                _autostartItem.Checked = want;
            else
                MessageBox.Show(Strings.F("autostart.fail", error ?? ""), "FPS Overlay",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        _resetItem = new ToolStripMenuItem(null, null, (_, _) => overlay.ResetPosition());
        _exitItem = new ToolStripMenuItem(null, null, (_, _) => exit());

        // Language submenu
        _languageMenu = new ToolStripMenuItem();
        foreach (var lang in new[] { Lang.En, Lang.Ru, Lang.Uk })
        {
            var item = new ToolStripMenuItem(Strings.NativeName(lang));
            item.Click += (_, _) => SetLanguage(lang);
            _langItems[lang] = item;
            _languageMenu.DropDownItems.Add(item);
        }

        // Theme submenu
        _themeMenu = new ToolStripMenuItem();
        foreach (var pref in new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark })
        {
            var item = new ToolStripMenuItem();
            item.Click += (_, _) => SetTheme(pref);
            _themeItems[pref] = item;
            _themeMenu.DropDownItems.Add(item);
        }

        // Updates (NIMVORO)
        _updateItem = new ToolStripMenuItem { Visible = false, Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _updateItem.Click += (_, _) => { if (Updater.Apply(_release)) _exit(); };
        _autoUpdateItem = new ToolStripMenuItem { Checked = settings.CheckUpdates, CheckOnClick = true };
        _autoUpdateItem.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = _autoUpdateItem.Checked;
            settings.Save();
        };
        _checkNowItem = new ToolStripMenuItem(null, null, async (_, _) => await CheckUpdates(true));
        _updatesMenu = new ToolStripMenuItem();
        _updatesMenu.DropDownItems.AddRange(new ToolStripItem[] { _autoUpdateItem, _checkNowItem });

        _menu = new ContextMenuStrip();
        _fpsErrorItem = new ToolStripMenuItem { Enabled = false };
        if (_fpsError != null)
        {
            _menu.Items.Add(_fpsErrorItem);
            _menu.Items.Add(new ToolStripSeparator());
        }
        _menu.Items.Add(_updateItem);
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _colorItem, _autostartItem, _resetItem, _languageMenu, _themeMenu, _updatesMenu,
            new ToolStripSeparator(), _exitItem,
        });

        _notifyIcon = new NotifyIcon { Icon = _icon, ContextMenuStrip = _menu, Visible = true };
        _notifyIcon.BalloonTipClicked += (_, _) => { if (_release != null && Updater.Apply(_release)) _exit(); };

        ApplyTexts();
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        if (settings.CheckUpdates)
            _ = CheckUpdates(false);
    }

    private void SetLanguage(Lang lang)
    {
        Strings.Set(lang);
        _settings.Language = Strings.Code(lang);
        _settings.Save();
        ApplyTexts();
    }

    private void SetTheme(ThemePreference pref)
    {
        _settings.Theme = pref;
        _settings.Save();
        ApplyTheme();
        ApplyTexts();
    }

    /// <summary>Sets every menu text for the current language and ticks the selected language/theme.</summary>
    private void ApplyTexts()
    {
        _colorItem.Text = Strings.T("menu.color");
        _autostartItem.Text = Strings.T("menu.autostart");
        _resetItem.Text = Strings.T("menu.reset");
        _exitItem.Text = Strings.T("menu.exit");
        _languageMenu.Text = Strings.T("menu.language");
        _themeMenu.Text = Strings.T("menu.theme");
        _updatesMenu.Text = Strings.T("menu.updates");
        _autoUpdateItem.Text = Strings.T("menu.autoUpdate");
        _checkNowItem.Text = Strings.F("menu.checkNow", Updater.CurrentText);
        if (_release != null) _updateItem.Text = Strings.F("menu.updateTo", _release.Version);
        if (_fpsError != null)
            _fpsErrorItem.Text = Strings.F("menu.fpsUnavailable",
                _fpsError == Metrics.FpsMonitor.AdminRequired ? Strings.T("err.admin") : _fpsError);
        _notifyIcon.Text = Strings.T("tray.tip");

        foreach (var (lang, item) in _langItems) item.Checked = lang == Strings.Current;
        foreach (var (pref, item) in _themeItems)
        {
            item.Text = Strings.T("theme." + pref.ToString().ToLowerInvariant());
            item.Checked = pref == _settings.Theme;
        }
    }

    private void ApplyTheme() => Theme.Apply(_menu, Theme.Resolve(_settings.Theme));

    /// <summary>System theme follows Windows live.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _settings.Theme != ThemePreference.System) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    /// <summary>Checks for updates. A manual check also reports that there is nothing new.</summary>
    private async System.Threading.Tasks.Task CheckUpdates(bool manual)
    {
        _checkNowItem.Enabled = false;
        var (ok, newer) = await Updater.CheckAsync();
        _checkNowItem.Enabled = true;
        _release = newer;
        if (newer != null)
        {
            _updateItem.Text = Strings.F("menu.updateTo", newer.Version);
            _updateItem.Visible = true;
            _notifyIcon.ShowBalloonTip(5000, "FPS Overlay", Strings.F("update.available", newer.Version), ToolTipIcon.Info);
        }
        else if (manual)
        {
            _notifyIcon.ShowBalloonTip(3000, "FPS Overlay",
                ok ? Strings.F("update.latest", Updater.CurrentText) : Strings.T("update.failed"), ToolTipIcon.Info);
        }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
