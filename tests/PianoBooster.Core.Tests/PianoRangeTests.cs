using System.Collections.Generic;
using Xunit;

namespace PianoBooster.Core.Tests;

/// <summary>The notes that can be played (the others are drawn dimmed) follow the input in use: the
/// PC keys when no MIDI keyboard is open, whatever the language and whether the song was just
/// started or the input was changed.</summary>
public class PianoRangeTests
{
    private sealed class Device : IMidiDevice
    {
        public readonly HashSet<string> Connected = new();
        public bool Output;
        public void PlayMidiEvent(in MidiEvent midiEvent) { }
        public int CheckMidiInput() => 0;
        public MidiEvent ReadMidiInput() => new();
        public bool ValidMidiOutput() => Output;
        public bool OpenMidiPort(MidiType type, string portName)
        {
            if (type == MidiType.Output)
                return Output = portName.Length > 0;
            return Connected.Contains(portName);
        }
    }

    private static (Song, MemorySettings, Device) Create(string input)
    {
        CoreStatics.Reset();
        var settings = new MemorySettings();
        settings.SetValue("Midi/Input", input);
        settings.SetValue("Midi/Output", "Synth");
        settings.SetValue("Keyboard/LowestNote", "36");
        settings.SetValue("Keyboard/HighestNote", "96");
        var device = new Device();
        var song = new Song { MidiDevice = device };
        song.Init2(new Score(settings), settings);
        return (song, settings, device);
    }

    private static bool PcRange() =>
        !Chord.IsNotePlayable(Song.PcKeyLowestNote - 1, 0) && Chord.IsNotePlayable(Song.PcKeyLowestNote, 0) &&
        Chord.IsNotePlayable(Song.PcKeyHighestNote, 0) && !Chord.IsNotePlayable(Song.PcKeyHighestNote + 1, 0);

    private static bool KeyboardRange() =>
        !Chord.IsNotePlayable(35, 0) && Chord.IsNotePlayable(36, 0) &&
        Chord.IsNotePlayable(96, 0) && !Chord.IsNotePlayable(97, 0);

    [Theory]
    [InlineData("None (PC Keyboard)")]
    [InlineData("Ninguno (Teclado PC)")] // the C++ code compared with "None" before loading the translation
    [InlineData("")]
    public void AtStartWithoutMidiKeyboardThePcKeysAreUsed(string input)
    {
        var (song, _, _) = Create(input);
        song.ReconnectMidi();
        Assert.False(song.MidiInputOpen);
        Assert.True(PcRange());
    }

    [Fact]
    public void AtStartAChosenKeyboardThatIsNotConnectedMeansThePcKeys()
    {
        var (song, _, _) = Create("1 - Piano USB");
        song.ReconnectMidi();
        Assert.False(song.MidiInputOpen);
        Assert.True(PcRange());
    }

    [Fact]
    public void AtStartAConnectedKeyboardUsesItsRange()
    {
        var (song, _, device) = Create("1 - Piano USB");
        device.Connected.Add("1 - Piano USB");
        song.ReconnectMidi();
        Assert.True(song.MidiInputOpen);
        Assert.True(KeyboardRange());
    }

    [Fact]
    public void ChangingTheInputChangesTheRange()
    {
        var (song, settings, device) = Create("Ninguno (Teclado PC)");
        song.ReconnectMidi();
        Assert.True(PcRange());

        device.Connected.Add("1 - Piano USB");
        song.OpenMidiPort(MidiType.Input, "1 - Piano USB");
        Assert.True(KeyboardRange());

        settings.SetValue("Keyboard/HighestNote", "84");
        song.UpdatePianoRange();
        Assert.True(Chord.IsNotePlayable(84, 0) && !Chord.IsNotePlayable(85, 0));

        song.OpenMidiPort(MidiType.Input, "Ninguno (Teclado PC)");
        Assert.True(PcRange());
    }
}

/// <summary>Each MIDI keyboard has its own range (kept by the application in its library), and the
/// notes pressed on it can be watched to find that range.</summary>
public class KeyboardRangeTests
{
    private sealed class Settings : IPianoSettings
    {
        public readonly Dictionary<string, (int, int)> Ranges = new();
        public string GetString(string key, string defaultValue = "") => defaultValue;
        public int GetInt(string key, int defaultValue = 0) => defaultValue;
        public void SetAdvancedMode(bool value) { }
        public bool ShowNoteNames => false;
        public bool ColoredNotes => false;
        public bool FollowThroughErrors => false;
        public bool IsNoteDurationTrailEnabled => false;
        public void UpdateWarningMessages() { }
        public void SetChannelHands(int left, int right) => Note.SetChannelHands(left, right);
        public (int Lowest, int Highest) GetKeyboardRange(string inputPort) =>
            Ranges.TryGetValue(inputPort, out (int, int) range) ? range : (0, 127);
        public void SetKeyboardRange(string inputPort, int lowest, int highest) => Ranges[inputPort] = (lowest, highest);
    }

    private sealed class Device : IMidiDevice
    {
        public readonly Queue<MidiEvent> Input = new();
        public void PlayMidiEvent(in MidiEvent midiEvent) { }
        public int CheckMidiInput() => Input.Count;
        public MidiEvent ReadMidiInput() => Input.Dequeue();
        public bool ValidMidiOutput() => true;
        public bool OpenMidiPort(MidiType type, string portName) => portName.EndsWith("USB") || portName.EndsWith("keys");
    }

    private static (Song, Settings, Device) Create()
    {
        CoreStatics.Reset();
        var settings = new Settings();
        var device = new Device();
        var song = new Song { MidiDevice = device };
        song.Init2(new Score(settings), settings);
        return (song, settings, device);
    }

    [Fact]
    public void ChoosingAKeyboardUsesItsOwnRange()
    {
        var (song, settings, _) = Create();
        settings.Ranges["1 - Piano USB"] = (21, 108);
        settings.Ranges["2 - Mini keys"] = (48, 72);

        song.OpenMidiPort(MidiType.Input, "1 - Piano USB");
        Assert.Equal("1 - Piano USB", song.MidiInputName);
        Assert.True(Chord.IsNotePlayable(21, 0) && !Chord.IsNotePlayable(20, 0) && Chord.IsNotePlayable(108, 0));

        song.OpenMidiPort(MidiType.Input, "2 - Mini keys");
        Assert.True(Chord.IsNotePlayable(48, 0) && !Chord.IsNotePlayable(47, 0) && !Chord.IsNotePlayable(73, 0));

        song.OpenMidiPort(MidiType.Input, "3 - Not connected");
        Assert.Equal("", song.MidiInputName);
        Assert.True(Chord.IsNotePlayable(Song.PcKeyLowestNote, 0) && !Chord.IsNotePlayable(Song.PcKeyLowestNote - 1, 0));
    }

    [Fact]
    public void TheNotesPressedOnTheKeyboardCanBeWatched()
    {
        var (song, _, device) = Create();
        song.OpenMidiPort(MidiType.Input, "1 - Piano USB");
        var notes = new List<int>();
        song.MidiKeyboardNoteObserver = notes.Add;

        var midi = new MidiEvent();
        midi.NoteOnEvent(0, 0, 36, 80);
        device.Input.Enqueue(midi);
        midi.NoteOnEvent(0, 0, 36, 0); // a note off as a note on without velocity
        device.Input.Enqueue(midi);
        midi.NoteOffEvent(0, 0, 36, 0);
        device.Input.Enqueue(midi);
        midi.NoteOnEvent(0, 0, 96, 100);
        device.Input.Enqueue(midi);
        song.RealTimeEngine(0);

        Assert.Equal(new[] { 36, 96 }, notes);
    }
}
