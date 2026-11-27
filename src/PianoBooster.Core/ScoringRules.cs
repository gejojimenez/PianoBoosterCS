// Not in the C++ code: how many points the notes of a practice are worth (decision D1 of
// FEATURES.md). GPLv3, like the rest of PianoBoosterCS.

namespace PianoBooster.Core;

/// <summary>The points of the practice scores, all in one place so that they can be changed.</summary>
public static class ScoringRules
{
    /// <summary>What each note asked for is worth: the maximum score is this times the notes.</summary>
    public const int NoteValue = 10;

    // Follow you: the score starts at the maximum and each error takes points away (never below 0)

    /// <summary>Follow you: a wrong note.</summary>
    public const int FollowWrongPenalty = 10;

    /// <summary>Follow you: a note played early (in the tolerance window before the play zone) or
    /// late (still not played when the play zone ends).</summary>
    public const int FollowTimingPenalty = 5;

    /// <summary>Follow you: a note of the chord that was not played (skill below 3, or a chord
    /// skipped by a wrong note with "follow through errors"). The same as a wrong note (D4).</summary>
    public const int FollowMissedPenalty = 10;

    // Play along: the score starts at 0 and each note played adds points

    /// <summary>Play along: a note played in the play zone.</summary>
    public const int PlayAlongOnTimePoints = 10;

    /// <summary>Play along: a note played in a tolerance window, before or after the play zone.</summary>
    public const int PlayAlongTimingPoints = 5;

    /// <summary>Play along: a note not played (the beat was skipped).</summary>
    public const int PlayAlongMissedPoints = 0;

    /// <summary>Play along: a wrong note (it is kept as an error but takes nothing away).</summary>
    public const int PlayAlongWrongPoints = 0;

    /// <summary>
    /// How wide the tolerance windows are (D2b), as a multiple of the play zone on their side:
    /// before the zone, from -(1 + this) × Early to -Early; after it, from Late to (1 + this) × Late.
    /// </summary>
    public const int ToleranceWindowZones = 1;
}
