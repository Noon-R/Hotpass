using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Hotpass.Core.Analysis;
using Hotpass.Core.Model;

namespace Hotpass.App.ViewModels;

public enum ChipRole { None, Viewing, Base, Compare }

/// <summary>タイムライン描画用の 1 スパン。</summary>
public sealed record TimelineItem(string Name, double StartMs, double DurMs, int Depth, GpuQueue Queue, BottleneckCategory Category, long EventId);

/// <summary>予算バーの 1 セグメント(トップレベルパスの時系列順)。</summary>
public sealed record BarSegment(string Name, double Ms, double PctOfFrame, Brush Brush);

/// <summary>開いている 1 キャプチャ。メタ+パス+派生値+チップ表示状態。</summary>
public partial class CaptureViewModel : ObservableObject
{
    public CaptureInfo Info { get; }
    public IReadOnlyList<PassRecord> Passes { get; }
    public FrameSummary Summary { get; }

    /// <summary>実キャプチャ(.wpix)由来の場合のファイルパス。サンプルは null。</summary>
    public string? SourceFilePath { get; }

    [ObservableProperty]
    private ChipRole _role;

    public CaptureViewModel(CaptureInfo info, IReadOnlyList<PassRecord> passes, string? sourceFilePath = null)
    {
        Info = info;
        Passes = passes;
        SourceFilePath = sourceFilePath;
        Summary = FrameAnalyzer.Summarize(info, passes);

        var top = passes.Where(p => p.Depth == 0 && p.Queue == GpuQueue.Graphics).OrderBy(p => p.StartNs).ToList();
        BarSegments = top
            .Select(p => new BarSegment(p.Name, ToMs(p.DurationNs), FrameAnalyzer.PctOfFrame(p, Summary.TotalGpuNs), CategoryMeta.For(p.Category).Brush))
            .ToList();
        Rows = top
            .OrderByDescending(p => p.DurationNs)
            .Select(p => new PassRowViewModel(this, p))
            .ToList();
        TimelineItems = passes
            .Select(p => new TimelineItem(p.Name, ToMs(p.StartNs), ToMs(p.DurationNs), p.Depth, p.Queue, p.Category, p.EventId))
            .ToList();
        UsedCategories = top.Select(p => CategoryMeta.For(p.Category)).DistinctBy(m => m.Label).ToList();
    }

    public IReadOnlyList<PassRowViewModel> Rows { get; }
    public IReadOnlyList<BarSegment> BarSegments { get; }
    public IReadOnlyList<TimelineItem> TimelineItems { get; }
    public IReadOnlyList<CategoryMeta> UsedCategories { get; }

    public static double ToMs(long ns) => ns / 1_000_000.0;

    public double TotalMs => ToMs(Summary.TotalGpuNs);
    public double DeltaMs => ToMs(Summary.BudgetDeltaNs);
    public bool IsOverBudget => Summary.BudgetDeltaNs > 0;

    public string FileName => Info.FileName;
    public string TotalText => TotalMs.ToString("0.0");
    public string DeltaText => (DeltaMs >= 0 ? "+" : "") + DeltaMs.ToString("0.0");
    public string BudgetPillText => IsOverBudget ? $"OVER BUDGET +{DeltaMs:0.0} MS" : $"UNDER BUDGET {DeltaMs:0.0} MS";
    public string SourceBadge => Info.Source == CaptureSource.Pix ? "PIX" : "NSIGHT";
    public bool IsPix => Info.Source == CaptureSource.Pix;
    public string SourceLong => Info.Source == CaptureSource.Pix ? "PIX GPU Capture" : "Nsight GPU Trace";
    public string ChipMeta => $"frame {Info.FrameNumber?.ToString() ?? "?"} · D3D12";

    /// <summary>そのファイルが実際に提供できる項目(design.md §4.1)。フラグから導出する。</summary>
    public string ProvidesText
    {
        get
        {
            var items = new List<string> { "duration" };
            if (Info.ProvidesOccupancy) items.Add("occupancy");
            if (Info.ProvidesLimiter) items.Add("limiter");
            if (Info.ProvidesSol) items.Add("SOL");
            return string.Join(" · ", items);
        }
    }

    public string HeadSubText => $"{SourceLong} · frame {Info.FrameNumber?.ToString() ?? "?"} · {ProvidesText}";
    public string PassCountText => $"{Rows.Count} passes · graphics + async compute";

    public CategoryMeta DominantMeta => CategoryMeta.For(Summary.DominantCategory);
    public string DominantSub => $"{ToMs(Summary.DominantCategoryNs):0.0} ms · {Summary.DominantCategoryPassCount} pass{(Summary.DominantCategoryPassCount > 1 ? "es" : "")}";
    public string WorstName => Summary.WorstPass?.Name ?? "—";
    public string WorstSub => Summary.WorstPass is { } w
        ? $"{ToMs(w.DurationNs):0.0} ms · {FrameAnalyzer.PctOfFrame(w, Summary.TotalGpuNs):0}% of frame"
        : "";
    public string AsyncText => Info.AsyncOverlapPct is { } a ? $"{a:0}%" : "—";
    public string SyncText => Info.SyncGapsNs is { } s ? $"{ToMs(s):0.0} ms" : "—";
}
