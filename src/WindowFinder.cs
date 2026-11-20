using System.Text;
using static Sccap.NativeMethods;

namespace Sccap;

internal sealed record WindowInfo(IntPtr Handle, Rectangle Bounds, string Title);

/// <summary>キャプチャー対象になり得るトップレベルウインドウを Z オーダー順 (手前→奥) に列挙する</summary>
internal static class WindowFinder
{
    public static List<WindowInfo> GetTopLevelWindows()
    {
        var list = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid) return true;

            long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TRANSPARENT) != 0) return true; // クリック透過のオーバーレイ類
            if ((exStyle & WS_EX_LAYERED) != 0 &&
                GetLayeredWindowAttributes(hWnd, out uint _, out byte alpha, out uint flags) &&
                (flags & LWA_ALPHA) != 0 && alpha == 0) return true; // 完全透明

            // 別の仮想デスクトップ上のウインドウや UWP の非表示ウインドウ
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            Rectangle bounds = GetVisibleBounds(hWnd);
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;

            list.Add(new WindowInfo(hWnd, bounds, GetTitle(hWnd)));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>影などの不可視枠を除いた、見た目どおりのウインドウ矩形</summary>
    public static Rectangle GetVisibleBounds(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
            return r.ToRectangle();
        return GetWindowRect(hWnd, out RECT wr) ? wr.ToRectangle() : Rectangle.Empty;
    }

    private static string GetTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
