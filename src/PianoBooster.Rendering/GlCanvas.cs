// The OpenGL 1.1 calls of PianoBooster (C++) on a SkiaSharp canvas. Part of the .NET port of
// PianoBooster. GPLv3.
//
// The C++ code draws in immediate mode: it sets a current colour and line width and then
// sends vertices (glBegin/glEnd). Some symbols (the sharp, the flat ...) do not set a colour
// and are drawn with the one set before, so the same state is kept here.
//
// The coordinates are those of the C++ code: in pixels of the window (device independent
// pixels), with y going up from the bottom of the viewport (glOrtho(0, w, 0, h)). The
// ViewRenderer sets the canvas matrix so; the text is turned the right way up when drawn.

using System;
using PianoBooster.Core;
using SkiaSharp;

namespace PianoBooster.Rendering;

/// <summary>Immediate mode drawing like OpenGL 1.1, on an <see cref="SKCanvas"/>.</summary>
public sealed class GlCanvas : IDisposable
{
    private readonly SKPaint _strokePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Butt,   // GL lines have no caps
        StrokeJoin = SKStrokeJoin.Round, // and no joins: round ones leave no gaps and no spikes
    };
    private readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPath _path = new();
    private SKPathEffect? _stipple;
    private float _stippleScale;

    private SKColor _color = SKColors.White;
    private float _lineWidth = 1.0f;
    private bool _lineStipple;

    public GlCanvas()
    {
        LineWidthScale = 1.0f;
    }

    /// <summary>Where to draw. Set before each frame (or each recording).</summary>
    public SKCanvas Canvas { get; set; } = null!;

    /// <summary>
    /// glLineWidth (and glLineStipple) are in pixels of the screen, which are smaller than the
    /// pixels of the window with the display scaling of the system (e.g. 150%). So the widths
    /// are multiplied by 1 / scaling, to draw the lines as thin as the C++ code does.
    /// </summary>
    public float LineWidthScale { get; set; }

    /// <summary>The current colour (glColor), with its alpha.</summary>
    public SKColor CurrentColor => _color;

    // ---- state ----

    /// <summary>glColor3f.</summary>
    public void Color3f(float red, float green, float blue) => _color = ToSkColor(red, green, blue, 1.0f);

    /// <summary>glColor4f (only used with blending).</summary>
    public void Color4f(float red, float green, float blue, float alpha) => _color = ToSkColor(red, green, blue, alpha);

    /// <summary>CDraw::drColor.</summary>
    public void Color(PbColor color) => Color3f(color.Red, color.Green, color.Blue);

    /// <summary>glLineWidth.</summary>
    public void LineWidth(float width) => _lineWidth = width;

    /// <summary>glEnable/glDisable(GL_LINE_STIPPLE) with glLineStipple(1, 0x0f0f): 4 pixels on, 4 off.</summary>
    public void LineStipple(bool enable) => _lineStipple = enable;

    /// <summary>A GL colour component (0 to 1) to a byte, as the GL does (rounded).</summary>
    public static byte ToByte(float value)
    {
        if (!(value > 0.0f)) return 0; // also NaN
        if (value >= 1.0f) return 255;
        return (byte)MathF.Round(value * 255.0f);
    }

    public static SKColor ToSkColor(float red, float green, float blue, float alpha) =>
        new(ToByte(red), ToByte(green), ToByte(blue), ToByte(alpha));

    public static SKColor ToSkColor(PbColor color) => ToSkColor(color.Red, color.Green, color.Blue, 1.0f);

    // ---- matrix ----

    /// <summary>glPushMatrix.</summary>
    public void PushMatrix() => Canvas.Save();

    /// <summary>glPopMatrix.</summary>
    public void PopMatrix() => Canvas.Restore();

    /// <summary>glTranslatef.</summary>
    public void Translate(float x, float y) => Canvas.Translate(x, y);

    /// <summary>glScalef.</summary>
    public void Scale(float x, float y) => Canvas.Scale(x, y);

    // ---- primitives ----

    private SKPaint StrokePaint()
    {
        _strokePaint.Color = _color;
        _strokePaint.StrokeWidth = _lineWidth * LineWidthScale;
        if (_lineStipple)
        {
            if (_stipple == null || _stippleScale != LineWidthScale)
            {
                _stipple?.Dispose();
                float dash = 4.0f * LineWidthScale;
                _stipple = SKPathEffect.CreateDash(new[] { dash, dash }, 0);
                _stippleScale = LineWidthScale;
            }
            _strokePaint.PathEffect = _stipple;
        }
        else
        {
            _strokePaint.PathEffect = null;
        }
        return _strokePaint;
    }

    /// <summary>GL_LINES: each pair of points (x1, y1, x2, y2) is a line.</summary>
    public void Lines(ReadOnlySpan<float> xy)
    {
        _path.Reset();
        for (int i = 0; i + 3 < xy.Length; i += 4)
        {
            _path.MoveTo(xy[i], xy[i + 1]);
            _path.LineTo(xy[i + 2], xy[i + 3]);
        }
        Canvas.DrawPath(_path, StrokePaint());
    }

    /// <summary>One line (CDraw::oneLine).</summary>
    public void Line(float x1, float y1, float x2, float y2)
    {
        if (_lineStipple)
        {
            Lines(stackalloc float[] { x1, y1, x2, y2 });
            return;
        }
        Canvas.DrawLine(x1, y1, x2, y2, StrokePaint());
    }

    /// <summary>GL_LINE_STRIP: the points (x, y ...) joined one after the other.</summary>
    public void LineStrip(ReadOnlySpan<float> xy) => Strip(xy, close: false);

    /// <summary>GL_LINE_LOOP: like a strip, and back to the first point.</summary>
    public void LineLoop(ReadOnlySpan<float> xy) => Strip(xy, close: true);

    private void Strip(ReadOnlySpan<float> xy, bool close)
    {
        if (xy.Length < 4)
            return;
        _path.Reset();
        _path.MoveTo(xy[0], xy[1]);
        for (int i = 2; i + 1 < xy.Length; i += 2)
            _path.LineTo(xy[i], xy[i + 1]);
        if (close)
            _path.Close();
        Canvas.DrawPath(_path, StrokePaint());
    }

    /// <summary>GL_POLYGON (convex) or GL_QUADS of one quad: filled with the current colour.</summary>
    public void Polygon(ReadOnlySpan<float> xy)
    {
        if (xy.Length < 6)
            return;
        _path.Reset();
        _path.MoveTo(xy[0], xy[1]);
        for (int i = 2; i + 1 < xy.Length; i += 2)
            _path.LineTo(xy[i], xy[i + 1]);
        _path.Close();
        _fillPaint.Color = _color;
        Canvas.DrawPath(_path, _fillPaint);
    }

    /// <summary>glRectf: a filled rectangle between two corners (in any order).</summary>
    public void Rect(float x1, float y1, float x2, float y2)
    {
        _fillPaint.Color = _color;
        Canvas.DrawRect(SKRect.Create(MathF.Min(x1, x2), MathF.Min(y1, y2), MathF.Abs(x2 - x1), MathF.Abs(y2 - y1)), _fillPaint);
    }

    /// <summary>
    /// Draws a text with the current colour, its baseline starting at (x, y) of the C++ coordinates
    /// (y up). The glyphs are turned the right way up.
    /// </summary>
    public void Text(string text, float x, float y, SKFont font)
    {
        _textPaint.Color = _color;
        Canvas.Save();
        Canvas.Translate(x, y);
        Canvas.Scale(1.0f, -1.0f);
        Canvas.DrawText(text, 0, 0, font, _textPaint);
        Canvas.Restore();
    }

    /// <summary>The advance width of a text.</summary>
    public static float MeasureText(SKFont font, string text)
    {
        int count = font.CountGlyphs(text);
        Span<ushort> glyphs = count <= 256 ? stackalloc ushort[count] : new ushort[count];
        font.GetGlyphs(text, glyphs);
        return font.MeasureText(glyphs);
    }

    public void Dispose()
    {
        _strokePaint.Dispose();
        _fillPaint.Dispose();
        _textPaint.Dispose();
        _path.Dispose();
        _stipple?.Dispose();
    }
}
