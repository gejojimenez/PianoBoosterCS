// Ported from SongMap.h, SongMap.cpp and MidiByteReader.h of PianoBooster (C++).
// Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PianoBooster.Core;

/// <summary>Reads the bytes of a part of a MIDI file, remembering if anything went wrong.</summary>
internal sealed class MidiByteReader
{
    private readonly byte[] _data;
    private long _pos;
    private readonly long _end;

    public MidiByteReader(byte[] data, long start, long end)
    {
        _data = data;
        _pos = start;
        _end = end;
    }

    public bool AtEnd => _pos >= _end;
    public bool Ok { get; private set; } = true;
    public long Pos => _pos;

    public void Skip(long count)
    {
        if (count < 0 || count > _end - _pos) { Ok = false; _pos = _end; return; }
        _pos += count;
    }

    public void Back()
    {
        if (_pos > 0) --_pos;
    }

    public int Byte()
    {
        if (_pos >= _end) { Ok = false; return 0; }
        return _data[_pos++];
    }

    public uint Word32()
    {
        uint v = 0;
        for (int i = 0; i < 4; ++i)
            v = (v << 8) | (uint)Byte();
        return v;
    }

    public ushort Word16()
    {
        ushort hi = (ushort)Byte();
        return (ushort)((hi << 8) | (ushort)Byte());
    }

    public long VarLength()
    {
        long value = 0;
        for (int i = 0; i < 4; ++i)
        {
            int b = Byte();
            value = (value << 7) | (long)(b & 0x7f);
            if ((b & 0x80) == 0)
                return value;
        }
        Ok = false;
        return 0;
    }

    public byte[] Bytes(long count)
    {
        if (count < 0 || count > _end - _pos) { Ok = false; _pos = _end; return Array.Empty<byte>(); }
        var result = new byte[count];
        Array.Copy(_data, _pos, result, 0, count);
        _pos += count;
        return result;
    }
}

/// <summary>A note of the song, with its times in ticks of the file (not scaled).</summary>
public struct SongNote
{
    public long Tick;
    public long Length;
    public int Note;
    public int Channel;
    public int Track;
    public int Velocity;
}

/// <summary>A channel message of the song (note, controller, programme, pitch bend ...), to play it.</summary>
public struct SongEvent
{
    public long Tick;
    public int Status; // the type ORed with the channel, e.g. 0x93
    public int Data1;
    public int Data2;
}

/// <summary>The whole song read at once (for the section editor), with its bars and tempo.</summary>
public sealed class SongMap
{
    private const int DrumChannel = 9;

    private struct TimeSigSegment
    {
        public int StartBar;
        public long StartTick;
        public int Top;
        public int Bottom;
        public long BeatTicks;
    }

    private struct TempoSegment
    {
        public long Tick;
        public double StartMs;
        public double MsPerTick;
    }

    private static readonly TimeSigSegment s_defaultSegment = new() { StartBar = 0, StartTick = 0, Top = 4, Bottom = 4, BeatTicks = 96 };

    private bool _valid;
    private int _ppqn = 96;
    private List<SongNote> _notes = new();
    private List<SongEvent> _events = new();
    private readonly List<TimeSigSegment> _timeSigs = new();  // never empty once loaded
    private readonly List<TempoSegment> _tempos = new();      // never empty once loaded
    private long _maxNoteLength;
    private long _lastTick;
    private int _lowestNote = 60;
    private int _highestNote = 72;

    public bool IsValid => _valid;
    public int Ppqn => _ppqn;
    public IReadOnlyList<SongNote> Notes => _notes;    // in order of time
    public IReadOnlyList<SongEvent> Events => _events; // in order of time
    public long MaxNoteLength => _maxNoteLength;
    public long LastTick => _lastTick;
    public int LowestNote => _lowestNote;   // drums are not counted
    public int HighestNote => _highestNote;

    // Notes off first, then the controllers and programmes, then the notes on, so that at the
    // same time a repeated note is stopped before it starts again, with the right sound.
    private static int EventOrder(in SongEvent songEvent)
    {
        int type = songEvent.Status & 0xf0;
        if (type == 0x80 || (type == 0x90 && songEvent.Data2 == 0))
            return 0;
        return (type == 0x90) ? 2 : 1;
    }

    private void Clear()
    {
        _valid = false;
        _ppqn = 96;
        _notes = new List<SongNote>();
        _events = new List<SongEvent>();
        _timeSigs.Clear();
        _tempos.Clear();
        _maxNoteLength = 0;
        _lastTick = 0;
        _lowestNote = 60;
        _highestNote = 72;
    }

    /// <summary>Reads a MIDI file; returns false if it can't be read.</summary>
    public bool Load(string fileName)
    {
        Clear();

        byte[] data;
        try
        {
            data = File.ReadAllBytes(fileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            return false;
        }

        var header = new MidiByteReader(data, 0, data.Length);
        byte[] id = header.Bytes(4);
        if (id.Length != 4 || id[0] != 'M' || id[1] != 'T' || id[2] != 'h' || id[3] != 'd')
            return false;
        uint headerLength = header.Word32();
        header.Word16(); // format
        int trackCount = header.Word16();
        int division = header.Word16();
        if (!header.Ok || headerLength < 6 || (division & 0x8000) != 0 || division == 0)
            return false; // SMPTE time division is not supported
        header.Skip((long)headerLength - 6);
        _ppqn = division;

        var timeSigs = new List<(long Tick, int Top, int Bottom)>();
        var tempos = new List<(long Tick, long MicrosecondsPerQuarter)>();

        long pos = header.Pos;
        int track = 0; // counts the MTrk chunks only, like MidiFile
        for (int chunkIndex = 0; chunkIndex < trackCount && pos + 8 <= data.Length; ++chunkIndex)
        {
            var chunk = new MidiByteReader(data, pos, data.Length);
            byte[] chunkId = chunk.Bytes(4);
            long chunkLength = chunk.Word32();
            long trackStart = chunk.Pos;
            long trackEnd = Math.Min(trackStart + chunkLength, data.Length);
            pos = trackEnd;
            if (chunkId.Length != 4 || chunkId[0] != 'M' || chunkId[1] != 'T' || chunkId[2] != 'r' || chunkId[3] != 'k')
                continue; // skip unknown chunks

            // the notes that have started and not ended yet, by channel and note
            var sounding = new Dictionary<int, List<int>>();

            var reader = new MidiByteReader(data, trackStart, trackEnd);
            long tick = 0;
            int runningStatus = 0;
            while (!reader.AtEnd && reader.Ok)
            {
                tick += reader.VarLength();
                int status = reader.Byte();
                if (status < 0x80) // running status: this byte is already data
                {
                    if (runningStatus == 0)
                        break;
                    status = runningStatus;
                    reader.Back();
                }

                if (status == 0xff) // meta event
                {
                    int type = reader.Byte();
                    long length = reader.VarLength();
                    byte[] meta = reader.Bytes(length);
                    if (type == 0x2f) // end of track
                        break;
                    if (type == 0x51 && meta.Length >= 3)
                        tempos.Add((tick, (meta[0] << 16) | (meta[1] << 8) | meta[2]));
                    else if (type == 0x58 && meta.Length >= 2)
                        timeSigs.Add((tick, meta[0], 1 << Math.Min((int)meta[1], 6)));
                    continue;
                }
                if (status == 0xf0 || status == 0xf7) // system exclusive
                {
                    reader.Skip(reader.VarLength());
                    continue;
                }

                runningStatus = status;
                int eventType = status & 0xf0;
                int channel = status & 0x0f;
                var songEvent = new SongEvent { Tick = tick, Status = status, Data1 = reader.Byte() & 0x7f };
                if (eventType != 0xc0 && eventType != 0xd0)
                    songEvent.Data2 = reader.Byte() & 0x7f;
                if (!reader.Ok)
                    break;
                _events.Add(songEvent);
                _lastTick = Math.Max(_lastTick, tick);

                int key = channel * 128 + songEvent.Data1;
                if (eventType == 0x90 && songEvent.Data2 > 0)
                {
                    var note = new SongNote
                    {
                        Tick = tick,
                        Note = songEvent.Data1,
                        Channel = channel,
                        Track = track,
                        Velocity = songEvent.Data2,
                    };
                    if (!sounding.TryGetValue(key, out List<int>? started))
                        sounding[key] = started = new List<int>();
                    started.Add(_notes.Count);
                    _notes.Add(note);
                }
                else if (eventType == 0x80 || eventType == 0x90)
                {
                    if (sounding.TryGetValue(key, out List<int>? started) && started.Count > 0)
                    {
                        int index = started[0];
                        started.RemoveAt(0);
                        SongNote note = _notes[index];
                        note.Length = tick - note.Tick;
                        _notes[index] = note;
                    }
                }
            }
            // notes that never end last up to the end of their track
            foreach (List<int> started in sounding.Values)
            {
                foreach (int index in started)
                {
                    SongNote note = _notes[index];
                    note.Length = tick - note.Tick;
                    _notes[index] = note;
                }
            }
            ++track;
        }

        // stable sorts, like std::stable_sort
        _events = _events.OrderBy(e => e.Tick).ThenBy(e => EventOrder(e)).ToList();
        _notes = _notes.OrderBy(n => n.Tick).ToList();

        bool anyNote = false;
        for (int i = 0; i < _notes.Count; i++)
        {
            SongNote note = _notes[i];
            if (note.Length <= 0)
            {
                note.Length = 1;
                _notes[i] = note;
            }
            _maxNoteLength = Math.Max(_maxNoteLength, note.Length);
            _lastTick = Math.Max(_lastTick, note.Tick + note.Length);
            if (note.Channel == DrumChannel)
                continue;
            if (!anyNote)
            {
                _lowestNote = _highestNote = note.Note;
                anyNote = true;
            }
            _lowestNote = Math.Min(_lowestNote, note.Note);
            _highestNote = Math.Max(_highestNote, note.Note);
        }

        // The bars, like in Song.MidiFileInfo() and Bar: 4/4 if there is no time signature
        foreach (var change in timeSigs.OrderBy(t => t.Tick))
            AddTimeSigChange(change.Tick, change.Top, change.Bottom);
        if (_timeSigs.Count == 0)
            AddTimeSigChange(0, 4, 4);

        // The tempo changes: 120 BPM until the first one
        _tempos.Add(new TempoSegment { Tick = 0, StartMs = 0.0, MsPerTick = 500.0 / _ppqn });
        foreach (var change in tempos.OrderBy(t => t.Tick))
        {
            if (change.MicrosecondsPerQuarter <= 0)
                continue;
            TempoSegment last = _tempos[^1];
            var segment = new TempoSegment
            {
                Tick = change.Tick,
                StartMs = last.StartMs + (double)(change.Tick - last.Tick) * last.MsPerTick,
                MsPerTick = (double)change.MicrosecondsPerQuarter / 1000.0 / _ppqn,
            };
            if (segment.Tick == last.Tick)
                _tempos[^1] = segment;
            else
                _tempos.Add(segment);
        }

        _valid = true;
        return true;
    }

    // The same rules as Bar.AddTimeSigChange(), so that the bars are the same as when playing
    private void AddTimeSigChange(long tick, int top, int bottom)
    {
        if (top <= 0 || bottom <= 0)
            return;
        var segment = new TimeSigSegment { Top = top, Bottom = bottom, BeatTicks = ((long)_ppqn * 4) / bottom };
        if (segment.BeatTicks <= 0)
            return;

        if (_timeSigs.Count > 0)
        {
            TimeSigSegment last = _timeSigs[^1];
            long lastBarTicks = last.BeatTicks * last.Top;
            long bars = (tick > last.StartTick) ? (tick - last.StartTick + lastBarTicks - 1) / lastBarTicks : 0;
            segment.StartBar = last.StartBar + (int)bars;
            segment.StartTick = last.StartTick + bars * lastBarTicks;
            if (segment.StartBar == last.StartBar)
            {
                _timeSigs[^1] = segment; // replaces a change at the same bar
                return;
            }
            if (last.Top == top && last.Bottom == bottom)
                return; // nothing changes
        }
        _timeSigs.Add(segment);
    }

    private TimeSigSegment SegmentOfBar(int bar)
    {
        if (_timeSigs.Count == 0)
            return s_defaultSegment;
        int index = 0;
        for (int i = 1; i < _timeSigs.Count && _timeSigs[i].StartBar <= bar; ++i)
            index = i;
        return _timeSigs[index];
    }

    private TimeSigSegment SegmentOfTick(long tick)
    {
        if (_timeSigs.Count == 0)
            return s_defaultSegment;
        int index = 0;
        for (int i = 1; i < _timeSigs.Count && _timeSigs[i].StartTick <= tick; ++i)
            index = i;
        return _timeSigs[index];
    }

    /// <summary>The bars up to the end of the last note. Bars count from 0, like in Bar.</summary>
    public int BarCount()
    {
        if (_lastTick <= 0)
            return 1;
        return BarOfTick(_lastTick - 1) + 1;
    }

    /// <summary>The end of the last bar.</summary>
    public long EndTick => BarStartTick(BarCount());

    public long BarStartTick(int bar)
    {
        if (bar <= 0)
            return 0;
        TimeSigSegment segment = SegmentOfBar(bar);
        return segment.StartTick + (long)(bar - segment.StartBar) * segment.BeatTicks * segment.Top;
    }

    public int BarOfTick(long tick)
    {
        if (tick <= 0)
            return 0;
        TimeSigSegment segment = SegmentOfTick(tick);
        return segment.StartBar + (int)((tick - segment.StartTick) / (segment.BeatTicks * segment.Top));
    }

    public int BeatsInBar(int bar) => SegmentOfBar(bar).Top;
    public int TimeSigBottom(int bar) => SegmentOfBar(bar).Bottom;
    /// <summary>The length of a beat of that bar.</summary>
    public long BeatTicks(int bar) => SegmentOfBar(bar).BeatTicks;
    public long BarTicks(int bar) => BeatTicks(bar) * BeatsInBar(bar);

    /// <summary>Converts a tick of the file to a position (whose ticks are scaled by Speed.AdjustFactor).</summary>
    public BarPosition PositionOfTick(long tick)
    {
        if (tick < 0)
            tick = 0;
        var position = new BarPosition { Bar = BarOfTick(tick) };
        long beat = BeatTicks(position.Bar);
        long inBar = tick - BarStartTick(position.Bar);
        position.Beat = (int)(inBar / beat);
        position.Ticks = (inBar % beat) * Speed.AdjustFactor;
        return position;
    }

    /// <summary>Converts a position to a tick of the file, rounding down.</summary>
    public long TickOfPosition(BarPosition position)
    {
        int bar = Math.Max(position.Bar, 0);
        long tick = BarStartTick(bar) + (long)Math.Max(position.Beat, 0) * BeatTicks(bar);
        if (position.Ticks > 0)
            tick += position.Ticks / Speed.AdjustFactor;
        return tick;
    }

    /// <summary>A bar and a beat (both counting from 0, the beat may have a fraction), rounded to
    /// the nearest tick. Beats past the end of the bar go on into the next bars.</summary>
    public long TickOfBarBeat(int bar, double beat)
    {
        bar = Math.Max(bar, 0);
        beat = Math.Max(beat, 0.0);
        // the beats past the end of the bar go on into the next bars, with their own time signature
        while (beat >= BeatsInBar(bar) && bar < BarCount() + 1000)
        {
            beat -= BeatsInBar(bar);
            ++bar;
        }
        return BarStartTick(bar) + (long)Math.Round(beat * (double)BeatTicks(bar), MidpointRounding.AwayFromZero);
    }

    /// <summary>The beat within its bar, counting from 0.</summary>
    public double BeatOfTick(long tick)
    {
        BarPosition position = PositionOfTick(tick);
        return position.Beat + (double)position.Ticks / (double)(BeatTicks(position.Bar) * Speed.AdjustFactor);
    }

    /// <summary>The end of a section (Bar.SetLoop()) is the position of its last notes, which are
    /// included. Here a section is a stretch that ends just before a tick, so its end position is
    /// one tick before that, and the notes at that tick are not included.</summary>
    public BarPosition SectionEndPosition(long endTick) => PositionOfTick(endTick - 1);
    public long SectionEndTick(BarPosition end) => TickOfPosition(end) + 1;

    /// <summary>The time at normal speed, using the tempo changes.</summary>
    public double MsOfTick(long tick)
    {
        if (_tempos.Count == 0)
            return 0.0;
        int index = 0;
        for (int i = 1; i < _tempos.Count && _tempos[i].Tick <= tick; ++i)
            index = i;
        TempoSegment segment = _tempos[index];
        return segment.StartMs + (double)(tick - segment.Tick) * segment.MsPerTick;
    }

    public long TickOfMs(double ms)
    {
        if (_tempos.Count == 0)
            return 0;
        int index = 0;
        for (int i = 1; i < _tempos.Count && _tempos[i].StartMs <= ms; ++i)
            index = i;
        TempoSegment segment = _tempos[index];
        if (segment.MsPerTick <= 0.0)
            return segment.Tick;
        return segment.Tick + (long)Math.Floor((ms - segment.StartMs) / segment.MsPerTick);
    }
}
