using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using static Sccap.NativeMethods;

namespace Sccap;

/// <summary>選択結果。ImageRect は仮想スクリーン画像上の座標</summary>
internal sealed record CaptureSelection(Rectangle ImageRect, WindowInfo? Window);

/// <summary>
/// 静止させた画面を全モニターにまたがって表示し、範囲またはウインドウを選ばせるフォーム。
/// フォームは仮想スクリーンと同じ位置・サイズなので、クライアント座標 = 画像座標。
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int DragThreshold = 4;
    private const int MagnifierSource = 15; // 奇数 (中心ピクセルを持つ)
    private const int MagnifierZoom = 8;

    private static readonly Color Accent = Color.FromArgb(0, 162, 255);

    private readonly Bitmap _screen;
    private readonly Bitmap _dimmed;
    private readonly Rectangle _virtual;
    private readonly Rectangle _imageBounds;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly Dictionary<int, Font> _fonts = new();

    private CaptureMode _mode;
    private Point? _anchor;   // 1点目
    private bool _pressed;    // 1点目でボタンを押下中
    private bool _dragging;
    private Point _pressPoint;
    private Point _cursor;
    private WindowInfo? _hover;

    public CaptureSelection? Selection { get; private set; }

    public OverlayForm(Bitmap screen, Rectangle virtualBounds, IReadOnlyList<WindowInfo> windows, CaptureMode mode)
    {
        _screen = screen;
        _virtual = virtualBounds;
        _imageBounds = new Rectangle(Point.Empty, screen.Size);
        _windows = windows;
        _mode = mode;
        _dimmed = CreateDimmed(screen);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        Text = "sccap";
        Bounds = virtualBounds;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    private static Bitmap CreateDimmed(Bitmap src)
    {
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        var r = new Rectangle(0, 0, src.Width, src.Height);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(src, r, r, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;
        using var brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        g.FillRectangle(brush, r);
        return bmp;
    }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // DPI の異なるモニターをまたぐ場合に備え、物理座標で確実に全体を覆う
        SetWindowPos(Handle, HWND_TOPMOST, _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height, SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(Handle);
        UpdateCursor(ToImage(Cursor.Position));
    }

    protected override void WndProc(ref Message m)
    {
        // モニター間の DPI 変化によるフォームの自動リサイズを抑止
        if (m.Msg == WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimmed.Dispose();
            foreach (var f in _fonts.Values) f.Dispose();
            _fonts.Clear();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Press(e.Location);
        else if (e.Button == MouseButtons.Right) ResetOrCancel();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateCursor(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) Release(e.Location);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        int step = (keyData & Keys.Shift) != 0 ? 10 : 1;
        switch (key)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Tab:
            case Keys.Space:
                ToggleMode();
                return true;
            case Keys.Enter:
                Press(_cursor);
                Release(_cursor);
                return true;
            case Keys.Left: MoveCursor(-step, 0); return true;
            case Keys.Right: MoveCursor(step, 0); return true;
            case Keys.Up: MoveCursor(0, -step); return true;
            case Keys.Down: MoveCursor(0, step); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveCursor(int dx, int dy)
    {
        Point p = Cursor.Position;
        Cursor.Position = new Point(p.X + dx, p.Y + dy);
        UpdateCursor(ToImage(Cursor.Position));
    }

    private void Press(Point p)
    {
        UpdateCursor(p);
        if (_mode == CaptureMode.Window)
        {
            if (_hover != null && GetSelectionRect() is Rectangle wr)
                Complete(new CaptureSelection(wr, _hover));
            return;
        }

        if (_anchor is Point a)
        {
            // 2点目のクリック
            Complete(new CaptureSelection(RectFromPoints(a, _cursor), null));
            return;
        }
        _anchor = _cursor;
        _pressed = true;
        _dragging = false;
        _pressPoint = _cursor;
        Invalidate();
    }

    private void Release(Point p)
    {
        if (_mode != CaptureMode.Region || !_pressed) return;
        UpdateCursor(p);
        _pressed = false;
        if (_dragging && _anchor is Point a)
            Complete(new CaptureSelection(RectFromPoints(a, _cursor), null));
        // ドラッグでなければ 1点目確定のまま 2点目のクリックを待つ
    }

    private void ResetOrCancel()
    {
        if (_mode == CaptureMode.Region && _anchor != null)
        {
            _anchor = null;
            _pressed = _dragging = false;
            Invalidate();
        }
        else
        {
            Close();
        }
    }

    private void ToggleMode()
    {
        _mode = _mode == CaptureMode.Region ? CaptureMode.Window : CaptureMode.Region;
        _anchor = null;
        _pressed = _dragging = false;
        UpdateCursor(_cursor);
    }

    private void UpdateCursor(Point p)
    {
        _cursor = new Point(
            Math.Clamp(p.X, 0, _imageBounds.Width - 1),
            Math.Clamp(p.Y, 0, _imageBounds.Height - 1));

        if (_pressed && !_dragging &&
            (Math.Abs(_cursor.X - _pressPoint.X) > DragThreshold || Math.Abs(_cursor.Y - _pressPoint.Y) > DragThreshold))
            _dragging = true;

        _hover = _mode == CaptureMode.Window ? HitTestWindow(ToScreen(_cursor)) : null;
        Invalidate();
    }

    private void Complete(CaptureSelection selection)
    {
        if (selection.ImageRect.Width <= 0 || selection.ImageRect.Height <= 0) return;
        Selection = selection;
        DialogResult = DialogResult.OK;
        Close();
    }

    // ---------------------------------------------------------------- geometry

    private Point ToImage(Point screen) => new(screen.X - _virtual.X, screen.Y - _virtual.Y);
    private Point ToScreen(Point image) => new(image.X + _virtual.X, image.Y + _virtual.Y);

    /// <summary>2点を対角とする矩形 (両端のピクセルを含む)</summary>
    private Rectangle RectFromPoints(Point a, Point b)
    {
        var r = Rectangle.FromLTRB(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
        r.Intersect(_imageBounds);
        return r;
    }

    private WindowInfo? HitTestWindow(Point screenPoint)
    {
        foreach (var w in _windows)
            if (w.Bounds.Contains(screenPoint)) return w;
        return null;
    }

    private Rectangle? GetSelectionRect()
    {
        if (_mode == CaptureMode.Region)
            return _anchor is Point a ? RectFromPoints(a, _cursor) : null;

        if (_hover == null) return null;
        var r = _hover.Bounds;
        r.Offset(-_virtual.X, -_virtual.Y);
        r.Intersect(_imageBounds);
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    /// <summary>カーソルがあるモニターの範囲 (画像座標)</summary>
    private Rectangle CurrentMonitorBounds()
    {
        var b = Screen.FromPoint(ToScreen(_cursor)).Bounds;
        b.Offset(-_virtual.X, -_virtual.Y);
        return b;
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_dimmed, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);

        Rectangle? sel = GetSelectionRect();
        if (sel is Rectangle s)
            g.DrawImage(_screen, s, s, GraphicsUnit.Pixel);

        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float scale = GetScaleAt(ToScreen(_cursor));
        Font font = GetFont(scale);
        Rectangle monitor = CurrentMonitorBounds();

        if (_mode == CaptureMode.Region && _anchor == null)
            DrawCrosshair(g, monitor);

        if (sel is Rectangle r)
        {
            float w = Math.Max(1f, MathF.Round(2 * scale));
            using var pen = new Pen(Accent, w) { Alignment = PenAlignment.Inset };
            g.DrawRectangle(pen, r);

            string label = $"{r.Width} × {r.Height}";
            if (_mode == CaptureMode.Window && _hover != null && _hover.Title.Length > 0)
                label = $"{Truncate(_hover.Title, 60)}   {label}";
            DrawLabelNear(g, label, font, r, scale);
        }

        if (_mode == CaptureMode.Region)
            DrawMagnifier(g, font, scale, monitor);

        DrawHelp(g, font, scale, monitor);
    }

    private void DrawCrosshair(Graphics g, Rectangle monitor)
    {
        using var pen = new Pen(Color.FromArgb(160, 255, 255, 255), 1) { DashStyle = DashStyle.Dash };
        g.DrawLine(pen, monitor.Left, _cursor.Y, monitor.Right, _cursor.Y);
        g.DrawLine(pen, _cursor.X, monitor.Top, _cursor.X, monitor.Bottom);
    }

    private void DrawMagnifier(Graphics g, Font font, float scale, Rectangle monitor)
    {
        int zoom = Math.Max(2, (int)MathF.Round(MagnifierZoom * scale));
        int size = MagnifierSource * zoom;
        int offset = (int)(24 * scale);
        int half = MagnifierSource / 2;

        string info = $"X {ToScreen(_cursor).X}  Y {ToScreen(_cursor).Y}";
        if (_anchor is Point a)
        {
            var r = RectFromPoints(a, _cursor);
            info += $"   {r.Width} × {r.Height}";
        }
        SizeF infoSize = g.MeasureString(info, font);
        int infoH = (int)Math.Ceiling(infoSize.Height) + 4;
        int totalW = Math.Max(size, (int)Math.Ceiling(infoSize.Width) + 8);
        int totalH = size + infoH;

        int x = _cursor.X + offset;
        int y = _cursor.Y + offset;
        if (x + totalW > monitor.Right) x = _cursor.X - offset - totalW;
        if (y + totalH > monitor.Bottom) y = _cursor.Y - offset - totalH;
        x = Math.Max(x, monitor.Left);
        y = Math.Max(y, monitor.Top);

        var dest = new Rectangle(x, y, size, size);
        g.FillRectangle(Brushes.Black, dest);

        var src = new Rectangle(_cursor.X - half, _cursor.Y - half, MagnifierSource, MagnifierSource);
        var clipped = Rectangle.Intersect(src, _imageBounds);
        if (clipped.Width > 0 && clipped.Height > 0)
        {
            var clippedDest = new Rectangle(
                dest.X + (clipped.X - src.X) * zoom, dest.Y + (clipped.Y - src.Y) * zoom,
                clipped.Width * zoom, clipped.Height * zoom);
            var oldOffset = g.PixelOffsetMode;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(_screen, clippedDest, clipped, GraphicsUnit.Pixel);
            g.PixelOffsetMode = oldOffset;
        }

        using (var centerPen = new Pen(Color.Red, 1))
            g.DrawRectangle(centerPen, dest.X + half * zoom, dest.Y + half * zoom, zoom - 1, zoom - 1);
        using (var borderPen = new Pen(Color.White, 1))
            g.DrawRectangle(borderPen, dest.X, dest.Y, dest.Width - 1, dest.Height - 1);

        var infoRect = new Rectangle(x, y + size, totalW, infoH);
        using (var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 20)))
            g.FillRectangle(bg, infoRect);
        g.DrawString(info, font, Brushes.White, infoRect.X + 4, infoRect.Y + 2);
    }

    private void DrawLabelNear(Graphics g, string text, Font font, Rectangle target, float scale)
    {
        SizeF sz = g.MeasureString(text, font);
        int pad = (int)(4 * scale);
        int w = (int)Math.Ceiling(sz.Width) + pad * 2;
        int h = (int)Math.Ceiling(sz.Height) + pad;
        int x = target.X;
        int y = target.Y - h - 2;
        if (y < 0) y = target.Y + 2; // 上に余白がなければ内側に表示
        x = Math.Clamp(x, 0, Math.Max(0, _imageBounds.Width - w));

        using var bg = new SolidBrush(Color.FromArgb(230, Accent));
        g.FillRectangle(bg, x, y, w, h);
        g.DrawString(text, font, Brushes.White, x + pad, y + pad / 2f);
    }

    private void DrawHelp(Graphics g, Font font, float scale, Rectangle monitor)
    {
        string text = _mode == CaptureMode.Region
            ? "範囲モード：2点をクリック または ドラッグ　｜　矢印キー：1px移動 (Shift：10px)　Enter：クリック\n" +
              "Tab：ウインドウモードへ　｜　右クリック：やり直し　｜　Esc：終了"
            : "ウインドウモード：キャプチャーするウインドウをクリック\n" +
              "Tab：範囲モードへ　｜　右クリック / Esc：終了";

        SizeF sz = g.MeasureString(text, font);
        int pad = (int)(10 * scale);
        int w = (int)Math.Ceiling(sz.Width) + pad * 2;
        int h = (int)Math.Ceiling(sz.Height) + pad * 2;
        int margin = (int)(16 * scale);
        int x = monitor.X + (monitor.Width - w) / 2;
        int y = monitor.Y + margin;

        // カーソルと重なる場合は画面下側へ逃がす
        if (new Rectangle(x, y, w, h + margin).Contains(_cursor))
            y = monitor.Bottom - margin - h;

        using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        g.FillRectangle(bg, x, y, w, h);
        g.DrawString(text, font, Brushes.White, x + pad, y + pad);
    }

    private Font GetFont(float scale)
    {
        int key = (int)MathF.Round(scale * 100);
        if (!_fonts.TryGetValue(key, out var font))
        {
            font = new Font("Yu Gothic UI", 14f * key / 100f, FontStyle.Regular, GraphicsUnit.Pixel);
            _fonts[key] = font;
        }
        return font;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
