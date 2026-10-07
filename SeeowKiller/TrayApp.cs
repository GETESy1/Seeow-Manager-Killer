using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SeeowKiller;

/// <summary>托盘 + 看门狗主循环。双击托盘图标可看状态，右键有菜单。</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppConfig _cfg;
    private readonly ProcessKiller _killer;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _logonTaskItem;
    private readonly ToolStripMenuItem _bootTaskItem;
    private readonly ToolStripMenuItem _ifeoItem;
    private readonly ToolStripMenuItem _aclItem;
    private readonly CancellationTokenSource _cts = new();

    private volatile bool _paused;
    private long _lastRoundKilled;
    private bool _updatingChecks;
    private int _reapplyCountdown;
    private readonly List<string> _knownPaths = new();

    /// <summary>累计检测到锁屏的次数。</summary>
    private long LockDetectedCount;

    /// <summary>最近一次检测到锁屏的时间。</summary>
    private DateTime? LastLockTime;

    /// <summary>最近一次对锁屏采取的动作（杀进程 / 隐藏窗口）。</summary>
    private string? LastAction;

    /// <summary>目标 exe 的真实路径（探测一次并缓存，ACL 相关操作都用它）。</summary>
    private List<string> KnownPaths()
    {
        lock (_knownPaths)
        {
            if (_knownPaths.Count > 0) return new List<string>(_knownPaths);
        }
        return DiscoverAndCachePaths();
    }

    private List<string> DiscoverAndCachePaths()
    {
        var paths = new List<string>(_cfg.BlockPaths);
        try
        {
            foreach (var p in Blocker.DiscoverPaths(_cfg.Targets))
                if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        catch (Exception ex)
        {
            Log.Error($"探测目标 exe 路径失败: {ex.Message}");
        }

        lock (_knownPaths)
        {
            _knownPaths.Clear();
            _knownPaths.AddRange(paths);
        }
        return paths;
    }

    public TrayApp(AppConfig cfg)
    {
        _cfg = cfg;
        _killer = new ProcessKiller(cfg);
        _icon = CreateIcon();

        var menu = new ContextMenuStrip();

        _pauseItem = new ToolStripMenuItem("暂停拦截", null, (_, _) => TogglePause());
        _logonTaskItem = new ToolStripMenuItem("随登录启动（计划任务·推荐）", null, (_, _) => ToggleLogonTask());
        _bootTaskItem = new ToolStripMenuItem("随开机启动（SYSTEM·需管理员）", null, (_, _) => ToggleBootTask());
        _ifeoItem = new ToolStripMenuItem("硬拦截：禁止启动（IFEO）", null, (_, _) => ToggleIfeo());
        _aclItem = new ToolStripMenuItem("硬拦截：禁止执行（ACL）", null, (_, _) => ToggleAcl());

        menu.Items.Add(new ToolStripMenuItem("立即结束一次", null, (_, _) => KillNow()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_ifeoItem);
        menu.Items.Add(_aclItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_logonTaskItem);
        menu.Items.Add(_bootTaskItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenPath(Log.Path)));
        menu.Items.Add(new ToolStripMenuItem("打开配置目录", null, (_, _) => OpenPath(AppConfig.DataDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "SeeowKiller",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowStatusBalloon();

        RefreshCheckStates();
        UpdateTooltip();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => UpdateTooltip();
        _uiTimer.Start();

        _ = Task.Run(() => WatchLoop(_cts.Token));

        // 后台先探测一次目标 exe 路径，供 ACL 层使用（不阻塞托盘启动）
        _ = Task.Run(() =>
        {
            DiscoverAndCachePaths();
            RefreshCheckStates();
        });

        _tray.ShowBalloonTip(3000, "SeeowKiller 已启动",
            _cfg.PolicyKind == InterceptPolicy.Lock
                ? $"锁屏触发模式：只在检测到「{_cfg.LockTitle}」锁屏窗口时动手"
                : $"看门狗模式：持续结束 {string.Join(", ", _cfg.Targets)}",
            ToolTipIcon.Info);
        Log.Info($"已启动：策略={_cfg.PolicyKind}，目标=[{string.Join(", ", _cfg.Targets)}]，间隔={_cfg.IntervalMs}ms，管理员={Elevation.IsAdmin}");
        if (_cfg.PolicyKind == InterceptPolicy.Lock)
            Log.Info($"锁屏判据：标题=\"{_cfg.LockTitle}\" 且 类名含\"{_cfg.LockClass}\" 且 进程含\"{_cfg.LockProcess}\" 且 置顶全屏；动作={(_cfg.HideInsteadOfKill ? "hide（失败则杀进程）" : "kill")}");
    }

    private async Task WatchLoop(CancellationToken token)
    {
        // 记录上一次的动作时间，防止"杀掉→被拉起→又锁屏"时疯狂刷屏
        var lastActionAt = new Dictionary<uint, DateTime>();

        while (!token.IsCancellationRequested)
        {
            if (!_paused)
            {
                try
                {
                    _lastRoundKilled = _cfg.PolicyKind switch
                    {
                        // 默认：只有"确实是锁屏窗口"才动手
                        InterceptPolicy.Lock => DetectAndIntercept(lastActionAt),
                        // 老行为：无差别反复结束目标进程
                        _ => _killer.KillOnce(),
                    };
                }
                catch (Exception ex)
                {
                    Log.Error($"本轮扫描异常: {ex}");
                }

                // 每 ~15 秒检查一次硬拦截（如果用户手动开过）是否被希沃的文件保护抹掉
                if (_cfg.ReapplyBlock && ++_reapplyCountdown * _cfg.IntervalMs >= 15000)
                {
                    _reapplyCountdown = 0;
                    ReapplyBlockIfNeeded();
                }
            }

            try
            {
                await Task.Delay(_cfg.IntervalMs, token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 核心：扫一遍窗口，只有"确实是锁屏窗口"才动手；平时希沃管家照常运行，一概不管。
    /// </summary>
    private long DetectAndIntercept(Dictionary<uint, DateTime> lastActionAt)
    {
        List<LockWindowInfo> locks;
        try
        {
            locks = LockDetector.Scan(_cfg, onlyMatches: true);
        }
        catch (Exception ex)
        {
            Log.Error($"扫描锁屏窗口失败: {ex.Message}");
            return 0;
        }

        if (locks.Count == 0) return 0;

        long acted = 0;
        foreach (var w in locks)
        {
            // 同一个进程 2 秒内不重复动手
            if (lastActionAt.TryGetValue(w.Pid, out var last) && (DateTime.Now - last).TotalSeconds < 2)
                continue;

            lastActionAt[w.Pid] = DateTime.Now;
            LockDetectedCount++;
            LastLockTime = DateTime.Now;
            Log.Info($"检测到锁屏：{w.Describe()}");

            bool handled;
            if (_cfg.HideInsteadOfKill)
            {
                // 先试着把窗口藏掉（进程还活着，不会触发"被杀→重锁"的循环）
                handled = LockDetector.TryHide(w);
                if (handled)
                {
                    Thread.Sleep(1200);
                    handled = !LockDetector.Scan(_cfg, onlyMatches: true).Any(x => x.Hwnd == w.Hwnd);
                }
                if (!handled)
                {
                    Log.Warn("隐藏锁屏窗口没生效（多半权限不够），改为结束该进程");
                    handled = LockDetector.KillOwner(w);
                    if (handled) LastAction = "杀进程";
                }
                else LastAction = "隐藏窗口";
            }
            else
            {
                handled = LockDetector.KillOwner(w);
                if (handled) LastAction = "杀进程";
            }

            if (handled)
            {
                acted++;
                _killer.NoteExternalKill(w.ProcessName);
            }
        }

        return acted;
    }

    /// <summary>IFEO 被抹掉就重新挂上（说明对方有保护在回滚，日志里会留痕）。</summary>
    private void ReapplyBlockIfNeeded()
    {
        try
        {
            var self = Environment.ProcessPath ?? "";
            var enabled = _cfg.Targets.Any(t => Blocker.IsIfeoBlocked(t, self));
            if (!enabled) return; // 用户本来就没开硬拦截，不去动它

            var missing = _cfg.Targets.Where(t => !Blocker.IsIfeoBlocked(t, self)).ToList();
            if (missing.Count == 0) return;

            Log.Warn($"检测到硬拦截被移除（{string.Join(", ", missing)}），重新挂回 IFEO");
            Blocker.ApplyIfeo(missing, Blocker.BuildDebuggerCommand(), out var messages);
            foreach (var m in messages) Log.Info("重挂 " + m);
            RefreshCheckStates();
        }
        catch (Exception ex)
        {
            Log.Error($"重挂硬拦截失败: {ex.Message}");
        }
    }

    private void KillNow()
    {
        try
        {
            int n = _killer.KillOnce();
            _tray.ShowBalloonTip(2000, "SeeowKiller", n > 0 ? $"已结束 {n} 个进程" : "当前没有发现目标进程", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error($"手动结束失败: {ex.Message}");
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Checked = _paused;
        Log.Info(_paused ? "已暂停拦截" : "已恢复拦截");
        UpdateTooltip();
    }

    private void ToggleLogonTask()
    {
        var exe = Environment.ProcessPath ?? "";
        string message;
        bool ok;

        if (AutoStartManager.TaskExists(AutoStartManager.LogonTaskName))
            ok = AutoStartManager.DeleteTask(AutoStartManager.LogonTaskName, out message);
        else
            ok = AutoStartManager.CreateLogonTask(exe, out message);

        Log.Write(ok ? "INFO " : "ERROR", message);
        RefreshCheckStates();
        _tray.ShowBalloonTip(3000, "SeeowKiller 自启设置", message, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void ToggleBootTask()
    {
        var exe = Environment.ProcessPath ?? "";
        string message;
        bool ok;

        if (AutoStartManager.TaskExists(AutoStartManager.BootTaskName))
            ok = AutoStartManager.DeleteTask(AutoStartManager.BootTaskName, out message);
        else
            ok = AutoStartManager.CreateBootTask(exe, out message);

        Log.Write(ok ? "INFO " : "ERROR", message);
        RefreshCheckStates();
        _tray.ShowBalloonTip(3000, "SeeowKiller 自启设置", message, ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>托盘里开关 IFEO 硬拦截。</summary>
    private void ToggleIfeo()
    {
        var self = Environment.ProcessPath ?? "";
        bool on = !_cfg.Targets.Any(t => Blocker.IsIfeoBlocked(t, self));
        var messages = new List<string>();
        bool ok;

        if (on)
        {
            ok = Blocker.ApplyIfeo(_cfg.Targets, Blocker.BuildDebuggerCommand(), out messages);
        }
        else
        {
            ok = Blocker.ClearIfeo(_cfg.Targets, self, out messages);
        }

        Log.Write(ok ? "INFO " : "ERROR", $"{(on ? "开启" : "关闭")} IFEO 硬拦截: " + string.Join(" | ", messages));
        RefreshCheckStates();
        _tray.ShowBalloonTip(4000, on ? "已开启硬拦截（禁止启动）" : "已关闭硬拦截",
            string.Join("\n", messages), ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>托盘里开关 ACL 层（路径探测比较费时，丢到线程池里做）。</summary>
    private void ToggleAcl()
    {
        _tray.ShowBalloonTip(2000, "SeeowKiller", "正在处理 ACL，请稍候…", ToolTipIcon.Info);
        Task.Run(() =>
        {
            try
            {
                var paths = KnownPaths();

                if (paths.Count == 0)
                {
                    Log.Warn("ACL: 没探测到目标 exe 路径，未做任何修改");
                    _tray.ShowBalloonTip(4000, "未找到目标 exe", "可用配置文件里的 BlockPaths 手动指定路径后重试。", ToolTipIcon.Warning);
                    return;
                }

                bool anyDenied = paths.Any(Blocker.IsExecuteDenied);
                var lines = new List<string>();
                bool allOk = true;

                foreach (var path in paths)
                {
                    string msg;
                    bool ok = anyDenied ? Blocker.RemoveDenyExecute(path, out msg) : Blocker.DenyExecute(path, out msg);
                    lines.Add(msg);
                    Log.Write(ok ? "INFO " : "ERROR", msg);
                    allOk &= ok;
                }

                RefreshCheckStates();
                _tray.ShowBalloonTip(4000, anyDenied ? "已恢复可执行" : "已禁止执行",
                    string.Join("\n", lines), allOk ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                Log.Error($"ACL 操作失败: {ex.Message}");
            }
        });
    }

    private void RefreshCheckStates()
    {
        _updatingChecks = true;
        try
        {
            _logonTaskItem.Checked = AutoStartManager.TaskExists(AutoStartManager.LogonTaskName);
            _bootTaskItem.Checked = AutoStartManager.TaskExists(AutoStartManager.BootTaskName);

            var self = Environment.ProcessPath ?? "";
            _ifeoItem.Checked = _cfg.Targets.Any(t => Blocker.IsIfeoBlocked(t, self));

            List<string> paths;
            lock (_knownPaths) paths = new List<string>(_knownPaths);
            _aclItem.Checked = paths.Count > 0 && paths.Any(Blocker.IsExecuteDenied);
        }
        finally
        {
            _updatingChecks = false;
        }
    }

    private void UpdateTooltip()
    {
        if (_updatingChecks) return;
        var text = _paused
            ? "SeeowKiller（已暂停）"
            : _cfg.PolicyKind == InterceptPolicy.Lock
                ? $"SeeowKiller 锁屏触发中（已拦 {LockDetectedCount} 次锁屏）"
                : $"SeeowKiller（运行中，累计结束 {_killer.TotalKilled} 次）";
        // NotifyIcon.Text 上限 63 字符
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowStatusBalloon()
    {
        string body;
        if (_cfg.PolicyKind == InterceptPolicy.Lock)
        {
            var lastDetect = LastLockTime is { } t
                ? $"最近一次锁屏：{t:HH:mm:ss}（动作：{LastAction ?? "无"}）"
                : "还没检测到过锁屏";
            body = $"模式：只在检测到锁屏时拦截\n" +
                   $"判据：\"{_cfg.LockTitle}\" + {_cfg.LockClass} + {_cfg.LockProcess}\n" +
                   $"累计拦截 {LockDetectedCount} 次\n{lastDetect}\n日志：{Log.Path}";
        }
        else
        {
            var last = _killer.LastKillTime is { } t
                ? $"最近一次：{t:HH:mm:ss} {_killer.LastKilledName}"
                : "最近还没有结束过进程";
            body = $"模式：无差别结束进程\n目标：{string.Join(", ", _cfg.Targets)}\n" +
                   $"累计结束 {_killer.TotalKilled} 次\n{last}\n日志：{Log.Path}";
        }

        _tray.ShowBalloonTip(4000, _paused ? "SeeowKiller 已暂停" : "SeeowKiller 运行中", body, ToolTipIcon.Info);
    }

    private static void OpenPath(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppConfig.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开路径失败: {ex.Message}");
        }
    }

    private void ExitApp()
    {
        Log.Info("用户从托盘退出");
        _cts.Cancel();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _cts.Cancel(); } catch { }
            _uiTimer.Dispose();
            _tray.Dispose();
            _icon.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- 运行时画一个图标，免得带二进制资源 ----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(230, 30, 30));
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var pen = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 10, 10, 22, 22);
            g.DrawLine(pen, 22, 10, 10, 22);
        }

        var h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }
}
