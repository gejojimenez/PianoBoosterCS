// Draws frames of real songs into bitmaps. The pictures are also saved (as PNG) in
// %TEMP%\PianoBooster.Rendering.Tests, to look at them.

using System;
using PianoBooster.Core;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace PianoBooster.Rendering.Tests;

[Collection("statics")] // the core has statics: one test at a time
public sealed class RenderingTests
{
    private readonly ITestOutputHelper _output;

    public RenderingTests(ITestOutputHelper output) => _output = output;

    private static SKColor Rgb(PbColor c) => GlCanvas.ToSkColor(c);

    private static int CountColor(SKBitmap bitmap, SKColor color, SKRectI area, int tolerance = 8)
    {
        int count = 0;
        for (int y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
            for (int x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
            {
                SKColor p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.Red - color.Red) <= tolerance && Math.Abs(p.Green - color.Green) <= tolerance &&
                    Math.Abs(p.Blue - color.Blue) <= tolerance)
                    count++;
            }
        return count;
    }

    private static int DifferentPixels(SKBitmap a, SKBitmap b, int tolerance = 0)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int count = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.Red - q.Red) > tolerance || Math.Abs(p.Green - q.Green) > tolerance ||
                    Math.Abs(p.Blue - q.Blue) > tolerance)
                    count++;
            }
        return count;
    }

    [Fact]
    public void ListeningDrawsTheStavesAndTheNotes()
    {
        using var h = new ScoreHarness("bm03-SkipToMyLou.mid");
        FrameSnapshot frame = h.Run(3000);

        Assert.Single(frame.Scrolls);
        Assert.True(frame.Scrolls[0].Slots.Count > 5, $"only {frame.Scrolls[0].Slots.Count} slots");
        Assert.True(frame.HasRating);

        using SKBitmap bitmap = h.Render(frame);
        _output.WriteLine(ScoreHarness.Save(bitmap, "listen-bm03"));

        // the background
        Assert.Equal(SKColors.Black, bitmap.GetPixel(2, 2));

        // the top line of the right hand stave, on the left (the stave before the scroll)
        int lineY = (int)MathF.Floor(h.ScreenY(new StavePos(WhichPart.Right, 4).PosY));
        int green = Math.Max(bitmap.GetPixel(30, lineY).Green, bitmap.GetPixel(30, lineY - 1).Green);
        Assert.True(green > 60, $"no stave line at y {lineY} (green {green})");

        // the playing zone (dark blue) and some notes in the scrolling part
        SKRectI scrollArea = new((int)Cfg.ScrollStartX, h.Renderer.ScoreViewport.Top, (int)Cfg.StaveEndX, h.Renderer.ScoreViewport.Bottom);
        Assert.True(CountColor(bitmap, new SKColor(0, 0, 77), scrollArea, 2) > 1000, "no playing zone");
        Assert.True(CountColor(bitmap, Rgb(Cfg.NoteColor), scrollArea) > 200, "no notes");

        // one picture per slot drawn
        Assert.Equal(frame.Scrolls[0].Slots.Count, h.Renderer.Score.Scroll.DrawnLastFrame);
        Assert.Equal(frame.Scrolls[0].Slots.Count, h.Renderer.Score.Scroll.CachedPictureCount);
    }

    [Fact]
    public void PicturesDrawTheSameAsTheSymbols()
    {
        using var h = new ScoreHarness("bm06-Greensleeves.mid", configure: s =>
        {
            s.ShowNoteNames = true;
            s.IsNoteDurationTrailEnabled = true;
        });
        FrameSnapshot frame = h.Run(4000);

        h.Renderer.Score.Scroll.UsePictures = true;
        using SKBitmap withPictures = h.Render(frame);
        h.Renderer.Score.Scroll.UsePictures = false;
        using SKBitmap direct = h.Render(frame);

        ScoreHarness.Save(withPictures, "pictures-bm06");
        Assert.Equal(0, DifferentPixels(withPictures, direct, tolerance: 1));
    }

    [Fact]
    public void TheSnapshotDoesNotChangeWhenTheSongMovesOn()
    {
        using var h = new ScoreHarness("bm04-FrereJacques.mid");
        FrameSnapshot frame = h.Run(2000);
        using SKBitmap before = h.Render(frame);

        FrameSnapshot later = h.Run(1500); // other frames, and the song moves on
        using SKBitmap moved = h.Render(later);
        using SKBitmap again = h.Render(frame);

        Assert.True(DifferentPixels(before, moved) > 1000, "the score did not move");
        Assert.Equal(0, DifferentPixels(before, again));
    }

    [Fact]
    public void FollowingTheSongShowsThePianistNotesAndTheKeyboard()
    {
        using var h = new ScoreHarness("bc02-ChordOfCMajor.mid", PlayMode.FollowYou, configure: s =>
        {
            s.ShowNoteNames = true;
            s.IsNoteDurationTrailEnabled = true;
            s.SetValue("View/PianoKeyboard", "on");
        });

        // the pianist plays the wanted chord (the tab key of the PC keyboard) every 600 ms,
        // and holds it down for 400 ms
        FrameSnapshot frame = h.Run(5100, t => // ends with the chord down
        {
            if (t % 600 == 0)
                h.Song.PcKeyPress('t', true);
            else if (t % 600 == 400)
                h.Song.PcKeyPress('t', false);
        });
        Assert.True(frame.GoodChord.Length > 0, "the pianist has no notes down");
        Assert.True(frame.NoteNameListLength > 0);

        using SKBitmap bitmap = h.Render(frame);
        _output.WriteLine(ScoreHarness.Save(bitmap, "follow-bc02"));

        SKRectI viewport = h.Renderer.ScoreViewport;
        // the lines of the notes the pianist has down, before the playing zone
        SKRectI beforeZone = new((int)Cfg.PlayZoneX - 70, viewport.Top, (int)Cfg.PlayZoneX, viewport.Bottom);
        Assert.True(CountColor(bitmap, Rgb(Cfg.PianoGoodColor), beforeZone) > 20, "no pianist lines");
        // the notes played (purple) on the score
        SKRectI scrollArea = new((int)Cfg.ScrollStartX, viewport.Top, (int)Cfg.StaveEndX, viewport.Bottom);
        Assert.True(CountColor(bitmap, Rgb(Cfg.PlayedGoodColor), scrollArea) > 20, "no played notes");
        // the keyboard at the bottom: white keys
        SKRectI keyboard = new((int)Cfg.StaveStartX, viewport.Bottom - 40, (int)Cfg.StaveEndX, viewport.Bottom);
        Assert.True(CountColor(bitmap, SKColors.White, keyboard, 2) > 5000, "no keyboard");
        // the accuracy bar (its white frame) and the texts
        Assert.True(CountColor(bitmap, SKColors.White, new SKRectI(0, viewport.Top, viewport.Right, viewport.Top + 60), 30) > 100);
    }

    [Fact]
    public void DrawsWithTheDisplayScalingOfTheSystem()
    {
        using var h = new ScoreHarness("sy02-SharpKeyPpqn480.mid", configure: s => s.ShowNoteNames = true);
        FrameSnapshot frame = h.Run(2500);
        using SKBitmap bitmap = h.Render(frame, scale: 1.5f);
        _output.WriteLine(ScoreHarness.Save(bitmap, "scaled-150-sy02"));
        Assert.Equal(1536, bitmap.Width);
        Assert.Equal(SKColors.Black, bitmap.GetPixel(3, 3));
    }

    [Fact]
    public void ASmallWindowAndTheTimeSignatureChanges()
    {
        using var h = new ScoreHarness("sy01-TimeSigChanges.mid", width: 700, height: 400,
                                       configure: s => s.IsNoteDurationTrailEnabled = true);
        FrameSnapshot frame = h.Run(9000);
        using SKBitmap bitmap = h.Render(frame);
        _output.WriteLine(ScoreHarness.Save(bitmap, "small-sy01"));
        Assert.Equal(700, h.Renderer.ScoreViewport.Width);
        Assert.Equal(400, h.Renderer.ScoreViewport.Height);
    }

    [Fact]
    public void TheModelScrollsEvenly()
    {
        // The position the frames draw follows the time of the song exactly (a frame every 16 ms)
        using var h = new ScoreHarness("bm07-AmazingGrace.mid");
        var meter = new SmoothnessMeter();
        int time = 0;
        h.Run(10000, t =>
        {
            time = t;
            if (t % 16 == 0)
            {
                FrameSnapshot f = h.Prepare();
                meter.Add(time / 1000.0, f.ScrollPosition);
            }
        });
        SmoothnessResult result = meter.Compute();
        _output.WriteLine(result.ToString());
        Assert.True(result.Frames > 500);
        Assert.True(result.PixelsPerSecond > 10);
        Assert.True(result.RmsDeviation < 0.05, result.ToString());
    }
}

[CollectionDefinition("statics", DisableParallelization = true)]
public sealed class StaticsCollection
{
}

public sealed class SmoothnessMeterTests
{
    [Fact]
    public void AnEvenMotionHasNoDeviation()
    {
        var meter = new SmoothnessMeter();
        for (int i = 0; i < 600; i++)
            meter.Add(i / 60.0, 100 + i * 2.5);
        SmoothnessResult result = meter.Compute();
        Assert.Equal(600, result.Frames);
        Assert.True(result.RmsDeviation < 1e-9);
        Assert.Equal(150.0, result.PixelsPerSecond, 6);
        Assert.Equal(1000.0 / 60.0, result.MeanFrameMs, 6);
    }

    [Fact]
    public void ALateFrameIsSeen()
    {
        // every 10th frame is drawn a frame late: it shows the position of the frame before
        var meter = new SmoothnessMeter();
        for (int i = 0; i < 600; i++)
            meter.Add(i / 60.0, (i % 10 == 9 ? i - 1 : i) * 2.0);
        SmoothnessResult result = meter.Compute();
        Assert.True(result.MaxDeviation > 1.5 && result.MaxDeviation < 2.5, result.ToString());
        Assert.True(result.RmsDeviation > 0.4 && result.RmsDeviation < 1.0, result.ToString());
    }

    [Fact]
    public void AStoppedScoreDoesNotCount()
    {
        var meter = new SmoothnessMeter();
        for (int i = 0; i < 300; i++)
            meter.Add(i / 60.0, 42.0);
        Assert.Equal(0, meter.Compute().Frames);
    }
}
