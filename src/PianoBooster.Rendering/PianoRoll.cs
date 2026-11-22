// Ported from GuiPianoRoll.h/.cpp of PianoBooster (C++). GPLv3.
//
// The views of the section editor: a piano roll of the whole song that can be scrolled and
// zoomed without playing it, where a stretch of the song is selected with the mouse
// (PianoRollView = CPianoRollView), and an overview of all the song (SongOverview =
// CSongOverview).
//
// In C++ they are Qt widgets drawn with QPainter. Here they are models without a user
// interface: their state, what the mouse does and the drawing on an SKCanvas (so that they
// can also be drawn into a bitmap). The Avalonia controls of the app (PianoRollControl) give
// them the size, the mouse and the scroll bar, and draw them. The QPalette is a PianoRollPalette.

using System;
using System.Collections.Generic;
using System.Globalization;
using PianoBooster.Core;
using SkiaSharp;

namespace PianoBooster.Rendering;

/// <summary>What the start and the end of a selection are rounded to (CSnap).</summary>
public enum Snap { Bar, Beat, HalfBeat, QuarterBeat, None }

/// <summary>The mouse cursors that the piano roll asks for.</summary>
public enum PianoRollCursor { Default, SizeHorizontal, PointingHand }

/// <summary>The colours of the QPalette that the views use.</summary>
public sealed class PianoRollPalette
{
    public SKColor Base { get; init; }
    public SKColor Text { get; init; }
    public SKColor Window { get; init; }
    public SKColor WindowText { get; init; }
    public SKColor Button { get; init; }
    public SKColor ButtonText { get; init; }
    public SKColor Highlight { get; init; }
    public SKColor Mid { get; init; }

    /// <summary>The light palette of Qt (Fusion/Windows).</summary>
    public static PianoRollPalette Light { get; } = new()
    {
        Base = SKColors.White,
        Text = SKColors.Black,
        Window = new SKColor(240, 240, 240),
        WindowText = SKColors.Black,
        Button = new SKColor(240, 240, 240),
        ButtonText = SKColors.Black,
        Highlight = new SKColor(0, 120, 215),
        Mid = new SKColor(160, 160, 160),
    };

    /// <summary>A dark palette (as Qt's dark Fusion palette).</summary>
    public static PianoRollPalette Dark { get; } = new()
    {
        Base = new SKColor(30, 30, 30),
        Text = SKColors.White,
        Window = new SKColor(45, 45, 45),
        WindowText = SKColors.White,
        Button = new SKColor(53, 53, 53),
        ButtonText = SKColors.White,
        Highlight = new SKColor(42, 130, 218),
        Mid = new SKColor(80, 80, 80),
    };

    /// <summary>isDark() of the C++: the lightness (HSL) of Base below one half.</summary>
    public bool IsDark
    {
        get
        {
            int max = Math.Max(Base.Red, Math.Max(Base.Green, Base.Blue));
            int min = Math.Min(Base.Red, Math.Min(Base.Green, Base.Blue));
            return (max + min) / 2.0 / 255.0 < 0.5;
        }
    }
}

/// <summary>What the two views share: which notes are shown and their colours.</summary>
public static class PianoRollNotes
{
    private const int DrumChannel = 9;
    private const int MinNoteSpan = 24; // show at least two octaves

    public static bool IsBlackKey(int note)
    {
        switch (note % 12)
        {
            case 1: case 3: case 6: case 8: case 10:
                return true;
            default:
                return false;
        }
    }

    public static SKColor Blend(SKColor a, SKColor b, double amount)
    {
        static byte Mix(byte x, byte y, double t) => (byte)Math.Round((x / 255.0 + (y / 255.0 - x / 255.0) * t) * 255.0);
        return new SKColor(Mix(a.Red, b.Red, amount), Mix(a.Green, b.Green, amount), Mix(a.Blue, b.Blue, amount));
    }

    /// <summary>QColor::setAlphaF.</summary>
    public static SKColor WithAlpha(SKColor color, float alpha) => color.WithAlpha((byte)Math.Round(alpha * 255f));

    // Without a channel chosen, the piano part
    private static int EffectiveChannel(int practiseChannel)
    {
        if (practiseChannel >= 0)
            return practiseChannel;
        return (Note.RightHandChan >= 0) ? Note.RightHandChan : Note.LeftHandChan;
    }

    /// <summary>The notes practised when a channel is chosen in the list of instruments, as when
    /// playing (see Note.FindHand()): those of that channel and, if it is one of the two hands,
    /// those of the other hand too.</summary>
    public static bool IsPractisedNote(in SongNote note, int practiseChannel)
    {
        int channel = EffectiveChannel(practiseChannel);
        if (channel < 0)
            return false;
        if (note.Channel == channel)
            return true;
        // the other hand of the piano part
        return Note.HasPianoPart(channel) && Note.HasPianoPart(note.Channel);
    }

    /// <summary>The colour of a note: its hand if it is practised, grey if not (pianoRollNoteColor).</summary>
    public static SKColor NoteColor(in SongNote note, int practiseChannel, PianoRollPalette palette)
    {
        bool dark = palette.IsDark;
        SKColor right = dark ? new SKColor(90, 160, 255) : new SKColor(40, 110, 215);
        SKColor left = dark ? new SKColor(80, 200, 120) : new SKColor(35, 150, 80);
        SKColor other = dark ? new SKColor(130, 130, 130) : new SKColor(165, 165, 165);

        if (!IsPractisedNote(note, practiseChannel))
            return other;

        // the hand, as in Note.FindHand()
        int channel = EffectiveChannel(practiseChannel);
        int rightTrack = Note.RightHandTrack(note.Channel);
        if (note.Channel == channel && rightTrack >= 0)
            return note.Track == rightTrack ? right : left; // both hands in one channel, in two tracks
        if (note.Channel == Note.RightHandChan)
            return right;
        if (note.Channel == Note.LeftHandChan)
            return left;
        return note.Note >= Midi.MiddleC ? right : left; // split at middle C
    }

    // The notes shown: those practised, and the others if wanted (but not the drums, unless
    // they are the instrument practised)
    internal static bool IsShown(in SongNote note, int practiseChannel, bool showOtherParts)
    {
        if (IsPractisedNote(note, practiseChannel))
            return true;
        return showOtherParts && note.Channel != DrumChannel;
    }

    /// <summary>The range of the notes shown, with a margin and at least two octaves.</summary>
    public static void NoteRange(SongMap? map, int practiseChannel, bool showOtherParts, out int low, out int high)
    {
        bool any = false;
        low = high = 0;
        if (map != null)
        {
            foreach (SongNote note in map.Notes)
            {
                if (!IsShown(note, practiseChannel, showOtherParts))
                    continue;
                low = any ? Math.Min(low, note.Note) : note.Note;
                high = any ? Math.Max(high, note.Note) : note.Note;
                any = true;
            }
        }
        if (!any)
        {
            low = 60;
            high = 72;
        }
        low -= 2;
        high += 2;
        if (high - low + 1 < MinNoteSpan)
        {
            int middle = (low + high) / 2;
            low = middle - MinNoteSpan / 2;
            high = low + MinNoteSpan - 1;
        }
        low = Math.Max(low, 0);
        high = Math.Min(high, 127);
    }

    private static readonly SKColor[] s_partColors =
    {
        new(230, 160, 40), new(170, 110, 210), new(40, 170, 170), new(220, 100, 110), new(120, 160, 60),
    };

    /// <summary>The colours of the sections in the lane, one after the other.</summary>
    public static SKColor PartColor(int index) => s_partColors[index % s_partColors.Length];
}

/// <summary>The horizontal QScrollBar of the QAbstractScrollArea: the value is kept in the range.</summary>
public sealed class ScrollBarModel
{
    private int _value;

    public int Minimum => 0;
    public int Maximum { get; private set; }
    public int PageStep { get; set; }
    public int SingleStep { get; set; } = 1;

    /// <summary>The value changed (scrollContentsBy, or the control that shows it).</summary>
    public event Action? ValueChanged;
    /// <summary>The range or the steps changed.</summary>
    public event Action? RangeChanged;

    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, Minimum, Maximum);
            if (value == _value)
                return;
            _value = value;
            ValueChanged?.Invoke();
        }
    }

    public void SetRange(int minimum, int maximum)
    {
        Maximum = Math.Max(minimum, maximum);
        RangeChanged?.Invoke();
        Value = _value; // clamped
    }

    internal void NotifyStepsChanged() => RangeChanged?.Invoke();
}

/// <summary>The text of the views (QFont of the widget with a point size).</summary>
internal static class RollText
{
    // Qt: points at the logical 96 dpi
    public static SKFont Font(SKTypeface typeface, double pointSize) =>
        new(typeface, (float)(pointSize * 96.0 / 72.0)) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };

    public static void Draw(SKCanvas canvas, string text, float x, float baseline, SKFont font, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
    }

    /// <summary>drawText(QRectF, flags, text): left or right, bottom or vertically centred.</summary>
    public static void DrawInRect(SKCanvas canvas, string text, SKRect rect, bool alignRight, bool alignBottom, SKFont font, SKColor color)
    {
        SKFontMetrics metrics = font.Metrics;
        float width = font.MeasureText(text);
        float x = alignRight ? rect.Right - width : rect.Left;
        float baseline = alignBottom ? rect.Bottom - metrics.Descent
                                     : rect.MidY - (metrics.Ascent + metrics.Descent) / 2f;
        Draw(canvas, text, x, baseline, font, color);
    }

    /// <summary>QFontMetrics::elidedText(text, Qt::ElideRight, width).</summary>
    public static string Elided(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width)
            return text;
        const string ellipsis = "…";
        for (int length = text.Length - 1; length > 0; length--)
        {
            string candidate = text.Substring(0, length) + ellipsis;
            if (font.MeasureText(candidate) <= width)
                return candidate;
        }
        return font.MeasureText(ellipsis) <= width ? ellipsis : "";
    }
}

/// <summary>The piano roll of the section editor (CPianoRollView), without the widget.</summary>
public sealed class PianoRollView
{
    public const int KeysWidth = 40;
    public const int RulerHeight = 22;
    public const int LaneHeight = 20;
    private const int HandleDistance = 6;

    private enum Drag { None, Selection }

    private SongMap? _map;
    private List<SongPart> _parts = new();
    private long _selectedPartId = -1;
    private long _selStart;
    private long _selEnd;
    private long _cursorTick = -1;
    private double _pixelsPerQuarter = 40.0;
    private bool _showOtherParts = true;
    private int _practiseChannel = -1;
    private int _lowNote = 48; // the notes shown, from bottom to top
    private int _highNote = 84;

    private Drag _drag = Drag.None;
    private long _dragAnchor;     // the edge of the selection that is not being moved
    private long _savedSelStart;  // the selection before the mouse was pressed
    private long _savedSelEnd;
    private bool _dragMoved;

    public PianoRollView()
    {
        HorizontalScrollBar.ValueChanged += ScrollContentsBy;
    }

    /// <summary>The size of the viewport (the area drawn, without the scroll bar), in logical pixels.</summary>
    public int Width { get; private set; } = 400;
    public int Height { get; private set; } = 200;

    public ScrollBarModel HorizontalScrollBar { get; } = new();
    public PianoRollPalette Palette { get; set; } = PianoRollPalette.Light;
    public SKTypeface Typeface { get; set; } = SKTypeface.Default;
    public Snap Snap { get; set; } = Snap.Beat;
    public PianoRollCursor Cursor { get; private set; } = PianoRollCursor.Default;

    /// <summary>While it is being dragged too.</summary>
    public event Action<long, long>? SelectionChanged;
    /// <summary>When the mouse is released.</summary>
    public event Action? SelectionEdited;
    /// <summary>Dragging away from the edges.</summary>
    public event Action? NewSelectionStarted;
    public event Action<long>? PartClicked;
    public event Action? ViewChanged;
    /// <summary>viewport()->update(): it has to be drawn again.</summary>
    public event Action? UpdateRequested;
    public event Action? CursorChanged;

    public SongMap? SongMap => _map;
    public long SelectionStart => _selStart;
    public long SelectionEnd => _selEnd;
    /// <summary>The selection, from start up to (not including) end, in ticks. Empty if end &lt;= start.</summary>
    public bool HasSelection => _selEnd > _selStart;
    public long CursorTick => _cursorTick;
    public double PixelsPerQuarter => _pixelsPerQuarter;
    public int LowNote => _lowNote;
    public int HighNote => _highNote;

    private int GridTop => RulerHeight + LaneHeight;

    private void Update() => UpdateRequested?.Invoke();

    public void SetSongMap(SongMap? map)
    {
        _map = map;
        _selStart = _selEnd = 0;
        _cursorTick = -1;
        PianoRollNotes.NoteRange(_map, _practiseChannel, _showOtherParts, out _lowNote, out _highNote);
        UpdateScrollBar();
        Update();
    }

    public void SetParts(IEnumerable<SongPart> parts, long selectedId)
    {
        _parts = new List<SongPart>(parts);
        _selectedPartId = selectedId;
        Update();
    }

    public void SetSelection(long start, long end)
    {
        _selStart = start;
        _selEnd = end;
        Update();
    }

    public void SetShowOtherParts(bool show)
    {
        _showOtherParts = show;
        PianoRollNotes.NoteRange(_map, _practiseChannel, _showOtherParts, out _lowNote, out _highNote);
        Update();
    }

    public void SetPractiseChannel(int channel)
    {
        _practiseChannel = channel;
        PianoRollNotes.NoteRange(_map, _practiseChannel, _showOtherParts, out _lowNote, out _highNote);
        Update();
    }

    private double PixelsPerTick() => _pixelsPerQuarter / (_map != null ? _map.Ppqn : 96);

    public double XOfTick(long tick) => KeysWidth + tick * PixelsPerTick() - HorizontalScrollBar.Value;

    public long TickOfX(double x) => (long)Math.Floor((x - KeysWidth + HorizontalScrollBar.Value) / PixelsPerTick());

    public long FirstVisibleTick() => Math.Max(0, TickOfX(KeysWidth));

    public long LastVisibleTick() => TickOfX(Width);

    /// <summary>resizeEvent: the size of the viewport changed.</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height)
            return;
        Width = width;
        Height = height;
        UpdateScrollBar();
        ViewChanged?.Invoke();
        Update();
    }

    private void UpdateScrollBar()
    {
        long endTick = _map != null ? _map.EndTick : 0;
        int viewWidth = Math.Max(1, Width - KeysWidth);
        double contentWidth = endTick * PixelsPerTick() + 40.0;
        ScrollBarModel bar = HorizontalScrollBar;
        bar.PageStep = viewWidth;
        bar.SingleStep = Math.Max(1, viewWidth / 20);
        bar.SetRange(0, Math.Max(0, (int)contentWidth - viewWidth));
    }

    // scrollContentsBy
    private void ScrollContentsBy()
    {
        Update();
        ViewChanged?.Invoke();
    }

    public long SnapTick(long tick)
    {
        if (_map == null)
            return tick;
        long endTick = _map.EndTick;
        tick = Math.Clamp(tick, 0, endTick);
        if (Snap == Snap.None)
            return tick;

        int bar = _map.BarOfTick(tick);
        long barStart = _map.BarStartTick(bar);
        long unit = _map.BeatTicks(bar);
        switch (Snap)
        {
            case Snap.Bar: unit = _map.BarTicks(bar); break;
            case Snap.HalfBeat: unit /= 2; break;
            case Snap.QuarterBeat: unit /= 4; break;
        }
        unit = Math.Max(unit, 1);
        // std::llround: halves away from zero
        long steps = (long)Math.Round((double)(tick - barStart) / unit, MidpointRounding.AwayFromZero);
        return Math.Clamp(barStart + steps * unit, 0, endTick);
    }

    /// <summary>Zoom, as the number of pixels of a quarter note.</summary>
    public void ZoomBy(double factor, int anchorX = -1)
    {
        if (_map == null)
            return;
        if (anchorX < KeysWidth)
            anchorX = KeysWidth + (Width - KeysWidth) / 2;
        double anchorTick = (anchorX - KeysWidth + HorizontalScrollBar.Value) / PixelsPerTick();
        _pixelsPerQuarter = Math.Clamp(_pixelsPerQuarter * factor, 0.2, 600.0);
        UpdateScrollBar();
        HorizontalScrollBar.Value = (int)(anchorTick * PixelsPerTick()) - (anchorX - KeysWidth);
        Update();
        ViewChanged?.Invoke();
    }

    public void ZoomToFit(long start, long end)
    {
        if (_map == null || end <= start)
            return;
        double width = Math.Max(50, Width - KeysWidth - 30);
        double quarters = (double)(end - start) / _map.Ppqn;
        _pixelsPerQuarter = Math.Clamp(width / quarters, 0.2, 600.0);
        UpdateScrollBar();
        HorizontalScrollBar.Value = (int)(start * PixelsPerTick()) - 15;
        Update();
        ViewChanged?.Invoke();
    }

    public void ZoomToSong() => ZoomToFit(0, _map != null ? _map.EndTick : 0);

    /// <summary>Scrolls so that the stretch is seen, if it isn't already.</summary>
    public void EnsureVisible(long start, long end)
    {
        if (_map == null)
            return;
        if (start >= FirstVisibleTick() && end <= LastVisibleTick())
            return;
        double width = Width - KeysWidth;
        if ((end - start) * PixelsPerTick() > width * 0.9)
            ZoomToFit(start, end);
        else
            HorizontalScrollBar.Value = (int)(start * PixelsPerTick() - width * 0.1);
    }

    public void CentreOn(long tick)
    {
        double width = Width - KeysWidth;
        HorizontalScrollBar.Value = (int)(tick * PixelsPerTick() - width / 2);
    }

    /// <summary>The playing position, or -1 for none.</summary>
    public void SetCursorTick(long tick, bool follow)
    {
        _cursorTick = tick;
        if (follow && tick >= 0)
        {
            double width = Width - KeysWidth;
            double x = XOfTick(tick);
            if (x < KeysWidth || x > Width - width * 0.05)
                HorizontalScrollBar.Value = (int)(tick * PixelsPerTick() - width * 0.1);
        }
        Update();
    }

    // ---------------------------------------------------------------- drawing

    private static SKPaint Fill(SKColor color) => new() { Color = color, Style = SKPaintStyle.Fill, IsAntialias = false };

    private static SKPaint Pen(SKColor color, float width = 0) =>
        new() { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = false };

    private static void Line(SKCanvas canvas, double x1, double y1, double x2, double y2, SKPaint pen) =>
        canvas.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, pen);

    private static SKRect Rect(double x, double y, double width, double height) =>
        SKRect.Create((float)x, (float)y, (float)width, (float)height);

    private void DrawGrid(SKCanvas canvas, long firstTick, long lastTick)
    {
        PianoRollPalette pal = Palette;
        using SKPaint barLine = Pen(PianoRollNotes.Blend(pal.Base, pal.Text, 0.45));
        using SKPaint beatLine = Pen(PianoRollNotes.Blend(pal.Base, pal.Text, 0.15));
        int height = Height;

        int lastBar = Math.Min(_map!.BarOfTick(lastTick) + 1, _map.BarCount());
        for (int bar = _map.BarOfTick(firstTick); bar <= lastBar; ++bar)
        {
            long barStart = _map.BarStartTick(bar);
            long beat = _map.BeatTicks(bar);
            if (beat * PixelsPerTick() >= 8.0 && bar < _map.BarCount())
            {
                for (int i = 1; i < _map.BeatsInBar(bar); ++i)
                {
                    double bx = XOfTick(barStart + i * beat);
                    Line(canvas, bx, GridTop, bx, height, beatLine);
                }
            }
            double x = XOfTick(barStart);
            Line(canvas, x, GridTop, x, height, barLine);
        }
    }

    private void DrawKeys(SKCanvas canvas, int lowNote, int highNote, double rowHeight)
    {
        PianoRollPalette pal = Palette;
        int height = Height;
        using (SKPaint keys = Fill(pal.IsDark ? new SKColor(200, 200, 200) : SKColors.White))
            canvas.DrawRect(SKRect.Create(0, GridTop, KeysWidth, height - GridTop), keys);
        using SKFont font = RollText.Font(Typeface, Math.Max(6.0, Math.Min(8.0, rowHeight * 0.9)));
        using SKPaint blackKey = Fill(new SKColor(40, 40, 40));
        using SKPaint octaveLine = Pen(new SKColor(120, 120, 120));
        for (int note = lowNote; note <= highNote; ++note)
        {
            double y = GridTop + (highNote - note) * rowHeight;
            if (PianoRollNotes.IsBlackKey(note))
                canvas.DrawRect(Rect(0, y, KeysWidth * 0.6, rowHeight), blackKey);
            if (note % 12 == 0)
            {
                Line(canvas, 0, y + rowHeight, KeysWidth, y + rowHeight, octaveLine);
                if (rowHeight * 12 >= 30)
                {
                    RollText.DrawInRect(canvas, "C" + (note / 12 - 1).ToString(CultureInfo.InvariantCulture),
                                        Rect(0, y - 10, KeysWidth - 3, rowHeight + 10), true, true, font, SKColors.Black);
                }
            }
        }
        using SKPaint mid = Pen(pal.Mid);
        Line(canvas, KeysWidth - 1, GridTop, KeysWidth - 1, height, mid);
    }

    /// <summary>paintEvent: draws the viewport, in logical pixels from its top left corner.</summary>
    public void Render(SKCanvas canvas)
    {
        PianoRollPalette pal = Palette;
        int width = Width;
        int height = Height;
        using (SKPaint background = Fill(pal.Base))
            canvas.DrawRect(SKRect.Create(0, 0, width, height), background);
        if (_map == null || !_map.IsValid)
            return;

        int lowNote = _lowNote;
        int highNote = _highNote;
        double rowHeight = (double)(height - GridTop) / (highNote - lowNote + 1);
        long firstTick = FirstVisibleTick();
        long lastTick = LastVisibleTick();

        canvas.Save();
        canvas.ClipRect(SKRect.Create(KeysWidth, 0, width - KeysWidth, height));

        // the rows of the black keys
        using (SKPaint blackRow = Fill(PianoRollNotes.Blend(pal.Base, pal.Text, 0.06)))
        {
            for (int note = lowNote; note <= highNote; ++note)
                if (PianoRollNotes.IsBlackKey(note))
                    canvas.DrawRect(Rect(KeysWidth, GridTop + (highNote - note) * rowHeight, width - KeysWidth, rowHeight), blackRow);
        }

        // after the end of the song
        double endX = XOfTick(_map.EndTick);
        if (endX < width)
        {
            using SKPaint window = Fill(pal.Window);
            canvas.DrawRect(Rect(endX, GridTop, width - endX, height - GridTop), window);
        }

        // the selection
        SKColor highlight = pal.Highlight;
        double selX1 = XOfTick(_selStart);
        double selX2 = XOfTick(_selEnd);
        if (HasSelection)
        {
            using SKPaint band = Fill(PianoRollNotes.WithAlpha(highlight, 0.18f));
            canvas.DrawRect(Rect(selX1, GridTop, selX2 - selX1, height - GridTop), band);
        }

        DrawGrid(canvas, firstTick, lastTick);

        // the notes, dimmed outside of the selection: the other instruments first, so that
        // the notes practised are drawn over them
        IReadOnlyList<SongNote> notes = _map.Notes;
        long searchFrom = firstTick - _map.MaxNoteLength;
        int first = LowerBound(notes, searchFrom);
        using SKPaint outline = Pen(pal.IsDark ? new SKColor(0, 0, 0, 120) : new SKColor(0, 0, 0, 90));
        using SKPaint noteFill = Fill(SKColors.Black);
        for (int pass = 0; pass < 2; ++pass)
        {
            for (int i = first; i < notes.Count && notes[i].Tick <= lastTick; ++i)
            {
                SongNote note = notes[i];
                if (PianoRollNotes.IsPractisedNote(note, _practiseChannel) != (pass == 1))
                    continue;
                if (note.Tick + note.Length < firstTick || !PianoRollNotes.IsShown(note, _practiseChannel, _showOtherParts))
                    continue;
                if (note.Note < lowNote || note.Note > highNote)
                    continue;
                SKColor color = PianoRollNotes.NoteColor(note, _practiseChannel, pal);
                if (HasSelection && (note.Tick < _selStart || note.Tick >= _selEnd))
                    color = PianoRollNotes.Blend(color, pal.Base, 0.6);
                double x1 = XOfTick(note.Tick);
                double x2 = Math.Max(XOfTick(note.Tick + note.Length), x1 + 2.0);
                double y = GridTop + (highNote - note.Note) * rowHeight;
                SKRect box = Rect(x1, y + 0.5, x2 - x1 - 0.5, Math.Max(1.0, rowHeight - 1.0));
                noteFill.Color = color;
                canvas.DrawRect(box, noteFill);
                if (rowHeight >= 4 && x2 - x1 >= 4)
                    canvas.DrawRect(box, outline);
            }
        }

        // the edges of the selection
        if (HasSelection)
        {
            using SKPaint edge = Pen(highlight, 2);
            Line(canvas, selX1, GridTop, selX1, height, edge);
            Line(canvas, selX2, GridTop, selX2, height, edge);
        }

        // the playing position
        if (_cursorTick >= 0)
        {
            using SKPaint cursor = Pen(new SKColor(220, 40, 40), 2);
            double x = XOfTick(_cursorTick);
            Line(canvas, x, 0, x, height, cursor);
        }

        // the ruler with the bar numbers
        using (SKPaint button = Fill(pal.Button))
            canvas.DrawRect(SKRect.Create(KeysWidth, 0, width - KeysWidth, RulerHeight), button);
        using (SKFont font = RollText.Font(Typeface, 8.0))
        {
            double barWidth = _map.BarTicks(_map.BarOfTick(firstTick)) * PixelsPerTick();
            int labelWidth = (int)font.MeasureText("8888 12/8") / 2 + 8;
            int every = 1;
            while (every * barWidth < labelWidth && every < 4096)
                every *= 2;
            int lastBar = Math.Min(_map.BarOfTick(lastTick) + 1, _map.BarCount());
            using SKPaint tick = Pen(pal.ButtonText);
            for (int bar = _map.BarOfTick(firstTick); bar <= lastBar; ++bar)
            {
                double x = XOfTick(_map.BarStartTick(bar));
                bool sigChange = bar == 0 || _map.BeatsInBar(bar) != _map.BeatsInBar(bar - 1) ||
                                 _map.TimeSigBottom(bar) != _map.TimeSigBottom(bar - 1);
                Line(canvas, x, RulerHeight - (bar % every == 0 ? 8 : 4), x, RulerHeight, tick);
                if (bar % every != 0 && !sigChange)
                    continue;
                if (bar >= _map.BarCount())
                    continue;
                string label = (bar + Bar.FirstBarNumber).ToString(CultureInfo.InvariantCulture);
                if (sigChange && barWidth >= labelWidth)
                    label += string.Create(CultureInfo.InvariantCulture, $"  {_map.BeatsInBar(bar)}/{_map.TimeSigBottom(bar)}");
                RollText.Draw(canvas, label, (float)(x + 3), RulerHeight - 8, font, pal.ButtonText);
            }
            using SKPaint mid = Pen(pal.Mid);
            Line(canvas, KeysWidth, RulerHeight - 1, width, RulerHeight - 1, mid);
        }

        // the selection in the ruler, with its two handles
        if (HasSelection)
        {
            using (SKPaint band = Fill(PianoRollNotes.WithAlpha(highlight, 0.35f)))
                canvas.DrawRect(Rect(selX1, 0, selX2 - selX1, RulerHeight), band);
            using SKPaint handlePaint = new() { Color = highlight, Style = SKPaintStyle.Fill, IsAntialias = true };
            foreach (double x in new[] { selX1, selX2 })
            {
                using var handle = new SKPath();
                handle.MoveTo((float)(x - 6), 0);
                handle.LineTo((float)(x + 6), 0);
                handle.LineTo((float)x, 9);
                handle.Close();
                canvas.DrawPath(handle, handlePaint);
            }
        }

        // the lane with the saved sections
        using (SKPaint window = Fill(pal.Window))
            canvas.DrawRect(SKRect.Create(KeysWidth, RulerHeight, width - KeysWidth, LaneHeight), window);
        using (SKFont font = RollText.Font(Typeface, 8.0))
        {
            using SKPaint partFill = Fill(SKColors.Black);
            using SKPaint selectedOutline = Pen(pal.Text, 1);
            for (int i = 0; i < _parts.Count; ++i)
            {
                SongPart part = _parts[i];
                double x1 = XOfTick(_map.TickOfPosition(part.Start));
                double x2 = XOfTick(_map.SectionEndTick(part.End));
                if (x2 < KeysWidth || x1 > width)
                    continue;
                bool selected = part.Id == _selectedPartId;
                partFill.Color = PianoRollNotes.WithAlpha(PianoRollNotes.PartColor(i), selected ? 0.95f : 0.55f);
                SKRect box = Rect(x1, RulerHeight + 2, Math.Max(2.0, x2 - x1), LaneHeight - 4);
                canvas.DrawRect(box, partFill);
                if (selected)
                    canvas.DrawRect(box, selectedOutline);
                SKRect textBox = new(box.Left + 3, box.Top, box.Right - 2, box.Bottom);
                textBox.Intersect(SKRect.Create(KeysWidth + 2, 0, width, height));
                if (textBox.Width > 0)
                {
                    RollText.DrawInRect(canvas, RollText.Elided(part.Name, font, textBox.Width), textBox, false, false, font,
                                        selected ? SKColors.Black : pal.WindowText);
                }
            }
            using SKPaint mid = Pen(pal.Mid);
            Line(canvas, KeysWidth, GridTop - 1, width, GridTop - 1, mid);
        }
        canvas.Restore();

        DrawKeys(canvas, lowNote, highNote, rowHeight);
        using (SKPaint corner = Fill(pal.Button))
            canvas.DrawRect(SKRect.Create(0, 0, KeysWidth, GridTop), corner);
    }

    internal static int LowerBound(IReadOnlyList<SongNote> notes, long tick)
    {
        int low = 0, high = notes.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].Tick < tick)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    // ---------------------------------------------------------------- the mouse

    private int PartAt(int px, int py)
    {
        if (_map == null || py < RulerHeight || py >= GridTop)
            return -1;
        int found = -1;
        long foundLength = 0;
        for (int i = 0; i < _parts.Count; i++)
        {
            SongPart part = _parts[i];
            long start = _map.TickOfPosition(part.Start);
            long end = _map.SectionEndTick(part.End);
            if (px < XOfTick(start) || px > Math.Max(XOfTick(end), XOfTick(start) + 2))
                continue;
            if (found < 0 || end - start < foundLength) // the shortest one, if they overlap
            {
                found = i;
                foundLength = end - start;
            }
        }
        return found;
    }

    /// <summary>The id of the section of the lane at a point, or -1.</summary>
    public long PartIdAt(double x, double y)
    {
        int index = PartAt(RoundToInt(x), RoundToInt(y));
        return index >= 0 ? _parts[index].Id : -1;
    }

    // QPointF::toPoint()
    private static int RoundToInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private void SetCursor(PianoRollCursor cursor)
    {
        if (cursor == Cursor)
            return;
        Cursor = cursor;
        CursorChanged?.Invoke();
    }

    /// <summary>mousePressEvent, with the left button (the others do nothing).</summary>
    public void MousePress(double x, double y)
    {
        if (_map == null || x < KeysWidth)
            return;
        int part = PartAt(RoundToInt(x), RoundToInt(y));
        if (part >= 0)
        {
            PartClicked?.Invoke(_parts[part].Id);
            return;
        }

        _savedSelStart = _selStart;
        _savedSelEnd = _selEnd;
        _dragMoved = false;
        _drag = Drag.Selection;
        double startDistance = Math.Abs(x - XOfTick(_selStart));
        double endDistance = Math.Abs(x - XOfTick(_selEnd));
        if (HasSelection && Math.Min(startDistance, endDistance) <= HandleDistance)
        {
            // move the nearest edge; the other one stays where it is
            _dragAnchor = (startDistance <= endDistance) ? _selEnd : _selStart;
            return;
        }
        _dragAnchor = SnapTick(TickOfX(x));
        _selStart = _selEnd = _dragAnchor;
        Update();
        NewSelectionStarted?.Invoke();
    }

    /// <summary>mouseMoveEvent (the mouse is tracked without buttons too).</summary>
    public void MouseMove(double x, double y)
    {
        if (_map == null)
            return;
        if (_drag == Drag.None)
        {
            int px = RoundToInt(x), py = RoundToInt(y);
            bool nearEdge = HasSelection && py >= 0 && x >= KeysWidth &&
                (Math.Abs(x - XOfTick(_selStart)) <= HandleDistance || Math.Abs(x - XOfTick(_selEnd)) <= HandleDistance);
            bool onPart = PartAt(px, py) >= 0;
            if (nearEdge && !onPart)
                SetCursor(PianoRollCursor.SizeHorizontal);
            else if (onPart)
                SetCursor(PianoRollCursor.PointingHand);
            else
                SetCursor(PianoRollCursor.Default);
            return;
        }

        // scroll when the mouse goes past the sides
        ScrollBarModel bar = HorizontalScrollBar;
        if (x > Width - 10)
            bar.Value += 20;
        else if (x < KeysWidth + 10)
            bar.Value -= 20;

        long tick = SnapTick(TickOfX(x));
        long start = Math.Min(_dragAnchor, tick);
        long end = Math.Max(_dragAnchor, tick);
        if (start != _selStart || end != _selEnd)
        {
            _selStart = start;
            _selEnd = end;
            _dragMoved = true;
            Update();
            SelectionChanged?.Invoke(_selStart, _selEnd);
        }
    }

    /// <summary>mouseReleaseEvent, with the left button.</summary>
    public void MouseRelease()
    {
        if (_drag == Drag.None)
            return;
        _drag = Drag.None;
        if (!HasSelection)
        {
            // a click without dragging keeps the selection there was
            _selStart = _savedSelStart;
            _selEnd = _savedSelEnd;
            Update();
            SelectionChanged?.Invoke(_selStart, _selEnd);
            return;
        }
        if (_dragMoved)
            SelectionEdited?.Invoke();
    }

    /// <summary>wheelEvent, with the angle delta of Qt (120 for each step of the wheel).</summary>
    public void Wheel(double deltaX, double deltaY, bool control, double x)
    {
        if (control)
        {
            ZoomBy(Math.Pow(1.0015, deltaY), (int)x);
        }
        else
        {
            int steps = (int)((deltaX != 0) ? deltaX : deltaY);
            ScrollBarModel bar = HorizontalScrollBar;
            bar.Value -= steps * bar.PageStep / 600;
        }
    }
}

/// <summary>The overview of all the song in the section editor (CSongOverview), without the widget.</summary>
public sealed class SongOverview : IDisposable
{
    private const int LaneHeight = 7;

    private SongMap? _map;
    private List<SongPart> _parts = new();
    private long _selectedPartId = -1;
    private long _selStart;
    private long _selEnd;
    private long _visibleFirst;
    private long _visibleLast;
    private long _cursorTick = -1;
    private bool _showOtherParts = true;
    private int _practiseChannel = -1;
    private SKPicture? _cache; // the notes, drawn once for each size
    private SKSizeI _cacheSize;

    public int Width { get; private set; } = 400;
    public int Height { get; private set; } = 48; // sizeHint
    public PianoRollPalette Palette { get; set; } = PianoRollPalette.Light;

    public event Action<long>? CentreRequested;
    public event Action? UpdateRequested;

    private void Update() => UpdateRequested?.Invoke();

    private void ClearCache()
    {
        _cache?.Dispose();
        _cache = null;
    }

    public void Dispose() => ClearCache();

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        Update();
    }

    public void SetSongMap(SongMap? map)
    {
        _map = map;
        ClearCache();
        Update();
    }

    public void SetParts(IEnumerable<SongPart> parts, long selectedId)
    {
        _parts = new List<SongPart>(parts);
        _selectedPartId = selectedId;
        Update();
    }

    public void SetSelection(long start, long end)
    {
        _selStart = start;
        _selEnd = end;
        Update();
    }

    public void SetVisibleRange(long first, long last)
    {
        _visibleFirst = first;
        _visibleLast = last;
        Update();
    }

    public void SetCursorTick(long tick)
    {
        _cursorTick = tick;
        Update();
    }

    public void SetShowOtherParts(bool show)
    {
        _showOtherParts = show;
        ClearCache();
        Update();
    }

    public void SetPractiseChannel(int channel)
    {
        _practiseChannel = channel;
        ClearCache();
        Update();
    }

    /// <summary>The palette changed: the notes are drawn again.</summary>
    public void InvalidateCache()
    {
        ClearCache();
        Update();
    }

    private double XOfTick(long tick)
    {
        long end = (_map != null && _map.EndTick > 0) ? _map.EndTick : 1;
        return (double)tick * (Width - 1) / end;
    }

    private long TickOfX(double x)
    {
        long end = (_map != null && _map.EndTick > 0) ? _map.EndTick : 1;
        return (long)(x * end / Math.Max(1, Width - 1));
    }

    private static SKPaint Fill(SKColor color) => new() { Color = color, Style = SKPaintStyle.Fill, IsAntialias = false };

    private static SKRect Rect(double x, double y, double width, double height) =>
        SKRect.Create((float)x, (float)y, (float)width, (float)height);

    public void Render(SKCanvas canvas)
    {
        PianoRollPalette pal = Palette;
        if (_map == null || !_map.IsValid)
        {
            using SKPaint background = Fill(pal.Base);
            canvas.DrawRect(SKRect.Create(0, 0, Width, Height), background);
            return;
        }

        // the notes do not change, so they are drawn once for each size
        var size = new SKSizeI(Width, Height);
        if (_cache == null || _cacheSize != size)
        {
            ClearCache();
            _cacheSize = size;
            using var recorder = new SKPictureRecorder();
            SKCanvas cache = recorder.BeginRecording(SKRect.Create(0, 0, Width, Height));
            using (SKPaint background = Fill(pal.Base))
                cache.DrawRect(SKRect.Create(0, 0, Width, Height), background);
            PianoRollNotes.NoteRange(_map, _practiseChannel, _showOtherParts, out int lowNote, out int highNote);
            double rowHeight = (double)(Height - LaneHeight - 2) / (highNote - lowNote + 1);
            using SKPaint noteFill = Fill(SKColors.Black);
            // the others first, so that the notes practised are drawn over them
            for (int pass = 0; pass < 2; ++pass)
            {
                foreach (SongNote note in _map.Notes)
                {
                    if (PianoRollNotes.IsPractisedNote(note, _practiseChannel) != (pass == 1))
                        continue;
                    if (!PianoRollNotes.IsShown(note, _practiseChannel, _showOtherParts) || note.Note < lowNote || note.Note > highNote)
                        continue;
                    double x1 = XOfTick(note.Tick);
                    double x2 = Math.Max(XOfTick(note.Tick + note.Length), x1 + 1.0);
                    double y = LaneHeight + 1 + (highNote - note.Note) * rowHeight;
                    noteFill.Color = PianoRollNotes.NoteColor(note, _practiseChannel, pal);
                    cache.DrawRect(Rect(x1, y, x2 - x1, Math.Max(1.0, rowHeight)), noteFill);
                }
            }
            _cache = recorder.EndRecording();
        }
        canvas.DrawPicture(_cache);

        // the saved sections, at the top
        using (SKPaint window = Fill(pal.Window))
            canvas.DrawRect(SKRect.Create(0, 0, Width, LaneHeight), window);
        using (SKPaint partFill = Fill(SKColors.Black))
        {
            for (int i = 0; i < _parts.Count; ++i)
            {
                double x1 = XOfTick(_map.TickOfPosition(_parts[i].Start));
                double x2 = XOfTick(_map.SectionEndTick(_parts[i].End));
                partFill.Color = PianoRollNotes.WithAlpha(PianoRollNotes.PartColor(i), _parts[i].Id == _selectedPartId ? 1.0f : 0.6f);
                canvas.DrawRect(Rect(x1, 1, Math.Max(2.0, x2 - x1), LaneHeight - 2), partFill);
            }
        }

        SKColor highlight = pal.Highlight;
        if (_selEnd > _selStart)
        {
            using SKPaint band = Fill(PianoRollNotes.WithAlpha(highlight, 0.25f));
            canvas.DrawRect(Rect(XOfTick(_selStart), LaneHeight, XOfTick(_selEnd) - XOfTick(_selStart), Height - LaneHeight), band);
        }

        // the part seen in the piano roll
        double vx1 = XOfTick(_visibleFirst);
        double vx2 = Math.Max(XOfTick(_visibleLast), vx1 + 3.0);
        using (SKPaint frame = new() { Color = pal.Text, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false })
            canvas.DrawRect(Rect(vx1, 0.5, vx2 - vx1, Height - 1.5), frame);

        if (_cursorTick >= 0)
        {
            using SKPaint cursor = new() { Color = new SKColor(220, 40, 40), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = false };
            float x = (float)XOfTick(_cursorTick);
            canvas.DrawLine(x, 0, x, Height, cursor);
        }
        using (SKPaint border = new() { Color = pal.Mid, Style = SKPaintStyle.Stroke, StrokeWidth = 0, IsAntialias = false })
            canvas.DrawRect(SKRect.Create(0, 0, Width - 1, Height - 1), border);
    }

    /// <summary>mousePressEvent with the left button.</summary>
    public void MousePress(double x) => CentreRequested?.Invoke(TickOfX(x));

    /// <summary>mouseMoveEvent with the left button held.</summary>
    public void MouseDrag(double x) => CentreRequested?.Invoke(TickOfX(x));
}
