using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FpsOverlay;

/// <summary>Why a self-update failed (see tasks/selfupdate-design.md in the NIMVORO workspace).</summary>
internal enum UpdateErrorCode { NoAsset, NoDigest, Download, BadDigest, Extract, BadPackage, SwapFailed, Restart }

internal sealed class UpdateException : Exception
{
    public UpdateErrorCode Code { get; }
    public UpdateException(UpdateErrorCode code, string? detail = null) : base(detail is null ? code.ToString() : $"{code}: {detail}") => Code = code;
}

internal enum UpdatePhase { Downloading, Verifying, Installing, Restarting, Failed }

internal sealed record UpdateProgress(UpdatePhase Phase, long Done = 0, long Total = 0, UpdateErrorCode? Error = null);

internal enum UpdateMode { Launcher, Self, Page }

internal sealed record UpdateAsset(string Name, string Url, long Size, string Sha256);

internal sealed class SelfUpdateOptions
{
    public JsonElement Release { get; init; }
    public string Id { get; init; } = "";
    public string AppDir { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string[] Args { get; init; } = Array.Empty<string>();
    /// <summary>true when the test override of the API URL is set: plain http://127.0.0.1 downloads are allowed.</summary>
    public bool Overridden { get; init; }
    public string? TempRoot { get; init; }
    public Action<UpdateProgress>? OnPhase { get; init; }
    public Action? BeforeSwap { get; init; }
    /// <summary>Called after the new exe has been started; the host exits here.</summary>
    public Action? Restart { get; init; }
}

/// <summary>
/// NIMVORO self-update for a standalone install (no launcher). Same design as the Rust apps:
/// download (curl) -> verify SHA-256 -> extract (tar) to .update-staging -> journalled swap with *.old backups
/// and rollback -> relaunch with NIMVORO_WAIT_PID. Pure logic (no UI), covered by tests/SelfUpdateTests.
/// </summary>
internal static class SelfUpdate
{
    public const string Staging = ".update-staging";
    public const string WriteTest = ".nimvoro-write-test";
    public const string WaitPidEnv = "NIMVORO_WAIT_PID";
    public const string WaitPidArg = "--nimvoro-wait-pid";

    // Never replaced, even if the package contains them: the language marker and portable data.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { ".nimvoro.json", "library.json", "config.json", "settings.json" };

    public static string EnvName(string id) => id.ToUpperInvariant().Replace('-', '_') + "_UPDATE_API";

    /// <summary>The asset for Windows (name ends with -win-x64.zip). Throws NoAsset or NoDigest.</summary>
    public static UpdateAsset ChooseAsset(JsonElement release)
    {
        if (release.ValueKind == JsonValueKind.Object && release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(url)) throw new UpdateException(UpdateErrorCode.NoAsset);
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                if (!IsSha256Digest(digest, out var hex)) throw new UpdateException(UpdateErrorCode.NoDigest);
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                return new UpdateAsset(name, url!, size, hex);
            }
        }
        throw new UpdateException(UpdateErrorCode.NoAsset);
    }

    private static bool IsSha256Digest(string digest, out string hex)
    {
        hex = "";
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        var h = digest[7..];
        if (h.Length != 64 || !h.All(Uri.IsHexDigit)) return false;
        hex = h.ToLowerInvariant();
        return true;
    }

    public static bool DownloadUrlAllowed(string url, bool overridden) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || (overridden && url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && (url.Length == 16 || url[16] is ':' or '/'));

    /// <summary>Launcher installed: hand over. Else a writable app folder: update here. Else open the release page.</summary>
    public static UpdateMode ChooseMode(string? launcherExe, string appDir) =>
        launcherExe != null ? UpdateMode.Launcher : IsWritable(appDir) ? UpdateMode.Self : UpdateMode.Page;

    public static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, WriteTest);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static string Sha256File(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>Relative paths of all regular files. Throws BadPackage on links or reparse points.</summary>
    public static List<string> ListFiles(string root)
    {
        var result = new List<string>();
        void Walk(string rel)
        {
            var dir = Path.Combine(root, rel);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var r = rel.Length == 0 ? Path.GetFileName(path) : Path.Combine(rel, Path.GetFileName(path));
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) throw new UpdateException(UpdateErrorCode.BadPackage, "link " + r);
                if ((attrs & FileAttributes.Directory) != 0) Walk(r); else result.Add(r);
            }
        }
        Walk("");
        return result;
    }

    /// <summary>Journalled swap of every staged file into appDir. Rolls back and throws SwapFailed on any failure. Returns the file count.</summary>
    public static int Swap(string stagingDir, string appDir)
    {
        var files = ListFiles(stagingDir).Where(r => !Protected.Contains(Path.GetFileName(r))).ToList();
        var journal = new List<(string dest, bool backedUp)>();
        try
        {
            foreach (var rel in files)
            {
                var dest = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                bool backed = false;
                if (File.Exists(dest))
                {
                    if (File.Exists(dest + ".old")) File.Delete(dest + ".old"); // a running exe can be renamed but not deleted
                    File.Move(dest, dest + ".old");
                    backed = true;
                }
                journal.Add((dest, backed));
                File.Move(Path.Combine(stagingDir, rel), dest);
            }
        }
        catch (Exception e)
        {
            for (int i = journal.Count - 1; i >= 0; i--)
            {
                var (dest, backed) = journal[i];
                try { File.Delete(dest); } catch { /* best effort */ }
                if (backed) { try { File.Move(dest + ".old", dest); } catch { /* best effort */ } }
            }
            throw new UpdateException(UpdateErrorCode.SwapFailed, e.Message);
        }
        return journal.Count;
    }

    /// <summary>Deletes leftovers of the previous update: *.old (top level and one folder deep), staging, temp download.</summary>
    public static void Cleanup(string appDir, string id, string? tempRoot = null)
    {
        void RmOld(string dir, bool recurse)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.old"))
                    try { File.Delete(f); } catch { /* still running */ }
                if (recurse) foreach (var d in Directory.EnumerateDirectories(dir)) RmOld(d, false);
            }
            catch { /* ignore */ }
        }
        RmOld(appDir, true);
        try { Directory.Delete(Path.Combine(appDir, Staging), true); } catch { /* ignore */ }
        try { Directory.Delete(Path.Combine(tempRoot ?? Path.GetTempPath(), $"{id}-update"), true); } catch { /* ignore */ }
    }

    /// <summary>
    /// Call first thing in OnStartup, before the single-instance mutex: waits (max 15 s) for the process that started us
    /// (env NIMVORO_WAIT_PID, or the --nimvoro-wait-pid argument), then cleans up after the previous update.
    /// </summary>
    public static void Startup(string appDir, string id, string[]? args = null)
    {
        int pid = 0;
        int.TryParse(Environment.GetEnvironmentVariable(WaitPidEnv), out pid);
        Environment.SetEnvironmentVariable(WaitPidEnv, null);
        var argv = args ?? Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (int i = 0; i < argv.Length; i++)
            if (argv[i] == WaitPidArg && i + 1 < argv.Length && int.TryParse(argv[i + 1], out var p)) pid = p;
        if (pid > 0 && pid != Environment.ProcessId)
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); } catch { /* already gone */ }
        }
        Cleanup(appDir, id);
    }

    /// <summary>Command line without our own --nimvoro-wait-pid pair, for the relaunch.</summary>
    public static string[] CleanArgs(string[] args)
    {
        var list = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == WaitPidArg) { i++; continue; }
            list.Add(args[i]);
        }
        return list.ToArray();
    }

    private static async Task DownloadAsync(string url, string file, long size, Action<long, long> progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file)) File.Delete(file);
        var psi = new ProcessStartInfo("curl.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-fsSL", "--max-time", "900", "-A", "NIMVORO-updater", "-o", file, url }) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new UpdateException(UpdateErrorCode.Download, "curl did not start"); }
        catch (Win32Exception e) { throw new UpdateException(UpdateErrorCode.Download, e.Message); }
        using (p)
        {
            while (!p.HasExited)
            {
                try { progress(size > 0 ? Math.Min(new FileInfo(file).Length, size) : new FileInfo(file).Length, size); } catch { /* not created yet */ }
                await Task.Delay(100);
            }
            if (p.ExitCode != 0) throw new UpdateException(UpdateErrorCode.Download, "curl exit " + p.ExitCode);
        }
        progress(size, size);
    }

    private static void Extract(string archive, string staging)
    {
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var tar = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "tar.exe");
        var psi = new ProcessStartInfo(tar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-xf", archive, "-C", staging }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UpdateException(UpdateErrorCode.Extract, "tar did not start");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new UpdateException(UpdateErrorCode.Extract, err);
    }

    private static void Relaunch(string exe, string appDir, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = appDir, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[WaitPidEnv] = Environment.ProcessId.ToString();
        try { Process.Start(psi)?.Dispose(); return; }
        catch (Win32Exception e) when (e.NativeErrorCode == 740) { /* elevation required: go through the shell */ }
        catch (Exception e) { throw new UpdateException(UpdateErrorCode.Restart, e.Message); }
        // Not elevated: ShellExecute shows the UAC prompt; the environment is not inherited, so the PID goes in an argument.
        var shell = new ProcessStartInfo(exe) { WorkingDirectory = appDir, UseShellExecute = true };
        foreach (var a in args) shell.ArgumentList.Add(a);
        shell.ArgumentList.Add(WaitPidArg);
        shell.ArgumentList.Add(Environment.ProcessId.ToString());
        try { Process.Start(shell)?.Dispose(); }
        catch (Exception e) { throw new UpdateException(UpdateErrorCode.Restart, e.Message); }
    }

    /// <summary>Runs the whole update. Failures are reported through OnPhase(Failed) and rethrown.</summary>
    public static async Task RunAsync(SelfUpdateOptions o)
    {
        var onPhase = o.OnPhase ?? (_ => { });
        var staging = Path.Combine(o.AppDir, Staging);
        try
        {
            var asset = ChooseAsset(o.Release);
            if (!DownloadUrlAllowed(asset.Url, o.Overridden)) throw new UpdateException(UpdateErrorCode.NoAsset, "insecure url");
            var file = Path.Combine(o.TempRoot ?? Path.GetTempPath(), $"{o.Id}-update", asset.Name);

            onPhase(new UpdateProgress(UpdatePhase.Downloading, 0, asset.Size));
            await DownloadAsync(asset.Url, file, asset.Size, (d, t) => onPhase(new UpdateProgress(UpdatePhase.Downloading, d, t)));

            onPhase(new UpdateProgress(UpdatePhase.Verifying));
            if (!string.Equals(await Task.Run(() => Sha256File(file)), asset.Sha256, StringComparison.Ordinal))
            {
                try { File.Delete(file); } catch { /* ignore */ }
                throw new UpdateException(UpdateErrorCode.BadDigest);
            }

            onPhase(new UpdateProgress(UpdatePhase.Installing));
            await Task.Run(() => Extract(file, staging));
            ListFiles(staging); // rejects links
            if (!File.Exists(Path.Combine(staging, Path.GetFileName(o.ExePath))))
                throw new UpdateException(UpdateErrorCode.BadPackage, "no " + Path.GetFileName(o.ExePath));
            o.BeforeSwap?.Invoke();
            try { await Task.Run(() => Swap(staging, o.AppDir)); }
            finally { try { Directory.Delete(staging, true); } catch { /* ignore */ } }

            onPhase(new UpdateProgress(UpdatePhase.Restarting));
            Relaunch(o.ExePath, o.AppDir, CleanArgs(o.Args));
            o.Restart?.Invoke();
        }
        catch (Exception e)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            var ue = e as UpdateException ?? new UpdateException(UpdateErrorCode.Download, e.Message);
            onPhase(new UpdateProgress(UpdatePhase.Failed, Error: ue.Code));
            throw ue;
        }
    }
}
