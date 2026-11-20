using System;
using System.Drawing;
using System.Windows.Forms;

namespace FpsOverlay;

/// <summary>
/// Small borderless progress window for the self-update, in the tray menu's style (palette, Plex Sans, accent bar).
/// Shows the phase, a thin progress bar with MB, and on failure a message with "Try again" and "Download manually".
/// </summary>
internal sealed class UpdateWindow : Form
{
    private readonly Palette _p;
    private readonly Label _title, _phase, _size;
    private readonly Panel _bar;
    private readonly Button _retry, _manual;
    private double _fraction;

    public event Action? RetryClicked;
    public event Action? ManualClicked;

    public UpdateWindow(Palette palette, string version)
    {
        _p = palette;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(380, 150);
        BackColor = _p.Background;
        ForeColor = _p.Text;
        DoubleBuffered = true;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

        var family = Theme.PlexSans();
        Font Make(float size) =>
            family != null && family.IsStyleAvailable(FontStyle.Regular)
                ? new Font(family, size, FontStyle.Regular, GraphicsUnit.Point)
                : new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Regular);

        _title = new Label { Left = 20, Top = 16, Width = 340, Height = 22, Font = Make(11f), Text = Strings.F("update.heading", version), ForeColor = _p.Text, AutoEllipsis = true };
        _phase = new Label { Left = 20, Top = 46, Width = 340, Height = 44, Font = Make(9.5f), ForeColor = _p.TextDisabled };
        _bar = new Panel { Left = 20, Top = 94, Width = 340, Height = 4, BackColor = _p.Border };
        _bar.Paint += (_, e) =>
        {
            using var b = new SolidBrush(_p.Accent);
            e.Graphics.FillRectangle(b, 0, 0, (int)(_bar.Width * _fraction), _bar.Height);
        };
        _size = new Label { Left = 20, Top = 106, Width = 340, Height = 18, Font = Make(8.5f), ForeColor = _p.TextDisabled, TextAlign = ContentAlignment.MiddleRight };
        _retry = FlatButton(Strings.T("update.tryAgain"), true, Make(9f));
        _manual = FlatButton(Strings.T("update.manual"), false, Make(9f));
        _retry.Click += (_, _) => RetryClicked?.Invoke();
        _manual.Click += (_, _) => ManualClicked?.Invoke();
        _retry.Visible = _manual.Visible = false;
        Controls.AddRange(new Control[] { _title, _phase, _bar, _size, _retry, _manual });
    }

    private Button FlatButton(string text, bool primary, Font font)
    {
        var b = new Button
        {
            Text = text, Height = 28, Top = 108, AutoSize = false, Font = font, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? _p.Accent : _p.Hover, ForeColor = primary ? _p.OnAccent : _p.Text,
        };
        b.FlatAppearance.BorderColor = primary ? _p.Accent : _p.Border;
        b.Width = TextRenderer.MeasureText(text, font).Width + 28;
        return b;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_p.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Updates the window for a progress report. Call on the UI thread.</summary>
    public void Report(UpdateProgress p)
    {
        _retry.Visible = _manual.Visible = p.Phase == UpdatePhase.Failed;
        _bar.Visible = _size.Visible = p.Phase != UpdatePhase.Failed;
        if (p.Phase == UpdatePhase.Failed)
        {
            _phase.Text = Strings.T(ErrorKey(p.Error));
            _phase.ForeColor = _p.Text;
            _manual.Left = 360 - _manual.Width;
            _retry.Left = _manual.Left - 8 - _retry.Width;
            return;
        }
        _phase.ForeColor = _p.TextDisabled;
        _phase.Text = Strings.T(p.Phase switch
        {
            UpdatePhase.Verifying => "update.verifying",
            UpdatePhase.Installing => "update.installing",
            UpdatePhase.Restarting => "update.restarting",
            _ => "update.downloading",
        });
        _fraction = p.Phase == UpdatePhase.Downloading ? (p.Total > 0 ? Math.Min(1.0, (double)p.Done / p.Total) : 0) : 1.0;
        _size.Text = p.Phase == UpdatePhase.Downloading && p.Total > 0 ? $"{p.Done / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB" : "";
        _bar.Invalidate();
    }

    public static string ErrorKey(UpdateErrorCode? e) => e switch
    {
        UpdateErrorCode.BadDigest => "update.errDigest",
        UpdateErrorCode.NoDigest => "update.errNoDigest",
        UpdateErrorCode.NoAsset or UpdateErrorCode.BadPackage or UpdateErrorCode.Extract => "update.errPackage",
        UpdateErrorCode.SwapFailed => "update.errInstall",
        UpdateErrorCode.Restart => "update.errRestart",
        _ => "update.errDownload",
    };

    protected override bool ShowWithoutActivation => true;
}
