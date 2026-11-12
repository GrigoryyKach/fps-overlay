using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FpsOverlay;

/// <summary>Theme preference stored in settings.json.</summary>
internal enum ThemePreference { System, Light, Dark }

/// <summary>Colours of the tray menu in the NIMVORO suite style (accent gradient #4f6bff to #8a5bff).</summary>
internal sealed record Palette(
    Color Background, Color Text, Color TextDisabled, Color Border, Color Separator,
    Color Hover, Color AccentFrom, Color AccentTo, Color OnAccent)
{
    public static readonly Palette Dark = new(
        Color.FromArgb(0x1b, 0x1d, 0x26), Color.FromArgb(0xe8, 0xea, 0xf2), Color.FromArgb(0x7b, 0x80, 0x93),
        Color.FromArgb(0x38, 0x3c, 0x4d), Color.FromArgb(0x33, 0x37, 0x47), Color.FromArgb(0x2a, 0x2e, 0x3d),
        Color.FromArgb(0x4f, 0x6b, 0xff), Color.FromArgb(0x8a, 0x5b, 0xff), Color.White);

    public static readonly Palette Light = new(
        Color.FromArgb(0xfb, 0xfb, 0xfe), Color.FromArgb(0x1d, 0x20, 0x2e), Color.FromArgb(0x9a, 0x9f, 0xb2),
        Color.FromArgb(0xd5, 0xd9, 0xe6), Color.FromArgb(0xe3, 0xe6, 0xf0), Color.FromArgb(0xec, 0xee, 0xfa),
        Color.FromArgb(0x4f, 0x6b, 0xff), Color.FromArgb(0x8a, 0x5b, 0xff), Color.White);
}

internal static class Theme
{
    /// <summary>true when Windows apps use the light theme.</summary>
    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>The palette for a preference; System follows Windows.</summary>
    public static Palette Resolve(ThemePreference pref) => pref switch
    {
        ThemePreference.Light => Palette.Light,
        ThemePreference.Dark => Palette.Dark,
        _ => SystemIsLight() ? Palette.Light : Palette.Dark,
    };

    /// <summary>Applies the palette to a context menu and all of its items (recursively).</summary>
    public static void Apply(ContextMenuStrip menu, Palette p)
    {
        menu.Renderer = new SuiteRenderer(p);
        menu.BackColor = p.Background;
        menu.ForeColor = p.Text;
        foreach (ToolStripItem item in menu.Items) ApplyItem(item, p);
    }

    private static void ApplyItem(ToolStripItem item, Palette p)
    {
        item.BackColor = p.Background;
        item.ForeColor = item.Enabled ? p.Text : p.TextDisabled;
        if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
        {
            dd.DropDown.BackColor = p.Background;
            dd.DropDown.ForeColor = p.Text;
            foreach (ToolStripItem sub in dd.DropDownItems) ApplyItem(sub, p);
        }
    }
}

/// <summary>Flat menu renderer: palette background, accent-gradient hover, thin border.</summary>
internal sealed class SuiteRenderer : ToolStripProfessionalRenderer
{
    private readonly Palette _p;

    public SuiteRenderer(Palette p) : base(new SuiteColorTable(p))
    {
        _p = p;
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(_p.Background);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(_p.Border);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No separate image margin: a flat background looks cleaner.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var r = new Rectangle(3, 1, item.Width - 6, item.Height - 2);
        if (item.Selected && item.Enabled)
        {
            using var br = new LinearGradientBrush(r, _p.AccentFrom, _p.AccentTo, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(br, r);
        }
        else if (item is ToolStripMenuItem { DropDown.Visible: true })
        {
            using var br = new SolidBrush(_p.Hover);
            e.Graphics.FillRectangle(br, r);
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(_p.Separator);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? _p.TextDisabled
            : e.Item.Selected ? _p.OnAccent
            : _p.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item is { Selected: true, Enabled: true } ? _p.OnAccent : _p.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Draw our own check mark (a tick) so it is visible on both palettes.
        var g = e.Graphics;
        var r = e.ImageRectangle;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = e.Item.Selected && e.Item.Enabled ? _p.OnAccent : _p.AccentFrom;
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        g.DrawLines(pen, new[]
        {
            new PointF(cx - 4, cy), new PointF(cx - 1, cy + 3), new PointF(cx + 4, cy - 3),
        });
    }
}

internal sealed class SuiteColorTable : ProfessionalColorTable
{
    private readonly Palette _p;

    public SuiteColorTable(Palette p)
    {
        _p = p;
        UseSystemColors = false;
    }

    public override Color ToolStripDropDownBackground => _p.Background;
    public override Color MenuBorder => _p.Border;
    public override Color MenuItemBorder => _p.AccentFrom;
    public override Color MenuItemSelected => _p.AccentFrom;
    public override Color MenuItemSelectedGradientBegin => _p.AccentFrom;
    public override Color MenuItemSelectedGradientEnd => _p.AccentTo;
    public override Color ImageMarginGradientBegin => _p.Background;
    public override Color ImageMarginGradientMiddle => _p.Background;
    public override Color ImageMarginGradientEnd => _p.Background;
    public override Color SeparatorDark => _p.Separator;
    public override Color SeparatorLight => _p.Separator;
}
