// Ported from Cfg.h, Cfg.cpp and Util.h of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

using System;

namespace PianoBooster.Core;

/// <summary>A colour with float components from 0 to 1 (the C++ CColor).</summary>
public readonly struct PbColor : IEquatable<PbColor>
{
    public readonly float Red;
    public readonly float Green;
    public readonly float Blue;

    public PbColor(double r, double g, double b)
    {
        Red = (float)r;
        Green = (float)g;
        Blue = (float)b;
    }

    public bool Equals(PbColor other) => Red == other.Red && Green == other.Green && Blue == other.Blue;
    public override bool Equals(object? obj) => obj is PbColor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue);
    public static bool operator ==(PbColor a, PbColor b) => a.Equals(b);
    public static bool operator !=(PbColor a, PbColor b) => !a.Equals(b);
    public override string ToString() => $"({Red}, {Green}, {Blue})";
}

/// <summary>All the configuration information (the C++ class Cfg).</summary>
public static class Cfg
{
    public const int LogLevelNone = 0;
    public const int LogLevelInfo = 1;
    public const int LogLevelWarn = 2;
    public const int LogLevelDebug = 3;

    public static float StaveStartX => 20;
    public static float StaveEndX => _staveEndX;
    public static float PlayZoneX => ScrollStartX + (StaveEndX - ScrollStartX) * 0.4f;
    public static float ClefX => StaveStartX + 20;
    public static float TimeSignatureX => ClefX + 25;
    public static float KeySignatureX => TimeSignatureX + 25;
    public static float ScrollStartX => KeySignatureX + 64;
    public static float PianoX => 25;

    public static float StaveThickness => 1;

    public static int PlayZoneEarly => 25;
    public static int PlayZoneLate => 25;
    public static int SilenceTimeOut => 8000; // the time in msec before everything goes quiet
    public static int ChordNoteGap => 10;     // all notes in a cord must be spaced less than this a gap
    public static int ChordMaxLength => 20;   // the max time between the start and end of a cord

    public static PbColor MenuColor => new(0.1, 0.6, 0.6);
    public static PbColor MenuSelectedColor => new(0.7, 0.7, 0.1);

    public static PbColor StaveColor => new(0.1, 0.7, 0.1);          // green
    public static PbColor StaveColorDim => new(0.15, 0.40, 0.15);    // grey
    public static PbColor NoteColor => new(0.1, 0.9, 0.1);           // green
    public static PbColor NoteColorDim => new(0.25, 0.45, 0.25);     // green
    public static PbColor PlayedGoodColor => new(0.5, 0.6, 1.0);     // purple
    public static PbColor PlayedBadColor => new(0.8, 0.3, 0.8);      // orange
    public static PbColor PlayedStoppedColor => new(1.0, 0.8, 0.0);  // bright orange
    public static PbColor BackgroundColor => new(0.0, 0.0, 0.0);     // black
    public static PbColor BarMarkerColor => new(0.3, 0.25, 0.25);    // grey
    public static PbColor BeatMarkerColor => new(0.25, 0.2, 0.2);    // grey
    public static PbColor PianoGoodColor => PlayedGoodColor;
    public static PbColor PianoBadColor => new(1.0, 0.0, 0.0);
    public static PbColor NoteNameColor => new(1.0, 1.0, 1.0);

    public static void SetDefaults()
    {
        // In msec. It was 12 on Windows, but its timer is now precise there too
        TickRate = 4;
    }

    public static void SetStaveEndX(float x) => _staveEndX = x;

    public static int AppX { get; private set; }
    public static int AppY { get; private set; }
    public static int AppWidth { get; private set; }
    public static int AppHeight { get; private set; }

    public static void SetAppDimensions(int x, int y, int width, int height)
    {
        AppX = x;
        AppY = y;
        AppWidth = width;
        AppHeight = height;
    }

    public static int DefaultWrongPatch => 7; // Starts at 1
    public static int DefaultRightPatch => 1; // Starts at 1

    public static int LogLevel = LogLevelInfo;
    public static bool ExperimentalTempo;
    public static bool ExperimentalNoteLength;
    public static int ExperimentalSwapInterval = -1;
    public static int TickRate;
    public static bool UseLogFile;
    public static bool MidiInputDump;
    public static int KeyboardLightsChan = -1;

    private static float _staveEndX;

    /// <summary>Back to the values of a new process (used by the tests).</summary>
    internal static void ResetStatics()
    {
        _staveEndX = 0;
        LogLevel = LogLevelInfo;
        ExperimentalTempo = false;
        ExperimentalNoteLength = false;
        ExperimentalSwapInterval = -1;
        TickRate = 0;
        UseLogFile = false;
        MidiInputDump = false;
        KeyboardLightsChan = -1;
        AppX = AppY = AppWidth = AppHeight = 0;
    }
}

/// <summary>The time values of the conductor are scaled by this factor (Util.h).</summary>
public static class Speed
{
    public const int AdjustFactor = 1000;
    public static int DeltaAdjust(int delta) => delta / AdjustFactor;
    public static long DeltaAdjustL(long delta) => delta / AdjustFactor;
    public static float DeltaAdjustF(long delta) => (float)delta / (float)AdjustFactor;
}

/// <summary>The log of the C++ code (ppLogInfo, ppLogWarn ...).</summary>
public static class Log
{
    /// <summary>Receives the messages; nothing is written while it is null.</summary>
    public static Action<string>? Output;

    public static void Info(string message)
    {
        if (Cfg.LogLevel >= Cfg.LogLevelInfo)
            Output?.Invoke("Info: " + message);
    }

    public static void Warn(string message)
    {
        if (Cfg.LogLevel >= Cfg.LogLevelWarn)
            Output?.Invoke("Warn: " + message);
    }

    public static void Debug(string message)
    {
        if (Cfg.LogLevel >= Cfg.LogLevelDebug)
            Output?.Invoke("Debug: " + message);
    }

    public static void Error(string message) => Output?.Invoke("ERROR: " + message);
}

/// <summary>
/// Tells the operating system that the display is in use (KeepDisplayOn.h). The
/// application sets the actions; by default they do nothing.
/// </summary>
public static class KeepDisplayOn
{
    public static Action<bool> SetRequiredAction = _ => { };
    public static Action UserActivityAction = () => { };

    /// <summary>Keep the display on until called again with false (while a song is playing).</summary>
    public static void SetRequired(bool required) => SetRequiredAction(required);

    /// <summary>Reset the idle timer, as if the user had moved the mouse.</summary>
    public static void UserActivity() => UserActivityAction();
}
