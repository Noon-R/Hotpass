using Hotpass.Core.Analysis;
using Hotpass.Core.Model;
using Hotpass.Core.SampleData;

namespace Hotpass.Core.Tests;

public class FrameAnalyzerTests
{
    private static SampleCaptures.Sample MainWpix => SampleCaptures.CreateAll()[0];

    [Fact]
    public void Summarize_TotalIsSumOfTopLevelGraphicsPasses()
    {
        var s = MainWpix;
        var sum = FrameAnalyzer.Summarize(s.Capture, s.Passes);

        // 5.4+3.8+3.1+2.2+1.3+1.1+0.6+0.4+0.3 = 18.2ms — ネスト子と async は含まない
        Assert.Equal(18_200_000, sum.TotalGpuNs);
        Assert.Equal(18_200_000 - FrameAnalyzer.DefaultBudgetNs, sum.BudgetDeltaNs);
    }

    [Fact]
    public void Summarize_DominantCategoryIsLargestByTime()
    {
        var s = MainWpix;
        var sum = FrameAnalyzer.Summarize(s.Capture, s.Passes);

        // texture(3.8+2.2=6.0) > raster(5.4+0.4=5.8) > memory(3.1+1.1+0.6=4.8)
        Assert.Equal(BottleneckCategory.Texture, sum.DominantCategory);
        Assert.Equal(6_000_000, sum.DominantCategoryNs);
        Assert.Equal(2, sum.DominantCategoryPassCount);
    }

    [Fact]
    public void Summarize_WorstPassIsShadowPass()
    {
        var s = MainWpix;
        var sum = FrameAnalyzer.Summarize(s.Capture, s.Passes);

        Assert.Equal("ShadowPass", sum.WorstPass!.Name);
    }

    [Fact]
    public void Summarize_EmptyPassListYieldsUnknownAndNoWorst()
    {
        var capture = new CaptureInfo { FileName = "empty.wpix", Source = CaptureSource.Pix };
        var sum = FrameAnalyzer.Summarize(capture, []);

        Assert.Equal(0, sum.TotalGpuNs);
        Assert.Equal(BottleneckCategory.Unknown, sum.DominantCategory);
        Assert.Null(sum.WorstPass);
    }

    [Fact]
    public void PctOfFrame_ComputesRatio()
    {
        var pass = new PassRecord { Name = "p", DurationNs = 5_000_000 };
        Assert.Equal(25.0, FrameAnalyzer.PctOfFrame(pass, 20_000_000), 3);
        Assert.Equal(0.0, FrameAnalyzer.PctOfFrame(pass, 0));
    }
}
