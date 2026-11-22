// Ported from QtMain.cpp and QtWindow::decodeCommandLine() of PianoBooster (C++).
// Copyright (c) 2008-2020, L. J. Barman and others. GPLv3.

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;

namespace PianoBooster.App;

/// <summary>What the command line asks for (QtWindow::decodeCommandLine()).</summary>
internal sealed class CommandLine
{
    /// <summary>The MIDI file given on the command line, or null.</summary>
    public string? MidiFile;
    /// <summary>Not in the C++: a settings file to use instead of the user's (for tests).</summary>
    public string? SettingsFile;
    /// <summary>Not in the C++: close the window after this many seconds (for tests).</summary>
    public double QuitAfterSeconds;
    /// <summary>Not in the C++: save a picture of the window just before it closes (for tests).</summary>
    public string? ScreenshotFile;
}

internal static class Program
{
    public const string Version = "1.3.11"; // PB_VERSION

    public static CommandLine CommandLine { get; } = new();

    private static void DisplayUsage()
    {
        Console.WriteLine("Usage: pianobooster [flags] [midifile]");
        Console.WriteLine("  -d, --debug             Increase the debug level.");
        Console.WriteLine("      --Xnote-length      Displays the note length (experimental)");
        Console.WriteLine("  -h, --help              Displays this help message.");
        Console.WriteLine("  -v, --version           Displays version number and then exits.");
        Console.WriteLine("  -l   --log              Write debug info to the \"pb.log\" log file.");
        Console.WriteLine("       --midi-input-dump  Displays the midi input in hex.");
        Console.WriteLine("       --lights           Turns on the keyboard lights.");
        Console.WriteLine("       --settings=<file>  Use this settings file (.json) instead of the user's one.");
    }

    private static int DecodeIntegerParam(string arg, int defaultParam)
    {
        int n = arg.LastIndexOf('=');
        if (n == -1 || (n + 1) >= arg.Length)
            return defaultParam;
        return int.TryParse(arg.AsSpan(n + 1), out int value) ? value : defaultParam;
    }

    private static bool ValidateIntegerParam(string arg)
    {
        int n = arg.LastIndexOf('=');
        if (n == -1 || (n + 1) >= arg.Length)
            return false;
        return int.TryParse(arg.AsSpan(n + 1), out _);
    }

    private static void ValidateIntegerParamWithMessage(string arg)
    {
        if (!ValidateIntegerParam(arg))
        {
            Console.Error.WriteLine($"ERROR: Invalid parameter to a command line argument \"{arg}\".");
            Environment.Exit(0);
        }
    }

    private static string StringParam(string arg)
    {
        int n = arg.IndexOf('=');
        return n < 0 ? "" : arg[(n + 1)..];
    }

    /// <summary>The flags of the command line, as decodeCommandLine() (the MIDI file is checked
    /// later, by the main window, as in the C++).</summary>
    private static void DecodeCommandLine(string[] args)
    {
        Core.Cfg.SetDefaults(); // QtWindow calls it before decodeCommandLine()
        bool hasMidiFile = false;
        foreach (string arg in args) // QCoreApplication::arguments() without the program
        {
            if (arg.StartsWith('-'))
            {
                if (arg.StartsWith("--settings", StringComparison.Ordinal))
                    CommandLine.SettingsFile = StringParam(arg);
                else if (arg.StartsWith("--Xscreenshot", StringComparison.Ordinal))
                    CommandLine.ScreenshotFile = StringParam(arg);
                else if (arg.StartsWith("--Xquit-after", StringComparison.Ordinal))
                    CommandLine.QuitAfterSeconds = DecodeIntegerParam(arg, 0);
                else if (arg.StartsWith("-d", StringComparison.Ordinal) || arg.StartsWith("--debug", StringComparison.Ordinal))
                    Core.Cfg.LogLevel++;
                else if (arg.StartsWith("--Xnote-length", StringComparison.Ordinal))
                    Core.Cfg.ExperimentalNoteLength = true;
                else if (arg.StartsWith("--Xtick-rate", StringComparison.Ordinal))
                {
                    ValidateIntegerParamWithMessage(arg);
                    Core.Cfg.TickRate = DecodeIntegerParam(arg, 12);
                }
                else if (arg.StartsWith("-l", StringComparison.Ordinal) || arg.StartsWith("--log", StringComparison.Ordinal))
                    Core.Cfg.UseLogFile = true;
                else if (arg.StartsWith("--midi-input-dump", StringComparison.Ordinal))
                    Core.Cfg.MidiInputDump = true;
                else if (arg.StartsWith("-X1", StringComparison.Ordinal))
                    Core.Cfg.ExperimentalTempo = true;
                else if (arg.StartsWith("-Xswap", StringComparison.Ordinal))
                    Core.Cfg.ExperimentalSwapInterval = DecodeIntegerParam(arg, 100);
                else if (arg.StartsWith("--lights", StringComparison.Ordinal))
                    Core.Cfg.KeyboardLightsChan = 1 - 1; // Channel 1 (really a zero)
                else if (arg.StartsWith("-h", StringComparison.Ordinal) || arg.StartsWith("-?", StringComparison.Ordinal) ||
                         arg.StartsWith("--help", StringComparison.Ordinal))
                {
                    DisplayUsage();
                    Environment.Exit(0);
                }
                else if (arg.StartsWith("-v", StringComparison.Ordinal) || arg.StartsWith("--version", StringComparison.Ordinal))
                {
                    Console.WriteLine("pianobooster Version " + Version);
                    Environment.Exit(0);
                }
                else
                {
                    Console.Error.WriteLine("ERROR: Unknown arguments.");
                    DisplayUsage();
                    Environment.Exit(0);
                }
            }
            else if (!hasMidiFile)
            {
                hasMidiFile = true;
                CommandLine.MidiFile = arg;
            }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        DecodeCommandLine(args);
        OpenLogs();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            CloseLogs();
        }
    }

    // Used by the designer of Avalonia
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static StreamWriter? s_logFile;

    // Util.cpp: the log goes to "pb.log" with --log, otherwise to the standard output
    private static void OpenLogs()
    {
        if (Core.Cfg.UseLogFile)
        {
            try
            {
                s_logFile = new StreamWriter("pb.log", append: false) { AutoFlush = true };
            }
            catch (IOException)
            {
            }
        }
        StreamWriter? file = s_logFile;
        var gate = new object();
        Core.Log.Output = line =>
        {
            lock (gate)
            {
                if (file != null)
                    file.WriteLine(line);
                else
                    Console.WriteLine(line);
            }
        };
    }

    private static void CloseLogs()
    {
        s_logFile?.Dispose();
        s_logFile = null;
    }

    internal static IReadOnlyList<string> MidiFileExtensions { get; } = new[] { ".mid", ".midi", ".kar" };
}
