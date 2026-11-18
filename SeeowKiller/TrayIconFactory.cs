using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SeeowKiller;

/// <summary>
/// 托盘图标的唯一来源：内置图标、全透明图标、用户自定义 .ico。
/// 图标全部在运行时生成，仓库里不带二进制资源。
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>按配置生成图标（自定义图标不可用时自动退回内置图标）。</summary>
    public static Icon Create(AppConfig cfg) => Create(cfg.IconStyle, cfg.TrayIconPath);

    public static Icon Create(TrayIconStyle style, string? customPath)
    {
        switch (style)
        {
            case TrayIconStyle.Transparent:
                return CreateTransparent();

            case TrayIconStyle.Custom:
                var custom = TryLoadCustom(customPath);
                if (custom is not null) return custom;
                Log.Warn($"自定义托盘图标不可用（{customPath}），已退回内置图标");
                break;
        }

        return CreateDefault();
    }

    /// <summary>内置图标：红圆 + 白色叉。</summary>
    public static Icon CreateDefault()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(230, 30, 30));
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var pen = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 10, 10, 22, 22);
            g.DrawLine(pen, 22, 10, 10, 22);
        }

        return FromBitmap(bmp);
    }

    /// <summary>全透明图标：托盘区看不见图标，但右键菜单照常可用。</summary>
    public static Icon CreateTransparent()
    {
        using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Transparent);
        return FromBitmap(bmp);
    }

    /// <summary>加载用户指定的 .ico；失败返回 null（不抛异常）。</summary>
    public static Icon? TryLoadCustom(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (!File.Exists(full)) return null;

            using var source = new Icon(full);
            // 复制一份，避免长时间占用用户的 .ico 文件
            return (Icon)source.Clone();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取自定义图标失败（{path}）：{ex.Message}");
            return null;
        }
    }

    private static Icon FromBitmap(Bitmap bmp)
    {
        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
