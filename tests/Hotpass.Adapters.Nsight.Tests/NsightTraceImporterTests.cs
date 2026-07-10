using Hotpass.Adapters.Nsight;
using Hotpass.Core.Model;
using Hotpass.Core.Storage;

namespace Hotpass.Adapters.Nsight.Tests;

public class NsightTraceImporterTests
{
    private const string Fixture = """
        Name, Depth, Queue, Start (ns), Duration (ns), SM Occupancy (%), SM Throughput (%), TEX Throughput (%), L2 Throughput (%), VRAM Throughput (%), CROP Throughput (%)
        Frame, 0, Graphics Queue, 0, 12500000, , , , , ,
        ShadowPass, 1, Graphics Queue, 0, 3400000, 43, 35.2, 22.0, 18.5, 40.1, 12.3
        GBuffer, 1, Graphics Queue, 3400000, 2900000, 59, 44.0, 82.4, 30.2, 55.0, 21.7
        "Lighting (tiled, deferred)", 1, Compute Queue, 6300000, 4100000, 71, 88.9, 41.5, 62.0, 47.3, 2.1
        Streaming, 0, Copy Queue, 0, 900000, , , , 12.0, 76.5,
        """;

    [Fact]
    public void BuildPasses_MapsTopUnitToSolAndCategory()
    {
        var passes = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(Fixture)));

        // GBuffer: TEX 82.4% が最繁 → texture 律速、"TEX 82%" 表示
        var gbuffer = passes.First(p => p.Name == "GBuffer");
        Assert.Equal(BottleneckCategory.Texture, gbuffer.Category);
        Assert.Equal("TEX 82%", gbuffer.SolTopUnit);
        Assert.Equal(59, gbuffer.OccupancyPct);

        // Lighting: SM 88.9% → compute
        var lighting = passes.First(p => p.Name.StartsWith("Lighting"));
        Assert.Equal(BottleneckCategory.Compute, lighting.Category);
        Assert.Equal(GpuQueue.AsyncCompute, lighting.Queue);

        // Streaming: VRAM 76.5% → memory、copy queue
        var streaming = passes.First(p => p.Name == "Streaming");
        Assert.Equal(BottleneckCategory.Memory, streaming.Category);
        Assert.Equal(GpuQueue.Copy, streaming.Queue);

        // メトリクスの無い行は unknown・SOL null
        var csv = """
            Name, Duration (ns), SM Occupancy (%), TEX Throughput (%)
            Bare pass, 1000, ,
            """;
        var bare = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(csv))).Single();
        Assert.Equal(BottleneckCategory.Unknown, bare.Category);
        Assert.Null(bare.SolTopUnit);
        Assert.Null(bare.OccupancyPct);
    }

    [Fact]
    public void BuildPasses_UnwrapsSingleFrameRootAndKeepsHierarchy()
    {
        var passes = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(Fixture)));

        // graphics トップレベルが "Frame" 1 行だけ → ラッパとみなして畳む
        Assert.DoesNotContain(passes, p => p.Name == "Frame");

        var gbuffer = passes.First(p => p.Name == "GBuffer");
        Assert.Equal(0, gbuffer.Depth);
        Assert.Null(gbuffer.ParentId);

        // copy キューの Streaming はトップレベルのまま
        var streaming = passes.First(p => p.Name == "Streaming");
        Assert.Equal(0, streaming.Depth);
        Assert.Null(streaming.ParentId);
    }

    [Fact]
    public void UnwrapFrameRoot_KeepsMultiFrameExports()
    {
        // トップレベルに Frame 行が複数並ぶ(複数フレームのエクスポート)場合は畳まない
        var csv = """
            Name, Depth, Duration (ns)
            Frame 1, 0, 1000
            PassA, 1, 600
            Frame 2, 0, 1200
            PassB, 1, 700
            """;
        var passes = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(csv)));
        Assert.Contains(passes, p => p.Name == "Frame 1");
        Assert.Contains(passes, p => p.Name == "Frame 2");
        Assert.Equal(1, passes.First(p => p.Name == "PassB").Depth);
    }

    [Fact]
    public void UnwrapFrameRoot_HandlesNestedWrappers()
    {
        // "Frame > Scene > パス群" の入れ子ラッパも段階的に畳まれる
        var csv = """
            Name, Depth, Duration (ns)
            Frame, 0, 3000
            Scene, 1, 2800
            PassA, 2, 1500
            PassB, 2, 1300
            """;
        var passes = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(csv)));
        Assert.Equal(new[] { "PassA", "PassB" }, passes.Select(p => p.Name).ToArray());
        Assert.All(passes, p => Assert.Equal(0, p.Depth));
    }

    [Fact]
    public void BuildPasses_SynthesizesTimelineWhenStartMissing()
    {
        var csv = """
            Name, Duration (ns)
            Pass A, 1000
            Pass B, 2000
            Pass C, 3000
            """;
        var passes = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(csv)));

        // start 列が無い → トップレベルを積み上げて時間軸を合成
        Assert.Equal(0, passes[0].StartNs);
        Assert.Equal(1_000, passes[1].StartNs);
        Assert.Equal(3_000, passes[2].StartNs);
        Assert.Equal(6_000, passes[2].EndNs);
    }

    [Fact]
    public void BuildPasses_DropsRowsWithoutDuration()
    {
        var csv = """
            Name, Duration (ns)
            Header only,
            Real pass, 500
            """;
        var passes = NsightTraceImporter.BuildPasses(TraceCsv.Parse(new StringReader(csv)));
        Assert.Single(passes);
        Assert.Equal("Real pass", passes[0].Name);
    }

    [Theory]
    [InlineData("TEX", BottleneckCategory.Texture)]
    [InlineData("L1TEX", BottleneckCategory.Texture)]
    [InlineData("L2", BottleneckCategory.Memory)]
    [InlineData("VRAM", BottleneckCategory.Memory)]
    [InlineData("DRAM", BottleneckCategory.Memory)]
    [InlineData("CROP", BottleneckCategory.Raster)]
    [InlineData("ZROP", BottleneckCategory.Raster)]
    [InlineData("PROP", BottleneckCategory.Raster)]
    [InlineData("RASTER", BottleneckCategory.Raster)]
    [InlineData("PD", BottleneckCategory.Geometry)]
    [InlineData("VAF", BottleneckCategory.Geometry)]
    [InlineData("PES+VPC", BottleneckCategory.Geometry)]
    [InlineData("SM", BottleneckCategory.Compute)]
    [InlineData("SMSP", BottleneckCategory.Compute)]
    [InlineData("XYZ", BottleneckCategory.Unknown)]
    public void CategoryForUnit_MapsHardwareUnits(string unit, BottleneckCategory expected)
        => Assert.Equal(expected, NsightTraceImporter.CategoryForUnit(unit));

    [Fact]
    public void Import_WritesCaptureInfoAndDbCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hotpass-nsight-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csvPath = Path.Combine(dir, "gputrace.csv");
            File.WriteAllText(csvPath, Fixture);

            var result = new NsightTraceImporter().Import(csvPath);

            Assert.Equal(CaptureSource.Nsight, result.Capture.Source);
            Assert.Equal("gputrace.csv", result.Capture.FileName);
            Assert.True(result.Capture.ProvidesOccupancy);
            Assert.True(result.Capture.ProvidesSol);
            Assert.False(result.Capture.ProvidesLimiter);   // limiter は PIX 系のみ
            Assert.Equal(4, result.Passes.Count);           // Frame ラッパは畳まれる

            // 前処理済み SQLite が CSV の隣にキャッシュされる
            Assert.True(File.Exists(result.DbPath));
            using var store = new CaptureStore(result.DbPath);
            var stored = store.GetCaptures().Single();
            Assert.Equal(CaptureSource.Nsight, stored.Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Import_ThrowsWhenNoUsableRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hotpass-nsight-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var csvPath = Path.Combine(dir, "empty.csv");
            File.WriteAllText(csvPath, "Name, Duration (ns)\n");
            Assert.Throws<InvalidDataException>(() => new NsightTraceImporter().Import(csvPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
