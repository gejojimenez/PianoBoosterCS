// The C# twin of tools/RefDump/RefDump.cpp: it drives PianoBooster.Core in the same way and
// MUST write exactly the same lines (see tools/RefDump/README.md for the format).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PianoBooster.Core;

namespace PianoBooster.Core.Tests;

/// <summary>A MIDI device that records the events played and gives the notes of the pianist.</summary>
internal sealed class FakeMidiDevice : IMidiDevice
{
    public readonly Queue<MidiEvent> Input = new();
    public Action<MidiEvent>? Output;

    public void PlayMidiEvent(in MidiEvent midiEvent) => Output?.Invoke(midiEvent);
    public int CheckMidiInput() => Input.Count;
    public MidiEvent ReadMidiInput() => Input.Dequeue();
    public bool ValidMidiOutput() => true;
    public bool OpenMidiPort(MidiType type, string portName) => true;
}

internal sealed class RefDriver
{
    private readonly StringBuilder _out = new();
    private readonly Dictionary<string, string> _params;

    private MemorySettings _settings = null!;
    private Song _song = null!;
    private Score _score = null!;
    private FakeMidiDevice _device = null!;
    private int _step;

    private RefDriver(Dictionary<string, string> parameters)
    {
        _params = parameters;
    }

    /// <summary>Runs a mode of the dump on a MIDI file and returns the lines written.</summary>
    public static string Run(string mode, string midiFile, Dictionary<string, string> parameters)
    {
        CultureInfo savedCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // numbers written like printf()
        try
        {
            var driver = new RefDriver(parameters);
            driver.RunMode(mode, midiFile);
            return driver._out.ToString();
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
        }
    }

    private void Line(string text) => _out.Append(text).Append('\n');

    private static string F(float value) => BitConverter.SingleToUInt32Bits(value).ToString("x8");
    private static string D(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("x16");
    private static string ColorStr(PbColor c) => F(c.Red) + "," + F(c.Green) + "," + F(c.Blue);

    private string Param(string key, string def) => _params.TryGetValue(key, out string? value) ? value : def;
    private int ParamInt(string key, int def) => int.Parse(Param(key, def.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    private double ParamDouble(string key, double def) =>
        _params.TryGetValue(key, out string? value) ? double.Parse(value, CultureInfo.InvariantCulture) : def;

    private void RunMode(string mode, string file)
    {
        CoreStatics.Reset();
        if (mode == "events")
            DumpEvents(file);
        else if (mode == "songmap")
            DumpSongMap(file);
        else
        {
            SetupEnv();
            LoadSong(file);
            if (mode == "info")
                DumpInfo();
            else if (mode == "notation")
                DumpNotation(file);
            else if (mode == "play")
                DumpPlay();
            else
                throw new ArgumentException("Unknown mode " + mode);
        }
    }

    //////////////////////////////////////////////////////////////////////////
    // events

    private void DumpEvents(string file)
    {
        var midiFile = new MidiFile();
        midiFile.OpenMidiFile(file);
        Line($"H {midiFile.NumberOfTracks} {MidiFile.PulsesPerQuarterNote} {(int)midiFile.MidiError} {midiFile.SongTitle}");
        if (midiFile.MidiError != MidiError.NoError)
            return;
        while (true)
        {
            MidiEvent e = midiFile.ReadMidiEvent();
            bool eof = e.Type == Midi.PbEof;
            Line($"E {e.Type} {e.DeltaTime} {e.Channel} {e.Note} {e.Velocity} {e.Duration} {(eof ? -1 : e.Track)}");
            if (eof)
                break;
        }
    }

    //////////////////////////////////////////////////////////////////////////
    // songmap

    private void DumpSongMap(string file)
    {
        var map = new SongMap();
        bool ok = map.Load(file);
        Line($"M {(ok ? 1 : 0)} {map.Ppqn} {map.LastTick} {map.MaxNoteLength} {map.LowestNote} {map.HighestNote} {map.BarCount()}");
        if (!ok)
            return;
        foreach (SongNote note in map.Notes)
            Line($"N {note.Tick} {note.Length} {note.Note} {note.Channel} {note.Track} {note.Velocity}");
        foreach (SongEvent e in map.Events)
            Line($"V {e.Tick} {e.Status} {e.Data1} {e.Data2}");
        for (int bar = 0; bar <= map.BarCount() + 1; bar++)
        {
            long start = map.BarStartTick(bar);
            Line($"B {bar} {start} {map.BeatsInBar(bar)} {map.TimeSigBottom(bar)} {map.BeatTicks(bar)} {D(map.MsOfTick(start))} " +
                 $"{map.BarOfTick(start + 1)} {map.TickOfMs(map.MsOfTick(start) + 0.5)}");
            BarPosition position = map.PositionOfTick(start + map.BeatTicks(bar) + 3);
            Line($"P {position.Bar} {position.Beat} {position.Ticks} {map.TickOfPosition(position)} {map.TickOfBarBeat(bar, 1.5)} " +
                 $"{D(map.BeatOfTick(start + 5))}");
        }
    }

    //////////////////////////////////////////////////////////////////////////
    // The objects of the application, set up like QtWindow, CGLView and CSettings do

    private void SetupEnv()
    {
        Cfg.SetDefaults();
        Cfg.LogLevel = 0;

        _settings = new MemorySettings
        {
            FollowThroughErrors = ParamInt("followErrors", 0) != 0,
            IsNoteDurationTrailEnabled = ParamInt("trail", 0) != 0,
        };

        // CGLView's constructor
        _song = new Song();
        _device = new FakeMidiDevice();
        _song.MidiDevice = _device; // the C++ CSong is the MIDI device
        _score = new Score(_settings);
        // QtWindow's constructor
        _song.Init2(_score, _settings);
        _song.TrackList.Init(_song, _settings);
        // CGLView::initializeGL()
        Cfg.SetStaveEndX(400);
        _song.SetActiveHand(WhichPart.Both);
        _score.Init();
        _song.RegenerateChordQueue();
        // CGLView::resizeGL() for a window of 1024 x 700
        StavePos.StaveCenterY = 296.0f;
        Cfg.SetAppDimensions(0, 43, 1024, 652);
        Cfg.SetStaveEndX(1004.0f);
        StavePos.StaveCentralOffset = 84.0f;
        DrawState.ForceCompileRedraw();
    }

    // CSettings::setCurrentSongName()
    private void LoadSong(string file)
    {
        _song.LoadSong(file);
        Note.SetChannelHands(-2, -2); // CSettings::loadSongSettings() without saved settings
        TrackList trackList = _song.TrackList;
        trackList.Refresh();                                      // GuiSidePanel::refresh()
        trackList.CurrentRowChanged(trackList.GetActiveItemIndex()); // its list selects the item
    }

    //////////////////////////////////////////////////////////////////////////
    // info

    private void DumpInfo()
    {
        Line($"T {_song.SongTitle}");
        Line($"K {StavePos.KeySignature}");
        Line($"L {Note.LeftHandChan} {Note.RightHandChan}");
        Line($"A {_song.ActiveChannel}");
        var sb = new StringBuilder("C");
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
            if (_song.HasPianistKeyboardChannel(chan))
                sb.Append(' ').Append(chan);
        Line(sb.ToString());
        sb = new StringBuilder("RT");
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
            sb.Append(' ').Append(Note.RightHandTrack(chan));
        Line(sb.ToString());
        foreach (string name in _song.TrackList.GetAllChannelProgramNames())
            Line($"PN {name}");
        foreach (int chan in _song.TrackList.GetAllChannels())
            Line($"CH {chan}");
        _song.GetTimeSig(out int top, out int bottom);
        Line($"TS {top} {bottom}");
        for (int bar = 0; bar < 40; bar++)
            Line($"BB {bar} {_song.BeatsInBar(bar)}");
        Line($"Q {_song.WantedChordQueue.Length} {_song.SongEventQueue.Length}");
        for (int i = 0; i < _song.WantedChordQueue.Length; i++)
        {
            Chord chord = _song.WantedChordQueue.Index(i);
            sb = new StringBuilder($"WQ {chord.DeltaTime} {chord.Length}");
            for (int n = 0; n < chord.Length; n++)
                sb.Append($" {chord.GetNote(n).Pitch}:{(int)chord.GetNote(n).Part}");
            Line(sb.ToString());
        }
    }

    //////////////////////////////////////////////////////////////////////////
    // notation

    private void DumpSlot(in Slot slot)
    {
        Line($"S {slot.DeltaTime} {slot.LeftSideDeltaTime} {slot.Av8Left} {slot.Length}");
        for (int i = 0; i < slot.Length; i++)
        {
            Symbol symbol = slot.GetSymbol(i);
            StavePos stavePos = symbol.StavePos;
            Line($"Y {(int)symbol.Type} {(int)symbol.Hand} {symbol.Note} {stavePos.StaveIndex} {stavePos.Accidental} " +
                 $"{ColorStr(symbol.Color)} {symbol.MidiDuration} {(int)symbol.AccidentalModifier} {symbol.BarNumber} " +
                 $"{symbol.NoteIndex} {symbol.NoteTotal} {symbol.PianistTiming} {F(stavePos.PosY)}");
        }
    }

    private void DumpNotation(string file)
    {
        var notation = new Notation();
        notation.SetChannel(_song.ActiveChannel);
        notation.Reset();
        var midiFile = new MidiFile();
        midiFile.OpenMidiFile(file);
        bool eof = false;
        for (int count = 0; count < 100000; count++)
        {
            while (!eof && notation.MidiEventSpace > 100)
            {
                MidiEvent e = midiFile.ReadMidiEvent();
                notation.MidiEventInsert(e);
                if (e.Type == Midi.PbEof)
                    eof = true;
            }
            Slot slot = notation.NextSlot();
            DumpSlot(slot);
            if (slot.Length == 0 || slot.GetSymbolType(0) == MusicalSymbol.TheEndMarker)
                break;
        }
    }

    //////////////////////////////////////////////////////////////////////////
    // play

    private static string ChordStr(in Chord chord)
    {
        var sb = new StringBuilder(chord.Length.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < chord.Length; i++)
            sb.Append(' ').Append(chord.GetNote(i).Pitch).Append(':').Append((int)chord.GetNote(i).Part);
        return sb.ToString();
    }

    private void PianistNote(bool on, int note)
    {
        var e = new MidiEvent();
        if (on)
            e.NoteOnEvent(0, 0, note, 64);
        else
            e.NoteOffEvent(0, 0, note, 64);
        _device.Input.Enqueue(e);
    }

    private void DumpPlay()
    {
        Song song = _song;
        Score score = _score;

        string mode = Param("mode", "follow");
        PlayMode playMode = PlayMode.FollowYou;
        if (mode == "listen") playMode = PlayMode.Listen;
        else if (mode == "along") playMode = PlayMode.PlayAlong;
        else if (mode == "rhythm") playMode = PlayMode.RhythmTapping;
        string hand = Param("hand", "both");
        WhichPart part = WhichPart.Both;
        if (hand == "right") part = WhichPart.Right;
        else if (hand == "left") part = WhichPart.Left;

        int steps = ParamInt("steps", 6000);
        int tick = ParamInt("tick", 4);
        int transpose = ParamInt("transpose", 0);
        // The pianist plays the wanted chord when it is this far from its time (in ticks of
        // 96 ppqn, negative for early), using the values in turn
        int[] offsets = { -10, 0, 5, -20, 2, -3, 8 };
        int wrongEvery = ParamInt("wrongEvery", 7); // a wrong note before every Nth chord (0 for never)
        int holdSteps = ParamInt("hold", 30);
        const int giveUpSteps = 400; // plays anyway after waiting this long

        _device.Output = e => Line($"O {_step} {e.Type} {e.Channel} {e.Note} {e.Velocity}");
        song.SetPlayMode(playMode);
        song.SetActiveHand(part);
        song.SetSkill(ParamInt("skill", 3));
        song.SetSpeed((float)ParamDouble("speed", 1.0));
        song.Transpose(transpose);
        song.MutePianistPart(ParamInt("mute", 1) != 0);
        song.BoostVolume(ParamInt("boost", 0));
        song.PianoVolume(ParamInt("piano", 0));
        song.SetLatencyFix(ParamInt("latency", 0));
        song.CfgTimingMarkersFlag = ParamInt("markers", 0) != 0;
        double from = ParamDouble("from", 0.0);
        double upto = ParamDouble("upto", 0.0);
        song.SetPlayFromBar(from);
        song.SetPlayUptoBar(upto);
        Line("START");
        song.PlayFromStartBar();

        string lastWanted = "0";
        int chordCounter = 0;
        int chordSince = 0;
        int attempts = 0;
        int nextTryStep = 0;
        int releaseAt = -1;
        var pressed = new List<int>();
        int lastBar = -1;
        string lastRating = "";
        string lastPiano = "";

        for (_step = 0; _step < steps; _step++)
        {
            uint bits = song.Task(tick);
            if (bits != 0)
                Line($"B {_step} {bits}");
            if ((bits & EventBits.UptoBarReached) != 0)
                song.PlayFromStartBar();
            if (_step % 4 == 3)
                score.DrawScrollingSymbols(true);

            if (song.BarNumber != lastBar)
            {
                lastBar = song.BarNumber;
                Line($"N {_step} {lastBar} {D(song.CurrentBarPos)}");
            }

            Rating rating = song.Rating;
            string ratingStr = $"{rating.TotalNoteCount} {rating.WrongNoteCount} {rating.LateNoteCount} " +
                               $"{F(rating.AccuracyValue)} {(rating.IsAccuracyGood ? 1 : 0)}";
            if (lastRating != ratingStr)
            {
                lastRating = ratingStr;
                Line($"R {_step} {ratingStr}");
            }

            Piano piano = score.Piano;
            string pianoStr = $"{piano.PianistAllNotesDown()} {piano.PianistBadNotesDown()} {piano.NoteNameListLength}";
            if (lastPiano != pianoStr)
            {
                lastPiano = pianoStr;
                Line($"PI {_step} {pianoStr}");
            }

            Chord wanted = song.WantedChord;
            string wantedStr = ChordStr(wanted);
            if (wantedStr != lastWanted)
            {
                lastWanted = wantedStr;
                Line($"W {_step} {wantedStr}");
                if (wanted.Length > 0)
                {
                    chordCounter++;
                    chordSince = _step;
                    attempts = 0;
                    nextTryStep = _step;
                }
            }

            if (_step == releaseAt)
            {
                foreach (int note in pressed)
                    PianistNote(false, note);
                pressed.Clear();
                releaseAt = -1;
            }
            if (wanted.Length > 0 && pressed.Count == 0 && _step >= nextTryStep)
            {
                long threshold = (long)offsets[(chordCounter + attempts) % 7] *
                                 MidiFile.PulsesPerQuarterNote / MidiFile.DefaultPpqn * Speed.AdjustFactor;
                if (song.ChordDeltaTime >= threshold || _step - chordSince >= giveUpSteps)
                {
                    if (wrongEvery > 0 && chordCounter % wrongEvery == 3 && attempts == 0)
                    {
                        int wrong = wanted.GetNote(0).Pitch + transpose + 1;
                        PianistNote(true, wrong);
                        pressed.Add(wrong);
                    }
                    for (int i = 0; i < wanted.Length; i++)
                    {
                        int note = wanted.GetNote(i).Pitch + transpose;
                        PianistNote(true, note);
                        pressed.Add(note);
                    }
                    attempts++;
                    releaseAt = _step + holdSteps;
                    nextTryStep = _step + holdSteps + 20;
                }
            }

            if (_step % 50 == 0)
            {
                Scroll? scroll = score.ActiveScroll;
                var sb = new StringBuilder($"Z {_step} {(int)song.FollowStateValue} {song.ChordDeltaTime} {song.PlayingDeltaTime} " +
                                           $"{song.SongEventQueue.Length} {song.WantedChordQueue.Length} {song.PianistTiming} {song.StopPoint}");
                if (scroll != null)
                    sb.Append($" {scroll.ScrollQueue.Length} {scroll.DeltaHead} {scroll.DeltaTail} {scroll.WantedIndex} " +
                              $"{scroll.WantedDelta} {scroll.TrailOnlySlots}");
                sb.Append(' ').Append(F(song.GetSpeed()));
                Line(sb.ToString());
            }
            if (_step % 500 == 0 && score.ActiveScroll != null)
            {
                Scroll scroll = score.ActiveScroll;
                for (int i = 0; i < scroll.ScrollQueue.Length; i++)
                    DumpSlot(scroll.ScrollQueue.Index(i).Slot);
            }

            if ((bits & EventBits.PlayingStopped) != 0)
            {
                Line($"STOP {_step}");
                break;
            }
        }
        _device.Output = null;
        Line($"END {song.Rating.TotalNoteCount} {song.Rating.WrongNoteCount} {song.Rating.LateNoteCount} {D(song.Rating.Percent)}");
    }
}
