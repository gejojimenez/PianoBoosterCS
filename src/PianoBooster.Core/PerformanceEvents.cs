// Not in the C++ code: what the pianist plays, told by the Conductor to an observer (used to score
// the practice). GPLv3, like the rest of PianoBoosterCS.

using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>
/// Receives what happens while the pianist plays (see <see cref="Conductor.PerformanceObserver"/>).
/// It is called in the thread of the conductor, with the lock of the song held: it must be quick,
/// must not block and must not call back into the song.
/// </summary>
public interface IPerformanceObserver
{
    void OnPerformanceEvent(PerformanceEvent performanceEvent);
}

/// <summary>A note that the pianist has to play: the key of the keyboard (the transposition is
/// already added) and the hand of the part it belongs to.</summary>
public readonly record struct ExpectedNote(int Pitch, WhichPart Hand);

/// <summary>
/// The play zone of the conductor, in the units of the delta times of the events (ticks scaled
/// by <see cref="Speed.AdjustFactor"/>, at the speed of the song). A note is accepted when its
/// delta time is in the zone: <c>-Early &lt; delta &lt;= Late</c>.
/// </summary>
public readonly record struct PlayZone(long Early, long Late)
{
    public bool IsBefore(long deltaTime) => deltaTime <= -Early;
    public bool IsAfter(long deltaTime) => deltaTime > Late;
    public bool Contains(long deltaTime) => !IsBefore(deltaTime) && !IsAfter(deltaTime);
}

/// <summary>A chord that the pianist is asked to play, with where it is in the song.</summary>
public sealed class ExpectedChord
{
    public ExpectedChord(long id, long tick, BarPosition position, IReadOnlyList<ExpectedNote> notes)
    {
        Id = id;
        Tick = tick;
        Position = position;
        Notes = notes;
    }

    /// <summary>Counts the chords asked for since the start of the song (from 1); -1 for the
    /// next chord when it is only given as the place of a wrong note.</summary>
    public long Id { get; }

    /// <summary>When the chord starts, in ticks of the file from the start of the song.</summary>
    public long Tick { get; }

    /// <summary>The bar, the beat in the bar (both from 0) and the ticks in the beat
    /// (scaled by <see cref="Speed.AdjustFactor"/>).</summary>
    public BarPosition Position { get; }

    /// <summary>Only the notes asked for: those of the active hand that fit on the keyboard.</summary>
    public IReadOnlyList<ExpectedNote> Notes { get; }

    public bool Contains(int pitch) => IndexOf(pitch) >= 0;

    public int IndexOf(int pitch)
    {
        for (int i = 0; i < Notes.Count; i++)
        {
            if (Notes[i].Pitch == pitch)
                return i;
        }
        return -1;
    }

    public override string ToString() => $"chord {Id} at {Position}: {string.Join(" ", Notes)}";
}

/// <summary>Why notes of a chord were not played.</summary>
public enum MissReason
{
    /// <summary>Follow you: the pianist was too slow, past the play zone. The chord is still
    /// waited for, so the notes may be played later (and then are late).</summary>
    Late,
    /// <summary>Play along: the chord went past the play zone and the next one is asked for.</summary>
    Skipped,
    /// <summary>Follow you with "follow through errors": a wrong note in the play zone skipped
    /// the chord.</summary>
    SkippedByWrongNote,
    /// <summary>The chord was taken as played without these notes (with a skill below 3 one
    /// note of the chord is enough).</summary>
    NotNeeded,
}

/// <summary>Something that happened while the pianist was playing.</summary>
public abstract record PerformanceEvent;

/// <summary>The conductor started to play the song, from a position, maybe looping.</summary>
public sealed record PlayingStarted(BarPosition From, bool LoopActive) : PerformanceEvent;

/// <summary>The conductor stopped playing: at the end of the song, or before it.</summary>
public sealed record PlayingStopped(bool ReachedEnd) : PerformanceEvent;

/// <summary>A new chord is waited for.</summary>
public sealed record ChordExpected(ExpectedChord Chord) : PerformanceEvent;

/// <summary>
/// A note of the chord waited for, played in the play zone: the conductor takes it as good.
/// DeltaTime is how far the pianist is from the time of the chord (negative before it).
/// In follow you the music waits for the pianist, so the delta time stops growing at the stop
/// point; PianistTiming keeps growing until the notes become late, and AfterTimeOut tells that
/// the chord had already been given as late (<see cref="MissReason.Late"/>).
/// </summary>
public sealed record NotePlayed(int Pitch, ExpectedNote Expected, ExpectedChord Chord, long DeltaTime,
    long PianistTiming, PlayZone Zone, bool AfterTimeOut) : PerformanceEvent;

/// <summary>
/// A note that the conductor does not take as good, while playing. BeforeZone: it was played
/// before the play zone of the chord waited for (InChord tells whether it is one of its notes,
/// played too early). NearestExpected is the note that should have been played instead: the
/// nearest in pitch of the chord waited for that has not been played (or of the next chord when
/// none is waited for), -1 if there is none; NearestChord is its chord. PreviousChord is the chord
/// before the one waited for, PreviousDeltaTime how far the note is from its time, to know a note
/// of a chord already skipped that is played late. Chord and the chords may be null.
/// </summary>
public sealed record WrongNote(int Pitch, ExpectedChord? Chord, long DeltaTime, PlayZone Zone,
    bool BeforeZone, bool InChord, int NearestExpected, ExpectedChord? NearestChord,
    ExpectedChord? PreviousChord, long PreviousDeltaTime) : PerformanceEvent;

/// <summary>Notes of a chord that were not played (see <see cref="MissReason"/>).</summary>
public sealed record NotesMissed(ExpectedChord Chord, IReadOnlyList<ExpectedNote> Notes, MissReason Reason)
    : PerformanceEvent;

/// <summary>The conductor takes the chord as played and goes on to the next one.</summary>
public sealed record ChordAccepted(ExpectedChord Chord, long DeltaTime) : PerformanceEvent;
