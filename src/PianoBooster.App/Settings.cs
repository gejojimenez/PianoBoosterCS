// Ported from Settings.h and Settings.cpp of PianoBooster (C++).
// Copyright (c) 2008-2020, L. J. Barman and others. GPLv3.
//
// The settings of the programme (the C++ CSettings, which is a QSettings), kept in a JSON file
// with the same keys (PianoBooster.Data.SettingsStore). The settings of each song (hand, speed
// and the MIDI channel of each hand) and the last song of each folder (book) are kept in the
// library (ISongLibrary; the SQLite library comes in phase 5).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using PianoBooster.Core;
using PianoBooster.Data;
using PianoBooster.Midi;

namespace PianoBooster.App;

public sealed class Settings : IPianoSettings
{
    public const string AppName = "pianobooster"; // QSTR_APPNAME
    public const int MusicRelease = 4; // MUSIC_RELEASE of version.h

    private readonly SettingsStore _store;
    private readonly MainWindow _mainWindow;
    private ISongLibrary? _library;
    private SongSettings _songSettings = new(); // the settings of the current song, as loaded or last saved
    private string _songSettingsPath = "";      // the song they belong to

    private Song _song = null!;
    private object _sync = new();               // the lock of the song (ConductorThread.SyncRoot)
    private SidePanel _guiSidePanel = null!;
    private TopBar _guiTopBar = null!;
    private bool _noteNamesEnabled;
    private bool _coloredNotes;
    private bool _tutorPagesEnabled;
    private bool _advancedMode;
    private bool _followThroughErrorsEnabled;
    private bool _noteDurationTrail;
    private string _bookPath = "";
    private string _currentBookName = "";
    private string _currentSongName = "";
    private string _warningMessage = "";
    private List<string> _fluidSoundFontNames = new();

    public Settings(MainWindow mainWindow, SettingsStore store)
    {
        _mainWindow = mainWindow;
        _store = store;

        _advancedMode = false;
        _noteNamesEnabled = Value("Score/NoteNames", true);
        _coloredNotes = Value("Score/ColoredNotes", false);
        _tutorPagesEnabled = Value("Tutor/TutorPages", true);
        Notation.DisplayCourtesyAccidentals = Value("Score/CourtesyAccidentals", false);
        _followThroughErrorsEnabled = Value("Score/FollowThroughErrors", false);
        _noteDurationTrail = Value("Score/NoteDurationTrail", false);

        // load Fluid settings
        SetFluidSoundFontNames(_store.GetStringList("FluidSynth/SoundFont"));
    }

    /// <summary>The file behind the settings (QSettings::fileName()).</summary>
    public SettingsStore Store => _store;

    // QSettings::value / setValue / contains
    public bool Contains(string key) => _store.Contains(key);
    public string Value(string key, string defaultValue = "") => _store.GetString(key, defaultValue);
    public int Value(string key, int defaultValue) => _store.GetInt(key, defaultValue);
    public bool Value(string key, bool defaultValue) => _store.GetBool(key, defaultValue);
    public List<string> StringListValue(string key) => _store.GetStringList(key);
    public void SetValue(string key, string value) => _store.SetValue(key, value);
    public void SetValue(string key, int value) => _store.SetValue(key, value);
    public void SetValue(string key, bool value) => _store.SetValue(key, value);
    public void SetValue(string key, IEnumerable<string> value) => _store.SetValue(key, value);

    // IPianoSettings
    public string GetString(string key, string defaultValue = "") => _store.GetString(key, defaultValue);
    public int GetInt(string key, int defaultValue = 0) => _store.GetInt(key, defaultValue);

    private void SetDefaultValue(string key, string value)
    {
        if (Contains(key)) // Do not change the value if it already set
            return;
        SetValue(key, value);
    }

    public void Init(Song song, object songLock, SidePanel sidePanel, TopBar topBar)
    {
        _song = song;
        _sync = songLock;
        _guiSidePanel = sidePanel;
        _guiTopBar = topBar;
    }

    /// <summary>Where the settings of the songs and the folders are kept.</summary>
    public void SetLibrary(ISongLibrary? library) => _library = library;

    /// <summary>Returns true if the user wants to see the note names.</summary>
    public bool IsNoteNamesEnabled => _noteNamesEnabled;
    public bool DisplayCourtesyAccidentals => Notation.DisplayCourtesyAccidentals;
    public bool IsTutorPagesEnabled => _tutorPagesEnabled;
    public bool IsFollowThroughErrorsEnabled => _followThroughErrorsEnabled;
    public bool IsColoredNotesEnabled => _coloredNotes;

    public bool ShowNoteNames => _noteNamesEnabled;
    /// <summary>Returns true if the user wants to see color-coded notes.</summary>
    public bool ColoredNotes => _coloredNotes;
    /// <summary>Returns true if the user wants Follow Skill to ignore errors.</summary>
    public bool FollowThroughErrors => _followThroughErrorsEnabled;
    /// <summary>A trail after each note head showing how long the note sounds.</summary>
    public bool IsNoteDurationTrailEnabled => _noteDurationTrail;
    public bool AdvancedMode => _advancedMode;

    public void SetAdvancedMode(bool value) => _advancedMode = value;

    /// <summary>Saves whether the user wants to show the note names.</summary>
    public void SetNoteNamesEnabled(bool value)
    {
        _noteNamesEnabled = value;
        SetValue("Score/NoteNames", value);
    }

    public void SetColoredNotes(bool value)
    {
        _coloredNotes = value;
        SetValue("Score/ColoredNotes", value);
    }

    public void SetTutorPagesEnabled(bool value)
    {
        _tutorPagesEnabled = value;
        SetValue("Tutor/TutorPages", value);
        UpdateTutorPage();
    }

    public void SetCourtesyAccidentals(bool value)
    {
        Notation.DisplayCourtesyAccidentals = value;
        SetValue("Score/CourtesyAccidentals", value);
    }

    public void SetFollowThroughErrorsEnabled(bool value)
    {
        _followThroughErrorsEnabled = value;
        SetValue("Score/FollowThroughErrors", value);
    }

    public void SetNoteDurationTrail(bool value)
    {
        _noteDurationTrail = value;
        SetValue("Score/NoteDurationTrail", value);
    }

    public List<string> GetFluidSoundFontNames() => _fluidSoundFontNames;
    public void SetFluidSoundFontNames(IEnumerable<string> names) => _fluidSoundFontNames = names.ToList();
    public void SetFluidSoundFontNames(string names) => _fluidSoundFontNames = new List<string> { names };
    public void AddFluidSoundFontName(string sfName) => _fluidSoundFontNames.Add(sfName);
    public void RemoveFluidSoundFontName(string sfName) => _fluidSoundFontNames.RemoveAll(n => n == sfName);
    public void ClearFluidSoundFontNames() => _fluidSoundFontNames.Clear();
    public void SaveSoundFontSettings() => SetValue("FluidSynth/SoundFont", GetFluidSoundFontNames());

    /// <summary>Has a new sound font been entered that is not the same as the old sound font.</summary>
    public bool IsNewSoundFontEntered()
    {
        if (GetFluidSoundFontNames().Count == 0)
            return false;
        return !GetFluidSoundFontNames().SequenceEqual(_store.GetStringList("FluidSynth/SoundFont"));
    }

    public string SelectedLangauge()
    {
        string locale = Value("General/lang", "");
        if (locale.Length == 0)
        {
            locale = System.Globalization.CultureInfo.CurrentUICulture.Name; // QLocale::system().bcp47Name()
            int n = locale.IndexOf('-');
            if (n > 0)
                locale = locale[..n];
            if (locale.Length == 0)
                locale = "en";
        }
        return locale;
    }

    // returns either 'left' 'right' or 'both'
    private static string PartToHandString(WhichPart part)
    {
        if (part == WhichPart.Left)
            return "left";
        if (part == WhichPart.Right)
            return "right";
        return "both";
    }

    private void LoadSongSettings()
    {
        _songSettingsPath = GetCurrentSongLongFileName();
        _songSettings = _library != null && _songSettingsPath.Length > 0 ? _library.GetSongSettings(_songSettingsPath) : new SongSettings();
        _guiSidePanel.SetCurrentHand(_songSettings.Hand);
        _guiTopBar.SetSpeed(_songSettings.Speed);
        lock (_sync)
            Note.SetChannelHands(_songSettings.LeftChannel, _songSettings.RightChannel);
    }

    // Saves the hand and the speed of the song whose settings were loaded last, if they have changed
    private void SaveSongSettings()
    {
        if (_library == null || _songSettingsPath.Length == 0 || !File.Exists(_songSettingsPath))
            return;
        string hand;
        lock (_sync)
            hand = PartToHandString(_song.ActiveHand);
        int speed = _guiTopBar.GetSpeed();
        if (_songSettings.Valid && hand == _songSettings.Hand && speed == _songSettings.Speed)
            return;
        if (!_songSettings.Valid && hand == "both" && speed == 100)
            return; // still the defaults: nothing to keep
        _songSettings.Valid = true;
        _songSettings.Hand = hand;
        _songSettings.Speed = speed;
        _library.SetSongSettings(_songSettingsPath, _songSettings);
    }

    private void LoadBookSettings()
    {
        if (_library == null)
            return;
        ImportBookConfig(CurrentBookFolder());
        string lastSong = _library.LastSong(CurrentBookFolder());
        if (lastSong.Length > 0 && _currentSongName.Length == 0)
            _currentSongName = lastSong;
    }

    // Older versions kept the settings of the songs of each folder in a "pb.cfg" file
    // in the folder: move them to the library and delete the file.
    private void ImportBookConfig(string folder)
    {
        string fileName = folder + "/pb.cfg";
        if (!File.Exists(fileName) || _library == null)
            return;

        _library.BeginBatch();
        try
        {
            using var reader = XmlReader.Create(fileName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.Name == "book")
                {
                    string lastSong = reader.GetAttribute("lastsong") ?? "";
                    if (lastSong.Length > 0 && _library.LastSong(folder).Length == 0)
                        _library.SetLastSong(folder, lastSong);
                }
                else if (reader.Name == "song")
                {
                    string name = reader.GetAttribute("name") ?? "";
                    string path = folder + '/' + name;
                    // the settings already in the library, maybe of a copy of the song, are kept
                    if (name.Length > 0 && File.Exists(path) && !_library.GetSongSettings(path).Valid)
                    {
                        var settings = new SongSettings { Valid = true };
                        if (reader.GetAttribute("hand") is string hand)
                            settings.Hand = hand;
                        if (int.TryParse(reader.GetAttribute("speed"), out int speed))
                            settings.Speed = speed;
                        if (int.TryParse(reader.GetAttribute("leftHandMidiChannel"), out int left))
                            settings.LeftChannel = left;
                        if (int.TryParse(reader.GetAttribute("rightHandMidiChannel"), out int right))
                            settings.RightChannel = right;
                        _library.SetSongSettings(path, settings);
                    }
                }
            }
        }
        catch (XmlException)
        {
            // QXmlStreamReader stops at the error; what has been read is kept
        }
        finally
        {
            _library.EndBatch();
        }
        try
        {
            File.Delete(fileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Cannot delete {fileName}");
        }
    }

    public void UpdateTutorPage()
    {
        string songFile = GetCurrentSongLongFileName();
        const string extn = ".html";

        if (_tutorPagesEnabled && songFile.Length > 0)
        {
            string locale = SelectedLangauge();
            string absolutePath = Path.GetDirectoryName(Path.GetFullPath(songFile))!.Replace('\\', '/');
            string completeBaseName = CompleteBaseName(songFile);

            string tutorFile = absolutePath + "/InfoPages/" + locale + "/" + completeBaseName + extn;
            if (File.Exists(tutorFile))
            {
                _mainWindow.LoadTutorHtml(tutorFile);
                return;
            }

            tutorFile = absolutePath + "/InfoPages/en/" + completeBaseName + extn;
            if (File.Exists(tutorFile))
            {
                _mainWindow.LoadTutorHtml(tutorFile);
                return;
            }

            // The previous location of the tutor pages English only
            tutorFile = absolutePath + "/InfoPages/" + completeBaseName + "_en" + extn;
            if (File.Exists(tutorFile))
            {
                _mainWindow.LoadTutorHtml(tutorFile);
                return;
            }
        }
        _mainWindow.LoadTutorHtml("");
    }

    // QFileInfo::completeBaseName(): the name without the last extension
    private static string CompleteBaseName(string path)
    {
        string name = Path.GetFileName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    public void OpenSongFile(string filename)
    {
        if (!File.Exists(filename))
        {
            Log.Error($"File does not exists {filename}");
            return;
        }
        // QDir(filename): the name of the song, its folder (the book) and the folder of the books
        string full = Path.GetFullPath(filename).Replace('\\', '/');
        string currentSongName = Path.GetFileName(full);
        string bookFolder = Path.GetDirectoryName(full)!.Replace('\\', '/');
        _currentBookName = Path.GetFileName(bookFolder);
        string? booksFolder = Path.GetDirectoryName(bookFolder)?.Replace('\\', '/');
        _bookPath = (booksFolder ?? bookFolder).TrimEnd('/') + '/';

        _currentSongName = currentSongName;
        _guiSidePanel.LoadBookList();
        UpdateWarningMessages();
    }

    public string GetCurrentSongName() => _currentSongName;
    public string GetCurrentBookName() => _currentBookName;
    private string CurrentBookFolder() => _bookPath + GetCurrentBookName();

    public string GetCurrentSongLongFileName()
    {
        if (GetCurrentSongName().Length == 0)
            return "";
        return _bookPath + GetCurrentBookName() + '/' + GetCurrentSongName();
    }

    public void SetActiveHand(WhichPart hand)
    {
        lock (_sync)
            _song.SetActiveHand(hand);
    }

    private static bool IsMidiFileName(string name) =>
        Program.MidiFileExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public List<string> GetSongList()
    {
        var songNames = new List<string>();
        string folder = _bookPath + GetCurrentBookName();
        if (!Directory.Exists(folder))
            return songNames;
        // QDir::entryList() sorts the names (case-insensitively on Windows)
        foreach (string file in SortedNames(Directory.GetFiles(folder)))
            if (IsMidiFileName(file))
                songNames.Add(file);
        return songNames;
    }

    public List<string> GetBookList()
    {
        if (_bookPath.Length == 0 || !Directory.Exists(_bookPath))
            return new List<string>();
        return SortedNames(Directory.GetDirectories(_bookPath));
    }

    private static List<string> SortedNames(IEnumerable<string> paths) =>
        paths.Select(Path.GetFileName).OfType<string>()
             .OrderBy(n => n, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
             .ToList();

    public void WriteSettings()
    {
        if (File.Exists(GetCurrentSongLongFileName()))
            SetValue("CurrentSong", GetCurrentSongLongFileName());
        SaveSongSettings();
        _store.Save();
    }

    public void LoadSettings()
    {
        UnzipBoosterMusicBooks();
        // Set default values
        SetValue("PianoBooster/Version", Program.Version);
        SetDefaultValue("ShortCuts/LeftHand", "F2");
        SetDefaultValue("ShortCuts/BothHands", "F3");
        SetDefaultValue("ShortCuts/RightHand", "F4");
        SetDefaultValue("ShortCuts/PlayFromStart", Strings.Tr("space"));
        SetDefaultValue("ShortCuts/PlayPause", "P");
        SetDefaultValue("ShortCuts/Faster", "=");
        SetDefaultValue("ShortCuts/Slower", "-");
        SetDefaultValue("ShortCuts/NextSong", "]");
        SetDefaultValue("ShortCuts/PreviousSong", "[");
        SetDefaultValue("ShortCuts/NextBook", "{");
        SetDefaultValue("ShortCuts/PreviousBook", "}");

        SetupDefaultSoundFont();

        UpdateWarningMessages();
        UpdateTutorPage();
    }

    /// <summary>The folder where the Booster Music course is unzipped.</summary>
    public static string BoosterMusicBooksFolder()
    {
        string musicDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).Replace('\\', '/') + "/My Music"
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/Music";
        return musicDir + "/BoosterMusicBooks" + MusicRelease;
    }

    public void UnzipBoosterMusicBooks()
    {
        const string zipFileName = "BoosterMusicBooks.zip";

        if (Value("PianoBooster/MusicRelease", 0) >= MusicRelease)
            return;

        string musicSrcDir = AppContext.BaseDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        Log.Debug($"unzipBoosterMusicBooks resourceDir1 {musicSrcDir}");
        if (!File.Exists(musicSrcDir + zipFileName))
        {
            if (OperatingSystem.IsMacOS())
                musicSrcDir = AppContext.BaseDirectory + "/../Resources/music/";
            else if (!OperatingSystem.IsWindows())
                musicSrcDir = "/usr/share/games/pianobooster/music/"; // Util::dataDir()
        }
        string zipFile = musicSrcDir + zipFileName;

        string musicDirName = OperatingSystem.IsWindows() ? "My Music" : "Music";
        string destMusicDir = (OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Replace('\\', '/') + "/" + musicDirName;

        if (!OperatingSystem.IsWindows())
        {
            // on windows the installer does the unzipping. The C++ runs "unzip -o"; here the zip is
            // read with System.IO.Compression.
            if (!File.Exists(zipFile))
            {
                Log.Error("Cannot find " + zipFileName);
                return;
            }
            try
            {
                Directory.CreateDirectory(destMusicDir);
                ZipFile.ExtractToDirectory(zipFile, destMusicDir, overwriteFiles: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Error("unzip failed: " + e.Message);
                return;
            }
        }
        string fileName = destMusicDir + "/BoosterMusicBooks" + MusicRelease + "/Beginner Course/01-StartWithMiddleC.mid";
        OpenSongFile(fileName);
        _mainWindow.SetCurrentFile(fileName);
        SetValue("PianoBooster/MusicRelease", MusicRelease);
    }

    public void SetCurrentSongName(string name)
    {
        if (name.Length == 0)
            return;
        SaveSongSettings();
        _currentSongName = name;
        SetValue("CurrentSong", GetCurrentSongLongFileName());
        _library?.SetLastSong(CurrentBookFolder(), _currentSongName);

        lock (_sync)
            _song.LoadSong(GetCurrentSongLongFileName());
        LoadSongSettings();

        _guiSidePanel.Refresh();
        _guiTopBar.Refresh(true);
        _mainWindow.SongLoaded(GetCurrentSongLongFileName()); // title, song details and library
        UpdateTutorPage();
    }

    public void SetCurrentBookName(string name, bool clearSongName)
    {
        if (name.Length == 0)
            return;
        SaveSongSettings();

        _currentBookName = name;
        if (clearSongName)
            _currentSongName = "";
        LoadBookSettings();
    }

    /// <summary>Called by the track list (with the lock of the song held).</summary>
    public void SetChannelHands(int left, int right)
    {
        Note.SetChannelHands(left, right);
        if (_library != null && _songSettingsPath.Length > 0 && File.Exists(_songSettingsPath))
        {
            _songSettings.Valid = true;
            _songSettings.Hand = PartToHandString(_song.ActiveHand);
            _songSettings.Speed = _guiTopBar.GetSpeed();
            _songSettings.LeftChannel = left;
            _songSettings.RightChannel = right;
            _library.SetSongSettings(_songSettingsPath, _songSettings);
        }
        _guiSidePanel.Refresh();
    }

    public string GetWarningMessage() => _warningMessage;

    public void UpdateWarningMessages()
    {
        bool validOutput;
        lock (_sync)
            validOutput = _song.ValidMidiOutput();
        if (!validOutput)
            _warningMessage = Strings.Tr("ERROR NO SOUND: To fix this use menu Setup/MIDI Setup ...");
        else if (_currentSongName.Length == 0)
            _warningMessage = Strings.Tr("ERROR NO MIDI FILE: To fix this use menu File/Open ...");
        else
            _warningMessage = "";
    }

    private void SetupDefaultSoundFont()
    {
        if (!MidiDeviceFluidSynth.IsLibraryAvailable)
            return; // WITH_INTERNAL_FLUIDSYNTH

        bool validOutput;
        lock (_sync)
            validOutput = _song.ValidMidiOutput();
        if (GetFluidSoundFontNames().Count != 0 || validOutput || Contains("LastSoundFontDir"))
            return;

        string? appPath = Environment.GetEnvironmentVariable("APPIMAGE");
        appPath = !string.IsNullOrEmpty(appPath) ? Path.GetDirectoryName(appPath) : AppContext.BaseDirectory;

        string? defaultSoundFont = null;
        string directory = Path.Combine(appPath ?? "", "soundfont");
        if (Directory.Exists(directory))
        {
            // Find the first sound font file
            defaultSoundFont = SortedNames(Directory.GetFileSystemEntries(directory))
                .Where(f => f.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.Combine(directory, f))
                .FirstOrDefault();
        }

        if (defaultSoundFont == null && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            foreach (string soundFontFolder in new[] { "/usr/share/soundfonts", "/usr/share/sounds/sf2" })
            {
                // fluid-soundfont-gm Fluid (R3) General MIDI SoundFont (GM) package
                string found = Path.Combine(soundFontFolder, "FluidR3_GM.sf2");
                if (File.Exists(found))
                {
                    defaultSoundFont = found;
                    break;
                }
            }
        }

        if (defaultSoundFont != null && File.Exists(defaultSoundFont))
        {
            string path = defaultSoundFont.Replace('\\', '/');
            SetFluidSoundFontNames(path);
            SetValue("Midi/Output", MidiDeviceFluidSynth.GetFluidInternalName());
            SetValue("LastSoundFontDir", Path.GetDirectoryName(path)!.Replace('\\', '/'));
            SaveSoundFontSettings();
            lock (_sync)
                _song.OpenMidiPort(MidiType.Output, MidiDeviceFluidSynth.GetFluidInternalName());
        }
    }
}
