// Ported from Draw.h and Draw.cpp of PianoBooster (C++). Copyright (c) 2008-2020, L. J. Barman
// and others. GPLv3.
//
// The OpenGL calls go to a GlCanvas (same coordinates, same colours and line widths). The text
// of FTGL (a polygon font of DejaVuSans at the exact position) is drawn with the same font by
// Skia. The code drawn when NO_USE_FTGL is defined (note names made of lines) is not ported:
// the C++ code uses FTGL (and does not compile without it, see PORTING.md).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PianoBooster.Core;
using SkiaSharp;

namespace PianoBooster.Rendering;

/// <summary>Draws the symbols of the score (the C++ CDraw). The state that the C++ code reads
/// from statics and from the settings is set for each frame by the ViewRenderer.</summary>
public class Draw : IDisposable
{
    public const float HorizontalSpacingFactor = DrawState.HorizontalSpacingFactor; // defines the speed of the scrolling
    public const int FontSize = 16;
    private const int BeatMarkerHeight = 10; // The height of the beat markers in the stave positions

    /// <summary>The names of the notes, C to B (translated by the interface; tr() in C++).</summary>
    public static string[] NoteNames { get; set; } = { "C", "D", "E", "F", "G", "A", "B" };

    private static SKTypeface? s_textTypeface;

    private readonly SKFont _font = new() { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
    private float _fontScale = -1;

    public Draw(GlCanvas gl)
    {
        Gl = gl;
        DisplayHand = WhichPart.Both;
        DevicePixelRatio = 1.0f;
        _font.Typeface = TextTypeface;
    }

    public GlCanvas Gl { get; }

    // ---- what the C++ code reads from statics and settings ----

    /// <summary>CDraw::m_displayHand.</summary>
    public WhichPart DisplayHand { get; set; }
    /// <summary>m_settings->showNoteNames().</summary>
    public bool ShowNoteNames { get; set; }
    /// <summary>m_settings->coloredNotes().</summary>
    public bool ColoredNotes { get; set; }
    /// <summary>m_settings->isNoteDurationTrailEnabled().</summary>
    public bool NoteDurationTrail { get; set; }
    /// <summary>qApp->devicePixelRatio(): the display scaling of the system (1.5 for 150%).</summary>
    public float DevicePixelRatio { get; set; }

    /// <summary>The DejaVuSans.ttf font, looked for in the same places as the C++ code.</summary>
    public static SKTypeface TextTypeface
    {
        get
        {
            if (s_textTypeface != null)
                return s_textTypeface;
            var listPathFonts = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "fonts", "DejaVuSans.ttf"),
                Path.Combine(AppContext.BaseDirectory, "..", "Resources", "fonts", "DejaVuSans.ttf"),
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/TTF/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/TTF/DejaVuSans.ttf",
                "/usr/share/fonts/truetype/DejaVuSans.ttf",
                "/usr/local/share/fonts/dejavu/DejaVuSans.ttf",
            };
            foreach (string path in listPathFonts)
            {
                if (File.Exists(path))
                {
                    s_textTypeface = SKTypeface.FromFile(path);
                    if (s_textTypeface != null)
                        return s_textTypeface;
                }
            }
            // The C++ code stops here (exit(0)); the default font is better than nothing
            Log.Error("Font DejaVuSans.ttf was not found !");
            s_textTypeface = SKTypeface.Default;
            return s_textTypeface;
        }
    }

    /// <summary>The length on the screen (in pixels) of the trail that shows how long a note sounds.</summary>
    public static float NoteDurationTrailLength(int midiDuration) => DrawState.NoteDurationTrailLength(midiDuration);

    private void DrColor(PbColor color) => Gl.Color(color);

    private void DrawNoteDurationTrail(Symbol symbol, float x, float y, PbColor color)
    {
        const float halfHeight = 4.0f; // a little narrower than the note head (12 pixels high)
        const float alpha = 0.4f;      // transparent, so that the stave lines show through

        float length = NoteDurationTrailLength(symbol.MidiDuration);
        if (length <= 0.0f)
            return;

        // It starts at the centre of the note head, which is drawn on top of it
        Gl.Color4f(color.Red, color.Green, color.Blue, alpha);
        Gl.Rect(x, y - halfHeight, x + length, y + halfHeight);
    }

    protected void OneLine(float x1, float y1, float x2, float y2) => Gl.Line(x1, y1, x2, y2);

    private void DrawStaveExtentsion(Symbol symbol, float x, int noteWidth, bool playable)
    {
        int index = symbol.StavePos.StaveIndex;
        if (index < 6 && index > -6)
            return;
        index = index & ~1; // Force index to be even
        WhichPart hand = symbol.StavePos.Hand;

        if (playable)
            DrColor(Cfg.StaveColor);
        else
            DrColor(Cfg.StaveColorDim);

        Gl.LineWidth(Cfg.StaveThickness);
        Span<float> lines = stackalloc float[4 * 16];
        int n = 0;
        while (index >= 6 || index <= -6)
        {
            float posY = new StavePos(hand, index).PosYRelative;
            if (n + 4 <= lines.Length)
            {
                lines[n++] = x - (float)noteWidth / 2.0f - 4.0f;
                lines[n++] = posY;
                lines[n++] = x + (float)noteWidth / 2.0f + 4.0f;
                lines[n++] = posY;
            }

            // Move the index closer to the stave centre
            if (index > 0)
                index -= 2;
            else
                index += 2;
        }
        Gl.Lines(lines[..n]);
    }

    private float FontScaleFactor()
    {
        // The font sizes are in screen pixels (as the bitmap font used before), but the score is
        // drawn in pixels of the window, which are bigger with the display scaling of the system
        // (e.g. 150%), so scale the text down to keep its size.
        // It is also made a little bigger than the bitmap font was (about 14 screen pixels high
        // instead of 11): the polygon font is anti-aliased, and at the smaller size its thinnest
        // lines (like the sharp of "Fa#") were narrower than a pixel and partly lost.
        const float textSizeFactor = 1.3f;
        return textSizeFactor / DevicePixelRatio;
    }

    /// <summary>CDraw::renderText: the text centred on x, the bottom of its descenders on y.</summary>
    protected void RenderText(float x, float y, string s)
    {
        float scale = FontScaleFactor();
        if (scale != _fontScale)
        {
            _font.Size = FontSize * scale;
            _fontScale = scale;
        }
        float w = GlCanvas.MeasureText(_font, s);
        float h = -_font.Metrics.Descent; // FTGL's Descender() is negative (below the baseline)
        Gl.Text(s, x - w / 2, y - h, _font);
    }

    protected void DrawNoteName(int midiNote, float x, float y, int type)
    {
        _ = type;
        // Ignore note that are too high
        if (midiNote > Midi.TopC + 6)
            return;

        StaveLookup item = StavePos.MidiNote2Name(midiNote);

        DrColor(Cfg.NoteNameColor);

        Gl.LineWidth(1.0f);

        const char flat = '♭';
        const char natural = '♮';
        const char sharp = '♯';

        if (0 < item.PianoNote && item.PianoNote < 8)
        {
            string accident = item.Accidental switch
            {
                -1 => flat.ToString(),
                1 => sharp.ToString(),
                2 => natural.ToString(),
                _ => "",
            };
            string note = NoteNames[item.PianoNote - 1] + accident;
            RenderText(x, y, note);
        }
    }

    private void DrawStaveNoteName(Symbol symbol, float x, float y)
    {
        if (symbol.NoteIndex + 1 != symbol.NoteTotal)
            return;
        if (!ShowNoteNames)
            return;
        y += StavePos.VerticalNoteSpacing * 2 + 3;
        DrawNoteName(symbol.Note, x, y, 1);
    }

    private void CheckAccidental(Symbol symbol, float x, float y)
    {
        const int xGap = 16;

        int accidental = symbol.StavePos.Accidental;

        if (symbol.AccidentalModifier == AccidentalModifier.Suppress)
            accidental = 0; // Suppress the accidental if it is the same bar

        if (symbol.AccidentalModifier == AccidentalModifier.Force)
        {
            // Force the display of an accidental including naturals if it is the same bar
            accidental = StavePos.MidiNote2Name(symbol.Note).Accidental;
            if (accidental == 0)
                accidental = 2;
        }

        if (accidental != 0)
        {
            if (accidental == 1)
                DrawSymbol(new Symbol(MusicalSymbol.Sharp, symbol.StavePos), x - xGap, y);
            else if (accidental == -1)
                DrawSymbol(new Symbol(MusicalSymbol.Flat, symbol.StavePos), x - xGap, y);
            else
                DrawSymbol(new Symbol(MusicalSymbol.Natural, symbol.StavePos), x - xGap, y);
        }
    }

    // The outline of a note head (the 12 vertices of the C++ code), moved to (x, y)
    private static readonly float[] s_noteHead =
    {
        -7.0f,  2.0f,
        -5.0f,  4.0f,
        -1.0f,  6.0f,
         4.0f,  6.0f,
         7.0f,  4.0f,
         7.0f,  1.0f,
         6.0f, -2.0f,
         4.0f, -4.0f,
         0.0f, -6.0f,
        -4.0f, -6.0f,
        -8.0f, -3.0f,
        -8.0f, -0.0f,
    };

    private static void Offset(ReadOnlySpan<float> shape, float x, float y, Span<float> result)
    {
        for (int i = 0; i + 1 < shape.Length; i += 2)
        {
            result[i] = shape[i] + x;
            result[i + 1] = shape[i + 1] + y;
        }
    }

    private void NoteHeadPolygon(float x, float y)
    {
        Span<float> v = stackalloc float[s_noteHead.Length];
        Offset(s_noteHead, x, y, v);
        Gl.Polygon(v);
    }

    private bool DrawNote(Symbol symbol, float x, float y, PbColor color, bool playable)
    {
        const float stemLength = 34.0f;
        float noteWidth = 6.0f;

        if (!Chord.IsNotePlayable(symbol.Note, 0))
        {
            color = Cfg.NoteColorDim;
            playable = false;
        }
        DrawStaveExtentsion(symbol, x, 16, playable);
        DrColor(color);
        bool solidNoteHead = false;
        bool showNoteStem = false;
        int stemFlagCount = 0;

        if (symbol.Type <= MusicalSymbol.Semiquaver)
            stemFlagCount = 2;
        else if (symbol.Type <= MusicalSymbol.Quaver)
            stemFlagCount = 1;

        if (symbol.Type <= MusicalSymbol.Crotchet)
            solidNoteHead = true;

        if (symbol.Type <= MusicalSymbol.Minim)
            showNoteStem = true;

        if (showNoteStem)
        {
            if (!solidNoteHead)
                noteWidth += 1.0f;
            Gl.LineWidth(2.0f);
            Gl.LineStrip(stackalloc float[] { noteWidth + x, 0.0f + y, noteWidth + x, stemLength + y });
        }

        float offset = stemLength;
        while (stemFlagCount > 0)
        {
            Gl.LineWidth(2.0f);
            Gl.Line(noteWidth + x, offset + y, noteWidth + 8.0f + x, offset - 16.0f + y); // a strip of two points
            offset -= 8;
            stemFlagCount--;
        }

        if (solidNoteHead)
        {
            NoteHeadPolygon(x, y);
        }
        else
        {
            Gl.LineWidth(2.0f);
            Span<float> v = stackalloc float[s_noteHead.Length];
            Offset(s_noteHead, x, y, v);
            Gl.LineStrip(v);
        }

        CheckAccidental(symbol, x, y);

        return playable;
    }

    // The treble clef (a line strip of 49 vertices)
    private static readonly float[] s_gClef =
    {
        -0.011922f, -16.11494f, -3.761922f, -12.48994f, -4.859633f, -8.85196f, -4.783288f, -5.42815f,
        -0.606711f, -1.11108f, 5.355545f, 0.48711f, 10.641104f, -1.6473f, 14.293812f, -6.18241f,
        14.675578f, -11.42744f, 12.550578f, -17.30244f, 7.912166f, -20.944f, 3.049705f, -21.65755f,
        -1.711005f, -21.36664f, -6.283661f, -19.66739f, -10.123329f, -16.79162f, -13.363008f, -12.28184f,
        -14.675578f, -5.79969f, -13.66821f, 0.20179f, -10.385341f, 6.27562f, 5.539491f, 20.32671f,
        10.431588f, 28.20584f, 11.00141f, 34.71585f, 9.204915f, 39.62875f, 7.854166f, 42.08262f,
        5.481415f, 42.66649f, 3.57972f, 41.4147f, 1.507889f, 37.35642f, -0.381338f, 31.14317f,
        -0.664306f, 25.51354f, 8.296044f, -32.22694f, 8.050507f, -36.6687f, 6.496615f, -39.52999f,
        3.368583f, -41.7968f, 0.253766f, -42.66649f, -3.599633f, -42.23514f, -8.098754f, -39.46637f,
        -9.463279f, -35.49796f, -7.08037f, -31.36512f, -3.336421f, -31.14057f, -1.360313f, -34.07738f,
        -1.608342f, -37.11828f, -5.729949f, -39.24759f, -7.480646f, -36.2136f, -6.826918f, -33.36919f,
        -4.069083f, -32.9226f, -3.040669f, -34.433f, -3.737535f, -36.38759f, -5.496558f, -36.97633f,
        -5.295932f, -34.01951f,
    };

    // The bass clef (a line strip of 25 vertices and two squares)
    private static readonly float[] s_fClef =
    {
        -15.370325f, -17.42068f, -7.171025f, -13.75432f, -2.867225f, -10.66642f, 0.925165f, -7.03249f,
        4.254425f, -0.65527f, 4.762735f, 7.77848f, 2.693395f, 13.92227f, -1.207935f, 16.80317f,
        -5.526425f, 17.42068f, -10.228205f, 15.65609f, -13.453995f, 10.7128f, -13.133655f, 5.43731f,
        -9.475575f, 3.00714f, -5.846445f, 4.72159f, -5.395545f, 9.72918f, -8.850025f, 11.64372f,
        -11.519385f, 10.35816f, -11.706365f, 6.8704f, -9.463505f, 5.01391f, -7.172075f, 5.81649f,
        -7.189565f, 8.62975f, -9.175055f, 9.82019f, -10.696425f, 8.08395f, -8.843065f, 6.66726f,
        -8.995775f, 8.71136f,
    };
    private static readonly float[] s_fClefDot1 = { 10.0f, 14.0f, 14.0f, 14.0f, 14.0f, 10.0f, 10.0f, 10.0f, 10.0f, 14.0f };
    private static readonly float[] s_fClefDot2 = { 10.0f, 4.0f, 14.0f, 4.0f, 14.0f, 0.0f, 10.0f, 0.0f, 10.0f, 4.0f };

    private static readonly float[] s_sharp =
    {
        -2.0f, -14.0f, -2.0f, 14.0f,
         2.0f, -13.0f,  2.0f, 15.0f,
        -5.0f,   4.0f,  5.0f,  7.0f,
        -5.0f,  -6.0f,  5.0f, -3.0f,
    };
    private static readonly float[] s_flat =
    {
        -4.0f, 17.0f, -4.0f, -6.0f, 2.0f, -2.0f, 5.0f, 2.0f, 5.0f, 4.0f, 3.0f, 5.0f, 0.0f, 5.0f, -4.0f, 2.0f,
    };
    private static readonly float[] s_natural =
    {
         3.0f, -15.0f,  3.0f,  8.0f,
        -3.0f,  -8.0f, -3.0f, 15.0f,
         3.0f,   8.0f, -3.0f,  2.0f,
         3.0f,  -2.0f, -3.0f, -8.0f,
    };

    private enum Primitive { Lines, LineStrip, Polygon }

    private const Primitive s_lines = Primitive.Lines;
    private const Primitive s_lineStrip = Primitive.LineStrip;
    private const Primitive s_polygon = Primitive.Polygon;

    // glBegin(primitive), the vertices of the shape moved to (x, y), glEnd()
    private void Shape(ReadOnlySpan<float> shape, float x, float y, Primitive primitive)
    {
        Span<float> v = shape.Length <= 128 ? stackalloc float[shape.Length] : new float[shape.Length];
        Offset(shape, x, y, v);
        switch (primitive)
        {
            case Primitive.Lines: Gl.Lines(v); break;
            case Primitive.LineStrip: Gl.LineStrip(v); break;
            default: Gl.Polygon(v); break;
        }
    }

    // The colours of the notes with Preferences > "Coloured notes" (see the forum post of Kory,
    // http://piano-booster.2625608.n2.nabble.com/Pianobooster-port-to-arm-linux-or-Android-td7572459.html)
    private static readonly PbColor[] s_noteColors =
    {
        new(1.0, 0.0, 0.0),     // C: Red
        new(1.0, 0.25, 0.0),    // C#: Red
        new(1.0, 0.5, 0.0),     // D: Orange
        new(1.0, 0.75, 0.0),    // D#: Orange
        new(1.0, 1.0, 0.0),     // E: Yellow
        new(0.0, 1.0, 0.0),     // F: Green
        new(0.0, 0.5, 0.5),     // F#: Green
        new(0.0, 0.0, 1.0),     // G: Blue
        new(0.290, 0.0, 0.903), // G#: Blue
        new(0.580, 0.0, 0.827), // A: Dark Violet #9400D3
        new(0.790, 0.0, 0.903), // A#: Dark Violet #9400D3
        new(1.0, 0.0, 1.0),     // B: Magenta
    };

    public void DrawSymbol(Symbol symbol, float x, float y)
    {
        PbColor color = symbol.Color;
        bool playable = true;

        if (DisplayHand != symbol.Hand && DisplayHand != WhichPart.Both)
        {
            if (color == Cfg.NoteColor)
                color = Cfg.NoteColorDim;
            if (color == Cfg.StaveColor)
                color = Cfg.StaveColorDim;
            playable = false;
        }

        switch (symbol.Type)
        {
            case MusicalSymbol.GClef: // The Treble Clef
                y += 4;
                DrColor(color);
                Gl.LineWidth(3.0f);
                Shape(s_gClef, x, y, s_lineStrip);
                break;

            case MusicalSymbol.FClef: // The Base Clef
                DrColor(color);
                Gl.LineWidth(3.0f);
                Shape(s_fClef, x, y, s_lineStrip);
                Shape(s_fClefDot1, x, y, s_polygon);
                Shape(s_fClefDot2, x, y, s_polygon);
                break;

            case MusicalSymbol.NoteHead:
                if (!Chord.IsNotePlayable(symbol.Note, 0))
                {
                    color = Cfg.NoteColorDim;
                    playable = false;
                }
                DrawStaveExtentsion(symbol, x, 16, playable);

                if (ColoredNotes && color == Cfg.NoteColor) //KORY added
                    color = s_noteColors[symbol.Note % Midi.Octave];

                // shows how long the note sounds, drawn first so that the note head is on top
                if (NoteDurationTrail)
                    DrawNoteDurationTrail(symbol, x, y, color);

                DrColor(color);
                NoteHeadPolygon(x, y);

                CheckAccidental(symbol, x, y);
                break;

            case MusicalSymbol.Drum:
                if (!Chord.IsNotePlayable(symbol.Note, 0))
                    color = Cfg.NoteColorDim;
                DrColor(color);
                Gl.LineWidth(3.0f);
                Gl.Lines(stackalloc float[] { 5.0f + x, -5.0f + y, -5.0f + x, 5.0f + y, -5.0f + x, -5.0f + y, 5.0f + x, 5.0f + y });
                CheckAccidental(symbol, x, y);
                break;

            case MusicalSymbol.Sharp: // drawn with the current colour
                Gl.LineWidth(2.0f);
                Shape(s_sharp, x, y, s_lines);
                break;

            case MusicalSymbol.Flat:
                Gl.LineWidth(2.0f);
                Shape(s_flat, x, y, s_lineStrip);
                break;

            case MusicalSymbol.Natural:
                Gl.LineWidth(2.0f);
                Shape(s_natural, x, y, s_lines);
                break;

            case MusicalSymbol.BarLine:
                x += Symbol.BeatMarkerOffset * HorizontalSpacingFactor; // the beat markers where entered early so now move them correctly
                Gl.LineWidth(4.0f);
                DrColor((DisplayHand == WhichPart.Left) ? Cfg.StaveColorDim : Cfg.StaveColor);
                OneLine(x, new StavePos(WhichPart.Right, 4).PosYRelative, x, new StavePos(WhichPart.Right, -4).PosYRelative);
                DrColor((DisplayHand == WhichPart.Right) ? Cfg.StaveColorDim : Cfg.StaveColor);
                OneLine(x, new StavePos(WhichPart.Left, 4).PosYRelative, x, new StavePos(WhichPart.Left, -4).PosYRelative);
                break;

            case MusicalSymbol.BarMarker:
                x += Symbol.BeatMarkerOffset * HorizontalSpacingFactor; // the beat markers where entered early so now move them correctly
                Gl.LineWidth(5.0f);
                DrColor(Cfg.BarMarkerColor);
                OneLine(x, new StavePos(WhichPart.Right, BeatMarkerHeight).PosYRelative, x, new StavePos(WhichPart.Left, -BeatMarkerHeight).PosYRelative);
                Gl.LineStipple(false);
                // the bar number above the stave, counting from 1 like in printed music
                if (symbol.BarNumber > 0)
                {
                    DrColor(Cfg.NoteNameColor);
                    RenderText(x, new StavePos(WhichPart.Right, BeatMarkerHeight + 3).PosYRelative,
                               (symbol.BarNumber + Bar.FirstBarNumber).ToString(CultureInfo.InvariantCulture));
                }
                break;

            case MusicalSymbol.BeatMarker:
                x += Symbol.BeatMarkerOffset * HorizontalSpacingFactor; // the beat markers where entered early so now move them correctly
                Gl.LineWidth(4.0f);
                DrColor(Cfg.BeatMarkerColor);
                OneLine(x, new StavePos(WhichPart.Right, BeatMarkerHeight).PosYRelative, x, new StavePos(WhichPart.Left, -BeatMarkerHeight).PosYRelative);
                Gl.LineStipple(false);
                break;

            case MusicalSymbol.PlayingZone:
            {
                float topY = new StavePos(WhichPart.Right, BeatMarkerHeight).PosY;
                float bottomY = new StavePos(WhichPart.Left, -BeatMarkerHeight).PosY;
                float early = (float)Cfg.PlayZoneEarly * HorizontalSpacingFactor;
                float late = (float)Cfg.PlayZoneLate * HorizontalSpacingFactor;
                Gl.Color3f(0.0f, 0.0f, 0.3f);
                Gl.Rect(x - late, topY, x + early, bottomY);
                Gl.LineWidth(2.0f);
                Gl.Color3f(0.0f, 0.0f, 0.8f);
                OneLine(x, topY, x, bottomY);
                Gl.LineWidth(1.0f);
                Gl.Color3f(0.0f, 0.0f, 0.6f);
                OneLine(x - late, topY, x - late, bottomY);
                OneLine(x + early, topY, x + early, bottomY);
                break;
            }

            default:
                if (symbol.Type >= MusicalSymbol.NoteHead)
                    playable = DrawNote(symbol, x, y, color, playable);
                else
                    Log.Debug("ERROR drawSymbol unhandled symbol");
                break;
        }

        if (symbol.Type >= MusicalSymbol.NoteHead)
        {
            if (symbol.PianistTiming != StavePos.NotUsed)
            {
                float pianistX = (float)symbol.PianistTiming;
                pianistX = x + pianistX * HorizontalSpacingFactor;
                Gl.Color3f(1.0f, 1.0f, 1.0f);
                Gl.LineWidth(2.0f);
                Gl.Lines(stackalloc float[]
                {
                    4.0f + pianistX, 4.0f + y, -5.0f + pianistX, -5.0f + y,
                    4.0f + pianistX, -4.0f + y, -5.0f + pianistX, 5.0f + y, // draw pianist note timing markers
                });
            }
            if (playable)
                DrawStaveNoteName(symbol, x, y);
        }
    }

    public void DrawSymbol(Symbol symbol, float x) => DrawSymbol(symbol, x, symbol.StavePos.PosY);

    /// <summary>Draws the symbols of a slot at x = 0 (CDraw::drawSlot).</summary>
    public void DrawSlot(CompiledSlot slot)
    {
        var stavePos = new StavePos();
        for (int i = 0; i < slot.Length; i++)
        {
            Symbol symbol = slot.GetSymbol(i);
            stavePos.NotePos(symbol.Hand, symbol.Note);
            DrawSymbol(symbol, 0.0f, stavePos.PosYRelative); // we add this back when drawing this symbol
        }
    }

    protected void DrawStaves(float startX, float endX)
    {
        Gl.LineWidth(Cfg.StaveThickness);

        // select color for all lines
        Span<float> lines = stackalloc float[4 * 5];
        DrColor((DisplayHand != WhichPart.Left) ? Cfg.StaveColor : Cfg.StaveColorDim);
        int n = 0;
        for (int i = -4; i <= 4; i += 2)
        {
            var pos = new StavePos(WhichPart.Right, i);
            lines[n++] = startX; lines[n++] = pos.PosY;
            lines[n++] = endX; lines[n++] = pos.PosY;
        }
        Gl.Lines(lines);
        // (the C++ code changes the colour inside glBegin, which is allowed for each vertex)
        DrColor((DisplayHand != WhichPart.Right) ? Cfg.StaveColor : Cfg.StaveColorDim);
        n = 0;
        for (int i = -4; i <= 4; i += 2)
        {
            var pos = new StavePos(WhichPart.Left, i);
            lines[n++] = startX; lines[n++] = pos.PosY;
            lines[n++] = endX; lines[n++] = pos.PosY;
        }
        Gl.Lines(lines);
    }

    private static readonly int[] s_sharpLookUpRight = { 4, 1, 5, 2, -1, 3, 0 };
    private static readonly int[] s_sharpLookUpLeft = { 2, -1, 3, 0, -3, 1, -2 };
    private static readonly int[] s_flatLookUpRight = { 0, 3, -1, 2, -2, 1, -3 };
    private static readonly int[] s_flatLookUpLeft = { -2, 1, -3, 0, -4, -1, -5 };

    protected void DrawKeySignature(int key)
    {
        const float gapX = 11.0f;

        if (key == StavePos.NotUsed)
            return;

        int i = 0;
        while (key != 0)
        {
            if (key > 0)
            {
                if (i < s_sharpLookUpRight.Length)
                {
                    DrColor((DisplayHand != WhichPart.Left) ? Cfg.NoteColor : Cfg.NoteColorDim);
                    var pos = new StavePos(WhichPart.Right, s_sharpLookUpRight[i]);
                    DrawSymbol(new Symbol(MusicalSymbol.Sharp, pos), Cfg.KeySignatureX + gapX * (float)i);
                }
                if (i < s_sharpLookUpLeft.Length)
                {
                    DrColor((DisplayHand != WhichPart.Right) ? Cfg.NoteColor : Cfg.NoteColorDim);
                    var pos = new StavePos(WhichPart.Left, s_sharpLookUpLeft[i]);
                    DrawSymbol(new Symbol(MusicalSymbol.Sharp, pos), Cfg.KeySignatureX + gapX * (float)i);
                }
                key--;
            }
            else
            {
                if (i < s_flatLookUpRight.Length)
                {
                    DrColor((DisplayHand != WhichPart.Left) ? Cfg.NoteColor : Cfg.NoteColorDim);
                    var pos = new StavePos(WhichPart.Right, s_flatLookUpRight[i]);
                    DrawSymbol(new Symbol(MusicalSymbol.Flat, pos), Cfg.KeySignatureX + gapX * (float)i);
                }
                if (i < s_flatLookUpLeft.Length)
                {
                    DrColor((DisplayHand != WhichPart.Right) ? Cfg.NoteColor : Cfg.NoteColorDim);
                    var pos = new StavePos(WhichPart.Left, s_flatLookUpLeft[i]);
                    DrawSymbol(new Symbol(MusicalSymbol.Flat, pos), Cfg.KeySignatureX + gapX * (float)i);
                }
                key++;
            }
            i++;
        }
    }

    public virtual void Dispose()
    {
        _font.Dispose();
        GC.SuppressFinalize(this);
    }
}
