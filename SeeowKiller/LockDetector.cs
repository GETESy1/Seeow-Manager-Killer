using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SeeowKiller;

/// <summary>一个候选窗口（锁屏识别用）。</summary>
internal sealed class LockWindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public uint Pid { get; init; }
    public string ProcessName { get; init; } = "";
    public string? ProcessPath { get; init; }
    public bool IsTopMost { get; init; }
    public bool IsFullScreen { get; init; }
    public bool IsVisible { get; init; }
    public System.Drawing.Rectangle Rect { get; init; }

    public bool TitleMatches { get; init; }
    public bool ClassMatches { get; init; }
    public bool ProcessMatches { get; init; }

    public bool IsLockCandidate => TitleMatches && ClassMatches && ProcessMatches && IsTopMost && IsFullScreen;

    public string Describe() =>
        $"\"{Title}\" [{ClassName}] pid={Pid} {ProcessName}" +
        (ProcessPath is null ? "" : $" ({ProcessPath})") +
        $" topmost={IsTopMost} fullscreen={IsFullScreen} visible={IsVisible} rect={Rect.Width}x{Rect.Height}" +
        $" 标题={Y(TitleMatches)} 类名={Y(ClassMatches)} 进程={Y(ProcessMatches)}";

    private static string Y(bool b) => b ? "√" : "×";
}

/// <summary>
/// 锁屏识别 + 只针对锁屏下手。
///
/// 判据来自对希沃管家的调研与真机取证（详见 README）：
///   窗口标题 = "希沃管家"（精确），窗口类名含 "Chrome_WidgetWin"（Electron/CEF），
///   窗口属于 SeewoServiceAssistant.exe，且是【可见 + 置顶 + 全屏】。
/// 命中才动手 —— 希沃管家的其它功能（白板、课件、巡视）不受影响。
/// </summary>
internal static class LockDetector
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_LAYERED = 0x00080000;

    private const int SW_MINIMIZE = 6;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint LWA_ALPHA = 0x00000002;

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static long ExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    /// <summary>扫描窗口。onlyMatches=true 时只返回命中的锁屏窗口。</summary>
    public static List<LockWindowInfo> Scan(AppConfig cfg, bool onlyMatches)
    {
        int screenW = GetSystemMetrics(0);   // SM_CXSCREEN
        int screenH = GetSystemMetrics(1);   // SM_CYSCREEN
        const int tolerance = 100;           // 和调研里 HugoUtils 的判定一致

        var result = new List<LockWindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            var title = GetWindowTextW2(hwnd);
            var cls = GetClassName2(hwnd);

            bool visible = IsWindowVisible(hwnd);
            bool topmost = (ExStyle(hwnd) & WS_EX_TOPMOST) != 0;
            GetWindowRect(hwnd, out var rect);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            bool fullscreen = Math.Abs(rect.Left) <= tolerance && Math.Abs(rect.Top) <= tolerance
                              && Math.Abs(w - screenW) <= tolerance && Math.Abs(h - screenH) <= tolerance;

            // 只关心"像锁屏"的窗口：可见 + 置顶，且（全屏 或 标题对得上）
            bool titleMatches = title.Equals(cfg.LockTitle, StringComparison.OrdinalIgnoreCase);
            if (!visible || !topmost || !(fullscreen || titleMatches)) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            var (name, path) = ProcessInfo(pid);

            var info = new LockWindowInfo
            {
                Hwnd = hwnd,
                Title = title,
                ClassName = cls,
                Pid = pid,
                ProcessName = name,
                ProcessPath = path,
                IsTopMost = topmost,
                IsFullScreen = fullscreen,
                IsVisible = visible,
                Rect = new System.Drawing.Rectangle(rect.Left, rect.Top, w, h),
                TitleMatches = titleMatches,
                ClassMatches = cls.Contains(cfg.LockClass, StringComparison.OrdinalIgnoreCase),
                ProcessMatches = name.Contains(cfg.LockProcess, StringComparison.OrdinalIgnoreCase),
            };

            if (!onlyMatches || info.IsLockCandidate) result.Add(info);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static string GetWindowTextW2(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClassName2(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static (string name, string? path) ProcessInfo(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return ("(读不到)", null);

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return ("(读不到)", null);

            var path = sb.ToString();
            return (System.IO.Path.GetFileName(path), path);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 尝试把锁屏窗口藏掉（抄 HugoLock 的做法：最小化 + 沉底隐藏 + 全透明）。
    /// 需要能操作那个窗口，权限不够会失败 —— 调用方负责失败后改用杀进程。
    /// </summary>
    public static bool TryHide(LockWindowInfo window)
    {
        try
        {
            ShowWindow(window.Hwnd, SW_MINIMIZE);
            if (!SetWindowPos(window.Hwnd, new IntPtr(1) /*HWND_BOTTOM*/, 0, 0, 0, 0,
                    SWP_HIDEWINDOW | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING))
                return false;

            var ex = ExStyle(window.Hwnd);
            if (IntPtr.Size == 8) SetWindowLongPtr64(window.Hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            else SetWindowLong32(window.Hwnd, GWL_EXSTYLE, (int)(ex | WS_EX_LAYERED));

            SetLayeredWindowAttributes(window.Hwnd, 0, 0, LWA_ALPHA);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"隐藏锁屏窗口失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>结束锁屏窗口的属主进程。</summary>
    public static bool KillOwner(LockWindowInfo window)
    {
        try
        {
            using var p = Process.GetProcessById((int)window.Pid);
            p.Kill(entireProcessTree: false);
            p.WaitForExit(3000);
            return true;
        }
        catch (ArgumentException)
        {
            return true; // 已经自己退出了
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            Log.Warn($"结束锁屏进程被拒绝访问（PID {window.Pid}）：需要管理员权限；当前管理员={Elevation.IsAdmin}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"结束锁屏进程失败（PID {window.Pid}）: {ex.Message}");
            return false;
        }
    }
}
