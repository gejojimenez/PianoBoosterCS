// Ported from the drawing parts of Scroll.cpp of PianoBooster (C++) (compileSlot() and
// drawScrollingSymbols()). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
//
// The C++ code compiles each slot into a display list that moves the drawing on by the delta
// time of the slot, draws its symbols and calls the display list of the next slot; drawing the
// first one draws them all. Here each CompiledSlot is recorded once into an SKPicture, and the
// pictures are drawn one after the other, each moved on by the delta of its slot.

using System.Collections.Generic;
using PianoBooster.Core;
using SkiaSharp;

namespace PianoBooster.Rendering;

/// <summary>Draws the scrolling notes (the C++ CScroll::drawScrollingSymbols(true)).</summary>
public sealed class ScrollRenderer : Draw
{
    // big enough for any slot: its trails can be 16 quarter notes long
    private static readonly SKRect s_slotBounds = new(-4000, -4000, 8000, 4000);

    private sealed class CachedPicture
    {
        public SKPicture Picture = null!;
        public int LastFrame;
    }

    private readonly Dictionary<CompiledSlot, CachedPicture> _pictures = new(ReferenceEqualityComparer.Instance);
    private readonly List<CompiledSlot> _unused = new();
    private int _frame;
    private float _picturesPixelRatio = -1;

    public ScrollRenderer(GlCanvas gl) : base(gl)
    {
    }

    /// <summary>Draw each slot from an SKPicture recorded once (like the display lists), or
    /// draw the symbols every time.</summary>
    public bool UsePictures { get; set; } = true;

    /// <summary>How many slots were recorded into a picture in the last frame.</summary>
    public int RecordedLastFrame { get; private set; }

    /// <summary>How many slots were drawn in the last frame.</summary>
    public int DrawnLastFrame { get; private set; }

    public void DrawScrollingSymbols(FrameSnapshot frame)
    {
        _frame++;
        RecordedLastFrame = 0;
        DrawnLastFrame = 0;
        if (_picturesPixelRatio != DevicePixelRatio) // the line widths and the text depend on it
        {
            ClearPictures();
            _picturesPixelRatio = DevicePixelRatio;
        }

        foreach (ScrollFrame scroll in frame.Scrolls)
        {
            if (scroll.Slots.Count == 0)
                continue;

            // The trails can go past the ends of the scrolling part of the stave, so cut them off there
            // (the clef and key signature on the left are not redrawn each time)
            bool clip = frame.ClipScroll;
            if (clip)
            {
                Gl.Canvas.Save();
                Gl.Canvas.ClipRect(new SKRect(Cfg.ScrollStartX, -100000, Cfg.StaveEndX, 100000));
            }

            Gl.PushMatrix();
            Gl.Translate(scroll.StartX, scroll.StaveCenterY);

            foreach (CompiledSlot slot in scroll.Slots)
            {
                Gl.Translate(slot.DeltaX, 0.0f); // move position
                if (UsePictures)
                    Gl.Canvas.DrawPicture(GetPicture(slot));
                else
                    DrawSlot(slot);
                DrawnLastFrame++;
            }

            Gl.PopMatrix();

            if (clip)
                Gl.Canvas.Restore();
        }

        RemoveUnusedPictures();
    }

    private SKPicture GetPicture(CompiledSlot slot)
    {
        if (_pictures.TryGetValue(slot, out CachedPicture? cached))
        {
            cached.LastFrame = _frame;
            return cached.Picture;
        }

        // glNewList(GL_COMPILE)
        SKCanvas canvas = Gl.Canvas;
        using var recorder = new SKPictureRecorder();
        Gl.Canvas = recorder.BeginRecording(s_slotBounds);
        try
        {
            DrawSlot(slot);
        }
        finally
        {
            Gl.Canvas = canvas;
        }
        cached = new CachedPicture { Picture = recorder.EndRecording(), LastFrame = _frame };
        _pictures.Add(slot, cached);
        RecordedLastFrame++;
        return cached.Picture;
    }

    // glDeleteLists(): the slots that have gone (or have been compiled again)
    private void RemoveUnusedPictures()
    {
        foreach (KeyValuePair<CompiledSlot, CachedPicture> item in _pictures)
        {
            if (item.Value.LastFrame != _frame)
                _unused.Add(item.Key);
        }
        foreach (CompiledSlot slot in _unused)
        {
            _pictures[slot].Picture.Dispose();
            _pictures.Remove(slot);
        }
        _unused.Clear();
    }

    private void ClearPictures()
    {
        foreach (CachedPicture cached in _pictures.Values)
            cached.Picture.Dispose();
        _pictures.Clear();
    }

    public int CachedPictureCount => _pictures.Count;

    public override void Dispose()
    {
        ClearPictures();
        base.Dispose();
    }
}
