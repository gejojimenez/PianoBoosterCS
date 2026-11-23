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
