using System.Windows.Media;
using Hotpass.Core.Model;

namespace Hotpass.App;

/// <summary>律速カテゴリの表示メタ(ラベル・説明・色)。色は Themes/Dark.xaml と同値。</summary>
public sealed record CategoryMeta(string Label, string Why, Color Color)
{
    public SolidColorBrush Brush { get; } = Freeze(Color);

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // Instrument スキン: 暖色アクセント(#EFA13C)と衝突しないよう memory=クリムゾン / compute=マスタード
    private static readonly Dictionary<BottleneckCategory, CategoryMeta> Map = new()
    {
        [BottleneckCategory.Raster] = new("Raster", "Fixed-function raster / depth bound", (Color)ColorConverter.ConvertFromString("#5B8DEF")),
        [BottleneckCategory.Texture] = new("Texture", "Texture fetch / sampler bound", (Color)ColorConverter.ConvertFromString("#9D7BE0")),
        [BottleneckCategory.Memory] = new("Memory", "VRAM bandwidth / L2 miss bound", (Color)ColorConverter.ConvertFromString("#E14B4B")),
        [BottleneckCategory.Compute] = new("Compute", "ALU / SM math bound", (Color)ColorConverter.ConvertFromString("#C9A227")),
        [BottleneckCategory.Geometry] = new("Geometry", "Geometry / vertex bound", (Color)ColorConverter.ConvertFromString("#2FB98B")),
        [BottleneckCategory.Sync] = new("Sync/Idle", "GPU starved — waiting on a queue", (Color)ColorConverter.ConvertFromString("#7E8794")),
        [BottleneckCategory.Unknown] = new("Unknown", "Not enough data to classify", (Color)ColorConverter.ConvertFromString("#5E6672")),
    };

    public static CategoryMeta For(BottleneckCategory cat) => Map[cat];

    /// <summary>ネスト深さに応じて親色を暗い側へ寄せたトーン(タイムラインの子スパン用)。</summary>
    public static Color Tint(Color c, int depth)
    {
        if (depth <= 0) return c;
        var f = depth == 1 ? 0.30 : 0.48;
        return Color.FromRgb(
            (byte)(c.R + (18 - c.R) * f),
            (byte)(c.G + (20 - c.G) * f),
            (byte)(c.B + (24 - c.B) * f));
    }
}
