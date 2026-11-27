// Ported from Chord.h and Chord.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System.Runtime.CompilerServices;

namespace PianoBooster.Core;

/// <summary>The part for each hand uses a different midi channel.</summary>
public enum WhichPart
{
    Both = 200, // keep well clear of real midi channels
    Right,
    Left,
    None,
}

/// <summary>A note of a chord, and (static) which channels and tracks hold the hands.</summary>
public struct Note
{
    private WhichPart _part;
    private int _pitch;
    private int _duration;

    private static int s_leftHandChannel = -2;
    private static int s_rightHandChannel = -2;
    private static WhichPart s_activeHand = WhichPart.Both;
    // -1 means there is a single track and no separate left and right hand parts
    private static readonly int[] s_rightHandTrack = new int[Midi.MaxMidiChannels];

    public Note()
    {
        _part = WhichPart.None;
        _pitch = 0;
        _duration = 0;
    }

    public Note(WhichPart part, int note, int duration = 0)
    {
        _part = part;
        _pitch = note;
        _duration = duration;
    }

    public void Transpose(int amount) => _pitch += amount;
    public readonly int Pitch => _pitch;
    public WhichPart Part { readonly get => _part; set => _part = value; }

    public static void Reset()
    {
        SetChannelHands(-2, -2); // -2 for not set -1 for none
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
            s_rightHandTrack[chan] = -1;
    }

    internal static void ResetStatics()
    {
        s_leftHandChannel = -2;
        s_rightHandChannel = -2;
        s_activeHand = WhichPart.Both;
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
            s_rightHandTrack[chan] = 0;
    }

    public static WhichPart FindHand(in MidiEvent midi, int whichChannel, WhichPart whichPart)
    {
        int midiNote = midi.Note;
        int midiChannel = midi.Channel;
        WhichPart hand = WhichPart.None;
        // exit if it is not for this channel
        if (midiChannel != whichChannel)
        {
            // return none if this is not being used with the other hand.
            if (!HasPianoPart(whichChannel) || !HasPianoPart(midiChannel))
                return WhichPart.None;
        }
        int rightHandTrack = RightHandTrack(midiChannel);

        if (midiChannel == whichChannel && rightHandTrack >= 0)
            hand = (midi.Track == rightHandTrack) ? WhichPart.Right : WhichPart.Left;
        else if (midiChannel == RightHandChan)
            hand = WhichPart.Right;
        else if (midiChannel == LeftHandChan)
            hand = WhichPart.Left;
        else if (midiChannel == whichChannel)
            hand = (midiNote >= Midi.MiddleC) ? WhichPart.Right : WhichPart.Left;

        if (whichPart == WhichPart.Left && hand == WhichPart.Right)
            hand = WhichPart.None;
        if (whichPart == WhichPart.Right && hand == WhichPart.Left)
            hand = WhichPart.None;
        return hand;
    }

    public static void SetRightHandTrack(int channel, int rightHandTrackNumber) =>
        s_rightHandTrack[channel] = rightHandTrackNumber;

    public static int RightHandTrack(int channel) => s_rightHandTrack[channel];

    public static void SetChannelHands(int left, int right)
    {
        s_leftHandChannel = left;
        s_rightHandChannel = right;
    }

    public static WhichPart ActiveHand
    {
        get => s_activeHand;
        set => s_activeHand = value;
    }

    public static int RightHandChan => s_rightHandChannel;
    public static int LeftHandChan => s_leftHandChannel;
    public static int BothHandsChan => s_leftHandChannel;
    public static int GetHandChannel(WhichPart whichPart) =>
        (whichPart == WhichPart.Right) ? s_rightHandChannel : s_leftHandChannel;
    public static bool HasPianoPart(int chan) => s_leftHandChannel == chan || s_rightHandChannel == chan;
}

public struct NoteRange
{
    public int HighestNote; // The highest note in the range (often on the users piano keyboard)
    public int LowestNote;
}

[InlineArray(Chord.MaxChordNotes)]
internal struct ChordNotes
{
    private Note _element;
}

/// <summary>The notes played at the same time. A value, like the C++ CChord.</summary>
public struct Chord
{
    public const int MaxChordNotes = 20; // The maximum notes in a chord well we only have 10 fingers

    private int _deltaTime;
    private long _tick;
    private ChordNotes _notes;
    private int _length;

    private static int s_highestPianoNote = 127; // The highest note on the users piano keyboard;
    private static int s_lowestPianoNote = 0;

    public Chord()
    {
        Clear();
    }

    public readonly Note GetNote(int index) => _notes[index];
    public readonly int Length => _length;
    public int DeltaTime { readonly get => _deltaTime; set => _deltaTime = value; }

    /// <summary>When the first note of the chord starts, in ticks of the file from the start of
    /// the song (not in the C++ code: used to know the bar and beat of the chord).</summary>
    public long Tick { readonly get => _tick; set => _tick = value; }

    public void Clear()
    {
        _length = 0;
        _deltaTime = 0;
        _tick = 0;
    }

    public void AddNote(WhichPart part, int note, int duration = 0)
    {
        if (_length >= MaxChordNotes)
            return; // Over the chord note limit

        if (SearchChord(note)) // don't add duplicates
            return;
        _notes[_length] = new Note(part, note, duration);
        _length++;
    }

    public bool RemoveNote(int note)
    {
        bool noteFound = false;
        for (int i = 0; i < MaxChordNotes; i++)
        {
            if (i >= _length)
                break;
            if (!noteFound)
            {
                if (GetNote(i).Pitch == note)
                    noteFound = true;
            }
            else
            {
                // shove everything else up to remove the note
                _notes[i - 1] = GetNote(i);
            }
        }
        if (noteFound)
            _length--;
        return noteFound;
    }

    public readonly bool SearchChord(int note, int transpose = 0)
    {
        for (int i = 0; i < _length; i++)
        {
            if (GetNote(i).Pitch + transpose == note)
                return true;
        }
        return false;
    }

    public int TrimOutOfRangeNotes(int transpose)
    {
        int removedNotes = 0;
        for (int i = 0; i < MaxChordNotes; i++)
        {
            if (i >= _length)
                break;

            if (!IsNotePlayable(GetNote(i).Pitch, transpose) || !IsHandPlayable(GetNote(i).Part))
                removedNotes++; // remove the note
            else if (removedNotes != 0)
            {
                // shove everything else up to remove the note
                _notes[i - removedNotes] = GetNote(i);
            }
        }
        _length -= removedNotes;
        return _length;
    }

    public void Transpose(int amount)
    {
        for (int i = 0; i < _length; i++)
            _notes[i].Transpose(amount);
    }

    public static void SetPianoRange(int lowestNote, int highestNote)
    {
        s_highestPianoNote = highestNote; // The highest note on the users piano keyboard;
        s_lowestPianoNote = lowestNote;
    }

    internal static void ResetStatics()
    {
        s_highestPianoNote = 127;
        s_lowestPianoNote = 0;
    }

    public static bool IsNotePlayable(int note, int transpose)
    {
        note += transpose;
        return note >= s_lowestPianoNote && note <= s_highestPianoNote;
    }

    public static bool IsPianistNote(in MidiEvent midi, int transpose, int whichChannel)
    {
        WhichPart whichPart = Note.ActiveHand;
        int note = midi.Note;
        WhichPart hand = Note.FindHand(midi, whichChannel, whichPart);
        if (hand == WhichPart.None)
            return false;
        return IsNotePlayable(note, transpose);
    }

    public static bool IsHandPlayable(WhichPart hand)
    {
        if (Note.ActiveHand == WhichPart.Both)
            return true;
        return Note.ActiveHand == hand;
    }
}

/// <summary>Finds the chords (notes that start at about the same time) in a stream of events.</summary>
public sealed class FindChord
{
    private int _noteGapTime;
    private int _cordSpanGapTime;
    private Chord _currentChord;
    private Chord _completeChord;
    private int _cfgChordNoteGap;
    private int _cfgChordMaxLength;
    private long _songTick;

    public FindChord()
    {
        Reset();
    }

    public void Reset()
    {
        _noteGapTime = 0;
        _cordSpanGapTime = 0;
        _songTick = 0;
        _completeChord.Clear();
        _currentChord.Clear();
        _cfgChordNoteGap = MidiFile.PpqnAdjust(Cfg.ChordNoteGap);
        _cfgChordMaxLength = MidiFile.PpqnAdjust(Cfg.ChordMaxLength);
    }

    /// <summary>The time of the last event given to Find(), in ticks of the file from the start
    /// of the song. Set it after Reset() when the events do not start at the start of the song.</summary>
    public long SongTick { get => _songTick; set => _songTick = value; }

    public Chord GetChord()
    {
        Chord chord = _completeChord;
        _completeChord.Clear();
        return chord;
    }

    public bool Find(in MidiEvent midi, int channel, WhichPart part)
    {
        bool foundChord = false;

        if (midi.Type == Midi.PbEof)
        {
            if (_currentChord.Length > 0)
            {
                _completeChord = _currentChord;
                foundChord = true;
            }
            return foundChord;
        }

        _noteGapTime += midi.DeltaTime;
        _songTick += midi.DeltaTime;

        if ((_noteGapTime >= _cfgChordNoteGap || _cordSpanGapTime > _cfgChordMaxLength)
                && _currentChord.Length > 0)
        {
            foundChord = true;
            _completeChord = _currentChord;
            _currentChord.Clear();
        }

        if (midi.Type == Midi.NoteOn)
        {
            WhichPart hand = Note.FindHand(midi, channel, part);
            if (hand != WhichPart.None)
            {
                if (_currentChord.Length == 0)
                    _currentChord.Tick = _songTick;
                _currentChord.AddNote(hand, midi.Note);
                _currentChord.DeltaTime = _noteGapTime + _currentChord.DeltaTime;
                if (_currentChord.Length <= 1)
                    _cordSpanGapTime = 0;
                else
                    _cordSpanGapTime += _noteGapTime; // measure the span of the cord
                _noteGapTime = 0;
            }
        }
        return foundChord;
    }
}
