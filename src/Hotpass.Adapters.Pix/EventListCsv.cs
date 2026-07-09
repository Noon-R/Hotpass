using System.Globalization;

namespace Hotpass.Adapters.Pix;

/// <summary>
/// save-event-list CSV の 1 行(正規化前)。
/// pixtool 2603.25 の実出力: `Queue ID, Parent, Name, Global ID, &lt;counters...&gt;`
///   - 先頭列(ヘッダ名は "Queue ID" だが実体は連番のイベント ID。PIX UI の event # と一致)
///   - Parent は親イベントの連番 ID(-1 = ルート)。マーカー(PIXBeginEvent)配下の階層はこれで表現される
///   - Global ID は GPU 操作を持つ行のみ(recapture-region / save-resource --global-id 用)
///   - 時間は "TOP to EOP Duration (ns)" / "Execution Start Time (ns)" 等のカウンタ列
/// </summary>
public sealed record EventRow(
    long RowId,
    long? ParentRowId,
    string Name,
    long? GlobalId,
    double? StartNs,
    double? DurationNs);

/// <summary>
/// pixtool save-event-list の CSV パーサ。
/// 列構成は PIX バージョン・指定カウンタ依存のため、列名の部分一致で防御的に解決する(CLAUDE.md 方針)。
/// </summary>
public static class EventListCsv
{
    public static List<EventRow> Parse(string csvPath)
    {
        using var reader = new StreamReader(csvPath);
        return Parse(reader);
    }

    public static List<EventRow> Parse(TextReader reader)
    {
        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("CSV が空です");
        var headers = SplitCsvLine(headerLine)
            .Select(h => h.Trim().TrimStart('﻿'))
            .ToList();

        int idCol = FindColumn(headers, exact: ["Queue ID", "Event ID"], contains: []);
        int parentCol = FindColumn(headers, exact: ["Parent"], contains: ["parent"]);
        int nameCol = FindColumn(headers, exact: ["Name"], contains: ["name", "event", "marker"]);
        int gidCol = FindColumn(headers, exact: ["Global ID"], contains: ["global"]);
        // 時間カウンタは優先順で解決(TOP to EOP が GPU 実行時間として最も素直)
        int durCol = FindColumn(headers,
            exact: ["TOP to EOP Duration (ns)"],
            contains: ["top to eop duration", "gpu duration", "executionduration", "duration"]);
        int startCol = FindColumn(headers,
            exact: ["Execution Start Time (ns)"],
            contains: ["execution start", "eop start", "executionstart", "start time", "start"]);

        if (nameCol < 0)
            throw new InvalidDataException(
                $"イベント名の列が見つかりません。ヘッダ: {string.Join(", ", headers)}");

        var rows = new List<EventRow>();
        string? line;
        long fallbackId = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = SplitCsvLine(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";

            var name = Get(nameCol);
            if (name.Length == 0) continue;

            var parent = ParseLong(Get(parentCol));
            rows.Add(new EventRow(
                RowId: ParseLong(Get(idCol)) ?? fallbackId,
                ParentRowId: parent is null or < 0 ? null : parent,
                Name: name,
                GlobalId: ParseLong(Get(gidCol)),
                StartNs: ParseDouble(Get(startCol)),
                DurationNs: ParseDouble(Get(durCol))));
            fallbackId++;
        }
        return rows;
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
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

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
