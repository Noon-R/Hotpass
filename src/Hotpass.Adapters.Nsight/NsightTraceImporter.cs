using Hotpass.Core.Model;
using Hotpass.Core.Storage;

namespace Hotpass.Adapters.Nsight;

/// <summary>
/// Nsight GPU Trace エクスポート CSV → 共通スキーマ、のインポートパイプライン(design.md §5)。
/// pixtool と違い外部プロセスは不要で、ユーザが GPU Trace からエクスポートした CSV を直接読む。
/// 可用性(design.md §3.4): duration ○ / occupancy ○ / SOL △(列があれば) / limiter ×。
/// </summary>
public sealed class NsightTraceImporter
{
    public sealed record ImportResult(CaptureInfo Capture, IReadOnlyList<PassRecord> Passes, string DbPath);

    public ImportResult Import(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("エクスポート CSV が見つかりません", csvPath);

        var rows = TraceCsv.Parse(csvPath);
        var passes = BuildPasses(rows);
        if (passes.Count == 0)
            throw new InvalidDataException(
                "GPU 時間を持つ行が見つかりません。GPU Trace の範囲/マーカー単位のメトリクス表をエクスポートしてください。");

        var capture = new CaptureInfo
        {
            FileName = Path.GetFileName(csvPath),
            Source = CaptureSource.Nsight,
            FrameNumber = null,           // エクスポートには含まれない(取れないものは諦める)
            AsyncOverlapPct = null,
            SyncGapsNs = null,
            ProvidesOccupancy = passes.Any(p => p.OccupancyPct is not null),
            ProvidesLimiter = false,      // limiter は PIX 系のみ(design.md §3.4)
            ProvidesSol = passes.Any(p => p.SolTopUnit is not null),
        };

        // 前処理済み SQLite を CSV の隣にキャッシュ(PIX アダプタと同じ規約)
        var workDir = GetWorkDir(csvPath);
        Directory.CreateDirectory(workDir);
        var dbPath = Path.Combine(workDir, "hotpass.db");
        File.Delete(dbPath);
        using (var store = new CaptureStore(dbPath))
        {
            var capId = store.AddCapture(capture);
            store.AddPasses(capId, passes);
        }

        return new ImportResult(capture, passes, dbPath);
    }

    /// <summary>キャプチャごとの作業ディレクトリ(DB の置き場)。</summary>
    public static string GetWorkDir(string csvPath)
        => csvPath + ".hotpass";

    /// <summary>
    /// トレース行 → PassRecord。
    /// 階層は Depth/Nesting Level 列から復元する(行順+深さのスタック)。duration の無い行は
    /// トリアージ対象外として落とす(GPU Trace の範囲行は原則 duration を持つ)。
    /// start が無いエクスポート向けに、トップレベルのみ積み上げカーソルで時間軸を合成する。
    /// </summary>
    internal static List<PassRecord> BuildPasses(IReadOnlyList<TraceRow> rows)
    {
        rows = UnwrapFrameRoot(rows);
        var passes = new List<PassRecord>();
        var stack = new Stack<(int Depth, PassRecord Pass)>();
        long nextId = 1;
        double cursorNs = 0;

        foreach (var row in rows)
        {
            var dur = row.DurationNs ?? 0;
            if (dur <= 0) continue;

            var depth = Math.Max(row.Depth ?? 0, 0);
            while (stack.Count > 0 && stack.Peek().Depth >= depth) stack.Pop();
            var parent = stack.Count > 0 ? stack.Peek().Pass : null;
            // Depth 列に飛びがあっても UI が壊れないよう、実効 depth は親 +1 に丸める
            var effDepth = parent is null ? 0 : parent.Depth + 1;

            var start = row.StartNs ?? parent?.StartNs ?? cursorNs;
            var (category, sol) = TopThroughput(row.Throughputs);

            var pass = new PassRecord
            {
                Id = nextId++,
                Name = row.Name,
                EventId = row.RowId,                 // PIX への導線は無いが、行の識別子として保持
                StartNs = (long)start,
                EndNs = (long)(start + dur),
                DurationNs = (long)dur,
                Depth = effDepth,
                ParentId = parent?.Id,
                Queue = MapQueue(row.QueueName),
                Category = category,
                OccupancyPct = row.OccupancyPct,
                OccupancyLimiter = null,
                SolTopUnit = sol,
            };
            passes.Add(pass);

            if (effDepth == 0 && row.StartNs is null) cursorNs += dur;
            stack.Push((depth, pass));
        }
        return passes;
    }

    /// <summary>
    /// GPU Trace エクスポートはフレーム全体を包むルート行(例 "Frame 812")を持つことが多く、
    /// そのままだと Breakdown がラッパ 1 行に潰れて律速も読めない。graphics キューのトップレベルが
    /// 単一で配下を持つ場合はラッパとみなして畳み、子を 1 段昇格させる(入れ子ラッパにも対応)。
    /// 複数フレームのエクスポート(トップレベルに Frame 行が並ぶ形)は畳まない。
    /// </summary>
    internal static IReadOnlyList<TraceRow> UnwrapFrameRoot(IReadOnlyList<TraceRow> rows)
    {
        var list = rows.ToList();
        while (true)
        {
            var rootIdx = -1;
            var rootCount = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if ((r.DurationNs ?? 0) <= 0) continue;
                if (Math.Max(r.Depth ?? 0, 0) != 0) continue;
                if (MapQueue(r.QueueName) != GpuQueue.Graphics) continue;
                rootCount++;
                rootIdx = i;
            }
            if (rootCount != 1) return list;

            // 配下 = ルート直後に続く depth > 0 の行(depth 列の階層は位置に基づく)
            var end = rootIdx + 1;
            while (end < list.Count && Math.Max(list[end].Depth ?? 0, 0) > 0) end++;
            if (end == rootIdx + 1) return list;

            for (var i = rootIdx + 1; i < end; i++)
                list[i] = list[i] with { Depth = Math.Max(list[i].Depth ?? 0, 0) - 1 };
            list.RemoveAt(rootIdx);
        }
    }

    /// <summary>最繁ユニットを選び、カテゴリと表示文字列(例 "TEX 82%")に落とす。</summary>
    internal static (BottleneckCategory Category, string? SolTopUnit) TopThroughput(IReadOnlyList<UnitThroughput> throughputs)
    {
        if (throughputs.Count == 0) return (BottleneckCategory.Unknown, null);
        var top = throughputs.MaxBy(t => t.Pct)!;
        return (CategoryForUnit(top.Unit), $"{top.Unit} {top.Pct:0}%");
    }

    /// <summary>
    /// NVIDIA ハードウェアユニット名 → 律速カテゴリ 7 分類。
    /// TEX 系はメモリ階層(L1TEX)より先に判定し、SM は最後(SMSP 等の派生名を拾うため)。
    /// </summary>
    internal static BottleneckCategory CategoryForUnit(string unit)
    {
        var u = unit.ToUpperInvariant();
        bool Has(params string[] keys) => keys.Any(u.Contains);
        if (Has("TEX")) return BottleneckCategory.Texture;                                  // TEX, L1TEX
        if (Has("L2", "LTS", "VRAM", "DRAM", "FB", "MSS", "MEM")) return BottleneckCategory.Memory;
        if (Has("RASTER", "PROP", "ROP")) return BottleneckCategory.Raster;                 // ROP は ZROP/CROP を含む
        if (Has("PD", "VAF", "VPC", "PES", "PRIM", "GEOM", "IDX", "TESS", "GS")) return BottleneckCategory.Geometry;
        if (Has("SM", "FMA", "ALU", "TENSOR", "SHADER")) return BottleneckCategory.Compute; // SM, SMSP
        return BottleneckCategory.Unknown;
    }

    internal static GpuQueue MapQueue(string? queueName)
    {
        if (queueName is null) return GpuQueue.Graphics;
        if (queueName.Contains("copy", StringComparison.OrdinalIgnoreCase) ||
            queueName.Contains("transfer", StringComparison.OrdinalIgnoreCase)) return GpuQueue.Copy;
        if (queueName.Contains("compute", StringComparison.OrdinalIgnoreCase)) return GpuQueue.AsyncCompute;
        return GpuQueue.Graphics;
    }
}
