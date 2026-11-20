using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FpsOverlay;

/// <summary>UI languages: English (default), Russian, Ukrainian.</summary>
internal enum Lang { En, Ru, Uk }

/// <summary>
/// String table and language resolution.
/// Starting language (NIMVORO contract): settings.json "Language" -> "language" in
/// "&lt;exe dir&gt;\.nimvoro.json" -> English.
/// </summary>
internal static class Strings
{
    public static Lang Current { get; private set; } = Lang.En;

    public static string Code(Lang l) => l switch { Lang.Ru => "ru", Lang.Uk => "uk", _ => "en" };

    public static string NativeName(Lang l) => l switch { Lang.Ru => "Русский", Lang.Uk => "Українська", _ => "English" };

    public static Lang? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "en" => Lang.En,
        "ru" => Lang.Ru,
        "uk" => Lang.Uk,
        _ => null,
    };

    public static void Set(Lang l) => Current = l;

    /// <summary>Resolves the starting language and stores it in the settings.</summary>
    public static Lang Resolve(Settings settings, string? exeDir = null)
    {
        var lang = Parse(settings.Language) ?? ReadMarker(exeDir) ?? Lang.En;
        if (Parse(settings.Language) is null)
        {
            settings.Language = Code(lang);
            settings.Save();
        }
        Current = lang;
        return lang;
    }

    /// <summary>"language" from the .nimvoro.json install marker next to the exe.</summary>
    public static Lang? ReadMarker(string? exeDir = null)
    {
        try
        {
            exeDir ??= Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir is null) return null;
            var file = Path.Combine(exeDir, ".nimvoro.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("language", out var v) ? Parse(v.GetString()) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Looks a key up in the table for the current language (falls back to English, then to the key).</summary>
    public static string T(string key) => T(key, Current);

    public static string T(string key, Lang lang)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        var s = row[(int)lang];
        return string.IsNullOrEmpty(s) ? row[0] : s;
    }

    public static string F(string key, params object[] args) => string.Format(T(key), args);

    // Order of values: en, ru, uk.
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["menu.color"] = new[] { "Color FPS by thresholds", "Цвет FPS по порогам", "Колір FPS за порогами" },
        ["menu.autostart"] = new[] { "Start with Windows", "Автозапуск с Windows", "Автозапуск із Windows" },
        ["menu.reset"] = new[] { "Reset position", "Сбросить позицию", "Скинути позицію" },
        ["menu.support"] = new[] { "Support the author ♥", "Поддержать автора ♥", "Підтримати автора ♥" },
        ["menu.exit"] = new[] { "Exit", "Выход", "Вихід" },
        ["menu.language"] = new[] { "Language", "Язык", "Мова" },
        ["menu.theme"] = new[] { "Theme", "Тема", "Тема" },
        ["theme.system"] = new[] { "System", "Системная", "Системна" },
        ["theme.light"] = new[] { "Light", "Светлая", "Світла" },
        ["theme.dark"] = new[] { "Dark", "Тёмная", "Темна" },
        ["menu.updates"] = new[] { "Updates", "Обновления", "Оновлення" },
        ["menu.autoUpdate"] = new[] { "Check for updates on startup", "Проверять обновления при запуске", "Перевіряти оновлення під час запуску" },
        ["menu.checkNow"] = new[] { "Check for updates (version {0})", "Проверить обновления (версия {0})", "Перевірити оновлення (версія {0})" },
        ["menu.updateTo"] = new[] { "Update to version {0}", "Обновить до версии {0}", "Оновити до версії {0}" },
        ["menu.fpsUnavailable"] = new[] { "FPS unavailable: {0}", "FPS недоступен: {0}", "FPS недоступний: {0}" },
        ["err.admin"] = new[] { "administrator rights required", "нужны права администратора", "потрібні права адміністратора" },
        ["tray.tip"] = new[] { "FPS Overlay — Alt + mouse: drag", "FPS Overlay — Alt + мышь: перетащить", "FPS Overlay — Alt + миша: перетягнути" },
        ["autostart.fail"] = new[] { "Could not change autostart:\n{0}", "Не удалось изменить автозапуск:\n{0}", "Не вдалося змінити автозапуск:\n{0}" },
        ["update.available"] = new[] { "Version {0} is available. Click to update.", "Доступна версия {0}. Щёлкните, чтобы обновить.", "Доступна версія {0}. Клацніть, щоб оновити." },
        ["update.latest"] = new[] { "You have the latest version ({0})", "Установлена последняя версия ({0})", "Встановлено останню версію ({0})" },
        ["update.failed"] = new[] { "Could not check for updates", "Не удалось проверить обновления", "Не вдалося перевірити оновлення" },
        ["update.heading"] = new[] { "Updating to version {0}", "Обновление до версии {0}", "Оновлення до версії {0}" },
        ["update.downloading"] = new[] { "Downloading update…", "Загружаем обновление…", "Завантажуємо оновлення…" },
        ["update.verifying"] = new[] { "Verifying…", "Проверяем…", "Перевіряємо…" },
        ["update.installing"] = new[] { "Installing…", "Устанавливаем…", "Встановлюємо…" },
        ["update.restarting"] = new[] { "Restarting…", "Перезапускаем…", "Перезапускаємо…" },
        ["update.tryAgain"] = new[] { "Try again", "Повторить", "Повторити" },
        ["update.manual"] = new[] { "Download manually", "Скачать вручную", "Завантажити вручну" },
        ["update.errDownload"] = new[] { "Download failed. Check your connection.", "Не удалось загрузить. Проверьте соединение.", "Не вдалося завантажити. Перевірте з'єднання." },
        ["update.errDigest"] = new[] { "The file did not match its checksum and was not installed.", "Файл не совпал с контрольной суммой и не установлен.", "Файл не збігся з контрольною сумою й не встановлений." },
        ["update.errNoDigest"] = new[] { "The release has no checksum, so it was not installed.", "У релиза нет контрольной суммы, поэтому он не установлен.", "У релізу немає контрольної суми, тому його не встановлено." },
        ["update.errPackage"] = new[] { "The update package is not valid for this app.", "Пакет обновления не подходит этому приложению.", "Пакет оновлення не підходить цьому застосунку." },
        ["update.errInstall"] = new[] { "Could not replace the files. Nothing was changed.", "Не удалось заменить файлы. Ничего не изменено.", "Не вдалося замінити файли. Нічого не змінено." },
        ["update.errRestart"] = new[] { "Updated, but the app could not restart. Start it again by hand.", "Обновлено, но приложение не перезапустилось. Запустите его вручную.", "Оновлено, але застосунок не перезапустився. Запустіть його вручну." },
    };
}
