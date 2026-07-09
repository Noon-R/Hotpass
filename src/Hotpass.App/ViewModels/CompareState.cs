using System.Windows;
using System.Windows.Media;
using Hotpass.Core.Model;

namespace Hotpass.App.ViewModels;

public enum DeltaState { Good, Bad, Zero }

/// <summary>Per-pass change の 1 行。Δ = compare − base(緑▼=速い、赤▲=遅い)。ネスト行は Depth &gt; 0。</summary>
public sealed class CompareRow
{
    public required string Name { get; init; }
    public double? BaseMs { get; init; }
    public double? CompareMs { get; init; }
    public double? DeltaMs { get; init; }
    public required Brush CatBrush { get; init; }
    public bool IsNew { get; init; }
    public bool IsGone { get; init; }

    /// <summary>マーカーのネスト深さ(トップレベル = 0)。</summary>
    public int Depth { get; init; }

    /// <summary>同名兄弟を集計した場合の件数(base/compare の大きい方)。1 なら非表示。</summary>
    public int SiblingCount { get; init; } = 1;

    public Thickness Indent => new(Depth * 20, 0, 0, 0);
    public bool IsNested => Depth > 0;
    public string CountText => SiblingCount > 1 ? $"×{SiblingCount}" : "";

    public string BaseText => BaseMs?.ToString("0.0") ?? "—";
    public string CompareText => CompareMs?.ToString("0.0") ?? "—";
    public string DeltaText => DeltaMs is { } d ? (d > 0 ? "+" : "") + d.ToString("0.0") : "—";
    public DeltaState State => DeltaMs switch
    {
        null => DeltaState.Zero,
        < -0.05 => DeltaState.Good,
        > 0.05 => DeltaState.Bad,
        _ => DeltaState.Zero,
    };

    /// <summary>発散バーの片側幅(0..1、最大変化量で正規化)。</summary>
    public double BarFraction { get; init; }
}

/// <summary>Compare モードの計算結果一式。base/compare が揃ったときのみ生成。</summary>
public sealed class CompareState
{
    public required CaptureViewModel Base { get; init; }
    public required CaptureViewModel Compare { get; init; }
    public required IReadOnlyList<CompareRow> Rows { get; init; }

    public double NetMs => Compare.TotalMs - Base.TotalMs;
    public string NetText => (NetMs > 0 ? "+" : "") + NetMs.ToString("0.0") + " ms";
    public DeltaState NetState => NetMs < -0.05 ? DeltaState.Good : NetMs > 0.05 ? DeltaState.Bad : DeltaState.Zero;
    public string FrameText => $"{Base.TotalMs:0.0} → {Compare.TotalMs:0.0} ms";
    public string FrameSub =>
        $"base {(Base.IsOverBudget ? "over +" + Base.DeltaMs.ToString("0.0") : "under " + Base.DeltaMs.ToString("0.0"))}" +
        $" → compare {(Compare.IsOverBudget ? "over +" + Compare.DeltaMs.ToString("0.0") : "under " + Compare.DeltaMs.ToString("0.0"))}";
    public string NetSub
    {
        get
        {
            var s = NetMs < -0.05 ? "compare is faster" : NetMs > 0.05 ? "compare is slower" : "no net change";
            if (!Compare.IsOverBudget && Base.IsOverBudget) s += " · now within budget";
            return s;
        }
    }

    public CompareRow? Biggest => Rows.FirstOrDefault(r => r is { DeltaMs: not null, Depth: 0 });
    public string BiggestName => Biggest?.Name ?? "—";
    public string BiggestText => Biggest?.DeltaText is { } t and not "—" ? t + " ms" : "";

    /// <summary>クロスツール比較の正直さ(design.md §4.3)。</summary>
    public string Note => Base.Info.Source == Compare.Info.Source
        ? $"Both captures are {Base.SourceBadge} — durations, occupancy and {(Base.IsPix ? "limiter" : "SOL")} are directly comparable."
        : $"Cross-tool compare ({Base.SourceBadge} vs {Compare.SourceBadge}) — duration and occupancy are comparable; limiter and SOL are tool-specific and shown per file in single view, not diffed here.";

    /// <summary>集計用の階層ノード(同名兄弟は 1 ノードに合算)。</summary>
    private sealed record Node(string Name, double Ms, BottleneckCategory Cat, int Count, IReadOnlyList<Node> Children);

    public static CompareState Create(CaptureViewModel baseCap, CaptureViewModel cmpCap)
    {
        var baseTree = BuildTree(baseCap.Passes);
        var cmpTree = BuildTree(cmpCap.Passes);

        var flat = new List<CompareRow>();

        // 正規化スケールは全階層の最大 |Δ|(相殺で親より子の変化が大きいケースがあるため)
        var all = new List<(Node? B, Node? C, int Depth)>();
        void CollectAll(IReadOnlyList<Node> b, IReadOnlyList<Node> c, int depth)
        {
            var level = new List<(Node? B, Node? C, int Depth)>();
            CollectLevel(b, c, depth, level);
            all.AddRange(level);
            foreach (var (nb, nc, d) in level)
                CollectAll(nb?.Children ?? [], nc?.Children ?? [], d + 1);
        }
        CollectAll(baseTree, cmpTree, 0);
        var maxAbs = Math.Max(all.Max(p => Math.Abs(Delta(p.B, p.C) ?? 0)), 0.1);

        void Emit(IReadOnlyList<Node> b, IReadOnlyList<Node> c, int depth)
        {
            var level = new List<(Node? B, Node? C, int Depth)>();
            CollectLevel(b, c, depth, level);
            foreach (var (nb, nc, d) in level
                .OrderByDescending(x => Math.Abs(Delta(x.B, x.C) ?? 0)))
            {
                var node = (nc ?? nb)!;
                var delta = Delta(nb, nc);
                flat.Add(new CompareRow
                {
                    Name = node.Name,
                    BaseMs = nb?.Ms,
                    CompareMs = nc?.Ms,
                    DeltaMs = delta,
                    CatBrush = d == 0
                        ? CategoryMeta.For(node.Cat).Brush
                        : new SolidColorBrush(CategoryMeta.Tint(CategoryMeta.For(node.Cat).Color, d)),
                    IsNew = nb is null,
                    IsGone = nc is null,
                    Depth = d,
                    SiblingCount = Math.Max(nb?.Count ?? 0, nc?.Count ?? 0),
                    BarFraction = delta is { } dd ? Math.Abs(dd) / maxAbs : 0,
                });
                // 子は「親の直下」に出す(親ごとの塊を保つため、レベル横断ではなく再帰)
                Emit(nb?.Children ?? [], nc?.Children ?? [], d + 1);
            }
        }
        Emit(baseTree, cmpTree, 0);

        return new CompareState { Base = baseCap, Compare = cmpCap, Rows = flat };
    }

    private static double? Delta(Node? b, Node? c)
        => b is not null && c is not null ? c.Ms - b.Ms : null;

    private static void CollectLevel(
        IReadOnlyList<Node> b, IReadOnlyList<Node> c, int depth, List<(Node?, Node?, int)> into)
    {
        var byNameB = b.ToDictionary(n => n.Name);
        var byNameC = c.ToDictionary(n => n.Name);
        foreach (var name in b.Select(n => n.Name).Union(c.Select(n => n.Name)))
            into.Add((byNameB.GetValueOrDefault(name), byNameC.GetValueOrDefault(name), depth));
    }

    /// <summary>
    /// PassRecord 列(ParentId 連結)→ 名前ベースの比較ツリー。
    /// 同名兄弟(例: 連続する DrawIndexedInstanced)は 1 ノードに合算し件数を持つ。
    /// graphics キューのみ対象(async はフレーム時間に直接効かないため diff 対象外)。
    /// </summary>
    private static IReadOnlyList<Node> BuildTree(IReadOnlyList<PassRecord> passes)
    {
        var children = passes
            .Where(p => p.ParentId is not null)
            .GroupBy(p => p.ParentId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PassRecord>)[.. g]);

        List<Node> Build(IEnumerable<PassRecord> level) =>
            [.. level
                .GroupBy(p => p.Name)
                .Select(g => new Node(
                    g.Key,
                    g.Sum(p => p.DurationNs) / 1_000_000.0,
                    g.First().Category,
                    g.Count(),
                    Build(g.SelectMany(p => children.GetValueOrDefault(p.Id, [])))))];

        return Build(passes.Where(p => p.Depth == 0 && p.Queue == GpuQueue.Graphics));
    }
}
