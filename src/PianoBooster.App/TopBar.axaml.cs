// Ported from GuiTopBar.h and GuiTopBar.cpp of PianoBooster (C++).
// Copyright (c) 2008-2013, L. J. Barman. GPLv3.
//
// The bar above the score: play, speed, key, transpose and the start / repeated section.
// Qt's "activated" (a choice of the user) is SelectionChanged here, so the changes made by the
// code are ignored with _updating; valueChanged of a spin box is ValueChanged in both.

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class TopBar : UserControl
{
    private Song? _song;
    private object _sync = new();
    private bool _atTheEndOfTheSong;
    private int _updating;

    private static readonly Bitmap s_playIcon = LoadIcon("play.png");
    private static readonly Bitmap s_stopIcon = LoadIcon("stop.png");

    public TopBar()
    {
        InitializeComponent();
        _atTheEndOfTheSong = false;

        _updating++;
        majorCombo.Items.Add(Strings.Tr("Major"));
        majorCombo.Items.Add(Strings.Tr("Minor"));
        majorCombo.SelectedIndex = 0;
        _updating--;
        loopingPopup.SectionChanged += UpdateSectionSummary;
    }

    // GuiTopBar::updateTranslate(); the texts of the .ui file (retranslateUi()) are translated with the
    // whole window by MainWindow.RefreshTranslate() (Strings.TranslateTree)
    public void UpdateTranslate()
    {
        _updating++;
        int index = majorCombo.SelectedIndex;
        majorCombo.Items[0] = Strings.Tr("Major");
        majorCombo.Items[1] = Strings.Tr("Minor");
        majorCombo.SelectedIndex = index;
        _updating--;
        if (profileText.Text is { Length: > 0 })
            SetProfileName(_profileName);

        // in C++ retranslateUi() puts back the text of the .ui file in the button, until the next change
        if (_song != null)
            UpdateSectionSummary();
    }

    /// <summary>The profile button was clicked (not in the C++: phase 1.5 of FEATURES.md).</summary>
    public event Action? ProfilesRequested;

    private string? _profileName;

    /// <summary>Shows the active profile; null: none (no progress.db), the button is disabled.</summary>
    public void SetProfileName(string? name)
    {
        _profileName = name;
        profileButton.IsEnabled = name != null;
        profileText.Text = name != null ? Strings.Tr("Profile: %1", name) : Strings.Tr("No profile");
    }

    private void OnProfileButtonClicked(object? sender, RoutedEventArgs e) => ProfilesRequested?.Invoke();

    private static Bitmap LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri("avares://PianoBoosterCS/images/" + name)));

    public void Init(Song songObj, object songLock)
    {
        _song = songObj;
        _sync = songLock;
        ReloadKeyCombo(true);
    }

    public void Refresh(bool reset)
    {
        if (reset)
        {
            _updating++;
            majorCombo.SelectedIndex = 0;
            _updating--;
            ReloadKeyCombo(true);
            transposeSpin.Value = 0;
        }
        UpdateSectionSummary();
        int index = 0;
        if (_song != null)
            lock (_sync)
                index = StavePos.KeySignature + 6;
        if (index >= 0 && index < keyCombo.ItemCount)
        {
            _updating++;
            keyCombo.SelectedIndex = index;
            _updating--;
        }
    }

    private static readonly string[] s_majorKeys = { "Gb", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#" }; // -6 .. 6
    private static readonly string[] s_minorKeys = { "Eb", "Bb", "F", "C", "G", "D", "A", "E", "B", "F#", "G#", "C#", "D#" };

    private void ReloadKeyCombo(bool major)
    {
        _updating++;
        keyCombo.Items.Clear();
        foreach (string key in major ? s_majorKeys : s_minorKeys)
            keyCombo.Items.Add(Strings.Translate("GuiTopBar", key)); // "B" is "H" only in CDraw
        _updating--;
        Refresh(false);
    }

    private void OnMajorComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating == 0 && majorCombo.SelectedIndex >= 0)
            ReloadKeyCombo(majorCombo.SelectedIndex == 0);
    }

    private void OnKeyComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating != 0 || _song == null || keyCombo.SelectedIndex < 0)
            return;
        lock (_sync)
        {
            StavePos.SetKeySignature(keyCombo.SelectedIndex - 6, 0);
            _song.RefreshScroll();
        }
    }

    private void OnTransposeSpinValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        //                         C  Db  D  Eb  E  F   F# G  Ab  A  Bb  B
        int[] nextKey = { 0, -5, 2, -3, 4, -1, 6, 1, -4, 3, -2, 5 };
        int nextKeySize = nextKey.Length;
        if (_song == null) return;
        int value = (int)(transposeSpin.Value ?? 0);
        int newValue;
        lock (_sync)
        {
            int diff = value - _song.Transposition;
            int oldValue = StavePos.KeySignature;
            if (oldValue == -6)
                oldValue = 6; // if key is Eb change to D#

            // Find the old value in the table
            int i;
            for (i = 0; i < nextKeySize; i++)
            {
                if (oldValue == nextKey[i])
                    break;
            }

            newValue = nextKey[(diff + i + nextKeySize) % nextKeySize];

            StavePos.SetKeySignature(newValue, 0);
        }

        newValue += 6;
        _updating++;
        if (newValue >= 0 && newValue < keyCombo.ItemCount)
            keyCombo.SelectedIndex = newValue;
        _updating--;

        lock (_sync)
        {
            _song.Transpose(value);
            _song.ForceScoreRedraw();
        }
    }

    public void SetPlayButtonState(bool checkedState, bool atTheEnd = false)
    {
        if (atTheEnd)
            _atTheEndOfTheSong = true;

        playButton.IsChecked = checkedState;
        if (checkedState)
        {
            playButtonImage.Source = s_stopIcon;
            ToolTip.SetTip(playButton, null);
            ToolTip.SetTip(playFromStartButton, null);
        }
        else
        {
            playButtonImage.Source = s_playIcon;
            ToolTip.SetTip(playButton, Strings.Tr("Start and stop playing music"));
            ToolTip.SetTip(playFromStartButton, Strings.Tr("Playing music from the beginning"));
        }
    }

    public void SetSpeed(int value) => speedSpin.Value = value;
    public int GetSpeed() => (int)(speedSpin.Value ?? 100);

    /// <summary>The speed spin box one step up or down (the Up and Down keys of the window).</summary>
    public void StepSpeed(int steps)
    {
        decimal value = (speedSpin.Value ?? 100) + steps * speedSpin.Increment;
        speedSpin.Value = Math.Clamp(value, speedSpin.Minimum, speedSpin.Maximum);
    }

    private void OnPlayButtonClicked(object? sender, RoutedEventArgs e) => On_playButton_clicked();
    private void OnPlayFromStartButtonClicked(object? sender, RoutedEventArgs e) => On_playFromStartButton_clicked();

    public void On_playButton_clicked()
    {
        if (_song == null) return;
        bool start;
        lock (_sync)
        {
            if (_atTheEndOfTheSong)
                _song.Rewind();
            _atTheEndOfTheSong = false;

            start = !_song.PlayingMusic;
            _song.PlayMusic(start);
        }
        SetPlayButtonState(start);
    }

    public void On_playFromStartButton_clicked()
    {
        if (_song == null) return;
        _atTheEndOfTheSong = false;
        lock (_sync)
            _song.PlayFromStartBar();
        SetPlayButtonState(true);
    }

    private void OnSpeedSpinValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_song == null) return;
        int speed = GetSpeed();
        lock (_sync)
            _song.SetSpeed((float)speed / 100.0f);
    }

    // Stop the muisc playing
    public void StopMuiscPlaying()
    {
        if (_song == null) return;
        lock (_sync)
            _song.PlayMusic(false);
        SetPlayButtonState(false);
    }

    private void OnLoopingBarsPopupButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (_song == null) return;
        if (loopingPopupHost.IsOpen)
        {
            loopingPopupHost.IsOpen = false;
            return;
        }

        lock (_sync)
            _song.PlayMusic(false);
        SetPlayButtonState(false);

        loopingPopup.Init(_song, _sync);
        loopingBarsPopupButton.IsChecked = true;
        loopingPopupHost.IsOpen = true;
    }

    private void OnLoopingPopupClosed(object? sender, EventArgs e) => loopingBarsPopupButton.IsChecked = false;

    // Shows the start and the end of the repeated section on the button that opens the popup
    public void UpdateSectionSummary()
    {
        string text = Strings.Tr("Start / Repeat");
        if (_song != null)
        {
            lock (_sync)
            {
                BarPosition start = _song.PlayFrom;
                string startText = LoopingPopup.PositionText(_song, start, false);
                if (_song.IsLoopActive)
                    text = startText + " → " + LoopingPopup.PositionText(_song, _song.PlayUpto, true) + " ↻";
                else if (!start.IsSongStart)
                    text = Strings.Tr("From %1", startText);
            }
        }
        // a long summary would make the top bar wider than the screen: the text is cut (MaxWidth)
        loopingBarsPopupText.Text = text;
        ToolTip.SetTip(loopingBarsPopupButton, Strings.Tr("Start position and repeated section") + "\n" + text);
    }
}
