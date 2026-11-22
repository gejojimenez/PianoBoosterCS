// Ported from MidiDeviceBase.h of PianoBooster (C++). Copyright (c) 2008-2020, L. J. Barman and others. GPLv3.

using System;
using System.Collections.Generic;
using PianoBooster.Core;

namespace PianoBooster.Midi;

using Midi = PianoBooster.Core.Midi; // the class of constants, not this namespace

/// <summary>A kind of MIDI device: the MIDI ports of the system or the internal synthesizer
/// (the C++ CMidiDeviceBase).</summary>
public abstract class MidiDeviceBase : IDisposable
{
    /// <summary>The settings (the C++ setQSettings()). FluidSynth reads its "FluidSynth/*" keys.</summary>
    public IPianoSettings? Settings { get; set; }

    public abstract void Init();
    /// <summary>Plays an event immediately.</summary>
    public abstract void PlayMidiEvent(in MidiEvent midiEvent);
    /// <summary>The number of bytes of the next event waiting to be read (0 when there is none).</summary>
    public abstract int CheckMidiInput();
    public abstract MidiEvent ReadMidiInput();

    public abstract List<string> GetMidiPortList(MidiType type);
    public abstract bool OpenMidiPort(MidiType type, string portName);
    /// <summary>Whether the output can be used.</summary>
    public abstract bool ValidMidiConnection();
    public abstract void CloseMidiPort(MidiType type, int index);

    // based on the fluid synth settings
    public virtual int MidiSettingsSetStr(string name, string str) => 0;
    public virtual int MidiSettingsSetNum(string name, double val) => 0;
    public virtual int MidiSettingsSetInt(string name, int val) => 0;
    public virtual string MidiSettingsGetStr(string name) => "";
    public virtual double MidiSettingsGetNum(string name) => 0.0;
    public virtual int MidiSettingsGetInt(string name) => 0;

    public virtual void Dispose()
    {
        CloseMidiPort(MidiType.Input, -1);
        CloseMidiPort(MidiType.Output, -1);
        GC.SuppressFinalize(this);
    }

    /// <summary>A list kept in one setting (a QStringList in the C++ QSettings): the items are
    /// separated by '|', which cannot appear in a Windows path.</summary>
    public static List<string> GetStringList(IPianoSettings? settings, string key)
    {
        var list = new List<string>();
        if (settings == null)
            return list;
        foreach (string item in settings.GetString(key).Split('|'))
            if (item.Length > 0)
                list.Add(item);
        return list;
    }

    /// <summary>Converts the bytes of a MIDI message of the keyboard into an event, like
    /// CMidiDeviceRt::readMidiInput(). Messages that are not channel messages give an empty event.</summary>
    public static MidiEvent EventFromBytes(ReadOnlySpan<byte> message)
    {
        var midiEvent = new MidiEvent();
        if (message.Length == 0)
            return midiEvent;

        int b1 = message.Length > 1 ? message[1] : 0;
        int b2 = message.Length > 2 ? message[2] : 0;
        int Byte(int i) => i == 1 ? b1 : b2;
        int channel = message[0] & 0x0f;
        switch (message[0] & 0xf0)
        {
            case Midi.NoteOn:
                if (Byte(2) != 0)
                    midiEvent.NoteOnEvent(0, channel, Byte(1), Byte(2));
                else
                    midiEvent.NoteOffEvent(0, channel, Byte(1), Byte(2));
                break;

            case Midi.NoteOff:
                midiEvent.NoteOffEvent(0, channel, Byte(1), Byte(2));
                break;

            case Midi.NotePressure: // POLY_AFTERTOUCH
                midiEvent.NotePressureEvent(0, channel, Byte(1), Byte(2));
                break;

            case Midi.ControlChange:
                midiEvent.ControlChangeEvent(0, channel, Byte(1), Byte(2));
                break;

            case Midi.ProgramChange:
                midiEvent.ProgramChangeEvent(0, channel, Byte(1));
                break;

            case Midi.ChannelPressure: // AFTERTOUCH
                midiEvent.ChannelPressureEvent(0, channel, Byte(1));
                break;

            case Midi.PitchBend:
                midiEvent.PitchBendEvent(0, channel, Byte(1), Byte(2));
                break;
        }
        return midiEvent;
    }

    /// <summary>The bytes of a channel message for an event, like CMidiDeviceRt::playMidiEvent().
    /// Returns 0 for the events that are not sent.</summary>
    public static int BytesFromEvent(in MidiEvent midiEvent, Span<byte> message)
    {
        int channel = midiEvent.Channel & 0x0f;
        switch (midiEvent.Type)
        {
            case Midi.NoteOff:
            case Midi.NoteOn:
                message[0] = (byte)(channel | midiEvent.Type);
                message[1] = (byte)midiEvent.Note;
                message[2] = (byte)midiEvent.Velocity;
                return 3;

            case Midi.NotePressure: // POLY_AFTERTOUCH: 3 bytes
            case Midi.ControlChange:
            case Midi.PitchBend:
                message[0] = (byte)(channel | midiEvent.Type);
                message[1] = (byte)midiEvent.Data1;
                message[2] = (byte)midiEvent.Data2;
                return 3;

            case Midi.ProgramChange:
                message[0] = (byte)(channel | Midi.ProgramChange);
                message[1] = (byte)midiEvent.Programme;
                return 2;

            case Midi.ChannelPressure: // AFTERTOUCH: 2 bytes only
                message[0] = (byte)(channel | Midi.ChannelPressure);
                message[1] = (byte)midiEvent.Data1;
                return 2;
        }
        return 0;
    }
}
