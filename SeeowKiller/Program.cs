using System.Diagnostics;
using System.Windows.Forms;

namespace SeeowKiller;

internal static class SingleInstance
{
    private const string GlobalName = @"Global\SeeowKiller.SingleInstance";
    private const string LocalName = @"Local\SeeowKiller.SingleInstance";
    private static Mutex? _mutex;

    /// <summary>
    /// 单实例。注意：普通用户没有 SeCreateGlobalPrivilege，创建 Global\ 互斥体会抛
    /// UnauthorizedAccessException —— 这时不能直接判定"已有实例"（那会让程序永远起不来），
    /// 先看看是不是真有人占着名字，没有就退化成 Local\。
    /// </summary>
    public static bool Acquire()
    {
        if (TryCreate(GlobalName, out var created))
            return created;

        // Global 拿不到：区分"名字被占"和"权限不够"
        try
        {
            using var existing = Mutex.OpenExisting(GlobalName);
            Log.Warn("检测到已有 SeeowKiller 实例（Global 互斥体被占用），本实例退出");
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log.Warn("无权限创建 Global 互斥体，退化为本会话单实例检查");
        }
        catch
        {
            // 其它异常（例如权限同样不足）也走退化路径
        }

        if (TryCreate(LocalName, out var localCreated))
            return localCreated;

        Log.Warn("互斥体不可用，跳过单实例检查");
        return true;
    }

    private static bool TryCreate(string name, out bool createdNew)
    {
        createdNew = false;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            // 任何其它异常都不该挡住看门狗干活
            createdNew = true;
            return true;
        }
    }
}

internal static class Program
{
    private const string Version = "1.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        var cfg = AppConfig.Parse(args);
        ConsoleBridge.TryAttach();

        // 作为 IFEO 假调试器被拉起：记一笔就走，绝不提权、绝不弹窗
        if (cfg.Mode == RunMode.IfeoStub)
            return CmdIfeoStub(args);

        switch (cfg.Mode)
        {
            case RunMode.Help:
                PrintHelp();
                return 0;
            case RunMode.Status:
                Log.Init(cfg.LogPath);
                return CmdStatus(cfg);
            case RunMode.Once:
                Log.Init(cfg.LogPath);
                EnsureAdminOrContinue(args, cfg, needAdmin: true);
                return CmdOnce(cfg);
            case RunMode.Install:
                Log.Init(cfg.LogPath);
                EnsureAdminOrContinue(args, cfg, needAdmin: !cfg.UseRunKey);
                return CmdInstall(cfg, install: true);
            case RunMode.Uninstall:
                Log.Init(cfg.LogPath);
                EnsureAdminOrContinue(args, cfg, needAdmin: !cfg.UseRunKey);
                return CmdInstall(cfg, install: false);
            case RunMode.Block:
                Log.Init(cfg.LogPath);
                EnsureAdminOrContinue(args, cfg, needAdmin: cfg.Action != ToggleAction.Status);
                return CmdBlock(cfg);
            case RunMode.BlockAcl:
                Log.Init(cfg.LogPath);
                EnsureAdminOrContinue(args, cfg, needAdmin: cfg.Action != ToggleAction.Status);
                return CmdBlockAcl(cfg);
            case RunMode.PrintTaskXml:
                return CmdPrintTaskXml();
            case RunMode.LockScan:
                Log.Init(cfg.LogPath);
                return CmdLockScan(cfg);
            case RunMode.InterceptOnce:
                Log.Init(cfg.LogPath);
                EnsureAdminOrContinue(args, cfg, needAdmin: true);
                return CmdInterceptOnce(cfg);
            case RunMode.SelfTest:
                return CmdSelfTest(cfg);
        }

        // 默认：托盘 + 看门狗
        Log.Init(cfg.LogPath);
        cfg.SaveIfMissing();
        if (cfg.SaveRequested) cfg.Save();

        if (!Elevation.IsAdmin && !cfg.NoElevate)
        {
            if (Elevation.TryRelaunchElevated(args))
                return 0; // 交给提权后的实例
            Log.Warn("未取得管理员权限，仍以当前权限继续（可能无法结束高权限的希沃进程）");
        }

        if (!SingleInstance.Acquire())
        {
            Console.WriteLine("SeeowKiller 已经在运行了。");
            Log.Warn("已有实例在运行，本实例退出");
            return 0;
        }

        if (Elevation.EnableDebugPrivilege())
            Log.Info("已启用 SeDebugPrivilege");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error($"界面线程异常: {e.Exception}");

        using var app = new TrayApp(cfg);
        Application.Run(app);
        return 0;
    }

    /// <summary>不满足权限时尝试提权重启自己（提权失败则继续跑）。</summary>
    private static void EnsureAdminOrContinue(string[] args, AppConfig cfg, bool needAdmin)
    {
        if (!needAdmin || cfg.NoElevate || Elevation.IsAdmin) return;
        if (Elevation.TryRelaunchElevated(args))
        {
            Log.Info("已启动提权实例，本实例退出");
            Environment.Exit(0);
        }
        Log.Warn("未取得管理员权限，操作可能失败");
    }

    private static int CmdOnce(AppConfig cfg)
    {
        var killer = new ProcessKiller(cfg);
        int n = killer.KillOnce();
        Console.WriteLine(n > 0 ? $"已结束 {n} 个进程。" : "没有发现正在运行的目标进程。");
        Console.WriteLine($"目标: {string.Join(", ", cfg.Targets)}");
        Console.WriteLine($"管理员: {Elevation.IsAdmin}");
        Console.WriteLine($"日志: {Log.Path}");
        return 0;
    }

    private static int CmdInstall(AppConfig cfg, bool install)
    {
        var exe = Environment.ProcessPath ?? "";
        var results = new List<string>();

        if (cfg.UseRunKey)
        {
            string msg;
            if (install) AutoStartManager.InstallRunKey(exe, out msg);
            else AutoStartManager.UninstallRunKey(out msg);
            results.Add(msg);
        }
        else
        {
            if (install)
            {
                AutoStartManager.CreateLogonTask(exe, out var msgLogon);
                results.Add(msgLogon);
                if (cfg.BootTask)
                {
                    AutoStartManager.CreateBootTask(exe, out var msgBoot);
                    results.Add(msgBoot);
                }
            }
            else
            {
                AutoStartManager.DeleteTask(AutoStartManager.LogonTaskName, out var msgLogon);
                results.Add(msgLogon);
                AutoStartManager.DeleteTask(AutoStartManager.BootTaskName, out var msgBoot);
                results.Add(msgBoot);

                // 一键全清：硬拦截也一起撤掉，别留一个"目标永远起不来"的坑
                if (Blocker.ClearIfeo(cfg.Targets, Environment.ProcessPath, out var ifeoMessages))
                    results.AddRange(ifeoMessages.Select(m => "IFEO " + m));
                else
                    results.AddRange(ifeoMessages.Select(m => "IFEO " + m + "（可能需要管理员）"));

                foreach (var path in ResolvePaths(cfg))
                {
                    if (Blocker.IsExecuteDenied(path) && Blocker.RemoveDenyExecute(path, out var aclMsg))
                        results.Add("ACL " + aclMsg);
                }
            }
        }

        Console.WriteLine(install ? "== 安装自启 ==" : "== 卸载自启 ==");
        foreach (var r in results)
        {
            Console.WriteLine("  " + r);
            Log.Info(r);
        }

        Console.WriteLine();
        Console.WriteLine($"程序路径: {exe}");
        Console.WriteLine($"管理员: {Elevation.IsAdmin}");
        Console.WriteLine("提示：计划任务在登录/开机时以最高权限静默启动，不会弹 UAC。");
        return 0;
    }

    /// <summary>
    /// 作为 IFEO 假调试器被 Windows 拉起。此时目标进程已被挂起，
    /// 我们只要立刻退出，那个进程就永远不会真正运行 —— 启动方也收不到任何错误。
    /// </summary>
    private static int CmdIfeoStub(string[] args)
    {
        var target = args.FirstOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("--"))
                     ?? "(未知映像)";
        Log.Init(AppConfig.DefaultLogPath);
        Log.InfoThrottled($"硬拦截命中：拒绝了 {target} 的一次启动", quietSeconds: 5);
        return 0;
    }

    private static int CmdBlock(AppConfig cfg)
    {
        var images = cfg.Targets;

        if (cfg.Action == ToggleAction.Status)
        {
            Console.WriteLine("== IFEO 硬拦截状态 ==");
            foreach (var image in images)
                Console.WriteLine($"  {image}: {Blocker.DescribeIfeo(image)}");
            Console.WriteLine();
            Console.WriteLine($"  本程序: {Blocker.BuildDebuggerCommand()}");
            return 0;
        }

        var messages = new List<string>();
        bool ok;

        if (cfg.Action == ToggleAction.On)
        {
            var command = Blocker.BuildDebuggerCommand();
            ok = Blocker.ApplyIfeo(images, command, out messages);

            Console.WriteLine("== 开启 IFEO 硬拦截（目标 exe 将无法启动）==");
            Console.WriteLine($"  假调试器命令: {command}");
        }
        else
        {
            ok = Blocker.ClearIfeo(images, Environment.ProcessPath, out messages);
            Console.WriteLine("== 关闭 IFEO 硬拦截 ==");
        }

        foreach (var m in messages)
        {
            Console.WriteLine("  " + m);
            Log.Write(ok ? "INFO " : "ERROR", m);
        }

        // 顺带处理 ACL（--with-acl）
        if (cfg.WithAcl)
        {
            Console.WriteLine();
            Console.WriteLine(cfg.Action == ToggleAction.On
                ? "== 同时开启 ACL 禁止执行 =="
                : "== 同时关闭 ACL 禁止执行 ==");
            ApplyAcl(cfg, cfg.Action == ToggleAction.On);
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "完成。" : "有部分操作失败，请看上面的原因（多半是权限或被希沃的文件保护拦住）。");
        return ok ? 0 : 1;
    }

    private static int CmdBlockAcl(AppConfig cfg)
    {
        if (cfg.Action == ToggleAction.Status)
        {
            Console.WriteLine("== ACL 禁止执行状态 ==");
            var paths = ResolvePaths(cfg);
            if (paths.Count == 0)
            {
                Console.WriteLine("  没找到目标 exe 的路径（可用 --block-path 手动指定；或先让它跑起来再查）");
            }
            else
            {
                foreach (var path in paths)
                    Console.WriteLine($"  {(Blocker.IsExecuteDenied(path) ? "[已禁止执行]" : "[可执行]    ")} {path}");
            }
            return 0;
        }

        Console.WriteLine(cfg.Action == ToggleAction.On ? "== 开启 ACL 禁止执行 ==" : "== 关闭 ACL 禁止执行 ==");
        bool ok = ApplyAcl(cfg, cfg.Action == ToggleAction.On);
        return ok ? 0 : 1;
    }

    /// <summary>对探测到的所有目标 exe 应用/撤销"拒绝执行"。</summary>
    private static bool ApplyAcl(AppConfig cfg, bool deny)
    {
        var paths = ResolvePaths(cfg);
        if (paths.Count == 0)
        {
            Console.WriteLine("  没找到目标 exe 的路径，跳过 ACL 这一步。");
            Console.WriteLine("  提示：先让它启动一次，或用 --block-path=\"C:\\...\\SeewoServiceAssistant.exe\" 指定。");
            return false;
        }

        bool allOk = true;
        foreach (var path in paths)
        {
            string msg;
            bool ok = deny
                ? Blocker.DenyExecute(path, out msg)
                : Blocker.RemoveDenyExecute(path, out msg);
            Console.WriteLine("  " + msg);
            Log.Write(ok ? "INFO " : "ERROR", msg);
            allOk &= ok;
        }

        if (deny)
            Console.WriteLine("  注意：拒绝执行会让希沃自带的修复/升级也一起失败，需要时用 --block-acl off 撤销。");

        return allOk;
    }

    private static List<string> ResolvePaths(AppConfig cfg)
    {
        var paths = new List<string>(cfg.BlockPaths);
        if (cfg.NoDiscover) return paths;

        foreach (var p in Blocker.DiscoverPaths(cfg.Targets))
        {
            if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        return paths;
    }

    /// <summary>列出当前所有"像锁屏"的窗口，方便确认识别判据对不对（只读）。</summary>
    private static int CmdLockScan(AppConfig cfg)
    {
        Console.WriteLine("== 锁屏窗口扫描 ==");
        Console.WriteLine($"  判据: 标题=\"{cfg.LockTitle}\"  类名含\"{cfg.LockClass}\"  进程含\"{cfg.LockProcess}\"");
        Console.WriteLine("       且必须【可见 + 置顶 + 全屏】（容差 100px）");
        Console.WriteLine();

        var all = LockDetector.Scan(cfg, onlyMatches: false);
        if (all.Count == 0)
        {
            Console.WriteLine("  当前没有任何「可见 + 置顶 + 全屏」的候选窗口。");
            Console.WriteLine("  （没锁屏时这是正常的；如果刚被锁屏却看不到，说明判据要调）");
            return 0;
        }

        foreach (var w in all)
        {
            Console.WriteLine($"  [{(w.IsLockCandidate ? "命中锁屏" : "候选    ")}] {w.Describe()}");
        }

        var matched = all.Count(w => w.IsLockCandidate);
        Console.WriteLine();
        Console.WriteLine($"  候选 {all.Count} 个，其中命中锁屏判据的 {matched} 个。");
        return 0;
    }

    /// <summary>扫一次，命中就处理一次（按 --on-lock）。给脚本/测试用。</summary>
    private static int CmdInterceptOnce(AppConfig cfg)
    {
        var locks = LockDetector.Scan(cfg, onlyMatches: true);
        if (locks.Count == 0)
        {
            Console.WriteLine("没有检测到锁屏窗口。");
            return 0;
        }

        int done = 0;
        foreach (var w in locks)
        {
            Console.WriteLine($"检测到锁屏：{w.Describe()}");

            bool ok;
            if (cfg.HideInsteadOfKill)
            {
                ok = LockDetector.TryHide(w);
                Console.WriteLine(ok ? "  已尝试隐藏该窗口" : "  隐藏失败（多半权限不够）");
                if (ok)
                {
                    Thread.Sleep(1200);
                    ok = !LockDetector.Scan(cfg, onlyMatches: true).Any(x => x.Hwnd == w.Hwnd);
                    Console.WriteLine(ok ? "  窗口已消失" : "  窗口还在，改为结束进程");
                }
                if (!ok) ok = LockDetector.KillOwner(w);
            }
            else
            {
                ok = LockDetector.KillOwner(w);
                Console.WriteLine(ok ? $"  已结束 {w.ProcessName} (PID {w.Pid})" : "  结束失败");
            }

            if (ok) done++;
        }

        Console.WriteLine($"处理了 {done} 个锁屏窗口。日志：{Log.Path}");
        return done > 0 ? 0 : 1;
    }

    /// <summary>自检：把不依赖权限的纯逻辑跑一遍（解析、探测），方便改代码后快速回归。</summary>
    private static int CmdSelfTest(AppConfig cfg)
    {
        int failed = 0;
        void Check(string name, bool actual, bool expected)
        {
            var ok = actual == expected;
            if (!ok) failed++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} => {actual}（期望 {expected}）");
        }

        Console.WriteLine("== 1) icacls 输出解析（(X) 会被打成 (S,X)，必须认出来）==");
        Check("Everyone:(DENY)(S,X)", Blocker.LineDeniesExecute(@"C:\x.exe Everyone:(DENY)(S,X)"), true);
        Check("Everyone:(DENY)(X)", Blocker.LineDeniesExecute(@"C:\x.exe Everyone:(DENY)(X)"), true);
        Check("X:(DENY)(F)", Blocker.LineDeniesExecute(@"C:\x.exe X:(DENY)(F)"), true);
        Check("X:(DENY)(RX)", Blocker.LineDeniesExecute(@"C:\x.exe X:(DENY)(RX)"), true);
        Check("Everyone:(DENY)(W) 不是执行", Blocker.LineDeniesExecute(@"C:\x.exe Everyone:(DENY)(W)"), false);
        Check("Everyone:(DENY)(S,RD) 不是执行", Blocker.LineDeniesExecute(@"C:\x.exe Everyone:(DENY)(S,RD)"), false);
        Check("普通 (RX) 允许", Blocker.LineDeniesExecute(@"C:\x.exe BUILTIN\Users:(I)(RX)"), false);
        Check("空行", Blocker.LineDeniesExecute(""), false);

        Console.WriteLine();
        Console.WriteLine("== 2) IFEO 假调试器命令 ==");
        Console.WriteLine("  " + Blocker.BuildDebuggerCommand());
        foreach (var image in cfg.Targets)
            Console.WriteLine($"  {image}: {Blocker.DescribeIfeo(image)}");

        Console.WriteLine();
        Console.WriteLine("== 3) 目标 exe 路径探测 ==");
        var paths = Blocker.DiscoverPaths(cfg.Targets);
        if (paths.Count == 0) Console.WriteLine("  （没找到，可能本机没装希沃管家）");
        foreach (var p in paths)
            Console.WriteLine($"  {(Blocker.IsExecuteDenied(p) ? "[已禁止执行]" : "[可执行]    ")} {p}");

        Console.WriteLine();
        Console.WriteLine("== 4) 环境 ==");
        Console.WriteLine($"  管理员: {Elevation.IsAdmin}");
        Console.WriteLine($"  SeDebugPrivilege 已启用: {Elevation.EnableDebugPrivilege()}");

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "自检全部通过。" : $"自检有 {failed} 项失败。");
        return failed == 0 ? 0 : 1;
    }

    private static string DescribePolicy(AppConfig cfg) => cfg.PolicyKind switch
    {
        InterceptPolicy.Lock => "lock —— 只在检测到锁屏窗口时拦截（默认）",
        _ => "kill —— 无差别反复结束目标进程",
    };

    private static int CmdPrintTaskXml()
    {
        var exe = Environment.ProcessPath ?? "";
        Console.WriteLine("=== 登录任务 XML ===");
        Console.WriteLine(AutoStartManager.BuildLogonTaskXml(exe));
        Console.WriteLine();
        Console.WriteLine("=== 开机任务 XML ===");
        Console.WriteLine(AutoStartManager.BuildBootTaskXml(exe));
        return 0;
    }

    private static int CmdStatus(AppConfig cfg)
    {
        var killer = new ProcessKiller(cfg);
        var running = killer.DescribeRunningTargets();

        Console.WriteLine($"SeeowKiller v{Version}");
        Console.WriteLine($"  程序路径   : {Environment.ProcessPath}");
        Console.WriteLine($"  管理员权限 : {Elevation.IsAdmin}");
        Console.WriteLine($"  目标进程   : {string.Join(", ", cfg.Targets)}");
        Console.WriteLine($"  拦截策略   : {DescribePolicy(cfg)}");
        Console.WriteLine($"  锁屏判据   : 标题=\"{cfg.LockTitle}\" 类名含\"{cfg.LockClass}\" 进程含\"{cfg.LockProcess}\"");
        Console.WriteLine($"  命中动作   : {(cfg.HideInsteadOfKill ? "hide（先隐藏，失败才杀进程）" : "kill（直接结束进程）")}");
        Console.WriteLine($"  轮询间隔   : {cfg.IntervalMs} ms");
        Console.WriteLine($"  日志文件   : {Log.Path}");
        Console.WriteLine($"  配置文件   : {AppConfig.ConfigPath}");
        Console.WriteLine($"  Run 键自启 : {(AutoStartManager.RunKeyInstalled() ? "已安装" : "未安装")}");
        Console.WriteLine($"  登录任务   : {(AutoStartManager.TaskExists(AutoStartManager.LogonTaskName) ? "已安装" : "未安装")}  ({AutoStartManager.LogonTaskName})");
        Console.WriteLine($"  开机任务   : {(AutoStartManager.TaskExists(AutoStartManager.BootTaskName) ? "已安装" : "未安装")}  ({AutoStartManager.BootTaskName})");
        Console.WriteLine();
        Console.WriteLine("  ---- 硬拦截 ----");
        var self = Environment.ProcessPath ?? "";
        foreach (var image in cfg.Targets)
        {
            var blocked = Blocker.IsIfeoBlocked(image, self);
            Console.WriteLine($"  IFEO       : {(blocked ? "[已拦截·无法启动]" : "[未拦截]")} {image}");
        }

        var aclPaths = ResolvePaths(cfg);
        if (aclPaths.Count == 0)
        {
            Console.WriteLine("  ACL        : [无目标 exe 路径可检查]");
        }
        else
        {
            foreach (var path in aclPaths)
                Console.WriteLine($"  ACL        : {(Blocker.IsExecuteDenied(path) ? "[已禁止执行]" : "[可执行]    ")} {path}");
        }
        Console.WriteLine();
        if (running.Count == 0)
        {
            Console.WriteLine("当前没有发现目标进程在运行。");
        }
        else
        {
            Console.WriteLine("当前正在运行的目标进程：");
            foreach (var line in running) Console.WriteLine("  " + line);
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
SeeowKiller v{Version} —— 持续结束希沃管家（SeewoServiceAssistant.exe）的看门狗

用法:
  SeeowKiller.exe [选项]

不带参数（或 --watch）:
  进入托盘常驻模式：按间隔反复结束目标进程；托盘右键可暂停/立即结束/装自启/退出。

一次性与查询:
  --once                     立刻结束一次目标进程然后退出（无差别模式）
  --status                   查看权限、策略、锁屏判据、自启状态、硬拦截状态
  --lockscan                 列出当前所有"可见+置顶+全屏"的窗口及命中情况（只读，调判据用）
  --intercept-once           扫一次锁屏窗口，命中就按 --on-lock 处理一次然后退出

拦截策略（默认只在锁屏时动手）:
  --policy=lock              默认：只在检测到锁屏窗口时拦截，希沃其它功能不受影响
  --policy=kill              老行为：无差别反复结束目标进程
  --on-lock=kill             命中锁屏后：结束该窗口的属主进程（默认）
  --on-lock=hide             命中锁屏后：先尝试隐藏该窗口，藏不住再杀进程
  --lock-title=希沃管家       锁屏窗口标题（精确匹配）
  --lock-class=Chrome_WidgetWin   锁屏窗口类名（包含匹配）
  --lock-process=SeewoServiceAssistant  锁屏窗口属主进程（包含匹配）
  --save                     把当前参数写进配置文件持久化

硬拦截（不让它启动，需要管理员）:
  --block on                 开：给目标 exe 挂 IFEO 假调试器，从此它启动即失败（静默，无报错弹窗）
  --block on --with-acl      开：再加一层 icacls 禁止执行（更硬，但希沃自修复也会被挡住）
  --block status             看：当前 IFEO 挂的是什么
  --block off                关：撤销 IFEO，并还原被覆盖的原值
  --block-acl on|off|status  单独管 ACL 那一层
  --block-path="C:\...\X.exe" 手动指定目标 exe 路径（自动探测不到时用）
  --no-discover              只用 --block-path 给的路径，不做自动探测（精准打击）
  --no-reapply               不让看门狗定期把被抹掉的拦截重新挂回去

开机自启:
  --install                  注册登录时计划任务（最高权限，静默启动，推荐）
  --install --boot           同时注册开机时 SYSTEM 计划任务
  --install-run              兜底方案：写 HKCU\...\Run（每次登录会弹一次 UAC）
  --uninstall                删除计划任务 + Run 键 + 撤销硬拦截（一键全清）
  --uninstall-run            只删 Run 键项
  --print-task-xml           打印将写入的计划任务 XML（排错/审计用）

微调:
  --interval=500             轮询间隔毫秒（50~60000，默认 500）
  --targets=a.exe,b.exe      要结束的进程名（默认 SeewoServiceAssistant.exe）
                             例如: --targets=SeewoServiceAssistant.exe,SeewoCore.exe
  --never-kill=xxx.exe       永不结束的进程名（保险丝，可重复指定）
  --log=D:\path\app.log      指定日志文件
  --no-log                   不写日志
  --no-elevate               不自动提权（以当前权限运行）
  --help                     显示本帮助

配置文件:
  {AppConfig.ConfigPath}
  （首次运行自动生成，改完重启程序生效；命令行参数优先级更高）

注意: 本程序是 GUI 子系统的 exe，PowerShell 里直接 `SeeowKiller.exe --status`
      不会等它也不会捕获输出，请用 `--status > out.txt` 后读文件，
      或 `Start-Process -Wait -RedirectStandardOutput out.txt`。
""");
    }
}
