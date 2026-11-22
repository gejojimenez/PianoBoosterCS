// The functions of the FluidSynth library (LGPL 2.1) used by MidiDeviceFluidSynth.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PianoBooster.Midi;

/// <summary>P/Invoke to libfluidsynth (version 2.x). The library is searched for in the folder
/// named by the environment variable PIANOBOOSTER_FLUIDSYNTH_DIR, next to the program, in the
/// usual places of the system and, on Windows, in the MSYS2 folders.</summary>
internal static partial class FluidSynthNative
{
    private const string Lib = "fluidsynth";

    public const int FluidOk = 0;
    public const int FluidFailed = -1;

    private static readonly object s_loadLock = new();
    private static bool s_loadTried;
    private static IntPtr s_handle;

    static FluidSynthNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(FluidSynthNative).Assembly, Resolve);
    }

    /// <summary>Whether the library can be loaded.</summary>
    public static bool IsAvailable => Load() != IntPtr.Zero;

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == Lib ? Load() : IntPtr.Zero;

    private static IntPtr Load()
    {
        lock (s_loadLock)
        {
            if (s_loadTried)
                return s_handle;
            s_loadTried = true;

            foreach (string candidate in Candidates())
            {
                // a full path makes Windows look for the libraries it needs in the same folder
                if (NativeLibrary.TryLoad(candidate, out s_handle))
                    return s_handle;
            }
            s_handle = IntPtr.Zero;
            return s_handle;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        string[] names;
        if (OperatingSystem.IsWindows())
            names = new[] { "libfluidsynth-3.dll", "fluidsynth-3.dll", "libfluidsynth-2.dll", "fluidsynth.dll" };
        else if (OperatingSystem.IsMacOS())
            names = new[] { "libfluidsynth.3.dylib", "libfluidsynth.dylib" };
        else
            names = new[] { "libfluidsynth.so.3", "libfluidsynth.so.2", "libfluidsynth.so" };

        var folders = new List<string>();
        string? envDir = Environment.GetEnvironmentVariable("PIANOBOOSTER_FLUIDSYNTH_DIR");
        if (!string.IsNullOrEmpty(envDir))
            folders.Add(envDir);
        folders.Add(AppContext.BaseDirectory);

        foreach (string folder in folders)
            foreach (string name in names)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path))
                    yield return path;
            }

        foreach (string name in names) // the search of the system (PATH, LD_LIBRARY_PATH ...)
            yield return name;

        if (OperatingSystem.IsWindows())
        {
            foreach (string folder in new[] { @"C:\msys64\ucrt64\bin", @"C:\msys64\mingw64\bin", @"C:\msys64\clang64\bin" })
                foreach (string name in names)
                {
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path))
                        yield return path;
                }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (string folder in new[] { "/opt/homebrew/lib", "/usr/local/lib" })
                foreach (string name in names)
                    yield return Path.Combine(folder, name);
        }
    }

    // fluid_settings_t
    [LibraryImport(Lib)] public static partial IntPtr new_fluid_settings();
    [LibraryImport(Lib)] public static partial void delete_fluid_settings(IntPtr settings);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fluid_settings_setstr(IntPtr settings, string name, string str);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fluid_settings_setnum(IntPtr settings, string name, double val);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fluid_settings_setint(IntPtr settings, string name, int val);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fluid_settings_getnum(IntPtr settings, string name, out double val);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fluid_settings_getint(IntPtr settings, string name, out int val);
    [LibraryImport(Lib)]
    public static unsafe partial int fluid_settings_copystr(IntPtr settings, byte* name, byte* str, int len);
    [LibraryImport(Lib)]
    public static unsafe partial void fluid_settings_foreach(IntPtr settings, IntPtr data,
        delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void> func);

    // fluid_synth_t
    [LibraryImport(Lib)] public static partial IntPtr new_fluid_synth(IntPtr settings);
    [LibraryImport(Lib)] public static partial void delete_fluid_synth(IntPtr synth);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fluid_synth_sfload(IntPtr synth, string filename, int resetPresets);
    [LibraryImport(Lib)] public static partial int fluid_synth_noteon(IntPtr synth, int chan, int key, int vel);
    [LibraryImport(Lib)] public static partial int fluid_synth_noteoff(IntPtr synth, int chan, int key);
    [LibraryImport(Lib)] public static partial int fluid_synth_cc(IntPtr synth, int chan, int ctrl, int val);
    [LibraryImport(Lib)] public static partial int fluid_synth_program_change(IntPtr synth, int chan, int program);
    [LibraryImport(Lib)] public static partial int fluid_synth_channel_pressure(IntPtr synth, int chan, int val);
    [LibraryImport(Lib)] public static partial int fluid_synth_pitch_bend(IntPtr synth, int chan, int val);
    [LibraryImport(Lib)] public static partial void fluid_synth_set_gain(IntPtr synth, float gain);
    // FluidSynth 2.2 and later
    [LibraryImport(Lib)] public static partial int fluid_synth_reverb_on(IntPtr synth, int fxGroup, int on);
    [LibraryImport(Lib)] public static partial int fluid_synth_chorus_on(IntPtr synth, int fxGroup, int on);
    // before FluidSynth 2.2 (deprecated)
    [LibraryImport(Lib)] public static partial void fluid_synth_set_reverb_on(IntPtr synth, int on);
    [LibraryImport(Lib)] public static partial void fluid_synth_set_chorus_on(IntPtr synth, int on);

    // fluid_audio_driver_t
    [LibraryImport(Lib)] public static partial IntPtr new_fluid_audio_driver(IntPtr settings, IntPtr synth);
    [LibraryImport(Lib)] public static partial void delete_fluid_audio_driver(IntPtr driver);

    [LibraryImport(Lib)] public static partial void fluid_version(out int major, out int minor, out int micro);
}
