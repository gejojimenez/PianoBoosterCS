// Not in the C++ code: the practices of the whole songs, scored and saved in progress.db
// (phase 1.4 of FEATURES.md: §4.2 "Fragmentos", D8-D11). GPLv3, like the rest of PianoBoosterCS.

using System;
using System.Collections.Generic;
using PianoBooster.Core;
using PianoBooster.Data;

namespace PianoBooster.App;

/// <summary>What a <see cref="PracticeSession"/> needs to know of the conductor while it plays:
/// read in the thread of the conductor, with the lock of the song held.</summary>
public interface IPracticeState
{
    PlayMode Mode { get; }
    WhichPart Hand { get; }
    /// <summary>The speed as a percentage.</summary>
    int Speed { get; }
    int ActiveChannel { get; }
    bool LoopActive { get; }
    BarPosition PlayFrom { get; }
    /// <summary>Where the song is now (after a pause, not where it started).</summary>
    BarPosition CurrentPosition { get; }
}

/// <summary>The state of a <see cref="Song"/>.</summary>
public sealed class SongPracticeState : IPracticeState
{
    private readonly Song _song;

    public SongPracticeState(Song song) => _song = song;

    public PlayMode Mode => Conductor.PlayMode;
    public WhichPart Hand => _song.ActiveHand;
    public int Speed => (int)Math.Round(_song.GetSpeed() * 100.0f);
    public int ActiveChannel => _song.ActiveChannel;
    public bool LoopActive => _song.IsLoopActive;
    public BarPosition PlayFrom => _song.PlayFrom;
    public BarPosition CurrentPosition => _song.CurrentNotePosition;
}

/// <summary>
/// Scores each practice of the song and keeps the whole ones (set it as
/// <see cref="Conductor.PerformanceObserver"/>). A practice is whole when it starts at the start
/// of the song (not resumed after a pause, not from a bar, no section and no loop) in follow you
/// or play along, and reaches the end of the song without any of that changing, nor the mode,
/// the hand, the speed or the instrument; otherwise it is thrown away (D8). The events come in
/// the thread of the conductor; the whole practices are kept in memory and saved by
/// <see cref="SaveFinished"/>, in the thread of the interface. It never throws.
/// </summary>
public sealed class PracticeSession : IPerformanceObserver
{
    private sealed class Practice
    {
        public required PracticeScorer Scorer { get; init; }
        public required long ProfileId { get; init; }
        public required long SongId { get; init; }
        public required PlayMode Mode { get; init; }
        public required WhichPart Hand { get; init; }
        public required int Speed { get; init; }
        public required int ActiveChannel { get; init; }
        public required DateTime Started { get; init; }
        public long LastTick { get; set; }
    }

    private readonly IProgressStore _store;
    private readonly IPracticeState _state;
    private readonly object _lock = new();
    private readonly List<PracticeRecord> _finished = new();
    private Practice? _practice;
    private long _profileId = -1;
    private long _songId = -1;

    public PracticeSession(IProgressStore store, IPracticeState state)
    {
        _store = store;
        _state = state;
    }

    /// <summary>The profile whose practices are saved (-1: none, nothing is saved).</summary>
    public long ProfileId
    {
        get { lock (_lock) return _profileId; }
        set { lock (_lock) _profileId = value; }
    }

    /// <summary>The song of the progress store that is loaded (-1: none, nothing is saved).</summary>
    public long SongId
    {
        get { lock (_lock) return _songId; }
        set { lock (_lock) _songId = value; }
    }

    /// <summary>Whether a practice that may be saved is being played now.</summary>
    public bool IsPractising
    {
        get { lock (_lock) return _practice != null; }
    }

    /// <summary>
    /// A song has been loaded (in the thread of the interface, after <see cref="Song.LoadSong"/>):
    /// finds it in the progress store by its contents, adding it if new, and works out its
    /// nominal maximum score if the store does not have it (D11). maxScore is called only then,
    /// so that the caller takes the lock of the song for it only when needed. The id of the
    /// song, or -1.
    /// </summary>
    public long SongLoaded(string path, string title, Func<SongMaxScore?> maxScore)
    {
        SongId = -1;
        string hash = Library.FileHash(path);
        ProgressSong? song = _store.GetOrCreateSong(hash, path, title);
        if (song == null)
            return -1;
        if (song.MaxScore == null && maxScore() is SongMaxScore max)
            _store.SetSongMaxScore(song.Id, max);
        SongId = song.Id;
        return song.Id;
    }

    public void OnPerformanceEvent(PerformanceEvent performanceEvent)
    {
        lock (_lock)
        {
            if (performanceEvent is PlayingStarted started)
                _practice = Start(started);
            if (_practice == null)
                return;

            if (!StillWhole(performanceEvent))
            {
                Log.Info("The practice is not of the whole song: it will not be saved");
                _practice = null;
                return;
            }
            _practice.Scorer.OnPerformanceEvent(performanceEvent);

            if (performanceEvent is PlayingStopped stopped)
            {
                PracticeResult result = _practice.Scorer.Result;
                if (stopped.ReachedEnd && result.ReachedEnd == true && result.NotesExpected > 0)
                    _finished.Add(PracticeRecord.FromResult(result, _practice.ProfileId, _practice.SongId,
                        _practice.Hand, _practice.Speed, _practice.Started, DateTime.Now));
                _practice = null;
            }
        }
    }

    // A new practice, or null if this one would not be of the whole song
    private Practice? Start(PlayingStarted started)
    {
        PlayMode mode = _state.Mode;
        if (_profileId < 0 || _songId < 0 || (mode != PlayMode.FollowYou && mode != PlayMode.PlayAlong))
            return null;
        if (!started.From.IsSongStart || started.LoopActive || _state.LoopActive
            || !_state.PlayFrom.IsSongStart || !_state.CurrentPosition.IsSongStart)
            return null;
        var scorer = new PracticeScorer(mode);
        if (!scorer.IsScored)
            return null;
        return new Practice
        {
            Scorer = scorer,
            ProfileId = _profileId,
            SongId = _songId,
            Mode = mode,
            Hand = _state.Hand,
            Speed = _state.Speed,
            ActiveChannel = _state.ActiveChannel,
            Started = DateTime.Now,
        };
    }

    // Nothing that makes it a fragment has changed (§4.2 "Fragmentos") and the song has not gone back
    private bool StillWhole(PerformanceEvent performanceEvent)
    {
        Practice practice = _practice!;
        // a change of profile too: the practice is not saved for the new one (phase 1.5)
        if (_profileId != practice.ProfileId || _songId != practice.SongId
            || _state.Mode != practice.Mode || _state.Hand != practice.Hand
            || _state.Speed != practice.Speed || _state.ActiveChannel != practice.ActiveChannel
            || _state.LoopActive || !_state.PlayFrom.IsSongStart)
            return false;
        if (performanceEvent is ChordExpected expected)
        {
            if (expected.Chord.Tick < practice.LastTick)
                return false; // a jump back (a loop)
            practice.LastTick = expected.Chord.Tick;
        }
        return true;
    }

    /// <summary>Saves the whole practices that have ended (in the thread of the interface).
    /// The ones that were saved, in the order they were played, each with the best practice of
    /// the same profile, song, mode and hand before it (phase 1.6).</summary>
    public List<SavedPractice> SaveFinished()
    {
        var saved = new List<SavedPractice>();
        List<PracticeRecord> finished;
        lock (_lock)
        {
            if (_finished.Count == 0)
                return saved;
            finished = new List<PracticeRecord>(_finished);
            _finished.Clear();
        }
        foreach (PracticeRecord practice in finished)
        {
            // before saving it, so that it is not its own best
            PracticeRecord? previousBest = _store.BestPractice(practice.ProfileId, practice.SongId,
                practice.Mode, practice.Hand);
            long id = _store.SavePractice(practice);
            if (id >= 0)
            {
                saved.Add(new SavedPractice(practice.WithId(id), previousBest));
                Log.Info($"Practice {id} saved: {practice.Score}/{practice.MaxScore}, {practice.ErrorCount} errors");
            }
        }
        return saved;
    }
}

/// <summary>A practice just saved (with its id and errors) and the best one of the same profile,
/// song, mode and hand before it (null if it is the first one).</summary>
public sealed record SavedPractice(PracticeRecord Practice, PracticeRecord? PreviousBest)
{
    /// <summary>Whether it is better than all the ones before it (also when it is the first one).</summary>
    public bool IsNewBest => PreviousBest == null || Practice.IsBetterThan(PreviousBest);
}
