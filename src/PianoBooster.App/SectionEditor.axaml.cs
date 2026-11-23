// Ported from GuiSectionEditor.h and GuiSectionEditor.cpp of PianoBooster (C++). GPLv3.
//
// The section editor: a window where the named sections of a song are created and changed on a
// piano roll of the whole song, which can be scrolled to any place without playing it, and where
// they can be heard (SectionPlayer). The song must be the one loaded in the practice window,
// whose conductor must be stopped while the editor is open, because the editor plays through the
// same device.
//
// The message boxes of Qt are modal and synchronous; here they are awaited, so the methods that
// ask (resolveChanges and those that call it) are async.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PianoBooster.Core;
using PianoBooster.Midi;
using PianoBooster.Rendering;

namespace PianoBooster.App;

public partial class SectionEditor : Window
{
    private static readonly Bitmap s_playIcon = LoadIcon("play.png");
    private static readonly Bitmap s_stopIcon = LoadIcon("stop.png");
    private static readonly Snap[] s_snaps = { Snap.Bar, Snap.Beat, Snap.HalfBeat, Snap.QuarterBeat, Snap.None };

    private readonly ISongLibrary? _library;
    private readonly string _songPath = "";
    private readonly IMidiDevice? _device;
    private readonly SongMap _map = new();
    private readonly List<int> _instrumentChannels = new();
    private List<SongPart> _parts = new();
    private long _currentPartId = -1;
    private long _selStart;
    private long _selEnd;
    private bool _firstShow = true;
    private int _updating; // QSignalBlocker: the changes made by the code are not the user's
    private bool _closeAllowed;
    private bool _asking; // a question is open (Qt's message boxes block; these are awaited)

    private long _practisePartId = -1;
    private BarPosition _practiseStart;
    private BarPosition _practiseEnd;

    private readonly SectionPlayer _player = null!;
    private readonly PianoRollView _roll = null!;
    private readonly SongOverview _overview = null!;

    /// <summary>For the designer of Avalonia only.</summary>
    public SectionEditor()
    {
        InitializeComponent();
    }

    /// <summary>The instruments are the items of the list of the practice window (their names and
    /// MIDI channels), and activeChannel the one chosen there.</summary>
    public SectionEditor(ISongLibrary? library, string songPath, string songTitle,
                         IMidiDevice? device, double speed,
                         IReadOnlyList<string> instrumentNames, IReadOnlyList<int> instrumentChannels, int activeChannel,
                         Window? parent)
    {
        InitializeComponent();
        Strings.TranslateTree(this); // retranslateUi() of the .ui file
        _library = library;
        _songPath = songPath;
        _device = device;

        Title = Strings.Tr("Section Editor - %1", songTitle);
        _map.Load(songPath);

        // created in the thread of the interface, where its events are sent
        _player = new SectionPlayer(_map, device);

        // the views
        _overview = overview.Overview;
        _roll = roll.View;
        _overview.SetSongMap(_map);
        _roll.SetSongMap(_map);

        // the tools above the views
        foreach (string text in new[] { "Bar", "Beat", "Half beat", "Quarter beat", "Free" })
            snapCombo.Items.Add(Strings.Tr(text));
        snapCombo.SelectedIndex = 1;

        // the instrument practised, as in the list of the practice window
        for (int i = 0; i < instrumentNames.Count && i < instrumentChannels.Count; ++i)
        {
            instrumentCombo.Items.Add(instrumentNames[i]);
            _instrumentChannels.Add(instrumentChannels[i]);
            if (instrumentChannels[i] == activeChannel || i == 0) // a QComboBox shows the first item
                instrumentCombo.SelectedIndex = i;
        }
        instrumentCombo.IsEnabled = _instrumentChannels.Count > 1;
        _roll.SetPractiseChannel(PractiseChannel);
        _overview.SetPractiseChannel(PractiseChannel);

        legendRight.Text = " " + Strings.Tr("Right hand") + "   ";
        legendLeft.Text = " " + Strings.Tr("Left hand") + "   ";
        legendOthers.Text = " " + Strings.Tr("Others");

        hint.FontSize = FontSize * 0.9;

        // the selection, as bars and beats
        int bars = _map.BarCount();
        startBarSpin.Minimum = Bar.FirstBarNumber;
        startBarSpin.Maximum = bars - 1 + Bar.FirstBarNumber;
        endBarSpin.Minimum = Bar.FirstBarNumber;
        endBarSpin.Maximum = bars + Bar.FirstBarNumber; // up to the end of the last bar

        // listening
        speedSpin.Value = Math.Clamp((int)Math.Round(speed * 100.0, MidpointRounding.AwayFromZero), 20, 200);

        // connections
        snapCombo.SelectionChanged += (_, _) =>
        {
            if (snapCombo.SelectedIndex >= 0)
                _roll.Snap = s_snaps[snapCombo.SelectedIndex];
        };
        instrumentCombo.SelectionChanged += (_, _) =>
        {
            _roll.SetPractiseChannel(PractiseChannel);
            _overview.SetPractiseChannel(PractiseChannel);
        };
        otherPartsCheck.IsCheckedChanged += (_, _) =>
        {
            bool show = otherPartsCheck.IsChecked == true;
            _roll.SetShowOtherParts(show);
            _overview.SetShowOtherParts(show);
        };
        _roll.SelectionChanged += RollSelectionChanged;
        _roll.NewSelectionStarted += RollNewSelection;
        _roll.PartClicked += PartClicked;
        _roll.ViewChanged += UpdateOverviewRange;
        _overview.CentreRequested += tick => _roll.CentreOn(tick);
        partList.SelectionChanged += (_, _) =>
        {
            if (_updating == 0)
                PartRowChanged(partList.SelectedIndex);
        };
        startBarSpin.ValueChanged += (_, _) => StartEdited();
        startBeatSpin.ValueChanged += (_, _) => StartEdited();
        endBarSpin.ValueChanged += (_, _) => EndEdited();
        endBeatSpin.ValueChanged += (_, _) => EndEdited();
        repeatCheck.IsCheckedChanged += (_, _) => _player.SetRepeat(repeatCheck.IsChecked == true);
        speedSpin.ValueChanged += (_, e) =>
        {
            if (e.NewValue is decimal value)
                _player.SetSpeed((double)value / 100.0);
        };
        _player.PositionChanged += PlayerPosition;
        _player.Stopped += PlayerStopped;

        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Reject();
            }
        };
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            // stop the sound before the song is gone (the destructor)
            _player.Dispose();
        };

        if (parent != null)
        {
            Width = parent.ClientSize.Width * 0.85;
            Height = parent.ClientSize.Height * 0.85;
        }
        else
        {
            Width = 1100;
            Height = 700;
        }

        LoadParts(-1);
        SetSelection(0, 0);
        ShowSelectionInFields();
    }

    private static Bitmap LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri("avares://PianoBoosterCS/images/" + name)));

    public bool IsValid => _map.IsValid;

    /// <summary>After the dialog is accepted with "Practise": the saved section to practise, or
    /// (id &lt; 0) the start and the end of a selection that was not saved.</summary>
    public long PractisePartId => _practisePartId;
    public BarPosition PractiseStart => _practiseStart;
    public BarPosition PractiseEnd => _practiseEnd;

    /// <summary>The MIDI channel of the instrument chosen in the editor (-1 if there is no list).</summary>
    public int PractiseChannel =>
        _instrumentChannels.Count > 0 && instrumentCombo.SelectedIndex >= 0 ? _instrumentChannels[instrumentCombo.SelectedIndex] : -1;

    /// <summary>What is selected when the editor opens: a saved section, or else the start and
    /// end set in the practice window (if the loop is active).</summary>
    public void SetInitialSelection(long partId, bool loopActive, BarPosition loopStart, BarPosition loopEnd)
    {
        for (int row = 0; row < _parts.Count; ++row)
        {
            if (_parts[row].Id == partId)
            {
                partList.SelectedIndex = row; // shows its start and end
                return;
            }
        }
        if (loopActive)
        {
            SetSelection(_map.TickOfPosition(loopStart), _map.SectionEndTick(loopEnd));
            ShowSelectionInFields();
        }
    }

    // showEvent()
    private void OnOpened(object? sender, EventArgs e)
    {
        if (!_firstShow)
            return;
        _firstShow = false;
        // the size of the piano roll is known only once it is shown
        Dispatcher.UIThread.Post(() =>
        {
            if (_roll.HasSelection)
                _roll.EnsureVisible(_selStart, _selEnd);
            UpdateOverviewRange();
        }, DispatcherPriority.Background);
    }

    // The QShortcut of the space bar: before the focused control, but not in a text field (Qt's
    // ShortcutOverride of QLineEdit)
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || _asking)
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        e.Handled = true;
        TogglePlay();
    }

    private void LoadParts(long selectId)
    {
        _parts = _library != null ? _library.Parts(_songPath) : new List<SongPart>();
        _currentPartId = -1;
        _updating++;
        try
        {
            partList.Items.Clear();
            int selectRow = -1;
            for (int i = 0; i < _parts.Count; i++)
            {
                SongPart part = _parts[i];
                partList.Items.Add(part.Name + "   (" + PartRangeText(part) + ")");
                if (part.Id == selectId)
                {
                    selectRow = i;
                    _currentPartId = part.Id;
                }
            }
            partList.SelectedIndex = selectRow;        }
        finally
        {
            _updating--;
        }
        _roll.SetParts(_parts, _currentPartId);
        _overview.SetParts(_parts, _currentPartId);
        UpdateInfo();
        UpdateButtons();
    }

    private SongPart? CurrentPart()
    {
        foreach (SongPart part in _parts)
            if (part.Id == _currentPartId)
                return part;
        return null;
    }

    private bool IsDirty()
    {
        if (CurrentPart() is not SongPart part || _selEnd <= _selStart)
            return false;
        return _map.TickOfPosition(part.Start) != _selStart || _map.SectionEndTick(part.End) != _selEnd;
    }

    /// <summary>Asks what to do with the changes to the current section. Returns false to cancel.</summary>
    private async Task<bool> ResolveChanges()
    {
        if (CurrentPart() is not SongPart part || !IsDirty())
            return true;
        MessageDialog.SaveAnswer answer = await Ask(() => MessageDialog.SaveDiscardCancel(this, Strings.Tr("Section Changed"),
            Strings.Tr("The start or the end of \"%1\" has been changed. Save the changes?", part.Name)));
        if (answer == MessageDialog.SaveAnswer.Cancel)
            return false;
        if (answer == MessageDialog.SaveAnswer.Save)
            SavePart();
        return true;
    }

    private async Task<T> Ask<T>(Func<Task<T>> question)
    {
        _asking = true;
        try
        {
            return await question();
        }
        finally
        {
            _asking = false;
        }
    }

    private async void PartRowChanged(int row)
    {
        long id = row >= 0 && row < _parts.Count ? _parts[row].Id : -1;
        if (id == _currentPartId)
            return;
        if (!await ResolveChanges())
        {
            // stay on the section that has been changed
            _updating++;
            try
            {
                for (int i = 0; i < _parts.Count; ++i)
                    if (_parts[i].Id == _currentPartId)
                        partList.SelectedIndex = i;
            }
            finally
            {
                _updating--;
            }
            return;
        }
        _currentPartId = id;
        _roll.SetParts(_parts, _currentPartId);
        _overview.SetParts(_parts, _currentPartId);
        if (CurrentPart() is SongPart part)
        {
            SetSelection(_map.TickOfPosition(part.Start), _map.SectionEndTick(part.End));
            ShowSelectionInFields();
            _roll.EnsureVisible(_selStart, _selEnd);
        }
        UpdateInfo();
        UpdateButtons();
    }

    private void PartClicked(long id)
    {
        for (int row = 0; row < _parts.Count; ++row)
            if (_parts[row].Id == id)
                partList.SelectedIndex = row;
    }

    // A new selection is being made away from the edges: it is no longer the chosen
    // section, unless that section has been changed and not saved yet.
    private void RollNewSelection()
    {
        if (_currentPartId < 0 || IsDirty())
            return;
        _currentPartId = -1;
        _updating++;
        partList.SelectedIndex = -1;
        _updating--;
        _roll.SetParts(_parts, -1);
        _overview.SetParts(_parts, -1);
    }

    private void RollSelectionChanged(long start, long end)
    {
        SetSelection(start, end);
        ShowSelectionInFields();
    }

    private void SetSelection(long start, long end)
    {
        _selStart = start;
        _selEnd = end;
        if (_roll.SelectionStart != start || _roll.SelectionEnd != end)
            _roll.SetSelection(start, end);
        _overview.SetSelection(start, end);
        UpdateInfo();
        UpdateButtons();
    }

    // A beat counting from 1, with two decimals, as it is shown in the fields
    private static double ShownBeat(double beatFromZero) =>
        Math.Round((beatFromZero + 1.0) * 100.0, MidpointRounding.AwayFromZero) / 100.0;

    private void ShowSelectionInFields()
    {
        _updating++;
        try
        {
            int startBar = _map.BarOfTick(_selStart);
            startBarSpin.Value = startBar + Bar.FirstBarNumber;
            startBeatSpin.Maximum = (decimal)(_map.BeatsInBar(startBar) + 0.99);
            startBeatSpin.Value = (decimal)ShownBeat(_map.BeatOfTick(_selStart));

            int endBar = _map.BarOfTick(_selEnd);
            endBarSpin.Value = endBar + Bar.FirstBarNumber;
            endBeatSpin.Maximum = (decimal)(_map.BeatsInBar(endBar) + 0.99);
            endBeatSpin.Value = (decimal)ShownBeat(_map.BeatOfTick(_selEnd));
        }
        finally
        {
            _updating--;
        }
    }

    // The value of a QDoubleSpinBox with two decimals
    private static double BeatValue(NumericUpDown spin) => Math.Round((double)(spin.Value ?? 1m), 2, MidpointRounding.AwayFromZero);

    private void StartEdited()
    {
        if (_updating != 0 || startBarSpin.Value is not decimal barValue)
            return;
        int bar = (int)barValue - Bar.FirstBarNumber;
        _updating++;
        startBeatSpin.Maximum = (decimal)(_map.BeatsInBar(bar) + 0.99);
        _updating--;
        SetSelection(_map.TickOfBarBeat(bar, BeatValue(startBeatSpin) - 1.0), _selEnd);
    }

    private void EndEdited()
    {
        if (_updating != 0 || endBarSpin.Value is not decimal barValue)
            return;
        int bar = (int)barValue - Bar.FirstBarNumber;
        _updating++;
        endBeatSpin.Maximum = (decimal)(_map.BeatsInBar(bar) + 0.99);
        _updating--;
        SetSelection(_selStart, Math.Min(_map.TickOfBarBeat(bar, BeatValue(endBeatSpin) - 1.0), _map.EndTick));
    }

    private string RangeText(long start, long end)
    {
        int firstBar = _map.BarOfTick(start) + Bar.FirstBarNumber;
        int lastBar = _map.BarOfTick(end - 1) + Bar.FirstBarNumber;
        return firstBar == lastBar ? Strings.Tr("bar %1", firstBar) : Strings.Tr("bars %1-%2", firstBar, lastBar);
    }

    private string PartRangeText(SongPart part) =>
        RangeText(_map.TickOfPosition(part.Start), _map.SectionEndTick(part.End));

    private static string DurationText(double ms)
    {
        long seconds = (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private void UpdateInfo()
    {
        if (_selEnd <= _selStart)
        {
            infoLabel.Text = Strings.Tr("Nothing is selected: drag on the ruler or on the notes.");
            return;
        }
        int bars = _map.BarOfTick(_selEnd - 1) - _map.BarOfTick(_selStart) + 1;
        string text = RangeText(_selStart, _selEnd) + " · " + Strings.TrPlural("%n bar(s)", bars) +
                      " · " + DurationText(_map.MsOfTick(_selEnd) - _map.MsOfTick(_selStart));
        if (IsDirty())
            text += "\n" + Strings.Tr("Changed: press \"Save\" to keep it in \"%1\", or \"New\" to make another section.",
                                      CurrentPart()!.Value.Name);
        else if (CurrentPart() is SongPart part)
            text += "\n" + Strings.Tr("Section \"%1\"", part.Name);
        infoLabel.Text = text;
    }

    private void UpdateButtons()
    {
        bool selection = _selEnd > _selStart;
        bool part = CurrentPart() != null;
        newButton.IsEnabled = selection && _library != null;
        saveButton.IsEnabled = IsDirty();
        renameButton.IsEnabled = part;
        deleteButton.IsEnabled = part;
        practiseButton.IsEnabled = selection;
    }

    private async void NewPart()
    {
        if (_library == null || _selEnd <= _selStart)
            return;
        string? name = (await Ask(() => MessageDialog.GetText(this, Strings.Tr("New Section"), Strings.Tr("Name of the section:"),
                                                              Strings.Tr("Section %1", _parts.Count + 1))))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        // a changed section keeps what was saved: the changes go to the new section
        long id = _library.AddPart(_songPath, name, _map.PositionOfTick(_selStart), _map.SectionEndPosition(_selEnd));
        LoadParts(id);
    }

    private void SavePart()
    {
        if (_library == null || CurrentPart() is not SongPart part || _selEnd <= _selStart)
            return;
        part.Start = _map.PositionOfTick(_selStart);
        part.End = _map.SectionEndPosition(_selEnd);
        _library.UpdatePart(part);
        LoadParts(part.Id);
    }

    private async void RenamePart()
    {
        if (_library == null || CurrentPart() is not SongPart current)
            return;
        string? name = (await Ask(() => MessageDialog.GetText(this, Strings.Tr("Rename Section"), Strings.Tr("Name of the section:"),
                                                              current.Name)))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        // only the name: changes to the start and end are still to be saved
        SongPart part = current;
        part.Name = name;
        _library.UpdatePart(part);
        LoadParts(part.Id);
    }

    private async void DeletePart()
    {
        if (_library == null || CurrentPart() is not SongPart current)
            return;
        if (!await Ask(() => MessageDialog.Question(this, Strings.Tr("Delete Section"),
                                                    Strings.Tr("Delete the section \"%1\"?", current.Name))))
            return;
        _library.DeletePart(current.Id);
        LoadParts(-1); // the selection stays, to make a new section with it if wanted
    }

    private async void TogglePlay()
    {
        if (_player.IsPlaying)
        {
            _player.Stop();
            return;
        }
        if (_device == null || !_device.ValidMidiOutput())
        {
            await Ask(async () =>
            {
                await MessageDialog.Information(this, Strings.Tr("Listen"),
                    Strings.Tr("There is no MIDI output to play through. Choose one in \"Setup\" / \"MIDI Setup\"."));
                return true;
            });
            return;
        }
        long start = _selStart;
        long end = _selEnd;
        if (end <= start)
        {
            // nothing selected: from the place shown up to the end of the song
            start = _roll.SnapTick(_roll.FirstVisibleTick());
            end = _map.EndTick;
        }
        _player.Play(start, end, repeatCheck.IsChecked == true, (double)(speedSpin.Value ?? 100m) / 100.0);
        if (_player.IsPlaying)
        {
            playButtonText.Text = Strings.Tr("Stop");
            playButtonImage.Source = s_stopIcon;
            PlayerPosition(start);
        }
    }

    private void PlayerPosition(long tick)
    {
        _roll.SetCursorTick(tick, true);
        _overview.SetCursorTick(tick);
    }

    private void PlayerStopped()
    {
        playButtonText.Text = Strings.Tr("Play");
        playButtonImage.Source = s_playIcon;
        _roll.SetCursorTick(-1, false);
        _overview.SetCursorTick(-1);
    }

    private void UpdateOverviewRange() =>
        _overview.SetVisibleRange(_roll.FirstVisibleTick(), _roll.LastVisibleTick());

    private async void Practise()
    {
        if (_selEnd <= _selStart || _asking)
            return;
        _player.Stop();
        long partId = _currentPartId;
        if (CurrentPart() is SongPart part && IsDirty())
        {
            MessageDialog.SaveAnswer answer = await Ask(() => MessageDialog.SaveDiscardCancel(this, Strings.Tr("Section Changed"),
                Strings.Tr("The start or the end of \"%1\" has been changed. Save the changes before practising?", part.Name)));
            if (answer == MessageDialog.SaveAnswer.Cancel)
                return;
            if (answer == MessageDialog.SaveAnswer.Save)
                SavePart();
            else
                partId = -1; // practise what is selected, without saving it
        }
        _practisePartId = partId;
        _practiseStart = _map.PositionOfTick(_selStart);
        _practiseEnd = _map.SectionEndPosition(_selEnd);
        _closeAllowed = true;
        Close(true); // QDialog::accept()
    }

    // reject(): the Close button, Esc and the close button of the window
    private async void Reject()
    {
        if (_asking)
            return;
        _player.Stop();
        if (!await ResolveChanges())
            return;
        _closeAllowed = true;
        Close(false);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAllowed)
            return;
        e.Cancel = true; // QDialog::closeEvent() calls reject(), which may ask first
        Reject();
    }

    // ---- the buttons

    private void OnZoomOutButtonClicked(object? sender, RoutedEventArgs e) => _roll.ZoomBy(1.0 / 1.5);

    private void OnZoomInButtonClicked(object? sender, RoutedEventArgs e) => _roll.ZoomBy(1.5);

    private void OnFitSongButtonClicked(object? sender, RoutedEventArgs e) => _roll.ZoomToSong();

    private void OnFitSelectionButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (_roll.HasSelection)
            _roll.ZoomToFit(_selStart, _selEnd);
    }

    private void OnNewButtonClicked(object? sender, RoutedEventArgs e) => NewPart();

    private void OnSaveButtonClicked(object? sender, RoutedEventArgs e) => SavePart();

    private void OnRenameButtonClicked(object? sender, RoutedEventArgs e) => RenamePart();

    private void OnDeleteButtonClicked(object? sender, RoutedEventArgs e) => DeletePart();

    private void OnPlayButtonClicked(object? sender, RoutedEventArgs e) => TogglePlay();

    private void OnPractiseButtonClicked(object? sender, RoutedEventArgs e) => Practise();

    private void OnCloseButtonClicked(object? sender, RoutedEventArgs e) => Reject();
}
