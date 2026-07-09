using Hotpass.Core.Model;
using Hotpass.Core.Storage;

namespace Hotpass.Adapters.Pix;

/// <summary>
/// .wpix → pixtool save-event-list → CSV → 共通スキーマ、のインポートパイプライン(design.md §5)。
/// PoC 範囲: duration とマーカー階層。カウンタ由来の bottleneck_category は今後(判定不能は Unknown)。
/// </summary>
public sealed class PixCaptureImporter
{
    private readonly PixToolRunner _runner;

    public PixCaptureImporter(PixToolRunner runner)
    {
        _runner = runner;
    }

    public sealed record ImportResult(CaptureInfo Capture, IReadOnlyList<PassRecord> Passes, string DbPath);

    public async Task<ImportResult> ImportAsync(string wpixPath, CancellationToken ct = default)
    {
        if (!File.Exists(wpixPath))
            throw new FileNotFoundException("キャプチャファイルが見つかりません", wpixPath);

        var workDir = GetWorkDir(wpixPath);
        Directory.CreateDirectory(workDir);
        var csvPath = Path.Combine(workDir, "events.csv");

        var result = await _runner.RunAsync(
            ["open-capture", wpixPath, "save-event-list", csvPath, "--counter-groups=D3D*"], ct);
        if (!result.Success || !File.Exists(csvPath))
        {
            // counter-groups が未対応/失敗の場合は素の一覧で再試行
            result = await _runner.RunAsync(["open-capture", wpixPath, "save-event-list", csvPath], ct);
            if (!result.Success || !File.Exists(csvPath))
                throw new InvalidOperationException(
                    $"pixtool save-event-list が失敗しました (exit {result.ExitCode}):\n{result.StdErr}\n{result.StdOut}");
        }

        var rows = EventListCsv.Parse(csvPath);
        var passes = BuildPasses(rows);

        var capture = new CaptureInfo
        {
            FileName = Path.GetFileName(wpixPath),
            Source = CaptureSource.Pix,
            FrameNumber = null,           // イベントリストからは取れない(取れないものは諦める)
            AsyncOverlapPct = null,       // PoC 未対応
            SyncGapsNs = null,            // PoC 未対応
            ProvidesOccupancy = false,    // カウンタ対応後に true
            ProvidesLimiter = false,
            ProvidesSol = false,
        };

        // 前処理済み SQLite を .wpix の隣にキャッシュ(ビューアは前処理済みデータを読むだけ、の原則)
        var dbPath = Path.Combine(workDir, "hotpass.db");
        File.Delete(dbPath);
        using (var store = new CaptureStore(dbPath))
        {
            var capId = store.AddCapture(capture);
            store.AddPasses(capId, passes);
        }

        return new ImportResult(capture, passes, dbPath);
    }

    /// <summary>キャプチャごとの作業ディレクトリ(CSV・DB・抽出画像の置き場)。</summary>
    public static string GetWorkDir(string wpixPath)
        => wpixPath + ".hotpass";

    /// <summary>
    /// イベント行 → PassRecord。Depth 列があれば階層をそのまま採用し、親子は走査で復元する。
    /// StartNs が無い場合はトップレベルを順に積み上げる。
    /// </summary>
    internal static List<PassRecord> BuildPasses(IReadOnlyList<EventRow> rows)
    {
        var passes = new List<PassRecord>();
        var stack = new Stack<PassRecord>();   // 深さ順の祖先
        double cursorNs = 0;
        long nextId = 1;

        foreach (var row in rows)
        {
            // duration の無い行(状態設定 API コール等)はトリアージ対象外
            if (row.DurationNs is not { } dur || dur <= 0) continue;

            while (stack.Count > row.Depth) stack.Pop();
            var parent = stack.Count > 0 ? stack.Peek() : null;

            var start = row.StartNs ?? (parent?.StartNs ?? (long)cursorNs);
            var p = new PassRecord
            {
                Id = nextId++,
                Name = row.Name,
                EventId = row.EventId,
                StartNs = (long)start,
                EndNs = (long)(start + dur),
                DurationNs = (long)dur,
                Depth = row.Depth,
                ParentId = parent?.Id,
                Queue = GpuQueue.Graphics,               // キュー判別はカウンタ/キュー列対応後
                Category = BottleneckCategory.Unknown,   // カウンタ判定は §7-5 で
            };
            passes.Add(p);
            stack.Push(p);

            if (row.Depth == 0 && row.StartNs is null)
                cursorNs += dur;
        }
        return passes;
    }
}
