namespace Hotpass.Core.Model;

/// <summary>律速カテゴリ 7 分類(design.md §3.2)。判定不能は Unknown。</summary>
public enum BottleneckCategory
{
    Raster,
    Texture,
    Memory,
    Compute,
    Geometry,
    Sync,
    Unknown,
}

public enum GpuQueue
{
    Graphics,
    AsyncCompute,
    Copy,
}

public enum CaptureSource
{
    Pix,
    Nsight,
}
