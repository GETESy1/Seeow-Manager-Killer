using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeeowKiller;

internal enum RunMode
{
    Watch,      // 托盘 + 看门狗（默认，也是自启任务用的模式）
    Once,       // 只杀一次就退出
    Status,     // 打印状态
    Install,    // 安装自启
    Uninstall,  // 卸载自启
    Block,      // IFEO 硬拦截开关
    BlockAcl,   // ACL 禁止执行开关
    LockScan,   // 列出当前所有"像锁屏"的窗口（诊断）
    InterceptOnce, // 扫一次锁屏窗口，命中就按策略处理一次（诊断/脚本）
    IfeoStub,   // 作为假调试器被拉起（内部用）
    PrintTaskXml, // 打印计划任务 XML（排错用）
    SelfTest,   // 自检（解析逻辑、路径探测）
    Settings,   // 打开 WinUI 设置窗口（主界面）
    Help,       // 帮助
}

/// <summary>on / off / status 三段式动作。</summary>
internal enum ToggleAction
{
    On,
    Off,
    Status,
}

/// <summary>托盘图标样式。</summary>
internal enum TrayIconStyle
{
    /// <summary>内置的红圆叉图标。</summary>
    Default,

    /// <summary>全透明图标（托盘里看不见，但仍能右键操作）。</summary>
    Transparent,

    /// <summary>用户自选的 .ico 文件。</summary>
    Custom,
}

/// <summary>拦截策略。</summary>
internal enum InterceptPolicy
{
    /// <summary>只在检测到"锁屏窗口"时才动手（默认，希沃其它功能照常）。</summary>
    Lock,

    /// <summary>老行为：无差别反复结束目标进程。</summary>
    KillAll,
}

/// <summary>运行时配置：config.json 持久化，命令行参数覆盖。</summary>
internal sealed class AppConfig
{
    // ---- 持久化字段（写进 config.json）----

    /// <summary>要持续结束的进程名（带不带 .exe 都行）。</summary>
    public List<string> Targets { get; set; } = new() { "SeewoServiceAssistant.exe" };

    /// <summary>轮询间隔（毫秒）。越小越"稳"，代价是 CPU 占用。</summary>
    public int IntervalMs { get; set; } = 500;

    /// <summary>日志文件路径；null 表示不写日志。</summary>
    public string? LogPath { get; set; }

    /// <summary>被结束的进程如果属于这些名字，则不动它（保险丝，默认空）。</summary>
    public List<string> NeverKill { get; set; } = new();

    /// <summary>看门狗是否定期检查硬拦截是否被希沃的文件保护抹掉，并重新挂回去。</summary>
    public bool ReapplyBlock { get; set; } = true;

    /// <summary>硬拦截（ACL）时使用的额外文件路径（自动探测不到时手填）。</summary>
    public List<string> BlockPaths { get; set; } = new();

    // ---- 外观 / 通知 ----

    /// <summary>是否弹托盘气泡通知（关掉后托盘不再打扰，只写日志）。</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>托盘图标样式：default（内置红圆叉）/ transparent（全透明）/ custom（自定义 .ico）。</summary>
    public string TrayIconMode { get; set; } = "default";

    /// <summary>自定义托盘图标的 .ico 路径（TrayIconMode = custom 时生效）。</summary>
    public string TrayIconPath { get; set; } = "";

    // ---- 锁屏触发拦截 ----

    /// <summary>拦截策略：lock（默认，只在检测到锁屏时动手）/ kill（无差别结束目标进程）。</summary>
    public string Policy { get; set; } = "lock";

    /// <summary>检测到锁屏后的动作：kill（默认）/ hide。</summary>
    public string OnLock { get; set; } = "kill";

    /// <summary>锁屏窗口标题（精确匹配）。</summary>
    public string LockTitle { get; set; } = "希沃管家";

    /// <summary>锁屏窗口类名（包含匹配）。</summary>
    public string LockClass { get; set; } = "Chrome_WidgetWin";

    /// <summary>锁屏窗口属主进程名（包含匹配）。</summary>
    public string LockProcess { get; set; } = "SeewoServiceAssistant";

    [JsonIgnore] public RunMode Mode { get; set; } = RunMode.Watch;
    [JsonIgnore] public ToggleAction Action { get; set; } = ToggleAction.On;
    [JsonIgnore] public bool WithAcl { get; set; }
    [JsonIgnore] public bool NoDiscover { get; set; }
    [JsonIgnore] public bool BootTask { get; set; }
    [JsonIgnore] public bool UseRunKey { get; set; }
    [JsonIgnore] public bool NoElevate { get; set; }
    [JsonIgnore] public bool SaveRequested { get; set; }

    [JsonIgnore] public InterceptPolicy PolicyKind => ParsePolicy(Policy);

    [JsonIgnore] public TrayIconStyle IconStyle => ParseIconStyle(TrayIconMode);

    [JsonIgnore] public bool HideInsteadOfKill =>
        OnLock.Trim().Equals("hide", StringComparison.OrdinalIgnoreCase);

    public static InterceptPolicy ParsePolicy(string? value) => (value ?? "lock").Trim().ToLowerInvariant() switch
    {
        "kill" or "killall" or "watch" or "all" or "process" => InterceptPolicy.KillAll,
        _ => InterceptPolicy.Lock,
    };

    /// <summary>解析托盘图标样式（也接受 none/blank/hidden 等写法）。</summary>
    public static TrayIconStyle ParseIconStyle(string? value) => (value ?? "default").Trim().Trim('"').ToLowerInvariant() switch
    {
        "transparent" or "none" or "blank" or "invisible" or "hidden" or "clear" => TrayIconStyle.Transparent,
        "custom" or "file" or "icon" or "path" or "image" => TrayIconStyle.Custom,
        _ => TrayIconStyle.Default,
    };

    public static string IconStyleName(TrayIconStyle style) => style switch
    {
        TrayIconStyle.Transparent => "transparent",
        TrayIconStyle.Custom => "custom",
        _ => "default",
    };

    [JsonIgnore] public static string DataDir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeeowKiller");

    [JsonIgnore] public static string ConfigPath { get; } = System.IO.Path.Combine(DataDir, "config.json");

    [JsonIgnore] public static string DefaultLogPath { get; } = System.IO.Path.Combine(DataDir, "SeeowKiller.log");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Parse(string[] args)
    {
        var cfg = Load();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Value()
            {
                var eq = a.IndexOf('=');
                if (eq >= 0) return a[(eq + 1)..].Trim('"');
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    i++;
                    return args[i].Trim('"');
                }
                return null;
            }

            var key = a.Split('=')[0].ToLowerInvariant();
            switch (key)
            {
                case "--watch":
                    cfg.Mode = RunMode.Watch;
                    break;
                case "--once":
                    cfg.Mode = RunMode.Once;
                    break;
                case "--status":
                    cfg.Mode = RunMode.Status;
                    break;
                case "--install":
                    cfg.Mode = RunMode.Install;
                    break;
                case "--uninstall":
                    cfg.Mode = RunMode.Uninstall;
                    break;
                case "--print-task-xml":
                    cfg.Mode = RunMode.PrintTaskXml;
                    break;
                case "--ifeo-stub":
                    cfg.Mode = RunMode.IfeoStub;
                    break;
                case "--selftest":
                    cfg.Mode = RunMode.SelfTest;
                    break;
                case "--block":
                    cfg.Mode = RunMode.Block;
                    cfg.Action = ParseToggle(Value());
                    break;
                case "--block-acl":
                    cfg.Mode = RunMode.BlockAcl;
                    cfg.Action = ParseToggle(Value());
                    break;
                case "--with-acl":
                    cfg.WithAcl = true;
                    break;
                case "--no-discover":
                    cfg.NoDiscover = true;
                    break;
                case "--policy":
                    cfg.Policy = (Value() ?? "lock").Trim().ToLowerInvariant();
                    break;
                case "--on-lock":
                    cfg.OnLock = (Value() ?? "kill").Trim().ToLowerInvariant();
                    break;
                case "--lock-title":
                    cfg.LockTitle = Value() ?? cfg.LockTitle;
                    break;
                case "--lock-class":
                    cfg.LockClass = Value() ?? cfg.LockClass;
                    break;
                case "--lock-process":
                    cfg.LockProcess = Value() ?? cfg.LockProcess;
                    break;
                case "--lockscan":
                    cfg.Mode = RunMode.LockScan;
                    break;
                case "--intercept-once":
                    cfg.Mode = RunMode.InterceptOnce;
                    break;
                case "--save":
                    cfg.SaveRequested = true;
                    break;
                case "--block-path":
                    var bp = Value();
                    if (!string.IsNullOrWhiteSpace(bp)) cfg.BlockPaths.Add(bp.Trim('"'));
                    break;
                case "--no-reapply":
                    cfg.ReapplyBlock = false;
                    break;
                case "--help" or "-h" or "/?":
                    cfg.Mode = RunMode.Help;
                    break;
                case "--boot":
                    cfg.BootTask = true;
                    break;
                case "--install-run":
                    cfg.Mode = RunMode.Install;
                    cfg.UseRunKey = true;
                    break;
                case "--uninstall-run":
                    cfg.Mode = RunMode.Uninstall;
                    cfg.UseRunKey = true;
                    break;
                case "--no-elevate":
                    cfg.NoElevate = true;
                    break;
                case "--settings":
                case "--ui":
                    cfg.Mode = RunMode.Settings;
                    break;
                case "--notifications":
                    cfg.Notifications = ParseToggle(Value()) != ToggleAction.Off;
                    break;
                case "--tray-icon":
                    cfg.TrayIconMode = (Value() ?? "default").Trim().ToLowerInvariant();
                    break;
                case "--tray-icon-path":
                    var iconPath = Value();
                    if (!string.IsNullOrWhiteSpace(iconPath))
                    {
                        cfg.TrayIconPath = iconPath.Trim('"');
                        cfg.TrayIconMode = "custom";
                    }
                    break;
                case "--interval":
                case "--intervalms":
                    if (int.TryParse(Value(), out var ms)) cfg.IntervalMs = Math.Clamp(ms, 50, 60000);
                    break;
                case "--targets":
                    var list = (Value() ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    if (list.Count > 0) cfg.Targets = list;
                    break;
                case "--never-kill":
                    var never = (Value() ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                    cfg.NeverKill.AddRange(never);
                    break;
                case "--log":
                    var p = Value();
                    if (!string.IsNullOrWhiteSpace(p)) cfg.LogPath = System.IO.Path.GetFullPath(p);
                    break;
                case "--no-log":
                    cfg.LogPath = null;
                    break;
            }
        }

        cfg.LogPath ??= DefaultLogPath;
        return cfg;
    }

    /// <summary>解析 on/off/status（也接受 enable/disable/1/0/query 等写法）。</summary>
    public static ToggleAction ParseToggle(string? value)
    {
        var v = (value ?? "on").Trim().Trim('"').ToLowerInvariant();
        return v switch
        {
            "off" or "disable" or "0" or "false" or "no" or "remove" or "clear" => ToggleAction.Off,
            "status" or "query" or "show" or "check" => ToggleAction.Status,
            _ => ToggleAction.On,
        };
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (loaded is not null)
                {
                    if (loaded.Targets.Count == 0) loaded.Targets = new() { "SeewoServiceAssistant.exe" };
                    loaded.IntervalMs = Math.Clamp(loaded.IntervalMs, 50, 60000);
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，不阻塞启动
        }

        return new AppConfig();
    }

    /// <summary>把外观字段规整成合法值（配置手改错、旧版本升级时用）。</summary>
    public void Normalize()
    {
        var style = ParseIconStyle(TrayIconMode);
        if (style == TrayIconStyle.Custom && string.IsNullOrWhiteSpace(TrayIconPath))
            style = TrayIconStyle.Default;

        TrayIconMode = IconStyleName(style);
        TrayIconPath = TrayIconPath?.Trim() ?? "";
        Policy = PolicyKind == InterceptPolicy.KillAll ? "kill" : "lock";
        OnLock = HideInsteadOfKill ? "hide" : "kill";
    }

    /// <summary>首次运行时落一份默认配置，方便手改。</summary>
    public void SaveIfMissing()
    {
        if (File.Exists(ConfigPath)) return;
        Save();
    }

    /// <summary>写回配置（托盘里改强制开关/策略、或命令行 --save 时用）。</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts), System.Text.Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"保存配置失败: {ex.Message}");
            return false;
        }
    }
}
