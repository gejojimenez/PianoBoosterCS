// Ported from Symbol.h of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

public enum MusicalSymbol
{
    None,
    Drum,
    GClef,          // The Treble Clef
    FClef,          // The Base Clef
    Sharp,
    Flat,
    Natural,
    BarLine,
    BarMarker,
    BeatMarker,
    PlayingZone,
    TheEndMarker,

    NoteHead,       // ONLY ADD NOTES BELOW THIS MAKER
    Demisemiquaver, // Demisemiquaver / Thirty-second note
    Semiquaver,     // Semiquaver / Sixteenth note
    Quaver,         // Quaver / Eighth note
    Crotchet,       // Crotchet / Quarter note
    Minim,          // Minim / Half note
    Semibreve,      // Semibreve / Whole note
    Breve,          // Breve / Double whole note
}

public enum AccidentalModifier
{
    NoChange,
    Suppress,
    Force, // force a natural/accidental to be played
}

/// <summary>A symbol of the score: a note head, a bar line, a beat marker ... A value.</summary>
public struct Symbol
{
    public const int BeatMarkerOffset = 20; // used to ensure that beat markers are drawn under the note by drawing them early

    private StavePos _stavePos;
    private MusicalSymbol _symbolType;
    private int _midiNote;
    private int _barNumber;
    private AccidentalModifier _accidentalModifier; // Used to suppress the second sharp in the same bar
    private int _midiDuration;
    private WhichPart _hand;
    private PbColor _color;
    private long _pianistTiming;
    private int _index;  // the number of the note per hand starting from the bottom.
    private int _total;  // the number of the notes per hand;

    public Symbol(MusicalSymbol type, WhichPart hand, int midiNote)
    {
        _stavePos = new StavePos();
        Init();
        _symbolType = type;
        _midiNote = midiNote;
        _hand = hand;
        _midiDuration = 0;
        _stavePos.NotePos(hand, midiNote);
    }

    public Symbol()
    {
        _stavePos = new StavePos();
        Init();
    }

    public Symbol(MusicalSymbol type, StavePos stavePos) : this(type, stavePos, Cfg.NoteColor)
    {
    }

    public Symbol(MusicalSymbol type, StavePos stavePos, PbColor color)
    {
        _stavePos = new StavePos();
        Init();
        _symbolType = type;
        _stavePos = stavePos;
        _color = color;
        _hand = stavePos.Hand;
    }

    private void Init()
    {
        _symbolType = MusicalSymbol.None;
        _midiNote = 0;
        _hand = WhichPart.None;
        _midiDuration = 0;
        _pianistTiming = StavePos.NotUsed;
        SetIndex(0, 0);
        _accidentalModifier = AccidentalModifier.NoChange;
        _barNumber = 0;
        _color = new PbColor();
    }

    public readonly MusicalSymbol Type => _symbolType;

    /// <summary>How long the midi note was played for.</summary>
    public int MidiDuration { readonly get => _midiDuration; set => _midiDuration = value; }

    /// <summary>Sets the note score length.</summary>
    public void SetNoteLength(MusicalSymbol type) => _symbolType = type;

    /// <summary>The midi note number.</summary>
    public readonly int Note => _midiNote;

    /// <summary>The bar number shown at a bar marker (counting from 0, like Bar).</summary>
    public int BarNumber { readonly get => _barNumber; set => _barNumber = value; }

    public readonly WhichPart Hand => _hand;
    public readonly StavePos StavePos => _stavePos;

    public PbColor Color { readonly get => _color; set => _color = value; }
    public long PianistTiming { readonly get => _pianistTiming; set => _pianistTiming = value; }

    public void Transpose(int amount)
    {
        if (_symbolType >= MusicalSymbol.NoteHead)
        {
            _midiNote += amount;
            _stavePos.NotePos(_hand, _midiNote); // The save position has now moved
        }
    }

    public void SetIndex(int index, int total)
    {
        _index = index;
        _total = total;
    }

    public readonly int NoteIndex => _index;
    public readonly int NoteTotal => _total;

    /// <summary>The accidental: 0 = none, 1=sharp, -1 =flat, 2=natural.</summary>
    public readonly int Accidental => _stavePos.Accidental;

    public AccidentalModifier AccidentalModifier
    {
        readonly get => _accidentalModifier;
        set => _accidentalModifier = value;
    }
}
