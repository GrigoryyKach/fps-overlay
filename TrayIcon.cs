using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using FpsOverlay.Native;

namespace FpsOverlay;

/// <summary>Иконка в трее — единственный способ управлять оверлеем (у него нет окна и кнопки на панели задач).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _checkNowItem;
    private readonly Action _exit;
    private Updater.Release? _release;

    public TrayIcon(OverlayWindow overlay, Settings settings, Action exit)
    {
        // Значок exe (assets/icon.ico); если не удалось — нарисованный кодом.
        _icon = (Environment.ProcessPath is string exePath ? Icon.ExtractAssociatedIcon(exePath) : null) ?? CreateIcon();
        _exit = exit;

        var colorItem = new ToolStripMenuItem("Цвет FPS по порогам") { Checked = settings.ColorFps, CheckOnClick = true };
        colorItem.CheckedChanged += (_, _) =>
        {
            settings.ColorFps = colorItem.Checked;
            settings.Save();
            overlay.RefreshReadout();
        };

        var autostartItem = new ToolStripMenuItem("Автозапуск с Windows") { Checked = Autostart.IsEnabled() };
        autostartItem.Click += (_, _) =>
        {
            bool want = !autostartItem.Checked;
            if (Autostart.Set(want, out string? error))
                autostartItem.Checked = want;
            else
                MessageBox.Show("Не удалось изменить автозапуск:\n" + error, "FPS Overlay",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        var resetItem = new ToolStripMenuItem("Сбросить позицию", null, (_, _) => overlay.ResetPosition());
        var exitItem = new ToolStripMenuItem("Выход", null, (_, _) => exit());

        // Обновления (NIMVORO)
        _updateItem = new ToolStripMenuItem("Обновить") { Visible = false, Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) };
        _updateItem.Click += (_, _) => { if (Updater.Apply(_release)) _exit(); };
        var autoUpdateItem = new ToolStripMenuItem("Проверять обновления при запуске") { Checked = settings.CheckUpdates, CheckOnClick = true };
        autoUpdateItem.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = autoUpdateItem.Checked;
            settings.Save();
        };
        _checkNowItem = new ToolStripMenuItem($"Проверить обновления (версия {Updater.CurrentText})", null, async (_, _) => await CheckUpdates(true));
        var updatesMenu = new ToolStripMenuItem("Обновления");
        updatesMenu.DropDownItems.AddRange(new ToolStripItem[] { autoUpdateItem, _checkNowItem });

        var menu = new ContextMenuStrip();
        if (overlay.Fps.Error is string fpsError)
        {
            menu.Items.Add(new ToolStripMenuItem("FPS недоступен: " + fpsError) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(_updateItem);
        menu.Items.AddRange(new ToolStripItem[] { colorItem, autostartItem, resetItem, updatesMenu, new ToolStripSeparator(), exitItem });

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "FPS Overlay — Alt + мышь: перетащить",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += (_, _) => { if (_release != null && Updater.Apply(_release)) _exit(); };

        if (settings.CheckUpdates)
            _ = CheckUpdates(false);
    }

    /// <summary>Проверка обновлений. При ручной проверке сообщаем и об отсутствии обновлений.</summary>
    private async System.Threading.Tasks.Task CheckUpdates(bool manual)
    {
        _checkNowItem.Enabled = false;
        var (ok, newer) = await Updater.CheckAsync();
        _checkNowItem.Enabled = true;
        _release = newer;
        if (newer != null)
        {
            _updateItem.Text = $"Обновить до версии {newer.Version}";
            _updateItem.Visible = true;
            _notifyIcon.ShowBalloonTip(5000, "FPS Overlay", $"Доступна версия {newer.Version}. Щёлкните, чтобы обновить.", ToolTipIcon.Info);
        }
        else if (manual)
        {
            _notifyIcon.ShowBalloonTip(3000, "FPS Overlay",
                ok ? $"Установлена последняя версия ({Updater.CurrentText})" : "Не удалось проверить обновления", ToolTipIcon.Info);
        }
    }

    /// <summary>Рисуем иконку кодом, чтобы не таскать .ico-файл.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(255, 24, 24, 24));
            using var path = new GraphicsPath();
            const int r = 8;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(31 - r, 0, r, r, 270, 90);
            path.AddArc(31 - r, 31 - r, r, r, 0, 90);
            path.AddArc(0, 31 - r, r, r, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);

            using var font = new Font("Consolas", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(255, 0x6E, 0xE7, 0x7A));
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("F", font, fg, new RectangleF(0, 1, 32, 32), fmt);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            Win32.DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
