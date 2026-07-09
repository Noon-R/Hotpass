using System.Globalization;

namespace Hotpass.Adapters.Pix;

/// <summary>save-event-list CSV の 1 行(正規化前)。</summary>
public sealed record EventRow(
    long EventId,
    string Name,
    int Depth,
    double? StartNs,
    double? DurationNs);

/// <summary>
/// pixtool save-event-list の CSV パーサ。
/// 列構成は PIX バージョン依存のため、列名の部分一致で防御的に解決する(CLAUDE.md 方針)。
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
        var headers = SplitCsvLine(headerLine);

        int idCol = FindColumn(headers, "event id", "eventid", "global id", "id");
        int nameCol = FindColumn(headers, "name", "event", "marker");
        int depthCol = FindColumn(headers, "depth", "level", "nesting");
        int startCol = FindColumn(headers, "start time", "start (ns)", "starttime", "start");
        int durCol = FindColumn(headers, "duration", "gpu duration", "exclusive duration");

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
            string Get(int i) => i >= 0 && i < f.Count ? f[i] : "";

            var name = Get(nameCol);
            if (name.Length == 0) continue;

            rows.Add(new EventRow(
                EventId: ParseLong(Get(idCol)) ?? ++fallbackId,
                Name: name,
                Depth: (int)(ParseLong(Get(depthCol)) ?? 0),
                StartNs: ParseDouble(Get(startCol)),
                DurationNs: ParseDouble(Get(durCol))));
        }
        return rows;
    }

    private static int FindColumn(List<string> headers, params string[] candidates)
    {
        // 完全一致 → 部分一致の順で探す("GPU Duration (ns)" のような列名に対応)
        foreach (var c in candidates)
        {
            var i = headers.FindIndex(h => h.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        foreach (var c in candidates)
        {
            var i = headers.FindIndex(h => h.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static long? ParseLong(string s)
        => long.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string s)
        => double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

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
