using System;
using System.IO;
using Xunit;

namespace PianoBooster.Core.Tests;

/// <summary>MidiInfo (CMidiInfo, phase 4), checked against what tools/MakeSyntheticMidi writes.</summary>
public class MidiInfoTests
{
    private static string Midi(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", "midi", name);

    [Fact]
    public void TimeSignatureTempoAndKeyChanges()
    {
        MidiInfo info = MidiInfo.Read(Midi("sy01-TimeSigChanges.mid"));
        Assert.True(info.Valid);
        Assert.Equal("4/4 (changes)", info.TimeSigText());
        Assert.Equal("B♭ major", info.KeyText());           // 2 flats
        Assert.Equal("120 BPM (changes)", info.TempoText()); // 500000 us, then 400000 us
        // two bars of 4/4, two of 12/8 and three of 3/4
        Assert.Equal(7, info.Bars);
        // up to the last note off, 10 ticks before the end: 1680 ticks at 500000 us and
        // 1790 at 400000 us, with 120 ticks per quarter
        Assert.Equal((long)((1680 * 500000.0 + 1790 * 400000.0) / 120 / 1000), info.DurationMs);
        Assert.Equal("0:13", info.DurationText());
    }

    [Fact]
    public void SharpKeyAndPpqn480()
    {
        MidiInfo info = MidiInfo.Read(Midi("sy02-SharpKeyPpqn480.mid"));
        Assert.True(info.Valid);
        Assert.Equal("3/4", info.TimeSigText());
        Assert.Equal("E major", info.KeyText());
        Assert.Equal("120 BPM", info.TempoText()); // no tempo event
    }

    [Fact]
    public void SongsOfTheCourse()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "midi"), "b*.mid"))
        {
            MidiInfo info = MidiInfo.Read(file);
            Assert.True(info.Valid, file);
            Assert.True(info.Bars > 0, file);
            Assert.True(info.DurationMs > 0, file);
        }
    }

    [Fact]
    public void NotAMidiFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "PianoBooster-not-midi.mid");
        File.WriteAllText(file, "hello");
        try
        {
            Assert.False(MidiInfo.Read(file).Valid);
            Assert.False(MidiInfo.Read(file + ".missing").Valid);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
