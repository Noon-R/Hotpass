namespace Hotpass.Core.Model;

/// <summary>
/// パス単位の正規化レコード(design.md §3.2)。
/// ソースにより取得不能な項目は null(UI では「—」表示)。
/// </summary>
public sealed class PassRecord
{
    public long Id { get; set; }

    /// <summary>マーカー名。必須。</summary>
    public required string Name { get; init; }

    /// <summary>PIX への導線キー(event id / Global ID)。</summary>
    public long EventId { get; init; }

    public long StartNs { get; init; }
    public long EndNs { get; init; }

    /// <summary>GPU 時間。全ソース共通の必須項目でトリアージの主役。</summary>
    public long DurationNs { get; init; }

    /// <summary>マーカーのネスト深さ(トップレベル = 0)。</summary>
    public int Depth { get; init; }

    /// <summary>ネスト親の PassRecord.Id。トップレベルは null。</summary>
    public long? ParentId { get; set; }

    public GpuQueue Queue { get; init; } = GpuQueue.Graphics;

    public BottleneckCategory Category { get; init; } = BottleneckCategory.Unknown;

    /// <summary>占有率 %。PIX / Nsight 両方で取得可(取れなければ null)。</summary>
    public double? OccupancyPct { get; init; }

    /// <summary>占有率の律速要因(例: "VGPR limited")。PIX 系のみ。</summary>
    public string? OccupancyLimiter { get; init; }

    /// <summary>最繁ハードウェアユニット(例: "TEX 82%")。Nsight 系のみ。</summary>
    public string? SolTopUnit { get; init; }
}
