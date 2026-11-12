using System;
using System.Diagnostics;
using System.IO;
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

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    public sealed record Release(string Version, string Url);

    /// <summary>The latest release if it is newer than the installed one; null means no update or the check failed.</summary>
    public static async Task<(bool ok, Release? newer)> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NIMVORO-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var ver = Parse(tag);
            return (true, ver > Current ? new Release(tag.TrimStart('v', 'V'), url) : null);
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
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NIMVORO", "launcher.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var p = doc.RootElement.GetProperty("path").GetString();
            return p != null && File.Exists(p) ? p : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>true means the update was handed to the launcher and the app should close.</summary>
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
        var url = release?.Url is { Length: > 0 } u ? u : $"https://github.com/{Repo}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        return false;
    }
}
