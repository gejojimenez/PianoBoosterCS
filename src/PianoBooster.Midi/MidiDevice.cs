// Ported from MidiDevice.h and MidiDevice.cpp of PianoBooster (C++). Copyright (c) 2008-2020,
// L. J. Barman and others. GPLv3.

using System;
using System.Collections.Generic;
using PianoBooster.Core;

namespace PianoBooster.Midi;

/// <summary>The MIDI device of the conductor (the C++ CMidiDevice): it chooses between the MIDI
/// ports of the system and the internal synthesizer by the name of the port.</summary>
/// <remarks>Not thread safe: call it from the thread of the conductor, or with the lock of
/// <see cref="ConductorThread"/>. The keyboard input arrives in another thread, in a queue.</remarks>
public sealed class MidiDevice : IMidiDevice, IDisposable
{
    private readonly MidiDeviceBase _rtMidiDevice;
    private readonly MidiDeviceBase? _fluidSynthMidiDevice;
    private MidiDeviceBase? _selectedMidiInputDevice;
    private MidiDeviceBase? _selectedMidiOutputDevice;
    private bool _validOutput;

    public MidiDevice(bool withInternalFluidSynth = true)
    {
        _rtMidiDevice = new MidiDeviceDryWet();
        if (withInternalFluidSynth)
            _fluidSynthMidiDevice = new MidiDeviceFluidSynth();
        _selectedMidiInputDevice = _rtMidiDevice;
        _selectedMidiOutputDevice = _rtMidiDevice;
        _validOutput = false;
    }

    /// <summary>Gives the settings to the devices (the C++ init() with its QSettings).</summary>
    public void Init(IPianoSettings settings)
    {
        _rtMidiDevice.Settings = settings;
        if (_fluidSynthMidiDevice != null)
            _fluidSynthMidiDevice.Settings = settings;
    }

    public List<string> GetMidiPortList(MidiType type)
    {
        var list = new List<string>();
        if (_fluidSynthMidiDevice != null)
            list.AddRange(_fluidSynthMidiDevice.GetMidiPortList(type));
        list.AddRange(_rtMidiDevice.GetMidiPortList(type));
        return list;
    }

    // The C++ code closes the input or the output of the *output* device and, when the input
    // opens, selects it as the output device (see PORTING.md). Here the input and the output are
    // kept apart, which is what the C++ code means (and does when only RtMidi is used).
    public bool OpenMidiPort(MidiType type, string portName)
    {
        CloseMidiPort(type, -1);

        if (type == MidiType.Input)
        {
            if (_rtMidiDevice.OpenMidiPort(type, portName))
            {
                _selectedMidiInputDevice = _rtMidiDevice;
                return true;
            }
        }
        else
        {
            _validOutput = false;

            if (_rtMidiDevice.OpenMidiPort(type, portName))
            {
                _selectedMidiOutputDevice = _rtMidiDevice;
                _validOutput = true;
                return true;
            }
            if (_fluidSynthMidiDevice != null && _fluidSynthMidiDevice.OpenMidiPort(type, portName))
            {
                _selectedMidiOutputDevice = _fluidSynthMidiDevice;
                _validOutput = true;
                return true;
            }
        }
        return false;
    }

    public void CloseMidiPort(MidiType type, int index)
    {
        if (type == MidiType.Input)
        {
            _selectedMidiInputDevice?.CloseMidiPort(type, index);
            _selectedMidiInputDevice = _rtMidiDevice; // the only device with an input
        }
        else
        {
            _validOutput = false;
            if (_selectedMidiOutputDevice == null)
                return;
            _selectedMidiOutputDevice.CloseMidiPort(type, index);
            _selectedMidiOutputDevice = null;
        }
    }

    //! add a midi event to be played immediately
    public void PlayMidiEvent(in MidiEvent midiEvent) => _selectedMidiOutputDevice?.PlayMidiEvent(midiEvent);

    // Return the number of events waiting to be read from the midi device
    public int CheckMidiInput() => _selectedMidiInputDevice?.CheckMidiInput() ?? 0;

    // reads the real midi event
    public MidiEvent ReadMidiInput() => _selectedMidiInputDevice?.ReadMidiInput() ?? new MidiEvent();

    public bool ValidMidiOutput() =>
        _validOutput && _selectedMidiOutputDevice != null && _selectedMidiOutputDevice.ValidMidiConnection();

    public bool ValidMidiConnection() => ValidMidiOutput();

    public int MidiSettingsSetStr(string name, string str) => _selectedMidiOutputDevice?.MidiSettingsSetStr(name, str) ?? 0;
    public int MidiSettingsSetNum(string name, double val) => _selectedMidiOutputDevice?.MidiSettingsSetNum(name, val) ?? 0;
    public int MidiSettingsSetInt(string name, int val) => _selectedMidiOutputDevice?.MidiSettingsSetInt(name, val) ?? 0;
    public string MidiSettingsGetStr(string name) => _selectedMidiOutputDevice?.MidiSettingsGetStr(name) ?? "";
    public double MidiSettingsGetNum(string name) => _selectedMidiOutputDevice?.MidiSettingsGetNum(name) ?? 0.0;
    public int MidiSettingsGetInt(string name) => _selectedMidiOutputDevice?.MidiSettingsGetInt(name) ?? 0;

    public void FlushMidiInput()
    {
        while (CheckMidiInput() > 0)
            ReadMidiInput();
    }

    public void Dispose()
    {
        _rtMidiDevice.Dispose();
        _fluidSynthMidiDevice?.Dispose();
    }
}
