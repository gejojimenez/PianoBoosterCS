// Ported from GuiSidePanel.h and GuiSidePanel.cpp of PianoBooster (C++), and from
// CTrackList::changeListWidgetItemView() (TrackList.cpp). Copyright (c) 2008-2013, L. J. Barman. GPLv3.
//
// The panel on the left: the song, its sections, the skill, the hands, the parts and their volumes.
// Qt's "activated" (a choice of the user) is SelectionChanged here, so the changes made by the
// code are ignored with _updating; toggled and valueChanged are called for both, as in Qt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class SidePanel : UserControl
{
    private Song? _song;
    private object _sync = new();
    private TrackList? _trackList;
    private TopBar? _topBar;
    private Settings _settings = null!;
    private string _songTitle = "";      // the song shown in the header
    private string _songFileName = "";
    private MidiInfo _songInfo = new();
    private ISongLibrary? _library;
    private List<SongPart> _parts = new();
    private int _updating;

    public SidePanel()
    {
        InitializeComponent();
        // GuiSidePanel(): the title is 1.15 times bigger
        songTitleLabel.FontSize = FontSize * 1.15;
        SetPartTexts();
        SetSongInfo("", "", new MidiInfo());
    }

    public event Action? LibraryRequested;
    public event Action? SectionEditorRequested;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    public void SetSettings(Settings settings) => _settings = settings;

    /// <summary>The library where the named parts of the songs are kept.</summary>
    public void SetLibrary(ISongLibrary? library) => _library = library;

    public string SongFileName => _songFileName;

    public long SelectedPartId => SelectedPart() is SongPart part ? part.Id : -1;

    public bool IsRepeatSong() => repeatSong.IsChecked == true;

    public int GetSongIndex() => songCombo.SelectedIndex;
    public void SetSongIndex(int index) => songCombo.SelectedIndex = index;

    private void OnLibraryButtonClicked(object? sender, RoutedEventArgs e) => LibraryRequested?.Invoke();
    private void OnEditorButtonClicked(object? sender, RoutedEventArgs e) => SectionEditorRequested?.Invoke();

    /// <summary>Shows the song name, its folder and its MIDI information in the header.</summary>
    public void SetSongInfo(string title, string fileName, MidiInfo info)
    {
        _songTitle = title;
        _songFileName = fileName;
        _songInfo = info;
        songTitleLabel.Text = fileName.Length == 0 ? Strings.Tr("No song") : title;

        string? dir = fileName.Length == 0 ? null : Path.GetFileName(Path.GetDirectoryName(fileName));
        songFolderLabel.Text = fileName.Length == 0 ? "" : "\U0001F4C1 " + dir;
        string native = fileName.Replace('/', Path.DirectorySeparatorChar);
        ToolTip.SetTip(songFolderLabel, fileName.Length == 0 ? null : native);
        ToolTip.SetTip(songTitleLabel, fileName.Length == 0 ? null : native);

        if (!info.Valid)
        {
            songInfoLabel.Text = "";
            return;
        }
        var first = new List<string> { info.TimeSigText() };
        if (info.KeyText().Length > 0)
            first.Add(info.KeyText());
        first.Add(info.TempoText());
        string second = info.DurationText() + " · " + Strings.TrPlural("%n bar", "%n bars", info.Bars);
        songInfoLabel.Text = string.Join(" · ", first) + "\n" + second;
    }

    private void SetPartTexts()
    {
        partLabel.Text = Strings.Tr("Section:");
        ToolTip.SetTip(partCombo, Strings.Tr("Practise a section of the song: it is played from its start to its end, repeating"));
        ToolTip.SetTip(addPartButton, Strings.Tr("Save the current start and end (Start / Repeat) as a new section"));
        ToolTip.SetTip(updatePartButton, Strings.Tr("Replace the start and end of the selected section with the current ones"));
        ToolTip.SetTip(renamePartButton, Strings.Tr("Rename the selected section"));
        ToolTip.SetTip(deletePartButton, Strings.Tr("Delete the selected section"));
        editorButton.Content = Strings.Tr("Editor...");
        ToolTip.SetTip(editorButton, Strings.Tr("Open the section editor, to create and change the sections on a view of the whole song (Ctrl+E)"));
    }

    /// <summary>Reads the parts of the current song from the library and selects selectId (or the whole song).</summary>
    public void LoadParts(long selectId = -1)
    {
        _parts = _library != null && _songFileName.Length > 0 ? _library.Parts(_songFileName) : new List<SongPart>();

        _updating++; // QSignalBlocker
        partCombo.Items.Clear();
        partCombo.Items.Add(new ComboBoxItem { Content = Strings.Tr("Whole song"), Tag = -1L });
        partCombo.SelectedIndex = 0;
        foreach (SongPart part in _parts)
        {
            partCombo.Items.Add(new ComboBoxItem { Content = part.Name, Tag = part.Id });
            if (part.Id == selectId)
                partCombo.SelectedIndex = partCombo.ItemCount - 1;
        }
        _updating--;
        UpdatePartButtons();
    }

    private void UpdatePartButtons()
    {
        bool hasSong = _library != null && _songFileName.Length > 0;
        bool partSelected = partCombo.SelectedIndex > 0;
        partCombo.IsEnabled = hasSong;
        addPartButton.IsEnabled = hasSong;
        editorButton.IsEnabled = hasSong;
        updatePartButton.IsEnabled = partSelected;
        renamePartButton.IsEnabled = partSelected;
        deletePartButton.IsEnabled = partSelected;
    }

    private SongPart? SelectedPart()
    {
        long id = (partCombo.SelectedItem as ComboBoxItem)?.Tag is long tag ? tag : -1;
        foreach (SongPart part in _parts)
            if (part.Id == id)
                return part;
        return null;
    }

    private void OnPartComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating == 0)
            PartSelected();
    }

    // Plays the selected part, or the whole song, from now on
    private void PartSelected()
    {
        if (_song == null)
            return;
        _topBar?.StopMuiscPlaying();

        lock (_sync)
        {
            if (SelectedPart() is SongPart part)
            {
                _song.SetPlayFrom(part.Start);
                _song.SetLoop(true, part.End);
            }
            else
            {
                _song.SetPlayFrom(new BarPosition());
                _song.SetLoop(false, _song.PlayUpto);
            }
        }
        _topBar?.UpdateSectionSummary();
        UpdatePartButtons();
    }

    // Checks that there is a start and an end to save as a part
    private async Task<bool> HasSectionForPart()
    {
        if (_song != null)
            lock (_sync)
                if (_song.IsLoopActive)
                    return true;
        if (OwnerWindow is Window owner)
            await MessageDialog.Information(owner, Strings.Tr("Save a Section"),
                Strings.Tr("First set the start and the end of the section in \"Start / Repeat\" (the button with the arrow " +
                           "in the top bar), with \"Repeat up to the end bar\" ticked."));
        return false;
    }

    private async void OnAddPartClicked(object? sender, RoutedEventArgs e)
    {
        if (_library == null || OwnerWindow is not Window owner || !await HasSectionForPart())
            return;
        string? name = (await MessageDialog.GetText(owner, Strings.Tr("New Section"), Strings.Tr("Name of the section:"),
                                                    Strings.Tr("Section %1", _parts.Count + 1)))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        BarPosition start, end;
        lock (_sync)
        {
            start = _song!.PlayFrom;
            end = _song.PlayUpto;
        }
        long id = _library.AddPart(_songFileName, name, start, end);
        LoadParts(id);
    }

    private async void OnUpdatePartClicked(object? sender, RoutedEventArgs e)
    {
        if (_library == null || SelectedPart() is not SongPart selected || OwnerWindow is not Window owner || !await HasSectionForPart())
            return;
        if (!await MessageDialog.Question(owner, Strings.Tr("Update Section"),
                Strings.Tr("Replace the start and end of \"%1\" with the current ones?", selected.Name)))
            return;
        SongPart part = selected;
        lock (_sync)
        {
            part.Start = _song!.PlayFrom;
            part.End = _song.PlayUpto;
        }
        _library.UpdatePart(part);
        LoadParts(part.Id);
    }

    private async void OnRenamePartClicked(object? sender, RoutedEventArgs e)
    {
        if (_library == null || SelectedPart() is not SongPart selected || OwnerWindow is not Window owner)
            return;
        string? name = (await MessageDialog.GetText(owner, Strings.Tr("Rename Section"), Strings.Tr("Name of the section:"),
                                                    selected.Name))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        SongPart part = selected;
        part.Name = name;
        _library.UpdatePart(part);
        LoadParts(part.Id);
    }

    private async void OnDeletePartClicked(object? sender, RoutedEventArgs e)
    {
        if (_library == null || SelectedPart() is not SongPart selected || OwnerWindow is not Window owner)
            return;
        if (!await MessageDialog.Question(owner, Strings.Tr("Delete Section"),
                Strings.Tr("Delete the section \"%1\"?", selected.Name)))
            return;
        _library.DeletePart(selected.Id);
        LoadParts();
        PartSelected(); // back to the whole song
    }

    /// <summary>After the section editor: reads the parts again, and plays the selected one with
    /// its new start and end (or the whole song, if it has been deleted).</summary>
    public void ReloadPartsAfterEditing()
    {
        long previous = SelectedPartId;
        LoadParts(previous);
        if (previous < 0)
            return; // the start and end set in "Start / Repeat" are kept
        // with its new start and end, or the whole song if it has been deleted
        PartSelected();
    }

    /// <summary>Selects a part and plays it, repeating.</summary>
    public void PractisePart(long id)
    {
        LoadParts(id);
        PartSelected();
    }

    /// <summary>Chooses the instrument to practise (a MIDI channel) in the list of instruments.</summary>
    public void SelectInstrument(int channel)
    {
        if (_song == null || _trackList == null || channel < 0)
            return;
        int row;
        lock (_sync)
        {
            if (channel == _song.ActiveChannel)
                return;
            row = _trackList.GetAllChannels().IndexOf(channel);
        }
        if (row < 0 || row >= trackListWidget.ItemCount)
            return;
        // as if it had been clicked in the list (even if that row is already the current one,
        // which happens because the list shows the right hand while the left one is active)
        _updating++;
        trackListWidget.SelectedIndex = row;
        _updating--;
        TrackListRowChanged(row);
    }

    /// <summary>Plays a stretch of the song that is not a saved part, repeating.</summary>
    public void PractiseSelection(BarPosition start, BarPosition end)
    {
        LoadParts();
        if (_song == null)
            return;
        _topBar?.StopMuiscPlaying();
        lock (_sync)
        {
            _song.SetPlayFrom(start);
            _song.SetLoop(true, end);
        }
        _topBar?.UpdateSectionSummary();
        UpdatePartButtons();
    }

    public void Init(Song songObj, object songLock, TrackList trackList, TopBar topBar)
    {
        _song = songObj;
        _sync = songLock;
        _trackList = trackList;
        _topBar = topBar;
        lock (_sync)
            _trackList.Init(songObj, _settings);

        // set skill from config
        var skill = (PlayMode)_settings.Value("SidePanel/skill", (int)PlayMode.FollowYou);
        switch (skill)
        {
            case PlayMode.Listen:
                listenRadio.IsChecked = true;
                OnListenRadioToggled(listenRadio, new RoutedEventArgs());
                break;
            case PlayMode.RhythmTapping:
                rhythmTapRadio.IsChecked = true;
                OnRhythmTapRadioToggled(rhythmTapRadio, new RoutedEventArgs());
                break;
            case PlayMode.FollowYou:
                followYouRadio.IsChecked = true;
                OnFollowYouRadioToggled(followYouRadio, new RoutedEventArgs());
                break;
            case PlayMode.PlayAlong:
                playAlongRadio.IsChecked = true;
                OnPlayAlongRadioToggled(playAlongRadio, new RoutedEventArgs());
                break;
        }

        bothHandsRadio.IsChecked = true;

        _updating++;
        rhythmTappingCombo.Items.Add(Strings.Tr("Drums"));
        rhythmTappingCombo.Items.Add(Strings.Tr("Melody"));
        //FIXME rhythmTappingCombo.Items.Add(Strings.Tr("Drums+M"));
        _updating--;

        RhythmTappingActivated(_settings.Value("SidePanel/rhythmTapping", 0));
        _updating++;
        lock (_sync)
            rhythmTappingCombo.SelectedIndex = (int)_song.CfgRhythmTapping;
        repeatSong.IsChecked = _settings.Value("SidePanel/repeatSong", false);
        _updating--;
    }

    /// <summary>The parts of the song in the list (and its colours, CTrackList::changeListWidgetItemView()).</summary>
    public void Refresh()
    {
        if (_trackList != null)
        {
            List<string> names;
            List<int> channels;
            int activeIndex;
            lock (_sync)
            {
                _trackList.Refresh();
                names = _trackList.GetAllChannelProgramNames();
                channels = _trackList.GetAllChannels();
                activeIndex = _trackList.GetActiveItemIndex();
            }
            _updating++;
            trackListWidget.Items.Clear(); // QListWidget::clear() gives row -1, which the track list ignores
            for (int i = 0; i < names.Count; i++)
                trackListWidget.Items.Add(NewTrackItem(names[i], channels[i]));
            _updating--;
            trackListWidget.SelectedIndex = activeIndex;
        }
        AutoSetMuteYourPart();
    }

    private ListBoxItem NewTrackItem(string name, int chan)
    {
        var item = new ListBoxItem { Content = name, Padding = new Avalonia.Thickness(6, 2) };
        lock (_sync)
        {
            if (Note.HasPianoPart(chan))
            {
                if (Note.RightHandChan >= 0 && Note.LeftHandChan >= 0)
                    item.FontWeight = FontWeight.Bold;
                item.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 128)); // Qt::darkBlue
            }
            else if (_song!.HasPianistKeyboardChannel(chan))
                item.Foreground = new SolidColorBrush(Color.FromRgb(192, 192, 192)); // Qt::lightGray
        }
        return item;
    }

    public void LoadBookList()
    {
        List<string> bookNames = _settings.GetBookList();

        _updating++;
        bookCombo.Items.Clear();
        for (int i = 0; i < bookNames.Count; i++)
        {
            bookCombo.Items.Add(bookNames[i]);
            if (bookNames[i] == _settings.GetCurrentBookName())
                bookCombo.SelectedIndex = i;
        }
        if (bookCombo.SelectedIndex < 0 && bookCombo.ItemCount > 0)
            bookCombo.SelectedIndex = 0; // a QComboBox shows its first item
        _updating--;
        BookComboActivated(-1);
    }

    private void BookComboActivated(int index)
    {
        _settings.SetCurrentBookName(bookCombo.SelectedItem as string ?? "", index >= 0);

        string currentSong = _settings.GetCurrentSongName();

        _updating++;
        songCombo.Items.Clear();
        List<string> songNames = _settings.GetSongList();
        for (int i = 0; i < songNames.Count; ++i)
        {
            songCombo.Items.Add(songNames[i]);
            if (songNames[i] == currentSong)
                songCombo.SelectedIndex = i;
        }
        if (songCombo.SelectedIndex < 0 && songCombo.ItemCount > 0)
            songCombo.SelectedIndex = 0;
        _updating--;
        SongComboActivated(); // Now load the selected song
    }

    private void SongComboActivated() => _settings.SetCurrentSongName(songCombo.SelectedItem as string ?? "");

    public void NextSong(int amount)
    {
        int n = songCombo.SelectedIndex + amount;
        if (n < 0 || n >= songCombo.ItemCount)
            return;

        songCombo.SelectedIndex = n;
        SongComboActivated();
    }

    public void NextBook(int amount)
    {
        int n = bookCombo.SelectedIndex + amount;
        if (n < 0 || n >= bookCombo.ItemCount)
            return;

        bookCombo.SelectedIndex = n;
        BookComboActivated(n);
    }

    public void SetActiveHand(WhichPart hand)
    {
        if (hand == WhichPart.Right) rightHandRadio.IsChecked = true;
        if (hand == WhichPart.Both) bothHandsRadio.IsChecked = true;
        if (hand == WhichPart.Left) leftHandRadio.IsChecked = true;
    }

    private void OnRightHandRadioToggled(object? sender, RoutedEventArgs e)
    {
        if (rightHandRadio.IsChecked == true)
            _settings.SetActiveHand(WhichPart.Right);
    }

    private void OnBothHandsRadioToggled(object? sender, RoutedEventArgs e)
    {
        if (bothHandsRadio.IsChecked == true)
            _settings.SetActiveHand(WhichPart.Both);
    }

    private void OnLeftHandRadioToggled(object? sender, RoutedEventArgs e)
    {
        if (leftHandRadio.IsChecked == true)
            _settings.SetActiveHand(WhichPart.Left);
    }

    private void OnRepeatSongReleased(object? sender, RoutedEventArgs e)
    {
        if (_updating == 0 && _settings != null)
            _settings.SetValue("SidePanel/repeatSong", repeatSong.IsChecked == true);
    }

    private void OnTrackListWidgetCurrentRowChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating == 0)
            TrackListRowChanged(trackListWidget.SelectedIndex);
    }

    private void TrackListRowChanged(int currentRow)
    {
        if (_trackList != null)
        {
            lock (_sync)
                _trackList.CurrentRowChanged(currentRow);
            AutoSetMuteYourPart();
        }
    }

    private void OnBoostSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_song != null)
            lock (_sync)
                _song.BoostVolume((int)boostSlider.Value);
    }

    private void OnPianoSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_song != null)
            lock (_sync)
                _song.PianoVolume((int)pianoSlider.Value);
    }

    private void SetPlayMode(RadioButton radio, PlayMode mode)
    {
        if (_song == null || radio.IsChecked != true) return;
        _settings.SetValue("SidePanel/skill", (int)mode);
        lock (_sync)
            _song.SetPlayMode(mode);
        AutoSetMuteYourPart();
    }

    private void OnListenRadioToggled(object? sender, RoutedEventArgs e) => SetPlayMode(listenRadio, PlayMode.Listen);
    private void OnRhythmTapRadioToggled(object? sender, RoutedEventArgs e) => SetPlayMode(rhythmTapRadio, PlayMode.RhythmTapping);
    private void OnFollowYouRadioToggled(object? sender, RoutedEventArgs e) => SetPlayMode(followYouRadio, PlayMode.FollowYou);
    private void OnPlayAlongRadioToggled(object? sender, RoutedEventArgs e) => SetPlayMode(playAlongRadio, PlayMode.PlayAlong);

    private void OnRhythmTappingComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating == 0 && rhythmTappingCombo.SelectedIndex >= 0)
            RhythmTappingActivated(rhythmTappingCombo.SelectedIndex);
    }

    private void RhythmTappingActivated(int index)
    {
        _settings.SetValue("SidePanel/rhythmTapping", index);

        lock (_sync)
        {
            _song!.CfgRhythmTapping = index switch
            {
                1 => RhythmTapping.MellodyOnly,
                2 => RhythmTapping.DrumsAndMellody,
                _ => RhythmTapping.DrumsOnly,
            };
        }
        AutoSetMuteYourPart();
    }

    private void OnMuteYourPartCheckToggled(object? sender, RoutedEventArgs e)
    {
        if (_song != null)
            lock (_sync)
                _song.MutePianistPart(muteYourPartCheck.IsChecked == true);
    }

    private void SetTrackHandPart(bool right)
    {
        if (_trackList == null || _song == null)
            return;
        int row = trackListWidget.SelectedIndex;
        lock (_sync)
        {
            int otherRow = _trackList.GetHandTrackIndex(right ? WhichPart.Left : WhichPart.Right);
            if (otherRow == row) otherRow = -1;
            if (right)
                _trackList.SetActiveHandsIndex(otherRow, row);
            else
                _trackList.SetActiveHandsIndex(row, otherRow);
        }
        trackListWidget.SelectedIndex = row;
        lock (_sync)
            _song.RefreshScroll();
    }

    private void OnSetTrackRightHandPart(object? sender, RoutedEventArgs e) => SetTrackHandPart(true);
    private void OnSetTrackLeftHandPart(object? sender, RoutedEventArgs e) => SetTrackHandPart(false);

    private void OnClearTrackPart(object? sender, RoutedEventArgs e)
    {
        if (_trackList == null || _song == null)
            return;
        int row = trackListWidget.SelectedIndex;
        lock (_sync)
            _trackList.SetActiveHandsIndex(-1, -1);
        trackListWidget.SelectedIndex = row;
        lock (_sync)
            _song.RefreshScroll();
    }

    private void AutoSetMuteYourPart()
    {
        if (_song == null)
            return;
        bool isChecked = true;
        lock (_sync)
        {
            if (Conductor.PlayMode == PlayMode.RhythmTapping)
            {
                if (_song.CfgRhythmTapping == RhythmTapping.DrumsOnly)
                    isChecked = false;
            }
        }

        muteYourPartCheck.IsChecked = isChecked;
        lock (_sync)
            _song.MutePianistPart(isChecked);
    }

    public void SetSongName(string songName)
    {
        for (int i = 0; i < songCombo.ItemCount; ++i)
            if (songCombo.Items[i] as string == songName)
                songCombo.SelectedIndex = i;
    }

    public void SetBookName(string bookName)
    {
        for (int i = 0; i < bookCombo.ItemCount; ++i)
            if (bookCombo.Items[i] as string == bookName)
                bookCombo.SelectedIndex = i;
    }

    // pass either 'left' 'right' or 'both'
    public void SetCurrentHand(string hand)
    {
        if (hand == "left")
            leftHandRadio.IsChecked = true;
        else if (hand == "right")
            rightHandRadio.IsChecked = true;
        else
            bothHandsRadio.IsChecked = true;
    }
}
