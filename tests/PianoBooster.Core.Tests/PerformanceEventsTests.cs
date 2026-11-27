using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace PianoBooster.Core.Tests;

/// <summary>Keeps the performance events told by the conductor.</summary>
internal sealed class EventRecorder : IPerformanceObserver
{
    public readonly List<PerformanceEvent> Events = new();

    public void OnPerformanceEvent(PerformanceEvent performanceEvent) => Events.Add(performanceEvent);

    public IEnumerable<T> Of<T>() where T : PerformanceEvent => Events.OfType<T>();

    public ExpectedChord? LastChord => Of<ChordExpected>().LastOrDefault()?.Chord;
}

/// <summary>Plays a song with notes of a simulated pianist, set up like RefDriver does.</summary>
internal sealed class PerformanceDriver
{
    public const int StepMs = 4;
    public const int HoldSteps = 10;

    public readonly Song Song;
    public readonly Score Score;
    public readonly EventRecorder Recorder = new();
    private readonly FakeMidiDevice _device = new();
    private readonly List<int> _held = new();
    private int _step;
    private int _releaseAt = -1;

    public bool Ended { get; private set; }

    public PerformanceDriver(string midiName, PlayMode mode, WhichPart hand = WhichPart.Both, int skill = 3,
        bool followThroughErrors = false, bool observe = true)
    {
        CoreStatics.Reset();
        Cfg.SetDefaults();
        Cfg.LogLevel = 0;
        var settings = new MemorySettings { FollowThroughErrors = followThroughErrors };

        Song = new Song { MidiDevice = _device };
        Score = new Score(settings);
        Song.Init2(Score, settings);
        Song.TrackList.Init(Song, settings);
        Cfg.SetStaveEndX(400);
        Song.SetActiveHand(WhichPart.Both);
        Score.Init();
        Song.RegenerateChordQueue();
        StavePos.StaveCenterY = 296.0f;
        Cfg.SetAppDimensions(0, 43, 1024, 652);
        Cfg.SetStaveEndX(1004.0f);
        StavePos.StaveCentralOffset = 84.0f;

        Song.LoadSong(MidiPath(midiName));
        Note.SetChannelHands(-2, -2);
        Song.TrackList.Refresh();
        Song.TrackList.CurrentRowChanged(Song.TrackList.GetActiveItemIndex());

        Song.SetPlayMode(mode);
        Song.SetActiveHand(hand);
        Song.SetSkill(skill);
        if (observe)
            Song.PerformanceObserver = Recorder;
        Song.SetPlayFromBar(0.0);
        Song.SetPlayUptoBar(0.0);
        Song.PlayFromStartBar();
    }

    public static string MidiPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "midi", name).Replace('\\', '/');

    public PlayZone Zone => Recorder.Of<NotePlayed>().FirstOrDefault()?.Zone
        ?? Recorder.Of<WrongNote>().First().Zone;

    public void Step()
    {
        if (Ended)
            return;
        if (_step == _releaseAt)
            ReleaseAll();
        uint bits = Song.Task(StepMs);
        if (_step % 4 == 3)
            Score.DrawScrollingSymbols(true);
        if ((bits & EventBits.PlayingStopped) != 0)
            Ended = true;
        _step++;
    }

    public void Steps(int count)
    {
        for (int i = 0; i < count; i++)
            Step();
    }

    /// <summary>Steps until the condition is true; false if the song ended first.</summary>
    public bool RunUntil(Func<bool> condition, int maxSteps = 50000)
    {
        for (int i = 0; i < maxSteps && !Ended; i++)
        {
            if (condition())
                return true;
            Step();
        }
        return condition();
    }

    public bool Holding => _held.Count > 0;

    /// <summary>Presses the notes (read at the next step) and releases them some steps later.</summary>
    public void Press(params int[] pitches)
    {
        foreach (int pitch in pitches)
        {
            var e = new MidiEvent();
            e.NoteOnEvent(0, 0, pitch, 64);
            _device.Input.Enqueue(e);
            _held.Add(pitch);
        }
        _releaseAt = _step + HoldSteps;
    }

    public void ReleaseAll()
    {
        foreach (int pitch in _held)
        {
            var e = new MidiEvent();
            e.NoteOffEvent(0, 0, pitch, 64);
            _device.Input.Enqueue(e);
        }
        _held.Clear();
        _releaseAt = -1;
    }

    /// <summary>The keys of the chord waited for.</summary>
    public int[] WantedKeys()
    {
        Chord wanted = Song.WantedChord;
        var keys = new int[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
            keys[i] = wanted.GetNote(i).Pitch + Song.Transposition;
        return keys;
    }

    public bool OnTime => Song.WantedChord.Length > 0 && Song.ChordDeltaTime >= 0;

    /// <summary>Plays the chords on time up to the end of the song. For each chord the action can
    /// do something else instead (and return true), or return false to let it be played.</summary>
    public void PlayToEnd(Func<ExpectedChord, bool>? instead = null)
    {
        long handled = 0;
        while (!Ended)
        {
            ExpectedChord? chord = Recorder.LastChord;
            if (chord != null && chord.Id != handled && Song.WantedChord.Length > 0 && !Holding)
            {
                if (instead != null && instead(chord))
                    handled = chord.Id;
                else if (OnTime)
                {
                    handled = chord.Id;
                    Press(WantedKeys());
                }
            }
            Step();
        }
    }

    /// <summary>Plays on time up to the first chord (after the first one) with that many notes.</summary>
    public ExpectedChord PlayUpToChordWith(int notes)
    {
        ExpectedChord? found = null;
        bool done = RunUntilChord(c => c.Id > 1 && c.Notes.Count == notes, c => found = c);
        Assert.True(done, $"no chord with {notes} notes");
        return found!;
    }

    private bool RunUntilChord(Func<ExpectedChord, bool> match, Action<ExpectedChord> found)
    {
        long handled = 0;
        while (!Ended)
        {
            ExpectedChord? chord = Recorder.LastChord;
            if (chord != null && chord.Id != handled && Song.WantedChord.Length > 0 && !Holding)
            {
                if (match(chord))
                {
                    found(chord);
                    return true;
                }
                if (OnTime)
                {
                    handled = chord.Id;
                    Press(WantedKeys());
                }
            }
            Step();
        }
        return false;
    }
}

public class PerformanceEventsTests
{
    // sy01: piano on channel 0 (right hand track 1, left hand track 2), 4/4, 12/8 and 3/4 bars
    private const string TimeSigSong = "sy01-TimeSigChanges.mid";

    private static void CheckPositions(string midiName, PlayMode mode)
    {
        var driver = new PerformanceDriver(midiName, mode);
        driver.PlayToEnd();
        Assert.True(driver.Ended);

        var map = new SongMap();
        Assert.True(map.Load(PerformanceDriver.MidiPath(midiName)));
        List<ExpectedChord> chords = driver.Recorder.Of<ChordExpected>().Select(e => e.Chord).ToList();
        Assert.NotEmpty(chords);

        // every note of the piano starts a chord or is in one
        var pianoNotes = map.Notes.Where(n => n.Channel != Midi.DrumChannel).ToList();
        var noteTicks = pianoNotes.Select(n => n.Tick).Distinct().OrderBy(t => t).ToList();
        Assert.Equal(noteTicks, chords.Select(c => c.Tick).ToList());

        for (int i = 0; i < chords.Count; i++)
        {
            ExpectedChord chord = chords[i];
            Assert.Equal(i + 1, chord.Id);
            Assert.Equal(map.PositionOfTick(chord.Tick), chord.Position);
            var pitches = pianoNotes.Where(n => n.Tick == chord.Tick).Select(n => n.Note).OrderBy(p => p);
            Assert.Equal(pitches, chord.Notes.Select(n => n.Pitch).OrderBy(p => p));
        }

        // played on time: nothing wrong or missed, every chord accepted
        Assert.Empty(driver.Recorder.Of<WrongNote>());
        Assert.Empty(driver.Recorder.Of<NotesMissed>());
        Assert.Equal(chords.Select(c => c.Id), driver.Recorder.Of<ChordAccepted>().Select(e => e.Chord.Id));
        Assert.All(driver.Recorder.Of<NotePlayed>(), e => Assert.True(e.Zone.Contains(e.DeltaTime)));
        Assert.IsType<PlayingStarted>(driver.Recorder.Events.First());
        Assert.Equal(new PlayingStopped(true), driver.Recorder.Events.Last());
    }

    [Theory]
    [InlineData(PlayMode.FollowYou)]
    [InlineData(PlayMode.PlayAlong)]
    public void ChordsHaveTheBarAndBeatOfTheSong(PlayMode mode) => CheckPositions(TimeSigSong, mode);

    [Fact]
    public void ChordsHaveTheBarAndBeatWithAnotherPpqn() =>
        CheckPositions("sy02-SharpKeyPpqn480.mid", PlayMode.FollowYou);

    [Fact]
    public void PositionsOfKnownChords()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou);
        driver.PlayToEnd();
        var byTick = driver.Recorder.Of<ChordExpected>().ToDictionary(e => e.Chord.Tick, e => e.Chord.Position);
        // 120 ticks a quarter note: bars 0-1 in 4/4 (480 ticks), 2-3 in 12/8 (60 ticks a beat,
        // from tick 960), 3/4 from bar 4 (tick 2400)
        Assert.Equal(new BarPosition(0, 0, 0), byTick[0]);
        Assert.Equal(new BarPosition(0, 2, 0), byTick[240]);
        Assert.Equal(new BarPosition(1, 0, 0), byTick[480]);
        Assert.Contains(byTick, kv => kv.Value.Bar == 2 && kv.Value.Beat >= 4); // a beat of 12/8
        foreach (var (tick, position) in byTick)
        {
            if (tick >= 960 && tick < 2400)
                Assert.Equal(new BarPosition((int)(2 + (tick - 960) / 720), (int)((tick - 960) % 720 / 60),
                    (tick - 960) % 60 * Speed.AdjustFactor), position);
            if (tick >= 2400 && tick < 2400 + 360)
                Assert.Equal(new BarPosition(4, (int)((tick - 2400) / 120), (tick - 2400) % 120 * Speed.AdjustFactor), position);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WrongNotesOfAChordAreCountedOneByOne(int wrongNotes)
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        Assert.True(driver.RunUntil(() => driver.OnTime));

        // a semitone above each of the first notes of the chord, then the chord
        int[] keys = chord.Notes.Select(n => n.Pitch).ToArray();
        int[] wrong = keys.Take(wrongNotes).Select(k => k + 1).ToArray();
        Assert.All(wrong, w => Assert.DoesNotContain(w, keys));
        driver.Press(wrong);
        driver.Steps(PerformanceDriver.HoldSteps + 5);
        driver.Press(keys);
        driver.Steps(PerformanceDriver.HoldSteps + 5);

        List<WrongNote> events = driver.Recorder.Of<WrongNote>().ToList();
        Assert.Equal(wrongNotes, events.Count);
        for (int i = 0; i < wrongNotes; i++)
        {
            Assert.Equal(wrong[i], events[i].Pitch);
            Assert.Same(chord, events[i].Chord);
            Assert.False(events[i].BeforeZone);
            Assert.False(events[i].InChord);
            Assert.Equal(keys[i], events[i].NearestExpected); // the nearest note not played
            Assert.Same(chord, events[i].NearestChord);
        }
        Assert.Contains(driver.Recorder.Of<ChordAccepted>(), e => e.Chord == chord);
        Assert.Equal(3, driver.Recorder.Of<NotePlayed>().Count(e => e.Chord == chord));
        Assert.Empty(driver.Recorder.Of<NotesMissed>());
    }

    [Fact]
    public void ANoteBeforeTheZoneIsToldWithItsChord()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        Assert.True(driver.RunUntil(() => !driver.Holding));
        Assert.True(driver.Song.ChordDeltaTime < -2 * MidiFile.PpqnAdjust(Cfg.PlayZoneEarly) * Speed.AdjustFactor);

        int key = chord.Notes[0].Pitch;
        driver.Press(key);
        driver.Steps(PerformanceDriver.HoldSteps + 5);

        WrongNote early = Assert.Single(driver.Recorder.Of<WrongNote>());
        Assert.Equal(key, early.Pitch);
        Assert.True(early.BeforeZone);
        Assert.True(early.InChord);
        Assert.Same(chord, early.Chord);
        Assert.True(early.Zone.IsBefore(early.DeltaTime));
        // the previous chord was played on time a moment ago
        Assert.NotNull(early.PreviousChord);
        Assert.Equal(chord.Id - 1, early.PreviousChord!.Id);
        Assert.True(early.PreviousDeltaTime > 0);
        Assert.True(early.PreviousDeltaTime - early.DeltaTime == (chord.Tick - early.PreviousChord.Tick) * Speed.AdjustFactor);
    }

    [Fact]
    public void FollowYouTellsTheLateNotesOnceAndWaits()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        driver.Steps(500); // 2 s without playing

        NotesMissed late = Assert.Single(driver.Recorder.Of<NotesMissed>());
        Assert.Equal(MissReason.Late, late.Reason);
        Assert.Same(chord, late.Chord);
        Assert.Equal(chord.Notes.OrderBy(n => n.Pitch), late.Notes.OrderBy(n => n.Pitch));
        Assert.Same(chord, driver.Recorder.LastChord); // still waiting for it

        driver.Press(driver.WantedKeys());
        driver.Steps(PerformanceDriver.HoldSteps + 5);
        List<NotePlayed> played = driver.Recorder.Of<NotePlayed>().Where(e => e.Chord == chord).ToList();
        Assert.Equal(3, played.Count);
        Assert.All(played, e => Assert.True(e.AfterTimeOut));
        Assert.All(played, e => Assert.True(e.PianistTiming > e.Zone.Late));
        Assert.Contains(driver.Recorder.Of<ChordAccepted>(), e => e.Chord == chord);
    }

    [Fact]
    public void PlayAlongTellsTheNotesOfASkippedBeat()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.PlayAlong);
        ExpectedChord? skipped = null;
        driver.PlayToEnd(chord =>
        {
            if (skipped == null && chord.Id > 1 && chord.Notes.Count == 3)
            {
                skipped = chord;
                return true; // not played
            }
            return false;
        });

        Assert.NotNull(skipped);
        NotesMissed missed = Assert.Single(driver.Recorder.Of<NotesMissed>());
        Assert.Equal(MissReason.Skipped, missed.Reason);
        Assert.Same(skipped, missed.Chord);
        Assert.Equal(skipped!.Notes.OrderBy(n => n.Pitch), missed.Notes.OrderBy(n => n.Pitch));
        Assert.DoesNotContain(driver.Recorder.Of<ChordAccepted>(), e => e.Chord == skipped);
        Assert.Empty(driver.Recorder.Of<WrongNote>());
        Assert.Equal(new PlayingStopped(true), driver.Recorder.Events.Last());
    }

    [Fact]
    public void PlayAlongTellsANoteOfASkippedChordPlayedLate()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.PlayAlong);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        Assert.True(driver.RunUntil(() => driver.Recorder.Of<NotesMissed>().Any()));
        driver.Steps(2);

        int key = chord.Notes[0].Pitch;
        Assert.False(driver.Recorder.LastChord!.Contains(key)); // not in the next chord
        driver.Press(key);
        driver.Steps(PerformanceDriver.HoldSteps + 5);

        WrongNote late = Assert.Single(driver.Recorder.Of<WrongNote>());
        Assert.Equal(key, late.Pitch);
        Assert.Same(chord, late.PreviousChord);
        Assert.True(late.Zone.IsAfter(late.PreviousDeltaTime));
        Assert.True(late.PreviousDeltaTime < 2 * late.Zone.Late);
    }

    [Fact]
    public void WithASkillBelow3TheNotesNotPlayedAreTold()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou, skill: 1);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        Assert.True(driver.RunUntil(() => driver.OnTime));
        driver.Press(chord.Notes[1].Pitch);
        driver.Steps(PerformanceDriver.HoldSteps + 5);

        // (the chords before it were played whole, but one note is enough: the others are not needed)
        NotesMissed missed = Assert.Single(driver.Recorder.Of<NotesMissed>(), e => e.Chord == chord);
        Assert.Equal(MissReason.NotNeeded, missed.Reason);
        Assert.Equal(new[] { chord.Notes[0], chord.Notes[2] }, missed.Notes);
        Assert.Contains(driver.Recorder.Of<ChordAccepted>(), e => e.Chord == chord);
        int accepted = driver.Recorder.Events.FindIndex(e => e is ChordAccepted a && a.Chord == chord);
        Assert.Same(missed, driver.Recorder.Events[accepted - 1]);
    }

    [Fact]
    public void FollowThroughErrorsSkipsTheChordOfAWrongNote()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou, followThroughErrors: true);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        Assert.True(driver.RunUntil(() => driver.OnTime));
        int wrong = chord.Notes.Max(n => n.Pitch) + 1;
        driver.Press(wrong);
        driver.Steps(PerformanceDriver.HoldSteps + 5);

        int wrongIndex = driver.Recorder.Events.FindIndex(e => e is WrongNote);
        Assert.True(wrongIndex >= 0);
        var wrongNote = (WrongNote)driver.Recorder.Events[wrongIndex];
        Assert.Equal(wrong, wrongNote.Pitch);
        Assert.Same(chord, wrongNote.Chord);
        var missed = Assert.IsType<NotesMissed>(driver.Recorder.Events[wrongIndex + 1]);
        Assert.Equal(MissReason.SkippedByWrongNote, missed.Reason);
        Assert.Equal(3, missed.Notes.Count);
        var next = Assert.IsType<ChordExpected>(driver.Recorder.Events[wrongIndex + 2]);
        Assert.Equal(chord.Id + 1, next.Chord.Id);
    }

    [Fact]
    public void StoppingBeforeTheEndIsNotTheEnd()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou);
        driver.PlayUpToChordWith(3);
        driver.Song.PlayMusic(false);
        Assert.Equal(new PlayingStopped(false), driver.Recorder.Events.Last());
        Assert.DoesNotContain(new PlayingStopped(true), driver.Recorder.Events);
    }

    [Fact]
    public void PlayingFromABarIsTold()
    {
        var driver = new PerformanceDriver(TimeSigSong, PlayMode.FollowYou);
        driver.Song.SetPlayFromBar(2.0);
        driver.Song.SetPlayUptoBar(4.0);
        driver.Song.PlayFromStartBar();
        PlayingStarted started = driver.Recorder.Of<PlayingStarted>().Last();
        Assert.Equal(2, started.From.Bar);
        Assert.True(started.LoopActive);
        Assert.False(driver.Recorder.Of<PlayingStarted>().First().LoopActive);
    }

    [Fact]
    public void WithoutAnObserverTheChordsAreTheSame()
    {
        // the drivers share the statics of the core: one after the other
        var observed = new List<string>();
        Run(new PerformanceDriver(TimeSigSong, PlayMode.PlayAlong), observed);
        var plain = new List<string>();
        Run(new PerformanceDriver(TimeSigSong, PlayMode.PlayAlong, observe: false), plain);
        Assert.Equal(observed, plain);

        static void Run(PerformanceDriver driver, List<string> trace)
        {
            for (int i = 0; i < 3000 && !driver.Ended; i++)
            {
                if (i % 97 == 0)
                    driver.Press(driver.WantedKeys().Take(1).Select(k => k + (i % 2)).ToArray());
                driver.Step();
                trace.Add($"{driver.Song.WantedChord.Length} {driver.Song.ChordDeltaTime} {driver.Song.Rating.WrongNoteCount} {driver.Song.Rating.LateNoteCount}");
            }
        }
    }
}
