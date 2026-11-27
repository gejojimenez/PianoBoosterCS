// Not in the C++ code: the types of the progress of each user profile (§4.4 of FEATURES.md) and
// the interface of the store that keeps them. The store itself (SQLite, progress.db) is
// PianoBooster.Data.ProgressStore. GPLv3, like the rest of PianoBoosterCS.

using System;
using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>A user profile: each one has its own practices.</summary>
public sealed record ProgressProfile(long Id, string Name, DateTime Created);

/// <summary>
/// A song as the progress store knows it. It is identified by the SHA-1 of its contents
/// (<see cref="Hash"/>), so a moved or renamed song keeps its practices; <see cref="Path"/> is
/// where it was last seen, to show it. <see cref="MaxScore"/> is its nominal maximum score (D11),
/// null until it has been worked out.
/// </summary>
public sealed record ProgressSong(long Id, string Hash, string Path, string Title, SongMaxScore? MaxScore);

/// <summary>
/// A whole practice of a song, as it is kept: who, which song, how (mode, hand, speed), when, the
/// score and its real maximum (D11), and its errors. Only follow you and play along have practices
/// (D10). <see cref="ErrorCount"/> is the number of errors even when <see cref="Errors"/> has not
/// been read (the lists of practices leave them out).
/// </summary>
public sealed class PracticeRecord
{
    public long Id { get; init; } = -1;
    public long ProfileId { get; init; }
    public long SongId { get; init; }
    public PlayMode Mode { get; init; } = PlayMode.FollowYou;
    /// <summary>Right, Left or Both.</summary>
    public WhichPart Hand { get; init; } = WhichPart.Both;
    /// <summary>The speed, as a percentage (D9).</summary>
    public int Speed { get; init; } = 100;
    /// <summary>Local time; kept to the second.</summary>
    public DateTime Started { get; init; }
    public DateTime Ended { get; init; }
    public int Score { get; init; }
    /// <summary>The real maximum of this practice (D11): the notes that were asked for.</summary>
    public int MaxScore { get; init; }
    public int NotesExpected { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<PracticeError> Errors { get; init; } = Array.Empty<PracticeError>();

    /// <summary>The score as a percentage of the real maximum (0 when nothing was asked for).</summary>
    public double Percent => MaxScore > 0 ? Score * 100.0 / MaxScore : 0;

    /// <summary>Whether this practice is better than another in the order of
    /// <see cref="IProgressStore.BestPractice"/>: a higher percentage of its real maximum, then a
    /// higher score, then the earliest (then the lower id).</summary>
    public bool IsBetterThan(PracticeRecord other)
    {
        double mine = MaxScore > 0 ? Score * 1.0 / MaxScore : 0;
        double theirs = other.MaxScore > 0 ? other.Score * 1.0 / other.MaxScore : 0;
        if (mine != theirs)
            return mine > theirs;
        if (Score != other.Score)
            return Score > other.Score;
        if (Started != other.Started)
            return Started < other.Started;
        return Id < other.Id;
    }

    /// <summary>The same practice with another id (the one it was saved with).</summary>
    public PracticeRecord WithId(long id) => new()
    {
        Id = id,
        ProfileId = ProfileId,
        SongId = SongId,
        Mode = Mode,
        Hand = Hand,
        Speed = Speed,
        Started = Started,
        Ended = Ended,
        Score = Score,
        MaxScore = MaxScore,
        NotesExpected = NotesExpected,
        ErrorCount = ErrorCount,
        Errors = Errors,
    };

    /// <summary>A record of the result of a <see cref="PracticeScorer"/>, to be saved.</summary>
    public static PracticeRecord FromResult(PracticeResult result, long profileId, long songId,
        WhichPart hand, int speed, DateTime started, DateTime ended) => new()
    {
        ProfileId = profileId,
        SongId = songId,
        Mode = result.Mode,
        Hand = hand,
        Speed = speed,
        Started = started,
        Ended = ended,
        Score = result.Score,
        MaxScore = result.MaxScore,
        NotesExpected = result.NotesExpected,
        ErrorCount = result.Errors.Count,
        Errors = result.Errors,
    };
}

/// <summary>
/// The practices of a profile with a song in a mode and with a hand, in short (the history of
/// phase 1.6): how many, the best one (in the order of <see cref="IProgressStore.BestPractice"/>)
/// and when the last one ended. <see cref="Best"/> has no errors.
/// </summary>
public sealed record PracticeSummary(ProgressSong Song, PlayMode Mode, WhichPart Hand, int Count,
    PracticeRecord Best, DateTime LastEnded);

/// <summary>
/// The progress of the user profiles (progress.db, next to library.db, D12): the profiles, the
/// songs (by their hash) with their maximum scores, and the whole practices with their errors.
/// Everything that uses it also works without it: when it is not open the queries are empty and
/// the changes do nothing. It is used from one thread (the thread of the interface).
/// </summary>
public interface IProgressStore
{
    bool IsOpen { get; }

    // ---- profiles ----

    /// <summary>The profiles, by name.</summary>
    List<ProgressProfile> Profiles();
    ProgressProfile? Profile(long id);
    /// <summary>The id of the new profile, or -1 if the name is empty or already used.</summary>
    long CreateProfile(string name);
    /// <summary>False if there is no such profile or the name is empty or already used.</summary>
    bool RenameProfile(long id, string name);
    /// <summary>Deletes a profile and all its practices.</summary>
    void DeleteProfile(long id);
    /// <summary>How many practices a profile has (0 if none, or on error).</summary>
    int PracticeCount(long profileId);

    // ---- songs ----

    /// <summary>The song with this hash, or null.</summary>
    ProgressSong? Song(string hash);
    /// <summary>The song with this hash, created if new. If it is known with another path or title
    /// (moved or renamed), they are updated. Null if the hash is empty or on error.</summary>
    ProgressSong? GetOrCreateSong(string hash, string path, string title);
    /// <summary>Stores the nominal maximum score of a song.</summary>
    void SetSongMaxScore(long songId, SongMaxScore maxScore);

    // ---- practices ----

    /// <summary>Saves a practice with all its errors (all or nothing). Its id, or -1 on error,
    /// also (logged) for a mode without practices (D10) or a practice of no hand.</summary>
    long SavePractice(PracticeRecord practice);
    /// <summary>A practice with its errors, or null.</summary>
    PracticeRecord? Practice(long id);
    /// <summary>The practices of a profile with a song in a mode and with a hand, the newest first,
    /// without their errors (see <see cref="PracticeRecord.ErrorCount"/>). None for a mode without
    /// practices or no hand.</summary>
    List<PracticeRecord> Practices(long profileId, long songId, PlayMode mode, WhichPart hand);
    /// <summary>The best of those practices (highest percentage of its real maximum, then highest
    /// score, then the earliest), with its errors, or null.</summary>
    PracticeRecord? BestPractice(long profileId, long songId, PlayMode mode, WhichPart hand);
    /// <summary>The newest of those practices, with its errors, or null.</summary>
    PracticeRecord? LastPractice(long profileId, long songId, PlayMode mode, WhichPart hand);
    /// <summary>Each song, mode and hand that a profile has practised, the last practised first,
    /// without errors (empty if none, or on error).</summary>
    List<PracticeSummary> PracticeSummaries(long profileId);
}
