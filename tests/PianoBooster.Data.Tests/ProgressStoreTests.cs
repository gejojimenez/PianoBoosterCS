using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using PianoBooster.Core;
using Xunit;

namespace PianoBooster.Data.Tests;

/// <summary>The progress of the user profiles (ProgressStore, progress.db) in a temporary folder.</summary>
public sealed class ProgressStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PianoBooster.Data.Tests", Guid.NewGuid().ToString("N"));

    public ProgressStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private ProgressStore Open() => new(_folder);

    private static readonly DateTime Start = new(2026, 11, 27, 18, 30, 5);

    // Runs a query on the file itself, to see what the store has written
    private object? Scalar(string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_folder, ProgressStore.FileName)};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private List<string> Columns(string table)
    {
        var result = new List<string>();
        using var db = new SqliteConnection($"Data Source={Path.Combine(_folder, ProgressStore.FileName)};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(1));
        return result;
    }

    // A follow-you practice of both hands with a chord of 3 notes, 2 of them wrong, in bar 2 beat 1,
    // an early note and a late one never played
    private static PracticeRecord Practice(long profile, long song, int score = 60, DateTime? started = null,
        PlayMode mode = PlayMode.FollowYou, WhichPart hand = WhichPart.Both, int maxScore = 100) => new()
    {
        ProfileId = profile,
        SongId = song,
        Mode = mode,
        Hand = hand,
        Speed = 80,
        Started = started ?? Start,
        Ended = (started ?? Start).AddMinutes(2),
        Score = score,
        MaxScore = maxScore,
        NotesExpected = maxScore / 10,
        Errors = new[]
        {
            new PracticeError(2, 1, 960, PracticeErrorKind.Wrong, 60, 61, WhichPart.Right),
            new PracticeError(2, 1, 960, PracticeErrorKind.Wrong, 64, 65, WhichPart.Right),
            new PracticeError(3, 0, 1152, PracticeErrorKind.Early, 48, 48, WhichPart.Left),
            new PracticeError(4, 2, 1632, PracticeErrorKind.Late, 67, null, WhichPart.Right),
            new PracticeError(0, 0, 0, PracticeErrorKind.Wrong, null, 70, WhichPart.None),
        },
    };

    [Fact]
    public void CreatesTheSchema()
    {
        using (ProgressStore store = Open())
            Assert.True(store.IsOpen);

        Assert.True(File.Exists(Path.Combine(_folder, "progress.db")));
        Assert.False(File.Exists(Path.Combine(_folder, "library.db")));
        Assert.Equal((long)ProgressStore.SchemaVersion, Scalar("PRAGMA user_version"));
        Assert.Equal(new[] { "id", "name", "created" }, Columns("profiles"));
        Assert.Equal(new[] { "id", "hash", "path", "title", "notes_right", "notes_left", "max_right", "max_left" }, Columns("songs"));
        Assert.Equal(new[] { "id", "profile_id", "song_id", "mode", "hand", "speed", "started", "ended", "score",
            "max_score", "notes_expected", "error_count" }, Columns("practices"));
        Assert.Equal(new[] { "id", "practice_id", "bar", "beat", "ticks", "kind", "expected_note", "played_note", "hand" },
            Columns("practice_errors"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'practices_profile_song'"));
    }

    [Fact]
    public void ProfilesAreCreatedRenamedListedAndDeleted()
    {
        using ProgressStore store = Open();
        Assert.Empty(store.Profiles());

        long ana = store.CreateProfile("Ana");
        long bea = store.CreateProfile(" bea ");
        Assert.True(ana > 0);
        Assert.True(bea > 0);
        Assert.Equal(-1, store.CreateProfile("Ana"));
        Assert.Equal(-1, store.CreateProfile("  "));

        Assert.Equal(new[] { "Ana", "bea" }, store.Profiles().Select(p => p.Name));
        ProgressProfile? profile = store.Profile(ana);
        Assert.NotNull(profile);
        Assert.Equal("Ana", profile!.Name);
        Assert.True((DateTime.Now - profile.Created).Duration() < TimeSpan.FromMinutes(1));

        Assert.False(store.RenameProfile(bea, "Ana"));
        Assert.False(store.RenameProfile(bea, ""));
        Assert.False(store.RenameProfile(999, "Carla"));
        Assert.True(store.RenameProfile(bea, "Alba"));
        Assert.Equal(new[] { "Alba", "Ana" }, store.Profiles().Select(p => p.Name));

        store.DeleteProfile(ana);
        Assert.Null(store.Profile(ana));
        Assert.Equal(new[] { "Alba" }, store.Profiles().Select(p => p.Name));
    }

    [Fact]
    public void SavesAndReadsAPracticeWithSeveralErrorsInTheSameBeat()
    {
        using ProgressStore store = Open();
        long profile = store.CreateProfile("Ana");
        ProgressSong song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!;

        PracticeRecord saved = Practice(profile, song.Id);
        long id = store.SavePractice(saved);
        Assert.True(id > 0);

        PracticeRecord? read = store.Practice(id);
        Assert.NotNull(read);
        Assert.Equal(id, read!.Id);
        Assert.Equal(profile, read.ProfileId);
        Assert.Equal(song.Id, read.SongId);
        Assert.Equal(PlayMode.FollowYou, read.Mode);
        Assert.Equal(WhichPart.Both, read.Hand);
        Assert.Equal(80, read.Speed);
        Assert.Equal(Start, read.Started);
        Assert.Equal(Start.AddMinutes(2), read.Ended);
        Assert.Equal(60, read.Score);
        Assert.Equal(100, read.MaxScore);
        Assert.Equal(10, read.NotesExpected);
        Assert.Equal(60.0, read.Percent);
        Assert.Equal(5, read.ErrorCount);
        Assert.Equal(saved.Errors, read.Errors); // records: by value, in the same order
        Assert.Equal(2, read.Errors.Count(e => e.Bar == 2 && e.Beat == 1));

        Assert.Equal("follow", Scalar("SELECT mode FROM practices"));
        Assert.Equal("both", Scalar("SELECT hand FROM practices"));
        Assert.Equal("2026-11-27T18:30:05", Scalar("SELECT started FROM practices"));
        Assert.Equal(DBNull.Value, Scalar("SELECT played_note FROM practice_errors WHERE kind = 'late'"));
        Assert.Equal(DBNull.Value, Scalar("SELECT hand FROM practice_errors WHERE expected_note IS NULL"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM practice_errors WHERE kind = 'early'"));
    }

    [Fact]
    public void APracticeOfAnUnknownProfileOrSongIsNotSaved()
    {
        using ProgressStore store = Open();
        long profile = store.CreateProfile("Ana");
        ProgressSong song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!;

        Assert.Equal(-1, store.SavePractice(Practice(profile + 10, song.Id)));
        Assert.Equal(-1, store.SavePractice(Practice(profile, song.Id + 10)));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM practices"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM practice_errors"));
    }

    [Fact]
    public void OnlyFollowYouAndPlayAlongHavePractices()
    {
        using ProgressStore store = Open();
        long profile = store.CreateProfile("Ana");
        ProgressSong song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!;

        // a mistake of the caller: not saved, logged, and no exception (it would close the app)
        Assert.Equal(-1, store.SavePractice(Practice(profile, song.Id, mode: PlayMode.Listen)));
        Assert.Equal(-1, store.SavePractice(Practice(profile, song.Id, mode: PlayMode.RhythmTapping)));
        Assert.Equal(-1, store.SavePractice(Practice(profile, song.Id, hand: WhichPart.None)));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM practices"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM practice_errors"));

        // and there are none to find
        Assert.Empty(store.Practices(profile, song.Id, PlayMode.Listen, WhichPart.Both));
        Assert.Empty(store.Practices(profile, song.Id, PlayMode.FollowYou, WhichPart.None));
        Assert.Null(store.BestPractice(profile, song.Id, PlayMode.RhythmTapping, WhichPart.Both));
        Assert.Null(store.LastPractice(profile, song.Id, PlayMode.Listen, WhichPart.Right));

        // the store still works
        Assert.True(store.SavePractice(Practice(profile, song.Id)) > 0);
    }

    [Fact]
    public void PracticesBestAndLastAreByProfileSongModeAndHand()
    {
        using ProgressStore store = Open();
        long ana = store.CreateProfile("Ana");
        long bea = store.CreateProfile("Bea");
        long song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!.Id;
        long other = store.GetOrCreateSong("def456", "C:/music/other.mid", "Other")!.Id;

        long first = store.SavePractice(Practice(ana, song, 50, Start));
        long best = store.SavePractice(Practice(ana, song, 90, Start.AddDays(1)));
        long sameScoreLater = store.SavePractice(Practice(ana, song, 90, Start.AddDays(2)));
        long last = store.SavePractice(Practice(ana, song, 70, Start.AddDays(3)));
        // the same percentage with a smaller real maximum (D11) is not better
        store.SavePractice(Practice(ana, song, 72, Start.AddDays(4).AddHours(-1), maxScore: 80));
        // others that must not be counted
        store.SavePractice(Practice(ana, song, 100, Start.AddDays(5), mode: PlayMode.PlayAlong));
        store.SavePractice(Practice(ana, song, 100, Start.AddDays(5), hand: WhichPart.Right));
        store.SavePractice(Practice(ana, other, 100, Start.AddDays(5)));
        store.SavePractice(Practice(bea, song, 100, Start.AddDays(5)));

        List<PracticeRecord> practices = store.Practices(ana, song, PlayMode.FollowYou, WhichPart.Both);
        Assert.Equal(5, practices.Count);
        Assert.Equal(new[] { 72, 70, 90, 90, 50 }, practices.Select(p => p.Score));
        Assert.All(practices, p => Assert.Empty(p.Errors));
        Assert.All(practices, p => Assert.Equal(5, p.ErrorCount));
        Assert.Equal(first, practices[^1].Id);

        PracticeRecord? bestRead = store.BestPractice(ana, song, PlayMode.FollowYou, WhichPart.Both);
        Assert.Equal(best, bestRead!.Id);
        Assert.NotEqual(sameScoreLater, bestRead.Id);
        Assert.Equal(5, bestRead.Errors.Count);

        PracticeRecord? lastRead = store.LastPractice(ana, song, PlayMode.FollowYou, WhichPart.Both);
        Assert.Equal(72, lastRead!.Score);
        Assert.NotEqual(last, lastRead.Id);
        Assert.Equal(5, lastRead.Errors.Count);

        Assert.Equal(100, store.BestPractice(ana, song, PlayMode.PlayAlong, WhichPart.Both)!.Score);
        Assert.Null(store.BestPractice(ana, song, PlayMode.PlayAlong, WhichPart.Left));
        Assert.Null(store.LastPractice(bea, other, PlayMode.FollowYou, WhichPart.Both));
    }

    [Fact]
    public void SummariesAreBySongModeAndHandOfOneProfile()
    {
        using ProgressStore store = Open();
        long ana = store.CreateProfile("Ana");
        long bea = store.CreateProfile("Bea");
        long song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!.Id;
        long other = store.GetOrCreateSong("def456", "C:/music/other.mid", "Other")!.Id;

        store.SavePractice(Practice(ana, song, 50, Start));
        long best = store.SavePractice(Practice(ana, song, 90, Start.AddDays(1)));
        store.SavePractice(Practice(ana, song, 90, Start.AddDays(2))); // as good, but later
        store.SavePractice(Practice(ana, song, 72, Start.AddDays(3), maxScore: 80)); // 90 % too, fewer points
        long along = store.SavePractice(Practice(ana, song, 40, Start.AddDays(5), mode: PlayMode.PlayAlong));
        long right = store.SavePractice(Practice(ana, other, 60, Start.AddDays(4), hand: WhichPart.Right));
        store.SavePractice(Practice(bea, other, 100, Start.AddDays(6)));

        List<PracticeSummary> summaries = store.PracticeSummaries(ana);
        Assert.Equal(new[] { ("Song", PlayMode.PlayAlong, WhichPart.Both), ("Other", PlayMode.FollowYou, WhichPart.Right),
                ("Song", PlayMode.FollowYou, WhichPart.Both) },
            summaries.Select(s => (s.Song.Title, s.Mode, s.Hand))); // the last practised first
        Assert.Equal(new[] { 1, 1, 4 }, summaries.Select(s => s.Count));
        Assert.Equal(new[] { along, right, best }, summaries.Select(s => s.Best.Id));
        Assert.Equal(Start.AddDays(3).AddMinutes(2), summaries[2].LastEnded);
        Assert.Equal("C:/music/song.mid", summaries[2].Song.Path);
        Assert.All(summaries, s => Assert.Empty(s.Best.Errors));
        Assert.Equal(best, store.BestPractice(ana, song, PlayMode.FollowYou, WhichPart.Both)!.Id); // the same order

        PracticeSummary beas = Assert.Single(store.PracticeSummaries(bea));
        Assert.Equal(("Other", 100), (beas.Song.Title, beas.Best.Score));
        Assert.Empty(store.PracticeSummaries(999));
    }

    [Fact]
    public void DeletingAProfileDeletesItsPractices()
    {
        using ProgressStore store = Open();
        long ana = store.CreateProfile("Ana");
        long bea = store.CreateProfile("Bea");
        long song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!.Id;
        store.SavePractice(Practice(ana, song));
        store.SavePractice(Practice(ana, song, mode: PlayMode.PlayAlong));
        long beaPractice = store.SavePractice(Practice(bea, song));
        Assert.Equal(15L, Scalar("SELECT COUNT(*) FROM practice_errors"));
        Assert.Equal(2, store.PracticeCount(ana));
        Assert.Equal(1, store.PracticeCount(bea));

        store.DeleteProfile(ana);

        Assert.Equal(0, store.PracticeCount(ana));
        Assert.Equal(1, store.PracticeCount(bea));

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM practices"));
        Assert.Equal(5L, Scalar("SELECT COUNT(*) FROM practice_errors"));
        Assert.Empty(store.Practices(ana, song, PlayMode.FollowYou, WhichPart.Both));
        Assert.Equal(beaPractice, store.LastPractice(bea, song, PlayMode.FollowYou, WhichPart.Both)!.Id);
        Assert.NotNull(store.Song("abc123")); // the song stays
    }

    [Fact]
    public void AMovedSongIsTheSameSong()
    {
        using ProgressStore store = Open();
        long profile = store.CreateProfile("Ana");
        ProgressSong song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!;
        Assert.Null(song.MaxScore);
        store.SetSongMaxScore(song.Id, new SongMaxScore(120, 80));
        store.SavePractice(Practice(profile, song.Id));

        ProgressSong? moved = store.GetOrCreateSong("abc123", "D:/other/renamed.mid", "");
        Assert.NotNull(moved);
        Assert.Equal(song.Id, moved!.Id);
        Assert.Equal("D:/other/renamed.mid", moved.Path);
        Assert.Equal("Song", moved.Title); // an empty title does not clear it
        Assert.Equal(new SongMaxScore(120, 80), moved.MaxScore);
        Assert.Single(store.Practices(profile, moved.Id, PlayMode.FollowYou, WhichPart.Both));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM songs"));
        Assert.Equal(1200L, Scalar("SELECT max_right FROM songs"));
        Assert.Equal(800L, Scalar("SELECT max_left FROM songs"));

        // another contents is another song; no hash, no song
        Assert.NotEqual(song.Id, store.GetOrCreateSong("def456", "C:/music/song.mid", "Song")!.Id);
        Assert.Null(store.GetOrCreateSong("", "C:/music/x.mid", "X"));
        Assert.Null(store.Song("nope"));
    }

    [Fact]
    public void KeepsEverythingWhenReopened()
    {
        long profile, song, practice;
        using (ProgressStore store = Open())
        {
            profile = store.CreateProfile("Ana");
            song = store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song")!.Id;
            store.SetSongMaxScore(song, new SongMaxScore(3, 4));
            practice = store.SavePractice(Practice(profile, song));
        }

        using (ProgressStore store = Open())
        {
            Assert.True(store.IsOpen);
            Assert.Equal("Ana", store.Profile(profile)!.Name);
            Assert.Equal(new SongMaxScore(3, 4), store.Song("abc123")!.MaxScore);
            PracticeRecord? read = store.Practice(practice);
            Assert.Equal(Practice(profile, song).Errors, read!.Errors);
            Assert.Equal(-1, store.CreateProfile("Ana"));
        }
        Assert.Equal((long)ProgressStore.SchemaVersion, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void ARecordFromTheResultOfAScorer()
    {
        var scorer = new PracticeScorer(PlayMode.PlayAlong);
        PracticeRecord record = PracticeRecord.FromResult(scorer.Result, 1, 2, WhichPart.Left, 75, Start, Start.AddSeconds(30));

        Assert.Equal(PlayMode.PlayAlong, record.Mode);
        Assert.Equal(WhichPart.Left, record.Hand);
        Assert.Equal(75, record.Speed);
        Assert.Equal(1, record.ProfileId);
        Assert.Equal(2, record.SongId);
        Assert.Equal(Start.AddSeconds(30), record.Ended);
        Assert.Equal(0, record.Score);
        Assert.Equal(0, record.ErrorCount);
        Assert.Equal(0, record.Percent);
    }

    [Fact]
    public void WorksWithoutADatabase()
    {
        // a file where the folder should be: the database can't be opened
        string file = Path.Combine(_folder, "not-a-folder");
        File.WriteAllText(file, "");
        using var store = new ProgressStore(file);

        Assert.False(store.IsOpen);
        Assert.Empty(store.Profiles());
        Assert.Equal(-1, store.CreateProfile("Ana"));
        Assert.False(store.RenameProfile(1, "Bea"));
        store.DeleteProfile(1);
        Assert.Equal(0, store.PracticeCount(1));
        Assert.Null(store.GetOrCreateSong("abc123", "C:/music/song.mid", "Song"));
        store.SetSongMaxScore(1, new SongMaxScore(1, 1));
        Assert.Equal(-1, store.SavePractice(Practice(1, 1)));
        Assert.Null(store.Practice(1));
        Assert.Empty(store.Practices(1, 1, PlayMode.FollowYou, WhichPart.Both));
        Assert.Null(store.BestPractice(1, 1, PlayMode.FollowYou, WhichPart.Both));
        Assert.Empty(store.PracticeSummaries(1));
    }
}
