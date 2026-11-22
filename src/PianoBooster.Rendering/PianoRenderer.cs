// Ported from the drawing parts of Piano.cpp of PianoBooster (C++) (drawPianoInput() and the
// functions it calls). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using PianoBooster.Core;

namespace PianoBooster.Rendering;

/// <summary>Draws the lines and names of the notes the pianist has down (the C++ CPiano drawing).</summary>
public sealed class PianoRenderer : Draw
{
    private const int PianoLineLengthLong = 64;
    private const int PianoLineLengthShort = 48;

    public PianoRenderer(GlCanvas gl) : base(gl)
    {
    }

    private void DrawPianoInputNoteNames(FrameSnapshot frame)
    {
        if (frame.NoteNameListLength > 8) // too many notes so don't name any of them
            return;

        for (int i = 0; i < frame.NoteNameListLength; i++)
        {
            NoteNameItem item = frame.NoteNameList[i];
            DrawNoteName(item.Pitch, Cfg.PlayZoneX - PianoLineLengthShort - 14, item.PosY, item.Type);
        }
    }

    private void DrawPianoInputLines(in Chord chord, PbColor color, int lineLength, bool rhythmTapping)
    {
        Gl.Color(color);

        var stavePos = new StavePos();

        for (int i = 0; i < chord.Length; i++)
        {
            if (!rhythmTapping)
            {
                int pitch = chord.GetNote(i).Pitch;
                stavePos.NotePos(chord.GetNote(i).Part, pitch);

                Gl.LineWidth(3.0f);

                if (stavePos.Accidental != 0)
                {
                    Gl.LineStipple(true); // dashed
                    Gl.LineWidth(3.0f);
                }

                float posY = stavePos.PosYAccidental;
                OneLine(Cfg.PlayZoneX - (float)lineLength, posY, Cfg.PlayZoneX, posY);
                Gl.LineStipple(false);
            }
            else
            {
                // draw a vertical line instead
                WhichPart hand = chord.GetNote(i).Part;
                var top = new StavePos(hand, 6);
                var bottom = new StavePos(hand, -6);
                Gl.LineWidth(3.0f);
                OneLine(Cfg.PlayZoneX, top.PosY, Cfg.PlayZoneX, bottom.PosY);
            }
        }
    }

    public void DrawPianoInput(FrameSnapshot frame)
    {
        bool showNoteName = frame.ShowNoteNames;
        int lineLength = showNoteName ? PianoLineLengthShort : PianoLineLengthLong;

        if (frame.GoodChord.Length > 0)
            DrawPianoInputLines(frame.GoodChord, Cfg.PianoGoodColor, lineLength, frame.RhythmTapping);

        if (frame.BadChord.Length > 0)
            DrawPianoInputLines(frame.BadChord, Cfg.PianoBadColor, lineLength, frame.RhythmTapping);

        if (showNoteName)
            DrawPianoInputNoteNames(frame);
    }
}
