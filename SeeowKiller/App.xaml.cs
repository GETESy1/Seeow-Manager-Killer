using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SeeowKiller;

/// <summary>WinUI 3 应用壳：只负责主线程消息循环和设置窗口的按需创建。</summary>
public partial class App : Application
{
    /// <summary>本次启动是否直接把设置窗口弹出来（--settings 或第二次运行）。</summary>
    public static bool ShowSettingsOnStart { get; set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error($"WinUI 未处理异常: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            TrayHost.AttachUi(DispatcherQueue.GetForCurrentThread());
            Log.Info("WinUI 已就绪");

            if (ShowSettingsOnStart)
                UI.SettingsWindow.ShowOrActivate();
        }
        catch (Exception ex)
        {
            Log.Error($"WinUI OnLaunched 失败: {ex}");
            throw;
        }
    }
}
