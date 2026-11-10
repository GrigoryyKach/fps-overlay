using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace FpsOverlay;

/// <summary>
/// Автозапуск через Планировщик заданий, а не через реестр Run:
/// программы с requireAdministrator из Run-ключа Windows молча не запускает.
/// Задача с "наивысшими правами" стартует при входе в систему без окна UAC.
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

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь к exe");
        string user = WindowsIdentity.GetCurrent().Name;

        // XML вместо простых флагов schtasks: иначе задача по умолчанию убивается через 72 часа
        // и не запускается от батареи.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>FPS Overlay: CPU / GPU / RAM / FPS поверх игр</Description></RegistrationInfo>
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
