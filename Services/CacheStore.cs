using Microsoft.Data.Sqlite;
using S3Explorer.Models;

namespace S3Explorer.Services;

public sealed record ScanInfo(DateTime ScannedAtUtc, long FileCount, long TotalSize, long FolderCount, long DurationMs);

public sealed record FolderInfo(long FileCount, long TotalSize);

/// <summary>A downloaded file: relative key, local path, and the object's size + last_modified at download time.</summary>
public sealed record DownloadRecord(string Key, string LocalPath, long Size, long LastModifiedTicksUtc);

/// <summary>
/// Cache metadata trong SQLite (%LocalAppData%\S3Explorer\cache.db).
/// Each object is stored with its parent folder so opening a folder takes a single indexed query,
/// and each folder's total file count / size is precomputed during the scan.
/// </summary>
public sealed class CacheStore
{
    private readonly string _cs;

    public CacheStore(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        using var c = Open();
        Exec(c, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS objects(
                profile_id    TEXT    NOT NULL,
                key           TEXT    NOT NULL,   -- path relative to the prefix
                parent        TEXT    NOT NULL,   -- parent folder, '' = root, ends with '/'
                name          TEXT    NOT NULL,
                ext           TEXT    NOT NULL,
                size          INTEGER NOT NULL,
                last_modified INTEGER NOT NULL,   -- ticks UTC
                storage_class TEXT,
                PRIMARY KEY(profile_id, key)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_objects_parent ON objects(profile_id, parent);

            CREATE TABLE IF NOT EXISTS folders(
                profile_id    TEXT    NOT NULL,
                path          TEXT    NOT NULL,
                parent        TEXT    NOT NULL,
                name          TEXT    NOT NULL,
                file_count    INTEGER NOT NULL,   -- recursive
                total_size    INTEGER NOT NULL,   -- recursive
                last_modified INTEGER NOT NULL,
                PRIMARY KEY(profile_id, path)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS ix_folders_parent ON folders(profile_id, parent);

            CREATE TABLE IF NOT EXISTS scans(
                profile_id   TEXT PRIMARY KEY,
                scanned_at   INTEGER NOT NULL,
                file_count   INTEGER NOT NULL,
                total_size   INTEGER NOT NULL,
                folder_count INTEGER NOT NULL,
                duration_ms  INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS downloads(
                profile_id    TEXT    NOT NULL,
                key           TEXT    NOT NULL,   -- path relative to the prefix
                local_path    TEXT    NOT NULL,
                size          INTEGER NOT NULL,   -- of the object at download time, to detect changed objects
                last_modified INTEGER NOT NULL,
                downloaded_at INTEGER NOT NULL,
                PRIMARY KEY(profile_id, key)) WITHOUT ROWID;

            -- objects of a scan in progress, moved into `objects` on commit
            CREATE TABLE IF NOT EXISTS scan_staging(
                scan_id       TEXT    NOT NULL,
                key           TEXT    NOT NULL,
                parent        TEXT    NOT NULL,
                name          TEXT    NOT NULL,
                ext           TEXT    NOT NULL,
                size          INTEGER NOT NULL,
                last_modified INTEGER NOT NULL,
                storage_class TEXT,
                PRIMARY KEY(scan_id, key)) WITHOUT ROWID;
            DELETE FROM scan_staging;   -- leftovers of a scan interrupted by a crash
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        // several scans / transfers may write at once; a scan commit can hold the write lock for a few seconds
        Exec(c, null, "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=60000;");
        return c;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, string? profileId = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        if (profileId != null) cmd.Parameters.AddWithValue("$p", profileId);
        cmd.ExecuteNonQuery();
    }

    public ScanInfo? GetScanInfo(string profileId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT scanned_at, file_count, total_size, folder_count, duration_ms FROM scans WHERE profile_id=$p";
        cmd.Parameters.AddWithValue("$p", profileId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ScanInfo(new DateTime(r.GetInt64(0), DateTimeKind.Utc), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    public FolderInfo? GetFolder(string profileId, string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT file_count, total_size FROM folders WHERE profile_id=$p AND path=$path";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$path", path);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new FolderInfo(r.GetInt64(0), r.GetInt64(1)) : null;
    }

    /// <summary>Direct subfolders and files of a folder — read entirely from the cache.</summary>
    public List<S3Entry> GetChildren(string profileId, string parent)
    {
        var list = new List<S3Entry>();
        using var c = Open();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT path, name, file_count, total_size, last_modified
                FROM folders WHERE profile_id=$p AND parent=$parent AND path<>''
                """;
            cmd.Parameters.AddWithValue("$p", profileId);
            cmd.Parameters.AddWithValue("$parent", parent);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ticks = r.GetInt64(4);
                list.Add(new S3Entry
                {
                    IsFolder = true,
                    RelativePath = r.GetString(0),
                    Name = r.GetString(1) is { Length: > 0 } n ? n : "(unnamed)",
                    FileCount = r.GetInt64(2),
                    Size = r.GetInt64(3),
                    LastModifiedUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null
                });
            }
        }

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT o.key, o.name, o.ext, o.size, o.last_modified, o.storage_class, d.local_path
                FROM objects o
                LEFT JOIN downloads d ON d.profile_id=o.profile_id AND d.key=o.key
                                     AND d.size=o.size AND d.last_modified=o.last_modified
                WHERE o.profile_id=$p AND o.parent=$parent
                """;
            cmd.Parameters.AddWithValue("$p", profileId);
            cmd.Parameters.AddWithValue("$parent", parent);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var ticks = r.GetInt64(4);
                list.Add(new S3Entry
                {
                    RelativePath = r.GetString(0),
                    Name = r.GetString(1),
                    Extension = r.GetString(2),
                    Size = r.GetInt64(3),
                    LastModifiedUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null,
                    StorageClass = r.IsDBNull(5) ? "" : r.GetString(5),
                    LocalPath = r.IsDBNull(6) ? null : r.GetString(6)
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Update the cache after deleting on S3: remove the files / folders (relative paths, folders end with '/'),
    /// subtract file counts and sizes from every parent folder, and update the totals in the scans table.
    /// </summary>
    public void ApplyDelete(string profileId, IEnumerable<string> fileKeys, IEnumerable<string> folderPaths)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var delta = new Dictionary<string, (long Count, long Size)>(StringComparer.Ordinal);
        long totalCount = 0, totalSize = 0;

        void AddDelta(string folder, long count, long size)
        {
            totalCount += count;
            totalSize += size;
            for (var f = folder; ; f = ParentOf(f))
            {
                delta.TryGetValue(f, out var d);
                delta[f] = (d.Count + count, d.Size + size);
                if (f.Length == 0) break;
            }
        }

        SqliteCommand Cmd(string sql)
        {
            var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$p", profileId);
            return cmd;
        }

        foreach (var key in fileKeys)
        {
            long size;
            string parent;
            using (var q = Cmd("SELECT size, parent FROM objects WHERE profile_id=$p AND key=$k"))
            {
                q.Parameters.AddWithValue("$k", key);
                using var r = q.ExecuteReader();
                if (!r.Read()) continue;
                size = r.GetInt64(0);
                parent = r.GetString(1);
            }
            using (var d = Cmd("DELETE FROM objects WHERE profile_id=$p AND key=$k"))
            {
                d.Parameters.AddWithValue("$k", key);
                d.ExecuteNonQuery();
            }
            AddDelta(parent, 1, size);
        }

        foreach (var folder in folderPaths)
        {
            if (folder.Length == 0) continue;
            long count, size;
            using (var q = Cmd("SELECT file_count, total_size FROM folders WHERE profile_id=$p AND path=$f"))
            {
                q.Parameters.AddWithValue("$f", folder);
                using var r = q.ExecuteReader();
                if (!r.Read()) continue;
                count = r.GetInt64(0);
                size = r.GetInt64(1);
            }
            using (var d = Cmd("DELETE FROM objects WHERE profile_id=$p AND substr(key, 1, length($f)) = $f"))
            {
                d.Parameters.AddWithValue("$f", folder);
                d.ExecuteNonQuery();
            }
            using (var d = Cmd("DELETE FROM folders WHERE profile_id=$p AND substr(path, 1, length($f)) = $f"))
            {
                d.Parameters.AddWithValue("$f", folder);
                d.ExecuteNonQuery();
            }
            AddDelta(ParentOf(folder), count, size);
        }

        using (var u = Cmd("""
            UPDATE folders SET file_count = max(0, file_count - $c), total_size = max(0, total_size - $s)
            WHERE profile_id=$p AND path=$path
            """))
        {
            var pc = u.Parameters.Add("$c", SqliteType.Integer);
            var ps = u.Parameters.Add("$s", SqliteType.Integer);
            var pp = u.Parameters.Add("$path", SqliteType.Text);
            foreach (var (path, d) in delta)
            {
                pc.Value = d.Count;
                ps.Value = d.Size;
                pp.Value = path;
                u.ExecuteNonQuery();
            }
        }

        using (var u = Cmd("""
            UPDATE scans SET file_count = max(0, file_count - $c), total_size = max(0, total_size - $s),
                folder_count = (SELECT count(*) - 1 FROM folders WHERE profile_id=$p)
            WHERE profile_id=$p
            """))
        {
            u.Parameters.AddWithValue("$c", totalCount);
            u.Parameters.AddWithValue("$s", totalSize);
            u.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Update the cache after copying objects onto S3: insert / replace the objects (relative keys; keys ending
    /// with '/' are folder markers), create missing folders, add file counts and sizes to every parent folder,
    /// and update the totals in the scans table.
    /// </summary>
    public void ApplyAdd(string profileId, IEnumerable<(string Key, long Size)> objects, long ticksUtc, string? storageClass)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var delta = new Dictionary<string, (long Count, long Size)>(StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        long totalCount = 0, totalSize = 0;

        SqliteCommand Cmd(string sql)
        {
            var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$p", profileId);
            return cmd;
        }

        using var insFolder = Cmd("""
            INSERT OR IGNORE INTO folders(profile_id, path, parent, name, file_count, total_size, last_modified)
            VALUES($p, $f, $parent, $name, 0, 0, 0)
            """);
        var fPath = insFolder.Parameters.Add("$f", SqliteType.Text);
        var fParent = insFolder.Parameters.Add("$parent", SqliteType.Text);
        var fName = insFolder.Parameters.Add("$name", SqliteType.Text);

        void EnsureFolder(string path)
        {
            for (var f = path; known.Add(f); f = ParentOf(f))
            {
                fPath.Value = f;
                fParent.Value = f.Length == 0 ? "" : ParentOf(f);
                fName.Value = FolderName(f);
                insFolder.ExecuteNonQuery();
                if (f.Length == 0) break;
            }
        }

        using var oldSize = Cmd("SELECT size FROM objects WHERE profile_id=$p AND key=$k");
        var oKey = oldSize.Parameters.Add("$k", SqliteType.Text);

        using var ins = Cmd("""
            INSERT OR REPLACE INTO objects(profile_id, key, parent, name, ext, size, last_modified, storage_class)
            VALUES($p, $k, $parent, $name, $ext, $size, $mod, $class)
            """);
        var pKey = ins.Parameters.Add("$k", SqliteType.Text);
        var pParent = ins.Parameters.Add("$parent", SqliteType.Text);
        var pName = ins.Parameters.Add("$name", SqliteType.Text);
        var pExt = ins.Parameters.Add("$ext", SqliteType.Text);
        var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
        ins.Parameters.AddWithValue("$mod", ticksUtc);
        ins.Parameters.AddWithValue("$class", (object?)storageClass ?? DBNull.Value);

        foreach (var (key, size) in objects)
        {
            if (key.Length == 0) continue;
            if (key.EndsWith('/')) { EnsureFolder(key); continue; }

            int slash = key.LastIndexOf('/');
            var parent = slash < 0 ? "" : key[..(slash + 1)];
            var name = key[(slash + 1)..];
            EnsureFolder(parent);

            oKey.Value = key;
            var old = oldSize.ExecuteScalar() is long o ? o : (long?)null;

            pKey.Value = key;
            pParent.Value = parent;
            pName.Value = name;
            pExt.Value = GetExt(name);
            pSize.Value = size;
            ins.ExecuteNonQuery();

            long dc = old == null ? 1 : 0, ds = size - (old ?? 0);
            totalCount += dc;
            totalSize += ds;
            for (var f = parent; ; f = ParentOf(f))
            {
                delta.TryGetValue(f, out var d);
                delta[f] = (d.Count + dc, d.Size + ds);
                if (f.Length == 0) break;
            }
        }

        using (var u = Cmd("""
            UPDATE folders SET file_count = max(0, file_count + $c), total_size = max(0, total_size + $s),
                last_modified = max(last_modified, $mod)
            WHERE profile_id=$p AND path=$path
            """))
        {
            var pc = u.Parameters.Add("$c", SqliteType.Integer);
            var ps = u.Parameters.Add("$s", SqliteType.Integer);
            var pp = u.Parameters.Add("$path", SqliteType.Text);
            u.Parameters.AddWithValue("$mod", ticksUtc);
            foreach (var (path, d) in delta)
            {
                pc.Value = d.Count;
                ps.Value = d.Size;
                pp.Value = path;
                u.ExecuteNonQuery();
            }
        }

        using (var u = Cmd("""
            UPDATE scans SET file_count = max(0, file_count + $c), total_size = max(0, total_size + $s),
                folder_count = (SELECT count(*) - 1 FROM folders WHERE profile_id=$p)
            WHERE profile_id=$p
            """))
        {
            u.Parameters.AddWithValue("$c", totalCount);
            u.Parameters.AddWithValue("$s", totalSize);
            u.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Record downloaded files (overwriting older records for the same key).</summary>
    public void RecordDownloads(string profileId, IEnumerable<DownloadRecord> records)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO downloads(profile_id, key, local_path, size, last_modified, downloaded_at)
            VALUES($p, $k, $local, $size, $mod, $at)
            """;
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Ticks);
        var pKey = cmd.Parameters.Add("$k", SqliteType.Text);
        var pLocal = cmd.Parameters.Add("$local", SqliteType.Text);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pMod = cmd.Parameters.Add("$mod", SqliteType.Integer);
        cmd.Prepare();
        foreach (var d in records)
        {
            pKey.Value = d.Key;
            pLocal.Value = d.LocalPath;
            pSize.Value = d.Size;
            pMod.Value = d.LastModifiedTicksUtc;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ForgetDownload(string profileId, string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM downloads WHERE profile_id=$p AND key=$k";
        cmd.Parameters.AddWithValue("$p", profileId);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    public void DeleteProfile(string profileId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, "DELETE FROM objects   WHERE profile_id=$p", profileId);
        Exec(c, tx, "DELETE FROM folders   WHERE profile_id=$p", profileId);
        Exec(c, tx, "DELETE FROM scans     WHERE profile_id=$p", profileId);
        Exec(c, tx, "DELETE FROM downloads WHERE profile_id=$p", profileId);
        tx.Commit();
    }

    /// <summary>
    /// Begin replacing a profile's cache. Old data can still be read normally
    /// until Commit; on cancel/error (Dispose without Commit) the old cache stays intact.
    /// Objects are staged in short batches, so other scans / transfers can write to the cache meanwhile.
    /// </summary>
    public ReplaceSession BeginReplace(string profileId) => new(Open(), profileId);

    public sealed class ReplaceSession : IDisposable
    {
        private sealed class Agg { public long Count, Size, MaxTicks; }

        private const int BatchSize = 2000;

        private readonly SqliteConnection _conn;
        private SqliteTransaction? _tx;                   // current staging batch
        private int _pending;
        private readonly SqliteCommand _ins;
        private readonly SqliteParameter _pKey, _pParent, _pName, _pExt, _pSize, _pMod, _pClass;
        private readonly string _profileId;
        private readonly string _scanId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, Agg> _folders = new(StringComparer.Ordinal) { [""] = new Agg() };
        private bool _done;

        public long FileCount { get; private set; }
        public long TotalBytes { get; private set; }

        internal ReplaceSession(SqliteConnection conn, string profileId)
        {
            _conn = conn;
            _profileId = profileId;

            _ins = conn.CreateCommand();
            _ins.CommandText = """
                INSERT OR REPLACE INTO scan_staging(scan_id, key, parent, name, ext, size, last_modified, storage_class)
                VALUES($s, $k, $parent, $name, $ext, $size, $mod, $class)
                """;
            _ins.Parameters.AddWithValue("$s", _scanId);
            _pKey = _ins.Parameters.Add("$k", SqliteType.Text);
            _pParent = _ins.Parameters.Add("$parent", SqliteType.Text);
            _pName = _ins.Parameters.Add("$name", SqliteType.Text);
            _pExt = _ins.Parameters.Add("$ext", SqliteType.Text);
            _pSize = _ins.Parameters.Add("$size", SqliteType.Integer);
            _pMod = _ins.Parameters.Add("$mod", SqliteType.Integer);
            _pClass = _ins.Parameters.Add("$class", SqliteType.Text);
        }

        private void Flush()
        {
            _tx?.Commit();
            _tx?.Dispose();
            _tx = null;
            _pending = 0;
        }

        public void Add(string rel, long size, long ticksUtc, string? storageClass)
        {
            if (rel.Length == 0) return;                 // object equal to the prefix itself

            if (rel.EndsWith('/'))                       // "folder marker" created by the S3 console
            {
                EnsureFolder(rel);
                return;
            }

            int slash = rel.LastIndexOf('/');
            var parent = slash < 0 ? "" : rel[..(slash + 1)];
            var name = rel[(slash + 1)..];

            EnsureFolder(parent);
            for (var f = parent; ; f = ParentOf(f))
            {
                var a = _folders[f];
                a.Count++;
                a.Size += size;
                if (ticksUtc > a.MaxTicks) a.MaxTicks = ticksUtc;
                if (f.Length == 0) break;
            }

            _pKey.Value = rel;
            _pParent.Value = parent;
            _pName.Value = name;
            _pExt.Value = GetExt(name);
            _pSize.Value = size;
            _pMod.Value = ticksUtc;
            _pClass.Value = (object?)storageClass ?? DBNull.Value;
            if (_tx == null)
            {
                _tx = _conn.BeginTransaction();
                _ins.Transaction = _tx;
            }
            _ins.ExecuteNonQuery();
            if (++_pending >= BatchSize) Flush();

            FileCount++;
            TotalBytes += size;
        }

        public ScanInfo Commit(long durationMs)
        {
            Flush();
            // Swap the staged objects in with one short write transaction
            using var tx = _conn.BeginTransaction();
            Exec(_conn, tx, "DELETE FROM objects WHERE profile_id=$p", _profileId);
            Exec(_conn, tx, "DELETE FROM folders WHERE profile_id=$p", _profileId);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO objects(profile_id, key, parent, name, ext, size, last_modified, storage_class)
                    SELECT $p, key, parent, name, ext, size, last_modified, storage_class FROM scan_staging WHERE scan_id=$s;
                    DELETE FROM scan_staging WHERE scan_id=$s;
                    """;
                cmd.Parameters.AddWithValue("$p", _profileId);
                cmd.Parameters.AddWithValue("$s", _scanId);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO folders(profile_id, path, parent, name, file_count, total_size, last_modified)
                    VALUES($p, $path, $parent, $name, $count, $size, $mod)
                    """;
                cmd.Parameters.AddWithValue("$p", _profileId);
                var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
                var pParent = cmd.Parameters.Add("$parent", SqliteType.Text);
                var pName = cmd.Parameters.Add("$name", SqliteType.Text);
                var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
                var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
                var pMod = cmd.Parameters.Add("$mod", SqliteType.Integer);
                cmd.Prepare();

                foreach (var (path, a) in _folders)
                {
                    pPath.Value = path;
                    pParent.Value = path.Length == 0 ? "" : ParentOf(path);
                    pName.Value = FolderName(path);
                    pCount.Value = a.Count;
                    pSize.Value = a.Size;
                    pMod.Value = a.MaxTicks;
                    cmd.ExecuteNonQuery();
                }
            }

            var info = new ScanInfo(DateTime.UtcNow, FileCount, TotalBytes, _folders.Count - 1, durationMs);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT OR REPLACE INTO scans(profile_id, scanned_at, file_count, total_size, folder_count, duration_ms)
                    VALUES($p, $at, $files, $size, $folders, $ms)
                    """;
                cmd.Parameters.AddWithValue("$p", _profileId);
                cmd.Parameters.AddWithValue("$at", info.ScannedAtUtc.Ticks);
                cmd.Parameters.AddWithValue("$files", info.FileCount);
                cmd.Parameters.AddWithValue("$size", info.TotalSize);
                cmd.Parameters.AddWithValue("$folders", info.FolderCount);
                cmd.Parameters.AddWithValue("$ms", info.DurationMs);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            _done = true;
            return info;
        }

        public void Dispose()
        {
            if (!_done)
            {
                try { _tx?.Rollback(); } catch { /* already closed */ }
                try
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM scan_staging WHERE scan_id=$s";
                    cmd.Parameters.AddWithValue("$s", _scanId);
                    cmd.ExecuteNonQuery();
                }
                catch { /* cleared on next start */ }
            }
            _ins.Dispose();
            _tx?.Dispose();
            _conn.Dispose();
        }

        private void EnsureFolder(string path)
        {
            while (!_folders.ContainsKey(path))
            {
                _folders[path] = new Agg();
                path = ParentOf(path);
            }
        }
    }

    private static string GetExt(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[dot..].ToLowerInvariant();
    }

    /// <summary>"a/b/" → "a/", "a/" → "".</summary>
    public static string ParentOf(string folderPath)
    {
        if (folderPath.Length <= 1) return "";
        int i = folderPath.LastIndexOf('/', folderPath.Length - 2);
        return i < 0 ? "" : folderPath[..(i + 1)];
    }

    /// <summary>"a/b/" → "b".</summary>
    public static string FolderName(string folderPath)
    {
        if (folderPath.Length == 0) return "";
        var trimmed = folderPath[..^1];
        int i = trimmed.LastIndexOf('/');
        return i < 0 ? trimmed : trimmed[(i + 1)..];
    }
}
