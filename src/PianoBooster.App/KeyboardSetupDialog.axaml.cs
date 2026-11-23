// Ported from GuiKeyboardSetupDialog.h and GuiKeyboardSetupDialog.cpp of PianoBooster (C++): the
// sounds of the right and wrong notes (with a test button each) and the note range of the piano
// keyboard. GPLv3.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PianoBooster.Core;

namespace PianoBooster.App;

public partial class KeyboardSetupDialog : Window
{
    private Settings _settings = null!;
    private Song _song = null!;
    private object _sync = null!;                    // the lock of the Song (ConductorThread.SyncRoot)
    private int _updating;                           // QSignalBlocker: changes made by the code are not "activated"
    private bool _done;                              // accept() or reject() already called
    private readonly HashSet<char> _keysDown = new(); // isAutoRepeat(): the keys that are down

    public KeyboardSetupDialog()
    {
        InitializeComponent();
        Strings.TranslateTree(this); // retranslateUi() of the .ui file
        Title = Strings.Tr("Piano Keyboard Settings");

        // the pressed() and released() signals of the test buttons (mouse or space bar)
        rightTestButton.PropertyChanged += (_, e) =>
        {
            if (e.Property == Button.IsPressedProperty && _song != null)
            {
                if (rightTestButton.IsPressed)
                    On_rightTestButton_pressed();
                else
                    On_rightTestButton_released();
            }
        };
        wrongTestButton.PropertyChanged += (_, e) =>
        {
            if (e.Property == Button.IsPressedProperty && _song != null)
            {
                if (wrongTestButton.IsPressed)
                    On_wrongTestButton_pressed();
                else
                    On_wrongTestButton_released();
            }
        };

        // the input mask "000; ": up to three digits
        AddDigitsOnly(lowestNoteEdit);
        AddDigitsOnly(highestNoteEdit);
        // editingFinished: Enter or losing the focus
        foreach (TextBox edit in new[] { lowestNoteEdit, highestNoteEdit })
        {
            edit.LostFocus += (_, _) => UpdateInfoText();
            edit.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Return)
                    UpdateInfoText();
            }, RoutingStrategies.Tunnel);
        }

        // keyPressEvent() and keyReleaseEvent(): the keys that no control used
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Bubble, handledEventsToo: false);
        AddHandler(KeyUpEvent, OnDialogKeyUp, RoutingStrategies.Bubble, handledEventsToo: false);

        // QDialog::closeEvent(): the X of the window is reject()
        Closing += (_, _) =>
        {
            if (!_done)
                RejectSounds();
        };
    }

    public void Init(Song song, object songLock, Settings settings)
    {
        _song = song;
        _sync = songLock;
        _settings = settings;

        _updating++;
        try
        {
            // Check inputs.
            string programName;
            int i;

            i = 0;
            while (true)
            {
                programName = TrackList.GetProgramName(i);
                if (programName.Length == 0)
                    break;
                rightSoundCombo.Items.Add(programName);
                wrongSoundCombo.Items.Add(programName);
                i++;
            }

            int program = _settings.Value("Keyboard/RightSound", Cfg.DefaultRightPatch);
            rightSoundCombo.SelectedIndex = program;
            program = _settings.Value("Keyboard/WrongSound", Cfg.DefaultWrongPatch);
            wrongSoundCombo.SelectedIndex = program;
            int lowestNote = _settings.Value("Keyboard/LowestNote", 0);
            int highestNote = _settings.Value("Keyboard/HighestNote", 127);

            bool midiKeyboard;
            lock (_sync)
                midiKeyboard = _song.MidiInputOpen;
            if (!midiKeyboard) // the PC keyboard: none chosen, or the chosen one is not connected
            {
                lowestNote = Song.PcKeyLowestNote;
                highestNote = Song.PcKeyHighestNote;
                lowestNoteEdit.IsEnabled = false;
                highestNoteEdit.IsEnabled = false;
                resetButton.IsEnabled = false;
            }

            lowestNoteEdit.Text = lowestNote.ToString();
            highestNoteEdit.Text = highestNote.ToString();
            UpdateInfoText();
            rightVolumeLabel.IsVisible = false; // fixme Hide for now
            rightVolumeSpin.IsVisible = false;
            wrongVolumeLabel.IsVisible = false;
            wrongVolumeSpin.IsVisible = false;
        }
        finally
        {
            _updating--;
        }
    }

    // ---- the test buttons ----

    private void On_rightTestButton_pressed()
    {
        lock (_sync)
        {
            _song.TestWrongNoteSound(false);
            _song.PcKeyPress('x', true);
        }
    }

    private void On_rightTestButton_released()
    {
        lock (_sync)
            _song.PcKeyPress('x', false);
    }

    private void On_wrongTestButton_pressed()
    {
        lock (_sync)
        {
            _song.TestWrongNoteSound(true);
            _song.PcKeyPress('x', true);
        }
    }

    private void On_wrongTestButton_released()
    {
        lock (_sync)
            _song.PcKeyPress('x', false);
    }

    // ---- the sounds and the note range ----

    private void OnResetButtonClicked(object? sender, RoutedEventArgs e)
    {
        lowestNoteEdit.Text = "0";
        highestNoteEdit.Text = "127";
        UpdateInfoText();
    }

    private void OnRightSoundComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || _song == null)
            return;
        UpdateSounds();
    }

    private void OnWrongSoundComboActivated(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating > 0 || _song == null)
            return;
        UpdateSounds();
    }

    private void UpdateSounds()
    {
        lock (_sync)
            _song.SetPianoSoundPatches(rightSoundCombo.SelectedIndex - 1,
                                       wrongSoundCombo.SelectedIndex - 1, true);
    }

    /// <summary>QString::toInt() of the text of a QLineEdit: 0 if it is not a number.</summary>
    private static int ToInt(TextBox edit) => int.TryParse((edit.Text ?? "").Trim(), out int n) ? n : 0;

    private static readonly IBrush Black = Brushes.Black;
    private static readonly IBrush Red = Brushes.Red;

    /// <summary>QTextEdit::append("&lt;span style=color:...&gt;text&lt;/span&gt;"): a new paragraph;
    /// the text may have &lt;b&gt; and &lt;/b&gt;.</summary>
    private void AppendInfo(IBrush color, string html)
    {
        InlineCollection inlines = keyboardInfoText.Inlines ??= new InlineCollection();
        if (inlines.Count > 0)
            inlines.Add(new LineBreak());
        bool bold = false;
        foreach (string part in Regex.Split(html, "(</?b>)"))
        {
            if (part == "<b>")
                bold = true;
            else if (part == "</b>")
                bold = false;
            else if (part.Length > 0)
                inlines.Add(new Run(part) { Foreground = color, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal });
        }
    }

    private void UpdateInfoText()
    {
        string str;
        keyboardInfoText.Inlines?.Clear();
        int lowestNote = ToInt(lowestNoteEdit);
        int highestNote = ToInt(highestNoteEdit);
        lowestNote = Math.Clamp(lowestNote, 0, 127);
        highestNote = Math.Clamp(highestNote, 0, 127);
        lowestNoteEdit.Text = lowestNote.ToString();
        highestNoteEdit.Text = highestNote.ToString();
        int noteRange = highestNote - lowestNote;
        AppendInfo(Black, Strings.Tr("Choose the right and wrong sound for your playing."));
        IBrush color = Black;
        if (!lowestNoteEdit.IsEnabled)
            str = Strings.Tr("You can use the PC keyboard instead of a MIDI keyboard; 'x' is middle C.");
        else if (noteRange > 0)
            str = Strings.Tr("Your keyboard range is <b>octaves %1</b> and <b>semitones %2</b>; 60 is middle C.",
                             noteRange / Core.Midi.Octave, noteRange % Core.Midi.Octave);
        else
        {
            str = Strings.Tr("Oops, you have <b>0 notes</b> on your keyboard!");
            color = Red;
        }

        AppendInfo(color, str);
    }

    /// <summary>The input mask "000; ": only digits (the length is limited by MaxLength).</summary>
    private static void AddDigitsOnly(TextBox edit)
    {
        edit.AddHandler(TextInputEvent, (_, e) =>
        {
            if (e.Text != null && !e.Text.All(char.IsAsciiDigit))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    // ---- keyPressEvent() / keyReleaseEvent() ----

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        // a QLineEdit uses the keys that make text; Tab moves the focus before keyPressEvent()
        if (FocusManager?.GetFocusedElement() is TextBox || e.Key == Key.Tab)
            return;
        if (e.KeySymbol is not { Length: > 0 } text) // event->text().length() == 0
            return;
        char c = text[0] < 256 ? text[0] : '\0';      // event->text().toLatin1().at(0)
        if (c == '\0' || !_keysDown.Add(c))
            return;                                    // the key repeats (isAutoRepeat())
        lock (_sync)
            _song.PcKeyPress(c, true);
        e.Handled = true;
    }

    private void OnDialogKeyUp(object? sender, KeyEventArgs e)
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

    // ---- accept() / reject() ----

    private void OnOkButtonClicked(object? sender, RoutedEventArgs e) => Accept();

    private void OnCancelButtonClicked(object? sender, RoutedEventArgs e) => Reject();

    private void Accept()
    {
        _settings.SetValue("Keyboard/RightSound", rightSoundCombo.SelectedIndex);
        _settings.SetValue("Keyboard/WrongSound", wrongSoundCombo.SelectedIndex);
        _settings.SetValue("Keyboard/RightSoundPrevious", rightSoundCombo.SelectedIndex);
        int lowestNote = ToInt(lowestNoteEdit);
        int highestNote = ToInt(highestNoteEdit);
        lowestNote = Math.Clamp(lowestNote, 0, 127);
        highestNote = Math.Clamp(highestNote, 0, 127);
        if (lowestNoteEdit.IsEnabled)
        {
            _settings.SetValue("Keyboard/LowestNote", lowestNote);
            _settings.SetValue("Keyboard/HighestNote", highestNote);
        }
        lock (_sync)
        {
            _song.UpdatePianoRange(); // the range of the input in use (the PC keys when no MIDI keyboard is open)
            _song.TestWrongNoteSound(false);
            _song.RegenerateChordQueue();
        }
        _done = true;
        Close(true); // QDialog::accept()
    }

    private void Reject()
    {
        RejectSounds();
        _done = true;
        Close(false); // QDialog::reject()
    }

    private void RejectSounds()
    {
        lock (_sync)
        {
            _song.TestWrongNoteSound(false);
            _song.SetPianoSoundPatches(_settings.Value("Keyboard/RightSound", Cfg.DefaultRightPatch) - 1,
                                       _settings.Value("Keyboard/WrongSound", Cfg.DefaultWrongPatch) - 1, true);
        }
    }
}
