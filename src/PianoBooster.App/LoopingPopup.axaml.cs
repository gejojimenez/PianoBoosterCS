// Ported from GuiLoopingPopup.h and GuiLoopingPopup.cpp of PianoBooster (C++).
// Copyright (c) 2008-2013, L. J. Barman. GPLv3.
//
// The start position and the repeated section of the song. The C++ widget is a Qt::Popup window;
// here it is the content of a Popup of the top bar.

using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class LoopingPopup : UserControl
{
    private Song? _song;
    private object _sync = new();
    private BarPosition _end; // the exact end position
    private int _blockSignals; // QSignalBlocker

    public LoopingPopup()
    {
        InitializeComponent();
    }

    /// <summary>The start or the repeated section has changed.</summary>
    public event Action? SectionChanged;

    public void Init(Song song, object songLock)
    {
        _song = song;
        _sync = songLock;
        lock (_sync)
        {
            ShowStart(_song.PlayFrom);

            _blockSignals++;
            loopCheck.IsChecked = _song.IsLoopEnabled;
            _blockSignals--;

            _end = _song.PlayUpto;
            if (!_song.IsLoopEnabled && !(_song.PlayFrom < _end))
                _end = new BarPosition(_song.PlayFrom.Bar + 1, 0, 0); // suggest the bar after the start
            ShowEnd(_end);
            UpdateInfo();
        }
    }

    /// <summary>A position as text for the user, e.g. "bar 12" or "bar 12, beat 2.5". The beat is
    /// rounded down for a start and up for an end, like in the fields. Call it with the lock of the song.</summary>
    public static string PositionText(Song song, BarPosition position, bool roundUp)
    {
        double exact = (song.BeatOfPosition(position) + 1.0) * 100.0;
        double beat = (roundUp ? Math.Ceiling(exact - 1e-6) : Math.Floor(exact + 1e-6)) / 100.0;
        if (beat == 1.0)
            return Strings.Tr("bar %1", position.Bar + Bar.FirstBarNumber);
        // QLocale().toString(beat, 'g', 4)
        return Strings.Tr("bar %1, beat %2", position.Bar + Bar.FirstBarNumber, beat.ToString("G4", CultureInfo.CurrentCulture));
    }

    // Lets the beat field go only up to the last beat of the bar, which depends on its
    // time signature (e.g. 4 beats in 4/4 and 12 in 12/8). It does not change the section.
    private void SetBeatRange(NumericUpDown beatSpin, int bar)
    {
        _blockSignals++;
        beatSpin.Maximum = (decimal)(_song!.BeatsInBar(bar) + 0.99);
        _blockSignals--;
    }

    private static int IntValue(NumericUpDown spin) => (int)(spin.Value ?? spin.Minimum);
    private static double DoubleValue(NumericUpDown spin) => (double)(spin.Value ?? spin.Minimum);

    // Shows a position in the start bar and beat fields, without changing the start.
    private void ShowStart(BarPosition start)
    {
        // rounded down so that typing the shown value again also starts before the notes
        double beat = Math.Floor((_song!.BeatOfPosition(start) + 1.0) * 100.0 + 1e-6) / 100.0;
        _blockSignals++;
        startBarSpin.Value = start.Bar + Bar.FirstBarNumber;
        SetBeatRange(startBeatSpin, start.Bar);
        startBeatSpin.Value = (decimal)beat;
        _blockSignals--;
    }

    private void OnStartBarSpinValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_blockSignals == 0)
            StartEdited();
    }

    private void OnStartBeatSpinValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_blockSignals == 0)
            StartEdited();
    }

    // The user has typed a start bar or beat
    private void StartEdited()
    {
        if (_song == null) return;
        lock (_sync)
        {
            SetBeatRange(startBeatSpin, IntValue(startBarSpin) - Bar.FirstBarNumber);
            // the beat is shown counting from 1; round down so the start is never after the notes
            _song.SetPlayFrom(_song.PositionFromBarBeat(IntValue(startBarSpin) - Bar.FirstBarNumber, DoubleValue(startBeatSpin) - 1.0));
            UpdateInfo();
        }
    }

    // Use the exact position of the current notes as the start, so that playing starts on them
    private void OnStartHereButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (_song == null) return;
        lock (_sync)
        {
            BarPosition start = _song.CurrentNotePosition;
            ShowStart(start);
            _song.SetPlayFrom(start);
            UpdateInfo();
        }
    }

    // Shows a position in the end bar and beat fields, without changing the end.
    private void ShowEnd(BarPosition end)
    {
        // The beat is shown counting from 1, rounded up so that typing the shown value
        // again still ends after the notes.
        double beat = Math.Ceiling((_song!.BeatOfPosition(end) + 1.0) * 100.0 - 1e-6) / 100.0;
        _blockSignals++;
        endBarSpin.Value = end.Bar + Bar.FirstBarNumber;
        SetBeatRange(endBeatSpin, end.Bar);
        endBeatSpin.Value = (decimal)beat;
        _blockSignals--;
    }

    private void OnLoopCheckToggled(object? sender, RoutedEventArgs e)
    {
        if (_blockSignals == 0)
            ApplyLoop();
    }

    private void OnEndBarSpinValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_blockSignals == 0)
            EndEdited();
    }

    private void OnEndBeatSpinValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_blockSignals == 0)
            EndEdited();
    }

    // The user has typed an end bar or beat
    private void EndEdited()
    {
        if (_song == null) return;
        lock (_sync)
        {
            SetBeatRange(endBeatSpin, IntValue(endBarSpin) - Bar.FirstBarNumber);
            // the beat is shown counting from 1; round up so the notes at the end are included
            _end = _song.PositionFromBarBeat(IntValue(endBarSpin) - Bar.FirstBarNumber, DoubleValue(endBeatSpin) - 1.0, true);
            _blockSignals++;
            loopCheck.IsChecked = true; // editing the end means the user wants to repeat
            _blockSignals--;
            ApplyLoop();
        }
    }

    // Use the exact position of the current notes as the end
    private void OnEndHereButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (_song == null) return;
        lock (_sync)
        {
            _end = _song.CurrentNotePosition;
            ShowEnd(_end);
            _blockSignals++;
            loopCheck.IsChecked = true;
            _blockSignals--;
            ApplyLoop();
        }
    }

    private void ApplyLoop()
    {
        if (_song == null) return;
        lock (_sync)
        {
            _song.SetLoop(loopCheck.IsChecked == true, _end);
            UpdateInfo();
        }
    }

    private void UpdateInfo()
    {
        if (loopCheck.IsChecked != true)
            loopingText.Text = Strings.Tr("Repeat Bar is disabled");
        else if (!_song!.IsLoopActive)
            loopingText.Text = Strings.Tr("The end must be after the start bar");
        else
            loopingText.Text = Strings.Tr("Repeats from %1 to %2", PositionText(_song, _song.PlayFrom, false),
                                          PositionText(_song, _song.PlayUpto, true));
        SectionChanged?.Invoke();
    }

}
