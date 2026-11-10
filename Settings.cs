using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FpsOverlay;

internal enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Настройки в %AppData%\FpsOverlay\settings.json.</summary>
internal sealed class Settings
{
    public Corner Corner { get; set; } = Corner.TopLeft;

    /// <summary>Точка внутри нужного монитора (центр), в физических пикселях. 0,0 — основной монитор.</summary>
    public int MonitorX { get; set; }
    public int MonitorY { get; set; }

    /// <summary>Красить FPS: зелёный ≥ 60, жёлтый ≥ 30, красный ниже.</summary>
    public bool ColorFps { get; set; } = true;

    /// <summary>Проверять обновления при запуске (NIMVORO).</summary>
    public bool CheckUpdates { get; set; } = true;

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
        catch { /* битый файл — начинаем с настроек по умолчанию */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* не критично */ }
    }
}
