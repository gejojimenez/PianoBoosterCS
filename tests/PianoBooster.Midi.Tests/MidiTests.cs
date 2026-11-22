// Tests of PianoBooster.Midi that need no MIDI hardware.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PianoBooster.Core;
using PianoBooster.Midi;
using Xunit;
using Xunit.Abstractions;

namespace PianoBooster.Midi.Tests;

using Midi = PianoBooster.Core.Midi;

public class MessageTests
{
    private static MidiEvent RoundTrip(MidiEvent e)
    {
        Span<byte> bytes = stackalloc byte[3];
        int length = MidiDeviceBase.BytesFromEvent(e, bytes);
        Assert.NotEqual(0, length);
        return MidiDeviceBase.EventFromBytes(bytes[..length]);
    }

    [Fact]
    public void ChannelMessagesSurviveTheBytes()
    {
        var events = new List<MidiEvent>();
        var e = new MidiEvent(); e.NoteOnEvent(0, 3, 60, 100); events.Add(e);
        e = new MidiEvent(); e.NoteOffEvent(0, 15, 61, 64); events.Add(e);
        e = new MidiEvent(); e.NotePressureEvent(0, 1, 62, 30); events.Add(e);
        e = new MidiEvent(); e.ControlChangeEvent(0, 2, Midi.MainVolume, 90); events.Add(e);
        e = new MidiEvent(); e.ProgramChangeEvent(0, 4, 19); events.Add(e);
        e = new MidiEvent(); e.ChannelPressureEvent(0, 5, 70); events.Add(e);
        e = new MidiEvent(); e.PitchBendEvent(0, 6, 0x10, 0x40); events.Add(e);

        foreach (MidiEvent original in events)
        {
            MidiEvent back = RoundTrip(original);
            Assert.Equal(original.Type, back.Type);
            Assert.Equal(original.Channel, back.Channel);
            Assert.Equal(original.Data1, back.Data1);
            if (original.Type != Midi.ProgramChange && original.Type != Midi.ChannelPressure)
                Assert.Equal(original.Data2, back.Data2);
        }
    }

    [Fact]
    public void NoteOnWithoutVelocityIsANoteOff()
    {
        MidiEvent e = MidiDeviceBase.EventFromBytes(new byte[] { 0x92, 60, 0 });
        Assert.Equal(Midi.NoteOff, e.Type);
        Assert.Equal(2, e.Channel);
        Assert.Equal(60, e.Note);
    }

    [Fact]
    public void OtherMessagesAreNotSentOrRead()
    {
        var e = new MidiEvent();
        e.MetaEvent(0, Midi.MetaTempo, 0, 0);
        Assert.Equal(0, MidiDeviceBase.BytesFromEvent(e, new byte[3]));
        Assert.Equal(Midi.None, MidiDeviceBase.EventFromBytes(new byte[] { 0xf8 }).Type);
        Assert.Equal(Midi.None, MidiDeviceBase.EventFromBytes(ReadOnlySpan<byte>.Empty).Type);
    }

    [Fact]
    public void SettingListsAreSeparatedByBars()
    {
        var settings = new MemorySettings();
        settings.SetValue("FluidSynth/SoundFont", @"C:\a b\x.sf2|/y.sf2|");
        Assert.Equal(new[] { @"C:\a b\x.sf2", "/y.sf2" }, MidiDeviceBase.GetStringList(settings, "FluidSynth/SoundFont"));
        Assert.Empty(MidiDeviceBase.GetStringList(settings, "Missing"));
    }

    [Fact]
    public void FluidSynthIsOfferedOnlyWithASoundFont()
    {
        var device = new MidiDeviceFluidSynth { Settings = new MemorySettings() };
        Assert.Empty(device.GetMidiPortList(MidiType.Output));
        Assert.False(device.OpenMidiPort(MidiType.Output, MidiDeviceFluidSynth.GetFluidInternalName()));
        Assert.False(device.ValidMidiConnection());
    }
}

public class ConductorThreadTests
{
    private readonly ITestOutputHelper _output;

    public ConductorThreadTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void GivesWholeMillisecondsKeepingTheRest()
    {
        long totalMs = 0;
        int calls = 0;
        var gaps = new List<double>();
        long last = 0;
        var clock = Stopwatch.StartNew();
        using var thread = new ConductorThread(ms =>
        {
            Assert.True(ms >= 0);
            totalMs += ms;
            calls++;
            long now = clock.ElapsedTicks;
            if (last != 0)
                gaps.Add((now - last) * 1000.0 / Stopwatch.Frequency);
            last = now;
            return calls == 10 ? EventBits.NewBarNumber : 0u;
        });
        Cfg.SetDefaults(); // tickRate 4 ms
        thread.Start();
        clock.Restart();
        Thread.Sleep(1000);
        thread.Stop();
        double elapsedMs = clock.Elapsed.TotalMilliseconds;

        gaps.Sort();
        double median = gaps[gaps.Count / 2];
        double p99 = gaps[(int)(gaps.Count * 0.99)];
        _output.WriteLine($"calls {calls}, ms given {totalMs}, elapsed {elapsedMs:0.0}, median gap {median:0.00} ms, 99% {p99:0.00} ms, max {gaps[^1]:0.00} ms");

        // no time is lost: the ms given are the time passed (less than the last ms)
        Assert.InRange(totalMs, (long)elapsedMs - 20, (long)elapsedMs + 1);
        Assert.InRange(median, 2.0, 8.0);
        Assert.Equal(EventBits.NewBarNumber, thread.TakeEventBits());
        Assert.Equal(0u, thread.TakeEventBits());
    }

    [Fact]
    public void InvokeHoldsTheLock()
    {
        int inside = 0;
        bool overlapped = false;
        using var thread = new ConductorThread(_ =>
        {
            if (Interlocked.Increment(ref inside) != 1) overlapped = true;
            Thread.SpinWait(100);
            Interlocked.Decrement(ref inside);
            return 0;
        });
        Cfg.SetDefaults();
        thread.Start();
        for (int i = 0; i < 200; i++)
            thread.Invoke(() =>
            {
                if (Interlocked.Increment(ref inside) != 1) overlapped = true;
                Thread.SpinWait(100);
                Interlocked.Decrement(ref inside);
            });
        thread.Stop();
        Assert.False(overlapped);
    }
}
