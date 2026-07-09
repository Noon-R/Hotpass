using Hotpass.Core.Model;

namespace Hotpass.Adapters.Pix;

/// <summary>
/// マーカー名 / Global ID を指定した RT・バックバッファ・深度の PNG 抽出。
///
/// pixtool 2603.25 の実仕様(--help save-resource で確認):
///   - `save-resource out.png --marker=&lt;名前&gt;` … そのマーカー配下で対象リソースが
///     バインドされた最後のイベント時点の内容を保存(= パス直後の状態が直接取れる)
///   - `--global-id=&lt;GID&gt;` … イベント指定
///   - リソース選択は `--rtv=&lt;index&gt;`(既定 0)または `--depth`(深度バッファ)
///   - 指定なしなら「そのリソースが最後にバインドされたイベント」= 実質バックバッファ最終状態
///
/// design.md §2.2 の recapture-region 2段構え(--start/--end)は、マーカー指定で足りない
/// ケース(範囲切り出しキャプチャ自体が欲しい場合)のために ExtractRegionAsync として残す。
/// </summary>
public sealed class PixImageExtractor
{
    private readonly PixToolRunner _runner;

    public PixImageExtractor(PixToolRunner runner)
    {
        _runner = runner;
    }

    /// <param name="markerName">パス(マーカー)名。null ならフレーム末尾の状態。</param>
    /// <param name="rtvIndex">保存する RenderTargetView のインデックス(既定 0)。</param>
    /// <param name="depth">true で深度バッファの可視化 PNG を保存。</param>
    public async Task<PassImage> ExtractAsync(
        string wpixPath, string? markerName, int rtvIndex = 0, bool depth = false, CancellationToken ct = default)
    {
        var imagesDir = Path.Combine(PixCaptureImporter.GetWorkDir(wpixPath), "images");
        Directory.CreateDirectory(imagesDir);

        var resourceLabel = depth ? "depth" : $"rtv{rtvIndex}";
        var pngPath = Path.Combine(imagesDir, $"{Sanitize(markerName ?? "frame")}_{resourceLabel}.png");
        File.Delete(pngPath);

        var args = new List<string> { "open-capture", wpixPath, "save-resource", pngPath };
        if (markerName is not null) args.Add($"--marker={markerName}");
        if (depth) args.Add("--depth");
        else if (rtvIndex != 0) args.Add($"--rtv={rtvIndex}");

        var save = await _runner.RunAsync(args, ct);
        if ((!save.Success || !File.Exists(pngPath)) && markerName is not null)
        {
            // pixtool はスペース入りマーカー名の引数を受け付けないことがある。
            // その場合はマーカー配下の最終 Global ID 指定にフォールバック
            // (--marker の意味 = 「配下で対象リソースが最後にバインドされたイベント」の近似)。
            var (_, end, _) = ResolveMarkerRange(PixCaptureImporter.GetWorkDir(wpixPath), markerName);
            args = ["open-capture", wpixPath, "save-resource", pngPath, $"--global-id={end}"];
            if (depth) args.Add("--depth");
            else if (rtvIndex != 0) args.Add($"--rtv={rtvIndex}");
            save = await _runner.RunAsync(args, ct);
        }
        if (!save.Success || !File.Exists(pngPath))
            throw new InvalidOperationException(
                $"save-resource が失敗しました (exit {save.ExitCode}):\n{save.StdErr}\n{save.StdOut}");

        return new PassImage
        {
            PassName = markerName,
            ResourceName = resourceLabel,
            EventId = null,
            PngPath = pngPath,
        };
    }

    /// <summary>
    /// マーカーの Global ID 範囲を recapture-region で小さな .wpix に切り出す
    /// (他チームへの再現ファイル共有や、マーカー指定で取れないリソースの調査用)。
    /// </summary>
    public async Task<string> ExtractRegionAsync(string wpixPath, string markerName, CancellationToken ct = default)
    {
        var workDir = PixCaptureImporter.GetWorkDir(wpixPath);
        var (begin, end, _) = ResolveMarkerRange(workDir, markerName);
        var regionPath = Path.Combine(workDir, $"region_{Sanitize(markerName)}.wpix");
        File.Delete(regionPath);

        var rec = await _runner.RunAsync(
            ["open-capture", wpixPath, "recapture-region", regionPath, $"--start={begin}", $"--end={end}"], ct);
        if (!rec.Success || !File.Exists(regionPath))
            throw new InvalidOperationException(
                $"recapture-region が失敗しました (exit {rec.ExitCode}):\n{rec.StdErr}\n{rec.StdOut}");
        return regionPath;
    }

    /// <summary>
    /// インポート時に生成した events.csv からマーカー配下の Global ID 範囲を解決する。
    /// recapture-region の --start/--end は Global ID を取るため、
    /// マーカーのサブツリーに含まれる GPU 操作の Global ID の最小〜最大を返す。
    /// </summary>
    internal static (long Begin, long End, long MarkerRowId) ResolveMarkerRange(string workDir, string markerName)
    {
        var csvPath = Path.Combine(workDir, "events.csv");
        if (!File.Exists(csvPath))
            throw new FileNotFoundException(
                "events.csv が見つかりません。先にキャプチャをインポートしてください。", csvPath);

        var rows = EventListCsv.Parse(csvPath);
        var marker = rows.FirstOrDefault(r => r.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"マーカー '{markerName}' が見つかりません");

        var childrenOf = rows.Where(r => r.ParentRowId is not null)
            .GroupBy(r => r.ParentRowId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        long? min = null, max = null;
        void Visit(EventRow r)
        {
            if (r.GlobalId is { } g)
            {
                if (min is null || g < min) min = g;
                if (max is null || g > max) max = g;
            }
            if (childrenOf.TryGetValue(r.RowId, out var kids))
                foreach (var k in kids) Visit(k);
        }
        Visit(marker);

        if (min is null)
            throw new InvalidOperationException($"マーカー '{markerName}' 配下に GPU 操作(Global ID)がありません");
        return (min.Value, max!.Value, marker.RowId);
    }

    private static string Sanitize(string s)
        => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
