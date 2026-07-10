using Hotpass.Adapters.Nsight;

namespace Hotpass.Adapters.Nsight.Tests;

/// <summary>
/// GPU Trace のエクスポート列は Nsight バージョン・メトリクスセット依存のため、
/// フィクスチャは「範囲/マーカー単位のメトリクス表」の代表形(ns 時間+ユニット別スループット%)。
/// </summary>
public class TraceCsvTests
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
    public void Parse_ResolvesColumnsDefensively()
    {
        var rows = TraceCsv.Parse(new StringReader(Fixture));

        Assert.Equal(5, rows.Count);
        var gbuffer = rows[2];
        Assert.Equal("GBuffer", gbuffer.Name);
        Assert.Equal(1, gbuffer.Depth);
        Assert.Equal("Graphics Queue", gbuffer.QueueName);
        Assert.Equal(3_400_000, gbuffer.StartNs);
        Assert.Equal(2_900_000, gbuffer.DurationNs);
        Assert.Equal(59, gbuffer.OccupancyPct);
        Assert.Equal(5, gbuffer.Throughputs.Count);
        Assert.Contains(gbuffer.Throughputs, t => t.Unit == "TEX" && t.Pct == 82.4);

        // メトリクスが空の行は null(UI では「—」)
        Assert.Null(rows[0].OccupancyPct);
        Assert.Empty(rows[0].Throughputs);
    }

    [Fact]
    public void Parse_HandlesQuotedCommaInName()
    {
        var rows = TraceCsv.Parse(new StringReader(Fixture));
        Assert.Contains(rows, r => r.Name == "Lighting (tiled, deferred)");
    }

    [Fact]
    public void Parse_ConvertsTimeUnitsFromHeader()
    {
        var csv = """
            Range, Start (µs), GPU Time (ms)
            Pass A, 1500, 2.5
            """;
        var rows = TraceCsv.Parse(new StringReader(csv));
        Assert.Equal(1_500_000, rows[0].StartNs);
        Assert.Equal(2_500_000, rows[0].DurationNs);
    }

    [Fact]
    public void Parse_RescalesRatioPercentsWithoutPercentHeader()
    {
        // % 表記が無く全値が 0..1 → 比率とみなして 100 倍
        var csv = """
            Name, Duration (ns), SM Occupancy, TEX Throughput
            Pass A, 1000, 0.43, 0.82
            Pass B, 2000, 0.71, 0.15
            """;
        var rows = TraceCsv.Parse(new StringReader(csv));
        Assert.Equal(43, rows[0].OccupancyPct);
        Assert.Equal(82, rows[0].Throughputs.Single(t => t.Unit == "TEX").Pct);

        // 1.0 を超える値があればそのまま(既に % スケール)
        var csv2 = """
            Name, Duration (ns), SM Occupancy
            Pass A, 1000, 43
            """;
        Assert.Equal(43, TraceCsv.Parse(new StringReader(csv2))[0].OccupancyPct);
    }

    [Fact]
    public void Parse_MissingNameColumnThrows()
    {
        Assert.Throws<InvalidDataException>(() =>
            TraceCsv.Parse(new StringReader("A,B,C\n1,2,3")));
    }

    [Theory]
    [InlineData("SM Throughput (%)", "SM")]
    [InlineData("TEX Throughput", "TEX")]
    [InlineData("L1TEX SOL %", "L1TEX")]
    [InlineData("SOL VRAM", "VRAM")]
    [InlineData("PES+VPC Throughput (%)", "PES+VPC")]
    public void UnitFromHeader_ExtractsUnitName(string header, string expected)
        => Assert.Equal(expected, TraceCsv.UnitFromHeader(header));

    [Theory]
    [InlineData("Duration (ns)", 1)]
    [InlineData("Start (us)", 1_000)]
    [InlineData("GPU Time (ms)", 1_000_000)]
    [InlineData("Duration", 1)]
    public void TimeScaleToNs_ReadsHeaderUnit(string header, double expected)
        => Assert.Equal(expected, TraceCsv.TimeScaleToNs(header));
}
