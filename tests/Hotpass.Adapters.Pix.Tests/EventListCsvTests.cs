using Hotpass.Adapters.Pix;

namespace Hotpass.Adapters.Pix.Tests;

/// <summary>
/// フィクスチャは pixtool 2603.25 の save-event-list 実出力形式
/// (`Queue ID, Parent, Name, Global ID, カウンタ...`、区切り後に空白、階層は Parent 列)。
/// </summary>
public class EventListCsvTests
{
    private const string Fixture = """
        Queue ID, Parent, Name, Global ID, EOP Start Time (ns), TOP to EOP Duration (ns)
        0, -1, Reset, , ,
        1, -1, Render, , ,
        2, 1, Draw cities, , ,
        3, 2, ClearRenderTargetView, 3, 1000, 5000
        4, 2, "DrawIndexedInstanced(36, 84)", 4, 6000, 20000
        5, 1, DrawIndexedInstanced, 7, 26000, 10000
        6, -1, Present, 38, 36000, 500
        """;

    [Fact]
    public void Parse_ResolvesRealPixToolColumns()
    {
        var rows = EventListCsv.Parse(new StringReader(Fixture));

        Assert.Equal(7, rows.Count);
        var clear = rows[3];
        Assert.Equal(3, clear.RowId);
        Assert.Equal(2, clear.ParentRowId);
        Assert.Equal("ClearRenderTargetView", clear.Name);
        Assert.Equal(3, clear.GlobalId);
        Assert.Equal(1000, clear.StartNs);
        Assert.Equal(5000, clear.DurationNs);

        // Parent=-1 はルート(null)、状態コールの Global ID は null
        Assert.Null(rows[0].ParentRowId);
        Assert.Null(rows[0].GlobalId);
    }

    [Fact]
    public void Parse_HandlesQuotedCommaInName()
    {
        var rows = EventListCsv.Parse(new StringReader(Fixture));
        Assert.Contains(rows, r => r.Name == "DrawIndexedInstanced(36, 84)");
    }

    [Fact]
    public void Parse_MissingNameColumnThrows()
    {
        Assert.Throws<InvalidDataException>(() =>
            EventListCsv.Parse(new StringReader("A,B,C\n1,2,3")));
    }

    [Fact]
    public void BuildPasses_AggregatesMarkersAndDropsStateCalls()
    {
        var rows = EventListCsv.Parse(new StringReader(Fixture));
        var passes = PixCaptureImporter.BuildPasses(rows);

        // Reset(duration 無し・子無し)は落ちる
        Assert.DoesNotContain(passes, p => p.Name == "Reset");

        // マーカー Render は配下集計: start=1000, end=36000, dur=35000
        var render = passes.First(p => p.Name == "Render");
        Assert.Equal(0, render.Depth);
        Assert.Null(render.ParentId);
        Assert.Equal(1_000, render.StartNs);
        Assert.Equal(36_000, render.EndNs);
        Assert.Equal(35_000, render.DurationNs);

        // ネスト: Draw cities は Render の子、GPU 操作はさらにその子
        var cities = passes.First(p => p.Name == "Draw cities");
        Assert.Equal(1, cities.Depth);
        Assert.Equal(render.Id, cities.ParentId);
        Assert.Equal(25_000, cities.DurationNs); // 1000..26000

        var clear = passes.First(p => p.Name == "ClearRenderTargetView");
        Assert.Equal(2, clear.Depth);
        Assert.Equal(cities.Id, clear.ParentId);

        // トップレベルの GPU 操作(Present)も残る
        var present = passes.First(p => p.Name == "Present");
        Assert.Equal(0, present.Depth);
        Assert.Equal(500, present.DurationNs);
    }

    [Fact]
    public void ResolveMarkerRange_UsesGlobalIdsOfSubtree()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hotpass-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "events.csv"), Fixture);

            var (begin, end, rowId) = PixImageExtractor.ResolveMarkerRange(dir, "Render");
            Assert.Equal(3, begin);   // 配下の最小 Global ID
            Assert.Equal(7, end);     // 配下の最大 Global ID
            Assert.Equal(1, rowId);

            var (b2, e2, _) = PixImageExtractor.ResolveMarkerRange(dir, "Draw cities");
            Assert.Equal(3, b2);
            Assert.Equal(4, e2);

            // GPU 操作を持たないマーカー名はエラー
            Assert.Throws<InvalidOperationException>(() =>
                PixImageExtractor.ResolveMarkerRange(dir, "Reset"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
