using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace SeeowKiller;

/// <summary>
/// 自启管理：优先用"计划任务 + 最高权限"（登录/开机触发，不弹 UAC），
/// 兜底用 HKCU\...\Run（每次登录会弹一次 UAC）。
/// </summary>
internal static class AutoStartManager
{
    public const string LogonTaskName = "SeeowKiller-Logon";
    public const string BootTaskName = "SeeowKiller-Boot";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SeeowKiller";

    public static bool TaskExists(string taskName) =>
        RunProcess("schtasks.exe", $"/Query /TN \"{taskName}\"") == 0;

    // ---------------- 计划任务 ----------------

    public static bool CreateLogonTask(string exePath, out string message) =>
        RegisterTask(LogonTaskName, BuildLogonTaskXml(exePath), out message);

    public static bool CreateBootTask(string exePath, out string message) =>
        RegisterTask(BootTaskName, BuildBootTaskXml(exePath), out message);

    /// <summary>生成"登录触发"任务的 XML（--print-task-xml 也会用到）。</summary>
    public static string BuildLogonTaskXml(string exePath)
    {
        var user = Elevation.CurrentUserName;
        return BuildTaskXml(
            description: "SeeowKiller：登录后持续结束希沃管家进程（SeewoServiceAssistant.exe）",
            userId: user,
            logonType: "InteractiveToken",
            trigger: $"""
      <LogonTrigger>
        <Enabled>true</Enabled>
        <UserId>{Esc(user)}</UserId>
      </LogonTrigger>
""",
            exePath: exePath);
    }

    /// <summary>生成"开机触发（SYSTEM）"任务的 XML。</summary>
    public static string BuildBootTaskXml(string exePath) =>
        BuildTaskXml(
            description: "SeeowKiller：开机即以 SYSTEM 权限持续结束希沃管家进程",
            userId: "S-1-5-18",
            logonType: "ServiceAccount",
            trigger: """
      <BootTrigger>
        <Enabled>true</Enabled>
        <Delay>PT20S</Delay>
      </BootTrigger>
""",
            exePath: exePath);

    public static bool DeleteTask(string taskName, out string message)
    {
        if (!TaskExists(taskName))
        {
            message = $"计划任务 {taskName} 不存在（无需删除）";
            return true;
        }

        var code = RunProcess("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", out var output);
        message = code == 0 ? $"已删除计划任务 {taskName}" : $"删除计划任务 {taskName} 失败({code}): {output.Trim()}";
        return code == 0;
    }

    private static bool RegisterTask(string taskName, string xml, out string message)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"SeeowKiller_{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks 对非 ASCII 的 XML 要求 UTF-16；统一用 UTF-16 最稳
            File.WriteAllText(tmp, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var code = RunProcess("schtasks.exe", $"/Create /TN \"{taskName}\" /XML \"{tmp}\" /F", out var output);
            if (code == 0)
            {
                message = $"已注册计划任务 {taskName}";
                return true;
            }

            message = $"注册计划任务 {taskName} 失败({code}): {output.Trim()}";
            return false;
        }
        catch (Exception ex)
        {
            message = $"注册计划任务 {taskName} 异常: {ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string BuildTaskXml(string description, string userId, string logonType, string trigger, string exePath)
    {
        var exe = Esc(exePath);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>{Esc(description)}</Description>
  </RegistrationInfo>
  <Triggers>
{trigger}  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Esc(userId)}</UserId>
      <LogonType>{logonType}</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>999</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exe}</Command>
      <Arguments>--watch</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    // ---------------- HKCU Run 兜底 ----------------

    public static bool RunKeyInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool InstallRunKey(string exePath, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                message = "打开 HKCU Run 键失败";
                return false;
            }

            // --no-elevate 由程序自己在启动时补提权，这里只管拉起
            key.SetValue(RunValueName, $"\"{exePath}\" --watch", RegistryValueKind.String);
            message = "已写入 HKCU\\...\\Run（注意：每次登录会出现一次 UAC 提示）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入 Run 键失败: {ex.Message}";
            return false;
        }
    }

    public static bool UninstallRunKey(out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) is null)
            {
                message = "Run 键中没有 SeeowKiller 项（无需删除）";
                return true;
            }

            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            message = "已移除 HKCU\\...\\Run 项";
            return true;
        }
        catch (Exception ex)
        {
            message = $"删除 Run 键失败: {ex.Message}";
            return false;
        }
    }

    // ---------------- 工具 ----------------

    private static string Esc(string s) => SecurityElement.Escape(s) ?? s;

    private static int RunProcess(string file, string arguments) => RunProcess(file, arguments, out _);

    private static int RunProcess(string file, string arguments, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                output = "无法启动进程";
                return -1;
            }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout + stderr;
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
