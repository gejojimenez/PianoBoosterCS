// Ported from MidiFile.h, MidiFile.cpp, Merge.h and Merge.cpp of PianoBooster (C++).
// Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;
using System.IO;

namespace PianoBooster.Core;

/// <summary>Merges the events of several streams (the tracks) in order of time.</summary>
public abstract class Merge
{
    private MidiEvent[] _mergeEvents = Array.Empty<MidiEvent>();

    protected void SetSize(int size)
    {
        _mergeEvents = new MidiEvent[size];
        for (int i = 0; i < size; i++)
            _mergeEvents[i] = new MidiEvent();
    }

    protected abstract bool CheckMidiEventFromStream(int streamIdx);
    protected abstract MidiEvent FetchMidiEventFromStream(int streamIdx);

    protected void InitMergedEvents()
    {
        for (int i = 0; i < _mergeEvents.Length; i++)
        {
            _mergeEvents[i].Clear();
            if (CheckMidiEventFromStream(i))
                _mergeEvents[i] = FetchMidiEventFromStream(i);
        }
    }

    private int NextMergedEvent()
    {
        int nearestIndex = 0;
        int nearest = -1;

        // find the first active slot
        for (int i = 0; i < _mergeEvents.Length; i++)
        {
            if (_mergeEvents[i].Type != Midi.None)
            {
                nearest = i;
                nearestIndex = i;
                break;
            }
        }
        if (nearest < 0)
            return 0;

        // now search the remaining active slots
        for (int i = nearestIndex + 1; i < _mergeEvents.Length; i++)
        {
            if (_mergeEvents[i].Type != Midi.None)
            {
                // find the slot with the lowest delta time
                if (_mergeEvents[i].DeltaTime < _mergeEvents[nearest].DeltaTime)
                {
                    nearest = i;
                    nearestIndex = i;
                }
            }
        }

        int deltaTime = -_mergeEvents[nearest].DeltaTime;

        // Now subtract the delta time from all the others
        for (int i = 0; i < _mergeEvents.Length; i++)
        {
            if (i == nearestIndex)
                continue;
            if (_mergeEvents[i].Type != Midi.None)
                _mergeEvents[i].AddDeltaTime(deltaTime);
        }
        return nearestIndex;
    }

    /// <summary>The next event of all the streams; <see cref="Midi.PbEof"/> at the end.</summary>
    public MidiEvent ReadMidiEvent()
    {
        int mergeIdx = NextMergedEvent();
        MidiEvent midiEvent = _mergeEvents[mergeIdx];

        _mergeEvents[mergeIdx].Clear();
        if (CheckMidiEventFromStream(mergeIdx))
            _mergeEvents[mergeIdx] = FetchMidiEventFromStream(mergeIdx);
        if (midiEvent.Type == Midi.None)
            midiEvent.Type = Midi.PbEof;
        return midiEvent;
    }
}

/// <summary>Reads the events of a standard MIDI file (the C++ CMidiFile).</summary>
public sealed class MidiFile : Merge
{
    public const int DefaultPpqn = 96; // Standard value for pulse per quarter note
    public const int MaxTracks = 40;

    private static int s_ppqn = DefaultPpqn;

    private MidiStream? _file;
    private MidiError _midiError;
    private readonly MidiTrack?[] _tracks = new MidiTrack?[MaxTracks];
    private string _songTitle = "";
    private int _numberOfTracks;

    public MidiFile()
    {
        _midiError = MidiError.NoError;
        s_ppqn = DefaultPpqn;
        SetSize(MaxTracks);
        _numberOfTracks = 0;
    }

    /// <summary>The pulses per quarter note of the last file read (a static, as in C++).</summary>
    public static int PulsesPerQuarterNote => s_ppqn;

    public static int PpqnAdjust(float value) =>
        (int)((value * (float)PulsesPerQuarterNote) / DefaultPpqn);

    public string SongTitle => _songTitle;
    public MidiError MidiError => _midiError;
    public int NumberOfTracks => _numberOfTracks;

    internal static void ResetStatics() => s_ppqn = DefaultPpqn;

    /// <summary>
    /// Opens and reads a MIDI file. The C++ code shows a message box if it can't be read;
    /// here <see cref="MidiError"/> tells it (CannotOpenFile or another error).
    /// </summary>
    public void OpenMidiFile(string fileName)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(fileName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or NotSupportedException)
        {
            _file = null;
            _midiError = MidiError.CannotOpenFile;
            return;
        }
        _file = new MidiStream(data);
        Rewind();
    }

    private int ReadWord()
    {
        int value = (_file!.Get() & 0x0ff) << 8;
        value |= _file.Get() & 0x0ff;
        return value;
    }

    private int ReadHeader()
    {
        for (int i = 0; i < 4; i++)
        {
            int c = _file!.Get();
            if (c != "MThd"[i])
            {
                _midiError = MidiError.CorruptedMidiFile;
                return 0;
            }
        }
        // length
        if (ReadWord() != 0)
        {
            _midiError = MidiError.CorruptedMidiFile;
            return 0;
        }
        if (ReadWord() != 6)
        {
            _midiError = MidiError.CorruptedMidiFile;
            return 0;
        }

        ReadWord();             // midi file format
        int tracks = ReadWord(); // ntrks (see Standard MIDI File Spec)
        s_ppqn = ReadWord();     // division

        Log.Info($"Tracks {tracks} PPQN {s_ppqn}");

        if (tracks == 0)
        {
            _midiError = MidiError.CorruptedMidiFile;
            return 0;
        }
        return tracks;
    }

    /// <summary>Reads the file again from its start.</summary>
    public void Rewind()
    {
        _numberOfTracks = 0;
        _midiError = MidiError.NoError;
        s_ppqn = DefaultPpqn;
        if (_file == null)
        {
            _midiError = MidiError.CannotOpenFile;
            return;
        }

        _file.SeekG(0);

        int ntrks = ReadHeader();
        if (ntrks == 0)
        {
            _midiError = MidiError.CorruptedMidiFile;
            Log.Error("Zero tracks in SMF file");
            return;
        }
        if (ntrks < 0 || ntrks > _tracks.Length)
        {
            _midiError = MidiError.TooManyTracks;
            Log.Error("Too many tracks in SMF file");
            return;
        }
        _numberOfTracks = ntrks;
        for (int trk = 0; trk < _tracks.Length; ++trk)
            _tracks[trk] = null;
        long filePos = _file.TellG();
        for (int trk = 0; trk < ntrks; ++trk)
        {
            var track = new MidiTrack(_file, trk);
            _tracks[trk] = track;
            uint trackLength = track.TrackLength;
            track.DecodeTrack();
            if (track.Failed)
            {
                _midiError = track.MidiError;
                break;
            }
            // now move onto the next track
            filePos += trackLength;
            _file.SeekG(filePos);
        }
        _songTitle = _tracks[0]!.TrackName;
        InitMergedEvents();
    }

    protected override bool CheckMidiEventFromStream(int streamIdx)
    {
        if (streamIdx < 0 || streamIdx >= MaxTracks)
            return false;
        MidiTrack? track = _tracks[streamIdx];
        return track != null && track.Length > 0;
    }

    protected override MidiEvent FetchMidiEventFromStream(int streamIdx) => _tracks[streamIdx]!.Pop(streamIdx);
}
