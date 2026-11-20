using System.Drawing.Imaging;
using static Sccap.NativeMethods;

namespace Sccap;

internal static class ScreenCapture
{
    /// <summary>全モニターを含む仮想スクリーン全体を物理ピクセルで取得する</summary>
    public static Bitmap CaptureVirtualScreen(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                // CAPTUREBLT: 半透明(レイヤード)ウインドウも含めて取得
                BitBlt(hdc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            }
            finally { g.ReleaseHdc(hdc); }
        }
        finally { ReleaseDC(IntPtr.Zero, screenDc); }
        return bmp;
    }

    /// <summary>画像の一部を 24bit RGB として切り出す</summary>
    public static Bitmap Crop(Bitmap source, Rectangle rect)
    {
        rect.Intersect(new Rectangle(Point.Empty, source.Size));
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException("キャプチャー範囲が空です。");
        var bmp = source.Clone(rect, PixelFormat.Format24bppRgb);
        bmp.SetResolution(96, 96);
        return bmp;
    }

    /// <summary>
    /// PrintWindow でウインドウ自身に描画させて取得する (背後に隠れた部分も取得可能)。
    /// 失敗した場合は null。
    /// </summary>
    public static Bitmap? CaptureWindowByPrint(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out RECT wr)) return null;
        Rectangle full = wr.ToRectangle();
        if (full.Width <= 0 || full.Height <= 0) return null;

        using var bmp = new Bitmap(full.Width, full.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            bool ok;
            try { ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
            if (!ok) return null;
        }

        // GetWindowRect は影の不可視枠を含むので、見た目の矩形に切り詰める
        Rectangle visible = WindowFinder.GetVisibleBounds(hWnd);
        visible.Offset(-full.X, -full.Y);
        visible.Intersect(new Rectangle(Point.Empty, full.Size));
        if (visible.Width <= 0 || visible.Height <= 0) return null;
        return Crop(bmp, visible);
    }
}
