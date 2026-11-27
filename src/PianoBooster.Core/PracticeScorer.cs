// Not in the C++ code: the score of a practice, from the performance events of the Conductor
// (§4.1 and decisions D1-D7 of FEATURES.md). GPLv3, like the rest of PianoBoosterCS.

using System;
using System.Collections.Generic;
using System.Linq;

namespace PianoBooster.Core;

/// <summary>The kinds of errors of a practice.</summary>
public enum PracticeErrorKind
{
    /// <summary>A note of the chord played too early: in the tolerance window before the play zone.</summary>
    Early,
    /// <summary>A note played too late: follow you, not played when the play zone ended; play
    /// along, a note of a skipped chord played in the tolerance window after the play zone.</summary>
    Late,
    /// <summary>A note that was not asked for (or one played out of the tolerance windows).</summary>
    Wrong,
    /// <summary>A note asked for that was not played: a skipped beat in play along, a note not
    /// needed with a skill below 3, or a chord skipped by "follow through errors".</summary>
    Missed,
}

/// <summary>
/// An error of a practice. Bar and Beat (from 0) and Tick (in ticks of the file) are those of the
/// chord the error belongs to. ExpectedNote is the note that should have been played (null if
/// none is known), PlayedNote the one that was played (null if none: a missed note, or a late one
/// never played). Hand is the hand of the expected note (None if there is no expected note).
/// </summary>
public sealed record PracticeError(int Bar, int Beat, long Tick, PracticeErrorKind Kind,
    int? ExpectedNote, int? PlayedNote, WhichPart Hand);

/// <summary>What a <see cref="PracticeScorer"/> knows of a practice at one moment.</summary>
public sealed class PracticeResult
{
    internal PracticeResult(PlayMode mode, int score, int maxScore, int notesExpected,
        IReadOnlyList<PracticeError> errors, bool started, BarPosition startedFrom, bool loopActive,
        bool? reachedEnd)
    {
        Mode = mode;
        Score = score;
        MaxScore = maxScore;
        NotesExpected = notesExpected;
        Errors = errors;
        Started = started;
        StartedFrom = startedFrom;
        LoopActive = loopActive;
        ReachedEnd = reachedEnd;
    }

    /// <summary>Follow you or play along.</summary>
    public PlayMode Mode { get; }

    /// <summary>The score: never below 0 nor above <see cref="MaxScore"/>.</summary>
    public int Score { get; }

    /// <summary>The maximum score of the notes asked for so far (at the end of the song, the real
    /// maximum of the practice, D11: only the notes of the active hand that fit on the keyboard).</summary>
    public int MaxScore { get; }

    /// <summary>How many notes have been asked for.</summary>
    public int NotesExpected { get; }

    /// <summary>The errors, in the order they were made.</summary>
    public IReadOnlyList<PracticeError> Errors { get; }

    /// <summary>The conductor started to play (told by <see cref="PlayingStarted"/>).</summary>
    public bool Started { get; }

    public BarPosition StartedFrom { get; }

    public bool LoopActive { get; }

    /// <summary>Null while playing; true when the end of the song was reached, false when the
    /// playing was stopped before it.</summary>
    public bool? ReachedEnd { get; }

    public int CountOf(PracticeErrorKind kind) => Errors.Count(e => e.Kind == kind);

    /// <summary>The score as a percentage of the maximum (0 when nothing was asked for).</summary>
    public double Percent => MaxScore > 0 ? Score * 100.0 / MaxScore : 0;
}

/// <summary>
/// Scores a practice from the events of the conductor (set it as
/// <see cref="Conductor.PerformanceObserver"/>). Follow you starts at the maximum and takes the
/// errors away (never below 0); play along starts at 0 and adds the notes played (half for an
/// early or late one). The play zone of the conductor is on time; a tolerance window as wide on
/// each side (<see cref="ScoringRules.ToleranceWindowZones"/>) is early or late; out of them a note
/// is wrong (or missed). The errors are counted note by note, not chord by chord.
/// The events come in the thread of the conductor; <see cref="Result"/> can be read from any thread.
/// A <see cref="PlayingStarted"/> starts a new practice (it clears what there was).
/// </summary>
public sealed class PracticeScorer : IPerformanceObserver
{
    private enum NoteState { Pending, OnTime, Early, Late, Missed }

    private sealed class NoteScore
    {
        public NoteState State;
        public int ErrorIndex = -1; // of the error that tells its state (early, late or missed)
    }

    private sealed class ChordScore
    {
        public ChordScore(ExpectedChord chord)
        {
            Chord = chord;
            foreach (ExpectedNote note in chord.Notes)
                Notes[note.Pitch] = new NoteScore();
        }

        public ExpectedChord Chord { get; }
        public Dictionary<int, NoteScore> Notes { get; } = new();
    }

    private readonly object _lock = new();
    private readonly bool _follow;
    private readonly Dictionary<long, ChordScore> _chords = new();
    private readonly List<PracticeError?> _errors = new(); // null: an error taken back
    private int _maxScore;
    private int _notesExpected;
    private int _penalty; // follow you
    private int _points;  // play along
    private bool _started;
    private BarPosition _startedFrom;
    private bool _loopActive;
    private bool? _reachedEnd;

    /// <summary>A scorer of a practice in this mode. Only follow you and play along are scored
    /// (D10): for another mode it is a mistake of the caller, which is logged, and the scorer does
    /// nothing (<see cref="IsScored"/> is false). It does not throw: it is made in the thread of
    /// the interface, where an exception would close the application.</summary>
    public PracticeScorer(PlayMode mode)
    {
        Mode = mode;
        _follow = mode == PlayMode.FollowYou;
        IsScored = mode == PlayMode.FollowYou || mode == PlayMode.PlayAlong;
        if (!IsScored)
            Log.Error($"A practice in the mode {mode} is not scored");
    }

    public PlayMode Mode { get; }

    /// <summary>False for a mode that is not scored: the events are ignored and the
    /// <see cref="Result"/> stays empty (never started, never reached the end), so it can't be saved.</summary>
    public bool IsScored { get; }

    public int Score
    {
        get
        {
            lock (_lock)
                return CurrentScore();
        }
    }

    public int MaxScore
    {
        get
        {
            lock (_lock)
                return _maxScore;
        }
    }

    /// <summary>A copy of what is known of the practice.</summary>
    public PracticeResult Result
    {
        get
        {
            lock (_lock)
            {
                var errors = _errors.Where(e => e != null).Select(e => e!).ToList();
                return new PracticeResult(Mode, CurrentScore(), _maxScore, _notesExpected, errors,
                    _started, _startedFrom, _loopActive, _reachedEnd);
            }
        }
    }

    public IReadOnlyList<PracticeError> Errors => Result.Errors;

    public void OnPerformanceEvent(PerformanceEvent performanceEvent)
    {
        if (!IsScored)
            return;
        lock (_lock)
        {
            switch (performanceEvent)
            {
                case PlayingStarted started:
                    Clear();
                    _started = true;
                    _startedFrom = started.From;
                    _loopActive = started.LoopActive;
                    break;
                case PlayingStopped stopped:
                    _reachedEnd = stopped.ReachedEnd;
                    break;
                case ChordExpected expected:
                    ChordScoreOf(expected.Chord);
                    break;
                case NotePlayed played:
                    OnNotePlayed(played);
                    break;
                case WrongNote wrong:
                    OnWrongNote(wrong);
                    break;
                case NotesMissed missed:
                    OnNotesMissed(missed);
                    break;
            }
        }
    }

    private void Clear()
    {
        _chords.Clear();
        _errors.Clear();
        _maxScore = 0;
        _notesExpected = 0;
        _penalty = 0;
        _points = 0;
        _started = false;
        _startedFrom = new BarPosition();
        _loopActive = false;
        _reachedEnd = null;
    }

    private int CurrentScore() =>
        _follow ? Math.Max(0, _maxScore - _penalty) : Math.Min(_points, _maxScore);

    // The scores of the notes of a chord; the first time, its notes are added to the maximum
    private ChordScore? ChordScoreOf(ExpectedChord? chord)
    {
        if (chord == null || chord.Id <= 0)
            return null;
        if (!_chords.TryGetValue(chord.Id, out ChordScore? score))
        {
            score = new ChordScore(chord);
            _chords[chord.Id] = score;
            _notesExpected += score.Notes.Count;
            _maxScore += score.Notes.Count * ScoringRules.NoteValue;
        }
        return score;
    }

    private static NoteScore? NoteScoreOf(ChordScore? chord, int pitch) =>
        chord != null && chord.Notes.TryGetValue(pitch, out NoteScore? note) ? note : null;

    private static WhichPart HandOf(ExpectedChord chord, int pitch)
    {
        int index = chord.IndexOf(pitch);
        return index >= 0 ? chord.Notes[index].Hand : WhichPart.None;
    }

    private int AddError(ExpectedChord? chord, PracticeErrorKind kind, int? expected, int? played, WhichPart hand)
    {
        BarPosition position = chord?.Position ?? new BarPosition();
        _errors.Add(new PracticeError(position.Bar, position.Beat, chord?.Tick ?? 0, kind, expected, played, hand));
        return _errors.Count - 1;
    }

    // An error of a note of a chord: the expected note is the note itself
    private int AddNoteError(ExpectedChord chord, int pitch, PracticeErrorKind kind, int? played) =>
        AddError(chord, kind, pitch, played, HandOf(chord, pitch));

    private static bool InEarlyWindow(long deltaTime, PlayZone zone) =>
        zone.IsBefore(deltaTime) && deltaTime > -(1 + ScoringRules.ToleranceWindowZones) * zone.Early;

    private static bool InLateWindow(long deltaTime, PlayZone zone) =>
        zone.IsAfter(deltaTime) && deltaTime <= (1 + ScoringRules.ToleranceWindowZones) * zone.Late;

    private void OnNotePlayed(NotePlayed played)
    {
        NoteScore? note = NoteScoreOf(ChordScoreOf(played.Chord), played.Pitch);
        if (note == null)
            return;

        if (_follow)
        {
            switch (note.State)
            {
                case NoteState.Pending:
                case NoteState.Early: // the early error stays: it was made
                    if (played.AfterTimeOut)
                    {
                        // (the notes not played are told late when the zone ends: should not happen)
                        note.State = NoteState.Late;
                        note.ErrorIndex = AddNoteError(played.Chord, played.Pitch, PracticeErrorKind.Late, played.Pitch);
                        _penalty += ScoringRules.FollowTimingPenalty;
                    }
                    else
                        note.State = NoteState.OnTime;
                    break;
                case NoteState.Late: // told late when the zone ended, now played
                    if (note.ErrorIndex >= 0 && _errors[note.ErrorIndex] is { PlayedNote: null } late)
                        _errors[note.ErrorIndex] = late with { PlayedNote = played.Pitch };
                    break;
            }
            return;
        }

        // play along: the best of the times it is played
        switch (note.State)
        {
            case NoteState.Pending:
                note.State = NoteState.OnTime;
                _points += ScoringRules.PlayAlongOnTimePoints;
                break;
            case NoteState.Early:
                note.State = NoteState.OnTime;
                _points += ScoringRules.PlayAlongOnTimePoints - ScoringRules.PlayAlongTimingPoints;
                if (note.ErrorIndex >= 0)
                    _errors[note.ErrorIndex] = null; // not an error any more
                note.ErrorIndex = -1;
                break;
        }
    }

    private void OnWrongNote(WrongNote wrong)
    {
        // early: a note of the chord waited for, in the tolerance window before the zone (D2b, D7)
        if (wrong.BeforeZone && wrong.InChord && InEarlyWindow(wrong.DeltaTime, wrong.Zone))
        {
            NoteScore? note = NoteScoreOf(ChordScoreOf(wrong.Chord), wrong.Pitch);
            if (note != null)
            {
                if (_follow)
                {
                    // each early press is an error, while the right one is not played
                    int index = AddNoteError(wrong.Chord!, wrong.Pitch, PracticeErrorKind.Early, wrong.Pitch);
                    _penalty += ScoringRules.FollowTimingPenalty;
                    if (note.State == NoteState.Pending)
                    {
                        note.State = NoteState.Early;
                        note.ErrorIndex = index;
                    }
                    return;
                }
                if (note.State == NoteState.Pending)
                {
                    note.State = NoteState.Early;
                    note.ErrorIndex = AddNoteError(wrong.Chord!, wrong.Pitch, PracticeErrorKind.Early, wrong.Pitch);
                    _points += ScoringRules.PlayAlongTimingPoints;
                    return;
                }
                if (note.State == NoteState.Early)
                    return; // already early: it counts once
            }
        }

        // play along, late: a note of a chord already skipped, in the tolerance window after its zone
        if (!_follow && wrong.PreviousChord != null && InLateWindow(wrong.PreviousDeltaTime, wrong.Zone))
        {
            NoteScore? note = NoteScoreOf(ChordScoreOf(wrong.PreviousChord), wrong.Pitch);
            if (note is { State: NoteState.Missed })
            {
                // the missed error becomes a late one
                note.State = NoteState.Late;
                if (note.ErrorIndex >= 0 && _errors[note.ErrorIndex] is { } missed)
                    _errors[note.ErrorIndex] = missed with { Kind = PracticeErrorKind.Late, PlayedNote = wrong.Pitch };
                else
                    note.ErrorIndex = AddNoteError(wrong.PreviousChord, wrong.Pitch, PracticeErrorKind.Late, wrong.Pitch);
                _points += ScoringRules.PlayAlongTimingPoints - ScoringRules.PlayAlongMissedPoints;
                return;
            }
        }

        // a wrong note: the note it should have been is the nearest not played (D6)
        ExpectedChord? place = wrong.NearestChord ?? wrong.Chord ?? wrong.PreviousChord;
        int? expected = wrong.NearestExpected >= 0 ? wrong.NearestExpected : null;
        WhichPart hand = expected != null && wrong.NearestChord != null
            ? HandOf(wrong.NearestChord, expected.Value) : WhichPart.None;
        AddError(place, PracticeErrorKind.Wrong, expected, wrong.Pitch, hand);
        if (_follow)
            _penalty += ScoringRules.FollowWrongPenalty;
        else
            _points += ScoringRules.PlayAlongWrongPoints;
    }

    private void OnNotesMissed(NotesMissed missed)
    {
        ChordScore? chord = ChordScoreOf(missed.Chord);
        foreach (ExpectedNote expected in missed.Notes)
        {
            NoteScore? note = NoteScoreOf(chord, expected.Pitch);
            if (note == null)
                continue;

            if (missed.Reason == MissReason.Late)
            {
                // follow you: one late error for each note not played yet, once a chord (D3)
                if (note.State is NoteState.Pending or NoteState.Early)
                {
                    note.State = NoteState.Late;
                    note.ErrorIndex = AddNoteError(missed.Chord, expected.Pitch, PracticeErrorKind.Late, null);
                    _penalty += _follow ? ScoringRules.FollowTimingPenalty : 0;
                }
                continue;
            }

            // skipped or not needed: missed (D4, D5); in play along an early note keeps its points
            bool counts = note.State == NoteState.Pending || (_follow && note.State == NoteState.Early);
            if (!counts)
                continue;
            note.State = NoteState.Missed;
            note.ErrorIndex = AddNoteError(missed.Chord, expected.Pitch, PracticeErrorKind.Missed, null);
            if (_follow)
                _penalty += ScoringRules.FollowMissedPenalty;
            else
                _points += ScoringRules.PlayAlongMissedPoints;
        }
    }
}
