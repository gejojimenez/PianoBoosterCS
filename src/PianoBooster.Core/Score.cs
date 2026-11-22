// Ported from Score.h and Score.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
// Only the model: the scrolls of the 16 channels and the piano. The drawing (drawScore,
// drawScroll, drawPianoKeyboard) goes to PianoBooster.Rendering.

namespace PianoBooster.Core;

/// <summary>The score: a scroll for each MIDI channel, of which one is shown, and the piano.</summary>
public sealed class Score
{
    private readonly Piano _piano;
    private Rating? _rating;
    private readonly Scroll[] _scroll = new Scroll[Midi.MaxMidiChannels];
    private int _activeScroll;

    public Score(IPianoSettings settings)
    {
        Settings = settings;
        DrawState.SetDisplayHand(WhichPart.Both); // the CDraw constructor
        _piano = new Piano(settings);
        _rating = null;
        for (int i = 0; i < _scroll.Length; i++)
        {
            _scroll[i] = new Scroll(i, settings);
            _scroll[i].SetChannel(i);
        }
        _activeScroll = -1;
    }

    public IPianoSettings Settings { get; }

    public void Init()
    {
    }

    /// <summary>Adds a midi event to be analysed and displayed on the score.</summary>
    public void MidiEventInsert(in MidiEvent midiEvent)
    {
        for (int i = 0; i < _scroll.Length; i++)
            _scroll[i].MidiEventInsert(midiEvent);
    }

    /// <summary>First check if there is space to add a midi event.</summary>
    public int MidiEventSpace
    {
        get
        {
            int minSpace = 1000;
            foreach (Scroll scroll in _scroll)
            {
                int space = scroll.MidiEventSpace;
                if (space < minSpace)
                    minSpace = space;
            }
            return minSpace;
        }
    }

    public void Transpose(int semitones)
    {
        foreach (Scroll scroll in _scroll)
            scroll.Transpose(semitones);
    }

    public void Reset()
    {
        foreach (Scroll scroll in _scroll)
            scroll.Reset();
    }

    public void DrawScrollingSymbols(bool show = true)
    {
        foreach (Scroll scroll in _scroll)
            scroll.DrawScrollingSymbols(show);
    }

    public void ScrollDeltaTime(long ticks)
    {
        foreach (Scroll scroll in _scroll)
            scroll.ScrollDeltaTime(ticks);
    }

    public void SetRatingObject(Rating rating) => _rating = rating;
    public Rating? RatingObject => _rating;

    public Piano Piano => _piano;

    public void SetPlayedNoteColor(int note, PbColor color, long wantedDelta, long pianistTimming = StavePos.NotUsed)
    {
        if (_activeScroll >= 0)
            _scroll[_activeScroll].SetPlayedNoteColor(note, color, wantedDelta, pianistTimming);
    }

    public void SetActiveChannel(int channel)
    {
        if (channel < 0 || channel >= _scroll.Length)
            return;
        int newActiveSroll = channel;

        if (_activeScroll != newActiveSroll)
        {
            if (_activeScroll >= 0)
                _scroll[_activeScroll].ShowScroll(false);
            _activeScroll = newActiveSroll;
            _scroll[_activeScroll].ShowScroll(true);
        }
    }

    /// <summary>The channel of the scroll shown, -1 for none.</summary>
    public int ActiveScrollIndex => _activeScroll;
    public Scroll? ActiveScroll => _activeScroll >= 0 ? _scroll[_activeScroll] : null;
    public Scroll GetScroll(int channel) => _scroll[channel];

    public void RefreshScroll()
    {
        if (_activeScroll >= 0)
            _scroll[_activeScroll].Refresh();
    }

    public void SetDisplayHand(WhichPart hand)
    {
        DrawState.SetDisplayHand(hand);
        RefreshScroll();
    }
}
