// Ported from StavePosition.h and StavePosition.cpp of PianoBooster (C++).
// Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

public struct StaveLookup
{
    public int PianoNote;  // 1 is Middle C, 2 is D
    public int Accidental;

    public StaveLookup(int pianoNote, int accidental)
    {
        PianoNote = pianoNote;
        Accidental = accidental;
    }
}

/// <summary>Calculates the position on the stave from the stave index number.</summary>
public struct StavePos
{
    public const int NotUsed = 0x7fffffff;
    public const int MaxStaveIndex = 16;
    public const int MinStaveIndex = -16;

    private int _staveIndex;   // 0 central line, 5 = top line, -5 the bottom line,
    private int _accidental;   // 0 = none, 1=sharp, -1 =flat, 2=natural
    private float _offsetY;
    private WhichPart _hand;

    private static int s_keySignature;
    private static int s_keySignatureMajorMinor;
    private static StaveLookup[] s_staveLookUpTable;
    private static float s_staveCentralOffset = (StaveHeight * 3) / 2;
    private static float s_staveCenterY;

    // after the initialisers of the tables below
    static StavePos()
    {
        s_staveLookUpTable = s_c;
    }

    public StavePos()
    {
        _staveIndex = 0;
        _accidental = 0;
        _offsetY = 0;
        _hand = WhichPart.None;
        SetHand(WhichPart.None);
    }

    /// <param name="hand">the top (right hand) or the bottom the (left hand).</param>
    /// <param name="index">the stave index number: 0 central line, 5 = top line, -5 the bottom line.</param>
    /// <param name="accidental">0 = none, 1=sharp, -1 =flat.</param>
    public StavePos(WhichPart hand, int index, int accidental = 0)
    {
        _staveIndex = index;
        _accidental = accidental;
        _offsetY = 0;
        _hand = hand;
        SetHand(hand);
    }

    /// <summary>Sets which stave the note will appear on.</summary>
    public void SetHand(WhichPart hand)
    {
        _hand = hand;
        _offsetY = StaveCenterY;
        if (_hand == WhichPart.Right)
            _offsetY += StaveCentralOffset;
        else if (_hand == WhichPart.Left)
            _offsetY -= StaveCentralOffset;
    }

    /// <summary>Calculates the position of a note on the stave.</summary>
    public void NotePos(WhichPart hand, int midiNote)
    {
        const int notesInAnOctive = 7; // Don't count middle C twice
        const int semitonesInAnOctive = 12;
        SetHand(hand);
        int index = midiNote % semitonesInAnOctive;

        StaveLookup lookUpItem = s_staveLookUpTable[index];

        if (_hand == WhichPart.Right)
            _staveIndex = lookUpItem.PianoNote - 7;
        else if (_hand == WhichPart.Left)
            _staveIndex = lookUpItem.PianoNote + 5;

        _staveIndex += (midiNote / semitonesInAnOctive) * notesInAnOctive - notesInAnOctive * 5;
        _accidental = lookUpItem.Accidental;
    }

    /// <summary>The position on the stave.</summary>
    public readonly float PosY => VerticalNoteSpacing * (float)_staveIndex + _offsetY;

    public readonly float PosYAccidental
    {
        get
        {
            int accidental = _accidental;
            if (accidental == 2) accidental = 1;
            else if (accidental == -2) accidental = -1;
            return PosY + (float)accidental * VerticalNoteSpacing / 2;
        }
    }

    /// <summary>The Y position relative to the stave centre.</summary>
    public readonly float PosYRelative => PosY - s_staveCenterY;

    /// <summary>0 = none, 1=sharp, -1 =flat, 2=natural.</summary>
    public readonly int Accidental => _accidental;
    public readonly int StaveIndex => _staveIndex;
    public readonly WhichPart Hand => _hand;

    public static float StaveCenterY
    {
        get => s_staveCenterY;
        set => s_staveCenterY = value;
    }

    public static int KeySignature => s_keySignature;
    public static int KeySignatureMajorMinor => s_keySignatureMajorMinor;
    public static float StaveCentralOffset
    {
        get => s_staveCentralOffset;
        set => s_staveCentralOffset = value;
    }
    public static float VerticalNoteSpacing => 7;
    public static float StaveHeight => VerticalNoteSpacing * 8;

    public static void SetKeySignature(int key, int majorMinor)
    {
        s_keySignature = key;
        s_keySignatureMajorMinor = majorMinor;
        if (key == NotUsed)
            key = 0;
        s_staveLookUpTable = GetStaveLookupTable(key);
        DrawState.ForceCompileRedraw();
    }

    internal static void ResetStatics()
    {
        s_keySignature = 0;
        s_keySignatureMajorMinor = 0;
        s_staveLookUpTable = GetStaveLookupTable(0);
        s_staveCentralOffset = (StaveHeight * 3) / 2;
        s_staveCenterY = 0;
    }

    /// <summary>Converts the midi note to the note name C=1, D=2, E=3, F=4, G=5, A=6, B=7.</summary>
    public static StaveLookup MidiNote2Name(int midiNote)
    {
        StaveLookup[] staffLookupC = GetStaveLookupTable(0); // Key of C so we get all the not names
        const int semitonesInAnOctive = 12;
        int index = midiNote % semitonesInAnOctive;

        StaveLookup item = staffLookupC[index];

        if (item.Accidental != 0) // should it be a sharp or a flat
        {
            // Should it be called for example G# or Ab
            if (item.PianoNote != s_staveLookUpTable[index].PianoNote)
            {
                item.PianoNote = s_staveLookUpTable[index].PianoNote;
                item.Accidental = (s_keySignature > 0) ? 1 : -1; // a sharp, or else a flat
            }
        }
        return item;
    }

    /// <summary>Do we show a sharp or a flat for this key signature:
    /// 0 = none, 1=sharp, -1 =flat, 2=natural (# Key), -2=natural (b Key).</summary>
    public static int GetStaveAccidental(int midiNote) => s_staveLookUpTable[midiNote % 12].Accidental;

    /// <summary>0 = none, 1=above, -1 =below (a natural is either above or below).</summary>
    public static int GetStaveAccidentalDirection(int midiNote)
    {
        int accidentalDirection = GetStaveAccidental(midiNote);
        if (accidentalDirection == 2) // A natural so change to above
            accidentalDirection = 1;
        else if (accidentalDirection == -2) // A natural so change to below
            accidentalDirection = -1;
        return accidentalDirection;
    }

    private static StaveLookup L(int pianoNote, int accidental) => new(pianoNote, accidental);

    private static readonly StaveLookup[] s_flat6 = { // Gb [Bb Eb Ab Db Gb Cb]
        L(1, 2), L(2, 0), L(2, 2), L(3, 0), L(3, 2), L(4, 0), L(5, 0), L(5, 2), L(6, 0), L(6, 2), L(7, 0), L(8, 0) };
    private static readonly StaveLookup[] s_flat5 = { // Db [Bb Eb Ab Db Gb]
        L(1, 0), L(2, 0), L(2, 2), L(3, 0), L(3, 2), L(4, 0), L(5, 0), L(5, 2), L(6, 0), L(6, 2), L(7, 0), L(7, 2) };
    private static readonly StaveLookup[] s_flat4 = { // Ab [Bb Eb Ab Db]
        L(1, 0), L(2, 0), L(2, 2), L(3, 0), L(3, 2), L(4, 0), L(5, -1), L(5, 0), L(6, 0), L(6, 2), L(7, 0), L(7, 2) };
    private static readonly StaveLookup[] s_flat3 = { // Eb [Bb Eb Ab]
        L(1, 0), L(2, -1), L(2, 0), L(3, 0), L(3, 2), L(4, 0), L(5, -1), L(5, 0), L(6, 0), L(6, 2), L(7, 0), L(7, 2) };
    private static readonly StaveLookup[] s_flat2 = { // Bb [Bb Eb]
        L(1, 0), L(2, -1), L(2, 0), L(3, 0), L(3, 2), L(4, 0), L(4, 1), L(5, 0), L(6, -1), L(6, 0), L(7, 0), L(7, 2) };
    private static readonly StaveLookup[] s_flat1 = { // F [Bb]
        L(1, 0), L(1, 1), L(2, 0), L(3, -1), L(3, 0), L(4, 0), L(4, 1), L(5, 0), L(6, -1), L(6, 0), L(7, 0), L(7, 2) };
    // more flats Cb Gb Db Ab Eb Bb <--> F# C# G# D# A# E# more sharps
    private static readonly StaveLookup[] s_c = {
        L(1, 0), L(1, 1), L(2, 0), L(3, -1), L(3, 0), L(4, 0), L(4, 1), L(5, 0), L(5, 1), L(6, 0), L(7, -1), L(7, 0) };
    private static readonly StaveLookup[] s_sharp1 = { // G [F#]
        L(1, 0), L(1, 1), L(2, 0), L(2, 1), L(3, 0), L(4, -2), L(4, 0), L(5, 0), L(5, 1), L(6, 0), L(7, -1), L(7, 0) };
    private static readonly StaveLookup[] s_sharp2 = { // D [F# C#]
        L(1, -2), L(1, 0), L(2, 0), L(2, 1), L(3, 0), L(4, -2), L(4, 0), L(5, 0), L(5, 1), L(6, 0), L(6, 1), L(7, 0) };
    private static readonly StaveLookup[] s_sharp3 = { // A [F# C# G#]
        L(1, -2), L(1, 0), L(2, 0), L(2, 1), L(3, 0), L(4, -2), L(4, 0), L(5, -2), L(5, 0), L(6, 0), L(6, 1), L(7, 0) };
    private static readonly StaveLookup[] s_sharp4 = { // E [F# C# G# D#]
        L(1, -2), L(1, 0), L(2, -2), L(2, 0), L(3, 0), L(4, -2), L(4, 0), L(5, -2), L(5, 0), L(6, 0), L(6, 1), L(7, 0) };
    private static readonly StaveLookup[] s_sharp5 = { // B [F# C# G# D# A#]
        L(1, -2), L(1, 0), L(2, -2), L(2, 0), L(3, 0), L(4, -2), L(4, 0), L(5, -2), L(5, 0), L(6, -2), L(6, 0), L(7, 0) };
    // I think there should be some double sharps here ??
    private static readonly StaveLookup[] s_sharp6 = { // F# [F# C# G# D# A# E#]
        L(1, -2), L(1, 0), L(2, -2), L(2, 0), L(3, -2), L(3, 0), L(4, 0), L(5, -2), L(5, 0), L(6, -2), L(6, 0), L(7, 0) };

    public static StaveLookup[] GetStaveLookupTable(int key) => key switch
    {
        -6 => s_flat6,
        -5 => s_flat5,
        -4 => s_flat4,
        -3 => s_flat3,
        -2 => s_flat2,
        -1 => s_flat1,
        0 => s_c,
        1 => s_sharp1,
        2 => s_sharp2,
        3 => s_sharp3,
        4 => s_sharp4,
        5 => s_sharp5,
        6 => s_sharp6,
        _ => s_c,
    };
}
