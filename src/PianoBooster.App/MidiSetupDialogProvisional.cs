// PROVISIONAL (phase 4): the choice of the MIDI input and output, so that the application can be
// used until GuiMidiSetupDialog is ported in phase 5. What it does on OK is the part of
// GuiMidiSetupDialog::accept() (PianoBooster, C++) about the ports. GPLv3.

using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PianoBooster.Core;
using PianoBooster.Midi;

namespace PianoBooster.App;

internal static class MidiSetupDialogProvisional
{
    public static async Task ShowDialog(Window owner, Song song, object songLock, Settings settings, MidiDevice midiDevice)
    {
        var inputCombo = new ComboBox { MinWidth = 320 };
        var outputCombo = new ComboBox { MinWidth = 320 };

        inputCombo.Items.Add(Strings.Tr("None (PC Keyboard)"));
        foreach (string port in midiDevice.GetMidiPortList(MidiType.Input))
            inputCombo.Items.Add(port);
        outputCombo.Items.Add(Strings.Tr("None"));
        List<string> outputs = midiDevice.GetMidiPortList(MidiType.Output);
        foreach (string port in outputs)
            outputCombo.Items.Add(port);
        Select(inputCombo, settings.Value("Midi/Input"));
        Select(outputCombo, settings.Value("Midi/Output"));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 8,
            ColumnSpacing = 8,
        };
        void Add(Control c, int row, int column)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            grid.Children.Add(c);
        }
        Add(new TextBlock { Text = Strings.Tr("MIDI Input Device:"), VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Add(inputCombo, 0, 1);
        Add(new TextBlock { Text = Strings.Tr("MIDI Output Device:"), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Add(outputCombo, 1, 1);
        var note = new TextBlock
        {
            Text = "A provisional dialog: the MIDI setup of PianoBooster (latency, FluidSynth...) is ported in phase 5.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 480,
            Opacity = 0.7,
        };
        Grid.SetColumnSpan(note, 2);
        Add(note, 2, 0);

        var ok = new Button { Content = Strings.Tr("OK"), IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = Strings.Tr("Cancel"), IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(buttons);
        root.Children.Add(grid);

        var dialog = new Window
        {
            Title = Strings.Tr("MIDI Setup"),
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        if (await dialog.ShowDialog<bool>(owner) != true)
            return;

        string input = inputCombo.SelectedItem as string ?? "";
        settings.SetValue("Midi/Input", input);
        lock (songLock)
        {
            song.OpenMidiPort(MidiType.Input, input);
            if (input.StartsWith(Strings.Tr("None"), System.StringComparison.Ordinal))
                Chord.SetPianoRange(Song.PcKeyLowestNote, Song.PcKeyHighestNote);
            else
                Chord.SetPianoRange(settings.Value("Keyboard/LowestNote", 0), settings.Value("Keyboard/HighestNote", 127));

            if (outputCombo.SelectedIndex == 0)
            {
                settings.SetValue("Midi/Output", "");
                song.OpenMidiPort(MidiType.Output, "");
            }
            else
            {
                string output = outputCombo.SelectedItem as string ?? "";
                settings.SetValue("Midi/Output", output);
                song.OpenMidiPort(MidiType.Output, output);
            }
        }
        settings.UpdateWarningMessages();
        lock (songLock)
            song.RegenerateChordQueue();
    }

    private static void Select(ComboBox combo, string text)
    {
        combo.SelectedIndex = 0;
        for (int i = 0; i < combo.ItemCount; i++)
            if (combo.Items[i] as string == text)
                combo.SelectedIndex = i;
    }
}
