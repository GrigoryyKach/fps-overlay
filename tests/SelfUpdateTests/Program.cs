using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace FpsOverlay;

internal static class Program
{
    private static int _passed;

    private static string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "fps-update-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void W(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static string R(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));

    private static void Check(bool cond, string msg) { if (!cond) throw new Exception("FAILED: " + msg); }
    private static void Eq<T>(T expected, T actual, string msg) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"FAILED: {msg} (expected {expected}, got {actual})"); }

    private static void Throws(UpdateErrorCode code, Action a)
    {
        try { a(); } catch (UpdateException e) { Eq(code, e.Code, "error code"); return; }
        throw new Exception("FAILED: expected " + code);
    }

    private static async Task ThrowsAsync(UpdateErrorCode code, Func<Task> a)
    {
        try { await a(); } catch (UpdateException e) { Eq(code, e.Code, "error code"); return; }
        throw new Exception("FAILED: expected " + code);
    }

    private static void Test(string name, Action a) { a(); _passed++; Console.WriteLine("ok  " + name); }

    private static JsonElement Rel(string assetName, string url, long size, string? digest)
    {
        var d = digest is null ? "" : $", \"digest\": \"{digest}\"";
        return JsonDocument.Parse($"{{\"assets\":[{{\"name\":\"notes.txt\"}},{{\"name\":\"{assetName}\",\"browser_download_url\":\"{url}\",\"size\":{size}{d}}}]}}").RootElement;
    }

    /// <summary>A tiny HTTP server on 127.0.0.1 that answers every request with the file.</summary>
    private static (TcpListener l, int port) Serve(string file)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[4096];
                        await s.ReadAsync(buf);
                        var data = File.ReadAllBytes(file);
                        var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                        await s.WriteAsync(head);
                        await s.WriteAsync(data);
                    }
                });
            }
        });
        return (l, ((IPEndPoint)l.LocalEndpoint).Port);
    }

    private static async Task<int> Main()
    {
        const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        Test("EnvName", () => Eq("FPS_OVERLAY_UPDATE_API", SelfUpdate.EnvName("fps-overlay"), "env name"));
        Test("ChooseAsset picks the win zip and its digest", () =>
        {
            var a = SelfUpdate.ChooseAsset(Rel("FpsOverlay-win-x64.zip", "https://x/a.zip", 10, "sha256:" + Sha));
            Eq("FpsOverlay-win-x64.zip", a.Name, "name"); Eq(10L, a.Size, "size"); Eq(Sha, a.Sha256, "sha");
        });
        Test("ChooseAsset: no asset / no digest / bad digest", () =>
        {
            Throws(UpdateErrorCode.NoAsset, () => SelfUpdate.ChooseAsset(JsonDocument.Parse("{\"assets\":[]}").RootElement));
            Throws(UpdateErrorCode.NoDigest, () => SelfUpdate.ChooseAsset(Rel("a-win-x64.zip", "https://x", 1, null)));
            Throws(UpdateErrorCode.NoDigest, () => SelfUpdate.ChooseAsset(Rel("a-win-x64.zip", "https://x", 1, "sha256:zz")));
        });
        Test("download URL policy", () =>
        {
            Check(SelfUpdate.DownloadUrlAllowed("https://github.com/a", false), "https ok");
            Check(!SelfUpdate.DownloadUrlAllowed("http://127.0.0.1:1/a", false), "http refused");
            Check(SelfUpdate.DownloadUrlAllowed("http://127.0.0.1:1/a", true), "http ok with override");
            Check(!SelfUpdate.DownloadUrlAllowed("http://127.0.0.1.evil.example/a", true), "lookalike host refused");
            Check(!SelfUpdate.DownloadUrlAllowed("http://evil.example/a", true), "other host refused");
        });
        Test("ChooseMode: launcher, then writable, then page", () =>
        {
            var d = Tmp();
            Eq(UpdateMode.Launcher, SelfUpdate.ChooseMode(@"C:\NIMVORO.exe", d), "launcher");
            Eq(UpdateMode.Self, SelfUpdate.ChooseMode(null, d), "self");
            Eq(UpdateMode.Page, SelfUpdate.ChooseMode(null, Path.Combine(d, "missing", "dir")), "page");
            Check(!File.Exists(Path.Combine(d, SelfUpdate.WriteTest)), "probe removed");
        });
        Test("Swap replaces files, keeps protected and foreign files", () =>
        {
            string app = Tmp(), st = Tmp();
            W(app, "App.exe", "old"); W(app, "lib/x.dll", "old"); W(app, ".nimvoro.json", "{\"language\":\"ru\"}"); W(app, "settings.json", "mine"); W(app, "extra.txt", "keep");
            W(st, "App.exe", "new"); W(st, "lib/x.dll", "new"); W(st, "lib/y.dll", "new"); W(st, ".nimvoro.json", "{\"language\":\"en\"}"); W(st, "settings.json", "pkg");
            SelfUpdate.Swap(st, app);
            Eq("new", R(app, "App.exe"), "exe"); Eq("new", R(app, "lib/x.dll"), "dll"); Eq("new", R(app, "lib/y.dll"), "new dll");
            Eq("{\"language\":\"ru\"}", R(app, ".nimvoro.json"), "marker kept"); Eq("mine", R(app, "settings.json"), "settings kept");
            Eq("keep", R(app, "extra.txt"), "foreign kept"); Eq("old", R(app, "App.exe.old"), "backup");
        });
        Test("Swap works while the exe is running (rename of a loaded image)", () =>
        {
            string app = Tmp(), st = Tmp();
            File.Copy(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "PING.EXE"), Path.Combine(app, "run.exe"));
            W(st, "run.exe", "new");
            using var child = Process.Start(new ProcessStartInfo(Path.Combine(app, "run.exe"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
            try
            {
                System.Threading.Thread.Sleep(500);
                Check(!child.HasExited, "child runs");
                SelfUpdate.Swap(st, app);
                Eq("new", R(app, "run.exe"), "new exe in place");
                Check(File.Exists(Path.Combine(app, "run.exe.old")), "running exe renamed to .old");
            }
            finally { child.Kill(); child.WaitForExit(); }
        });
        Test("Swap rolls back when a step fails", () =>
        {
            string app = Tmp(), st = Tmp();
            W(app, "a.txt", "old-a"); W(app, "b.txt", "old-b"); W(app, "c", "a file where a folder is needed");
            W(st, "a.txt", "new-a"); W(st, "b.txt", "new-b"); W(st, "c/d.txt", "new-d");
            Throws(UpdateErrorCode.SwapFailed, () => SelfUpdate.Swap(st, app));
            Eq("old-a", R(app, "a.txt"), "a restored"); Eq("old-b", R(app, "b.txt"), "b restored");
            Check(!File.Exists(Path.Combine(app, "a.txt.old")) && !File.Exists(Path.Combine(app, "b.txt.old")), "no backups left");
        });
        Test("Cleanup removes *.old and staging, one level deep only", () =>
        {
            var app = Tmp();
            W(app, "a.exe.old", "x"); W(app, "lib/b.dll.old", "x"); W(app, "lib/deep/c.old", "keep"); W(app, "keep.exe", "x"); W(app, ".update-staging/f", "x");
            SelfUpdate.Cleanup(app, "fps-test-none");
            Check(!File.Exists(Path.Combine(app, "a.exe.old")), "top-level .old");
            Check(!File.Exists(Path.Combine(app, "lib/b.dll.old")), "sub folder .old");
            Check(File.Exists(Path.Combine(app, "lib/deep/c.old")), "two levels deep is kept");
            Check(File.Exists(Path.Combine(app, "keep.exe")), "exe kept");
            Check(!Directory.Exists(Path.Combine(app, ".update-staging")), "staging removed");
        });
        Test("CleanArgs drops the wait-pid pair", () =>
            Check(SelfUpdate.CleanArgs(new[] { "--x", "--nimvoro-wait-pid", "12", "--y" }).SequenceEqual(new[] { "--x", "--y" }), "args"));

        // End to end with a local server: curl + tar + digest + swap
        string srcDir = Tmp(), zipDir = Tmp();
        W(srcDir, "FpsOverlay.exe", "new exe"); W(srcDir, "lib/a.dll", "new dll");
        var zip = Path.Combine(zipDir, "Fake-win-x64.zip");
        ZipFile.CreateFromDirectory(srcDir, zip);
        var good = SelfUpdate.Sha256File(zip);
        var (listener, port) = Serve(zip);
        var url = $"http://127.0.0.1:{port}/Fake-win-x64.zip";
        var size = new FileInfo(zip).Length;
        var temp = Tmp();
        try
        {
            string app = Tmp(); W(app, "FpsOverlay.exe", "old exe"); W(app, "lib/a.dll", "old dll");
            SelfUpdateOptions Opts(string digest, bool over, string exe, List<UpdatePhase>? phases = null, Action? restart = null) => new()
            {
                Release = Rel("Fake-win-x64.zip", url, size, "sha256:" + digest), Id = "fps-test", AppDir = app,
                ExePath = Path.Combine(app, exe), Overridden = over, TempRoot = temp, OnPhase = p => phases?.Add(p.Phase), Restart = restart,
            };

            await ThrowsAsync(UpdateErrorCode.BadDigest, () => SelfUpdate.RunAsync(Opts(Sha, true, "FpsOverlay.exe")));
            Eq("old exe", R(app, "FpsOverlay.exe"), "bad digest installs nothing"); _passed++; Console.WriteLine("ok  bad digest installs nothing");

            await ThrowsAsync(UpdateErrorCode.NoAsset, () => SelfUpdate.RunAsync(Opts(good, false, "FpsOverlay.exe")));
            Console.WriteLine("ok  http is refused without the override"); _passed++;

            await ThrowsAsync(UpdateErrorCode.BadPackage, () => SelfUpdate.RunAsync(Opts(good, true, "Other.exe")));
            Check(!Directory.Exists(Path.Combine(app, SelfUpdate.Staging)), "staging removed");
            Console.WriteLine("ok  package without the exe is rejected, staging removed"); _passed++;

            // Good digest: extract + swap work; the relaunch of the dummy "exe" fails, which is reported as Restart (no app is launched).
            var phases = new List<UpdatePhase>();
            await ThrowsAsync(UpdateErrorCode.Restart, () => SelfUpdate.RunAsync(Opts(good, true, "FpsOverlay.exe", phases)));
            Eq("new exe", R(app, "FpsOverlay.exe"), "exe swapped"); Eq("new dll", R(app, "lib/a.dll"), "dll swapped");
            Eq("old exe", R(app, "FpsOverlay.exe.old"), "backup kept until next start");
            Check(!Directory.Exists(Path.Combine(app, SelfUpdate.Staging)), "staging removed");
            Check(phases.Take(4).SequenceEqual(new[] { UpdatePhase.Downloading, UpdatePhase.Verifying, UpdatePhase.Installing, UpdatePhase.Restarting }) || phases.Contains(UpdatePhase.Restarting), "phases");
            Console.WriteLine("ok  good digest: download, verify, extract, swap"); _passed++;
        }
        finally { listener.Stop(); }

        Console.WriteLine($"\n{_passed} tests passed");
        return 0;
    }
}
