// Ported from GlView.h and GlView.cpp of PianoBooster (C++) (paintGL(), resizeGL() and the
// text, accuracy bar, bar number and time signature). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
//
// paintGL() reads the song while it draws. Here it is split in two:
//  - PrepareFrame(): what reads or changes the song and the score (the rating, the bar number,
//    moving the score on with DrawScrollingSymbols(true), the keyboard lights ...). It must be
//    called holding the lock of the song (ConductorThread.Invoke) and it is quick.
//  - Render(): draws the FrameSnapshot that PrepareFrame() returned, without the lock (it can
//    run in the render thread of the interface).
// The timer and the frame pacing of CGLView (timerEvent(), onFrameSwapped()) belong to the
// window that shows the score (see tools/ScoreWindow).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using PianoBooster.Core;
using SkiaSharp;

namespace PianoBooster.Rendering;

/// <summary>Draws the practice window (the C++ CGLView without its timers).</summary>
public sealed class ViewRenderer : IDisposable
{
    private const int TextLeftMargin = 30;

    private readonly GlCanvas _gl = new();
    private readonly ScoreRenderer _score;
    private readonly List<int> _keyboardNotes = new(64);
    private readonly SKFont _timeSigFont = new() { Subpixel = true, Edging = SKFontEdging.Antialias };
    private readonly SKFont _timeRatingFont = new() { Subpixel = true, Edging = SKFontEdging.Antialias };
    private float _fontsPointSize = -1;

    private int _titleHeight;
    private int _windowWidth;
    private int _windowHeight;
    private int _viewportX, _viewportY, _viewportWidth, _viewportHeight; // y from the bottom, like glViewport
    private bool _ratingSeen;
    private int _accuracyBarStart;
    private bool _keyboardStopped;

    public ViewRenderer()
    {
        _score = new ScoreRenderer(_gl);
        Typeface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
    }

    public ScoreRenderer Score => _score;

    // ---- the texts (tr() in C++) ----
    public string AccuracyText { get; set; } = "Accuracy:";
    public string SongText { get; set; } = "Song:";
    public string BarText { get; set; } = "Bar:";

    /// <summary>CSettings::getWarningMessage(): shown instead of the accuracy.</summary>
    public string WarningMessage { get; set; } = "";

    /// <summary>The point size of the font of the window (QtWindow::font().pointSize(); 9 with
    /// Qt on Windows). The texts are 1.2 times bigger and the time signature 2 times.</summary>
    public int WindowFontPointSize { get; set; } = 9;

    /// <summary>The font of the texts ("Arial" in C++).</summary>
    public SKTypeface Typeface { get; set; }

    /// <summary>The size of the score (Cfg::getAppWidth/Height): the window up to 1024 wide.</summary>
    public SKRectI ScoreViewport => new(_viewportX, _windowHeight - _viewportY - _viewportHeight,
                                        _viewportX + _viewportWidth, _windowHeight - _viewportY);

    /// <summary>
    /// CGLView::resizeGL: places the score in the window. It changes values that the song reads,
    /// so it must be called holding the lock of the song.
    /// </summary>
    public void Resize(int width, int height)
    {
        const int maxSoreWidth = 1024;
        const int staveEndGap = 20;
        int heightAboveStave = (int)(StavePos.VerticalNoteSpacing * StavePos.MaxStaveIndex);
        int heightBelowStave = (int)(StavePos.VerticalNoteSpacing * -StavePos.MinStaveIndex);
        const int minTitleHeight = 20;
        const int minStaveGap = 120;
        int staveGap;
        int maxSoreHeight;

        if (height < 430) // So it works on an eeepc 701 (for Trev)
        {
            staveGap = minStaveGap;
            _titleHeight = minTitleHeight;
        }
        else
        {
            staveGap = (int)(StavePos.StaveHeight * 3);
            _titleHeight = 60;
        }
        maxSoreHeight = heightAboveStave + heightBelowStave + staveGap + _titleHeight;
        if (height > maxSoreHeight)
        {
            int scoreExtraTopGap = height - maxSoreHeight;
            maxSoreHeight += Math.Min(200, scoreExtraTopGap);
        }
        int sizeX = Math.Min(width, maxSoreWidth);
        int sizeY = Math.Min(height, maxSoreHeight);
        int x = 0;
        int y = (height - sizeY) - 5;

        _windowWidth = width;
        _windowHeight = height;
        _viewportX = x;
        _viewportY = y;
        _viewportWidth = sizeX;
        _viewportHeight = sizeY;

        StavePos.StaveCenterY = (float)sizeY - (float)maxSoreHeight / 2.0f - (float)_titleHeight / 2.0f;
        Cfg.SetAppDimensions(x, y, sizeX, sizeY);
        Cfg.SetStaveEndX((float)(sizeX - staveEndGap));
        StavePos.StaveCentralOffset = (float)staveGap / 2.0f;
        DrawState.ForceCompileRedraw();
    }

    /// <summary>
    /// Takes the photo of what paintGL() reads, and does what it changes, in the same order:
    /// the rating, the bar number, then updateMidiTask (the parameter, it can be null), then the
    /// score is moved on (CScore::drawScroll). Call it holding the lock of the song.
    /// </summary>
    public FrameSnapshot PrepareFrame(Song song, Score score, IPianoSettings settings, Action? updateMidiTask,
                                      FrameSnapshot? reuse = null)
    {
        FrameSnapshot f = reuse ?? new FrameSnapshot();
        f.WindowWidth = _windowWidth;
        f.WindowHeight = _windowHeight;
        f.Viewport = ScoreViewport;
        f.TitleHeight = _titleHeight;

        // drawDisplayText(): nothing the first time
        f.HasRating = _ratingSeen;
        _ratingSeen = true;
        f.PlayMode = Conductor.PlayMode;
        f.WarningMessage = WarningMessage;
        f.SongTitle = song.SongTitle;

        // drawAccurracyBar()
        f.DrawAccuracyBar = f.PlayMode != PlayMode.Listen && f.WarningMessage.Length == 0;
        if (f.DrawAccuracyBar)
        {
            Rating rating = song.Rating;
            rating.CalculateAccuracy();
            f.AccuracyValue = rating.AccuracyValue;
            f.AccuracyColor = rating.AccuracyColor;
        }

        // drawBarNumber(), drawScore(), drawTimeSignature()
        f.BarNumber = song.BarNumber;
        f.RecompileScore = DrawState.CompileRedrawCount != 0;
        f.DisplayHand = DrawState.DisplayHand;
        f.KeySignature = StavePos.KeySignature;
        f.ShowNoteNames = settings.ShowNoteNames;
        f.ColoredNotes = settings.ColoredNotes;
        f.NoteDurationTrail = settings.IsNoteDurationTrailEnabled;
        song.GetTimeSig(out f.TimeSigTop, out f.TimeSigBottom);

        updateMidiTask?.Invoke();

        // CScore::drawScroll()
        if (f.RecompileScore)
            DrawState.ForceCompileRedraw(0); // decrement the compile count until is reaches zero

        f.ShowPianoKeyboard = settings.GetString("View/PianoKeyboard") == "on";
        Array.Clear(f.KeyState);
        if (f.ShowPianoKeyboard)
            TakeKeyboardInfo(score, f);
        f.KeyboardStopped = _keyboardStopped;

        // CScroll::drawScrollingSymbols(true): the queue is brought up to date, then drawn
        score.DrawScrollingSymbols(true);
        f.ClearScrolls();
        for (int channel = 0; channel < Midi.MaxMidiChannels; channel++)
        {
            Scroll scroll = score.GetScroll(channel);
            CircularQueue<SlotDisplayList> queue = scroll.ScrollQueue;
            if (queue.Length == 0 || queue.IndexRef(0).DisplayListId == 0)
                continue;
            ScrollFrame frame = f.NewScroll();
            frame.StartX = Cfg.PlayZoneX + Speed.DeltaAdjustF(scroll.DeltaTail) * scroll.NoteSpacingFactor;
            frame.StaveCenterY = StavePos.StaveCenterY;
            // glCallList of the first slot: each display list calls the next one
            int id = queue.IndexRef(0).DisplayListId;
            for (int i = 0; i < queue.Length && id != 0; i++)
            {
                CompiledSlot? compiled = scroll.GetCompiledSlot(id);
                if (compiled == null)
                    break; // not compiled yet (or deleted): the chain ends here
                frame.Slots.Add(compiled);
                id = compiled.NextDisplayListId;
            }
        }
        f.ClipScroll = f.NoteDurationTrail;

        // CPiano::drawPianoInput()
        Piano piano = score.Piano;
        f.GoodChord = piano.GoodChord;
        f.BadChord = piano.BadChord;
        f.NoteNameListLength = Math.Min(piano.NoteNameListLength, f.NoteNameList.Length);
        Array.Copy(piano.NoteNameList, f.NoteNameList, f.NoteNameListLength);
        f.RhythmTapping = piano.RhythmTapping;

        Scroll? active = score.ActiveScroll;
        f.ScrollPosition = active == null ? 0.0 : (double)active.ScrolledTicks / Speed.AdjustFactor * active.NoteSpacingFactor;
        f.Timestamp = Stopwatch.GetTimestamp();
        return f;
    }

    // The part of CScore::drawPianoKeyboard() that reads the score
    private void TakeKeyboardInfo(Score score, FrameSnapshot f)
    {
        Chord chord = score.Piano.BadChord;
        for (int n = 0; n < chord.Length; ++n)
        {
            int k = chord.GetNote(n).Pitch - 21;
            k = k < 0 ? 0 : (k >= FrameSnapshot.KeysCount ? FrameSnapshot.KeysCount - 1 : k);
            f.KeyState[k] = 2;
        }

        for (int channel = 0; channel < Midi.MaxMidiChannels; channel++)
        {
            _keyboardNotes.Clear();
            bool stopped = score.GetScroll(channel).GetKeyboardInfo(_keyboardNotes);
            foreach (int note in _keyboardNotes)
            {
                if (note == 0)
                    break; // the C++ list ends with a 0
                _keyboardStopped = stopped;
                int k = note - 21;
                k = k < 0 ? 0 : (k >= FrameSnapshot.KeysCount ? FrameSnapshot.KeysCount - 1 : k);
                f.KeyState[k] = 1;
            }
        }
    }

    /// <summary>
    /// CGLView::paintGL: draws a frame. The canvas is in device independent pixels of the window
    /// (the size given to <see cref="Resize"/>); devicePixelRatio is the display scaling.
    /// </summary>
    public void Render(SKCanvas canvas, FrameSnapshot frame, float devicePixelRatio)
    {
        if (devicePixelRatio <= 0)
            devicePixelRatio = 1.0f;
        UpdateFonts();

        canvas.Save();
        // glClear(): the whole window (SKCanvas.Clear would ignore the place of the control)
        using (var background = new SKPaint { Color = GlCanvas.ToSkColor(Cfg.BackgroundColor) })
            canvas.DrawRect(new SKRect(0, 0, frame.WindowWidth, frame.WindowHeight), background);

        // glViewport and glOrtho(0, w, 0, h): y goes up from the bottom of the viewport
        SKRectI viewport = frame.Viewport;
        canvas.ClipRect(new SKRect(viewport.Left, viewport.Top, viewport.Right, viewport.Bottom));
        canvas.Translate(viewport.Left, viewport.Bottom);
        canvas.Scale(1.0f, -1.0f);

        _gl.Canvas = canvas;
        _gl.LineWidthScale = 1.0f / devicePixelRatio;
        _gl.LineWidth(1.0f);
        _gl.LineStipple(false);
        _score.ApplyFrame(frame, devicePixelRatio);

        DrawDisplayText(frame);
        DrawAccurracyBar(frame);
        DrawBarNumber(frame);
        _score.DrawScore(frame);
        DrawTimeSignature(frame);
        _score.DrawScroll(frame, refresh: true);

        canvas.Restore();
        _gl.Canvas = null!;
    }

    private void UpdateFonts()
    {
        if (_fontsPointSize == WindowFontPointSize && _timeSigFont.Typeface == Typeface)
            return;
        // This is a work around for Windows different display scaling option
        int widgetPointSize = WindowFontPointSize;
        _timeSigFont.Typeface = Typeface;
        _timeSigFont.Size = PointsToPixels(widgetPointSize * 2);
        _timeRatingFont.Typeface = Typeface;
        _timeRatingFont.Size = PointsToPixels((int)(widgetPointSize * 1.2));
        _fontsPointSize = WindowFontPointSize;
        _accuracyBarStart = 0;
    }

    // Qt fonts are in points at 96 dots per inch of the window
    private static float PointsToPixels(int points) => points * 96.0f / 72.0f;

    // CGLView::renderText(): a QPainter text with its baseline at (x, y), "4 pixels" lower
    private void RenderText(float x, float y, string text, SKFont font)
    {
        _gl.Text(text, MathF.Round(x), MathF.Round(y) - 4, font);
    }

    private void DrawTimeSignature(FrameSnapshot frame)
    {
        int topNumber = frame.TimeSigTop;
        int bottomNumber = frame.TimeSigBottom;
        if (topNumber == 0)
            return;

        string bufferTop = topNumber.ToString(CultureInfo.InvariantCulture);
        string bufferBottom = bottomNumber.ToString(CultureInfo.InvariantCulture);

        float x = Cfg.TimeSignatureX;

        _gl.Color((frame.DisplayHand != WhichPart.Left) ? Cfg.NoteColor : Cfg.NoteColorDim);

        float y = new StavePos(WhichPart.Right, 0).PosY + 5;
        RenderText(x, y, bufferTop, _timeSigFont);
        y = new StavePos(WhichPart.Right, -3).PosY - 2;
        RenderText(x, y, bufferBottom, _timeSigFont);

        _gl.Color((frame.DisplayHand != WhichPart.Right) ? Cfg.NoteColor : Cfg.NoteColorDim);

        y = new StavePos(WhichPart.Left, 0).PosY + 5;
        RenderText(x, y, bufferTop, _timeSigFont);
        y = new StavePos(WhichPart.Left, -3).PosY - 2;
        RenderText(x, y, bufferBottom, _timeSigFont);
    }

    private void DrawAccurracyBar(FrameSnapshot frame)
    {
        if (!frame.DrawAccuracyBar)
            return;

        float y = (float)(Cfg.AppHeight - 14);
        float x = (float)_accuracyBarStart;
        const int width = 360;
        const int lineWidth = 8 / 2;

        float accuracy = frame.AccuracyValue;
        _gl.Color(frame.AccuracyColor);
        _gl.Rect(x, y - lineWidth, x + width * accuracy, y + lineWidth);
        _gl.Color(Cfg.BackgroundColor);
        _gl.Rect(x + width * accuracy, y - lineWidth, x + width, y + lineWidth);

        _gl.LineWidth(1);
        _gl.Color3f(1.0f, 1.0f, 1.0f);
        _gl.LineLoop(stackalloc float[] { x, y + lineWidth, x + width, y + lineWidth, x + width, y - lineWidth, x, y - lineWidth });
    }

    private void DrawDisplayText(FrameSnapshot frame)
    {
        if (!frame.HasRating)
            return; // don't run this func the first time it is called

        int y = Cfg.AppHeight - 14;

        if (frame.WarningMessage.Length != 0)
        {
            _gl.Color3f(1.0f, 0.2f, 0.0f);
            RenderText(TextLeftMargin, y - 4, frame.WarningMessage, _timeRatingFont);
            return;
        }

        _gl.Color3f(1.0f, 1.0f, 1.0f);

        if (frame.PlayMode != PlayMode.Listen)
        {
            if (_accuracyBarStart == 0)
            {
                // QFontMetrics::boundingRect(text).right(): the advance of the text, less one
                _accuracyBarStart = (int)MathF.Round(GlCanvas.MeasureText(_timeRatingFont, AccuracyText + "  ")) - 1 + TextLeftMargin;
            }
            RenderText(TextLeftMargin, y - 4, AccuracyText, _timeRatingFont);
        }

        if (frame.TitleHeight < 45)
            return;

        y = Cfg.AppHeight - frame.TitleHeight;

        RenderText(TextLeftMargin, y + 6, SongText + " " + frame.SongTitle, _timeRatingFont);
    }

    private void DrawBarNumber(FrameSnapshot frame)
    {
        float y = (float)(Cfg.AppHeight - frame.TitleHeight - 34);
        float x = (float)TextLeftMargin;

        _gl.Color3f(1.0f, 1.0f, 1.0f);
        RenderText(x, y, BarText + " " + (frame.BarNumber + Bar.FirstBarNumber).ToString(CultureInfo.InvariantCulture), _timeRatingFont);
    }

    public void Dispose()
    {
        _score.Dispose();
        _gl.Dispose();
        _timeSigFont.Dispose();
        _timeRatingFont.Dispose();
    }
}
