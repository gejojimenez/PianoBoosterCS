// Ported from MidiTrack.h and MidiTrack.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System.Collections.Generic;
using System.Text;

namespace PianoBooster.Core;

public enum MidiError
{
    NoError,
    CannotOpenFile,
    CorruptedMidiFile,
    UnknownEvent,
    TooManyTracks,
    EndOfFile,
}

/// <summary>
/// The bytes of a file read like the C++ std::fstream used by CMidiFile and CMidiTrack:
/// reading past the end returns -1 (EOF) and sets the fail state, which stays set (seeking
/// does nothing and the position is -1) until the file is opened again.
/// </summary>
internal sealed class MidiStream
{
    private readonly byte[] _data;
    private long _pos;
    private bool _fail;

    public MidiStream(byte[] data)
    {
        _data = data;
    }

    public int Get()
    {
        if (_fail)
            return -1;
        if (_pos < 0 || _pos >= _data.Length)
        {
            _fail = true;
            return -1;
        }
        return _data[_pos++];
    }

    public bool Fail => _fail;
    public long TellG() => _fail ? -1 : _pos;

    public void SeekG(long position)
    {
        if (_fail)
            return;
        if (position < 0)
        {
            _fail = true;
            return;
        }
        _pos = position;
    }
}

/// <summary>Reads one track of a standard MIDI file.</summary>
internal sealed class MidiTrack
{
    private readonly MidiStream _file;
    private readonly int _trackNumber;

    private long _filePos;
    private uint _trackLength;
    private uint _trackLengthCounter;
    private readonly CircularQueue<MidiEvent>? _trackEventQueue;
    private byte _savedRunningStatus;
    private int _deltaTime;
    private int _currentTime;      // The current time (all the delta times added up)
    private MidiError _midiError;
    private string _trackName = "";
    // The place in the queue of the note on event of each channel and note that has not ended yet
    private readonly int[]?[] _noteOnEventSlot = new int[]?[Midi.MaxMidiChannels];

    public MidiTrack(MidiStream file, int no)
    {
        _file = file;
        _trackNumber = no;
        _midiError = MidiError.NoError;

        _trackLengthCounter = 8;
        for (int i = 0; i < 4; i++)
        {
            if (_file.Get() != "MTrk"[i])
            {
                Log.Error("No valid MIDI tracks");
                ErrorFail(MidiError.CorruptedMidiFile);
                return;
            }
        }
        _trackLengthCounter = ReadDWord();

        _filePos = _file.TellG();
        _trackLength = _trackLengthCounter + 8; // 4 bytes for the "MTrk" + 4 bytes for the track length
        if (_trackLength > int.MaxValue)
        {
            Log.Error("The track length is too big.");
            ErrorFail(MidiError.TooManyTracks);
            return;
        }
        _trackEventQueue = new CircularQueue<MidiEvent>((int)(_trackLength / 3u)); // The minimum bytes per event is 3
    }

    private int ReadDelaTime()
    {
        int deltaTime = _deltaTime;
        _deltaTime = 0;
        return deltaTime;
    }

    public uint TrackLength => _trackLength;
    public bool Failed => _midiError != MidiError.NoError;
    public MidiError MidiError => _midiError;
    public int Length => _trackEventQueue?.Length ?? 0;

    public MidiEvent Pop(int trackNo)
    {
        MidiEvent m = _trackEventQueue!.Pop();
        m.Track = trackNo;
        return m;
    }

    public string TrackName => _trackName;

    private void ErrorFail(MidiError error)
    {
        if (_midiError != MidiError.NoError || error != MidiError.NoError)
        {
            _midiError = error;
            if (_midiError != MidiError.NoError)
                Log.Error($"MIDI error {(int)_midiError}");
        }
    }

    private byte ReadByte()
    {
        byte c;
        if (_trackLengthCounter != 0)
        {
            c = unchecked((byte)_file.Get());
            if (_file.Fail)
                ErrorFail(MidiError.EndOfFile);
            _trackLengthCounter--;
        }
        else
            c = 0;
        return c;
    }

    private ushort ReadWord()
    {
        ushort value = (ushort)(ReadByte() << 8);
        value |= ReadByte();
        return value;
    }

    private uint ReadDWord()
    {
        uint value = unchecked((uint)(ReadWord() << 16));
        value |= ReadWord();
        return value;
    }

    private uint ReadVarLen()
    {
        uint value;
        byte c;

        if (((value = ReadByte()) & 0x80) != 0)
        {
            value &= 0x7F;
            for (int i = 0; i < 4; i++)
            {
                value = unchecked((value << 7) + ((uint)(c = ReadByte()) & 0x7F));
                if (Failed)
                    break;
                if ((c & 0x80) == 0)
                    break;
                else if (i >= 3)
                    ErrorFail(MidiError.EndOfFile);
            }
        }
        return value;
    }

    private string ReadTextEvent()
    {
        uint length = ReadVarLen();
        var text = new List<byte>();
        if (length >= 1000)
        {
            Log.Error($"Text Event too large {length}");
            ErrorFail(MidiError.EndOfFile);
            return "";
        }
        while (length-- > 0)
        {
            if (Failed)
                break;
            text.Add(ReadByte());
        }
        // Like QString(text.c_str()): up to the first zero byte, as UTF-8
        int end = text.IndexOf(0);
        if (end >= 0)
            text.RemoveRange(end, text.Count - end);
        return Encoding.UTF8.GetString(text.ToArray());
    }

    private uint ReadDataEvent(int expectedLength)
    {
        int length = ReadByte();
        if (length != expectedLength)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return 0;
        }
        uint data = 0;
        while (length-- > 0)
        {
            data <<= 8;
            data |= ReadByte();
        }
        return data;
    }

    private void IgnoreSysexEvent(byte data)
    {
        if (data >= 127)
        {
            // this could be a variable length word
            Log.Warn($"SysexEvent, is too long {data}");
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }
        int length = data;
        while (length-- > 0)
        {
            if (Failed)
                return;
            ReadByte();
        }
    }

    // Time Signature
    private void ReadTimeSignatureEvent()
    {
        var midiEvent = new MidiEvent();
        uint len = ReadVarLen();
        if (len != 4)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }
        byte timeSigNumerator = ReadByte();   // The number on the top
        byte timeSigDenominator = ReadByte(); // the number on the bottom
        if (timeSigDenominator >= 5)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }
        if (timeSigNumerator > 20)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }
        ReadByte(); // Ignore the last bytes
        ReadByte();
        midiEvent.MetaEvent(ReadDelaTime(), Midi.PbTimeSignature, timeSigNumerator, 1 << timeSigDenominator);
        _trackEventQueue!.Push(midiEvent);
    }

    // Key Signature
    private void ReadKeySignatureEvent()
    {
        var midiEvent = new MidiEvent();
        uint len = ReadVarLen();
        if (len != 2)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }
        int keySig = unchecked((sbyte)ReadByte()); // force sign conversion The key sig 0=middle C
        int majorKey = ReadByte(); // Major or Minor
        if (keySig > 7 || keySig < -7)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }

        midiEvent.MetaEvent(ReadDelaTime(), Midi.PbKeySignature, keySig, majorKey);
        _trackEventQueue!.Push(midiEvent);
        if (StavePos.KeySignature == StavePos.NotUsed)
            StavePos.SetKeySignature(midiEvent.Data1, midiEvent.Data2);
    }

    private void ReadMetaEvent(byte type)
    {
        if (Failed)
            return;

        var midiEvent = new MidiEvent();

        switch (type)
        {
            case Midi.MetaSeqN:             // Sequence Number
                ReadDataEvent(2);
                break;

            case Midi.MetaCopyright:        // Copyright Notice
                ReadTextEvent();
                break;

            case Midi.MetaTrackName:        // Seq/Track Name
                _trackName = ReadTextEvent();
                break;

            case Midi.MetaInstrumentName:   // Instrument
                ReadTextEvent();
                break;

            case Midi.MetaEndOfTrack:       // End of Track
                ReadVarLen();
                break;

            case Midi.MetaTempo:            // Set Tempo
            {
                uint b1 = ReadVarLen();
                if (b1 != 3)
                {
                    ErrorFail(MidiError.CorruptedMidiFile);
                    break;
                }
                b1 = ReadByte();
                byte b2 = ReadByte();
                byte b3 = ReadByte();
                uint tempo = b1 << 16 | (uint)(b2 << 8) | b3; // microseconds per quarter-note
                midiEvent.MetaEvent(ReadDelaTime(), Midi.PbTempo, unchecked((int)tempo), 0);
                _trackEventQueue!.Push(midiEvent);
                break;
            }

            case Midi.MetaTimeSignature:    // Time Signature
                ReadTimeSignatureEvent();
                break;

            case Midi.MetaKeySignature:     // Key Signature
                ReadKeySignatureEvent();
                break;

            case Midi.MetaText:             // Text Event
            case Midi.MetaLyric:            // Lyric
            case Midi.MetaMarker:
            case Midi.MetaCuePoint:
            case Midi.MetaSmpteOffset:      // SMPTE Offset
            case Midi.MetaSequencerEvent:   // sequencer Specific
            case 0x09:                      // meta type
                ReadTextEvent();
                break;

            case Midi.MetaChannelPrefix:    // MIDI Channel Prefix
            case 0x21:                      // MIDI Port
                ReadDataEvent(1);
                break;

            default:                        // Unknown meta type
            {
                string text = ReadTextEvent();
                Log.Warn($"unknown midi meta event 0x{type:x2} {text}");
                break;
            }
        }
    }

    private void DecodeSystemMessage(byte status, byte data1)
    {
        switch (status)
        {
            case Midi.SysexEvent:   // System exclusive Transmitted
            case 0xf7:              // System exclusive Not transmitted
                IgnoreSysexEvent(data1);
                break;

            case Midi.MetaEvent:
                ReadMetaEvent(data1);
                break;

            default:
                IgnoreSysexEvent(data1);
                ErrorFail(MidiError.UnknownEvent);
                break;
        }
    }

    // The note on event holds its start time until the note ends, then its duration
    private void CloseNoteDuration(int slot)
    {
        if (slot >= 0)
        {
            ref MidiEvent noteOn = ref _trackEventQueue!.SlotRef(slot);
            noteOn.Duration = _currentTime - noteOn.Duration;
        }
    }

    // Notes that never got a note off end with the track
    private void CloseOpenNotes()
    {
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            int[]? slots = _noteOnEventSlot[chan];
            if (slots == null)
                continue;
            for (int pitch = 0; pitch < Midi.MaxMidiNotes; pitch++)
            {
                CloseNoteDuration(slots[pitch]);
                slots[pitch] = -1;
            }
        }
    }

    private int[] CreateNoteEventSlots(int channel)
    {
        int[]? slots = _noteOnEventSlot[channel];
        if (slots == null)
        {
            slots = new int[Midi.MaxMidiNotes];
            for (int pitch = 0; pitch < Midi.MaxMidiNotes; pitch++)
                slots[pitch] = -1;
            _noteOnEventSlot[channel] = slots;
        }
        return slots;
    }

    private void NoteOffEvent(ref MidiEvent midiEvent, int deltaTime, int channel, int pitch, int velocity)
    {
        int[] slots = CreateNoteEventSlots(channel);

        int noteOnSlot = slots[pitch];
        if (noteOnSlot >= 0)
            CloseNoteDuration(noteOnSlot);
        else
            Log.Warn($"Missing note off duration Chan {channel + 1} Note off {pitch}");
        slots[pitch] = -1;

        midiEvent.NoteOffEvent(deltaTime, channel, pitch, velocity);
        _trackEventQueue!.Push(midiEvent);
    }

    private void DecodeMidiEvent()
    {
        var midiEvent = new MidiEvent();
        byte status, data1, data2;

        uint readDeltaTicks = ReadVarLen();
        if (readDeltaTicks > int.MaxValue)
        {
            ErrorFail(MidiError.CorruptedMidiFile);
            return;
        }
        int deltaTicks = (int)readDeltaTicks;
        _deltaTime += deltaTicks;
        _currentTime += deltaTicks;

        byte c = ReadByte();
        if ((c & 0x80) == 0)
        {
            status = _savedRunningStatus;
            data1 = c;
        }
        else
        {
            _savedRunningStatus = status = c;
            data1 = ReadByte();
            if ((data1 & 0x80) != 0)
            {
                ErrorFail(MidiError.CorruptedMidiFile);
                return;
            }
        }

        int channel = status & 0x0f;

        switch (status & 0xf0)
        {
            case Midi.NoteOff:              // Note off
                data2 = ReadByte();
                NoteOffEvent(ref midiEvent, ReadDelaTime(), channel, data1, data2);
                break;

            case Midi.NoteOn:               // Note on
                data2 = ReadByte();
                if (data2 != 0)
                {
                    midiEvent.NoteOnEvent(ReadDelaTime(), channel, data1, data2);
                    midiEvent.Duration = _currentTime; // Set the duration to the current time for now

                    int slot = _trackEventQueue!.Push(midiEvent);

                    // Save a reference to the note on event
                    int[] slots = CreateNoteEventSlots(channel);
                    // The same note is struck again without a note off, so the previous one ends here
                    // (otherwise it keeps the start time as its duration)
                    CloseNoteDuration(slots[data1]);
                    slots[data1] = slot;
                }
                else
                {
                    NoteOffEvent(ref midiEvent, ReadDelaTime(), channel, data1, 0);
                }
                break;

            case Midi.NotePressure:         // Key pressure After touch (POLY_AFTERTOUCH) 3 bytes
                data2 = ReadByte();
                midiEvent.NotePressureEvent(ReadDelaTime(), channel, data1, data2);
                _trackEventQueue!.Push(midiEvent);
                break;

            case Midi.ProgramChange:        // program change
                midiEvent.ProgramChangeEvent(ReadDelaTime(), channel, data1);
                _trackEventQueue!.Push(midiEvent);
                break;

            case Midi.ControlChange:        // Control Change
                data2 = ReadByte();
                midiEvent.ControlChangeEvent(ReadDelaTime(), channel, data1, data2);
                _trackEventQueue!.Push(midiEvent);
                break;

            case Midi.ChannelPressure:      // Channel Pressure (AFTERTOUCH)
                midiEvent.ChannelPressureEvent(ReadDelaTime(), channel, data1);
                _trackEventQueue!.Push(midiEvent);
                break;

            case Midi.PitchBend:            // Pitch bend
                data2 = ReadByte();
                midiEvent.PitchBendEvent(ReadDelaTime(), channel, data1, data2);
                _trackEventQueue!.Push(midiEvent);
                break;

            case Midi.SysexEvent:           // System EX
                DecodeSystemMessage(status, data1);
                _savedRunningStatus = 0;
                break;
        }
    }

    public void DecodeTrack()
    {
        // The C++ code crashes on a track that failed in the constructor (no "MTrk"),
        // here the track is just left empty
        if (_trackEventQueue == null)
            return;

        _file.SeekG(_filePos);
        while (true)
        {
            if (_trackLengthCounter == 0)
            {
                CloseOpenNotes();
                break;
            }
            if (_trackEventQueue.Space <= 1)
            {
                Log.Error("Out of Space");
                break;
            }
            DecodeMidiEvent();
            if (Failed)
                break;
        }
        _filePos = _file.TellG();
    }
}
