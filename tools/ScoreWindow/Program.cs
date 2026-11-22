// ScoreWindow: a minimal Avalonia window with the score of PianoBooster drawn with Skia
// (phase 3 of the port). Part of the .NET port of PianoBooster. GPLv3.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using PianoBooster.Core;
using PianoBooster.App;
using PianoBooster.Midi;

namespace PianoBooster.ScoreWindow;

internal sealed class Options
{
    public string? MidiFile;
    public readonly Dictionary<string, string> Values = new();

    public string Get(string key, string def) => Values.TryGetValue(key, out string? value) ? value : def;
    public bool Has(string key) => Values.ContainsKey(key);
}

internal static class Program
{
    public static Options Options { get; } = new();

    private static void Usage()
    {
        Console.WriteLine("""
            ScoreWindow: la ventana de práctica de PianoBooster con Skia (fase 3 del port).

            Uso:
              ScoreWindow <fichero.mid> [opciones]

            Opciones:
              --mode listen|follow|along|rhythm   modo (por defecto listen)
              --hand both|right|left              mano que toca el pianista (por defecto both)
              --out "<puerto>"                    salida MIDI (los nombres de PlaySong --list)
              --in "<puerto>"                     teclado MIDI (por defecto el teclado del PC)
              --soundfont <fichero.sf2>           usa el sintetizador interno (FluidSynth)
              --speed <0.2-2.0>                   velocidad (por defecto 1.0)
              --names                             nombres de las notas
              --trail                             estela de la duración de las notas
              --colored                           notas de colores
              --keyboard                          teclado de piano bajo la partitura
              --size <ancho>x<alto>               tamaño de la ventana (por defecto 1024x700)
              --measure <segundos>                mide la fluidez durante ese tiempo, la escribe y termina
              --skip <segundos>                   no mide los primeros segundos (por defecto 2)
              --direct                            dibuja los símbolos cada vez, sin SKPicture

            Teclas: las del PC hacen de piano (a z x d c f v b h n j m k , . ; / '), el tabulador
            toca el acorde esperado, espacio para o sigue, Esc termina.
            """);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--names" or "--trail" or "--colored" or "--keyboard" or "--direct" or "--verbose" or "-h" or "--help")
                Options.Values[arg] = "1";
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                Options.Values[arg] = args[++i];
            else if (Options.MidiFile == null)
                Options.MidiFile = arg;
            else
            {
                Usage();
                return 2;
            }
        }
        if (Options.Has("-h") || Options.Has("--help") || Options.MidiFile == null)
        {
            Usage();
            return Options.MidiFile == null ? 2 : 0;
        }
        if (!File.Exists(Options.MidiFile))
        {
            Console.Error.WriteLine($"No existe {Options.MidiFile}");
            return 1;
        }

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
        return App.ExitCode;
    }
}

internal sealed class App : Application
{
    public static int ExitCode;

    private MidiDevice? _midiDevice;
    private ConductorThread? _conductor;
    private Song? _song;
    private ScoreView? _view;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = CreateWindow();
                desktop.Exit += (_, _) => Shutdown();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
                ExitCode = 1;
                desktop.Shutdown(1);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private Window CreateWindow()
    {
        Options o = Program.Options;
        Cfg.SetDefaults();
        Cfg.LogLevel = o.Has("--verbose") ? Cfg.LogLevelDebug : Cfg.LogLevelWarn;
        Log.Output = Console.Error.WriteLine;

        var settings = new MemorySettings
        {
            ShowNoteNames = o.Has("--names"),
            IsNoteDurationTrailEnabled = o.Has("--trail"),
            ColoredNotes = o.Has("--colored"),
        };
        if (o.Has("--keyboard"))
            settings.SetValue("View/PianoKeyboard", "on");
        string? soundFont = o.Values.GetValueOrDefault("--soundfont");
        if (soundFont != null)
        {
            settings.SetValue("FluidSynth/SoundFont", Path.GetFullPath(soundFont));
            settings.SetValue("FluidSynth/masterGainSpin", MidiDeviceFluidSynth.FluidDefaultGain.ToString(CultureInfo.InvariantCulture));
        }

        _midiDevice = new MidiDevice();
        _midiDevice.Init(settings);
        List<string> outputs = _midiDevice.GetMidiPortList(MidiType.Output);
        string output = o.Get("--out", soundFont != null ? MidiDeviceFluidSynth.GetFluidInternalName() : (outputs.Count > 0 ? outputs[0] : ""));
        settings.SetValue("Midi/Output", output);
        settings.SetValue("Midi/Input", o.Get("--in", "None"));

        PlayMode playMode = o.Get("--mode", "listen") switch
        {
            "follow" => PlayMode.FollowYou,
            "along" => PlayMode.PlayAlong,
            "rhythm" => PlayMode.RhythmTapping,
            _ => PlayMode.Listen,
        };
        WhichPart hand = o.Get("--hand", "both") switch
        {
            "right" => WhichPart.Right,
            "left" => WhichPart.Left,
            _ => WhichPart.Both,
        };

        // Set up like QtWindow and CGLView (see PlaySong). The view does resizeGL().
        var song = new Song { MidiDevice = _midiDevice };
        var score = new Score(settings);
        song.Init2(score, settings);
        song.TrackList.Init(song, settings);
        Cfg.SetStaveEndX(400); // CGLView::initializeGL()
        song.SetActiveHand(WhichPart.Both);
        score.Init();
        song.RegenerateChordQueue();

        string[] size = o.Get("--size", "1024x700").Split('x');
        int width = int.Parse(size[0], CultureInfo.InvariantCulture);
        int height = int.Parse(size[1], CultureInfo.InvariantCulture);
        new PianoBooster.Rendering.ViewRenderer().Resize(width, height); // the size before the first frame

        song.LoadSong(Path.GetFullPath(o.MidiFile!).Replace('\\', '/'));
        if (song.MidiError != MidiError.NoError)
            throw new InvalidOperationException($"No se puede leer {o.MidiFile}: {song.MidiError}");
        Note.SetChannelHands(-2, -2);
        song.TrackList.Refresh();
        song.TrackList.CurrentRowChanged(song.TrackList.GetActiveItemIndex());
        song.SetPlayMode(playMode);
        song.SetActiveHand(hand);
        score.SetDisplayHand(hand);
        song.SetSpeed(float.Parse(o.Get("--speed", "1.0"), CultureInfo.InvariantCulture));
        _song = song;

        // The calls to Song.Task are timed, to see if the drawing holds up the conductor
        var taskIntervals = new TimeStats();
        long lastTask = 0;
        _conductor = new ConductorThread(ms =>
        {
            long now = Stopwatch.GetTimestamp();
            if (lastTask != 0)
                taskIntervals.Add(Stopwatch.GetElapsedTime(lastTask, now).TotalMilliseconds);
            lastTask = now;
            return song.Task(ms);
        });
        _conductor.Invoke(song.PlayFromStartBar);

        _view = new ScoreView(song, score, settings, _conductor);
        _view.Renderer.Score.Scroll.UsePictures = !o.Has("--direct");

        var window = new Window
        {
            Title = "PianoBooster - " + song.SongTitle,
            Width = width,
            Height = height,
            Content = _view,
            Background = Avalonia.Media.Brushes.Black,
        };
        window.KeyDown += OnKeyDown;
        window.KeyUp += OnKeyUp;
        window.Opened += (_, _) => _conductor.Start();

        double measureSeconds = double.Parse(o.Get("--measure", "0"), CultureInfo.InvariantCulture);
        if (measureSeconds > 0)
        {
            double skip = double.Parse(o.Get("--skip", "2"), CultureInfo.InvariantCulture);
            DispatcherTimer.RunOnce(() =>
            {
                _view.RestartMeasurements();
                taskIntervals.Clear();
                DispatcherTimer.RunOnce(() =>
                {
                    Report(taskIntervals, window);
                    window.Close();
                }, TimeSpan.FromSeconds(measureSeconds));
            }, TimeSpan.FromSeconds(skip));
        }
        return window;
    }

    private void Report(TimeStats taskIntervals, Window window)
    {
        ScoreView view = _view!;
        Console.WriteLine($"Ventana {window.Bounds.Width}x{window.Bounds.Height}, escala {window.RenderScaling}, " +
                          $"{(view.Renderer.Score.Scroll.UsePictures ? "con SKPicture" : "sin SKPicture")}");
        Console.WriteLine("Fotogramas (hora del refresco):  " + view.FrameMeter.Compute());
        Console.WriteLine("Fotogramas (hora del dibujo):    " + view.RenderMeter.Compute());
        Console.WriteLine("Lock para la foto:               " + view.PrepareTimes);
        Console.WriteLine("Dibujo de un fotograma:          " + view.RenderTimes);
        Console.WriteLine("Entre llamadas a Song.Task:      " + taskIntervals);
    }

    private readonly HashSet<char> _keysDown = new();

    private static char KeyChar(KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
            return 't';
        string? symbol = e.KeySymbol;
        return string.IsNullOrEmpty(symbol) ? '\0' : char.ToLowerInvariant(symbol[0]);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_song == null || _conductor == null)
            return;
        if (e.Key == Key.Escape)
        {
            (sender as Window)?.Close();
            return;
        }
        if (e.Key == Key.Space)
        {
            _conductor.Invoke(() => _song.PlayMusic(!_song.PlayingMusic));
            e.Handled = true;
            return;
        }
        char c = KeyChar(e);
        if (c == '\0' || !_keysDown.Add(c))
            return; // the key repeats
        if (_conductor.Invoke(() => _song.PcKeyPress(c, true)))
            e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_song == null || _conductor == null)
            return;
        char c = KeyChar(e);
        if (c == '\0' || !_keysDown.Remove(c))
            return;
        _conductor.Invoke(() => _song.PcKeyPress(c, false));
        e.Handled = true;
    }

    private void Shutdown()
    {
        _conductor?.Stop();
        _song?.PlayMusic(false); // all the sounds off
        _midiDevice?.Dispose();
    }
}
