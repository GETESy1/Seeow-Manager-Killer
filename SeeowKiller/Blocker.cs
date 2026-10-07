using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SeeowKiller;

/// <summary>
/// "不让它起来" 的两层硬拦截：
///   1) IFEO（Image File Execution Options）：给目标 exe 挂一个假的"调试器"，
///      Windows 在启动该映像时会去启动调试器并把目标挂起 —— 调试器（我们的 --ifeo-stub）
///      直接退出，目标就永远起不来。启动方不会收到错误，安静得像没装一样。
///   2) ACL：icacls 给 exe 加一条 Everyone 拒绝执行（X）的 ACE，CreateProcess 直接 ERROR_ACCESS_DENIED。
/// 两层都只影响"新的启动"，已经在跑的进程仍由看门狗（ProcessKiller）负责按死。
/// </summary>
internal static class Blocker
{
    private const string IfeoSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string BackupSubKey = @"SOFTWARE\SeeowKiller";
    private const string EveryoneSid = "*S-1-1-0";
    private const string DebuggerValue = "Debugger";

    public const string StubArgument = "--ifeo-stub";

    // ==================== IFEO ====================

    /// <summary>给这些映像名挂上 IFEO 假调试器。需要管理员（HKLM）。</summary>
    public static bool ApplyIfeo(IEnumerable<string> images, string debuggerCommand, out List<string> messages)
    {
        var list = new List<string>();
        bool allOk = true;

        foreach (var image in images)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var label = $"{image} [{ViewName(view)}]";
                try
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                        .CreateSubKey($@"{IfeoSubKey}\{image}", writable: true);
                    if (key is null)
                    {
                        list.Add($"{label}: 无法创建/打开 IFEO 键");
                        allOk = false;
                        continue;
                    }

                    var current = key.GetValue(DebuggerValue) as string;

                    // 备份原有值（可能是希沃自己设的，别弄丢），只备份一次
                    BackUp(key, image, view, current);

                    if (string.Equals(current, debuggerCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add($"{label}: 已经是我们的拦截值");
                        continue;
                    }

                    key.SetValue(DebuggerValue, debuggerCommand, RegistryValueKind.String);

                    var verify = key.GetValue(DebuggerValue) as string;
                    if (string.Equals(verify, debuggerCommand, StringComparison.Ordinal))
                        list.Add($"{label}: 已拦截{(string.IsNullOrEmpty(current) ? "" : $"（覆盖了原值: {current}）")}");
                    else
                    {
                        list.Add($"{label}: 写入后校验失败（可能被文件保护回滚）");
                        allOk = false;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    list.Add($"{label}: 拒绝访问，需要管理员权限");
                    allOk = false;
                }
                catch (Exception ex)
                {
                    list.Add($"{label}: 失败 {ex.Message}");
                    allOk = false;
                }
            }
        }

        messages = list;
        return allOk;
    }

    /// <summary>撤销 IFEO 拦截，并尽量还原被我们覆盖的原值。</summary>
    public static bool ClearIfeo(IEnumerable<string> images, string? ourCommandPrefix, out List<string> messages)
    {
        var list = new List<string>();
        bool allOk = true;

        foreach (var image in images)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var label = $"{image} [{ViewName(view)}]";
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey($@"{IfeoSubKey}\{image}", writable: true);
                    if (key is null)
                    {
                        list.Add($"{label}: 没有 IFEO 键，无需处理");
                        continue;
                    }

                    var current = key.GetValue(DebuggerValue) as string;
                    if (current is null)
                    {
                        list.Add($"{label}: 没有 Debugger 值");
                        continue;
                    }

                    // 不是我们设的就不动它（避免破坏别人的设置）
                    if (ourCommandPrefix is not null &&
                        !current.Contains(ourCommandPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add($"{label}: Debugger 不是本程序设置的，保持不动（{current}）");
                        continue;
                    }

                    if (TryRestore(key, image, view, out var restored))
                        list.Add($"{label}: 已撤销，并还原为 {restored}");
                    else
                    {
                        key.DeleteValue(DebuggerValue, throwOnMissingValue: false);
                        list.Add($"{label}: 已撤销");
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    list.Add($"{label}: 拒绝访问，需要管理员权限");
                    allOk = false;
                }
                catch (Exception ex)
                {
                    list.Add($"{label}: 失败 {ex.Message}");
                    allOk = false;
                }
            }
        }

        messages = list;
        return allOk;
    }

    /// <summary>目标映像是不是正被我们的 IFEO 拦着。</summary>
    public static bool IsIfeoBlocked(string image, string ourCommandPrefix)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey($@"{IfeoSubKey}\{image}", writable: false);
                var value = key?.GetValue(DebuggerValue) as string;
                if (value is not null && value.Contains(ourCommandPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // 读不到就当没拦着
            }
        }
        return false;
    }

    /// <summary>读一下当前 Debugger 值（诊断用）。</summary>
    public static string DescribeIfeo(string image)
    {
        var parts = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                    .OpenSubKey($@"{IfeoSubKey}\{image}", writable: false);
                var value = key?.GetValue(DebuggerValue) as string;
                parts.Add($"{ViewName(view)}={(value is null ? "无" : value)}");
            }
            catch (Exception ex)
            {
                parts.Add($"{ViewName(view)}=读取失败({ex.Message})");
            }
        }
        return string.Join("; ", parts);
    }

    /// <summary>看门狗用：拦截值被希沃的文件保护抹掉时，重新挂回去。</summary>
    public static bool EnsureIfeoStillApplied(IEnumerable<string> images, string debuggerCommand, out int reApplied)
    {
        reApplied = 0;
        var exePath = Environment.ProcessPath ?? "";
        var missing = images.Where(i => !IsIfeoBlocked(i, exePath)).ToList();
        if (missing.Count == 0) return true;

        ApplyIfeo(missing, debuggerCommand, out _);
        reApplied = missing.Count;
        return true;
    }

    // ==================== ACL（拒绝执行） ====================

    public static bool DenyExecute(string path, out string message)
    {
        var r = RunIcacls($"\"{path}\" /deny \"{EveryoneSid}:(X)\"");
        message = r.ok
            ? $"已禁止执行: {path}"
            : $"禁止执行失败: {path} -> {r.output.Trim()}";
        return r.ok;
    }

    public static bool RemoveDenyExecute(string path, out string message)
    {
        var r = RunIcacls($"\"{path}\" /remove:d \"{EveryoneSid}\"");
        message = r.ok
            ? $"已恢复可执行: {path}"
            : $"恢复失败: {path} -> {r.output.Trim()}";
        return r.ok;
    }

    /// <summary>是否已存在"拒绝执行"的 ACE。</summary>
    public static bool IsExecuteDenied(string path)
    {
        var r = RunIcacls($"\"{path}\"");
        if (!r.ok) return false;

        foreach (var rawLine in r.output.Split('\n'))
        {
            if (LineDeniesExecute(rawLine)) return true;
        }
        return false;
    }

    /// <summary>
    /// 判断 icacls 输出的一行是不是"拒绝执行"。
    /// 注意 icacls 会把 (X) 打成 (S,X)（S=Synchronize），所以不能简单地找 "(X"，
    /// 必须把 (DENY) 后面的权限标志位拆开看：X / RX / F 都会挡住 CreateProcess。
    /// </summary>
    internal static bool LineDeniesExecute(string line)
    {
        var idx = line.IndexOf("(DENY)", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var match = Regex.Match(line[(idx + 6)..], @"\(([^)]*)\)");
        if (!match.Success) return false;

        foreach (var flag in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = flag.Trim().ToUpperInvariant();
            if (f is "X" or "RX" or "F" or "FX") return true;
        }

        return false;
    }

    private static (bool ok, string output) RunIcacls(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return (false, "无法启动 icacls.exe");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return (p.HasExited && p.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 找目标 exe 的真实路径 ====================

    /// <summary>先问正在运行的进程，再翻常见的希沃安装目录。</summary>
    public static List<string> DiscoverPaths(IEnumerable<string> images)
    {
        var names = images.Select(Normalize).Where(n => n.Length > 0)
            .Select(n => n + ".exe").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1) 正在运行的进程
        foreach (var name in names)
        {
            var bare = name[..^4];
            try
            {
                foreach (var p in Process.GetProcessesByName(bare))
                {
                    using (p)
                    {
                        try
                        {
                            var path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path)) found[path] = path;
                        }
                        catch
                        {
                            // 权限不足时读不到路径，忽略
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // 2) 常见安装目录
        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in SafeEnumerate(root, names))
                found[file] = file;
        }

        return found.Values.ToList();
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var roots = new List<string>();
        foreach (var baseDir in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;
            roots.Add(Path.Combine(baseDir, "Seewo"));
            try
            {
                // Program Files 下名字带 Seewo 的一级目录
                foreach (var dir in Directory.EnumerateDirectories(baseDir))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Contains("seewo", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("希沃", StringComparison.OrdinalIgnoreCase))
                        roots.Add(dir);
                }
            }
            catch
            {
                // ignore
            }
        }

        if (!string.IsNullOrEmpty(common)) roots.Add(Path.Combine(common, "Seewo"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerate(string root, HashSet<string> names)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int visited = 0;

        while (stack.Count > 0 && visited < 20000)
        {
            var dir = stack.Pop();
            visited++;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var f in files)
            {
                if (names.Contains(Path.GetFileName(f))) yield return f;
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var d in subdirs) stack.Push(d);
        }
    }

    // ==================== 杂项 ====================

    /// <summary>拼出要写进 Debugger 的命令行：本程序自己 + --ifeo-stub。</summary>
    public static string BuildDebuggerCommand()
    {
        var exe = Environment.ProcessPath ?? "";
        return $"\"{exe}\" {StubArgument}";
    }

    private static void BackUp(RegistryKey ifeoKey, string image, RegistryView view, string? current)
    {
        try
        {
            if (ifeoKey.GetValue("SeeowKiller.Backup", null) is string) return; // 已备份过

            using var backupKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view)
                .CreateSubKey(BackupSubKey, writable: true);
            if (backupKey is null) return;

            var valueName = BackupValueName(image);
            if (backupKey.GetValue(valueName) is not null) return; // 已经有备份，别覆盖

            backupKey.SetValue(valueName, current ?? BackupAbsent, RegistryValueKind.String);
            ifeoKey.SetValue("SeeowKiller.Backup", "1", RegistryValueKind.String);
        }
        catch
        {
            // 备份失败不影响拦截本身
        }
    }

    private static bool TryRestore(RegistryKey ifeoKey, string image, RegistryView view, out string? restored)
    {
        restored = null;
        try
        {
            using var backupBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var backupKey = backupBase.OpenSubKey(BackupSubKey, writable: true);
            var valueName = BackupValueName(image);
            var saved = backupKey?.GetValue(valueName) as string;

            if (saved is null)
            {
                ifeoKey.DeleteValue("SeeowKiller.Backup", throwOnMissingValue: false);
                return false;
            }

            if (saved == BackupAbsent)
            {
                ifeoKey.DeleteValue(DebuggerValue, throwOnMissingValue: false);
                restored = "（原本没有）";
            }
            else
            {
                ifeoKey.SetValue(DebuggerValue, saved, RegistryValueKind.String);
                restored = saved;
            }

            backupKey!.DeleteValue(valueName, throwOnMissingValue: false);
            ifeoKey.DeleteValue("SeeowKiller.Backup", throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const string BackupAbsent = "<SeeowKiller:原本无 Debugger 值>";

    private static string BackupValueName(string image) => $"Backup:{image}";

    private static string ViewName(RegistryView view) => view == RegistryView.Registry64 ? "64位" : "32位";

    private static string Normalize(string name)
    {
        var n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n;
    }
}
