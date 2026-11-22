// Ported from MidiEvent.h of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

/// <summary>The MIDI message types and the events of PianoBooster itself.</summary>
public static class Midi
{
    public const int NoteOff = 0x80;            // MIDI voice messages
    public const int NoteOn = 0x90;
    public const int NotePressure = 0xA0;       // POLY_AFTERTOUCH: 3 bytes
    public const int ControlChange = 0xB0;
    public const int ProgramChange = 0xC0;
    public const int ChannelPressure = 0xD0;    // AFTERTOUCH: 2 bytes
    public const int PitchBend = 0xE0;
    public const int SystemEvent = 0xF0;
    public const int Sustain = 0x40;
    public const int MainVolume = 0x07;
    public const int ResetAllControllers = 121;
    public const int AllSoundOff = 120;
    public const int AllNotesOff = 123;         // 0x7B channel mode message

    // PianoBooster's own events
    public const int None = 0x0ff0;
    public const int Error = 0x0ff1;
    public const int PbEof = 0x0ff2;
    public const int PbChordSeparator = 0x0ff3; // All the notes (note on) between these counts as one chord
    public const int PbTempo = 0x0ff4;
    public const int PbTimeSignature = 0x0ff5;
    public const int PbKeySignature = 0x0ff6;
    public const int PbCollateRawMidiData = 0x0ff7;
    public const int PbOutputRawMidiData = 0x0ff8; // Raw data is used for used for a SYSTEM_EVENT

    // Standard MIDI file events
    public const int SysexEvent = 0xF0;
    public const int MetaEvent = 0xFF;
    public const int MetaSeqN = 0;
    public const int MetaText = 1;
    public const int MetaCopyright = 2;
    public const int MetaTrackName = 3;
    public const int MetaInstrumentName = 4;
    public const int MetaLyric = 5;
    public const int MetaMarker = 6;
    public const int MetaCuePoint = 7;
    public const int MetaChannelPrefix = 0x20;
    public const int MetaEndOfTrack = 0x2F;
    public const int MetaTempo = 0x51;
    public const int MetaSmpteOffset = 0x54;
    public const int MetaTimeSignature = 0x58;
    public const int MetaKeySignature = 0x59;
    public const int MetaSequencerEvent = 0x7F;

    public const int GmPianoPatch = 0; // The default grand piano sound

    public static bool IsPianoOrOrganPatch(int p) => (p >= 0 && p <= 7) || (p >= 16 && p <= 23);

    // Util.h
    public const int MaxMidiChannels = 16;     // There are always at most 16 midi channels
    public const int MiddleC = 60;
    public const int Octave = 12;
    public const int BottomC = MiddleC - Octave * 2;
    public const int TopC = MiddleC + Octave * 2;
    public const int DrumChannel = 10 - 1;
    public const int MaxMidiNotes = 128;
    public const int MaxMidiTracks = 32;       // This will allow us to map midi track on to midi channels
}

/// <summary>
/// A MIDI event. Like the C++ CMidiEvent it is a value: copies are independent.
/// </summary>
public struct MidiEvent
{
    // The type is kept XORed with Midi.None, so that default(MidiEvent) is a cleared event
    // (type Midi.None) like the one made by the C++ constructor.
    private int _typeX;
    private int _deltaTime;
    private int _channel;
    private int _note;
    private int _velocity;
    private int _duration;
    private int _track;

    public MidiEvent()
    {
        Clear();
    }

    /// <summary>A cleared event (type <see cref="Midi.None"/>).</summary>
    public static MidiEvent Empty => default;

    public void Clear()
    {
        Type = Midi.None;
        _deltaTime = 0;
        _channel = 0;
        _note = 0;
        _velocity = 0;
        _duration = 0;
        // like the C++ clear(), the track is kept
    }

    public int DeltaTime { readonly get => _deltaTime; set => _deltaTime = value; }
    public void AddDeltaTime(int delta) => _deltaTime += delta;
    public void SubtractDeltaTime(int delta) => _deltaTime -= delta;

    /// <summary>The MIDI note number.</summary>
    public int Note { readonly get => _note; set => _note = value; }
    public readonly int Programme => _note;
    /// <summary>The channel (can also contain the track number).</summary>
    public int Channel { readonly get => _channel; set => _channel = value; }
    public int Velocity { readonly get => _velocity; set => _velocity = value; }
    public int Type { readonly get => _typeX ^ Midi.None; set => _typeX = value ^ Midi.None; }
    public void Transpose(int amount) => _note += amount;
    public readonly int Data1 => _note; // Meta data is stored here
    public int Data2 { readonly get => _velocity; set => _velocity = value; }
    public int Track { readonly get => _track; set => _track = value; }

    /// <summary>How long the note was played for (the note on events of a MIDI file).</summary>
    public int Duration { readonly get => _duration; set => _duration = value; }

    public void NoteOffEvent(int deltaTime, int channel, int note, int velocity)
    {
        Type = Midi.NoteOff;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = note;
        _velocity = velocity;
    }

    public void NoteOnEvent(int deltaTime, int channel, int note, int velocity)
    {
        Type = Midi.NoteOn;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = note;
        _velocity = velocity;
    }

    public void NotePressureEvent(int deltaTime, int channel, int data1, int data2)
    {
        Type = Midi.NotePressure;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = data1;
        _velocity = data2;
    }

    public void ProgramChangeEvent(int deltaTime, int channel, int program)
    {
        Type = Midi.ProgramChange;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = program;
        _velocity = 0;
    }

    public void ControlChangeEvent(int deltaTime, int channel, int data1, int data2)
    {
        Type = Midi.ControlChange;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = data1;
        _velocity = data2;
    }

    public void ChannelPressureEvent(int deltaTime, int channel, int data1)
    {
        Type = Midi.ChannelPressure;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = data1;
        _velocity = 0;
    }

    public void PitchBendEvent(int deltaTime, int channel, int data1, int data2)
    {
        Type = Midi.PitchBend;
        _deltaTime = deltaTime;
        _channel = channel;
        _note = data1;
        _velocity = data2;
    }

    public void ChordSeparator(in MidiEvent other)
    {
        Type = Midi.PbChordSeparator;
        _note = 0;
        _channel = other.Channel;
        _deltaTime = 0;
        _velocity = 0;
    }

    public void MetaEvent(int deltaTime, int type, int data1, int data2)
    {
        Type = type;
        _deltaTime = deltaTime;
        _channel = 0;
        _note = data1;
        _velocity = data2;
    }

    // Raw data is used for used for a SYSTEM_EVENT
    public void CollateRawByte(int deltaTime, int nextByte)
    {
        Type = Midi.PbCollateRawMidiData;
        _deltaTime = deltaTime;
        _note = nextByte;
        _velocity = 0;
    }

    public void OutputCollatedRawBytes(int deltaTime)
    {
        Type = Midi.PbOutputRawMidiData;
        _deltaTime = deltaTime;
        _note = 0;
        _velocity = 0;
    }

    /// <summary>
    /// Merges two events: a copy of the one that happens first (the lowest delta time) is
    /// returned and that one is cleared; the delta time of the other one is reduced by it.
    /// Both are changed.
    /// </summary>
    public MidiEvent GetNextMergedEvent(ref MidiEvent other)
    {
        MidiEvent result;
        int thisDelta = DeltaTime;
        int otherDelta = other.DeltaTime;
        if (thisDelta <= otherDelta)
        {
            result = this;
            other.SubtractDeltaTime(thisDelta);
            Clear();
        }
        else
        {
            result = other;
            SubtractDeltaTime(otherDelta);
            other.Clear();
        }
        return result;
    }

    public override readonly string ToString() =>
        $"type 0x{Type:x} delta {DeltaTime} chan {Channel} note {Note} vel {Velocity}";
}
