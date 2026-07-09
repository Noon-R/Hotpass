using Hotpass.Core.Model;
using Microsoft.Data.Sqlite;

namespace Hotpass.Core.Storage;

/// <summary>
/// 共通スキーマ SQLite(design.md §2.3)。アダプタが書き込み、ビューアは読むだけ。
/// enum はスキーマの可読性のため文字列で永続化する。
/// </summary>
public sealed class CaptureStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public CaptureStore(string dbPath)
    {
        // Pooling=False: 既定の接続プールは Dispose 後もファイルハンドルを保持するため、
        // 再インポート時の File.Delete(db) が IOException になる(実測)。都度クローズさせる。
        _conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _conn.Open();
        CreateSchema();
    }

    /// <summary>インメモリ DB(テスト・サンプルデータ用)。</summary>
    public static CaptureStore InMemory() => new(":memory:");

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS captures(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name TEXT NOT NULL,
                source TEXT NOT NULL,
                frame_number INTEGER,
                async_overlap_pct REAL,
                sync_gaps_ns INTEGER,
                provides_occupancy INTEGER NOT NULL DEFAULT 0,
                provides_limiter INTEGER NOT NULL DEFAULT 0,
                provides_sol INTEGER NOT NULL DEFAULT 0);

            CREATE TABLE IF NOT EXISTS passes(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                capture_id INTEGER NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                event_id INTEGER NOT NULL DEFAULT 0,
                start_ns INTEGER NOT NULL DEFAULT 0,
                end_ns INTEGER NOT NULL DEFAULT 0,
                duration_ns INTEGER NOT NULL,
                depth INTEGER NOT NULL DEFAULT 0,
                parent_id INTEGER REFERENCES passes(id),
                queue TEXT NOT NULL,
                category TEXT NOT NULL,
                occupancy_pct REAL,
                occupancy_limiter TEXT,
                sol_top_unit TEXT);

            CREATE TABLE IF NOT EXISTS images(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                capture_id INTEGER NOT NULL REFERENCES captures(id) ON DELETE CASCADE,
                pass_name TEXT,
                resource_name TEXT,
                event_id INTEGER,
                png_path TEXT NOT NULL);

            CREATE INDEX IF NOT EXISTS idx_passes_capture ON passes(capture_id);
            CREATE INDEX IF NOT EXISTS idx_images_capture ON images(capture_id);
            """);
    }

    public long AddCapture(CaptureInfo capture)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO captures(file_name, source, frame_number, async_overlap_pct, sync_gaps_ns,
                                 provides_occupancy, provides_limiter, provides_sol)
            VALUES ($file, $source, $frame, $async, $sync, $occ, $lim, $sol);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$file", capture.FileName);
        cmd.Parameters.AddWithValue("$source", capture.Source.ToString());
        cmd.Parameters.AddWithValue("$frame", (object?)capture.FrameNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$async", (object?)capture.AsyncOverlapPct ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sync", (object?)capture.SyncGapsNs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$occ", capture.ProvidesOccupancy ? 1 : 0);
        cmd.Parameters.AddWithValue("$lim", capture.ProvidesLimiter ? 1 : 0);
        cmd.Parameters.AddWithValue("$sol", capture.ProvidesSol ? 1 : 0);
        var id = (long)cmd.ExecuteScalar()!;
        capture.Id = id;
        return id;
    }

    /// <summary>パス列を一括登録し、採番した Id を各レコードに書き戻す(ParentId は事前解決済み前提)。</summary>
    public void AddPasses(long captureId, IEnumerable<PassRecord> passes)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO passes(capture_id, name, event_id, start_ns, end_ns, duration_ns,
                               depth, parent_id, queue, category, occupancy_pct, occupancy_limiter, sol_top_unit)
            VALUES ($cap, $name, $event, $start, $end, $dur, $depth, $parent, $queue, $cat, $occ, $lim, $sol);
            SELECT last_insert_rowid();
            """;
        foreach (var p in passes)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$cap", captureId);
            cmd.Parameters.AddWithValue("$name", p.Name);
            cmd.Parameters.AddWithValue("$event", p.EventId);
            cmd.Parameters.AddWithValue("$start", p.StartNs);
            cmd.Parameters.AddWithValue("$end", p.EndNs);
            cmd.Parameters.AddWithValue("$dur", p.DurationNs);
            cmd.Parameters.AddWithValue("$depth", p.Depth);
            cmd.Parameters.AddWithValue("$parent", (object?)p.ParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$queue", p.Queue.ToString());
            cmd.Parameters.AddWithValue("$cat", p.Category.ToString());
            cmd.Parameters.AddWithValue("$occ", (object?)p.OccupancyPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lim", (object?)p.OccupancyLimiter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sol", (object?)p.SolTopUnit ?? DBNull.Value);
            p.Id = (long)cmd.ExecuteScalar()!;
        }
        tx.Commit();
    }

    public long AddImage(PassImage image)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO images(capture_id, pass_name, resource_name, event_id, png_path)
            VALUES ($cap, $pass, $res, $event, $path);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$cap", image.CaptureId);
        cmd.Parameters.AddWithValue("$pass", (object?)image.PassName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$res", (object?)image.ResourceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$event", (object?)image.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", image.PngPath);
        var id = (long)cmd.ExecuteScalar()!;
        image.Id = id;
        return id;
    }

    public IReadOnlyList<CaptureInfo> GetCaptures()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, file_name, source, frame_number, async_overlap_pct, sync_gaps_ns, provides_occupancy, provides_limiter, provides_sol FROM captures ORDER BY id";
        using var r = cmd.ExecuteReader();
        var list = new List<CaptureInfo>();
        while (r.Read())
        {
            list.Add(new CaptureInfo
            {
                Id = r.GetInt64(0),
                FileName = r.GetString(1),
                Source = Enum.Parse<CaptureSource>(r.GetString(2)),
                FrameNumber = r.IsDBNull(3) ? null : r.GetInt64(3),
                AsyncOverlapPct = r.IsDBNull(4) ? null : r.GetDouble(4),
                SyncGapsNs = r.IsDBNull(5) ? null : r.GetInt64(5),
                ProvidesOccupancy = r.GetInt64(6) != 0,
                ProvidesLimiter = r.GetInt64(7) != 0,
                ProvidesSol = r.GetInt64(8) != 0,
            });
        }
        return list;
    }

    public IReadOnlyList<PassRecord> GetPasses(long captureId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, event_id, start_ns, end_ns, duration_ns, depth, parent_id,
                   queue, category, occupancy_pct, occupancy_limiter, sol_top_unit
            FROM passes WHERE capture_id = $cap ORDER BY start_ns, depth
            """;
        cmd.Parameters.AddWithValue("$cap", captureId);
        using var r = cmd.ExecuteReader();
        var list = new List<PassRecord>();
        while (r.Read())
        {
            list.Add(new PassRecord
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                EventId = r.GetInt64(2),
                StartNs = r.GetInt64(3),
                EndNs = r.GetInt64(4),
                DurationNs = r.GetInt64(5),
                Depth = r.GetInt32(6),
                ParentId = r.IsDBNull(7) ? null : r.GetInt64(7),
                Queue = Enum.Parse<GpuQueue>(r.GetString(8)),
                Category = Enum.Parse<BottleneckCategory>(r.GetString(9)),
                OccupancyPct = r.IsDBNull(10) ? null : r.GetDouble(10),
                OccupancyLimiter = r.IsDBNull(11) ? null : r.GetString(11),
                SolTopUnit = r.IsDBNull(12) ? null : r.GetString(12),
            });
        }
        return list;
    }

    public IReadOnlyList<PassImage> GetImages(long captureId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, capture_id, pass_name, resource_name, event_id, png_path FROM images WHERE capture_id = $cap ORDER BY id";
        cmd.Parameters.AddWithValue("$cap", captureId);
        using var r = cmd.ExecuteReader();
        var list = new List<PassImage>();
        while (r.Read())
        {
            list.Add(new PassImage
            {
                Id = r.GetInt64(0),
                CaptureId = r.GetInt64(1),
                PassName = r.IsDBNull(2) ? null : r.GetString(2),
                ResourceName = r.IsDBNull(3) ? null : r.GetString(3),
                EventId = r.IsDBNull(4) ? null : r.GetInt64(4),
                PngPath = r.GetString(5),
            });
        }
        return list;
    }

    public void RemoveCapture(long captureId)
    {
        Exec($"PRAGMA foreign_keys = ON");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM captures WHERE id = $cap";
        cmd.Parameters.AddWithValue("$cap", captureId);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
