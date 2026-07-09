namespace Hotpass.Adapters.Pix;

/// <summary>pixtool.exe の検出。既定インストール先の最新バージョンディレクトリを探す。</summary>
public static class PixToolLocator
{
    /// <summary>環境変数 HOTPASS_PIXTOOL でフルパスを明示指定できる。</summary>
    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("HOTPASS_PIXTOOL");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            return overridePath;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft PIX");
        if (!Directory.Exists(root)) return null;

        // バージョンディレクトリ(例 2603.25)を新しい順に走査
        return Directory.EnumerateDirectories(root)
            .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
            .Select(d => Path.Combine(d, "pixtool.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static Version ParseVersion(string name)
        => Version.TryParse(name, out var v) ? v : new Version(0, 0);
}
