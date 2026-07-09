namespace Hotpass.Core.Model;

/// <summary>
/// パス/リソースに紐付く抽出済み画像(pixtool recapture-region + save-resource の成果物)。
/// </summary>
public sealed class PassImage
{
    public long Id { get; set; }

    public long CaptureId { get; init; }

    /// <summary>どのパス(マーカー)直後の状態か。フレーム末尾なら null。</summary>
    public string? PassName { get; init; }

    /// <summary>抽出したリソース名(RT / バックバッファ等)。</summary>
    public string? ResourceName { get; init; }

    public long? EventId { get; init; }

    public required string PngPath { get; init; }
}
