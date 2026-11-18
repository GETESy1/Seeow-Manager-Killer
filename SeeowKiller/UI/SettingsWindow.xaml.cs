using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace SeeowKiller.UI;

/// <summary>
/// 主界面：WinUI 3 设置窗口。改设置即生效（同时写回 config.json），
/// 托盘那边通过 TrayHost / TrayApp 的跨线程入口更新图标和菜单。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;

    /// <summary>真正退出程序时置 true，让窗口可以正常关闭（而不是被拦成隐藏）。</summary>
    public static bool AllowClose { get; set; }

    private readonly AppConfig _cfg;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();

        _cfg = TrayHost.Config ?? AppConfig.Load();
        Title = "SeeowKiller 设置";

        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(600, 880));
        }
        catch (Exception ex)
        {
            Log.Warn($"调整窗口尺寸失败: {ex.Message}");
        }

        VersionText.Text = $"SeeowKiller v{Program.Version} · 自启状态会实时刷新";
        ConfigPathText.Text = $"配置文件：{AppConfig.ConfigPath}";

        _loading = true;
        LoadSettings();

        // RadioButtons 的 SelectionChanged 会在首次布局后再补一次，必须等布局完成才解锁，
        // 否则一打开窗口就会触发"保存"，把默认值写回配置。
        RootGrid.Loaded += (_, _) => _loading = false;

        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        // WinUI 3 在“最后一个窗口关闭”时会把整个应用退出。托盘程序不能这样：
        // 拦下关闭动作改成隐藏窗口，程序继续在托盘里干活。
        // （真正退出程序时 TrayHost 会先把 AllowClose 打开，让窗口正常关掉。）
        AppWindow.Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            AppWindow.Hide();
        };

        Closed += OnWindowClosed;
        RefreshStatus();
        UpdatePreview();
    }

    /// <summary>单例：重复打开就激活已有窗口。</summary>
    public static void ShowOrActivate()
    {
        if (_current is null)
        {
            _current = new SettingsWindow();
            _current.Closed += (_, _) => _current = null;
        }

        try { _current.AppWindow.Show(); } catch { }
        _current.Activate();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
    }

    // ---- 加载 / 刷新 ----

    private void LoadSettings()
    {
        NotificationsSwitch.IsOn = _cfg.Notifications;
        IconStyleSelector.SelectedIndex = (int)_cfg.IconStyle;
        PickIconButton.IsEnabled = _cfg.IconStyle == TrayIconStyle.Custom;

        PolicyCombo.SelectedIndex = _cfg.PolicyKind == InterceptPolicy.KillAll ? 1 : 0;
        OnLockCombo.SelectedIndex = _cfg.HideInsteadOfKill ? 1 : 0;

        LogonTaskSwitch.IsOn = TrayHost.Tray?.LogonTaskInstalled
                               ?? AutoStartManager.TaskExists(AutoStartManager.LogonTaskName);
        BootTaskSwitch.IsOn = TrayHost.Tray?.BootTaskInstalled
                              ?? AutoStartManager.TaskExists(AutoStartManager.BootTaskName);
    }

    private void RefreshStatus()
    {
        var tray = TrayHost.Tray;
        if (tray is null)
        {
            StatusText.Text = "托盘未启动";
            PolicyText.Text = "—";
            CountText.Text = "—";
            LastActionText.Text = "—";
            return;
        }

        StatusText.Text = tray.IsPaused ? "已暂停拦截" : "运行中（看门狗已就绪）";
        PolicyText.Text = _cfg.PolicyKind == InterceptPolicy.KillAll
            ? "无差别结束目标进程"
            : $"只在「{_cfg.LockTitle}」锁屏窗口出现时动手";

        CountText.Text = _cfg.PolicyKind == InterceptPolicy.KillAll
            ? $"结束进程 {tray.KilledCount} 次"
            : $"拦截锁屏 {tray.DetectedLockCount} 次";

        LastActionText.Text = tray.LastLockAt is { } time
            ? $"{tray.LastActionName ?? "无动作"} · {time:HH:mm:ss}"
            : "还没有触发过";

        if (PauseSwitch.IsOn != tray.IsPaused)
        {
            _loading = true;
            PauseSwitch.IsOn = tray.IsPaused;
            _loading = false;
        }
    }

    private void UpdatePreview()
    {
        try
        {
            var style = _cfg.IconStyle;
            PreviewDefault.Visibility = style == TrayIconStyle.Default ? Visibility.Visible : Visibility.Collapsed;
            PreviewTransparent.Visibility = style == TrayIconStyle.Transparent ? Visibility.Visible : Visibility.Collapsed;
            PreviewCustom.Visibility = style == TrayIconStyle.Custom ? Visibility.Visible : Visibility.Collapsed;

            IconPathText.Text = style switch
            {
                TrayIconStyle.Custom => string.IsNullOrWhiteSpace(_cfg.TrayIconPath)
                    ? "还没有选择文件（当前显示默认图标）"
                    : _cfg.TrayIconPath,
                TrayIconStyle.Transparent => "透明图标：托盘区看不到图标，但右键菜单照常可用。",
                _ => "内置图标（红圆叉），不需要文件。",
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"图标预览失败: {ex.Message}");
        }
    }

    // ---- 各开关的处理 ----

    private void OnNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (_cfg.Notifications == NotificationsSwitch.IsOn) return;

        _cfg.Notifications = NotificationsSwitch.IsOn;
        PersistAndApply(quiet: true);
        FooterHint.Text = _cfg.Notifications ? "通知已开启" : "通知已关闭（只写日志）";
    }

    private void OnPauseToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var tray = TrayHost.Tray;
        if (tray is null)
        {
            Revert(PauseSwitch, !PauseSwitch.IsOn);
            return;
        }

        if (tray.IsPaused == PauseSwitch.IsOn) return;

        tray.SetPaused(PauseSwitch.IsOn);
        FooterHint.Text = PauseSwitch.IsOn ? "已暂停拦截" : "已恢复拦截";
    }

    private void OnIconStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var style = (TrayIconStyle)Math.Clamp(IconStyleSelector.SelectedIndex, 0, 2);
        if (style == _cfg.IconStyle) return;
        _cfg.TrayIconMode = AppConfig.IconStyleName(style);
        PickIconButton.IsEnabled = style == TrayIconStyle.Custom;

        if (style == TrayIconStyle.Custom && string.IsNullOrWhiteSpace(_cfg.TrayIconPath))
            ShowMessage("选「自定义」后点下面的按钮挑一个 .ico；不选的话仍显示默认图标。", InfoBarSeverity.Warning);

        PersistAndApply(quiet: true);
    }

    private async void OnPickIconClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add(".ico");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            _cfg.TrayIconPath = file.Path;
            _cfg.TrayIconMode = AppConfig.IconStyleName(TrayIconStyle.Custom);

            _loading = true;
            IconStyleSelector.SelectedIndex = (int)TrayIconStyle.Custom;
            PickIconButton.IsEnabled = true;
            _loading = false;

            PersistAndApply(quiet: true);
            ShowMessage($"已选用自定义图标：{Path.GetFileName(file.Path)}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error($"选择自定义图标失败: {ex}");
            ShowMessage($"选择图标失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var tag = (PolicyCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "lock";
        if (string.Equals(_cfg.Policy, tag, StringComparison.OrdinalIgnoreCase)) return;

        _cfg.Policy = tag;
        PersistAndApply(quiet: true);
    }

    private void OnOnLockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var tag = (OnLockCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "kill";
        if (string.Equals(_cfg.OnLock, tag, StringComparison.OrdinalIgnoreCase)) return;

        _cfg.OnLock = tag;
        PersistAndApply(quiet: true);
    }

    private void OnLogonTaskToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var want = LogonTaskSwitch.IsOn;
        var tray = TrayHost.Tray;
        if (tray is null)
        {
            Revert(LogonTaskSwitch, !want);
            return;
        }

        if (tray.LogonTaskInstalled == want) return;

        if (tray.SetLogonTask(want))
        {
            ShowMessage(want ? "已开启登录自启。" : "已关闭登录自启。", InfoBarSeverity.Success);
        }
        else
        {
            Revert(LogonTaskSwitch, !want);
            ShowMessage("改登录自启失败（多半是权限不够，用管理员身份运行再试）。", InfoBarSeverity.Error);
        }
    }

    private void OnBootTaskToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var want = BootTaskSwitch.IsOn;
        var tray = TrayHost.Tray;
        if (tray is null)
        {
            Revert(BootTaskSwitch, !want);
            return;
        }

        if (tray.BootTaskInstalled == want) return;

        if (tray.SetBootTask(want))
        {
            ShowMessage(want ? "已开启开机自启（SYSTEM 任务）。" : "已关闭开机自启。", InfoBarSeverity.Success);
        }
        else
        {
            Revert(BootTaskSwitch, !want);
            ShowMessage("改开机自启失败（SYSTEM 任务需要管理员权限）。", InfoBarSeverity.Error);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        PersistAndApply(quiet: false);
    }

    private void OnOpenLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Log.Path ?? AppConfig.DataDir;
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppConfig.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage($"打开日志失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 隐藏而不是退出：看门狗和托盘还要继续跑
        AppWindow.Hide();
    }

    // ---- 小工具 ----

    private void PersistAndApply(bool quiet)
    {
        _cfg.Normalize();
        var saved = _cfg.Save();
        TrayHost.Tray?.ApplyConfig();
        UpdatePreview();

        FooterHint.Text = saved
            ? $"已保存 · {DateTime.Now:HH:mm:ss}"
            : "保存失败，详情见日志";

        if (!quiet)
        {
            ShowMessage(saved ? "设置已保存并立即生效。" : "设置保存失败（日志里有原因）。",
                saved ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
    }

    private void Revert(ToggleSwitch toggle, bool value)
    {
        _loading = true;
        toggle.IsOn = value;
        _loading = false;
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = severity switch
        {
            InfoBarSeverity.Error => "出错了",
            InfoBarSeverity.Warning => "注意",
            InfoBarSeverity.Success => "完成",
            _ => "提示",
        };
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}
