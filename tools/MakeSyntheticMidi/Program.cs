// Writes the synthetic MIDI files of the tests (tests/PianoBooster.Core.Tests/TestData/midi/sy*.mid).
// They cover what the songs of the course don't: time signature changes (4/4 -> 12/8 -> 3/4),
// key signatures with flats and sharps, a tempo change, running status, a note struck again
// before its note off, a note without a note off, a note on with velocity 0, both hands on one
// channel in two tracks, drums, and a ppqn of 480.
//
// Usage: dotnet run --project tools/MakeSyntheticMidi [output folder]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

string outDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "PianoBooster.Core.Tests", "TestData", "midi");
Directory.CreateDirectory(outDir);

File.WriteAllBytes(Path.Combine(outDir, "sy01-TimeSigChanges.mid"), TimeSigChanges());
File.WriteAllBytes(Path.Combine(outDir, "sy02-SharpKeyPpqn480.mid"), SharpKeyPpqn480());
Console.WriteLine("Written to " + Path.GetFullPath(outDir));

static byte[] TimeSigChanges()
{
    const int ppqn = 120;
    const int q = ppqn, e8 = ppqn / 2;
    const int bar44 = 4 * q, bar128 = 12 * e8, bar34 = 3 * q;
    const int t128 = 2 * bar44;           // 12/8 from bar 3 (tick 960)
    const int t34 = t128 + 2 * bar128;    // 3/4 from bar 5 (tick 2400)
    const int end = t34 + 3 * bar34;      // three bars of 3/4

    var conductor = new Track();
    conductor.Meta(0, 0x03, Encoding.ASCII.GetBytes("Synthetic Time Signatures"));
    conductor.Meta(0, 0x51, 0x07, 0xa1, 0x20);           // 500000 us per quarter
    conductor.Meta(0, 0x58, 4, 2, 24, 8);                // 4/4
    conductor.Meta(0, 0x59, 0xfe, 0);                    // 2 flats, major
    conductor.Meta(t128, 0x58, 12, 3, 36, 8);            // 12/8
    conductor.Meta(t128 + bar128, 0x51, 0x06, 0x1a, 0x80); // 400000 us per quarter
    conductor.Meta(t34, 0x58, 3, 2, 24, 8);              // 3/4

    // right hand (track 1, channel 0)
    var right = new Track();
    right.Meta(0, 0x03, Encoding.ASCII.GetBytes("Right"));
    right.Add(0, 0xc0, 0);
    int[] melody44 = { 70, 72, 74, 75, 74, 72, 70, 66 }; // Bb C D Eb D C Bb F#
    for (int i = 0; i < melody44.Length; i++)
    {
        int t = i * q;
        right.On(t, 0, melody44[i], 90);
        if (i == 5)
            right.On(t + q - 1, 0, melody44[i], 0); // note off as a note on with velocity 0
        else
            right.Off(t + q - 10, 0, melody44[i]);
    }
    int[] melody128 = { 67, 68, 69, 70, 71, 70, 68, 67, 66, 67, 68, 69, 70, 71, 72, 71, 70, 69, 68, 67, 66, 65, 66, 67 };
    for (int i = 0; i < melody128.Length; i++)
    {
        int t = t128 + i * e8;
        right.On(t, 0, melody128[i], 80);
        if (i == 3 || i == 13)
            continue; // 3 is struck again below before its note off, 13 never gets a note off
        right.Off(t + e8 - 5, 0, melody128[i]);
    }
    right.On(t128 + 3 * e8 + 20, 0, 70, 70); // Bb again before its note off
    right.Off(t128 + 4 * e8, 0, 70);
    int[] melody34 = { 72, 73, 74, 72, 71, 70, 69, 67, 65 };
    for (int i = 0; i < melody34.Length; i++)
    {
        int t = t34 + i * q;
        right.On(t, 0, melody34[i], 85);
        right.Off(t + q - 10, 0, melody34[i]);
    }

    // left hand (track 2, also channel 0): chords of two notes
    var left = new Track();
    left.Meta(0, 0x03, Encoding.ASCII.GetBytes("Left"));
    int[][] chords = { new[] { 46, 53 }, new[] { 48, 55 }, new[] { 43, 50 }, new[] { 41, 48 },
                       new[] { 46, 53 }, new[] { 39, 46 }, new[] { 41, 53 }, new[] { 34, 46 } };
    int[] times = { 0, 2 * q, bar44, bar44 + 2 * q, t128, t128 + bar128 / 2, t128 + bar128, t34 };
    for (int i = 0; i < chords.Length; i++)
    {
        int length = i < 4 ? 2 * q : i < 7 ? bar128 / 2 : 3 * bar34;
        foreach (int note in chords[i])
        {
            left.On(times[i], 0, note, 70);
            left.Off(times[i] + length - 10, 0, note);
        }
    }

    // drums (track 3, channel 10)
    var drums = new Track();
    drums.Meta(0, 0x03, Encoding.ASCII.GetBytes("Drums"));
    for (int t = 0; t < end; t += q)
    {
        drums.On(t, 9, 42, 60);
        drums.Off(t + 20, 9, 42);
    }

    return MidiFileBytes(ppqn, conductor, right, left, drums);
}

static byte[] SharpKeyPpqn480()
{
    const int ppqn = 480, q = ppqn;
    var track = new Track();
    track.Meta(0, 0x58, 3, 2, 24, 8);   // 3/4
    track.Meta(0, 0x59, 4, 0);          // 4 sharps (E major)
    track.Add(0, 0xc2, 0);
    track.Add(0, 0xc3, 0);
    track.Add(0, 0xb2, 7, 90);          // main volume
    int[] notes = { 64, 66, 68, 69, 71, 73, 75, 76, 75, 74, 73, 71, 69, 68, 67, 66, 64 };
    for (int i = 0; i < notes.Length; i++)
    {
        int t = i * q;
        track.On(t, 3, notes[i], 90);
        track.Off(t + q - 40, 3, notes[i]);
        if (i % 3 == 0)
        {
            track.On(t, 2, notes[i] - 24, 70);
            track.Off(t + 3 * q - 40, 2, notes[i] - 24);
        }
    }
    return MidiFileBytes(ppqn, track);
}

static byte[] MidiFileBytes(int ppqn, params Track[] tracks)
{
    var file = new List<byte>();
    file.AddRange(Encoding.ASCII.GetBytes("MThd"));
    file.AddRange(new byte[] { 0, 0, 0, 6, 0, 1, 0, (byte)tracks.Length, (byte)(ppqn >> 8), (byte)(ppqn & 0xff) });
    foreach (Track track in tracks)
        file.AddRange(track.Chunk());
    return file.ToArray();
}

sealed class Track
{
    private readonly List<(int Tick, int Order, byte[] Message)> _events = new();

    public void Add(int tick, params int[] message) =>
        _events.Add((tick, _events.Count, message.Select(b => (byte)b).ToArray()));

    public void Meta(int tick, int type, params int[] payload)
    {
        var message = new List<int> { 0xff, type };
        message.AddRange(VarLen(payload.Length).Select(b => (int)b));
        message.AddRange(payload);
        Add(tick, message.ToArray());
    }

    public void Meta(int tick, int type, byte[] payload) => Meta(tick, type, payload.Select(b => (int)b).ToArray());
    public void On(int tick, int channel, int note, int velocity) => Add(tick, 0x90 | channel, note, velocity);
    public void Off(int tick, int channel, int note) => Add(tick, 0x80 | channel, note, 64);

    private static byte[] VarLen(int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7f) };
        value >>= 7;
        while (value > 0)
        {
            bytes.Insert(0, (byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        return bytes.ToArray();
    }

    // the events in order of time, with running status for the channel messages
    public byte[] Chunk()
    {
        var data = new List<byte>();
        int last = 0;
        int running = 0;
        foreach (var (tick, _, message) in _events.OrderBy(e => e.Tick).ThenBy(e => e.Order))
        {
            data.AddRange(VarLen(tick - last));
            last = tick;
            int status = message[0];
            if (status < 0xf0 && status == running)
                data.AddRange(message.Skip(1));
            else
            {
                data.AddRange(message);
                running = status < 0xf0 ? status : 0;
            }
        }
        data.AddRange(new byte[] { 0x00, 0xff, 0x2f, 0x00 });
        var chunk = new List<byte>();
        chunk.AddRange(Encoding.ASCII.GetBytes("MTrk"));
        int length = data.Count;
        chunk.AddRange(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
        chunk.AddRange(data);
        return chunk.ToArray();
    }
}
