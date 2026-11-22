// The parts of CSettings and CMidiDevice of PianoBooster (C++) that the core uses, as
// interfaces, so that the core does not depend on the interface or on the MIDI hardware.

using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>The settings the core reads (a part of the C++ CSettings, which is a QSettings).</summary>
public interface IPianoSettings
{
    /// <summary>A stored value, like QSettings::value(key, default).toString().</summary>
    string GetString(string key, string defaultValue = "");
    /// <summary>A stored value, like QSettings::value(key, default).toInt().</summary>
    int GetInt(string key, int defaultValue = 0);

    void SetAdvancedMode(bool value);
    bool ShowNoteNames { get; }
    bool ColoredNotes { get; }
    bool FollowThroughErrors { get; }
    bool IsNoteDurationTrailEnabled { get; }
    void UpdateWarningMessages();
    /// <summary>Sets the channels of the hands (CSettings also keeps them for the song).</summary>
    void SetChannelHands(int left, int right);
}

/// <summary>Settings kept in memory, for the tests and for programs without an interface.</summary>
public sealed class MemorySettings : IPianoSettings
{
    private readonly Dictionary<string, string> _values = new();

    public bool AdvancedMode { get; private set; }
    public bool ShowNoteNames { get; set; }
    public bool ColoredNotes { get; set; }
    public bool FollowThroughErrors { get; set; }
    public bool IsNoteDurationTrailEnabled { get; set; }

    public void SetValue(string key, string value) => _values[key] = value;
    public string GetString(string key, string defaultValue = "") =>
        _values.TryGetValue(key, out string? value) ? value : defaultValue;
    public int GetInt(string key, int defaultValue = 0) =>
        _values.TryGetValue(key, out string? value) && int.TryParse(value, out int result) ? result : defaultValue;

    public void SetAdvancedMode(bool value) => AdvancedMode = value;
    public void UpdateWarningMessages() { }
    public void SetChannelHands(int left, int right) => Note.SetChannelHands(left, right);
}

public enum MidiType
{
    Input,
    Output,
}

/// <summary>A MIDI device for the conductor: where the events are played and where the notes of
/// the pianist come from (the C++ CMidiDevice). The implementations come in phase 2.</summary>
public interface IMidiDevice
{
    /// <summary>Plays an event immediately.</summary>
    void PlayMidiEvent(in MidiEvent midiEvent);
    /// <summary>The number of events waiting to be read.</summary>
    int CheckMidiInput();
    MidiEvent ReadMidiInput();
    bool ValidMidiOutput();
    bool OpenMidiPort(MidiType type, string portName);
}

/// <summary>The C++ CDraw statics that are not about drawing: the hand shown and whether the
/// score must be drawn again.</summary>
public static class DrawState
{
    public const float HorizontalSpacingFactor = 0.75f; // defines the speed of the scrolling

    private static WhichPart s_displayHand = WhichPart.Both;
    private static int s_forceCompileRedraw = 1;

    public static WhichPart DisplayHand => s_displayHand;

    public static void SetDisplayHand(WhichPart hand)
    {
        s_displayHand = hand;
        s_forceCompileRedraw = 1;
    }

    public static void ForceCompileRedraw(int value = 1) => s_forceCompileRedraw = value;
    public static int CompileRedrawCount => s_forceCompileRedraw;

    /// <summary>The length on the screen (in pixels) of the trail that shows how long a note sounds.</summary>
    public static float NoteDurationTrailLength(int midiDuration)
    {
        // a safety limit for broken MIDI files: 16 quarter notes (four bars of 4/4)
        int maxDuration = 16 * MidiFile.PulsesPerQuarterNote;

        if (midiDuration <= 0)
            return 0.0f;
        if (midiDuration > maxDuration)
            midiDuration = maxDuration;
        // the same scale the score scrolls with (see Scroll's note spacing factor)
        return (float)midiDuration * MidiFile.DefaultPpqn / (float)MidiFile.PulsesPerQuarterNote
               * HorizontalSpacingFactor;
    }

    internal static void ResetStatics()
    {
        s_displayHand = WhichPart.Both;
        s_forceCompileRedraw = 1;
    }
}

/// <summary>Puts all the statics of the core back to the values of a new process, so that tests
/// (or a second song player in the same process) start from the same state as the C++ program.</summary>
public static class CoreStatics
{
    public static void Reset()
    {
        Cfg.ResetStatics();
        MidiFile.ResetStatics();
        Note.ResetStatics();
        Chord.ResetStatics();
        Tempo.ResetStatics();
        StavePos.ResetStatics();
        Notation.ResetStatics();
        DrawState.ResetStatics();
        Conductor.ResetStatics();
        Scroll.ResetStatics();
    }
}
