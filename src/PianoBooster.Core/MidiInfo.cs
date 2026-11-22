// Ported from MidiInfo.h and MidiInfo.cpp of PianoBooster (C++). GPLv3.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PianoBooster.Core;

/// <summary>What a MIDI file says about itself: title, time and key signature, tempo, duration
/// and number of bars (the C++ CMidiInfo). The side panel shows it; the library keeps it.</summary>
public sealed class MidiInfo
{
    public const int NoKey = 99;

    /// <summary>Translates the texts (QCoreApplication::translate("CMidiInfo", ...)); English until phase 6.</summary>
    public static Func<string, string> Translate = text => text;

    public bool Valid;
    public string Title = "";        // from the first track name, may be empty
    public int TimeSigTop = 4;       // the first time signature
    public int TimeSigBottom = 4;
    public bool TimeSigChanges;
    public int KeySharps = NoKey;    // the first key signature: -7 (7 flats) .. 7 (7 sharps)
    public bool KeyMinor;
    public double TempoBpm = 120.0;  // the first tempo, in quarter notes per minute
    public bool TempoChanges;
    public long DurationMs;
    public int Bars;

    private readonly record struct TempoChange(long Tick, long MicrosecondsPerQuarter);
    private readonly record struct TimeSigChange(long Tick, int Top, int Bottom);

    /// <summary>Reads the information from a MIDI file; <see cref="Valid"/> is false if it can't be read.</summary>
    public static MidiInfo Read(string fileName)
    {
        var info = new MidiInfo();

        byte[] data;
        try
        {
            data = File.ReadAllBytes(fileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return info;
        }

        var header = new MidiByteReader(data, 0, data.Length);
        if (!header.Bytes(4).AsSpan().SequenceEqual("MThd"u8))
            return info;
        uint headerLength = header.Word32();
        header.Word16(); // format
        int trackCount = header.Word16();
        int division = header.Word16();
        if (!header.Ok || headerLength < 6 || (division & 0x8000) != 0 || division == 0)
            return info; // SMPTE time division is not supported
        header.Skip((long)headerLength - 6);

        var tempos = new List<TempoChange>();
        var timeSigs = new List<TimeSigChange>();
        long keyTick = -1;
        long lastTick = 0;

        long pos = header.Pos;
        for (int track = 0; track < trackCount && pos + 8 <= data.Length; ++track)
        {
            var chunk = new MidiByteReader(data, pos, data.Length);
            byte[] chunkId = chunk.Bytes(4);
            long chunkLength = chunk.Word32();
            long trackStart = chunk.Pos;
            long trackEnd = Math.Min(trackStart + chunkLength, data.Length);
            pos = trackEnd;
            if (!chunkId.AsSpan().SequenceEqual("MTrk"u8))
                continue; // skip unknown chunks

            var reader = new MidiByteReader(data, trackStart, trackEnd);
            long tick = 0;
            int runningStatus = 0;
            while (!reader.AtEnd && reader.Ok)
            {
                tick += reader.VarLength();
                int status = reader.Byte();
                if (status < 0x80) // running status: this byte is already data
                {
                    if (runningStatus == 0)
                        break;
                    status = runningStatus;
                    reader.Back();
                }

                if (status == 0xff) // meta event
                {
                    int type = reader.Byte();
                    long length = reader.VarLength();
                    byte[] meta = reader.Bytes(length);
                    if (type == 0x2f) // end of track
                        break;
                    if (type == 0x51 && meta.Length >= 3)
                        tempos.Add(new TempoChange(tick, (meta[0] << 16) | (meta[1] << 8) | meta[2]));
                    else if (type == 0x58 && meta.Length >= 2)
                        timeSigs.Add(new TimeSigChange(tick, meta[0], 1 << Math.Min((int)meta[1], 6)));
                    else if (type == 0x59 && meta.Length >= 2 && (keyTick < 0 || tick < keyTick))
                    {
                        keyTick = tick;
                        info.KeySharps = (sbyte)meta[0];
                        info.KeyMinor = meta[1] != 0;
                    }
                    else if (type == 0x03 && track == 0 && info.Title.Length == 0)
                        info.Title = Encoding.Latin1.GetString(meta).Trim(); // QString::fromLatin1(meta).trimmed()
                }
                else if (status == 0xf0 || status == 0xf7) // system exclusive
                {
                    reader.Skip(reader.VarLength());
                }
                else
                {
                    runningStatus = status;
                    int type = status & 0xf0;
                    reader.Skip((type == 0xc0 || type == 0xd0) ? 1 : 2);
                    lastTick = Math.Max(lastTick, tick);
                }
            }
        }

        if (info.KeySharps < -7 || info.KeySharps > 7)
            info.KeySharps = NoKey;

        // Duration, using the tempo changes (OrderBy is a stable sort, like std::stable_sort)
        tempos = tempos.OrderBy(t => t.Tick).ToList();
        double microseconds = 0.0;
        long segmentTick = 0;
        long microsecondsPerQuarter = 500000; // 120 BPM until the first tempo event
        foreach (TempoChange change in tempos)
        {
            if (change.Tick >= lastTick)
                break;
            microseconds += (double)(change.Tick - segmentTick) * (double)microsecondsPerQuarter / division;
            segmentTick = change.Tick;
            if (change.MicrosecondsPerQuarter > 0)
                microsecondsPerQuarter = change.MicrosecondsPerQuarter;
        }
        microseconds += (double)(lastTick - segmentTick) * (double)microsecondsPerQuarter / division;
        info.DurationMs = (long)(microseconds / 1000.0);

        if (tempos.Count > 0 && tempos[0].MicrosecondsPerQuarter > 0)
            info.TempoBpm = 60000000.0 / (double)tempos[0].MicrosecondsPerQuarter;
        info.TempoChanges = tempos.Count(t => t.MicrosecondsPerQuarter != tempos[0].MicrosecondsPerQuarter && t.Tick < lastTick) > 0;

        // Number of bars, using the time signature changes (each one starts a new bar)
        timeSigs = timeSigs.OrderBy(t => t.Tick).ToList();
        if (timeSigs.Count > 0 && timeSigs[0].Top > 0)
        {
            info.TimeSigTop = timeSigs[0].Top;
            info.TimeSigBottom = timeSigs[0].Bottom;
        }
        int bars = 0;
        long sigTick = 0;
        int top = 4, bottom = 4;
        int BarsUntil(long endTick, bool roundUp)
        {
            long barTicks = (long)division * 4 * top / bottom;
            if (barTicks <= 0 || endTick <= sigTick)
                return 0;
            long ticks = endTick - sigTick;
            return (int)(roundUp ? (ticks + barTicks - 1) / barTicks : (ticks + barTicks / 2) / barTicks);
        }
        foreach (TimeSigChange change in timeSigs)
        {
            if (change.Top <= 0)
                continue;
            if (change.Tick >= lastTick)
                break;
            if (change.Tick > sigTick)
            {
                bars += BarsUntil(change.Tick, false);
                if (change.Top != top || change.Bottom != bottom)
                    info.TimeSigChanges = true;
            }
            sigTick = change.Tick;
            top = change.Top;
            bottom = change.Bottom;
        }
        // the last note may end in the middle of a bar, which still counts as a bar
        bars += BarsUntil(Math.Max(lastTick, sigTick + 1), true);
        info.Bars = bars;

        info.Valid = true;
        return info;
    }

    public string TimeSigText()
    {
        string text = $"{TimeSigTop}/{TimeSigBottom}";
        if (TimeSigChanges)
            text += " " + Translate("(changes)");
        return text;
    }

    private static readonly string[] s_majorKeys = { "Cb", "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#" };
    private static readonly string[] s_minorKeys = { "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "C#", "G#", "D#", "A#" };
    private static readonly string[] s_letters = { "C", "D", "E", "F", "G", "A", "B" };

    public string KeyText()
    {
        if (KeySharps == NoKey)
            return "";
        // index 0 is 7 flats, 14 is 7 sharps
        string key = KeyMinor ? s_minorKeys[KeySharps + 7] : s_majorKeys[KeySharps + 7];

        // translate the note letter and show the accidental as a symbol
        int letterIndex = "CDEFGAB".IndexOf(key[0]);
        string name = Translate(s_letters[letterIndex]);
        if (key.Length > 1)
            name += key[1] == '#' ? "♯" : "♭";

        return KeyMinor ? Translate("%1 minor").Replace("%1", name) : Translate("%1 major").Replace("%1", name);
    }

    public string TempoText()
    {
        // qRound rounds halves away from zero
        string text = Translate("%1 BPM").Replace("%1", ((int)Math.Round(TempoBpm, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));
        if (TempoChanges)
            text += " " + Translate("(changes)");
        return text;
    }

    public string DurationText()
    {
        long seconds = (DurationMs + 500) / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
