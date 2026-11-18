using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SeeowKiller;

/// <summary>
/// WinUI 3 启动器：先起托盘线程（保证拦截立刻生效），再初始化 Windows App SDK 的消息循环。
/// 主界面（设置窗口）按需创建；WinUI 起不来也不影响看门狗，退化成纯托盘模式。
/// </summary>
internal static class WinUiBootstrap
{
    public static int Run(AppConfig cfg, bool showSettings)
    {
        TrayHost.CreateShowSettingsEvent();
        TrayHost.StartTray(cfg);

        App.ShowSettingsOnStart = showSettings;

        try
        {
            // 必须先初始化 COM 包装，否则 Application.Start 里的类型投影会炸
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(_ =>
            {
                try
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                }
                catch (Exception ex)
                {
                    Log.Error($"创建 WinUI App 失败: {ex}");
                    throw;
                }
            });

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"WinUI 初始化失败，退化为纯托盘模式: {ex}");

            try
            {
                System.Windows.Forms.MessageBox.Show(
                    "WinUI 主界面启动失败，程序会继续以托盘方式运行（拦截功能不受影响）。\n\n" + ex.Message,
                    "SeeowKiller",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            catch
            {
                // 弹不出窗口就算了
            }

            TrayHost.WaitForExit();
            return 1;
        }
    }
}
