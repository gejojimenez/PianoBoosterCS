// The state of the song and the score that one frame of the practice window draws. Part of
// the .NET port of PianoBooster. GPLv3.
//
// In C++ everything runs in the thread of the interface, and paintGL() reads the song and the
// score while it draws. Here the song runs in the thread of the conductor, so each frame first
// takes this "photo" holding the lock of the song (ViewRenderer.PrepareFrame), which is quick,
// and then draws it without the lock (ViewRenderer.Render), maybe in another thread.

using System.Collections.Generic;
using PianoBooster.Core;

namespace PianoBooster.Rendering;

/// <summary>The slots of a scroll that are drawn (the chain of its display lists).</summary>
public sealed class ScrollFrame
{
    /// <summary>Where the first slot is measured from: playZoneX + deltaTail (CScroll::drawScrollingSymbols).</summary>
    public float StartX;
    /// <summary>The centre of the staves (the glTranslatef of the y).</summary>
    public float StaveCenterY;
    /// <summary>The display lists, in the order they call each other. They never change.</summary>
    public readonly List<CompiledSlot> Slots = new();
}

/// <summary>Everything a frame draws, copied from the song and the score.</summary>
public sealed class FrameSnapshot
{
    public const int KeysCount = 88;

    // ---- the statics and settings that the drawing reads ----
    public WhichPart DisplayHand;
    public bool ShowNoteNames;
    public bool ColoredNotes;
    public bool NoteDurationTrail;
    public bool ShowPianoKeyboard;
    public int KeySignature;
    /// <summary>The score and the staves must be drawn again (CDraw::getCompileRedrawCount()).</summary>
    public bool RecompileScore;

    // ---- CGLView ----
    /// <summary>The size of the window (resizeGL()) and where the score is in it (y down).</summary>
    public int WindowWidth;
    public int WindowHeight;
    public SkiaSharp.SKRectI Viewport;
    public int TitleHeight;
    /// <summary>False the first time (CGLView::drawDisplayText does not draw then).</summary>
    public bool HasRating;
    public PlayMode PlayMode;
    public string WarningMessage = "";
    public string SongTitle = "";
    public int BarNumber;
    public int TimeSigTop;
    public int TimeSigBottom;
    public bool DrawAccuracyBar;
    public float AccuracyValue;
    public PbColor AccuracyColor;

    // ---- CScore / CScroll ----
    public readonly List<ScrollFrame> Scrolls = new();
    /// <summary>The trails can go past the ends of the scrolling part of the stave: cut them off.</summary>
    public bool ClipScroll;

    // ---- the piano keyboard (View > Show Piano Keyboard) ----
    /// <summary>0 nothing, 1 a note to play (or sounding), 2 a wrong note.</summary>
    public readonly byte[] KeyState = new byte[KeysCount];
    public bool KeyboardStopped;

    // ---- CPiano ----
    public Chord GoodChord;
    public Chord BadChord;
    public readonly NoteNameItem[] NoteNameList = new NoteNameItem[20];
    public int NoteNameListLength;
    public bool RhythmTapping;

    // ---- for measuring the smoothness ----
    /// <summary>How far (in pixels) the score has scrolled since the song was reset.</summary>
    public double ScrollPosition;
    /// <summary>Stopwatch ticks when the photo was taken.</summary>
    public long Timestamp;

    internal readonly Stack<ScrollFrame> SpareScrolls = new();

    internal void ClearScrolls()
    {
        foreach (ScrollFrame scroll in Scrolls)
        {
            scroll.Slots.Clear();
            SpareScrolls.Push(scroll);
        }
        Scrolls.Clear();
    }

    internal ScrollFrame NewScroll()
    {
        ScrollFrame scroll = SpareScrolls.Count > 0 ? SpareScrolls.Pop() : new ScrollFrame();
        Scrolls.Add(scroll);
        return scroll;
    }
}
