using Microsoft.UI.Dispatching;

namespace SeeowKiller;

/// <summary>
/// 进程内两个 UI 世界的桥：WinUI（主线程，设置窗口）↔ WinForms（托盘线程，NotifyIcon）。
/// 托盘线程先起来，WinUI 启动失败也还能干活；设置窗口按需创建，关掉不等于退出程序。
/// </summary>
internal static class TrayHost
{
    private const string ShowSettingsEventName = @"Local\SeeowKiller.ShowSettings";

    private static readonly ManualResetEventSlim Exited = new(false);
    private static Thread? _trayThread;
    private static TrayApp? _tray;
    private static DispatcherQueue? _uiQueue;
    private static AppConfig? _config;
    private static EventWaitHandle? _settingsEvent;

    public static AppConfig? Config => _config;
    public static TrayApp? Tray => _tray;
    public static bool UiReady => _uiQueue is not null;

    /// <summary>启动托盘 + 看门狗（独占一个 STA 线程）。</summary>
    public static void StartTray(AppConfig cfg)
    {
        _config = cfg;

        var ready = new ManualResetEventSlim(false);
        _trayThread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                System.Windows.Forms.Application.SetUnhandledExceptionMode(
                    System.Windows.Forms.UnhandledExceptionMode.CatchException);
                System.Windows.Forms.Application.ThreadException += (_, e) =>
                    Log.Error($"托盘界面线程异常: {e.Exception}");

                var tray = new TrayApp(cfg);
                _tray = tray;
                ready.Set();
                System.Windows.Forms.Application.Run(tray);
            }
            catch (Exception ex)
            {
                Log.Error($"托盘线程退出: {ex}");
                ready.Set();
            }
            finally
            {
                Exited.Set();
            }
        })
        {
            IsBackground = true,
            Name = "SeeowKiller.Tray",
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();

        ready.Wait(TimeSpan.FromSeconds(20));

        // 等第二个实例来敲门：直接弹设置窗口，而不是默默退出
        _ = Task.Run(() =>
        {
            while (!Exited.IsSet)
            {
                try
                {
                    if (_settingsEvent is not null && _settingsEvent.WaitOne(1000))
                        OpenSettings();
                    else if (_settingsEvent is null)
                        Thread.Sleep(1000);
                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
        });
    }

    /// <summary>第二个实例启动时用：通知已经在跑的那个实例把设置窗口弹出来。</summary>
    public static void SignalShowSettings()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowSettingsEventName);
            handle.Set();
        }
        catch
        {
            // 没人在听就算了
        }
    }

    /// <summary>由 UI 线程在 App.OnLaunched 里登记自己的 DispatcherQueue。</summary>
    public static void AttachUi(DispatcherQueue queue)
    {
        _uiQueue = queue;
    }

    /// <summary>任意线程调用：切到 UI 线程打开设置窗口。</summary>
    public static void OpenSettings()
    {
        var queue = _uiQueue;
        if (queue is null)
        {
            Log.Warn("设置窗口不可用（WinUI 没有初始化成功）");
            return;
        }

        queue.TryEnqueue(() =>
        {
            try
            {
                UI.SettingsWindow.ShowOrActivate();
            }
            catch (Exception ex)
            {
                Log.Error($"打开设置窗口失败: {ex}");
            }
        });
    }

    /// <summary>创建单实例信号量（必须在启动托盘线程之前调用）。</summary>
    public static void CreateShowSettingsEvent()
    {
        try
        {
            _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        }
        catch (Exception ex)
        {
            Log.Warn($"创建设置窗口信号失败: {ex.Message}");
        }
    }

    /// <summary>托盘里点退出：关掉 WinUI，让整个进程结束。</summary>
    public static void ExitApplication()
    {
        var queue = _uiQueue;
        if (queue is null)
        {
            Environment.Exit(0);
            return;
        }

        UI.SettingsWindow.AllowClose = true;
        queue.TryEnqueue(() => Microsoft.UI.Xaml.Application.Current?.Exit());
    }

    /// <summary>WinUI 起不来时的兜底：只保留托盘，主线程在这里等它结束。</summary>
    public static void WaitForExit() => Exited.Wait();
}
