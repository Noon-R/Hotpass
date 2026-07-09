using Hotpass.Core.Model;

namespace Hotpass.Core.Analysis;

/// <summary>フレーム単位サマリ(design.md §3.3)。取得不能な項目は null。</summary>
public sealed class FrameSummary
{
    /// <summary>トップレベル graphics パスの合計 GPU 時間。</summary>
    public required long TotalGpuNs { get; init; }

    /// <summary>予算(16.6ms 等)との差。正 = 超過。</summary>
    public required long BudgetDeltaNs { get; init; }

    /// <summary>支配的な律速カテゴリ(カテゴリ別 ms 合計の最大)。</summary>
    public required BottleneckCategory DominantCategory { get; init; }

    /// <summary>支配的カテゴリの合計時間。</summary>
    public required long DominantCategoryNs { get; init; }

    /// <summary>支配的カテゴリに属するパス数。</summary>
    public required int DominantCategoryPassCount { get; init; }

    /// <summary>最重パス。パスが 1 つも無い場合のみ null。</summary>
    public PassRecord? WorstPass { get; init; }

    public double? AsyncOverlapPct { get; init; }

    public long? SyncGapsNs { get; init; }
}
