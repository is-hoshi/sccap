using System.Diagnostics;
using System.Drawing.Imaging;

namespace Sccap;

internal static class Program
{
    private const string AppName = "sccap";

    private const string Usage =
        "使い方: sccap.exe [region | window]\n\n" +
        "  region (r) : 2点を指定した矩形範囲をキャプチャー\n" +
        "  window (w) : クリックしたウインドウをキャプチャー\n\n" +
        "省略時は設定ファイル (sccap.json) の defaultMode に従います。\n" +
        "キャプチャー画面では Tab キーでモードを切り替えられます。";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 二重起動 (ショートカットキーの連打など) を防ぐ
        using var mutex = new Mutex(true, @"Local\sccap-capture-overlay", out bool createdNew);
        if (!createdNew) return 0;

        try
        {
            return Run(args);
        }
        catch (ConfigException ex)
        {
            ShowError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            ShowError($"エラーが発生しました。\n\n{ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        AppConfig config = AppConfig.Load();
        CaptureMode mode = config.Mode;

        foreach (string arg in args)
        {
            string a = arg.TrimStart('-', '/');
            if (a is "h" or "help" or "?")
            {
                MessageBox.Show(Usage, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            mode = AppConfig.ParseMode(a) ?? throw new ConfigException($"不明な引数です: {arg}\n\n{Usage}");
        }

        // 保存先の設定ミスはキャプチャー前に知らせる
        config.ResolveSaveDirectory();

        // オーバーレイ表示前に画面とウインドウ一覧を確定させる
        Rectangle virtualBounds = SystemInformation.VirtualScreen;
        using Bitmap screen = ScreenCapture.CaptureVirtualScreen(virtualBounds);
        List<WindowInfo> windows = WindowFinder.GetTopLevelWindows();

        CaptureSelection? selection;
        using (var overlay = new OverlayForm(screen, virtualBounds, windows, mode))
        {
            Application.Run(overlay);
            selection = overlay.Selection;
        }
        if (selection == null) return 0; // キャンセル

        Bitmap? image = null;
        try
        {
            if (selection.Window != null && config.WindowMethod == WindowCaptureMethod.PrintWindow)
                image = ScreenCapture.CaptureWindowByPrint(selection.Window.Handle);
            image ??= ScreenCapture.Crop(screen, selection.ImageRect);

            string path = config.BuildOutputPath(DateTime.Now);
            image.Save(path, ImageFormat.Png);

            if (config.CopyToClipboard)
            {
                try { Clipboard.SetImage(image); }
                catch (Exception ex) { ShowError($"クリップボードへのコピーに失敗しました。\n\n{ex.Message}"); }
            }

            if (config.OpenAfterSave)
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch (Exception ex) { ShowError($"画像を開けませんでした。\n\n{ex.Message}"); }
            }

            if (config.ShowNotification)
                Application.Run(new NotificationForm(path));
        }
        finally
        {
            image?.Dispose();
        }
        return 0;
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
