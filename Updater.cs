using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace FpsOverlay;

/// <summary>
/// NIMVORO: update check via GitHub Releases and hand-off of the update to the launcher.
/// The contract is described in nimvoro/docs/ADDING_A_TOOL.md.
/// </summary>
internal static class Updater
{
    public const string Id = "fps-overlay";
    public const string Repo = "GrigoryyKach/fps-overlay";

    /// <summary>Test override: FPS_OVERLAY_UPDATE_API replaces the whole release URL (and allows http://127.0.0.1 downloads).</summary>
    public static string? ApiOverride => Environment.GetEnvironmentVariable(SelfUpdate.EnvName(Id)) is { Length: > 0 } v ? v : null;

    private static string ApiUrl() => ApiOverride ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public sealed record Release(string Version, string Url, JsonElement Json);

    /// <summary>The latest release if it is newer than the installed one; null means no update or the check failed.</summary>
    public static async Task<(bool ok, Release? newer)> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NIMVORO-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(ApiUrl());
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var ver = Parse(tag);
            return (true, ver > Current ? new Release(tag.TrimStart('v', 'V'), url, doc.RootElement.Clone()) : null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static Version Parse(string tag)
    {
        var core = tag.TrimStart('v', 'V').Split('-', '+')[0];
        var parts = core.Split('.');
        int At(int i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : 0;
        return new Version(At(0), At(1), At(2), 0);
    }

    /// <summary>Path to NIMVORO.exe from %APPDATA%\NIMVORO\launcher.json.</summary>
    public static string? LauncherPath()
    {
        try
        {
            // NIMVORO_LAUNCHER_JSON: test-only override of the launcher pointer
            var file = Environment.GetEnvironmentVariable("NIMVORO_LAUNCHER_JSON") is { Length: > 0 } o
                ? o
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NIMVORO", "launcher.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var p = doc.RootElement.GetProperty("path").GetString();
            return p != null && File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    public static string AppDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static UpdateMode ChooseMode() => SelfUpdate.ChooseMode(LauncherPath(), AppDir);

    /// <summary>Downloads, verifies and installs the release here, then restarts. Progress goes to onPhase; restart() quits the app.</summary>
    public static Task SelfUpdateAsync(Release release, Action<UpdateProgress> onPhase, Action restart, Action? beforeSwap = null) =>
        SelfUpdate.RunAsync(new SelfUpdateOptions
        {
            Release = release.Json,
            Id = Id,
            AppDir = AppDir,
            ExePath = Environment.ProcessPath ?? Path.Combine(AppDir, "FpsOverlay.exe"),
            Args = Environment.GetCommandLineArgs().Skip(1).ToArray(),
            Overridden = ApiOverride != null,
            OnPhase = onPhase,
            BeforeSwap = beforeSwap,
            Restart = restart,
        });

    public static void OpenReleasePage(Release? release)
    {
        var url = release?.Url is { Length: > 0 } u ? u : $"https://github.com/{Repo}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Launcher or release page (no self-update): true means the update was handed to the launcher and the app should close.</summary>
    public static bool Apply(Release? release)
    {
        if (LauncherPath() is string exe)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--update", Id }, UseShellExecute = false });
                return true;
            }
            catch { /* fall back to the release page */ }
        }
        OpenReleasePage(release);
        return false;
    }
}
