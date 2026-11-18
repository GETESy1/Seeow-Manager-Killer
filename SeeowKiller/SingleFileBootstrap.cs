using System.Runtime.CompilerServices;

namespace SeeowKiller;

/// <summary>
/// 单文件（PublishSingleFile + IncludeAllContentForSelfExtract）模式的启动补丁，模块初始化器
/// 在 Main 之前执行，做两件事：
///
/// 1) 告诉 Windows App SDK 运行时的 base directory 在哪儿（官方要求；
///    见 WindowsAppSDK.SingleFile.targets 里的警告）。
/// 2) 给"被改过名的 exe"补一份 PRI。MRT Core 在不打包（unpackaged）场景下是按
///    **进程名**去找资源包的：exe 叫 SeeowKiller.exe 时找 SeeowKiller.pri；如果用户把单文件
///    exe 改名成 SeeowKiller-1.1.0-win-x64-selfcontained.exe，它就会去找
///    SeeowKiller-1.1.0-win-x64-selfcontained.pri，找不到 → 托盘正常，但一开设置窗口就
///    XamlParseException: XAML parsing failed。这里把 SeeowKiller.pri 复制一份改成新名字即可。
/// </summary>
internal static class SingleFileBootstrap
{
    private const string AppPriName = "SeeowKiller.pri";
    private const string AppExeName = "SeeowKiller";

    [ModuleInitializer]
    internal static void Initialize()
    {
        SetRuntimeBaseDirectory();
        AlignPriWithExeName();
    }

    private static void SetRuntimeBaseDirectory()
    {
        try
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY")))
                return;

            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
                Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", baseDir);
        }
        catch
        {
            // 设不上就照旧跑
        }
    }

    private static void AlignPriWithExeName()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(exePath)) return;

            var exeName = Path.GetFileNameWithoutExtension(exePath);
            if (string.IsNullOrEmpty(exeName)) return;
            if (string.Equals(exeName, AppExeName, StringComparison.OrdinalIgnoreCase)) return;

            var source = Path.Combine(baseDir, AppPriName);
            if (!File.Exists(source)) return; // 没有 PRI（比如打包版/别的方式部署）就不用管

            var target = Path.Combine(baseDir, exeName + ".pri");
            if (File.Exists(target)) return;

            File.Copy(source, target);
            Log.Info($"已为改名后的程序补充资源包：{Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            // 目录只读（例如装在 Program Files）时复制会失败；不影响 watch/status，只是设置窗口可能打不开
            Log.Warn($"补充 PRI 失败（改过名的 exe 可能打不开设置窗口）：{ex.Message}");
        }
    }
}
