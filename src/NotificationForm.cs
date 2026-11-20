using System.Diagnostics;
using static Sccap.NativeMethods;

namespace Sccap;

/// <summary>保存完了を画面右下に数秒表示する小さな通知。クリックでエクスプローラーで表示</summary>
internal sealed class NotificationForm : Form
{
    private const string Title = "保存しました";
    private readonly string _path;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private Font? _titleFont;
    private Font? _bodyFont;
    private int _pad;

    public NotificationForm(string path)
    {
        _path = path;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(32, 32, 32);
        Cursor = Cursors.Hand;
        Text = "sccap";
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        Location = new Point(wa.Right - 10, wa.Bottom - 10); // 仮位置 (プライマリモニター上で作成させる)
        Size = new Size(1, 1);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _timer.Tick += (_, _) => Close();
        Click += (_, _) => { RevealInExplorer(); Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        float scale = DeviceDpi / 96f;
        _titleFont = new Font("Yu Gothic UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _bodyFont = new Font("Yu Gothic UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _pad = (int)(14 * scale);

        int maxBody = (int)(480 * scale);
        Size t = TextRenderer.MeasureText(Title, _titleFont);
        Size b = TextRenderer.MeasureText(_path, _bodyFont, new Size(maxBody, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        int w = Math.Max(t.Width, b.Width) + _pad * 2;
        int h = t.Height + b.Height + _pad * 2 + (int)(4 * scale);

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
        int margin = (int)(16 * scale);
        Bounds = new Rectangle(wa.Right - w - margin, wa.Bottom - h - margin, w, h);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_titleFont == null || _bodyFont == null) return;
        var g = e.Graphics;
        using (var pen = new Pen(Color.FromArgb(0, 162, 255), Math.Max(1, DeviceDpi / 48)))
            g.DrawLine(pen, 0, 0, 0, Height);

        Size t = TextRenderer.MeasureText(Title, _titleFont);
        TextRenderer.DrawText(g, Title, _titleFont, new Point(_pad, _pad), Color.White);
        var bodyRect = new Rectangle(_pad, _pad + t.Height + (int)(4 * DeviceDpi / 96f),
            Width - _pad * 2, Height - _pad * 2 - t.Height);
        TextRenderer.DrawText(g, _path, _bodyFont, bodyRect, Color.FromArgb(200, 200, 200),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
    }

    private void RevealInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont?.Dispose();
            _bodyFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
