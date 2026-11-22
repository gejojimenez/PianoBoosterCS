// Ported from TrackList.h and TrackList.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;
using System.Collections.Generic;

namespace PianoBooster.Core;

/// <summary>What is found out about the notes of a channel.</summary>
public sealed class AnalyseItem
{
    private int _noteCount;
    private int _firstPatch = -1;
    private readonly List<int[]> _noteFrequencyByTrack = new();
    private readonly List<int> _noteCountByTrack = new();

    public AnalyseItem(int numberOfTracks)
    {
        _noteCount = 0;
        for (int i = 0; i < numberOfTracks; i++)
        {
            _noteFrequencyByTrack.Add(new int[Midi.MaxMidiNotes]);
            _noteCountByTrack.Add(0);
        }
    }

    public void AddNoteEvent(in MidiEvent midiEvent)
    {
        _noteCount++;
        int trackNo = midiEvent.Track;
        _noteCountByTrack[trackNo]++;
        int[] noteFrequency = _noteFrequencyByTrack[trackNo];
        int note = midiEvent.Note;
        // count each note so we can guess the key signature
        if (note >= 0 && note < Midi.MaxMidiNotes)
            noteFrequency[note]++;
        // If we have a note and no patch then default to grand piano patch
        if (_firstPatch == -1 && midiEvent.Channel != Midi.DrumChannel)
            _firstPatch = Midi.GmPianoPatch;
    }

    public int NoteCount => _noteCount;
    public bool Active => _noteCount > 0;

    public void AddPatch(int patch)
    {
        if (_firstPatch == -1)
            _firstPatch = patch;
    }

    public int FirstPatch => _firstPatch;

    public int TrackCount()
    {
        int activeTracks = 0;
        for (int track = 0; track < _noteCountByTrack.Count; track++)
        {
            if (_noteCountByTrack[track] > 0)
                activeTracks++;
        }
        return activeTracks;
    }

    public int RightHandTrack()
    {
        if (TrackCount() <= 1)
            return -1;
        double highestAveragePitch = 0.0;
        int rightHandTrack = 1;
        for (int track = 0; track < _noteCountByTrack.Count; track++)
        {
            if (_noteCountByTrack[track] > 0)
            {
                double averagePitch = AverageNotePitch(track);
                if (averagePitch > highestAveragePitch)
                {
                    highestAveragePitch = averagePitch;
                    rightHandTrack = track;
                }
            }
        }
        return rightHandTrack;
    }

    public double AverageNotePitch(int trackNo)
    {
        int totalNoteCount = 0;
        double sumOffAllPitches = 0.0;
        int[] noteFrequency = _noteFrequencyByTrack[trackNo];

        for (int note = 0; note < Midi.MaxMidiNotes; note++)
        {
            int frequency = noteFrequency[note];
            totalNoteCount += frequency;
            sumOffAllPitches += frequency * note;
        }
        return sumOffAllPitches / totalNoteCount;
    }
}

/// <summary>The parts (MIDI channels) of the song, and which ones are the piano hands.</summary>
public sealed class TrackList
{
    public const int ConventionLeftHandChannel = 3 - 1;
    public const int ConventionRightHandChannel = 4 - 1;

    private Song? _song;
    private IPianoSettings? _settings;
    private readonly List<int> _partsList = new(); // the MIDI channel of each part
    private readonly List<AnalyseItem> _midiChannels = new();
    private readonly int[,] _noteFrequency = new int[Midi.MaxMidiChannels, Midi.MaxMidiNotes];

    /// <summary>Translates the names of the instruments (QObject::tr() in C++).</summary>
    public static Func<string, string> Translate = text => text;

    public TrackList()
    {
        _song = null;
        _settings = null;
        Reset(0);
    }

    public void Init(Song songObj, IPianoSettings settings)
    {
        _song = songObj;
        _settings = settings;
    }

    public void Reset(int numberOfTracks)
    {
        _partsList.Clear();
        _midiChannels.Clear();
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            for (int i = 0; i < Midi.MaxMidiNotes; i++)
                _noteFrequency[chan, i] = 0;
            _midiChannels.Add(new AnalyseItem(numberOfTracks));
        }
    }

    public void CurrentRowChanged(int currentRow)
    {
        if (_song == null) return;
        if (currentRow >= _partsList.Count || currentRow < 0)
            return;

        _song.SetActiveChannel(_partsList[currentRow]);
    }

    public void ExamineMidiEvent(in MidiEvent midiEvent)
    {
        int chan = midiEvent.Channel;

        if (chan < Midi.MaxMidiChannels && chan >= 0)
        {
            if (midiEvent.Type == Midi.NoteOn)
            {
                _midiChannels[chan].AddNoteEvent(midiEvent);

                // count each note so we can guess the key signature
                if (midiEvent.Note >= 0 && midiEvent.Note < Midi.MaxMidiNotes)
                    _noteFrequency[chan, midiEvent.Note]++;
            }

            if (midiEvent.Type == Midi.ProgramChange)
                _midiChannels[chan].AddPatch(midiEvent.Programme);
        }
    }

    /// <summary>Returns true if there is a piano part on channels 3 &amp; 4.</summary>
    public bool PianoPartConventionTest()
    {
        AnalyseItem left = _midiChannels[ConventionLeftHandChannel];
        AnalyseItem right = _midiChannels[ConventionRightHandChannel];
        // Both hands on channels 3 & 4
        if (left.Active && right.Active)
        {
            if (left.FirstPatch == right.FirstPatch && Midi.IsPianoOrOrganPatch(right.FirstPatch))
            {
                Note.SetChannelHands(ConventionLeftHandChannel, ConventionRightHandChannel);
                return true;
            }
        }
        // Right hand only channel 4 and no left hand on channel 3
        if (right.Active && !left.Active)
        {
            if (Midi.IsPianoOrOrganPatch(right.FirstPatch))
            {
                Note.SetChannelHands(ConventionLeftHandChannel, ConventionRightHandChannel);
                return true;
            }
        }
        // Left hand only channel 3 and no right hand on channel 4
        if (left.Active && !right.Active)
        {
            if (Midi.IsPianoOrOrganPatch(left.FirstPatch))
            {
                Note.SetChannelHands(ConventionLeftHandChannel, ConventionRightHandChannel);
                return true;
            }
        }
        return false;
    }

    public bool FindLeftAndRightPianoParts()
    {
        int patchA = -1;
        int chanA = -1;

        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            if (chan == Midi.DrumChannel)
                continue;
            if (_midiChannels[chan].Active)
            {
                int patch = _midiChannels[chan].FirstPatch;
                if (Midi.IsPianoOrOrganPatch(patch))
                {
                    if (_midiChannels[chan].TrackCount() > 1)
                    {
                        Note.SetChannelHands(chan, chan);
                        return true;
                    }

                    if (patchA == -1)
                    {
                        patchA = patch;
                        chanA = chan;
                    }
                    else
                    {
                        if (patchA == patch)
                        {
                            if (AverageNotePitch(chan) < AverageNotePitch(chanA))
                                Note.SetChannelHands(chan, chanA);
                            else
                                Note.SetChannelHands(chanA, chan);
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    public int GuessKeySignature(int chanA, int chanB)
    {
        int keySignature = 0;
        int highScore = 0;
        var scale = new int[Midi.Octave];
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            if (chanA == -1 || chan == chanA || chan == chanB)
            {
                for (int note = 0; note < Midi.MaxMidiNotes; note++)
                    scale[note % Midi.Octave] += _noteFrequency[chan, note];
            }
        }

        (int Offset, int Key)[] keyLookUp =
        {
            (0, 0),   // 0  C
            (7, 1),   // 1  G  1#
            (5, -1),  // 2  F  1b
            (2, 2),   // 3  D  2#
            (10, -2), // 4  Bb 2b
            (9, 3),   // 5  A  3#
            (3, -3),  // 6  Eb 3b
            (4, 4),   // 7  E  4#
            (8, -4),  // 8  Ab 4b
            (11, 5),  // 9  B  5#
            (1, -5),  // 10 Db 5b
            (6, 6),   // 11 F# 6#
        };

        for (int i = 0; i < Midi.Octave; i++)
        {
            int score = 0;
            int idx = keyLookUp[i].Offset;
            score += scale[(idx + 0) % Midi.Octave];  // First note in the scale
            score += scale[(idx + 2) % Midi.Octave];  // Tone
            score += scale[(idx + 4) % Midi.Octave];  // Tone
            score += scale[(idx + 5) % Midi.Octave];  // Semi tone
            score += scale[(idx + 7) % Midi.Octave];  // Tone
            score += scale[(idx + 9) % Midi.Octave];  // Tone
            score += scale[(idx + 11) % Midi.Octave]; // Tone
                                                      // the Last note don't count it
            if (score > highScore)
            {
                highScore = score;
                keySignature = keyLookUp[i].Key;
            }
        }
        return keySignature;
    }

    /// <summary>Finds an unused channel.</summary>
    public int FindFreeChannel(int startChannel)
    {
        for (int chan = startChannel; chan < Midi.MaxMidiChannels; chan++)
        {
            if (chan == Cfg.KeyboardLightsChan)
                continue;
            if (chan == Midi.DrumChannel)
                continue;
            if (!_midiChannels[chan].Active)
                return chan;
        }
        return -1; // Not found
    }

    public void Refresh()
    {
        Song song = _song!;
        _partsList.Clear();

        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            if (_midiChannels[chan].Active)
                _partsList.Add(chan);
        }

        if (Note.BothHandsChan != -2) // -2 for not set -1 for not used
            song.SetActiveChannel(Note.BothHandsChan);
        else if (PianoPartConventionTest())
            song.SetActiveChannel(Note.BothHandsChan);
        else if (FindLeftAndRightPianoParts())
            song.SetActiveChannel(Note.BothHandsChan);
        else
        {
            if (_partsList.Count > 0)
            {
                // for the case when there is no piano or organ patch
                song.SetActiveChannel(_partsList[0]);
            }
        }

        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            AnalyseItem item = _midiChannels[chan];
            if (item.Active)
            {
                if (item.FirstPatch == Midi.GmPianoPatch)
                {
                    // For those midi files that do not include ANY patch we want to set it piano
                    var midi = new MidiEvent();
                    midi.ProgramChangeEvent(0, chan, Midi.GmPianoPatch);
                    song.PlayMidiEvent(midi);
                }
            }
        }

        if (StavePos.KeySignature == StavePos.NotUsed)
            StavePos.SetKeySignature(GuessKeySignature(Note.RightHandChan, Note.LeftHandChan), 0);

        // Find an unused channel that we can use for the keyboard
        song.Reset();
        int goodChan = FindFreeChannel(0);
        int badChan = FindFreeChannel(goodChan + 1);
        int spareChan = FindFreeChannel(badChan + 1);
        if (badChan == -1)
        {
            // As we have not found two we have not found to empty channels to use
            goodChan = 15 - 1;
            badChan = 16 - 1;
        }
        song.SetPianistChannels(goodChan, badChan);
        Log.Info($"Using Pianist Channels {goodChan + 1} + {badChan + 1}");
        if (Cfg.KeyboardLightsChan != -1 && spareChan != -1)
            song.MapTrack2Channel(Cfg.KeyboardLightsChan, spareChan);
        for (int chan = 0; chan < Midi.MaxMidiChannels; chan++)
        {
            AnalyseItem item = _midiChannels[chan];
            Note.SetRightHandTrack(chan, item.RightHandTrack());
        }
    }

    public int GetActiveItemIndex()
    {
        for (int i = 0; i < _partsList.Count; ++i)
        {
            if (_partsList[i] == Note.RightHandChan)
                return i;
        }
        return 0; // Not found so return first item on the list
    }

    /// <summary>The names of the parts, e.g. "4R Grand Piano" (the programme names start at 1 with 0 = "(None)").</summary>
    public List<string> GetAllChannelProgramNames(bool raw = false)
    {
        var items = new List<string>();
        foreach (int chan in _partsList)
        {
            string hand = "";
            if (!raw)
            {
                if (Note.LeftHandChan == chan)
                    hand += Translate("L");
                if (Note.RightHandChan == chan)
                    hand += Translate("R");
            }
            items.Add((chan + 1) + hand + " " + GetChannelProgramName(chan));
        }
        return items;
    }

    /// <summary>The MIDI channel of each item of the list, in the same order as the names.</summary>
    public List<int> GetAllChannels() => new(_partsList);

    public int GetActiveHandIndex(WhichPart whichPart)
    {
        int index = 0;
        for (int i = 0; i < _partsList.Count; ++i)
            if (_partsList[i] == Note.GetHandChannel(whichPart))
                return index;
        return index;
    }

    /// <summary>Sets the midi channels to use for the left and right hand piano parts.</summary>
    public void SetActiveHandsIndex(int leftIndex, int rightIndex)
    {
        int leftChannel = -1;
        int rightChannel = -1;

        if (leftIndex >= 0)
            leftChannel = _partsList[leftIndex];
        if (rightIndex >= 0)
            rightChannel = _partsList[rightIndex];
        _settings!.SetChannelHands(leftChannel, rightChannel);
        Refresh();
        _song!.Rewind();
    }

    /// <summary>The index in the list of the selected hand, -1 if not found.</summary>
    public int GetHandTrackIndex(WhichPart whichPart)
    {
        int index = 0;
        int midiHand = Note.GetHandChannel(whichPart);
        for (int i = 0; i < _partsList.Count; ++i)
        {
            if (_partsList[i] == midiHand)
                return index;
            index++;
        }
        return -1;
    }

    public double AverageNotePitch(int chan)
    {
        int totalNoteCount = 0;
        double sumOffAllPitches = 0.0;

        for (int note = 0; note < Midi.MaxMidiNotes; note++)
        {
            int frequency = _noteFrequency[chan, note];
            totalNoteCount += frequency;
            sumOffAllPitches += frequency * note;
        }
        return sumOffAllPitches / totalNoteCount;
    }

    private string GetChannelProgramName(int chan)
    {
        if (chan < 0 || chan >= Midi.MaxMidiChannels)
            return "";
        int program = _midiChannels[chan].FirstPatch;

        if (chan == 10 - 1)
            return Translate("Drums");
        string name = GetProgramName(program + 1); // Skip
        if (name.Length == 0)
            name = Translate("Unknown");
        return name;
    }

    private static readonly string[] s_gmInstrumentNames =
    {
        "(None)",  // Don't use
        "Grand Piano", "Bright Piano", "Electric Grand", "Honky-tonk Piano",
        "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavi",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone",
        "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ",
        "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)",
        "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar harmonics",
        "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass",
        "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass",
        "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "SynthStrings 1", "SynthStrings 2",
        "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet",
        "French Horn", "Brass Section", "SynthBrass 1", "SynthBrass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax",
        "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute",
        "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)",
        "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
        "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)",
        "Pad 5 (bowed)", "Pad 6 (metallic)", "Pad 7 (halo)", "Pad 8 (sweep)",
        "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)",
        "FX 5 (brightness)", "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
        "Sitar", "Banjo", "Shamisen", "Koto",
        "Kalimba", "Bag pipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock",
        "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet",
        "Telephone Ring", "Helicopter", "Applause", "Gunshot",
    };

    /// <summary>The General MIDI name of a programme, starting at 1 (0 = "(None)").</summary>
    public static string GetProgramName(int program)
    {
        if (program >= 0 && program < s_gmInstrumentNames.Length)
            return Translate(s_gmInstrumentNames[program]);
        return "";
    }
}
