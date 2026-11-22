// Ported from Tempo.h and Tempo.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

/// <summary>The speed of the music: the tempo of the MIDI file and the speed the user chooses.</summary>
public sealed class Tempo
{
    public const float MicroSecond = 1000000.0f;

    private float _userSpeed; // controls the speed of the piece playing
    private float _midiTempo; // controls the speed of the piece playing
    private long _jumpAheadDelta;
    private static int s_maxJumpAhead;
    private static int s_followTempoAmount;
    // The C++ code keeps a pointer to the wanted chord of the conductor, only to check that it is set
    private bool _hasSavedWantedChord;

    public Tempo()
    {
        _hasSavedWantedChord = false;
        Reset();
    }

    internal void SetSavedWantedChord(bool hasSavedWantedChord) => _hasSavedWantedChord = hasSavedWantedChord;

    public void Reset()
    {
        // 120 beats per minute is the default
        SetMidiTempo((int)((60 * MicroSecond) / 120));
        _jumpAheadDelta = 0;
    }

    /// <summary>Tempo, microseconds-per-MIDI-quarter-note.</summary>
    public void SetMidiTempo(int tempo)
    {
        _midiTempo = ((float)tempo * MidiFile.DefaultPpqn) / (float)MidiFile.PulsesPerQuarterNote;
    }

    public void SetSpeed(float speed)
    {
        // limit the allowed speed
        if (speed > 2.0f)
            speed = 2.0f;
        if (speed < 0.1f)
            speed = 0.1f;
        _userSpeed = speed;
    }

    public float Speed => _userSpeed;

    public long MSecToTicks(long mSec) =>
        (long)((float)mSec * _userSpeed * (100.0f * MicroSecond) / _midiTempo);

    public void InsertPlayingTicks(long ticks)
    {
        _jumpAheadDelta -= ticks;
        if (_jumpAheadDelta < MidiFile.PpqnAdjust(-10) * PianoBooster.Core.Speed.AdjustFactor)
            _jumpAheadDelta = MidiFile.PpqnAdjust(-10) * PianoBooster.Core.Speed.AdjustFactor;
    }

    public void RemovePlayingTicks(long ticks)
    {
        if (s_maxJumpAhead != 0)
            _jumpAheadDelta = ticks;
    }

    public void ClearPlayingTicks() => _jumpAheadDelta = 0;

    public void AdjustTempo(ref long ticks)
    {
        if (_jumpAheadDelta != 0 && s_maxJumpAhead != 0 && _hasSavedWantedChord)
        {
            if (_jumpAheadDelta > 0)
                ticks += _jumpAheadDelta;

            // Automatically adjust the speed
            _userSpeed = _userSpeed + _userSpeed * (float)_jumpAheadDelta * 0.00002f;

            if (_userSpeed > 2.0f) _userSpeed = 2.0f;
            if (_userSpeed < 0.2f) _userSpeed = 0.2f;

            _jumpAheadDelta = 0;
        }
    }

    public static void EnableFollowTempo(bool enable)
    {
        s_maxJumpAhead = enable ? 1 : 0;
        s_followTempoAmount = enable ? 1 : 0;
    }

    internal static void ResetStatics()
    {
        s_maxJumpAhead = 0;
        s_followTempoAmount = 0;
    }
}
