// Ported from Bar.h and Bar.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;
using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>The event bits returned by Song.Task(); they can be ORed together.</summary>
public static class EventBits
{
    public const uint PlayingStopped = 0x0001;    // set when we reach the end of piece
    public const uint ForceFullRedraw = 0x0002;   // force the whole screen to be redrawn
    public const uint ForceRatingRedraw = 0x0004; // force the score to be redrawn
    public const uint NewBarNumber = 0x0008;      // force the bar number to be redrawn
    public const uint UptoBarReached = 0x0010;    // Used for looping when playing between two bars.
    public const uint LoadSong = 0x0020;          // load song
}

/// <summary>
/// An exact position in the song: the bar, the beat within the bar and the ticks within the
/// beat. Bars and beats count from 0 and the ticks are scaled by Speed.AdjustFactor, like all
/// the time values in <see cref="Bar"/>. Being whole numbers, positions can be compared exactly.
/// </summary>
public struct BarPosition : IComparable<BarPosition>, IEquatable<BarPosition>
{
    public int Bar;
    public int Beat;
    public long Ticks;

    public BarPosition(int bar, int beat, long ticks)
    {
        Bar = bar;
        Beat = beat;
        Ticks = ticks;
    }

    public readonly int CompareTo(BarPosition other)
    {
        if (Bar != other.Bar)
            return Bar.CompareTo(other.Bar);
        if (Beat != other.Beat)
            return Beat.CompareTo(other.Beat);
        return Ticks.CompareTo(other.Ticks);
    }

    public static bool operator <(BarPosition a, BarPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(BarPosition a, BarPosition b) => a.CompareTo(b) > 0;
    public readonly bool Equals(BarPosition other) => Bar == other.Bar && Beat == other.Beat && Ticks == other.Ticks;
    public override readonly bool Equals(object? obj) => obj is BarPosition other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Bar, Beat, Ticks);
    public readonly bool IsSongStart => Bar <= 0 && Beat <= 0 && Ticks <= 0;
    public override readonly string ToString() => $"bar {Bar} beat {Beat} ticks {Ticks}";
}

/// <summary>Controls the bar numbers.</summary>
public sealed class Bar
{
    /// <summary>Bars are counted from 0 internally, but shown to the user counting from 1.</summary>
    public const int FirstBarNumber = 1;
    public const double EndBeatTolerance = 0.01; // the precision of the beat fields

    // The time signature that is used from a bar onwards, up to the next change.
    private struct TimeSigSegment
    {
        public int StartBar;     // the first bar with this time signature
        public long StartTick;   // the time of that bar from the start of the song, in ppqn ticks
        public int Top;          // beats in each bar
        public int Bottom;
        public long BeatLength;  // in ppqn ticks
    }

    private readonly List<TimeSigSegment> _timeSigMap = new();

    private long _deltaTime;
    private long _beatLength; // in ppqn ticks
    private long _barLength;  // _beatLength * TimeSigTop (also in ppqn ticks)

    private int _startTimeSigTop; // The time Sig at the start of the piece
    private int _startTimeSigBottom;
    private int _currentTimeSigTop;
    private int _currentTimeSigBottom;

    private int _barCounter;
    private int _beatCounter;
    private BarPosition _playFrom;
    private BarPosition _playUpto;
    private bool _loopEnabled;
    private bool _seekingBarNumber;
    private bool _flushTicks;
    private uint _eventBits;
    private bool _enableLooping;
    private bool _enablePlayFromBar;

    public Bar()
    {
        Reset();
    }

    /// <summary>You MUST clear the time sig to 0 first before setting a new start time signature
    /// at the start of the piece of music.</summary>
    public void SetTimeSig(int top, int bottom)
    {
        _currentTimeSigTop = top;
        _currentTimeSigBottom = bottom;
        if (bottom == 0 || _startTimeSigBottom == 0)
        {
            _startTimeSigTop = top;
            _startTimeSigBottom = bottom;
            if (bottom == 0)
            {
                _currentTimeSigTop = 4;
                _currentTimeSigBottom = 4;
                _deltaTime = 0;
                _beatCounter = 0;
                _barCounter = 0;
            }
        }
        _beatLength = (MidiFile.PulsesPerQuarterNote * 4) / _currentTimeSigBottom;
        _barLength = _beatLength * TimeSigTop;
    }

    /// <summary>The time signatures of the whole song, read ahead when it is loaded.</summary>
    public void ClearTimeSigMap() => _timeSigMap.Clear();

    /// <summary>Adds a time signature change found at a time (in ppqn ticks from the start of the
    /// song). The changes must be added in order. A change within a bar starts at the next bar.</summary>
    public void AddTimeSigChange(long tick, int top, int bottom)
    {
        if (top <= 0 || bottom <= 0)
            return;
        var segment = new TimeSigSegment
        {
            Top = top,
            Bottom = bottom,
            BeatLength = (MidiFile.PulsesPerQuarterNote * 4) / bottom,
        };
        if (segment.BeatLength <= 0)
            return;

        // like Rewind(), the first time signature of the song is used from its start
        if (_timeSigMap.Count > 0)
        {
            TimeSigSegment last = _timeSigMap[^1];
            long barTicks = last.BeatLength * last.Top;
            // the whole bars of the last time signature up to the change, rounded up
            long bars = (tick > last.StartTick) ? (tick - last.StartTick + barTicks - 1) / barTicks : 0;
            segment.StartBar = last.StartBar + (int)bars;
            segment.StartTick = last.StartTick + bars * barTicks;
            if (segment.StartBar == last.StartBar)
            {
                _timeSigMap[^1] = segment; // replaces a change at the same bar
                return;
            }
            if (last.Top == top && last.Bottom == bottom)
                return; // nothing changes
        }
        _timeSigMap.Add(segment);
    }

    private int SegmentOfBar(int bar)
    {
        int found = -1;
        for (int i = 0; i < _timeSigMap.Count; i++)
        {
            if (_timeSigMap[i].StartBar > bar && found >= 0)
                break;
            found = i;
        }
        return found;
    }

    public int BeatsInBar(int bar)
    {
        int segment = SegmentOfBar(bar);
        return segment >= 0 ? _timeSigMap[segment].Top : _currentTimeSigTop;
    }

    /// <summary>The length of a beat of that bar, scaled by Speed.AdjustFactor.</summary>
    public long BeatTicksInBar(int bar)
    {
        int segment = SegmentOfBar(bar);
        return (segment >= 0 ? _timeSigMap[segment].BeatLength : _beatLength) * PianoBooster.Core.Speed.AdjustFactor;
    }

    public int TimeSigTop => _currentTimeSigTop; // The Numerator
    public long BeatLength => _beatLength;
    public long BarLength => _barLength; // in ppqn

    public void SetPlayFromBar(double bar)
    {
        int wholeBar = (int)bar;
        SetPlayFrom(PositionFromBarBeat(wholeBar, (bar - wholeBar) * BeatsInBar(wholeBar)));
    }

    public void SetPlayFrom(BarPosition position)
    {
        _playFrom = position;
        SetupEnableFlags();
        CheckGotoBar();
    }

    public BarPosition PlayFrom => _playFrom;

    public void Reset()
    {
        SetTimeSig(0, 0);
        _playFrom = new BarPosition();
        _playUpto = new BarPosition();
        _loopEnabled = false;
        _seekingBarNumber = false;
        _flushTicks = false;
        _eventBits = 0;
        SetupEnableFlags();
    }

    /// <summary>Repeat from the start position up to (and including) the notes at the end position.
    /// The end is an exact position of its own: changing the start does not move it.</summary>
    public void SetLoop(bool enabled, BarPosition end)
    {
        _loopEnabled = enabled;
        _playUpto = end;
        SetupEnableFlags();
        CheckGotoBar();
    }

    public bool IsLoopEnabled => _loopEnabled;
    public BarPosition PlayUpto => _playUpto;
    /// <summary>True when the loop is enabled and its end is after the start, so it is used.</summary>
    public bool IsLoopActive => _enableLooping;

    // Old interface, in bars with decimals
    public void SetPlayUptoBar(double endBar)
    {
        if (endBar <= 0.0)
        {
            SetLoop(false, _playUpto);
            return;
        }
        int wholeBar = (int)endBar;
        SetLoop(true, PositionFromBarBeat(wholeBar, (endBar - wholeBar) * BeatsInBar(wholeBar)));
    }

    public double PlayUptoBar => BarPosOfPosition(_playUpto);

    public void SetLoopingBars(double bars)
    {
        if (bars <= 0.0)
            SetLoop(false, _playUpto);
        else
            SetPlayUptoBar(BarPosOfPosition(_playFrom) + bars);
    }

    public double LoopingBars => _enableLooping ? PlayUptoBar - BarPosOfPosition(_playFrom) : 0.0;

    public void Rewind()
    {
        int top = _startTimeSigTop;
        int bottom = _startTimeSigBottom;

        SetTimeSig(0, 0);
        SetTimeSig(top, bottom);
        CheckGotoBar();
    }

    public void GetTimeSig(out int top, out int bottom)
    {
        top = _currentTimeSigTop;
        bottom = _currentTimeSigBottom;
    }

    public long AddDeltaTime(long ticks)
    {
        if (_flushTicks && ticks != 0) // ignore this set of ticks
        {
            _flushTicks = false;
            return 0;
        }
        _deltaTime += ticks;
        NormaliseDeltaTime();
        if (ticks != 0 && _enablePlayFromBar)
            CheckGotoBar();

        return ticks;
    }

    /// <summary>Moves the current position by a (possibly negative) number of ticks, without
    /// any of the side effects of AddDeltaTime(); used to stay in step with the conductor.</summary>
    public void AdjustDeltaTime(long ticks)
    {
        _deltaTime += ticks;
        NormaliseDeltaTime();
    }

    public int BarNumber => _barCounter;

    public BarPosition CurrentPosition => new(_barCounter, _beatCounter, _deltaTime);

    /// <summary>Returns the position moved by a (possibly negative) number of ticks,
    /// using the time signature of each bar it goes through.</summary>
    public BarPosition OffsetPosition(BarPosition position, long ticks)
    {
        position.Ticks += ticks;
        long beatTicks = BeatTicksInBar(position.Bar);
        if (beatTicks <= 0)
            return position;

        while (position.Ticks >= beatTicks)
        {
            position.Ticks -= beatTicks;
            if (++position.Beat >= BeatsInBar(position.Bar))
            {
                position.Beat = 0;
                position.Bar++;
                beatTicks = BeatTicksInBar(position.Bar);
                if (beatTicks <= 0)
                    return position;
            }
        }
        while (position.Ticks < 0 && (position.Beat > 0 || position.Bar > 0))
        {
            if (position.Beat > 0)
                position.Beat--;
            else
            {
                position.Bar--;
                position.Beat = BeatsInBar(position.Bar) - 1;
                beatTicks = BeatTicksInBar(position.Bar);
                if (beatTicks <= 0)
                    return position;
            }
            position.Ticks += beatTicks;
        }
        return position;
    }

    /// <summary>
    /// Converts a bar and a beat (both counting from 0, the beat may have a fraction) to a
    /// position, using the time signature of that bar. The fraction is rounded down, so that a
    /// start position is never after the note that the user meant; with roundUp it is rounded
    /// up, so that an end position is never before it.
    /// </summary>
    public BarPosition PositionFromBarBeat(int bar, double beat, bool roundUp = false)
    {
        if (bar < 0)
            bar = 0;
        if (beat < 0.0)
            beat = 0.0;

        long beatTicks = BeatTicksInBar(bar);
        // Beats are typed with two decimals, so an end typed as e.g. 2.33 is meant to
        // include a note at 2 1/3: extend it by the precision of the field.
        if (roundUp)
            beat += EndBeatTolerance;
        int wholeBeats = (int)beat;
        double exactTicks = (beat - wholeBeats) * (double)beatTicks;
        long fractionTicks = (long)(roundUp ? Math.Ceiling(exactTicks) : Math.Floor(exactTicks));
        // OffsetPosition() also moves beats past the end of the bar into the next bars
        return OffsetPosition(new BarPosition(bar, 0, 0), wholeBeats * beatTicks + fractionTicks);
    }

    /// <summary>The beat (counting from 0, with a fraction) of a position.</summary>
    public double BeatOfPosition(BarPosition position)
    {
        long beatTicks = BeatTicksInBar(position.Bar);
        if (beatTicks <= 0)
            return position.Beat;
        return position.Beat + (double)position.Ticks / (double)beatTicks;
    }

    /// <summary>The position as a bar number with the fraction of the bar as decimals.</summary>
    public double BarPosOfPosition(BarPosition position) =>
        position.Bar + BeatOfPosition(position) / (double)BeatsInBar(position.Bar);

    public double CurrentBarPos => BarPosOfPosition(CurrentPosition);

    public bool SeekingBarNumber => _seekingBarNumber;

    /// <summary>Gets and resets the current bar event bits.</summary>
    public uint ReadEventBits()
    {
        uint bits = _eventBits;
        _eventBits = 0;
        return bits;
    }

    /// <summary>Advances the time while seeking the start position: to the next beat, or exactly
    /// to the start position when it is within the current beat.</summary>
    public long GoToBarNumber()
    {
        long ticks;
        if (_barCounter == _playFrom.Bar && _beatCounter == _playFrom.Beat)
            ticks = _playFrom.Ticks - _deltaTime;
        else
            ticks = BeatTicksInBar(_barCounter) - _deltaTime;
        if (ticks <= 0)
            ticks = 1;
        AddDeltaTime(ticks);
        return ticks;
    }

    private void CheckGotoBar()
    {
        if (CurrentPosition < _playFrom)
            _seekingBarNumber = true;
        else
        {
            if (_seekingBarNumber)
                _flushTicks = true; // now throw away ticks before we start the music
            _seekingBarNumber = false;

            // repeat once past the end, so that the notes at the end position are included
            if (_enableLooping && _playUpto < CurrentPosition)
                _eventBits |= EventBits.UptoBarReached;
        }
    }

    private void SetupEnableFlags()
    {
        _enableLooping = _loopEnabled && _playFrom < _playUpto;
        _enablePlayFromBar = _enableLooping || !_playFrom.IsSongStart;
    }

    // Moves the bar and beat counters so that _deltaTime is within the current beat.
    private void NormaliseDeltaTime()
    {
        // the length of the beats changes with the time signature of each bar
        long beatTicks = BeatTicksInBar(_barCounter);
        if (beatTicks <= 0)
            return;

        while (_deltaTime >= beatTicks)
        {
            _deltaTime -= beatTicks;
            _beatCounter++;
            if (_beatCounter >= BeatsInBar(_barCounter))
            {
                _barCounter++;
                _beatCounter = 0;
                _eventBits |= EventBits.NewBarNumber;
                beatTicks = BeatTicksInBar(_barCounter);
                if (beatTicks <= 0)
                    return;
            }
        }
        while (_deltaTime < 0 && (_beatCounter > 0 || _barCounter > 0))
        {
            if (_beatCounter > 0)
                _beatCounter--;
            else
            {
                _barCounter--;
                _beatCounter = BeatsInBar(_barCounter) - 1;
                _eventBits |= EventBits.NewBarNumber;
                beatTicks = BeatTicksInBar(_barCounter);
                if (beatTicks <= 0)
                    return;
            }
            _deltaTime += beatTicks;
        }
    }
}
