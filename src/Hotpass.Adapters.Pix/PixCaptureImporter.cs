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

        // 時間カウンタ(TOP to EOP Duration / Execution Start Time 等)を含めて出力する。
        // パターンが未対応のバージョン向けに素の一覧へフォールバック。
        var result = await _runner.RunAsync(
            ["open-capture", wpixPath, "save-event-list", csvPath, "--counters=*Duration*", "--counters=*Start*"], ct);
        if (!result.Success || !File.Exists(csvPath))
        {
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
    /// イベント行 → PassRecord。
    /// 階層は Parent 列(親の連番 ID)から復元する。マーカー(PIXBeginEvent)は自身の
    /// duration を持たないため、配下 GPU 操作の合計/範囲を集計して 1 パスにする。
    /// 残すのは「子を持つ行(=マーカー)」と「duration &gt; 0 の GPU 操作」のみ
    /// (SetXxx 等の状態設定コールはトリアージ対象外)。
    /// </summary>
    internal static List<PassRecord> BuildPasses(IReadOnlyList<EventRow> rows)
    {
        var childrenOf = new Dictionary<long, List<EventRow>>();
        var roots = new List<EventRow>();
        var byId = rows.ToDictionary(r => r.RowId);
        foreach (var r in rows)
        {
            if (r.ParentRowId is { } p && byId.ContainsKey(p))
            {
                if (!childrenOf.TryGetValue(p, out var list)) childrenOf[p] = list = [];
                list.Add(r);
            }
            else
            {
                roots.Add(r);
            }
        }

        var passes = new List<PassRecord>();
        long nextId = 1;
        double cursorNs = 0; // start が取れないキャプチャ用の積み上げカーソル(トップレベルのみ)

        void Walk(EventRow row, PassRecord? parent, int depth)
        {
            var kids = childrenOf.TryGetValue(row.RowId, out var list) ? list : null;
            var isMarker = kids is { Count: > 0 };
            var ownDur = row.DurationNs ?? 0;
            if (!isMarker && ownDur <= 0) return;

            double start, dur;
            if (isMarker)
            {
                if (ownDur > 0)
                {
                    // PIX がマーカー行に集計済みの時間を出している場合はそれを採用
                    start = row.StartNs ?? parent?.StartNs ?? cursorNs;
                    dur = ownDur;
                }
                else
                {
                    // 集計が無い場合は配下 GPU 操作から復元(start は最小、end は最大、無ければ合計)
                    var agg = Aggregate(row);
                    if (agg.TotalDur <= 0) return; // GPU 時間ゼロのマーカーは畳む
                    start = agg.MinStart ?? parent?.StartNs ?? cursorNs;
                    dur = agg.MinStart is not null && agg.MaxEnd is not null
                        ? agg.MaxEnd.Value - agg.MinStart.Value
                        : agg.TotalDur;
                }
            }
            else
            {
                start = row.StartNs ?? parent?.StartNs ?? cursorNs;
                dur = ownDur;
            }

            var pass = new PassRecord
            {
                Id = nextId++,
                Name = row.Name,
                EventId = row.RowId,                     // PIX UI の event # に対応
                StartNs = (long)start,
                EndNs = (long)(start + dur),
                DurationNs = (long)dur,
                Depth = depth,
                ParentId = parent?.Id,
                Queue = GpuQueue.Graphics,               // キュー判別は複数キュー対応時に
                Category = BottleneckCategory.Unknown,   // カウンタ判定は §7-5 で
            };
            passes.Add(pass);

            if (depth == 0 && row.StartNs is null) cursorNs += dur;

            if (kids is not null)
                foreach (var k in kids)
                    Walk(k, pass, depth + 1);
        }

        (double TotalDur, double? MinStart, double? MaxEnd) Aggregate(EventRow row)
        {
            double total = row.DurationNs ?? 0;
            double? minStart = row.StartNs;
            double? maxEnd = row.StartNs is { } s && row.DurationNs is { } d ? s + d : null;
            if (childrenOf.TryGetValue(row.RowId, out var kids))
            {
                foreach (var k in kids)
                {
                    var a = Aggregate(k);
                    total += a.TotalDur;
                    if (a.MinStart is { } ms && (minStart is null || ms < minStart)) minStart = ms;
                    if (a.MaxEnd is { } me && (maxEnd is null || me > maxEnd)) maxEnd = me;
                }
            }
            return (total, minStart, maxEnd);
        }

        foreach (var r in roots)
            Walk(r, null, 0);
        return passes;
    }
}
