using Hotpass.Core.Model;

namespace Hotpass.Adapters.Pix;

/// <summary>
/// マーカー名 / リソース名を指定した RT・バックバッファの PNG 抽出(design.md §2.2 の 2 段構え)。
///   1. マーカー名 → イベントリストから Global ID 範囲を解決
///   2. pixtool recapture-region で範囲切り出し(パス直後の状態を得るため)
///   3. pixtool save-resource で PNG 化
/// フレーム末尾の状態で良い場合(markerName = null)は recapture を省略して直接 save-resource。
/// ※ サブコマンドの正確な引数は PIX バージョンにより異なる可能性があるため、失敗時は
///   pixtool の stdout/stderr をそのまま例外に載せて調整可能にしている。
/// </summary>
public sealed class PixImageExtractor
{
    private readonly PixToolRunner _runner;

    public PixImageExtractor(PixToolRunner runner)
    {
        _runner = runner;
    }

    /// <param name="markerName">パス(マーカー)名。null ならフレーム末尾の状態。</param>
    /// <param name="resourceName">リソース名。null なら pixtool の既定(バックバッファ)。</param>
    public async Task<PassImage> ExtractAsync(
        string wpixPath, string? markerName, string? resourceName, CancellationToken ct = default)
    {
        var workDir = PixCaptureImporter.GetWorkDir(wpixPath);
        var imagesDir = Path.Combine(workDir, "images");
        Directory.CreateDirectory(imagesDir);

        var sourceWpix = wpixPath;
        long? eventId = null;

        if (markerName is not null)
        {
            var (begin, end, id) = ResolveMarkerRange(workDir, markerName);
            eventId = id;
            var regionPath = Path.Combine(imagesDir, $"region_{Sanitize(markerName)}.wpix");
            File.Delete(regionPath);

            var rec = await _runner.RunAsync(
                ["open-capture", wpixPath, "recapture-region", regionPath,
                 $"--begin-event={begin}", $"--end-event={end}"], ct);
            if (!rec.Success || !File.Exists(regionPath))
                throw new InvalidOperationException(
                    $"recapture-region が失敗しました (exit {rec.ExitCode}):\n{rec.StdErr}\n{rec.StdOut}");
            sourceWpix = regionPath;
        }

        var pngName = $"{Sanitize(markerName ?? "frame")}_{Sanitize(resourceName ?? "backbuffer")}.png";
        var pngPath = Path.Combine(imagesDir, pngName);
        File.Delete(pngPath);

        var args = new List<string> { "open-capture", sourceWpix, "save-resource", pngPath };
        if (resourceName is not null) args.Add($"--resource-name={resourceName}");
        var save = await _runner.RunAsync(args, ct);
        if (!save.Success || !File.Exists(pngPath))
            throw new InvalidOperationException(
                $"save-resource が失敗しました (exit {save.ExitCode}):\n{save.StdErr}\n{save.StdOut}");

        return new PassImage
        {
            PassName = markerName,
            ResourceName = resourceName ?? "backbuffer",
            EventId = eventId,
            PngPath = pngPath,
        };
    }

    /// <summary>
    /// インポート時に生成した events.csv からマーカーの Global ID 範囲を解決する。
    /// 範囲 = マーカー自身のイベント ID 〜 次の同深度以下マーカーの直前(無ければ最終イベント)。
    /// </summary>
    internal static (long Begin, long End, long MarkerId) ResolveMarkerRange(string workDir, string markerName)
    {
        var csvPath = Path.Combine(workDir, "events.csv");
        if (!File.Exists(csvPath))
            throw new FileNotFoundException(
                "events.csv が見つかりません。先にキャプチャをインポートしてください。", csvPath);

        var rows = EventListCsv.Parse(csvPath);
        var idx = rows.FindIndex(r => r.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            throw new InvalidOperationException($"マーカー '{markerName}' が見つかりません");

        var marker = rows[idx];
        long end = rows[^1].EventId;
        for (var i = idx + 1; i < rows.Count; i++)
        {
            if (rows[i].Depth <= marker.Depth)
            {
                end = rows[i].EventId - 1;
                break;
            }
        }
        return (marker.EventId, end, marker.EventId);
    }

    private static string Sanitize(string s)
        => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
