// Not in the C++ code: the maximum score of a song for each hand (§4.1 and D11 of FEATURES.md).
// GPLv3, like the rest of PianoBoosterCS.

using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>
/// The notes of each hand of a song and its maximum scores: a hand is worth
/// <see cref="ScoringRules.NoteValue"/> for each of its notes, and both hands the sum of the two.
/// This is the nominal maximum (D11): all the notes of each hand, whatever the keyboard and the
/// transposition (a practice keeps its real maximum, <see cref="PracticeResult.MaxScore"/>).
/// </summary>
public readonly record struct SongMaxScore(int NotesRight, int NotesLeft)
{
    public int NotesBoth => NotesRight + NotesLeft;

    public int MaxRight => NotesRight * ScoringRules.NoteValue;
    public int MaxLeft => NotesLeft * ScoringRules.NoteValue;
    public int MaxBoth => MaxRight + MaxLeft;

    public int NotesFor(WhichPart hand) => hand switch
    {
        WhichPart.Right => NotesRight,
        WhichPart.Left => NotesLeft,
        _ => NotesBoth,
    };

    public int MaxFor(WhichPart hand) => NotesFor(hand) * ScoringRules.NoteValue;

    /// <summary>Counts the notes of chords by their hand (notes of no hand are not counted).</summary>
    public static SongMaxScore FromChords(IEnumerable<Chord> chords)
    {
        int right = 0, left = 0;
        foreach (Chord chord in chords)
        {
            for (int i = 0; i < chord.Length; i++)
            {
                if (chord.GetNote(i).Part == WhichPart.Right)
                    right++;
                else if (chord.GetNote(i).Part == WhichPart.Left)
                    left++;
            }
        }
        return new SongMaxScore(right, left);
    }

    /// <summary>
    /// Reads a MIDI file and finds its chords like the song does for the conductor (the same
    /// <see cref="FindChord"/>, with the hands of the piano part set for the song loaded: call it
    /// after <see cref="Song.LoadSong"/> of that file and with the lock of the song held).
    /// Null if the file can't be read.
    /// </summary>
    public static SongMaxScore? FromMidiFile(string fileName, int activeChannel)
    {
        int songPpqn = MidiFile.PulsesPerQuarterNote;
        try
        {
            var midiFile = new MidiFile();
            midiFile.OpenMidiFile(fileName);
            if (midiFile.MidiError != MidiError.NoError)
                return null;
            return FromChords(ReadChords(midiFile, activeChannel));
        }
        finally
        {
            MidiFile.RestorePulsesPerQuarterNote(songPpqn);
        }
    }

    private static List<Chord> ReadChords(MidiFile midiFile, int activeChannel)
    {
        var chords = new List<Chord>();
        var findChord = new FindChord(); // after the file is opened: its gaps depend on the ppqn
        while (true)
        {
            MidiEvent midiEvent = midiFile.ReadMidiEvent();
            if (findChord.Find(midiEvent, activeChannel, WhichPart.Both))
                chords.Add(findChord.GetChord());
            if (midiEvent.Type == Midi.PbEof || midiFile.MidiError != MidiError.NoError)
                break;
        }
        return chords;
    }
}
