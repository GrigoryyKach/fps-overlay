using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace FpsOverlay;

/// <summary>
/// Autostart through Task Scheduler rather than the Run registry key:
/// Windows silently skips requireAdministrator programs in the Run key.
/// A task with the highest privileges starts at logon without a UAC prompt.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "FpsOverlay";

    public static bool IsEnabled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    public static bool Set(bool enable, out string? error)
    {
        error = null;
        if (!enable)
        {
            int code = RunSchtasks($"/Delete /F /TN \"{TaskName}\"", out string output);
            if (code != 0) error = output;
            return code == 0;
        }

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the exe path");
        string user = WindowsIdentity.GetCurrent().Name;

        // XML instead of plain schtasks flags: otherwise the task is killed after 72 hours by default
        // and does not start on battery.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>FPS Overlay: CPU / GPU / RAM / FPS on top of games</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{SecurityElement.Escape(exe)}</Command></Exec>
              </Actions>
            </Task>
            """;

        string tmp = Path.Combine(Path.GetTempPath(), "FpsOverlay_task.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"", out string output);
            if (code != 0) error = output;
            return code == 0;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static int RunSchtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode;
    }
}
