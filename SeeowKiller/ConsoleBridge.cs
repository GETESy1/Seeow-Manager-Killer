using System.Runtime.InteropServices;

namespace SeeowKiller;

/// <summary>
/// 本程序是 WinExe（双击不弹黑框），所以需要一套输出兼容处理：
///   1. 如果标准输出已经被重定向到管道/文件（例如 `SeeowKiller.exe --status > s.txt`
///      或 PowerShell 的 $(...) 捕获），那 stdout 句柄就是有效的，直接用，绝不能去抢父控制台，
///      否则输出会绕过管道，脚本里什么都读不到。
///   2. 如果句柄无效（Explorer 双击、或由 GUI 程序拉起），再 AttachConsole 到父进程的控制台，
///      这样在 cmd/PowerShell 里手动运行时仍能看到输出。
/// </summary>
internal static class ConsoleBridge
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    public static bool TryAttach()
    {
        try
        {
            var stdout = GetStdHandle(STD_OUTPUT_HANDLE);
            var stderr = GetStdHandle(STD_ERROR_HANDLE);

            var stdoutValid = stdout != IntPtr.Zero && stdout != INVALID_HANDLE_VALUE;

            // 已经有有效句柄（管道/文件/控制台）：什么也不用做
            if (stdoutValid)
            {
                // 控制台保持系统代码页（否则 cmd 里中文会乱码）；
                // 重定向到管道/文件时用 UTF-8，方便脚本处理。
                if (!IsConsoleHandle(stdout)) TrySetUtf8();
                return true;
            }

            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return false;

            if (stdout == IntPtr.Zero || stdout == INVALID_HANDLE_VALUE)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            if (stderr == IntPtr.Zero || stderr == INVALID_HANDLE_VALUE)
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsConsoleHandle(IntPtr handle) => GetConsoleMode(handle, out _);

    private static void TrySetUtf8()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 没有控制台时会抛，忽略 */ }
    }
}
