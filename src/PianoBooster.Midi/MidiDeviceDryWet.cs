// Ported from MidiDeviceRt.h and MidiDeviceRt.cpp of PianoBooster (C++). Copyright (c) 2008-2013,
// L. J. Barman. GPLv3. RtMidi is replaced by DryWetMIDI (Windows and macOS).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using PianoBooster.Core;
using MidiEvent = PianoBooster.Core.MidiEvent;

namespace PianoBooster.Midi;

using Midi = PianoBooster.Core.Midi; // the class of constants, not this namespace

/// <summary>The MIDI ports of the system (the C++ CMidiDeviceRt, which uses RtMidi).
/// Port names have the form "index - name", as in the C++ code, so that saved settings still work.</summary>
public sealed class MidiDeviceDryWet : MidiDeviceBase
{
    private InputDevice? _midiIn;
    private OutputDevice? _midiOut;

    // 0 for input, 1 for output
    private readonly int[] _midiPorts = { -1, -1 }; // the index of the port open
    private bool _validConnection;

    // The input arrives in the thread of DryWetMIDI and waits here for the conductor
    private readonly ConcurrentQueue<byte[]> _inputQueue = new();
    private byte[] _inputMessage = Array.Empty<byte>();
    private readonly MidiEventToBytesConverter _inputConverter = new();
    private readonly object _inputConverterLock = new();

    private readonly BytesToMidiEventConverter _outputConverter = new();
    private readonly byte[] _message = new byte[3];
    private readonly byte[] _savedRawBytes = new byte[40]; // Raw data is used for used for a SYSTEM_EVENT
    private int _rawDataIndex;

    public MidiDeviceDryWet()
    {
        Init();
    }

    public override void Init()
    {
    }

    private static string AddIndexToString(string name, int index) => index + " - " + name;

    /// <summary>The names of the ports of a type, or an empty list if MIDI cannot be used.</summary>
    public override List<string> GetMidiPortList(MidiType type)
    {
        var portNameList = new List<string>();
        try
        {
            if (type == MidiType.Input)
            {
                int i = 0;
                foreach (InputDevice device in InputDevice.GetAll())
                {
                    AddPortName(portNameList, device.Name, i++);
                    if (device != _midiIn)
                        device.Dispose();
                }
            }
            else
            {
                int i = 0;
                foreach (OutputDevice device in OutputDevice.GetAll())
                {
                    AddPortName(portNameList, device.Name, i++);
                    if (device != _midiOut)
                        device.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is MidiDeviceException or PlatformNotSupportedException or DllNotFoundException or TypeInitializationException)
        {
            Log.Warn("MIDI ports: " + ex.Message);
        }
        return portNameList;
    }

    private static void AddPortName(List<string> list, string deviceName, int index)
    {
        string name = AddIndexToString(deviceName, index);
        if (name.Contains("RtMidi Output Client") || name.Contains("RtMidi Input Client"))
            return;
        list.Add(name);
    }

    public override bool OpenMidiPort(MidiType type, string portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        int dev = type == MidiType.Input ? 0 : 1;
        try
        {
            int count = type == MidiType.Input ? InputDevice.GetDevicesCount() : OutputDevice.GetDevicesCount();
            for (int i = 0; i < count; i++)
            {
                if (type == MidiType.Input)
                {
                    InputDevice device = InputDevice.GetByIndex(i);
                    if (AddIndexToString(device.Name, i) != portName) // Test for a match
                    {
                        device.Dispose();
                        continue;
                    }
                    CloseInput();
                    _midiIn = device;
                    _midiIn.EventReceived += OnEventReceived;
                    _midiIn.StartEventsListening();
                }
                else
                {
                    OutputDevice device = OutputDevice.GetByIndex(i);
                    if (AddIndexToString(device.Name, i) != portName) // Test for a match
                    {
                        device.Dispose();
                        continue;
                    }
                    CloseOutput();
                    _midiOut = device;
                    _midiOut.PrepareForEventsSending();
                }
                _midiPorts[dev] = i;
                _rawDataIndex = 0;
                if (type == MidiType.Output)
                    _validConnection = true;
                return true;
            }
        }
        catch (Exception ex) when (ex is MidiDeviceException or PlatformNotSupportedException or DllNotFoundException or TypeInitializationException)
        {
            Log.Warn($"Cannot open the MIDI port {portName}: {ex.Message}");
        }
        return false;
    }

    public override void CloseMidiPort(MidiType type, int index)
    {
        if (type == MidiType.Input)
            CloseInput();
        else
            CloseOutput();
    }

    private void CloseInput()
    {
        if (_midiIn != null)
        {
            _midiIn.EventReceived -= OnEventReceived;
            try
            {
                _midiIn.StopEventsListening();
            }
            catch (MidiDeviceException)
            {
            }
            _midiIn.Dispose();
            _midiIn = null;
        }
        _midiPorts[0] = -1;
        _inputQueue.Clear();
        _inputMessage = Array.Empty<byte>();
    }

    private void CloseOutput()
    {
        _validConnection = false;
        if (_midiOut != null)
        {
            _midiOut.Dispose();
            _midiOut = null;
        }
        _midiPorts[1] = -1;
    }

    public override bool ValidMidiConnection() => _validConnection;

    //! add a midi event to be played immediately
    public override void PlayMidiEvent(in MidiEvent midiEvent)
    {
        if (_midiPorts[1] < 0 || _midiOut == null)
            return;

        byte[] message;
        switch (midiEvent.Type)
        {
            case Midi.PbCollateRawMidiData: // used for a SYSTEM_EVENT
                if (_rawDataIndex < _savedRawBytes.Length)
                    _savedRawBytes[_rawDataIndex++] = (byte)midiEvent.Data1;
                return; // Don't output any thing yet so just return

            case Midi.PbOutputRawMidiData: // used for a SYSTEM_EVENT
                message = _savedRawBytes.AsSpan(0, _rawDataIndex).ToArray();
                _rawDataIndex = 0;
                break;

            default:
                int length = BytesFromEvent(midiEvent, _message);
                if (length == 0)
                    return;
                message = length == _message.Length ? _message : _message.AsSpan(0, length).ToArray();
                break;
        }
        if (message.Length == 0)
            return;

        try
        {
            _midiOut.SendEvent(_outputConverter.Convert(message));
        }
        catch (Exception ex)
        {
            Log.Warn("MIDI output: " + ex.Message);
            _validConnection = false;
        }
    }

    // Called by DryWetMIDI in its own thread
    private void OnEventReceived(object? sender, MidiEventReceivedEventArgs e)
    {
        byte[] bytes;
        lock (_inputConverterLock)
            bytes = _inputConverter.Convert(e.Event);
        // like RtMidi by default: ignore system exclusive, timing and active sensing messages
        if (bytes.Length == 0 || bytes[0] >= 0xf0)
            return;
        _inputQueue.Enqueue(bytes);
    }

    // Return the number of events waiting to be read from the midi device
    public override int CheckMidiInput()
    {
        if (_midiPorts[0] < 0)
            return 0;

        if (_inputMessage.Length == 0 && _inputQueue.TryDequeue(out byte[]? message))
            _inputMessage = message;
        return _inputMessage.Length;
    }

    // reads the real midi event
    public override MidiEvent ReadMidiInput()
    {
        if (Cfg.MidiInputDump)
        {
            var str = new StringBuilder();
            foreach (byte b in _inputMessage)
                str.Append(" 0x").Append(b.ToString("x")).Append(',');
            Log.Info("midi input : " + str);
        }

        MidiEvent midiEvent = EventFromBytes(_inputMessage);
        _inputMessage = Array.Empty<byte>();
        return midiEvent;
    }

    public override void Dispose()
    {
        base.Dispose();
        _inputConverter.Dispose();
        _outputConverter.Dispose();
    }
}
