// Ported from the drawing parts of Score.cpp of PianoBooster (C++) (drawScore(), drawScroll()
// and drawPianoKeyboard()). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
//
// The display lists of the score (the clefs, the key signature and the start of the staves)
// and of the staves (the playing zone and the staves) are SKPictures, recorded again when the
// C++ code compiles its display lists again (CDraw::forceCompileRedraw()).

using System;
using PianoBooster.Core;
using SkiaSharp;

namespace PianoBooster.Rendering;

/// <summary>Draws the score (the C++ CScore drawing), with the scrolls and the piano.</summary>
public sealed class ScoreRenderer : Draw
{
    private SKPicture? _scoreDisplayList;
    private SKPicture? _stavesDisplayList;
    private float _picturesPixelRatio = -1;
    private readonly PianoKeyboard _pianoKeyboard = new();

    public ScoreRenderer(GlCanvas gl) : base(gl)
    {
        Scroll = new ScrollRenderer(gl);
        Piano = new PianoRenderer(gl);
    }

    public ScrollRenderer Scroll { get; }
    public PianoRenderer Piano { get; }

    /// <summary>Sets what the C++ code reads from statics and settings, for this frame.</summary>
    public void ApplyFrame(FrameSnapshot frame, float devicePixelRatio)
    {
        foreach (Draw draw in new Draw[] { this, Scroll, Piano })
        {
            draw.DisplayHand = frame.DisplayHand;
            draw.ShowNoteNames = frame.ShowNoteNames;
            draw.ColoredNotes = frame.ColoredNotes;
            draw.NoteDurationTrail = frame.NoteDurationTrail;
            draw.DevicePixelRatio = devicePixelRatio;
        }
        if (_picturesPixelRatio != devicePixelRatio)
        {
            ClearPictures();
            _picturesPixelRatio = devicePixelRatio;
        }
    }

    // glNewList(GL_COMPILE_AND_EXECUTE)
    private SKPicture Record(Action draw)
    {
        SKCanvas canvas = Gl.Canvas;
        using var recorder = new SKPictureRecorder();
        Gl.Canvas = recorder.BeginRecording(new SKRect(-10000, -10000, 10000, 10000));
        try
        {
            draw();
        }
        finally
        {
            Gl.Canvas = canvas;
        }
        return recorder.EndRecording();
    }

    public void DrawScroll(FrameSnapshot frame, bool refresh)
    {
        if (!refresh)
        {
            float topY = new StavePos(WhichPart.Right, StavePos.MaxStaveIndex).PosY;
            float bottomY = new StavePos(WhichPart.Left, StavePos.MinStaveIndex).PosY;
            Gl.Color(Cfg.BackgroundColor);
            Gl.Rect(Cfg.ScrollStartX, topY, (float)Cfg.AppWidth, bottomY);
        }

        if (frame.RecompileScore || _stavesDisplayList == null)
        {
            _stavesDisplayList?.Dispose();
            _stavesDisplayList = Record(() =>
            {
                DrawSymbol(new Symbol(MusicalSymbol.PlayingZone, new StavePos(WhichPart.Both, 0)), Cfg.PlayZoneX);
                DrawStaves(Cfg.ScrollStartX, Cfg.StaveEndX);
            });
        }
        Gl.Canvas.DrawPicture(_stavesDisplayList);

        if (frame.ShowPianoKeyboard)
            DrawPianoKeyboard(frame);

        Scroll.DrawScrollingSymbols(frame);
        Piano.DrawPianoInput(frame);
    }

    /// <summary>The keyboard of View > Show Piano Keyboard (the struct of CScore::drawPianoKeyboard()).</summary>
    private sealed class PianoKeyboard
    {
        private int _i, _k;
        private float _yStart;
        private float _xSize;
        private float _ySize;
        private float _xPlaceSize;
        private float _xKeySize;
        public readonly byte[] State = new byte[FrameSnapshot.KeysCount];
        public bool Stopped;
        private bool _constructed;
        private GlCanvas _gl = null!;

        // In C++ it is a static, made the first time the keyboard is drawn: its size does not
        // follow the window when it is resized afterwards (see PORTING.md)
        private void Construct()
        {
            _i = 0; _k = 0;
            _yStart = 0.0f;
            _xSize = Cfg.StaveEndX - Cfg.StaveStartX;
            _ySize = 30;

            _xPlaceSize = _xSize / 52.0f;
            _xKeySize = _xPlaceSize - _xPlaceSize * 0.1f;
            Stopped = false;
            _constructed = true;
        }

        private void KeyColor(int k, PbColor normal)
        {
            _gl.Color(normal);
            if (State[k] == 1) _gl.Color(Stopped ? Cfg.PlayedStoppedColor : Cfg.NoteColor);
            if (State[k] == 2) _gl.Color(Cfg.PlayedBadColor);
        }

        private void DrawBlackKey(int i, int k)
        {
            _gl.PushMatrix();
            float yBlackShift = _ySize / 2.5f;
            float yBlackSize = _ySize - yBlackShift;
            _gl.Scale(1.0f, 1.4f);
            _gl.Translate(Cfg.StaveStartX + _xPlaceSize * (float)i - _xPlaceSize / 3.0f, _yStart + yBlackShift);

            float xKeySize = _xKeySize / 1.5f;

            KeyColor(k, new PbColor(0.0, 0.0, 0.0));
            _gl.Polygon(stackalloc float[] { 0, yBlackSize, xKeySize, yBlackSize, xKeySize, 0, 0, 0 });

            _gl.PopMatrix();
            State[k] = 0;
        }

        private void DrawWhiteKey()
        {
            _gl.PushMatrix();
            _gl.Scale(1.0f, 1.4f);
            _gl.Translate(Cfg.StaveStartX + _xPlaceSize * (float)(_i++), _yStart);

            KeyColor(_k, new PbColor(1.0, 1.0, 1.0));
            _gl.Polygon(stackalloc float[] { 0, _ySize, _xKeySize, _ySize, _xKeySize, 0, 0, 0 });

            _gl.PopMatrix();
            State[_k++] = 0;
        }

        private void DrawOctave()
        {
            DrawWhiteKey();
            int b1 = _i, k1 = _k++;
            DrawWhiteKey();
            int b2 = _i, k2 = _k++;
            DrawWhiteKey();
            DrawWhiteKey();
            int b3 = _i, k3 = _k++;
            DrawWhiteKey();
            int b4 = _i, k4 = _k++;
            DrawWhiteKey();
            int b5 = _i, k5 = _k++;
            DrawWhiteKey();
            DrawBlackKey(b1, k1);
            DrawBlackKey(b2, k2);
            DrawBlackKey(b3, k3);
            DrawBlackKey(b4, k4);
            DrawBlackKey(b5, k5);
        }

        public void DrawKeyboard(GlCanvas gl)
        {
            _gl = gl;
            _i = _k = 0;
            DrawWhiteKey();
            int b1 = _i, k1 = _k++;
            DrawWhiteKey();
            DrawBlackKey(b1, k1);
            for (int i = 0; i < 7; ++i) DrawOctave();
            DrawWhiteKey();
        }

        public void EnsureConstructed()
        {
            if (!_constructed)
                Construct();
        }
    }

    public void DrawPianoKeyboard(FrameSnapshot frame)
    {
        _pianoKeyboard.EnsureConstructed();
        // The notes were looked for (CScroll::getKeyboardInfo) when the photo was taken
        for (int k = 0; k < FrameSnapshot.KeysCount; k++)
        {
            if (frame.KeyState[k] != 0)
                _pianoKeyboard.State[k] = frame.KeyState[k];
        }
        _pianoKeyboard.Stopped = frame.KeyboardStopped;
        _pianoKeyboard.DrawKeyboard(Gl);
    }

    public void DrawScore(FrameSnapshot frame)
    {
        if (frame.RecompileScore || _scoreDisplayList == null)
        {
            _scoreDisplayList?.Dispose();
            _scoreDisplayList = Record(() =>
            {
                Gl.Color(Cfg.StaveColor);

                DrawSymbol(new Symbol(MusicalSymbol.GClef, new StavePos(WhichPart.Right, -1)), Cfg.ClefX); // The Treble Clef
                DrawSymbol(new Symbol(MusicalSymbol.FClef, new StavePos(WhichPart.Left, 1)), Cfg.ClefX);
                DrawKeySignature(frame.KeySignature);
                DrawStaves(Cfg.StaveStartX, Cfg.ScrollStartX);
            });
        }
        Gl.Canvas.DrawPicture(_scoreDisplayList);
    }

    private void ClearPictures()
    {
        _scoreDisplayList?.Dispose();
        _scoreDisplayList = null;
        _stavesDisplayList?.Dispose();
        _stavesDisplayList = null;
    }

    public override void Dispose()
    {
        ClearPictures();
        Scroll.Dispose();
        Piano.Dispose();
        base.Dispose();
    }
}
