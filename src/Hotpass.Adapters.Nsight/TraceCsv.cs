using System.Globalization;

namespace Hotpass.Adapters.Nsight;

/// <summary>
/// Nsight GPU Trace エクスポートの 1 行(正規化済み)。時間はヘッダの単位表記から ns に揃える。
/// </summary>
public sealed record TraceRow(
    long RowId,
    string Name,
    int? Depth,
    string? QueueName,
    double? StartNs,
    double? DurationNs,
    double? OccupancyPct,
    IReadOnlyList<UnitThroughput> Throughputs);

/// <summary>ハードウェアユニット別スループット(SOL)%。</summary>
public sealed record UnitThroughput(string Unit, double Pct);

/// <summary>
/// Nsight Graphics GPU Trace の CSV エクスポート(範囲/マーカー単位のメトリクス表)のパーサ。
/// GPU Trace のエクスポート列は Nsight バージョン・メトリクスセット依存で公式スキーマが無いため、
/// PIX アダプタと同じく列名の部分一致で防御的に解決する(CLAUDE.md 方針)。
///   - 時間列: ヘッダの "(ns)/(us)/(ms)" 表記から ns へ換算(表記なしは ns とみなす)
///   - % 列: ヘッダに % が無く全値が 0..1 の場合は比率とみなし 100 倍する
///   - スループット(SOL)列: "Throughput" / "SOL" を含む列をすべて拾い、ユニット名はヘッダから抽出
/// </summary>
public static class TraceCsv
{
    public static List<TraceRow> Parse(string csvPath)
    {
        using var reader = new StreamReader(csvPath);
        return Parse(reader);
    }

    public static List<TraceRow> Parse(TextReader reader)
    {
        string? headerLine;
        do
        {
            headerLine = reader.ReadLine();
        } while (headerLine is not null && headerLine.Trim().Length == 0);
        if (headerLine is null)
            throw new InvalidDataException("CSV が空です");

        var headers = SplitCsvLine(headerLine)
            .Select(h => h.Trim().TrimStart('﻿'))
            .ToList();

        int nameCol = FindColumn(headers,
            exact: ["Name", "Range", "Range Name", "Marker", "Workload"],
            contains: ["name", "range", "marker", "workload"]);
        int depthCol = FindColumn(headers,
            exact: ["Depth", "Nesting Level", "Level"],
            contains: ["depth", "nesting"]);
        int queueCol = FindColumn(headers, exact: ["Queue"], contains: ["queue"]);
        int startCol = FindColumn(headers,
            exact: [],
            contains: ["start"]);
        int durCol = FindColumn(headers,
            exact: [],
            contains: ["duration", "gpu time", "elapsed"]);
        int occCol = FindColumn(headers, exact: [], contains: ["occupancy"]);

        // ユニット別スループット(SOL)列は複数あり得るので全部拾う
        var thrCols = new List<(int Index, string Unit)>();
        for (var i = 0; i < headers.Count; i++)
        {
            if (i == occCol) continue;
            if (headers[i].Contains("throughput", StringComparison.OrdinalIgnoreCase) ||
                headers[i].Contains("sol", StringComparison.OrdinalIgnoreCase))
            {
                var unit = UnitFromHeader(headers[i]);
                if (unit.Length > 0) thrCols.Add((i, unit));
            }
        }

        if (nameCol < 0)
            throw new InvalidDataException(
                $"範囲/マーカー名の列が見つかりません。ヘッダ: {string.Join(", ", headers)}");

        var startScale = startCol >= 0 ? TimeScaleToNs(headers[startCol]) : 1.0;
        var durScale = durCol >= 0 ? TimeScaleToNs(headers[durCol]) : 1.0;

        // % 正規化(0..1 スケール検出)のため、いったん生値で全行を読む
        var raw = new List<(string Name, int? Depth, string? Queue, double? Start, double? Dur, double? Occ, double?[] Thr)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim().Length == 0) continue;
            var f = SplitCsvLine(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";

            var name = Get(nameCol);
            if (name.Length == 0) continue;

            var thr = new double?[thrCols.Count];
            for (var j = 0; j < thrCols.Count; j++)
                thr[j] = ParseDouble(Get(thrCols[j].Index));

            raw.Add((
                Name: name,
                Depth: (int?)ParseLong(Get(depthCol)),
                Queue: queueCol >= 0 && Get(queueCol).Length > 0 ? Get(queueCol) : null,
                Start: ParseDouble(Get(startCol)) is { } s ? s * startScale : null,
                Dur: ParseDouble(Get(durCol)) is { } d ? d * durScale : null,
                Occ: ParseDouble(Get(occCol)),
                Thr: thr));
        }

        var occScale = occCol >= 0 ? PercentScale(headers[occCol], raw.Select(r => r.Occ)) : 1.0;
        var thrScales = new double[thrCols.Count];
        for (var j = 0; j < thrCols.Count; j++)
            thrScales[j] = PercentScale(headers[thrCols[j].Index], raw.Select(r => r.Thr[j]));

        var rows = new List<TraceRow>(raw.Count);
        long rowId = 0;
        foreach (var r in raw)
        {
            var thr = new List<UnitThroughput>();
            for (var j = 0; j < thrCols.Count; j++)
                if (r.Thr[j] is { } v)
                    thr.Add(new UnitThroughput(thrCols[j].Unit, v * thrScales[j]));

            rows.Add(new TraceRow(
                RowId: rowId++,
                Name: r.Name,
                Depth: r.Depth,
                QueueName: r.Queue,
                StartNs: r.Start,
                DurationNs: r.Dur,
                OccupancyPct: r.Occ is { } o ? o * occScale : null,
                Throughputs: thr));
        }
        return rows;
    }

    /// <summary>ヘッダの単位表記 → ns 換算係数。表記が無ければ ns とみなす。</summary>
    internal static double TimeScaleToNs(string header)
    {
        var h = header.ToLowerInvariant();
        if (h.Contains("(ns)") || h.Contains("nsec") || h.Contains("nanosec")) return 1;
        if (h.Contains("(us)") || h.Contains("(µs)") || h.Contains("(μs)") || h.Contains("usec") || h.Contains("microsec")) return 1_000;
        if (h.Contains("(ms)") || h.Contains("msec") || h.Contains("millisec")) return 1_000_000;
        if (h.Contains("(s)") || h.Contains("(sec)")) return 1_000_000_000;
        return 1;
    }

    /// <summary>ヘッダに % 表記が無く全値が 0..1 に収まる場合は比率とみなして 100 倍する。</summary>
    internal static double PercentScale(string header, IEnumerable<double?> values)
    {
        if (header.Contains('%')) return 1.0;
        double max = 0;
        var any = false;
        foreach (var v in values)
        {
            if (v is not { } x) continue;
            any = true;
            if (x > max) max = x;
        }
        return any && max <= 1.0 ? 100.0 : 1.0;
    }

    /// <summary>"SM Throughput (%)" → "SM"、"SOL TEX" → "TEX" のようにユニット名を抽出する。</summary>
    internal static string UnitFromHeader(string header)
    {
        var s = header;
        // 括弧内(単位表記)を除去
        var open = s.IndexOf('(');
        if (open >= 0) s = s[..open];
        s = s.Replace("throughput", "", StringComparison.OrdinalIgnoreCase);
        // "SOL" は単語として除去("SOL TEX" / "TEX SOL %" の両形に対応)
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bsol\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = s.Replace("%", "");
        return s.Trim(' ', '-', '_', ':', '.').ToUpperInvariant();
    }

    private static int FindColumn(List<string> headers, string[] exact, string[] contains)
    {
        foreach (var c in exact)
        {
            var i = headers.FindIndex(h => h.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        foreach (var c in contains)
        {
            var i = headers.FindIndex(h => h.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static long? ParseLong(string s)
        => long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string s)
        => double.TryParse(s.TrimEnd('%'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>RFC4180 風の 1 行分割(引用符・エスケープ対応)。</summary>
    internal static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
