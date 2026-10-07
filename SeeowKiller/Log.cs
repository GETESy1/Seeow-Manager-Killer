using System.Text;

namespace SeeowKiller;

/// <summary>极简日志：写文件 + Debug 输出，任何失败都不影响主逻辑。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? Path => _path;

    public static void Init(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _path = null;
            return;
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(path!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _path = path;
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// 限流日志：距离上次写入不足 quietSeconds 就跳过。
    /// 用于 --ifeo-stub 这种"每次被拉起都是新进程"的场景，避免被反复拉起的守护进程刷爆日志。
    /// </summary>
    public static void InfoThrottled(string message, int quietSeconds)
    {
        try
        {
            if (_path is not null && File.Exists(_path) &&
                (DateTime.UtcNow - File.GetLastWriteTimeUtc(_path)).TotalSeconds < quietSeconds)
                return;
        }
        catch
        {
            // 读不到时间就照常写
        }

        Info(message);
    }

    public static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写不进去就算了，绝不能因此让看门狗挂掉
            }
        }
    }
}
