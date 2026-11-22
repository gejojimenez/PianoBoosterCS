// Ported from Library.h and Library.cpp of PianoBooster (C++): the song library, kept in an
// SQLite database (library.db) next to the settings. Same schema as the C++, so both programs
// share the same file. GPLv3.
//
// QSqlDatabase/QSqlQuery → Microsoft.Data.Sqlite, one connection open while the library lives.
// Where the C++ ignores a failed query, so does this (SqliteException is caught); where it logs
// it (ppLogError), this logs it with Log.Error. Like the C++, it is used from one thread (the
// thread of the interface).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PianoBooster.Core;

namespace PianoBooster.Data;

/// <summary>The song library (CLibrary): the locations (folders) with MIDI files and what is
/// known about each song (favourite, when it was last opened, its settings and named parts).</summary>
public sealed class Library : ISongLibrary, IDisposable
{
    public const string FileName = "library.db";

    private const string SongColumns =
        "path, hash, favourite, last_opened, times_opened, title, time_sig_top, time_sig_bottom, " +
        "time_sig_changes, key_sharps, key_minor, tempo_bpm, tempo_changes, duration_ms, bars, " +
        "(SELECT COUNT(*) FROM parts WHERE parts.song_id = songs.id)";

    // QDateTime::toString(Qt::ISODate) of a local time
    private const string IsoDateFormat = "yyyy-MM-ddTHH:mm:ss";

    private readonly SqliteConnection? _db;
    private SqliteTransaction? _transaction;

    /// <summary>Opens (creating it if needed) the database in the given folder.</summary>
    public Library(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder); // QDir().mkpath(folder)
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(folder, FileName),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, // the file is closed when the library is
            };
            _db = new SqliteConnection(builder.ToString());
            _db.Open();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Cannot open the library database: " + e.Message);
            _db?.Dispose();
            _db = null;
            return;
        }

        IsOpen = Exec("CREATE TABLE IF NOT EXISTS locations (path TEXT PRIMARY KEY)") &&
                 Exec("CREATE TABLE IF NOT EXISTS songs (" +
                      "id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, hash TEXT, " +
                      "favourite INTEGER NOT NULL DEFAULT 0, last_opened TEXT, times_opened INTEGER NOT NULL DEFAULT 0, " +
                      "title TEXT, time_sig_top INTEGER, time_sig_bottom INTEGER, time_sig_changes INTEGER, " +
                      "key_sharps INTEGER, key_minor INTEGER, tempo_bpm REAL, tempo_changes INTEGER, " +
                      "duration_ms INTEGER, bars INTEGER, info_valid INTEGER NOT NULL DEFAULT 0)") &&
                 Exec("CREATE INDEX IF NOT EXISTS songs_hash ON songs(hash)") &&
                 Exec("CREATE TABLE IF NOT EXISTS parts (" +
                      "id INTEGER PRIMARY KEY, song_id INTEGER NOT NULL REFERENCES songs(id) ON DELETE CASCADE, " +
                      "name TEXT NOT NULL, start_bar INTEGER, start_beat INTEGER, start_ticks INTEGER, " +
                      "end_bar INTEGER, end_beat INTEGER, end_ticks INTEGER)") &&
                 Exec("CREATE INDEX IF NOT EXISTS parts_song ON parts(song_id)") &&
                 // version 3: the settings to practise each song, and the last song of each folder
                 AddColumn("songs", "hand", "TEXT") &&
                 AddColumn("songs", "speed", "INTEGER") &&
                 AddColumn("songs", "left_channel", "INTEGER") &&
                 AddColumn("songs", "right_channel", "INTEGER") &&
                 Exec("CREATE TABLE IF NOT EXISTS folders (path TEXT PRIMARY KEY, last_song TEXT)") &&
                 Exec("PRAGMA foreign_keys = ON") &&
                 Exec("PRAGMA user_version = 3");
    }

    public bool IsOpen { get; }

    public void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        _db?.Dispose();
    }

    public static IReadOnlyList<string> MidiFileFilters() => new[] { "*.mid", "*.midi", "*.kar" };

    /// <summary>QDir::cleanPath(QFileInfo(path).absoluteFilePath()): absolute, with '/' separators,
    /// without "." or ".." or a trailing '/', and (on Windows) the drive letter in capitals.</summary>
    public static string NormalisePath(string path)
    {
        string full;
        try
        {
            full = path.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            full = path;
        }
        return CleanPath(full);
    }

    // QDir::cleanPath() of a path that Path.GetFullPath has already resolved
    private static string CleanPath(string path)
    {
        string result = path.Replace('\\', '/');
        bool unc = result.StartsWith("//", StringComparison.Ordinal);
        while (result.Contains("//", StringComparison.Ordinal))
            result = result.Replace("//", "/", StringComparison.Ordinal);
        if (unc)
            result = "/" + result;
        bool root = result == "/" || (result.Length == 3 && result[1] == ':' && result[2] == '/');
        if (!root && result.Length > 1 && result.EndsWith('/'))
            result = result.TrimEnd('/');
        if (OperatingSystem.IsWindows() && result.Length >= 2 && result[1] == ':' && char.IsAsciiLetterLower(result[0]))
            result = char.ToUpperInvariant(result[0]) + result[1..];
        return result;
    }

    /// <summary>The SHA-1 of the file contents in lower-case hexadecimal, empty if it can't be read.</summary>
    public static string FileHash(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA1.HashData(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    // ---- the queries ----

    private SqliteCommand Command(string sql, params object?[] values)
    {
        // The '?' of the Qt queries (addBindValue, in order) become $1, $2... (Microsoft.Data.Sqlite binds by name).
        // None of the queries has a '?' inside a literal.
        var text = new System.Text.StringBuilder(sql.Length + 8);
        int count = 0;
        foreach (char ch in sql)
        {
            if (ch == '?')
                text.Append('$').Append(++count);
            else
                text.Append(ch);
        }
        if (count != values.Length)
            throw new ArgumentException($"{count} parameters and {values.Length} values: {sql}");

        SqliteCommand command = _db!.CreateCommand();
        command.CommandText = text.ToString();
        command.Transaction = _transaction;
        for (int i = 0; i < values.Length; ++i)
            command.Parameters.AddWithValue("$" + (i + 1).ToString(CultureInfo.InvariantCulture), values[i] ?? DBNull.Value);
        return command;
    }

    // Runs a statement; false (and logged) on error, as CLibrary::exec
    private bool Exec(string sql)
    {
        if (_db == null)
            return false;
        try
        {
            using SqliteCommand command = Command(sql);
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException e)
        {
            Log.Error("Library database error: " + e.Message);
            return false;
        }
    }

    // A statement with values whose error the C++ ignores (q.exec() without checking) or logs
    private bool Run(bool logError, string sql, params object?[] values)
    {
        if (_db == null)
            return false;
        try
        {
            using SqliteCommand command = Command(sql, values);
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException e)
        {
            if (logError)
                Log.Error("Library database error: " + e.Message);
            return false;
        }
    }

    // Adds a column to a table unless it already has it (made by an older version)
    private bool AddColumn(string table, string column, string type)
    {
        try
        {
            using SqliteCommand command = Command($"PRAGMA table_info({table})");
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                if (Text(reader, 1) == column)
                    return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        return Exec($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private long LastInsertId()
    {
        using SqliteCommand command = Command("SELECT last_insert_rowid()");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // QVariant::toString(), toInt(), toLongLong(), toDouble() of a column: 0 or "" if NULL
    private static string Text(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? "" : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? "";
    private static long Integer(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column))
            return 0;
        object value = reader.GetValue(column);
        return value switch
        {
            long l => l,
            double d => (long)d,
            string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0,
            _ => 0,
        };
    }
    private static double Real(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column))
            return 0.0;
        object value = reader.GetValue(column);
        return value switch
        {
            double d => d,
            long l => l,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0,
            _ => 0.0,
        };
    }

    // A null QString (an empty hash, a song without title) is bound as NULL
    private static object? TextOrNull(string text) => text.Length == 0 ? null : text;

    // ---- locations ----

    public List<string> Locations()
    {
        var result = new List<string>();
        if (_db == null)
            return result;
        try
        {
            using SqliteCommand command = Command("SELECT path FROM locations ORDER BY path COLLATE NOCASE");
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(Text(reader, 0));
        }
        catch (SqliteException)
        {
        }
        return result;
    }

    public void AddLocation(string folder) =>
        Run(false, "INSERT OR IGNORE INTO locations (path) VALUES (?)", NormalisePath(folder));

    public void RemoveLocation(string folder) =>
        Run(false, "DELETE FROM locations WHERE path = ?", NormalisePath(folder));

    // ---- songs ----

    // Finds the song by its path, or else by its contents if it has been moved or renamed,
    // or else adds it. Returns its id, or -1 on error.
    private long FindOrCreateSong(string path, string hash)
    {
        if (_db == null)
            return -1;
        long id = -1;
        try
        {
            using (SqliteCommand find = Command("SELECT id FROM songs WHERE path = ?", path))
            using (SqliteDataReader reader = find.ExecuteReader())
            {
                if (reader.Read())
                    id = Integer(reader, 0);
            }
            if (id < 0 && hash.Length > 0)
            {
                using SqliteCommand find = Command("SELECT id, path FROM songs WHERE hash = ?", hash);
                using SqliteDataReader reader = find.ExecuteReader();
                while (reader.Read())
                    if (!Path.Exists(Text(reader, 1))) // QFile::exists: also true for a folder
                    {
                        id = Integer(reader, 0);
                        break;
                    }
            }
        }
        catch (SqliteException)
        {
            id = -1;
        }
        if (id < 0)
        {
            if (!Run(false, "INSERT INTO songs (path) VALUES (?)", path))
                return -1;
            id = LastInsertId();
        }
        return id;
    }

    public void SongOpened(string songPath, MidiInfo info)
    {
        string path = NormalisePath(songPath);
        string hash = FileHash(path);
        long id = FindOrCreateSong(path, hash);
        if (id < 0)
            return;

        Run(true, "UPDATE songs SET path = ?, hash = ?, last_opened = ?, times_opened = times_opened + 1, " +
                  "title = ?, time_sig_top = ?, time_sig_bottom = ?, time_sig_changes = ?, key_sharps = ?, " +
                  "key_minor = ?, tempo_bpm = ?, tempo_changes = ?, duration_ms = ?, bars = ?, info_valid = ? " +
                  "WHERE id = ?",
            path, TextOrNull(hash), DateTime.Now.ToString(IsoDateFormat, CultureInfo.InvariantCulture),
            TextOrNull(info.Title), info.TimeSigTop, info.TimeSigBottom, info.TimeSigChanges ? 1 : 0, info.KeySharps,
            info.KeyMinor ? 1 : 0, info.TempoBpm, info.TempoChanges ? 1 : 0, info.DurationMs, info.Bars, info.Valid ? 1 : 0,
            id);
    }

    public void SetFavourite(string songPath, bool favourite)
    {
        string path = NormalisePath(songPath);
        Run(false, "INSERT OR IGNORE INTO songs (path, hash) VALUES (?, ?)", path, TextOrNull(FileHash(path)));
        Run(false, "UPDATE songs SET favourite = ? WHERE path = ?", favourite ? 1 : 0, path);
    }

    public void BeginBatch()
    {
        // QSqlDatabase::transaction() fails if there is one already
        if (_db == null || _transaction != null)
            return;
        try
        {
            _transaction = _db.BeginTransaction();
        }
        catch (SqliteException)
        {
        }
    }

    public void EndBatch()
    {
        if (_transaction == null)
            return;
        try
        {
            _transaction.Commit();
        }
        catch (SqliteException)
        {
        }
        _transaction.Dispose();
        _transaction = null;
    }

    public void StoreInfo(string songPath, MidiInfo info)
    {
        string path = NormalisePath(songPath);
        Run(false, "INSERT OR IGNORE INTO songs (path, hash) VALUES (?, ?)", path, TextOrNull(FileHash(path)));
        Run(false, "UPDATE songs SET title = ?, time_sig_top = ?, time_sig_bottom = ?, time_sig_changes = ?, " +
                   "key_sharps = ?, key_minor = ?, tempo_bpm = ?, tempo_changes = ?, duration_ms = ?, bars = ?, " +
                   "info_valid = ? WHERE path = ?",
            TextOrNull(info.Title), info.TimeSigTop, info.TimeSigBottom, info.TimeSigChanges ? 1 : 0,
            info.KeySharps, info.KeyMinor ? 1 : 0, info.TempoBpm, info.TempoChanges ? 1 : 0, info.DurationMs, info.Bars,
            info.Valid ? 1 : 0, path);
    }

    private List<LibrarySong> Query(string where, object?[]? values = null, string tail = "")
    {
        var result = new List<LibrarySong>();
        if (_db == null)
            return result;
        try
        {
            using SqliteCommand command = Command($"SELECT {SongColumns}, info_valid FROM songs {where} {tail}", values ?? Array.Empty<object?>());
            using SqliteDataReader q = command.ExecuteReader();
            while (q.Read())
            {
                var song = new LibrarySong();
                int c = 0;
                song.Path = Text(q, c++);
                song.Hash = Text(q, c++);
                song.Favourite = Integer(q, c++) != 0;
                song.LastOpened = ParseIsoDate(Text(q, c++));
                song.TimesOpened = (int)Integer(q, c++);
                song.Info.Title = Text(q, c++);
                song.Info.TimeSigTop = (int)Integer(q, c++);
                song.Info.TimeSigBottom = (int)Integer(q, c++);
                song.Info.TimeSigChanges = Integer(q, c++) != 0;
                song.Info.KeySharps = q.IsDBNull(c) ? MidiInfo.NoKey : (int)Integer(q, c);
                c++;
                song.Info.KeyMinor = Integer(q, c++) != 0;
                song.Info.TempoBpm = Real(q, c++);
                song.Info.TempoChanges = Integer(q, c++) != 0;
                song.Info.DurationMs = Integer(q, c++);
                song.Info.Bars = (int)Integer(q, c++);
                song.PartCount = (int)Integer(q, c++);
                song.Info.Valid = Integer(q, c++) != 0;
                result.Add(song);
            }
        }
        catch (SqliteException e)
        {
            Log.Error("Library database error: " + e.Message);
        }
        return result;
    }

    // QDateTime::fromString(text, Qt::ISODate): null (invalid) if it is not a date
    private static DateTime? ParseIsoDate(string text)
    {
        if (text.Length == 0)
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime date)
            ? date
            : null;
    }

    public LibrarySong Song(string path)
    {
        string normalised = NormalisePath(path);
        List<LibrarySong> found = Query("WHERE path = ?", new object?[] { normalised });
        if (found.Count > 0)
            return found[0];
        return new LibrarySong { Path = normalised };
    }

    public List<LibrarySong> Songs(IEnumerable<string> paths)
    {
        // read them all at once: much faster than one query per song for big folders
        var known = new Dictionary<string, LibrarySong>(StringComparer.Ordinal);
        foreach (LibrarySong song in Query(""))
            known[song.Path] = song;

        var result = new List<LibrarySong>();
        foreach (string path in paths)
        {
            string normalised = NormalisePath(path);
            result.Add(known.TryGetValue(normalised, out LibrarySong? song) ? song : new LibrarySong { Path = normalised });
        }
        return result;
    }

    public List<LibrarySong> Favourites() =>
        Query("WHERE favourite = 1", null, "ORDER BY path COLLATE NOCASE");

    public List<LibrarySong> Recent(int maximum) =>
        Query("WHERE last_opened IS NOT NULL", null, string.Create(CultureInfo.InvariantCulture, $"ORDER BY last_opened DESC LIMIT {maximum}"));

    private long SongId(string songPath, bool create)
    {
        if (_db == null)
            return -1;
        string path = NormalisePath(songPath);
        if (create)
            Run(false, "INSERT OR IGNORE INTO songs (path, hash) VALUES (?, ?)", path, TextOrNull(FileHash(path)));
        try
        {
            using SqliteCommand command = Command("SELECT id FROM songs WHERE path = ?", path);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
                return Integer(reader, 0);
        }
        catch (SqliteException)
        {
        }
        return -1;
    }

    // ---- parts ----

    public List<SongPart> Parts(string songPath)
    {
        var result = new List<SongPart>();
        long id = SongId(songPath, false);
        if (id < 0)
            return result;
        try
        {
            using SqliteCommand command = Command("SELECT id, name, start_bar, start_beat, start_ticks, end_bar, end_beat, end_ticks " +
                                                  "FROM parts WHERE song_id = ? ORDER BY id", id);
            using SqliteDataReader q = command.ExecuteReader();
            while (q.Read())
            {
                result.Add(new SongPart
                {
                    Id = Integer(q, 0),
                    Name = Text(q, 1),
                    Start = new BarPosition((int)Integer(q, 2), (int)Integer(q, 3), Integer(q, 4)),
                    End = new BarPosition((int)Integer(q, 5), (int)Integer(q, 6), Integer(q, 7)),
                });
            }
        }
        catch (SqliteException)
        {
        }
        return result;
    }

    public long AddPart(string songPath, string name, BarPosition start, BarPosition end)
    {
        long id = SongId(songPath, true);
        if (id < 0)
            return -1;
        if (!Run(true, "INSERT INTO parts (song_id, name, start_bar, start_beat, start_ticks, end_bar, end_beat, end_ticks) " +
                       "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                id, name, start.Bar, start.Beat, start.Ticks, end.Bar, end.Beat, end.Ticks))
            return -1;
        return LastInsertId();
    }

    public void UpdatePart(SongPart part) =>
        Run(true, "UPDATE parts SET name = ?, start_bar = ?, start_beat = ?, start_ticks = ?, " +
                  "end_bar = ?, end_beat = ?, end_ticks = ? WHERE id = ?",
            part.Name, part.Start.Bar, part.Start.Beat, part.Start.Ticks, part.End.Bar, part.End.Beat, part.End.Ticks, part.Id);

    public void DeletePart(long id) =>
        Run(false, "DELETE FROM parts WHERE id = ?", id);

    // ---- settings of each song ----

    public SongSettings GetSongSettings(string songPath)
    {
        var settings = new SongSettings();
        if (_db == null)
            return settings;
        string path = NormalisePath(songPath);
        string hash = FileHash(path);
        try
        {
            // its own settings first, or else those of a copy of the song
            using SqliteCommand command = Command("SELECT hand, speed, left_channel, right_channel FROM songs " +
                                                  "WHERE hand IS NOT NULL AND (path = ? OR (hash = ? AND hash <> '')) " +
                                                  "ORDER BY path = ? DESC LIMIT 1",
                path, TextOrNull(hash), path);
            using SqliteDataReader q = command.ExecuteReader();
            if (q.Read())
            {
                settings.Valid = true;
                settings.Hand = Text(q, 0);
                settings.Speed = q.IsDBNull(1) ? 100 : (int)Integer(q, 1);
                settings.LeftChannel = q.IsDBNull(2) ? -2 : (int)Integer(q, 2);
                settings.RightChannel = q.IsDBNull(3) ? -2 : (int)Integer(q, 3);
            }
        }
        catch (SqliteException)
        {
        }
        return settings;
    }

    public void SetSongSettings(string songPath, SongSettings settings)
    {
        string path = NormalisePath(songPath);
        string hash = FileHash(path);
        long id = FindOrCreateSong(path, hash);
        if (id < 0)
            return;
        Run(false, "UPDATE songs SET path = ?, hash = ? WHERE id = ?", path, TextOrNull(hash), id);

        // the song and all its copies
        Run(true, "UPDATE songs SET hand = ?, speed = ?, left_channel = ?, right_channel = ? " +
                  "WHERE id = ? OR (hash = ? AND hash <> '')",
            settings.Hand, settings.Speed,
            settings.LeftChannel == -2 ? null : settings.LeftChannel,
            settings.RightChannel == -2 ? null : settings.RightChannel,
            id, TextOrNull(hash));
    }

    // ---- the last song of each folder ----

    public string LastSong(string folder)
    {
        if (_db == null)
            return "";
        try
        {
            using SqliteCommand command = Command("SELECT last_song FROM folders WHERE path = ?", NormalisePath(folder));
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
                return Text(reader, 0);
        }
        catch (SqliteException)
        {
        }
        return "";
    }

    public void SetLastSong(string folder, string songName) =>
        Run(true, "INSERT OR REPLACE INTO folders (path, last_song) VALUES (?, ?)", NormalisePath(folder), songName);
}
