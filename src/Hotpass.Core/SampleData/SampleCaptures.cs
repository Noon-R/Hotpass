using Hotpass.Core.Model;

namespace Hotpass.Core.SampleData;

/// <summary>
/// mock/hotpass-mock.html と同内容のダミーデータ。
/// 実アダプタが未接続でも UI 開発・デモができるようにするためのもの。
/// </summary>
public static class SampleCaptures
{
    public sealed record Sample(CaptureInfo Capture, IReadOnlyList<PassRecord> Passes);

    private static readonly Dictionary<string, (string Name, double Fraction, (string, double)[]? Children)[]> Nest = new()
    {
        ["ShadowPass"] = [("Cascade 0", .34, null), ("Cascade 1", .28, null), ("Cascade 2", .22, null), ("Cascade 3", .16, null)],
        ["GBuffer"] = [("Opaque", .60, null), ("Masked", .28, null), ("Decals", .12, null)],
        ["Lighting"] = [("Cluster build", .18, null), ("Directional", .34, null), ("Local lights", .48, [("Point", .62), ("Spot", .38)])],
        ["Bloom"] = [("Downsample", .5, null), ("Upsample", .5, null)],
    };

    public static IReadOnlyList<Sample> CreateAll() =>
    [
        Build("main.wpix", CaptureSource.Pix, 2314, 62, 0.8,
        [
            ("ShadowPass", 1204, 5.4, BottleneckCategory.Raster, 44, "ROP / depth", null),
            ("GBuffer", 1560, 3.8, BottleneckCategory.Texture, 58, "TEX wait", null),
            ("Lighting", 1847, 3.1, BottleneckCategory.Memory, 51, "L2 miss / bw", null),
            ("SSR", 2033, 2.2, BottleneckCategory.Texture, 47, "TEX wait", null),
            ("SSAO", 2210, 1.3, BottleneckCategory.Compute, 66, "VGPR limited", null),
            ("Bloom", 2402, 1.1, BottleneckCategory.Memory, 39, "bandwidth", null),
            ("ToneMap", 2515, 0.6, BottleneckCategory.Memory, 35, "bandwidth", null),
            ("UI / ImGui", 2588, 0.4, BottleneckCategory.Raster, 20, null, null),
            ("Present", 2601, 0.3, BottleneckCategory.Unknown, null, null, null),
        ]),
        Build("shadowfix.wpix", CaptureSource.Pix, 2314, 64, 0.7,
        [
            ("ShadowPass", 1204, 2.9, BottleneckCategory.Raster, 63, "ROP / depth", null),
            ("GBuffer", 1560, 3.9, BottleneckCategory.Texture, 57, "TEX wait", null),
            ("Lighting", 1847, 3.0, BottleneckCategory.Memory, 52, "L2 miss / bw", null),
            ("SSR", 2033, 2.2, BottleneckCategory.Texture, 47, "TEX wait", null),
            ("SSAO", 2210, 1.3, BottleneckCategory.Compute, 66, "VGPR limited", null),
            ("Bloom", 2402, 1.1, BottleneckCategory.Memory, 39, "bandwidth", null),
            ("ToneMap", 2515, 0.6, BottleneckCategory.Memory, 35, "bandwidth", null),
            ("UI / ImGui", 2588, 0.4, BottleneckCategory.Raster, 20, null, null),
            ("Present", 2601, 0.3, BottleneckCategory.Unknown, null, null, null),
        ]),
        Build("main.nsys-rep", CaptureSource.Nsight, 2314, 60, 0.9,
        [
            ("ShadowPass", 1204, 5.6, BottleneckCategory.Raster, 43, null, "PROP 79%"),
            ("GBuffer", 1560, 3.7, BottleneckCategory.Texture, 59, null, "TEX 82%"),
            ("Lighting", 1847, 3.2, BottleneckCategory.Memory, 50, null, "VRAM 75%"),
            ("SSR", 2033, 2.2, BottleneckCategory.Texture, 46, null, "TEX 70%"),
            ("SSAO", 2210, 1.2, BottleneckCategory.Compute, 67, null, "SM 72%"),
            ("Bloom", 2402, 1.1, BottleneckCategory.Memory, 38, null, "VRAM 66%"),
            ("ToneMap", 2515, 0.6, BottleneckCategory.Memory, 34, null, "VRAM 59%"),
            ("UI / ImGui", 2588, 0.5, BottleneckCategory.Raster, 19, null, "PROP 41%"),
            ("Present", 2601, 0.3, BottleneckCategory.Unknown, null, null, null),
        ]),
        Build("highend_main.wpix", CaptureSource.Pix, 2314, 58, 0.4,
        [
            ("ShadowPass", 1204, 3.2, BottleneckCategory.Raster, 52, "ROP / depth", null),
            ("GBuffer", 1560, 2.3, BottleneckCategory.Texture, 64, "TEX wait", null),
            ("Lighting", 1847, 1.9, BottleneckCategory.Memory, 58, "bandwidth", null),
            ("SSR", 2033, 1.3, BottleneckCategory.Texture, 55, "TEX wait", null),
            ("SSAO", 2210, 0.8, BottleneckCategory.Compute, 70, "VGPR limited", null),
            ("Bloom", 2402, 0.7, BottleneckCategory.Memory, 44, "bandwidth", null),
            ("ToneMap", 2515, 0.4, BottleneckCategory.Memory, 40, "bandwidth", null),
            ("UI / ImGui", 2588, 0.3, BottleneckCategory.Raster, 22, null, null),
            ("Present", 2601, 0.2, BottleneckCategory.Unknown, null, null, null),
        ]),
    ];

    private static Sample Build(
        string file, CaptureSource source, long frame, double asyncOverlapPct, double syncGapsMs,
        (string Name, long EventId, double Ms, BottleneckCategory Cat, double? Occ, string? Limiter, string? Sol)[] tops)
    {
        var capture = new CaptureInfo
        {
            FileName = file,
            Source = source,
            FrameNumber = frame,
            AsyncOverlapPct = asyncOverlapPct,
            SyncGapsNs = MsToNs(syncGapsMs),
            ProvidesOccupancy = true,
            ProvidesLimiter = source == CaptureSource.Pix,
            ProvidesSol = source == CaptureSource.Nsight,
        };

        var passes = new List<PassRecord>();
        long cursor = 0;
        long nextId = 1;
        foreach (var t in tops)
        {
            var durNs = MsToNs(t.Ms);
            var parent = new PassRecord
            {
                Id = nextId++,
                Name = t.Name,
                EventId = t.EventId,
                StartNs = cursor,
                EndNs = cursor + durNs,
                DurationNs = durNs,
                Depth = 0,
                Queue = GpuQueue.Graphics,
                Category = t.Cat,
                OccupancyPct = t.Occ,
                OccupancyLimiter = t.Limiter,
                SolTopUnit = t.Sol,
            };
            passes.Add(parent);

            if (Nest.TryGetValue(t.Name, out var children))
            {
                var childStart = cursor;
                var childIndex = 0;
                foreach (var c in children)
                {
                    var childDur = (long)(durNs * c.Fraction);
                    var child = new PassRecord
                    {
                        Id = nextId++,
                        Name = c.Name,
                        EventId = t.EventId + ++childIndex,
                        StartNs = childStart,
                        EndNs = childStart + childDur,
                        DurationNs = childDur,
                        Depth = 1,
                        ParentId = parent.Id,
                        Queue = GpuQueue.Graphics,
                        Category = t.Cat,
                    };
                    passes.Add(child);

                    if (c.Children is not null)
                    {
                        var gcStart = childStart;
                        foreach (var (gcName, gcFraction) in c.Children)
                        {
                            var gcDur = (long)(childDur * gcFraction);
                            passes.Add(new PassRecord
                            {
                                Id = nextId++,
                                Name = gcName,
                                EventId = t.EventId + ++childIndex,
                                StartNs = gcStart,
                                EndNs = gcStart + gcDur,
                                DurationNs = gcDur,
                                Depth = 2,
                                ParentId = child.Id,
                                Queue = GpuQueue.Graphics,
                                Category = t.Cat,
                            });
                            gcStart += gcDur;
                        }
                    }
                    childStart += childDur;
                }
            }
            cursor += durNs;
        }

        // async compute レーン: 序盤の graphics と重なる ParticleSim(モックと同じ演出)
        var asyncDur = passes.Where(p => p.Depth == 0).Take(2).Sum(p => p.DurationNs) + MsToNs(1.0);
        passes.Add(new PassRecord
        {
            Id = nextId,
            Name = "ParticleSim",
            EventId = 900,
            StartNs = MsToNs(0.3),
            EndNs = MsToNs(0.3) + asyncDur,
            DurationNs = asyncDur,
            Depth = 0,
            Queue = GpuQueue.AsyncCompute,
            Category = BottleneckCategory.Compute,
        });

        return new Sample(capture, passes);
    }

    private static long MsToNs(double ms) => (long)(ms * 1_000_000);
}
