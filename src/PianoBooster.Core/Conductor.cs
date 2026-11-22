// Ported from Conductor.h and Conductor.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;

namespace PianoBooster.Core;

public enum FollowState
{
    Searching,
    EarlyNotes,
    Waiting,
}

public enum PlayMode
{
    Listen,
    RhythmTapping,
    FollowYou,
    PlayAlong,
}

public enum RhythmTapping
{
    DrumsOnly,
    MellodyOnly,
    DrumsAndMellody,
}

public enum StopPointMode
{
    Automatic,
    OnTheBeat,
    AfterTheBeat,
}

/// <summary>
/// Plays the music and follows the pianist. In C++ CConductor is a CMidiDevice; here the device
/// is a property (<see cref="MidiDevice"/>), and without one nothing is played.
/// </summary>
public class Conductor
{
    private static PlayMode s_playMode = PlayMode.Listen;

    protected Score? _scoreWin;
    protected IPianoSettings? _settings;

    protected readonly CircularQueue<MidiEvent> _songEventQueue;
    protected readonly CircularQueue<Chord> _wantedChordQueue;

    protected uint _realTimeEventBits; // used to signal real time events to the caller of Task()

    private long _playingDeltaTime;
    private long _chordDeltaTime;
    private bool _playing;

    private int _transpose; // the number of semitones to transpose the music
    private FollowState _followState;

    private readonly Rating _rating = new();
    private readonly CircularQueue<MidiEvent> _savedNoteQueue;
    private readonly CircularQueue<MidiEvent> _savedNoteOffQueue;
    private MidiEvent _nextMidiEvent = new();

    private Piano? _piano;

    private readonly Bar _bar = new();
    private long _leadLagAdjust;  // Synchronise the sound with the video
    private long _silenceTimeOut; // used to create silence if the student stops for toooo long
    private Chord _wantedChord = new();      // The chord the pianist needs to play
    private Chord _savedWantedChord = new(); // A copy of the wanted chord complete with both left and right parts
    private Chord _goodPlayedNotes = new();  // The good notes the pianist plays
    private readonly Tempo _tempo = new();
    private bool _keyboardLightsOn;

    private int _pianistSplitPoint; // Defines which notes go in the base and treble clef
    private bool _followSkillAdvanced;
    private int _lastSound;
    private long _stopPoint; // Were we stop the music if the pianist is late
    private int _cfgRightNoteSound;
    private int _cfgWrongNoteSound;
    private int _pianistGoodChan;
    private int _pianistBadChan;
    private long _cfgEarlyNotesPoint;      // don't press the note too early
    private long _cfgStopPointAdvanced;    // Were we stop the music if the pianist is late
    private long _cfgStopPointBeginner;    // Were we stop the music if the pianist is late
    private long _cfgImminentNotesOffPoint;
    private long _cfgPlayZoneEarly;        // when playing along
    private long _cfgPlayZoneLate;

    private int _cfgRhythmTapLeftHandDrumSound;
    private int _cfgRhythmTapRightHandDrumSound;

    private long _pianistTiming;         // measure whether the pianist is playing early or late
    private bool _followPlayingTimeOut;  // O dear, the student is too slow

    private bool _testWrongNoteSound;
    private int _boostVolume;
    private int _pianoVolume;
    private int _activeChannel; // The current part that is being displayed (used for boost)
    private readonly int[] _savedMainVolume = new int[Midi.MaxMidiChannels];
    private int _skill;
    private bool _mutePianistPart;
    private int _latencyFix; // Try to fix the latency (put the time in msec, 0 disables it)
    private readonly int[] _track2ChannelLookUp = new int[Midi.MaxMidiTracks];

    public bool CfgTimingMarkersFlag;
    public StopPointMode CfgStopPointMode;
    public RhythmTapping CfgRhythmTapping;

    public Conductor()
    {
        _scoreWin = null;
        _settings = null;
        _piano = null;

        _songEventQueue = new CircularQueue<MidiEvent>(1000);
        _wantedChordQueue = new CircularQueue<Chord>(1000);
        _savedNoteQueue = new CircularQueue<MidiEvent>(200);
        _savedNoteOffQueue = new CircularQueue<MidiEvent>(200);
        _playing = false;
        _transpose = 0;
        _latencyFix = 0;
        _leadLagAdjust = 0;
        SetSpeed(1.0f);
        SetLatencyFix(0);
        _boostVolume = 0;
        _pianoVolume = 0;
        _activeChannel = 0;
        _skill = 0;
        _silenceTimeOut = 0;
        _realTimeEventBits = 0;
        _mutePianistPart = false;
        SetPianistChannels(1 - 1, 2 - 1);
        CfgTimingMarkersFlag = false;
        CfgStopPointMode = StopPointMode.Automatic;
        CfgRhythmTapping = RhythmTapping.DrumsOnly;
        _cfgRhythmTapRightHandDrumSound = 40;
        _cfgRhythmTapLeftHandDrumSound = 41;

        SetPianoSoundPatches(1 - 1, 7 - 1);
        _tempo.SetSavedWantedChord(true);

        Reset();
        Rewind();
        TestWrongNoteSound(false);
    }

    internal static void ResetStatics() => s_playMode = PlayMode.Listen;

    //////////////////////////////////////////////////////////////////////////
    // The MIDI device (CMidiDevice in C++)

    /// <summary>Where the music is played and the notes of the pianist come from.</summary>
    public IMidiDevice? MidiDevice { get; set; }

    public void PlayMidiEvent(in MidiEvent midiEvent) => MidiDevice?.PlayMidiEvent(midiEvent);
    protected int CheckMidiInput() => MidiDevice?.CheckMidiInput() ?? 0;
    protected MidiEvent ReadMidiInput() => MidiDevice!.ReadMidiInput();
    public bool ValidMidiOutput() => MidiDevice?.ValidMidiOutput() ?? false;
    protected bool OpenMidiPort(MidiType type, string portName) => MidiDevice?.OpenMidiPort(type, portName) ?? false;

    //////////////////////////////////////////////////////////////////////////

    /// <summary>Resets the conductor between each song.</summary>
    public void Reset()
    {
        for (int i = 0; i < Midi.MaxMidiTracks; i++)
        {
            MapTrack2Channel(i, i);
            if (i >= Midi.MaxMidiChannels)
                MapTrack2Channel(i, -1);
        }
    }

    /// <summary>Adds a midi event to be analysed and played.</summary>
    public void MidiEventInsert(in MidiEvent midiEvent) => _songEventQueue.Push(midiEvent);

    /// <summary>First check if there is space to add a midi event.</summary>
    public int MidiEventSpace() => _songEventQueue.Space;

    /// <summary>Adds a chord to be played by the pianist.</summary>
    public void ChordEventInsert(in Chord chord) => _wantedChordQueue.Push(chord);

    /// <summary>First check if there is space to add a chord event.</summary>
    public int ChordEventSpace() => _wantedChordQueue.Space;

    public bool PlayingMusic => _playing;

    public float GetSpeed() => _tempo.Speed;

    public void SetSpeed(float speed)
    {
        _tempo.SetSpeed(speed);
        _leadLagAdjust = _tempo.MSecToTicks(-LatencyFix);
    }

    public void SetLatencyFix(int latencyFix)
    {
        _latencyFix = latencyFix;
        _leadLagAdjust = _tempo.MSecToTicks(-LatencyFix);
    }

    public int LatencyFix => _latencyFix;

    public int Transposition => _transpose;
    public int Skill => _skill;

    public void SetSkill(int skill)
    {
        _skill = skill;
        if (_skill > 5)
            _skill = 5;
        if (_skill < 0)
            _skill = 0;
    }

    public int BoostVolumeValue => _boostVolume;

    public void BoostVolume(int boostVolume)
    {
        _boostVolume = boostVolume;
        if (_boostVolume < -100) _boostVolume = -100;
        if (_boostVolume > 100) _boostVolume = 100;
        OutputBoostVolume();
    }

    public int PianoVolumeValue => _pianoVolume;

    public void PianoVolume(int pianoVolume)
    {
        _pianoVolume = pianoVolume;
        if (_pianoVolume < -100) _pianoVolume = -100;
        if (_pianoVolume > 100) _pianoVolume = 100;
        OutputBoostVolume();
    }

    public static PlayMode PlayMode => s_playMode;

    public Chord WantedChord => _wantedChord;

    public int ActiveChannel => _activeChannel;

    public void SetPianistChannels(int goodChan, int badChan)
    {
        _pianistGoodChan = goodChan;
        _pianistBadChan = badChan;
    }

    public bool HasPianistKeyboardChannel(int chan) => _pianistGoodChan == chan || _pianistBadChan == chan;

    public bool ShouldMutePianistPart() => s_playMode != PlayMode.Listen && _mutePianistPart;

    public Rating Rating => _rating;

    /// <summary>You MUST clear the time sig to 0 first before setting a new start time sig.</summary>
    public void SetTimeSig(int top, int bottom) => _bar.SetTimeSig(top, bottom);
    public void GetTimeSig(out int top, out int bottom) => _bar.GetTimeSig(out top, out bottom);
    // the time signatures of the whole song, see Bar
    public void ClearTimeSigMap() => _bar.ClearTimeSigMap();
    public void AddTimeSigChange(long tick, int top, int bottom) => _bar.AddTimeSigChange(tick, top, bottom);
    public int BeatsInBar(int bar) => _bar.BeatsInBar(bar);

    /// <summary>-1 means no sound -2 means ignore this parameter.</summary>
    public void SetPianoSoundPatches(int rightSound, int wrongSound, bool update = false)
    {
        _cfgRightNoteSound = rightSound;
        if (wrongSound != -2)
            _cfgWrongNoteSound = wrongSound;
        if (update)
            UpdatePianoSounds();
    }

    /// <summary>Sets event bits (without changing the other bits).</summary>
    public void SetEventBits(uint bits) => _realTimeEventBits |= bits;
    /// <summary>Forces the score to be redrawn.</summary>
    public void ForceScoreRedraw() => SetEventBits(EventBits.ForceFullRedraw);
    public int BarNumber => _bar.BarNumber;

    public double CurrentBarPos => _bar.CurrentBarPos;

    public void SetPlayFromBar(double bar) => _bar.SetPlayFromBar(bar);
    public void SetPlayFrom(BarPosition position) => _bar.SetPlayFrom(position);
    public BarPosition PlayFrom => _bar.PlayFrom;
    public BarPosition PositionFromBarBeat(int bar, double beat, bool roundUp = false) =>
        _bar.PositionFromBarBeat(bar, beat, roundUp);
    public void SetLoop(bool enabled, BarPosition end) => _bar.SetLoop(enabled, end);
    public bool IsLoopEnabled => _bar.IsLoopEnabled;
    public bool IsLoopActive => _bar.IsLoopActive;
    public BarPosition PlayUpto => _bar.PlayUpto;
    public double BeatOfPosition(BarPosition position) => _bar.BeatOfPosition(position);

    public void SetPlayUptoBar(double bar) => _bar.SetPlayUptoBar(bar);
    public double PlayUptoBar => _bar.PlayUptoBar;
    public void SetLoopingBars(double bars) => _bar.SetLoopingBars(bars);
    public double LoopingBars => _bar.LoopingBars;

    public void MapTrack2Channel(int trackNumber, int channelNumber) => _track2ChannelLookUp[trackNumber] = channelNumber;

    protected bool SeekingBarNumber() => _bar.SeekingBarNumber;

    private int Track2Channel(int track) => _track2ChannelLookUp[track];

    // The state that the tests look at
    internal FollowState FollowStateValue => _followState;
    internal long ChordDeltaTime => _chordDeltaTime;
    internal long PlayingDeltaTime => _playingDeltaTime;
    internal long PianistTiming => _pianistTiming;
    internal long StopPoint => _stopPoint;
    internal CircularQueue<MidiEvent> SongEventQueue => _songEventQueue;
    internal CircularQueue<Chord> WantedChordQueue => _wantedChordQueue;

    private FollowState GetFollowState()
    {
        if (s_playMode == PlayMode.Listen)
            return FollowState.Searching;
        return _followState;
    }

    private void ChannelSoundOff(int channel)
    {
        if (channel < 0 || channel >= Midi.MaxMidiChannels)
            return;

        var midi = new MidiEvent();
        midi.ControlChangeEvent(0, channel, Midi.AllNotesOff, 0);
        PlayMidiEvent(midi);
        // remove the sustain pedal as well
        midi.ControlChangeEvent(0, channel, Midi.Sustain, 0);
        PlayMidiEvent(midi);
    }

    private void AllSoundOff()
    {
        for (int channel = 0; channel < Midi.MaxMidiChannels; channel++)
        {
            if (channel != _pianistGoodChan)
                ChannelSoundOff(channel);
        }
        _savedNoteQueue.Clear();
        _savedNoteOffQueue.Clear();
    }

    private void ResetAllChannels()
    {
        var midi = new MidiEvent();
        for (int channel = 0; channel < Midi.MaxMidiChannels; channel++)
        {
            midi.ControlChangeEvent(0, channel, Midi.ResetAllControllers, 0);
            PlayMidiEvent(midi);
        }
    }

    // calculate the new solo_volume
    private int CalcBoostVolume(int channel, int volume)
    {
        if (volume == -1)
            volume = _savedMainVolume[channel];
        else
            _savedMainVolume[channel] = volume;

        int returnVolume = volume;
        bool activePart = false;
        if (Note.HasPianoPart(_activeChannel))
        {
            if (s_playMode == PlayMode.Listen) // only boost one hand in listen mode
            {
                if (channel == Note.LeftHandChan && Note.ActiveHand != WhichPart.Right)
                    activePart = true;
                if (channel == Note.RightHandChan && Note.ActiveHand != WhichPart.Left)
                    activePart = true;
            }
            else // otherwise always boost both hands
            {
                if (Note.HasPianoPart(channel))
                    activePart = true;
            }
        }
        else
        {
            if (channel == _activeChannel)
                activePart = true;
        }
        if (channel == _activeChannel)
            activePart = true;

        if (activePart)
        {
            if (returnVolume == 0)
            {
                // Don't adjust the volume if zero
            }
            else if (_boostVolume < 0)
                returnVolume = (returnVolume * (_boostVolume + 100)) / 100;
            else
                returnVolume += _boostVolume;
        }
        else
        {
            if (_boostVolume > 0)
                returnVolume = (returnVolume * (100 - _boostVolume)) / 100;
        }
        // The piano volume can reduce the volume of the music
        if (_pianoVolume > 0)
            returnVolume = (returnVolume * (100 - _pianoVolume)) / 100;

        if (returnVolume > 127)
            returnVolume = 127;
        return returnVolume;
    }

    // send boost volume by adjusting all channels
    private void OutputBoostVolume()
    {
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            if (HasPianistKeyboardChannel(chan))
                continue;
            var midi = new MidiEvent();
            midi.ControlChangeEvent(0, chan, Midi.MainVolume, CalcBoostVolume(chan, -1));
            PlayTrackEvent(midi);
        }
        OutputPianoVolume();
    }

    public void Transpose(int transpose)
    {
        if (_transpose != transpose)
        {
            AllSoundOff();
            _transpose = transpose;
            if (_transpose > 12)
                _transpose = 12;
            if (_transpose < -12)
                _transpose = -12;
            _scoreWin!.Transpose(_transpose);
        }
    }

    public void MutePianistPart(bool state) => _mutePianistPart = state;

    public void SetActiveHand(WhichPart hand)
    {
        if (Note.ActiveHand == hand)
            return;
        Note.ActiveHand = hand;
        OutputBoostVolume();
        ResetWantedChord();

        FindSplitPoint();
        ForceScoreRedraw();
    }

    public void SetPlayMode(PlayMode mode)
    {
        s_playMode = mode;
        if (s_playMode == PlayMode.Listen)
            ResetWantedChord();
        OutputBoostVolume();
        _piano!.SetRhythmTapping(s_playMode == PlayMode.RhythmTapping);
    }

    public void SetActiveChannel(int channel)
    {
        _activeChannel = channel;
        OutputBoostVolume();
        ResetWantedChord();
        FetchNextChord();
    }

    private void OutputPianoVolume()
    {
        var midiEvent = new MidiEvent();
        int volume = 127;
        // if piano volume is between -100 and 0 reduce the volume accordingly
        if (_pianoVolume < 0)
            volume = (volume * (100 + _pianoVolume)) / 100;

        midiEvent.ControlChangeEvent(0, _pianistGoodChan, Midi.MainVolume, volume);
        PlayTrackEvent(midiEvent); // Play the midi note or event
        midiEvent.ControlChangeEvent(0, _pianistBadChan, Midi.MainVolume, volume);
        PlayTrackEvent(midiEvent); // Play the midi note or event
    }

    private void UpdatePianoSounds()
    {
        var midiEvent = new MidiEvent();

        if (_cfgRightNoteSound >= 0) // ignore if set to -1 ("None")
        {
            midiEvent.ProgramChangeEvent(0, _pianistGoodChan, _cfgRightNoteSound);
            PlayTrackEvent(midiEvent);
        }
        if (_cfgWrongNoteSound >= 0)
        {
            midiEvent.ProgramChangeEvent(0, _pianistBadChan, _cfgWrongNoteSound);
            PlayTrackEvent(midiEvent);
        }
    }

    public void TestWrongNoteSound(bool enable)
    {
        _testWrongNoteSound = enable;
        UpdatePianoSounds();
    }

    /// <summary>The name of the "None" MIDI input (translated by the application).</summary>
    public static Func<string, string> Translate = text => text;

    public void ReconnectMidi()
    {
        if (!ValidMidiOutput())
        {
            string midiInputName = _settings!.GetString("Midi/Input");
            if (midiInputName.StartsWith(Translate("None"), StringComparison.Ordinal))
                Chord.SetPianoRange(Song.PcKeyLowestNote, Song.PcKeyHighestNote);
            else
                Chord.SetPianoRange(_settings.GetInt("Keyboard/LowestNote", 0),
                                    _settings.GetInt("Keyboard/HighestNote", 127));
            OpenMidiPort(MidiType.Input, midiInputName);
            OpenMidiPort(MidiType.Output, _settings.GetString("Midi/Output"));
        }
        _settings!.UpdateWarningMessages();
    }

    public void PlayMusic(bool start)
    {
        ReconnectMidi();
        _playing = start;
        KeepDisplayOn.SetRequired(start); // don't let the display turn off while a song is playing
        AllSoundOff();
        if (start)
        {
            ResetAllChannels();

            TestWrongNoteSound(false);
            OutputBoostVolume();
            if (SeekingBarNumber())
                ResetWantedChord();
        }
    }

    // This will allow us to map midi tracks onto midi channels
    // tacks will eventually allow for more than the 16 midi channels (eg with two mid devices)
    // At the moment tracks are only used to remap midi events from channel 0 when using the keyboard lights.
    private void PlayTrackEvent(MidiEvent midiEvent)
    {
        int track = midiEvent.Channel;
        int chan = Track2Channel(track);
        if (chan == -1)
            return;
        midiEvent.Channel = chan;
        PlayMidiEvent(midiEvent);
    }

    private void PlayTransposeEvent(MidiEvent midiEvent)
    {
        if (_transpose != 0 && midiEvent.Channel != Midi.DrumChannel &&
                (midiEvent.Type == Midi.NoteOn || midiEvent.Type == Midi.NoteOff))
            midiEvent.Transpose(_transpose);

        if (midiEvent.Type == Midi.NoteOn && Chord.IsPianistNote(midiEvent, _transpose, ActiveChannel) && ShouldMutePianistPart())
            return; // mute the note by not playing it

        // boost any volume events
        if (midiEvent.Type == Midi.ControlChange && midiEvent.Data1 == Midi.MainVolume)
            midiEvent.Data2 = CalcBoostVolume(midiEvent.Channel, midiEvent.Data2);

        // Don't output note on if we are seeking to bar
        if (!SeekingBarNumber())
            PlayTrackEvent(midiEvent); // Play the midi note or event
        else
        {
            if (midiEvent.Type == Midi.ProgramChange || midiEvent.Type == Midi.ControlChange)
                PlayTrackEvent(midiEvent); // Play the midi note or event
        }
    }

    protected void OutputSavedNotes()
    {
        // The saved notes off are note needed any more
        // (as the are also in the savedNoteQueue
        _savedNoteOffQueue.Clear();

        // output any the saved up notes
        while (_savedNoteQueue.Length > 0)
            PlayTransposeEvent(_savedNoteQueue.Pop());
    }

    protected void ResetWantedChord()
    {
        _wantedChord.Clear();

        _followPlayingTimeOut = false;
        _chordDeltaTime = _playingDeltaTime;
        _pianistTiming = _chordDeltaTime;
        _pianistSplitPoint = Midi.MiddleC;

        OutputSavedNotes();
        _followState = FollowState.Searching;
    }

    // switch modes if we are playing well enough (i.e. don't slow down if we are playing late)
    private void SetFollowSkillAdvanced(bool enable)
    {
        if (_settings == null || _scoreWin == null)
            return;

        _settings.SetAdvancedMode(enable);

        if (LatencyFix > 0)
        {
            // behave differently if we are using the latency fix
            _cfgEarlyNotesPoint = _cfgImminentNotesOffPoint; //disable the early notes
        }

        if (CfgStopPointMode == StopPointMode.OnTheBeat)
            enable = false;
        if (CfgStopPointMode == StopPointMode.AfterTheBeat)
            enable = true;
        if (s_playMode == PlayMode.RhythmTapping)
            enable = true;

        _followSkillAdvanced = enable;
        _stopPoint = enable ? _cfgStopPointAdvanced : _cfgStopPointBeginner;
    }

    private void FindSplitPoint()
    {
        // find the split point
        int lowestTreble = Midi.MiddleC + 37;
        int highestBase = Midi.MiddleC - 37;

        // Find where to put the split point
        for (int i = 0; i < _wantedChord.Length; i++)
        {
            Note note = _wantedChord.GetNote(i);
            if (note.Part == WhichPart.Right && note.Pitch < lowestTreble)
                lowestTreble = note.Pitch;
            if (note.Part == WhichPart.Left && note.Pitch > highestBase)
                highestBase = note.Pitch;
        }

        //put the split point in the middle
        _pianistSplitPoint = ((lowestTreble + highestBase) / 2) + _transpose;
    }

    private void TurnOnKeyboardLights(bool on)
    {
        // exit if not enable
        if (Cfg.KeyboardLightsChan == -1)
            return;

        // exit if keyboard light are already on
        if (_keyboardLightsOn && on)
            return;

        _keyboardLightsOn = on;
        var midiEvent = new MidiEvent();
        for (int i = 0; i < _wantedChord.Length; i++)
        {
            int note = _wantedChord.GetNote(i).Pitch;
            if (on)
                midiEvent.NoteOnEvent(0, Cfg.KeyboardLightsChan, note, 1);
            else
                midiEvent.NoteOffEvent(0, Cfg.KeyboardLightsChan, note, 1);
            PlayMidiEvent(midiEvent); // don't use the track settings
        }
    }

    private void FetchNextChord()
    {
        _followState = FollowState.Searching;
        _followPlayingTimeOut = false;

        OutputSavedNotes();
        TurnOnKeyboardLights(false);

        do // Remove notes or chords that are out of our range
        {
            if (_wantedChordQueue.Length == 0)
            {
                _wantedChord.Clear();
                _pianistSplitPoint = Midi.MiddleC;
                return;
            }

            _wantedChord = _wantedChordQueue.Pop();
            _savedWantedChord = _wantedChord;
            _chordDeltaTime -= _wantedChord.DeltaTime * Speed.AdjustFactor;
            _pianistTiming = _chordDeltaTime;
        }
        while (_wantedChord.TrimOutOfRangeNotes(_transpose) == 0);

        // now find the split point
        FindSplitPoint();
    }

    private bool ValidatePianistNote(in MidiEvent inputNote)
    {
        if (_chordDeltaTime <= -_cfgPlayZoneEarly)
            return false;

        return _wantedChord.SearchChord(inputNote.Note, _transpose);
    }

    private bool ValidatePianistChord()
    {
        if (_piano!.PianistBadNotesDown() >= 2)
            return false;

        if (_skill >= 3)
        {
            if (_goodPlayedNotes.Length == _wantedChord.Length)
                return true;
        }
        else
        {
            if (_goodPlayedNotes.Length >= 1)
                return true;
        }
        return false;
    }

    /// <summary>Adds in the extra notes in rhythm practice.</summary>
    public void ExpandPianistInput(MidiEvent inputNote)
    {
        if (s_playMode == PlayMode.RhythmTapping)
        {
            MidiEvent newNote = inputNote;
            if (inputNote.Type == Midi.NoteOn || inputNote.Type == Midi.NoteOff)
            {
                Chord chord = _wantedChord;
                var chordForOneHand = new Chord();
                int notesFound = 0;

                if (inputNote.Type == Midi.NoteOff)
                {
                    chord = _piano!.RemoveSavedChord(inputNote.Note);
                    for (int i = 0; i < chord.Length; i++)
                    {
                        inputNote.Note = chord.GetNote(i).Pitch;
                        PianistInput(inputNote);
                    }

                    // We have already played and removed this chord
                    if (chord.Length > 0)
                        return;
                }

                WhichPart targetPart = (inputNote.Note >= Midi.MiddleC) ? WhichPart.Right : WhichPart.Left;
                for (int i = 0; i < chord.Length; i++)
                {
                    if (chord.GetNote(i).Part == targetPart && PlayingMusic)
                    {
                        newNote.Note = chord.GetNote(i).Pitch;
                        if (notesFound >= 1 && CfgRhythmTapping == RhythmTapping.DrumsOnly)
                            newNote.Velocity = -1;
                        else
                            chordForOneHand.AddNote(targetPart, chord.GetNote(i).Pitch);
                        PianistInput(newNote);
                        notesFound++;
                    }
                }
                if (notesFound > 0)
                    _piano!.AddSavedChord(inputNote, chordForOneHand);
                else
                {
                    inputNote.Channel = Midi.DrumChannel;
                    PianistInput(inputNote);
                }
            }
            else
            {
                PianistInput(inputNote);
            }
        }
        else
        {
            PianistInput(inputNote);
        }
    }

    // The same in the two places of PianistInput() where a good note is played
    private void PlayGoodNote(WhichPart hand, in MidiEvent inputNote)
    {
        _goodPlayedNotes.AddNote(hand, inputNote.Note);
        _piano!.AddPianistNote(hand, inputNote, true);
        long pianistTiming;
        if ((CfgTimingMarkersFlag && _followSkillAdvanced) || s_playMode == PlayMode.RhythmTapping)
            pianistTiming = _pianistTiming;
        else
            pianistTiming = StavePos.NotUsed;
        _scoreWin!.SetPlayedNoteColor(inputNote.Note,
                    (!_followPlayingTimeOut) ? Cfg.PlayedGoodColor : Cfg.PlayedBadColor,
                    _chordDeltaTime, pianistTiming);

        if (ValidatePianistChord())
        {
            if (_chordDeltaTime < 0)
                _tempo.RemovePlayingTicks(-_chordDeltaTime);

            _goodPlayedNotes.Clear();
            FetchNextChord();
            // count the good notes so that the live percentage looks OK
            _rating.TotalNotes(_wantedChord.Length);
            _rating.CalculateAccuracy();
            if (_rating.IsAccuracyGood || s_playMode == PlayMode.PlayAlong)
                SetFollowSkillAdvanced(true); // change the skill level only when they are good enough
            else
                SetFollowSkillAdvanced(false);
            SetEventBits(EventBits.ForceRatingRedraw);
        }
    }

    public void PianistInput(MidiEvent inputNote)
    {
        bool goodSound = true;

        if (_testWrongNoteSound)
            goodSound = false;

        WhichPart hand = (inputNote.Note >= _pianistSplitPoint) ? WhichPart.Right : WhichPart.Left;

        // for rhythm tapping
        if (inputNote.Channel == Midi.DrumChannel)
            hand = (inputNote.Note >= Midi.MiddleC) ? WhichPart.Right : WhichPart.Left;

        if (inputNote.Type == Midi.NoteOn)
        {
            if (ValidatePianistNote(inputNote))
            {
                PlayGoodNote(hand, inputNote);
            }
            else
            {
                if (_playing)
                {
                    goodSound = false;

                    _piano!.AddPianistNote(hand, inputNote, false);
                    _rating.WrongNotes(1);

                    if (_settings!.FollowThroughErrors && s_playMode == PlayMode.FollowYou) // If the setting is checked, errors cause following too
                    {
                        if (_chordDeltaTime <= -_cfgPlayZoneEarly) // We're hitting bad notes, but earlier than the zone (so ignore them)
                        {
                            _piano.Clear();
                            _savedNoteQueue.Clear();
                            _savedNoteOffQueue.Clear();
                        }
                        else // Hitting bad notes within the zone (so register them)
                        {
                            // Register the wrong note in the ratings calculation
                            MissedNotesColor(Cfg.PianoBadColor);
                            _rating.LateNotes(_wantedChord.Length - _goodPlayedNotes.Length);
                            SetEventBits(EventBits.ForceRatingRedraw);
                            FetchNextChord(); // Skip through the wrong note and continue to the next

                            // Was the next note the one keyed by accident (dyslexia)?  If so, validate it instead and continue as usual.
                            if (_wantedChord.SearchChord(inputNote.Note, _transpose)) // replaces ValidatePianistNote ignoring out-of-zone
                                PlayGoodNote(hand, inputNote);

                            // Clear & ignore any further slips until in range of the next note's zone
                            _piano.Clear();
                            _savedNoteQueue.Clear();
                            _savedNoteOffQueue.Clear();
                        }
                    }
                }
                else
                    _piano!.AddPianistNote(hand, inputNote, true);
            }
        }
        else if (inputNote.Type == Midi.NoteOff)
        {
            if (_piano!.RemovePianistNote(inputNote.Note))
                goodSound = false;
            bool hasNote = _goodPlayedNotes.RemoveNote(inputNote.Note);

            if (hasNote)
                _scoreWin!.SetPlayedNoteColor(inputNote.Note,
                        (!_followPlayingTimeOut) ? Cfg.NoteColor : Cfg.PlayedStoppedColor,
                        _chordDeltaTime);

            OutputSavedNotesOff();
        }

        if (inputNote.Velocity == -1)
            return;

        if (goodSound || _cfgWrongNoteSound < 0)
        {
            if (_cfgRightNoteSound >= 0) // don't play anything if the sound is set to -1 (none)
            {
                bool playDrumBeat = false;
                if (inputNote.Channel != Midi.DrumChannel)
                {
                    if (CfgRhythmTapping != RhythmTapping.DrumsOnly || s_playMode != PlayMode.RhythmTapping)
                    {
                        inputNote.Channel = _pianistGoodChan;
                        PlayTrackEvent(inputNote);
                    }
                }
                else
                {
                    playDrumBeat = true;
                }

                if (CfgRhythmTapping != RhythmTapping.MellodyOnly && s_playMode == PlayMode.RhythmTapping)
                    playDrumBeat = true;

                if (playDrumBeat)
                {
                    inputNote.Channel = Midi.DrumChannel;
                    inputNote.Note = (hand == WhichPart.Right) ? _cfgRhythmTapRightHandDrumSound : _cfgRhythmTapLeftHandDrumSound;
                    PlayTrackEvent(inputNote);
                }
            }
        }
        else
        {
            inputNote.Channel = _pianistBadChan;
            if (s_playMode == PlayMode.RhythmTapping)
            {
                inputNote.Channel = Midi.DrumChannel;
                inputNote.Note = (hand == WhichPart.Right) ? _cfgRhythmTapRightHandDrumSound : _cfgRhythmTapLeftHandDrumSound;
            }
            PlayTrackEvent(inputNote);
        }
    }

    private void AddDeltaTime(long ticks)
    {
        _scoreWin!.ScrollDeltaTime(ticks);
        _playingDeltaTime += ticks;
        _chordDeltaTime += ticks;
    }

    private void FollowPlaying()
    {
        if (s_playMode == PlayMode.Listen)
            return;

        if (_wantedChord.Length == 0)
            FetchNextChord();

        if (_wantedChord.Length == 0)
            return;

        if (SeekingBarNumber())
        {
            if (Speed.DeltaAdjustL(_chordDeltaTime) > -_stopPoint)
                FetchNextChord();
        }
        else if (s_playMode == PlayMode.FollowYou || s_playMode == PlayMode.RhythmTapping)
        {
            if (Speed.DeltaAdjustL(_chordDeltaTime) > -_cfgEarlyNotesPoint)
                _followState = FollowState.EarlyNotes;
            if (Speed.DeltaAdjustL(_chordDeltaTime) > -_stopPoint)
            {
                _followState = FollowState.Waiting;
                // Throw away the time past the stop point (by adding a negative ticks)
                long pastStopPoint = -_stopPoint * Speed.AdjustFactor - _chordDeltaTime;
                AddDeltaTime(pastStopPoint);
                _bar.AdjustDeltaTime(pastStopPoint); // keep the bar position in step
            }
        }
        else // PlayMode.PlayAlong
        {
            if (_chordDeltaTime > _cfgPlayZoneLate)
            {
                MissedNotesColor(Cfg.PlayedStoppedColor);
                FetchNextChord();
                _rating.LateNotes(_wantedChord.Length - _goodPlayedNotes.Length);
                SetEventBits(EventBits.ForceRatingRedraw);
            }
        }
        if (Speed.DeltaAdjustL(_chordDeltaTime) > -_cfgEarlyNotesPoint)
            TurnOnKeyboardLights(true);
    }

    /// <summary>The exact position of the notes the pianist is being waited for (or the current
    /// position if not waiting), to be used as the start position.</summary>
    public BarPosition CurrentNotePosition
    {
        get
        {
            BarPosition position = _bar.CurrentPosition;
            // When waiting for the pianist the time has passed the notes by _chordDeltaTime
            // (the stop point), so go back to exactly where the notes are.
            if (_followState == FollowState.Waiting && _wantedChord.Length > 0 && _chordDeltaTime > 0)
                position = _bar.OffsetPosition(position, -_chordDeltaTime);
            return position;
        }
    }

    private void OutputSavedNotesOff()
    {
        while (_savedNoteOffQueue.Length > 0)
            PlayTransposeEvent(_savedNoteOffQueue.Pop()); // Output the saved note off events
    }

    // untangle the sound in case there is any notes off just after we have stopped
    private void FindImminentNotesOff()
    {
        MidiEvent midiEvent = _nextMidiEvent;
        int i = 0;
        long aheadDelta = 0;

        while (Speed.DeltaAdjustL(_playingDeltaTime) + aheadDelta > _cfgImminentNotesOffPoint)
        {
            if (midiEvent.Type == Midi.NoteOff)
                _savedNoteOffQueue.Push(midiEvent);
            if (i >= _songEventQueue.Length)
                break;
            midiEvent = _songEventQueue.Index(i);
            aheadDelta -= midiEvent.DeltaTime;
            i++;
        }
    }

    private void MissedNotesColor(PbColor color)
    {
        for (int i = 0; i < _wantedChord.Length; i++)
        {
            Note note = _wantedChord.GetNote(i);
            if (!_goodPlayedNotes.SearchChord(note.Pitch, _transpose))
                _scoreWin!.SetPlayedNoteColor(note.Pitch + _transpose, color, _chordDeltaTime);
        }
    }

    public void RealTimeEngine(long mSecTicks)
    {
        long ticks = _tempo.MSecToTicks(mSecTicks);
        if (!_followPlayingTimeOut)
            _pianistTiming += ticks;

        while (CheckMidiInput() > 0)
        {
            MidiEvent inputEvent = ReadMidiInput();
            // the OS does not count the MIDI keyboard as user activity, so tell it
            if (inputEvent.Type == Midi.NoteOn)
                KeepDisplayOn.UserActivity();
            ExpandPianistInput(inputEvent);
        }

        if (GetFollowState() == FollowState.Waiting)
        {
            if (_silenceTimeOut > 0)
            {
                _silenceTimeOut -= mSecTicks;
                if (_silenceTimeOut <= 0)
                {
                    AllSoundOff();
                    _silenceTimeOut = 0;
                }
            }

            _tempo.InsertPlayingTicks(ticks);

            if (_pianistTiming > _cfgPlayZoneLate)
            {
                if (!_followPlayingTimeOut)
                {
                    _followPlayingTimeOut = true;

                    _tempo.ClearPlayingTicks();
                    _rating.LateNotes(_wantedChord.Length - _goodPlayedNotes.Length);
                    SetEventBits(EventBits.ForceRatingRedraw);

                    MissedNotesColor(Cfg.PlayedStoppedColor);
                    FindImminentNotesOff();
                    // Don't keep any saved notes off if there are no notes down
                    if (_piano!.PianistAllNotesDown() == 0)
                        OutputSavedNotesOff();
                    _silenceTimeOut = Cfg.SilenceTimeOut;
                }
            }
            return;
        }

        _silenceTimeOut = 0;
        if (!_playing)
            return;

        if (SeekingBarNumber())
            ticks = 0;

        _tempo.AdjustTempo(ref ticks);

        ticks = _bar.AddDeltaTime(ticks);

        if (SeekingBarNumber())
            ticks = _bar.GoToBarNumber();

        SetEventBits(_bar.ReadEventBits());

        AddDeltaTime(ticks);

        FollowPlaying();
        while (_playingDeltaTime >= _leadLagAdjust)
        {
            int type = _nextMidiEvent.Type;

            if (_songEventQueue.Length == 0 && type == Midi.PbEof)
            {
                Log.Info("The End of the song");
                SetEventBits(EventBits.PlayingStopped);
                _playing = false;
                KeepDisplayOn.SetRequired(false);
                break;
            }

            if (type == Midi.PbTempo)
            {
                _tempo.SetMidiTempo(_nextMidiEvent.Data1);
                _leadLagAdjust = _tempo.MSecToTicks(-LatencyFix);
            }
            else if (type == Midi.PbTimeSignature)
            {
                _bar.SetTimeSig(_nextMidiEvent.Data1, _nextMidiEvent.Data2);
            }
            else if (type != Midi.None) // this marks the end of the piece of music
            {
                int channel = _nextMidiEvent.Channel;

                // Is this channel_muted
                if (!HasPianistKeyboardChannel(channel))
                {
                    if (GetFollowState() >= FollowState.EarlyNotes &&
                            (s_playMode == PlayMode.FollowYou || s_playMode == PlayMode.RhythmTapping) &&
                            !SeekingBarNumber() &&
                            !_followSkillAdvanced)
                    {
                        // Save up the notes until the pianist presses the right key
                        if (_savedNoteQueue.Space > 0)
                            _savedNoteQueue.Push(_nextMidiEvent);
                        else
                            Log.Warn("Warning the m_savedNoteQueue is full");

                        if (type == Midi.NoteOff)
                        {
                            if (_savedNoteOffQueue.Space > 0)
                                _savedNoteOffQueue.Push(_nextMidiEvent);
                            else
                                Log.Debug("Warning the m_savedNoteOffQueue is full");
                        }
                    }
                    else
                    {
                        PlayTransposeEvent(_nextMidiEvent); // Play the midi note or event
                    }
                }
            }
            if (_songEventQueue.Length > 0)
                _nextMidiEvent = _songEventQueue.Pop();
            else
            {
                _nextMidiEvent.Clear();
                break;
            }

            _playingDeltaTime -= _nextMidiEvent.DeltaTime * Speed.AdjustFactor;
            FollowPlaying();
        }
    }

    public void Rewind()
    {
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
            _savedMainVolume[chan] = 100;
        _lastSound = -1;
        _rating.Reset();
        _playingDeltaTime = 0;
        _tempo.Reset();

        _songEventQueue.Clear();
        _savedNoteQueue.Clear();
        _savedNoteOffQueue.Clear();
        _wantedChordQueue.Clear();
        _nextMidiEvent.Clear();
        _bar.Rewind();

        _goodPlayedNotes.Clear(); // The good notes the pianist plays
        _piano?.Clear();
        ResetWantedChord();
        SetFollowSkillAdvanced(false);

        _cfgEarlyNotesPoint = MidiFile.PpqnAdjust(15);          // was 10 playZoneEarly
        _cfgStopPointBeginner = MidiFile.PpqnAdjust(-0);        // was -3; // stop just after the beat
        _cfgStopPointAdvanced = MidiFile.PpqnAdjust(-15);       // was -3; // stop just after the beat
        _cfgImminentNotesOffPoint = MidiFile.PpqnAdjust(-15);   // look ahead and find an Notes off coming up

        _cfgPlayZoneEarly = MidiFile.PpqnAdjust(Cfg.PlayZoneEarly) * Speed.AdjustFactor; // when playing along
        _cfgPlayZoneLate = MidiFile.PpqnAdjust(Cfg.PlayZoneLate) * Speed.AdjustFactor;
    }

    public void Init2(Score scoreWin, IPianoSettings settings)
    {
        _scoreWin = scoreWin;
        _settings = settings;

        SetFollowSkillAdvanced(false);

        _followState = FollowState.Searching;

        _scoreWin.SetRatingObject(_rating);
        _piano = _scoreWin.Piano;

        Rewind();
    }
}
