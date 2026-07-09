using Hotpass.Core.Model;
using Hotpass.Core.SampleData;
using Hotpass.Core.Storage;

namespace Hotpass.Core.Tests;

public class CaptureStoreTests
{
    [Fact]
    public void RoundTrip_CaptureAndPasses()
    {
        using var store = CaptureStore.InMemory();
        var sample = SampleCaptures.CreateAll()[0];

        var capId = store.AddCapture(sample.Capture);
        store.AddPasses(capId, sample.Passes);

        var captures = store.GetCaptures();
        var cap = Assert.Single(captures);
        Assert.Equal("main.wpix", cap.FileName);
        Assert.Equal(CaptureSource.Pix, cap.Source);
        Assert.True(cap.ProvidesLimiter);
        Assert.False(cap.ProvidesSol);

        var passes = store.GetPasses(capId);
        Assert.Equal(sample.Passes.Count, passes.Count);

        var shadow = passes.First(p => p.Name == "ShadowPass");
        Assert.Equal(5_400_000, shadow.DurationNs);
        Assert.Equal("ROP / depth", shadow.OccupancyLimiter);
        Assert.Null(shadow.SolTopUnit);

        // ネスト階層の復元: Cascade 0 の親は ShadowPass
        var cascade = passes.First(p => p.Name == "Cascade 0");
        Assert.Equal(1, cascade.Depth);
        Assert.NotNull(cascade.ParentId);

        // ParentId は DB 上の実 Id を指す
        var parentDbId = passes.First(p => p.Name == "ShadowPass").Id;
        Assert.Equal(parentDbId, cascade.ParentId);
    }

    [Fact]
    public void RoundTrip_NullableFieldsSurviveAsNull()
    {
        using var store = CaptureStore.InMemory();
        var capture = new CaptureInfo { FileName = "n.nsys-rep", Source = CaptureSource.Nsight };
        var capId = store.AddCapture(capture);
        store.AddPasses(capId, [new PassRecord { Name = "Present", DurationNs = 100, Category = BottleneckCategory.Unknown }]);

        var p = Assert.Single(store.GetPasses(capId));
        Assert.Null(p.OccupancyPct);
        Assert.Null(p.OccupancyLimiter);
        Assert.Null(p.SolTopUnit);
        Assert.Null(p.ParentId);
    }

    [Fact]
    public void Images_RoundTrip()
    {
        using var store = CaptureStore.InMemory();
        var capId = store.AddCapture(new CaptureInfo { FileName = "a.wpix", Source = CaptureSource.Pix });
        store.AddImage(new PassImage { CaptureId = capId, PassName = "GBuffer", ResourceName = "GBufferA", EventId = 1560, PngPath = @"images\gbuffer_a.png" });

        var img = Assert.Single(store.GetImages(capId));
        Assert.Equal("GBuffer", img.PassName);
        Assert.Equal(@"images\gbuffer_a.png", img.PngPath);
    }

    [Fact]
    public void SampleData_ParentIdsResolveWithinList()
    {
        foreach (var sample in SampleCaptures.CreateAll())
        {
            var ids = sample.Passes.Select(p => p.Id).ToHashSet();
            foreach (var p in sample.Passes.Where(p => p.ParentId is not null))
                Assert.Contains(p.ParentId!.Value, ids);
        }
    }
}
