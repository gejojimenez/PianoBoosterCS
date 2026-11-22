// Ported from GuiMidiSetupDialog.h and GuiMidiSetupDialog.cpp of PianoBooster (C++): the choice
// of the MIDI input and output, the internal synthesizer (FluidSynth: SoundFont and its settings)
// and the latency fix. GPLv3.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PianoBooster.Core;
using PianoBooster.Midi;

namespace PianoBooster.App;

public partial class MidiSetupDialog : Window
{
    private Settings _settings = null!;
    private Song _song = null!;
    private object _sync = null!;           // the lock of the Song (ConductorThread.SyncRoot)
    private MidiDevice _midiDevice = null!; // m_song->getMidiPortList(): the device of the Song
    private int _latencyFix;
    private bool _latencyChanged;
    private int _updating;                  // QSignalBlocker: changes made by the code are not "activated"

    public MidiSetupDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this); // retranslateUi() of the .ui file
        _latencyFix = 0;
        _latencyChanged = false;
        midiSetupTabWidget.SelectedIndex = 0;

        // the C++ removes the tab when built without WITH_INTERNAL_FLUIDSYNTH; here, without the library
        if (!MidiDeviceFluidSynth.IsLibraryAvailable)
            midiSetupTabWidget.Items.Remove(tab_2);

        Title = Strings.Tr("MIDI Setup");

        // QIntValidator of the editable combo boxes
        AddDigitsOnly(sampleRateCombo, 96000);
        AddDigitsOnly(bufferSizeCombo, 8192);
        AddDigitsOnly(bufferCountCombo, 64);
    }

    public void Init(Song song, object songLock, Settings settings, MidiDevice midiDevice)
    {
        _song = song;
        _sync = songLock;
        _settings = settings;
        _midiDevice = midiDevice;

        _updating++;
        try
        {
            lock (_sync)
                _latencyFix = _song.LatencyFix;

            RefreshMidiInputCombo();
            RefreshMidiOutputCombo();
            masterGainSpin.Value = MidiDeviceFluidSynth.FluidDefaultGain;
            reverbCheck.IsChecked = false;
            chorusCheck.IsChecked = false;

            AddItems(sampleRateCombo, "22050", "44100", "48000", "88200", "96000");
            AddItems(bufferSizeCombo, "64", "128", "512", "1024", "2024", "4096", "8192");
            SetCurrentIndex(bufferSizeCombo, 1);
            AddItems(bufferCountCombo, "2", "4", "8", "16", "32", "64");
            SetCurrentIndex(bufferCountCombo, 1);

            UpdateMidiInfoText();

            audioDriverCombo.Items.Clear();
            if (OperatingSystem.IsLinux())
                AddItems(audioDriverCombo, "pulseaudio", "alsa");
            else if (OperatingSystem.IsMacOS())
                AddItems(audioDriverCombo, "coreaudio", "portaudio");
            else if (!OperatingSystem.IsWindows()) // Q_OS_UNIX
                AddItems(audioDriverCombo, "pulseaudio");

            if (_settings.GetFluidSoundFontNames().Count > 0)
            {
                masterGainSpin.Value = _settings.Value("FluidSynth/masterGainSpin", 40);
                reverbCheck.IsChecked = _settings.Value("FluidSynth/reverbCheck", false);
                chorusCheck.IsChecked = _settings.Value("FluidSynth/chorusCheck", false);
                SetComboFromSetting(audioDriverCombo, "FluidSynth/audioDriverCombo", "pulseaudio");
                SetComboFromSetting(sampleRateCombo, "FluidSynth/sampleRateCombo", "22050");
                SetComboFromSetting(bufferSizeCombo, "FluidSynth/bufferSizeCombo", "128");
                SetComboFromSetting(bufferCountCombo, "FluidSynth/bufferCountCombo", "4");
            }

            UpdateFluidInfoStatus();
        }
        finally
        {
            _updating--;
        }
    }

    // ---- QComboBox helpers ----

    private static void AddItems(ComboBox combo, params string[] items)
    {
        foreach (string item in items)
            combo.Items.Add(item);
    }

    private static int FindText(ComboBox combo, string text)
    {
        for (int i = 0; i < combo.ItemCount; i++)
            if (combo.Items[i] as string == text)
                return i;
        return -1;
    }

    /// <summary>QComboBox::currentText(): the edited text of an editable combo box, if not the current item.</summary>
    private static string CurrentText(ComboBox combo) =>
        combo.IsEditable ? combo.Text ?? "" : combo.SelectedItem as string ?? "";

    private static void SetCurrentIndex(ComboBox combo, int index)
    {
        combo.SelectedIndex = index;
        if (combo.IsEditable)
            combo.Text = index >= 0 && index < combo.ItemCount ? combo.Items[index] as string : "";
    }

    private void SetComboFromSetting(ComboBox combo, string key, string defaultValue)
    {
        string value = _settings.Value(key, defaultValue);
        int index = FindText(combo, value);

        if (index != -1) // -1 for not found
            SetCurrentIndex(combo, index);
        else if (combo.IsEditable) // setCurrentText() does nothing on a non-editable combo without that item
        {
            combo.SelectedIndex = -1;
            combo.Text = value;
        }
    }

    /// <summary>Like QIntValidator(bottom, top): only digits, and never more than top.</summary>
    private static void AddDigitsOnly(ComboBox combo, int top)
    {
        combo.AddHandler(TextInputEvent, (_, e) =>
        {
            if (e.Text == null)
                return;
            if (!e.Text.All(char.IsAsciiDigit))
            {
                e.Handled = true;
                return;
            }
            if (e.Source is TextBox box)
            {
                string text = box.Text ?? "";
                int start = Math.Min(box.SelectionStart, box.SelectionEnd);
                int length = Math.Abs(box.SelectionEnd - box.SelectionStart);
                start = Math.Clamp(start, 0, text.Length);
                string result = text.Remove(start, Math.Min(length, text.Length - start)).Insert(start, e.Text);
                if (!long.TryParse(result, out long n) || n > top)
                    e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    // ---- the MIDI devices ----

    private void RefreshMidiInputCombo()
    {
        int i;
        _updating++;
        midiInputCombo.Items.Clear();
        midiInputCombo.Items.Add(Strings.Tr("None (PC Keyboard)"));
        lock (_sync)
            foreach (string port in _midiDevice.GetMidiPortList(MidiType.Input))
                midiInputCombo.Items.Add(port);
        midiInputCombo.SelectedIndex = 0;
        i = FindText(midiInputCombo, _settings.Value("Midi/Input"));
        if (i != -1)
            midiInputCombo.SelectedIndex = i;
        _updating--;
    }

    private void RefreshMidiOutputCombo()
    {
        int i;
        _updating++;
        // Check outputs.
        midiOutputCombo.Items.Clear();
        midiOutputCombo.Items.Add(Strings.Translate("GuiMidiSetupDialog", "None"));
        lock (_sync)
            foreach (string port in _midiDevice.GetMidiPortList(MidiType.Output))
                midiOutputCombo.Items.Add(port);
        midiOutputCombo.SelectedIndex = 0;
        i = FindText(midiOutputCombo, _settings.Value("Midi/Output"));
        if (i != -1)
            midiOutputCombo.SelectedIndex = i;
        _updating--;
    }

    private static readonly IBrush Black = Brushes.Black;
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF6600"));
    private static readonly IBrush Gray = Brushes.Gray;
    private static readonly IBrush Red = Brushes.Red;

    /// <summary>QTextEdit::append("&lt;span style=color:...&gt;text&lt;/span&gt;"): a new paragraph.</summary>
    private void AppendInfo(IBrush color, string text)
    {
        InlineCollection inlines = midiInfoText.Inlines ??= new InlineCollection();
        if (inlines.Count > 0)
            inlines.Add(new LineBreak());
        inlines.Add(new Run(text) { Foreground = color });
    }

    private void UpdateMidiInfoText()
    {
        midiInfoText.Inlines?.Clear();
        string input = midiInputCombo.SelectedItem as string ?? "";
        string output = midiOutputCombo.SelectedItem as string ?? "";

        if (midiInputCombo.SelectedIndex == 0)
            AppendInfo(Black, Strings.Tr("If you don't have a MIDI keyboard you can use the PC keyboard; 'X' is middle C."));
        else if (input.Contains("Midi Through", StringComparison.OrdinalIgnoreCase))
            AppendInfo(Orange, Strings.Tr("The use of MIDI Through is not recommended!"));
        else
            AppendInfo(Gray, Strings.Tr("MIDI Input Device:") + " " + input);

        if (output == Strings.Translate("GuiMidiSetupDialog", "None"))
            AppendInfo(Red, Strings.Tr("No Sound Output Device selected; Choose a MIDI Output Device"));
        else if (output.Contains("MIDI Through", StringComparison.OrdinalIgnoreCase))
            AppendInfo(Orange, Strings.Tr("The use of MIDI Through is not recommended!"));
        else if (output.Contains("Microsoft GS Wavetable", StringComparison.OrdinalIgnoreCase))
            // the "\n" of the C++ is inside HTML: Qt shows it as a space
            AppendInfo(Orange, Strings.Tr("Note: the Microsoft GS Wavetable Synth introduces an unwanted delay!") + " "
                               + Strings.Tr("(Try a latency fix of 150msc)"));
        else
            AppendInfo(Gray, Strings.Tr("MIDI Output Device:") + " " + output);

        latencyFixLabel.Text = Strings.Tr("%1 mSec", _latencyFix);

        UpdateFluidInfoStatus();
    }

    private void OnMidiInputComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || _settings == null)
            return;
        UpdateMidiInfoText();
    }

    private void OnMidiOutputComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || _settings == null)
            return;
        UpdateMidiInfoText();
    }

    // ---- the latency fix ----

    private async void OnLatencyFixButtonClicked(object? sender, RoutedEventArgs e)
    {
        int? latencyFix = await MessageDialog.GetInt(this, Strings.Tr("Enter a value for the latency fix in milliseconds"),
            Strings.Tr(
            "The latency fix works by running the music ahead of what you<br>" +
            "are playing to counteract the delay within the sound generator.<br><br>" +
            "You will need a piano <b>with speakers</b> that are <b>turned up</b>.<br><br>" +
            "Enter the time in milliseconds for the delay (1000 mSec = 1 sec)<br>" +
            "(For the Microsoft GS Wavetable SW Synth try a value of 150)<br>" +
            "If you are not sure enter a value of zero."),
            _latencyFix, 0, 1000, 50);
        if (latencyFix != null)
        {
            _latencyFix = latencyFix.Value;
            _latencyChanged = true;
            UpdateMidiInfoText();
        }
    }

    // ---- accept() / reject() ----

    private void OnOkButtonClicked(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancelButtonClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void Accept()
    {
        // save FluidSynth settings
        if (_settings.GetFluidSoundFontNames().Count == 0)
        {
            _settings.Remove("FluidSynth");
        }
        else
        {
            _settings.SetValue("FluidSynth/masterGainSpin", (int)(masterGainSpin.Value ?? 0));
            _settings.SetValue("FluidSynth/bufferSizeCombo", CurrentText(bufferSizeCombo));
            _settings.SetValue("FluidSynth/bufferCountCombo", CurrentText(bufferCountCombo));
            _settings.SetValue("FluidSynth/reverbCheck", reverbCheck.IsChecked == true);
            _settings.SetValue("FluidSynth/chorusCheck", chorusCheck.IsChecked == true);
            _settings.SetValue("FluidSynth/audioDriverCombo", CurrentText(audioDriverCombo));
            _settings.SetValue("FluidSynth/sampleRateCombo", CurrentText(sampleRateCombo));
        }

        _settings.SaveSoundFontSettings();

        string input = midiInputCombo.SelectedItem as string ?? "";
        _settings.SetValue("Midi/Input", input);
        lock (_sync)
        {
            _song.OpenMidiPort(MidiType.Input, input);
            if (input.StartsWith(Strings.Translate("GuiMidiSetupDialog", "None"), StringComparison.Ordinal))
                Chord.SetPianoRange(Song.PcKeyLowestNote, Song.PcKeyHighestNote);
            else
                Chord.SetPianoRange(_settings.Value("Keyboard/LowestNote", 0),
                                    _settings.Value("Keyboard/HighestNote", 127));

            if (midiOutputCombo.SelectedIndex == 0)
            {
                _settings.SetValue("Midi/Output", "");
                _song.OpenMidiPort(MidiType.Output, "");
            }
            else
            {
                string output = midiOutputCombo.SelectedItem as string ?? "";
                _settings.SetValue("Midi/Output", output);
                _song.OpenMidiPort(MidiType.Output, output);
            }
        }
        _settings.UpdateWarningMessages();

        _settings.SetValue("Midi/Latency", _latencyFix);
        lock (_sync)
        {
            _song.SetLatencyFix(_latencyFix);
            _song.RegenerateChordQueue();
        }
        if (_latencyChanged)
        {
            if (_latencyFix > 0)
            {
                int rightSound = _settings.Value("Keyboard/RightSound", Cfg.DefaultRightPatch);
                _settings.SetValue("Keyboard/RightSoundPrevious", rightSound); // Save the current right sound
                // Mute the Piano if we are using the latency fix;
                _settings.SetValue("Keyboard/RightSound", 0);
                lock (_sync)
                    _song.SetPianoSoundPatches(-1, -2); // -1 means no sound and -2 means ignore this parameter
            }
            else
            {
                int previousRightSound = _settings.Value("Keyboard/RightSoundPrevious", Cfg.DefaultRightPatch);
                _settings.SetValue("Keyboard/RightSound", previousRightSound);
                // as in the C++, without the "- 1" of QtWindow::init() (see PORTING.md §11.5)
                lock (_sync)
                    _song.SetPianoSoundPatches(previousRightSound, -2); // -2 means ignore this parameter
            }
            _latencyChanged = false;
        }

        Close(true); // QDialog::accept()
    }

    // ---- FluidSynth ----

    private void UpdateFluidInfoStatus()
    {
        List<string> soundFontNames = _settings.GetFluidSoundFontNames();
        soundFontList.Items.Clear();
        if (soundFontNames.Count > 0)
        {
            var fileInfo = new FileInfo(soundFontNames[0]);
            if (fileInfo.Exists)
                soundFontList.Items.Add(fileInfo.Name);
        }

        bool fontLoaded = soundFontList.ItemCount > 0;
        fluidClearButton.IsEnabled = fontLoaded;

        fluidLoadButton.IsEnabled = soundFontList.ItemCount < 2;

        fluidSettingsGroupBox.IsEnabled = fontLoaded;
    }

    private async void OnFluidLoadButtonClicked(object? sender, RoutedEventArgs e)
    {
        string lastSoundFont = _settings.Value("LastSoundFontDir", "");

        if (lastSoundFont.Length == 0)
        {
            lastSoundFont = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var possibleSoundFontFolders = new List<string>();
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) // Q_OS_LINUX || Q_OS_UNIX
            {
                possibleSoundFontFolders.Add("/usr/share/soundfonts");
                possibleSoundFontFolders.Add("/usr/share/sounds/sf2");
            }
            foreach (string soundFontFolder in possibleSoundFontFolders)
            {
                if (Directory.Exists(soundFontFolder))
                {
                    lastSoundFont = soundFontFolder;
                    break;
                }
            }
        }

        IStorageFolder? start = await StorageProvider.TryGetFolderFromPathAsync(lastSoundFont);
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Tr("Open SoundFont File for fluidsynth"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Strings.Tr("SoundFont Files")) { Patterns = new[] { "*.sf2", "*.sf3" } },
            },
        });
        string? soundFontFile = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(soundFontFile))
            return;

        LoadSoundFont(soundFontFile);
    }

    /// <summary>The part of on_fluidLoadButton_clicked() after the file dialog.</summary>
    private void LoadSoundFont(string soundFontFile)
    {
        // QFileInfo::filePath() and path(): with '/', as the C++ stores them
        string filePath = soundFontFile.Replace('\\', '/');
        string path = (Path.GetDirectoryName(soundFontFile) ?? "").Replace('\\', '/');
        _settings.SetFluidSoundFontNames(filePath);
        _settings.SetValue("LastSoundFontDir", path);

        if (_settings.IsNewSoundFontEntered())
        {
            _updating++;
            int i = FindText(midiOutputCombo, MidiDeviceFluidSynth.GetFluidInternalName());
            if (i == -1)
                midiOutputCombo.Items.Add(MidiDeviceFluidSynth.GetFluidInternalName());
            i = FindText(midiOutputCombo, MidiDeviceFluidSynth.GetFluidInternalName());
            if (i != -1)
                midiOutputCombo.SelectedIndex = i;
            _updating--;
        }
        UpdateFluidInfoStatus();
        UpdateMidiInfoText();
    }

    private void OnFluidClearButtonClicked(object? sender, RoutedEventArgs e)
    {
        _settings.ClearFluidSoundFontNames();
        int i = FindText(midiOutputCombo, MidiDeviceFluidSynth.GetFluidInternalName());
        if (i >= 0)
        {
            _updating++;
            midiOutputCombo.Items.RemoveAt(i);
            midiOutputCombo.SelectedIndex = 0;
            _updating--;
        }
        UpdateFluidInfoStatus();
    }
}
