using System.IO;
using Xunit;

namespace PianoBooster.Core.Tests;

/// <summary>What the C# port does differently from the C++ code on purpose, and small checks of
/// the parts that the reference scenarios don't reach.</summary>
public class CoreTests
{
    [Fact]
    public void MissingFileGivesCannotOpenFile()
    {
        CoreStatics.Reset();
        var midiFile = new MidiFile();
        midiFile.OpenMidiFile(Path.Combine(Path.GetTempPath(), "no-such-file-PianoBooster.mid"));
        Assert.Equal(MidiError.CannotOpenFile, midiFile.MidiError);
        Assert.Equal(Midi.PbEof, midiFile.ReadMidiEvent().Type);
    }

    [Fact]
    public void ChunkThatIsNotATrackIsAnErrorAndNotACrash()
    {
        // The C++ code crashes when a track does not start with "MTrk"
        CoreStatics.Reset();
        byte[] data =
        {
            (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 1, 0, 1, 0, 96,
            (byte)'X', (byte)'X', (byte)'X', (byte)'X', 0, 0, 0, 4, 0, 0xff, 0x2f, 0,
        };
        string file = Path.Combine(Path.GetTempPath(), "PianoBooster-bad-chunk.mid");
        File.WriteAllBytes(file, data);
        var midiFile = new MidiFile();
        midiFile.OpenMidiFile(file);
        Assert.Equal(MidiError.CorruptedMidiFile, midiFile.MidiError);
        Assert.Equal(Midi.PbEof, midiFile.ReadMidiEvent().Type);
        File.Delete(file);
    }

    [Fact]
    public void TruncatedFileIsAnError()
    {
        CoreStatics.Reset();
        byte[] data =
        {
            (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 1, 0, 1, 0, 96,
            (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, 0, 40, 0, 0x90, 60, 100, 96,
        };
        string file = Path.Combine(Path.GetTempPath(), "PianoBooster-truncated.mid");
        File.WriteAllBytes(file, data);
        var midiFile = new MidiFile();
        midiFile.OpenMidiFile(file);
        // Reading past the end gives EndOfFile, but the C++ code goes on decoding the event with
        // the bytes 0xff it read, and the data byte with its high bit set makes it CorruptedMidiFile
        Assert.Equal(MidiError.CorruptedMidiFile, midiFile.MidiError);
        File.Delete(file);
    }

    [Fact]
    public void DefaultMidiEventIsCleared()
    {
        MidiEvent midiEvent = default;
        Assert.Equal(Midi.None, midiEvent.Type);
        Assert.Equal(Midi.None, new MidiEvent().Type);
    }

    [Fact]
    public void QueueBehavesLikeTheCppReleaseBuild()
    {
        var queue = new CircularQueue<int>(2);
        Assert.Equal(0, queue.Push(10));
        Assert.Equal(1, queue.Push(11));
        Assert.Equal(-1, queue.Push(12)); // full: nothing is pushed
        Assert.Equal(10, queue.Pop());
        Assert.Equal(0, queue.Push(13)); // wraps round
        Assert.Equal(11, queue.Index(0));
        Assert.Equal(13, queue.Index(1));
        Assert.Equal(11, queue.Pop());
        Assert.Equal(13, queue.Pop());
        Assert.Equal(0, queue.Length);
    }

    [Fact]
    public void ChordIsAValue()
    {
        var a = new Chord();
        a.AddNote(WhichPart.Right, 60);
        Chord b = a;
        b.AddNote(WhichPart.Right, 64);
        Assert.Equal(1, a.Length);
        Assert.Equal(2, b.Length);
    }

    [Fact]
    public void BarPositionsAreOrdered()
    {
        Assert.True(new BarPosition(1, 0, 0) < new BarPosition(1, 0, 1));
        Assert.True(new BarPosition(1, 3, 999) < new BarPosition(2, 0, 0));
        Assert.True(new BarPosition().IsSongStart);
    }
}
