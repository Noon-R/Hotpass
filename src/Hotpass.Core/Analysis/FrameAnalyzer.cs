using Hotpass.Core.Model;

namespace Hotpass.Core.Analysis;

/// <summary>正規化済みパス列からフレーム単位の派生値を計算する(design.md §3.3)。</summary>
public static class FrameAnalyzer
{
    /// <summary>60fps 予算。</summary>
    public const long DefaultBudgetNs = 16_600_000;

    /// <summary>
    /// フレームサマリを計算する。集計対象はトップレベル(Depth == 0)の graphics キューのパス。
    /// ネスト子は親に内包されるため合計に入れない。async compute は合計時間に足さない(重なるため)。
    /// </summary>
    public static FrameSummary Summarize(CaptureInfo capture, IReadOnlyList<PassRecord> passes, long budgetNs = DefaultBudgetNs)
    {
        var top = passes.Where(p => p.Depth == 0 && p.Queue == GpuQueue.Graphics).ToList();
        var totalNs = top.Sum(p => p.DurationNs);

        var dominant = top
            .GroupBy(p => p.Category)
            .Select(g => (Category: g.Key, Ns: g.Sum(p => p.DurationNs), Count: g.Count()))
            .OrderByDescending(x => x.Ns)
            .FirstOrDefault();

        return new FrameSummary
        {
            TotalGpuNs = totalNs,
            BudgetDeltaNs = totalNs - budgetNs,
            DominantCategory = dominant.Ns > 0 ? dominant.Category : BottleneckCategory.Unknown,
            DominantCategoryNs = dominant.Ns,
            DominantCategoryPassCount = dominant.Count,
            WorstPass = top.OrderByDescending(p => p.DurationNs).FirstOrDefault(),
            AsyncOverlapPct = capture.AsyncOverlapPct,
            SyncGapsNs = capture.SyncGapsNs,
        };
    }

    /// <summary>パスのフレーム内比率 %(合計 0 のときは 0)。</summary>
    public static double PctOfFrame(PassRecord pass, long totalNs)
        => totalNs <= 0 ? 0 : (double)pass.DurationNs / totalNs * 100.0;
}
