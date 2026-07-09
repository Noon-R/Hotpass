using Hotpass.Adapters.Pix;

namespace Hotpass.Adapters.Pix.Tests;

public class EventListCsvTests
{
    private const string Fixture = """
        Event ID,Depth,Name,Start Time (ns),GPU Duration (ns)
        1200,0,Frame 2314,0,18200000
        1204,1,ShadowPass,0,5400000
        1210,2,Cascade 0,0,1836000
        1230,2,Cascade 1,1836000,1512000
        1560,1,GBuffer,5400000,3800000
        1600,2,"Opaque, static",5400000,2280000
        1847,1,Lighting,9200000,3100000
        2601,1,Present,17900000,300000
        """;

    [Fact]
    public void Parse_ResolvesColumnsByName()
    {
        var rows = EventListCsv.Parse(new StringReader(Fixture));

        Assert.Equal(8, rows.Count);
        var shadow = rows[1];
        Assert.Equal(1204, shadow.EventId);
        Assert.Equal("ShadowPass", shadow.Name);
        Assert.Equal(1, shadow.Depth);
        Assert.Equal(5_400_000, shadow.DurationNs);
    }

    [Fact]
    public void Parse_HandlesQuotedCommaInName()
    {
        var rows = EventListCsv.Parse(new StringReader(Fixture));
        Assert.Contains(rows, r => r.Name == "Opaque, static");
    }

    [Fact]
    public void Parse_MissingNameColumnThrows()
    {
        Assert.Throws<InvalidDataException>(() =>
            EventListCsv.Parse(new StringReader("A,B,C\n1,2,3")));
    }

    [Fact]
    public void BuildPasses_ReconstructsHierarchy()
    {
        var rows = EventListCsv.Parse(new StringReader(Fixture));
        var passes = PixCaptureImporter.BuildPasses(rows);

        // Frame(0) > ShadowPass(1) > Cascade(2)
        var frame = passes.First(p => p.Name == "Frame 2314");
        var shadow = passes.First(p => p.Name == "ShadowPass");
        var cascade0 = passes.First(p => p.Name == "Cascade 0");

        Assert.Null(frame.ParentId);
        Assert.Equal(frame.Id, shadow.ParentId);
        Assert.Equal(shadow.Id, cascade0.ParentId);
        Assert.Equal(2, cascade0.Depth);
        Assert.Equal(5_400_000, shadow.DurationNs);
    }

    [Fact]
    public void ResolveMarkerRange_EndsBeforeNextSibling()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hotpass-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "events.csv"), Fixture);

            var (begin, end, id) = PixImageExtractor.ResolveMarkerRange(dir, "ShadowPass");
            Assert.Equal(1204, begin);
            Assert.Equal(1559, end); // 次の同深度マーカー GBuffer(1560) の直前
            Assert.Equal(1204, id);

            var (_, endLast, _) = PixImageExtractor.ResolveMarkerRange(dir, "Present");
            Assert.Equal(2601, endLast); // 最後のマーカーは最終イベントまで
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
