using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using PianoBooster.Core;
using Xunit;
using Xunit.Abstractions;

namespace PianoBooster.Data.Tests;

/// <summary>The song library (Library, the C++ CLibrary) in a temporary folder.</summary>
public sealed class LibraryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "PianoBooster.Data.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibraryTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, true);

    // A "song": any contents will do, the library only hashes them
    private string Song(string relativePath, string contents)
    {
        string path = Path.Combine(_folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static MidiInfo Info(string title) => new()
    {
        Valid = true,
        Title = title,
        TimeSigTop = 3,
        TimeSigBottom = 4,
        TimeSigChanges = true,
        KeySharps = -2,
        KeyMinor = true,
        TempoBpm = 96.5,
        TempoChanges = false,
        DurationMs = 123456,
        Bars = 42,
    };

    [Fact]
    public void NormalisesPathsAsQt()
    {
        string expected = Path.GetFullPath(Path.Combine(_folder, "a", "b.mid")).Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            expected = char.ToUpperInvariant(expected[0]) + expected[1..];
        Assert.Equal(expected, Library.NormalisePath(Path.Combine(_folder, "a", "x", "..", ".", "b.mid")));
        Assert.Equal(expected[..^"/b.mid".Length], Library.NormalisePath(Path.Combine(_folder, "a") + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("C:/Music/song.mid", Library.NormalisePath(@"c:\Music\\song.mid"));
            Assert.Equal("C:/", Library.NormalisePath(@"C:\"));
            Assert.Equal("//server/share/song.mid", Library.NormalisePath(@"\\server\share\song.mid"));
        }
        Assert.Equal(new[] { "*.mid", "*.midi", "*.kar" }, Library.MidiFileFilters());
    }

    [Fact]
    public void HashesTheContents()
    {
        string song = Song("song.mid", "abc");
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Library.FileHash(song)); // SHA-1 of "abc"
        Assert.Equal("", Library.FileHash(Path.Combine(_folder, "missing.mid")));
    }

    [Fact]
    public void KeepsLocationsAndSongs()
    {
        string a = Song("Book/a.mid", "song a");
        string b = Song("Book/b.mid", "song b");
        string c = Song("Book/c.mid", "song c");
        using var library = new Library(_folder);
        Assert.True(library.IsOpen);
        Assert.True(File.Exists(Path.Combine(_folder, "library.db")));

        library.AddLocation(Path.Combine(_folder, "Book"));
        library.AddLocation(Path.Combine(_folder, "Book") + Path.DirectorySeparatorChar); // the same one
        library.AddLocation(Path.Combine(_folder, "another"));
        Assert.Equal(new[] { Library.NormalisePath(Path.Combine(_folder, "another")), Library.NormalisePath(Path.Combine(_folder, "Book")) },
                     library.Locations());
        library.RemoveLocation(Path.Combine(_folder, "another"));
        Assert.Single(library.Locations());

        // an unknown song
        LibrarySong unknown = library.Song(a);
        Assert.Equal(Library.NormalisePath(a), unknown.Path);
        Assert.False(unknown.Info.Valid);
        Assert.Null(unknown.LastOpened);

        DateTime before = DateTime.Now.AddSeconds(-1);
        library.SongOpened(a, Info("Song A"));
        library.SongOpened(a, Info("Song A"));
        library.StoreInfo(b, Info("Song B"));
        library.SetFavourite(c, true);

        LibrarySong songA = library.Song(a);
        Assert.Equal(2, songA.TimesOpened);
        Assert.Equal(Library.FileHash(a), songA.Hash);
        Assert.NotNull(songA.LastOpened);
        Assert.InRange(songA.LastOpened!.Value, before, DateTime.Now.AddSeconds(1));
        Assert.True(songA.Info.Valid);
        Assert.Equal("Song A", songA.Info.Title);
        Assert.Equal(3, songA.Info.TimeSigTop);
        Assert.Equal(4, songA.Info.TimeSigBottom);
        Assert.True(songA.Info.TimeSigChanges);
        Assert.Equal(-2, songA.Info.KeySharps);
        Assert.True(songA.Info.KeyMinor);
        Assert.Equal(96.5, songA.Info.TempoBpm);
        Assert.False(songA.Info.TempoChanges);
        Assert.Equal(123456, songA.Info.DurationMs);
        Assert.Equal(42, songA.Info.Bars);
        Assert.False(songA.Favourite);

        LibrarySong songB = library.Song(b);
        Assert.Equal(0, songB.TimesOpened);
        Assert.Null(songB.LastOpened);
        Assert.Equal("Song B", songB.Info.Title);

        LibrarySong songC = library.Song(c);
        Assert.True(songC.Favourite);
        Assert.False(songC.Info.Valid);
        Assert.Equal(MidiInfo.NoKey, songC.Info.KeySharps); // NULL in the database

        Assert.Equal(new[] { songC.Path }, library.Favourites().Select(s => s.Path));
        Assert.Equal(new[] { songA.Path }, library.Recent(10).Select(s => s.Path));
        library.SetFavourite(c, false);
        Assert.Empty(library.Favourites());

        string missing = Path.Combine(_folder, "Book", "d.mid");
        List<LibrarySong> songs = library.Songs(new[] { c, missing, a });
        Assert.Equal(new[] { songC.Path, Library.NormalisePath(missing), songA.Path }, songs.Select(s => s.Path));
        Assert.Equal(2, songs[2].TimesOpened);
        Assert.Equal(0, songs[1].TimesOpened);
    }

    [Fact]
    public void KeepsParts()
    {
        string a = Song("a.mid", "song a");
        using var library = new Library(_folder);
        Assert.Empty(library.Parts(a));

        long first = library.AddPart(a, "Intro", new BarPosition(1, 0, 0), new BarPosition(4, 3, 120));
        long second = library.AddPart(a, "Chorus", new BarPosition(9, 0, 0), new BarPosition(16, 0, 0));
        Assert.True(first > 0 && second > first);

        List<SongPart> parts = library.Parts(a);
        Assert.Equal(new[] { "Intro", "Chorus" }, parts.Select(p => p.Name));
        Assert.Equal(new BarPosition(4, 3, 120), parts[0].End);
        Assert.Equal(2, library.Song(a).PartCount);

        SongPart part = parts[0];
        part.Name = "Beginning";
        part.Start = new BarPosition(2, 1, 0);
        library.UpdatePart(part);
        Assert.Equal("Beginning", library.Parts(a)[0].Name);
        Assert.Equal(new BarPosition(2, 1, 0), library.Parts(a)[0].Start);

        library.DeletePart(second);
        Assert.Single(library.Parts(a));
    }

    [Fact]
    public void KeepsTheSettingsOfEachSongAndItsCopies()
    {
        string a = Song("One/song.mid", "the same song");
        string copy = Song("Two/song.mid", "the same song");
        string other = Song("Two/other.mid", "another song");
        using var library = new Library(_folder);

        SongSettings defaults = library.GetSongSettings(a);
        Assert.False(defaults.Valid);
        Assert.Equal("both", defaults.Hand);
        Assert.Equal(100, defaults.Speed);
        Assert.Equal(-2, defaults.LeftChannel);

        library.SetSongSettings(a, new SongSettings { Valid = true, Hand = "left", Speed = 80, LeftChannel = 3, RightChannel = -2 });
        SongSettings settings = library.GetSongSettings(a);
        Assert.True(settings.Valid);
        Assert.Equal("left", settings.Hand);
        Assert.Equal(80, settings.Speed);
        Assert.Equal(3, settings.LeftChannel);
        Assert.Equal(-2, settings.RightChannel); // NULL in the database

        // a copy not in the library yet takes the settings of the song
        Assert.Equal("left", library.GetSongSettings(copy).Hand);
        Assert.False(library.GetSongSettings(other).Valid);

        // saving those of the copy changes both
        library.SongOpened(copy, Info("Copy"));
        library.SetSongSettings(copy, new SongSettings { Valid = true, Hand = "right", Speed = 60, LeftChannel = -1, RightChannel = 4 });
        Assert.Equal("right", library.GetSongSettings(a).Hand);
        Assert.Equal(-1, library.GetSongSettings(a).LeftChannel);
        Assert.Equal(4, library.GetSongSettings(a).RightChannel);
        Assert.Equal(60, library.GetSongSettings(copy).Speed);
    }

    [Fact]
    public void AMovedSongKeepsItsData()
    {
        string a = Song("Old/song.mid", "a song to move");
        using var library = new Library(_folder);
        library.SongOpened(a, Info("Moved"));
        library.AddPart(a, "Part", new BarPosition(1, 0, 0), new BarPosition(2, 0, 0));
        library.SetSongSettings(a, new SongSettings { Valid = true, Hand = "right", Speed = 70 });

        string moved = Path.Combine(_folder, "New", "renamed.mid");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(a, moved);

        // the settings are found by the contents before it is opened again
        Assert.Equal("right", library.GetSongSettings(moved).Hand);
        // opening it takes the old row (its old path does not exist any more)
        library.SongOpened(moved, Info("Moved"));
        LibrarySong song = library.Song(moved);
        Assert.Equal(2, song.TimesOpened);
        Assert.Equal(1, song.PartCount);
        Assert.Equal("Part", library.Parts(moved).Single().Name);
        Assert.False(library.Song(a).Info.Valid); // nothing is left at the old path
    }

    [Fact]
    public void KeepsTheLastSongOfEachFolder()
    {
        using var library = new Library(_folder);
        Assert.Equal("", library.LastSong(Path.Combine(_folder, "Book")));
        library.SetLastSong(Path.Combine(_folder, "Book"), "01-first.mid");
        library.SetLastSong(Path.Combine(_folder, "Book") + Path.DirectorySeparatorChar, "02-second.mid");
        Assert.Equal("02-second.mid", library.LastSong(Path.Combine(_folder, "Book")));
    }

    [Fact]
    public void KeepsTheRangeOfEachKeyboard()
    {
        using (var library = new Library(_folder))
        {
            Assert.Null(library.KeyboardRange("Piano USB"));
            library.SetKeyboardRange("Piano USB", 21, 108);
            library.SetKeyboardRange("Mini keys", 48, 72);
            library.SetKeyboardRange("Mini keys", 41, 72); // changed: replaces it
        }
        using (var library = new Library(_folder))
        {
            Assert.Equal((21, 108), library.KeyboardRange("Piano USB"));
            Assert.Equal((41, 72), library.KeyboardRange("Mini keys"));
            Assert.Null(library.KeyboardRange("Other"));
        }
        Assert.Equal(3, UserVersion(Path.Combine(_folder, "library.db"))); // a new table only: the C++ program still reads it
    }

    [Fact]
    public void GroupsChangesInABatchAndKeepsThemAfterClosing()
    {
        string a = Song("a.mid", "song a");
        using (var library = new Library(_folder))
        {
            library.BeginBatch();
            library.BeginBatch(); // already in one: ignored, as in Qt
            for (int i = 0; i < 50; ++i)
                library.AddPart(a, "Part " + i, new BarPosition(i, 0, 0), new BarPosition(i + 1, 0, 0));
            library.SetLastSong(_folder, "a.mid");
            library.EndBatch();
            library.EndBatch();
        }
        using (var library = new Library(_folder))
        {
            Assert.Equal(50, library.Parts(a).Count);
            Assert.Equal("a.mid", library.LastSong(_folder));
        }
    }

    [Fact]
    public void UpgradesADatabaseOfAnOlderVersion()
    {
        // version 2: without the settings of the songs nor the folders
        string db = Path.Combine(_folder, "library.db");
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE locations (path TEXT PRIMARY KEY);" +
                "CREATE TABLE songs (id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, hash TEXT, " +
                "favourite INTEGER NOT NULL DEFAULT 0, last_opened TEXT, times_opened INTEGER NOT NULL DEFAULT 0, " +
                "title TEXT, time_sig_top INTEGER, time_sig_bottom INTEGER, time_sig_changes INTEGER, " +
                "key_sharps INTEGER, key_minor INTEGER, tempo_bpm REAL, tempo_changes INTEGER, " +
                "duration_ms INTEGER, bars INTEGER, info_valid INTEGER NOT NULL DEFAULT 0);" +
                "INSERT INTO songs (path, hash, last_opened, times_opened) VALUES ('C:/x/old.mid', 'abc', '2025-03-04T05:06:07', 3);" +
                "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        using var library = new Library(_folder);
        Assert.True(library.IsOpen);
        LibrarySong old = library.Recent(5).Single();
        Assert.Equal("C:/x/old.mid", old.Path);
        Assert.Equal(new DateTime(2025, 3, 4, 5, 6, 7), old.LastOpened);
        Assert.Equal(3, old.TimesOpened);
        Assert.Equal(0, old.PartCount);
        library.SetLastSong(_folder, "song.mid");
        Assert.Equal("song.mid", library.LastSong(_folder));
        Assert.Equal(3L, UserVersion(db));
    }

    private static long UserVersion(string db)
    {
        using var connection = new SqliteConnection($"Data Source={db};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }

    // The library.db of the C++ program on this computer, if there is one: a copy is opened
    // (never the original), and everything in it must be read as it is.
    [Fact]
    public void ReadsACopyOfTheLibraryOfTheCppProgram()
    {
        string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PianoBooster", "library.db");
        if (!File.Exists(original))
        {
            _output.WriteLine("No library.db of the C++ program on this computer: nothing to check.");
            return;
        }
        File.Copy(original, Path.Combine(_folder, "library.db"));

        long songCount = Count("SELECT COUNT(*) FROM songs");
        long partCount = Count("SELECT COUNT(*) FROM parts");
        using var library = new Library(_folder);
        Assert.True(library.IsOpen);

        List<LibrarySong> recent = library.Recent(int.MaxValue);
        _output.WriteLine($"{songCount} songs, {partCount} parts, {library.Locations().Count} locations, " +
                          $"{library.Favourites().Count} favourites, {recent.Count} opened");
        foreach (LibrarySong song in recent.Take(5))
            _output.WriteLine($"  {song.Path} [{song.Hash}] {song.LastOpened:s} x{song.TimesOpened} '{song.Info.Title}' " +
                              $"{song.Info.TimeSigText()} {song.Info.KeyText()} {song.Info.TempoText()} {song.Info.DurationText()} " +
                              $"{song.Info.Bars} bars, {song.PartCount} parts");
        Assert.All(recent, song => Assert.NotNull(song.LastOpened)); // every date of the C++ can be read
        // the C++ writes them as here (QDateTime::toString(Qt::ISODate) of a local time), so they sort together
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_folder, "library.db")};Pooling=False;Mode=ReadOnly"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT last_opened FROM songs WHERE last_opened IS NOT NULL";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", reader.GetString(0));
        }
        foreach (string location in library.Locations())
            Assert.Equal(location, Library.NormalisePath(location)); // the C++ paths are already normalised as here
        foreach (LibrarySong song in recent.Where(s => File.Exists(s.Path)).Take(20))
        {
            Assert.Equal(song.Path, Library.NormalisePath(song.Path));
            Assert.Equal(song.Hash, Library.FileHash(song.Path));
            library.Parts(song.Path); // no errors
            library.GetSongSettings(song.Path);
        }
        Assert.Equal(songCount, Count("SELECT COUNT(*) FROM songs")); // reading changes nothing
        Assert.Equal(3L, UserVersion(Path.Combine(_folder, "library.db")));
    }

    private long Count(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_folder, "library.db")};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
