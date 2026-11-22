// Ported from Scroll.h and Scroll.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
// Only the model: the queue of the slots on the screen and their colours. The C++ code
// compiles each slot into an OpenGL display list; here the display list ids are kept (as
// numbers, so that the same checks can be made) and the drawing goes to
// PianoBooster.Rendering, which can watch Revision to know when the slots have changed.

using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>A slot of the scroll queue with the ids of its display lists (the C++ CSlotDisplayList).</summary>
public struct SlotDisplayList
{
    public Slot Slot;
    public int DisplayListId;     // the display list id for this slot (0 when not shown)
    public int NextDisplayListId; // and this points to the next one

    public SlotDisplayList(Slot slot, int displayListId, int nextDisplayListId)
    {
        Slot = slot;
        DisplayListId = displayListId;
        NextDisplayListId = nextDisplayListId;
    }
}

/// <summary>The scrolling notes of one MIDI channel (the C++ CScroll without its drawing).</summary>
public sealed class Scroll
{
    public const int QueueLength = 1000;
    private const int NoteAheadGap = 22;  // the notes on the left hand side of the score
    private const int NoteBehindGap = 14;

    // glGenLists() of the C++ code: any number that is not 0
    private static int s_nextDisplayListId = 1;

    private readonly IPianoSettings _settings;
    private readonly int _id; // There are lots of these class running but each class has a unique id
    private readonly Notation _notation;
    private long _deltaHead;
    private long _deltaTail;

    private int _symbolId; // the next Display List name (or ID) to use
    private Slot _headSlot = new(); // The next slot to be put in at the head of the queue;

    private int _transpose;
    private int _trailOnlySlots; // the slots at the front of the scrollQueue whose note heads have gone and only their trails are left
    private int _wantedIndex;    // The index number of the wanted call in the scrollQueue
    private long _wantedDelta;   // The running delta time of the wanted chord

    private readonly CircularQueue<SlotDisplayList> _scrollQueue; // The current active display list of notes/chords on the screen
    private bool _show; // set to true to show on the screen
    private float _noteSpacingFactor;
    private float _ppqnFactor; // if PulsesPerQuarterNote is 96 then the factor is 1.0

    public Scroll(int id, IPianoSettings settings)
    {
        _settings = settings;
        _id = id;
        _symbolId = 0;
        _trailOnlySlots = 0;
        DrawState.SetDisplayHand(WhichPart.Both); // the CDraw constructor

        _notation = new Notation();
        _scrollQueue = new CircularQueue<SlotDisplayList>(QueueLength);
        Reset();
        _show = false;
        _noteSpacingFactor = 1.0f;
        _ppqnFactor = 1.0f;
        _transpose = 0;
    }

    internal static void ResetStatics() => s_nextDisplayListId = 1;

    private static int GenList() => s_nextDisplayListId++;

    public int Id => _id;
    public bool Shown => _show;
    public int Transposition => _transpose;
    public float NoteSpacingFactor => _noteSpacingFactor;
    /// <summary>The time (scaled by Speed.AdjustFactor) from the play zone to the end of the first slot.</summary>
    public long DeltaTail => _deltaTail;
    public long DeltaHead => _deltaHead;
    public int WantedIndex => _wantedIndex;
    public long WantedDelta => _wantedDelta;
    public int TrailOnlySlots => _trailOnlySlots;
    /// <summary>The slots on the screen, the first one is the leftmost.</summary>
    public CircularQueue<SlotDisplayList> ScrollQueue => _scrollQueue;
    /// <summary>Goes up each time the slots change (the C++ code compiles a display list then).</summary>
    public int Revision { get; private set; }

    public void SetChannel(int chan) => _notation.SetChannel(chan);

    /// <summary>Adds a midi event to be analysed and displayed on the score.</summary>
    public void MidiEventInsert(in MidiEvent midiEvent) => _notation.MidiEventInsert(midiEvent);

    /// <summary>First check if there is space to add a midi event.</summary>
    public int MidiEventSpace => _notation.MidiEventSpace;

    private void CompileSlot(in SlotDisplayList info)
    {
        if (!_show || info.DisplayListId == 0)
            return;
        Revision++;
    }

    /// <summary>Inserts the slots into the queue up to the right side of the score;
    /// returns false when we have run out of symbols.</summary>
    private bool InsertSlots()
    {
        int nextListId;

        if (_headSlot.Length == 0)
            _headSlot = _notation.NextSlot();
        if (_headSlot.Length == 0 || _headSlot.GetSymbolType(0) == MusicalSymbol.TheEndMarker) // this means we have reached the end of the file
            return false;

        while (true)
        {
            float headDelta = Speed.DeltaAdjustF(_deltaHead) * _noteSpacingFactor;
            float slotDetlta = Cfg.StaveEndX - Cfg.PlayZoneX - (float)_headSlot.DeltaTime * _noteSpacingFactor - NoteBehindGap;

            if (headDelta > slotDetlta)
                break;

            if (_show)
            {
                if (_symbolId == 0)
                    _symbolId = GenList();
                nextListId = GenList();
            }
            else
            {
                nextListId = _symbolId = 0;
            }

            var info = new SlotDisplayList(_headSlot, _symbolId, nextListId);

            _deltaHead += info.Slot.DeltaTime * Speed.AdjustFactor;

            CompileSlot(info);

            _scrollQueue.Push(info);
            _symbolId = nextListId;

            _headSlot = _notation.NextSlot();
            if (_headSlot.Length == 0 || _headSlot.GetSymbolType(0) == MusicalSymbol.TheEndMarker) // this means we have reached the end of the file
                return false;
        }
        return true;
    }

    private void RemoveEarlyTimingMakers()
    {
        float delta = Speed.DeltaAdjustF(_deltaTail) * _noteSpacingFactor + Cfg.PlayZoneX - Cfg.ScrollStartX - NoteAheadGap;
        // only look a few steps (10) into the scroll queue (after the slots kept only for their trails)
        for (int i = 0; i < _trailOnlySlots + 10 && i < _scrollQueue.Length; i++)
        {
            if (delta < -((float)_scrollQueue.Index(i).Slot.LeftSideDeltaTime * _noteSpacingFactor))
            {
                ref SlotDisplayList slot = ref _scrollQueue.IndexRef(i);
                bool hasTimings = false;
                for (int j = 0; j < slot.Slot.Length; j++)
                {
                    if (slot.Slot.GetSymbol(j).PianistTiming != StavePos.NotUsed)
                        hasTimings = true;
                }
                if (hasTimings) // the slots kept for their trails stay here a while, so only compile them once
                {
                    slot.Slot.ClearAllNoteTimings();
                    CompileSlot(slot);
                }
            }
            delta += (float)_scrollQueue.Index(i).Slot.DeltaTime * _noteSpacingFactor;
        }
    }

    private bool IsNoteDurationTrailEnabled => _settings.IsNoteDurationTrailEnabled;

    // the length of the longest note duration trail in this slot
    public static float SlotTrailLength(in Slot slot)
    {
        float length = 0.0f;
        for (int i = 0; i < slot.Length; i++)
        {
            if (slot.GetSymbol(i).Type == MusicalSymbol.NoteHead)
                length = System.Math.Max(length, DrawState.NoteDurationTrailLength(slot.GetSymbol(i).MidiDuration));
        }
        return length;
    }

    // The x position of the left side of a slot.
    // slotStartX is where the slots before it end (the position that its delta time is measured from)
    private float SlotLeftSideX(int index, float slotStartX) =>
        slotStartX + (float)_scrollQueue.Index(index).Slot.LeftSideDeltaTime * _noteSpacingFactor;

    private void RemoveSlots()
    {
        while (_scrollQueue.Length > 0)
        {
            float slotStartX = Cfg.PlayZoneX + Speed.DeltaAdjustF(_deltaTail) * _noteSpacingFactor;
            if (SlotLeftSideX(0, slotStartX) > Cfg.ScrollStartX + NoteAheadGap)
                break;

            // The note heads have gone, but keep the slot until its trails have also gone past the left edge
            // (a long note that is still sounding must not disappear)
            if (IsNoteDurationTrailEnabled)
            {
                float slotX = slotStartX + (float)_scrollQueue.Index(0).Slot.DeltaTime * _noteSpacingFactor;
                if (slotX + SlotTrailLength(_scrollQueue.IndexRef(0).Slot) > Cfg.ScrollStartX)
                    break;
            }

            SlotDisplayList info = _scrollQueue.Pop();

            _deltaTail += info.Slot.DeltaTime * Speed.AdjustFactor;

            if (_wantedIndex > 0)
                _wantedIndex--; // also the Chord has moved down one place
            else
            {
                _wantedIndex = 0;
                _wantedDelta = _deltaTail;
            }
        }

        // count the slots at the front that are only kept for their trails
        _trailOnlySlots = 0;
        if (IsNoteDurationTrailEnabled)
        {
            float slotStartX = Cfg.PlayZoneX + Speed.DeltaAdjustF(_deltaTail) * _noteSpacingFactor;
            while (_trailOnlySlots < _scrollQueue.Length &&
                   SlotLeftSideX(_trailOnlySlots, slotStartX) <= Cfg.ScrollStartX + NoteAheadGap)
            {
                slotStartX += (float)_scrollQueue.Index(_trailOnlySlots).Slot.DeltaTime * _noteSpacingFactor;
                _trailOnlySlots++;
            }
        }
    }

    /// <summary>
    /// Brings the queue up to date: new symbols at the end of the score, old ones removed.
    /// The C++ code then draws the symbols when show is true; that is done by the renderer.
    /// </summary>
    public void DrawScrollingSymbols(bool show)
    {
        InsertSlots();  // new symbols at the end of the score
        RemoveSlots();  // delete old symbols no longer required
        RemoveEarlyTimingMakers();
    }

    public void ScrollDeltaTime(long ticks)
    {
        _deltaHead -= ticks;
        _deltaTail -= ticks;
        _wantedDelta -= ticks;
    }

    private bool ValidPianistChord(int index)
    {
        ref SlotDisplayList pSlot = ref _scrollQueue.IndexRef(index);

        if (pSlot.Slot.GetSymbol(0).Type >= MusicalSymbol.NoteHead)
        {
            if (DrawState.DisplayHand == WhichPart.Both)
                return true;

            //eventually we need two slot queues one for each hand
            for (int i = 0; i < pSlot.Slot.Length; i++)
            {
                if (pSlot.Slot.GetSymbol(i).Hand == DrawState.DisplayHand)
                    return true;
            }
        }
        return false;
    }

    private int FindWantedChord(int note, PbColor color, long wantedDelta)
    {
        if (color == Cfg.PlayedBadColor) // fixme should be an enum
            return _wantedIndex;
        while (_wantedIndex + 1 < _scrollQueue.Length)
        {
            if ((_wantedDelta + _scrollQueue.IndexRef(_wantedIndex).Slot.DeltaTime * Speed.AdjustFactor) >= -wantedDelta)
            {
                if (ValidPianistChord(_wantedIndex))
                    break;
            }
            _wantedDelta += _scrollQueue.IndexRef(_wantedIndex).Slot.DeltaTime * Speed.AdjustFactor;
            _wantedIndex++;
        }
        return _wantedIndex;
    }

    public void SetPlayedNoteColor(int note, PbColor color, long wantedDelta, long pianistTimming)
    {
        if (_wantedIndex >= _scrollQueue.Length)
            return;
        int index = FindWantedChord(note, color, wantedDelta);
        note -= _transpose;
        _scrollQueue.IndexRef(index).Slot.SetNoteColor(note, color);
        if (pianistTimming != StavePos.NotUsed)
        {
            pianistTimming = Speed.DeltaAdjustL(pianistTimming) * MidiFile.DefaultPpqn / MidiFile.PulsesPerQuarterNote;
            _scrollQueue.IndexRef(index).Slot.SetNoteTiming(note, pianistTimming);
        }
        CompileSlot(_scrollQueue.Index(index));
    }

    public void Refresh()
    {
        if (!_show)
            return;
        for (int i = 0; i < _scrollQueue.Length; i++)
            CompileSlot(_scrollQueue.Index(i));
    }

    /// <summary>
    /// The notes to light up on the keyboard (View > Show Piano Keyboard), and whether the
    /// music is stopped waiting for them. It also changes the colour of some notes, like the C++ code.
    /// </summary>
    public bool GetKeyboardInfo(List<int> notes)
    {
        // Skip the slots that have already gone past the play zone. Nobody changes the colour of
        // their notes when just listening, so they would still be lit up on the keyboard
        // (several beats behind the music, as far as the left edge of the score).
        float lateEdgeX = Cfg.PlayZoneX - (float)Cfg.PlayZoneLate * DrawState.HorizontalSpacingFactor;
        float slotX = Cfg.PlayZoneX + Speed.DeltaAdjustF(_deltaTail) * _noteSpacingFactor;
        int firstSlot = 0;
        while (firstSlot < _scrollQueue.Length)
        {
            slotX += (float)_scrollQueue.Index(firstSlot).Slot.DeltaTime * _noteSpacingFactor;
            if (slotX >= lateEdgeX)
                break;
            firstSlot++;
        }
        int stoppedScrollIdx = -1;
        for (int i = firstSlot; i < _scrollQueue.Length; ++i)
        {
            ref SlotDisplayList info = ref _scrollQueue.IndexRef(i);
            if (!_show || info.DisplayListId == 0) continue;

            for (int j = 0; j < info.Slot.Length; ++j)
            {
                if (info.Slot.GetSymbol(j).Type < MusicalSymbol.NoteHead) continue;
                if (info.Slot.GetSymbol(j).Color == Cfg.PlayedStoppedColor)
                {
                    stoppedScrollIdx = i;
                    break;
                }
            }
            if (stoppedScrollIdx > -1) break;
        }
        if (stoppedScrollIdx > -1)
        {
            for (int i = firstSlot; i < stoppedScrollIdx; ++i)
            {
                ref SlotDisplayList info = ref _scrollQueue.IndexRef(i);
                if (!_show || info.DisplayListId == 0) continue;

                for (int j = 0; j < info.Slot.Length; ++j)
                {
                    if (info.Slot.GetSymbol(j).Type < MusicalSymbol.NoteHead) continue;
                    info.Slot.GetSymbolRef(j).Color = Cfg.PlayedGoodColor;
                }
            }
        }

        for (int i = firstSlot; i < _scrollQueue.Length; ++i)
        {
            ref SlotDisplayList info = ref _scrollQueue.IndexRef(i);
            if (!_show || info.DisplayListId == 0) continue;

            bool stopped = false;
            int before = notes.Count;
            for (int j = 0; j < info.Slot.Length; ++j)
            {
                Symbol symbol = info.Slot.GetSymbol(j);
                if (symbol.Type < MusicalSymbol.NoteHead) continue;

                if (symbol.Color == Cfg.NoteColor || symbol.Color == Cfg.PlayedStoppedColor)
                    notes.Add(symbol.Note);
                if (symbol.Color == Cfg.PlayedStoppedColor) stopped = true;
            }
            if (notes.Count != before) return stopped;
        }
        return false;
    }

    public void Transpose(int transpose)
    {
        if (_transpose == transpose)
            return;
        _transpose = transpose;
        Refresh();
    }

    public void ShowScroll(bool show)
    {
        int nextListId = 0;

        _show = show;
        if (show)
        {
            if (_symbolId == 0)
                _symbolId = GenList();

            // add in the missing display lists
            for (int i = 0; i < _scrollQueue.Length; i++)
            {
                nextListId = GenList();
                _scrollQueue.IndexRef(i).DisplayListId = _symbolId;
                _scrollQueue.IndexRef(i).NextDisplayListId = nextListId;
                _symbolId = nextListId;
            }
            // And now compile the slot (remember that each slot points to the next one)
            for (int i = 0; i < _scrollQueue.Length; i++)
                CompileSlot(_scrollQueue.Index(i));
        }
        else
        {
            // Remove all the display lists
            for (int i = 0; i < _scrollQueue.Length; i++)
            {
                _scrollQueue.IndexRef(i).DisplayListId = 0;
                _scrollQueue.IndexRef(i).NextDisplayListId = 0;
            }
            _symbolId = 0;
        }
    }

    public void Reset()
    {
        _wantedIndex = 0;
        _wantedDelta = 0;
        _trailOnlySlots = 0;
        _deltaHead = _deltaTail = 0;
        _notation.Reset();
        _headSlot.Clear();
        _symbolId = 0;

        _scrollQueue.Clear();
        _ppqnFactor = (float)MidiFile.DefaultPpqn / (float)MidiFile.PulsesPerQuarterNote;
        _noteSpacingFactor = _ppqnFactor * DrawState.HorizontalSpacingFactor;
    }
}
