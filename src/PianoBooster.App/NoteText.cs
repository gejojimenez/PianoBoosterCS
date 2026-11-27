// Not in the C++ code: the names of the notes in the results of the practices (phase 1.6 of
// FEATURES.md). GPLv3, like the rest of PianoBoosterCS.

using PianoBooster.Core;
using PianoBooster.Rendering;

namespace PianoBooster.App;

/// <summary>
/// The name of a MIDI note as the score names it (<see cref="StavePos.MidiNote2Name"/> with the
/// translated <see cref="Draw.NoteNames"/>: a sharp or a flat as the key signature of the loaded
/// song asks for) followed by its octave, with the middle C (60) in octave 4: "C4", "F♯3", "Do4".
/// </summary>
internal static class NoteText
{
    /// <summary>For a note that is not known (a missed note has no played one).</summary>
    public const string None = "—";

    /// <summary>keyOfC: as in the key of C (for a song that is not the loaded one, whose key
    /// signature is not known): C♯, E♭, F♯, G♯ and B♭.</summary>
    public static string Name(int? pitch, bool keyOfC = false)
    {
        if (pitch is not int midiNote || midiNote < 0 || midiNote > 127)
            return None;
        StaveLookup item = keyOfC ? StavePos.GetStaveLookupTable(0)[midiNote % 12] : StavePos.MidiNote2Name(midiNote);
        // C=1 ... B=7. Only the black keys change their name with the key signature (C♯ or D♭),
        // so the octave is always the one of the C below.
        int letter = item.PianoNote;
        int octave = midiNote / 12 - 1;
        string[] names = Draw.NoteNames;
        if (letter < 1 || letter > names.Length)
            return midiNote.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string accidental = item.Accidental switch
        {
            -1 => "♭",
            1 => "♯",
            _ => "",
        };
        return names[letter - 1] + accidental + octave.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
