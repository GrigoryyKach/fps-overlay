using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsOverlay;

internal enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Settings in %AppData%\FpsOverlay\settings.json.</summary>
internal sealed class Settings
{
    public Corner Corner { get; set; } = Corner.TopLeft;

    /// <summary>A point inside the chosen monitor (its centre), in physical pixels. 0,0 is the primary monitor.</summary>
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }

    /// <summary>Colour FPS: green from 60, yellow from 30, red below.</summary>
    public bool ColorFps { get; set; } = true;

    /// <summary>Check for updates on startup (NIMVORO).</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>UI language code: en, ru or uk. Null until first resolved (see Strings.Resolve).</summary>
    public string? Language { get; set; }

    /// <summary>Tray menu theme: System (follows Windows), Light or Dark.</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FpsOverlay");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { /* corrupt file: start with defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* not critical */ }
    }
}
