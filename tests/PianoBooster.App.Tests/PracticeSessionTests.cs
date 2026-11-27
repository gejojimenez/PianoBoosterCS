using System;
using System.IO;
using System.Linq;
using PianoBooster.Core;
using PianoBooster.Data;
using Xunit;

namespace PianoBooster.App.Tests;

/// <summary>Only the whole practices are saved (phase 1.4 of FEATURES.md, §4.2 "Fragmentos", D8-D10),
/// and the active profile (D13 without its interface).</summary>
public sealed class PracticeSessionTests : IDisposable
{
    private sealed class FakeState : IPracticeState
    {
        public PlayMode Mode { get; set; } = PlayMode.FollowYou;
        public WhichPart Hand { get; set; } = WhichPart.Right;
        public int Speed { get; set; } = 80;
        public int ActiveChannel { get; set; } = 3;
        public bool LoopActive { get; set; }
        public BarPosition PlayFrom { get; set; }
        public BarPosition CurrentPosition { get; set; }
    }

    private static readonly PlayZone Zone = new(100, 100);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pb-session-" + Guid.NewGuid().ToString("N"));
    private readonly ProgressStore _store;
    private readonly FakeState _state = new();
    private readonly PracticeSession _session;
    private readonly long _profileId;
    private readonly long _songId;

    public PracticeSessionTests()
    {
        _store = new ProgressStore(_folder);
        _profileId = _store.CreateProfile("Ana");
        _songId = _store.GetOrCreateSong("abc", "C:/songs/a.mid", "A")!.Id;
        _session = new PracticeSession(_store, _state) { ProfileId = _profileId, SongId = _songId };
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private static ExpectedChord Chord(long id, int bar, params int[] pitches) =>
        new(id, bar * 480, new BarPosition(bar, 0, 0), pitches.Select(p => new ExpectedNote(p, WhichPart.Right)).ToArray());

    private void Tell(PerformanceEvent e) => _session.OnPerformanceEvent(e);

    private void Start(BarPosition from = default, bool loop = false) => Tell(new PlayingStarted(from, loop));

    private void PlayChord(ExpectedChord chord, int wrongPitch = -1)
    {
        Tell(new ChordExpected(chord));
        if (wrongPitch >= 0)
            Tell(new WrongNote(wrongPitch, chord, 0, Zone, false, false, chord.Notes[0].Pitch, chord, null, 0));
        foreach (ExpectedNote note in chord.Notes)
            Tell(new NotePlayed(note.Pitch, note, chord, 0, 0, Zone, false));
        Tell(new ChordAccepted(chord, 0));
    }

    private void PlaySong(bool reachEnd = true)
    {
        PlayChord(Chord(1, 0, 60, 64));
        PlayChord(Chord(2, 1, 62), wrongPitch: 63);
        _state.CurrentPosition = new BarPosition(1, 0, 0);
        PlayChord(Chord(3, 2, 65));
        Tell(new PlayingStopped(reachEnd));
    }

    private int Saved() => _store.Practices(_profileId, _songId, _state.Mode, _state.Hand).Count;

    [Fact]
    public void AWholePracticeIsSaved()
    {
        DateTime before = DateTime.Now.AddSeconds(-1);
        Start();
        Assert.True(_session.IsPractising);
        PlaySong();
        Assert.False(_session.IsPractising);
        Assert.Equal(1, _session.SaveFinished());
        Assert.Equal(0, _session.SaveFinished()); // only once

        PracticeRecord last = _store.LastPractice(_profileId, _songId, PlayMode.FollowYou, WhichPart.Right)!;
        Assert.Equal(80, last.Speed);
        Assert.Equal(4, last.NotesExpected);
        Assert.Equal(40, last.MaxScore);
        Assert.Equal(30, last.Score); // one wrong note in follow you: -10
        PracticeError error = Assert.Single(last.Errors);
        Assert.Equal(PracticeErrorKind.Wrong, error.Kind);
        Assert.Equal((1, 63, 62), (error.Bar, error.PlayedNote!.Value, error.ExpectedNote!.Value));
        Assert.True(last.Started >= before && last.Ended >= last.Started);
    }

    [Fact]
    public void PlayAlongHasItsOwnRecord()
    {
        _state.Mode = PlayMode.PlayAlong;
        Start();
        PlaySong();
        Assert.Equal(1, _session.SaveFinished());
        Assert.Single(_store.Practices(_profileId, _songId, PlayMode.PlayAlong, WhichPart.Right));
        Assert.Empty(_store.Practices(_profileId, _songId, PlayMode.FollowYou, WhichPart.Right));
    }

    [Fact]
    public void StoppingBeforeTheEndSavesNothing()
    {
        Start();
        PlaySong(reachEnd: false);
        Assert.Equal(0, _session.SaveFinished());
        Assert.Equal(0, Saved());
    }

    [Fact]
    public void FragmentsAreNotPractices()
    {
        Start(from: new BarPosition(2, 0, 0)); // from a bar
        PlaySong();
        _state.LoopActive = true; // a section or a loop
        _state.CurrentPosition = default;
        Start(loop: true);
        PlaySong();
        _state.LoopActive = false;
        _state.PlayFrom = new BarPosition(1, 0, 0); // the start of the conductor is not the song start
        _state.CurrentPosition = default;
        Start();
        PlaySong();
        Assert.Equal(0, _session.SaveFinished());
        Assert.Equal(0, Saved());
    }

    [Fact]
    public void ResumingAfterAPauseIsNotAWholePractice()
    {
        Start();
        PlayChord(Chord(1, 0, 60, 64));
        Tell(new PlayingStopped(false)); // paused
        _state.CurrentPosition = new BarPosition(1, 0, 0);
        Start(); // the conductor tells its start bar again, but the song goes on from bar 1
        PlayChord(Chord(2, 1, 62));
        Tell(new PlayingStopped(true));
        Assert.Equal(0, _session.SaveFinished());
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("from")]
    [InlineData("mode")]
    [InlineData("hand")]
    [InlineData("speed")]
    [InlineData("instrument")]
    [InlineData("song")]
    [InlineData("back")]
    public void AChangeDuringThePracticeThrowsItAway(string change)
    {
        Start();
        PlayChord(Chord(1, 0, 60, 64));
        PlayChord(Chord(2, 1, 62));
        switch (change)
        {
            case "loop": _state.LoopActive = true; break;
            case "from": _state.PlayFrom = new BarPosition(1, 0, 0); break;
            case "mode": _state.Mode = PlayMode.PlayAlong; break;
            case "hand": _state.Hand = WhichPart.Both; break;
            case "speed": _state.Speed = 100; break;
            case "instrument": _state.ActiveChannel = 4; break;
            case "song": _session.SongId = _store.GetOrCreateSong("def", "C:/songs/b.mid", "B")!.Id; break;
            case "back": PlayChord(Chord(3, 0, 60, 64)); break; // the loop went back to bar 0
        }
        PlayChord(Chord(4, 2, 65));
        Assert.False(_session.IsPractising);
        Tell(new PlayingStopped(true));
        Assert.Equal(0, _session.SaveFinished());
        Assert.Empty(_store.Practices(_profileId, _songId, PlayMode.FollowYou, WhichPart.Right));
    }

    [Fact]
    public void TheNextPracticeAfterAThrownAwayOneIsSaved()
    {
        Start();
        PlayChord(Chord(1, 0, 60, 64));
        _state.Speed = 90;
        PlayChord(Chord(2, 1, 62));
        Tell(new PlayingStopped(true));
        _state.CurrentPosition = default;
        Start(); // played again from the start: "repeat the song"
        PlaySong();
        Assert.Equal(1, _session.SaveFinished());
        Assert.Equal(90, _store.LastPractice(_profileId, _songId, PlayMode.FollowYou, WhichPart.Right)!.Speed);
    }

    [Fact]
    public void AChangeOfProfileDuringThePracticeThrowsItAway()
    {
        long bea = _store.CreateProfile("Bea");
        Start();
        PlayChord(Chord(1, 0, 60, 64));
        _session.ProfileId = bea; // chosen in the profiles dialog while playing
        PlayChord(Chord(2, 1, 62));
        Assert.False(_session.IsPractising);
        Tell(new PlayingStopped(true));
        Assert.Equal(0, _session.SaveFinished());
        Assert.Equal(0, _store.PracticeCount(_profileId));
        Assert.Equal(0, _store.PracticeCount(bea));

        _state.CurrentPosition = default;
        Start(); // the next one is for the new profile
        PlaySong();
        Assert.Equal(1, _session.SaveFinished());
        Assert.Equal(0, _store.PracticeCount(_profileId));
        Assert.Equal(1, _store.PracticeCount(bea));
    }

    [Fact]
    public void DeletingTheActiveProfileDuringThePracticeSavesNothing()
    {
        var settings = new SettingsStore(Path.Combine(_folder, "s.json"));
        long bea = _store.CreateProfile("Bea");
        ActiveProfile.Set(settings, _profileId);
        Start();
        PlayChord(Chord(1, 0, 60, 64));
        Assert.True(ActiveProfile.Delete(_store, settings, _profileId, _profileId, out long active));
        _session.ProfileId = active;
        Assert.Equal(bea, active);
        PlayChord(Chord(2, 1, 62));
        Tell(new PlayingStopped(true));
        Assert.Equal(0, _session.SaveFinished());
        Assert.Equal(0, _store.PracticeCount(bea));
    }

    [Fact]
    public void APracticeThatEndedIsNotSavedIfItsProfileIsDeletedFirst()
    {
        _store.CreateProfile("Bea");
        Start();
        PlaySong();
        _store.DeleteProfile(_profileId);
        Assert.Equal(0, _session.SaveFinished()); // no exception: the store refuses it
    }

    [Fact]
    public void DeletingAProfileChoosesTheNextActiveOneAndNeverTheLast()
    {
        var settings = new SettingsStore(Path.Combine(_folder, "s.json"));
        long bea = _store.CreateProfile("Bea");
        long carla = _store.CreateProfile("Carla");
        ActiveProfile.Set(settings, carla);

        // another profile than the active one: the active one stays
        Assert.True(ActiveProfile.Delete(_store, settings, bea, carla, out long active));
        Assert.Equal(carla, active);
        Assert.Equal(carla, ActiveProfile.Resolve(_store, settings, "X"));

        // the active one: the first that is left (by name) becomes the active one
        Assert.True(ActiveProfile.Delete(_store, settings, carla, carla, out active));
        Assert.Equal(_profileId, active);
        Assert.Equal(_profileId, ActiveProfile.Resolve(_store, settings, "X"));

        // the last one is not deleted
        Assert.False(ActiveProfile.Delete(_store, settings, _profileId, _profileId, out active));
        Assert.Equal(_profileId, active);
        Assert.NotNull(_store.Profile(_profileId));
        Assert.Single(_store.Profiles());
    }

    [Theory]
    [InlineData(PlayMode.Listen)]
    [InlineData(PlayMode.RhythmTapping)]
    public void ListenAndRhythmHaveNoPractices(PlayMode mode)
    {
        _state.Mode = mode;
        Start();
        Assert.False(_session.IsPractising);
        PlaySong();
        Assert.Equal(0, _session.SaveFinished());
    }

    [Fact]
    public void WithoutAProfileOrASongNothingIsSaved()
    {
        _session.ProfileId = -1;
        Start();
        PlaySong();
        _session.ProfileId = _profileId;
        _session.SongId = -1;
        _state.CurrentPosition = default;
        Start();
        PlaySong();
        Assert.Equal(0, _session.SaveFinished());
    }

    [Fact]
    public void TheMaximumOfASongIsWorkedOutOnlyWhenTheStoreHasNone()
    {
        string file = Path.Combine(_folder, "song.mid");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        int calls = 0;
        long id = _session.SongLoaded(file, "Song", () => { calls++; return new SongMaxScore(3, 2); });
        Assert.True(id > 0);
        Assert.Equal(id, _session.SongId);
        Assert.Equal(1, calls);
        Assert.Equal(50, _store.Song(Library.FileHash(file))!.MaxScore!.Value.MaxBoth);

        // moved: the same song, with its maximum
        string moved = Path.Combine(_folder, "moved.mid");
        File.Move(file, moved);
        Assert.Equal(id, _session.SongLoaded(moved, "Song", () => { calls++; return null; }));
        Assert.Equal(1, calls);

        // changed: another song
        File.WriteAllBytes(moved, new byte[] { 4, 5, 6 });
        Assert.NotEqual(id, _session.SongLoaded(moved, "Song", () => { calls++; return null; }));
        Assert.Equal(2, calls);

        // can't be read: no song, nothing saved
        Assert.Equal(-1, _session.SongLoaded(Path.Combine(_folder, "none.mid"), "None", () => { calls++; return null; }));
        Assert.Equal(-1, _session.SongId);
    }

    [Fact]
    public void TheDefaultProfileIsCreatedOnceAndKept()
    {
        string folder = Path.Combine(_folder, "profile");
        using var store = new ProgressStore(folder);
        var settings = new SettingsStore(Path.Combine(folder, "s.json"));

        long id = ActiveProfile.Resolve(store, settings, "Usuario");
        Assert.True(id > 0);
        Assert.Equal("Usuario", Assert.Single(store.Profiles()).Name);
        Assert.Equal(id.ToString(), settings.GetString(ActiveProfile.SettingsKey));
        Assert.Equal(id, ActiveProfile.Resolve(store, settings, "Otro")); // not created again

        // renamed: still the active one (kept by its id)
        Assert.True(store.RenameProfile(id, "Ana"));
        Assert.Equal(id, ActiveProfile.Resolve(store, settings, "Usuario"));

        // the active one deleted: the first of the others
        long other = store.CreateProfile("Bea");
        store.DeleteProfile(id);
        Assert.Equal(other, ActiveProfile.Resolve(store, settings, "Usuario"));
        Assert.Equal(other.ToString(), settings.GetString(ActiveProfile.SettingsKey));
        Assert.Single(store.Profiles());
    }

    [Fact]
    public void WithoutAStoreThereIsNoProfile()
    {
        string blocker = Path.Combine(_folder, "file");
        File.WriteAllText(blocker, "");
        using var store = new ProgressStore(blocker); // a file, not a folder: it can't be opened
        Assert.False(store.IsOpen);
        var settings = new SettingsStore(null);
        Assert.Equal(-1, ActiveProfile.Resolve(store, settings, "Usuario"));
        Assert.False(settings.Contains(ActiveProfile.SettingsKey));
    }

    [Fact]
    public void TheDefaultNameIsTheAccount()
    {
        string name = ActiveProfile.DefaultName("Usuario");
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
