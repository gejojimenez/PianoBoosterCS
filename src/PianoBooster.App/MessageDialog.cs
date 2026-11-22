// The message boxes of Qt (QMessageBox::information/warning/question and QInputDialog::getText),
// which Avalonia does not have. Part of the .NET port of PianoBooster. GPLv3.

using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PianoBooster.App;

internal sealed class MessageDialog : Window
{
    private readonly StackPanel _buttons = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Spacing = 8,
    };

    private MessageDialog(string title, Control content, double minWidth)
    {
        Title = title;
        MinWidth = minWidth;
        MaxWidth = 900;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_buttons, Dock.Bottom);
        _buttons.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(_buttons);
        root.Children.Add(new ScrollViewer { Content = content, MaxHeight = 700 });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close(null);
        };
    }

    private Button AddButton(string text, object? result, bool isDefault = false) => AddButtonWith(text, () => result, isDefault);

    private Button AddButtonWith(string text, System.Func<object?> result, bool isDefault = false)
    {
        var button = new Button { Content = text, MinWidth = 80, IsDefault = isDefault, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => Close(result());
        _buttons.Children.Add(button);
        return button;
    }

    private static Control TextContent(string text) =>
        new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };

    /// <summary>A message with an OK button (QMessageBox::information / warning / exec()).</summary>
    public static Task Show(Window owner, string title, Control content, double minWidth = 300)
    {
        var dialog = new MessageDialog(title, content, minWidth);
        dialog.AddButton(Strings.Tr("OK"), null, isDefault: true);
        return dialog.ShowDialog(owner);
    }

    public static Task Information(Window owner, string title, string text) => Show(owner, title, TextContent(text));

    public static Task Warning(Window owner, string title, string text) => Show(owner, title, TextContent(text));

    /// <summary>QMessageBox::question(): true for Yes.</summary>
    public static async Task<bool> Question(Window owner, string title, string text)
    {
        var dialog = new MessageDialog(title, TextContent(text), 300);
        dialog.AddButton(Strings.Tr("Yes"), true, isDefault: true);
        dialog.AddButton(Strings.Tr("No"), false);
        return await dialog.ShowDialog<object?>(owner) is true;
    }

    /// <summary>QInputDialog::getText(): the text, or null if cancelled.</summary>
    public static async Task<string?> GetText(Window owner, string title, string label, string text)
    {
        var box = new TextBox { Text = text, MinWidth = 300 };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        var dialog = new MessageDialog(title, panel, 300);
        dialog.AddButtonWith(Strings.Tr("OK"), () => box.Text ?? "", isDefault: true);
        dialog.AddButton(Strings.Tr("Cancel"), null);
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return await dialog.ShowDialog<object?>(owner) as string;
    }
}
