// Not in the C++ code: the progress of the user profiles, kept in an SQLite database
// (progress.db) next to library.db (D12 of FEATURES.md). A separate file, so library.db, which the
// C++ program shares, keeps its schema and version. GPLv3, like the rest of PianoBoosterCS.
//
// Like Library: one connection open while the store lives, used from one thread (the thread of
// the interface). Errors are logged with Log.Error and the operation does nothing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using PianoBooster.Core;

namespace PianoBooster.Data;

/// <summary>The progress of the user profiles in progress.db (see <see cref="IProgressStore"/>).</summary>
public sealed class ProgressStore : IProgressStore, IDisposable
{
    public const string FileName = "progress.db";
    public const int SchemaVersion = 1;

    // as library.db: QDateTime::toString(Qt::ISODate) of a local time
    private const string IsoDateFormat = "yyyy-MM-ddTHH:mm:ss";

    private const string PracticeColumns =
        "id, profile_id, song_id, mode, hand, speed, started, ended, score, max_score, notes_expected, error_count";

    private readonly SqliteConnection? _db;

    /// <summary>Opens (creating it if needed) the database in the given folder.</summary>
    public ProgressStore(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(folder, FileName),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, // the file is closed when the store is
            };
            _db = new SqliteConnection(builder.ToString());
            _db.Open();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("Cannot open the progress database: " + e.Message);
            _db?.Dispose();
            _db = null;
            return;
        }

        IsOpen = Exec("PRAGMA foreign_keys = ON") &&
                 Exec("CREATE TABLE IF NOT EXISTS profiles (" +
                      "id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL, created TEXT NOT NULL)") &&
                 Exec("CREATE TABLE IF NOT EXISTS songs (" +
                      "id INTEGER PRIMARY KEY, hash TEXT UNIQUE NOT NULL, path TEXT, title TEXT, " +
                      "notes_right INTEGER, notes_left INTEGER, max_right INTEGER, max_left INTEGER)") &&
                 Exec("CREATE TABLE IF NOT EXISTS practices (" +
                      "id INTEGER PRIMARY KEY, " +
                      "profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE, " +
                      "song_id INTEGER NOT NULL REFERENCES songs(id) ON DELETE CASCADE, " +
                      "mode TEXT NOT NULL, hand TEXT NOT NULL, speed INTEGER NOT NULL, " +
                      "started TEXT NOT NULL, ended TEXT NOT NULL, score INTEGER NOT NULL, " +
                      "max_score INTEGER NOT NULL, notes_expected INTEGER NOT NULL, error_count INTEGER NOT NULL)") &&
                 Exec("CREATE INDEX IF NOT EXISTS practices_profile_song ON practices(profile_id, song_id, mode, hand)") &&
                 Exec("CREATE TABLE IF NOT EXISTS practice_errors (" +
                      "id INTEGER PRIMARY KEY, " +
                      "practice_id INTEGER NOT NULL REFERENCES practices(id) ON DELETE CASCADE, " +
                      "bar INTEGER NOT NULL, beat INTEGER NOT NULL, ticks INTEGER NOT NULL, kind TEXT NOT NULL, " +
                      "expected_note INTEGER, played_note INTEGER, hand TEXT)") &&
                 Exec("CREATE INDEX IF NOT EXISTS practice_errors_practice ON practice_errors(practice_id)") &&
                 Exec("PRAGMA user_version = " + SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    public bool IsOpen { get; }

    public void Dispose() => _db?.Dispose();

    // ---- the names kept in the database ----

    /// <summary>"follow" or "playalong"; null for the modes without practices (D10).</summary>
    public static string? ModeName(PlayMode mode) => mode switch
    {
        PlayMode.FollowYou => "follow",
        PlayMode.PlayAlong => "playalong",
        _ => null,
    };

    private static PlayMode? ParseMode(string name) => name switch
    {
        "follow" => PlayMode.FollowYou,
        "playalong" => PlayMode.PlayAlong,
        _ => null,
    };

    /// <summary>"right", "left" or "both" (null for no hand).</summary>
    public static string? HandName(WhichPart hand) => hand switch
    {
        WhichPart.Right => "right",
        WhichPart.Left => "left",
        WhichPart.Both => "both",
        _ => null,
    };

    private static WhichPart ParseHand(string name) => name switch
    {
        "right" => WhichPart.Right,
        "left" => WhichPart.Left,
        "both" => WhichPart.Both,
        _ => WhichPart.None,
    };

    public static string KindName(PracticeErrorKind kind) => kind switch
    {
        PracticeErrorKind.Early => "early",
        PracticeErrorKind.Late => "late",
        PracticeErrorKind.Wrong => "wrong",
        PracticeErrorKind.Missed => "missed",
        _ => throw new ArgumentException($"Unknown kind of error {kind}", nameof(kind)),
    };

    private static PracticeErrorKind? ParseKind(string name) => name switch
    {
        "early" => PracticeErrorKind.Early,
        "late" => PracticeErrorKind.Late,
        "wrong" => PracticeErrorKind.Wrong,
        "missed" => PracticeErrorKind.Missed,
        _ => null,
    };

    private static string FormatDate(DateTime date) => date.ToString(IsoDateFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string text) =>
        DateTime.TryParseExact(text, IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime date)
            ? date
            : DateTime.MinValue;

    // ---- the queries ----

    private SqliteCommand Command(SqliteTransaction? transaction, string sql, params object?[] values)
    {
        // '?' in order become $1, $2... (Microsoft.Data.Sqlite binds by name). No query has a '?' in a literal.
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
        command.Transaction = transaction;
        for (int i = 0; i < values.Length; ++i)
            command.Parameters.AddWithValue("$" + (i + 1).ToString(CultureInfo.InvariantCulture), values[i] ?? DBNull.Value);
        return command;
    }

    private SqliteCommand Command(string sql, params object?[] values) => Command(null, sql, values);

    private static void LogError(SqliteException e) => Log.Error("Progress database error: " + e.Message);

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
            LogError(e);
            return false;
        }
    }

    private long LastInsertId(SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = Command(transaction, "SELECT last_insert_rowid()");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Text(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? "" : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? "";

    private static int? NullableInt(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetInt32(column);

    // ---- profiles ----

    public List<ProgressProfile> Profiles() => QueryProfiles("ORDER BY name COLLATE NOCASE, id");

    public ProgressProfile? Profile(long id)
    {
        List<ProgressProfile> found = QueryProfiles("WHERE id = ?", id);
        return found.Count > 0 ? found[0] : null;
    }

    private List<ProgressProfile> QueryProfiles(string tail, params object?[] values)
    {
        var result = new List<ProgressProfile>();
        if (_db == null)
            return result;
        try
        {
            using SqliteCommand command = Command("SELECT id, name, created FROM profiles " + tail, values);
            using SqliteDataReader q = command.ExecuteReader();
            while (q.Read())
                result.Add(new ProgressProfile(q.GetInt64(0), Text(q, 1), ParseDate(Text(q, 2))));
        }
        catch (SqliteException e)
        {
            LogError(e);
        }
        return result;
    }

    public long CreateProfile(string name)
    {
        name = name.Trim();
        if (_db == null || name.Length == 0)
            return -1;
        try
        {
            using SqliteCommand command = Command("INSERT INTO profiles (name, created) VALUES (?, ?)",
                name, FormatDate(DateTime.Now));
            command.ExecuteNonQuery();
            return LastInsertId();
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT)
        {
            return -1; // the name is used
        }
        catch (SqliteException e)
        {
            LogError(e);
            return -1;
        }
    }

    public bool RenameProfile(long id, string name)
    {
        name = name.Trim();
        if (_db == null || name.Length == 0)
            return false;
        try
        {
            using SqliteCommand command = Command("UPDATE profiles SET name = ? WHERE id = ?", name, id);
            return command.ExecuteNonQuery() == 1;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT)
        {
            return false;
        }
        catch (SqliteException e)
        {
            LogError(e);
            return false;
        }
    }

    public void DeleteProfile(long id)
    {
        if (_db == null)
            return;
        try
        {
            // the practices and their errors go with it (ON DELETE CASCADE)
            using SqliteCommand command = Command("DELETE FROM profiles WHERE id = ?", id);
            command.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            LogError(e);
        }
    }

    public int PracticeCount(long profileId)
    {
        if (_db == null)
            return 0;
        try
        {
            using SqliteCommand command = Command("SELECT COUNT(*) FROM practices WHERE profile_id = ?", profileId);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException e)
        {
            LogError(e);
            return 0;
        }
    }

    // ---- songs ----

    public ProgressSong? Song(string hash)
    {
        if (_db == null || hash.Length == 0)
            return null;
        try
        {
            using SqliteCommand command = Command(
                "SELECT id, hash, path, title, notes_right, notes_left FROM songs WHERE hash = ?", hash);
            using SqliteDataReader q = command.ExecuteReader();
            if (!q.Read())
                return null;
            int? right = NullableInt(q, 4), left = NullableInt(q, 5);
            SongMaxScore? max = right.HasValue && left.HasValue ? new SongMaxScore(right.Value, left.Value) : null;
            return new ProgressSong(q.GetInt64(0), Text(q, 1), Text(q, 2), Text(q, 3), max);
        }
        catch (SqliteException e)
        {
            LogError(e);
            return null;
        }
    }

    public ProgressSong? GetOrCreateSong(string hash, string path, string title)
    {
        if (_db == null || hash.Length == 0)
            return null;
        ProgressSong? song = Song(hash);
        try
        {
            if (song == null)
            {
                using SqliteCommand insert = Command("INSERT INTO songs (hash, path, title) VALUES (?, ?, ?)", hash, path, title);
                insert.ExecuteNonQuery();
            }
            else if (song.Path != path || (title.Length > 0 && song.Title != title))
            {
                // moved or renamed: the same contents
                using SqliteCommand update = Command("UPDATE songs SET path = ?, title = ? WHERE id = ?",
                    path, title.Length > 0 ? title : song.Title, song.Id);
                update.ExecuteNonQuery();
            }
            else
            {
                return song;
            }
        }
        catch (SqliteException e)
        {
            LogError(e);
            return null;
        }
        return Song(hash);
    }

    public void SetSongMaxScore(long songId, SongMaxScore maxScore)
    {
        if (_db == null)
            return;
        try
        {
            using SqliteCommand command = Command(
                "UPDATE songs SET notes_right = ?, notes_left = ?, max_right = ?, max_left = ? WHERE id = ?",
                maxScore.NotesRight, maxScore.NotesLeft, maxScore.MaxRight, maxScore.MaxLeft, songId);
            command.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            LogError(e);
        }
    }

    // ---- practices ----

    public long SavePractice(PracticeRecord practice)
    {
        if (_db == null)
            return -1;
        string? mode = ModeName(practice.Mode);
        string? hand = HandName(practice.Hand);
        if (mode == null || hand == null)
        {
            // a mistake of the caller: only follow you and play along have practices (D10), of a hand
            Log.Error($"A practice in the mode {practice.Mode} with the hand {practice.Hand} is not saved");
            return -1;
        }

        SqliteTransaction? transaction = null;
        try
        {
            transaction = _db.BeginTransaction();
            using (SqliteCommand insert = Command(transaction,
                       "INSERT INTO practices (profile_id, song_id, mode, hand, speed, started, ended, score, max_score, " +
                       "notes_expected, error_count) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                       practice.ProfileId, practice.SongId, mode, hand, practice.Speed, FormatDate(practice.Started),
                       FormatDate(practice.Ended), practice.Score, practice.MaxScore, practice.NotesExpected,
                       practice.Errors.Count))
            {
                insert.ExecuteNonQuery();
            }
            long id = LastInsertId(transaction);

            using (SqliteCommand insertError = Command(transaction,
                       "INSERT INTO practice_errors (practice_id, bar, beat, ticks, kind, expected_note, played_note, hand) " +
                       "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                       id, 0, 0, 0L, "", null, null, null))
            {
                insertError.Prepare();
                foreach (PracticeError error in practice.Errors)
                {
                    insertError.Parameters["$2"].Value = error.Bar;
                    insertError.Parameters["$3"].Value = error.Beat;
                    insertError.Parameters["$4"].Value = error.Tick;
                    insertError.Parameters["$5"].Value = KindName(error.Kind);
                    insertError.Parameters["$6"].Value = (object?)error.ExpectedNote ?? DBNull.Value;
                    insertError.Parameters["$7"].Value = (object?)error.PlayedNote ?? DBNull.Value;
                    insertError.Parameters["$8"].Value = (object?)HandName(error.Hand) ?? DBNull.Value;
                    insertError.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return id;
        }
        catch (SqliteException e)
        {
            LogError(e); // a missing profile or song breaks a foreign key: nothing is saved
            try
            {
                transaction?.Rollback();
            }
            catch (SqliteException)
            {
            }
            return -1;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    public PracticeRecord? Practice(long id)
    {
        List<PracticeRecord> found = QueryPractices("WHERE id = ?", id);
        return found.Count > 0 ? WithErrors(found[0]) : null;
    }

    public List<PracticeRecord> Practices(long profileId, long songId, PlayMode mode, WhichPart hand) =>
        QueryPracticesOf(profileId, songId, mode, hand, "ORDER BY started DESC, id DESC");

    public PracticeRecord? BestPractice(long profileId, long songId, PlayMode mode, WhichPart hand) =>
        FirstWithErrors(QueryPracticesOf(profileId, songId, mode, hand,
            "ORDER BY CASE WHEN max_score > 0 THEN score * 1.0 / max_score ELSE 0 END DESC, score DESC, started, id LIMIT 1"));

    public PracticeRecord? LastPractice(long profileId, long songId, PlayMode mode, WhichPart hand) =>
        FirstWithErrors(QueryPracticesOf(profileId, songId, mode, hand, "ORDER BY started DESC, id DESC LIMIT 1"));

    // The practices of a profile with a song in a mode and with a hand: none for a mode without
    // practices (D10) or no hand, as there can't be any saved
    private List<PracticeRecord> QueryPracticesOf(long profileId, long songId, PlayMode mode, WhichPart hand, string order)
    {
        string? modeName = ModeName(mode);
        string? handName = HandName(hand);
        if (modeName == null || handName == null)
            return new List<PracticeRecord>();
        return QueryPractices("WHERE profile_id = ? AND song_id = ? AND mode = ? AND hand = ? " + order,
            profileId, songId, modeName, handName);
    }

    private PracticeRecord? FirstWithErrors(List<PracticeRecord> practices) =>
        practices.Count > 0 ? WithErrors(practices[0]) : null;

    private List<PracticeRecord> QueryPractices(string tail, params object?[] values)
    {
        var result = new List<PracticeRecord>();
        if (_db == null)
            return result;
        try
        {
            using SqliteCommand command = Command($"SELECT {PracticeColumns} FROM practices {tail}", values);
            using SqliteDataReader q = command.ExecuteReader();
            while (q.Read())
            {
                PlayMode? mode = ParseMode(Text(q, 3));
                if (mode == null)
                    continue; // not written by this version
                result.Add(new PracticeRecord
                {
                    Id = q.GetInt64(0),
                    ProfileId = q.GetInt64(1),
                    SongId = q.GetInt64(2),
                    Mode = mode.Value,
                    Hand = ParseHand(Text(q, 4)),
                    Speed = q.GetInt32(5),
                    Started = ParseDate(Text(q, 6)),
                    Ended = ParseDate(Text(q, 7)),
                    Score = q.GetInt32(8),
                    MaxScore = q.GetInt32(9),
                    NotesExpected = q.GetInt32(10),
                    ErrorCount = q.GetInt32(11),
                });
            }
        }
        catch (SqliteException e)
        {
            LogError(e);
        }
        return result;
    }

    private PracticeRecord WithErrors(PracticeRecord practice)
    {
        var errors = new List<PracticeError>();
        try
        {
            using SqliteCommand command = Command(
                "SELECT bar, beat, ticks, kind, expected_note, played_note, hand FROM practice_errors " +
                "WHERE practice_id = ? ORDER BY id", practice.Id);
            using SqliteDataReader q = command.ExecuteReader();
            while (q.Read())
            {
                PracticeErrorKind? kind = ParseKind(Text(q, 3));
                if (kind == null)
                    continue;
                errors.Add(new PracticeError(q.GetInt32(0), q.GetInt32(1), q.GetInt64(2), kind.Value,
                    NullableInt(q, 4), NullableInt(q, 5), ParseHand(Text(q, 6))));
            }
        }
        catch (SqliteException e)
        {
            LogError(e);
        }
        return new PracticeRecord
        {
            Id = practice.Id,
            ProfileId = practice.ProfileId,
            SongId = practice.SongId,
            Mode = practice.Mode,
            Hand = practice.Hand,
            Speed = practice.Speed,
            Started = practice.Started,
            Ended = practice.Ended,
            Score = practice.Score,
            MaxScore = practice.MaxScore,
            NotesExpected = practice.NotesExpected,
            ErrorCount = practice.ErrorCount,
            Errors = errors,
        };
    }
}
