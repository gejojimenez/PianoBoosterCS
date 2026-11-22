// Tests of the piano roll and the overview of the section editor (GuiPianoRoll): rounding,
// zoom and scrolling, the mouse, and the drawing into a bitmap (also saved as PNG in
// %TEMP%\PianoBooster.Rendering.Tests).

using System;
using System.Collections.Generic;
using System.Linq;
using PianoBooster.Core;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PianoBooster.Rendering.Tests;

[Collection("statics")] // the hands of Note are statics
public sealed class PianoRollTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public PianoRollTests(ITestOutputHelper output)
    {
        _output = output;
        CoreStatics.Reset();
    }

    public void Dispose() => CoreStatics.Reset();

    private static SongMap Load(string name)
    {
        var map = new SongMap();
        Assert.True(map.Load(ScoreHarness.MidiPath(name)), name);
        return map;
    }

    private static PianoRollView Roll(SongMap map, int width = 900, int height = 300)
    {
        var view = new PianoRollView();
        view.Resize(width, height);
        view.SetSongMap(map);
        return view;
    }

    // The channel with most notes: the right hand
    private static int MainChannel(SongMap map) =>
        map.Notes.GroupBy(n => n.Channel).OrderByDescending(g => g.Count()).First().Key;

// As when a song is opened (no right hand track), with the piano part in one channel
    private static void SetHands(int channel)
    {
        Note.Reset();
        Note.SetChannelHands(-1, channel);
    }

    private static int Count(SKBitmap bitmap, Func<SKColor, bool> match, SKRectI area)
    {
        int count = 0;
        for (int y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
            for (int x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
                if (match(bitmap.GetPixel(x, y)))
                    count++;
        return count;
    }

    private static bool Near(SKColor p, SKColor c, int tolerance = 6) =>
        Math.Abs(p.Red - c.Red) <= tolerance && Math.Abs(p.Green - c.Green) <= tolerance && Math.Abs(p.Blue - c.Blue) <= tolerance;

    private static SKBitmap Draw(int width, int height, Action<SKCanvas> render)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        render(canvas);
        canvas.Flush();
        return bitmap;
    }

    [Fact]
    public void TicksAreRoundedToTheBarsAndBeats()
    {
        SongMap map = Load("sy01-TimeSigChanges.mid");
        PianoRollView view = Roll(map);
        int bar = 1;
        long barStart = map.BarStartTick(bar);
        long beat = map.BeatTicks(bar);

        view.Snap = Snap.Beat;
        Assert.Equal(barStart + beat, view.SnapTick(barStart + beat - beat / 3));
        Assert.Equal(barStart + beat, view.SnapTick(barStart + beat + beat / 3));
        Assert.Equal(barStart + beat, view.SnapTick(barStart + beat / 2)); // halves go up (llround)
        view.Snap = Snap.Bar;
        Assert.Equal(barStart, view.SnapTick(barStart + beat));
        Assert.Equal(map.BarStartTick(bar + 1), view.SnapTick(map.BarStartTick(bar + 1) - beat));
        view.Snap = Snap.HalfBeat;
        Assert.Equal(barStart + beat / 2, view.SnapTick(barStart + beat / 2 + 1));
        view.Snap = Snap.QuarterBeat;
        Assert.Equal(barStart + beat / 4, view.SnapTick(barStart + beat / 4 + 1));
        view.Snap = Snap.None;
        Assert.Equal(barStart + 7, view.SnapTick(barStart + 7));
        // inside the song
        Assert.Equal(0, view.SnapTick(-50));
        Assert.Equal(map.EndTick, view.SnapTick(map.EndTick + 1000));
    }

    [Fact]
    public void ZoomAndScroll()
    {
        SongMap map = Load("bm03-SkipToMyLou.mid");
        PianoRollView view = Roll(map);
        int changes = 0;
        view.ViewChanged += () => changes++;

        long start = map.BarStartTick(4), end = map.BarStartTick(8);
        view.ZoomToFit(start, end);
        Assert.True(changes > 0);
        Assert.InRange(view.FirstVisibleTick(), start - map.Ppqn, start);
        Assert.True(view.LastVisibleTick() >= end);
        Assert.True(view.LastVisibleTick() < end + map.BarTicks(4));

        // the tick under the mouse stays there
        int anchor = 500;
        long tick = view.TickOfX(anchor);
        view.ZoomBy(2.0, anchor);
        Assert.InRange(view.TickOfX(anchor), tick - 2, tick + 2);
        view.ZoomBy(1000.0, anchor);
        Assert.Equal(600.0, view.PixelsPerQuarter);

        // the whole song fits, without scrolling
        view.ZoomToSong();
        Assert.Equal(0, view.FirstVisibleTick());
        Assert.True(view.LastVisibleTick() >= map.EndTick);

        // the scroll bar keeps its value in its range
        view.ZoomToFit(start, end);
        view.HorizontalScrollBar.Value = int.MaxValue;
        Assert.Equal(view.HorizontalScrollBar.Maximum, view.HorizontalScrollBar.Value);
        view.EnsureVisible(start, start + map.Ppqn);
        Assert.True(view.FirstVisibleTick() <= start && view.LastVisibleTick() >= start + map.Ppqn);
        view.CentreOn(map.BarStartTick(10));
        long middle = (view.FirstVisibleTick() + view.LastVisibleTick()) / 2;
        Assert.InRange(middle, map.BarStartTick(10) - map.Ppqn, map.BarStartTick(10) + map.Ppqn);

        // the cursor is followed when it goes out of sight
        view.SetCursorTick(map.BarStartTick(20), follow: true);
        Assert.True(view.FirstVisibleTick() <= map.BarStartTick(20) && view.LastVisibleTick() > map.BarStartTick(20));
    }

    [Fact]
    public void TheMouseSelectsAStretch()
    {
        SongMap map = Load("bm03-SkipToMyLou.mid");
        PianoRollView view = Roll(map);
        view.ZoomToFit(0, map.BarStartTick(8));
        var changed = new List<(long, long)>();
        int edited = 0, started = 0;
        view.SelectionChanged += (s, e) => changed.Add((s, e));
        view.SelectionEdited += () => edited++;
        view.NewSelectionStarted += () => started++;

        double y = 150;
        double x1 = view.XOfTick(map.BarStartTick(2) + 3);
        double x2 = view.XOfTick(map.BarStartTick(4) + map.BeatTicks(4) * 2 - 5);
        view.MousePress(x1, y);
        Assert.Equal(1, started);
        view.MouseMove((x1 + x2) / 2, y);
        view.MouseMove(x2, y);
        view.MouseRelease();
        Assert.Equal(1, edited);
        Assert.True(view.HasSelection);
        Assert.Equal(map.BarStartTick(2), view.SelectionStart);
        Assert.Equal(map.BarStartTick(4) + map.BeatTicks(4) * 2, view.SelectionEnd);
        Assert.Equal((view.SelectionStart, view.SelectionEnd), changed[^1]);

        // near an edge: the cursor changes and dragging moves only that edge
        double endX = view.XOfTick(view.SelectionEnd);
        view.MouseMove(endX + 3, y);
        Assert.Equal(PianoRollCursor.SizeHorizontal, view.Cursor);
        view.MousePress(endX + 3, y);
        Assert.Equal(1, started); // not a new selection
        view.MouseMove(view.XOfTick(map.BarStartTick(6)), y);
        view.MouseRelease();
        Assert.Equal(map.BarStartTick(2), view.SelectionStart);
        Assert.Equal(map.BarStartTick(6), view.SelectionEnd);
        Assert.Equal(2, edited);

        // a click without dragging keeps the selection there was
        view.MouseMove(view.XOfTick(map.BarStartTick(7)) + 1, y);
        Assert.Equal(PianoRollCursor.Default, view.Cursor);
        view.MousePress(view.XOfTick(map.BarStartTick(7)) + 1, y);
        Assert.False(view.HasSelection);
        view.MouseRelease();
        Assert.Equal(map.BarStartTick(2), view.SelectionStart);
        Assert.Equal(map.BarStartTick(6), view.SelectionEnd);
        Assert.Equal(2, edited);

        // the keys do nothing
        view.MousePress(PianoRollView.KeysWidth - 5, y);
        view.MouseRelease();
        Assert.Equal(1 + 1, started);
    }

    [Fact]
    public void TheSectionsOfTheLaneCanBeClicked()
    {
        SongMap map = Load("bm03-SkipToMyLou.mid");
        PianoRollView view = Roll(map);
        view.ZoomToFit(0, map.BarStartTick(12));
        var parts = new[]
        {
            new SongPart { Id = 7, Name = "Long", Start = map.PositionOfTick(map.BarStartTick(1)), End = map.SectionEndPosition(map.BarStartTick(9)) },
            new SongPart { Id = 9, Name = "Short", Start = map.PositionOfTick(map.BarStartTick(3)), End = map.SectionEndPosition(map.BarStartTick(4)) },
        };
        view.SetParts(parts, 7);
        var clicked = new List<long>();
        view.PartClicked += clicked.Add;
        double laneY = PianoRollView.RulerHeight + PianoRollView.LaneHeight / 2.0;

        view.MouseMove(view.XOfTick(map.BarStartTick(2)), laneY);
        Assert.Equal(PianoRollCursor.PointingHand, view.Cursor);
        view.MousePress(view.XOfTick(map.BarStartTick(2)), laneY);
        view.MousePress(view.XOfTick(map.BarStartTick(3) + 10), laneY); // the shortest one, where they overlap
        view.MousePress(view.XOfTick(map.BarStartTick(10)), laneY);     // none
        Assert.Equal(new long[] { 7, 9 }, clicked);
        Assert.False(view.HasSelection);
        Assert.Equal(-1, view.PartIdAt(view.XOfTick(map.BarStartTick(2)), laneY + PianoRollView.LaneHeight));
    }

    [Fact]
    public void TheNotesAreDrawnWithTheColoursOfTheHands()
    {
        SongMap map = Load("bm03-SkipToMyLou.mid");
        int channel = MainChannel(map);
        SetHands(channel);

        const int width = 900, height = 300;
        PianoRollView view = Roll(map, width, height);
        view.SetPractiseChannel(channel);
        view.ZoomToFit(0, map.BarStartTick(8));
        long start = map.BarStartTick(2), end = map.BarStartTick(4);
        view.SetSelection(start, end);
        view.SetCursorTick(map.BarStartTick(3), false);
        view.SetParts(new[]
        {
            new SongPart { Id = 1, Name = "A section with a long name", Start = map.PositionOfTick(map.BarStartTick(1)), End = map.SectionEndPosition(map.BarStartTick(3)) },
        }, 1);

        using SKBitmap bitmap = Draw(width, height, view.Render);
        _output.WriteLine(ScoreHarness.Save(bitmap, "pianoroll-bm03"));

        var grid = new SKRectI(PianoRollView.KeysWidth, PianoRollView.RulerHeight + PianoRollView.LaneHeight, width, height);
        SongNote right = map.Notes.First(n => n.Channel == channel);
        SKColor rightColor = PianoRollNotes.NoteColor(right, channel, PianoRollPalette.Light);
        Assert.Equal(new SKColor(40, 110, 215), rightColor);
        SKColor dimmed = PianoRollNotes.Blend(rightColor, SKColors.White, 0.6);

        // in the selection (over its band) the notes are bright, outside they are dimmed
        int selX1 = (int)view.XOfTick(start), selX2 = (int)view.XOfTick(end);
        int brightInside = Count(bitmap, p => Near(p, rightColor, 30), new SKRectI(selX1 + 3, grid.Top, selX2 - 3, grid.Bottom));
        int brightOutside = Count(bitmap, p => Near(p, rightColor, 6), new SKRectI(selX2 + 3, grid.Top, width, grid.Bottom));
        int dimmedOutside = Count(bitmap, p => Near(p, dimmed), new SKRectI(selX2 + 3, grid.Top, width, grid.Bottom));
        Assert.True(brightInside > 100, $"bright notes in the selection: {brightInside}");
        Assert.Equal(0, brightOutside);
        Assert.True(dimmedOutside > 100, $"dimmed notes after the selection: {dimmedOutside}");

        // the edges of the selection, the red cursor, the section in the lane, the keys
        Assert.True(Near(bitmap.GetPixel(selX1, height - 3), PianoRollPalette.Light.Highlight, 10));
        int cursorX = (int)view.XOfTick(map.BarStartTick(3));
        Assert.True(Count(bitmap, p => Near(p, new SKColor(220, 40, 40)), new SKRectI(cursorX - 1, 0, cursorX + 2, height)) > height);
        int laneY = PianoRollView.RulerHeight + PianoRollView.LaneHeight / 2;
        Assert.True(Near(bitmap.GetPixel((int)view.XOfTick(map.BarStartTick(1)) + 2, laneY),
                         PianoRollNotes.Blend(PianoRollNotes.PartColor(0), PianoRollPalette.Light.Window, 0.05), 12));
        Assert.True(Count(bitmap, p => Near(p, new SKColor(40, 40, 40)), new SKRectI(0, grid.Top, 24, height)) > 500, "no black keys");
    }

    [Fact]
    public void TheOtherInstrumentsCanBeHidden()
    {
        SongMap map = Load("bm03-SkipToMyLou.mid");
        int channel = MainChannel(map);
        bool others = map.Notes.Any(n => n.Channel != channel && n.Channel != 9);
        SetHands(channel);
        PianoRollView view = Roll(map);
        view.SetPractiseChannel(channel);
        view.ZoomToSong();
        SKColor grey = new(165, 165, 165);

        using SKBitmap shown = Draw(900, 300, view.Render);
        view.SetShowOtherParts(false);
        using SKBitmap hidden = Draw(900, 300, view.Render);
        var grid = new SKRectI(PianoRollView.KeysWidth, PianoRollView.RulerHeight + PianoRollView.LaneHeight, 900, 300);
        Assert.Equal(0, Count(hidden, p => Near(p, grey, 3), grid));
        if (others)
            Assert.True(Count(shown, p => Near(p, grey, 3), grid) > 50);
        // the range of the notes is at least two octaves
        Assert.True(view.HighNote - view.LowNote + 1 >= 24);
    }

    [Fact]
    public void TheOverviewShowsTheWholeSong()
    {
        SongMap map = Load("bm07-AmazingGrace.mid");
        int channel = MainChannel(map);
        SetHands(channel);
        using var overview = new SongOverview();
        overview.Resize(600, 48);
        overview.SetSongMap(map);
        overview.SetPractiseChannel(channel);
        overview.SetSelection(map.BarStartTick(2), map.BarStartTick(6));
        overview.SetVisibleRange(map.BarStartTick(1), map.BarStartTick(9));
        overview.SetCursorTick(map.BarStartTick(4));
        overview.SetParts(new[] { new SongPart { Id = 3, Name = "x", Start = map.PositionOfTick(0), End = map.SectionEndPosition(map.BarStartTick(3)) } }, 3);
        var centred = new List<long>();
        overview.CentreRequested += centred.Add;

        using SKBitmap bitmap = Draw(600, 48, overview.Render);
        _output.WriteLine(ScoreHarness.Save(bitmap, "overview-bm07"));
        Assert.True(Count(bitmap, p => Near(p, new SKColor(40, 110, 215), 40), new SKRectI(0, 8, 600, 48)) > 100, "no notes");
        Assert.True(Count(bitmap, p => Near(p, new SKColor(220, 40, 40)), new SKRectI(0, 0, 600, 48)) > 40, "no cursor");
        Assert.True(Near(bitmap.GetPixel(5, 3), PianoRollNotes.PartColor(0)), "no section");

        // drawn again (from the cache) the same
        using SKBitmap again = Draw(600, 48, overview.Render);
        Assert.Equal(bitmap.Bytes, again.Bytes);

        overview.MousePress(0);
        overview.MouseDrag(599);
        Assert.Equal(0, centred[0]);
        Assert.Equal(map.EndTick, centred[1]);
    }

    [Fact]
    public void WithoutASongOnlyTheBackgroundIsDrawn()
    {
        var view = new PianoRollView();
        view.Resize(300, 200);
        view.MousePress(100, 100);
        view.ZoomBy(2.0);
        using SKBitmap bitmap = Draw(300, 200, view.Render);
        Assert.Equal(300 * 200, Count(bitmap, p => p == SKColors.White, new SKRectI(0, 0, 300, 200)));
    }
}
