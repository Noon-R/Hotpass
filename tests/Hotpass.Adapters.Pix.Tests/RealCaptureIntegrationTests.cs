using Hotpass.Adapters.Pix;
using Hotpass.Core.Model;

namespace Hotpass.Adapters.Pix.Tests;

/// <summary>
/// 実 pixtool + 実キャプチャによる統合テスト。
/// 環境変数 HOTPASS_TEST_WPIX に .wpix パスが設定され、pixtool が見つかる場合のみ実行される
/// (CI や PIX 未導入環境では自動スキップ)。
/// </summary>
public class RealCaptureIntegrationTests
{
    private static string? TestWpix =>
        Environment.GetEnvironmentVariable("HOTPASS_TEST_WPIX") is { Length: > 0 } p && File.Exists(p) ? p : null;

    private static bool Available => TestWpix is not null && PixToolLocator.Find() is not null;

    [SkippableFact]
    public async Task ImportAsync_RealCapture_ProducesPassesAndDb()
    {
        Skip.IfNot(Available, "HOTPASS_TEST_WPIX / pixtool が無いためスキップ");

        var importer = new PixCaptureImporter(PixToolRunner.CreateDefault());
        var result = await importer.ImportAsync(TestWpix!);

        Assert.Equal(CaptureSource.Pix, result.Capture.Source);
        Assert.NotEmpty(result.Passes);
        Assert.True(File.Exists(result.DbPath));

        // マーカー "Render" が集計付きで取り込まれている(D3D12Bundles サンプル前提)
        var render = result.Passes.FirstOrDefault(p => p.Name == "Render");
        Assert.NotNull(render);
        Assert.True(render.DurationNs > 0);
        Assert.Equal(0, render.Depth);

        // トップレベル合計 > 0
        Assert.True(result.Passes.Where(p => p.Depth == 0).Sum(p => p.DurationNs) > 0);
    }

    [SkippableFact]
    public async Task ExtractAsync_RealCapture_SavesPngForMarker()
    {
        Skip.IfNot(Available, "HOTPASS_TEST_WPIX / pixtool が無いためスキップ");

        // 画像抽出は events.csv(インポート成果物)に依存するため先にインポート
        var runner = PixToolRunner.CreateDefault();
        await new PixCaptureImporter(runner).ImportAsync(TestWpix!);

        var extractor = new PixImageExtractor(runner);
        var image = await extractor.ExtractAsync(TestWpix!, "Render");
        Assert.True(File.Exists(image.PngPath), $"PNG が生成されていない: {image.PngPath}");
        Assert.True(new FileInfo(image.PngPath).Length > 1000);

        // スペース入りマーカー名(--marker が弾く場合の --global-id フォールバック検証)
        var image2 = await extractor.ExtractAsync(TestWpix!, "Draw cities");
        Assert.True(File.Exists(image2.PngPath), $"PNG が生成されていない: {image2.PngPath}");
    }
}
