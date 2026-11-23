// Ported from MidiDeviceFluidSynth.h and MidiDeviceFluidSynth.cpp of PianoBooster (C++).
// Copyright (c) 2008-2020, L. J. Barman. GPLv3.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PianoBooster.Core;
using static PianoBooster.Midi.FluidSynthNative;

namespace PianoBooster.Midi;

using Midi = PianoBooster.Core.Midi; // the class of constants, not this namespace

/// <summary>The internal synthesizer, FluidSynth (the C++ CMidiDeviceFluidSynth). It only has an
/// output, and it is offered only when a SoundFont is set in "FluidSynth/SoundFont".</summary>
public sealed partial class MidiDeviceFluidSynth : MidiDeviceBase
{
    public const int FluidDefaultGain = 80;
    private const string FluidName = "(FluidSynth)";

    /// <summary>Translates "Internal Sound" (the application sets it).</summary>
    public static Func<string, string> Translate = text => text;

    public static string GetFluidInternalName() => Translate("Internal Sound") + " " + FluidName;

    private readonly byte[] _savedRawBytes = new byte[40]; // Raw data is used for used for a SYSTEM_EVENT
    private int _rawDataIndex;

    private IntPtr _fluidSettings;
    private IntPtr _synth;
    private IntPtr _audioDriver;
    private int _soundFontId;
    private bool _validConnection;
    private bool _timerPeriodRaised; // timeBeginPeriod(1) in effect (Windows)

    public override void Init()
    {
    }

    /// <summary>Whether the FluidSynth library is installed.</summary>
    public static bool IsLibraryAvailable => FluidSynthNative.IsAvailable;

    public override List<string> GetMidiPortList(MidiType type)
    {
        if (type != MidiType.Output) // Only has an output
            return new List<string>();

        if (Settings == null || !IsLibraryAvailable)
            return new List<string>();

        List<string> fontList = GetStringList(Settings, "FluidSynth/SoundFont");
        if (fontList.Count > 0)
            return new List<string> { GetFluidInternalName() };
        return fontList;
    }

    public override bool OpenMidiPort(MidiType type, string portName)
    {
        CloseMidiPort(MidiType.Output, -1);

        if (string.IsNullOrEmpty(portName))
            return false;

        if (type == MidiType.Input)
            return false;

        if (!portName.EndsWith(FluidName, StringComparison.Ordinal))
            return false;

        if (GetMidiPortList(type).Count == 0)
            return false;

        // Load a SoundFont
        List<string> fontList = GetStringList(Settings, "FluidSynth/SoundFont");
        if (fontList.Count == 0)
            return false;
        IPianoSettings settings = Settings!;

        // Create the settings.
        _fluidSettings = new_fluid_settings();

        // Change the settings if necessary
        int sampleRate = settings.GetInt("FluidSynth/sampleRateCombo", 22050);
        int periodSize = settings.GetInt("FluidSynth/bufferSizeCombo", 128);
        int periods = settings.GetInt("FluidSynth/bufferCountCombo", 4);
        if (OperatingSystem.IsWindows())
        {
            int safePeriods = DirectSoundSafePeriods(sampleRate, periodSize, periods);
            if (safePeriods != periods)
                Log.Debug($"FluidSynth: {periodSize} x {periods} at {sampleRate} Hz is too short for DirectSound, using {periodSize} x {safePeriods}");
            periods = safePeriods;
        }
        fluid_settings_setnum(_fluidSettings, "synth.sample-rate", sampleRate);
        fluid_settings_setint(_fluidSettings, "audio.period-size", periodSize);
        fluid_settings_setint(_fluidSettings, "audio.periods", periods);

        if (!OperatingSystem.IsWindows())
            fluid_settings_setstr(_fluidSettings, "audio.driver", settings.GetString("FluidSynth/audioDriverCombo", "pulseaudio"));

        // Create the synthesizer.
        _synth = new_fluid_synth(_fluidSettings);
        try
        {
            fluid_synth_reverb_on(_synth, -1, 0);
            fluid_synth_chorus_on(_synth, -1, 0);
        }
        catch (EntryPointNotFoundException) // before FluidSynth 2.2
        {
            fluid_synth_set_reverb_on(_synth, 0);
            fluid_synth_set_chorus_on(_synth, 0);
        }

        // FluidSynth's DirectSound thread refills its ring buffer after a 1 ms timed wait. With the
        // default Windows timer resolution (15.6 ms) that wait lasts far longer and the small default
        // buffers (128 x 4) run dry. The C++ version gets a 1 ms resolution as a side effect of Qt's
        // precise timers (timeSetEvent); here it is asked for explicitly while the synth is open.
        if (OperatingSystem.IsWindows() && !_timerPeriodRaised)
            _timerPeriodRaised = timeBeginPeriod(1) == 0; // TIMERR_NOERROR

        // Create the audio driver.
        _audioDriver = new_fluid_audio_driver(_fluidSettings, _synth);
        if (_audioDriver == IntPtr.Zero)
            Log.Warn("FluidSynth: cannot create the audio driver");

        string pathName = fontList[0];
        Log.Debug($"Sound font {pathName}");
        _soundFontId = fluid_synth_sfload(_synth, pathName, 0);
        if (_soundFontId == FluidFailed)
            return false;

        for (int channel = 0; channel < Midi.MaxMidiChannels; channel++)
            fluid_synth_program_change(_synth, channel, Midi.GmPianoPatch);

        float gain = float.TryParse(settings.GetString("FluidSynth/masterGainSpin", FluidDefaultGain.ToString(CultureInfo.InvariantCulture)),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out float g) ? g : 0.0f; // QVariant::toFloat()
        fluid_synth_set_gain(_synth, gain / 100.0f);
        _validConnection = true;
        if (Cfg.LogLevel >= Cfg.LogLevelDebug)
            DebugSettings(_fluidSettings);
        return true;
    }

    /// <summary>The shortest ring buffer FluidSynth's DirectSound driver is given. DirectSound moves its
    /// play cursor in 10 ms steps: measured with a 1 ms timer resolution, rings of 20 ms or less ran
    /// dry (128 x 4 at 44100 or 48000 Hz, 128 x 15 at 96000 Hz), 20.3 to 21.3 ms played with little
    /// margin, and 23.2 ms (the default 128 x 4 at 22050 Hz) and longer played correctly.</summary>
    public const double DirectSoundMinBufferMs = 23.0;

    private const int FluidMaxPeriods = 64; // the range of "audio.periods" is 2..64

    /// <summary>The number of periods to use so that periodSize x periods lasts at least
    /// <see cref="DirectSoundMinBufferMs"/>; never fewer than asked for, nor more than FluidSynth accepts.</summary>
    public static int DirectSoundSafePeriods(int sampleRate, int periodSize, int periods)
    {
        if (sampleRate <= 0 || periodSize <= 0)
            return periods;
        int needed = (int)Math.Ceiling(DirectSoundMinBufferMs * sampleRate / (1000.0 * periodSize));
        return Math.Max(periods, Math.Min(needed, FluidMaxPeriods));
    }

    private static unsafe void DebugSettings(IntPtr settings)
    {
        GCHandle handle = GCHandle.Alloc(settings);
        try
        {
            fluid_settings_foreach(settings, GCHandle.ToIntPtr(handle), &DebugSettingsForeach);
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void DebugSettingsForeach(IntPtr data, byte* name, int type)
    {
        var settings = (IntPtr)GCHandle.FromIntPtr(data).Target!;
        byte* buffer = stackalloc byte[300];
        buffer[0] = 0;
        fluid_settings_copystr(settings, name, buffer, 300);
        Log.Debug($"settings_foreach_func {Marshal.PtrToStringUTF8((IntPtr)name)} : {Marshal.PtrToStringUTF8((IntPtr)buffer)}");
    }

    public override void CloseMidiPort(MidiType type, int index)
    {
        _validConnection = false;

        if (type != MidiType.Output)
            return;

        if (_fluidSettings == IntPtr.Zero)
            return;

        /* Clean up */
        if (_audioDriver != IntPtr.Zero)
            delete_fluid_audio_driver(_audioDriver);
        delete_fluid_synth(_synth);
        delete_fluid_settings(_fluidSettings);
        _audioDriver = IntPtr.Zero;
        _synth = IntPtr.Zero;
        _fluidSettings = IntPtr.Zero;
        _rawDataIndex = 0;

        if (_timerPeriodRaised)
        {
            timeEndPeriod(1);
            _timerPeriodRaised = false;
        }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);

    public override bool ValidMidiConnection() => _validConnection;

    //! add a midi event to be played immediately
    public override void PlayMidiEvent(in MidiEvent midiEvent)
    {
        if (_synth == IntPtr.Zero)
            return;

        int channel = midiEvent.Channel & 0x0f;
        switch (midiEvent.Type)
        {
            case Midi.NoteOff:
                fluid_synth_noteoff(_synth, channel, midiEvent.Note);
                break;
            case Midi.NoteOn:
                fluid_synth_noteon(_synth, channel, midiEvent.Note, midiEvent.Velocity);
                break;

            case Midi.NotePressure: // POLY_AFTERTOUCH: 3 bytes
                break;

            case Midi.ControlChange:
                fluid_synth_cc(_synth, channel, midiEvent.Data1, midiEvent.Data2);
                break;

            case Midi.ProgramChange:
                fluid_synth_program_change(_synth, channel, midiEvent.Programme);
                break;

            case Midi.ChannelPressure: // AFTERTOUCH: 2 bytes only
                fluid_synth_channel_pressure(_synth, channel, midiEvent.Programme);
                break;

            case Midi.PitchBend:
                // a 14 bit number LSB first 0x4000 is the off positions
                fluid_synth_pitch_bend(_synth, channel, (midiEvent.Data2 << 7) | midiEvent.Data1);
                break;

            case Midi.PbCollateRawMidiData: // used for a SYSTEM_EVENT
                if (_rawDataIndex < _savedRawBytes.Length)
                    _savedRawBytes[_rawDataIndex++] = (byte)midiEvent.Data1;
                return; // Don't output any thing yet so just return

            case Midi.PbOutputRawMidiData: // used for a SYSTEM_EVENT
                _rawDataIndex = 0;
                // FIXME missing (as in the C++ code)
                break;
        }
    }

    // Return the number of events waiting to be read from the midi device
    public override int CheckMidiInput() => 0;

    // reads the real midi event
    public override MidiEvent ReadMidiInput() => new();

    public override int MidiSettingsSetStr(string name, string str) =>
        _fluidSettings == IntPtr.Zero ? 0 : fluid_settings_setstr(_fluidSettings, name, str);

    public override int MidiSettingsSetNum(string name, double val) =>
        _fluidSettings == IntPtr.Zero ? 0 : fluid_settings_setnum(_fluidSettings, name, val);

    public override int MidiSettingsSetInt(string name, int val) =>
        _fluidSettings == IntPtr.Zero ? 0 : fluid_settings_setint(_fluidSettings, name, val);

    // the C++ code always returns an empty string (its buffer was never filled)
    public override string MidiSettingsGetStr(string name) => "";

    public override double MidiSettingsGetNum(string name)
    {
        if (_fluidSettings == IntPtr.Zero)
            return 0.0;
        fluid_settings_getnum(_fluidSettings, name, out double val);
        return val;
    }

    public override int MidiSettingsGetInt(string name)
    {
        if (_fluidSettings == IntPtr.Zero)
            return 0;
        fluid_settings_getint(_fluidSettings, name, out int val);
        return val;
    }
}
