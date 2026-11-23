// Ported from GuiPreferencesDialog.h and GuiPreferencesDialog.cpp of PianoBooster (C++): the score
// settings (note names, courtesy accidentals, timing markers, follow through errors, colored notes,
// follow stop point, note display), the tutor pages and the language. GPLv3.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class PreferencesDialog : Window
{
    private Settings _settings = null!;
    private Song _song = null!;
    private object _sync = null!;      // the lock of the Song (ConductorThread.SyncRoot)
    private ScoreView _glView = null!; // m_glView: kept as in the C++, which does not use it either

    public PreferencesDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this); // retranslateUi() of the .ui file
        Title = Strings.Tr("Preferences");
        followStopPointCombo.Items.Add(Strings.Tr("Automatic (Recommended)"));
        followStopPointCombo.Items.Add(Strings.Tr("On the Beat"));
        followStopPointCombo.Items.Add(Strings.Tr("After the Beat"));
        noteDisplayCombo.Items.Add(Strings.Tr("Note heads only"));
        noteDisplayCombo.Items.Add(Strings.Tr("Note heads with duration trail"));
        AdjustToContents(followStopPointCombo);
        AdjustToContents(noteDisplayCombo);
    }

    /// <summary>QComboBox::AdjustToContents: as wide as the longest item, whichever is chosen.</summary>
    private void AdjustToContents(ComboBox combo)
    {
        double width = 0;
        foreach (object? item in combo.Items)
        {
            var text = new TextBlock { Text = item?.ToString(), FontSize = combo.FontSize, FontFamily = combo.FontFamily };
            text.Measure(Size.Infinity);
            width = Math.Max(width, text.DesiredSize.Width);
        }
        combo.MinWidth = width + 48; // the padding and the arrow of the combo
    }

    /// <summary>The folder with langs.json and the translations (the localeDirectory of the C++).</summary>
    private static string LocaleDirectory()
    {
        string localeDirectory = Path.Combine(AppContext.BaseDirectory, "translations") + "/";
        if (!Directory.Exists(localeDirectory))
        {
            localeDirectory = "/usr/share/games/pianobooster/translations/"; // Util::dataDir()
            if (OperatingSystem.IsMacOS())
                localeDirectory = Path.Combine(AppContext.BaseDirectory, "../Resources/translations/");
        }
        return localeDirectory;
    }

    private void AddLanguage(string name, string code) =>
        languageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });

    private void InitLanguageCombo()
    {
        string localeDirectory = LocaleDirectory();

        // read langs.json
        JsonElement rootLangs;
        try
        {
            string val = File.ReadAllText(localeDirectory + "/langs.json");
            using JsonDocument document = JsonDocument.Parse(val);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Log.Error("langs.json is not valid");
                return;
            }
            rootLangs = document.RootElement.Clone();
        }
        catch (IOException)
        {
            Log.Error("Error while opening langs.json");
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Log.Error("Error while opening langs.json");
            return;
        }
        catch (JsonException)
        {
            Log.Error("langs.json is not valid");
            return;
        }

        string savedLang = _settings.Value("General/lang", "");

        // loading languages
        languageCombo.Items.Clear();
        AddLanguage("<" + Strings.Tr("System Language") + ">", "");
        languageCombo.SelectedIndex = 0;
        AddLanguage("English", "en");
        if (savedLang == "en")
            languageCombo.SelectedIndex = languageCombo.Items.Count - 1;

        // the pianobooster_<code>.qm files in the C++ code; here the satellite assemblies of the
        // translations (<culture>/PianoBoosterCS.resources.dll), sorted by name
        foreach (string langCode in Strings.InstalledLanguages())
        {
            AddLanguage(LanguageName(rootLangs, langCode), langCode);
            if (savedLang == langCode)
                languageCombo.SelectedIndex = languageCombo.Items.Count - 1;
        }

        // A saved language that is not installed is kept (in the C++ the combo shows "<System Language>"
        // and OK would forget it).
        if (savedLang.Length > 0 && !languageCombo.Items.OfType<ComboBoxItem>().Any(i => (string?)i.Tag == savedLang))
        {
            AddLanguage(LanguageName(rootLangs, savedLang), savedLang);
            languageCombo.SelectedIndex = languageCombo.Items.Count - 1;
        }
    }

    /// <summary>The name of a language: the "nativeName" of langs.json, or else the one of the locale.</summary>
    private static string LanguageName(JsonElement rootLangs, string langCode)
    {
        string languageName = "";
        if (rootLangs.TryGetProperty(langCode, out JsonElement lang) && lang.ValueKind == JsonValueKind.Object &&
            lang.TryGetProperty("nativeName", out JsonElement nativeName) && nativeName.ValueKind == JsonValueKind.String)
            languageName = nativeName.GetString() ?? "";

        if (languageName.Length == 0)
            languageName = CultureLanguageName(langCode);

        if (languageName.Length > 0)
            languageName = char.ToUpper(languageName[0]) + languageName[1..];

        if (languageName.Length == 0 || languageName == "C")
            languageName = langCode;
        return languageName;
    }

    /// <summary>QLocale(code).nativeLanguageName(), or else QLocale::languageToString(): "" if unknown.</summary>
    private static string CultureLanguageName(string langCode)
    {
        try
        {
            CultureInfo loc = CultureInfo.GetCultureInfo(langCode.Replace('_', '-'));
            if (!loc.IsNeutralCulture && loc.Parent != CultureInfo.InvariantCulture)
                loc = loc.Parent; // the language without the country
            string name = loc.NativeName;
            if (name.Length == 0)
                name = loc.EnglishName;
            // .NET makes up a culture for an unknown code: its name is the code itself
            return name.Equals(langCode, StringComparison.OrdinalIgnoreCase) ? "" : name;
        }
        catch (CultureNotFoundException)
        {
            return "";
        }
    }

    public void Init(Song song, object songLock, Settings settings, ScoreView glView)
    {
        _song = song;
        _sync = songLock;
        _settings = settings;
        _glView = glView;

        lock (_sync)
        {
            timingMarkersCheck.IsChecked = _song.CfgTimingMarkersFlag;
            followStopPointCombo.SelectedIndex = (int)_song.CfgStopPointMode;
        }
        showNoteNamesCheck.IsChecked = _settings.IsNoteNamesEnabled;
        courtesyAccidentalsCheck.IsChecked = _settings.DisplayCourtesyAccidentals;
        showTutorPagesCheck.IsChecked = _settings.IsTutorPagesEnabled;
        followThroughErrorsCheck.IsChecked = _settings.IsFollowThroughErrorsEnabled;
        showColoredNotesCheck.IsChecked = _settings.IsColoredNotesEnabled;

        noteDisplayCombo.SelectedIndex = _settings.IsNoteDurationTrailEnabled ? 1 : 0;

        InitLanguageCombo();
    }

    private void OnOkButtonClicked(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancelButtonClicked(object? sender, RoutedEventArgs e) => Close(false); // QDialog::reject()

    private void Accept()
    {
        bool timingMarkers = timingMarkersCheck.IsChecked == true;
        var stopPointMode = (StopPointMode)followStopPointCombo.SelectedIndex;
        lock (_sync)
            _song.CfgTimingMarkersFlag = timingMarkers;
        _settings.SetValue("Score/TimingMarkers", timingMarkers);
        _settings.SetNoteNamesEnabled(showNoteNamesCheck.IsChecked == true);
        _settings.SetCourtesyAccidentals(courtesyAccidentalsCheck.IsChecked == true);
        _settings.SetTutorPagesEnabled(showTutorPagesCheck.IsChecked == true);
        _settings.SetFollowThroughErrorsEnabled(followThroughErrorsCheck.IsChecked == true);
        _settings.SetColoredNotes(showColoredNotesCheck.IsChecked == true);
        lock (_sync)
            _song.CfgStopPointMode = stopPointMode;
        _settings.SetValue("Score/StopPointMode", (int)stopPointMode);
        _settings.SetNoteDurationTrail(noteDisplayCombo.SelectedIndex == 1);

        // currentData().toString(): "" if the combo is empty (no langs.json)
        _settings.SetValue("General/lang", (languageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "");

        lock (_sync)
            _song.RefreshScroll();

        Close(true); // QDialog::accept()
    }
}
