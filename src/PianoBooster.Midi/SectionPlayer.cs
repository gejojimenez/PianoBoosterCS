// Ported from SectionPlayer.h/.cpp of PianoBooster (C++). GPLv3.
//
// Plays a stretch of a song (e.g. a section being edited) through the MIDI output, to hear
// where it starts and ends. It plays all the channels, as in "Listen", and does not use the
// conductor, which must be stopped while it plays.
//
// In C++ a QTimer of 2 ms (Qt::PreciseTimer) of the interface thread calls timerTick(). Here
// it runs in its own thread with the high resolution timer of ConductorThread, and the
// PositionChanged and Stopped events are posted to the SynchronizationContext of the thread
// that created the player (the interface thread), as Qt delivers its signals there.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PianoBooster.Core;

namespace PianoBooster.Midi;

using Midi = PianoBooster.Core.Midi;

/// <summary>Plays a stretch of a song through the MIDI output (CSectionPlayer).</summary>
public sealed class SectionPlayer : IDisposable
{
    private const int TimerIntervalMs = 2;
    private const int PositionIntervalMs = 30;

    private readonly SongMap? _map;
    private readonly IMidiDevice? _device;
    private readonly SynchronizationContext? _context;
    private readonly Func<long> _clockNow; // ms, from any origin
    private readonly bool _ownThread;
    private readonly object _lock = new();

    private Thread? _thread;
    private bool _playing; // m_timer.isActive()
    private int _generation; // changes with each Play and Stop
    private long _clockOrigin;
    private long _startTick;
    private long _endTick;
    private bool _repeat;
    private double _speed = 1.0;
    private double _clockStartMs; // the song time when the clock was started
    private int _nextEvent;
    private long _lastPositionMs;
    private readonly bool[,] _sounding = new bool[16, 128];

    public SectionPlayer(SongMap? map, IMidiDevice? device)
        : this(map, device, SynchronizationContext.Current, () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency, true)
    {
    }

    /// <summary>For the tests: a clock of their own and, without a thread, <see cref="TimerTick"/>
    /// called by them; the events are raised at once if there is no context.</summary>
    internal SectionPlayer(SongMap? map, IMidiDevice? device, SynchronizationContext? context, Func<long> clockNowMs, bool ownThread)
    {
        _map = map;
        _device = device;
        _context = context;
        _clockNow = clockNowMs;
        _ownThread = ownThread;
    }

    /// <summary>The playing position, a few times a second while playing.</summary>
    public event Action<long>? PositionChanged;
    public event Action? Stopped;

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _playing;
        }
    }

    public void SetRepeat(bool repeat)
    {
        lock (_lock)
            _repeat = repeat;
    }

    /// <summary>Plays from the start tick up to (not including) the end tick, at a speed (1.0 is
    /// the normal speed), starting again at the end if repeat is true.</summary>
    public void Play(long startTick, long endTick, bool repeat, double speed)
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _generation);
            if (_playing)
            {
                _playing = false;
                Silence();
            }
            if (_map == null || _device == null || endTick <= startTick)
                return;
            _startTick = startTick;
            _endTick = endTick;
            _repeat = repeat;
            _speed = Math.Max(speed, 0.1);
            // the sounds, volumes and pedals that the song has set up by the start
            ChaseControllers(startTick);
            Restart();
            _playing = true;
            if (_ownThread && _thread == null)
            {
                _thread = new Thread(Run)
                {
                    Name = "PianoBooster section player",
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                };
                _thread.Start();
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_playing)
                return;
            Interlocked.Increment(ref _generation);
            _playing = false;
            Silence();
        }
        JoinThread();
        Stopped?.Invoke(); // in the thread of the caller, as the C++ emit
    }

    public void SetSpeed(double speed)
    {
        lock (_lock)
        {
            speed = Math.Max(speed, 0.1);
            if (_playing)
            {
                // carry on from the same place in the song
                double songMs = _clockStartMs + Elapsed() * _speed;
                _clockStartMs = songMs;
                _clockOrigin = _clockNow();
            }
            _speed = speed;
        }
    }

    /// <summary>The destructor of the C++: stops without the Stopped event.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _generation);
            if (_playing)
            {
                _playing = false;
                Silence();
            }
        }
        JoinThread();
    }

    private void JoinThread()
    {
        Thread? thread;
        lock (_lock)
            thread = _thread;
        if (thread != null && thread != Thread.CurrentThread)
            thread.Join();
    }

    private long Elapsed() => _clockNow() - _clockOrigin;

    private void Restart()
    {
        IReadOnlyList<SongEvent> events = _map!.Events;
        _nextEvent = LowerBound(events, _startTick);
        _clockStartMs = _map.MsOfTick(_startTick);
        _clockOrigin = _clockNow();
        _lastPositionMs = -PositionIntervalMs;
    }

    // The first event at or after a tick (std::lower_bound)
    private static int LowerBound(IReadOnlyList<SongEvent> events, long tick)
    {
        int low = 0, high = events.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].Tick < tick)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private void Run()
    {
        using ConductorThread.PeriodicWaiter waiter = ConductorThread.PeriodicWaiter.Create();
        while (true)
        {
            lock (_lock)
            {
                if (!_playing)
                {
                    _thread = null;
                    return;
                }
                TimerTick();
            }
            waiter.Wait(TimerIntervalMs * 1_000_000L);
        }
    }

    /// <summary>One tick of the timer of the C++ (with the lock held, or from the tests).</summary>
    internal void TimerTick()
    {
        if (!_playing)
            return;
        IReadOnlyList<SongEvent> events = _map!.Events;
        long elapsed = Elapsed();
        long now = _map.TickOfMs(_clockStartMs + elapsed * _speed);

        while (_nextEvent < events.Count && events[_nextEvent].Tick <= now && events[_nextEvent].Tick < _endTick)
            Send(events[_nextEvent++]);

        if (now >= _endTick)
        {
            Silence();
            if (_repeat)
            {
                ChaseControllers(_startTick); // e.g. a pedal pressed before the start
                Restart();
                RaisePositionChanged(_startTick);
                return;
            }
            _playing = false;
            RaisePositionChanged(_endTick);
            Raise(() => Stopped?.Invoke());
            return;
        }
        if (elapsed - _lastPositionMs >= PositionIntervalMs)
        {
            _lastPositionMs = elapsed;
            RaisePositionChanged(now);
        }
    }

    private void RaisePositionChanged(long tick) => Raise(() => PositionChanged?.Invoke(tick));

    // In Qt the signals of the timer can't arrive after stop() or a new play(), because they run
    // in the same thread: the ones posted before are dropped.
    private void Raise(Action action)
    {
        if (_context == null)
        {
            action();
            return;
        }
        int generation = _generation;
        _context.Post(_ =>
        {
            if (Volatile.Read(ref _generation) == generation)
                action();
        }, null);
    }

    // Sends the last programme, controllers and pitch bend of each channel before a time,
    // without the notes, so that the section sounds as it does when the song is played.
    private void ChaseControllers(long tick)
    {
        var programme = new int[16];
        var controller = new int[16, 128];
        var bendLsb = new int[16];
        var bendMsb = new int[16];
        Array.Fill(programme, -1);
        Array.Fill(bendLsb, -1);
        Array.Fill(bendMsb, -1);
        for (int c = 0; c < 16; c++)
            for (int n = 0; n < 128; n++)
                controller[c, n] = -1;

        foreach (SongEvent songEvent in _map!.Events)
        {
            if (songEvent.Tick >= tick)
                break;
            int channel = songEvent.Status & 0x0f;
            switch (songEvent.Status & 0xf0)
            {
                case Midi.ProgramChange:
                    programme[channel] = songEvent.Data1;
                    break;
                case Midi.ControlChange:
                    if (songEvent.Data1 < Midi.AllSoundOff) // not the channel mode messages
                        controller[channel, songEvent.Data1] = songEvent.Data2;
                    break;
                case Midi.PitchBend:
                    bendLsb[channel] = songEvent.Data1;
                    bendMsb[channel] = songEvent.Data2;
                    break;
            }
        }

        for (int channel = 0; channel < 16; ++channel)
        {
            var midi = new MidiEvent();
            if (programme[channel] >= 0)
            {
                midi.ProgramChangeEvent(0, channel, programme[channel]);
                _device!.PlayMidiEvent(midi);
            }
            for (int number = 0; number < 128; ++number)
            {
                if (controller[channel, number] < 0)
                    continue;
                midi.ControlChangeEvent(0, channel, number, controller[channel, number]);
                _device!.PlayMidiEvent(midi);
            }
            if (bendMsb[channel] >= 0)
            {
                midi.PitchBendEvent(0, channel, bendLsb[channel], bendMsb[channel]);
                _device!.PlayMidiEvent(midi);
            }
        }
    }

    private void Send(in SongEvent songEvent)
    {
        int channel = songEvent.Status & 0x0f;
        var midi = new MidiEvent();
        switch (songEvent.Status & 0xf0)
        {
            case Midi.NoteOff:
                midi.NoteOffEvent(0, channel, songEvent.Data1, songEvent.Data2);
                _sounding[channel, songEvent.Data1] = false;
                break;
            case Midi.NoteOn:
                if (songEvent.Data2 == 0)
                {
                    midi.NoteOffEvent(0, channel, songEvent.Data1, 0);
                    _sounding[channel, songEvent.Data1] = false;
                }
                else
                {
                    midi.NoteOnEvent(0, channel, songEvent.Data1, songEvent.Data2);
                    _sounding[channel, songEvent.Data1] = true;
                }
                break;
            case Midi.NotePressure:
                midi.NotePressureEvent(0, channel, songEvent.Data1, songEvent.Data2);
                break;
            case Midi.ControlChange:
                midi.ControlChangeEvent(0, channel, songEvent.Data1, songEvent.Data2);
                break;
            case Midi.ProgramChange:
                midi.ProgramChangeEvent(0, channel, songEvent.Data1);
                break;
            case Midi.ChannelPressure:
                midi.ChannelPressureEvent(0, channel, songEvent.Data1);
                break;
            case Midi.PitchBend:
                midi.PitchBendEvent(0, channel, songEvent.Data1, songEvent.Data2);
                break;
            default:
                return;
        }
        _device!.PlayMidiEvent(midi);
    }

    // Stops every note that is sounding, and lifts the pedal
    private void Silence()
    {
        var midi = new MidiEvent();
        for (int channel = 0; channel < 16; ++channel)
        {
            for (int note = 0; note < 128; ++note)
            {
                if (!_sounding[channel, note])
                    continue;
                midi.NoteOffEvent(0, channel, note, 0);
                _device!.PlayMidiEvent(midi);
                _sounding[channel, note] = false;
            }
            midi.ControlChangeEvent(0, channel, Midi.Sustain, 0);
            _device!.PlayMidiEvent(midi);
            midi.ControlChangeEvent(0, channel, Midi.AllNotesOff, 0);
            _device!.PlayMidiEvent(midi);
        }
    }
}
