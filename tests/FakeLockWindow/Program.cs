using System.Runtime.InteropServices;

namespace FakeLockWindow;

/// <summary>
/// 造一个和"希沃管家锁屏"特征一致的窗口，用来测试 SeeowKiller 的锁屏识别：
///   窗口类名 = Chrome_WidgetWin_0，标题 = 希沃管家，全屏 + 置顶 + 可见。
/// 默认做成【全透明】（WS_EX_LAYERED + alpha 0）：对检测方来说它是"可见窗口"，
/// 但你屏幕上什么都看不到，测起来不打扰人。加 --opaque 可以看它长什么样。
///
/// 用法：
///   FakeLockWindow.exe [--seconds=20] [--title=希沃管家] [--class=Chrome_WidgetWin_0] [--opaque]
/// </summary>
internal static class Program
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint PM_REMOVE = 0x0001;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    private static WndProcDelegate? _proc; // 必须保持引用，别被 GC 回收

    [STAThread]
    private static int Main(string[] args)
    {
        var title = "希沃管家";
        var className = "Chrome_WidgetWin_0";
        var seconds = 20;
        var opaque = false;

        foreach (var a in args)
        {
            var v = a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : "";
            if (a.StartsWith("--title=", StringComparison.OrdinalIgnoreCase)) title = v;
            else if (a.StartsWith("--class=", StringComparison.OrdinalIgnoreCase)) className = v;
            else if (a.StartsWith("--seconds=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var s)) seconds = s;
            else if (a.Equals("--opaque", StringComparison.OrdinalIgnoreCase)) opaque = true;
        }

        var instance = GetModuleHandleW(null);
        _proc = WndProcImpl;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            hbrBackground = CreateSolidBrush(0x00303060),  // 深蓝，仅 --opaque 时看得见
            lpszClassName = className,
        };

        if (RegisterClassExW(ref wc) == 0)
        {
            // 类名已存在（多开）也能继续，CreateWindowEx 仍可用已有类
        }

        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);

        uint exStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | (opaque ? 0u : WS_EX_LAYERED);
        var hwnd = CreateWindowExW(exStyle, className, title, WS_POPUP | WS_VISIBLE,
            0, 0, screenW, screenH, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine($"CreateWindowEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            return 1;
        }

        ShowWindow(hwnd, 5 /*SW_SHOW*/);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, screenW, screenH, SWP_SHOWWINDOW);
        UpdateWindow(hwnd);

        if (!opaque)
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA); // 全透明：可见但不挡人

        // 跑 seconds 秒（或直到被关闭）
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        bool quit = false;

        while (!quit && DateTime.UtcNow < deadline)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == 0x0012 /*WM_QUIT*/) { quit = true; break; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(50);
        }

        return 0;
    }

    private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0002 /*WM_DESTROY*/)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
