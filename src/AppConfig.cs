using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sccap;

internal enum CaptureMode { Region, Window }

internal enum WindowCaptureMethod { Screen, PrintWindow }

internal sealed class ConfigException(string message) : Exception(message);

internal sealed class AppConfig
{
    public const string FileName = "sccap.json";

    public string? SaveDirectory { get; set; } = @"{MyPictures}\sccap";
    public string? FileNameFormat { get; set; } = "sccap_{yyyyMMdd_HHmmss}";
    public string? DefaultMode { get; set; } = "region";
    public string? WindowCaptureMethod { get; set; } = "screen";
    public bool CopyToClipboard { get; set; }
    public bool OpenAfterSave { get; set; }
    public bool ShowNotification { get; set; } = true;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            TryWriteDefault(path);
            return new AppConfig();
        }

        AppConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"設定ファイルの書式が正しくありません。\n{path}\n\n{ex.Message}");
        }
        cfg ??= new AppConfig();
        // 型チェックを兼ねて一度解決しておく
        _ = cfg.Mode;
        _ = cfg.WindowMethod;
        return cfg;
    }

    private static void TryWriteDefault(string path)
    {
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("sccap.default.json");
            if (s == null) return;
            using var fs = File.Create(path);
            s.CopyTo(fs);
        }
        catch
        {
            // 書き込めない場所 (Program Files など) でも既定値で動作させる
        }
    }

    public CaptureMode Mode => ParseMode(DefaultMode ?? "region")
        ?? throw new ConfigException($"defaultMode の値が不正です: \"{DefaultMode}\" (\"region\" または \"window\")");

    public WindowCaptureMethod WindowMethod => (WindowCaptureMethod ?? "screen").Trim().ToLowerInvariant() switch
    {
        "screen" or "" => Sccap.WindowCaptureMethod.Screen,
        "printwindow" => Sccap.WindowCaptureMethod.PrintWindow,
        _ => throw new ConfigException($"windowCaptureMethod の値が不正です: \"{WindowCaptureMethod}\" (\"screen\" または \"printWindow\")"),
    };

    public static CaptureMode? ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "region" or "r" or "" => CaptureMode.Region,
        "window" or "w" => CaptureMode.Window,
        _ => null,
    };

    /// <summary>保存先フォルダの絶対パス</summary>
    public string ResolveSaveDirectory()
    {
        string dir = string.IsNullOrWhiteSpace(SaveDirectory) ? @"{MyPictures}\sccap" : SaveDirectory;
        dir = Regex.Replace(dir, @"\{(\w+)\}", m =>
            Enum.TryParse(m.Groups[1].Value, ignoreCase: true, out Environment.SpecialFolder folder)
                ? Environment.GetFolderPath(folder)
                : throw new ConfigException($"saveDirectory の特殊フォルダ名が不明です: {m.Value}"));
        dir = Environment.ExpandEnvironmentVariables(dir);
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(AppContext.BaseDirectory, dir);
        return Path.GetFullPath(dir);
    }

    /// <summary>重複しない保存ファイルパスを作る</summary>
    public string BuildOutputPath(DateTime now)
    {
        string format = string.IsNullOrWhiteSpace(FileNameFormat) ? "sccap_{yyyyMMdd_HHmmss}" : FileNameFormat;
        string name;
        try
        {
            name = Regex.Replace(format, @"\{([^{}]+)\}", m => now.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        catch (FormatException)
        {
            throw new ConfigException($"fileNameFormat の日時書式が不正です: \"{format}\"");
        }

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "sccap";

        string dir = ResolveSaveDirectory();
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, name + ".png");
        for (int i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name}_{i}.png");
        return path;
    }
}
