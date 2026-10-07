using System.ComponentModel;
using System.Diagnostics;

namespace SeeowKiller;

/// <summary>按进程名结束目标进程。单次扫描 + 可重复调用（看门狗循环就是反复调它）。</summary>
internal sealed class ProcessKiller
{
    private readonly AppConfig _cfg;
    private readonly Dictionary<string, DateTime> _lastDeniedLog = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _selfPid = Environment.ProcessId;
    private readonly string? _selfPath = Environment.ProcessPath;

    public ProcessKiller(AppConfig cfg) => _cfg = cfg;

    public long TotalKilled { get; private set; }
    public DateTime? LastKillTime { get; private set; }
    public string? LastKilledName { get; private set; }

    /// <summary>扫描一轮，返回本轮成功结束的进程数。</summary>
    public int KillOnce()
    {
        int killed = 0;
        foreach (var raw in _cfg.Targets)
        {
            var name = Normalize(raw);
            if (name.Length == 0) continue;
            if (IsNeverKill(name)) continue;

            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                Log.Error($"枚举进程 {name} 失败: {ex.Message}");
                continue;
            }

            foreach (var p in procs)
            {
                using (p)
                {
                    if (p.Id == _selfPid) continue;
                    if (IsNeverKill(baseNameFromPid: null, processName: name)) continue;

                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 权限不足或位数不匹配，忽略 */ }

                    // 绝不误杀自己和 NeverKill 名单里的进程
                    if (path is not null)
                    {
                        if (_selfPath is not null && string.Equals(path, _selfPath, StringComparison.OrdinalIgnoreCase)) continue;
                        var file = System.IO.Path.GetFileName(path);
                        if (IsNeverKill(Normalize(file))) continue;
                    }

                    var display = $"{name} (PID {p.Id}{(path is null ? "" : ", " + path)})";

                    try
                    {
                        p.Kill(entireProcessTree: false);
                        p.WaitForExit(3000);
                        killed++;
                        TotalKilled++;
                        LastKillTime = DateTime.Now;
                        LastKilledName = name;
                        Log.Info($"已结束 {display}");
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                    {
                        LogDenied(name,
                            $"结束 {display} 被拒绝访问（错误 5）：需要管理员权限；当前管理员={Elevation.IsAdmin}");
                    }
                    catch (InvalidOperationException)
                    {
                        // 进程已经自己退出了
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"结束 {display} 失败: {ex.Message}");
                    }
                }
            }
        }

        return killed;
    }

    /// <summary>
    /// 由"锁屏触发"那条路径（LockDetector）结束进程后回填计数，
    /// 让托盘上的累计数字把这种结束也算进去。
    /// </summary>
    public void NoteExternalKill(string processName)
    {
        TotalKilled++;
        LastKillTime = DateTime.Now;
        LastKilledName = Normalize(processName);
    }

    /// <summary>列出当前正在运行的目标进程，供 --status 使用。</summary>
    public List<string> DescribeRunningTargets()
    {
        var result = new List<string>();
        foreach (var raw in _cfg.Targets)
        {
            var name = Normalize(raw);
            if (name.Length == 0) continue;
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    result.Add($"PID {p.Id,-8} {name}{(path is null ? "" : "  " + path)}");
                }
            }
        }
        return result;
    }

    private static string Normalize(string name)
    {
        var n = name.Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n;
    }

    private bool IsNeverKill(string? baseNameFromPid = null, string? processName = null)
    {
        foreach (var n in _cfg.NeverKill)
        {
            var v = Normalize(n);
            if (v.Length == 0) continue;
            if (processName is not null && string.Equals(v, processName, StringComparison.OrdinalIgnoreCase)) return true;
            if (baseNameFromPid is not null && string.Equals(v, baseNameFromPid, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>"拒绝访问"每 60 秒最多记一次，避免刷爆日志。</summary>
    private void LogDenied(string name, string message)
    {
        var now = DateTime.Now;
        if (_lastDeniedLog.TryGetValue(name, out var last) && (now - last).TotalSeconds < 60) return;
        _lastDeniedLog[name] = now;
        Log.Warn(message);
    }
}
