// Ported from Piano.h and Piano.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
// Only the model: the notes the pianist has down and the positions of their names.
// The drawing (drawPianoInput) goes to PianoBooster.Rendering.

namespace PianoBooster.Core;

public struct NoteNameItem
{
    public float PosY;
    public float PosYOriginal;
    public int Type; // not used
    public int Pitch;
}

/// <summary>The notes that the pianist is playing (the C++ CPiano without its drawing).</summary>
public sealed class Piano
{
    private const float MinNameGap = 14.0f;
    private const int ListSize = 20;

    private struct SavedNoteOffChord
    {
        public int PitchKey;       // This used to fined the Saved note off;
        public Chord SavedChord;
    }

    private readonly NoteNameItem[] _noteNameList = new NoteNameItem[ListSize];
    private readonly SavedNoteOffChord[] _savedChordLookUp = new SavedNoteOffChord[ListSize];
    private uint _noteNameListLength;

    private Chord _goodChord = new(); // The colored note lines that appear on the score when the pianist plays
    private Chord _badChord = new();
    private bool _rhythmTapping;

    public Piano(IPianoSettings settings)
    {
        Settings = settings;
    }

    public IPianoSettings Settings { get; }

    /// <summary>The note names shown next to the notes played (sorted, spaced apart).</summary>
    public NoteNameItem[] NoteNameList => _noteNameList;
    public int NoteNameListLength => (int)_noteNameListLength;
    public bool RhythmTapping => _rhythmTapping;

    public Chord GoodChord => _goodChord;
    public Chord BadChord => _badChord;

    public void SetRhythmTapping(bool state) => _rhythmTapping = state;

    private void SpaceNoteBunch(uint bottomIndex, uint topIndex)
    {
        long range = (long)topIndex - (long)bottomIndex;
        if (range <= 1)
            return;

        long midPoint = range / 2 + bottomIndex;
        if (midPoint >= ListSize)
            return;

        float gap;
        if (range % 2 == 0) // test for an even number of notes
        {
            gap = _noteNameList[midPoint].PosY - _noteNameList[midPoint - 1].PosY;
            if (gap < MinNameGap)
            {
                gap = (MinNameGap - gap) / 2;
                _noteNameList[midPoint].PosY += gap; // then move the middle two notes apart by the same amount
                _noteNameList[midPoint - 1].PosY -= gap;
            }
        }

        // Search from the middle upwards
        float lastY = _noteNameList[midPoint].PosY;
        for (long i = midPoint + 1; i < (int)topIndex; i++)
        {
            gap = _noteNameList[i].PosY - lastY;
            // If the gap is too small make it bigger
            if (gap < MinNameGap)
                _noteNameList[i].PosY = lastY + MinNameGap;
            lastY = _noteNameList[i].PosY;
        }

        lastY = _noteNameList[midPoint].PosY;

        // now go the other way
        for (long i = midPoint - 1; i >= 0; i--)
        {
            gap = lastY - _noteNameList[i].PosY;
            if (gap < MinNameGap)
                _noteNameList[i].PosY = lastY - MinNameGap;
            lastY = _noteNameList[i].PosY;
        }
    }

    private void SpaceNoteNames()
    {
        _noteNameList[0].PosY = _noteNameList[0].PosYOriginal;

        if (_noteNameListLength <= 1)
            return;

        uint bottomIndex = _noteNameListLength;
        uint topIndex = 0;
        bool foundBunch = false;
        for (uint i = 1; i < _noteNameListLength; i++)
        {
            _noteNameList[i].PosY = _noteNameList[i].PosYOriginal;
            if (_noteNameList[i].PosY - _noteNameList[i - 1].PosY < MinNameGap)
            {
                if (bottomIndex > i) bottomIndex = i;
                if (topIndex < i) topIndex = i;
                foundBunch = true;
            }
            else
            {
                if (foundBunch)
                    SpaceNoteBunch(bottomIndex - 1, topIndex + 1);

                // reset everything ready for the next bunch
                bottomIndex = _noteNameListLength;
                topIndex = 0;
                foundBunch = false;
            }
        }
        if (foundBunch)
            SpaceNoteBunch(bottomIndex - 1, topIndex + 1);

        // finally try again in case moving things have created a fresh bunch
        SpaceNoteBunch(0, _noteNameListLength);
    }

    private void AddNoteNameItem(float posY, int pitch, int type)
    {
        if (_noteNameListLength >= ListSize)
            return;

        var noteNameItem = new NoteNameItem { PosY = posY, PosYOriginal = posY, Type = type, Pitch = pitch };

        // Sort the entries low to high
        int i;
        for (i = (int)_noteNameListLength - 1; i >= 0; i--)
        {
            if (_noteNameList[i].Pitch <= pitch)
                break;
            // move the previous entry up one position
            _noteNameList[i + 1] = _noteNameList[i];
        }
        // (the C++ code reads the item before the list when i is -1)
        if (i >= 0 && _noteNameList[i].Pitch == pitch)
            return; // ignore duplicates
        _noteNameList[i + 1] = noteNameItem;
        _noteNameListLength++;
        SpaceNoteNames();
    }

    public void AddPianistNote(WhichPart part, MidiEvent midiNote, bool good)
    {
        if (midiNote.Velocity == -1)
            return;

        int note = midiNote.Note;

        var stavePos = new StavePos();
        stavePos.NotePos(part, note);

        if (stavePos.StaveIndex >= StavePos.MaxStaveIndex || stavePos.StaveIndex <= StavePos.MinStaveIndex)
            return;

        if (good)
            _goodChord.AddNote(part, note);
        else
            _badChord.AddNote(part, note);

        float posY = stavePos.PosYAccidental;
        AddNoteNameItem(posY, note, 0);
    }

    private void RemoveNoteNameItem(int pitch)
    {
        bool foundMatch = false;

        for (uint i = 0; i < _noteNameListLength; i++)
        {
            if (foundMatch)
                _noteNameList[i - 1] = _noteNameList[i]; // found a match so move every thing else up

            if (_noteNameList[i].Pitch == pitch)
                foundMatch = true;
        }
        if (_noteNameListLength > 0 && foundMatch)
            _noteNameListLength--;
        SpaceNoteNames();
    }

    /// <summary>Returns true only if the note is in the bad note list.</summary>
    public bool RemovePianistNote(int note)
    {
        RemoveNoteNameItem(note);
        _goodChord.RemoveNote(note);
        return _badChord.RemoveNote(note);
    }

    private void NoteNameListClear() => _noteNameListLength = 0;

    /// <summary>Counts the number of notes the pianist has down.</summary>
    public int PianistAllNotesDown() => _goodChord.Length + _badChord.Length;

    public int PianistBadNotesDown() => _badChord.Length;

    public void Clear()
    {
        _goodChord.Clear();
        _badChord.Clear();
        NoteNameListClear();
        for (int i = 0; i < _savedChordLookUp.Length; i++)
            _savedChordLookUp[i].PitchKey = 0;
    }

    public void AddSavedChord(MidiEvent midiNote, Chord chord)
    {
        int key = midiNote.Note;
        for (int i = 0; i < _savedChordLookUp.Length; i++)
        {
            ref SavedNoteOffChord savedChord = ref _savedChordLookUp[i];
            if (midiNote.Type == Midi.NoteOn)
            {
                if (savedChord.PitchKey == 0)
                {
                    savedChord.PitchKey = key;
                    savedChord.SavedChord = chord;
                    return;
                }
            }
            else if (midiNote.Type == Midi.NoteOff)
            {
                if (savedChord.PitchKey == key)
                {
                    savedChord.PitchKey = 0;
                    return;
                }
            }
        }
        _savedChordLookUp[0].SavedChord = chord;
    }

    public Chord RemoveSavedChord(int key)
    {
        int i = 0;
        for (; i < _savedChordLookUp.Length; ++i)
        {
            if (_savedChordLookUp[i].PitchKey == key)
            {
                _savedChordLookUp[i].PitchKey = 0;
                return _savedChordLookUp[i].SavedChord;
            }
        }
        --i;
        _savedChordLookUp[i].SavedChord.Clear();
        return _savedChordLookUp[i].SavedChord;
    }
}
