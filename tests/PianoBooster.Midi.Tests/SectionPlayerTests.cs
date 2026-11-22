// Tests of SectionPlayer (CSectionPlayer) with a clock of their own and a device that keeps
// what it is sent.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PianoBooster.Core;
using Xunit;

namespace PianoBooster.Midi.Tests;

using Midi = PianoBooster.Core.Midi;

internal sealed class RecordingMidiDevice : IMidiDevice
{
    private readonly object _lock = new();
    private readonly List<MidiEvent> _played = new();

    public List<MidiEvent> Played
    {
        get
        {
            lock (_lock)
                return new List<MidiEvent>(_played);
        }
    }

    public void Clear()
    {
        lock (_lock)
            _played.Clear();
    }

    public void PlayMidiEvent(in MidiEvent midiEvent)
    {
        lock (_lock)
            _played.Add(midiEvent);
    }

    public int CheckMidiInput() => 0;
    public MidiEvent ReadMidiInput() => new();
    public bool ValidMidiOutput() => true;
    public bool OpenMidiPort(MidiType type, string portName) => true;
}

public sealed class SectionPlayerTests
{
    private static SongMap Load(string name)
    {
        var map = new SongMap();
        Assert.True(map.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "midi", name)), name);
        return map;
    }

    private sealed class Harness
    {
        public readonly SongMap Map;
        public readonly RecordingMidiDevice Device = new();
        public readonly SectionPlayer Player;
        public readonly List<long> Positions = new();
        public int StoppedCount;
        public long Now;

        public Harness(string song)
        {
            Map = Load(song);
            Player = new SectionPlayer(Map, Device, null, () => Now, ownThread: false);
            Player.PositionChanged += tick => Positions.Add(tick);
            Player.Stopped += () => StoppedCount++;
        }

        /// <summary>Calls the timer every 2 ms, as the QTimer, for some time or until it stops.</summary>
        public void Run(long ms)
        {
            for (long t = 0; t < ms && Player.IsPlaying; t += 2)
            {
                Now += 2;
                Player.TimerTick();
            }
        }
    }

    private static bool IsNoteOn(in MidiEvent e) => e.Type == Midi.NoteOn && e.Velocity > 0;

    [Fact]
    public void PlaysTheNotesOfTheStretchAndStops()
    {
        var h = new Harness("bm03-SkipToMyLou.mid");
        long start = h.Map.BarStartTick(2);
        long end = h.Map.BarStartTick(4);
        h.Player.Play(start, end, repeat: false, speed: 1.0);
        Assert.True(h.Player.IsPlaying);
        h.Run(60_000);

        Assert.False(h.Player.IsPlaying);
        Assert.Equal(1, h.StoppedCount);
        Assert.Equal(end, h.Positions[^1]);
        for (int i = 1; i < h.Positions.Count; i++)
            Assert.True(h.Positions[i] >= h.Positions[i - 1]);

        // the notes on sent are those of the file in [start, end), in order
        var expected = h.Map.Events.Where(e => e.Tick >= start && e.Tick < end && (e.Status & 0xf0) == Midi.NoteOn && e.Data2 > 0)
                                   .Select(e => (e.Status & 0x0f, e.Data1)).ToList();
        var played = h.Device.Played.Where(e => IsNoteOn(e)).Select(e => (e.Channel, e.Note)).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, played);

        // every note started was stopped, and the pedal lifted on every channel at the end
        var sounding = new HashSet<(int, int)>();
        foreach (MidiEvent e in h.Device.Played)
        {
            if (IsNoteOn(e))
                sounding.Add((e.Channel, e.Note));
            else if (e.Type == Midi.NoteOff || e.Type == Midi.NoteOn)
                sounding.Remove((e.Channel, e.Note));
        }
        Assert.Empty(sounding);
        List<MidiEvent> all = h.Device.Played;
        List<MidiEvent> tail = all.Skip(all.FindLastIndex(e => IsNoteOn(e)) + 1).ToList();
        for (int channel = 0; channel < 16; channel++)
        {
            List<MidiEvent> controls = tail.Where(e => e.Channel == channel && e.Type == Midi.ControlChange).ToList();
            Assert.Contains(controls, e => e.Data1 == Midi.Sustain && e.Data2 == 0);
            Assert.Equal(Midi.AllNotesOff, controls[^1].Data1);
        }

        // it took the time of the stretch (2 ms steps)
        double ms = h.Map.MsOfTick(end) - h.Map.MsOfTick(start);
        Assert.InRange(h.Now, ms - 1, ms + 4);
    }

    [Fact]
    public void SendsTheControllersSetUpBeforeTheStart()
    {
        var h = new Harness("bm03-SkipToMyLou.mid");
        long start = h.Map.BarStartTick(3);
        h.Player.Play(start, h.Map.BarStartTick(4), false, 1.0);

        // before any note, the last programme and controllers of each channel before the start
        List<MidiEvent> chased = h.Device.Played;
        Assert.DoesNotContain(chased, e => IsNoteOn(e));
        foreach (int channel in Enumerable.Range(0, 16))
        {
            var programmes = h.Map.Events.Where(e => e.Tick < start && e.Status == (Midi.ProgramChange | channel)).ToList();
            if (programmes.Count == 0)
                continue;
            Assert.Contains(chased, e => e.Type == Midi.ProgramChange && e.Channel == channel && e.Programme == programmes[^1].Data1);
        }
        Assert.Contains(chased, e => e.Type == Midi.ProgramChange || e.Type == Midi.ControlChange);
        h.Player.Stop();
    }

    [Fact]
    public void RepeatsUntilStopped()
    {
        var h = new Harness("bm03-SkipToMyLou.mid");
        long start = h.Map.BarStartTick(1);
        long end = h.Map.BarStartTick(2);
        h.Player.Play(start, end, repeat: true, speed: 1.0);
        double ms = h.Map.MsOfTick(end) - h.Map.MsOfTick(start);
        h.Run((long)(ms * 2.5));

        Assert.True(h.Player.IsPlaying);
        Assert.Equal(0, h.StoppedCount);
        // started again twice: the position goes back to the start
        Assert.Equal(2, h.Positions.Where((p, i) => i > 0 && p < h.Positions[i - 1]).Count());
        Assert.All(h.Positions.Where((p, i) => i > 0 && p < h.Positions[i - 1]), p => Assert.Equal(start, p));
        int notes = h.Map.Events.Count(e => e.Tick >= start && e.Tick < end && (e.Status & 0xf0) == Midi.NoteOn && e.Data2 > 0);
        Assert.InRange(h.Device.Played.Count(e => IsNoteOn(e)), notes * 2 + 1, notes * 3);

        h.Player.Stop();
        Assert.False(h.Player.IsPlaying);
        Assert.Equal(1, h.StoppedCount);
        h.Player.Stop(); // already stopped: nothing
        Assert.Equal(1, h.StoppedCount);
    }

    [Fact]
    public void TheSpeedChangesTheTimeButNotThePlace()
    {
        var h = new Harness("bm03-SkipToMyLou.mid");
        long start = h.Map.BarStartTick(2);
        long end = h.Map.BarStartTick(4);
        double ms = h.Map.MsOfTick(end) - h.Map.MsOfTick(start);
        h.Player.Play(start, end, false, 2.0);
        h.Run(60_000);
        Assert.InRange(h.Now, ms / 2 - 1, ms / 2 + 4);

        // half the way at the normal speed, then twice as fast: 3/4 of the time
        var g = new Harness("bm03-SkipToMyLou.mid");
        g.Player.Play(start, end, false, 1.0);
        g.Run((long)(ms / 2));
        g.Player.SetSpeed(2.0);
        g.Run(60_000);
        Assert.InRange(g.Now, ms * 0.75 - 2, ms * 0.75 + 4);
        Assert.Equal(1, g.StoppedCount);
    }

    [Fact]
    public void NothingIsPlayedWithoutAStretch()
    {
        var h = new Harness("bm03-SkipToMyLou.mid");
        h.Player.Play(100, 100, false, 1.0);
        Assert.False(h.Player.IsPlaying);
        Assert.Empty(h.Device.Played);

        var player = new SectionPlayer(null, new RecordingMidiDevice());
        player.Play(0, 100, false, 1.0);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public void PlaysInItsOwnThread()
    {
        SongMap map = Load("bm03-SkipToMyLou.mid");
        var device = new RecordingMidiDevice();
        long start = map.BarStartTick(1);
        long end = map.BarStartTick(2);
        using var stopped = new ManualResetEventSlim();
        using var player = new SectionPlayer(map, device, null,
            () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency, ownThread: true);
        player.Stopped += stopped.Set;
        double ms = (map.MsOfTick(end) - map.MsOfTick(start)) / 4.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        player.Play(start, end, false, 4.0);
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(10)), "it did not stop");
        Assert.InRange(clock.Elapsed.TotalMilliseconds, ms - 5, ms + 500);
        Assert.False(player.IsPlaying);
        Assert.Contains(device.Played, e => IsNoteOn(e));

        // again, and stopped in the middle
        stopped.Reset();
        player.Play(start, end, true, 1.0);
        Thread.Sleep(50);
        Assert.True(player.IsPlaying);
        player.Stop();
        Assert.True(stopped.IsSet);
        Assert.False(player.IsPlaying);
    }
}
