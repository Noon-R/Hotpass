namespace Hotpass.Core.Model;

/// <summary>開いている 1 キャプチャのメタ情報と、そのソースが提供できる項目のフラグ。</summary>
public sealed class CaptureInfo
{
    public long Id { get; set; }

    public required string FileName { get; init; }

    public CaptureSource Source { get; init; }

    public long? FrameNumber { get; init; }

    /// <summary>async compute が graphics と重なっていた割合 %(算出不能なら null)。</summary>
    public double? AsyncOverlapPct { get; init; }

    /// <summary>キュー同期でGPUが遊んでいた合計時間(算出不能なら null)。</summary>
    public long? SyncGapsNs { get; init; }

    public bool ProvidesOccupancy { get; init; }
    public bool ProvidesLimiter { get; init; }
    public bool ProvidesSol { get; init; }
}
