// Ported from Song.h and Song.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

/// <summary>The song being practised: reads the MIDI file and feeds the conductor and the score.</summary>
public sealed class Song : Conductor
{
    public const int PcKeyLowestNote = 58;
    public const int PcKeyHighestNote = 75;

    private readonly MidiFile _midiFile;
    private readonly FindChord _findChord = new();
    private bool _reachedMidiEof;
    private Chord _fakeChord = new(); // the chord played with the tab key
    private readonly TrackList _trackList;
    private string _songTitle = "";

    public Song()
    {
        StavePos.SetKeySignature(StavePos.NotUsed, 0);
        _midiFile = new MidiFile();
        _trackList = new TrackList();

        Reset();
    }

    public new void Reset()
    {
        _reachedMidiEof = false;
        _findChord.Reset();
    }

    public new void Init2(Score scoreWin, IPianoSettings settings)
    {
        Note.Reset();

        base.Init2(scoreWin, settings);

        SetActiveHand(WhichPart.Both);
        SetPlayMode(PlayMode.FollowYou);
        SetSpeed(1.0f);
        SetSkill(3);
    }

    /// <summary>The error of the last MIDI file loaded (the C++ code shows a message box).</summary>
    public MidiError MidiError => _midiFile.MidiError;

    public void LoadSong(string fileName)
    {
        Note.Reset();

        _songTitle = fileName;
        int index = _songTitle.LastIndexOf('/');
        if (index >= 0)
            _songTitle = _songTitle.Substring(index + 1);

        _midiFile.OpenMidiFile(fileName);
        Log.Info($"Opening song {fileName}");
        Transpose(0);
        MidiFileInfo();
        PlayMusic(false);
        Rewind();
        SetPlayFromBar(0.0);
        SetLoopingBars(0.0);
        SetEventBits(EventBits.LoadSong);
        if (_midiFile.SongTitle.Length > 0)
            _songTitle = _midiFile.SongTitle;
    }

    // read the file ahead to collect info about the song first
    private void MidiFileInfo()
    {
        _trackList.Reset(_midiFile.NumberOfTracks);
        SetTimeSig(0, 0);
        ClearTimeSigMap();
        StavePos.SetKeySignature(StavePos.NotUsed, 0);

        // Read the next events to find the active channels and the time signatures
        long songTick = 0;
        while (true)
        {
            MidiEvent midiEvent = _midiFile.ReadMidiEvent();
            _trackList.ExamineMidiEvent(midiEvent);
            songTick += midiEvent.DeltaTime;

            if (midiEvent.Type == Midi.PbTimeSignature)
            {
                SetTimeSig(midiEvent.Data1, midiEvent.Data2);
                AddTimeSigChange(songTick, midiEvent.Data1, midiEvent.Data2);
            }

            if (midiEvent.Type == Midi.PbEof)
                break;
        }
    }

    public new void Rewind()
    {
        _midiFile.Rewind();
        base.Rewind();
        _scoreWin!.Reset();
        Reset();
        ForceScoreRedraw();
    }

    public void PlayFromStartBar()
    {
        Rewind();
        PlayMusic(true);
    }

    public new void SetActiveHand(WhichPart hand)
    {
        if (hand < WhichPart.Both)
            hand = WhichPart.Both;
        if (hand > WhichPart.Left)
            hand = WhichPart.Left;

        base.SetActiveHand(hand);
        RegenerateChordQueue();

        _scoreWin!.SetDisplayHand(hand);
    }

    public WhichPart ActiveHand => Note.ActiveHand;

    public new void SetActiveChannel(int chan)
    {
        base.SetActiveChannel(chan);
        _scoreWin!.SetActiveChannel(chan);
        RegenerateChordQueue();
    }

    public new void SetPlayMode(PlayMode mode)
    {
        RegenerateChordQueue();
        base.SetPlayMode(mode);
        ForceScoreRedraw();
    }

    public TrackList TrackList => _trackList;

    public void RegenerateChordQueue()
    {
        _wantedChordQueue.Clear();
        _findChord.Reset();

        int length = _songEventQueue.Length;

        for (int i = 0; i < length; i++)
        {
            MidiEvent midiEvent = _songEventQueue.Index(i);
            // Find the next chord
            if (_findChord.Find(midiEvent, ActiveChannel, WhichPart.Both))
                ChordEventInsert(_findChord.GetChord()); // give the Conductor the chord event
        }
        ResetWantedChord();
    }

    public void RefreshScroll()
    {
        _scoreWin!.RefreshScroll();
        ForceScoreRedraw();
    }

    public string SongTitle => _songTitle;

    /// <summary>Moves the song on by a number of milliseconds; returns the event bits.</summary>
    public uint Task(long ticks)
    {
        RealTimeEngine(ticks);

        while (true)
        {
            if (_reachedMidiEof)
                break;

            while (true)
            {
                // Check that there is space
                if (MidiEventSpace() <= 10 || ChordEventSpace() <= 10)
                    break;

                // and that the Score has space also
                if (_scoreWin!.MidiEventSpace <= 100)
                    break;

                // Read the next events
                MidiEvent midiEvent = _midiFile.ReadMidiEvent();

                // Find the next chord
                if (_findChord.Find(midiEvent, ActiveChannel, WhichPart.Both))
                    ChordEventInsert(_findChord.GetChord()); // give the Conductor the chord event

                // send the events to the other end
                _scoreWin.MidiEventInsert(midiEvent);

                // send the events to the other end
                MidiEventInsert(midiEvent);

                if (midiEvent.Type == Midi.PbEof)
                {
                    _reachedMidiEof = true;
                    break;
                }
            }

            // carry on with the data until we reach the bar we want
            if (SeekingBarNumber() && !_reachedMidiEof && PlayingMusic)
            {
                RealTimeEngine(0);
                _scoreWin!.DrawScrollingSymbols(false); // don't display any thing just remove from the queue
            }
            else
                break;
        }

        uint eventBits = _realTimeEventBits;
        _realTimeEventBits = 0;
        return eventBits;
    }

    private static readonly (int Key, int Note)[] s_pcNoteLookup =
    {
        ('a', PcKeyLowestNote),
        ('z', 59), // B
        ('x', 60), // Middle C
        ('d', 61),
        ('c', 62), // D
        ('f', 63),
        ('v', 64), // E
        ('b', 65), // F
        ('h', 66),
        ('n', 67), // G
        ('j', 68),
        ('m', 69), // A
        ('k', 70),
        (',', 71), // B
        ('.', 72), // C
        (';', 73),
        ('/', 74), // D
        ('\'', PcKeyHighestNote),
    };

    /// <summary>Fakes a midi piano keyboard using the PC keyboard.</summary>
    public bool PcKeyPress(int key, bool down)
    {
        var midi = new MidiEvent();
        const int cfgPcKeyVolume = 64;
        const int cfgPcKeyChannel = 1 - 1;

        if (key == 't') // the tab key on the PC fakes good notes
        {
            if (down)
                _fakeChord = WantedChord;
            for (int i = 0; i < _fakeChord.Length; i++)
            {
                if (down)
                    midi.NoteOnEvent(0, cfgPcKeyChannel, _fakeChord.GetNote(i).Pitch + Transposition, cfgPcKeyVolume);
                else
                    midi.NoteOffEvent(0, cfgPcKeyChannel, _fakeChord.GetNote(i).Pitch + Transposition, cfgPcKeyVolume);
                ExpandPianistInput(midi);
            }
            return true;
        }

        foreach (var (pcKey, note) in s_pcNoteLookup)
        {
            if (key == pcKey)
            {
                if (down)
                    midi.NoteOnEvent(0, cfgPcKeyChannel, note, cfgPcKeyVolume);
                else
                    midi.NoteOffEvent(0, cfgPcKeyChannel, note, cfgPcKeyVolume);

                ExpandPianistInput(midi);
                return true;
            }
        }
        return false;
    }
}
