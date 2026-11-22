// PlaySong: plays a MIDI file with PianoBooster.Core and PianoBooster.Midi, without an interface.
// Part of the .NET port of PianoBooster. GPLv3.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PianoBooster.Core;
using PianoBooster.Midi;

namespace PianoBooster.PlaySong;

using Midi = PianoBooster.Core.Midi;

/// <summary>Passes everything to the real device and tells what the pianist plays.</summary>
internal sealed class ReportingMidiDevice : IMidiDevice
{
    private readonly IMidiDevice _device;
    private readonly ConcurrentQueue<string> _messages;

    public ReportingMidiDevice(IMidiDevice device, ConcurrentQueue<string> messages)
    {
        _device = device;
        _messages = messages;
    }

    public void PlayMidiEvent(in MidiEvent midiEvent) => _device.PlayMidiEvent(midiEvent);
    public int CheckMidiInput() => _device.CheckMidiInput();

    public MidiEvent ReadMidiInput()
    {
        MidiEvent midiEvent = _device.ReadMidiInput();
        if (midiEvent.Type == Midi.NoteOn)
            _messages.Enqueue($"  tocas {Program.NoteName(midiEvent.Note)} (vel {midiEvent.Velocity})");
        return midiEvent;
    }

    public bool ValidMidiOutput() => _device.ValidMidiOutput();
    public bool OpenMidiPort(MidiType type, string portName) => _device.OpenMidiPort(type, portName);
}

internal static class Program
{
    private static readonly string[] s_noteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static string NoteName(int note) => s_noteNames[((note % 12) + 12) % 12] + (note / 12 - 1);

    private static void Usage()
    {
        Console.WriteLine("""
            PlaySong: reproduce un MIDI con el núcleo de PianoBooster (fase 2 del port).

            Uso:
              PlaySong --list
              PlaySong <fichero.mid> [opciones]

            Opciones:
              --mode listen|follow|along|rhythm   modo (por defecto listen)
              --hand both|right|left              mano que toca el pianista (por defecto both)
              --out "<puerto>"                    salida MIDI (un nombre de --list)
              --in "<puerto>"                     teclado MIDI (por defecto el teclado del PC)
              --soundfont <fichero.sf2>           usa el sintetizador interno (FluidSynth)
              --gain <0-200>                      ganancia de FluidSynth (por defecto 80)
              --rate <Hz>                         frecuencia de FluidSynth (por defecto 22050)
              --speed <0.2-2.0>                   velocidad (por defecto 1.0)
              --skill <0-5>                       nivel (por defecto 3)
              --from <compás> / --upto <compás>   tocar desde / hasta un compás
              --seconds <s>                       parar tras unos segundos
              --verbose                           mensajes de depuración

            Sin --in, las teclas del PC hacen de piano: a z x d c f v b h n j m k , . ; / '
            (de La#3 a Re#5; el Do central es la x) y el tabulador toca el acorde esperado.
            Esc o q terminan.
            """);
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var options = new Dictionary<string, string>();
        string? midiFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--list" or "--verbose" or "-h" or "--help")
                options[arg] = "1";
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                options[arg] = args[++i];
            else if (midiFile == null)
                midiFile = arg;
            else
            {
                Usage();
                return 2;
            }
        }
        string Opt(string key, string def) => options.TryGetValue(key, out string? value) ? value : def;

        if (options.ContainsKey("-h") || options.ContainsKey("--help"))
        {
            Usage();
            return 0;
        }

        Cfg.SetDefaults();
        Cfg.LogLevel = options.ContainsKey("--verbose") ? Cfg.LogLevelDebug : Cfg.LogLevelWarn;

        var settings = new MemorySettings();
        string? soundFont = options.GetValueOrDefault("--soundfont");
        if (soundFont != null)
        {
            if (!File.Exists(soundFont))
            {
                Console.Error.WriteLine($"No existe el SoundFont {soundFont}");
                return 1;
            }
            settings.SetValue("FluidSynth/SoundFont", Path.GetFullPath(soundFont));
            settings.SetValue("FluidSynth/masterGainSpin", Opt("--gain", MidiDeviceFluidSynth.FluidDefaultGain.ToString()));
            settings.SetValue("FluidSynth/sampleRateCombo", Opt("--rate", "22050"));
        }

        using var midiDevice = new MidiDevice();
        midiDevice.Init(settings);

        if (options.ContainsKey("--list"))
        {
            Console.WriteLine("Entradas MIDI:");
            foreach (string name in midiDevice.GetMidiPortList(MidiType.Input))
                Console.WriteLine("  " + name);
            Console.WriteLine("Salidas MIDI:");
            foreach (string name in midiDevice.GetMidiPortList(MidiType.Output))
                Console.WriteLine("  " + name);
            if (!MidiDeviceFluidSynth.IsLibraryAvailable)
                Console.WriteLine("(no se encuentra la biblioteca de FluidSynth: se puede indicar su carpeta en PIANOBOOSTER_FLUIDSYNTH_DIR)");
            else if (soundFont == null)
                Console.WriteLine("(con --soundfont aparece también el sintetizador interno)");
            return 0;
        }

        if (midiFile == null)
        {
            Usage();
            return 2;
        }
        if (!File.Exists(midiFile))
        {
            Console.Error.WriteLine($"No existe {midiFile}");
            return 1;
        }

        // The output: the one asked for, the internal synthesizer or the first port
        List<string> outputs = midiDevice.GetMidiPortList(MidiType.Output);
        string output = Opt("--out", soundFont != null ? MidiDeviceFluidSynth.GetFluidInternalName() : (outputs.Count > 0 ? outputs[0] : ""));
        if (output.Length == 0)
        {
            Console.Error.WriteLine("No hay ninguna salida MIDI");
            return 1;
        }
        string input = Opt("--in", "None");
        settings.SetValue("Midi/Output", output);
        settings.SetValue("Midi/Input", input);
        bool pcKeyboard = input == "None";

        PlayMode playMode = Opt("--mode", "listen") switch
        {
            "listen" => PlayMode.Listen,
            "follow" => PlayMode.FollowYou,
            "along" => PlayMode.PlayAlong,
            "rhythm" => PlayMode.RhythmTapping,
            _ => throw new ArgumentException("Modo desconocido: " + Opt("--mode", "")),
        };
        WhichPart hand = Opt("--hand", "both") switch
        {
            "right" => WhichPart.Right,
            "left" => WhichPart.Left,
            _ => WhichPart.Both,
        };

        var messages = new ConcurrentQueue<string>();

        // Set up like QtWindow, CGLView and CSettings (see tests/PianoBooster.Core.Tests/RefDriver.cs)
        var song = new Song();
        song.MidiDevice = new ReportingMidiDevice(midiDevice, messages);
        var score = new Score(settings);
        song.Init2(score, settings);
        song.TrackList.Init(song, settings);
        Cfg.SetStaveEndX(400);
        song.SetActiveHand(WhichPart.Both);
        score.Init();
        song.RegenerateChordQueue();
        StavePos.StaveCenterY = 296.0f;
        Cfg.SetAppDimensions(0, 43, 1024, 652);
        Cfg.SetStaveEndX(1004.0f);
        StavePos.StaveCentralOffset = 84.0f;
        DrawState.ForceCompileRedraw();

        song.LoadSong(Path.GetFullPath(midiFile).Replace('\\', '/'));
        if (song.MidiError != MidiError.NoError)
        {
            Console.Error.WriteLine($"No se puede leer {midiFile}: {song.MidiError}");
            return 1;
        }
        Note.SetChannelHands(-2, -2);
        song.TrackList.Refresh();
        song.TrackList.CurrentRowChanged(song.TrackList.GetActiveItemIndex());

        song.SetPlayMode(playMode);
        song.SetActiveHand(hand);
        song.SetSkill(int.Parse(Opt("--skill", "3"), CultureInfo.InvariantCulture));
        song.SetSpeed(float.Parse(Opt("--speed", "1.0"), CultureInfo.InvariantCulture));
        song.SetPlayFromBar(double.Parse(Opt("--from", "0"), CultureInfo.InvariantCulture));
        song.SetPlayUptoBar(double.Parse(Opt("--upto", "0"), CultureInfo.InvariantCulture));

        Console.WriteLine($"Canción: {song.SongTitle}");
        Console.WriteLine($"Salida: {output}   Entrada: {(pcKeyboard ? "teclado del PC" : input)}   Modo: {playMode}   Mano: {hand}");
        Console.WriteLine("Esc o q para terminar.");

        var conductor = new ConductorThread(song);
        conductor.Invoke(song.PlayFromStartBar);
        if (!conductor.Invoke(song.ValidMidiOutput))
        {
            Console.Error.WriteLine($"No se puede abrir la salida {output}");
            return 1;
        }
        conductor.Start();

        double seconds = double.Parse(Opt("--seconds", "0"), CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        var keysDown = new Dictionary<char, long>(); // PC key -> when to release it (ms)
        int lastBar = -1;
        string lastRating = "";
        bool interactive = !Console.IsInputRedirected;
        bool finished = false;

        while (!finished)
        {
            Thread.Sleep(16); // a frame
            long now = clock.ElapsedMilliseconds;

            // The drawing takes the symbols out of the score (it must, or the song stops reading)
            conductor.Invoke(() => score.DrawScrollingSymbols(true));
            uint bits = conductor.TakeEventBits();

            if ((bits & EventBits.UptoBarReached) != 0)
                conductor.Invoke(song.PlayFromStartBar);
            if ((bits & EventBits.PlayingStopped) != 0)
                finished = true;

            while (interactive && Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape || key.KeyChar == 'q')
                {
                    finished = true;
                    break;
                }
                if (!pcKeyboard)
                    continue;
                char c = key.Key == ConsoleKey.Tab ? 't' : char.ToLowerInvariant(key.KeyChar);
                if (keysDown.ContainsKey(c))
                {
                    keysDown[c] = now + 250; // the key repeats: keep it down
                    continue;
                }
                if (conductor.Invoke(() => song.PcKeyPress(c, true)))
                {
                    keysDown[c] = now + 250; // the console does not tell when a key is released
                    if (c != 't')
                        messages.Enqueue($"  tecla {c}");
                }
            }
            foreach (char c in new List<char>(keysDown.Keys))
                if (keysDown[c] <= now)
                {
                    keysDown.Remove(c);
                    conductor.Invoke(() => song.PcKeyPress(c, false));
                }

            (int bar, string rating) = conductor.Invoke(() =>
            {
                Rating r = song.Rating;
                return (song.BarNumber, $"notas {r.TotalNoteCount}  erróneas {r.WrongNoteCount}  tardías {r.LateNoteCount}  " +
                                        $"precisión {r.AccuracyValue:0.00}{(r.IsAccuracyGood ? "" : " (baja)")}");
            });
            while (messages.TryDequeue(out string? message))
                Console.WriteLine(message);
            if (bar != lastBar)
            {
                lastBar = bar;
                Console.WriteLine($"Compás {bar + 1}");
            }
            if (playMode != PlayMode.Listen && rating != lastRating)
            {
                lastRating = rating;
                Console.WriteLine("  " + rating);
            }

            if (seconds > 0 && clock.Elapsed.TotalSeconds >= seconds)
                finished = true;
        }

        conductor.Stop();
        song.PlayMusic(false); // all the sounds off
        Thread.Sleep(100);
        Rating final = song.Rating;
        if (playMode != PlayMode.Listen)
            Console.WriteLine($"Fin: {final.TotalNoteCount} notas, {final.WrongNoteCount} erróneas, {final.LateNoteCount} tardías, " +
                              $"{final.Percent:0.0} %");
        else
            Console.WriteLine("Fin.");
        return 0;
    }
}
