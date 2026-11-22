// Ported from GuiSongDetailsDialog.h and GuiSongDetailsDialog.cpp of PianoBooster (C++): the MIDI
// channels of the left and right hand piano parts of the song. GPLv3.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class SongDetailsDialog : Window
{
    private static readonly IBrush Black = Brushes.Black;
    private static readonly IBrush Red = Brushes.Red;
    private static readonly IBrush Gray = Brushes.Gray;

    private Settings _settings = null!;
    private Song _song = null!;
    private object _sync = null!;         // the lock of the Song (ConductorThread.SyncRoot)
    private TrackList _trackList = null!;
    private int _updating;                // QSignalBlocker: changes made by the code are not "activated"

    public SongDetailsDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this); // retranslateUi() of the .ui file
        Title = Strings.Tr("Song Details");
        // The width fits the contents when the dialog opens, then stays (as in Qt): otherwise it would
        // change with the length of the text of songInfoText.
        Opened += (_, _) => SizeToContent = SizeToContent.Manual;
    }

    public void Init(Song song, object songLock, Settings settings)
    {
        _song = song;
        _sync = songLock;
        _settings = settings;

        _updating++;
        try
        {
            lock (_sync)
            {
                _trackList = _song.TrackList;
                leftHandChannelCombo.Items.Add(Strings.Tr("No channel assigned"));
                rightHandChannelCombo.Items.Add(Strings.Tr("No channel assigned"));
                foreach (string name in _trackList.GetAllChannelProgramNames(true))
                {
                    leftHandChannelCombo.Items.Add(name);
                    rightHandChannelCombo.Items.Add(name);
                }

                leftHandChannelCombo.SelectedIndex = _trackList.GetHandTrackIndex(WhichPart.Left) + 1;
                rightHandChannelCombo.SelectedIndex = _trackList.GetHandTrackIndex(WhichPart.Right) + 1;
            }
            AdjustToContents(leftHandChannelCombo);
            AdjustToContents(rightHandChannelCombo);
            UpdateSongInfoText();
        }
        finally
        {
            _updating--;
        }
    }

    /// <summary>QComboBox::AdjustToContentsOnFirstShow: at least as wide as the longest item (and the
    /// minimum width of the .ui).</summary>
    private static void AdjustToContents(ComboBox combo)
    {
        double width = 0;
        foreach (object? item in combo.Items)
        {
            var text = new TextBlock { Text = item?.ToString(), FontSize = combo.FontSize, FontFamily = combo.FontFamily };
            text.Measure(Size.Infinity);
            width = Math.Max(width, text.DesiredSize.Width);
        }
        combo.MinWidth = Math.Max(combo.MinWidth, width + 48); // the padding and the arrow of the combo
    }

    /// <summary>QTextEdit::append() of a span with a colour: a new paragraph.</summary>
    private void AppendInfo(IBrush color, string text)
    {
        InlineCollection inlines = songInfoText.Inlines ??= new InlineCollection();
        if (inlines.Count > 0)
            inlines.Add(new LineBreak());
        inlines.Add(new Run(text) { Foreground = color });
    }

    private void UpdateSongInfoText()
    {
        songInfoText.Inlines?.Clear();
        bool activateOkButton = false;
        int left = leftHandChannelCombo.SelectedIndex;
        int right = rightHandChannelCombo.SelectedIndex;

        if (left != 0 && left == right)
            AppendInfo(Red, Strings.Tr("The left and right hand channels must be different"));
        else if ((left == 0 && right != 0) || (right == 0 && left != 0))
            AppendInfo(Red, Strings.Tr("Both left and right hand channels must be none to disable this feature"));
        else
        {
            AppendInfo(Gray, Strings.Tr("Set the MIDI Channels to be used for left and right hand piano parts:"));
            AppendInfo(Black, Strings.Tr("the left  hand piano part is using MIDI Channels 1"));
            AppendInfo(Black, Strings.Tr("the right hand piano part is using MIDI Channels 1"));
            activateOkButton = true;
        }

        okButton.IsEnabled = activateOkButton;
    }

    private void OnLeftHandChannelComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || _trackList == null)
            return;
        UpdateSongInfoText();
    }

    private void OnRightHandChannelComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || _trackList == null)
            return;
        UpdateSongInfoText();
    }

    private void OnOkButtonClicked(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancelButtonClicked(object? sender, RoutedEventArgs e) => Close(false); // QDialog::reject()

    private void Accept()
    {
        int left = leftHandChannelCombo.SelectedIndex - 1;
        int right = rightHandChannelCombo.SelectedIndex - 1;
        lock (_sync)
            _trackList.SetActiveHandsIndex(left, right);
        Close(true); // QDialog::accept()
    }
}
