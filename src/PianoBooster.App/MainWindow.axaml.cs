// Ported from QtWindow.h and QtWindow.cpp of PianoBooster (C++), with the set-up of CGLView
// (its constructor and initializeGL()). Copyright (c) 2008-2020, L. J. Barman and others. GPLv3.
//
// The song is driven by the conductor thread (ConductorThread, phase 2) and drawn by ScoreView
// (phase 3). Everything here runs in the thread of the interface and holds the lock of the song
// (_sync) while it uses the song.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PianoBooster.Core;
using PianoBooster.Data;
using PianoBooster.Midi;

namespace PianoBooster.App;

public partial class MainWindow : Window
{
    public const int MaxRecentFiles = 20;

    private readonly CommandLine _commandLine = new();
    private readonly Settings _settings = null!;
    private readonly Library? _library;
    private readonly ProgressStore? _progress;
    private readonly PracticeSession? _practice;
    private readonly MidiDevice _midiDevice = null!;
    private readonly ConductorThread _conductor = null!;
    private readonly Song _song = null!;
    private readonly Score _score = null!;
    private readonly ScoreView _glWidget = null!;
    private readonly object _sync = new();
    private readonly List<MenuItem> _recentFileActs = new();
    private readonly List<(string Key, Action Method)> _shortcutActions = new();
    private readonly HashSet<char> _keysDown = new(); // to ignore the repeats of a key held down
    private string? _midiFileError;
    private PixelRect _normalGeometry; // the window size and position when not maximized (logical pixels)
    private bool _initialised;

    /// <summary>For the designer of Avalonia only.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    internal MainWindow(CommandLine commandLine)
    {
        _commandLine = commandLine;
        InitializeComponent();
        Strings.TranslateTree(this); // keeps the texts of the XAML files (listWidgetsRetranslateUi): see RefreshTranslate()

        SettingsStore store = commandLine.SettingsFile is { Length: > 0 } file
            ? SettingsStore.Open(Path.GetFullPath(file))
            : SettingsStore.OpenUserSettings();
        _settings = new Settings(this, store);
        // in the folder of the settings, where the C++ keeps its library.db (the same file is shared)
        _library = store.Folder is string folder ? new Library(folder) : null;
        _settings.SetLibrary(_library);
        // the practices of the user profiles, next to it in progress.db (D12)
        _progress = store.Folder is string progressFolder ? new ProgressStore(progressFolder) : null;
        Title = Program.Name + " " + Program.Version; // the name is not translated

        // Cfg::setDefaults() and decodeCommandLine(): see Program
        if (commandLine.MidiFile != null)
            DecodeMidiFileArg(commandLine.MidiFile);

        KeepDisplayOnPlatform.Install();

        // CGLView::CGLView()
        _midiDevice = new MidiDevice();
        _midiDevice.Init(_settings);
        _song = new Song { MidiDevice = _midiDevice };
        _score = new Score(_settings);
        _conductor = new ConductorThread(_song);
        _sync = _conductor.SyncRoot;
        _practice = StartPracticeSession(store);
        _glWidget = new ScoreView(_song, _score, _settings, _conductor)
        {
            WarningMessageSource = _settings.GetWarningMessage,
        };
        _glWidget.SongEventUpdated += SongEventUpdated;
        scoreHost.Content = _glWidget;

        sidePanel.LibraryRequested += OpenLibrary;
        sidePanel.SectionEditorRequested += OpenSectionEditor;
        sidePanel.SetLibrary(_library);
        sidePanel.SetSettings(_settings);
        tutorWindow.IsVisible = false;

        _settings.Init(_song, _sync, sidePanel, topBar);

        lock (_sync)
            _song.Init2(_score, _settings);

        sidePanel.Init(_song, _sync, _song.TrackList, topBar);
        topBar.Init(_song, _sync);
        topBar.ProfilesRequested += () => OnShowProfilesDialog(null, new RoutedEventArgs());
        ShowActiveProfile();

        lock (_sync)
        {
            _song.SetPianoSoundPatches(_settings.Value("Keyboard/RightSound", Cfg.DefaultRightPatch) - 1,
                                       _settings.Value("Keyboard/WrongSound", Cfg.DefaultWrongPatch) - 1, true);

            _song.SetLatencyFix(_settings.Value("Midi/Latency", 0));

            _song.CfgTimingMarkersFlag = _settings.Value("Score/TimingMarkers", _song.CfgTimingMarkersFlag);
            _song.CfgStopPointMode = (StopPointMode)_settings.Value("Score/StopPointMode", (int)_song.CfgStopPointMode);
            _song.CfgRhythmTapping = (RhythmTapping)_settings.Value("Score/RtyhemTappingMode", (int)_song.CfgRhythmTapping);

            _song.ReconnectMidi();
        }

        ReadSettings();

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) => Shutdown();
        PositionChanged += (_, _) => KeepNormalGeometry();
        Resized += (_, _) => KeepNormalGeometry();
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble, handledEventsToo: false);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Bubble, handledEventsToo: false);
    }

    // The whole practices are scored and saved for the active profile (none: as before, no observer)
    private PracticeSession? StartPracticeSession(SettingsStore store)
    {
        if (_progress is not { IsOpen: true })
            return null;
        long profileId = ActiveProfile.Resolve(_progress, store, ActiveProfile.DefaultName(Strings.Tr("User")));
        if (profileId < 0)
            return null;
        var practice = new PracticeSession(_progress, new SongPracticeState(_song)) { ProfileId = profileId };
        lock (_sync)
            _song.PerformanceObserver = practice;
        return practice;
    }

    // CGLView::initializeGL(), which calls QtWindow::init(): when the window is first shown
    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_midiFileError != null)
        {
            await MessageDialog.Warning(this, Strings.Tr("PianoBooster MIDI File Error"), _midiFileError);
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1); // exit(1)
            return;
        }

        lock (_sync)
        {
            Cfg.SetStaveEndX(400); // This value get changed by the resizeGL func
            _song.SetActiveHand(WhichPart.Both);
        }
        Init();
        lock (_sync)
        {
            _score.Init();
            _song.RegenerateChordQueue();
        }
        _conductor.Start(); // m_timer.start(Cfg::tickRate)
        _glWidget.Focus();

        DispatcherTimer.RunOnce(() =>
        {
            string songName = _settings.Value("CurrentSong");
            if (songName.Length > 0)
                _settings.OpenSongFile(songName);
        }, TimeSpan.FromMilliseconds(100));

        if (_commandLine.QuitAfterSeconds > 0)
            DispatcherTimer.RunOnce(() =>
            {
                if (!string.IsNullOrEmpty(_commandLine.ScreenshotFile))
                    SaveScreenshot(_commandLine.ScreenshotFile);
                Close();
            }, TimeSpan.FromSeconds(_commandLine.QuitAfterSeconds));
    }

    // --Xscreenshot (for tests): a picture of the window
    private void SaveScreenshot(string fileName)
    {
        var size = new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
        bitmap.Render(this);
#pragma warning disable CS0618 // a PNG with the default options is all that is wanted
        bitmap.Save(fileName);
#pragma warning restore CS0618
    }

    // QtWindow::init()
    private void Init()
    {
        _settings.LoadSettings();

        CreateActions();
        CreateMenus();
        ReadSettings();

        RefreshTranslate();
        _initialised = true;
    }

    private void Shutdown()
    {
        _conductor.Stop();
        _song.PlayMusic(false); // all the sounds off
        _midiDevice.Dispose();
        _library?.Dispose(); // delete m_library
        _practice?.SaveFinished(); // one that ended just before closing
        _progress?.Dispose();
    }

    public void SongEventUpdated(uint eventBits)
    {
        if ((eventBits & EventBits.PlayingStopped) != 0)
        {
            _practice?.SaveFinished();
            if (sidePanel.IsRepeatSong())
                topBar.On_playFromStartButton_clicked();
            else
                topBar.SetPlayButtonState(false, true);
        }
        if ((eventBits & EventBits.LoadSong) != 0)
            topBar.SetPlayButtonState(false, true);
    }

    private void DecodeMidiFileArg(string arg)
    {
        string path = Path.GetFullPath(arg);
        string name = Path.GetFileName(path);
        if (!File.Exists(path))
            _midiFileError = Strings.Translate("QtWindow", "Cannot open \"%1\"", path);
        else if (!Program.MidiFileExtensions.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            _midiFileError = Strings.Tr("\"%1\" is not a MIDI File", name);
        else
        {
            bool validMidiFile = true;
            try
            {
                using FileStream file = File.OpenRead(path);
                var bytes = new byte[4];
                if (file.Read(bytes, 0, 4) != 4 || Encoding.ASCII.GetString(bytes) != "MThd")
                    validMidiFile = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                validMidiFile = false;
            }
            if (validMidiFile)
                _settings.SetValue("CurrentSong", path.Replace('\\', '/'));
            else
                _midiFileError = Strings.Tr("\"%1\" is not a valid MIDI file", path);
        }
    }

    // ---- actions, menus and shortcuts (createActions(), createMenus(), addShortcutAction())

    private void AddShortcutAction(string key, Action method) => _shortcutActions.Add((key, method));

    private void CreateActions()
    {
        _viewPianoKeyboardChecked(_settings.Value("View/PianoKeyboard") == "on");

        AddShortcutAction("ShortCuts/RightHand", () => sidePanel.SetActiveHand(WhichPart.Right));
        AddShortcutAction("ShortCuts/BothHands", () => sidePanel.SetActiveHand(WhichPart.Both));
        AddShortcutAction("ShortCuts/LeftHand", () => sidePanel.SetActiveHand(WhichPart.Left));
        AddShortcutAction("ShortCuts/PlayFromStart", On_playFromStart);
        AddShortcutAction("ShortCuts/PlayPause", () => topBar.On_playButton_clicked());
        AddShortcutAction("ShortCuts/Faster", () => ChangeSpeed(+0.04f));
        AddShortcutAction("ShortCuts/Slower", () => ChangeSpeed(-0.04f));
        AddShortcutAction("ShortCuts/NextSong", () => sidePanel.NextSong(+1));
        AddShortcutAction("ShortCuts/PreviousSong", () => sidePanel.NextSong(-1));
        AddShortcutAction("ShortCuts/NextBook", () => sidePanel.NextBook(+1));
        AddShortcutAction("ShortCuts/PreviousBook", () => sidePanel.NextBook(-1));
    }

    private void _viewPianoKeyboardChecked(bool value) => viewPianoKeyboard.IsChecked = value;

    private void CreateMenus()
    {
        int index = fileMenu.Items.IndexOf(separatorAct) + 1;
        for (int i = 0; i < MaxRecentFiles; ++i)
        {
            var act = new MenuItem { IsVisible = false };
            act.Click += OnOpenRecentFile;
            _recentFileActs.Add(act);
            fileMenu.Items.Insert(index + i, act);
        }
        UpdateRecentFileActions();
    }

    /// <summary>The fixed shortcuts of the actions (Qt's setShortcut()).</summary>
    private bool FixedShortcut(KeyEventArgs e)
    {
        KeyModifiers mods = e.KeyModifiers;
        if (mods == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.O: OnOpen(null, new RoutedEventArgs()); return true;
                case Key.B: OpenLibrary(); return true;
                case Key.Q: Close(); return true;
                case Key.S: OnShowMidiSetup(null, new RoutedEventArgs()); return true;
                case Key.K: OnShowKeyboardSetup(null, new RoutedEventArgs()); return true;
                case Key.P: OnShowPreferencesDialog(null, new RoutedEventArgs()); return true;
                case Key.D: OnShowSongDetailsDialog(null, new RoutedEventArgs()); return true;
                case Key.E: OpenSectionEditor(); return true;
            }
        }
        else if (mods == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.F11:
                    fullScreenStateAct.IsChecked = !fullScreenStateAct.IsChecked;
                    OnFullScreenStateAct(null, new RoutedEventArgs());
                    return true;
                case Key.F12:
                    sidePanelStateAct.IsChecked = !sidePanelStateAct.IsChecked;
                    OnToggleSidePanel(null, new RoutedEventArgs());
                    return true;
            }
        }
        else if (e.Key == Key.F1 && mods == KeyModifiers.Shift)
        {
            Tempo.EnableFollowTempo(Cfg.ExperimentalTempo); // enableFollowTempo()
            return true;
        }
        else if (e.Key == Key.F1 && mods == KeyModifiers.Alt)
        {
            Tempo.EnableFollowTempo(false); // disableFollowTempo()
            return true;
        }
        return false;
    }

    private static bool IsTextInputFocused(Window window) =>
        window.FocusManager?.GetFocusedElement() is TextBox;

    // While a menu is open it has the keyboard, as in Qt: no shortcuts and no piano keys
    private bool IsMenuOpen() => menuBar.IsOpen || FocusManager?.GetFocusedElement() is MenuItem;

    /// <summary>Whether a key matches a shortcut of the settings (a QKeySequence such as "F2",
    /// "space", "P" or "{"). One character is compared with the text of the key, so that "{"
    /// is Shift+[ on the keyboards that have it there, as in Qt.</summary>
    private static bool MatchesShortcut(string shortcut, KeyEventArgs e)
    {
        shortcut = shortcut.Trim();
        if (shortcut.Length == 0)
            return false;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 && shortcut.Length == 1)
            return false;
        if (shortcut.Length == 1)
            return e.KeySymbol is { Length: > 0 } symbol && string.Equals(symbol, shortcut, StringComparison.OrdinalIgnoreCase);
        try
        {
            // "space" -> "Space", "ctrl+f2" -> "Ctrl+F2"
            string normalised = string.Join("+", shortcut.Split('+').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
            return KeyGesture.Parse(normalised).Matches(e);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    // The shortcuts take the key before the focused control, as the QAction shortcuts of Qt do,
    // but not the characters typed in a text box (Qt's ShortcutOverride).
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_initialised || IsMenuOpen())
            return;
        if (FixedShortcut(e))
        {
            e.Handled = true;
            return;
        }
        bool textFocused = IsTextInputFocused(this);
        foreach ((string key, Action method) in _shortcutActions)
        {
            string shortcut = _settings.Value(key);
            if (textFocused && shortcut.Trim().Length == 1)
                continue;
            if (MatchesShortcut(shortcut, e))
            {
                method();
                e.Handled = true;
                return;
            }
        }
    }

    // keyPressEvent() of QtWindow, and the Up and Down keys of the top bar (its event filter)
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsMenuOpen())
            return;
        if (e.Key == Key.Up)
        {
            topBar.StepSpeed(+1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down)
        {
            topBar.StepSpeed(-1);
            e.Handled = true;
            return;
        }
        if (IsTextInputFocused(this) || e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Alt)
            return;
        if (e.KeySymbol is not { Length: > 0 } text || e.Key == Key.F1)
            return;
        char c = text[0] < 256 ? text[0] : '\0'; // event->text().toLatin1().at(0)
        if (c == '\0' || !_keysDown.Add(c))
            return; // the key repeats (isAutoRepeat())
        bool used;
        lock (_sync)
            used = _song.PcKeyPress(c, true);
        if (used)
            e.Handled = true;
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeySymbol is not { Length: > 0 } text)
            return;
        char c = text[0] < 256 ? text[0] : '\0';
        if (c == '\0' || !_keysDown.Remove(c))
            return;
        lock (_sync)
            _song.PcKeyPress(c, false);
        e.Handled = true;
    }

    private void On_playFromStart()
    {
        bool playing;
        lock (_sync)
            playing = _song.PlayingMusic;
        if (playing)
            topBar.On_playButton_clicked(); // Stop the music first if playing
        else
            topBar.On_playFromStartButton_clicked();
    }

    // on_faster() and on_slower()
    private void ChangeSpeed(float change)
    {
        float speed;
        lock (_sync)
        {
            speed = _song.GetSpeed() + change;
            _song.SetSpeed(speed);
            speed = _song.GetSpeed();
        }
        topBar.SetSpeed((int)(speed * 100.0f + 0.5f));
    }

    // ---- the menus

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleSidePanel(object? sender, RoutedEventArgs e) => sidePanelScroll.IsVisible = sidePanelStateAct.IsChecked;

    private void OnViewPianoKeyboard(object? sender, RoutedEventArgs e) =>
        _settings.SetValue("View/PianoKeyboard", viewPianoKeyboard.IsChecked ? "on" : "off");

    private void OnFullScreenStateAct(object? sender, RoutedEventArgs e) =>
        WindowState = fullScreenStateAct.IsChecked ? WindowState.FullScreen : WindowState.Normal;

    private void OnOpenRecentFile(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string fileName })
            _settings.OpenSongFile(fileName);
    }

    // load the recent file list from the config file into the file menu
    private void UpdateRecentFileActions()
    {
        List<string> files = _settings.StringListValue("RecentFileList");

        int numRecentFiles = Math.Min(files.Count, MaxRecentFiles);

        for (int i = 0; i < numRecentFiles && i < _recentFileActs.Count; ++i)
        {
            // "&1 name"; a '_' of the name is doubled so that it is not taken as the mnemonic
            _recentFileActs[i].Header = "_" + (i + 1) + " " + StrippedName(files[i]).Replace("_", "__");
            _recentFileActs[i].Tag = files[i];
            _recentFileActs[i].IsVisible = true;
        }

        for (int j = numRecentFiles; j < _recentFileActs.Count; ++j)
            _recentFileActs[j].IsVisible = false;

        separatorAct.IsVisible = numRecentFiles > 0;
    }

    private static string StrippedName(string fullFileName) => Path.GetFileName(fullFileName);

    /// <summary>Just used for the RecentFileList.</summary>
    public void SetCurrentFile(string fileName)
    {
        List<string> files = _settings.StringListValue("RecentFileList");
        files.RemoveAll(f => f == fileName);
        files.Insert(0, fileName);
        while (files.Count > MaxRecentFiles)
            files.RemoveAt(files.Count - 1);

        _settings.SetValue("RecentFileList", files);

        UpdateRecentFileActions();
    }

    private void OnWebsite(object? sender, RoutedEventArgs e) => HtmlText.OpenUrl("https://www.pianobooster.org/");

    private async void OnHelp(object? sender, RoutedEventArgs e)
    {
        string text =
            Strings.Tr("<h3>Getting Started</h3>") +
            Strings.Tr("<p>You need a <b>MIDI Piano Keyboard </b> and a <b>MIDI interface</b> for the PC. If you " +
                       "don't have a MIDI keyboard you can still try out PianoBooster using the PC keyboard, 'X' is " +
                       "middle C.</p>") +
            Strings.Tr("<p>PianoBooster now includes a built-in sound generator called FluidSynth " +
                       "which requires a General MIDI (GM) SoundFont. " +
                       "Use the ‘Setup/MIDI Setup’ menu option and then the load button on the FluidSynth tab to install the SoundFont.</p>") +
            Strings.Tr("<p>PianoBooster works best with MIDI files that have separate left and right piano parts " +
                       "using MIDI channels 3 and 4.") +
            Strings.Tr("<h3>Setting Up</h3>") +
            Strings.Tr("<p>First use the <i>Setup/MIDI Setup</i> menu and in the dialog box select the MIDI input and MIDI " +
                       "output interfaces that match your hardware. ") +
            Strings.Tr("Next use <i>File/Open</i> to open the MIDI file \".mid\" or a karaoke \".kar\" file. " +
                       "Now select whether you want to just <i>listen</i> to the music or " +
                       "<i>play along</i> on the piano keyboard by setting the <i>skill</i> level on the side panel. Finally when " +
                       "you are ready click the <i>play icon</i> (or press the <i>space bar</i>) to roll the music.") +
            Strings.Tr("<h3>Hints on Playing the Piano</h3>" +
                       "<p>For hints on how to play the piano see: ") +
            "<a href=\"https://www.pianobooster.org/music-info.html\" ><b>" + Strings.Tr("Piano Hints") + "</b></a></p>" +
            Strings.Tr("<h3>More Information</h3>" +
                       "<p>For more help please visit the PianoBooster ") +
            "<a href=\"https://www.pianobooster.org\" ><b>" + Strings.Tr("website") + "</b></a>, " +
            Strings.Tr("the PianoBooster") + " <a href=\"https://www.pianobooster.org/faq.html\" ><b> " + Strings.Tr("FAQ") + "</b></a> " +
            Strings.Tr("and the") + " <a href=\"http://piano-booster.2625608.n2.nabble.com/Piano-Booster-Users-f1591936.html\"><b>" +
            Strings.Tr("user forum") + "</b></a>.";
        await MessageDialog.Show(this, Strings.Tr("Piano Booster Help"), HtmlText.ToPanel(text), 600);
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        string text =
            Strings.Tr("<b>PianoBooster - Version %1</b> <br><br>", Program.Version) +
            Strings.Tr("<b>Boost</b> your <b>Piano</b> playing skills!<br><br>") +
            "<a href=\"https://www.pianobooster.org/\" ><b>https://www.pianobooster.org/</b></a><br><br>" +
            Strings.Tr("Copyright(c) L. J. Barman, 2008-2020; All rights reserved.<br>") +
            Strings.Tr("Copyright(c) Fabien Givors, 2018-2019; All rights reserved.<br>") +
            Strings.Tr("Copyright(c) Marius Kittler, 2021-2022; All rights reserved.<br>") +
            "<br>" +
            Strings.Tr("This program is made available " +
                       "under the terms of the GNU General Public License version 3 as published by " +
                       "the Free Software Foundation.<br><br>");
        await MessageDialog.Show(this, Strings.Tr("About Piano Booster"), HtmlText.ToPanel(text), 600);
    }

    private async void OnKeyboardShortcuts(object? sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(HtmlText.ToPanel(Strings.Tr(
            "<h2><center>Keyboard shortcuts</center></h2>" +
            "<p>The following PC keyboard shortcuts have been defined.</p>")));

        var rows = new List<(string Action, string Key)>
        {
            (Strings.Tr("Choose the right hand"), DisplayShortCut("ShortCuts/RightHand")),
            (Strings.Tr("Choose both hands"), DisplayShortCut("ShortCuts/BothHands")),
            (Strings.Tr("Choose the left Hand"), DisplayShortCut("ShortCuts/LeftHand")),
            (Strings.Tr("Play from start toggle"), DisplayShortCut("ShortCuts/PlayFromStart")),
            (Strings.Tr("Play Pause Toggle"), DisplayShortCut("ShortCuts/PlayPause")),
            (Strings.Tr("Increase the speed by 5%"), DisplayShortCut("ShortCuts/Faster")),
            (Strings.Tr("Increase the speed by 5%"), DisplayShortCut("ShortCuts/Slower")), // sic, as in the C++
            (Strings.Tr("Change to the Next Song"), DisplayShortCut("ShortCuts/NextSong")),
            (Strings.Tr("Change to the Previous Song"), DisplayShortCut("ShortCuts/PreviousSong")),
            (Strings.Tr("Change to the Next Book"), DisplayShortCut("ShortCuts/NextBook")),
            (Strings.Tr("Change to the Previous Book"), DisplayShortCut("ShortCuts/PreviousBook")),
            (Strings.Tr("Fake Piano keys"), Strings.Tr("X is middle C")),
        };
        // the table of the C++ (border='1' cellpadding='4')
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), HorizontalAlignment = HorizontalAlignment.Center };
        void AddCell(int row, int column, string text, bool header)
        {
            var cell = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
                Padding = new Thickness(4),
                Child = new TextBlock { Text = text, FontWeight = header ? FontWeight.Bold : FontWeight.Normal },
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddCell(0, 0, Strings.Tr("Action"), true);
        AddCell(0, 1, Strings.Tr("Key"), true);
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddCell(i + 1, 0, rows[i].Action, false);
            AddCell(i + 1, 1, rows[i].Key, false);
        }
        panel.Children.Add(grid);
        await MessageDialog.Show(this, Strings.Tr("PC Keyboard ShortCuts"), panel, 600);
    }

    private string DisplayShortCut(string key) => Strings.Tr(_settings.Value(key));

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        _glWidget.StopTimerEvent();
        try
        {
            string currentSong = _settings.GetCurrentSongLongFileName();
            string dir = File.Exists(currentSong) ? Path.GetDirectoryName(currentSong)! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(dir);
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Tr("Open MIDI File"),
                AllowMultiple = false,
                SuggestedStartLocation = start,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(Strings.Tr("MIDI Files")) { Patterns = new[] { "*.mid", "*.MID", "*.midi", "*.MIDI", "*.kar", "*.KAR" } },
                },
            });
            string? fileName = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(fileName))
            {
                fileName = fileName.Replace('\\', '/');
                _settings.OpenSongFile(fileName);
                SetCurrentFile(fileName);
            }
        }
        finally
        {
            _midiDevice.FlushMidiInput();
            _glWidget.StartTimerEvent();
        }
    }

    private async void OpenLibrary()
    {
        topBar.StopMuiscPlaying();
        _glWidget.StopTimerEvent();
        try
        {
            if (_library == null)
            {
                // only without a settings file (the C++ always has a library)
                await MessageDialog.Information(this, Strings.Tr("Library"), Strings.Tr("The library is not available."));
                return;
            }

            // the first time, start the library with the Booster Music course
            if (!_settings.Value("Library/Initialised", false))
            {
                string books = Settings.BoosterMusicBooksFolder();
                if (Directory.Exists(books))
                    _library.AddLocation(books);
                _settings.SetValue("Library/Initialised", true);
            }

            var library = new LibraryDialog(_library, _settings.GetCurrentSongLongFileName());
            if (await library.ShowDialog<bool?>(this) == true && library.SelectedSong.Length > 0)
            {
                _settings.OpenSongFile(library.SelectedSong);
                SetCurrentFile(library.SelectedSong);
            }
        }
        finally
        {
            _midiDevice.FlushMidiInput();
            _glWidget.StartTimerEvent();
        }
    }

    private void OnOpenLibrary(object? sender, RoutedEventArgs e) => OpenLibrary();

    private bool _sectionEditorOpen; // Ctrl+E again while it is open (QDialog::exec() is modal)

    private async void OpenSectionEditor()
    {
        if (_sectionEditorOpen)
            return;
        string songFile = sidePanel.SongFileName;
        if (songFile.Length == 0)
        {
            await MessageDialog.Information(this, Strings.Tr("Section Editor"), Strings.Tr("First open a song."));
            return;
        }
        topBar.StopMuiscPlaying();
        // the editor plays through the same MIDI output, so the conductor must not run
        _glWidget.StopTimerEvent();
        _sectionEditorOpen = true;
        try
        {
            SectionEditor editor;
            long selectedPartId = sidePanel.SelectedPartId;
            lock (_sync)
            {
                TrackList trackList = _song.TrackList;
                // the C++ passes m_song as the device: CConductor is a CMidiDevice
                editor = new SectionEditor(_library, songFile, _song.SongTitle, _midiDevice, _song.GetSpeed(),
                                           trackList.GetAllChannelProgramNames(), trackList.GetAllChannels(),
                                           _song.ActiveChannel, this);
            }
            if (!editor.IsValid)
            {
                await MessageDialog.Warning(this, Strings.Tr("Section Editor"),
                    Strings.Tr("The MIDI file \"%1\" cannot be read.", songFile.Replace('/', Path.DirectorySeparatorChar)));
            }
            else
            {
                bool loopActive;
                BarPosition playFrom, playUpto;
                lock (_sync)
                {
                    loopActive = _song.IsLoopActive;
                    playFrom = _song.PlayFrom;
                    playUpto = _song.PlayUpto;
                }
                editor.SetInitialSelection(selectedPartId, loopActive, playFrom, playUpto);
                if (await editor.ShowDialog<bool?>(this) == true)
                {
                    // the instrument chosen in the editor, then the section
                    sidePanel.SelectInstrument(editor.PractiseChannel);
                    if (editor.PractisePartId >= 0)
                        sidePanel.PractisePart(editor.PractisePartId);
                    else
                        sidePanel.PractiseSelection(editor.PractiseStart, editor.PractiseEnd);
                }
                else
                    sidePanel.ReloadPartsAfterEditing();
                lock (_sync)
                {
                    // the editor may have played with the volumes of the song: put back those of the side panel
                    _song.BoostVolume(_song.BoostVolumeValue);
                    _song.PianoVolume(_song.PianoVolumeValue);
                }
            }
        }
        finally
        {
            _sectionEditorOpen = false;
            _midiDevice.FlushMidiInput();
            _glWidget.StartTimerEvent();
        }
    }

    private void OnOpenSectionEditor(object? sender, RoutedEventArgs e) => OpenSectionEditor();

    private async void OnShowMidiSetup(object? sender, RoutedEventArgs e)
    {
        topBar.StopMuiscPlaying();

        _glWidget.StopTimerEvent();
        try
        {
            var midiSetupDialog = new MidiSetupDialog();
            midiSetupDialog.Init(_song, _sync, _settings, _midiDevice);
            await midiSetupDialog.ShowDialog<bool?>(this);
        }
        finally
        {
            lock (_sync)
                _score.RefreshScroll(); // the notes that can be played may have changed with the input
            _midiDevice.FlushMidiInput();
            _glWidget.StartTimerEvent();
        }
    }

    private async void OnShowPreferencesDialog(object? sender, RoutedEventArgs e)
    {
        var preferencesDialog = new PreferencesDialog();
        preferencesDialog.Init(_song, _sync, _settings, _glWidget);
        await preferencesDialog.ShowDialog<bool?>(this);

        RefreshTranslate();
        lock (_sync)
            _score.RefreshScroll();
    }

    // Not in the C++: the user profiles (phase 1.5 of FEATURES.md). A change of the active profile
    // reaches the practice session at once, so that the practice being played is thrown away
    // instead of being saved for the new profile.
    private async void OnShowProfilesDialog(object? sender, RoutedEventArgs e)
    {
        if (_progress is not { IsOpen: true } || _practice == null)
            return;
        var profilesDialog = new ProfilesDialog();
        profilesDialog.Init(_progress, _settings.Store, _practice.ProfileId, id =>
        {
            _practice.ProfileId = id;
            Log.Info($"Active profile: {id}");
            ShowActiveProfile();
        });
        await profilesDialog.ShowDialog<bool?>(this);
        ShowActiveProfile(); // it may have been renamed
    }

    private void ShowActiveProfile()
    {
        string? name = _practice != null && _progress != null ? _progress.Profile(_practice.ProfileId)?.Name : null;
        topBar.SetProfileName(name);
        profilesAct.IsEnabled = name != null;
    }

    private async void OnShowSongDetailsDialog(object? sender, RoutedEventArgs e)
    {
        var songDetailsDialog = new SongDetailsDialog();
        songDetailsDialog.Init(_song, _sync, _settings);
        await songDetailsDialog.ShowDialog<bool?>(this);
    }

    // showKeyboardSetup(): as in the C++, the conductor keeps running (the test buttons play through it)
    private async void OnShowKeyboardSetup(object? sender, RoutedEventArgs e)
    {
        var keyboardSetup = new KeyboardSetupDialog();
        keyboardSetup.Init(_song, _sync, _settings);
        await keyboardSetup.ShowDialog<bool?>(this);
        lock (_sync)
            _score.RefreshScroll(); // the range of the MIDI keyboard may have changed
    }

    /// <summary>Called by the settings when a song has been loaded.</summary>
    public void SongLoaded(string fileName)
    {
        MidiInfo info = MidiInfo.Read(fileName);
        _library?.SongOpened(fileName, info);
        string title;
        lock (_sync)
            title = _song.SongTitle;
        sidePanel.SetSongInfo(title, fileName, info);
        _practice?.SongLoaded(fileName, title, () =>
        {
            lock (_sync) // after LoadSong, with the hands of its piano part
                return SongMaxScore.FromMidiFile(fileName, _song.ActiveChannel);
        });
        sidePanel.LoadParts(); // the song starts from the beginning, with no part selected
        Title = Program.Name + " " + Program.Version + " - " + title;
    }

    // ---- the size and position of the window

    private void ReadSettings()
    {
        // The window is shown maximized (see App); this is the size and position it
        // has when it is restored down. Avalonia has no restore bounds to set: they are the
        // size and position set before the window is shown, so once it is shown (init() reads
        // the settings again) they are left alone, which moved the maximized window.
        if (IsVisible)
            return;
        SettingsRect normal = _settings.Store.GetRect("Window/NormalGeometry");
        double scaling = DesktopScaling;
        if (normal.IsValid && Screens.ScreenFromPoint(new PixelPoint(
                (int)((normal.X + normal.Width / 2) * scaling), (int)((normal.Y + normal.Height / 2) * scaling))) != null)
            SetGeometry(new PixelRect(normal.X, normal.Y, normal.Width, normal.Height));
        else
        {
            // settings saved by an older version, or the screen has changed
            (int x, int y) = _settings.Store.GetPoint("Window/Pos", (25, 25));
            (int w, int h) = _settings.Store.GetSize("Window/Size", (1200, 800));
            SetGeometry(new PixelRect(x, y, w, h));
        }
        _normalGeometry = Geometry();
    }

    // QWidget::setGeometry(), in logical pixels; Avalonia's position is in physical pixels and
    // is the outer corner of the window (Qt's is the corner of the inside of the window)
    private void SetGeometry(PixelRect r)
    {
        double scaling = DesktopScaling;
        Position = new PixelPoint((int)(r.X * scaling), (int)(r.Y * scaling));
        Width = r.Width;
        Height = r.Height;
    }

    private PixelRect Geometry()
    {
        double scaling = DesktopScaling;
        return new PixelRect((int)Math.Round(Position.X / scaling), (int)Math.Round(Position.Y / scaling),
                             (int)Math.Round(Width), (int)Math.Round(Height));
    }

    private void WriteSettings()
    {
        // the size and position when the window is not maximized
        _settings.Store.SetValue("Window/NormalGeometry",
            new SettingsRect(_normalGeometry.X, _normalGeometry.Y, _normalGeometry.Width, _normalGeometry.Height));
        _settings.WriteSettings();
    }

    // Keep the size and position of the window when it is not maximized (moveEvent(), resizeEvent())
    private void KeepNormalGeometry()
    {
        if (IsVisible && WindowState == WindowState.Normal)
            _normalGeometry = Geometry();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        lock (_sync)
        {
            if (_song.PlayingMusic)
                _song.PlayMusic(false);
        }
        WriteSettings();
    }

    // ---- the tutor page

    public void LoadTutorHtml(string name)
    {
        if (name.Length == 0)
        {
            tutorWindow.IsVisible = false;
            tutorScroll.Content = null;
            return;
        }
        string htmlBody;
        try
        {
            htmlBody = File.ReadAllText(name, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        // <style> body{background-color:#FFFFC0;color: black} p{font-size: 18px;} blockquote{color: #ff0000;}
        StackPanel page = HtmlText.ToPanel(htmlBody, 18, Brushes.Red);
        page.SetValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty, Brushes.Black);
        tutorScroll.Content = page;
        tutorWindow.Height = 180;
        tutorWindow.IsVisible = true;
    }

    // QtWindow::refreshTranslate()
    private void RefreshTranslate()
    {
        string locale = _settings.SelectedLangauge();

        // the QTranslator of the application: the satellite assembly of the language
        Strings.Culture = Strings.CultureOfLanguage(locale);
        Log.Info($"Translations: '{locale}' ({(Strings.Culture.Name.Length > 0 ? Strings.Culture.Name : "English")})");

        // the tr() of the classes without interface, with their contexts of the C++ code
        MidiInfo.Translate = text => Strings.Translate("CMidiInfo", text);
        TrackList.Translate = text => Strings.Translate("QObject", text); // QObject::tr(); also tr() of CTrackList
        PianoBooster.Rendering.Draw.NoteNames = new[] { "C", "D", "E", "F", "G", "A", "B" }
            .Select(n => Strings.Translate("CDraw", n)).ToArray();
        _glWidget.Renderer.AccuracyText = Strings.Translate("CGLView", "Accuracy:");
        _glWidget.Renderer.SongText = Strings.Translate("CGLView", "Song:");
        _glWidget.Renderer.BarText = Strings.Translate("CGLView", "Bar:");

        // retranslate UI: the texts of the XAML files, kept by the constructor
        Strings.TranslateTree(this);

        sidePanel.UpdateTranslate();
        topBar.UpdateTranslate();
        _settings.UpdateWarningMessages();
        _settings.UpdateTutorPage();
    }
}
