// Ported from Notation.h and Notation.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PianoBooster.Core;

[InlineArray(Slot.MaxSymbols)]
internal struct SlotSymbols
{
    private Symbol _element;
}

/// <summary>The symbols drawn at the same place of the score (a chord, a bar line ...). A value.</summary>
public struct Slot
{
    public const int MaxSymbols = 20; // The maximum number of symbols that can be stored in one slot

    private long _deltaTime;
    private SlotSymbols _symbols;
    private int _length;
    private int _av8Left;
    private int _av8Right;
    private long _maxLeftEdge; // the furthest the note will appear on the left hand edge (used when removing the note)

    public Slot()
    {
        for (int i = 0; i < MaxSymbols; i++)
            _symbols[i] = new Symbol();
        Clear();
    }

    public void Clear()
    {
        _length = 0;
        _deltaTime = 0;
        _av8Left = 0;
        _av8Right = 0;
        _maxLeftEdge = 0;
    }

    public readonly Symbol GetSymbol(int index) => _symbols[index];
    [UnscopedRef] public ref Symbol GetSymbolRef(int index) => ref _symbols[index];
    public readonly MusicalSymbol GetSymbolType(int index) => _symbols[index].Type;
    public readonly int Length => _length;
    public long DeltaTime { readonly get => _deltaTime; set => _deltaTime = value; }
    public void AddDeltaTime(long delta) => _deltaTime += delta;
    public readonly long LeftSideDeltaTime => _deltaTime + _maxLeftEdge;

    public int Av8Left { readonly get => _av8Left; set => _av8Left = value; }

    /// <summary>Adds a symbol, keeping them sorted by note; returns false when the slot is full.</summary>
    public bool AddSymbol(Symbol symbol)
    {
        int i;
        if (_length >= MaxSymbols)
            return false;

        // Sort the entries low to high
        for (i = _length - 1; i >= 0; i--)
        {
            if (_symbols[i].Note <= symbol.Note)
            {
                // don't add duplicates
                if (_symbols[i].Note == symbol.Note &&
                    _symbols[i].Type == symbol.Type &&
                    _symbols[i].Hand == symbol.Hand)
                    return true;
                break;
            }
            // move the previous entry up one position
            _symbols[i + 1] = _symbols[i];
        }
        _symbols[i + 1] = symbol;
        _length++;
        return true;
    }

    public void Analyse()
    {
        int rightIndex = 0;
        int leftIndex = 0;
        int rightTotal = 0;
        int leftTotal = 0;

        for (int i = 0; i < _length; i++)
        {
            if (_symbols[i].Type >= MusicalSymbol.NoteHead)
            {
                if (_symbols[i].Hand == WhichPart.Right)
                    rightTotal++;
                else if (_symbols[i].Hand == WhichPart.Left)
                    leftTotal++;
            }
        }
        for (int i = 0; i < _length; i++)
        {
            if (_symbols[i].Type >= MusicalSymbol.NoteHead)
            {
                if (_symbols[i].Hand == WhichPart.Right)
                    _symbols[i].SetIndex(rightIndex++, rightTotal);
                else if (_symbols[i].Hand == WhichPart.Left)
                    _symbols[i].SetIndex(leftIndex++, leftTotal);
            }
        }
    }

    public void SetSymbol(long delta, Symbol symbol)
    {
        Clear();
        DeltaTime = delta;
        AddSymbol(symbol);
    }

    public void Transpose(int amount)
    {
        for (int i = 0; i < _length; i++)
            _symbols[i].Transpose(amount);
    }

    public void SetNoteColor(int note, PbColor color)
    {
        for (int i = 0; i < _length; i++)
        {
            if (note == _symbols[i].Note || note == 0)
                _symbols[i].Color = color;
        }
    }

    public void SetNoteTiming(int note, long timing)
    {
        for (int i = 0; i < _length; i++)
        {
            if (note == _symbols[i].Note || note == 0)
                _symbols[i].PianistTiming = timing;
        }
        if (timing < _maxLeftEdge)
            _maxLeftEdge = timing;
    }

    public void ClearAllNoteTimings()
    {
        for (int i = 0; i < _length; i++)
            _symbols[i].PianistTiming = StavePos.NotUsed;
        _maxLeftEdge = 0;
    }
}

/// <summary>Remembers the state of a running accidental: don't display the accidental twice in
/// the same bar.</summary>
internal struct NoteState
{
    public int BarChange;
    public AccidentalModifier AccidentalState;
    public int NoteLength; // Used to determine the note length
    public int BackLink;   // the note whose state links back here, -1 for none (a pointer in C++)

    public void Clear()
    {
        BarChange = -1;
        AccidentalState = AccidentalModifier.NoChange;
        NoteLength = 0;
        BackLink = -1;
    }
}

/// <summary>Turns the MIDI events of one channel into the slots of the score, with the bar
/// lines and the beat markers.</summary>
public sealed class Notation
{
    private const int MergeSlotNoteIndex = 0;
    private const int MergeSlotBeatMarkIndex = 1;

    private const int SemiquaverBoundary = 1; // the indices of the C++ cfg_param
    private const int QuaverBoundary = 2;
    private const int CrotchetBoundary = 3;
    private const int MinimBoundary = 4;
    private const int SemibreveBoundary = 5;
    private const int MaxParams = 7;

    private readonly CircularQueue<Slot> _slotQueue;          // Queue of symbol slots that have not been read yet
    private readonly CircularQueue<MidiEvent> _midiInputQueue; // A Queue of midi events
    private Slot _currentSlot = new();
    private long _currentDeltaTime;   // time difference between this and the previous slot
    private long _noteStreamTime;     // absolute time of the midi events read so far

    // The beat markers and bar lines are generated separately from the notes, which are read
    // ahead, so time signature changes are kept here until the markers reach their time.
    private readonly Queue<(long Time, int Top, int Bottom)> _markerTimeSigChanges = new();
    private int _markerTimeSigTop;      // the time signature used by the beat markers
    private int _markerTimeSigBottom;
    private long _beatStartTime;        // absolute time of the start of the current beat
    private long _markerEmitTime;       // absolute time of the last beat marker returned
    private bool _pendingBarMarker;     // a bar marker follows the bar line just returned
    private long _pendingBarMarkerTime;
    private int _beatPerBarCounter;     // the current beat in the bar, counting from 0
    private int _markerBarNumber;       // the current bar of the beat markers, counting from 0
    // The bars used to show an accidental only once in each bar. They follow the same
    // rules as the bar lines: a new time signature always starts a new bar.
    private int _accidentalBarCounter;  // the current bar of the notes, counting from 0
    private long _accidentalBarStart;   // absolute time of the start of that bar
    private int _accidentalTimeSigTop;  // the time signature of that bar
    private int _accidentalTimeSigBottom;
    private readonly Slot[] _mergeSlots = { new(), new() };
    private int _displayChannel;
    private readonly FindChord _findScrollerChord = new();
    private readonly NoteState[] _noteState = new NoteState[Midi.MaxMidiNotes];
    private static bool s_displayCourtesyAccidentals;
    private static readonly int[] s_cfgParam = new int[MaxParams];

    public Notation()
    {
        _midiInputQueue = new CircularQueue<MidiEvent>(1000);
        _slotQueue = new CircularQueue<Slot>(200);
        Reset();
        _displayChannel = 0;
    }

    public void SetChannel(int channel) => _displayChannel = channel;

    public int MidiEventSpace => _midiInputQueue.Space;

    public static bool DisplayCourtesyAccidentals
    {
        get => s_displayCourtesyAccidentals;
        set => s_displayCourtesyAccidentals = value;
    }

    internal static void ResetStatics()
    {
        s_displayCourtesyAccidentals = false;
        Array.Clear(s_cfgParam);
    }

    private Slot NextBeatMarker()
    {
        int cfgBarGap = MidiFile.PpqnAdjust(30);

        var slot = new Slot();

        // the bar marker at the start of a bar, just after its bar line
        if (_pendingBarMarker)
        {
            _pendingBarMarker = false;
            var barMarker = new Symbol(MusicalSymbol.BarMarker, WhichPart.Both, 0);
            barMarker.BarNumber = ++_markerBarNumber; // the bar that starts here
            slot.SetSymbol(_pendingBarMarkerTime - _markerEmitTime, barMarker);
            _markerEmitTime = _pendingBarMarkerTime;
            return slot;
        }

        // Use the time signature of the beat that is starting; a new time signature
        // always starts a new bar.
        while (_markerTimeSigChanges.Count > 0 && _markerTimeSigChanges.Peek().Time <= _beatStartTime)
        {
            var change = _markerTimeSigChanges.Dequeue();
            _markerTimeSigTop = (change.Top > 0) ? change.Top : 4;
            _markerTimeSigBottom = (change.Bottom > 0) ? change.Bottom : 4;
            _beatPerBarCounter = 0;
        }

        long beatLength = (MidiFile.PulsesPerQuarterNote * 4) / _markerTimeSigBottom;
        long nextBeatTime = _beatStartTime + beatLength;
        bool newBar = _beatPerBarCounter + 1 >= _markerTimeSigTop;
        // a time signature change within this beat ends the bar there
        if (_markerTimeSigChanges.Count > 0 && _markerTimeSigChanges.Peek().Time <= nextBeatTime)
        {
            nextBeatTime = _markerTimeSigChanges.Peek().Time;
            newBar = true;
        }
        _beatStartTime = nextBeatTime;

        if (newBar)
        {
            // Sneak in a bar line just before the bar starts, followed by the bar marker
            _beatPerBarCounter = 0;
            long barLineTime = nextBeatTime - Math.Min((long)cfgBarGap, (nextBeatTime - _markerEmitTime) / 2);
            slot.SetSymbol(barLineTime - _markerEmitTime, new Symbol(MusicalSymbol.BarLine, WhichPart.Both, 0));
            _markerEmitTime = barLineTime;
            _pendingBarMarker = true;
            _pendingBarMarkerTime = nextBeatTime;
        }
        else
        {
            _beatPerBarCounter++;
            slot.SetSymbol(nextBeatTime - _markerEmitTime, new Symbol(MusicalSymbol.BeatMarker, WhichPart.Both, 0));
            _markerEmitTime = nextBeatTime;
        }
        return slot;
    }

    private int NextMergeSlot()
    {
        int nearestIndex = 0;
        long nearestDelta = _mergeSlots[0].DeltaTime;
        for (int i = 1; i < _mergeSlots.Length; i++)
        {
            // find the slot with the lowest delta time
            if (_mergeSlots[i].DeltaTime < nearestDelta)
            {
                nearestDelta = _mergeSlots[i].DeltaTime;
                nearestIndex = i;
            }
        }

        // Now subtract the delta time from all the others
        for (int i = 0; i < _mergeSlots.Length; ++i)
        {
            if (i == nearestIndex)
                continue;
            _mergeSlots[i].AddDeltaTime(-nearestDelta);
        }
        return nearestIndex;
    }

    private long AccidentalBarLength() =>
        (MidiFile.PulsesPerQuarterNote * 4) / _accidentalTimeSigBottom * _accidentalTimeSigTop;

    // Moves on to the bar that is playing at this time (absolute, in ppqn ticks)
    private void AdvanceAccidentalBars(long time)
    {
        long barLength = AccidentalBarLength();
        if (barLength <= 0)
            return;
        while (time >= _accidentalBarStart + barLength)
        {
            _accidentalBarStart += barLength;
            _accidentalBarCounter++;
        }
    }

    private AccidentalModifier DetectSuppressedNatural(int note)
    {
        if (note <= 0 || note + 1 >= Midi.MaxMidiNotes)
            return AccidentalModifier.NoChange;

        AccidentalModifier modifier = AccidentalModifier.NoChange;

        // We want to detect the bar change a little early
        long cfgEarlyBarLead = MidiFile.PpqnAdjust(8);
        AdvanceAccidentalBars(_noteStreamTime + cfgEarlyBarLead);

        ref NoteState noteState = ref _noteState[note];
        int backLink = noteState.BackLink;

        int direction = -StavePos.GetStaveAccidentalDirection(note);
        // check if this note has occurred in this bar before
        if (noteState.BarChange == _accidentalBarCounter)
        {
            if (backLink >= 0)
                modifier = AccidentalModifier.Force;
            else if (direction != 0 && !s_displayCourtesyAccidentals)
                modifier = AccidentalModifier.Suppress;
        }

        if (direction != 0)
        {
            // we are display a accidental so force the note above (or below) to display
            _noteState[note + direction].BackLink = note; // point back to this note
            _noteState[note + direction].BarChange = _accidentalBarCounter;
        }
        if (backLink >= 0)
        {
            noteState.BackLink = -1;
            _noteState[backLink].BarChange = -1; // this prevents further suppression on the original note
        }

        noteState.BarChange = _accidentalBarCounter;
        return modifier;
    }

    private static void SetupNotationParameters()
    {
        s_cfgParam[SemiquaverBoundary] = MidiFile.PpqnAdjust(MidiFile.DefaultPpqn / 4 + 10);
        s_cfgParam[QuaverBoundary] = MidiFile.PpqnAdjust(MidiFile.DefaultPpqn / 2 + 10);
        s_cfgParam[CrotchetBoundary] = MidiFile.PpqnAdjust(MidiFile.DefaultPpqn + 10);
        s_cfgParam[MinimBoundary] = MidiFile.PpqnAdjust(MidiFile.DefaultPpqn * 2 + 10);
        s_cfgParam[SemibreveBoundary] = MidiFile.PpqnAdjust(MidiFile.DefaultPpqn * 4 + 10);
    }

    private void CalculateScoreNoteLength()
    {
        if (!Cfg.ExperimentalNoteLength)
            return;

        ref Slot slot = ref _slotQueue.IndexRef(0);
        for (int i = 0; i < slot.Length; i++)
        {
            ref Symbol symbol = ref slot.GetSymbolRef(i);

            if (symbol.Type != MusicalSymbol.NoteHead)
                break;

            long midiDuration = symbol.MidiDuration;

            if (midiDuration < s_cfgParam[SemiquaverBoundary])
                symbol.SetNoteLength(MusicalSymbol.Semiquaver);
            if (midiDuration < s_cfgParam[QuaverBoundary])
                symbol.SetNoteLength(MusicalSymbol.Quaver);
            else if (midiDuration < s_cfgParam[CrotchetBoundary])
                symbol.SetNoteLength(MusicalSymbol.Crotchet);
            else if (midiDuration < s_cfgParam[MinimBoundary])
                symbol.SetNoteLength(MusicalSymbol.Minim);
            else
                symbol.SetNoteLength(MusicalSymbol.Semibreve);
        }
    }

    private void FindNoteSlots()
    {
        var slot = new Slot();

        while (true)
        {
            // Check that some body has put in some events for us
            if (_midiInputQueue.Length == 0)
                break;

            MidiEvent midi = _midiInputQueue.Pop();

            _currentDeltaTime += midi.DeltaTime;
            _noteStreamTime += midi.DeltaTime;
            if (midi.Type == Midi.PbChordSeparator || midi.Type == Midi.PbEof)
            {
                if (_currentSlot.Length > 0)
                {
                    // the cord separator arrives very late so we are behind the times
                    _currentSlot.Analyse();
                    _slotQueue.Push(_currentSlot);
                    _currentSlot.Clear();
                }
                if (midi.Type == Midi.PbEof)
                {
                    slot.SetSymbol(0, new Symbol(MusicalSymbol.TheEndMarker, WhichPart.Both, 0));
                    _slotQueue.Push(slot);
                }
                break;
            }
            else if (midi.Type == Midi.PbTimeSignature)
            {
                // finish the bars of the previous time signature, then a new bar starts here
                // (unless one has just started), like the bar lines of the beat markers
                AdvanceAccidentalBars(_noteStreamTime);
                if (_noteStreamTime > _accidentalBarStart)
                {
                    _accidentalBarCounter++;
                    _accidentalBarStart = _noteStreamTime;
                }
                _accidentalTimeSigTop = (midi.Data1 > 0) ? midi.Data1 : 4;
                _accidentalTimeSigBottom = (midi.Data2 > 0) ? midi.Data2 : 4;
                // the beat markers use it once they reach this time
                _markerTimeSigChanges.Enqueue((_noteStreamTime, midi.Data1, midi.Data2));
            }
            else if (midi.Type == Midi.PbKeySignature)
                StavePos.SetKeySignature(midi.Data1, midi.Data2);
            else if (midi.Type == Midi.NoteOn)
            {
                WhichPart hand = Note.FindHand(midi, _displayChannel, WhichPart.Both);
                if (hand != WhichPart.None)
                {
                    MusicalSymbol symbolType = (midi.Channel == Midi.DrumChannel) ? MusicalSymbol.Drum : MusicalSymbol.NoteHead;
                    var symbol = new Symbol(symbolType, hand, midi.Note);
                    symbol.Color = Cfg.NoteColor;
                    symbol.MidiDuration = midi.Duration;

                    // check if this note has occurred in this bar before
                    symbol.AccidentalModifier = DetectSuppressedNatural(midi.Note);

                    if (!_currentSlot.AddSymbol(symbol))
                        Log.Warn($"[{_displayChannel + 1}] Over the Max symbols limit");
                    _currentSlot.AddDeltaTime(_currentDeltaTime);
                    _currentDeltaTime = 0;
                    if (hand == WhichPart.Left)
                    {
                        if (midi.Note < Midi.BottomC)
                            _currentSlot.Av8Left = Midi.Octave;
                    }
                }
            }
        }
    }

    private Slot NextNoteSlot()
    {
        // only if the slot queue is empty should we try to find some more
        if (_slotQueue.Length == 0)
            FindNoteSlots();

        if (_slotQueue.Length > 0)
        {
            CalculateScoreNoteLength();
            return _slotQueue.Pop();
        }
        return new Slot(); // this is an empty slot which means end of file
    }

    /// <summary>The next slot of the score (notes and markers merged in order of time).</summary>
    public Slot NextSlot()
    {
        if (_mergeSlots[MergeSlotBeatMarkIndex].Length == 0)
        {
            // load up the two slots on start up
            _mergeSlots[MergeSlotNoteIndex] = NextNoteSlot();
            _mergeSlots[MergeSlotBeatMarkIndex] = NextBeatMarker();

            // This inserts the beat markers into the queue early (so they get drawn underneath)
            _mergeSlots[MergeSlotBeatMarkIndex].AddDeltaTime(
                -MidiFile.PulsesPerQuarterNote * Symbol.BeatMarkerOffset / MidiFile.DefaultPpqn);
        }

        if (_mergeSlots[0].GetSymbolType(0) == MusicalSymbol.TheEndMarker)
            return _mergeSlots[0];

        int mergeIdx = NextMergeSlot();
        Slot slot = _mergeSlots[mergeIdx];
        if (mergeIdx == 0)
            _mergeSlots[mergeIdx] = NextNoteSlot();
        else
            _mergeSlots[mergeIdx] = NextBeatMarker();
        return slot;
    }

    public void MidiEventInsert(in MidiEvent midiEvent)
    {
        if (_findScrollerChord.Find(midiEvent, _displayChannel, WhichPart.Both))
        {
            // the Score works differently we just send down a chord separator
            var separator = new MidiEvent();
            separator.ChordSeparator(midiEvent);
            _midiInputQueue.Push(separator);
        }
        _midiInputQueue.Push(midiEvent);
    }

    public void Reset()
    {
        _currentDeltaTime = 0;
        _midiInputQueue.Clear();
        _slotQueue.Clear();
        for (int i = 0; i < _mergeSlots.Length; i++)
            _mergeSlots[i].Clear();
        _currentSlot.Clear();
        _beatPerBarCounter = 0;
        _noteStreamTime = 0;
        _markerTimeSigChanges.Clear();
        _markerTimeSigTop = 4;
        _markerTimeSigBottom = 4;
        _beatStartTime = 0;
        _markerEmitTime = 0;
        _pendingBarMarker = false;
        _markerBarNumber = 0;
        _pendingBarMarkerTime = 0;
        _accidentalBarCounter = 0;
        _accidentalBarStart = 0;
        _accidentalTimeSigTop = 4;
        _accidentalTimeSigBottom = 4;
        _findScrollerChord.Reset();
        for (int i = 0; i < _noteState.Length; i++)
            _noteState[i].Clear();
        SetupNotationParameters();
    }
}
