// PianoRollWindow: a test window for the piano roll, the overview and the SectionPlayer of the
// section editor (phase 5.3 of the port), before the editor itself (5.4). It links the
// controls of the app. Part of the .NET port of PianoBooster. GPLv3.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using PianoBooster.App;
using PianoBooster.Core;
using PianoBooster.Midi;
using PianoBooster.Rendering;

namespace PianoBooster.PianoRollWindow;

internal static class Program
{
    public static string? MidiFile;
    public static readonly Dictionary<string, string> Options = new();

    public static string Get(string key, string def) => Options.TryGetValue(key, out string? value) ? value : def;

    private static void Usage()
    {
        Console.WriteLine("""
            PianoRollWindow: el piano roll, la vista general y el SectionPlayer del editor de secciones
            (fase 5.3 del port).

            Uso:
              PianoRollWindow <fichero.mid> [opciones]

            Opciones:
              --out "<puerto>"          salida MIDI (los nombres de PlaySong --list); sin ella no suena
              --soundfont <fichero.sf2> usa el sintetizador interno (FluidSynth)
              --channel <0-15>          canal practicado (por defecto el que tiene más notas)
              --select <compás>-<compás> selección inicial (compases desde 1, el final incluido)
              --dark                    tema oscuro
              --play                    reproduce la selección al abrir
              --quit-after <s>          se cierra sola
              --screenshot <fichero.png> guarda una imagen de la ventana antes de cerrarse

            Teclas: espacio reproduce o para la selección, R repetir, + / - zoom, 0 toda la canción,
            S cambia el redondeo (compás, tiempo, medio, cuarto, nada), O muestra u oculta los otros
            instrumentos, 1-4 velocidad (0,5 / 1 / 1,5 / 2), Esc termina.
            """);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "--dark" or "--play" or "-h" or "--help")
                Options[arg] = "1";
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                Options[arg] = args[++i];
            else if (MidiFile == null)
                MidiFile = arg;
            else
            {
                Usage();
                return 2;
            }
        }
        if (Options.ContainsKey("-h") || Options.ContainsKey("--help") || MidiFile == null)
        {
            Usage();
            return MidiFile == null ? 2 : 0;
        }
        if (!File.Exists(MidiFile))
        {
            Console.Error.WriteLine($"No existe {MidiFile}");
            return 1;
        }

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
        return App.ExitCode;
    }
}

/// <summary>A device that plays nothing (without --out the cursor still moves).</summary>
internal sealed class SilentDevice : IMidiDevice
{
    public void PlayMidiEvent(in MidiEvent midiEvent) { }
    public int CheckMidiInput() => 0;
    public MidiEvent ReadMidiInput() => new();
    public bool ValidMidiOutput() => true;
    public bool OpenMidiPort(MidiType type, string portName) => true;
}

internal sealed class App : Application
{
    public static int ExitCode;

    private MidiDevice? _midiDevice;
    private SectionPlayer? _player;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Program.Options.ContainsKey("--dark") ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = CreateWindow(desktop);
                desktop.Exit += (_, _) =>
                {
                    _player?.Dispose();
                    _midiDevice?.Dispose();
                };
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

    private Window CreateWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Cfg.SetDefaults();
        Log.Output = Console.Error.WriteLine;

        var map = new SongMap();
        if (!map.Load(Path.GetFullPath(Program.MidiFile!)))
            throw new InvalidOperationException($"No se puede leer {Program.MidiFile}");

        // the output
        IMidiDevice device = new SilentDevice();
        string? soundFont = Program.Options.GetValueOrDefault("--soundfont");
        string? output = Program.Options.GetValueOrDefault("--out");
        if (soundFont != null || output != null)
        {
            var settings = new MemorySettings();
            if (soundFont != null)
            {
                settings.SetValue("FluidSynth/SoundFont", Path.GetFullPath(soundFont));
                settings.SetValue("FluidSynth/masterGainSpin", MidiDeviceFluidSynth.FluidDefaultGain.ToString(CultureInfo.InvariantCulture));
                output ??= MidiDeviceFluidSynth.GetFluidInternalName();
            }
            _midiDevice = new MidiDevice();
            _midiDevice.Init(settings);
            if (!_midiDevice.OpenMidiPort(MidiType.Output, output!))
                Console.Error.WriteLine($"No se puede abrir la salida {output}");
            device = _midiDevice;
        }

        // the hands: the piano part in one channel, as when a song is opened
        int channel = int.Parse(Program.Get("--channel",
            map.Notes.Count == 0 ? "0" : map.Notes.GroupBy(n => n.Channel).OrderByDescending(g => g.Count()).First().Key.ToString(CultureInfo.InvariantCulture)),
            CultureInfo.InvariantCulture);
        Note.Reset();
        Note.SetChannelHands(-1, channel);

        var overviewControl = new SongOverviewControl { Margin = new Thickness(4) };
        var rollControl = new PianoRollControl { Margin = new Thickness(4, 0, 4, 4) };
        var status = new TextBlock { Margin = new Thickness(6, 4), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        SongOverview overview = overviewControl.Overview;
        PianoRollView roll = rollControl.View;
        _player = new SectionPlayer(map, device);
        double speed = 1.0;
        bool repeat = false;

        void ShowStatus()
        {
            string selection = roll.HasSelection
                ? $"compás {map.PositionOfTick(roll.SelectionStart).Bar + Bar.FirstBarNumber} a {map.SectionEndPosition(roll.SelectionEnd).Bar + Bar.FirstBarNumber}"
                : "sin selección";
            status.Text = $"{Path.GetFileName(Program.MidiFile)} · canal {channel + 1} · {selection} · redondeo {roll.Snap} · " +
                          $"velocidad {speed:0.0} · repetir {(repeat ? "sí" : "no")} · {(_player.IsPlaying ? "sonando" : "parado")}" +
                          $"  —  espacio: tocar/parar, R, +/-, 0, S, O, 1-4";
        }

        overview.SetSongMap(map);
        roll.SetSongMap(map);
        overview.SetPractiseChannel(channel);
        roll.SetPractiseChannel(channel);

        // as the section editor will do
        overview.CentreRequested += tick => roll.CentreOn(tick);
        roll.ViewChanged += () => overview.SetVisibleRange(roll.FirstVisibleTick(), roll.LastVisibleTick());
        roll.SelectionChanged += (start, end) =>
        {
            overview.SetSelection(start, end);
            ShowStatus();
        };
        roll.SelectionEdited += ShowStatus;
        var parts = new List<SongPart>();
        roll.PartClicked += id =>
        {
            SongPart part = parts.First(p => p.Id == id);
            long start = map.TickOfPosition(part.Start), end = map.SectionEndTick(part.End);
            roll.SetParts(parts, id);
            overview.SetParts(parts, id);
            roll.SetSelection(start, end);
            overview.SetSelection(start, end);
            roll.EnsureVisible(start, end);
            ShowStatus();
        };
        _player.PositionChanged += tick =>
        {
            roll.SetCursorTick(tick, true);
            overview.SetCursorTick(tick);
        };
        _player.Stopped += () =>
        {
            roll.SetCursorTick(-1, false);
            overview.SetCursorTick(-1);
            ShowStatus();
        };

        // two sections to click in the lane
        int bars = map.BarCount();
        if (bars >= 4)
        {
            parts.Add(new SongPart { Id = 1, Name = "Intro", Start = map.PositionOfTick(0), End = map.SectionEndPosition(map.BarStartTick(Math.Min(4, bars))) });
            parts.Add(new SongPart { Id = 2, Name = "Middle part", Start = map.PositionOfTick(map.BarStartTick(bars / 2)), End = map.SectionEndPosition(map.BarStartTick(Math.Min(bars, bars / 2 + 4))) });
            roll.SetParts(parts, -1);
            overview.SetParts(parts, -1);
        }

        string? select = Program.Options.GetValueOrDefault("--select");
        if (select != null)
        {
            string[] range = select.Split('-');
            long start = map.BarStartTick(int.Parse(range[0], CultureInfo.InvariantCulture) - Bar.FirstBarNumber);
            long end = map.BarStartTick(int.Parse(range[1], CultureInfo.InvariantCulture) - Bar.FirstBarNumber + 1);
            roll.SetSelection(start, end);
            overview.SetSelection(start, end);
        }

        var panel = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(overviewControl, Dock.Top);
        panel.Children.Add(status);
        panel.Children.Add(overviewControl);
        panel.Children.Add(rollControl);
        var window = new Window
        {
            Title = "PianoRollWindow - " + Path.GetFileName(Program.MidiFile),
            Width = 1100,
            Height = 520,
            Content = panel,
        };

        void PlayOrStop()
        {
            if (_player.IsPlaying)
                _player.Stop();
            else if (roll.HasSelection)
                _player.Play(roll.SelectionStart, roll.SelectionEnd, repeat, speed);
            else
                _player.Play(Math.Max(0, roll.FirstVisibleTick()), map.EndTick, repeat, speed);
            ShowStatus();
        }

        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Space: PlayOrStop(); break;
                case Key.R: repeat = !repeat; _player.SetRepeat(repeat); break;
                case Key.OemPlus or Key.Add: roll.ZoomBy(1.25); break;
                case Key.OemMinus or Key.Subtract: roll.ZoomBy(0.8); break;
                case Key.D0 or Key.NumPad0: roll.ZoomToSong(); break;
                case Key.S: roll.Snap = (Snap)(((int)roll.Snap + 1) % 5); break;
                case Key.O:
                    bool show = !_othersShown;
                    _othersShown = show;
                    roll.SetShowOtherParts(show);
                    overview.SetShowOtherParts(show);
                    break;
                case >= Key.D1 and <= Key.D4:
                    speed = 0.5 * (e.Key - Key.D0);
                    _player.SetSpeed(speed);
                    break;
                case Key.Escape: window.Close(); break;
                default: return;
            }
            e.Handled = true;
            ShowStatus();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        window.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (roll.HasSelection)
                    roll.EnsureVisible(roll.SelectionStart, roll.SelectionEnd);
                else
                    roll.ZoomToFit(0, map.BarStartTick(Math.Min(map.BarCount(), 8)));
                overview.SetVisibleRange(roll.FirstVisibleTick(), roll.LastVisibleTick());
                if (Program.Options.ContainsKey("--play"))
                    PlayOrStop();
                ShowStatus();
            }, DispatcherPriority.Background);

            if (Program.Options.TryGetValue("--quit-after", out string? seconds))
            {
                DispatcherTimer.RunOnce(() =>
                {
                    if (Program.Options.TryGetValue("--screenshot", out string? png))
                    {
                        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
                        using var bitmap = new RenderTargetBitmap(size);
                        bitmap.Render(window);
#pragma warning disable CS0618 // a PNG with the default options is all that is wanted
                        bitmap.Save(png);
#pragma warning restore CS0618
                        Console.WriteLine($"Imagen: {png}");
                    }
                    Console.WriteLine(status.Text);
                    desktop.Shutdown(0);
                }, TimeSpan.FromSeconds(double.Parse(seconds, CultureInfo.InvariantCulture)));
            }
        };
        ShowStatus();
        return window;
    }

    private bool _othersShown = true;
}
