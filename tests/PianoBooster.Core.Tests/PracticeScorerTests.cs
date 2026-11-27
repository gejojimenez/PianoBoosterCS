using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PianoBooster.Core.Tests;

/// <summary>The rules of the practice scores (§4.1 and D1-D7 of FEATURES.md), with events made by hand.</summary>
public class PracticeScorerTests
{
    private static readonly PlayZone Zone = new(100, 100);
    private const int R = ScoringRules.NoteValue;

    private long _nextId = 1;

    private ExpectedChord Chord(int bar, int beat, params int[] pitches) =>
        new(_nextId++, bar * 480 + beat * 120, new BarPosition(bar, beat, 0),
            pitches.Select(p => new ExpectedNote(p, p >= 60 ? WhichPart.Right : WhichPart.Left)).ToArray());

    private static PracticeScorer Start(PlayMode mode)
    {
        var scorer = new PracticeScorer(mode);
        scorer.OnPerformanceEvent(new PlayingStarted(new BarPosition(), false));
        return scorer;
    }

    private static void Expect(PracticeScorer scorer, ExpectedChord chord) =>
        scorer.OnPerformanceEvent(new ChordExpected(chord));

    private static void Play(PracticeScorer scorer, ExpectedChord chord, int pitch, long delta = 0, bool afterTimeOut = false) =>
        scorer.OnPerformanceEvent(new NotePlayed(pitch, chord.Notes[chord.IndexOf(pitch)], chord, delta,
            afterTimeOut ? 2 * Zone.Late : delta, Zone, afterTimeOut));

    private static void PlayAll(PracticeScorer scorer, ExpectedChord chord)
    {
        foreach (ExpectedNote note in chord.Notes)
            Play(scorer, chord, note.Pitch);
        scorer.OnPerformanceEvent(new ChordAccepted(chord, 0));
    }

    // A wrong note in the zone of the chord: the nearest not played is given by the conductor
    private static void Wrong(PracticeScorer scorer, ExpectedChord? chord, int pitch, int nearest, long delta = 0,
        ExpectedChord? previous = null, long previousDelta = 0)
    {
        bool before = Zone.IsBefore(delta);
        bool inChord = chord != null && chord.Contains(pitch);
        scorer.OnPerformanceEvent(new WrongNote(pitch, chord, delta, Zone, before, inChord, nearest,
            nearest >= 0 ? chord : null, previous, previousDelta));
    }

    private static void Miss(PracticeScorer scorer, ExpectedChord chord, MissReason reason, params int[] pitches) =>
        scorer.OnPerformanceEvent(new NotesMissed(chord,
            chord.Notes.Where(n => pitches.Contains(n.Pitch)).ToArray(), reason));

    [Theory]
    [InlineData(PlayMode.Listen)]
    [InlineData(PlayMode.RhythmTapping)]
    public void OnlyFollowYouAndPlayAlongAreScored(PlayMode mode)
    {
        // a mistake of the caller: no exception (it would close the app), a scorer that does nothing
        var scorer = Start(mode);
        Assert.False(scorer.IsScored);
        Assert.Equal(mode, scorer.Mode);

        ExpectedChord chord = Chord(0, 0, 60, 64);
        Expect(scorer, chord);
        Play(scorer, chord, 60);
        Wrong(scorer, chord, 61, 64);
        Miss(scorer, chord, MissReason.Skipped, 64);
        scorer.OnPerformanceEvent(new PlayingStopped(true));

        PracticeResult result = scorer.Result;
        Assert.False(result.Started);
        Assert.Null(result.ReachedEnd); // never a whole practice, so never saved (D8)
        Assert.Equal(0, result.Score);
        Assert.Equal(0, result.MaxScore);
        Assert.Equal(0, result.NotesExpected);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(PlayMode.FollowYou)]
    [InlineData(PlayMode.PlayAlong)]
    public void FollowYouAndPlayAlongAreScored(PlayMode mode) =>
        Assert.True(new PracticeScorer(mode).IsScored);

    [Theory]
    [InlineData(PlayMode.FollowYou)]
    [InlineData(PlayMode.PlayAlong)]
    public void PlayedOnTimeTheScoreIsTheMaximum(PlayMode mode)
    {
        var scorer = Start(mode);
        foreach (var chord in new[] { Chord(0, 0, 60, 64, 67), Chord(0, 1, 48), Chord(0, 2, 62, 50) })
        {
            Expect(scorer, chord);
            PlayAll(scorer, chord);
        }
        scorer.OnPerformanceEvent(new PlayingStopped(true));

        PracticeResult result = scorer.Result;
        Assert.Equal(6, result.NotesExpected);
        Assert.Equal(6 * R, result.MaxScore);
        Assert.Equal(6 * R, result.Score);
        Assert.Equal(100.0, result.Percent);
        Assert.Empty(result.Errors);
        Assert.True(result.ReachedEnd);
        Assert.True(result.Started);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FollowYouCountsTheWrongNotesOfAChordOneByOne(int wrongNotes)
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(3, 2, 60, 64, 67);
        Expect(scorer, chord);
        int[] keys = { 60, 64, 67 };
        for (int i = 0; i < wrongNotes; i++)
            Wrong(scorer, chord, keys[i] + 1, keys[i]);
        PlayAll(scorer, chord);

        PracticeResult result = scorer.Result;
        Assert.Equal(3 * R - wrongNotes * ScoringRules.FollowWrongPenalty, result.Score);
        Assert.Equal(wrongNotes, result.Errors.Count);
        for (int i = 0; i < wrongNotes; i++)
        {
            Assert.Equal(new PracticeError(3, 2, chord.Tick, PracticeErrorKind.Wrong, keys[i], keys[i] + 1,
                WhichPart.Right), result.Errors[i]);
        }
    }

    [Fact]
    public void FollowYouAllowsManyErrorsButNeverGoesBelowZero()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(0, 0, 60);
        Expect(scorer, chord);
        for (int i = 0; i < 5; i++)
            Wrong(scorer, chord, 61 + i, 60);
        PlayAll(scorer, chord);

        Assert.Equal(0, scorer.Score);
        Assert.Equal(R, scorer.MaxScore);
        Assert.Equal(5, scorer.Result.CountOf(PracticeErrorKind.Wrong));

        // and the errors of the next chords take nothing more away
        ExpectedChord next = Chord(0, 1, 62);
        Expect(scorer, next);
        PlayAll(scorer, next);
        Assert.Equal(0, scorer.Score);
        Assert.Equal(2 * R, scorer.MaxScore);
    }

    [Fact]
    public void FollowYouEarlyNotesInTheToleranceWindowAreTimingErrors()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(1, 0, 60, 64);
        Expect(scorer, chord);
        Wrong(scorer, chord, 60, 60, delta: -150);  // early, in the window
        Wrong(scorer, chord, 64, 64, delta: -250);  // before the window: wrong
        PlayAll(scorer, chord);

        PracticeResult result = scorer.Result;
        Assert.Equal(2 * R - ScoringRules.FollowTimingPenalty - ScoringRules.FollowWrongPenalty, result.Score);
        Assert.Equal(new PracticeError(1, 0, chord.Tick, PracticeErrorKind.Early, 60, 60, WhichPart.Right), result.Errors[0]);
        Assert.Equal(PracticeErrorKind.Wrong, result.Errors[1].Kind);
        Assert.Equal(64, result.Errors[1].PlayedNote);
    }

    [Fact]
    public void FollowYouLateNotesAreToldOnceAndKeepTheNotePlayed()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(2, 1, 60, 64, 67);
        Expect(scorer, chord);
        Play(scorer, chord, 64); // one on time
        Miss(scorer, chord, MissReason.Late, 60, 67);
        Play(scorer, chord, 60, afterTimeOut: true);
        Play(scorer, chord, 67, afterTimeOut: true);
        Play(scorer, chord, 67, afterTimeOut: true); // again
        scorer.OnPerformanceEvent(new ChordAccepted(chord, 0));

        PracticeResult result = scorer.Result;
        Assert.Equal(3 * R - 2 * ScoringRules.FollowTimingPenalty, result.Score);
        Assert.Equal(new[]
        {
            new PracticeError(2, 1, chord.Tick, PracticeErrorKind.Late, 60, 60, WhichPart.Right),
            new PracticeError(2, 1, chord.Tick, PracticeErrorKind.Late, 67, 67, WhichPart.Right),
        }, result.Errors);
    }

    [Fact]
    public void FollowYouWithALowSkillTheNotesNotPlayedAreMissed()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(0, 0, 48, 60, 64);
        Expect(scorer, chord);
        Play(scorer, chord, 60);
        Miss(scorer, chord, MissReason.NotNeeded, 48, 64);

        PracticeResult result = scorer.Result;
        Assert.Equal(3 * R - 2 * ScoringRules.FollowMissedPenalty, result.Score);
        Assert.Equal(new[]
        {
            new PracticeError(0, 0, chord.Tick, PracticeErrorKind.Missed, 48, null, WhichPart.Left),
            new PracticeError(0, 0, chord.Tick, PracticeErrorKind.Missed, 64, null, WhichPart.Right),
        }, result.Errors);
    }

    [Fact]
    public void FollowThroughErrorsCountsTheWrongNoteAndTheSkippedNotes()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(0, 0, 60, 64);
        ExpectedChord next = Chord(0, 1, 67);
        Expect(scorer, chord);
        Wrong(scorer, chord, 70, 64);
        Miss(scorer, chord, MissReason.SkippedByWrongNote, 60, 64);
        Expect(scorer, next);
        PlayAll(scorer, next);

        PracticeResult result = scorer.Result;
        Assert.Equal(1, result.CountOf(PracticeErrorKind.Wrong));
        Assert.Equal(2, result.CountOf(PracticeErrorKind.Missed));
        Assert.Equal(Math.Max(0, 3 * R - ScoringRules.FollowWrongPenalty - 2 * ScoringRules.FollowMissedPenalty), result.Score);
    }

    [Fact]
    public void PlayAlongAddsTheNotesPlayedAndHalfForTheEarlyOnes()
    {
        var scorer = Start(PlayMode.PlayAlong);
        ExpectedChord chord = Chord(0, 0, 60, 64, 67);
        Expect(scorer, chord);
        Wrong(scorer, chord, 60, 60, delta: -150); // early, in the window: half
        Wrong(scorer, chord, 60, 60, delta: -120); // again: it counts once
        Wrong(scorer, chord, 64, 64, delta: -150); // early...
        Play(scorer, chord, 64, delta: 10);         // ...and then on time: the best stays
        Play(scorer, chord, 67);
        Miss(scorer, chord, MissReason.Skipped, 60); // the early one was not played in the zone

        PracticeResult result = scorer.Result;
        Assert.Equal(ScoringRules.PlayAlongTimingPoints + 2 * ScoringRules.PlayAlongOnTimePoints, result.Score);
        Assert.Equal(3 * R, result.MaxScore);
        PracticeError early = Assert.Single(result.Errors);
        Assert.Equal(new PracticeError(0, 0, chord.Tick, PracticeErrorKind.Early, 60, 60, WhichPart.Right), early);
    }

    [Fact]
    public void PlayAlongASkippedBeatAddsNothingAndEachNoteIsAnError()
    {
        var scorer = Start(PlayMode.PlayAlong);
        ExpectedChord first = Chord(0, 0, 60);
        ExpectedChord skipped = Chord(0, 1, 48, 60, 64);
        ExpectedChord partial = Chord(0, 2, 50, 62, 65);
        Expect(scorer, first);
        PlayAll(scorer, first);
        Expect(scorer, skipped);
        Miss(scorer, skipped, MissReason.Skipped, 48, 60, 64);
        Expect(scorer, partial);
        Play(scorer, partial, 50);
        Play(scorer, partial, 65);
        Miss(scorer, partial, MissReason.Skipped, 62);

        PracticeResult result = scorer.Result;
        Assert.Equal(3 * ScoringRules.PlayAlongOnTimePoints, result.Score);
        Assert.Equal(7 * R, result.MaxScore);
        Assert.Equal(4, result.CountOf(PracticeErrorKind.Missed));
        Assert.All(result.Errors, e => Assert.Null(e.PlayedNote));
        Assert.Equal(new int?[] { 48, 60, 64 }, result.Errors.Where(e => e.Beat == 1).Select(e => e.ExpectedNote));
        Assert.Equal(new PracticeError(0, 2, partial.Tick, PracticeErrorKind.Missed, 62, null, WhichPart.Right),
            result.Errors.Last());
    }

    [Fact]
    public void PlayAlongANoteOfASkippedChordPlayedInTheWindowIsLate()
    {
        var scorer = Start(PlayMode.PlayAlong);
        ExpectedChord skipped = Chord(4, 0, 60, 64);
        ExpectedChord next = Chord(4, 1, 67);
        Expect(scorer, skipped);
        Miss(scorer, skipped, MissReason.Skipped, 60, 64);
        Expect(scorer, next);
        Wrong(scorer, next, 60, 67, delta: -300, previous: skipped, previousDelta: 150); // late, in the window
        Wrong(scorer, next, 64, 67, delta: -150, previous: skipped, previousDelta: 300); // after the window
        PlayAll(scorer, next);

        PracticeResult result = scorer.Result;
        Assert.Equal(ScoringRules.PlayAlongTimingPoints + ScoringRules.PlayAlongOnTimePoints, result.Score);
        Assert.Equal(new[]
        {
            new PracticeError(4, 0, skipped.Tick, PracticeErrorKind.Late, 60, 60, WhichPart.Right),
            new PracticeError(4, 0, skipped.Tick, PracticeErrorKind.Missed, 64, null, WhichPart.Right),
            new PracticeError(4, 1, next.Tick, PracticeErrorKind.Wrong, 67, 64, WhichPart.Right),
        }, result.Errors);
    }

    [Fact]
    public void PlayAlongWrongNotesAreKeptButTakeNothingAway()
    {
        var scorer = Start(PlayMode.PlayAlong);
        ExpectedChord chord = Chord(0, 0, 60, 64);
        Expect(scorer, chord);
        Wrong(scorer, chord, 61, 60);
        Wrong(scorer, chord, 65, 64);
        PlayAll(scorer, chord);

        PracticeResult result = scorer.Result;
        Assert.Equal(2 * R, result.Score);
        Assert.Equal(2, result.CountOf(PracticeErrorKind.Wrong));
    }

    [Fact]
    public void AWrongNoteBetweenChordsIsPlacedAtTheNextChord()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord next = Chord(5, 3, 48);
        scorer.OnPerformanceEvent(new WrongNote(50, null, 0, Zone, false, false, 48, next, null, 0));
        scorer.OnPerformanceEvent(new WrongNote(52, null, 0, Zone, false, false, -1, null, null, 0));

        PracticeError first = scorer.Errors[0];
        Assert.Equal(new PracticeError(5, 3, next.Tick, PracticeErrorKind.Wrong, 48, 50, WhichPart.Left), first);
        Assert.Equal(new PracticeError(0, 0, 0, PracticeErrorKind.Wrong, null, 52, WhichPart.None), scorer.Errors[1]);
        Assert.Equal(0, scorer.Score);
        Assert.Equal(0, scorer.MaxScore); // the next chord is not asked for yet
    }

    [Fact]
    public void ManyErrorsCanBeInTheSameBarAndBeat()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(7, 1, 60, 64, 67);
        Expect(scorer, chord);
        Wrong(scorer, chord, 61, 60);
        Wrong(scorer, chord, 61, 60);
        Wrong(scorer, chord, 66, 67);
        PlayAll(scorer, chord);

        Assert.Equal(3, scorer.Errors.Count);
        Assert.All(scorer.Errors, e => Assert.Equal((7, 1), (e.Bar, e.Beat)));
    }

    [Fact]
    public void PlayingStartedClearsTheScoreAndStoppingTellsIfTheEndWasReached()
    {
        var scorer = Start(PlayMode.FollowYou);
        ExpectedChord chord = Chord(0, 0, 60);
        Expect(scorer, chord);
        Wrong(scorer, chord, 61, 60);
        Assert.Null(scorer.Result.ReachedEnd);
        scorer.OnPerformanceEvent(new PlayingStopped(false));
        Assert.False(scorer.Result.ReachedEnd);

        scorer.OnPerformanceEvent(new PlayingStarted(new BarPosition(2, 0, 0), true));
        PracticeResult result = scorer.Result;
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.MaxScore);
        Assert.Null(result.ReachedEnd);
        Assert.Equal(2, result.StartedFrom.Bar);
        Assert.True(result.LoopActive);
    }

    [Fact]
    public void TheMaximumOfBothHandsIsTheSumOfTheHands()
    {
        var chords = new List<Chord>();
        var chord = new Chord();
        chord.AddNote(WhichPart.Right, 60);
        chord.AddNote(WhichPart.Right, 64);
        chord.AddNote(WhichPart.Left, 48);
        chords.Add(chord);
        chord.Clear();
        chord.AddNote(WhichPart.Left, 43);
        chord.AddNote(WhichPart.None, 30); // of no hand: not counted
        chords.Add(chord);
        chord.Clear();
        chord.AddNote(WhichPart.Right, 67);
        chords.Add(chord);

        SongMaxScore max = SongMaxScore.FromChords(chords);
        Assert.Equal(new SongMaxScore(3, 2), max);
        Assert.Equal(3 * R, max.MaxRight);
        Assert.Equal(2 * R, max.MaxLeft);
        Assert.Equal(max.MaxRight + max.MaxLeft, max.MaxBoth);
        Assert.Equal(max.MaxRight, max.MaxFor(WhichPart.Right));
        Assert.Equal(max.MaxLeft, max.MaxFor(WhichPart.Left));
        Assert.Equal(max.MaxBoth, max.MaxFor(WhichPart.Both));
        Assert.Equal(5, max.NotesFor(WhichPart.Both));
    }

    [Fact]
    public void AFileThatCanNotBeReadHasNoMaximum() =>
        Assert.Null(SongMaxScore.FromMidiFile(PerformanceDriver.MidiPath("does-not-exist.mid"), 0));
}

/// <summary>The scorer and the maximum with the conductor, from the start of a song to its end.</summary>
public class PracticeScorerSongTests
{
    private const string Song = "sy01-TimeSigChanges.mid";

    private static (PerformanceDriver, PracticeScorer) Start(PlayMode mode, WhichPart hand = WhichPart.Both)
    {
        var scorer = new PracticeScorer(mode);
        var driver = new PerformanceDriver(Song, mode, hand, alsoTell: scorer);
        return (driver, scorer);
    }

    [Theory]
    [InlineData(PlayMode.FollowYou, WhichPart.Both)]
    [InlineData(PlayMode.PlayAlong, WhichPart.Both)]
    [InlineData(PlayMode.FollowYou, WhichPart.Right)]
    [InlineData(PlayMode.PlayAlong, WhichPart.Left)]
    public void AWholeSongPlayedOnTimeGetsTheMaximumOfTheSong(PlayMode mode, WhichPart hand)
    {
        var (driver, scorer) = Start(mode, hand);
        SongMaxScore? songMax = SongMaxScore.FromMidiFile(PerformanceDriver.MidiPath(Song), driver.Song.ActiveChannel);
        driver.PlayToEnd();

        Assert.NotNull(songMax);
        Assert.True(songMax!.Value.NotesRight > 0 && songMax.Value.NotesLeft > 0);
        PracticeResult result = scorer.Result;
        Assert.True(result.ReachedEnd);
        Assert.Empty(result.Errors);
        Assert.Equal(songMax.Value.MaxFor(hand), result.MaxScore); // all the notes fit on the keyboard
        Assert.Equal(result.MaxScore, result.Score);
        Assert.Equal(driver.Recorder.Of<ChordExpected>().Sum(e => e.Chord.Notes.Count), result.NotesExpected);
    }

    [Fact]
    public void ReadingTheMaximumKeepsTheSongAsItWas()
    {
        var (driver, _) = Start(PlayMode.FollowYou);
        int ppqn = MidiFile.PulsesPerQuarterNote;
        SongMaxScore.FromMidiFile(PerformanceDriver.MidiPath("sy02-SharpKeyPpqn480.mid"), driver.Song.ActiveChannel);
        Assert.Equal(ppqn, MidiFile.PulsesPerQuarterNote);
    }

    [Fact]
    public void FollowYouWrongNotesOfAChordInTheSong()
    {
        var (driver, scorer) = Start(PlayMode.FollowYou);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        Assert.True(driver.RunUntil(() => driver.OnTime));
        int[] keys = chord.Notes.Select(n => n.Pitch).ToArray();
        driver.Press(keys[0] + 1, keys[1] + 1);
        driver.Steps(PerformanceDriver.HoldSteps + 5);
        driver.Press(keys);
        driver.Steps(PerformanceDriver.HoldSteps + 5);
        driver.PlayToEnd();

        PracticeResult result = scorer.Result;
        Assert.True(result.ReachedEnd);
        Assert.Equal(result.MaxScore - 2 * ScoringRules.FollowWrongPenalty, result.Score);
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e =>
        {
            Assert.Equal(PracticeErrorKind.Wrong, e.Kind);
            Assert.Equal((chord.Position.Bar, chord.Position.Beat, chord.Tick), (e.Bar, e.Beat, e.Tick));
        });
        Assert.Equal(new int?[] { keys[0], keys[1] }, result.Errors.Select(e => e.ExpectedNote));
        Assert.Equal(new int?[] { keys[0] + 1, keys[1] + 1 }, result.Errors.Select(e => e.PlayedNote));
    }

    [Fact]
    public void PlayAlongASkippedBeatInTheSong()
    {
        var (driver, scorer) = Start(PlayMode.PlayAlong);
        ExpectedChord? skipped = null;
        driver.PlayToEnd(chord =>
        {
            if (skipped == null && chord.Id > 1 && chord.Notes.Count == 3)
            {
                skipped = chord;
                return true;
            }
            return false;
        });

        PracticeResult result = scorer.Result;
        Assert.True(result.ReachedEnd);
        Assert.Equal(result.MaxScore - 3 * ScoringRules.NoteValue, result.Score);
        Assert.Equal(3, result.CountOf(PracticeErrorKind.Missed));
        Assert.All(result.Errors, e => Assert.Equal((skipped!.Position.Bar, skipped.Position.Beat), (e.Bar, e.Beat)));
    }

    [Fact]
    public void FollowYouLateChordInTheSong()
    {
        var (driver, scorer) = Start(PlayMode.FollowYou);
        ExpectedChord chord = driver.PlayUpToChordWith(3);
        driver.Steps(500); // 2 s without playing
        driver.PlayToEnd();

        PracticeResult result = scorer.Result;
        Assert.Equal(result.MaxScore - 3 * ScoringRules.FollowTimingPenalty, result.Score);
        Assert.Equal(3, result.CountOf(PracticeErrorKind.Late));
        Assert.Equal(chord.Notes.Select(n => (int?)n.Pitch).OrderBy(p => p), result.Errors.Select(e => e.PlayedNote).OrderBy(p => p));
    }
}
