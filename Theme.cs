using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FpsOverlay;

/// <summary>Theme preference stored in settings.json.</summary>
internal enum ThemePreference { System, Light, Dark }

/// <summary>Colours of the tray menu: the NIMVORO suite graphite ramp with the sky accent of this app.</summary>
internal sealed record Palette(
    Color Background, Color Text, Color TextDisabled, Color Border, Color Separator,
    Color Hover, Color Accent, Color AccentInk, Color OnAccent)
{
    // Dark: card step #19191b, text #ededed, muted #85858b, hairline = #ededed at 8% on the card.
    public static readonly Palette Dark = new(
        Color.FromArgb(0x19, 0x19, 0x1b), Color.FromArgb(0xed, 0xed, 0xed), Color.FromArgb(0x85, 0x85, 0x8b),
        Color.FromArgb(0x2c, 0x2c, 0x2e), Color.FromArgb(0x2c, 0x2c, 0x2e), Color.FromArgb(0x23, 0x23, 0x25),
        Color.FromArgb(0x4f, 0xc3, 0xf7), Color.FromArgb(0x4f, 0xc3, 0xf7), Color.FromArgb(0x14, 0x14, 0x15));

    // Light: white card, text #171717, muted #737373, hairline = #171717 at 8% on white.
    // AccentInk is the darker sky used for ticks on the light surface (5.6:1 on white).
    public static readonly Palette Light = new(
        Color.FromArgb(0xff, 0xff, 0xff), Color.FromArgb(0x17, 0x17, 0x17), Color.FromArgb(0x73, 0x73, 0x73),
        Color.FromArgb(0xec, 0xec, 0xec), Color.FromArgb(0xec, 0xec, 0xec), Color.FromArgb(0xf4, 0xf4, 0xf5),
        Color.FromArgb(0x4f, 0xc3, 0xf7), Color.FromArgb(0x0b, 0x6e, 0x9f), Color.FromArgb(0x17, 0x17, 0x17));
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

    private static PrivateFontCollection? _fonts;

    /// <summary>IBM Plex Sans (embedded, OFL) as the menu font; null falls back to the system font.</summary>
    internal static FontFamily? PlexSans()
    {
        if (_fonts == null)
        {
            _fonts = new PrivateFontCollection();
            try
            {
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PlexSans-Regular.ttf");
                if (s != null)
                {
                    var data = new byte[s.Length];
                    s.ReadExactly(data);
                    // The memory must stay alive for the life of the collection, so it is never freed.
                    IntPtr ptr = Marshal.AllocHGlobal(data.Length);
                    Marshal.Copy(data, 0, ptr, data.Length);
                    _fonts.AddMemoryFont(ptr, data.Length);
                }
            }
            catch { }
        }
        return _fonts.Families.Length > 0 ? _fonts.Families[0] : null;
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
        var fam = PlexSans();
        if (fam != null && fam.IsStyleAvailable(FontStyle.Regular))
            item.Font = new Font(fam, item.Font.Size, item.Font.Style, GraphicsUnit.Point);
        item.ForeColor = item.Enabled ? p.Text : p.TextDisabled;
        if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
        {
            dd.DropDown.BackColor = p.Background;
            dd.DropDown.ForeColor = p.Text;
            foreach (ToolStripItem sub in dd.DropDownItems) ApplyItem(sub, p);
        }
    }
}

/// <summary>Flat menu renderer: palette background, solid accent hover, thin border.</summary>
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
            using var br = new SolidBrush(_p.Accent);
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
        var color = e.Item.Selected && e.Item.Enabled ? _p.OnAccent : _p.AccentInk;
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
    public override Color MenuItemBorder => _p.Accent;
    public override Color MenuItemSelected => _p.Accent;
    public override Color MenuItemSelectedGradientBegin => _p.Accent;
    public override Color MenuItemSelectedGradientEnd => _p.Accent;
    public override Color ImageMarginGradientBegin => _p.Background;
    public override Color ImageMarginGradientMiddle => _p.Background;
    public override Color ImageMarginGradientEnd => _p.Background;
    public override Color SeparatorDark => _p.Separator;
    public override Color SeparatorLight => _p.Separator;
}
